using System.Buffers;

namespace Vht.Core.Protocol;

/// <summary>
/// Reader/writer for the USB/IP v1.1.1 wire format.
/// </summary>
/// <remarks>
/// Two phases, both implemented here:
/// <list type="number">
///   <item>the <b>operation phase</b> — an 8-byte <c>op_common</c> header
///   (<c>version, code, status</c>) optionally followed by a request/reply body;
///   and the device-list/import bodies, which are size-prefixed by construction
///   (<c>ndev</c> or a fixed 312-byte record), never by an attacker-controlled length.</item>
///   <item>the <b>URB phase</b> — a fixed 48-byte <c>usbip_header</c> (20-byte basic header
///   plus the largest union member, <c>cmd_submit</c>) followed by the transfer buffer and
///   then ISO descriptors.</item>
/// </list>
/// All length fields are validated against <see cref="UsbIp.MaxTransferBuffer"/> and
/// <see cref="UsbIp.MaxIsoPackets"/> before anything is allocated (ARCHITECTURE.md risk 10).
/// </remarks>
public static class UsbIpCodec
{
    // =====================================================================================
    // Operation phase
    // =====================================================================================

    /// <summary>Size of the operation-phase header (<c>op_common</c>).</summary>
    public const int OpHeaderSize = 8;

    /// <summary><c>op_common</c>: version, code, status.</summary>
    public readonly struct OpHeader
    {
        public ushort Version { get; init; }
        public ushort Code { get; init; }
        public uint Status { get; init; }

        public override string ToString() => $"op version=0x{Version:x4} code=0x{Code:x4} status={Status}";
    }

    /// <summary>Serialises an <c>op_common</c> header.</summary>
    public static void WriteOpHeader(Span<byte> buffer, ushort code, uint status = 0)
    {
        if (buffer.Length < OpHeaderSize)
            throw new ArgumentException($"need {OpHeaderSize} bytes", nameof(buffer));
        BigEndian.WriteU16(buffer, 0, UsbIp.ProtocolVersion);
        BigEndian.WriteU16(buffer, 2, code);
        BigEndian.WriteU32(buffer, 4, status);
    }

    /// <summary>Parses an <c>op_common</c> header.</summary>
    public static OpHeader ReadOpHeader(ReadOnlySpan<byte> buffer) => new()
    {
        Version = BigEndian.ReadU16(buffer, 0),
        Code = BigEndian.ReadU16(buffer, 2),
        Status = BigEndian.ReadU32(buffer, 4),
    };

    /// <summary>
    /// Validates the protocol version of an inbound operation header. The kernel's own
    /// stub rejects a mismatch rather than guessing, and so do we.
    /// </summary>
    public static void ValidateVersion(OpHeader header)
    {
        if (header.Version != UsbIp.ProtocolVersion)
            throw new UsbIpProtocolException(
                $"unsupported USB/IP version 0x{header.Version:x4} (this server speaks 0x{UsbIp.ProtocolVersion:x4})");
    }

    /// <summary>Parses the 32-byte <c>busid</c> of an <c>OP_REQ_IMPORT</c> body.</summary>
    public static string ReadImportBusId(ReadOnlySpan<byte> body)
    {
        if (body.Length < UsbIp.BusIdLength)
            throw new UsbIpProtocolException(
                $"OP_REQ_IMPORT body truncated: need {UsbIp.BusIdLength} bytes, got {body.Length}");
        return BigEndian.ReadFixedString(body, 0, UsbIp.BusIdLength);
    }

    /// <summary>Writes a 32-byte <c>busid</c> (used by tests and by any client-side code).</summary>
    public static void WriteImportBusId(Span<byte> buffer, string busId) =>
        BigEndian.WriteFixedString(buffer, 0, UsbIp.BusIdLength, busId);

    /// <summary>
    /// Builds a complete <c>OP_REP_DEVLIST</c> reply for <paramref name="devices"/>.
    /// Layout: <c>op_common</c>, <c>op_devlist_reply{ndev}</c>, then per device a 312-byte
    /// <c>usbip_usb_device</c> followed by <c>bNumInterfaces</c> 4-byte interface records.
    /// </summary>
    public static byte[] EncodeDevlistReply(IReadOnlyList<UsbIpDevice> devices, uint status = 0)
    {
        ArgumentNullException.ThrowIfNull(devices);

        long total = OpHeaderSize + 4L;
        foreach (var d in devices)
        {
            int ifaces = Math.Max(d.NumInterfaces, d.Interfaces.Count);
            total += UsbIpDevice.WireSize + (long)ifaces * UsbIpDevice.InterfaceWireSize;
        }

        if (total > int.MaxValue)
            throw new UsbIpProtocolException($"device list reply too large ({total} bytes)");

        var buffer = new byte[(int)total];
        var span = buffer.AsSpan();

        WriteOpHeader(span, UsbIp.OpRepDevlist, status);
        BigEndian.WriteU32(span, OpHeaderSize, (uint)devices.Count);

        int offset = OpHeaderSize + 4;
        foreach (var d in devices)
        {
            WriteDeviceRecord(span, offset, d);
            offset += UsbIpDevice.WireSize;

            int ifaces = Math.Max(d.NumInterfaces, d.Interfaces.Count);
            for (int i = 0; i < ifaces; i++)
            {
                UsbIpInterface iface = i < d.Interfaces.Count
                    ? d.Interfaces[i]
                    : default;
                BigEndian.WriteU8(span, offset + 0, iface.InterfaceClass);
                BigEndian.WriteU8(span, offset + 1, iface.InterfaceSubClass);
                BigEndian.WriteU8(span, offset + 2, iface.InterfaceProtocol);
                BigEndian.WriteU8(span, offset + 3, 0);
                offset += UsbIpDevice.InterfaceWireSize;
            }
        }

        return buffer;
    }

    /// <summary>Writes one 312-byte <c>usbip_usb_device</c> record.</summary>
    private static void WriteDeviceRecord(Span<byte> span, int offset, UsbIpDevice d)
    {
        BigEndian.WriteFixedString(span, offset + 0, UsbIp.PathLength, d.Path);
        BigEndian.WriteFixedString(span, offset + 256, UsbIp.BusIdLength, d.BusId);
        BigEndian.WriteU32(span, offset + 288, d.BusNum);
        BigEndian.WriteU32(span, offset + 292, d.DevNum);
        BigEndian.WriteU32(span, offset + 296, d.Speed);
        BigEndian.WriteU16(span, offset + 300, d.VendorId);
        BigEndian.WriteU16(span, offset + 302, d.ProductId);
        BigEndian.WriteU16(span, offset + 304, d.BcdDevice);
        BigEndian.WriteU8(span, offset + 306, d.DeviceClass);
        BigEndian.WriteU8(span, offset + 307, d.DeviceSubClass);
        BigEndian.WriteU8(span, offset + 308, d.DeviceProtocol);
        BigEndian.WriteU8(span, offset + 309, d.ConfigurationValue);
        BigEndian.WriteU8(span, offset + 310, d.NumConfigurations);
        BigEndian.WriteU8(span, offset + 311, d.NumInterfaces);
    }

    /// <summary>
    /// Reads a device record and consumes its interface records.
    /// <paramref name="offset"/> advances past the whole group.
    /// </summary>
    public static UsbIpDevice ReadDeviceRecord(ReadOnlySpan<byte> buffer, ref int offset)
    {
        if (offset + UsbIpDevice.WireSize > buffer.Length)
            throw new UsbIpProtocolException("device list truncated inside a device record");

        var d = new UsbIpDevice
        {
            Path = BigEndian.ReadFixedString(buffer, offset + 0, UsbIp.PathLength),
            BusId = BigEndian.ReadFixedString(buffer, offset + 256, UsbIp.BusIdLength),
            BusNum = BigEndian.ReadU32(buffer, offset + 288),
            DevNum = BigEndian.ReadU32(buffer, offset + 292),
            Speed = BigEndian.ReadU32(buffer, offset + 296),
            VendorId = BigEndian.ReadU16(buffer, offset + 300),
            ProductId = BigEndian.ReadU16(buffer, offset + 302),
            BcdDevice = BigEndian.ReadU16(buffer, offset + 304),
            DeviceClass = BigEndian.ReadU8(buffer, offset + 306),
            DeviceSubClass = BigEndian.ReadU8(buffer, offset + 307),
            DeviceProtocol = BigEndian.ReadU8(buffer, offset + 308),
            ConfigurationValue = BigEndian.ReadU8(buffer, offset + 309),
            NumConfigurations = BigEndian.ReadU8(buffer, offset + 310),
            NumInterfaces = BigEndian.ReadU8(buffer, offset + 311),
        };
        offset += UsbIpDevice.WireSize;

        int ifaceCount = d.NumInterfaces;
        if (offset + ifaceCount * UsbIpDevice.InterfaceWireSize > buffer.Length)
            throw new UsbIpProtocolException("device list truncated inside interface records");

        if (ifaceCount > 0)
        {
            var list = new UsbIpInterface[ifaceCount];
            for (int i = 0; i < ifaceCount; i++)
            {
                list[i] = new UsbIpInterface
                {
                    InterfaceClass = BigEndian.ReadU8(buffer, offset + 0),
                    InterfaceSubClass = BigEndian.ReadU8(buffer, offset + 1),
                    InterfaceProtocol = BigEndian.ReadU8(buffer, offset + 2),
                };
                offset += UsbIpDevice.InterfaceWireSize;
            }
            d.Interfaces = list;
        }

        return d;
    }

    /// <summary>Parses a complete <c>OP_REP_DEVLIST</c> reply (client-side / test use).</summary>
    public static IReadOnlyList<UsbIpDevice> DecodeDevlistReply(ReadOnlySpan<byte> buffer)
    {
        OpHeader header = ReadOpHeader(buffer);
        ValidateVersion(header);
        if (header.Code != UsbIp.OpRepDevlist)
            throw new UsbIpProtocolException($"expected OP_REP_DEVLIST, got 0x{header.Code:x4}");
        if (header.Status != 0)
            return Array.Empty<UsbIpDevice>();

        uint ndev = BigEndian.ReadU32(buffer, OpHeaderSize);
        if (ndev > 4096)
            throw new UsbIpProtocolException($"implausible device count {ndev} in devlist reply");

        var devices = new List<UsbIpDevice>((int)ndev);
        int offset = OpHeaderSize + 4;
        for (uint i = 0; i < ndev; i++)
            devices.Add(ReadDeviceRecord(buffer, ref offset));
        return devices;
    }

    /// <summary>
    /// A successful <c>OP_REP_IMPORT</c>: <c>op_common</c> + the 312-byte device record.
    /// </summary>
    public static byte[] EncodeImportReply(UsbIpDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var buffer = new byte[OpHeaderSize + UsbIpDevice.WireSize];
        var span = buffer.AsSpan();
        WriteOpHeader(span, UsbIp.OpRepImport, 0);
        WriteDeviceRecord(span, OpHeaderSize, device);
        return buffer;
    }

    /// <summary>
    /// A failed <c>OP_REP_IMPORT</c>. The kernel sends only <c>op_common</c>, with a non-zero
    /// status; the client turns it into its own error text. Also used for every
    /// <see cref="BindStatus"/> refusal, which is how VirtualThere reports
    /// <see cref="BindStatus.DeviceBusy"/>, <see cref="BindStatus.PasswordRequired"/> and the
    /// rest — and how it would report a device-count refusal, if such a concept existed
    /// (it does not: see <see cref="BindStatus"/>).
    /// </summary>
    public static byte[] EncodeImportError(uint status)
    {
        var buffer = new byte[OpHeaderSize];
        WriteOpHeader(buffer, UsbIp.OpRepImport, status);
        return buffer;
    }

    // =====================================================================================
    // VirtualThere's op-phase extension: export metadata (device nicknames)
    // =====================================================================================

    /// <summary>
    /// One entry of an <c>OP_REP_VHT_DEVINFO</c>: a device the server exports and the
    /// operator's nickname for it (<see langword="null"/> when none is configured).
    /// </summary>
    public readonly record struct VhtDeviceInfo(string BusId, string? Nickname);

    /// <summary>Fixed part of one reply entry: 32-byte busid plus the 1-byte name length.</summary>
    public const int VhtDeviceInfoEntryHeaderSize = UsbIp.BusIdLength + 1;

    /// <summary>Upper bound on entries in one extension reply (a parse bound, not a limit).</summary>
    public const int MaxVhtDeviceInfoEntries = 4096;

    /// <summary>
    /// The <c>OP_REQ_VHT_DEVINFO</c> request: <c>op_common</c> + the extension magic.
    /// 12 bytes, no length field, nothing for a peer to be confused by.
    /// </summary>
    public static byte[] EncodeVhtDeviceInfoRequest()
    {
        var buffer = new byte[OpHeaderSize + 4];
        WriteOpHeader(buffer, UsbIp.OpReqVhtDevInfo);
        BigEndian.WriteU32(buffer, OpHeaderSize, UsbIp.VhtDevInfoMagic);
        return buffer;
    }

    /// <summary>
    /// Builds a complete <c>OP_REP_VHT_DEVINFO</c> reply.
    /// Layout: <c>op_common</c>, u32 magic, u32 count, then per entry a 32-byte NUL-padded
    /// ASCII busid, a u8 name length and that many UTF-8 name bytes.
    /// </summary>
    public static byte[] EncodeVhtDeviceInfoReply(IReadOnlyList<VhtDeviceInfo> entries, uint status = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);

        long total = OpHeaderSize + 8L;
        var names = new byte[entries.Count][];
        for (int i = 0; i < entries.Count; i++)
        {
            names[i] = EncodeNickname(entries[i].Nickname);
            total += VhtDeviceInfoEntryHeaderSize + names[i].Length;
        }

        if (total > int.MaxValue)
            throw new UsbIpProtocolException($"device info reply too large ({total} bytes)");

        var buffer = new byte[(int)total];
        var span = buffer.AsSpan();

        WriteOpHeader(span, UsbIp.OpRepVhtDevInfo, status);
        BigEndian.WriteU32(span, OpHeaderSize, UsbIp.VhtDevInfoMagic);
        BigEndian.WriteU32(span, OpHeaderSize + 4, (uint)entries.Count);

        int offset = OpHeaderSize + 8;
        for (int i = 0; i < entries.Count; i++)
        {
            BigEndian.WriteFixedString(span, offset, UsbIp.BusIdLength, entries[i].BusId);
            BigEndian.WriteU8(span, offset + UsbIp.BusIdLength, (byte)names[i].Length);
            names[i].CopyTo(span[(offset + VhtDeviceInfoEntryHeaderSize)..]);
            offset += VhtDeviceInfoEntryHeaderSize + names[i].Length;
        }

        return buffer;
    }

    /// <summary>
    /// Parses a complete <c>OP_REP_VHT_DEVINFO</c> reply. Every length is validated against
    /// the frame before anything is read, and a name length above
    /// <see cref="UsbIp.VhtNicknameMaxBytes"/> is a protocol error rather than a big read.
    /// </summary>
    public static IReadOnlyList<VhtDeviceInfo> DecodeVhtDeviceInfoReply(ReadOnlySpan<byte> buffer)
    {
        OpHeader header = ReadOpHeader(buffer);
        ValidateVersion(header);

        if (header.Code != UsbIp.OpRepVhtDevInfo)
            throw new UsbIpProtocolException($"expected OP_REP_VHT_DEVINFO, got 0x{header.Code:x4}");
        if (header.Status != 0)
            return Array.Empty<VhtDeviceInfo>();

        if (BigEndian.ReadU32(buffer, OpHeaderSize) != UsbIp.VhtDevInfoMagic)
            throw new UsbIpProtocolException("device info reply is not a VHT1 magic frame");

        uint count = BigEndian.ReadU32(buffer, OpHeaderSize + 4);
        if (count > MaxVhtDeviceInfoEntries)
            throw new UsbIpProtocolException($"implausible device info count {count} in reply");

        var entries = new List<VhtDeviceInfo>((int)count);
        int offset = OpHeaderSize + 8;

        for (uint i = 0; i < count; i++)
        {
            if (offset + VhtDeviceInfoEntryHeaderSize > buffer.Length)
                throw new UsbIpProtocolException("device info reply truncated inside an entry header");

            string busId = BigEndian.ReadFixedString(buffer, offset, UsbIp.BusIdLength);
            int nameLength = BigEndian.ReadU8(buffer, offset + UsbIp.BusIdLength);
            if (nameLength > UsbIp.VhtNicknameMaxBytes)
                throw new UsbIpProtocolException(
                    $"device info name length {nameLength} exceeds the {UsbIp.VhtNicknameMaxBytes}-byte maximum");
            offset += VhtDeviceInfoEntryHeaderSize;

            if (offset + nameLength > buffer.Length)
                throw new UsbIpProtocolException("device info reply truncated inside a name");

            string? nickname = nameLength == 0
                ? null
                : System.Text.Encoding.UTF8.GetString(buffer.Slice(offset, nameLength));
            offset += nameLength;

            entries.Add(new VhtDeviceInfo(busId, nickname));
        }

        return entries;
    }

    /// <summary>
    /// UTF-8 encodes a nickname, truncated to <see cref="UsbIp.VhtNicknameMaxBytes"/> without
    /// splitting a codepoint. An empty or absent nickname encodes as zero bytes.
    /// </summary>
    public static byte[] EncodeNickname(string? nickname)
    {
        if (string.IsNullOrEmpty(nickname))
            return Array.Empty<byte>();

        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(nickname);
        if (utf8.Length <= UsbIp.VhtNicknameMaxBytes)
            return utf8;

        // 0b10xxxxxx is a UTF-8 continuation byte: back up to the start of the char it belongs to
        // so the truncated name is still valid UTF-8 on the far side.
        int end = UsbIp.VhtNicknameMaxBytes;
        while (end > 0 && (utf8[end] & 0xC0) == 0x80)
            end--;
        return utf8[..end];
    }

    // =====================================================================================
    // URB phase
    // =====================================================================================

    /// <summary>A parsed URB-phase request. <see cref="Payload"/> is a view into the caller's buffer.</summary>
    public readonly struct UrbRequest
    {
        public UsbIpBasicHeader Basic { get; init; }
        public UsbIpCmdSubmit Submit { get; init; }
        public UsbIpCmdUnlink Unlink { get; init; }
        public ReadOnlyMemory<byte> Payload { get; init; }
        public IReadOnlyList<IsoPacketDescriptor> IsoPackets { get; init; }

        public bool IsSubmit => Basic.Command == UsbIp.CmdSubmit;
        public bool IsUnlink => Basic.Command == UsbIp.CmdUnlink;
    }

    /// <summary>
    /// Number of bytes a submit packet occupies after the 48-byte header
    /// (<c>transfer_buffer_length</c> plus the ISO descriptors, when present).
    /// Returns 0 for unlink packets, which have no body.
    /// </summary>
    /// <remarks>
    /// The transfer buffer is transmitted <b>only for an OUT transfer</b>; for an IN transfer
    /// <c>transfer_buffer_length</c> is the size the client wants to read, not a payload
    /// (usbip_protocol.rst: "If direction is USBIP_DIR_OUT then n equals
    /// transfer_buffer_length; otherwise n equals 0"; the kernel's <c>usbip_recv_xbuff</c>
    /// returns 0 for an IN pipe). ISO descriptors always trail the buffer.
    /// </remarks>
    public static long SubmitBodyLength(UsbIpBasicHeader basic, UsbIpCmdSubmit submit)
    {
        if (basic.Command != UsbIp.CmdSubmit)
            return 0;

        long body = basic.Direction == UsbIp.DirOut ? submit.TransferBufferLength : 0;
        if (submit.IsIso)
            body += (long)submit.NumberOfPackets * UsbIp.IsoPacketDescriptorSize;
        return body;
    }

    /// <summary>
    /// Parses the 48-byte URB header that has already been read from the wire.
    /// </summary>
    /// <param name="header">Exactly <see cref="UsbIp.HeaderSize"/> bytes.</param>
    public static (UsbIpBasicHeader Basic, UsbIpCmdSubmit Submit, UsbIpCmdUnlink Unlink) ParseUrbHeader(
        ReadOnlySpan<byte> header)
    {
        if (header.Length < UsbIp.HeaderSize)
            throw new UsbIpProtocolException(
                $"URB header truncated: need {UsbIp.HeaderSize} bytes, got {header.Length}");

        var basic = new UsbIpBasicHeader
        {
            Command = BigEndian.ReadU32(header, 0),
            SeqNum = BigEndian.ReadU32(header, 4),
            DevId = BigEndian.ReadU32(header, 8),
            Direction = BigEndian.ReadU32(header, 12),
            Ep = BigEndian.ReadU32(header, 16),
        };

        switch (basic.Command)
        {
            case UsbIp.CmdSubmit:
            {
                var submit = new UsbIpCmdSubmit
                {
                    TransferFlags = BigEndian.ReadU32(header, 20),
                    TransferBufferLength = BigEndian.ReadU32(header, 24),
                    StartFrame = BigEndian.ReadU32(header, 28),
                    NumberOfPackets = BigEndian.ReadU32(header, 32),
                    Interval = BigEndian.ReadU32(header, 36),
                    Setup = BigEndian.ReadU64(header, 40),
                };
                ValidateSubmit(submit);
                return (basic, submit, default);
            }
            case UsbIp.CmdUnlink:
            {
                var unlink = new UsbIpCmdUnlink
                {
                    UnlinkSeqNum = BigEndian.ReadU32(header, 20),
                };
                return (basic, default, unlink);
            }
            case UsbIp.RetSubmit or UsbIp.RetUnlink:
                throw new UsbIpProtocolException(
                    $"received a return packet (0x{basic.Command:x}) from a client; " +
                    "the server only accepts USBIP_CMD_SUBMIT/UNLINK");
            default:
                throw new UsbIpProtocolException($"unknown URB command 0x{basic.Command:x}");
        }
    }

    /// <summary>
    /// Bounds-checks every length field of a submit before any buffer is sized from it.
    /// </summary>
    public static void ValidateSubmit(UsbIpCmdSubmit submit)
    {
        if (submit.TransferBufferLength > UsbIp.MaxTransferBuffer)
            throw new UsbIpProtocolException(
                $"transfer_buffer_length {submit.TransferBufferLength} exceeds the {UsbIp.MaxTransferBuffer}-byte maximum");

        // number_of_packets is 0xffffffff (or 0, which some clients send) for a non-ISO
        // transfer and the packet count for an ISO one; anything else IS the ISO count.
        if (submit.IsIso && submit.NumberOfPackets > UsbIp.MaxIsoPackets)
            throw new UsbIpProtocolException(
                $"number_of_packets {submit.NumberOfPackets} exceeds the {UsbIp.MaxIsoPackets} maximum");
    }

    /// <summary>Parses the ISO descriptors that trail a submit's transfer buffer.</summary>
    public static IReadOnlyList<IsoPacketDescriptor> ParseIsoDescriptors(
        ReadOnlySpan<byte> tail, uint numberOfPackets)
    {
        if (numberOfPackets is 0 or UsbIp.NoIsoPackets)
            return Array.Empty<IsoPacketDescriptor>();
        if (numberOfPackets > UsbIp.MaxIsoPackets)
            throw new UsbIpProtocolException($"too many ISO descriptors: {numberOfPackets}");

        long needed = (long)numberOfPackets * UsbIp.IsoPacketDescriptorSize;
        if (tail.Length < needed)
            throw new UsbIpProtocolException(
                $"ISO descriptor block truncated: need {needed} bytes, got {tail.Length}");

        var list = new IsoPacketDescriptor[numberOfPackets];
        for (int i = 0; i < numberOfPackets; i++)
        {
            int o = i * UsbIp.IsoPacketDescriptorSize;
            list[i] = new IsoPacketDescriptor
            {
                Offset = BigEndian.ReadU32(tail, o + 0),
                Length = BigEndian.ReadU32(tail, o + 4),
                ActualLength = BigEndian.ReadU32(tail, o + 8),
                Status = BigEndian.ReadI32(tail, o + 12),
            };
        }
        return list;
    }

    /// <summary>
    /// Builds a <c>USBIP_RET_SUBMIT</c> packet: the 48-byte header (always the size of the
    /// largest union member, which is what stock clients read) followed by the data buffer
    /// for a successful IN transfer.
    /// </summary>
    public static byte[] EncodeRetSubmit(
        UsbIpBasicHeader request,
        int status,
        ReadOnlySpan<byte> data,
        uint startFrame = 0,
        uint numberOfPackets = 0,
        uint errorCount = 0,
        IReadOnlyList<IsoPacketDescriptor>? packets = null,
        uint? actualLength = null)
    {
        if (status == 0 && data.Length > UsbIp.MaxTransferBuffer)
            throw new UsbIpProtocolException($"outbound data length {data.Length} out of range");

        int packetBytes = packets is { Count: > 0 } ? packets.Count * UsbIp.IsoPacketDescriptorSize : 0;
        var buffer = new byte[UsbIp.HeaderSize + data.Length + packetBytes];
        var span = buffer.AsSpan();

        BigEndian.WriteU32(span, 0, UsbIp.RetSubmit);
        BigEndian.WriteU32(span, 4, request.SeqNum);
        BigEndian.WriteU32(span, 8, request.DevId);
        // usbip_header_basic: direction and ep are "only used by client, for server this shall
        // be 0". Linux's vhci ignores them, but conforming costs nothing and removes a way for
        // a stricter client to disagree with us.
        BigEndian.WriteU32(span, 12, 0);
        BigEndian.WriteU32(span, 16, 0);

        BigEndian.WriteI32(span, 20, status);
        // <c>actual_length</c> is not always <c>data.Length</c>: an OUT transfer has no data
        // stage on the wire but still transferred bytes, and the kernel's client reports that
        // count (usbip_ret_submit.actual_length is urb->actual_length). Sending 0 for a
        // successful OUT URB makes the client believe nothing was written to the device.
        BigEndian.WriteU32(span, 24, actualLength ?? (uint)data.Length);
        BigEndian.WriteU32(span, 28, startFrame);
        BigEndian.WriteU32(span, 32, numberOfPackets);
        BigEndian.WriteU32(span, 36, errorCount);
        BigEndian.WriteU64(span, 40, 0);

        data.CopyTo(span[UsbIp.HeaderSize..]);

        if (packets is { Count: > 0 })
        {
            int o = UsbIp.HeaderSize + data.Length;
            for (int i = 0; i < packets.Count; i++)
            {
                IsoPacketDescriptor p = packets[i];
                BigEndian.WriteU32(span, o + 0, p.Offset);
                BigEndian.WriteU32(span, o + 4, p.Length);
                BigEndian.WriteU32(span, o + 8, p.ActualLength);
                BigEndian.WriteI32(span, o + 12, p.Status);
                o += UsbIp.IsoPacketDescriptorSize;
            }
        }

        return buffer;
    }

    /// <summary>
    /// Builds a <c>USBIP_RET_UNLINK</c>: <c>op_common</c>-sized payload shape is not used —
    /// the URB phase always carries the full 48-byte header with the unlink status in the
    /// first return field. <see cref="UsbIp.StatusEnoent"/> means "already completed".
    /// </summary>
    public static byte[] EncodeRetUnlink(UsbIpBasicHeader request, int status)
    {
        var buffer = new byte[UsbIp.HeaderSize];
        var span = buffer.AsSpan();

        BigEndian.WriteU32(span, 0, UsbIp.RetUnlink);
        BigEndian.WriteU32(span, 4, request.SeqNum);
        BigEndian.WriteU32(span, 8, request.DevId);
        BigEndian.WriteU32(span, 12, 0);
        BigEndian.WriteU32(span, 16, 0);
        BigEndian.WriteI32(span, 20, status);
        BigEndian.WriteU32(span, 24, 0);
        BigEndian.WriteU32(span, 28, 0);
        BigEndian.WriteU32(span, 32, 0);
        BigEndian.WriteU32(span, 36, 0);
        BigEndian.WriteU64(span, 40, 0);

        return buffer;
    }

    /// <summary>
    /// Builds a complete <c>USBIP_CMD_SUBMIT</c> packet. Used by the acceptance harness and
    /// the unit tests to drive a real server over a real socket without a Linux client.
    /// </summary>
    public static byte[] EncodeCmdSubmit(
        uint seqNum,
        uint devId,
        uint direction,
        byte ep,
        uint transferFlags,
        ReadOnlySpan<byte> payload,
        ulong setup = 0,
        uint startFrame = 0,
        uint numberOfPackets = UsbIp.NoIsoPackets,
        uint? transferBufferLength = null)
    {
        var buffer = new byte[UsbIp.HeaderSize + payload.Length];
        var span = buffer.AsSpan();

        BigEndian.WriteU32(span, 0, UsbIp.CmdSubmit);
        BigEndian.WriteU32(span, 4, seqNum);
        BigEndian.WriteU32(span, 8, devId);
        BigEndian.WriteU32(span, 12, direction);
        BigEndian.WriteU32(span, 16, ep);

        uint flags = transferFlags;
        if (direction == UsbIp.DirIn) flags |= UsbIp.UrbDirIn;
        BigEndian.WriteU32(span, 20, flags);

        // For an IN transfer the wire carries no payload, so transfer_buffer_length is the
        // requested read size and must be settable independently of payload.Length.
        BigEndian.WriteU32(span, 24, transferBufferLength ?? (uint)payload.Length);
        BigEndian.WriteU32(span, 28, startFrame);
        BigEndian.WriteU32(span, 32, numberOfPackets);
        BigEndian.WriteU32(span, 36, 0);
        BigEndian.WriteU64(span, 40, setup);

        payload.CopyTo(span[UsbIp.HeaderSize..]);
        return buffer;
    }

    /// <summary>Builds a complete <c>USBIP_CMD_UNLINK</c> packet.</summary>
    public static byte[] EncodeCmdUnlink(uint seqNum, uint devId, uint unlinkSeqNum, byte ep = 0)
    {
        var buffer = new byte[UsbIp.HeaderSize];
        var span = buffer.AsSpan();

        BigEndian.WriteU32(span, 0, UsbIp.CmdUnlink);
        BigEndian.WriteU32(span, 4, seqNum);
        BigEndian.WriteU32(span, 8, devId);
        BigEndian.WriteU32(span, 12, UsbIp.DirOut);
        BigEndian.WriteU32(span, 16, ep);
        BigEndian.WriteU32(span, 20, unlinkSeqNum);
        return buffer;
    }

    // =====================================================================================
    // Socket plumbing shared by server and tests
    // =====================================================================================

    /// <summary>
    /// Fills <paramref name="buffer"/> completely, or throws. A partial read is normal on a
    /// stream socket, so every fixed-size header read goes through here.
    /// </summary>
    public static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException(
                    $"connection closed after {read} of {buffer.Length} byte(s)");
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes into a fresh array.</summary>
    public static async ValueTask<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var buffer = new byte[count];
        await ReadExactAsync(stream, buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>
    /// The largest single allocation the codec is ever permitted to make from a wire length
    /// field. Kept explicit so a reviewer can see the bound rather than infer it.
    /// </summary>
    public static int MaxFrameAllocation => UsbIp.MaxTransferBuffer + UsbIp.HeaderSize
        + UsbIp.MaxIsoPackets * UsbIp.IsoPacketDescriptorSize;
}
