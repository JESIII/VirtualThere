using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vht.Interop;

/// <summary>
/// CfgMgr32: device instance identity, hub topology and device notifications.
/// </summary>
/// <remarks>
/// <c>CM_Register_Notification</c> is used rather than <c>RegisterDeviceNotificationW</c>
/// (which the reference product uses) because it needs no window handle, so the service can
/// receive arrivals and removals while running without a GUI (ARCHITECTURE.md §4.2).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class CfgMgr32
{
    public const int CR_SUCCESS = 0;
    public const int CR_BUFFER_SMALL = 0x1A;
    public const int CR_NO_SUCH_VALUE = 0x1D;
    public const int MAX_DEVICE_ID_LEN = 200;

    /// <summary>First device instance in a device tree walk (used for parent/topology queries).</summary>
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size", SetLastError = true)]
    internal static partial int CM_Get_Device_ID_Size(out int pulLen, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CM_Get_Device_IDW(uint dnDevInst, [Out] char[] buffer, int bufferLen, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent", SetLastError = true)]
    internal static partial int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CM_Get_Device_ID_List_Size(out int pulLen, string? pszFilter, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CM_Get_Device_ID_List(string? pszFilter, [Out] char[] buffer, int bufferLen, uint ulFlags);

    /// <summary>Returns the device instance's class GUID, used for the refusal heuristics in §4.3.</summary>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CM_Get_DevNode_Registry_Property(
        uint dnDevInst, uint ulProperty, out uint pulRegDataType,
        byte[]? buffer, ref int pulLength, uint ulFlags);

    public const uint CM_DRP_CLASS = 0x00000008;
    public const uint CM_DRP_CLASSGUID = 0x00000009;
    public const uint CM_DRP_DRIVER = 0x0000000A;
    public const uint CM_DRP_DEVICEDESC = 0x00000001;
    public const uint CM_DRP_HARDWAREID = 0x00000002;
    public const uint CM_DRP_SERVICE = 0x00000005;

    /// <summary>Device property used to read the driver's service name (is it <c>winusb</c>?).</summary>
    public const uint CM_DRP_COMPATIBLEIDS = 0x00000003;

    // -----------------------------------------------------------------------------------
    // Device notifications
    // -----------------------------------------------------------------------------------

    public enum CM_NOTIFY_FILTER_TYPE
    {
        DeviceInterface = 0,
        DeviceHandle = 1,
        DeviceInstance = 2,
    }

    public enum CM_NOTIFY_ACTION
    {
        DeviceInterfaceArrival = 0,
        DeviceInterfaceRemoval = 1,
        DeviceInstanceArrival = 2,
        DeviceInstanceRemoval = 3,
        DeviceInstancePropertyChange = 4,
    }

    public const int CM_NOTIFY_FILTER_FLAG_ALL_INTERFACE_CLASSES = 0x00000001;
    public const int CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES = 0x00000002;

    /// <summary>Union member of <c>CM_NOTIFY_FILTER</c>; 16 bytes covers every variant we use.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct CM_NOTIFY_FILTER_UNION
    {
        [FieldOffset(0)] public Guid ClassGuid;
        [FieldOffset(0)] public nint hTargetInstance;
    }

    /// <summary><c>CM_NOTIFY_FILTER</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CM_NOTIFY_FILTER
    {
        public int cbSize;
        public int Flags;
        public CM_NOTIFY_FILTER_TYPE FilterType;
        public int Reserved;
        public CM_NOTIFY_FILTER_UNION Union;
    }

    /// <summary>Callback for <c>CM_Register_Notification</c>.</summary>
    internal delegate uint CM_NOTIFY_CALLBACK(
        nint hNotify, nint context, CM_NOTIFY_ACTION action, nint eventData, uint eventDataSize);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Register_Notification", SetLastError = true)]
    internal static partial int CM_Register_Notification(
        ref CM_NOTIFY_FILTER filter, nint context, CM_NOTIFY_CALLBACK callback, out nint notifyContext);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Unregister_Notification", SetLastError = true)]
    internal static partial int CM_Unregister_Notification(nint notifyContext);
}
