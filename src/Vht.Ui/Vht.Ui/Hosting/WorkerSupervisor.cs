using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vht.Ui.Services;

namespace Vht.Ui.Hosting;

/// <summary>Outcome of "make sure the privileged side is up".</summary>
public enum WorkerStartResult
{
    /// <summary>A control plane already answered on the pipe: the window attached, nothing was started.</summary>
    AlreadyRunning,

    /// <summary>The worker was started and the control plane answered.</summary>
    Started,

    /// <summary>The worker could not be started (declined consent, or the process would not start).</summary>
    LaunchFailed,

    /// <summary>The worker started but never bound the control pipe in time.</summary>
    TimedOut,
}

/// <summary>
/// The window's side of the worker lifecycle, as the view models use it.
/// </summary>
/// <remarks>
/// Defined as an interface so a view model can be driven in a test without an elevated process
/// anywhere near it; <see cref="WorkerSupervisor"/> is the shipping implementation.
/// </remarks>
public interface IWorkerSupervisor
{
    /// <summary>True when this window started the worker that is currently running.</summary>
    bool OwnsWorker { get; }

    /// <summary>Process id of the worker this window started, if any.</summary>
    int? WorkerProcessId { get; }

    /// <summary>Last failure detail, for the window log.</summary>
    string? LastError { get; }

    /// <summary>
    /// The last lines the worker wrote to its standard output and error, oldest first. Empty for an
    /// elevated worker, whose handles cannot be redirected: its lines are in the server's runtime
    /// log, and <see cref="LastError"/> names that file in that case.
    /// </summary>
    IReadOnlyList<string> LastWorkerOutput { get; }

    /// <summary>
    /// Attaches when a control plane already answers on the pipe; otherwise starts the worker and
    /// waits for it to bind the pipe.
    /// </summary>
    Task<WorkerStartResult> EnsureRunningAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the worker this window started. A worker the window merely attached to (the installed
    /// service, or another instance's worker) is left running; the return value says whether a
    /// worker was stopped.
    /// </summary>
    Task<bool> StopOwnedWorkerAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Starts, watches and stops the privileged worker behind one management window.
/// </summary>
/// <remarks>
/// <para>
/// The supervisor never assumes it is the only thing talking to the pipe. If a control plane
/// already answers - the installed service, an earlier worker, or another instance - it reports
/// <see cref="WorkerStartResult.AlreadyRunning"/> and owns nothing, so closing the window cannot
/// take down a service it did not start.
/// </para>
/// <para>
/// When the window did start the worker, the worker is stopped on **Exit** and only on Exit
/// (<see cref="WorkerLifetime"/>): first politely (the role's own shutdown exchange on the frozen
/// pipe vocabulary), then by ending the process. Closing or minimising the window leaves the worker
/// serving, and so does dying: a worker that outlives its window is adopted by the next window
/// rather than reaped, which is the behaviour docs/UI.md section 1 freezes.
/// </para>
/// </remarks>
public sealed class WorkerSupervisor : IWorkerSupervisor, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IWorkerProcessHost _host;
    private readonly WorkerLaunchSpec _spec;
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Func<CancellationToken, Task>? _gracefulStop;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeSpan _gracefulTimeout;
    private readonly TimeSpan _terminateTimeout;

    private IWorkerProcess? _worker;
    private bool _disposed;

    public WorkerSupervisor(
        IWorkerProcessHost host,
        WorkerLaunchSpec spec,
        Func<CancellationToken, Task<bool>> probe,
        Func<CancellationToken, Task>? gracefulStop = null,
        TimeSpan? readyTimeout = null,
        TimeSpan? gracefulTimeout = null,
        TimeSpan? terminateTimeout = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _gracefulStop = gracefulStop;
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(40);
        _gracefulTimeout = gracefulTimeout ?? TimeSpan.FromSeconds(10);
        _terminateTimeout = terminateTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    public bool OwnsWorker => _worker is not null && !_worker.HasExited;

    /// <inheritdoc />
    public int? WorkerProcessId => _worker is null ? null : _worker.Id;

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<string> LastWorkerOutput =>
        _worker is null ? Array.Empty<string>() : _worker.OutputTail;

    /// <summary>The supervisor a server window uses: probe and shutdown both on <c>server.*</c>.</summary>
    public static WorkerSupervisor ForServer(ControlPlane.VhtServiceClient client, AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new WorkerSupervisor(
            new WorkerProcessLauncher(),
            WorkerLaunchSpec.ForRole(UiRole.Server, options),
            probe: async ct => (await client.GetServerStatusAsync(ct).ConfigureAwait(false)).Status
                == ControlPlane.ControlPlaneStatus.Ok,
            gracefulStop: ct => client.StopServerAsync(ct));
    }

    /// <summary>
    /// The supervisor a client window uses. The client control plane has no shutdown verb, so the
    /// polite step is detaching every attached device and the process is ended afterwards.
    /// </summary>
    public static WorkerSupervisor ForClient(ControlPlane.VhtServiceClient client, AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new WorkerSupervisor(
            new WorkerProcessLauncher(),
            WorkerLaunchSpec.ForRole(UiRole.Client, options),
            probe: async ct => (await client.ListPeersAsync(ct).ConfigureAwait(false)).Status
                == ControlPlane.ControlPlaneStatus.Ok,
            gracefulStop: ct => client.DetachAllAsync(ct));
    }

    /// <inheritdoc />
    public async Task<WorkerStartResult> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WorkerSupervisor));
        }

        // A live control plane means "attach": the installed service keeps its own lifetime, and
        // starting a second worker would fight it for the pipe.
        if (await IsLiveAsync(cancellationToken).ConfigureAwait(false))
        {
            return WorkerStartResult.AlreadyRunning;
        }

        if (!OwnsWorker)
        {
            try
            {
                _worker = _host.Launch(_spec);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _worker = null;
                return WorkerStartResult.LaunchFailed;
            }
        }

        bool ready = await WaitForControlPlaneAsync(cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            // Not overwritten when the wait already knows something more specific: "the worker exited
            // before it bound the control pipe" (with its output) is the diagnosis, and replacing it
            // with the generic "did not answer in time" is what made this undiagnosable.
            LastError ??= "the worker did not answer on the control pipe within " +
                _readyTimeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s" +
                WhereTheOutputIs();
            return WorkerStartResult.TimedOut;
        }

        return WorkerStartResult.Started;
    }

    /// <inheritdoc />
    public async Task<bool> StopOwnedWorkerAsync(CancellationToken cancellationToken)
    {
        IWorkerProcess? worker = _worker;
        _worker = null;

        if (worker is null)
        {
            // Nothing of ours: an attached service or another instance's worker stays up.
            return false;
        }

        try
        {
            if (worker.HasExited)
            {
                return true;
            }

            if (_gracefulStop is not null)
            {
                try
                {
                    await _gracefulStop(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LastError = "graceful stop failed: " + ex.Message;
                }
            }

            if (worker.WaitForExit(_gracefulTimeout))
            {
                return true;
            }

            try
            {
                worker.Terminate();
            }
            catch (Exception ex)
            {
                // Expected for an elevated worker: an unelevated window may not end it. The
                // worker's parent watch finishes the job the moment this process disappears.
                LastError = "could not end the worker (pid " +
                    worker.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "): " + ex.Message;
            }

            return worker.WaitForExit(_terminateTimeout);
        }
        finally
        {
            worker.Dispose();
        }
    }

    private async Task<bool> IsLiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _probe(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private async Task<bool> WaitForControlPlaneAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + _readyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_worker is { HasExited: true })
            {
                LastError = "the worker (pid " +
                    _worker.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ") exited before it bound the control pipe" + WhereTheOutputIs();
                return false;
            }

            if (await IsLiveAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// The sentence that says where a worker's dying words ended up, appended to both failure
    /// messages so an operator is never told "it did not come up" without being told where to look.
    /// </summary>
    private string WhereTheOutputIs()
    {
        if (LastWorkerOutput.Count > 0)
        {
            return "; its last output is shown below";
        }

        return _spec.Elevate
            ? "; its output went to the server's runtime log (run 'vht status' for the path) - an " +
              "elevated worker cannot be redirected by this window"
            : "; it wrote no output";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _worker?.Dispose();
        _worker = null;
    }
}
