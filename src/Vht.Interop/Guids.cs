using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vht.Interop;

/// <summary>Well-known device interface class GUIDs and SetupAPI class GUIDs.</summary>
[SupportedOSPlatform("windows")]
public static class Guids
{
    /// <summary>
    /// <c>GUID_DEVINTERFACE_USB_DEVICE</c> — the interface every USB device exposes. This is
    /// the class the server enumerates, matching the reference product's use of
    /// <c>SetupDiEnumDeviceInterfaces</c> (RECON_AND_SPEC.md §2).
    /// </summary>
    public static readonly Guid DeviceInterfaceUsbDevice =
        new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    /// <summary><c>GUID_DEVINTERFACE_USB_HUB</c> — hubs, used for topology and to filter them out.</summary>
    public static readonly Guid DeviceInterfaceUsbHub =
        new("F18A0E88-C30C-11D0-8815-00A0C906BED8");

    /// <summary><c>GUID_DEVINTERFACE_USB_HOST_CONTROLLER</c>.</summary>
    public static readonly Guid DeviceInterfaceUsbHostController =
        new("3ABF6F2D-71C4-462A-8A92-1E6861E6AF27");

    /// <summary><c>GUID_DEVCLASS_HIDCLASS</c> — used to identify boot HID devices we must not take.</summary>
    public static readonly Guid DeviceClassHid = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");

    /// <summary><c>GUID_DEVCLASS_KEYBOARD</c>.</summary>
    public static readonly Guid DeviceClassKeyboard = new("4D36E96B-E325-11CE-BFC1-08002BE10318");

    /// <summary><c>GUID_DEVCLASS_MOUSE</c>.</summary>
    public static readonly Guid DeviceClassMouse = new("4D36E96F-E325-11CE-BFC1-08002BE10318");

    /// <summary><c>GUID_DEVCLASS_NET</c> — used when deciding whether a device is the management NIC.</summary>
    public static readonly Guid DeviceClassNet = new("4D36E972-E325-11CE-BFC1-08002BE10318");

    /// <summary><c>GUID_DEVCLASS_DISKDRIVE</c> — used when deciding whether a device hosts the running OS.</summary>
    public static readonly Guid DeviceClassDiskDrive = new("4D36E967-E325-11CE-BFC1-08002BE10318");

    /// <summary><c>GUID_DEVCLASS_USB</c>.</summary>
    public static readonly Guid DeviceClassUsb = new("36FC9E60-C465-11CF-8056-444553540000");
}
