using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vht.Ui.Hosting;
using Vht.Ui.Services;

namespace Vht.Ui.Tests;

/// <summary>
/// The durable record of a worker launch: <c>&lt;log directory&gt;\worker-&lt;pid&gt;.log</c>.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory tail (<see cref="WorkerOutputBuffer"/>) dies with the window, and an elevated
/// worker's streams cannot be redirected at all. So the acceptance question is not "does the window
/// still have the lines?" but "can an operator find them after the worker was killed before it
/// bound the control pipe?" - which means a file written as the lines arrive, not at exit.
/// </para>
/// <para>
/// The elevated half cannot be exercised end to end here: starting one raises a real consent
/// prompt, and no test may answer it. What is covered instead is that both launch modes get the
/// same header written through the same code, before the elevation branch, and that the elevated
/// header tells the operator which channel the worker's own lines went to.
/// </para>
/// </remarks>
public sealed class WorkerOutputLogTests
{
    private static readonly Regex Stamped = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z \[pid \d+\] ",
        RegexOptions.CultureInvariant);

    [Fact]
    public void AnUnelevatedWorkersOutputIsWrittenToADurableFile()
    {
        string directory = NewTempDirectory();
        try
        {
            using IWorkerProcess worker = LaunchEcho(directory, "hello-from-the-durable-log");

            Assert.NotNull(worker.OutputLogFile);
            Assert.Equal(
                Path.Combine(directory, "worker-" + worker.Id.ToString(CultureInfo.InvariantCulture) + ".log"),
                worker.OutputLogFile);

            Assert.True(
                WaitForLine(worker.OutputLogFile!, "hello-from-the-durable-log", worker),
                "the worker's output never reached " + worker.OutputLogFile + ": " +
                SafeRead(worker.OutputLogFile!));

            string[] lines = ReadShared(worker.OutputLogFile!);

            // The launch record comes first, then the worker's own lines, each stamped with the
            // time and the pid so two records in one directory cannot be confused.
            Assert.Contains("launched unelevated", lines[0], StringComparison.Ordinal);
            Assert.Contains("[pid " + worker.Id.ToString(CultureInfo.InvariantCulture) + "]", lines[0], StringComparison.Ordinal);
            Assert.True(Stamped.IsMatch(lines[0]), "the record's header is not stamped: " + lines[0]);
            Assert.True(
                HasWorkerLine(lines, "hello-from-the-durable-log"),
                "no stamped line for the worker's output in: " + string.Join(" | ", lines));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void AWorkerKilledBeforeItBindsAnythingLeavesItsOutputOnDisk()
    {
        string directory = NewTempDirectory();
        try
        {
            // No pipe is ever involved, which is the worst case the card names: a worker that is
            // killed before it binds the control pipe. It writes one line, then hangs.
            string command = Command();
            var spec = new WorkerLaunchSpec
            {
                ExecutablePath = command,
                Arguments = ["/c", "echo first-and-last-words & ping -n 300 127.0.0.1 > nul"],
                WorkingDirectory = Environment.CurrentDirectory,
                Elevate = false,
                OutputLogDirectory = directory,
            };

            IWorkerProcess worker = new WorkerProcessLauncher().Launch(spec);
            string? file = worker.OutputLogFile;
            Assert.NotNull(file);

            Assert.True(
                WaitForLine(file!, "first-and-last-words", worker),
                "the worker's line was not on disk before the kill: " + SafeRead(file!));

            try
            {
                worker.Terminate();
            }
            finally
            {
                worker.Dispose();
            }

            // Read after the process is gone, through a fresh handle: the line is on disk because
            // each line is flushed as it arrives, not once at exit (the process never exited on its
            // own - it was killed).
            string[] lines = ReadShared(file!);
            Assert.True(
                HasWorkerLine(lines, "first-and-last-words"),
                "the killed worker's line is not in: " + string.Join(" | ", lines));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothLaunchModesGetTheLaunchRecordBeforeAnythingElseCanFail(bool elevate)
    {
        string directory = NewTempDirectory();
        try
        {
            WorkerLaunchSpec fromFixture = SupervisorFixture.Spec(elevate);
            WorkerLaunchSpec spec = new()
            {
                ExecutablePath = fromFixture.ExecutablePath,
                Arguments = fromFixture.Arguments,
                WorkingDirectory = fromFixture.WorkingDirectory,
                Elevate = elevate,
                OutputLogDirectory = directory,
            };

            // Exactly what the launcher does with the spec, minus Process.Start: the record is
            // written for both modes through the same call, before the elevation branch.
            using (WorkerOutputLog? log = WorkerOutputLog.TryCreate(
                spec.OutputLogDirectory, 4242, WorkerProcessLauncher.LaunchRecord(spec)))
            {
                Assert.NotNull(log);
                Assert.Equal(Path.Combine(directory, "worker-4242.log"), log!.Path);
            }

            string[] lines = ReadShared(Path.Combine(directory, "worker-4242.log"));
            Assert.Single(lines);
            Assert.True(Stamped.IsMatch(lines[0]), "the record's header is not stamped: " + lines[0]);
            Assert.Contains("[pid 4242]", lines[0], StringComparison.Ordinal);
            Assert.Contains(spec.ExecutablePath, lines[0], StringComparison.Ordinal);

            if (elevate)
            {
                // The only thing a window can write for an elevated worker: where the worker's own
                // lines went instead, since ShellExecuteEx hands over no handles.
                Assert.Contains("launched elevated", lines[0], StringComparison.Ordinal);
                Assert.Contains("cannot be redirected", lines[0], StringComparison.Ordinal);
                Assert.Contains("runtime log", lines[0], StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("launched unelevated", lines[0], StringComparison.Ordinal);
                Assert.Contains("captured below", lines[0], StringComparison.Ordinal);
            }
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void StderrStaysDistinguishableAndIsFlushedAsItArrives()
    {
        string directory = NewTempDirectory();
        try
        {
            string command = Command();
            var spec = new WorkerLaunchSpec
            {
                ExecutablePath = command,
                Arguments = ["/c", "echo went-wrong 1>&2"],
                WorkingDirectory = Environment.CurrentDirectory,
                Elevate = false,
                OutputLogDirectory = directory,
            };

            using IWorkerProcess worker = new WorkerProcessLauncher().Launch(spec);

            Assert.NotNull(worker.OutputLogFile);
            Assert.True(
                WaitForLine(worker.OutputLogFile!, "went-wrong", worker),
                "stderr never reached the durable file: " + SafeRead(worker.OutputLogFile!));

            string[] stderrLines = ReadShared(worker.OutputLogFile!);
            Assert.True(
                HasWorkerLine(stderrLines, "stderr: went-wrong"),
                "stderr is not distinguishable in: " + string.Join(" | ", stderrLines));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void TheDirectoryTheExecutableResolvedIsWhatTheLaunchCarries()
    {
        // The window does not decide this itself: UiHost.Run stamps the executable's resolved
        // default log directory onto the options, and the launch spec copies it. This is the hop
        // between "the server writes <config>\logs\server.log" and "the window writes
        // <config>\logs\worker-<pid>.log" - one rule, one directory.
        var options = new AppOptions
        {
            WorkerOutputLogDirectory = @"C:\ProgramData\VirtualThere\logs",
        };

        WorkerLaunchSpec spec = WorkerLaunchSpec.ForRole(UiRole.Server, options);

        Assert.Equal(@"C:\ProgramData\VirtualThere\logs", spec.OutputLogDirectory);
    }

    [Fact]
    public void TheCommandLineCannotPointTheRecordSomewhereElse()
    {
        AppOptions options = AppOptions.Parse([]);

        Assert.Null(options.WorkerOutputLogDirectory);
        Assert.Null(WorkerLaunchSpec.ForRole(UiRole.Server, options).OutputLogDirectory);
    }

    [Fact]
    public void NoDirectorySuppliedMeansNoFileAndAnUnchangedLaunch()
    {
        using IWorkerProcess worker = LaunchEcho(directory: null, "still-runs");

        Assert.Null(worker.OutputLogFile);
        Assert.True(WaitForTail(worker, "still-runs"), "the launch regressed: output was not retained");
    }

    [Fact]
    public void ALogThatCannotBeOpenedDoesNotStopTheLaunch()
    {
        string parent = NewTempDirectory();
        try
        {
            // A regular file where the directory should be: CreateDirectory fails with an
            // IOException, which must be swallowed - capture is a diagnostic, never a dependency.
            string occupied = Path.Combine(parent, "not-a-directory");
            File.WriteAllText(occupied, "x");

            using IWorkerProcess worker = LaunchEcho(occupied, "still-runs");

            Assert.Null(worker.OutputLogFile);
            Assert.True(WaitForTail(worker, "still-runs"), "the launch regressed: output was not retained");
        }
        finally
        {
            Delete(parent);
        }
    }

    [Fact]
    public async Task TheFailureMessageNamesTheDurableFile()
    {
        string directory = NewTempDirectory();
        try
        {
            string file = Path.Combine(directory, "worker-9003.log");
            var host = new FakeWorkerProcessHost(_ => new FakeWorkerProcess
            {
                Id = 9003,
                HasExited = true,
                OutputTail = ["starting", "access denied"],
                OutputLogFile = file,
            });

            var supervisor = SupervisorFixture.Supervisor(host, new FakeControlPlane(() => false));
            WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(WorkerStartResult.TimedOut, result);
            Assert.Contains("its last output is shown below", supervisor.LastError!, StringComparison.Ordinal);
            Assert.Contains(file, supervisor.LastError!, StringComparison.Ordinal);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task AnElevatedWorkerWithoutATailStillNamesTheRecord()
    {
        string directory = NewTempDirectory();
        try
        {
            string file = Path.Combine(directory, "worker-9004.log");
            var host = new FakeWorkerProcessHost(_ => new FakeWorkerProcess
            {
                Id = 9004,
                HasExited = true,
                OutputLogFile = file,
            });

            var supervisor = SupervisorFixture.Supervisor(host, new FakeControlPlane(() => false));
            WorkerStartResult result = await supervisor.EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(WorkerStartResult.TimedOut, result);
            Assert.Empty(supervisor.LastWorkerOutput);
            Assert.Contains("vht status", supervisor.LastError!, StringComparison.Ordinal);
            Assert.Contains("the launch is recorded in " + file, supervisor.LastError!, StringComparison.Ordinal);
        }
        finally
        {
            Delete(directory);
        }
    }

    private static IWorkerProcess LaunchEcho(string? directory, string text)
    {
        var spec = new WorkerLaunchSpec
        {
            ExecutablePath = Command(),
            Arguments = ["/c", "echo " + text],
            WorkingDirectory = Environment.CurrentDirectory,
            Elevate = false,
            OutputLogDirectory = directory,
        };

        return new WorkerProcessLauncher().Launch(spec);
    }

    private static string Command() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private static string NewTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vht-worker-output-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// True when a line the worker itself wrote (not the launch header, whose command line also
    /// contains the text) carries <paramref name="expected"/>. Trailing blanks are ignored: cmd's
    /// <c>echo x &amp; ...</c> leaves the blank before the ampersand in the output.
    /// </summary>
    private static bool HasWorkerLine(string[] lines, string expected) =>
        lines.Any(line => Stamped.IsMatch(line) &&
            !line.Contains("launched ", StringComparison.Ordinal) &&
            line.TrimEnd().Contains(expected, StringComparison.Ordinal));

    /// <summary>Waits for a line the worker wrote to appear in the durable file itself.</summary>
    private static bool WaitForLine(string file, string expected, IWorkerProcess worker)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(file) && HasWorkerLine(ReadShared(file), expected))
            {
                return true;
            }

            if (worker.HasExited)
            {
                // The readers flush on their own schedule; give them a moment, then look again.
                Thread.Sleep(200);
                return File.Exists(file) && HasWorkerLine(ReadShared(file), expected);
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static bool WaitForTail(IWorkerProcess worker, string expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (worker.OutputTail.Any(line => line.Contains(expected, StringComparison.Ordinal)))
            {
                return true;
            }

            if (worker.HasExited)
            {
                Thread.Sleep(200);
                return worker.OutputTail.Any(line => line.Contains(expected, StringComparison.Ordinal));
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static string SafeRead(string file)
    {
        try
        {
            return File.Exists(file) ? string.Join(" | ", ReadShared(file)) : "(no file)";
        }
        catch (IOException ex)
        {
            return "(unreadable: " + ex.Message + ")";
        }
    }

    /// <summary>
    /// Reads the record while the window still holds it open for writing: the launcher keeps the
    /// file open with <see cref="FileShare.Read"/>, so a plain read has to share it back.
    /// </summary>
    private static string[] ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }
}
