using System.Net.Sockets;

namespace Vht.Core.Transport;

/// <summary>
/// The stock-interop transport: USB/IP bytes straight over a TCP stream, nothing added.
/// </summary>
/// <remarks>
/// <para>
/// This is the day-one implementation required by ARCHITECTURE.md Appendix B step 6 — the
/// headline acceptance test is a stock Linux <c>usbip attach</c>, which needs no handshake and
/// no crypto, so the server must be able to speak a byte-exact stock stream.
/// </para>
/// <para>
/// It is also the transport that keeps the §6.4 rule honest: <see cref="Capabilities"/> is
/// <see cref="TransportCapabilities.None"/>, and <see cref="AuthenticateAsync"/> fails closed.
/// A password-protected device is therefore never advertised or attachable on this transport,
/// because there is nowhere for a password to come from and nowhere to send one.
/// </para>
/// <para>
/// The TLS implementation (<see cref="TransportKind.Tls"/>) is owned by the transport card
/// and lands behind <see cref="ITransport"/> without changes here.
/// </para>
/// </remarks>
public sealed class PlainTcpTransport : ITransport
{
    private readonly NetworkStream _stream;

    public PlainTcpTransport(NetworkStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.PlainTcp;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.None;

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        _stream.ReadAsync(buffer, ct);

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) =>
        _stream.WriteAsync(buffer, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Fails closed, always. USB/IP has no authentication field and this transport adds none,
    /// so there is no request that could succeed. Returning success here would be the exact
    /// downgrade the architecture forbids (§6.1 "Downgrade to plaintext", §6.4).
    /// </remarks>
    public Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct) =>
        Task.FromResult(AuthResult.Failure(
            "plaintext USB/IP carries no authentication; device passwords and the shared secret " +
            "are only available on the TLS listener"));

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
