namespace Vht.Core.Protocol;

/// <summary>
/// USB/IP wire constants, version 1.1.1 as documented by the Linux kernel
/// (<c>Documentation/usb/usbip_protocol.rst</c>, protocol version <c>0x0111</c>).
/// All multi-byte fields on the wire are <b>big-endian</b>.
/// </summary>
public static class UsbIp
{
    /// <summary>Protocol version advertised in every reply header (<c>0x0111</c> = 1.1.1).</summary>
    public const ushort ProtocolVersion = 0x0111;

    // ---- operation codes (basic header, first 2 bytes) -------------------------------

    /// <summary><c>OP_REQ_DEVLIST</c> — client asks for the exported device list.</summary>
    public const ushort OpReqDevlist = 0x8005;

    /// <summary><c>OP_REP_DEVLIST</c> — server answers with the device list.</summary>
    public const ushort OpRepDevlist = 0x0005;

    /// <summary><c>OP_REQ_IMPORT</c> — client asks to import one device.</summary>
    public const ushort OpReqImport = 0x8003;

    /// <summary><c>OP_REP_IMPORT</c> — server answers the import request.</summary>
    public const ushort OpRepImport = 0x0003;

    // ---- command/return codes (URB phase, first 4 bytes) ------------------------------

    /// <summary><c>USBIP_CMD_SUBMIT</c> — client submits a URB.</summary>
    public const uint CmdSubmit = 1;

    /// <summary><c>USBIP_CMD_UNLINK</c> — client cancels a previously submitted URB.</summary>
    public const uint CmdUnlink = 2;

    /// <summary><c>USBIP_RET_SUBMIT</c> — server completes a URB.</summary>
    public const uint RetSubmit = 3;

    /// <summary><c>USBIP_RET_UNLINK</c> — server acknowledges a cancellation.</summary>
    public const uint RetUnlink = 4;

    // ---- transfer direction (usbip_header_basic.direction) ----------------------------

    public const uint DirOut = 0; // host -> device
    public const uint DirIn = 1;  // device -> host

    // ---- endpoint number encoding -----------------------------------------------------
    // Bits 0..3 = endpoint number, bits 4..6 = transfer type, bit 7 = direction (IN).

    public const byte EpDirIn = 0x80;
    public const byte EpTypeMask = 0x70;
    public const byte EpTypeControl = 0x00;
    public const byte EpTypeIso = 0x10;
    public const byte EpTypeBulk = 0x20;
    public const byte EpTypeInterrupt = 0x30;

    // ---- transfer flags (usbip_header_cmd_submit.transfer_flags) ----------------------

    public const uint UrbShortNotOk = 0x0001;
    public const uint UrbIsoAsap = 0x0002;
    public const uint UrbNoTransferDma = 0x0004;
    public const uint UrbZeroPacket = 0x0008;
    public const uint UrbFreeBuffer = 0x0010;
    public const uint UrbDirIn = 0x0200;
    public const uint UrbDirOut = 0x0000;

    // ---- speed values (as reported in usbip_usb_device.speed) -------------------------

    public const uint SpeedUnknown = 0;
    public const uint SpeedLow = 1;
    public const uint SpeedFull = 2;
    public const uint SpeedHigh = 3;
    public const uint SpeedWireless = 4;
    public const uint SpeedSuper = 5;
    public const uint SpeedSuperPlus = 6;

    // ---- sizes ------------------------------------------------------------------------

    /// <summary>Size of <c>usbip_header_basic</c> on the wire, in bytes.</summary>
    public const int BasicHeaderSize = 20;

    /// <summary>Full size of <c>usbip_header</c> (basic + command union), in bytes.</summary>
    public const int HeaderSize = 48;

    /// <summary>Size of the <c>setup</c> field of a submit request, in bytes.</summary>
    public const int SetupSize = 8;

    /// <summary>Size of one <c>usbip_iso_packet_descriptor</c>, in bytes.</summary>
    public const int IsoPacketDescriptorSize = 16;

    /// <summary>
    /// Value of <c>number_of_packets</c> for a non-isochronous transfer. The kernel sends
    /// <c>0xffffffff</c>; some clients send <c>0</c>. Both mean "not isochronous".
    /// </summary>
    public const uint NoIsoPackets = 0xffffffff;

    /// <summary>
    /// Hard ceiling on an inbound transfer buffer. Bounded parsing is mandatory
    /// (ARCHITECTURE.md §6.1, risk 10): no allocation is ever driven by an unchecked
    /// length field.
    /// </summary>
    public const int MaxTransferBuffer = 16 * 1024 * 1024;

    /// <summary>Hard ceiling on the number of isochronous descriptors in one submit.</summary>
    public const int MaxIsoPackets = 1024;

    /// <summary>Hard ceiling on a device-list reply buffer we are willing to build.</summary>
    public const int MaxDevlistEntryNote = 1024 * 1024;

    /// <summary>Busid length ceiling, matching the kernel's <c>usbip_usb_device</c> busid field.</summary>
    public const int BusIdLength = 32;

    /// <summary>Path length ceiling, matching the kernel's <c>usbip_usb_device</c> path field.</summary>
    public const int PathLength = 256;

    /// <summary>
    /// The USB/IP status code the kernel expects for a transfer that was cancelled or whose
    /// device disappeared. <c>-ECONNRESET</c> is what usbip-host uses for an aborted URB.
    /// </summary>
    public const int StatusEconnreset = -104;

    /// <summary><c>-ENOENT</c>, used by RET_UNLINK when the target URB is already gone.</summary>
    public const int StatusEnoent = -2;

    /// <summary><c>-ESHUTDOWN</c>, used when the server is going down mid-transfer.</summary>
    public const int StatusEshutdown = -108;

    /// <summary><c>-EPIPE</c>, used when the device handle is no longer usable.</summary>
    public const int StatusEpipe = -32;

    /// <summary>Decodes the transfer type bits of an endpoint address.</summary>
    public static byte TransferType(byte ep) => (byte)(ep & EpTypeMask);

    /// <summary>True when the endpoint address carries the IN direction bit.</summary>
    public static bool IsInEndpoint(byte ep) => (ep & EpDirIn) != 0;

    /// <summary>Extracts the endpoint number (0..15) from an endpoint address.</summary>
    public static byte EndpointNumber(byte ep) => (byte)(ep & 0x0f);

    /// <summary>Human-readable name of a transfer type.</summary>
    public static string TransferTypeName(byte ep) => TransferType(ep) switch
    {
        EpTypeControl => "control",
        EpTypeIso => "isochronous",
        EpTypeBulk => "bulk",
        EpTypeInterrupt => "interrupt",
        _ => "unknown",
    };

    /// <summary>Human-readable name of a USB speed value, matching the recon's vocabulary.</summary>
    public static string SpeedName(uint speed) => speed switch
    {
        SpeedLow => "low",
        SpeedFull => "full",
        SpeedHigh => "high",
        SpeedWireless => "wireless",
        SpeedSuper => "super",
        SpeedSuperPlus => "superplus",
        _ => "unknown",
    };
}
