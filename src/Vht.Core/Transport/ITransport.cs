namespace Vht.Core.Transport;

/// <summary>Which transport a connection is speaking.</summary>
public enum TransportKind
{
    /// <summary>Stock USB/IP over a plain TCP stream. No auth, no framing, no compression.</summary>
    PlainTcp = 0,

    /// <summary>USB/IP inside TLS with the mandatory capability handshake.</summary>
    Tls = 1,

    /// <summary>TLS with LZ4-negotiated payload frames.</summary>
    TlsLz4 = 2,
}

/// <summary>What a transport can do. Negotiated, never assumed.</summary>
[Flags]
public enum TransportCapabilities
{
    /// <summary>Stock plaintext USB/IP: nothing beyond the protocol itself.</summary>
    None = 0,

    /// <summary>The peer must complete the capability handshake before any USB/IP frame.</summary>
    HandshakeRequired = 1,

    /// <summary>LZ4 block compression may be negotiated for bulk/interrupt payloads.</summary>
    Lz4 = 2,

    /// <summary>Per-device passwords may be carried.</summary>
    DevicePassword = 4,

    /// <summary>A server-wide shared secret is required.</summary>
    SharedSecret = 8,
}

/// <summary>Credentials presented by a client.</summary>
public sealed class AuthRequest
{
    public string ClientId { get; init; } = string.Empty;
    public string ServerSecret { get; init; } = string.Empty;
    public string? DevicePassword { get; init; }
    public byte[]? ChallengeResponse { get; init; }
}

/// <summary>Outcome of an authentication attempt.</summary>
public readonly struct AuthResult
{
    public bool Ok { get; init; }
    public string Detail { get; init; }
    public bool RateLimited { get; init; }
    public bool LockedOut { get; init; }

    public static AuthResult Success() => new() { Ok = true, Detail = "ok" };

    public static AuthResult Failure(string detail) => new() { Ok = false, Detail = detail };

    public static AuthResult Limited(string detail) => new() { Ok = false, Detail = detail, RateLimited = true };
}

/// <summary>
/// The transport abstraction frozen in ARCHITECTURE.md Appendix C.
/// </summary>
/// <remarks>
/// Rules attached to it, all enforced by the transport card's own tests:
/// <list type="bullet">
///   <item><see cref="TransportKind.TlsLz4"/> may not exist without <see cref="TransportKind.Tls"/>.</item>
///   <item><see cref="TransportKind.PlainTcp"/> reports <see cref="TransportCapabilities.None"/> and
///   <see cref="AuthenticateAsync"/> must fail closed with <see cref="AuthResult"/>
///   <c>Ok == false</c>, never no-op successfully.</item>
///   <item><see cref="TransportCapabilities.HandshakeRequired"/> is always set for TLS kinds.</item>
/// </list>
/// The server is written against this interface from day one so the transport card can land
/// behind it without touching server code (Appendix B step 6).
/// </remarks>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Which transport this is.</summary>
    TransportKind Kind { get; }

    /// <summary>What it supports.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>Reads into <paramref name="buffer"/>; returns the byte count, 0 at end of stream.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Writes the whole buffer.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);

    /// <summary>Authenticates the peer. Must fail closed on a transport with no auth.</summary>
    Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct);
}

/// <summary>Which USB transfer class a payload belongs to. Decides whether LZ4 may be applied.</summary>
/// <remarks>
/// Values are deliberately identical to the transport card's own <c>Vht.Transport.PayloadKind</c>
/// member for member, so an adapter that bridges the two interfaces is a cast, not a mapping table.
/// </remarks>
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

/// <summary>
/// Optional and additive to the frozen <see cref="ITransport"/> shape (the same pattern
/// <c>Vht.Transport.IPeerInfo</c> uses): a transport that can classify one write, so a caller can
/// declare a data stage as bulk/interrupt and let a compression layer decide what to do with it.
/// </summary>
/// <remarks>
/// <para>
/// This is the missing half of ARCHITECTURE.md §8's "applied only to declared payload frames".
/// The compression opt-in lives in the transport card
/// (<c>Vht.Transport.IPayloadCompressionTransport.WritePayloadAsync</c>), but the USB/IP session
/// writes through <see cref="ITransport"/>, which had no way to declare anything — so every reply
/// frame, data stage included, was classified control and never compressed. Adding the parameter
/// to <see cref="WriteAsync"/> would break every existing implementation of the frozen interface,
/// hence the narrow downcast: <see cref="Vht.Core.Server.UsbIpSession"/> uses it when it is
/// present and falls back to a single plain <see cref="WriteAsync"/> when it is not, so a
/// transport that cannot classify can only lose compression, never correctness.
/// </para>
/// <para>
/// Callers must write a USB/IP URB header as <see cref="PayloadKind.Control"/>: that is what keeps
/// "headers are never compressed" true, which is what lets the codec route a frame without
/// decompressing it.
/// </para>
/// </remarks>
public interface IPayloadKindTransport : ITransport
{
    /// <summary>True when this session may actually compress; a classified write is only useful then.</summary>
    bool CompressionActive { get; }

    /// <summary>Byte threshold below which compression is not attempted.</summary>
    int CompressionThresholdBytes { get; }

    /// <summary>
    /// Writes one payload, declaring its transfer class. A transport that cannot compress it
    /// writes it verbatim; the distinction is invisible on the wire beyond the frame header.
    /// </summary>
    ValueTask WritePayloadAsync(PayloadKind payloadKind, ReadOnlyMemory<byte> payload, CancellationToken ct);
}
