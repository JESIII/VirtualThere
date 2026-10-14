using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vht.Interop;

/// <summary>
/// WinUSB: the user-mode I/O surface for a claimed device.
/// </summary>
/// <remarks>
/// This is the P/Invoke set the server's capture path needs, matching the reference
/// product's use of <c>WINUSB.DLL</c>, <c>WinUsb_QueryDeviceInformation</c> and
/// <c>WinUsb_Free</c> (RECON_AND_SPEC.md §2) — without a filter driver in front of it,
/// which is the deliberate v1 simplification (ARCHITECTURE.md §4).
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class WinUsb
{
    /// <summary>Information types for <c>WinUsb_QueryDeviceInformation</c>.</summary>
    public const uint DEVICE_SPEED = 0x01;
    public const uint RAW_DEVICE_DESCRIPTOR = 0x02;
    public const uint RAW_CURRENT_CONFIGURATION_DESCRIPTOR = 0x03;

    /// <summary>Pipe policies used by the capture path.</summary>
    public const uint AUTO_CLEAR_STALL = 1;
    public const uint PIPE_TRANSFER_TIMEOUT = 3;
    public const uint ALLOW_PARTIAL_READS = 5;
    public const uint RAW_IO = 7;

    /// <summary>Write-policy flag: <c>ALLOW_PARTIAL_READS</c> / <c>SHORT_PACKET_TERMINATE</c>.</summary>
    public const byte TRUE = 1;

    public enum USBD_PIPE_TYPE
    {
        Control = 0,
        Isochronous = 1,
        Bulk = 2,
        Interrupt = 3,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WINUSB_SETUP_PACKET
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct USB_DEVICE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass;
        public byte bDeviceSubClass;
        public byte bDeviceProtocol;
        public byte bMaxPacketSize0;
        public ushort idVendor;
        public ushort idProduct;
        public ushort bcdDevice;
        public byte iManufacturer;
        public byte iProduct;
        public byte iSerialNumber;
        public byte bNumConfigurations;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WINUSB_PIPE_INFORMATION
    {
        public USBD_PIPE_TYPE PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_Initialize", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_Initialize(SafeFileHandle deviceHandle, out nint interfaceHandle);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_Free", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_Free(nint interfaceHandle);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_QueryDeviceInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_QueryDeviceInformation(
        nint interfaceHandle, uint informationType, ref uint bufferLength, nint buffer);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_QueryInterfaceSettings", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_QueryInterfaceSettings(
        nint interfaceHandle, byte alternateInterfaceNumber, out USB_INTERFACE_DESCRIPTOR descriptor);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_QueryPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_QueryPipe(
        nint interfaceHandle, byte alternateInterfaceNumber, byte pipeIndex, out WINUSB_PIPE_INFORMATION pipeInfo);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ControlTransfer", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ControlTransfer(
        nint interfaceHandle, WINUSB_SETUP_PACKET setupPacket, nint buffer,
        uint bufferLength, out uint lengthTransferred, nint overlapped);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_GetDescriptor", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_GetDescriptor(
        nint interfaceHandle, byte descriptorType, byte index, ushort languageId,
        nint buffer, uint bufferLength, out uint lengthTransferred);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ReadPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ReadPipe(
        nint interfaceHandle, byte pipeId, nint buffer, uint bufferLength,
        nint lengthTransferred, nint overlapped);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_WritePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_WritePipe(
        nint interfaceHandle, byte pipeId, nint buffer, uint bufferLength,
        nint lengthTransferred, nint overlapped);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_AbortPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_AbortPipe(nint interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ResetPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ResetPipe(nint interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_FlushPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_FlushPipe(nint interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_SetPipePolicy", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_SetPipePolicy(
        nint interfaceHandle, byte pipeId, uint policyType, uint valueLength, nint value);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_GetPipePolicy", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_GetPipePolicy(
        nint interfaceHandle, byte pipeId, uint policyType, ref uint valueLength, nint value);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ResetDevice", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsb_ResetDevice(nint interfaceHandle);
}
