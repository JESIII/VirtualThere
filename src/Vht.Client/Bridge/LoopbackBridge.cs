// SPDX-License-Identifier: Apache-2.0
//
// The loopback bridge: ARCHITECTURE.md §6.2. The UDE driver opens the USB/IP socket itself, in
// kernel over WSK, so TLS/LZ4 cannot live there and must terminate here. The bridge listens on
// 127.0.0.1 (never 0.0.0.0), reads the op the driver sends, decides which remote peer that busid
// belongs to, opens the server-leg transport through IClientTransportFactory, and relays bytes.
//
// Properties that matter and are enforced, not just intended:
//
//  * Bind address is 127.0.0.1 only. The driver's plaintext leg is therefore physically local and
//    can never be the remote hop.
//  * Fail closed: if the busid cannot be resolved, or the upstream connect fails, the bridge closes
//    the loopback connection. It never falls back to relaying anywhere else.
//  * Nothing counts devices. One loopback connection = one relay = one attached device, and the
//    number of concurrent relays is unbounded (a ConcurrentDictionary, not a list with a cap).
//  * Relay ends are events, not silent EOFs: the attach manager needs to know a device's transport
//    died so it can detach locally and re-attach cleanly (deliverable 5).

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Vht.Client.Credentials;
using Vht.Client.Diagnostics;
using Vht.Client.Transport;
using Vht.Client.Usbip;

namespace Vht.Client.Bridge;

/// <summary>Where the bridge listens. Default is the USB/IP conventional port, loopback-only.</summary>
internal sealed record BridgeOptions
{
    internal string Address { get; init; } = "127.0.0.1";

    /// <summary>3240 is what the upstream driver defaults to; 0 asks the OS for a free port (tests).</summary>
    internal int Port { get; init; } = 3240;

    /// <summary>
    /// The peers this client may open a connection to. The bridge uses this to refuse a listen port
    /// it would otherwise reach through its own outbound leg - see
    /// <c>LoopbackBridge.ChooseListenerPort</c>. Supplied from the configured peer list, because the
    /// live route table is empty until the first catalog refresh, long after the bind.
    /// </summary>
    internal IReadOnlyCollection<Peer> DialTargets { get; init; } = [];

    internal static BridgeOptions LoopbackDefault { get; } = new();
}

/// <summary>A live relay: one loopback connection, one attached remote device.</summary>
internal sealed record RelaySessionInfo
{
    internal required long Id { get; init; }
    internal required string RoutingKey { get; init; }
    internal required string RemoteBusId { get; init; }
    internal required string PeerName { get; init; }
    internal required string PeerEndpoint { get; init; }
    internal required DateTimeOffset StartedUtc { get; init; }
}

/// <summary>Why and how a relay ended. `UpstreamBytes`/`DownstreamBytes` are useful for the soak card.</summary>
internal sealed record RelayEndedEventArgs
{
    internal required RelaySessionInfo Session { get; init; }
    internal required string Reason { get; init; }
    internal required bool UpstreamClosed { get; init; }
    internal long UpstreamBytes { get; init; }
    internal long DownstreamBytes { get; init; }
    internal TimeSpan Duration { get; init; }
}

/// <summary>
/// The loopback bridge. Start it before any attach, stop it last; every attach the driver performs
/// is addressed to this listener.
/// </summary>
internal sealed class LoopbackBridge : IAsyncDisposable
{
    private const int RelayBufferSize = 64 * 1024;

    private readonly BridgeOptions _options;
    private readonly BusidRouter _router;
    private readonly IClientTransportFactory _transportFactory;
    private readonly IRemoteDeviceInventory _inventory;
    private readonly ClientLog _log;

    private IAttachHandshake? _handshake;

    private readonly ConcurrentDictionary<long, RelaySession> _sessions = new();
    private readonly ConcurrentDictionary<long, Task> _connectionTasks = new();
    private readonly CancellationTokenSource _shutdown = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _nextRelayId;

    internal LoopbackBridge(
        BridgeOptions options,
        BusidRouter router,
        IClientTransportFactory transportFactory,
        IRemoteDeviceInventory inventory,
        ClientLog log)
    {
        _options = options;
        _router = router;
        _transportFactory = transportFactory;
        _inventory = inventory;
        _log = log;
    }

    /// <summary>
    /// The one policy hook on the relay path: run the auth handshake on a freshly connected server
    /// leg before replaying the driver's import. Null means "skip" (the default), which is only
    /// correct for a transport with no auth capabilities — the plaintext stub.
    /// </summary>
    internal IAttachHandshake? Handshake
    {
        get => _handshake;
        set => _handshake = value;
    }

    /// <summary>Raised when a relay begins — one attached device is now live through the bridge.</summary>
    internal event EventHandler<RelaySessionInfo>? RelayStarted;

    /// <summary>Raised when a relay ends, with the reason (peer closed, loopback closed, error).</summary>
    internal event EventHandler<RelayEndedEventArgs>? RelayEnded;

    internal bool IsRunning => _listener is not null;

    internal IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    internal int RelayCount => _sessions.Count;

    internal IReadOnlyCollection<RelaySessionInfo> Sessions => _sessions.Values.Select(s => s.Info).ToArray();

    /// <summary>
    /// Starts listening. The configured port is preferred, but the bridge will not bind a port that
    /// another process holds or that it would reach by dialling one of its own peers; either way it
    /// falls back to an ephemeral port and says why. See <see cref="ChooseListenerPort"/>.
    /// </summary>
    internal void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("bridge already started");

        IPAddress address = IPAddress.Parse(_options.Address);
        if (!IPAddress.IsLoopback(address))
        {
            // Hard rule from §6.2: the plaintext leg is loopback-only. A configuration mistake here
            // would put cleartext USB/IP on the network, so it fails loudly instead.
            throw new InvalidOperationException(
                $"the loopback bridge may only bind a loopback address, not {_options.Address}.");
        }

        _listener = ChooseListenerPort(address, out string? fallbackWarning);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));

        if (fallbackWarning is not null)
        {
            _log.Warn("bridge", fallbackWarning);
        }

        _log.Info("bridge", $"listening on {address}:{LocalEndpoint!.Port} (plaintext USB/IP, loopback only); " +
                            $"server leg via {_transportFactory.Description}");
    }

    /// <summary>
    /// Picks the loopback port to listen on, preferring <see cref="BridgeOptions.Port"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default port is 3240 because that is what the upstream <c>usbip.exe</c> debug client
    /// assumes, and it is what makes <c>usbip attach -r 127.0.0.1 -b &lt;busid&gt;</c> work as a
    /// cross-check. It is <b>not</b> a contract: <see cref="Attach.AttachManager"/> passes
    /// <see cref="LocalEndpoint"/>'s port to the driver in every <c>plugin_hardware</c> request, so
    /// the product's own attach path works on whatever port was actually bound.
    /// </para>
    /// <para>
    /// Two conditions override that preference, and both end in an ephemeral bind.
    /// </para>
    /// <para>
    /// <b>The port is held.</b> The bind throws. Windows reports this as <c>WSAEACCES</c> (10013)
    /// rather than "address in use", which reads as a permissions problem and is not one.
    /// </para>
    /// <para>
    /// <b>The port is one this client dials.</b> This is the dangerous case, because it does not
    /// throw. A server and a client on the same machine means a peer at <c>127.0.0.1:3240</c> while
    /// the local server listens on <c>0.0.0.0:3240</c>; Windows accepts the bind to the more
    /// specific address and routes connections there, so the bridge's relay to that peer reaches the
    /// bridge itself. The relay opens, moves nothing in either direction, and dies on the driver's
    /// timeout, with nothing logged server-side to explain it. Only a different port avoids it, and
    /// the port is already arbitrary to everything except a human running <c>usbip.exe</c>.
    /// </para>
    /// </remarks>
    private TcpListener ChooseListenerPort(IPAddress address, out string? fallbackWarning)
    {
        fallbackWarning = null;
        int preferred = _options.Port;

        if (preferred == 0)
        {
            return BindEphemeral(address);
        }

        Peer? selfDialled = WouldDialItself(preferred)
            ? DialTargets().First(p => p.Port == preferred && IsLoopbackHost(p.Host))
            : null;

        if (selfDialled is null)
        {
            var probe = new TcpListener(address, preferred);
            try
            {
                probe.Start();
                return probe;
            }
            catch (SocketException)
            {
                probe.Stop();
            }

            TcpListener onEphemeral = BindEphemeral(address);
            fallbackWarning =
                $"port {preferred} on {address} is already in use, so the loopback bridge is on " +
                $"{Port(onEphemeral)} instead. Attaches this client performs are unaffected - each " +
                $"one names the port it actually bound - but a stand-alone `usbip.exe attach` pointed " +
                $"at this machine would still look for port {preferred}. The usual cause is a " +
                $"VirtualThere server (or another USB/IP server) already listening on " +
                $"0.0.0.0:{preferred} on this machine.";
            return onEphemeral;
        }

        TcpListener avoidingSelf = BindEphemeral(address);
        fallbackWarning =
            $"the loopback bridge is on {Port(avoidingSelf)}, not port {preferred}, because port " +
            $"{preferred} is also a peer this client dials ({selfDialled.Name} @ {selfDialled.Endpoint} " +
            $"on loopback). Listening there would be routed to the bridge itself - Windows prefers the " +
            $"specific address over the wildcard one - so the bridge's own relay would connect back " +
            $"into its own listener and hang. Attaches are unaffected: each one names the port it " +
            $"actually bound.";
        return avoidingSelf;
    }

    private TcpListener BindEphemeral(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        return listener;
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>
    /// True when listening on <paramref name="port"/> would leave the bridge reachable through its
    /// own outbound leg, because some peer it dials sits on loopback at that same port.
    /// </summary>
    private bool WouldDialItself(int port) =>
        DialTargets().Any(p => p.Port == port && IsLoopbackHost(p.Host));

    /// <summary>
    /// Every peer this client may open a connection to. Taken from the configured peer list rather
    /// than <see cref="BusidRouter.Routes"/>, because the route table is not populated until the
    /// first catalog refresh - well after this listener is bound.
    /// </summary>
    private IEnumerable<Peer> DialTargets()
    {
        foreach (Peer peer in _options.DialTargets)
        {
            yield return peer;
        }

        if (_router.DefaultPeer is { } fallback)
        {
            yield return fallback;
        }
    }

    private static bool IsLoopbackHost(string host) =>
        IPAddress.TryParse(host, out IPAddress? parsed)
            ? IPAddress.IsLoopback(parsed)
            : string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        TcpListener listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _log.Warn("bridge", $"accept failed: {ex.Message}");
                continue;
            }

            long id = Interlocked.Increment(ref _nextRelayId);
            _connectionTasks[id] = Task.Run(() => HandleConnectionAsync(client, id, ct), CancellationToken.None);
        }
    }

    /// <summary>
    /// Handles one loopback connection: read the op the driver sent, resolve it, relay.
    /// Package-internal so tests can drive it directly without a socket when that is clearer.
    /// </summary>
    private async Task HandleConnectionAsync(TcpClient client, long connectionId, CancellationToken ct)
    {
        RelaySession? session = null;
        try
        {
            client.NoDelay = true;
            using var loopback = client;
            using NetworkStream stream = client.GetStream();

            var opHeader = new byte[UsbipProtocol.OpCommonSize];
            if (!await TryReadExactAsync(stream, opHeader, ct).ConfigureAwait(false))
            {
                _log.Debug("bridge", "loopback closed before an op header arrived");
                return;
            }

            ushort version = Usbip.BigEndian.ReadU16(opHeader);
            ushort code = Usbip.BigEndian.ReadU16(opHeader.AsSpan(2));
            if (version != UsbipProtocol.Version)
            {
                _log.Warn("bridge", $"loopback peer speaks USB/IP 0x{version:x4}, expected 0x{UsbipProtocol.Version:x4}; closing");
                return;
            }

            if (code == UsbipProtocol.OpReqImport)
            {
                session = await HandleImportAsync(stream, opHeader, connectionId, ct).ConfigureAwait(false);
            }
            else if (code == UsbipProtocol.OpReqDevList)
            {
                await HandleDevListAsync(stream, opHeader, ct).ConfigureAwait(false);
            }
            else
            {
                _log.Warn("bridge", $"unexpected op-code 0x{code:x4} on the loopback leg (a URB stream without an " +
                                    "import is a protocol error); closing");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown or deliberate cancellation.
        }
        catch (Exception ex) when (ex is IOException or SocketException or UsbipProtocolException or ClientException)
        {
            _log.Warn("bridge", $"loopback connection failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (session is not null)
            {
                _sessions.TryRemove(session.Info.Id, out _);
            }
        }
    }

    /// <summary>OP_REQ_IMPORT: resolve the busid, connect the peer, relay the peer's reply verbatim.</summary>
    private async Task<RelaySession?> HandleImportAsync(
        NetworkStream loopback,
        byte[] opHeader,
        long connectionId,
        CancellationToken ct)
    {
        var rest = new byte[UsbipProtocol.ImportRequestSize - UsbipProtocol.OpCommonSize];
        if (!await TryReadExactAsync(loopback, rest, ct).ConfigureAwait(false))
        {
            _log.Debug("bridge", "loopback closed mid OP_REQ_IMPORT");
            return null;
        }

        string requested = Usbip.BigEndian.ReadCString(rest.AsSpan(0, UsbipProtocol.BusIdSize));
        BusidResolution resolution = _router.Resolve(requested);
        if (!resolution.IsFound)
        {
            // Fail closed. §6.2/§6.3: an unresolvable busid is refused, never forwarded somewhere else.
            _log.Warn("bridge", $"refusing attach: {resolution.Describe()}");
            return null;
        }

        BusidRoute route = resolution.Route!;
        IClientTransport upstream;
        try
        {
            upstream = await _transportFactory.ConnectAsync(route.Peer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ClientException)
        {
            _log.Error("bridge", $"could not reach peer '{route.Peer.Name}' ({route.Peer.Endpoint})", ex);
            return null;
        }

        await using (upstream)
        {
            // One policy hook on the relay path: authenticate the server leg before importing.
            if (_handshake is not null)
            {
                ClientAuthResult auth = await _handshake
                    .AuthenticateAsync(route.Peer, route.RemoteBusId, upstream, ct)
                    .ConfigureAwait(false);
                if (!auth.Success)
                {
                    _log.Warn("bridge",
                        $"refusing attach of {route.RoutingKey}: handshake with '{route.Peer.Name}' failed: " +
                        $"{auth.FailureReason ?? "unspecified"}");
                    return null;
                }
            }

            // Relay the driver's own request byte-for-byte, but with the peer's native busid: the
            // routing key is a local concept and must never leak onto the wire.
            var request = new byte[UsbipProtocol.ImportRequestSize];
            opHeader.CopyTo(request, 0);
            Usbip.BigEndian.WriteCString(
                request.AsSpan(UsbipProtocol.ImportBusIdOffset, UsbipProtocol.BusIdSize),
                route.RemoteBusId);
            await upstream.WriteAsync(request, ct).ConfigureAwait(false);

            var session = new RelaySession(
                new RelaySessionInfo
                {
                    Id = connectionId,
                    RoutingKey = route.RoutingKey,
                    RemoteBusId = route.RemoteBusId,
                    PeerName = route.Peer.Name,
                    PeerEndpoint = route.Peer.Endpoint,
                    StartedUtc = DateTimeOffset.UtcNow,
                });

            _sessions[session.Info.Id] = session;
            _log.Info("bridge", $"relay open: {route.RoutingKey} -> {route.Peer.Name}:{route.RemoteBusId}");
            RelayStarted?.Invoke(this, session.Info);

            await RelayAsync(loopback, upstream, session, route, ct).ConfigureAwait(false);
            return session;
        }
    }

    /// <summary>
    /// OP_REQ_DEVLIST: the bridge answers it itself by aggregating every configured peer — that is
    /// what makes one loopback listener serve many remote servers with disambiguated busids.
    /// </summary>
    private async Task HandleDevListAsync(NetworkStream loopback, byte[] opHeader, CancellationToken ct)
    {
        var reserved = new byte[UsbipProtocol.DevListRequestSize - UsbipProtocol.OpCommonSize];
        if (!await TryReadExactAsync(loopback, reserved, ct).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<InventoryEntry> entries;
        try
        {
            entries = await _inventory.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ClientException or IOException or SocketException)
        {
            _log.Warn("bridge", $"device-list refresh failed: {ex.Message}");
            entries = Array.Empty<InventoryEntry>();
        }

        var devices = entries
            .Select(e => (e.Device, e.RoutingKey))
            .ToList();

        byte[] reply = UsbipOpWriter.BuildDevListReply(devices);
        await loopback.WriteAsync(reply, ct).ConfigureAwait(false);
        _log.Debug("bridge", $"answered OP_REQ_DEVLIST with {devices.Count} device(s)");
    }

    /// <summary>Pumps bytes both ways until either side stops. Never interprets URB traffic.</summary>
    private async Task RelayAsync(
        NetworkStream loopback,
        IClientTransport upstream,
        RelaySession session,
        BusidRoute route,
        CancellationToken ct)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken relayToken = relayCts.Token;

        string reason = "closed";
        bool upstreamClosed = false;

        Task down = PumpAsync(upstream.ReadAsync, loopback.WriteAsync, session,
            RelayDirection.Downstream, relayToken);
        Task up = PumpStreamToTransportAsync(loopback, upstream, session, relayToken);

        try
        {
            Task finished = await Task.WhenAny(up, down).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
                                       or OperationCanceledException or ClientException)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            await relayCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(SwallowAsync(up), SwallowAsync(down)).ConfigureAwait(false);

            if (session.LocalClosed) { reason = "loopback leg closed"; }
            if (session.UpstreamClosed) { reason = "peer leg closed"; upstreamClosed = true; }
            if (ct.IsCancellationRequested) { reason = "client shutting down"; }
        }

        _sessions.TryRemove(session.Info.Id, out _);
        var ended = new RelayEndedEventArgs
        {
            Session = session.Info,
            Reason = reason,
            UpstreamClosed = upstreamClosed,
            UpstreamBytes = session.UpstreamBytes,
            DownstreamBytes = session.DownstreamBytes,
            Duration = DateTimeOffset.UtcNow - session.Info.StartedUtc,
        };
        _log.Info("bridge",
            $"relay closed: {route.RoutingKey} ({reason}); up {ended.UpstreamBytes} B, down {ended.DownstreamBytes} B, " +
            $"{ended.Duration.TotalSeconds:F1}s");
        RelayEnded?.Invoke(this, ended);
    }

    private async Task PumpStreamToTransportAsync(
        NetworkStream loopback,
        IClientTransport upstream,
        RelaySession session,
        CancellationToken ct)
    {
        byte[] buffer = new byte[RelayBufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await loopback.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    session.LocalClosed = true;
                    return;
                }

                await upstream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                session.AddUpstream(n);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            session.LocalClosed = true;
            throw;
        }
    }

    private async Task PumpAsync(
        Func<Memory<byte>, CancellationToken, ValueTask<int>> read,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        RelaySession session,
        RelayDirection direction,
        CancellationToken ct)
    {
        byte[] buffer = new byte[RelayBufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await read(buffer, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    if (direction == RelayDirection.Downstream) session.UpstreamClosed = true;
                    return;
                }

                await write(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                session.Add(direction, n);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (direction == RelayDirection.Downstream) session.UpstreamClosed = true;
            throw;
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
                                       or OperationCanceledException or ClientException)
        {
            // Expected during relay teardown.
        }
    }

    private enum RelayDirection
    {
        Upstream,
        Downstream,
    }

    /// <summary>Stops accepting and closes every relay, then waits for the connection tasks.</summary>
    internal async ValueTask StopAsync()
    {
        if (_listener is null) return;

        _log.Info("bridge", "stopping");
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            await SwallowAsync(_acceptLoop).ConfigureAwait(false);
        }

        Task[] tasks = _connectionTasks.Values.ToArray();
        _connectionTasks.Clear();
        await Task.WhenAll(tasks.Select(SwallowAsync)).ConfigureAwait(false);
        _sessions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private static async Task<bool> TryReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>Per-relay mutable state and byte counters.</summary>
    private sealed class RelaySession
    {
        internal RelaySession(RelaySessionInfo info) => Info = info;

        internal RelaySessionInfo Info { get; }

        internal long UpstreamBytes => Volatile.Read(ref _upstreamBytes);

        internal long DownstreamBytes => Volatile.Read(ref _downstreamBytes);

        internal bool UpstreamClosed { get; set; }

        internal bool LocalClosed { get; set; }

        private long _upstreamBytes;
        private long _downstreamBytes;

        internal void AddUpstream(int n) => Interlocked.Add(ref _upstreamBytes, n);

        internal void Add(RelayDirection direction, int n)
        {
            if (direction == RelayDirection.Upstream)
            {
                Interlocked.Add(ref _upstreamBytes, n);
            }
            else
            {
                Interlocked.Add(ref _downstreamBytes, n);
            }
        }
    }
}
