namespace Vht.Core.Transport;

/// <summary>
/// Reads and writes whole frames over an <see cref="ITransport"/>, which exposes only raw
/// byte-level reads/writes.
/// </summary>
/// <remarks>
/// The transport card owns the transports but not this plumbing, so it lives here next to the
/// frozen interface. Everything is bounds-checked: a peer that closes mid-frame produces an
/// <see cref="EndOfStreamException"/>, never a partially-filled buffer that the codec would
/// then parse as valid.
/// </remarks>
public static class TransportIo
{
    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes or throws.</summary>
    public static async ValueTask ReadExactAsync(ITransport transport, Memory<byte> buffer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport);

        int read = 0;
        while (read < buffer.Length)
        {
            int n = await transport.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException(
                    $"connection closed after {read} of {buffer.Length} byte(s)");
            read += n;
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes into a new array.</summary>
    public static async ValueTask<byte[]> ReadExactlyAsync(ITransport transport, int count, CancellationToken ct)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return Array.Empty<byte>();

        var buffer = new byte[count];
        await ReadExactAsync(transport, buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>Writes the whole buffer, looping over short writes.</summary>
    public static async ValueTask WriteAllAsync(ITransport transport, ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (buffer.Length == 0) return;
        await transport.WriteAsync(buffer, ct).ConfigureAwait(false);
    }
}
