using System.Text;
using System.Xml.Linq;

namespace Vht.Core.Tests;

/// <summary>
/// Makes a missing project a failure instead of a silence: the solution file is the only thing that
/// decides what a solution-wide build and test covers.
/// </summary>
/// <remarks>
/// <para>
/// <c>dotnet build VirtualThere.slnx</c> and <c>dotnet test VirtualThere.slnx</c> succeed for exactly
/// the projects the solution file lists. A project that is absent from it is not reported as absent:
/// it is never built and never run, so a green solution-wide run says nothing about it. That is what
/// happened here — the acceptance harness (<c>tests/Vht.Acceptance</c>) and the parity suite
/// (<c>tests/Vht.Acceptance.Tests</c>) were both missing from the solution, so every green
/// <c>dotnet test VirtualThere.slnx</c> was a statement about the other suites only. The build tool
/// (<c>tools/Xtask</c>) was missing too.
/// </para>
/// <para>
/// This guard is a test rather than a script on purpose: <c>dotnet test VirtualThere.slnx</c> is the
/// command CONTRIBUTING.md and the pull-request template hand a contributor, and the command CI runs,
/// so the check has to live inside that command to be unskippable. Deleting a project from the
/// solution file without deleting it from disk fails here, by name.
/// </para>
/// </remarks>
public sealed class SolutionFileTests
{
    private const string SolutionFileName = "VirtualThere.slnx";

    /// <summary>
    /// Directories whose <c>*.csproj</c> files are not first-party source and so are not expected in
    /// the solution: build output, published output, the captured evidence, the pinned upstream
    /// material, and the reverse-engineering working set (<c>unpacked/</c> and <c>GhidraProj/</c> are
    /// gitignored). Everything else in the repository is in scope.
    /// </summary>
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "bin",
        "obj",
        "dist",
        "artifacts",
        "unpacked",
        "GhidraProj",
        "third_party",
    };

    // =====================================================================================
    // The guard — every project on disk is in the solution, and every solution entry exists
    // =====================================================================================

    [Fact]
    public void EveryProjectOnDiskIsListedInTheSolution()
    {
        string root = RepoRoot.Find();
        string[] onDisk = ProjectsOnDisk(root);
        string[] listed = ProjectsInSolution(root);

        string[] missing = onDisk.Except(listed, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] stale = listed.Except(onDisk, StringComparer.OrdinalIgnoreCase).ToArray();

        Assert.True(
            missing.Length == 0 && stale.Length == 0,
            Describe(root, onDisk, listed, missing, stale));
    }

    /// <summary>
    /// The specific regression this card exists for, pinned by name so it cannot come back quietly
    /// even if the enumeration above is ever loosened.
    /// </summary>
    [Theory]
    [InlineData("tests/Vht.Acceptance/Vht.Acceptance.csproj")]
    [InlineData("tests/Vht.Acceptance.Tests/Vht.Acceptance.Tests.csproj")]
    [InlineData("tools/Xtask/Xtask.csproj")]
    public void TheAcceptanceHarnessAndTheBuildToolAreInTheSolution(string project)
    {
        string root = RepoRoot.Find();

        Assert.True(
            File.Exists(Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar))),
            $"{project} is not on disk under {root}, so this pin is stale.");
        Assert.Contains(project, ProjectsInSolution(root));
    }

    // =====================================================================================
    // Discovery
    // =====================================================================================

    /// <summary>Every first-party project file in the repository, as a repo-relative path.</summary>
    private static string[] ProjectsOnDisk(string root) =>
        Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(root, path))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static bool IsIgnored(string root, string path)
    {
        string[] segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // The last segment is the file name; only the directories it sits in can disqualify it.
        return segments[..^1].Any(segment => IgnoredDirectories.Contains(segment));
    }

    /// <summary>The project paths the solution file lists, normalized to forward slashes.</summary>
    private static string[] ProjectsInSolution(string root)
    {
        string path = Path.Combine(root, SolutionFileName);
        return XDocument.Load(path)
            .Descendants("Project")
            .Select(element => (string?)element.Attribute("Path"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Describe(string root, string[] onDisk, string[] listed, string[] missing, string[] stale)
    {
        var text = new StringBuilder();
        text.AppendLine($"{SolutionFileName} does not match the projects on disk under {root}.");
        text.AppendLine($"projects on disk: {onDisk.Length}; projects listed in the solution: {listed.Length}.");

        if (missing.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("NOT IN THE SOLUTION - `dotnet build/test VirtualThere.slnx` skips these silently:");
            foreach (string path in missing) text.AppendLine($"  - {path}");
        }

        if (stale.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("LISTED IN THE SOLUTION BUT NOT ON DISK:");
            foreach (string path in stale) text.AppendLine($"  - {path}");
        }

        return text.ToString();
    }
}
