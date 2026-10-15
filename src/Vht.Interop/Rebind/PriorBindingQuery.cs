using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Vht.Interop.Rebind;

/// <summary>
/// What one existing devnode is currently bound to: the driver package in effect and the path
/// of the INF that installs it.
/// </summary>
/// <remarks>
/// This is the record the P2 handback needs (ARCHITECTURE.md §4.1: "record the original
/// INF/driver before rebinding and implement handback"). It is read <em>before</em> anything
/// re-selects a driver, so it describes the machine as the operator had it, not as we left it.
/// </remarks>
public readonly record struct DeviceBinding
{
    /// <summary>Device instance id, e.g. <c>USB\VID_0781&amp;PID_5583\4C530001120328105553</c>.</summary>
    public string InstanceId { get; init; }

    /// <summary>The INF models hardware id the P2 INF also matches on: <c>USB\VID_xxxx&amp;PID_yyyy</c>.</summary>
    public string HardwareId { get; init; }

    /// <summary>Inf file name in effect, e.g. <c>disk.inf</c> or <c>oem42.inf</c>.</summary>
    public string InfName { get; init; }

    /// <summary>Full path of that INF, e.g. <c>C:\Windows\INF\disk.inf</c>.</summary>
    public string InfPath { get; init; }

    /// <summary>The bound function driver's service name, e.g. <c>disk</c> or <c>usbstor</c>.</summary>
    public string Service { get; init; }

    /// <summary>The driver package's provider, as Windows records it.</summary>
    public string Provider { get; init; }

    public bool IsUsable => !string.IsNullOrEmpty(InstanceId) && !string.IsNullOrEmpty(InfPath);

    public string Describe() =>
        $"{InstanceId} (service {Service}, inf {InfName}, provider {Provider})";
}

/// <summary>
/// Reads what every present devnode matching one vid/pid is bound to, without changing anything.
/// </summary>
/// <remarks>
/// <para>
/// This is the read-only half of the handback pair: it answers "what would the operator have to
/// be given back?" before the P2 rebind touches a binding. It enumerates devnodes
/// (<c>DIGCF_ALLCLASSES</c>), not device interfaces, because the driver binding lives on the
/// devnode - a composite device's interfaces are children of it.
/// </para>
/// <para>
/// The vid/pid is the unit on purpose: <c>UpdateDriverForPlugAndPlayDevices</c> re-selects a
/// driver by <em>hardware id</em>, so a rebind of one vid/pid moves every present instance of
/// it, and the handback has to cover exactly the same set. Every matching devnode is returned,
/// each carrying its own instance id so the record is per-device even though the operation is
/// per-vid/pid.
/// </para>
/// <para>
/// Nothing here writes: no driver store call, no re-selection. It is safe to call from the
/// enumeration path.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class PriorBindingQuery
{
    /// <summary><c>DEVPROP_TYPE_STRING</c>: the type all three properties below are read as.</summary>
    private const uint DEVPROP_TYPE_STRING = 0x00000012;

    /// <summary>The <c>DEVPKEY_Device_Driver*</c> property format id.</summary>
    private static readonly Guid DriverPropertyFormatId = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    /// <summary>The <c>DEVPKEY_Device_*</c> property format id.</summary>
    private static readonly Guid DevicePropertyFormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    /// <summary><c>DEVPKEY_Device_DriverInfPath</c> — the INF file name in effect.</summary>
    private const uint DEVPKEY_DEVICE_DRIVER_INF_PATH = 5;

    /// <summary><c>DEVPKEY_Device_DriverProvider</c>.</summary>
    private const uint DEVPKEY_DEVICE_DRIVER_PROVIDER = 9;

    /// <summary><c>DEVPKEY_Device_Service</c>.</summary>
    private const uint DEVPKEY_DEVICE_SERVICE = 6;

    /// <summary>Where Windows keeps driver packages: the INF directory of the installation.</summary>
    public static string InfDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    /// <summary>The full path of an INF name Windows reported, e.g. <c>disk.inf</c>.</summary>
    public static string InfPathFor(string infName) =>
        string.IsNullOrEmpty(infName) ? string.Empty : Path.Combine(InfDirectory, infName);

    /// <summary>
    /// Every present devnode whose hardware id list contains <c>USB\VID_xxxx&amp;PID_yyyy</c>.
    /// Never throws: a failure to enumerate yields an empty list, which the caller reports as
    /// "no prior binding recorded" rather than pretending it recorded one.
    /// </summary>
    public static IReadOnlyList<DeviceBinding> ReadBindings(ushort vendorId, ushort productId)
    {
        string hardwareId = DeviceRebindApi.HardwareId(vendorId, productId);
        var found = new List<DeviceBinding>();

        nint set = SetupApi.SetupDiGetClassDevs(
            in Guid.Empty, null, 0, SetupApi.DIGCF_PRESENT | SetupApi.DIGCF_ALLCLASSES);

        if (set == new nint(-1))
            return found;

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SetupApi.SP_DEVINFO_DATA
                {
                    cbSize = Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>(),
                };

                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                    break;

                if (!MatchesHardwareId(set, ref data, hardwareId))
                    continue;

                string instanceId = ReadInstanceId(set, ref data);
                if (string.IsNullOrEmpty(instanceId))
                    continue;

                string infName = ReadProperty(set, ref data, DriverPropertyFormatId, DEVPKEY_DEVICE_DRIVER_INF_PATH);
                found.Add(new DeviceBinding
                {
                    InstanceId = instanceId,
                    HardwareId = hardwareId,
                    InfName = infName,
                    InfPath = InfPathFor(infName),
                    Service = ReadProperty(set, ref data, DevicePropertyFormatId, DEVPKEY_DEVICE_SERVICE),
                    Provider = ReadProperty(set, ref data, DriverPropertyFormatId, DEVPKEY_DEVICE_DRIVER_PROVIDER),
                });
            }
        }
        catch (Exception)
        {
            // A devnode that vanishes between the enumeration and the property read is not an
            // error: whatever was read before it stands, and the caller records that.
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    /// <summary>True when the devnode's hardware id list contains the id the P2 INF matches.</summary>
    private static bool MatchesHardwareId(nint set, ref SetupApi.SP_DEVINFO_DATA data, string hardwareId)
    {
        string list = ReadMultiString(set, ref data, SetupApi.SPDRP_HARDWAREID);
        if (list.Length == 0)
            return false;

        foreach (string id in list.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // The list carries a revision-qualified form first (USB\VID_x&PID_y&REV_zzzz) and
            // the plain hardware id the INF matches on second; only the plain one selects the
            // driver, so only it counts as a match.
            if (string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string ReadInstanceId(nint set, ref SetupApi.SP_DEVINFO_DATA data)
    {
        var buffer = new char[SetupApi.MAX_DEVICE_ID_LEN + 1];
        return SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Length, out _)
            ? new string(buffer).TrimEnd('\0')
            : string.Empty;
    }

    /// <summary>Reads a <c>REG_MULTI_SZ</c> device registry property as a NUL-separated string.</summary>
    private static string ReadMultiString(nint set, ref SetupApi.SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[2048];
        if (!SetupApi.SetupDiGetDeviceRegistryProperty(
                set, ref data, property, out _, buffer, buffer.Length, out int required) || required <= 0)
            return string.Empty;

        return Encoding.Unicode.GetString(buffer, 0, Math.Min(required, buffer.Length)).TrimEnd('\0');
    }

    /// <summary>Reads one string device property; empty when the devnode does not carry it.</summary>
    private static string ReadProperty(
        nint set, ref SetupApi.SP_DEVINFO_DATA data, Guid formatId, uint propertyId)
    {
        var key = new DevicePropertyKey { FormatId = formatId, PropertyId = propertyId };
        var buffer = new byte[1024];

        if (!SetupDiGetDeviceProperty(
                set, ref data, in key, out uint type, buffer, buffer.Length, out int required, 0))
            return string.Empty;

        if (type != DEVPROP_TYPE_STRING || required <= 0)
            return string.Empty;

        return Encoding.Unicode.GetString(buffer, 0, Math.Min(required, buffer.Length)).TrimEnd('\0');
    }

    /// <summary><c>DEVPROPKEY</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DevicePropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // -----------------------------------------------------------------------------------
    // The devnode-property entry points this file adds to the SetupAPI surface. The
    // interface-enumeration entry points live in SetupApi.cs and are reused as-is.
    // -----------------------------------------------------------------------------------

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet, uint memberIndex, ref SetupApi.SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInstanceId(
        nint deviceInfoSet, ref SetupApi.SP_DEVINFO_DATA deviceInfoData,
        [Out] char[] deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceProperty(
        nint deviceInfoSet, ref SetupApi.SP_DEVINFO_DATA deviceInfoData,
        in DevicePropertyKey propertyKey, out uint propertyType,
        byte[] propertyBuffer, int propertyBufferSize, out int requiredSize, uint flags);
}
