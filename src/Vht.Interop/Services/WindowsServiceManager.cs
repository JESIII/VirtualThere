using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vht.Interop.Services;

/// <summary>Outcome of an SCM operation.</summary>
public readonly struct ServiceOperationResult
{
    public bool Ok { get; init; }
    public string Detail { get; init; }
    public int Win32Error { get; init; }
    public uint State { get; init; }

    public static ServiceOperationResult Success(string detail, uint state = 0) =>
        new() { Ok = true, Detail = detail, State = state };

    public static ServiceOperationResult Failure(string detail, int win32Error) =>
        new() { Ok = false, Detail = detail, Win32Error = win32Error };

    public override string ToString() => Ok ? Detail : $"{Detail} (win32 {Win32Error}: {new Win32Exception(Win32Error).Message})";
}

/// <summary>
/// Installs, removes and controls the VirtualThere Windows service.
/// </summary>
/// <remarks>
/// Every method needs an elevated token; on this machine that is a hard requirement, and each
/// method reports <c>access denied</c> with the exact command to re-run rather than failing
/// silently (the reference product instead warns and restarts itself elevated —
/// RECON_AND_SPEC.md §4 and the packaging card's deliverable 4).
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsServiceManager
{
    /// <summary>Creates the service if it does not exist. Idempotent.</summary>
    public static ServiceOperationResult Install(
        string binaryPath,
        string serviceName = ServiceControl.DefaultServiceName,
        string displayName = ServiceControl.DefaultDisplayName,
        string description = "Shares this machine's USB devices over USB/IP. Free and unlimited.",
        bool autoStart = true)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
            throw new ArgumentException("binary path is required", nameof(binaryPath));

        nint manager = ServiceControl.OpenSCManager(null, null,
            ServiceControl.SC_MANAGER_CONNECT | ServiceControl.SC_MANAGER_CREATE_SERVICE);
        if (manager == 0)
            return Failure("OpenSCManager", requireElevation: true);

        nint service = 0;
        try
        {
            service = ServiceControl.OpenService(manager, serviceName, ServiceControl.SERVICE_ALL_ACCESS);
            if (service != 0)
                return ServiceOperationResult.Success($"service '{serviceName}' already installed");

            service = ServiceControl.CreateService(
                manager,
                serviceName,
                displayName,
                ServiceControl.SERVICE_ALL_ACCESS,
                ServiceControl.SERVICE_WIN32_OWN_PROCESS,
                autoStart ? ServiceControl.SERVICE_AUTO_START : ServiceControl.SERVICE_DEMAND_START,
                ServiceControl.SERVICE_ERROR_NORMAL,
                $"\"{binaryPath}\" serve",
                null,
                0,
                null,
                null,
                null);

            if (service == 0)
                return Failure($"CreateService('{serviceName}')", requireElevation: true);

            SetDescription(service, description);

            return ServiceOperationResult.Success(
                autoStart
                    ? $"service '{serviceName}' installed (automatic start, survives reboot)"
                    : $"service '{serviceName}' installed (manual start)");
        }
        finally
        {
            if (service != 0) ServiceControl.CloseServiceHandle(service);
            ServiceControl.CloseServiceHandle(manager);
        }
    }

    /// <summary>Stops and deletes the service. Idempotent.</summary>
    public static ServiceOperationResult Uninstall(string serviceName = ServiceControl.DefaultServiceName)
    {
        nint manager = ServiceControl.OpenSCManager(null, null, ServiceControl.SC_MANAGER_CONNECT);
        if (manager == 0)
            return Failure("OpenSCManager", requireElevation: true);

        nint service = 0;
        try
        {
            service = ServiceControl.OpenService(manager, serviceName, ServiceControl.SERVICE_ALL_ACCESS);
            if (service == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ServiceControl.ERROR_SERVICE_DOES_NOT_EXIST)
                    return ServiceOperationResult.Success($"service '{serviceName}' is not installed");
                return Failure($"OpenService('{serviceName}')", requireElevation: true);
            }

            var status = new ServiceControl.SERVICE_STATUS();
            if (ServiceControl.QueryServiceStatus(service, ref status) &&
                status.dwCurrentState != ServiceControl.SERVICE_STOPPED)
            {
                if (!ServiceControl.ControlService(service, ServiceControl.SERVICE_CONTROL_STOP, ref status))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ServiceControl.ERROR_SERVICE_NOT_ACTIVE)
                        return Failure($"ControlService(STOP '{serviceName}')", requireElevation: false);
                }

                WaitForState(service, ServiceControl.SERVICE_STOPPED, TimeSpan.FromSeconds(30));
            }

            if (!ServiceControl.DeleteService(service))
                return Failure($"DeleteService('{serviceName}')", requireElevation: true);

            return ServiceOperationResult.Success($"service '{serviceName}' removed");
        }
        finally
        {
            if (service != 0) ServiceControl.CloseServiceHandle(service);
            ServiceControl.CloseServiceHandle(manager);
        }
    }

    /// <summary>Starts the service and waits for it to report running.</summary>
    public static ServiceOperationResult Start(string serviceName = ServiceControl.DefaultServiceName)
    {
        nint manager = ServiceControl.OpenSCManager(null, null, ServiceControl.SC_MANAGER_CONNECT);
        if (manager == 0) return Failure("OpenSCManager", requireElevation: true);

        nint service = 0;
        try
        {
            service = ServiceControl.OpenService(manager, serviceName,
                ServiceControl.SERVICE_START | ServiceControl.SERVICE_QUERY_STATUS);
            if (service == 0)
                return Failure($"OpenService('{serviceName}')", requireElevation: true);

            var status = new ServiceControl.SERVICE_STATUS();
            if (ServiceControl.QueryServiceStatus(service, ref status) &&
                status.dwCurrentState == ServiceControl.SERVICE_RUNNING)
                return ServiceOperationResult.Success($"service '{serviceName}' is already running", status.dwCurrentState);

            if (!ServiceControl.StartService(service, 0, null))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ServiceControl.ERROR_SERVICE_ALREADY_RUNNING)
                    return Failure($"StartService('{serviceName}')", requireElevation: false);
            }

            uint state = WaitForState(service, ServiceControl.SERVICE_RUNNING, TimeSpan.FromSeconds(30));
            return state == ServiceControl.SERVICE_RUNNING
                ? ServiceOperationResult.Success($"service '{serviceName}' started", state)
                : ServiceOperationResult.Failure($"service '{serviceName}' did not reach running (state={ServiceControl.StateName(state)})", 0);
        }
        finally
        {
            if (service != 0) ServiceControl.CloseServiceHandle(service);
            ServiceControl.CloseServiceHandle(manager);
        }
    }

    /// <summary>Stops the service and waits for it to report stopped.</summary>
    public static ServiceOperationResult Stop(string serviceName = ServiceControl.DefaultServiceName)
    {
        nint manager = ServiceControl.OpenSCManager(null, null, ServiceControl.SC_MANAGER_CONNECT);
        if (manager == 0) return Failure("OpenSCManager", requireElevation: true);

        nint service = 0;
        try
        {
            service = ServiceControl.OpenService(manager, serviceName,
                ServiceControl.SERVICE_STOP | ServiceControl.SERVICE_QUERY_STATUS);
            if (service == 0)
                return Failure($"OpenService('{serviceName}')", requireElevation: true);

            var status = new ServiceControl.SERVICE_STATUS();
            if (ServiceControl.QueryServiceStatus(service, ref status) &&
                status.dwCurrentState == ServiceControl.SERVICE_STOPPED)
                return ServiceOperationResult.Success($"service '{serviceName}' is already stopped", status.dwCurrentState);

            if (!ServiceControl.ControlService(service, ServiceControl.SERVICE_CONTROL_STOP, ref status))
                return Failure($"ControlService(STOP '{serviceName}')", requireElevation: false);

            uint state = WaitForState(service, ServiceControl.SERVICE_STOPPED, TimeSpan.FromSeconds(30));
            return state == ServiceControl.SERVICE_STOPPED
                ? ServiceOperationResult.Success($"service '{serviceName}' stopped", state)
                : ServiceOperationResult.Failure($"service '{serviceName}' did not reach stopped (state={ServiceControl.StateName(state)})", 0);
        }
        finally
        {
            if (service != 0) ServiceControl.CloseServiceHandle(service);
            ServiceControl.CloseServiceHandle(manager);
        }
    }

    /// <summary>Reports the service's current state without changing it.</summary>
    public static ServiceOperationResult Query(string serviceName = ServiceControl.DefaultServiceName)
    {
        nint manager = ServiceControl.OpenSCManager(null, null, ServiceControl.SC_MANAGER_CONNECT);
        if (manager == 0) return Failure("OpenSCManager", requireElevation: true);

        nint service = 0;
        try
        {
            service = ServiceControl.OpenService(manager, serviceName, ServiceControl.SERVICE_QUERY_STATUS);
            if (service == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ServiceControl.ERROR_SERVICE_DOES_NOT_EXIST)
                    return ServiceOperationResult.Success($"service '{serviceName}' is not installed", 0);
                return Failure($"OpenService('{serviceName}')", requireElevation: true);
            }

            var statusProcess = new ServiceControl.SERVICE_STATUS_PROCESS();
            if (!ServiceControl.QueryServiceStatusEx(
                service, 0, ref statusProcess,
                (uint)Marshal.SizeOf<ServiceControl.SERVICE_STATUS_PROCESS>(), out _))
                return Failure($"QueryServiceStatusEx('{serviceName}')", requireElevation: false);

            return ServiceOperationResult.Success(
                $"service '{serviceName}' is {ServiceControl.StateName(statusProcess.dwCurrentState)} " +
                $"(pid {statusProcess.dwProcessId})",
                statusProcess.dwCurrentState);
        }
        finally
        {
            if (service != 0) ServiceControl.CloseServiceHandle(service);
            ServiceControl.CloseServiceHandle(manager);
        }
    }

    /// <summary>True when the current process is elevated, checked against the Administrators role.</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    private static uint WaitForState(nint service, uint desired, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var status = new ServiceControl.SERVICE_STATUS();
        uint state = 0;

        while (DateTime.UtcNow < deadline)
        {
            if (!ServiceControl.QueryServiceStatus(service, ref status))
                return state;
            state = status.dwCurrentState;
            if (state == desired) return state;
            Thread.Sleep(200);
        }

        return state;
    }

    private static ServiceOperationResult Failure(string what, bool requireElevation)
    {
        int err = Marshal.GetLastWin32Error();
        string detail = requireElevation && !IsElevated
            ? $"{what} failed: this operation needs an elevated shell (run it from an Administrator prompt)"
            : $"{what} failed";
        return ServiceOperationResult.Failure(detail, err);
    }

    private static void SetDescription(nint service, string description)
    {
        nint buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            var desc = new ServiceControl.SERVICE_DESCRIPTION { lpDescription = description };
            Marshal.StructureToPtr(desc, buffer, false);
            ServiceControl.ChangeServiceConfig2(service, 1 /* SERVICE_CONFIG_DESCRIPTION */, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
