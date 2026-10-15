using System;
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

        if (!spec.Elevate)
        {
            // Drained and discarded: the worker reports its progress over the control pipe, and an
            // undrained pipe would block it once the buffer fills.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return new WorkerProcess(process);
    }

    private sealed class WorkerProcess : IWorkerProcess
    {
        private readonly Process _process;

        internal WorkerProcess(Process process) => _process = process;

        public int Id => _process.Id;

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
