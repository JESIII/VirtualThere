using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Vht.Ui.Model;

namespace Vht.Ui.ControlPlane;

public sealed class ServerStatusSnapshot
{
    public bool Running { get; init; }

    public string? ServerName { get; init; }

    public int? PlaintextPort { get; init; }

    public int? TlsPort { get; init; }

    public int ExportedDevices { get; init; }

    public int AttachedClients { get; init; }

    public string Describe()
    {
        if (!Running)
        {
            return "Stopped";
        }

        var ports = new List<string>(2);
        if (PlaintextPort is > 0)
        {
            ports.Add("plaintext :" + PlaintextPort.Value);
        }

        if (TlsPort is > 0)
        {
            ports.Add("TLS :" + TlsPort.Value);
        }

        return ports.Count == 0 ? "Running" : "Running (" + string.Join(", ", ports) + ")";
    }
}

public sealed class LogEntry
{
    public long Sequence { get; init; }

    public string Text { get; init; } = string.Empty;
}

/// <summary>Result of a status exchange: payload, or why there is none.</summary>
public sealed class StatusResult
{
    public StatusResult(ControlPlaneStatus status, ServerStatusSnapshot? snapshot, ControlPlaneError? error)
    {
        Status = status;
        Snapshot = snapshot;
        Error = error;
    }

    public ControlPlaneStatus Status { get; }

    public ServerStatusSnapshot? Snapshot { get; }

    public ControlPlaneError? Error { get; }
}

/// <summary>
/// Typed façade over the control plane. This is the only place that knows the JSON shapes of
/// the frozen verbs; view models only see snapshots, and tests substitute
/// <see cref="IControlPlane"/> instead.
/// </summary>
public sealed class VhtServiceClient
{
    private readonly IControlPlane _plane;

    public VhtServiceClient(IControlPlane plane)
    {
        _plane = plane ?? throw new ArgumentNullException(nameof(plane));
    }

    public string PipeName => _plane.PipeName;

    public async Task<StatusResult> GetServerStatusAsync(CancellationToken ct)
    {
        ControlPlaneResponse response = await _plane.SendAsync(ControlPlaneVerbs.ServerStatus, null, ct)
            .ConfigureAwait(false);
        if (!response.IsOk)
        {
            return new StatusResult(response.Status, null, response.Error);
        }

        JsonNode? node = response.Result;
        return new StatusResult(ControlPlaneStatus.Ok, new ServerStatusSnapshot
        {
            Running = JsonRead.Bool(node, "running", true) && !JsonRead.Bool(node, "stopped"),
            ServerName = JsonRead.String(node, "serverName") ?? JsonRead.String(node, "name"),
            PlaintextPort = JsonRead.Int(node, "plaintextPort", JsonRead.Int(node, "port")),
            TlsPort = JsonRead.Int(node, "tlsPort", JsonRead.Int(node, "sslPort")),
            ExportedDevices = JsonRead.Int(node, "exportedDevices"),
            AttachedClients = JsonRead.Int(node, "attachedClients"),
        }, null);
    }

    public async Task<(ControlPlaneStatus Status, IReadOnlyList<DeviceSnapshot> Devices, ControlPlaneError? Error)>
        ListDevicesAsync(CancellationToken ct)
    {
        ControlPlaneResponse response = await _plane.SendAsync(ControlPlaneVerbs.DevicesList, null, ct)
            .ConfigureAwait(false);
        if (!response.IsOk)
        {
            return (response.Status, Array.Empty<DeviceSnapshot>(), response.Error);
        }

        var list = new List<DeviceSnapshot>();
        JsonArray? array = JsonRead.Array(response.Result, "devices", "exports");
        if (array is not null)
        {
            int index = 0;
            foreach (JsonNode? item in array)
            {
                if (item is null)
                {
                    continue;
                }

                list.Add(ToDevice(item, index++));
            }
        }

        return (ControlPlaneStatus.Ok, list, null);
    }

    private static DeviceSnapshot ToDevice(JsonNode item, int index)
    {
        return new DeviceSnapshot
        {
            Key = JsonRead.String(item, "key") ?? JsonRead.String(item, "busid") ?? "device-" + index,
            BusId = JsonRead.String(item, "busid") ?? JsonRead.String(item, "busId"),
            Nickname = JsonRead.String(item, "nickname"),
            VendorId = JsonRead.Int(item, "vid", JsonRead.Int(item, "vendorId")),
            ProductId = JsonRead.Int(item, "pid", JsonRead.Int(item, "productId")),
            Vendor = JsonRead.String(item, "vendor"),
            Product = JsonRead.String(item, "product"),
            AttachedClients = JsonRead.Int(item, "attachedClients", JsonRead.Int(item, "clients")),
            CaptureState = CaptureText.ParseState(JsonRead.String(item, "captureState")),
            CaptureReason = CaptureText.ParseReason(JsonRead.String(item, "captureReason")),
            Ignored = JsonRead.Bool(item, "ignored"),
            PasswordProtected = JsonRead.Bool(item, "passwordProtected") || JsonRead.Bool(item, "password"),
        };
    }

    public Task<ControlPlaneResponse> KickAsync(string key, CancellationToken ct) =>
        _plane.SendAsync(ControlPlaneVerbs.DevicesKick, Args(("key", key)), ct);

    public Task<ControlPlaneResponse> SetIgnoredAsync(string key, bool ignored, CancellationToken ct) =>
        _plane.SendAsync(
            ignored ? ControlPlaneVerbs.DevicesIgnore : ControlPlaneVerbs.DevicesUnignore,
            Args(("key", key)),
            ct);

    public Task<ControlPlaneResponse> SetNicknameAsync(string key, string? nickname, CancellationToken ct) =>
        _plane.SendAsync(
            ControlPlaneVerbs.DevicesNickname,
            Args(("key", key), ("nickname", nickname ?? string.Empty)),
            ct);

    /// <summary>
    /// Sets or clears the per-device password. A <c>null</c> password clears it; the frozen
    /// verb set has no separate "remove password" verb, so clearing is expressed as an
    /// explicit null rather than as an empty string (docs/UI.md, "Per-device password").
    /// </summary>
    public Task<ControlPlaneResponse> SetPasswordAsync(string key, string? password, CancellationToken ct) =>
        _plane.SendAsync(
            ControlPlaneVerbs.DevicesPassword,
            Args(("key", key), ("password", password)),
            ct);

    public Task<ControlPlaneResponse> StartServerAsync(CancellationToken ct) =>
        _plane.SendAsync(ControlPlaneVerbs.ServerStart, null, ct);

    public Task<ControlPlaneResponse> StopServerAsync(CancellationToken ct) =>
        _plane.SendAsync(ControlPlaneVerbs.ServerStop, null, ct);

    public async Task<(ControlPlaneStatus Status, IReadOnlyList<LogEntry> Lines, ControlPlaneError? Error)>
        TailLogAsync(int maxLines, CancellationToken ct)
    {
        ControlPlaneResponse response = await _plane
            .SendAsync(ControlPlaneVerbs.LogTail, Args(("max", maxLines)), ct)
            .ConfigureAwait(false);
        if (!response.IsOk)
        {
            return (response.Status, Array.Empty<LogEntry>(), response.Error);
        }

        var entries = new List<LogEntry>();
        JsonArray? array = JsonRead.Array(response.Result, "lines", "entries", "log");
        if (array is not null)
        {
            int index = 0;
            foreach (JsonNode? item in array)
            {
                if (item is null)
                {
                    continue;
                }

                if (item.GetValueKind() == System.Text.Json.JsonValueKind.String)
                {
                    entries.Add(new LogEntry { Sequence = index++, Text = item.GetValue<string>() });
                    continue;
                }

                entries.Add(new LogEntry
                {
                    Sequence = JsonRead.Int(item, "seq", JsonRead.Int(item, "sequence", index++)),
                    Text = FormatEntry(item),
                });
            }
        }

        return (ControlPlaneStatus.Ok, entries, null);
    }

    private static string FormatEntry(JsonNode item)
    {
        string? message = JsonRead.String(item, "message") ?? JsonRead.String(item, "text");
        if (message is null)
        {
            return item.ToJsonString();
        }

        string? timestamp = JsonRead.String(item, "timestamp") ?? JsonRead.String(item, "ts");
        string? level = JsonRead.String(item, "level");
        string prefix = string.Empty;
        if (!string.IsNullOrWhiteSpace(timestamp))
        {
            prefix += "[" + timestamp + "] ";
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            prefix += level!.ToUpperInvariant() + " ";
        }

        return prefix + message;
    }

    public async Task<(ControlPlaneStatus Status, IReadOnlyList<PeerSnapshot> Peers, ControlPlaneError? Error)>
        ListPeersAsync(CancellationToken ct)
    {
        ControlPlaneResponse response = await _plane.SendAsync(ControlPlaneVerbs.PeersList, null, ct)
            .ConfigureAwait(false);
        if (!response.IsOk)
        {
            return (response.Status, Array.Empty<PeerSnapshot>(), response.Error);
        }

        var peers = new List<PeerSnapshot>();
        JsonArray? array = JsonRead.Array(response.Result, "peers", "servers");
        if (array is not null)
        {
            int index = 0;
            foreach (JsonNode? item in array)
            {
                if (item is null)
                {
                    continue;
                }

                peers.Add(ToPeer(item, index++));
            }
        }

        return (ControlPlaneStatus.Ok, peers, null);
    }

    private static PeerSnapshot ToPeer(JsonNode item, int index)
    {
        var devices = new List<RemoteDeviceSnapshot>();
        if (JsonRead.Array(item, "devices", "exports") is { } array)
        {
            foreach (JsonNode? device in array)
            {
                if (device is null)
                {
                    continue;
                }

                devices.Add(new RemoteDeviceSnapshot
                {
                    BusId = JsonRead.String(device, "busid") ?? JsonRead.String(device, "busId") ?? "1-1",
                    Nickname = JsonRead.String(device, "nickname"),
                    Product = JsonRead.String(device, "product"),
                    VendorId = JsonRead.Int(device, "vid", JsonRead.Int(device, "vendorId")),
                    ProductId = JsonRead.Int(device, "pid", JsonRead.Int(device, "productId")),
                    Attached = JsonRead.Bool(device, "attached"),
                    AutoAttach = JsonRead.Bool(device, "autoAttach", JsonRead.Bool(device, "auto")),
                    PasswordRequired = JsonRead.Bool(device, "passwordRequired"),
                    Status = JsonRead.String(device, "status"),
                });
            }
        }

        string? address = JsonRead.String(item, "host") ?? JsonRead.String(item, "address");
        return new PeerSnapshot
        {
            Id = JsonRead.String(item, "id") ?? address ?? "peer-" + index,
            Name = JsonRead.String(item, "name"),
            Address = address,
            Port = JsonRead.Int(item, "port"),
            Status = JsonRead.String(item, "status"),
            Online = JsonRead.Bool(item, "online", JsonRead.Bool(item, "connected")),
            Manual = JsonRead.Bool(item, "manual"),
            Devices = devices,
        };
    }

    public Task<ControlPlaneResponse> AddPeerAsync(string host, int? port, string? name, CancellationToken ct) =>
        _plane.SendAsync(
            ControlPlaneVerbs.PeersAdd,
            Args(("host", host), ("port", port), ("name", name)),
            ct);

    /// <summary>
    /// Attaches or detaches one exported device of one peer.
    /// </summary>
    public Task<ControlPlaneResponse> SetAttachmentAsync(
        string peerId,
        string busId,
        bool attach,
        CancellationToken ct) =>
        _plane.SendAsync(
            attach ? ControlPlaneVerbs.ClientsAttach : ControlPlaneVerbs.ClientsDetach,
            Args(("peer", peerId), ("busid", busId)),
            ct);

    /// <summary>
    /// Records or revokes the per-device standing grant the architecture requires before the
    /// client may attach a device on its own (docs/ARCHITECTURE.md section 7). It rides on the
    /// frozen <c>clients.attach</c> verb as an <c>auto</c> argument rather than introducing a
    /// verb that is not in Appendix C.
    /// </summary>
    public Task<ControlPlaneResponse> SetAutoAttachAsync(string peerId, string busId, bool autoAttach, CancellationToken ct) =>
        _plane.SendAsync(
            ControlPlaneVerbs.ClientsAttach,
            Args(("peer", peerId), ("busid", busId), ("auto", autoAttach)),
            ct);

    internal static IReadOnlyDictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var dictionary = new Dictionary<string, object?>(pairs.Length, StringComparer.Ordinal);
        foreach ((string key, object? value) in pairs)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }
}
