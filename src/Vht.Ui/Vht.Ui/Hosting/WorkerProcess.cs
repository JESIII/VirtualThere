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

    /// <summary>
    /// The durable file this launch's record is written to
    /// (<c>worker-&lt;pid&gt;.log</c> in the launch's log directory), or null when nothing is being
    /// persisted - no directory was supplied, or the file could not be opened. It holds the launch
    /// line and, when the output could be redirected, every line the worker wrote, stamped with
    /// time and pid and flushed as it arrived: that is what survives a worker killed before it
    /// binds the control pipe.
    /// </summary>
    string? OutputLogFile { get; }

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

        ProcessStartInfo info = CreateStartInfo(spec);

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException("the worker process could not be started");

        var output = new WorkerOutputBuffer();

        // Written for both launch modes, before anything else can be known about the worker's
        // output: a worker killed before it binds the pipe leaves at least this - pid, time, and
        // the command line it was started with. For an elevated worker it is all the window can
        // write at all.
        WorkerOutputLog? log = WorkerOutputLog.TryCreate(
            spec.OutputLogDirectory, process.Id, LaunchRecord(spec));

        if (!spec.Elevate)
        {
            // Retained, not discarded. An unelevated worker's own log lines are the only record of
            // why it did not come up - and once it has exited there is nothing left to ask. They
            // are kept twice: the bounded tail below for the window log, and the durable file
            // (stamped and flushed line by line) for the worker that is killed mid-sentence. The
            // handler has to keep draining the pipes either way: undrained, the worker blocks once
            // the buffer fills. The file is written first, so a line visible in the tail is a line
            // already on disk.
            process.OutputDataReceived += (_, e) =>
            {
                log?.Append(e.Data);
                output.Append(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                log?.Append(e.Data, isError: true);
                output.Append(e.Data, isError: true);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return new WorkerProcess(process, output, log);
    }

    /// <summary>
    /// The durable record's first line: what was launched, and which channel its output has. The
    /// two modes differ because the channels do - <c>ShellExecuteEx</c> hands over no handles, so
    /// an elevated worker's lines only ever reach its own runtime log.
    /// </summary>
    internal static string LaunchRecord(WorkerLaunchSpec spec) =>
        "launched " + (spec.Elevate ? "elevated (ShellExecuteEx, verb=runas)" : "unelevated") +
        ": " + spec.ExecutablePath + " " + spec.ArgumentsText() +
        (spec.Elevate
            ? "; stdout and stderr cannot be redirected by this window - the worker writes its own " +
              "runtime log in the default log directory ('vht status' prints the path)"
            : "; its stdout and stderr are captured below");

    /// <summary>
    /// The description Windows is handed for the worker: this is the whole of the elevation
    /// contract, in one place, so it can be asserted instead of assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Elevated, <c>UseShellExecute</c> plus <c>Verb=runas</c> is the only combination that raises a
    /// consent prompt, and it raises it for the one process it starts: exactly one prompt per spawn,
    /// by construction rather than by retry logic. <c>WindowStyle.Hidden</c> is the worker's window
    /// style, and <c>CreateNoWindow</c> is set for both modes: the role executables are WinExe
    /// (PE subsystem 2), so there is no console to allocate in the first place, and the flag keeps
    /// that true even if a console-subsystem binary were ever passed in here.
    /// </para>
    /// <para>
    /// Unelevated (the automation path, <c>--no-elevate</c>) is started directly instead of through
    /// the shell, because redirected handles are the only way to read what such a worker said, and
    /// <c>ShellExecuteEx</c> hands over no handles at all.
    /// </para>
    /// </remarks>
    internal static ProcessStartInfo CreateStartInfo(WorkerLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var info = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            Arguments = spec.ArgumentsText(),
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = spec.Elevate,

            // No console window for the worker, ever - in either mode, elevated or not.
            CreateNoWindow = true,

            // Only asked for when the shell is not involved: a WinExe worker started unelevated
            // would otherwise inherit the window's standard handles, and its own log lines would
            // land in whatever console the window was launched from.
            RedirectStandardOutput = !spec.Elevate,
            RedirectStandardError = !spec.Elevate,
        };

        if (spec.Elevate)
        {
            // The one consent prompt. Windows allows one Verb per launch, and the worker is
            // started with nothing that would start another process first.
            info.Verb = "runas";
            info.WindowStyle = ProcessWindowStyle.Hidden;
        }

        return info;
    }

    private sealed class WorkerProcess : IWorkerProcess
    {
        private readonly Process _process;
        private readonly WorkerOutputBuffer _output;
        private readonly WorkerOutputLog? _log;

        internal WorkerProcess(Process process, WorkerOutputBuffer output, WorkerOutputLog? log)
        {
            _process = process;
            _output = output;
            _log = log;
        }

        public int Id => _process.Id;

        public IReadOnlyList<string> OutputTail => _output.Snapshot();

        public string? OutputLogFile => _log?.Path;

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

        public void Dispose()
        {
            // An exited worker's last lines are still in flight on the reader threads; the
            // parameterless WaitForExit is the documented drain point for asynchronous reads, and
            // it returns at once once the process is gone. Without it a killed worker's dying
            // words can be dropped here, after the file they belong in was flushed and before
            // anyone gets to read it - which is exactly the case the durable record exists for.
            try
            {
                if (HasExited)
                {
                    _process.WaitForExit();
                }
            }
            catch (SystemException)
            {
                // The process is gone: nothing left to drain.
            }

            _process.Dispose();
            _log?.Dispose();
        }
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
