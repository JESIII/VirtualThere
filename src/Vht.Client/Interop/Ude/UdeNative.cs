// SPDX-License-Identifier: Apache-2.0
//
// The native calls the client makes against the UDE driver's device interface: open the interface,
// send an IOCTL, read the result. Nothing here knows about USB/IP or policy — UdeDeviceController
// (Vht.Client.Attach) is the layer that does.
//
// Device discovery uses SetupAPI with GUID_DEVINTERFACE_USBIP_VHCI, the interface GUID the driver
// declares (upstream include/usbip/vhci.h). This is the same enumeration the reference products do;
// no shelling out to usbip.exe (ARCHITECTURE.md §3.3).

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vht.Interop.Ude;

/// <summary>P/Invoke declarations and small safe wrappers for the UDE driver interface.</summary>
internal static partial class UdeNative
{
    // SetupAPI
    private const int DigcfPresent = 0x02;
    private const int DigcfDeviceInterface = 0x10;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        internal int CbSize;                 // @0
        internal Guid InterfaceClassGuid;    // @4
        internal int Flags;                  // @20
        internal IntPtr Reserved;            // @24 (8-aligned on x64) → 32 bytes total
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        int memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // Kernel32
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[]? inBuffer,
        int inBufferSize,
        byte[]? outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    /// <summary>
    /// Finds every present device interface path published under
    /// <see cref="UdeConstants.DeviceInterfaceVhci"/>. Returns an empty list (not an error) when the
    /// driver is not installed, because "driver absent" is a state this client must report clearly
    /// and not crash on.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateVhciInterfacePaths()
    {
        var paths = new List<string>();
        Guid guid = UdeConstants.DeviceInterfaceVhci;
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevsW failed");
        }

        try
        {
            var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (int index = 0; ; index++)
            {
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    int error = Marshal.GetLastWin32Error();
                    const int ErrorNoMoreItems = 259;
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }
                    throw new Win32Exception(error, $"SetupDiEnumDeviceInterfaces failed at index {index}");
                }

                // Two-call size query: first to learn the length, then to fetch it.
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required <= 0)
                {
                    continue;
                }

                IntPtr buffer = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8 on 64-bit (the struct is a
                    // DWORD plus a WCHAR[] plus alignment), 6 on 32-bit. Getting this wrong is the
                    // documented way to make this call fail with ERROR_INVALID_USER_BUFFER.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                        if (!string.IsNullOrEmpty(path))
                        {
                            paths.Add(path);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    /// <summary>
    /// Opens a device interface path for IOCTLs. Requires Administrator when the driver enforces it
    /// (it does), so a failure here is expected and reported verbatim rather than retried.
    /// </summary>
    internal static SafeFileHandle OpenForIoctl(string interfacePath)
    {
        SafeFileHandle handle = CreateFileW(
            interfacePath,
            GenericRead | GenericWrite,
            shareMode: 0,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                $"could not open the UDE driver interface '{interfacePath}'. The client needs Administrator " +
                "to load/use the virtual host controller (ARCHITECTURE.md §10 risk 1); unelevated runs must " +
                "fail here with a clear message, not misbehave silently.");
        }

        return handle;
    }

    /// <summary>
    /// Sends a METHOD_BUFFERED IOCTL. Input is optional; when <paramref name="outBuffer"/> is given it
    /// is the buffer the driver writes into (the driver may also modify the input buffer, which is why
    /// the same array is returned for the caller to parse).
    /// </summary>
    internal static int Ioctl(
        SafeFileHandle handle,
        uint ioControlCode,
        byte[]? inBuffer,
        byte[]? outBuffer,
        string operation)
    {
        bool ok = DeviceIoControl(
            handle,
            ioControlCode,
            inBuffer,
            inBuffer?.Length ?? 0,
            outBuffer,
            outBuffer?.Length ?? 0,
            out int bytesReturned,
            IntPtr.Zero);

        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"{operation} failed (IOCTL 0x{ioControlCode:x8})");
        }

        return bytesReturned;
    }
}
