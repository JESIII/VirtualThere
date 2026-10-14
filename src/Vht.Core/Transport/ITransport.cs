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
