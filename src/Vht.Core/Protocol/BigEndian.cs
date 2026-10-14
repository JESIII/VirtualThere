namespace Vht.Core.Protocol;

/// <summary>
/// Big-endian primitives for the USB/IP wire format.
/// </summary>
/// <remarks>
/// USB/IP is network byte order throughout, including fields a naive implementation would
/// treat as native (status codes are big-endian <c>int32_t</c>). Every read is
/// bounds-checked against the span's length and throws <see cref="UsbIpProtocolException"/>
/// rather than reading past the end — the server parses attacker-controlled frames
/// (ARCHITECTURE.md §6.1), so a short frame must be a protocol error, never a memory read.
/// </remarks>
public static class BigEndian
{
    public static byte ReadU8(ReadOnlySpan<byte> b, int offset)
    {
        Check(b, offset, 1);
        return b[offset];
    }

    public static ushort ReadU16(ReadOnlySpan<byte> b, int offset)
    {
        Check(b, offset, 2);
        return (ushort)((b[offset] << 8) | b[offset + 1]);
    }

    public static uint ReadU32(ReadOnlySpan<byte> b, int offset)
    {
        Check(b, offset, 4);
        return ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) |
               ((uint)b[offset + 2] << 8) | b[offset + 3];
    }

    public static int ReadI32(ReadOnlySpan<byte> b, int offset) => (int)ReadU32(b, offset);

    public static ulong ReadU64(ReadOnlySpan<byte> b, int offset)
    {
        Check(b, offset, 8);
        ulong hi = ReadU32(b, offset);
        ulong lo = ReadU32(b, offset + 4);
        return (hi << 32) | lo;
    }

    public static void WriteU8(Span<byte> b, int offset, byte v)
    {
        Check(b, offset, 1);
        b[offset] = v;
    }

    public static void WriteU16(Span<byte> b, int offset, ushort v)
    {
        Check(b, offset, 2);
        b[offset] = (byte)(v >> 8);
        b[offset + 1] = (byte)v;
    }

    public static void WriteU32(Span<byte> b, int offset, uint v)
    {
        Check(b, offset, 4);
        b[offset] = (byte)(v >> 24);
        b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8);
        b[offset + 3] = (byte)v;
    }

    public static void WriteI32(Span<byte> b, int offset, int v) => WriteU32(b, offset, (uint)v);

    public static void WriteU64(Span<byte> b, int offset, ulong v)
    {
        Check(b, offset, 8);
        WriteU32(b, offset, (uint)(v >> 32));
        WriteU32(b, offset + 4, (uint)v);
    }

    /// <summary>
    /// Writes a fixed-width, NUL-padded ASCII string (the wire format of
    /// <c>usbip_usb_device.path</c> / <c>.busid</c> / <c>.serial</c>).
    /// A longer input is truncated at <paramref name="length"/> - 1 and NUL-terminated.
    /// </summary>
    public static void WriteFixedString(Span<byte> b, int offset, int length, string? value)
    {
        Check(b, offset, length);
        var span = b.Slice(offset, length);
        span.Clear();
        if (string.IsNullOrEmpty(value)) return;

        int n = Math.Min(value.Length, length - 1);
        for (int i = 0; i < n; i++)
        {
            char c = value[i];
            span[i] = c < 0x80 ? (byte)c : (byte)'?';
        }
        span[n] = 0;
    }

    /// <summary>
    /// Reads a fixed-width string field up to its first NUL, matching the kernel's
    /// <c>%s</c> treatment of the field.
    /// </summary>
    public static string ReadFixedString(ReadOnlySpan<byte> b, int offset, int length)
    {
        Check(b, offset, length);
        var span = b.Slice(offset, length);
        int end = span.IndexOf((byte)0);
        if (end < 0) end = length;
        return System.Text.Encoding.ASCII.GetString(span[..end]);
    }

    private static void Check(ReadOnlySpan<byte> b, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset + size > b.Length)
            throw new UsbIpProtocolException(
                $"frame truncated: need {size} byte(s) at offset {offset}, buffer holds {b.Length}");
    }

    private static void Check(Span<byte> b, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset + size > b.Length)
            throw new UsbIpProtocolException(
                $"frame truncated: need {size} byte(s) at offset {offset}, buffer holds {b.Length}");
    }
}
