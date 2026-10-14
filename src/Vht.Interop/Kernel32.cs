using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vht.Interop;

/// <summary>
/// Kernel32 entry points the capture path needs: opening a device interface by path and
/// closing it. Overlapped I/O is done through <c>SafeFileHandle</c>, which the runtime
/// already wraps correctly.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class Kernel32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    public const int ERROR_SEM_TIMEOUT = 121;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_GEN_FAILURE = 31;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_CRC = 23;
    public const int ERROR_IO_PENDING = 997;

    public const int WAIT_OBJECT_0 = 0;
    public const int WAIT_TIMEOUT = 258;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "CancelIoEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, nint lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLastError")]
    internal static partial uint GetLastError();

    [LibraryImport("kernel32.dll", EntryPoint = "GetOverlappedResult", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetOverlappedResult(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, nint lpOverlapped,
        out uint lpNumberOfBytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool bWait);
}
