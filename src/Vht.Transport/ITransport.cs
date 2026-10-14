using System.Runtime.CompilerServices;

namespace Vht.Transport;

/// <summary>
/// The three transport flavours VirtualThere ships (ARCHITECTURE.md Appendix C).
/// </summary>
/// <remarks>
/// Rule fixed by the architecture: <see cref="TlsLz4"/> may not exist without <see cref="Tls"/>.
/// The LZ4 layer is a pure decorator over a live TLS session and cannot be constructed otherwise.
/// </remarks>
public enum TransportKind : byte
{
    /// <summary>Stock USB/IP over raw TCP. No handshake, no authentication, no compression.</summary>
    PlainTcp = 0,

    /// <summary>USB/IP inside TLS 1.3 with the mandatory VirtualThere capability handshake.</summary>
    Tls = 1,

    /// <summary>TLS, plus negotiated LZ4 block compression on bulk/interrupt payload frames.</summary>
    TlsLz4 = 2,
}

/// <summary>
/// What a session can do, as negotiated. <see cref="PlainTcp"/> sessions always report <see cref="None"/>.
/// </summary>
[Flags]
public enum TransportCapabilities : uint
{
    None = 0,

    /// <summary>The peer completed the version/capability handshake. Mandatory for TLS kinds.</summary>
    HandshakeRequired = 1u << 0,

    /// <summary>LZ4 block compression is negotiated and active for payload frames.</summary>
    Lz4 = 1u << 1,

    /// <summary>The server accepts per-device passwords as an authentication method.</summary>
    DevicePassword = 1u << 2,

    /// <summary>The server accepts the server-wide shared secret as an authentication method.</summary>
    SharedSecret = 1u << 3,

    /// <summary>The peer identity is pinned (SPKI SHA-256) rather than validated by a CA chain.</summary>
    PeerPinning = 1u << 4,

    /// <summary>Isochronous transfers are negotiated as supported by both ends (best-effort, see risk 6).</summary>
    Isochronous = 1u << 5,
}

/// <summary>
/// The USB/IP-facing duplex byte transport. Shape frozen in ARCHITECTURE.md Appendix C.
/// </summary>
/// <remarks>
/// <para>
/// Wire behaviour: the byte stream carried by <see cref="ReadAsync"/>/<see cref="WriteAsync"/> is the
/// USB/IP framing owned by <c>Vht.Core</c>. The transport never inspects it.
/// </para>
/// <para>
/// For <see cref="TransportKind.Tls"/> and <see cref="TransportKind.TlsLz4"/> the version/capability
/// handshake is performed <em>before</em> the instance is handed to the caller (by
/// <see cref="TransportFactory"/>), and it is mandatory: a peer that does not speak it is rejected, so
/// a session cannot be downgraded. <see cref="AuthenticateAsync"/> is then called once, before any
/// device traffic; a TLS transport whose negotiated method requires authentication refuses
/// <see cref="ReadAsync"/>/<see cref="WriteAsync"/> until it has authenticated.
/// </para>
/// </remarks>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Which flavour this session is.</summary>
    TransportKind Kind { get; }

    /// <summary>What was negotiated. <see cref="TransportCapabilities.None"/> for <see cref="TransportKind.PlainTcp"/>.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>Read bytes into <paramref name="buffer"/>. Returns 0 only at end of stream.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Write all of <paramref name="buffer"/>.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);

    /// <summary>
    /// Run the authentication exchange. Must be called before device traffic on authenticated sessions.
    /// Fails closed: an implementation with no authentication path returns
    /// <see cref="AuthStatus.Unauthorized"/>, never a silent success.
    /// </summary>
    Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct);
}

/// <summary>
/// Implemented by transports that can compress individual payload frames (ARCHITECTURE.md §8).
/// </summary>
/// <remarks>
/// Only <see cref="TransportKind.TlsLz4"/> implements this. Compression is opt-in per call: the plain
/// <see cref="ITransport.WriteAsync"/> path is always raw, so a caller that does not classify its data
/// can never accidentally have a USB/IP header compressed.
/// </remarks>
public interface IPayloadCompressionTransport : ITransport
{
    /// <summary>True when LZ4 is negotiated and this session may compress.</summary>
    bool CompressionActive { get; }

    /// <summary>Byte threshold below which compression is not attempted. Parity with the reference product's <c>CompressionLimit</c>.</summary>
    int CompressionThresholdBytes { get; }

    /// <summary>
    /// Write one logical USB/IP payload frame. Compression is applied only for
    /// <see cref="PayloadKind.Bulk"/>/<see cref="PayloadKind.Interrupt"/> payloads at or above the
    /// threshold, and only when it actually wins.
    /// </summary>
    ValueTask WritePayloadAsync(PayloadKind payloadKind, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

/// <summary>Which USB transfer class a payload belongs to. Decides whether LZ4 may be applied.</summary>
public enum PayloadKind : byte
{
    /// <summary>Control transfers: never compressed (small, latency-critical).</summary>
    Control = 0,

    /// <summary>Bulk transfers: compressed above the threshold.</summary>
    Bulk = 1,

    /// <summary>Interrupt transfers: compressed above the threshold.</summary>
    Interrupt = 2,

    /// <summary>Isochronous transfers: never compressed (small per-packet, deadline-driven).</summary>
    Isochronous = 3,
}

/// <summary>Optional peer description, additive to the frozen <see cref="ITransport"/> shape.</summary>
public interface IPeerInfo
{
    /// <summary>Stable label for logging and rate limiting, e.g. <c>10.0.0.4:51234</c>.</summary>
    string PeerLabel { get; }

    /// <summary>Remote address, when the transport is socket-backed.</summary>
    System.Net.EndPoint? RemoteEndPoint { get; }
}

/// <summary>How a session proves it knows a secret.</summary>
public enum AuthMethod : byte
{
    /// <summary>No credential required (plaintext sessions, or TLS with nothing configured).</summary>
    None = 0,

    /// <summary>Server-wide shared secret.</summary>
    SharedSecret = 1,

    /// <summary>Per-device password, addressed by busid.</summary>
    DevicePassword = 2,
}

/// <summary>Outcome of an authentication attempt. Deliberately shaped to map onto <c>Vht.Core.BindStatus</c>.</summary>
public enum AuthStatus : int
{
    Success = 0,

    /// <summary>Credential rejected. Fail closed.</summary>
    Unauthorized = 1,

    /// <summary>A device password must be supplied for this busid.</summary>
    PasswordRequired = 2,

    /// <summary>Rate limited or locked out. A peer may retry after <see cref="AuthResult.RetryAfter"/>.</summary>
    Throttled = 3,

    /// <summary>The requested method is not supported by this peer.</summary>
    NotSupported = 4,

    /// <summary>The authentication exchange was malformed or out of order.</summary>
    Malformed = 5,

    /// <summary>Internal failure; never treated as success.</summary>
    Error = 6,
}

/// <summary>Credentials a client presents. The server side ignores the credential fields (it reads them from the wire).</summary>
public sealed record AuthRequest
{
    /// <summary>Method to use.</summary>
    public AuthMethod Method { get; init; } = AuthMethod.None;

    /// <summary>Busid of the device being imported, for <see cref="AuthMethod.DevicePassword"/>.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Server-wide shared secret, for <see cref="AuthMethod.SharedSecret"/>.</summary>
    public string? SharedSecret { get; init; }

    /// <summary>Per-device password, for <see cref="AuthMethod.DevicePassword"/>.</summary>
    public string? DevicePassword { get; init; }

    /// <summary>No credential.</summary>
    public static AuthRequest None { get; } = new();

    /// <summary>Shared-secret credentials.</summary>
    public static AuthRequest ForSharedSecret(string sharedSecret) =>
        new() { Method = AuthMethod.SharedSecret, SharedSecret = sharedSecret };

    /// <summary>Per-device password credentials.</summary>
    public static AuthRequest ForDevicePassword(string deviceId, string password) =>
        new() { Method = AuthMethod.DevicePassword, DeviceId = deviceId, DevicePassword = password };
}

/// <summary>Result of an authentication attempt.</summary>
public sealed record AuthResult
{
    /// <summary>Outcome.</summary>
    public required AuthStatus Status { get; init; }

    /// <summary>Method that was used (or the method the server required, on failure).</summary>
    public AuthMethod Method { get; init; } = AuthMethod.None;

    /// <summary>Human-readable detail for logs. Never contains credential material.</summary>
    public string? Message { get; init; }

    /// <summary>When <see cref="Status"/> is <see cref="AuthStatus.Throttled"/>, how long until a retry may succeed.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>True only for <see cref="AuthStatus.Success"/>.</summary>
    public bool IsSuccess => Status == AuthStatus.Success;

    /// <summary>Success with no credential needed.</summary>
    public static AuthResult NoneRequired { get; } = new() { Status = AuthStatus.Success, Method = AuthMethod.None };

    /// <summary>Successful authentication.</summary>
    public static AuthResult Success(AuthMethod method) => new() { Status = AuthStatus.Success, Method = method };

    /// <summary>Rejected.</summary>
    public static AuthResult Unauthorized(AuthMethod method, string message) =>
        new() { Status = AuthStatus.Unauthorized, Method = method, Message = message };

    /// <summary>Rate limited.</summary>
    public static AuthResult Throttled(AuthMethod method, TimeSpan retryAfter, string message) =>
        new() { Status = AuthStatus.Throttled, Method = method, RetryAfter = retryAfter, Message = message };

    /// <summary>Method not supported by this peer.</summary>
    public static AuthResult NotSupported(AuthMethod method, string message) =>
        new() { Status = AuthStatus.NotSupported, Method = method, Message = message };
}

/// <summary>Coarse classification of a transport failure, so callers can react without string matching.</summary>
public enum TransportErrorCode
{
    /// <summary>Peer sent something that violates the wire format.</summary>
    Protocol = 0,

    /// <summary>The mandatory version/capability handshake was skipped, malformed or disagreed on version.</summary>
    HandshakeRequired = 1,

    /// <summary>Authentication failed and the session was closed.</summary>
    AuthenticationFailed = 2,

    /// <summary>Rate limited.</summary>
    Throttled = 3,

    /// <summary>A CRC32C or decompression check failed: the frame stream is corrupt or tampered with.</summary>
    IntegrityCheckFailed = 4,

    /// <summary>A frame declared a length outside the configured bounds.</summary>
    FrameTooLarge = 5,

    /// <summary>Peer certificate did not match the pinned SPKI, or pinning was required and no pin exists.</summary>
    PinMismatch = 6,

    /// <summary>Underlying socket/TLS failure.</summary>
    TransportFailure = 7,

    /// <summary>The requested feature or method is not supported.</summary>
    NotSupported = 8,

    /// <summary>The operation is not legal in the current connection state.</summary>
    InvalidState = 9,
}

/// <summary>Thrown for every protocol/security failure on a transport.</summary>
public sealed class TransportException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TransportException(TransportErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    /// <summary>Coarse failure class.</summary>
    public TransportErrorCode Code { get; }

    /// <summary>Throws if <paramref name="condition"/> is false.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIf(bool condition, TransportErrorCode code, string message)
    {
        if (!condition)
        {
            throw new TransportException(code, message);
        }
    }
}

/// <summary>Counters for one session. Used by the benchmark and by the server's logging.</summary>
public sealed class TransportStatistics
{
    private long _bytesWritten;
    private long _bytesRead;
    private long _payloadBytesWritten;
    private long _compressedFrames;
    private long _rawFrames;

    /// <summary>Bytes handed to the peer, including framing overhead.</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>Bytes received from the peer, including framing overhead.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>Uncompressed payload bytes handed to the transport by the caller.</summary>
    public long PayloadBytesWritten => Interlocked.Read(ref _payloadBytesWritten);

    /// <summary>Frames emitted with LZ4 compression.</summary>
    public long CompressedFrames => Interlocked.Read(ref _compressedFrames);

    /// <summary>Frames emitted raw.</summary>
    public long RawFrames => Interlocked.Read(ref _rawFrames);

    internal void AddBytesWritten(long n) => Interlocked.Add(ref _bytesWritten, n);

    internal void AddBytesRead(long n) => Interlocked.Add(ref _bytesRead, n);

    internal void AddPayloadWritten(long n) => Interlocked.Add(ref _payloadBytesWritten, n);

    internal void AddFrame(bool compressed)
    {
        if (compressed)
        {
            Interlocked.Increment(ref _compressedFrames);
        }
        else
        {
            Interlocked.Increment(ref _rawFrames);
        }
    }
}
