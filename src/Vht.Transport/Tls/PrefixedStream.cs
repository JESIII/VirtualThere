namespace Vht.Transport.Tls;

/// <summary>
/// A stream wrapper that replays a small prefix before delegating to the underlying stream.
/// </summary>
/// <remarks>
/// Used to peek at the first TLS record header without consuming it: <c>SslStream</c> must see the
/// record from its first byte, so the bytes read during the sniff have to be pushed back.
/// </remarks>
internal sealed class PrefixedStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _prefix;
    private int _position;

    public PrefixedStream(byte[] prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position < _prefix.Length)
        {
            var take = Math.Min(buffer.Length, _prefix.Length - _position);
            _prefix.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        return _inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position < _prefix.Length)
        {
            var take = Math.Min(buffer.Length, _prefix.Length - _position);
            _prefix.AsMemory(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        _inner.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _inner.WriteAsync(buffer, offset, count, ct);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
