using System.Runtime.InteropServices;

namespace Vht.Ui;

/// <summary>
/// The only P/Invoke in the tray application: window activation and icon cleanup.
/// Explicitly no SetupAPI / WinUSB / driver interop here — the UI never touches devices
/// (docs/ARCHITECTURE.md section 1).
/// </summary>
internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    internal const int SwRestore = 9;
}
