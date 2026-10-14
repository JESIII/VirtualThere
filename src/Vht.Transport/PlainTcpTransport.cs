namespace Vht.Transport;

/// <summary>Read helpers shared by the transports and the framing layer.</summary>
internal static class TransportIo
{
    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes.
    /// Returns false when the peer closed cleanly before any byte of this read arrived; throws when
    /// the stream ends mid-buffer (a truncated frame is corruption, not a clean shutdown).
    /// </summary>
    public static async ValueTask<bool> TryReadExactlyAsync(
        ITransport transport,
        Memory<byte> buffer,
        CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await transport.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n <= 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new TransportException(
                    TransportErrorCode.Protocol,
                    $"peer closed the connection after {read} of {buffer.Length} expected bytes");
            }

            read += n;
        }

        return true;
    }
}

/// <summary>
/// Stock USB/IP over raw TCP: no handshake, no authentication, no compression.
/// </summary>
/// <remarks>
/// <para>
/// This is the interop listener of ARCHITECTURE.md §6.3 — the shape that lets an unmodified Linux
/// <c>usbip attach</c> work with zero flags. It is deliberately incapable of anything else:
/// <see cref="Capabilities"/> is always <see cref="TransportCapabilities.None"/>, and
/// <see cref="AuthenticateAsync"/> fails closed with <see cref="AuthStatus.Unauthorized"/> rather than
/// pretending to have checked a credential it never received. A caller that treats "no exception" as
/// "authenticated" is therefore broken loudly, not silently.
/// </para>
/// </remarks>
public sealed class PlainTcpTransport : ITransport, IPeerInfo
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    /// <summary>Wraps a connected socket stream.</summary>
    /// <param name="stream">The duplex stream to carry USB/IP bytes.</param>
    /// <param name="peerLabel">Label used for logging and rate limiting.</param>
    /// <param name="remoteEndPoint">Remote address, when known.</param>
    /// <param name="ownsStream">Whether disposing this transport disposes the stream.</param>
    public PlainTcpTransport(
        Stream stream,
        string peerLabel,
        System.Net.EndPoint? remoteEndPoint = null,
        bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerLabel);
        _stream = stream;
        _ownsStream = ownsStream;
        PeerLabel = peerLabel;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.PlainTcp;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.None;

    /// <inheritdoc />
    public string PeerLabel { get; }

    /// <inheritdoc />
    public System.Net.EndPoint? RemoteEndPoint { get; }

    /// <summary>Session counters.</summary>
    public TransportStatistics Statistics { get; } = new();

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        Statistics.AddBytesRead(read);
        return read;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        Statistics.AddBytesWritten(buffer.Length);
    }

    /// <inheritdoc />
    /// <remarks>Always fails: a plaintext session has no way to establish who the peer is.</remarks>
    public Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(AuthResult.Unauthorized(
            AuthMethod.None,
            "the plaintext transport has no authentication path; a password-protected export is never served on it"));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownsStream)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
