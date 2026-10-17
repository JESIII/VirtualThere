using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vht.Ui.Hosting;

namespace Vht.Ui.Tests;

/// <summary>A worker process that does exactly what a test tells it to.</summary>
internal sealed class FakeWorkerProcess : IWorkerProcess
{
    public int Id { get; init; } = 4242;

    public bool HasExited { get; set; }

    /// <summary>Lines the fake worker "wrote", for the tests that check they survive its exit.</summary>
    public IReadOnlyList<string> OutputTail { get; set; } = Array.Empty<string>();

    /// <summary>
    /// True when the process leaves on its own once <see cref="Terminate"/> is called.
    /// <c>ExitOnTerminate=false</c> models the client worker, which has no shutdown verb at all; the
    /// default models the server worker, which answers <c>server.stop</c>.
    /// </summary>
    public bool ExitOnTerminate { get; init; } = true;

    /// <summary>Set to make <see cref="Terminate"/> fail, as it does for an elevated worker.</summary>
    public Exception? TerminateThrows { get; init; }

    public bool Terminated { get; private set; }

    public int DisposeCount { get; private set; }

    public bool WaitForExit(TimeSpan timeout)
    {
        if (!HasExited && Terminated && ExitOnTerminate)
        {
            HasExited = true;
        }

        return HasExited;
    }

    public void Terminate()
    {
        if (TerminateThrows is not null)
        {
            throw TerminateThrows;
        }

        Terminated = true;
        if (ExitOnTerminate)
        {
            HasExited = true;
        }
    }

    public void Dispose() => DisposeCount++;
}

/// <summary>A process host that records launches instead of starting anything.</summary>
internal sealed class FakeWorkerProcessHost : IWorkerProcessHost
{
    private readonly Func<WorkerLaunchSpec, IWorkerProcess> _factory;

    public FakeWorkerProcessHost(Func<WorkerLaunchSpec, IWorkerProcess>? factory = null) =>
        _factory = factory ?? (_ => new FakeWorkerProcess());

    public int LaunchCount { get; private set; }

    public WorkerLaunchSpec? LastSpec { get; private set; }

    /// <summary>Set to make the launch fail, as a declined consent prompt does.</summary>
    public Exception? LaunchThrows { get; set; }

    public IWorkerProcess Launch(WorkerLaunchSpec spec)
    {
        LastSpec = spec;
        LaunchCount++;
        if (LaunchThrows is not null)
        {
            throw LaunchThrows;
        }

        return _factory(spec);
    }
}

/// <summary>
/// A control plane whose pipe answers, or does not, under the test's control - so "is something
/// already serving this pipe?" is a property of the test rather than an accident of timing.
/// </summary>
internal sealed class FakeControlPlane
{
    private readonly Func<bool> _live;

    /// <param name="live">
    /// Called for every probe. The default always answers, which is the "a worker is already
    /// serving this pipe" case.
    /// </param>
    public FakeControlPlane(Func<bool>? live = null) => _live = live ?? (() => true);

    public int Probes { get; private set; }

    public int GracefulStops { get; private set; }

    public Task<bool> ProbeAsync(CancellationToken ct)
    {
        Probes++;
        return Task.FromResult(_live());
    }

    public Task StopGracefullyAsync(CancellationToken ct)
    {
        GracefulStops++;
        return Task.CompletedTask;
    }
}

/// <summary>Shared setup for the supervisor tests.</summary>
internal static class SupervisorFixture
{
    internal static WorkerLaunchSpec Spec(bool elevate = true) => new()
    {
        ExecutablePath = @"C:\portable\vht-server.exe",
        Arguments = ["--worker", "--pipe", "vht-server"],
        WorkingDirectory = @"C:\portable",
        Elevate = elevate,
    };

    internal static WorkerSupervisor Supervisor(
        FakeWorkerProcessHost host,
        FakeControlPlane plane,
        TimeSpan? readyTimeout = null,
        TimeSpan? gracefulTimeout = null,
        TimeSpan? terminateTimeout = null) =>
        new(
            host,
            Spec(),
            plane.ProbeAsync,
            plane.StopGracefullyAsync,
            readyTimeout ?? TimeSpan.FromSeconds(2),
            gracefulTimeout ?? TimeSpan.FromMilliseconds(200),
            terminateTimeout ?? TimeSpan.FromMilliseconds(200));

    /// <summary>A real, long-lived child process, for the tests that use the shipping launcher.</summary>
    internal static Process StartIdleChild()
    {
        string command = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = command,
            Arguments = "/c ping -n 300 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        return process ?? throw new InvalidOperationException("could not start a child process for the test");
    }

    /// <summary>True while a process with that id is alive.</summary>
    internal static bool IsRunning(int pid)
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
}
