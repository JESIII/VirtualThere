using System.Buffers.Binary;
using Vht.Transport.Wire;

namespace Vht.Transport;

/// <summary>
/// LZ4 block compression layered over a live TLS session: the <see cref="TransportKind.TlsLz4"/>
/// implementation of ARCHITECTURE.md §8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a decorator.</b> The architecture's rule "TlsLz4 may not exist without Tls" is enforced by
/// the constructor: this type refuses to wrap anything that is not already a TLS transport. It also
/// refuses to wrap a TLS transport that did not <em>negotiate</em> LZ4 in the version/capability
/// handshake, which is what makes compression disable-able per peer.
/// </para>
/// <para>
/// <b>Why framing cannot corrupt a stream.</b> Each frame carries its uncompressed length and a
/// CRC32C over the uncompressed bytes, and the reader validates both before handing a single byte to
/// the caller. A frame that fails either check raises <see cref="TransportErrorCode.IntegrityCheckFailed"/>
/// and the connection dies — it never yields a short or scrambled read.
/// </para>
/// <para>
/// <b>Why headers are never compressed.</b> The plain <see cref="WriteAsync"/> overload is the only
/// path the USB/IP codec needs, and it treats its argument as <see cref="PayloadKind.Control"/>,
/// which is never compressed. Compression happens only when a caller explicitly declares a payload as
/// bulk or interrupt via <see cref="WritePayloadAsync"/>, so a caller cannot accidentally compress a
/// header by writing it through the wrong method.
/// </para>
/// </remarks>
public sealed class Lz4Transport : IPayloadCompressionTransport, IPeerInfo
{
    private readonly ITransport _inner;
    private readonly Lz4Options _options;
    private readonly int _maxPayloadFrameBytes;
    private readonly int _maxCompressedBytes;
    private readonly byte[] _header = new byte[Lz4Framing.HeaderSize];

    private byte[] _readBuffer = new byte[4096];
    private byte[] _compressedBuffer = new byte[4096];
    private byte[] _writeBuffer = new byte[4096];
    private int _readOffset;
    private int _readLength;
    private bool _endOfStream;

    /// <summary>Wraps a TLS transport that negotiated LZ4.</summary>
    /// <param name="inner">The TLS transport. Must report <see cref="TransportCapabilities.Lz4"/>.</param>
    /// <param name="options">LZ4 policy; the threshold is the writer's decision, the max frame bounds both directions.</param>
    /// <param name="serverMaxPayloadFrameBytes">
    /// The frame size the server advertised, when it is smaller than the local maximum.
    /// </param>
    public Lz4Transport(ITransport inner, Lz4Options options, int? serverMaxPayloadFrameBytes = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (inner.Kind == TransportKind.PlainTcp)
        {
            throw new TransportException(
                TransportErrorCode.NotSupported,
                "TlsLz4 cannot be layered on a plaintext transport: the plaintext listener has no handshake to negotiate with");
        }

        if (!inner.Capabilities.HasFlag(TransportCapabilities.Lz4))
        {
            throw new TransportException(
                TransportErrorCode.NotSupported,
                "LZ4 was not negotiated with this peer, so compressed frames would be unreadable to it");
        }

        _inner = inner;
        _options = options;
        _maxPayloadFrameBytes = serverMaxPayloadFrameBytes is > 0
            ? Math.Min(options.MaxPayloadFrameBytes, serverMaxPayloadFrameBytes.Value)
            : options.MaxPayloadFrameBytes;
        _maxCompressedBytes = Lz4Framing.MaximumCompressedSize(_maxPayloadFrameBytes);
    }

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.TlsLz4;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => _inner.Capabilities | TransportCapabilities.Lz4;

    /// <inheritdoc />
    public bool CompressionActive => true;

    /// <inheritdoc />
    public int CompressionThresholdBytes => _options.ThresholdBytes;

    /// <inheritdoc />
    public string PeerLabel => (_inner as IPeerInfo)?.PeerLabel ?? Kind.ToString();

    /// <inheritdoc />
    public System.Net.EndPoint? RemoteEndPoint => (_inner as IPeerInfo)?.RemoteEndPoint;

    /// <summary>Session counters for the compressed leg.</summary>
    public TransportStatistics Statistics { get; } = new();

    /// <summary>Largest payload frame this session will send or accept.</summary>
    public int MaxPayloadFrameBytes => _maxPayloadFrameBytes;

    /// <inheritdoc />
    public Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct) =>
        _inner.AuthenticateAsync(request, ct);

    /// <inheritdoc />
    /// <remarks>Delegates to <see cref="AuthenticateAsync"/>'s owner; compression starts only after authentication.</remarks>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        if (_readOffset >= _readLength)
        {
            if (!await FillAsync(ct).ConfigureAwait(false))
            {
                return 0;
            }
        }

        var count = Math.Min(buffer.Length, _readLength - _readOffset);
        _readBuffer.AsSpan(_readOffset, count).CopyTo(buffer.Span);
        _readOffset += count;
        Statistics.AddBytesRead(count);
        return count;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) =>
        // Conservative default: an unclassified write is treated as control/header traffic and is
        // never compressed. Only WritePayloadAsync can opt a frame into compression.
        WritePayloadAsync(PayloadKind.Control, buffer, ct);

    /// <inheritdoc />
    public async ValueTask WritePayloadAsync(PayloadKind payloadKind, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length == 0)
        {
            return;
        }

        var offset = 0;
        while (offset < payload.Length)
        {
            var chunkLength = Math.Min(_maxPayloadFrameBytes, payload.Length - offset);
            await WriteFrameAsync(payloadKind, payload.Slice(offset, chunkLength), ct).ConfigureAwait(false);
            offset += chunkLength;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        Array.Clear(_readBuffer);
        Array.Clear(_compressedBuffer);
        Array.Clear(_writeBuffer);
    }

    private async ValueTask WriteFrameAsync(PayloadKind payloadKind, ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        var mayCompress = payloadKind is PayloadKind.Bulk or PayloadKind.Interrupt
            && chunk.Length >= _options.ThresholdBytes;

        var compressed = false;
        int frameLength;

        if (mayCompress)
        {
            EnsureCapacity(ref _writeBuffer, Lz4Framing.HeaderSize + Lz4Framing.MaximumCompressedSize(chunk.Length));
            var body = _writeBuffer.AsSpan(Lz4Framing.HeaderSize);
            var compressedLength = Lz4Framing.TryCompress(chunk.Span, body);

            // Only keep the compressed form when it actually wins; otherwise fall through to raw.
            if (compressedLength > 0 && compressedLength < chunk.Length)
            {
                Lz4Framing.WriteHeader(
                    _writeBuffer,
                    Lz4FrameType.Lz4,
                    compressedLength,
                    chunk.Length,
                    Crc32C.Compute(chunk.Span));
                frameLength = Lz4Framing.HeaderSize + compressedLength;
                compressed = true;
            }
            else
            {
                frameLength = WriteRawFrame(chunk);
            }
        }
        else
        {
            frameLength = WriteRawFrame(chunk);
        }

        await _inner.WriteAsync(_writeBuffer.AsMemory(0, frameLength), ct).ConfigureAwait(false);
        Statistics.AddFrame(compressed);
        Statistics.AddPayloadWritten(chunk.Length);
        Statistics.AddBytesWritten(frameLength);
    }

    private int WriteRawFrame(ReadOnlyMemory<byte> chunk)
    {
        EnsureCapacity(ref _writeBuffer, Lz4Framing.HeaderSize + chunk.Length);
        Lz4Framing.WriteHeader(
            _writeBuffer,
            Lz4FrameType.Raw,
            chunk.Length,
            chunk.Length,
            Crc32C.Compute(chunk.Span));
        chunk.Span.CopyTo(_writeBuffer.AsSpan(Lz4Framing.HeaderSize));
        return Lz4Framing.HeaderSize + chunk.Length;
    }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_endOfStream)
        {
            return false;
        }

        if (!await TransportIo.TryReadExactlyAsync(_inner, _header, ct).ConfigureAwait(false))
        {
            _endOfStream = true;
            return false;
        }

        var header = Lz4Framing.ReadHeader(_header, _maxPayloadFrameBytes, _maxCompressedBytes);
        EnsureCapacity(ref _compressedBuffer, header.CompressedLength);
        EnsureCapacity(ref _readBuffer, header.UncompressedLength);

        await TransportIo
            .TryReadExactlyAsync(_inner, _compressedBuffer.AsMemory(0, header.CompressedLength), ct)
            .ConfigureAwait(false);

        var target = _readBuffer.AsSpan(0, header.UncompressedLength);
        if (header.Type == Lz4FrameType.Lz4)
        {
            var written = Lz4Framing.Decompress(_compressedBuffer.AsSpan(0, header.CompressedLength), target);
            if (written != header.UncompressedLength)
            {
                throw new TransportException(
                    TransportErrorCode.IntegrityCheckFailed,
                    $"compressed frame decoded to {written} bytes but declared {header.UncompressedLength}; the frame stream is corrupt");
            }
        }
        else
        {
            _compressedBuffer.AsSpan(0, header.CompressedLength).CopyTo(target);
        }

        var crc = Crc32C.Compute(target);
        if (crc != header.Crc32C)
        {
            throw new TransportException(
                TransportErrorCode.IntegrityCheckFailed,
                $"frame CRC32C mismatch: computed 0x{crc:x8}, frame declared 0x{header.Crc32C:x8}; the frame stream is corrupt or was tampered with");
        }

        _readOffset = 0;
        _readLength = header.UncompressedLength;
        Statistics.AddFrame(header.Type == Lz4FrameType.Lz4);
        return true;
    }

    private static void EnsureCapacity(ref byte[] buffer, int required)
    {
        if (buffer.Length >= required)
        {
            return;
        }

        var size = buffer.Length;
        while (size < required)
        {
            size *= 2;
        }

        buffer = new byte[size];
    }

    /// <summary>Reads only the header of a frame, for diagnostics in tests.</summary>
    internal static Lz4FrameHeader PeekHeader(ReadOnlySpan<byte> frame, int maxFrameBytes) =>
        Lz4Framing.ReadHeader(frame, maxFrameBytes, Lz4Framing.MaximumCompressedSize(maxFrameBytes));

    /// <summary>Tests read the raw big-endian length field.</summary>
    internal static int PeekLength(ReadOnlySpan<byte> frame) => BinaryPrimitives.ReadInt32BigEndian(frame[1..]);
}
