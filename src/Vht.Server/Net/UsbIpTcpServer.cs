using System.Net;
using System.Net.Sockets;
using Vht.Core.Concurrency;
using Vht.Core.Logging;
using Vht.Core.Server;
using Vht.Core.Transport;

namespace Vht.Server.Net;

/// <summary>
/// The USB/IP listener set: one TCP endpoint per configured transport, one session per
/// accepted connection, and no limit on how many devices may be attached across them.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no connection cap and no device cap here.</b> Every accepted client gets a
/// session and a <see cref="Connection"/> record; the only bound anywhere in the accept path is
/// the per-connection resource guard in <see cref="ConnectionResources"/>, which bounds one
/// connection's unmanaged memory and never the number of devices
/// (ARCHITECTURE.md §5.3–§5.4). The reference product's equivalent is the
/// <c>BIND MAX_DEVICES_EXCEEDED</c> refusal; this file contains no branch that can produce it.
/// </para>
/// <para>
/// The TLS listener is not implemented here: it is the transport card's
/// <see cref="ITransport"/>, injected as a factory. If TLS is enabled without a factory the
/// server refuses to start rather than silently serving plaintext — advertising encryption it
/// does not have is the one failure mode §6 forbids outright.
/// </para>
/// </remarks>
public sealed class UsbIpTcpServer : IAsyncDisposable
{
    private readonly VhtServerState _state;
    private readonly ILogger _log;
    private readonly Func<NetworkStream, TransportKind, ITransport>? _transportFactory;
    private readonly List<TcpListener> _listeners = new();
    private readonly List<Task> _acceptLoops = new();
    private readonly CancellationTokenSource _stop = new();

    public UsbIpTcpServer(
        VhtServerState state,
        Func<NetworkStream, TransportKind, ITransport>? transportFactory = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _log = state.Logger;
        _transportFactory = transportFactory;
    }

    /// <summary>Client connections accepted since start (a diagnostic, not a limit).</summary>
    public long AcceptedTotal { get; private set; }

    /// <summary>Bound endpoints, as <c>address:port (kind)</c>.</summary>
    public IReadOnlyList<string> Endpoints { get; private set; } = Array.Empty<string>();

    /// <summary>Binds every configured listener. Throws when a listener cannot be bound.</summary>
    public IReadOnlyList<string> Bind()
    {
        var endpoints = new List<string>();

        foreach ((TransportKind kind, string address, int port) in _state.Options.Listeners())
        {
            if (kind != TransportKind.PlainTcp && _transportFactory is null)
            {
                throw new InvalidOperationException(
                    $"transport.{kind} is enabled but no transport implementation was supplied; " +
                    "VirtualThere will not advertise an encrypted listener it cannot serve " +
                    "(ARCHITECTURE.md §6.3). Disable it, or start the server with the transport card's factory.");
            }

            IPAddress bind = address.StartsWith('*') || address == "0.0.0.0"
                ? IPAddress.Any
                : IPAddress.Parse(address);

            var listener = new TcpListener(bind, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();

            _listeners.Add(listener);
            endpoints.Add($"{address}:{port} ({kind})");

            _log.Write(LogLevel.Info, LogEvent.ListenerStarted,
                $"listening for USB/IP on {address}:{port} ({kind})",
                new Dictionary<string, object?>
                {
                    ["address"] = address,
                    ["port"] = port,
                    ["transport"] = kind.ToString(),
                });
        }

        Endpoints = endpoints;
        return endpoints;
    }

    /// <summary>Runs the accept loops until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);

        IReadOnlyList<(TransportKind Kind, string Address, int Port)> configured = _state.Options.Listeners();
        for (int i = 0; i < _listeners.Count; i++)
        {
            TcpListener listener = _listeners[i];
            TransportKind kind = i < configured.Count ? configured[i].Kind : TransportKind.PlainTcp;
            _acceptLoops.Add(Task.Run(() => AcceptLoopAsync(listener, kind, linked.Token), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(_acceptLoops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, TransportKind kind, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Write(LogLevel.Warn, LogEvent.ListenerFailed,
                    $"accept failed on {listener.LocalEndpoint}: {ex.Message}");
                continue;
            }

            Interlocked.Increment(ref _accepted);
            _ = Task.Run(() => HandleClientAsync(client, kind, ct), CancellationToken.None);
        }
    }

    private long _accepted;

    private async Task HandleClientAsync(TcpClient client, TransportKind kind, CancellationToken ct)
    {
        Connection? connection = null;
        ITransport? transport = null;

        try
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();

            transport = _transportFactory is not null
                ? _transportFactory(stream, kind)
                : new PlainTcpTransport(stream);

            var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
            string remote = endpoint is null ? "unknown" : $"{endpoint.Address}:{endpoint.Port}";

            connection = new Connection
            {
                Id = _state.Connections.NextId(),
                ClientUuid = Guid.NewGuid().ToString(),
                RemoteAddress = remote,
                Kind = kind,
                Cancellation = new CancellationTokenSource(),
            };

            _state.Connections.Add(connection);
            AcceptedTotal = Interlocked.Read(ref _accepted);

            var session = new UsbIpSession(_state, connection, transport);
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warn, LogEvent.ClientDisconnected,
                $"connection handling failed: {ex.Message}");
        }
        finally
        {
            if (connection is not null)
            {
                _state.Attachments.DetachAll(connection.Id);
                _state.Connections.Remove(connection.Id);
                connection.Cancellation.Dispose();
            }

            if (transport is not null)
            {
                try
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (TcpListener listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
            }
        }
        _listeners.Clear();

        _stop.Dispose();
    }
}
