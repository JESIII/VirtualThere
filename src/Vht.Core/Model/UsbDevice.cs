using Vht.Core.Protocol;

namespace Vht.Core.Model;

/// <summary>One endpoint of an exported device, as discovered from the configuration descriptor.</summary>
public sealed class EndpointDescriptor
{
    /// <summary>Endpoint address including direction bit and transfer-type bits.</summary>
    public byte Address { get; init; }

    /// <summary>Transfer type (control/iso/bulk/interrupt) decoded from <see cref="Address"/>.</summary>
    public byte TransferType => UsbIp.TransferType(Address);

    /// <summary>Maximum packet size in bytes.</summary>
    public ushort MaxPacketSize { get; init; }

    /// <summary>Polling interval for interrupt/ISO endpoints.</summary>
    public byte Interval { get; init; }

    public bool IsIn => UsbIp.IsInEndpoint(Address);

    public override string ToString() =>
        $"ep 0x{Address:x2} ({UsbIp.TransferTypeName(Address)}, {(IsIn ? "in" : "out")}, max={MaxPacketSize})";
}

/// <summary>
/// The server's model of one local USB device: identity, descriptors, capture verdict and
/// the USB/IP device record handed to clients.
/// </summary>
public sealed class UsbDevice
{
    public required DeviceKey Key { get; init; }

    /// <summary>Windows device instance path (<c>USB\VID_1234&amp;PID_5678\serial</c>).</summary>
    public string InstancePath { get; init; } = string.Empty;

    /// <summary>Bus id advertised over USB/IP, in <c>bus-port</c> form.</summary>
    public string BusId => Key.BusId;

    public uint BusNum { get; init; }
    public uint DevNum { get; init; }
    public uint Speed { get; init; }

    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort BcdDevice { get; init; }

    public byte DeviceClass { get; init; }
    public byte DeviceSubClass { get; init; }
    public byte DeviceProtocol { get; init; }

    public byte ConfigurationValue { get; init; }
    public byte NumConfigurations { get; init; }

    /// <summary>Manufacturer string from the device descriptor (may be empty).</summary>
    public string Manufacturer { get; init; } = string.Empty;

    /// <summary>Product string from the device descriptor (may be empty).</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>Serial number from the device descriptor (may be empty).</summary>
    public string Serial { get; init; } = string.Empty;

    /// <summary>Interfaces of the active configuration.</summary>
    public IReadOnlyList<UsbIpInterface> Interfaces { get; init; } = Array.Empty<UsbIpInterface>();

    /// <summary>Endpoints of the active configuration.</summary>
    public IReadOnlyList<EndpointDescriptor> Endpoints { get; init; } = Array.Empty<EndpointDescriptor>();

    /// <summary>Whether this device can be exported right now.</summary>
    public CaptureState CaptureState { get; init; } = CaptureState.Capturable;

    /// <summary>Machine-readable reason behind <see cref="CaptureState"/>.</summary>
    public CaptureReason CaptureReason { get; init; } = CaptureReason.None;

    /// <summary>Operator-facing explanation, always populated for a refusal.</summary>
    public string CaptureDetail { get; init; } = string.Empty;

    /// <summary>Device file path the capture handle was opened on (WinUSB interface path).</summary>
    public string DevicePath { get; init; } = string.Empty;

    /// <summary>The path advertised in the USB/IP device list.</summary>
    public string SysPath => string.IsNullOrEmpty(DevicePath) ? InstancePath : DevicePath;

    /// <summary>Nickname assigned by policy, when one exists.</summary>
    public string? Nickname { get; init; }

    /// <summary>True when a per-device password protects this export.</summary>
    public bool RequiresPassword { get; init; }

    /// <summary>Stable identity used by policy.</summary>
    public DeviceIdentity Identity => new(VendorId, ProductId, Serial);

    /// <summary>Display name: nickname when set, otherwise the product string.</summary>
    public string DisplayName => Nickname ?? (string.IsNullOrWhiteSpace(Product) ? Identity.VpKey : Product);

    /// <summary>
    /// Builds the USB/IP device record for a device-list or import reply. Field widths and
    /// semantics match <c>usbip_usb_device</c> exactly so stock clients accept it.
    /// </summary>
    public UsbIpDevice ToUsbIpDevice() => new()
    {
        Path = SysPath,
        BusId = BusId,
        BusNum = BusNum,
        DevNum = DevNum,
        Speed = Speed,
        VendorId = VendorId,
        ProductId = ProductId,
        BcdDevice = BcdDevice,
        DeviceClass = DeviceClass,
        DeviceSubClass = DeviceSubClass,
        DeviceProtocol = DeviceProtocol,
        ConfigurationValue = ConfigurationValue,
        NumConfigurations = NumConfigurations,
        NumInterfaces = (byte)Math.Min(Interfaces.Count, byte.MaxValue),
        Manufacturer = Manufacturer,
        Product = Product,
        Serial = Serial,
        Interfaces = Interfaces,
    };

    /// <summary>The recon's enumeration line, kept verbatim (`Found %s speed device ...`).</summary>
    public string DescribeFound() => ToUsbIpDevice().DescribeFound();

    /// <summary>A one-line operator summary including capture state.</summary>
    public string Describe() =>
        $"{BusId} [{VendorId:x4}:{ProductId:x4}] \"{DisplayName}\" " +
        $"speed={UsbIp.SpeedName(Speed)} class={UsbClass.Name(DeviceClass)} " +
        $"state={CaptureState}" +
        (CaptureReason == CaptureReason.None ? string.Empty : $" reason={CaptureReason.Describe()}")
        + (string.IsNullOrEmpty(CaptureDetail) ? string.Empty : $" detail=\"{CaptureDetail}\"");

    public override string ToString() => Describe();
}
