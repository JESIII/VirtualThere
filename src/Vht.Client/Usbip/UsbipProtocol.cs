// SPDX-License-Identifier: Apache-2.0
//
// USB/IP protocol constants and the big-endian primitives used by every codec in Vht.Client.
//
// Protocol reference: https://docs.kernel.org/usb/usbip_protocol.html (USB/IP protocol version
// 0x0111 = v1.1.1). Op-code / struct names are re-declared from that specification and from
// usbip-win2's BSD-2-Clause include/usbip/proto_op.h (see third_party/usbip-win2-driver for the
// upstream package + licence); no upstream code is copied, only the protocol's own vocabulary,
// which is what ARCHITECTURE.md §3.3 requires and what makes the two implementations comparable
// field for field.
//
// Vht.Core (card server-workstream) owns the canonical codec for the project. This file is the client's
// own copy of the subset the client needs, per ARCHITECTURE.md Appendix A note 1 ("if server-workstream
// has not landed, the others compile against their own copies of the minimal types"): the client
// must be able to list and import devices without a shared build dependency, because it is also
// what makes the client testable on its own. When Vht.Core lands, UsbipOp/UsbipDeviceRecord become
// thin wrappers over it — that is a mechanical change, tracked as a follow-up in the handoff.

using System.Buffers.Binary;

namespace Vht.Client.Usbip;

/// <summary>USB/IP wire constants (protocol version 0x0111).</summary>
internal static class UsbipProtocol
{
    /// <summary>USB/IP protocol version we speak (v1.1.1).</summary>
    internal const ushort Version = 0x0111;

    // op_common.code — the request bit is 0x80 << 8, so a request's code is 0x8000 | n.
    internal const ushort OpReqDevList = 0x8005;
    internal const ushort OpRepDevList = 0x0005;
    internal const ushort OpReqImport = 0x8003;
    internal const ushort OpRepImport = 0x0003;

    // VirtualThere's own op-phase extension (docs/SERVER.md §8): the nickname of each exported
    // device, which the stock 312-byte device record has no field for. A stock peer has never
    // heard of these codes and every stock USB/IP implementation closes the connection on an
    // unknown op, so the probe is made on a connection of its own — a stock peer costs one
    // socket and nothing else. Vht.Core declares the same two codes and the same encoder.
    internal const ushort OpReqVhtDevInfo = 0x8f01;
    internal const ushort OpRepVhtDevInfo = 0x0f01;
    internal const uint VhtDevInfoMagic = 0x56485431;
    internal const int VhtNicknameMaxBytes = 96;
    internal const int VhtDeviceInfoRequestSize = OpCommonSize + sizeof(uint);        // 12
    internal const int VhtDeviceInfoEntryHeaderSize = BusIdSize + 1;                  // 33
    internal const uint MaxVhtDeviceInfoEntries = 4096;

    /// <summary>value of op_common.code that is not an op-code (i.e. a URB stream started).</summary>
    internal const ushort OpReqBit = 0x8000;

    // URB commands on an imported device (usbip_protocol.html §USBIP_CMD_SUBMIT and friends).
    internal const uint CmdSubmit = 1;
    internal const uint CmdUnlink = 2;
    internal const uint RetSubmit = 3;
    internal const uint RetUnlink = 4;

    // Sizes, all fixed by the specification. These are the numbers the bridge uses to know how many
    // bytes a device-list or import request occupies before it can be routed.
    internal const int OpCommonSize = 8;             // u16 version, u16 code, u32 status
    internal const int BusIdSize = 32;               // BUS_ID_SIZE
    internal const int DevPathMax = 256;             // DEV_PATH_MAX
    internal const int ImportRequestSize = OpCommonSize + BusIdSize;                  // 40
    internal const int DevListRequestSize = OpCommonSize + sizeof(uint);              // 12
    internal const int UsbipUsbDeviceSize = 312;     // path[256] busid[32] + 12 + 6 + 6
    internal const int UsbipUsbInterfaceSize = 4;    // class, subclass, protocol, padding
    internal const int ImportReplySize = OpCommonSize + UsbipUsbDeviceSize;           // 320
    internal const int UrbHeaderSize = 48;           // usbip_header: basic(20) + union(28)

    /// <summary>
    /// Upper bound on how many device records we will parse out of a single OP_REP_DEVLIST frame.
    /// This is a *parse bound on one reply*, not a device limit: it exists so a hostile or corrupt
    /// peer cannot make us allocate from an unvalidated length field (ARCHITECTURE.md §6.1, §10
    /// risk 10). It is deliberately far above any real machine's device count and it is never
    /// consulted when attaching, only when decoding. See DeviceCountInvariantTests.
    /// </summary>
    internal const uint MaxDevlistRecordsPerReply = 4096;

    /// <summary>Offset of <c>busid</c> inside an OP_REQ_IMPORT / OP_REQ_DEVLIST message.</summary>
    internal const int ImportBusIdOffset = OpCommonSize;

    /// <summary>True when <paramref name="code"/> is one of the two op-codes we serve on the bridge.</summary>
    internal static bool IsRequestOpCode(ushort code) =>
        code is OpReqDevList or OpReqImport;
}

/// <summary>op_common.status — the reply status an OP_REP_IMPORT carries.</summary>
internal enum OpStatus : uint
{
    Ok = 0,
    /// <summary>!SDEV_ST_AVAILABLE — the device is not exported by the peer.</summary>
    NotAvailable = 1,
    /// <summary>SDEV_ST_USED — already attached elsewhere (the reference product's "in use elsewhere" parity).</summary>
    DeviceBusy = 2,
    /// <summary>SDEV_ST_ERROR — the peer bound it but its own state is bad.</summary>
    DeviceError = 3,
    /// <summary>requested device not found by busid.</summary>
    NoDevice = 4,
    /// <summary>generic error.</summary>
    Error = 5,
}

/// <summary>usb_device_speed, as carried in a device record (upstream include/usbip/ch9.h).</summary>
internal enum UsbDeviceSpeed : uint
{
    Unknown = 0,
    Low = 1,
    Full = 2,
    High = 3,
    Wireless = 4,
    Super = 5,
    SuperPlus = 6,
}

/// <summary>Big-endian (network order) primitives. Every USB/IP multi-byte field is network order.</summary>
internal static class BigEndian
{
    internal static ushort ReadU16(ReadOnlySpan<byte> s)
    {
        if (s.Length < 2) throw new ArgumentOutOfRangeException(nameof(s));
        return BinaryPrimitives.ReadUInt16BigEndian(s);
    }

    internal static uint ReadU32(ReadOnlySpan<byte> s)
    {
        if (s.Length < 4) throw new ArgumentOutOfRangeException(nameof(s));
        return BinaryPrimitives.ReadUInt32BigEndian(s);
    }

    internal static void WriteU16(Span<byte> s, ushort value)
    {
        if (s.Length < 2) throw new ArgumentOutOfRangeException(nameof(s));
        BinaryPrimitives.WriteUInt16BigEndian(s, value);
    }

    internal static void WriteU32(Span<byte> s, uint value)
    {
        if (s.Length < 4) throw new ArgumentOutOfRangeException(nameof(s));
        BinaryPrimitives.WriteUInt32BigEndian(s, value);
    }

    /// <summary>
    /// Reads a NUL-padded, NUL-terminated ASCII field of fixed width. The kernel protocol says these
    /// fields are NUL-terminated when shorter than the field; a field that fills its width exactly
    /// has no terminator, which is why the whole width is trimmed of NULs rather than only the tail
    /// after the first NUL.
    /// </summary>
    internal static string ReadCString(ReadOnlySpan<byte> s)
    {
        int end = s.IndexOf((byte)0);
        if (end >= 0) s = s[..end];
        return System.Text.Encoding.ASCII.GetString(s).TrimEnd('\0');
    }

    /// <summary>Writes an ASCII string into a fixed-width, NUL-padded field. Throws if it will not fit.</summary>
    internal static void WriteCString(Span<byte> s, string value)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
        if (bytes.Length > s.Length)
        {
            throw new ArgumentException(
                $"'{value}' needs {bytes.Length} bytes but the field holds {s.Length}.", nameof(value));
        }
        s.Clear();
        bytes.CopyTo(s);
    }
}
