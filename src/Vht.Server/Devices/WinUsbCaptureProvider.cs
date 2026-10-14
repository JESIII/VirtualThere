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
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinUsbCaptureProvider : ICaptureProvider
{
    private readonly IDeviceEnumerator _enumerator;
    private readonly ServerOptions _options;
    private readonly ILogger _log;
    private readonly WinUsbRebinder? _rebinder;
    private readonly ConcurrentDictionary<DeviceKey, byte> _rebound = new();

    public WinUsbCaptureProvider(
        IDeviceEnumerator enumerator,
        ServerOptions options,
        ILogger log,
        WinUsbRebinder? rebinder = null)
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

        if (!device.CaptureState.IsExportable())
        {
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
        bool mayRebind = _options.CaptureMode == "auto" && _options.RebindEnabled && _rebinder is not null;
        if (!mayRebind)
            return prebound;

        if (!WinUsbRebinder.CanRebind)
        {
            _log.Write(LogLevel.Warn, LogEvent.DeviceCaptureRefused,
                $"{device.BusId}: capture.rebind.enabled is set but this process is not elevated, " +
                "so the P2 rebind path cannot run",
                new Dictionary<string, object?> { ["busid"] = device.BusId });
            return prebound;
        }

        RebindResult rebind = _rebinder!.Rebind(device.VendorId, device.ProductId);
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

        _rebound[device.Key] = 0;
        _log.Write(LogLevel.Info, LogEvent.DeviceCaptureRefused,
            $"{device.BusId}: rebound onto the inbox winusb.sys ({rebind.InfPath})" +
            (rebind.RebootRequired ? "; a reboot is required before the binding is effective" : string.Empty),
            new Dictionary<string, object?> { ["busid"] = device.BusId, ["inf"] = rebind.InfPath });

        // The interface path changes when the driver changes, so the enumerator is asked again
        // rather than reusing a stale path.
        IReadOnlyList<UsbDevice> after = await _enumerator.EnumerateAsync(ct).ConfigureAwait(false);
        UsbDevice? refreshed = after.FirstOrDefault(d => d.Key == device.Key);

        return refreshed is null
            ? CaptureOpenResult.Failure(CaptureReason.DriverHoldsDevice,
                "device disappeared while being rebound")
            : TryPrebound(refreshed);
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(UsbDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (_rebound.TryRemove(device.Key, out _))
        {
            // A rebind is not rolled back automatically: restoring the previous driver needs
            // the INF that was in effect, which we never captured. Say so precisely instead of
            // pretending the device was put back.
            _log.Write(LogLevel.Warn, LogEvent.DeviceReleased,
                $"{device.BusId} was rebound to winusb.sys by this server; the original driver is " +
                "restored with 'pnputil /delete-driver <oemNN.inf> /uninstall' (or Device Manager > " +
                "Uninstall device, then Scan for hardware changes)",
                new Dictionary<string, object?> { ["busid"] = device.BusId });
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
