// SPDX-License-Identifier: Apache-2.0
//
// Build context: where the repo is, what version we are building, where artifacts go, and how to
// run child processes. Everything the commands need to know about the machine lives here.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Xtask;

/// <summary>Result of a child process.</summary>
internal sealed record RunResult(int ExitCode, string Output)
{
    internal bool Ok => ExitCode == 0;
}

/// <summary>One file in the release output.</summary>
internal sealed record Artifact(string RelativePath, string FullPath, long Bytes, string Sha256);

/// <summary>Paths, version and process helpers for the release build.</summary>
internal sealed class BuildContext
{
    private BuildContext(string repoRoot)
    {
        RepoRoot = repoRoot;
        Version = ReadVersion(repoRoot);
        DistRoot = Path.Combine(repoRoot, "dist");
        PortableDir = Path.Combine(DistRoot, "portable");
        InstallerDir = Path.Combine(DistRoot, "installer");
        VerifyDir = Path.Combine(DistRoot, "verify");
        PublishDir = Path.Combine(DistRoot, "publish");
        ManifestFile = Path.Combine(DistRoot, "release-manifest.txt");
        SumsFile = Path.Combine(DistRoot, "SHA256SUMS");
        ThirdPartyDriverDir = Path.Combine(repoRoot, "third_party", "usbip-win2-driver");
        InstallerSourceDir = Path.Combine(repoRoot, "installer");
        ScriptsSourceDir = Path.Combine(InstallerSourceDir, "scripts");
    }

    internal string RepoRoot { get; }

    internal string Version { get; }

    internal string DistRoot { get; }

    internal string PortableDir { get; }

    internal string InstallerDir { get; }

    internal string VerifyDir { get; }

    internal string PublishDir { get; }

    internal string ManifestFile { get; }

    internal string SumsFile { get; }

    internal string ThirdPartyDriverDir { get; }

    internal string InstallerSourceDir { get; }

    internal string ScriptsSourceDir { get; }

    internal string MsiPath(string version) =>
        Path.Combine(InstallerDir, $"VirtualThere-{version}-x64.msi");

    /// <summary>
    /// The shipped binaries, in the order they are listed to the operator. Two of them are windows:
    /// <c>vht-server.exe</c> opens the server window and <c>vht-client.exe</c> the client window,
    /// each hosting its own privileged worker. <c>vht.exe</c> is the scripting surface. There is no
    /// separate tray binary any more.
    /// </summary>
    internal static readonly (string Name, string Project)[] Products =
    [
        ("vht-server", "src/Vht.Server/Vht.Server.csproj"),
        ("vht-client", "src/Vht.Client/Vht.Client.csproj"),
        ("vht", "src/Vht.Cli/Vht.Cli.csproj"),
    ];

    /// <summary>The test projects `release` runs before it packages anything.</summary>
    internal static readonly string[] TestProjects =
    [
        "tests/Vht.Core.Tests/Vht.Core.Tests.csproj",
        "tests/Vht.Config.Tests/Vht.Config.Tests.csproj",
        "tests/Vht.Transport.Tests/Vht.Transport.Tests.csproj",
        "tests/Vht.Ui.Tests/Vht.Ui.Tests.csproj",
        "src/Vht.Client/Tests/Vht.Client.Tests.csproj",
    ];

    /// <summary>Walks up from the running assembly to the directory holding the solution marker.</summary>
    internal static BuildContext Discover()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VirtualThere.slnx")))
            {
                return new BuildContext(dir.FullName);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "could not find the repository root (no VirtualThere.slnx above " + AppContext.BaseDirectory + ")");
    }

    /// <summary>
    /// One version for every binary and for the installer, read from Directory.Build.props — the
    /// same source MSBuild uses for the assemblies (ARCHITECTURE.md §9 "Versioning").
    /// </summary>
    private static string ReadVersion(string repoRoot)
    {
        string props = Path.Combine(repoRoot, "Directory.Build.props");
        if (!File.Exists(props))
        {
            throw new InvalidOperationException("Directory.Build.props not found at " + props);
        }

        XDocument document = XDocument.Load(props);
        string? version = document.Root?
            .Elements("PropertyGroup")
            .Elements("Version")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => value.Length > 0);

        return string.IsNullOrWhiteSpace(version)
            ? throw new InvalidOperationException($"<Version> is missing from {props}")
            : version;
    }

    // ---- process helpers ---------------------------------------------------------------------

    /// <summary>Runs a child process, echoing its output. Never throws on a non-zero exit code.</summary>
    internal RunResult Run(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        bool echo = true,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? RepoRoot,
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        if (echo)
        {
            Console.WriteLine($"  $ {fileName} {string.Join(' ', info.ArgumentList.Select(Quote))}");
        }

        using var process = new Process { StartInfo = info };
        var output = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);

        void Collect(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
            }

            if (echo)
            {
                Console.WriteLine("    | " + line);
            }
        }

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        return new RunResult(process.ExitCode, output.ToString());
    }

    /// <summary>Runs a child process and throws when it fails.</summary>
    internal RunResult RunChecked(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        RunResult result = Run(fileName, arguments, workingDirectory, echo: true, environment);
        if (!result.Ok)
        {
            throw new InvalidOperationException(
                $"{fileName} exited with {result.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        }

        return result;
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOf(' ', StringComparison.Ordinal) < 0 ? argument : '"' + argument + '"';

    // ---- filesystem helpers ------------------------------------------------------------------

    internal static void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    internal static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    internal static void CopyFile(string source, string destinationDirectory, string? destinationName = null)
    {
        EnsureDirectory(destinationDirectory);
        File.Copy(source, Path.Combine(destinationDirectory, destinationName ?? Path.GetFileName(source)), overwrite: true);
    }

    // ---- hashing -----------------------------------------------------------------------------

    internal static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Every release artifact, with size and SHA-256, sorted by relative path.</summary>
    internal List<Artifact> CollectArtifacts()
    {
        var artifacts = new List<Artifact>();
        foreach (string path in Directory.EnumerateFiles(DistRoot, "*", SearchOption.AllDirectories))
        {
            // The generated manifest and hash list are not artifacts of the build; neither is
            // anything else named the same, so compare full paths rather than file names.
            // dist/verify/ is `xtask verify`'s scratch area — the decompiled MSI source and the files
            // it extracted. It is not a release artifact, and hashing it here would put its contents
            // into the manifest when `verify` is run before `hash`.
            if (path.StartsWith(VerifyDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, ManifestFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, SumsFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(path);
            artifacts.Add(new Artifact(
                Path.GetRelativePath(RepoRoot, path).Replace('\\', '/'),
                path,
                info.Length,
                HashFile(path)));
        }

        artifacts.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return artifacts;
    }

    // ---- environment -------------------------------------------------------------------------

    internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    internal static string HostRid =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    internal static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    internal static string Bytes(long value) =>
        value >= 1024L * 1024 * 1024
            ? (value / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB"
            : value >= 1024L * 1024
                ? (value / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
                : value >= 1024
                    ? (value / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KiB"
                    : value.ToString(CultureInfo.InvariantCulture) + " B";
}
