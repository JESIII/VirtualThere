using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vht.Interop.Rebind;

/// <summary>
/// The two <c>setupapi</c>/<c>newdev</c> entry points needed to move a device onto the inbox
/// <c>winusb.sys</c> driver: stage a driver package, then re-select the driver for an existing
/// devnode.
/// </summary>
/// <remarks>
/// <para>
/// This is the P2 capture path in ARCHITECTURE.md §4.1, and it is the only place in
/// VirtualThere that a device's driver binding is changed. It is used solely when
/// <c>capture.rebind.enabled = true</c> and the operator has elevated the process; P1
/// (a device already on WinUSB) and P3 (a device whose interface cannot be opened) never reach
/// it. Nothing here is a kernel driver of ours: the target is the inbox driver Windows already
/// ships (risk 4 §4.1).
/// </para>
/// <para>
/// Both calls require administrator rights; they fail with <c>ERROR_ACCESS_DENIED</c> (5)
/// otherwise, which the caller reports rather than retries.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class DeviceRebindApi
{
    /// <summary><c>SPOST_NONE</c> — copy the INF into the driver store and install it.</summary>
    public const uint SPOST_NONE = 0;

    /// <summary><c>SPOST_PATH</c> — the source is a full path to an INF.</summary>
    public const uint SPOST_PATH = 1;

    /// <summary><c>INSTALLFLAG_FORCE</c> — re-select the driver even when the devnode is healthy.</summary>
    public const uint INSTALLFLAG_FORCE = 0x00000001;

    /// <summary><c>INSTALLFLAG_NONINTERACTIVE</c> — never show UI (a service has no desktop).</summary>
    public const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;

    /// <summary>Maximum length, in characters, of an INF path we will hand to SetupAPI.</summary>
    public const int MaxInfPath = 260;

    /// <summary>
    /// Stages an INF in the driver store. <paramref name="destinationName"/> receives the
    /// <c>oemNN.inf</c> name SetupAPI assigned, which is what a rollback needs.
    /// </summary>
    [LibraryImport("setupapi.dll", EntryPoint = "SetupCopyOEMInfW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupCopyOEMInf(
        string sourceInfFileName,
        string? oemSourceMediaLocation,
        uint oemSourceMediaType,
        uint copyStyle,
        [Out] char[]? destinationInfFileName,
        int destinationInfFileNameSize,
        out int requiredSize,
        out nint destinationInfFileNameComponent);

    /// <summary>
    /// Points the devnode whose hardware id matches <paramref name="hardwareId"/> at the staged
    /// INF. Requires elevation; <paramref name="rebootRequired"/> is set when Windows wants a
    /// restart before the binding is effective.
    /// </summary>
    [LibraryImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateDriverForPlugAndPlayDevices(
        nint hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        out int rebootRequired);

    /// <summary>
    /// The inf models hardware id for a USB device: <c>USB\VID_xxxx&amp;PID_yyyy</c>. This is
    /// the id the P2 INF matches on, and it is deliberately vid/pid-wide rather than
    /// instance-specific so the package also covers the device's other interfaces.
    /// </summary>
    public static string HardwareId(ushort vendorId, ushort productId) =>
        $"USB\\VID_{vendorId:x4}&PID_{productId:x4}";
}
