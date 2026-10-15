using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Vht.Ui.Hosting;

/// <summary>One worker process a window started, as the supervisor sees it.</summary>
public interface IWorkerProcess : IDisposable
{
    /// <summary>Operating-system process id, reported in the window log.</summary>
    int Id { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>
    /// The worker's most recent standard output and error lines, oldest first, bounded. Empty when
    /// its output could not be redirected - an elevated worker is started through
    /// <c>ShellExecuteEx</c>, which has no handles to redirect; that worker's lines are in the
    /// server's runtime log instead (<c>vht status</c> prints its path).
    /// </summary>
    IReadOnlyList<string> OutputTail { get; }

    /// <summary>Waits up to <paramref name="timeout"/>; returns true if the process exited.</summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>
    /// Ends the process and its children. An unelevated window cannot terminate an elevated
    /// worker, so callers must expect a <see cref="System.ComponentModel.Win32Exception"/> here and
    /// fall back on the control pipe's own shutdown exchange (<c>server.stop</c>), which is the
    /// first thing <see cref="WorkerSupervisor.StopOwnedWorkerAsync"/> tries.
    /// </summary>
    void Terminate();
}

/// <summary>Starts worker processes. Substituted in tests so no process is really started.</summary>
public interface IWorkerProcessHost
{
    IWorkerProcess Launch(WorkerLaunchSpec spec);
}

/// <summary>The real launcher: <c>ShellExecuteEx</c> with <c>Verb=runas</c>, hidden, no console.</summary>
public sealed class WorkerProcessLauncher : IWorkerProcessHost
{
    public IWorkerProcess Launch(WorkerLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var info = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            Arguments = spec.ArgumentsText(),
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = spec.Elevate,

            // A WinExe worker has no console of its own, but when it is started without
            // elevation it would inherit the window's standard handles; redirecting them keeps
            // the worker's own log lines out of whatever console the window was launched from.
            CreateNoWindow = !spec.Elevate,
            RedirectStandardOutput = !spec.Elevate,
            RedirectStandardError = !spec.Elevate,
        };

        if (spec.Elevate)
        {
            info.Verb = "runas";
            info.WindowStyle = ProcessWindowStyle.Hidden;
        }

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException("the worker process could not be started");

        var output = new WorkerOutputBuffer();

        if (!spec.Elevate)
        {
            // Retained, not discarded. An unelevated worker's own log lines are the only record of
            // why it did not come up - and once it has exited there is nothing left to ask. The
            // handler has to keep draining the pipes either way: undrained, the worker blocks once
            // the buffer fills.
            process.OutputDataReceived += (_, e) => output.Append(e.Data);
            process.ErrorDataReceived += (_, e) => output.Append(e.Data, isError: true);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return new WorkerProcess(process, output);
    }

    private sealed class WorkerProcess : IWorkerProcess
    {
        private readonly Process _process;
        private readonly WorkerOutputBuffer _output;

        internal WorkerProcess(Process process, WorkerOutputBuffer output)
        {
            _process = process;
            _output = output;
        }

        public int Id => _process.Id;

        public IReadOnlyList<string> OutputTail => _output.Snapshot();

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            // A process that is already gone counts as exited: this is asked on the way out, when
            // "did it leave?" is the question, not "is it still there?".
            if (HasExited)
            {
                return true;
            }

            return _process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));
        }

        public void Terminate()
        {
            if (HasExited)
            {
                return;
            }

            _process.Kill(entireProcessTree: true);
        }

        public void Dispose() => _process.Dispose();
    }
}

/// <summary>
/// Keeps the last <see cref="Capacity"/> lines a worker wrote, so a worker that never bound the
/// control pipe can still be explained after it is gone. Bounded on purpose: a worker that writes
/// in a loop must not become a leak in the window that is trying to report it.
/// </summary>
internal sealed class WorkerOutputBuffer
{
    internal const int Capacity = 200;

    private readonly Queue<string> _lines = new(Capacity + 1);
    private readonly object _gate = new();

    /// <summary>
    /// Adds one line. <paramref name="isError"/> prefixes it so the two streams stay
    /// distinguishable in a view that shows one list. Null means "that stream ended".
    /// </summary>
    internal void Append(string? line, bool isError = false)
    {
        if (line is null)
        {
            return;
        }

        string text = isError ? "stderr: " + line : line;

        lock (_gate)
        {
            while (_lines.Count >= Capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(text);
        }
    }

    /// <summary>A copy of the retained lines, oldest first.</summary>
    internal IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToArray();
        }
    }
}
