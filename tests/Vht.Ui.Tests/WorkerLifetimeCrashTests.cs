using System;
using System.Threading;
using System.Threading.Tasks;
using Vht.Ui.Hosting;
using Xunit;

namespace Vht.Ui.Tests;

/// <summary>
/// The two ways a window can die without the operator pressing Exit, and what each one does to the
/// privileged worker it started. <c>docs/UI.md</c> section 1 used to say "killed or crashes -> keeps
/// serving", which is only half true and was measured to be wrong for the crash half (card
/// <c>t_e25238ae</c>):
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>
/// A window that is **killed outright** (`taskkill /F`, End task, a debugger detaching) runs no code
/// of ours: nothing reaps the worker, and the next launch adopts it. Measured: the worker and its
/// pipe were still there 15 s after `TerminateProcess` on the window.
/// </item>
/// <item>
/// A window that **dies from an unhandled exception** tears the process down through
/// <c>UiHost.Run</c>'s <c>finally</c>, which disposes the session; the session's shutdown runs the
/// same exchange Exit uses, so the worker receives the role's stop verb and goes. Measured: the
/// worker logged `ServerStopping`/`ServerStopped` 0.39 s after binding and was gone ~30 ms before the
/// window process.
/// </item>
/// </list>
/// </remarks>
public sealed class WorkerLifetimeCrashTests
{
    [Fact]
    public async Task A_window_killed_outright_leaves_the_worker_serving()
    {
        // What a killed process gets at best: disposal, from the finalizer or the runtime's teardown.
        // That must never turn into "stop the worker" - a reaper here would take the server down
        // whenever the window was force-killed, which is the case the row's promise exists for.
        var worker = new FakeWorkerProcess();
        var host = new FakeWorkerProcessHost(_ => worker);
        int probes = 0;
        var plane = new FakeControlPlane(() => ++probes > 1); // nothing answers until the worker is up
        WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);

        WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);
        Assert.Equal(WorkerStartResult.Started, result);
        Assert.True(supervisor.OwnsWorker);

        supervisor.Dispose();

        Assert.False(worker.Terminated, "disposal must not end the worker: a killed window cannot stop anything");
        Assert.False(worker.HasExited, "the worker is expected to outlive a window that ran no shutdown code");
        Assert.Equal(0, plane.GracefulStops);
    }

    [Fact]
    public async Task The_teardown_that_runs_on_a_fatal_exception_stops_the_worker_like_exit()
    {
        // The fatal-exception path in UiHost.Run disposes the session, and UiSession.Dispose calls
        // Shutdown - which is the same OnExitAsync exchange the Exit button uses. So "the window
        // crashed" and "the operator pressed Exit" end the owned worker the same way, and a test that
        // pins the crash row is a test of this call.
        var worker = new FakeWorkerProcess();
        var host = new FakeWorkerProcessHost(_ => worker);
        int probes = 0;
        var plane = new FakeControlPlane(() => ++probes > 1);
        WorkerSupervisor supervisor = SupervisorFixture.Supervisor(host, plane);
        var lifetime = new WorkerLifetime(supervisor);

        await supervisor.EnsureRunningAsync(CancellationToken.None);
        bool stopped = await lifetime.OnExitAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.Equal(1, plane.GracefulStops);
        Assert.True(worker.HasExited);
    }

    [Fact]
    public async Task A_worker_this_window_only_attached_to_survives_the_crash_teardown_too()
    {
        // The crash path must not take down something it did not start: a worker another instance
        // started, or the installed service, is left alone exactly as on Exit.
        var plane = new FakeControlPlane(); // something already answers: attach only
        WorkerSupervisor supervisor = SupervisorFixture.Supervisor(new FakeWorkerProcessHost(), plane);

        WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);
        Assert.Equal(WorkerStartResult.AlreadyRunning, result);
        Assert.False(supervisor.OwnsWorker);

        var lifetime = new WorkerLifetime(supervisor);
        Assert.False(await lifetime.OnExitAsync(CancellationToken.None));
        Assert.Equal(0, plane.GracefulStops);
    }
}
