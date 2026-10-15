using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vht.Ui.Hosting;
using Vht.Ui.Services;

namespace Vht.Ui.Tests;

/// <summary>
/// The lifecycle the card requires: a window starts its own privileged worker, attaches instead of
/// starting a second one when a control plane is already live, and never leaves a worker behind.
/// </summary>
public sealed class WorkerSupervisorTests
{
    [Fact]
    public async Task EnsureRunning_starts_the_worker_when_nothing_answers_on_the_pipe()
    {
        var process = new FakeWorkerProcess();
        var host = new FakeWorkerProcessHost(_ => process);
        var plane = new FakeControlPlane(() => host.LaunchCount > 0);
        using WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);

        WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(WorkerStartResult.Started, result);
        Assert.Equal(1, host.LaunchCount);
        Assert.True(supervisor.OwnsWorker);
        Assert.Equal(process.Id, supervisor.WorkerProcessId!.Value);

        // The worker is the same executable, told which pipe to bind - and nothing else: it is not
        // told to watch this window, because it outlives it (WorkerLifetime).
        WorkerLaunchSpec spec = Assert.IsType<WorkerLaunchSpec>(host.LastSpec);
        Assert.Contains(UiHost.WorkerSwitch, spec.Arguments);
        Assert.Contains("--pipe", spec.Arguments);
        Assert.DoesNotContain("--parent", spec.Arguments);
    }

    [Fact]
    public async Task EnsureRunning_attaches_and_starts_nothing_when_a_control_plane_is_live()
    {
        var host = new FakeWorkerProcessHost();
        var plane = new FakeControlPlane();
        using WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);

        WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(WorkerStartResult.AlreadyRunning, result);
        Assert.Equal(0, host.LaunchCount);
        Assert.False(supervisor.OwnsWorker);
        Assert.Null(supervisor.WorkerProcessId);
    }

    [Fact]
    public async Task EnsureRunning_reports_a_declined_consent_prompt_without_owning_anything()
    {
        var host = new FakeWorkerProcessHost
        {
            // The user clicked No on the prompt: ShellExecute raises 1223.
            LaunchThrows = new Win32Exception(1223, "The operation was canceled by the user."),
        };
        var plane = new FakeControlPlane(() => false);
        using WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);

        WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(WorkerStartResult.LaunchFailed, result);
        Assert.False(supervisor.OwnsWorker);
        Assert.Contains("canceled", supervisor.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureRunning_times_out_when_the_worker_never_binds_the_pipe()
    {
        var host = new FakeWorkerProcessHost();
        var plane = new FakeControlPlane(() => false);
        using WorkerSupervisor supervisor = SupervisorFixture.Supervisor(
            host, plane, readyTimeout: TimeSpan.FromMilliseconds(400));

        WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(WorkerStartResult.TimedOut, result);
        Assert.True(supervisor.OwnsWorker);
        Assert.NotNull(supervisor.LastError);
    }

    [Fact]
    public async Task EnsureRunning_is_idempotent_while_the_worker_it_started_is_alive()
    {
        var host = new FakeWorkerProcessHost();
        var plane = new FakeControlPlane(() => host.LaunchCount > 0);
        using WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);

        await supervisor.EnsureRunningAsync(CancellationToken.None);
        await supervisor.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(1, host.LaunchCount);
        Assert.True(supervisor.OwnsWorker);
    }

    [Fact]
    public async Task Stopping_stops_the_worker_this_window_started()
    {
        var worker = new FakeWorkerProcess();
        var host = new FakeWorkerProcessHost(_ => worker);
        var plane = new FakeControlPlane(() => host.LaunchCount > 0);
        using WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);
        await supervisor.EnsureRunningAsync(CancellationToken.None);

        bool stopped = await supervisor.StopOwnedWorkerAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.Equal(1, plane.GracefulStops);
        Assert.False(supervisor.OwnsWorker);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task Stopping_ends_a_worker_that_ignores_the_graceful_shutdown()
    {
        var worker = new FakeWorkerProcess { ExitOnTerminate = true };
        var host = new FakeWorkerProcessHost(_ => worker);
        var plane = new FakeControlPlane(() => host.LaunchCount > 0);
        using WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);
        await supervisor.EnsureRunningAsync(CancellationToken.None);

        // The graceful step is taken and the process stays up until it is ended.
        bool stopped = await supervisor.StopOwnedWorkerAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.True(worker.Terminated);
        Assert.Equal(1, plane.GracefulStops);
    }

    [Fact]
    public async Task Stopping_leaves_alone_a_worker_this_window_merely_attached_to()
    {
        var host = new FakeWorkerProcessHost();
        var plane = new FakeControlPlane();
        using WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);
        await supervisor.EnsureRunningAsync(CancellationToken.None);

        bool stopped = await supervisor.StopOwnedWorkerAsync(CancellationToken.None);

        // The installed service, or another instance's worker: not ours to stop.
        Assert.False(stopped);
        Assert.Equal(0, plane.GracefulStops);
        Assert.Equal(0, host.LaunchCount);
    }

    [Fact]
    public async Task Stopping_survives_an_elevated_worker_it_may_not_end()
    {
        var worker = new FakeWorkerProcess
        {
            ExitOnTerminate = false,
            TerminateThrows = new Win32Exception(5, "Access is denied."),
        };
        var host = new FakeWorkerProcessHost(_ => worker);
        var plane = new FakeControlPlane(() => host.LaunchCount > 0);
        using WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);
        await supervisor.EnsureRunningAsync(CancellationToken.None);

        bool stopped = await supervisor.StopOwnedWorkerAsync(CancellationToken.None);

        // The window cannot kill an elevated process and says so rather than pretending; the
        // worker's own parent watch is what actually reaps it, a second after the window exits.
        Assert.False(stopped);
        Assert.Contains("could not end the worker", supervisor.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task A_real_child_process_started_by_the_shipping_launcher_is_really_ended()
    {
        // The one test here with no fakes on the process side: the shipping launcher starts a real
        // child and the supervisor has to end it. This is the "no orphan worker" claim measured.
        var spec = new WorkerLaunchSpec
        {
            ExecutablePath = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
            Arguments = ["/c", "ping", "-n", "300", "127.0.0.1"],
            Elevate = false,
        };

        // The pipe answers as soon as the child has been started: false on the first probe (so the
        // supervisor starts the worker), true afterwards (so it sees the pipe come up).
        int probes = 0;
        var plane = new FakeControlPlane(() => ++probes > 1);
        using var supervisor = new WorkerSupervisor(
            new WorkerProcessLauncher(),
            spec,
            plane.ProbeAsync,
            gracefulStop: null,
            readyTimeout: TimeSpan.FromSeconds(10),
            gracefulTimeout: TimeSpan.FromMilliseconds(300),
            terminateTimeout: TimeSpan.FromSeconds(5));

        WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);
        Assert.Equal(WorkerStartResult.Started, result);

        int pid = Assert.IsType<int>(supervisor.WorkerProcessId);
        Assert.True(SupervisorFixture.IsRunning(pid), "the child should be alive while the window holds it");

        bool stopped = await supervisor.StopOwnedWorkerAsync(CancellationToken.None);

        Assert.True(stopped, supervisor.LastError);
        Assert.False(SupervisorFixture.IsRunning(pid), "the child must be gone after the supervisor stops it");
    }
}

/// <summary>The launch spec both role executables build for their worker.</summary>
public sealed class WorkerLaunchSpecTests
{
    [Fact]
    public void The_server_spec_names_the_worker_the_owner_the_pipe_and_the_port()
    {
        AppOptions options = AppOptions.Parse(["--pipe", "vht-test", "--port", "33240"]);
        options.Role = UiRole.Server;
        options.ResolveDefaultPipe();

        WorkerLaunchSpec spec = WorkerLaunchSpec.ForRole(UiRole.Server, options);

        Assert.Equal(Environment.ProcessPath, spec.ExecutablePath);
        Assert.True(spec.Elevate);
        Assert.Contains(UiHost.WorkerSwitch, spec.Arguments);
        Assert.Contains("--pipe", spec.Arguments);
        Assert.Contains("vht-test", spec.Arguments);
        Assert.Contains("--port", spec.Arguments);
        Assert.Contains("33240", spec.Arguments);
        Assert.Contains("--pipe vht-test", spec.ArgumentsText(), StringComparison.Ordinal);

        // Nothing about the window travels with the worker: it is not told to watch this process,
        // because it outlives it (WorkerLifetime).
        Assert.DoesNotContain("--parent", spec.Arguments);
        Assert.DoesNotContain(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            spec.Arguments);
    }

    [Fact]
    public void The_client_spec_carries_no_port_and_no_elevation_when_asked()
    {
        AppOptions options = AppOptions.Parse(["--pipe", "vht-client-test", "--port", "33241", "--no-elevate"]);
        options.Role = UiRole.Client;
        options.ResolveDefaultPipe();

        WorkerLaunchSpec spec = WorkerLaunchSpec.ForRole(UiRole.Client, options);

        Assert.False(spec.Elevate);
        Assert.DoesNotContain("--port", spec.Arguments);
        Assert.Contains("vht-client-test", spec.Arguments);
    }
}

/// <summary>The role executables' command line: the harness flags keep working, the mode does not.</summary>
public sealed class AppOptionsTests
{
    [Fact]
    public void The_harness_flags_are_still_understood()
    {
        AppOptions options = AppOptions.Parse(["--pipe", "vht-ui-driver-server", "--minimized"]);

        Assert.Equal("vht-ui-driver-server", options.PipeName);
        Assert.True(options.PipeNameExplicit);
        Assert.True(options.StartMinimized);
        Assert.False(options.ShowHelp);
    }

    [Fact]
    public void Help_is_still_understood()
    {
        Assert.True(AppOptions.Parse(["--help"]).ShowHelp);
        Assert.True(AppOptions.Parse(["-h"]).ShowHelp);
        Assert.True(AppOptions.Parse(["/?"]).ShowHelp);
    }

    [Fact]
    public void A_shortcut_written_for_the_old_tray_executable_cannot_change_the_role()
    {
        AppOptions options = AppOptions.Parse(["--mode", "client", "--minimized"]);

        // --mode is consumed and ignored; the role comes from which executable was started.
        Assert.Equal(UiRole.Server, options.Role);
        Assert.True(options.StartMinimized);
        Assert.Equal("vht-server", options.PipeName);
    }

    [Fact]
    public void The_pipe_default_follows_the_role()
    {
        AppOptions server = AppOptions.Parse([]);
        server.Role = UiRole.Server;
        server.ResolveDefaultPipe();

        AppOptions client = AppOptions.Parse([]);
        client.Role = UiRole.Client;
        client.ResolveDefaultPipe();

        Assert.Equal("vht-server", server.PipeName);
        Assert.Equal("vht-client", client.PipeName);
    }

    [Fact]
    public void An_explicit_pipe_survives_the_role_default()
    {
        AppOptions options = AppOptions.Parse(["--pipe", "vht-ui-driver-client"]);
        options.Role = UiRole.Client;
        options.ResolveDefaultPipe();

        Assert.Equal("vht-ui-driver-client", options.PipeName);
    }

    [Fact]
    public void Elevation_is_on_unless_the_harness_turns_it_off()
    {
        Assert.False(AppOptions.Parse([]).NoElevate);
        Assert.True(AppOptions.Parse(["--no-elevate"]).NoElevate);
        Assert.Equal(33240, AppOptions.Parse(["--port", "33240"]).Port);
    }
}

