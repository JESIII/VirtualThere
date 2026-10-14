namespace Vht.Core.Protocol;

/// <summary>
/// <c>usbip_header_basic</c> — the 20-byte prefix common to every USB/IP packet.
/// </summary>
public readonly struct UsbIpBasicHeader
{
    /// <summary>Operation / command / return code.</summary>
    public uint Command { get; init; }

    /// <summary>Monotonic request id chosen by the client; echoed on every reply.</summary>
    public uint SeqNum { get; init; }

    /// <summary>Device id: <c>busnum &lt;&lt; 16 | devnum</c>.</summary>
    public uint DevId { get; init; }

    /// <summary>Transfer direction: 0 = OUT (host to device), 1 = IN (device to host).</summary>
    public uint Direction { get; init; }

    /// <summary>Endpoint number (0..15) the client named. The transfer type is not on the wire.</summary>
    public uint Ep { get; init; }
    /// <summary>
    /// The endpoint number (0..15) carried in <see cref="Ep"/>.
    /// </summary>
    /// <remarks>
    /// <c>usbip_header_basic.ep</c> is the <b>endpoint number only</b> — the transfer type is
    /// not on the wire at all. The reference server derives it from its own endpoint
    /// descriptors (<c>get_pipe()</c> in <c>stub_rx.c</c>), which is why the type is resolved
    /// against the device in the session rather than read out of this field.
    /// </remarks>
    public byte EndpointNumber => UsbIp.EndpointNumber((byte)Ep);

    /// <summary>True when the transfer direction is IN (device to host).</summary>
    public bool IsIn => Direction == UsbIp.DirIn;

    /// <summary>Bus number carried in <see cref="DevId"/>.</summary>
    public ushort BusNum => (ushort)(DevId >> 16);

    /// <summary>Device number carried in <see cref="DevId"/>.</summary>
    public ushort DevNum => (ushort)(DevId & 0xffff);

    public override string ToString() =>
        $"cmd=0x{Command:x4} seq={SeqNum} devid={DevId:x8} dir={(Direction == UsbIp.DirIn ? "in" : "out")} ep={Ep:x2}";
}

/// <summary>
/// <c>usbip_header_cmd_submit</c> — one URB submitted by the client.
/// </summary>
public readonly struct UsbIpCmdSubmit
{
    /// <summary>Sequence number of the URB (independent of the header's <see cref="UsbIpBasicHeader.SeqNum"/>).</summary>
    public uint TransferFlags { get; init; }

    /// <summary>Number of bytes to transfer.</summary>
    public uint TransferBufferLength { get; init; }

    /// <summary>ISO start frame; 0 for non-ISO transfers.</summary>
    public uint StartFrame { get; init; }

    /// <summary>ISO packet count, or <see cref="UsbIp.NoIsoPackets"/> when not ISO.</summary>
    public uint NumberOfPackets { get; init; }

    /// <summary>ISO interval; 0 for non-ISO transfers.</summary>
    public uint Interval { get; init; }

    /// <summary>8-byte setup packet for control transfers.</summary>
    public ulong Setup { get; init; }

    /// <summary>True when this transfer is isochronous.</summary>
    public bool IsIso => NumberOfPackets is not (0 or UsbIp.NoIsoPackets);

    /// <summary>Decoded setup packet bytes (little-endian on the wire, as USB defines it).</summary>
    public byte[] SetupBytes() => BitConverter.GetBytes(Setup);
}

/// <summary>
/// <c>usbip_header_cmd_unlink</c> — a cancellation for a previously submitted URB.
/// </summary>
public readonly struct UsbIpCmdUnlink
{
    /// <summary><c>seqnum</c> of the URB being cancelled.</summary>
    public uint UnlinkSeqNum { get; init; }
}

/// <summary>
/// <c>usbip_header_ret_submit</c> — the server's completion of a URB.
/// </summary>
public readonly struct UsbIpRetSubmit
{
    /// <summary>Linux errno-style status; 0 on success, negative on failure.</summary>
    public int Status { get; init; }

    /// <summary>Bytes actually transferred.</summary>
    public uint ActualLength { get; init; }

    /// <summary>ISO start frame echoed back.</summary>
    public uint StartFrame { get; init; }

    /// <summary>ISO packet count echoed back.</summary>
    public uint NumberOfPackets { get; init; }

    /// <summary>Error count for ISO transfers.</summary>
    public uint ErrorCount { get; init; }

    /// <summary>Per-packet descriptors, empty for non-ISO transfers.</summary>
    public IReadOnlyList<IsoPacketDescriptor> Packets { get; init; }
}

/// <summary>
/// <c>usbip_header_ret_unlink</c> — the server's answer to a cancellation.
/// </summary>
public readonly struct UsbIpRetUnlink
{
    /// <summary>0 when the URB was cancelled, negative when it could not be.</summary>
    public int Status { get; init; }
}

/// <summary>
/// <c>usbip_iso_packet_descriptor</c> — per-packet ISO outcome.
/// </summary>
public readonly struct IsoPacketDescriptor
{
    public uint Offset { get; init; }
    public uint Length { get; init; }
    public uint ActualLength { get; init; }
    public int Status { get; init; }
}

/// <summary>
/// <c>usbip_usb_interface</c> — one 4-byte interface record in a device-list reply.
/// </summary>
public readonly struct UsbIpInterface
{
    public byte InterfaceClass { get; init; }
    public byte InterfaceSubClass { get; init; }
    public byte InterfaceProtocol { get; init; }
}

/// <summary>
/// <c>usbip_usb_device</c> — one device record in an <c>OP_REP_DEVLIST</c> reply.
/// Field widths are fixed on the wire (312 bytes) so stock clients parse them.
/// </summary>
public sealed class UsbIpDevice
{
    /// <summary>Sysfs-style path of the device, e.g. <c>/sys/devices/pci0000:00/.../usb1/1-1</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Bus id in <c>bus-port[.port...]</c> form, e.g. <c>1-2.3</c>.</summary>
    public string BusId { get; set; } = string.Empty;

    public uint BusNum { get; set; }
    public uint DevNum { get; set; }
    public uint Speed { get; set; }
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    public ushort BcdDevice { get; set; }

    public byte DeviceClass { get; set; }
    public byte DeviceSubClass { get; set; }
    public byte DeviceProtocol { get; set; }

    public byte ConfigurationValue { get; set; }
    public byte NumConfigurations { get; set; }
    public byte NumInterfaces { get; set; }

    /// <summary>Per-interface class triplets sent after this record in a device-list reply.
    /// When empty, <see cref="NumInterfaces"/> zero-filled records are emitted so the
    /// reply length stays internally consistent.</summary>
    public IReadOnlyList<UsbIpInterface> Interfaces { get; set; } = Array.Empty<UsbIpInterface>();

    /// <summary>On-wire size of one interface record.</summary>
    public const int InterfaceWireSize = 4;

    /// <summary>Manufacturer string, as reported by the device descriptor.</summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>Product string, as reported by the device descriptor.</summary>
    public string Product { get; set; } = string.Empty;

    /// <summary>Serial number, as reported by the device descriptor.</summary>
    public string Serial { get; set; } = string.Empty;

    /// <summary>On-wire size of one device record.</summary>
    public const int WireSize = 312;

    /// <summary>
    /// The recon's enumeration vocabulary, kept identical so the two products can be
    /// compared line for line (ARCHITECTURE.md §4.2):
    /// <c>Found %s speed device [%04x:%04x] "%s, %s" at address %u</c>.
    /// </summary>
    public string DescribeFound() =>
        $"Found {UsbIp.SpeedName(Speed)} speed device [{VendorId:x4}:{ProductId:x4}] " +
        $"\"{Product}, {Manufacturer}\" at address {DevNum}";

    public override string ToString() => $"{BusId} [{VendorId:x4}:{ProductId:x4}] {Product}";
}
