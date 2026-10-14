// SPDX-License-Identifier: Apache-2.0
//
// Byte-exact builders/parsers for the UDE driver's IOCTL structs, written against explicit field
// offsets instead of Marshal-based struct layouts. Two reasons:
//
//  1. Every ambiguity about padding becomes visible and testable here, in one place, rather than
//     hidden inside a LayoutKind attribute. `UdeBufferCodecTests` asserts every offset below.
//  2. The driver takes METHOD_BUFFERED IOCTLs whose input is a self-sized struct; building the
//     buffer by hand is what the C++ side does and removes marshalling as a failure mode.
//
// Offsets come from upstream include/usbip/vhci.h (BSD-2-Clause; see third_party/ for the pinned
// package). The structs are:
//
//   namespace usbip::vhci {
//     struct base                     { ULONG size; };                   // IN, self size
//     struct imported_device_location { int port; ULONG location_hash;
//                                       char busid[32]; char service[32]; char host[1025]; };
//     struct imported_device_properties { UINT32 devid; usb_device_speed speed;
//                                       UINT16 vendor, product; char serial[16];
//                                       UCHAR iserial; bool wsk_events; };
//     struct imported_device : imported_device_location, imported_device_properties {};
//   }
//   namespace usbip::vhci::ioctl {
//     struct plugin_hardware      : base, imported_device_location { char serial[16]; bool wsk_events; };
//     struct plugout_hardware     : base { int port; };
//     struct stop_attach_attempts : base, imported_device_location { int count; };
//     struct get_imported_devices : base { imported_device devices[]; };
//   }
//
// PADDING, and the one consequence that bites: vhci.h has no #pragma pack, so MSVC's default
// (natural field alignment) applies — that is the layout assumed here. Those structs use C++
// multiple inheritance, so the second base class `imported_device_location` starts at offset 4, not
// 0, in plugin_hardware and stop_attach_attempts.
//
// The driver never reports which packing it used, and the two plausible layouts differ in the
// trailing padding after `imported_device`'s final `bool`, so the element stride of
// GET_IMPORTED_DEVICES is either 1132 (natural) or 1127 (pack(1)). `ParseImportedDevices`
// auto-detects by validating the array rather than guessing, and both candidates are covered by
// tests (`ImportedDeviceLayoutOffsets_FollowTheHeaderFieldWidths` derives every offset from the
// header's field widths, so the tables cannot drift). Everything the client acts on — port,
// location_hash, busid, service, host, i.e. the first 1097 bytes of each element — sits at an
// identical offset under both layouts.

using System.Buffers.Binary;
using System.Text;

namespace Vht.Interop.Ude;

/// <summary>Field offsets of one <c>imported_device</c> array element, per candidate packing.</summary>
internal readonly record struct ImportedDeviceLayout(
    string Name,
    int Stride,
    int DevIdOffset,
    int SpeedOffset,
    int VendorOffset,
    int ProductOffset,
    int SerialOffset,
    int ISerialOffset,
    int WskEventsOffset)
{
    /// <summary>MSVC default packing: `host` ends at 1097, `devid` is 4-aligned at 1100, element 1132.</summary>
    internal static readonly ImportedDeviceLayout Natural = new(
        "natural (MSVC default, assumed)", 1132, 1100, 1104, 1108, 1110, 1112, 1128, 1129);

    /// <summary>
    /// #pragma pack(1): every member follows the previous one with no padding, so `devid` follows
    /// `host` immediately at 1097. `host` ends at 1097; devid 1097..1100, speed 1101..1104,
    /// vendor 1105..1106, product 1107..1108, serial 1109..1124, iserial 1125, wsk_events 1126,
    /// giving an element stride of 1127. (vhci.h has no #pragma pack, so this candidate is not what
    /// the shipped driver uses; it exists so a packing difference is detected instead of misread.)
    /// </summary>
    internal static readonly ImportedDeviceLayout Packed = new(
        "packed (pack(1))", 1127, 1097, 1101, 1105, 1107, 1109, 1125, 1126);

    internal static readonly ImportedDeviceLayout[] Candidates = [Natural, Packed];
}

/// <summary>Builds the IOCTL input buffers and parses the output buffers for the UDE driver.</summary>
internal static class UdeBufferCodec
{
    // ---- plugin_hardware -------------------------------------------------------------------------
    // size @0 (4) | port @4 (4) | location_hash @8 (4) | busid @12 [32] | service @44 [32] |
    // host @76 [1025] | serial @1101 [16] | wsk_events @1117 | 1118 bytes → padded to 1120.
    // Identical under either packing, because the last member is a single byte.
    internal const int PluginHardwareSize = 1120;
    internal const int PluginPortOffset = 4;
    internal const int PluginLocationHashOffset = 8;
    internal const int PluginBusIdOffset = 12;
    internal const int PluginServiceOffset = PluginBusIdOffset + UdeConstants.BusIdSize;            // 44
    internal const int PluginHostOffset = PluginServiceOffset + UdeConstants.ServiceBufSize;         // 76
    internal const int PluginSerialOffset = PluginHostOffset + UdeConstants.HostBufSize;             // 1101
    internal const int PluginWskEventsOffset = PluginSerialOffset + UdeConstants.SerialBufSize;      // 1117

    // ---- plugout_hardware ------------------------------------------------------------------------
    internal const int PlugoutHardwareSize = 8;
    internal const int PlugoutPortOffset = 4;

    // ---- stop_attach_attempts ---------------------------------------------------------------------
    // base + imported_device_location (ends at 1101) + int count. `count` lands at 1104 naturally
    // (4-aligned) or 1101 packed; totals 1108 / 1105.
    internal const int StopAttachAttemptsSizeNatural = 1108;
    internal const int StopAttachAttemptsSizePacked = 1105;
    internal const int StopAttachCountOffsetNatural = 1104;
    internal const int StopAttachCountOffsetPacked = 1101;

    // ---- get_imported_devices ---------------------------------------------------------------------
    /// <summary>sizeof(base) — the imported_device array starts right after the self-size field.</summary>
    internal const int GetImportedDevicesHeaderOffset = 4;

    internal const int DevPortOffset = 0;
    internal const int DevLocationHashOffset = 4;
    internal const int DevBusIdOffset = 8;
    internal const int DevServiceOffset = DevBusIdOffset + UdeConstants.BusIdSize;                  // 40
    internal const int DevHostOffset = DevServiceOffset + UdeConstants.ServiceBufSize;              // 72

    /// <summary>Bytes of an element that are layout-independent (through the end of `host`).</summary>
    internal const int DevLayoutIndependentPrefix = DevHostOffset + UdeConstants.HostBufSize;       // 1097

    /// <summary>Input buffer size for GET_IMPORTED_DEVICES holding <paramref name="elements"/> devices.</summary>
    internal static int GetImportedDevicesBufferSize(int elements, ImportedDeviceLayout layout) =>
        GetImportedDevicesHeaderOffset + (elements * layout.Stride);

    /// <summary>
    /// Builds the input for IOCTL_PLUGIN_HARDWARE: import <paramref name="busId"/> from
    /// <paramref name="host"/>:<paramref name="service"/>.
    ///
    /// `host`/`service` are always the *loopback bridge* (127.0.0.1 / 3240), never the remote peer —
    /// that is the point of ARCHITECTURE.md §6.2, and <c>UdeDeviceController</c> enforces it.
    /// </summary>
    internal static byte[] BuildPluginHardware(
        string host,
        string service,
        string busId,
        string? serial,
        bool wskEvents)
    {
        var buffer = new byte[PluginHardwareSize];
        WriteU32(buffer, 0, PluginHardwareSize);                 // base.size = self size
        WriteString(buffer.AsSpan(PluginBusIdOffset, UdeConstants.BusIdSize), busId, nameof(busId));
        WriteString(buffer.AsSpan(PluginServiceOffset, UdeConstants.ServiceBufSize), service, nameof(service));
        WriteString(buffer.AsSpan(PluginHostOffset, UdeConstants.HostBufSize), host, nameof(host));
        WriteString(buffer.AsSpan(PluginSerialOffset, UdeConstants.SerialBufSize), serial ?? string.Empty, nameof(serial));
        // Only wsk_events is a real field here. "once" is not a flag on this struct: it is expressed
        // by choosing IOCTL_PLUGIN_HARDWARE_ONCE, a different function code.
        buffer[PluginWskEventsOffset] = wskEvents ? (byte)1 : (byte)0;
        return buffer;
    }

    /// <summary>Reads back (port, location_hash) from a plugin_hardware OUTPUT buffer. port &lt; 1 means failure.</summary>
    internal static (int Port, uint LocationHash) ParsePluginHardwareResult(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < PluginLocationHashOffset + 4)
        {
            throw new ArgumentException(
                $"plugin_hardware result is {buffer.Length} bytes; port is at {PluginPortOffset} and " +
                $"location_hash at {PluginLocationHashOffset}, so at least 12 bytes are required.", nameof(buffer));
        }
        return (ReadI32(buffer, PluginPortOffset), ReadU32(buffer, PluginLocationHashOffset));
    }

    /// <summary>Builds the input for IOCTL_PLUGOUT_HARDWARE: detach one port, or all via <see cref="UdeConstants.PortAll"/>.</summary>
    internal static byte[] BuildPlugoutHardware(int port)
    {
        var buffer = new byte[PlugoutHardwareSize];
        WriteU32(buffer, 0, PlugoutHardwareSize);
        WriteI32(buffer, PlugoutPortOffset, port);
        return buffer;
    }

    /// <summary>Builds the input for IOCTL_STOP_ATTACH_ATTEMPTS: cancel pending attaches for (host,service,busid).</summary>
    internal static byte[] BuildStopAttachAttempts(
        string host,
        string service,
        string busId,
        ImportedDeviceLayout? layout = null)
    {
        int size = layout == ImportedDeviceLayout.Packed
            ? StopAttachAttemptsSizePacked
            : StopAttachAttemptsSizeNatural;
        var buffer = new byte[size];
        WriteU32(buffer, 0, size);
        WriteString(buffer.AsSpan(PluginBusIdOffset, UdeConstants.BusIdSize), busId, nameof(busId));
        WriteString(buffer.AsSpan(PluginServiceOffset, UdeConstants.ServiceBufSize), service, nameof(service));
        WriteString(buffer.AsSpan(PluginHostOffset, UdeConstants.HostBufSize), host, nameof(host));
        return buffer;
    }

    /// <summary>Reads the OUT `count` (number of canceled attach requests) from a stop_attach_attempts buffer.</summary>
    internal static int ParseStopAttachAttemptsCount(byte[] buffer, ImportedDeviceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int offset = layout == ImportedDeviceLayout.Packed
            ? StopAttachCountOffsetPacked
            : StopAttachCountOffsetNatural;
        if (buffer.Length < offset + 4)
        {
            throw new ArgumentException(
                $"stop_attach_attempts result is {buffer.Length} bytes but `count` sits at {offset} under the " +
                $"{layout.Name} layout.", nameof(buffer));
        }
        return ReadI32(buffer, offset);
    }

    /// <summary>
    /// Parses a GET_IMPORTED_DEVICES output buffer, auto-detecting the element layout. Returns the
    /// devices and the layout that validated, so the caller can cache it and log which one it was.
    /// </summary>
    internal static (IReadOnlyList<UdeImportedDevice> Devices, ImportedDeviceLayout Layout) ParseImportedDevices(
        byte[] buffer,
        int elements)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (elements < 0) throw new ArgumentOutOfRangeException(nameof(elements));

        // Zero elements is layout-agnostic; report the natural layout so the caller has something to log.
        if (elements == 0)
        {
            return (Array.Empty<UdeImportedDevice>(), ImportedDeviceLayout.Natural);
        }

        foreach (ImportedDeviceLayout layout in ImportedDeviceLayout.Candidates)
        {
            if (TryParse(buffer, elements, layout, out IReadOnlyList<UdeImportedDevice> devices))
            {
                return (devices, layout);
            }
        }

        throw new UdeLayoutException(
            $"GET_IMPORTED_DEVICES buffer ({buffer.Length} bytes, {elements} elements) validated under neither " +
            $"known element layout (natural stride {ImportedDeviceLayout.Natural.Stride}, packed stride " +
            $"{ImportedDeviceLayout.Packed.Stride}). The driver's struct packing differs from both assumptions — " +
            "capture the raw buffer and report it rather than guessing.");
    }

    private static bool TryParse(
        byte[] buffer,
        int elements,
        ImportedDeviceLayout layout,
        out IReadOnlyList<UdeImportedDevice> devices)
    {
        var list = new List<UdeImportedDevice>(elements);
        for (int i = 0; i < elements; i++)
        {
            int start = GetImportedDevicesHeaderOffset + (i * layout.Stride);
            if (start + DevLayoutIndependentPrefix > buffer.Length)
            {
                devices = Array.Empty<UdeImportedDevice>();
                return false;
            }

            int port = ReadI32(buffer, start + DevPortOffset);
            string busId = ReadString(buffer.AsSpan(start + DevBusIdOffset, UdeConstants.BusIdSize));
            string service = ReadString(buffer.AsSpan(start + DevServiceOffset, UdeConstants.ServiceBufSize));
            string host = ReadString(buffer.AsSpan(start + DevHostOffset, UdeConstants.HostBufSize));

            // Validation: the driver's ports are 1..N — 0 means "an error" and PORT_ALL is a sentinel
            // that is never stored — and all three fields are ASCII in practice. Under the wrong
            // stride the next element starts inside the previous element's `host` buffer (or past the
            // end, i.e. zeroes), which this rejects immediately. Requiring port >= 1 is what makes the
            // two candidate layouts distinguishable rather than merely differently-aligned.
            if (port is < 1 or > 1024)
            {
                devices = Array.Empty<UdeImportedDevice>();
                return false;
            }
            if (!IsPrintableAscii(host) || !IsPrintableAscii(service) || !IsPrintableAscii(busId))
            {
                devices = Array.Empty<UdeImportedDevice>();
                return false;
            }

            int fields = (i * layout.Stride) + layout.DevIdOffset;
            int speedOffset = fields + (layout.SpeedOffset - layout.DevIdOffset);
            int vendorOffset = fields + (layout.VendorOffset - layout.DevIdOffset);
            int productOffset = fields + (layout.ProductOffset - layout.DevIdOffset);
            int serialOffset = fields + (layout.SerialOffset - layout.DevIdOffset);

            list.Add(new UdeImportedDevice
            {
                Port = port,
                LocationHash = ReadU32(buffer, start + DevLocationHashOffset),
                BusId = busId,
                Service = service,
                Host = host,
                DevId = fields + 4 <= buffer.Length ? ReadU32(buffer, fields) : 0u,
                Speed = speedOffset + 4 <= buffer.Length ? ReadU32(buffer, speedOffset) : 0u,
                VendorId = vendorOffset + 2 <= buffer.Length ? ReadU16(buffer, vendorOffset) : (ushort)0,
                ProductId = productOffset + 2 <= buffer.Length ? ReadU16(buffer, productOffset) : (ushort)0,
                Serial = serialOffset + UdeConstants.SerialBufSize <= buffer.Length
                    ? ReadString(buffer.AsSpan(serialOffset, UdeConstants.SerialBufSize))
                    : string.Empty,
            });
        }

        devices = list;
        return true;
    }

    private static bool IsPrintableAscii(string s)
    {
        foreach (char c in s)
        {
            if (c != 0 && (c < 0x20 || c > 0x7e)) return false;
        }
        return true;
    }

    // ---- primitives ------------------------------------------------------------------------------
    // Within an IOCTL struct the driver uses native little-endian integers (ULONG/UINT32). The
    // big-endian rule applies to the USB/IP *wire* format, not to this ABI — writing a port number
    // byte-swapped is the classic way to make ports look random, and the distinction is asserted in
    // UdeBufferCodecTests.

    private static void WriteU32(byte[] b, int offset, int value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset, 4), (uint)value);

    private static void WriteI32(byte[] b, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset, 4), value);

    private static uint ReadU32(byte[] b, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));

    private static ushort ReadU16(byte[] b, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset, 2));

    private static int ReadI32(byte[] b, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset, 4));

    private static string ReadString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end >= 0) field = field[..end];
        return Encoding.ASCII.GetString(field);
    }

    /// <summary>
    /// Writes a UTF-8 string (the driver documents UTF-8 for these fields; ASCII is what busids,
    /// hosts, services and serials actually are) into a fixed-width, NUL-padded field, failing loudly
    /// when it does not fit rather than truncating a device identity silently.
    /// </summary>
    private static void WriteString(Span<byte> field, string value, string name)
    {
        int written = Encoding.UTF8.GetBytes(value, field);
        if (written >= field.Length)
        {
            throw new ArgumentException(
                $"{name} '{value}' needs {written + 1} bytes including the NUL terminator but the field " +
                $"holds {field.Length}.", name);
        }
        field[written] = 0;
    }
}

/// <summary>The driver's struct packing matched neither known candidate.</summary>
internal sealed class UdeLayoutException : Exception
{
    internal UdeLayoutException(string message) : base(message) { }
}
