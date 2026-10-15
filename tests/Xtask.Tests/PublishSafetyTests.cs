// SPDX-License-Identifier: Apache-2.0
//
// `xtask portable` destroyed a working portable install: it deleted dist/portable and then failed on
// the one file it could not delete (a vht-server.exe running from that folder), leaving a stale
// executable and nothing else. These tests pin the two properties that make that impossible:
//
//   1. an artifact directory that is in use is refused *before* anything is deleted, with the
//      process and pid named;
//   2. the new set is assembled elsewhere and swapped in, so a failure at any point leaves the
//      previous set complete.
//
// Only the pieces are exercised here; the command-level behaviour (a real vht-server.exe running
// from dist/portable, a publish failure part way through) is what the card demonstrates by hand
// against the built tool.

using Xunit;

namespace Xtask.Tests;

public sealed class PublishSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "xtask-tests", Guid.NewGuid().ToString("n"));

    public PublishSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A test failed while a probe was still open; the temp folder is the OS's problem.
        }
    }

    // ---- the guard --------------------------------------------------------------------------

    [Fact]
    public void AFileHeldOpenExclusivelyBlocksTheDirectory()
    {
        string directory = NewDirectory("portable");
        string file = NewFile(directory, "vht-server.exe", "old build");

        using FileStream held = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.True(OutputDirectoryGuard.IsLocked(file));
        Assert.Equal([file], OutputDirectoryGuard.LockedFiles(directory));

        InvalidOperationException refusal =
            Assert.Throws<InvalidOperationException>(() => OutputDirectoryGuard.EnsureReplaceable(directory, "dist/portable"));

        Assert.Contains("dist/portable is in use", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(file, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("byte-for-byte", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileNobodyHoldsDoesNotBlockTheDirectory()
    {
        string directory = NewDirectory("portable");
        NewFile(directory, "vht-server.exe", "old build");
        NewFile(directory, "scripts/Setup-Portable.ps1", "param()");

        Assert.Empty(OutputDirectoryGuard.LockedFiles(directory));
        OutputDirectoryGuard.EnsureReplaceable(directory, "dist/portable");
    }

    [Fact]
    public void TheLockIsGoneOnceTheHolderIsGone()
    {
        string directory = NewDirectory("portable");
        string file = NewFile(directory, "vht-client.exe", "old build");

        using (FileStream held = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(OutputDirectoryGuard.IsLocked(file));
        }

        Assert.False(OutputDirectoryGuard.IsLocked(file));
        Assert.Empty(OutputDirectoryGuard.LockedFiles(directory));
    }

    [Fact]
    public void AMissingDirectoryIsNeverBlocked()
    {
        // A first build, or a `clean` before anything has been built.
        string missing = Path.Combine(_root, "dist", "portable");

        Assert.Empty(OutputDirectoryGuard.LockedFiles(missing));
        OutputDirectoryGuard.EnsureReplaceable(missing, "dist/portable");
    }

    [Fact]
    public void TheRefusalNamesTheProcessAndItsPid()
    {
        string lockedFile = Path.Combine(_root, "dist", "portable", "vht-server.exe");

        string message = OutputDirectoryGuard.Refusal(
            "dist/portable",
            [lockedFile],
            [new DirectoryHolder(45184, "vht-server.exe", lockedFile)]);

        Assert.Contains($"holder: vht-server.exe (pid 45184, {lockedFile})", message, StringComparison.Ordinal);
        Assert.Contains("locked: " + lockedFile, message, StringComparison.Ordinal);
        Assert.Contains("taskkill /pid 45184 /t /f", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalSaysSoWhenTheHolderCannotBeAttributed()
    {
        // An elevated worker asked from an unelevated shell: the name and pid are not readable, so the
        // message must not pretend to know them, and must still refuse.
        string message = OutputDirectoryGuard.Refusal(
            "dist/portable",
            [Path.Combine(_root, "dist", "portable", "vht-server.exe")],
            []);

        Assert.Contains("not attributable", message, StringComparison.Ordinal);
        Assert.Contains("Close whatever holds the locked file", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteReportsTheLockedFileInsteadOfThrowingUnauthorizedAccess()
    {
        string directory = NewDirectory("portable");
        string file = NewFile(directory, "vht-server.exe", "old build");
        using FileStream held = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(() => BuildContext.DeleteDirectory(directory));

        Assert.Contains("locked: " + file, failure.Message, StringComparison.Ordinal);
        Assert.Contains("incomplete", failure.Message, StringComparison.Ordinal);
    }

    // ---- the swap ---------------------------------------------------------------------------

    [Fact]
    public void TheSwapReplacesTheSetCompletely()
    {
        string destination = NewDirectory("portable");
        NewFile(destination, "vht-server.exe", "previous");
        NewFile(destination, "portable.marker", "previous");
        NewFile(destination, "scripts/Setup-Portable.ps1", "previous");

        string staging = NewDirectory("publish/portable");
        NewFile(staging, "vht-server.exe", "new");
        NewFile(staging, "vht-client.exe", "new");
        NewFile(staging, "portable.marker", "new");
        NewFile(staging, "driver/winusbip.sys", "new");

        string previous = Path.Combine(_root, "publish", "portable.previous");
        ArtifactSwap.Replace(staging, destination, previous, "dist/portable");

        Assert.Equal(["portable.marker", "vht-client.exe", "vht-server.exe"], FileNames(destination));
        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "portable.marker")));
        Assert.True(File.Exists(Path.Combine(destination, "driver", "winusbip.sys")));
        Assert.False(File.Exists(Path.Combine(destination, "scripts", "Setup-Portable.ps1")));
        Assert.False(Directory.Exists(staging));
        Assert.False(Directory.Exists(previous));
    }

    [Fact]
    public void TheSwapCreatesTheDestinationWhenThereIsNoPreviousSet()
    {
        string destination = Path.Combine(_root, "dist", "portable");
        string staging = NewDirectory("publish/portable");
        NewFile(staging, "vht.exe", "new");

        ArtifactSwap.Replace(staging, destination, Path.Combine(_root, "publish", "portable.previous"), "dist/portable");

        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "vht.exe")));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void AFailedSwapLeavesThePreviousSetInPlace()
    {
        string destination = NewDirectory("portable");
        NewFile(destination, "vht-server.exe", "previous");
        NewFile(destination, "portable.marker", "previous");

        // The staged set is not there: whatever removed it (a full disk, a crash while staging) this is
        // the moment the old build would already have destroyed dist/portable.
        string missingStaging = Path.Combine(_root, "publish", "portable");
        string previous = Path.Combine(_root, "publish", "portable.previous");

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => ArtifactSwap.Replace(missingStaging, destination, previous, "dist/portable"));

        Assert.Contains("moved back", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["portable.marker", "vht-server.exe"], FileNames(destination));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(destination, "vht-server.exe")));
        Assert.False(Directory.Exists(previous));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private string NewDirectory(string relative)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string NewFile(string directory, string relative, string contents)
    {
        string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string[] FileNames(string directory) =>
        Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>().Order().ToArray();
}
