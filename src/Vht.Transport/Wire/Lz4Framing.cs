using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace Vht.Transport.Wire;

/// <summary>What a payload frame carries.</summary>
public enum Lz4FrameType : byte
{
    /// <summary>Uncompressed payload.</summary>
    Raw = 0,

    /// <summary>LZ4-block-compressed payload.</summary>
    Lz4 = 1,
}

/// <summary>Decoded 13-byte frame header.</summary>
public readonly record struct Lz4FrameHeader(
    Lz4FrameType Type,
    int CompressedLength,
    int UncompressedLength,
    uint Crc32C);

/// <summary>
/// The per-frame layout of ARCHITECTURE.md §8:
/// <c>[u8 type][u32 compressed_len][u32 uncompressed_len][u32 crc32c][body]</c>, all integers big-endian
/// to match USB/IP.
/// </summary>
/// <remarks>
/// <para>
/// Two independent failure modes are covered. The <b>CRC32C is computed over the uncompressed
/// payload</b>, so a frame whose ciphertext was tampered with — or whose compression stream was
/// corrupted — fails the check instead of being silently truncated. The <b>declared uncompressed
/// length is validated against a hard maximum before any buffer is sized</b>, so a hostile frame
/// cannot act as a memory bomb (§10 risk 6).
/// </para>
/// <para>
/// A writer that cannot beat raw emits <see cref="Lz4FrameType.Raw"/> with the payload verbatim, so
/// enabling compression never makes incompressible data slower or larger beyond the fixed 13-byte
/// header.
/// </para>
/// </remarks>
public static class Lz4Framing
{
    /// <summary>Fixed header size in bytes.</summary>
    public const int HeaderSize = 13;

    /// <summary>Smallest sensible frame. A zero-length frame is a protocol error.</summary>
    public const int MinPayloadLength = 1;

    /// <summary>Writes a frame header.</summary>
    public static void WriteHeader(
        Span<byte> destination,
        Lz4FrameType type,
        int compressedLength,
        int uncompressedLength,
        uint crc32c)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException($"header needs {HeaderSize} bytes", nameof(destination));
        }

        destination[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(destination[1..], compressedLength);
        BinaryPrimitives.WriteInt32BigEndian(destination[5..], uncompressedLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination[9..], crc32c);
    }

    /// <summary>Reads and validates a frame header. Throws <see cref="TransportException"/> on anything out of bounds.</summary>
    public static Lz4FrameHeader ReadHeader(
        ReadOnlySpan<byte> source,
        int maxFrameBytes,
        int maxCompressedBytes)
    {
        if (source.Length < HeaderSize)
        {
            throw new TransportException(TransportErrorCode.Protocol, $"frame header needs {HeaderSize} bytes, got {source.Length}");
        }

        var typeByte = source[0];
        if (typeByte is not ((byte)Lz4FrameType.Raw) and not ((byte)Lz4FrameType.Lz4))
        {
            throw new TransportException(TransportErrorCode.Protocol, $"unknown frame type 0x{typeByte:x2}");
        }

        var compressed = BinaryPrimitives.ReadInt32BigEndian(source[1..]);
        var uncompressed = BinaryPrimitives.ReadInt32BigEndian(source[5..]);
        var crc = BinaryPrimitives.ReadUInt32BigEndian(source[9..]);

        if (uncompressed is < MinPayloadLength || uncompressed > maxFrameBytes)
        {
            throw new TransportException(
                TransportErrorCode.FrameTooLarge,
                $"frame declares {uncompressed} uncompressed bytes, outside the permitted 1..{maxFrameBytes} range");
        }

        if (compressed is < MinPayloadLength || compressed > maxCompressedBytes)
        {
            throw new TransportException(
                TransportErrorCode.FrameTooLarge,
                $"frame declares {compressed} compressed bytes, outside the permitted 1..{maxCompressedBytes} range");
        }

        var type = (Lz4FrameType)typeByte;
        if (type == Lz4FrameType.Raw && compressed != uncompressed)
        {
            throw new TransportException(
                TransportErrorCode.Protocol,
                $"raw frame declares compressed={compressed} but uncompressed={uncompressed}; they must be equal");
        }

        return new Lz4FrameHeader(type, compressed, uncompressed, crc);
    }

    /// <summary>Upper bound on the compressed size of <paramref name="length"/> bytes, from the LZ4 block format.</summary>
    public static int MaximumCompressedSize(int length) => LZ4Codec.MaximumOutputSize(length);

    /// <summary>Attempts LZ4 block compression. Returns the number of bytes written, or -1 if it did not fit.</summary>
    public static int TryCompress(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        try
        {
            return LZ4Codec.Encode(payload, destination);
        }
        catch (ArgumentException)
        {
            return -1;
        }
    }

    /// <summary>Decompresses an LZ4 block. Returns the number of bytes written, or -1 when the block is invalid.</summary>
    public static int Decompress(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        var written = LZ4Codec.Decode(compressed, destination);
        return written < 0 ? -1 : written;
    }
}
