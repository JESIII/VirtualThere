// SPDX-License-Identifier: Apache-2.0
//
// The client's control plane: \\.\pipe\vht-client, JSON lines, one request/response pair per line
// (ARCHITECTURE.md §1 and Appendix C). This is the *only* way the tray/UI talks to the client — the
// UI runs unelevated and never touches the driver, and the client keeps its privileges.
//
// Vocabulary follows Appendix C: `devices.list`, `clients.attach`, `clients.detach`, `peers.add`,
// `peers.list`, `server.status`, `log.tail`, plus the policy verbs the tray's menus need. Unknown
// verbs are rejected with an error object, never ignored — a UI that misspells a verb must find out.
//
// Payloads are built as anonymous objects on purpose: System.Text.Json only serialises public
// members, and these DTOs are throwaway shapes, so an anonymous type (public by construction) is the
// least error-prone option and keeps the wire format visible at the call site.

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Vht.Client.Attach;
using Vht.Client.Bridge;
using Vht.Client.Credentials;
using Vht.Client.Diagnostics;
using Vht.Client.Peers;
using Vht.Client.Policy;

namespace Vht.Client.Control;

/// <summary>Serves the client's control pipe.</summary>
internal sealed class ClientControlServer : IAsyncDisposable
{
    /// <summary>Appendix C's pipe name for the client.</summary>
    internal const string DefaultPipeName = "vht-client";

    private readonly PeerStore _peers;
    private readonly RemoteDeviceCatalog _catalog;
    private readonly AttachManager _attachments;
    private readonly AutoAttachPolicy _policy;
    private readonly CredentialCache _credentials;
    private readonly ClientLog _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _clients = new();

    private Task? _acceptLoop;
    private bool _disposed;

    internal ClientControlServer(
        PeerStore peers,
        RemoteDeviceCatalog catalog,
        AttachManager attachments,
        AutoAttachPolicy policy,
        CredentialCache credentials,
        ClientLog log,
        string? pipeName = null)
    {
        _peers = peers;
        _catalog = catalog;
        _attachments = attachments;
        _policy = policy;
        _credentials = credentials;
        _log = log;
        PipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
    }

    /// <summary>
    /// The pipe this instance serves. <see cref="DefaultPipeName"/> unless a caller overrode it -
    /// the client window passes the pipe it was told to use, so it and its worker always agree.
    /// </summary>
    internal string PipeName { get; }

    internal void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        _log.Info("control", $"listening on \\\\.\\pipe\\{PipeName}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            Task handler = Task.Run(() => HandleClientAsync(pipe, ct), CancellationToken.None);
            lock (_clients)
            {
                _clients.RemoveAll(t => t.IsCompleted);
                _clients.Add(handler);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null)
                    {
                        return;
                    }
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string response = await DispatchAsync(line, ct).ConfigureAwait(false);
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Control-plane disconnects are routine.
            }
        }
    }

    /// <summary>Handles one JSON-line request and returns the JSON-line response.</summary>
    internal async Task<string> DispatchAsync(string requestLine, CancellationToken ct)
    {
        string verb = "?";
        try
        {
            using JsonDocument document = JsonDocument.Parse(requestLine);
            JsonElement root = document.RootElement;
            verb = root.TryGetProperty("verb", out JsonElement verbElement) ? verbElement.GetString() ?? "?" : "?";

            object payload = await ExecuteAsync(verb, root, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, verb, result = payload });
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, verb, error = $"malformed JSON request: {ex.Message}" });
        }
        catch (Exception ex) when (ex is ClientException or ArgumentException or NotSupportedException)
        {
            return JsonSerializer.Serialize(new { ok = false, verb, error = ex.Message });
        }
    }

    private async Task<object> ExecuteAsync(string verb, JsonElement request, CancellationToken ct) => verb switch
    {
        "server.status" => Status(),
        "devices.list" => Devices(),
        "clients.attached" => AttachedDevices(),
        "clients.attach" => await AttachAsync(request, ct).ConfigureAwait(false),
        "clients.detach" => Detach(request),
        "clients.detachAll" => new { detached = _attachments.DetachAll() },
        "peers.list" => Peers(),
        "peers.add" => AddPeer(request),
        "peers.remove" => new { removed = _peers.Remove(RequiredString(request, "name")) },
        "policy.allow" => PolicyList(request, allow: true),
        "policy.ignore" => PolicyList(request, allow: false),
        "policy.grant" => Grant(request),
        "policy.deny" => Deny(request),
        "policy.revoke" => new { revoked = _policy.Revoke(RequiredString(request, "deviceKey")) },
        "policy.lists" => new { allow = _policy.AllowList, ignore = _policy.IgnoreList, granted = _policy.GrantedKeys },
        "credentials.remembered" => new { keys = _credentials.RememberedKeys },
        "port.capacity" => Capacity(),
        "log.tail" => new { lines = _log.Tail(OptionalInt(request, "count") ?? 200).Select(l => l.ToString()).ToArray() },
        _ => throw new NotSupportedException(
            $"unknown verb '{verb}'. Known verbs: server.status, devices.list, clients.attached, clients.attach, " +
            "clients.detach, clients.detachAll, peers.list, peers.add, peers.remove, policy.allow, policy.ignore, " +
            "policy.grant, policy.deny, policy.revoke, policy.lists, credentials.remembered, port.capacity, log.tail."),
    };

    private object Status() => new
    {
        bridgeRunning = _attachments.IsBridgeRunning,
        bridgeEndpoint = _attachments.BridgeEndpoint,
        controllerAvailable = _attachments.ControllerDescription,
        attachedCount = _attachments.AttachedCount,
        pendingCount = _attachments.PendingAttaches.Count,
        peerCount = _peers.Peers.Count,
        unavailablePeers = _catalog.UnavailablePeers.Select(u => new { peer = u.Peer.Name, reason = u.Reason }).ToArray(),
        autoAttachEnabled = true,
        transport = "plain TCP (Vht.Transport's TLS/LZ4 layer is not wired into the client yet)",
    };

    private object Devices() => new
    {
        devices = _catalog.Snapshot.Select(e => new
        {
            busid = e.RoutingKey,
            remoteBusId = e.Device.BusId,
            peer = e.Peer.Name,
            endpoint = e.Peer.Endpoint,
            vid = $"0x{e.Device.VendorId:x4}",
            pid = $"0x{e.Device.ProductId:x4}",
            deviceClass = $"0x{e.Device.DeviceClass:x2}",
            className = e.Device.ClassName,
            // The operator's nickname, when the peer implements VirtualThere's device-info
            // extension (docs/SERVER.md §8). Absent for a stock usbipd peer, which has no field
            // to carry one — the UI then falls back to the vendor/product pair.
            nickname = e.Nickname,
            speed = e.Device.Speed.ToString(),
            attached = _attachments.AttachedDevices.ContainsKey(e.DeviceKey),
            autoAttach = _policy.Evaluate(e).ShouldAutoAttach,
            policy = _policy.Evaluate(e).Reason,
        }).ToArray(),
    };

    private object AttachedDevices() => new
    {
        attached = _attachments.AttachedDevices.Values.Select(d => new
        {
            deviceKey = d.DeviceKey,
            busid = d.RoutingKey,
            peer = d.PeerName,
            port = d.Port,
            locationHash = $"0x{d.LocationHash:x8}",
            attachedUtc = d.AttachedUtc.ToString("O"),
            reason = d.Reason.ToString(),
            relayLive = d.IsRelayLive,
        }).ToArray(),
        pending = _attachments.PendingAttaches.Select(p => new
        {
            deviceKey = p.DeviceKey,
            busid = p.RoutingKey,
            peer = p.PeerName,
            notBeforeUtc = p.NotBeforeUtc.ToString("O"),
            because = p.Reason,
        }).ToArray(),
    };

    private async Task<object> AttachAsync(JsonElement request, CancellationToken ct)
    {
        string busid = RequiredString(request, "busid");
        InventoryEntry entry = _catalog.Snapshot.FirstOrDefault(e =>
            string.Equals(e.RoutingKey, busid, StringComparison.Ordinal))
            ?? throw new ClientAttachException(
                $"no device with busid '{busid}' in the current inventory. Run 'devices.list' first.");

        AttachedDevice device = await _attachments.AttachAsync(entry, AttachReason.Manual, ct).ConfigureAwait(false);
        return new { attached = true, busid = device.RoutingKey, port = device.Port };
    }

    private object Detach(JsonElement request)
    {
        string busid = RequiredString(request, "busid");
        InventoryEntry? entry = _catalog.Snapshot.FirstOrDefault(e =>
            string.Equals(e.RoutingKey, busid, StringComparison.Ordinal));

        string? deviceKey = entry?.DeviceKey
            ?? _attachments.AttachedDevices.Values
                .FirstOrDefault(d => string.Equals(d.RoutingKey, busid, StringComparison.Ordinal))?.DeviceKey;

        if (deviceKey is null)
        {
            throw new ClientAttachException($"'{busid}' is neither in the inventory nor attached");
        }

        return new { detached = _attachments.Detach(deviceKey), busid };
    }

    private object Peers() => new
    {
        peers = _peers.Peers.Select(p => new
        {
            name = p.Name,
            host = p.Host,
            port = p.Port,
            useTls = p.UseTls,
            pinned = p.SpkiPin is not null,
            source = p.Manual ? "manual" : "discovery",
        }).ToArray(),
        discovery = NullPeerDiscovery.Instance.Description,
    };

    private object AddPeer(JsonElement request)
    {
        string spec = RequiredString(request, "spec");
        bool useTls = OptionalBool(request, "useTls") ?? false;
        Peer peer = PeerStore.Parse(spec, useTls);
        _peers.Add(peer);
        return new { added = true, peer = peer.Name, endpoint = peer.Endpoint };
    }

    private object PolicyList(JsonElement request, bool allow)
    {
        string[] patterns = RequiredStringArray(request, "patterns");
        if (allow)
        {
            _policy.SetAllow(patterns);
        }
        else
        {
            _policy.SetIgnore(patterns);
        }

        IReadOnlyList<string> unrecognised = DeviceSelector.Validate(patterns);
        return new
        {
            applied = patterns,
            warnings = unrecognised.Select(p =>
                $"'{p}' does not match any supported pattern form (server, server/busid, vid:, pid:, class:, *)").ToArray(),
        };
    }

    private object Grant(JsonElement request)
    {
        InventoryEntry entry = RequireInventoryEntry(RequiredString(request, "busid"));
        _policy.Grant(entry);
        return new { granted = entry.DeviceKey };
    }

    private object Deny(JsonElement request)
    {
        InventoryEntry entry = RequireInventoryEntry(RequiredString(request, "busid"));
        _policy.Deny(entry);
        return new { denied = entry.DeviceKey };
    }

    private object Capacity()
    {
        PortCapacityReport report = _attachments.MeasurePortCapacity();
        return new
        {
            allocatedPorts = report.Allocated,
            ceilingReached = report.CeilingReached,
            detail = report.Detail,
            note = "VirtualThere imposes no device limit; this is the third-party driver's measured port ceiling " +
                   "(ARCHITECTURE.md §10 risk 3).",
        };
    }

    private InventoryEntry RequireInventoryEntry(string busid) =>
        _catalog.Snapshot.FirstOrDefault(e => string.Equals(e.RoutingKey, busid, StringComparison.Ordinal))
        ?? throw new ClientAttachException($"no device with busid '{busid}' in the current inventory");

    private static string RequiredString(JsonElement request, string name)
    {
        if (!request.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"missing required string field '{name}'");
        }
        string value = element.GetString() ?? throw new ArgumentException($"field '{name}' is null");
        if (value.Length == 0)
        {
            throw new ArgumentException($"field '{name}' is empty");
        }
        return value;
    }

    private static string[] RequiredStringArray(JsonElement request, string name)
    {
        if (!request.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"missing required array field '{name}'");
        }
        return element.EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToArray();
    }

    private static int? OptionalInt(JsonElement request, string name) =>
        request.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;

    private static bool? OptionalBool(JsonElement request, string name) =>
        request.TryGetProperty(name, out JsonElement e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // Expected.
            }
        }

        Task[] clients;
        lock (_clients)
        {
            clients = _clients.ToArray();
        }
        await Task.WhenAll(clients.Select(Swallow)).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Control connections end whenever the UI closes.
        }
    }
}
