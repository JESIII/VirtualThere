using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Vht.Server.Config;

namespace Vht.Acceptance.Tests;

/// <summary>
/// <c>status</c> reports the runtime log of the configuration it was <b>asked about</b>: both entry
/// points - <c>vht</c> and <c>vht-server</c> - have to honour <c>--config</c> on that line.
/// </summary>
/// <remarks>
/// <para>
/// The defect this covers: <c>ServerCommands.Status</c> resolved the configuration with no argument,
/// so <c>vht status --config X</c> printed the <c>VHT_CONFIG</c> or machine-wide default log while
/// the server it was being asked about logged to <c>X</c>'s directory. The line is the first thing a
/// user reads when nothing answers, so it sent them to another instance's log - exactly the case a
/// portable or second-instance server creates.
/// </para>
/// <para>
/// These run the <b>real</b> executables and read their stdout, because the gap was in the wiring
/// between the argument parser and the shared verb: a unit test of the resolver alone would have
/// passed all along. <c>VHT_CONFIG</c> is set to a different directory in the explicit-path test, so
/// "wins" is measured against something that would otherwise be printed.
/// </para>
/// </remarks>
public sealed class StatusLogPathTests
{
    [Theory]
    [InlineData("vht.exe")]
    [InlineData("vht-server.exe")]
    public void AnExplicitConfigPathDecidesTheRuntimeLogStatusPrints(string executable)
    {
        string root = NewDirectory("status-config");
        try
        {
            string chosen = Path.Combine(root, "chosen");
            string environment = Path.Combine(root, "environment");
            string config = WriteConfig(chosen);

            string output = Run(executable, ["status", "--config", config], environmentConfig: WriteConfig(environment));

            Assert.Equal(
                Path.Combine(chosen, "logs", "server.log"),
                RuntimeLogLine(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("vht.exe")]
    [InlineData("vht-server.exe")]
    public void TheEnvironmentStillDecidesTheRuntimeLogWhenNoPathIsGiven(string executable)
    {
        string root = NewDirectory("status-env");
        try
        {
            string environment = Path.Combine(root, "environment");
            string output = Run(executable, ["status"], environmentConfig: WriteConfig(environment));

            Assert.Equal(
                Path.Combine(environment, "logs", "server.log"),
                RuntimeLogLine(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The path on the <c>runtime log:</c> line - the sentence the defect got wrong.</summary>
    private static string RuntimeLogLine(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("runtime log:", StringComparison.Ordinal))
            {
                string path = trimmed["runtime log:".Length..].Trim();
                Assert.True(Path.IsPathRooted(path), $"the log line did not name a path: '{trimmed}'");
                return path;
            }
        }

        throw new InvalidOperationException("status printed no 'runtime log:' line; its output was:\n" + output);
    }

    /// <summary>
    /// Runs a role executable's <c>status</c> verb with stdout captured. The verb exits 3 when no
    /// server answers, which is expected here - the log line is printed before the pipe is tried, so
    /// the exit code is deliberately not asserted.
    /// </summary>
    private static string Run(string executable, string[] arguments, string? environmentConfig)
    {
        string path = Path.Combine(AppContext.BaseDirectory, executable);
        Assert.True(File.Exists(path), $"{executable} was not found at {path}");

        var info = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // A unique pipe so a server that happens to be running on this machine is never the one
        // this test queries - the log line must not depend on whether anything answers.
        info.ArgumentList.Add(arguments[0]);
        info.Environment["VHT_CONFIG"] = environmentConfig ?? string.Empty;
        for (int i = 1; i < arguments.Length; i++)
        {
            info.ArgumentList.Add(arguments[i]);
        }

        if (!arguments.Contains("--pipe"))
        {
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add("vht-status-accept-" + Guid.NewGuid().ToString("N")[..12]);
        }

        if (environmentConfig is null)
        {
            info.Environment.Remove("VHT_CONFIG");
        }

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException($"{executable} could not be started");

        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Assert.True(process.WaitForExit(60_000), $"{executable} status did not exit within 60 s");
        process.WaitForExit();

        return stdout.ToString();
    }

    /// <summary>A minimal, valid configuration in <paramref name="directory"/>; its path is returned.</summary>
    private static string WriteConfig(string directory)
    {
        Directory.CreateDirectory(directory);
        string config = Path.Combine(directory, ServerConfigLoader.FileName);
        File.WriteAllText(config, """
            [server]
            name = "status-acceptance"
            """);
        return config;
    }

    private static string NewDirectory(string label)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "vht-status-accept", label + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
