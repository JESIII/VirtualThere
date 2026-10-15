using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vht.Ui.Hosting;

namespace Vht.Ui.Tests;

/// <summary>
/// What the window does with a worker's own output.
/// </summary>
/// <remarks>
/// <para>
/// The defect: an unelevated worker's stdout and stderr were redirected - and then explicitly
/// drained and thrown away (<c>WorkerProcess.cs</c>, "Drained and discarded"). So when the worker
/// died before it bound the control pipe, the window could only say "the worker did not bind the
/// control pipe in time" and the operator had nothing to go on. The lines are the diagnosis.
/// </para>
/// <para>
/// The elevated case cannot be redirected at all: <c>ShellExecuteEx</c> has no handles to hand over.
/// That worker writes to the server's runtime log instead, and the failure message has to say so
/// rather than leaving the operator to guess that a file exists.
/// </para>
/// </remarks>
public sealed class WorkerOutputTests
{
    [Fact]
    public void TheLauncherKeepsWhatTheWorkerWroteInsteadOfDiscardingIt()
    {
        string command = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

        var spec = new WorkerLaunchSpec
        {
            ExecutablePath = command,
            Arguments = ["/c", "echo hello-from-the-worker"],
            WorkingDirectory = Environment.CurrentDirectory,
            Elevate = false,
        };

        using IWorkerProcess worker = new WorkerProcessLauncher().Launch(spec);

        Assert.True(
            WaitForOutput(worker, "hello-from-the-worker"),
            "the worker's stdout was not retained: " + string.Join(" | ", worker.OutputTail));
    }

    [Fact]
    public void TheBufferIsBoundedAndKeepsTheLastLines()
    {
        var buffer = new WorkerOutputBuffer();

        for (int i = 0; i < WorkerOutputBuffer.Capacity + 50; i++)
        {
            buffer.Append("line " + i);
        }

        buffer.Append("went wrong", isError: true);

        IReadOnlyList<string> lines = buffer.Snapshot();

        Assert.Equal(WorkerOutputBuffer.Capacity, lines.Count);

        // The tail is what matters, and the two streams stay distinguishable.
        Assert.Equal("stderr: went wrong", lines[^1]);
        Assert.Equal("line " + (WorkerOutputBuffer.Capacity + 49), lines[^2]);

        // Oldest first, and the beginning really is gone.
        Assert.Equal("line 51", lines[0]);
    }

    [Fact]
    public async Task AWorkerThatExitsBeforeBindingSaysWhereItsOutputWent()
    {
        var plane = new FakeControlPlane(() => false);
        var host = new FakeWorkerProcessHost(_ => new FakeWorkerProcess
        {
            Id = 9001,
            HasExited = true,
            OutputTail = ["starting", "access denied"],
        });

        var supervisor = SupervisorFixture.Supervisor(host, plane);
        WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(WorkerStartResult.TimedOut, result);
        Assert.NotNull(supervisor.LastError);
        Assert.Contains("exited before it bound the control pipe", supervisor.LastError!, StringComparison.Ordinal);
        Assert.Contains("its last output is shown below", supervisor.LastError!, StringComparison.Ordinal);

        // And the lines themselves are there for the window log, not just promised.
        Assert.Equal(new[] { "starting", "access denied" }, supervisor.LastWorkerOutput);
    }

    [Fact]
    public async Task AnElevatedWorkerThatCannotBeRedirectedPointsAtTheRuntimeLog()
    {
        var plane = new FakeControlPlane(() => false);
        var host = new FakeWorkerProcessHost(_ => new FakeWorkerProcess
        {
            Id = 9002,
            HasExited = true,
        });

        // Elevate = true: ShellExecuteEx hands over no handles, so there is nothing to retain.
        var supervisor = SupervisorFixture.Supervisor(host, plane);
        WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(WorkerStartResult.TimedOut, result);
        Assert.Empty(supervisor.LastWorkerOutput);
        Assert.Contains("vht status", supervisor.LastError!, StringComparison.Ordinal);
        Assert.Contains("runtime log", supervisor.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WaitForOutput(IWorkerProcess worker, string expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            foreach (string line in worker.OutputTail)
            {
                if (line.Contains(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (worker.HasExited)
            {
                // One more read after exit: the async readers flush on their own schedule.
                Thread.Sleep(200);
                return worker.OutputTail.Any(line => line.Contains(expected, StringComparison.Ordinal));
            }

            Thread.Sleep(50);
        }

        return false;
    }
}
