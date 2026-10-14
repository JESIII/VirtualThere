using System.Net;
using System.Net.Sockets;

namespace Vht.Transport;

/// <summary>
/// The single place a session is created, so no caller has to know the handshake order or remember to
/// layer compression (ARCHITECTURE.md Appendix C).
/// </summary>
/// <remarks>
/// Every entry point returns a transport whose version/capability handshake has already completed, or
/// throws. In particular <see cref="AcceptAsync"/> on a TLS listener throws for a peer that skips the
/// handshake, which is the acceptance criterion "a session cannot be downgraded".
/// </remarks>
public static class TransportFactory
{
    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/> and returns a ready transport.</summary>
    public static async Task<ITransport> ConnectAsync(
        string host,
        int port,
        TransportOptions options,
        AuthRequest? credential,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var stream = client.GetStream();
            var peerIdentity = $"{host}:{port}";
            ITransport transport = options.Kind == TransportKind.PlainTcp
                ? new PlainTcpTransport(stream, peerIdentity, client.Client.RemoteEndPoint)
                : await TlsTransport.ConnectAsync(stream, peerIdentity, options, credential, ct).ConfigureAwait(false);

            return ApplyCompression(transport, options);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Wraps an already-connected stream as a client transport.</summary>
    public static async Task<ITransport> ConnectStreamAsync(
        Stream stream,
        string peerIdentity,
        TransportOptions options,
        AuthRequest? credential,
        CancellationToken ct,
        bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.Kind == TransportKind.PlainTcp)
        {
            return new PlainTcpTransport(stream, peerIdentity, (stream as IPeerInfo)?.RemoteEndPoint, ownsStream);
        }

        var transport = await TlsTransport
            .ConnectAsync(stream, peerIdentity, options, credential, ct)
            .ConfigureAwait(false);
        return ApplyCompression(transport, options);
    }

    /// <summary>
    /// Accepts one connection from <paramref name="stream"/>: TLS handshake, mandatory <c>_vht</c>
    /// handshake, and the compression decision.
    /// </summary>
    /// <exception cref="TransportException">
    /// Thrown with <see cref="TransportErrorCode.HandshakeRequired"/> when the peer does not speak the
    /// handshake, and with <see cref="TransportErrorCode.PinMismatch"/> or
    /// <see cref="TransportErrorCode.Protocol"/> for the other refusal paths.
    /// </exception>
    public static async Task<ITransport> AcceptAsync(
        Stream stream,
        TransportOptions options,
        EndPoint? remoteEndPoint,
        CancellationToken ct,
        bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.Kind == TransportKind.PlainTcp)
        {
            return new PlainTcpTransport(stream, remoteEndPoint?.ToString() ?? "unknown", remoteEndPoint, ownsStream);
        }

        var transport = await TlsTransport
            .AcceptAsync(stream, options, remoteEndPoint, ct)
            .ConfigureAwait(false);
        return ApplyCompression(transport, options);
    }

    /// <summary>
    /// Layers <see cref="Lz4Transport"/> when the configuration asked for it <em>and</em> the peer
    /// negotiated it. A peer that did not get a plain TLS session back, which is the per-peer off
    /// switch of ARCHITECTURE.md §8.
    /// </summary>
    public static ITransport ApplyCompression(ITransport transport, TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Kind != TransportKind.TlsLz4)
        {
            return transport;
        }

        if (transport is not TlsTransport tls || !tls.Lz4Negotiated)
        {
            return transport;
        }

        return new Lz4Transport(tls, options.Lz4, tls.ServerMaxPayloadFrameBytes);
    }
}

/// <summary>
/// A listening socket that yields ready transports, with the handshake performed off the accept loop so
/// one slow or hostile peer cannot stall everyone else.
/// </summary>
public sealed class TransportListener : IAsyncDisposable
{
    private readonly TransportOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly System.Threading.Channels.Channel<ITransport> _accepted =
        System.Threading.Channels.Channel.CreateBounded<ITransport>(128);
    private readonly SemaphoreSlim _handshakeSlots = new(32, 32);
    private readonly Task _acceptLoop;

    private TransportListener(TransportOptions options, TcpListener listener)
    {
        _options = options;
        _listener = listener;
        LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>Starts listening. <paramref name="address"/> defaults to IPv4 any.</summary>
    public static TransportListener Start(
        TransportOptions options,
        IPAddress? address = null,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var listener = new TcpListener(address ?? IPAddress.Any, port ?? TransportOptions.DefaultPort(options.Kind));
        listener.Start();
        return new TransportListener(options.WithSharedAuthState(), listener);
    }

    /// <summary>The bound endpoint, including the resolved port when 0 was requested.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Which flavour this listener speaks.</summary>
    public TransportKind Kind => _options.Kind;

    /// <summary>Raised when a connection was refused (bad handshake, pin mismatch, hostile framing).</summary>
    public event Action<Exception>? Rejected;

    /// <summary>Completes when the accept loop stops.</summary>
    public Task Completion => _acceptLoop;

    /// <summary>Waits for the next fully-handshaken transport.</summary>
    public async Task<ITransport> AcceptAsync(CancellationToken ct)
    {
        try
        {
            return await _accepted.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (System.Threading.Channels.ChannelClosedException ex)
        {
            throw new TransportException(TransportErrorCode.TransportFailure, "the transport listener stopped accepting", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        _accepted.Writer.TryComplete();
        _handshakeSlots.Dispose();
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    Rejected?.Invoke(ex);
                    continue;
                }

                client.NoDelay = true;
                _ = HandshakeAndPublishAsync(client, ct);
            }
        }
        catch (Exception ex)
        {
            _accepted.Writer.TryComplete(ex);
            return;
        }

        _accepted.Writer.TryComplete();
    }

    private async Task HandshakeAndPublishAsync(TcpClient client, CancellationToken ct)
    {
        await _handshakeSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = client.GetStream();
            var transport = await TransportFactory
                .AcceptAsync(stream, _options, client.Client.RemoteEndPoint, ct)
                .ConfigureAwait(false);
            await _accepted.Writer.WriteAsync(transport, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            client.Dispose();
            Rejected?.Invoke(ex);
        }
        finally
        {
            _handshakeSlots.Release();
        }
    }
}
