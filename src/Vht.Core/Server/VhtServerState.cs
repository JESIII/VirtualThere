using System.Collections.Concurrent;
using Vht.Core.Capture;
using Vht.Core.Concurrency;
using Vht.Core.Hooks;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Policy;
using Vht.Core.Protocol;

namespace Vht.Core.Server;

/// <summary>
/// The shared server state: the export/attach/connection tables, policy, hooks and capture
/// provider. One instance per server process; every connection and the control plane hold the
/// same instance.
/// </summary>
/// <remarks>
/// Nothing in here, and nothing reachable from here, has a device-count field or a capacity.
/// The three tables are the whole registry, and the only bounds anywhere are
/// <see cref="ConnectionResources"/> per connection (see
/// <see cref="Concurrency.ConnectionResources"/> for why those are not a device limit).
/// </remarks>
public sealed class VhtServerState
{
    public VhtServerState(
        ServerOptions options,
        ICaptureProvider capture,
        ILogger logger,
        HookRunner? hooks = null,
        DevicePolicy? policy = null,
        CaptureOutcomeLog? captureOutcomes = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Hooks = hooks;
        Policy = policy ?? options.BuildPolicy();
        Resources = new ConnectionResources(options.MaxInFlightUrbs, options.MaxTransferBuffer);
        CaptureOutcomes = captureOutcomes ?? new CaptureOutcomeLog();
    }

    public ServerOptions Options { get; }
    public ICaptureProvider Capture { get; }
    public ILogger Logger { get; }
    public HookRunner? Hooks { get; }
    public DevicePolicy Policy { get; }
    public ConnectionResources Resources { get; }

    /// <summary>Every device the server is willing to export.</summary>
    public ExportTable Exports { get; } = new();

    /// <summary>
    /// Every device the last enumeration saw, exported or not, so the operator's view can show
    /// a refused or skipped device with its reason instead of omitting it. Keyed like
    /// <see cref="Exports"/>; the export table stays "willing to export" only.
    /// </summary>
    private readonly ConcurrentDictionary<DeviceKey, UsbDevice> _observed = new();

    /// <summary>
    /// Last capture/rebind outcome per device. The capture provider records into this and the
    /// control plane reports it per device, which is how the server window shows <em>which</em>
    /// of P1/P2 ran and what came of it (see <see cref="CaptureOutcome"/>).
    /// </summary>
    public CaptureOutcomeLog CaptureOutcomes { get; }

    /// <summary>Which connection holds which device. Zero or one holder per device.</summary>
    public AttachTable Attachments { get; } = new();

    /// <summary>
    /// Every device the last enumeration saw - exported or refused - in stable bus-id order.
    /// The operator's view (the control plane's <c>devices.list</c>, and the server window built
    /// on it) is built from this, so a device that cannot be exported is listed with its reason
    /// and its capture outcome rather than being missing. The export table alone would omit it.
    /// </summary>
    public IReadOnlyList<UsbDevice> ObservedDevices() =>
        _observed.Values.OrderBy(d => d.BusId, StringComparer.Ordinal).ToArray();

    /// <summary>Every live client connection.</summary>
    public ConnectionRegistry Connections { get; } = new();

    /// <summary>Generation counter bumped whenever the export table is refreshed.</summary>
    public long ExportGeneration => Volatile.Read(ref _generation);
    private long _generation;

    /// <summary>Raised after an export-table refresh, so listeners can log/notify once.</summary>
    public event Action<int>? ExportCountChanged;

    /// <summary>The server's own version banner, for the control plane and logs.</summary>
    public string Banner => VhtVersion.Banner;

    /// <summary>
    /// Re-enumerates the machine and rewrites the export table. Returns the number of
    /// exportable devices.
    /// </summary>
    /// <remarks>
    /// A device the OS will not release is not advertised to clients — offering a device the
    /// server cannot claim would just fail every attach — but it is never silently missing
    /// either: the refusal is logged with its reason, and <c>vht list --local</c> and the
    /// control plane's <c>devices.list</c> show it in the operator's view with that reason
    /// (ARCHITECTURE.md §4.3). A driver-held device the operator permitted a rebind for
    /// (<see cref="CaptureState.RebindOffered"/>) <em>is</em> advertised, because it becomes
    /// claimable the moment the P2 rebind runs at attach.
    /// </remarks>
    public async ValueTask<int> RefreshExportsAsync(CancellationToken ct)
    {
        IReadOnlyList<UsbDevice> found = await Capture.EnumerateAsync(ct).ConfigureAwait(false);
        long generation = Interlocked.Increment(ref _generation);

        var seen = new HashSet<DeviceKey>();
        int exportable = 0;

        foreach (UsbDevice device in found)
        {
            UsbDevice applied = Policy.Apply(device);
            seen.Add(applied.Key);

            // Observed regardless of the verdict: a device this server refuses or skips must
            // still be visible to the operator with its reason, not silently absent from the
            // window (ARCHITECTURE.md §4.3, docs/UI.md section 2).
            _observed[applied.Key] = applied;

            // A device that cannot be claimed at all is not advertised - but a driver-held device
            // the operator permitted a rebind for is: the P2 rebind runs when a client attaches,
            // so it belongs in the export table (CaptureState.RebindOffered).
            if (!applied.CaptureState.IsClaimable())
            {
                Exports.Remove(applied.Key, out _);
                Logger.Write(LogLevel.Detail, LogEvent.DeviceCaptureRefused,
                    $"{applied.BusId} not exported: {applied.CaptureReason.Describe()} " +
                    $"{applied.CaptureDetail}",
                    new Dictionary<string, object?>
                    {
                        ["busid"] = applied.BusId,
                        ["vid_pid"] = applied.Identity.VpKey,
                        ["state"] = applied.CaptureState.ToString(),
                        ["reason"] = applied.CaptureReason.Describe(),
                    });
                continue;
            }

            bool isNew = !Exports.Contains(applied.Key);
            Exports.AddOrUpdate(applied, generation);
            exportable++;

            Logger.Write(LogLevel.Info, LogEvent.DeviceEnumerated, applied.DescribeFound(),
                new Dictionary<string, object?>
                {
                    ["busid"] = applied.BusId,
                    ["vid"] = $"{applied.VendorId:x4}",
                    ["pid"] = $"{applied.ProductId:x4}",
                    ["speed"] = Protocol.UsbIp.SpeedName(applied.Speed),
                    ["class"] = UsbClass.Name(applied.DeviceClass),
                    ["password"] = applied.RequiresPassword,
                    ["new"] = isNew,
                });

            if (isNew)
            {
                Logger.Write(LogLevel.Info, LogEvent.DeviceExportAdded,
                    $"exporting {applied.BusId} [{applied.VendorId:x4}:{applied.ProductId:x4}] " +
                    $"\"{applied.DisplayName}\"" +
                    (applied.CaptureState.OffersRebind()
                        ? " - the function driver holds it; the P2 rebind runs when a client attaches"
                        : string.Empty),
                    new Dictionary<string, object?>
                    {
                        ["busid"] = applied.BusId,
                        ["state"] = applied.CaptureState.ToString(),
                    });
            }
        }

        // Any exported device that is no longer present is un-exported here, which is what
        // makes hotplug removal converge without a special-case removal path.
        foreach (ExportEntry entry in Exports.Snapshot())
        {
            if (seen.Contains(entry.Device.Key)) continue;

            Exports.Remove(entry.Device.Key, out _);
            Logger.Write(LogLevel.Info, LogEvent.DeviceExportRemoved,
                $"{entry.Device.BusId} is no longer present; un-exported",
                new Dictionary<string, object?> { ["busid"] = entry.Device.BusId });

            // A device that has physically gone must not stay attached to a client: the
            // holder is force-detached, which the owning session observes and tears down.
            DetachGoneDevice(entry.Device.Key, "device is no longer present");

            await RunHookAsync(HookEvent.OnDeviceUnplug, entry.Device, ct).ConfigureAwait(false);
        }

        // A device that is no longer present leaves the operator's view too, and takes its
        // capture outcome with it: a re-plugged device starts with no outcome rather than
        // inheriting the previous one's story.
        foreach (DeviceKey key in _observed.Keys)
        {
            if (seen.Contains(key)) continue;
            _observed.TryRemove(key, out _);
            CaptureOutcomes.Forget(key);
        }

        ExportCountChanged?.Invoke(exportable);
        return exportable;
    }

    /// <summary>
    /// Adds or removes a device in the export table without a full re-enumeration. Used by
    /// the hotplug watcher so an arrival or removal lands in milliseconds.
    /// </summary>
    public void ApplyHotplugChange(UsbDevice device, bool present)
    {
        ArgumentNullException.ThrowIfNull(device);
        UsbDevice applied = Policy.Apply(device);
        long generation = Interlocked.Increment(ref _generation);

        if (present)
        {
            // Observed even when it cannot be claimed: see RefreshExportsAsync.
            _observed[applied.Key] = applied;
        }
        else
        {
            _observed.TryRemove(applied.Key, out _);
            CaptureOutcomes.Forget(applied.Key);
        }

        if (present && applied.CaptureState.IsClaimable())
        {
            Exports.AddOrUpdate(applied, generation);
            Logger.Write(LogLevel.Info, LogEvent.HotplugArrival,
                $"arrival: {applied.DescribeFound()}",
                new Dictionary<string, object?> { ["busid"] = applied.BusId });
        }
        else
        {
            Exports.Remove(applied.Key, out _);
            Logger.Write(LogLevel.Info, LogEvent.HotplugRemoval,
                $"removal: {applied.BusId} [{(applied.VendorId):x4}:{(applied.ProductId):x4}]",
                new Dictionary<string, object?> { ["busid"] = applied.BusId });

            DetachGoneDevice(applied.Key, "device was removed");
        }

        ExportCountChanged?.Invoke(Exports.Count);
    }

    /// <summary>
    /// Force-detaches whichever client holds <paramref name="key"/>. Unconditional: no
    /// licence check, no permission model beyond the fact that only an operator or the local
    /// control plane can call it. This is the <c>devices.kick</c> verb.
    /// </summary>
    public bool KickDevice(DeviceKey key, out AttachRecord? record, out string reason)
    {
        if (!Attachments.Kick(key, out record))
        {
            reason = "device is not currently attached";
            return false;
        }

        reason = string.Empty;
        Logger.Write(LogLevel.Info, LogEvent.DeviceKicked,
            $"kicked {key.BusId} from connection {record!.ConnectionId} ({record.RemoteAddress})",
            new Dictionary<string, object?>
            {
                ["busid"] = key.BusId,
                ["connection_id"] = record.ConnectionId.Value,
                ["client_ip"] = record.RemoteAddress,
            });

        return true;
    }

    /// <summary>
    /// Force-detaches whoever holds <paramref name="key"/> because the device has gone away.
    /// Cancels the attach record's token; the owning session observes that and tears down.
    /// </summary>
    private void DetachGoneDevice(DeviceKey key, string why)
    {
        if (!Attachments.Kick(key, out AttachRecord? record) || record is null)
            return;

        Logger.Write(LogLevel.Info, LogEvent.DeviceKicked,
            $"force-detached {key.BusId} from connection {record.ConnectionId}: {why}",
            new Dictionary<string, object?>
            {
                ["busid"] = key.BusId,
                ["connection_id"] = record.ConnectionId.Value,
                ["client_ip"] = record.RemoteAddress,
                ["reason"] = why,
            });
    }

    /// <summary>Runs a hook for a device, if hooks are configured, and returns its verdict.</summary>
    public async Task<HookResult> RunHookAsync(
        HookEvent hook,
        UsbDevice device,
        CancellationToken ct,
        string clientId = "",
        string clientIp = "",
        bool peerAuthenticated = false,
        string newNickname = "")
    {
        if (Hooks is null) return HookResult.NotConfigured;

        var context = new HookContext
        {
            Device = device,
            ClientId = clientId,
            ClientIp = clientIp,
            DevicePath = device.SysPath,
            VendorId = $"{device.VendorId:x4}",
            ProductId = $"{device.ProductId:x4}",
            Nickname = device.Nickname ?? string.Empty,
            NewNickname = newNickname,
            PeerAuthenticated = peerAuthenticated,
        };

        return await Hooks.RunAsync(hook, context, ct).ConfigureAwait(false);
    }
}
