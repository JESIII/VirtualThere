// SPDX-License-Identifier: Apache-2.0
//
// test / portable / installer / release / hash / clean — the parts `xtask release` composes.
// Installer/PACKAGING.md describes what each one produces and why.

using System.Globalization;
using System.Text;

namespace Xtask;

/// <summary>`xtask test` — the four test projects. A failure stops the release.</summary>
internal static class TestCommand
{
    internal static Task<int> RunAsync(BuildContext context)
    {
        Console.WriteLine("test: running the test projects");
        int failed = 0;

        foreach (string project in BuildContext.TestProjects)
        {
            Console.WriteLine($"test: {project}");
            RunResult result = context.Run("dotnet", ["test", project, "-c", "Release", "--nologo"], echo: false);

            string summary = Summarize(result.Output);
            Console.WriteLine("  " + summary.Replace("\n", "\n  ", StringComparison.Ordinal));

            if (!result.Ok)
            {
                failed++;
                Console.Error.WriteLine($"  FAILED: {project} exited with {result.ExitCode}");
            }
        }

        if (failed > 0)
        {
            throw new InvalidOperationException($"{failed} test project(s) failed");
        }

        return Task.FromResult(0);
    }

    /// <summary>Pulls the pass/fail lines out of `dotnet test` output instead of echoing all of it.</summary>
    private static string Summarize(string output)
    {
        var lines = new List<string>();
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("Passed!", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("error", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("test files", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Failed:", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(trimmed);
            }
        }

        return lines.Count == 0 ? "(no summary line found in the test output)" : string.Join('\n', lines);
    }
}

/// <summary>`xtask portable` — the four self-contained single-file executables plus their support files.</summary>
internal static class PortableCommand
{
    internal static async Task<int> RunAsync(BuildContext context, Program.Options options)
    {
        if (!BuildContext.IsWindows)
        {
            throw new InvalidOperationException("the VirtualThere release artifacts are Windows-only (win-x64/win-arm64)");
        }

        string rid = BuildContext.HostRid;
        string selfContained = options.FrameworkDependent ? "false" : "true";

        Console.WriteLine($"portable: publishing {BuildContext.Products.Length} products for {rid}, " +
                          $"self-contained={selfContained}, single file, compressed");

        // Start from a clean folder so a stale executable can never end up in a release.
        BuildContext.DeleteDirectory(context.PortableDir);
        BuildContext.EnsureDirectory(context.PortableDir);
        BuildContext.EnsureDirectory(context.PublishDir);

        foreach ((string name, string project) in BuildContext.Products)
        {
            string staging = Path.Combine(context.PublishDir, name);
            BuildContext.DeleteDirectory(staging);

            context.RunChecked("dotnet",
            [
                "publish", project,
                "-c", "Release",
                "-r", rid,
                "--self-contained", selfContained,
                "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:EnableCompressionInSingleFile=true",
                "-p:DebugType=None",
                "-p:DebugSymbols=false",
                "-o", staging,
            ]);

            string exe = Path.Combine(staging, name + ".exe");
            if (!File.Exists(exe))
            {
                throw new InvalidOperationException($"publish did not produce {exe}");
            }

            long size = new FileInfo(exe).Length;
            Console.WriteLine($"  {name}.exe  {BuildContext.Bytes(size)}");
            File.Copy(exe, Path.Combine(context.PortableDir, name + ".exe"), overwrite: true);
        }

        BuildContext.DeleteDirectory(context.PublishDir);
        WriteSupportFiles(context);

        Console.WriteLine($"portable: {context.PortableDir}");
        Program.PrintArtifacts(context, "portable artifacts");
        return await Task.FromResult(0).ConfigureAwait(false);
    }

    /// <summary>
    /// The files a portable folder needs for parity with an installed tree: the marker that keeps
    /// config next to the binaries, the pinned driver package with its notice, the privileged setup
    /// scripts, and this card's packaging document.
    /// </summary>
    internal static void WriteSupportFiles(BuildContext context)
    {
        // portable.marker: Vht.Config/Vht.Client look for this file next to the executable and keep
        // config.toml, peers.json, grants and logs in ./config instead of %LOCALAPPDATA%.
        File.WriteAllText(
            Path.Combine(context.PortableDir, "portable.marker"),
            "VirtualThere portable build marker: config lives in ./config next to these executables.\n",
            Encoding.ASCII);

        // The driver payload, verbatim, with the hash list the install script pins.
        string driverTarget = Path.Combine(context.PortableDir, "driver");
        VerifyThirdPartyHashes(context.ThirdPartyDriverDir);
        foreach (string file in Directory.EnumerateFiles(context.ThirdPartyDriverDir, "*", SearchOption.TopDirectoryOnly))
        {
            BuildContext.CopyFile(file, driverTarget);
        }

        // Scripts: the portable folder gets the whole folder, and Setup-Portable.ps1 also at its root
        // (it resolves the layout either way - see the script's own comment).
        foreach (string script in Directory.EnumerateFiles(context.ScriptsSourceDir, "*.ps1"))
        {
            BuildContext.CopyFile(script, Path.Combine(context.PortableDir, "scripts"));
        }

        BuildContext.CopyFile(
            Path.Combine(context.ScriptsSourceDir, "Setup-Portable.ps1"),
            context.PortableDir);

        BuildContext.CopyFile(
            Path.Combine(context.InstallerSourceDir, "PACKAGING.md"),
            context.PortableDir);
    }

    /// <summary>
    /// Checks every file listed in third_party/usbip-win2-driver/SHA256SUMS before it is packaged.
    /// This is the same pin the installer re-checks at run time, so a tampered or stale vendor
    /// package cannot reach a release artifact in the first place.
    /// </summary>
    internal static void VerifyThirdPartyHashes(string directory)
    {
        string sums = Path.Combine(directory, "SHA256SUMS");
        if (!File.Exists(sums))
        {
            throw new InvalidOperationException($"missing provenance file: {sums}");
        }

        foreach (string line in File.ReadAllLines(sums))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            // sha256sum format: <hash> *<name> or <hash>  <name>
            int split = trimmed.IndexOf(' ', StringComparison.Ordinal);
            if (split <= 0)
            {
                throw new InvalidOperationException($"unparsable line in {sums}: {trimmed}");
            }

            string expected = trimmed[..split];
            string relative = trimmed[(split + 1)..].TrimStart('*', ' ');
            string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"{sums} lists {relative}, which does not exist");
            }

            string actual = BuildContext.HashFile(path);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for {relative}: SHA256SUMS says {expected}, the file is {actual}");
            }
        }

        Console.WriteLine("portable: third_party/usbip-win2-driver verified against its SHA256SUMS");
    }
}

/// <summary>`xtask installer` — the MSI, built with the repo-pinned WiX toolset.</summary>
internal static class InstallerCommand
{
    /// <summary>
    /// Pinned tool version. WiX v5 (MS-RL) is used rather than v6/v7 because those require accepting
    /// the Open Source Maintenance Fee EULA to run at all, which a build tool has no business doing
    /// on a developer's behalf.
    /// </summary>
    internal const string WixVersion = "5.0.2";

    /// <summary>Extensions the MSI source needs; fetched into the WiX cache by `wix extension add`.</summary>
    private static readonly string[] Extensions =
    [
        "WixToolset.Firewall.wixext",   // fire:FirewallException - the two listening-port rules
        "WixToolset.UI.wixext",         // ui:WixUI - the install wizard (no licence page)
        "WixToolset.Util.wixext",       // util:CloseApplication - close a running tray/client
    ];

    internal static async Task<int> RunAsync(BuildContext context, Program.Options options)
    {
        if (!BuildContext.IsWindows)
        {
            throw new InvalidOperationException("the MSI can only be built on Windows");
        }

        foreach ((string name, _) in BuildContext.Products)
        {
            if (!File.Exists(Path.Combine(context.PortableDir, name + ".exe")))
            {
                Console.WriteLine($"installer: {name}.exe is missing from dist/portable; running the portable build first");
                await PortableCommand.RunAsync(context, options).ConfigureAwait(false);
                break;
            }
        }

        Console.WriteLine($"installer: WiX {WixVersion} from installer/dotnet-tools.json");

        // The tool manifest lives in installer/ next to the source, so every wix command runs there.
        context.RunChecked("dotnet", ["tool", "restore"], workingDirectory: context.InstallerSourceDir);

        RunResult listed = context.Run("dotnet", ["wix", "extension", "list", "--global"], echo: false);
        string installed = listed.Output;

        foreach (string extension in Extensions)
        {
            if (installed.Contains(extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine($"installer: fetching {extension}/{WixVersion} into the WiX extension cache");
            context.RunChecked("dotnet", ["wix", "extension", "add", "--global", $"{extension}/{WixVersion}"],
                workingDirectory: context.InstallerSourceDir);
        }

        BuildContext.EnsureDirectory(context.InstallerDir);

        // A stale package or its debug database must never end up in a release: WiX will not
        // overwrite an existing .wixpdb, and `hash` would then publish the old one.
        foreach (string stale in Directory.EnumerateFiles(context.InstallerDir, "*", SearchOption.TopDirectoryOnly))
        {
            if (stale.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                stale.EndsWith(".wixpdb", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(stale);
            }
        }

        string msi = context.MsiPath(context.Version);

        var arguments = new List<string>
        {
            "wix", "build", "VirtualThere.wxs",
            "-arch", "x64",
            "-o", msi,
            "-pdbtype", "none",
            "-d", $"Version={context.Version}",
            "-d", $"BinDir={Native(context.PortableDir)}",
            "-d", $"DriverDir={Native(context.ThirdPartyDriverDir)}",
            "-d", $"ScriptDir={Native(context.ScriptsSourceDir)}",
            "-d", $"DocDir={Native(context.InstallerSourceDir)}",
        };

        foreach (string extension in Extensions)
        {
            arguments.Add("-ext");
            arguments.Add(extension);
        }

        context.RunChecked("dotnet", arguments, workingDirectory: context.InstallerSourceDir);

        if (!File.Exists(msi))
        {
            throw new InvalidOperationException($"WiX reported success but {msi} does not exist");
        }

        Console.WriteLine($"installer: {Path.GetRelativePath(context.RepoRoot, msi)}  {BuildContext.Bytes(new FileInfo(msi).Length)}");
        return 0;
    }

    /// <summary>Forward slashes: WiX accepts them and they survive MSBuild-style path handling.</summary>
    private static string Native(string path) => path.Replace('\\', '/');
}

/// <summary>`xtask release` — everything, in the order that fails fastest.</summary>
internal static class ReleaseCommand
{
    internal static async Task<int> RunAsync(BuildContext context, Program.Options options)
    {
        Console.WriteLine($"release: VirtualThere {context.Version} ({BuildContext.HostRid}) from {context.RepoRoot}");
        BuildContext.EnsureDirectory(context.DistRoot);

        if (options.SkipTests)
        {
            Console.WriteLine("release: --skip-tests given, not running the test projects");
        }
        else
        {
            await TestCommand.RunAsync(context).ConfigureAwait(false);
        }

        await PortableCommand.RunAsync(context, options).ConfigureAwait(false);
        await InstallerCommand.RunAsync(context, options).ConfigureAwait(false);
        int hash = HashCommand.Run(context);

        Program.PrintArtifacts(context, "release artifacts");
        Console.WriteLine();
        Console.WriteLine($"release: manifest  {context.ManifestFile}");
        Console.WriteLine($"release: hashes    {context.SumsFile}");
        Console.WriteLine("release: next step is `xtask verify`, which decompiles the MSI and greps every artifact.");
        return hash;
    }
}

/// <summary>`xtask hash` — dist/release-manifest.txt and dist/SHA256SUMS.</summary>
internal static class HashCommand
{
    internal static int Run(BuildContext context)
    {
        List<Artifact> artifacts = context.CollectArtifacts();
        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException($"no artifacts under {context.DistRoot}; run `xtask release` first");
        }

        var manifest = new StringBuilder();
        manifest.AppendLine("VirtualThere release manifest");
        manifest.AppendLine("Generated by: dotnet run --project tools/Xtask -- release");
        manifest.AppendLine("product: VirtualThere");
        manifest.AppendLine($"version: {context.Version}");
        manifest.AppendLine($"runtime-identifier: {BuildContext.HostRid}");
        manifest.AppendLine("publish: self-contained single file (compressed)");
        manifest.AppendLine($"built-utc: {BuildContext.Now()}");
        manifest.AppendLine($"artifacts: {artifacts.Count.ToString(CultureInfo.InvariantCulture)}");
        manifest.AppendLine();
        manifest.AppendLine($"{"path",-64}{"bytes",14}  sha256");

        long total = 0;
        foreach (Artifact artifact in artifacts)
        {
            total += artifact.Bytes;
            manifest.AppendLine($"{artifact.RelativePath,-64}{artifact.Bytes,14}  {artifact.Sha256}");
        }

        manifest.AppendLine();
        manifest.AppendLine($"total bytes: {total.ToString(CultureInfo.InvariantCulture)} ({BuildContext.Bytes(total)})");
        File.WriteAllText(context.ManifestFile, manifest.ToString(), new UTF8Encoding(false));

        var sums = new StringBuilder();
        foreach (Artifact artifact in artifacts)
        {
            sums.AppendLine($"{artifact.Sha256}  {artifact.RelativePath}");
        }

        File.WriteAllText(context.SumsFile, sums.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"hash: {artifacts.Count.ToString(CultureInfo.InvariantCulture)} artifacts, {BuildContext.Bytes(total)}");
        return 0;
    }
}

/// <summary>`xtask clean` — remove dist/.</summary>
internal static class CleanCommand
{
    internal static int Run(BuildContext context)
    {
        BuildContext.DeleteDirectory(context.DistRoot);
        Console.WriteLine($"clean: removed {context.DistRoot}");
        return 0;
    }
}
