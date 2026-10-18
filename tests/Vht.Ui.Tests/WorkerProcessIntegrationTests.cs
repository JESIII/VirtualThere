using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vht.Ui.ControlPlane;
using Vht.Ui.Hosting;
using Vht.Ui.Services;

namespace Vht.Ui.Tests;

/// <summary>
/// The worker lifecycle measured on the real thing: the real <c>vht-server.exe</c>, a real control
/// pipe, real process ids, and a window process that is killed rather than closed politely.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is an assertion about the operating system, not about a mock: a process really
/// is started, really does answer on <c>\\.\pipe\&lt;name&gt;</c>, really does survive its window,
/// and really is really gone afterwards. The fakes in <c>WorkerSupervisorTests</c> cover the
/// decisions; this file covers the effect.
/// </para>
/// <para>
/// <b>What it cannot assert.</b> A test cannot answer a UAC consent prompt, so the window is
/// launched with <c>--no-elevate</c> (the documented automation switch): the same executable, the
/// same <c>--worker</c> switch, the same supervisor, the same launch path. What differs is only who
/// authorises the process. The consent prompt itself is asserted where it is decided - as
/// <c>Verb=runas</c> with <see cref="ProcessWindowStyle.Hidden"/> in
/// <see cref="WorkerProcessLauncher.CreateStartInfo"/> - and the "no console window" claim is
/// measured twice: the executables are Windows-subsystem binaries (PE subsystem 2, so Windows gives
/// them no console at all), and neither the window nor its worker owns a
/// <c>ConsoleWindowClass</c> window on the desktop while they run.
/// </para>
/// <para>
/// <b>The chosen close semantics: detach, deterministically.</b> Closing (X) hides the window to
/// the notification area and leaves the worker serving; Exit stops the worker this window started;
/// and if the window process dies in any other way - crash, End task, a kill - the worker keeps
/// serving and the next window <b>attaches</b> to it instead of spawning a second one. That is the
/// behaviour docs/UI.md section 1 freezes, and
/// <see cref="A_window_process_that_dies_leaves_its_worker_serving_and_the_next_window_attaches"/>
/// is the test that fails if it regresses: it kills the window, then requires the worker to be
/// alive, still serving the pipe, joined rather than duplicated by the next supervisor, and
/// removable by the operator's own shutdown verb.
/// </para>
/// </remarks>
public sealed class WorkerProcessIntegrationTests
{
    private const string ServerImage = "vht-server";

    /// <summary>
    /// The elevated launch is one process with one verb: that is what makes it exactly one consent
    /// prompt, and it is asserted here rather than trusted.
    /// </summary>
    [Fact]
    public void An_elevated_launch_asks_windows_for_one_consent_prompt_and_a_hidden_process()
    {
        var spec = new WorkerLaunchSpec
        {
            ExecutablePath = @"C:\portable\vht-server.exe",
            Arguments = [UiHost.WorkerSwitch, "--pipe", "vht-server"],
            WorkingDirectory = @"C:\portable",
            Elevate = true,
        };

        ProcessStartInfo info = WorkerProcessLauncher.CreateStartInfo(spec);

        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute, "ShellExecuteEx is what raises the consent prompt");
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.True(info.CreateNoWindow, "no console window for the worker, elevated or not");
        Assert.False(info.RedirectStandardOutput, "ShellExecuteEx hands over no handles to redirect");
        Assert.False(info.RedirectStandardError, "ShellExecuteEx hands over no handles to redirect");

        // One launch is one prompt: the worker switch is passed once, and nothing in the command
        // line could start a second elevated process.
        Assert.Equal(
            1,
            spec.Arguments.Count(a => string.Equals(a, UiHost.WorkerSwitch, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The automation path is the same launch minus the prompt, and it keeps every other guarantee:
    /// no console, and the handles the window needs to read a worker that fails to come up.
    /// </summary>
    [Fact]
    public void An_unelevated_launch_redirects_instead_of_elevating_and_still_has_no_console()
    {
        var spec = new WorkerLaunchSpec
        {
            ExecutablePath = @"C:\portable\vht-server.exe",
            Arguments = [UiHost.WorkerSwitch],
            WorkingDirectory = @"C:\portable",
            Elevate = false,
        };

        ProcessStartInfo info = WorkerProcessLauncher.CreateStartInfo(spec);

        Assert.Equal(string.Empty, info.Verb);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    /// <summary>
    /// "No console window ever appears" starts with the binary itself: a Windows-subsystem
    /// executable has no console to show. Read out of the real release PE, not out of the project
    /// property that is supposed to produce it.
    /// </summary>
    [Fact]
    public void Both_role_executables_are_windows_subsystem_binaries()
    {
        foreach (string executable in new[] { RoleExecutables.Server, RoleExecutables.Client })
        {
            Assert.True(File.Exists(executable), executable + " was not built; this test needs the real executables");

            // 2 = IMAGE_SUBSYSTEM_WINDOWS_GUI, 3 = IMAGE_SUBSYSTEM_WINDOWS_CUI.
            Assert.Equal(2, PeSubsystem(executable));
        }
    }

    /// <summary>The automation switch this file needs, and the rest of the command line still parses.</summary>
    [Fact]
    public void The_window_can_be_asked_to_press_its_own_start()
    {
        Assert.False(AppOptions.Parse([]).StartWorker);
        Assert.True(AppOptions.Parse(["--start-worker"]).StartWorker);

        AppOptions options = AppOptions.Parse(
            ["--pipe", "vht-e2e", "--port", "33340", "--no-elevate", "--start-worker", "--minimized"]);
        Assert.Equal("vht-e2e", options.PipeName);
        Assert.Equal(33340, options.Port);
        Assert.True(options.NoElevate);
        Assert.True(options.StartWorker);
        Assert.True(options.StartMinimized);
    }

    /// <summary>
    /// The card's central assertion, measured end to end: a window process that starts its worker
    /// and then dies (killed, so there is no shutdown exchange at all - the harshest case) leaves
    /// its worker serving the pipe; the next window attaches to that worker instead of spawning a
    /// second one (no second process, no second consent prompt); and the operator's own shutdown
    /// verb - <c>server.stop</c>, the one <c>vht-server stop-server</c> sends - takes it away, so a
    /// detached worker is never an unbounded leak.
    /// </summary>
    [Fact]
    public async Task A_window_process_that_dies_leaves_its_worker_serving_and_the_next_window_attaches()
    {
        string serverExecutable = RoleExecutables.Server;
        Assert.True(File.Exists(serverExecutable), serverExecutable + " was not built; this test needs the real executable");

        // The window claims a per-role single-instance mutex, so a window the operator already has
        // open would make this one exit on startup. Detect the guard itself rather than "any
        // vht-server process exists": a worker left serving from an earlier run is harmless here,
        // and the pid diff below already tolerates it. A window that is on its way out (a previous
        // test run, another checkout's harness) gets a moment to release it first.
        Assert.True(
            await WaitForServerWindowGuardAsync(TimeSpan.FromSeconds(30)),
            "a " + Branding.ServerTitle + " window is running and holds this role's " +
            "single-instance guard; this measurement needs it closed, then re-run");

        string pipe = "vht-e2e-" + Guid.NewGuid().ToString("N");
        int port = FreePort();
        int[] baseline = ProcessIds(ServerImage);
        Process? window = null;

        try
        {
            var start = new ProcessStartInfo(
                serverExecutable,
                "--pipe " + pipe +
                " --port " + port.ToString(CultureInfo.InvariantCulture) +
                " --no-elevate --start-worker --minimized")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(serverExecutable) ?? AppContext.BaseDirectory,
            };

            window = Process.Start(start)
                ?? throw new InvalidOperationException("the window process could not be started");

            // Drain both streams: an undrained pipe would block the window on its own output.
            window.OutputDataReceived += (_, _) => { };
            window.ErrorDataReceived += (_, _) => { };
            window.BeginOutputReadLine();
            window.BeginErrorReadLine();

            var client = new VhtServiceClient(new ControlPlaneClient(pipe, TimeSpan.FromSeconds(2)));
            await WaitForServiceAsync(client, window, TimeSpan.FromSeconds(120));

            // Exactly one worker, and it is a different process from the window that started it.
            int[] withWorker = await WaitForWorkerAsync(window.Id, TimeSpan.FromSeconds(15));
            int worker = withWorker.Single(id => id != window.Id);
            Assert.True(IsRunning(worker), "the worker the window started must be alive while the window is");

            // No console window for either of them, measured on the desktop's own window list.
            Assert.False(HasConsoleWindow(window.Id), "the window process owns a console window");
            Assert.False(HasConsoleWindow(worker), "the worker owns a console window");

            // The window dies with no Exit, no shutdown exchange, no chance to clean up.
            window.Kill(entireProcessTree: false);
            Assert.True(window.WaitForExit(15000), "the window process did not die");
            await Task.Delay(1500);

            // ---- detach: the worker outlived its window, and it is still the control plane ----
            Assert.True(
                IsRunning(worker),
                "the worker died with its window: killing the window must leave the worker serving " +
                "(docs/UI.md section 1)");
            StatusResult stillServing = await WaitForServiceAsync(client, window: null, TimeSpan.FromSeconds(20));
            Assert.Equal(ControlPlaneStatus.Ok, stillServing.Status);
            Assert.True(
                stillServing.Snapshot is null || stillServing.Snapshot.Running || stillServing.Snapshot.ServerName is not null,
                "the pipe answered, but not with a server status");

            // ---- attach: the next window joins the worker instead of spawning a second one ----
            WorkerLaunchSpec spec = WorkerLaunchSpec.ForRole(
                UiRole.Server,
                ServerOptionsFor(pipe, port));

            using var supervisor = new WorkerSupervisor(
                new WorkerProcessLauncher(),
                spec,
                probe: async ct => (await client.GetServerStatusAsync(ct).ConfigureAwait(false)).Status
                    == ControlPlaneStatus.Ok,
                gracefulStop: ct => client.StopServerAsync(ct),
                readyTimeout: TimeSpan.FromSeconds(10));

            WorkerStartResult attached = await supervisor.EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(WorkerStartResult.AlreadyRunning, attached);
            Assert.False(supervisor.OwnsWorker, "attaching must own nothing");
            Assert.Null(supervisor.WorkerProcessId);
            Assert.Equal(
                new[] { worker },
                ProcessIds(ServerImage).Where(id => id != window.Id).ToArray());

            // ---- the detached worker is removable, and by the documented route ----
            ControlPlaneResponse stopped = await client.StopServerAsync(CancellationToken.None);
            Assert.True(stopped.IsOk, "server.stop was refused: " + (stopped.Error?.ToString() ?? "no error detail"));

            Assert.True(
                await WaitUntilGoneAsync(worker, TimeSpan.FromSeconds(20)),
                "the detached worker ignored server.stop and is still running as pid " +
                worker.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            TryKill(window);
            foreach (int pid in ProcessIds(ServerImage).Where(pid => !baseline.Contains(pid)))
            {
                TryKill(pid);
            }
        }
    }

    // ------------------------------------------------------------------ server options for the attach

    /// <summary>
    /// The options the second window would be started with: the same pipe and port, unelevated.
    /// Built through the public parser so this stays the real command line, not a second one.
    /// </summary>
    private static AppOptions ServerOptionsFor(string pipe, int port)
    {
        AppOptions options = AppOptions.Parse(
            ["--pipe", pipe, "--port", port.ToString(CultureInfo.InvariantCulture), "--no-elevate"]);
        options.Role = UiRole.Server;
        options.ResolveDefaultPipe();
        return options;
    }

    // ------------------------------------------------------------------ single-instance guard

    /// <summary>
    /// Waits until no window of this role holds the single-instance guard, and says whether it
    /// became free. The kernel keeps the name alive exactly as long as some process holds a handle
    /// to it, so a guard from a window that has just died stops counting on its own.
    /// </summary>
    private static async Task<bool> WaitForServerWindowGuardAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (!ServerWindowGuardIsHeld())
            {
                return true;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        return !ServerWindowGuardIsHeld();
    }

    /// <summary>
    /// True when another window of this role is running: the per-role single-instance mutex
    /// <c>UiSession.TryClaimSingleInstance</c> creates is still open by name.
    /// </summary>
    private static bool ServerWindowGuardIsHeld()
    {
        try
        {
            using Mutex guard = Mutex.OpenExisting(@"Local\VirtualThere.server");
            return true;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ waiting

    private static async Task<StatusResult> WaitForServiceAsync(
        VhtServiceClient client,
        Process? window,
        TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        StatusResult last = new(ControlPlaneStatus.Unavailable, null, null);

        while (clock.Elapsed < timeout)
        {
            if (window is { HasExited: true })
            {
                throw new InvalidOperationException(
                    "the window process exited (code " + window.ExitCode.ToString(CultureInfo.InvariantCulture) +
                    ") before its worker served \\\\.\\pipe\\" + client.PipeName +
                    "; check the window's own log for why");
            }

            last = await client.GetServerStatusAsync(CancellationToken.None).ConfigureAwait(false);
            if (last.Status == ControlPlaneStatus.Ok)
            {
                return last;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "no control plane answered on \\\\.\\pipe\\" + client.PipeName + " within " +
            timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s (last: " +
            last.Status + (last.Error is null ? string.Empty : ", " + last.Error) + ")");
    }

    /// <summary>Waits until exactly one process of this image is not the window, and returns them all.</summary>
    private static async Task<int[]> WaitForWorkerAsync(int windowPid, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        int[] pids = ProcessIds(ServerImage);

        while (clock.Elapsed < timeout)
        {
            pids = ProcessIds(ServerImage);
            if (pids.Count(id => id != windowPid) == 1)
            {
                return pids;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "expected exactly one worker process of '" + ServerImage + "' beside the window (pid " +
            windowPid.ToString(CultureInfo.InvariantCulture) + "); found [" +
            string.Join(", ", pids) + "]");
    }

    private static async Task<bool> WaitUntilGoneAsync(int pid, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (!IsRunning(pid))
            {
                return true;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return !IsRunning(pid);
    }

    // ------------------------------------------------------------------ processes

    private static int[] ProcessIds(string imageName)
    {
        var ids = new List<int>();
        foreach (Process process in Process.GetProcessesByName(imageName))
        {
            try
            {
                ids.Add(process.Id);
            }
            catch (InvalidOperationException)
            {
                // Exited between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }

        return ids.ToArray();
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryKill(int pid)
    {
        if (!IsRunning(pid))
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to end.
        }
    }

    /// <summary>
    /// True when the process owns a top-level window of class <c>ConsoleWindowClass</c>: a console
    /// window really exists, which is the measurement the claim needs.
    /// </summary>
    private static bool HasConsoleWindow(int pid)
    {
        bool found = false;
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid == (uint)pid &&
                    string.Equals(NativeMethods.ClassNameOf(hwnd), "ConsoleWindowClass", StringComparison.Ordinal))
                {
                    found = true;
                    return false;
                }

                return true;
            },
            nint.Zero);

        return found;
    }

    // ------------------------------------------------------------------ PE header

    /// <summary>The PE optional header's subsystem of a built executable, read out of the file.</summary>
    private static int PeSubsystem(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        stream.Position = 0x3C;
        int peOffset = reader.ReadInt32();
        stream.Position = peOffset;

        if (reader.ReadInt32() != 0x00004550)
        {
            throw new InvalidOperationException(path + " is not a PE file");
        }

        // 4 signature + 20 COFF header bytes, then the optional header; the subsystem is at
        // optional-header offset 68 for both PE32 and PE32+.
        stream.Position = peOffset + 24 + 68;
        return reader.ReadUInt16();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassNameW(nint hWnd, StringBuilder className, int maxCount);

        internal static string ClassNameOf(nint hWnd)
        {
            var buffer = new StringBuilder(256);
            GetClassNameW(hWnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
    }
}

/// <summary>
/// Where the real role executables are: beside this test's own build output, in the configuration
/// and target framework the test was built for. The test project references both executables so
/// they exist by the time it runs.
/// </summary>
internal static class RoleExecutables
{
    public static string Server => Path.Combine(BuildDirectory("Vht.Server"), "vht-server.exe");

    public static string Client => Path.Combine(BuildDirectory("Vht.Client"), "vht-client.exe");

    private static string BuildDirectory(string project)
    {
        // .../tests/Vht.Ui.Tests/bin/<Configuration>/<TargetFramework>/
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string framework = baseDirectory.Name;
        string configuration = baseDirectory.Parent?.Name ?? "Debug";

        DirectoryInfo? root = baseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VirtualThere.slnx")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            throw new InvalidOperationException(
                "the repository root (VirtualThere.slnx) was not found above " + AppContext.BaseDirectory);
        }

        return Path.Combine(root.FullName, "src", project, "bin", configuration, framework);
    }
}
