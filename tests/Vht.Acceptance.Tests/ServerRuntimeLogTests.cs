using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Vht.Config;
using Vht.Config.Diagnostics;
using Vht.Server.Config;

namespace Vht.Acceptance.Tests;

/// <summary>
/// The server's <b>runtime log</b>: that it exists by default, that it follows the same directory
/// rule as the configuration, and that it survives the cases that used to leave nothing behind.
/// </summary>
/// <remarks>
/// <para>
/// The defect this covers: the shipped configuration template said <c>[logging] file = ""</c>, and
/// the server read that as "no file". Every capture and rebind outcome - the refusal, the failure,
/// the "reboot required" caveat, the success - was therefore logged into nothing at all. The
/// window's worker is a <c>WinExe</c> (PE subsystem 2) started with <c>Verb=runas</c>, so its
/// stdout has nowhere to go either: elevated, <c>ShellExecuteEx</c> redirects nothing and there is
/// no console to inherit; unelevated, the window redirected the two streams and then drained and
/// threw them away. The whole pipeline was a black box in the shipping configuration, and three
/// outcomes the code distinguishes precisely were indistinguishable to the operator.
/// </para>
/// <para>
/// These tests run the <b>real</b> <c>vht-server.exe</c> from the test output folder and read the
/// file it left on disk - not a mock of its logger. The second test is the requirement that matters
/// most: a server that <i>dies before it binds the control pipe</i> must still have left its output
/// somewhere readable, because that is exactly the case an operator has to diagnose.
/// </para>
/// </remarks>
public sealed class ServerRuntimeLogTests
{
    [Fact]
    public void TheDefaultLogFileFollowsTheConfigDirectoryTheMarkerGoverns()
    {
        string root = NewDirectory("log-default");
        try
        {
            string programData = Path.Combine(root, "programdata");

            // Without the marker: the installed, machine-wide directory.
            ConfigPaths installed = ServerConfigLoader.Resolve(
                executableDirectory: root,
                environmentValue: null,
                programDataDirectory: programData);

            Assert.Equal(
                Path.Combine(programData, "VirtualThere", "logs", "server.log"),
                ServerConfigLoader.DefaultLogFile(installed));

            // With it: beside the configuration the server is actually reading, so a portable build
            // logs into its own folder.
            File.WriteAllText(Path.Combine(root, ConfigPathResolver.PortableMarkerFileName), string.Empty);

            ConfigPaths portable = ServerConfigLoader.Resolve(
                executableDirectory: root,
                environmentValue: null,
                programDataDirectory: programData);

            Assert.Equal(
                Path.Combine(root, "config", "logs", "server.log"),
                ServerConfigLoader.DefaultLogFile(portable));

            // The rule is "the resolved directory", not a second copy of the rule: whatever the
            // config file resolved to, the log is under it.
            Assert.Equal(
                Path.Combine(portable.Directory, "logs", "server.log"),
                ServerConfigLoader.DefaultLogFile(portable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AServerStartedFromAConfigFileWritesItsRuntimeLogThere()
    {
        string root = NewDirectory("log-serve");
        Process? server = null;
        try
        {
            string config = Path.Combine(root, "server.toml");
            File.WriteAllText(config, MinimalConfig(port: FreePort()));

            string logFile = Path.Combine(root, "logs", "server.log");
            Assert.False(File.Exists(logFile), "the log existed before the server was started");

            server = StartServer(root, ["serve", "--config", config, "--pipe", NewPipeName()]);

            string text = WaitForLog(logFile, "running in the foreground");

            Assert.Contains("runtime log: " + InJson(logFile), text, StringComparison.Ordinal);
            Assert.Contains("configuration path: " + InJson(config), text, StringComparison.Ordinal);

            // The file is JSON lines, so every record is machine-readable as well as readable.
            Assert.Contains("\"event\":\"ServerStarting\"", text, StringComparison.Ordinal);
            Assert.Contains("\"path\":", text, StringComparison.Ordinal);
        }
        finally
        {
            Stop(server);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The requirement that the defect was really about: a worker that exits before it binds the
    /// control pipe must still have left its output somewhere. A rejected configuration is the
    /// deterministic version of that - the process gives up before the pipe exists, and the lines it
    /// wrote on the way are the whole diagnosis.
    /// </summary>
    [Fact]
    public void OutputSurvivesAWorkerThatDiesBeforeBindingTheControlPipe()
    {
        string root = NewDirectory("log-died");
        try
        {
            // general.strict turns an unknown key from a warning into a rejection (exit 2), which
            // happens before the runtime - and therefore before the control pipe - is ever built.
            string config = Path.Combine(root, "server.toml");
            File.WriteAllText(config, MinimalConfig(port: FreePort()) +
                "\n[general]\nstrict = true\n\n[logging]\nnonsense_key = 1\n");

            string logFile = Path.Combine(root, "logs", "server.log");

            using Process server = StartServer(root, ["serve", "--config", config, "--pipe", NewPipeName()]);
            Assert.True(server.WaitForExit(60_000), "the server did not exit on a rejected configuration");
            Assert.Equal(2, server.ExitCode);

            string text = WaitForLog(logFile, "configuration rejected");

            // Everything it said before it gave up is retrievable, including where its log is, which
            // file it read and why it stopped. None of this was written down at all before the fix.
            Assert.Contains("runtime log: " + InJson(logFile), text, StringComparison.Ordinal);
            Assert.Contains("configuration loaded from " + InJson(config), text, StringComparison.Ordinal);
            Assert.Contains("configuration rejected", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("nonsense_key", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A named <c>logging.file</c> adds a destination; it never removes the default one, which is
    /// the only place the pre-configuration lines exist.
    /// </summary>
    [Fact]
    public void AnExplicitLogFileIsAddedAlongsideTheDefaultAndNotInsteadOfIt()
    {
        string root = NewDirectory("log-explicit");
        Process? server = null;
        try
        {
            string extra = Path.Combine(root, "elsewhere", "extra.log");
            string config = Path.Combine(root, "server.toml");
            File.WriteAllText(config, MinimalConfig(port: FreePort(), logFile: extra));

            string logFile = Path.Combine(root, "logs", "server.log");

            server = StartServer(root, ["serve", "--config", config, "--pipe", NewPipeName()]);

            string text = WaitForLog(logFile, "ServerStarting");
            Assert.Contains("runtime log: " + InJson(logFile), text, StringComparison.Ordinal);

            WaitForLog(extra, "also writing to");
            Assert.True(File.Exists(extra), "the named logging.file was not written");
        }
        finally
        {
            Stop(server);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The template's own <c>[logging] file = ""</c>, which the server must read as "the default
    /// location" rather than "no file".
    /// </summary>
    private static string MinimalConfig(int port, string? logFile = null) =>
        $"""
        [server]
        name = "vht-log-test"

        [transport.plaintext]
        enabled = true
        address = "127.0.0.1"
        port = {port}

        [transport.tls]
        enabled = false

        [capture]
        mode = "auto"
        rebind.enabled = false

        [logging]
        level = "Info"
        file = "{AsTomlPath(logFile)}"
        """;

    /// <summary>
    /// The log is JSON lines, so a Windows path in it carries doubled backslashes. Assertions name
    /// the escaped form rather than the raw path, because the raw one is not what is on disk.
    /// </summary>
    private static string InJson(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    /// <summary>
    /// A Windows path as a TOML basic string. Forward slashes on purpose: the loader strips the
    /// quotes without unescaping, and a TOML basic string would otherwise need every backslash
    /// doubled just to be read back as one - Windows accepts forward slashes and this way the test
    /// asserts against the exact text the assertion names.
    /// </summary>
    private static string AsTomlPath(string? value) =>
        (value ?? string.Empty).Replace('\\', '/');

    /// <summary>The built server binary, which this test project already copies beside itself.</summary>
    private static string ServerExecutable()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "vht-server.exe");
        Assert.True(File.Exists(path), $"the server binary was not found at {path}");
        return path;
    }

    private static Process StartServer(string workingDirectory, string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = ServerExecutable(),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException("the server process could not be started");

        // Drained so a full pipe cannot block the server being tested.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    /// <summary>
    /// Polls the log file - and its rolled generation, in case a previous run filled it - until the
    /// marker line appears. Returns everything read, so a failing assert can show what was there.
    /// </summary>
    private static string WaitForLog(string path, string marker)
    {
        var text = new StringBuilder();
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            text.Clear();
            foreach (string file in new[] { path, path + ".1" })
            {
                if (File.Exists(file))
                {
                    text.Append(ReadShared(file));
                }
            }

            if (text.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return text.ToString();
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            $"'{marker}' did not appear in {path} within 60 s; the file {(File.Exists(path) ? "held" : "was missing")}: " +
            text.ToString());
    }

    /// <summary>
    /// Reads a file another process is still writing: the server holds it <c>FileShare.Read</c>.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Stop(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// A port nothing is listening on, so the test never fights an installed service for 3240.
    /// The bind is the server's problem, not this test's: it only has to be unoccupied.
    /// </summary>
    private static int FreePort() => 39000 + (Math.Abs(Guid.NewGuid().GetHashCode()) % 20000);

    private static string NewPipeName() => "vht-log-accept-" + Guid.NewGuid().ToString("N")[..12];

    private static string NewDirectory(string label)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "vht-server-log-tests", label + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
