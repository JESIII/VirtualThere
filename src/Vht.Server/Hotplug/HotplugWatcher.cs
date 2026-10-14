using System.Runtime.Versioning;
using Vht.Core.Capture;
using Vht.Core.Concurrency;
using Vht.Core.Hooks;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Server;
using Vht.Interop.Hotplug;
using Vht.Server.Devices;

namespace Vht.Server.Hotplug;

/// <summary>
/// Turns device-interface notifications into export-table changes.
/// </summary>
/// <remarks>
/// <para>
/// Two things happen on every event, and the pair is what makes removal converge in well under
/// the "few seconds" the acceptance criteria ask for:
/// </para>
/// <list type="number">
///   <item>A <b>removal</b> matches the symbolic link against the export table and un-exports
///   that device immediately, on the callback thread's queue — no enumeration round trip.</item>
///   <item>Every event also schedules a <b>coalesced re-enumeration</b> (a few hundred
///   milliseconds later, all events inside the window folding into one), because a device that
///   arrives needs its descriptors read before it can be exported, and a device that leaves can
///   take more than one interface with it. Re-enumeration is the single convergence path, so
///   there is no second, divergent update path to get wrong.</item>
/// </list>
/// <para>
/// A hub being plugged in produces a burst of interface arrivals; that is exactly why the
/// re-enumeration is debounced rather than run per notification.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HotplugWatcher : IAsyncDisposable
{
    private readonly VhtServerState _state;
    private readonly ILogger _log;
    private readonly DeviceInterfaceWatcher _watcher;
    private readonly TimeSpan _debounce;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _rescan = new(0);
    private Task? _worker;

    public HotplugWatcher(VhtServerState state, TimeSpan? debounce = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _log = state.Logger;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        _watcher = new DeviceInterfaceWatcher(Interop.Guids.DeviceInterfaceUsbDevice);
        _watcher.Changed += OnInterfaceChanged;
    }

    /// <summary>True when the OS registration is live.</summary>
    public bool IsRunning => _watcher.IsRunning;

    /// <summary>Notifications seen since start, including those coalesced into one rescan.</summary>
    public long Notifications => _watcher.Notifications;

    /// <summary>
    /// <c>CR_*</c> code returned by the class-specific registration, or 0 when it succeeded.
    /// Non-zero with <see cref="IsRunning"/> true means the all-interface-classes form is live.
    /// </summary>
    public int ClassSpecificError => _watcher.ClassSpecificError;

    /// <summary>True when the live registration spans every interface class.</summary>
    public bool UsingAllInterfaceClasses => _watcher.UsingAllInterfaceClasses;

    /// <summary>Re-enumerations actually performed.</summary>
    public long Rescans { get; private set; }

    /// <summary>Devices un-exported directly by a removal event, without waiting for a rescan.</summary>
    public long DirectRemovals { get; private set; }

    /// <summary>
    /// Registers for notifications and starts the coalescing worker. Returns false with the
    /// <c>CR_*</c> code when registration failed; the caller then falls back to polling.
    /// </summary>
    public bool Start(out int error)
    {
        if (!_watcher.Start(out error))
            return false;

        _worker = Task.Run(() => WorkerAsync(_stop.Token));
        return true;
    }

    private void OnInterfaceChanged(string symbolicLink, bool present)
    {
        if (!present && !string.IsNullOrEmpty(symbolicLink))
            RemoveMatches(symbolicLink);

        try
        {
            // The worker may already be awake; one pending signal is enough for a burst.
            if (_rescan.CurrentCount == 0)
                _rescan.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void RemoveMatches(string symbolicLink)
    {
        foreach (ExportEntry entry in _state.Exports.Snapshot())
        {
            if (!Matches(entry.Device, symbolicLink)) continue;
            if (!_state.Exports.Remove(entry.Device.Key, out _)) continue;

            DirectRemovals++;

            _log.Write(LogLevel.Info, LogEvent.HotplugRemoval,
                $"removal: {entry.Device.BusId} [{(entry.Device.VendorId):x4}:{(entry.Device.ProductId):x4}] " +
                "un-exported immediately",
                new Dictionary<string, object?>
                {
                    ["busid"] = entry.Device.BusId,
                    ["path"] = symbolicLink,
                    ["direct"] = true,
                });

            _ = _state.RunHookAsync(HookEvent.OnDeviceUnplug, entry.Device, CancellationToken.None);
        }
    }

    private static bool Matches(UsbDevice device, string symbolicLink) =>
        (!string.IsNullOrEmpty(device.DevicePath)
            && string.Equals(device.DevicePath, symbolicLink, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrEmpty(device.InstancePath)
            && symbolicLink.Contains(device.InstancePath, StringComparison.OrdinalIgnoreCase));

    private async Task WorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _rescan.WaitAsync(ct).ConfigureAwait(false);
                await Task.Delay(_debounce, ct).ConfigureAwait(false);

                // Collapse the burst: anything signalled during the delay is this same rescan.
                while (_rescan.CurrentCount > 0)
                    await _rescan.WaitAsync(ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) return;

                if (_state.Capture is WinUsbCaptureProvider provider)
                    provider.EnumerateAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

                int count = await _state.RefreshExportsAsync(ct).ConfigureAwait(false);
                Rescans++;

                _log.Write(LogLevel.Detail, LogEvent.HotplugArrival,
                    $"hotplug rescan complete: {count} device(s) exported",
                    new Dictionary<string, object?> { ["exported"] = count });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Warn, LogEvent.HotplugRemoval,
                    $"hotplug rescan failed: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Changed -= OnInterfaceChanged;
        _watcher.Dispose();

        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutting down; an abandoned worker thread cannot outlive the process.
            }
        }

        _rescan.Dispose();
        _stop.Dispose();
    }
}
