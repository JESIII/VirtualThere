using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vht.Interop;

/// <summary>
/// The Service Control Manager (SCM) surface: install, uninstall, start, stop and query the
/// VirtualThere service.
/// </summary>
/// <remarks>
/// This is a full SCM implementation, matching the reference product's <c>-b</c>
/// install/uninstall switch set (RECON_AND_SPEC.md §2 and its CLI block), with the difference
/// that VirtualThere's service has no licence state to consult at start-up.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class ServiceControl
{
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    public const uint SC_MANAGER_ALL_ACCESS = 0xF003F;

    public const uint SERVICE_QUERY_CONFIG = 0x0001;
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const uint SERVICE_START = 0x0010;
    public const uint SERVICE_STOP = 0x0020;
    public const uint SERVICE_ALL_ACCESS = 0xF01FF;

    public const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    public const uint SERVICE_AUTO_START = 0x00000002;
    public const uint SERVICE_DEMAND_START = 0x00000003;
    public const uint SERVICE_ERROR_NORMAL = 0x00000001;

    public const uint SERVICE_CONTROL_STOP = 0x00000001;
    public const uint SERVICE_CONTROL_INTERROGATE = 0x00000004;
    public const uint SERVICE_CONTROL_SHUTDOWN = 0x00000005;
    public const uint SERVICE_CONTROL_PRESHUTDOWN = 0x0000000F;

    public const uint SERVICE_STOPPED = 0x00000001;
    public const uint SERVICE_START_PENDING = 0x00000002;
    public const uint SERVICE_STOP_PENDING = 0x00000003;
    public const uint SERVICE_RUNNING = 0x00000004;
    public const uint SERVICE_CONTINUE_PENDING = 0x00000005;
    public const uint SERVICE_PAUSE_PENDING = 0x00000006;
    public const uint SERVICE_PAUSED = 0x00000007;

    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_SERVICE_EXISTS = 1073;
    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    public const int ERROR_ACCESS_DENIED = 5;

    /// <summary>Name used for the service's Event Log source and SCM entry.</summary>
    public const string DefaultServiceName = "vht-server";

    /// <summary>Display name shown in <c>services.msc</c>.</summary>
    public const string DefaultDisplayName = "VirtualThere USB Server";

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_DESCRIPTION
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpDescription;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenSCManager(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint serviceHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenService(nint manager, string serviceName, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateService(
        nint manager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [LibraryImport("advapi32.dll", EntryPoint = "DeleteService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteService(nint service);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfig2(nint service, uint infoLevel, nint info);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StartService(nint service, uint numServiceArgs, string?[]? serviceArgVectors);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ControlService(nint service, uint control, ref SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatus(nint service, ref SERVICE_STATUS status);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatusEx(
        nint service, int infoLevel, ref SERVICE_STATUS_PROCESS buffer, uint bufferSize, out uint bytesNeeded);

    /// <summary>Human-readable name of a service state.</summary>
    public static string StateName(uint state) => state switch
    {
        SERVICE_STOPPED => "stopped",
        SERVICE_START_PENDING => "start-pending",
        SERVICE_STOP_PENDING => "stop-pending",
        SERVICE_RUNNING => "running",
        SERVICE_CONTINUE_PENDING => "continue-pending",
        SERVICE_PAUSE_PENDING => "pause-pending",
        SERVICE_PAUSED => "paused",
        _ => $"state-0x{state:x}",
    };
}
