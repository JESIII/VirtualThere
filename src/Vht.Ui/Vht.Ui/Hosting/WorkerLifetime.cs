using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vht.Ui.Hosting;

/// <summary>
/// How one management session relates the window's lifetime to the privileged worker's. This is the
/// frozen rule from docs/UI.md section 1, in code, so it is a decision the session consults rather
/// than an absence of one:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>
/// Closing (X) or minimising the window **hides it to the notification area and changes nothing
/// about the worker**. The worker keeps serving: connected clients and in-flight transfers are
/// unaffected, and the operator is still in control through the notification-area icon.
/// </item>
/// <item>
/// **Exit** - the window's Exit affordance, or the notification-area Exit item - stops the worker
/// this window started, and only that one. A worker this window merely attached to (the installed
/// service, or a worker another instance started) is left running.
/// </item>
/// </list>
/// <para>
/// Deliberately *not* here: reaping the worker when this window process dies. A worker outlives the
/// window that started it - a crash, an End task or a debugger detaching leaves the server (or
/// client) serving - and the next launch of the window attaches to it instead of spawning a second
/// one. The operator stops it with Exit, or, with no window open, over the control pipe
/// (<c>vht-server stop-server --pipe &lt;name&gt;</c>).
/// </para>
/// </remarks>
public sealed class WorkerLifetime
{
    private readonly IWorkerSupervisor? _workers;

    public WorkerLifetime(IWorkerSupervisor? workers) => _workers = workers;

    /// <summary>True when this session started the worker that is currently running.</summary>
    public bool OwnsWorker => _workers?.OwnsWorker ?? false;

    /// <summary>
    /// The window was closed or minimised: the worker is left exactly as it is.
    /// </summary>
    /// <remarks>
    /// This method exists, and is asserted in tests/Vht.Ui.Tests, so that "closing the window keeps
    /// the worker serving" is a code path a reader can follow rather than an absence of a code path.
    /// It must never call <see cref="IWorkerSupervisor.StopOwnedWorkerAsync"/>.
    /// </remarks>
    public void OnWindowHidden()
    {
        // Nothing happens here on purpose. See the type's remarks and docs/UI.md section 1.
    }

    /// <summary>
    /// Explicit Exit: stops the worker this session started, before the window process ends.
    /// Returns true when a worker was stopped; false means there was nothing of ours to stop.
    /// </summary>
    public async Task<bool> OnExitAsync(CancellationToken cancellationToken)
    {
        if (_workers is null)
        {
            return false;
        }

        return await _workers.StopOwnedWorkerAsync(cancellationToken).ConfigureAwait(false);
    }
}
