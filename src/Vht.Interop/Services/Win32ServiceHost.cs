using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vht.Interop.Services;

/// <summary>
/// The service-control surface the SCM needs from a process: the dispatcher, the control
/// handler and the status reports.
/// </summary>
/// <remarks>
/// <para>
/// This lives here rather than in <c>vht-server</c> so the server project needs no
/// <c>System.ServiceProcess.ServiceController</c> or
/// <c>Microsoft.Extensions.Hosting.WindowsServices</c> package: the whole SCM contract is four
/// <c>advapi32</c> entry points, and the server keeps its dependency surface to the BCL plus
/// this assembly (ARCHITECTURE.md §2.2 — the toolchain on the build machine must be enough to
/// build the product, offline).
/// </para>
/// <para>
/// The same binary therefore runs as a service and as a console application: when it is
/// launched by the SCM, <see cref="Run"/> reaches the service main and blocks there; when it is
/// launched by a human, <c>StartServiceCtrlDispatcherW</c> fails with
/// <c>ERROR_FAILED_SERVICE_CONTROLLER_CONNECT</c> (1063) and the caller falls back to console
/// mode.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Win32ServiceHost
{
    /// <summary>Returned by <c>StartServiceCtrlDispatcherW</c> when we were not started by the SCM.</summary>
    public const int ErrorFailedServiceControllerConnect = 1063;

    private const uint SERVICE_ACCEPT_STOP = 0x00000001;
    private const uint SERVICE_ACCEPT_SHUTDOWN = 0x00000004;
    private const uint SERVICE_ACCEPT_PRESHUTDOWN = 0x00000100;

    private const int SERVICE_CONFIG_PRESHUTDOWN_INFO = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_TABLE_ENTRY
    {
        public nint lpServiceName;
        public nint lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_PRESHUTDOWN_INFO
    {
        public uint dwPreshutdownTimeout;
    }

    private delegate void ServiceMainDelegate(int argc, nint argv);

    private delegate uint ServiceControlHandlerEx(uint control, uint eventType, nint eventData, nint context);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] SERVICE_TABLE_ENTRY[] serviceStartTable);

    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern nint RegisterServiceCtrlHandlerEx(
        string serviceName, ServiceControlHandlerEx handler, nint context);

    [DllImport("advapi32.dll", EntryPoint = "SetServiceStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint handle, ref ServiceControl.SERVICE_STATUS status);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(nint service, uint infoLevel, nint info);

    // Static state is required, not lazy: the SCM holds raw pointers to these delegates for
    // the lifetime of the process.
    private static ServiceMainDelegate? _serviceMain;
    private static ServiceControlHandlerEx? _handler;
    private static nint _statusHandle;
    private static CancellationTokenSource? _stop;
    private static Func<CancellationToken, Task>? _application;
    private static string _serviceName = ServiceControl.DefaultServiceName;
    private static uint _checkpoint;

    /// <summary>
    /// Runs <paramref name="application"/> under the SCM. Returns false — with
    /// <paramref name="error"/> set — when the process was not started by the SCM, which is the
    /// caller's signal to run in console mode instead.
    /// </summary>
    public static bool Run(string serviceName, Func<CancellationToken, Task> application, out int error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(application);

        _serviceName = serviceName;
        _application = application;
        _serviceMain = ServiceMain;
        _handler = ControlHandler;

        var table = new SERVICE_TABLE_ENTRY[2];
        table[0].lpServiceName = Marshal.StringToHGlobalUni(serviceName);
        table[0].lpServiceProc = Marshal.GetFunctionPointerForDelegate(_serviceMain);
        table[1].lpServiceName = 0;
        table[1].lpServiceProc = 0;

        try
        {
            bool ok = StartServiceCtrlDispatcher(table);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(table[0].lpServiceName);
        }
    }

    private static void ServiceMain(int argc, nint argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(_serviceName, _handler!, 0);
        if (_statusHandle == 0)
            return; // nothing to report to: the SCM already gave up on us

        // A generous preshutdown window so in-flight URBs can drain instead of being killed.
        SetPreshutdownTimeout(_statusHandle, 15_000);

        _stop = new CancellationTokenSource();

        Report(ServiceControl.SERVICE_START_PENDING, waitHint: 30_000, accepted: 0);
        Report(ServiceControl.SERVICE_RUNNING, waitHint: 0,
            accepted: SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_PRESHUTDOWN);

        try
        {
            _application!(_stop.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // The application logs its own failure; the SCM only needs the exit state below.
        }
        finally
        {
            Report(ServiceControl.SERVICE_STOPPED, waitHint: 0, accepted: 0);
        }
    }

    private static uint ControlHandler(uint control, uint eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case ServiceControl.SERVICE_CONTROL_STOP:
            case ServiceControl.SERVICE_CONTROL_SHUTDOWN:
                Report(ServiceControl.SERVICE_STOP_PENDING, waitHint: 5_000,
                    accepted: SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN);
                Cancel();
                break;

            case ServiceControl.SERVICE_CONTROL_PRESHUTDOWN:
                Cancel();
                break;

            case ServiceControl.SERVICE_CONTROL_INTERROGATE:
                Report(ServiceControl.SERVICE_RUNNING, waitHint: 0,
                    accepted: SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_PRESHUTDOWN);
                break;
        }

        return 0;
    }

    private static void Cancel()
    {
        try
        {
            _stop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void Report(uint state, uint waitHint, uint accepted)
    {
        if (_statusHandle == 0) return;

        var status = new ServiceControl.SERVICE_STATUS
        {
            dwServiceType = ServiceControl.SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = accepted,
            dwWin32ExitCode = 0,
            dwServiceSpecificExitCode = 0,
            dwCheckPoint = state is ServiceControl.SERVICE_RUNNING or ServiceControl.SERVICE_STOPPED
                ? 0
                : ++_checkpoint,
            dwWaitHint = waitHint,
        };

        SetServiceStatus(_statusHandle, ref status);
    }

    private static void SetPreshutdownTimeout(nint handle, uint milliseconds)
    {
        var info = new SERVICE_PRESHUTDOWN_INFO { dwPreshutdownTimeout = milliseconds };
        nint buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_PRESHUTDOWN_INFO>());
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            ChangeServiceConfig2(handle, SERVICE_CONFIG_PRESHUTDOWN_INFO, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
