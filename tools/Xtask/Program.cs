// SPDX-License-Identifier: Apache-2.0
//
// Xtask — VirtualThere's one build command (ARCHITECTURE.md §9). Owner card: t_428614c4.
//
//   dotnet run --project tools/Xtask -- release     every release artifact, from a clean checkout
//   dotnet run --project tools/Xtask -- portable    just dist/portable (self-contained single files)
//   dotnet run --project tools/Xtask -- installer   just the MSI (needs dist/portable or builds it)
//   dotnet run --project tools/Xtask -- test        the test projects
//   dotnet run --project tools/Xtask -- verify      decompile the MSI + scan artifacts for forbidden strings
//   dotnet run --project tools/Xtask -- hash        rewrite dist/release-manifest.txt + dist/SHA256SUMS
//   dotnet run --project tools/Xtask -- clean       remove dist/
//
// Nothing here is shipped: Xtask is a build-time tool (Appendix D lists WiX the same way).

using System.Globalization;

namespace Xtask;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("xtask: " + ex.Message);
            Console.Error.WriteLine("try: dotnet run --project tools/Xtask -- help");
            return 2;
        }

        try
        {
            BuildContext context = BuildContext.Discover();
            return options.Verb switch
            {
                "release" => await ReleaseCommand.RunAsync(context, options).ConfigureAwait(false),
                "portable" => await PortableCommand.RunAsync(context, options).ConfigureAwait(false),
                "installer" => await InstallerCommand.RunAsync(context, options).ConfigureAwait(false),
                "test" => await TestCommand.RunAsync(context).ConfigureAwait(false),
                "verify" => await VerifyCommand.RunAsync(context).ConfigureAwait(false),
                "hash" => HashCommand.Run(context),
                "clean" => CleanCommand.Run(context),
                "version" => Version(context),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(options.Verb),
            };
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("xtask: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked or unwritable path that got past the pre-flight checks. Reports the operating
            // system's own description of the path it refused, with the exit code a script expects,
            // instead of an unhandled exception's stack trace and Windows' 0xE0434352.
            Console.Error.WriteLine("xtask: " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine("xtask: the build stopped before it finished; check what still holds that path.");
            return 1;
        }
    }

    private static int Version(BuildContext context)
    {
        Console.WriteLine($"VirtualThere {context.Version} — xtask ({BuildContext.HostRid})");
        Console.WriteLine($"repo: {context.RepoRoot}");
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"xtask: unknown verb '{verb}'");
        return Help();
    }

    private static int Help()
    {
        Console.WriteLine("""
            xtask — the VirtualThere release build.

            usage: dotnet run --project tools/Xtask -- <verb> [options]

            verbs:
              release      tests, portable build, MSI, then the artifact manifest (all of it)
              portable     publish the single-file executables into dist/portable
              installer    build dist/installer/VirtualThere-<version>-x64.msi with WiX
              test         run the test projects
              verify       decompile the MSI, check its tables, and scan every artifact for
                           forbidden strings (the reference product's name, licence/purchase vocabulary, device caps)
              hash         rewrite dist/release-manifest.txt and dist/SHA256SUMS
              clean        delete dist/
              version      print the version and the repo root
              help         this text

            options:
              --skip-tests             release: package without running the test projects
              --framework-dependent    publish against an installed .NET runtime instead of
                                       self-contained (smaller, needs the .NET 10 Desktop Runtime)
              -v, --verbose            echo every child command (on by default)
            """);
        return 0;
    }

    /// <summary>Parsed command line. Deliberately tiny: this is a build script, not a product.</summary>
    internal sealed class Options
    {
        internal string Verb { get; private set; } = "help";

        internal bool SkipTests { get; private set; }

        internal bool FrameworkDependent { get; private set; }

        internal bool Verbose { get; private set; } = true;

        internal static Options Parse(string[] args)
        {
            var options = new Options();
            bool verbSeen = false;

            foreach (string arg in args)
            {
                switch (arg)
                {
                    case "--skip-tests":
                        options.SkipTests = true;
                        break;
                    case "--framework-dependent":
                        options.FrameworkDependent = true;
                        break;
                    case "-v":
                    case "--verbose":
                        options.Verbose = true;
                        break;
                    case "-q":
                    case "--quiet":
                        options.Verbose = false;
                        break;
                    case "help" or "--help" or "-h":
                        options.Verb = "help";
                        verbSeen = true;
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                        {
                            throw new ArgumentException("unknown option '" + arg + "'");
                        }

                        if (verbSeen)
                        {
                            throw new ArgumentException(
                                "unexpected argument '" + arg + "' (one verb per invocation)");
                        }

                        options.Verb = arg;
                        verbSeen = true;
                        break;
                }
            }

            return options;
        }
    }

    /// <summary>Prints the artifact table both `release` and `portable` end with.</summary>
    internal static void PrintArtifacts(BuildContext context, string heading)
    {
        List<Artifact> artifacts = context.CollectArtifacts();
        long total = 0;

        Console.WriteLine();
        Console.WriteLine(heading);
        Console.WriteLine(new string('-', 100));
        Console.WriteLine($"{"artifact",-64}{"size",12}  sha256");

        foreach (Artifact artifact in artifacts)
        {
            total += artifact.Bytes;
            Console.WriteLine(
                $"{artifact.RelativePath,-64}{BuildContext.Bytes(artifact.Bytes),12}  {artifact.Sha256[..16]}…");
        }

        Console.WriteLine(new string('-', 100));
        Console.WriteLine(
            $"{artifacts.Count.ToString(CultureInfo.InvariantCulture)} files, " +
            $"{BuildContext.Bytes(total)} total under {context.DistRoot}");
    }
}
