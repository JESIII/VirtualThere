using System.Net;
using System.Net.Sockets;
using Vht.Core.Server;
using Vht.Server.Net;
using Vht.Transport;
using CoreTransport = Vht.Core.Transport;

namespace Vht.Acceptance;

/// <summary>
/// Bridges the transport card's <see cref="Vht.Transport.ITransport"/> onto the frozen server-side
/// <see cref="Vht.Core.Transport.ITransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two interfaces are structurally identical — same member names, same signatures — and differ
/// only in the type of their credential/result records. The adapter is therefore a mapping, not a
/// behaviour change.
/// </para>
/// <para>
/// <b>Finding.</b> <see cref="UsbIpTcpServer"/> takes its factory as
/// <c>Func&lt;NetworkStream, TransportKind, ITransport&gt;</c> — <i>synchronous</i> — while
/// <see cref="TransportFactory.AcceptAsync"/> is asynchronous, because the TLS handshake, the
/// <c>_vht</c> capability handshake and the authentication exchange all are. The rig therefore has
/// to block on the handshake inside the factory (<see cref="BlockingServerTransportFactory"/>),
/// which costs a thread pool thread per connection for the duration of the handshake. It works, and
/// it is what a shipping server binary would have to do today too; the follow-up is a
/// <c>Func&lt;NetworkStream, TransportKind, CancellationToken, Task&lt;ITransport&gt;&gt;</c> factory.
/// </para>
/// </remarks>
public sealed class CoreTransportAdapter : CoreTransport.ITransport, CoreTransport.IPayloadKindTransport
{
    private readonly Vht.Transport.ITransport _inner;

    public CoreTransportAdapter(Vht.Transport.ITransport inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public CoreTransport.TransportKind Kind => _inner.Kind switch
    {
        Vht.Transport.TransportKind.PlainTcp => CoreTransport.TransportKind.PlainTcp,
        Vht.Transport.TransportKind.Tls => CoreTransport.TransportKind.Tls,
        Vht.Transport.TransportKind.TlsLz4 => CoreTransport.TransportKind.TlsLz4,
        _ => throw new NotSupportedException($"unknown transport kind {_inner.Kind}"),
    };

    public CoreTransport.TransportCapabilities Capabilities
    {
        get
        {
            CoreTransport.TransportCapabilities caps = CoreTransport.TransportCapabilities.None;
            if ((_inner.Capabilities & Vht.Transport.TransportCapabilities.HandshakeRequired) != 0)
                caps |= CoreTransport.TransportCapabilities.HandshakeRequired;
            if ((_inner.Capabilities & Vht.Transport.TransportCapabilities.Lz4) != 0)
                caps |= CoreTransport.TransportCapabilities.Lz4;
            if ((_inner.Capabilities & Vht.Transport.TransportCapabilities.DevicePassword) != 0)
                caps |= CoreTransport.TransportCapabilities.DevicePassword;
            if ((_inner.Capabilities & Vht.Transport.TransportCapabilities.SharedSecret) != 0)
                caps |= CoreTransport.TransportCapabilities.SharedSecret;
            return caps;
        }
    }

    /// <summary>Raw statistics from the wrapped transport, for the TLS/LZ4 overhead numbers.</summary>
    public TransportStatistics? Statistics => _inner switch
    {
        Lz4Transport lz4 => lz4.Statistics,
        TlsTransport tls => tls.Statistics,
        PlainTcpTransport plain => plain.Statistics,
        _ => null,
    };

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        _inner.ReadAsync(buffer, ct);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) =>
        _inner.WriteAsync(buffer, ct);

    // ---- payload classification (Vht.Core.Transport.IPayloadKindTransport) ------------------

    /// <summary>
    /// True only when the wrapped transport is the LZ4 decorator, i.e. when LZ4 was both configured
    /// and negotiated. Every other inner transport reports false, so the session takes its single
    /// whole-frame write and the plain TLS arm of a measurement is unchanged by this adapter.
    /// </summary>
    public bool CompressionActive =>
        (_inner as Vht.Transport.IPayloadCompressionTransport)?.CompressionActive ?? false;

    /// <inheritdoc />
    public int CompressionThresholdBytes =>
        (_inner as Vht.Transport.IPayloadCompressionTransport)?.CompressionThresholdBytes ?? int.MaxValue;

    /// <summary>
    /// Forwards a classified write to the LZ4 decorator's <c>WritePayloadAsync</c> (the two
    /// <c>PayloadKind</c> enums are member-for-member identical, so this is a cast), and falls back
    /// to a plain write for an inner transport that cannot compress — which is what the session
    /// already does on its own, so the fallback is a belt, not a behaviour.
    /// </summary>
    public ValueTask WritePayloadAsync(
        CoreTransport.PayloadKind payloadKind, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        _inner is Vht.Transport.IPayloadCompressionTransport compression
            ? compression.WritePayloadAsync((Vht.Transport.PayloadKind)payloadKind, payload, ct)
            : _inner.WriteAsync(payload, ct);

    public async Task<CoreTransport.AuthResult> AuthenticateAsync(
        CoreTransport.AuthRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mapped = request.DevicePassword is not null
            ? Vht.Transport.AuthRequest.ForDevicePassword(string.Empty, request.DevicePassword)
            : request.ServerSecret.Length > 0
                ? Vht.Transport.AuthRequest.ForSharedSecret(request.ServerSecret)
                : Vht.Transport.AuthRequest.None;

        Vht.Transport.AuthResult result = await _inner.AuthenticateAsync(mapped, ct).ConfigureAwait(false);

        return result.Status switch
        {
            Vht.Transport.AuthStatus.Success => CoreTransport.AuthResult.Success(),
            Vht.Transport.AuthStatus.Throttled => CoreTransport.AuthResult.Limited(result.Message ?? "throttled"),
            _ => CoreTransport.AuthResult.Failure(result.Message ?? result.Status.ToString()),
        };
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// The synchronous factory <see cref="UsbIpTcpServer"/> insists on, wrapping the asynchronous
/// <see cref="TransportFactory.AcceptAsync"/>.
/// </summary>
public sealed class BlockingServerTransportFactory
{
    private readonly List<CoreTransportAdapter> _accepted = new();
    private readonly object _gate = new();

    /// <summary>Handshake failures per connection, so a refusal is visible rather than silent.</summary>
    public int Refusals => Volatile.Read(ref _refusals);
    private int _refusals;

    /// <summary>
    /// The transports this factory has handed to the server, in accept order. The sweep verb reads the
    /// per-session LZ4 counters from here, which is the only way to prove from outside the session
    /// whether compression actually engaged on the wire.
    /// </summary>
    public IReadOnlyList<CoreTransportAdapter> Accepted
    {
        get { lock (_gate) return _accepted.ToArray(); }
    }

    /// <summary>Drops the recorded transports, so one measurement case cannot read another's counters.</summary>
    public void ResetAccepted()
    {
        lock (_gate) _accepted.Clear();
    }

    public Func<NetworkStream, CoreTransport.TransportKind, CoreTransport.ITransport> Build(
        TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        TransportOptions shared = options.WithSharedAuthState();

        return (stream, _) => Accept(stream, shared);
    }

    private CoreTransport.ITransport Accept(NetworkStream stream, TransportOptions options)
    {
        try
        {
            EndPoint? remote = stream.Socket?.RemoteEndPoint;
            Vht.Transport.ITransport transport = Task.Run(
                    () => TransportFactory.AcceptAsync(stream, options, remote, CancellationToken.None))
                .GetAwaiter()
                .GetResult();
            var adapter = new CoreTransportAdapter(transport);
            lock (_gate) _accepted.Add(adapter);
            return adapter;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _refusals);
            throw new InvalidOperationException($"transport handshake refused: {ex.Message}", ex);
        }
    }
}
