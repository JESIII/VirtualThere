using System;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Vht.Ui.Hosting;
using Vht.Ui.Views;

namespace Vht.Ui.Tests;

/// <summary>
/// The window/worker lifetime rules docs/UI.md section 1 freezes, asserted here because the card
/// demands tests for them by name: (b) closing the window leaves the worker alive and serving, and
/// (c) an explicit Exit stops it.
/// </summary>
public sealed class WorkerLifetimeTests
{
    [Fact]
    public void Closing_the_window_does_not_touch_the_worker()
    {
        var supervisor = new RecordingSupervisor();
        var lifetime = new WorkerLifetime(supervisor);

        // The window's close/minimise path, driven the way the session drives it.
        lifetime.OnWindowHidden();

        Assert.Equal(0, supervisor.StopCalls);
        Assert.False(supervisor.OwnedWorkerStopped);
    }

    [Fact]
    public async Task Exit_stops_the_worker_this_window_started()
    {
        var supervisor = new RecordingSupervisor { OwnsWorker = true };
        var lifetime = new WorkerLifetime(supervisor);

        bool stopped = await lifetime.OnExitAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.Equal(1, supervisor.StopCalls);
    }

    [Fact]
    public async Task Exit_leaves_alone_a_worker_this_window_merely_attached_to()
    {
        // Nothing of ours: the installed service, or a worker another instance started.
        var supervisor = new RecordingSupervisor { OwnsWorker = false, StopResult = false };
        var lifetime = new WorkerLifetime(supervisor);

        bool stopped = await lifetime.OnExitAsync(CancellationToken.None);

        Assert.False(stopped);
        Assert.Equal(1, supervisor.StopCalls);
    }

    [Fact]
    public async Task A_session_with_no_supervisor_exits_cleanly()
    {
        var lifetime = new WorkerLifetime(workers: null);

        lifetime.OnWindowHidden();

        Assert.False(lifetime.OwnsWorker);
        Assert.False(await lifetime.OnExitAsync(CancellationToken.None));
    }

    [Fact]
    public void Closing_a_role_window_hides_it_to_the_notification_area_instead_of_exiting()
    {
        RunOnStaThread(() =>
        {
            var window = new TestWindow();
            int hidden = 0;
            window.MinimizedToTray += (_, _) => hidden++;

            window.Close();

            Assert.Equal(1, hidden);
            Assert.False(window.ExitRequested);
            Assert.True(window.CloseWasCancelled, "the close must be cancelled: hiding is not exiting");
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void After_exit_the_window_really_closes()
    {
        RunOnStaThread(() =>
        {
            var window = new TestWindow();

            window.ExitApplication();
            Assert.True(window.ExitRequested);

            window.Close();

            Assert.False(window.CloseWasCancelled, "after Exit the close is the real one");
        });
    }

    /// <summary>Runs the body on an STA thread, because a WPF <see cref="Window"/> requires one.</summary>
    private static void RunOnStaThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        failure?.Throw();
    }

    /// <summary>A <see cref="TrayWindow"/> with no XAML, so the close policy can be driven directly.</summary>
    private sealed class TestWindow : TrayWindow
    {
        internal bool CloseWasCancelled { get; private set; }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            // What the base class decided: cancelled means "hidden", not closed.
            CloseWasCancelled = e.Cancel;
        }
    }

    /// <summary>A supervisor that records what was asked of it.</summary>
    private sealed class RecordingSupervisor : IWorkerSupervisor
    {
        public bool OwnsWorker { get; init; }

        public bool StopResult { get; init; } = true;

        public int StopCalls { get; private set; }

        public bool OwnedWorkerStopped => StopCalls > 0 && StopResult;

        public int? WorkerProcessId => OwnsWorker ? 4242 : null;

        public string? LastError => null;

        public IReadOnlyList<string> LastWorkerOutput => Array.Empty<string>();

        public Task<WorkerStartResult> EnsureRunningAsync(CancellationToken cancellationToken) =>
            Task.FromResult(WorkerStartResult.AlreadyRunning);

        public Task<bool> StopOwnedWorkerAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            return Task.FromResult(StopResult);
        }
    }
}
