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
        DevicePolicy? policy = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Hooks = hooks;
        Policy = policy ?? options.BuildPolicy();
        Resources = new ConnectionResources(options.MaxInFlightUrbs, options.MaxTransferBuffer);
    }

    public ServerOptions Options { get; }
    public ICaptureProvider Capture { get; }
    public ILogger Logger { get; }
    public HookRunner? Hooks { get; }
    public DevicePolicy Policy { get; }
    public ConnectionResources Resources { get; }

    /// <summary>Every device the server is willing to export.</summary>
    public ExportTable Exports { get; } = new();

    /// <summary>Which connection holds which device. Zero or one holder per device.</summary>
    public AttachTable Attachments { get; } = new();

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
