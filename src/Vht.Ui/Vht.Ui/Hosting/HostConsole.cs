using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Vht.Ui.Hosting;

/// <summary>
/// Console plumbing for the two role executables, which are <c>WinExe</c> processes: they must
/// never show a console window, but their console verbs still have to print when a caller with a
/// console asks for them.
/// </summary>
/// <remarks>
/// A GUI-subsystem process started from a shell inherits that shell's standard handles, so
/// <see cref="Console.Out"/> already works in the common case; the attach below only matters when
/// the process was started without them (a shortcut, <c>Start-Process</c>) and the caller still
/// expects the output on their console. It never allocates a console, so nothing can make a window
/// appear.
/// </remarks>
public static class HostConsole
{
    private const uint StdOutputHandle = unchecked((uint)-11);
    private const uint StdErrorHandle = unchecked((uint)-12);
    private const nint InvalidHandleValue = -1;
    private const int AttachParentProcess = -1;

    /// <summary>
    /// Makes <see cref="Console.Out"/> usable for a verb invocation: keeps an inherited or
    /// redirected handle, otherwise attaches to the parent's console. Returns true when the console
    /// is usable afterwards - false means the caller has nowhere to print and should fall back to a
    /// dialog.
    /// </summary>
    public static bool EnsureForVerbs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (HasUsableHandle(StdOutputHandle) || HasUsableHandle(StdErrorHandle))
        {
            return true;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            return false;
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        return true;
    }

    private static bool HasUsableHandle(uint which)
    {
        nint handle = GetStdHandle(which);
        return handle != 0 && handle != InvalidHandleValue;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(uint which);
}
