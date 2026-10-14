using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vht.Interop;

/// <summary>
/// SetupAPI: the device interface enumeration surface.
/// </summary>
/// <remarks>
/// This is the same API set the reference product uses for its capture step
/// (<c>SetupDiEnumDeviceInterfaces</c>, <c>SetupDiGetDeviceInterfaceDetailW</c> —
/// RECON_AND_SPEC.md §2). Nothing here writes to a device or a driver binding: enumeration is
/// read-only by construction, which is what lets the server report a device it cannot use
/// instead of breaking it (ARCHITECTURE.md §4).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class SetupApi
{
    public const int DIGCF_PRESENT = 0x00000002;
    public const int DIGCF_DEVICEINTERFACE = 0x00000010;
    public const int DIGCF_ALLCLASSES = 0x00000004;

    public const int MAX_DEVICE_ID_LEN = 200;
    public const int MAX_CLASS_NAME_LEN = 32;
    public const int MAX_GUID_STRING_LEN = 39;

    /// <summary><c>SP_DEVICE_INTERFACE_DATA</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    /// <summary><c>SP_DEVINFO_DATA</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    /// <summary><c>SP_DEVICE_INTERFACE_DETAIL_DATA_W</c>: a 4/8-byte header followed by the path.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public int cbSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
        public string DevicePath;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SetupDiGetClassDevs(
        in Guid classGuid, string? enumerator, nint hwndParent, int flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet, nint deviceInfoData, in Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        nint deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        nint deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceRegistryProperty(
        nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        out uint propertyRegDataType, byte[]? propertyBuffer, int propertyBufferSize, out int requiredSize);

    public const uint SPDRP_DEVICEDESC = 0x00000000;
    public const uint SPDRP_HARDWAREID = 0x00000001;
    public const uint SPDRP_MFG = 0x0000000B;
    public const uint SPDRP_FRIENDLYNAME = 0x0000000C;
    public const uint SPDRP_CLASS = 0x00000007;
    public const uint SPDRP_CLASSGUID = 0x00000008;
    public const uint SPDRP_DRIVER = 0x00000009;
}
