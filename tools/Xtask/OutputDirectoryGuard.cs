// SPDX-License-Identifier: Apache-2.0
//
// Replacing an artifact directory that is still in use.
//
// `xtask portable` used to delete dist/portable and then publish into it. A `vht-server.exe`
// started from that folder (the documented portable workflow) keeps its own image open, so the
// delete removed everything it could, the first File.Copy then failed on the one locked file, and
// the release folder was left holding a stale executable and nothing else.
//
// Nothing here is allowed to touch a live artifact directory until it has proved that every file
// in it can be replaced, and the new set is assembled elsewhere and swapped in (see ArtifactSwap).

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Xtask;

/// <summary>A process that keeps a file in an artifact directory open.</summary>
internal sealed record DirectoryHolder(int Pid, string Name, string? ImagePath)
{
    /// <summary>Process name and pid always; the image path when the OS lets us read it.</summary>
    internal string Describe() =>
        ImagePath is null ? $"{Name} (pid {Pid})" : $"{Name} (pid {Pid}, {ImagePath})";
}

/// <summary>Detects that an output directory cannot be replaced, before anything is deleted.</summary>
internal static partial class OutputDirectoryGuard
{
    /// <summary>
    /// The executables `xtask portable` publishes. A lock in dist/portable is attributed to one of
    /// these: they are the processes that run *from* the folder being replaced. The image path is
    /// checked when the OS will give it to us, so a copy that is installed elsewhere (Program
    /// Files, a service) is not blamed for a lock it does not hold.
    /// </summary>
    private static readonly string[] ShippedProcessNames = ["vht-server", "vht-client", "vht"];

    /// <summary>
    /// Throws unless every file under <paramref name="directory"/> can be deleted and rewritten.
    /// Call it before the first destructive step: it is the difference between "refused, your folder
    /// is untouched" and "destroyed, then failed".
    /// </summary>
    internal static void EnsureReplaceable(string directory, string description)
    {
        IReadOnlyList<string> locked = LockedFiles(directory);
        if (locked.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(Refusal(description, locked, Holders(directory)));
    }

    /// <summary>Throws when a single output file (the MSI, its symbols) is open and cannot be replaced.</summary>
    internal static void EnsureFileReplaceable(string file)
    {
        if (!File.Exists(file) || !IsLocked(file))
        {
            return;
        }

        string? parent = Path.GetDirectoryName(file);
        throw new InvalidOperationException(
            Refusal(file, [file], parent is null ? [] : Holders(parent)));
    }

    /// <summary>
    /// Every file under <paramref name="directory"/> that another process holds open. Returns an
    /// empty list for a directory that does not exist, so a first build is never a special case.
    /// </summary>
    internal static IReadOnlyList<string> LockedFiles(string directory)
    {
        var locked = new List<string>();
        if (!Directory.Exists(directory))
        {
            return locked;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (IsLocked(file))
            {
                locked.Add(file);
            }
        }

        return locked;
    }

    /// <summary>
    /// True when the file cannot be opened exclusively, i.e. somebody else has it open. A running
    /// executable's image is opened by the loader without write access, so this is exactly the
    /// condition that made the old build destroy the folder and then fail on its first File.Copy:
    /// the folder is not replaceable while it is true. A file we are not allowed to read at all
    /// counts as locked too - the publish would fail on it either way.
    /// </summary>
    internal static bool IsLocked(string file)
    {
        try
        {
            using FileStream probe = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The shipped processes that could be holding <paramref name="directory"/> open: the ones whose
    /// image lives in it, plus the ones whose image we cannot read (an elevated worker started by a
    /// window, asked from an unelevated shell) - those cannot be ruled out.
    /// </summary>
    internal static IReadOnlyList<DirectoryHolder> Holders(string directory)
    {
        var holders = new List<DirectoryHolder>();

        foreach (string name in ShippedProcessNames)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? image = ImagePath(process);
                    if (image is not null && !IsUnder(image, directory))
                    {
                        continue;
                    }

                    holders.Add(new DirectoryHolder(process.Id, name + ".exe", image));
                }
            }
        }

        holders.Sort(static (left, right) => left.Pid.CompareTo(right.Pid));
        return holders;
    }

    /// <summary>
    /// The refusal message. Separate from the check so the wording is testable without a process to
    /// point at, and so the entry in docs/TROUBLESHOOTING.md and the message the operator sees are
    /// the same text.
    /// </summary>
    internal static string Refusal(
        string description,
        IReadOnlyList<string> lockedFiles,
        IReadOnlyList<DirectoryHolder> holders)
    {
        var message = new StringBuilder();
        message.AppendLine($"{description} is in use and cannot be replaced safely.");
        message.AppendLine();

        foreach (string file in lockedFiles)
        {
            message.AppendLine($"  locked: {file}");
        }

        if (holders.Count == 0)
        {
            message.AppendLine("  holder: not attributable to a VirtualThere process (it may be elevated, " +
                               "or it may be another program - the locked file is the answer)");
        }
        else
        {
            foreach (DirectoryHolder holder in holders)
            {
                message.AppendLine($"  holder: {holder.Describe()}");
            }
        }

        message.AppendLine();
        message.AppendLine("  A server or client started from this folder keeps its own executable open, so a");
        message.AppendLine("  publish would delete the folder and then fail on the locked file. Nothing has been");
        message.AppendLine("  deleted: the folder is byte-for-byte what it was. See docs/TROUBLESHOOTING.md.");
        message.AppendLine();

        if (holders.Count > 0)
        {
            message.AppendLine("  Stop the process and run the command again (elevated shell if it is elevated):");
            foreach (DirectoryHolder holder in holders)
            {
                message.AppendLine($"      taskkill /pid {holder.Pid} /t /f");
            }
        }
        else
        {
            message.AppendLine("  Close whatever holds the locked file, then run the command again.");
        }

        return message.ToString().TrimEnd();
    }

    /// <summary>True when <paramref name="path"/> is the directory itself or below it.</summary>
    private static bool IsUnder(string path, string directory)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The image path of a process: the module list when the OS lets us read it (same integrity
    /// level), otherwise the kernel query, which a same-user process may make even when the target
    /// is elevated. Null when both are refused - the caller still has the name and the pid.
    /// </summary>
    private static string? ImagePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return QueryFullImagePath(process.Id);
        }
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr process, int flags, [Out] char[] buffer, ref int size);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    private static string? QueryFullImagePath(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0
                ? new string(buffer, 0, size)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
