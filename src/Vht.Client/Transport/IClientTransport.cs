// SPDX-License-Identifier: Apache-2.0
//
// The client-side transport seam and the *stub* transport this card is allowed to ship.
//
// ARCHITECTURE.md §6.2: the third-party UDE driver opens the USB/IP socket itself, in kernel, over
// WSK — so TLS and LZ4 can never live in the driver and must terminate here, in a userspace
// loopback bridge. Appendix C freezes the `ITransport` shape; Vht.Transport (card transport-workstream) owns
// its three real implementations.
//
// THIS CARD USES THE AGREED STUB TRANSPORT. Task client-workstream explicitly says "Use the agreed stub
// transport", and Appendix B note 6 says the transport card "is stubbed behind ITransport with a
// PlainTcpTransport implementation from day one". So the default here is a plain TCP transport,
// and the seam (`IClientTransportFactory`) is what the TLS/LZ4 implementation drops into later
// without touching bridge, attach or policy code.
//
// The shape deliberately mirrors the frozen `Vht.Transport.ITransport` member-for-member so the
// eventual adapter is a straight wrap:
//
//     ValueTask<int> ReadAsync(Memory<byte>, CancellationToken)
//     ValueTask WriteAsync(ReadOnlyMemory<byte>, CancellationToken)
//     Task<AuthResult> AuthenticateAsync(AuthRequest, CancellationToken)
//     TransportKind Kind { get; }             TransportCapabilities Capabilities { get; }

namespace Vht.Client.Transport;

/// <summary>Kind of transport on the server leg (frozen vocabulary, ARCHITECTURE.md Appendix C).</summary>
internal enum TransportKind
{
    PlainTcp,
    Tls,
    TlsLz4,
}

/// <summary>What a given transport can do. PlainTcp reports <see cref="None"/>.</summary>
[Flags]
internal enum TransportCapabilities
{
    None = 0,

    /// <summary>The peer must complete the extended `_vht` handshake before any op is sent.</summary>
    HandshakeRequired = 1,

    /// <summary>LZ4 block compression may be negotiated on bulk/interrupt payloads (§8).</summary>
    Lz4 = 2,

    /// <summary>A per-device password participates in the handshake (§6.1, deliverable 7).</summary>
    DevicePassword = 4,

    /// <summary>A server-wide shared secret participates in the handshake.</summary>
    SharedSecret = 8,
}

/// <summary>A credential the client can present. Never logged, never persisted in plaintext.</summary>
internal sealed record ClientAuthRequest
{
    internal string? BusId { get; init; }
    internal string? DevicePassword { get; init; }
    internal string? SharedSecret { get; init; }
    internal string? ExpectedSpkiPin { get; init; }
}

/// <summary>Outcome of <see cref="IClientTransport.AuthenticateAsync"/>.</summary>
internal sealed record ClientAuthResult(bool Success, string? FailureReason = null, string? ObservedSpkiPin = null)
{
    internal static ClientAuthResult Ok(string? spkiPin = null) => new(true, null, spkiPin);
    internal static ClientAuthResult Failed(string reason) => new(false, reason);
}

/// <summary>
/// The server-leg transport: a byte stream to a remote peer, plus authentication. Byte-stream
/// semantics only — the loopback bridge relays frames without needing to understand the URB model.
/// Disposal must be idempotent: the bridge disposes on relay-end and again on shutdown.
/// </summary>
internal interface IClientTransport : IAsyncDisposable
{
    TransportKind Kind { get; }

    TransportCapabilities Capabilities { get; }

    /// <summary>Reads at most <paramref name="buffer"/>.Length bytes; 0 means the peer closed.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);

    /// <summary>
    /// Runs the extended handshake. Rule from Appendix C: a transport without
    /// <see cref="TransportCapabilities.DevicePassword"/> / <see cref="TransportCapabilities.SharedSecret"/>
    /// MUST fail closed rather than no-op, because a no-op would silently accept an unauthenticated
    /// plaintext attach for a password-protected export.
    /// </summary>
    Task<ClientAuthResult> AuthenticateAsync(ClientAuthRequest request, CancellationToken ct);
}

/// <summary>
/// Connects a transport to a peer. This is the single seam the TLS+LZ4 card replaces; the bridge,
/// the attach manager and the policy layer never see a socket.
/// </summary>
internal interface IClientTransportFactory
{
    /// <summary>Human-readable description of what this factory produces, for logs and diagnostics.</summary>
    string Description { get; }

    /// <summary>
    /// Opens a transport to <paramref name="peer"/>. For TLS peers the factory owns pinning: it must
    /// refuse a peer whose observed SPKI does not match <see cref="Peer.SpkiPin"/> (trust on first
    /// use, then hard pin — §6.1).
    /// </summary>
    Task<IClientTransport> ConnectAsync(Peer peer, CancellationToken ct);
}
