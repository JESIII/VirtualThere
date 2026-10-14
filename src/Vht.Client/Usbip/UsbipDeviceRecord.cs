// SPDX-License-Identifier: Apache-2.0
//
// The device record carried by OP_REP_DEVLIST / OP_REP_IMPORT. Wire layout:
// include/usbip/proto_op.h `usbip_usb_device` (312 bytes, packed).

namespace Vht.Client.Usbip;

/// <summary>
/// One entry of an OP_REP_DEVLIST reply: a device a peer is exporting. Mirrors the reference
/// products' log vocabulary so the two can be compared field for field (ARCHITECTURE.md §4.2).
/// </summary>
internal sealed record UsbipDeviceRecord
{
    internal required string Path { get; init; }
    internal required string BusId { get; init; }
    internal uint BusNum { get; init; }
    internal uint DevNum { get; init; }
    internal UsbDeviceSpeed Speed { get; init; }
    internal ushort VendorId { get; init; }
    internal ushort ProductId { get; init; }
    internal ushort BcdDevice { get; init; }
    internal byte DeviceClass { get; init; }
    internal byte DeviceSubClass { get; init; }
    internal byte DeviceProtocol { get; init; }
    internal byte ConfigurationValue { get; init; }
    internal byte NumConfigurations { get; init; }
    internal byte NumInterfaces { get; init; }

    /// <summary>Interface descriptors that followed the device record in the reply.</summary>
    internal IReadOnlyList<UsbipInterface> Interfaces { get; init; } = Array.Empty<UsbipInterface>();

    /// <summary>"[vid:pid]" — the reference products' rendering of the pair.</summary>
    internal string VendorProduct => $"[{VendorId:x4}:{ProductId:x4}]";

    /// <summary>
    /// The human-readable USB class name for the device class, used by the device-class matrix.
    /// Unknown classes are reported by number, never guessed at.
    /// </summary>
    internal string ClassName => ClassNames.Describe(DeviceClass);

    public override string ToString() =>
        $"{BusId} {Speed} {VendorProduct} class 0x{DeviceClass:x2} ({ClassName})";
}

/// <summary>usbip_usb_interface — one interface descriptor in a device-list reply.</summary>
internal readonly record struct UsbipInterface(byte InterfaceClass, byte InterfaceSubClass, byte InterfaceProtocol)
{
    public override string ToString() => $"0x{InterfaceClass:x2}/0x{InterfaceSubClass:x2}/0x{InterfaceProtocol:x2}";
}

/// <summary>USB device class codes (USB 2.0 §9.6.3 bDeviceClass).</summary>
internal static class ClassNames
{
    internal static string Describe(byte deviceClass) => deviceClass switch
    {
        0x00 => "per-interface",
        0x01 => "audio",
        0x02 => "communications",
        0x03 => "HID",
        0x05 => "physical",
        0x06 => "still-image",
        0x07 => "printer",
        0x08 => "mass-storage",
        0x09 => "hub",
        0x0a => "CDC-data",
        0x0b => "smart-card",
        0x0d => "content-security",
        0x0e => "video",
        0x0f => "personal-healthcare",
        0x10 => "audio/video",
        0x11 => "billboard",
        0xdc => "diagnostic",
        0xe0 => "wireless-controller",
        0xef => "miscellaneous",
        0xfe => "application-specific",
        0xff => "vendor-specific",
        _ => $"class-0x{deviceClass:x2}",
    };
}

