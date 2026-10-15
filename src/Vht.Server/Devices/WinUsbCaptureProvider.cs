using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Vht.Core.Capture;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Protocol;
using Vht.Core.Server;
using Vht.Interop.Devices;

namespace Vht.Server.Devices;

/// <summary>
/// Supplies WinUSB capture handles for the devices the enumerator found, applying
/// <c>capture.mode</c> and refusing in the vocabulary of ARCHITECTURE.md §4.3.
/// </summary>
/// <remarks>
/// <para>
/// The three paths are decided here and nowhere else:
/// </para>
/// <list type="number">
///   <item><b>P1 (prebound)</b> — the device already answers on a WinUSB interface. Always
///   tried first, in every mode.</item>
///   <item><b>P2 (rebind)</b> — the function driver holds the device, <c>auto</c> is in effect
///   and <c>capture.rebind.enabled</c> is set. This is the only path that changes a binding,
///   it needs elevation, and it selects the inbox <c>winusb.sys</c>.</item>
///   <item><b>P3 (refused)</b> — neither worked. The device is reported with its
///   <see cref="CaptureReason"/>, never silently dropped and never "taken" anyway.</item>
/// </list>
/// <para>
/// A device the enumerator reported as <see cref="CaptureState.RebindOffered"/> is driver-held
/// and therefore not exportable yet, so it enters this method already committed to P2: the
/// claimability gate below accepts it, P1 is still tried first (the binding may have changed
/// since enumeration), and the rebind runs if P1 fails. A device that is neither exportable nor
/// rebind-offered is refused here with the reason the enumerator recorded, before any path is
/// touched.
/// </para>
/// <para>
/// <b>Every rebind is reversible.</b> A binding this class changed is given back by the same
/// class, on the three occasions it can be: the client detaching (<see cref="ReleaseAsync"/>,
/// the only release path - it also covers <c>devices.kick</c>), a device released explicitly,
/// and the server shutting down (<see cref="DisposeAsync"/>, and the startup sweep in
/// <see cref="RestorePendingHandbacks"/> for a run that was killed). Nothing here rebinds a
/// device on the enumeration path: <see cref="OpenAsync"/> is reached only from a client's
/// session.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinUsbCaptureProvider : ICaptureProvider
{
    private readonly IDeviceEnumerator _enumerator;
    private readonly ServerOptions _options;
    private readonly ILogger _log;
    private readonly IDeviceRebinder? _rebinder;

    /// <summary>Devices this run claimed through P2, for <see cref="ReboundCount"/> only.</summary>
    private readonly ConcurrentDictionary<DeviceKey, byte> _rebound = new();

    public WinUsbCaptureProvider(
        IDeviceEnumerator enumerator,
        ServerOptions options,
        ILogger log,
        IDeviceRebinder? rebinder = null)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _rebinder = rebinder;
    }

    /// <summary>Number of devices this provider has rebound to <c>winusb.sys</c> in this run.</summary>
    public int ReboundCount => _rebound.Count;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct) =>
        _enumerator.EnumerateAsync(ct);

    /// <inheritdoc />
    public async ValueTask<CaptureOpenResult> OpenAsync(UsbDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);

        // The P2 decision comes first, and it is a question about the *offer*, not about the
        // device being claimable right now. A device the enumerator reported as RebindOffered is
        // driver-held by definition, so it is not exportable yet - the rebind below is what makes
        // it exportable. Ordering an IsExportable() bail in front of the P2 block is what made
        // this whole path unreachable (see CaptureState.RebindOffered).
        bool offersRebind = device.CaptureState.OffersRebind();
        bool mayRebind = _options.CaptureMode == "auto" && _options.RebindEnabled && _rebinder is not null;

        if (!device.CaptureState.IsClaimable() || (offersRebind && !mayRebind))
        {
            // Two honest refusals land here: a device that can never be claimed (unsafe, no
            // interface, ignored by policy), and a device whose rebind offer has been withdrawn
            // since it was enumerated (capture.rebind.enabled or capture.mode changed under it).
            // Both are reported with the reason the enumerator recorded.
            return CaptureOpenResult.Failure(device.CaptureReason,
                string.IsNullOrEmpty(device.CaptureDetail)
                    ? device.CaptureReason.Describe()
                    : device.CaptureDetail);
        }

        if (string.IsNullOrWhiteSpace(device.DevicePath))
        {
            return CaptureOpenResult.Failure(CaptureReason.NoWinUsbInterface,
                "the device exposes no openable device interface path");
        }

        // ---- P1: the prebound attempt, in every capture mode ----------------------------------
        CaptureOpenResult prebound = TryPrebound(device);
        if (prebound.Ok) return prebound;

        // ---- P2: the rebind attempt, behind explicit opt-in ----------------------------------
        if (!mayRebind)
            return prebound;

        if (!_rebinder!.CanRebind)
        {
            _log.Write(LogLevel.Warn, LogEvent.DeviceCaptureRefused,
                $"{device.BusId}: capture.rebind.enabled is set but this process is not elevated, " +
                "so the P2 rebind path cannot run",
                new Dictionary<string, object?> { ["busid"] = device.BusId });
            return prebound;
        }

        RebindResult rebind = _rebinder.Rebind(device.VendorId, device.ProductId);
        if (!rebind.Ok)
        {
            _log.Write(LogLevel.Warn, LogEvent.DeviceCaptureRefused,
                $"{device.BusId}: P2 rebind failed: {rebind.Detail}",
                new Dictionary<string, object?>
                {
                    ["busid"] = device.BusId,
                    ["win32_error"] = rebind.Win32Error,
                });
            return CaptureOpenResult.Failure(CaptureReason.DriverHoldsDevice, rebind.Detail, rebind.Win32Error);
        }

        if (rebind.RebootRequired)
        {
            // Said before the device is taken, not after: a binding that only exists after a
            // restart is not a device this server can serve, so nothing is handed to the client
            // and the operator's device goes straight back to the driver it had.
            _log.Write(LogLevel.Warn, LogEvent.DeviceCaptureRefused,
                $"{device.BusId}: the rebind onto the inbox winusb.sys ({rebind.InfPath}) only takes " +
                "effect after a reboot, so the device is not being taken; reboot and attach it again",
                new Dictionary<string, object?>
                {
                    ["busid"] = device.BusId,
                    ["inf"] = rebind.InfPath,
                    ["reboot_required"] = true,
                });

            HandBack(device, why: "the rebind needed a reboot, so the device was never taken");

            return CaptureOpenResult.Failure(CaptureReason.DriverHoldsDevice,
                $"the rebind onto {rebind.InfPath} requires a reboot before it takes effect; the device " +
                "has not been taken - reboot the machine and attach it again");
        }

        _rebound[device.Key] = 0;
        _log.Write(LogLevel.Info, LogEvent.DeviceCaptureRefused,
            $"{device.BusId}: rebound onto the inbox winusb.sys ({rebind.InfPath})",
            new Dictionary<string, object?> { ["busid"] = device.BusId, ["inf"] = rebind.InfPath });

        // The interface path changes when the driver changes, so the enumerator is asked again
        // rather than reusing a stale path.
        IReadOnlyList<UsbDevice> after = await _enumerator.EnumerateAsync(ct).ConfigureAwait(false);
        UsbDevice? refreshed = after.FirstOrDefault(d => d.Key == device.Key);

        if (refreshed is null)
        {
            HandBack(device, why: "the device disappeared while it was being rebound");
            return CaptureOpenResult.Failure(CaptureReason.DriverHoldsDevice,
                "device disappeared while being rebound");
        }

        CaptureOpenResult claimed = TryPrebound(refreshed);
        if (!claimed.Ok)
        {
            // The rebind left the device unusable: give it back rather than leaving it here.
            HandBack(device, why: $"the device could not be claimed after the rebind ({claimed.Detail})");
            return claimed;
        }

        return claimed;
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(UsbDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (_rebound.TryRemove(device.Key, out _))
        {
            _log.Write(LogLevel.Detail, LogEvent.DeviceReleased,
                $"{device.BusId}: the client no longer holds this device; handing back the driver " +
                "binding this server changed",
                new Dictionary<string, object?> { ["busid"] = device.BusId });
        }

        HandBack(device, why: "the device was released");

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Server shutdown: whatever this server is still holding is handed back before the process
    /// goes away, so no device is left on <c>winusb.sys</c> because a client was attached when
    /// the server stopped.
    /// </summary>
    /// <remarks>
    /// A session that is still tearing down when this runs will find its device already handed
    /// back and its own <see cref="ReleaseAsync"/> call a no-op - the handback is idempotent by
    /// construction (the record is what makes it a handback, and a completed record is gone).
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        RestorePendingHandbacks();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hands back every device this server moved and has not given back, including records left
    /// by an earlier run. Returns how many were handed back; the rest are reported.
    /// </summary>
    /// <remarks>
    /// Called at start-up (a run that was killed mid-rebind leaves a record the next run must
    /// honour) and on shutdown. Nothing is touched when the record is empty, which is the normal
    /// case: this server does not move devices it was not asked to serve.
    /// </remarks>
    public int RestorePendingHandbacks()
    {
        if (_rebinder is null) return 0;

        IReadOnlyList<RestoreResult> results = _rebinder.RestorePending();
        int handedBack = 0;

        foreach (RestoreResult result in results)
        {
            if (result.NothingToDo) continue;
            if (result.Ok) handedBack++;
            WriteHandbackOutcome(result);
        }

        if (handedBack > 0)
        {
            _log.Write(LogLevel.Info, LogEvent.DeviceHandedBack,
                $"handed {handedBack} device(s) back to the driver they had before this server moved them",
                new Dictionary<string, object?> { ["handed_back"] = handedBack });
        }

        return handedBack;
    }

    /// <summary>
    /// Gives one device back to the driver it had, and reports whatever came of it. A device
    /// this server never moved produces no output at all.
    /// </summary>
    private void HandBack(UsbDevice device, string why)
    {
        if (_rebinder is null) return;

        RestoreResult restore = _rebinder.Restore(device.VendorId, device.ProductId);

        if (restore.NothingToDo) return;

        // Given back means this run is no longer holding it: the counter tracks devices this
        // server moved *and still has*, which is what an operator reading it needs to know.
        _rebound.TryRemove(device.Key, out _);

        if (restore.Ok) return;

        // A handback that did not finish is the one case where the machine stays as this server
        // made it, so it is a warning that names the undo path (see WinUsbRebinder.ManualUndoPath).
        _log.Write(LogLevel.Warn, LogEvent.DeviceHandedBack,
            $"{device.BusId}: handing the device back failed ({why}): {restore.Detail}",
            new Dictionary<string, object?>
            {
                ["busid"] = device.BusId,
                ["instance_id"] = restore.InstanceId,
                ["win32_error"] = restore.Win32Error,
            });
    }

    private void WriteHandbackOutcome(RestoreResult result)
    {
        if (result.Ok && result.RebootRequired)
        {
            _log.Write(LogLevel.Warn, LogEvent.DeviceHandedBack,
                $"{result.Detail}; the device stays on the package this server staged until then",
                new Dictionary<string, object?> { ["instance_id"] = result.InstanceId });
            return;
        }

        if (!result.Ok)
        {
            _log.Write(LogLevel.Warn, LogEvent.DeviceHandedBack,
                $"handing the device back failed: {result.Detail}",
                new Dictionary<string, object?>
                {
                    ["instance_id"] = result.InstanceId,
                    ["win32_error"] = result.Win32Error,
                });
        }
    }

    private CaptureOpenResult TryPrebound(UsbDevice device)
    {
        WinUsbIo? io = WinUsbIo.Open(device.DevicePath, out int error);
        if (io is null)
        {
            return CaptureOpenResult.Failure(CaptureReason.DriverHoldsDevice,
                $"WinUsbIo.Open('{device.DevicePath}') failed (win32 {error})", error);
        }

        return CaptureOpenResult.Success(new WinUsbCapture(device.Key, io));
    }
}
