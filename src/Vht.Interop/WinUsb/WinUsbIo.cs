using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vht.Interop.Devices;

/// <summary>
/// The managed handle for a claimed USB device: opens the device interface path, initialises
/// WinUSB on it, and runs overlapped control and data transfers.
/// </summary>
/// <remarks>
/// <para>
/// A device interface path is opened with <c>FILE_FLAG_OVERLAPPED</c> and every transfer is a
/// genuine overlapped WinUSB operation, for two reasons that matter in practice:
/// </para>
/// <list type="number">
///   <item>a bulk IN transfer of an unknown length must be able to complete short — with a
///   synchronous <c>WinUsb_ReadPipe</c> the call blocks until the buffer is full or the device
///   stalls, which for a USB/IP client waiting on a reply is a hang;</item>
///   <item>cancellation has to be immediate and precise, because
///   <c>USBIP_CMD_UNLINK</c> is a protocol obligation and a slow kick would break the
///   "unlink/cancel is honoured" requirement.</item>
/// </list>
/// <para>
/// Cancellation is implemented as an abort of the specific pipe, which fails the outstanding
/// overlapped operation with <c>ERROR_OPERATION_ABORTED</c>; that is translated by the caller
/// into a cancel status rather than an error.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinUsbIo : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly ThreadPoolBoundHandle _bound;
    private readonly ConcurrentDictionary<nint, TransferState> _pending = new();
    private nint _interfaceHandle;
    private int _disposed;

    private sealed class TransferState
    {
        public required TaskCompletionSource<uint> Completion { get; init; }
        public IOCompletionCallback Callback { get; set; } = null!;
        public nint Buffer { get; init; }
        public int Length { get; init; }
        public byte PipeId { get; init; }
    }

    private WinUsbIo(SafeFileHandle file, nint interfaceHandle, ThreadPoolBoundHandle bound)
    {
        _file = file;
        _interfaceHandle = interfaceHandle;
        _bound = bound;
    }

    /// <summary>The device interface path this handle was opened on.</summary>
    public string DevicePath { get; private init; } = string.Empty;

    /// <summary>True while the handle is open and usable.</summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && !_file.IsInvalid;

    /// <summary>
    /// Opens <paramref name="devicePath"/> and initialises WinUSB.
    /// Returns <c>null</c> and the Win32 error on failure, so the caller can report a precise
    /// reason instead of a generic failure (ARCHITECTURE.md §4.3).
    /// </summary>
    public static WinUsbIo? Open(string devicePath, out int win32Error)
    {
        win32Error = 0;
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            win32Error = Kernel32.ERROR_INVALID_PARAMETER;
            return null;
        }

        SafeFileHandle file = Kernel32.CreateFile(
            devicePath,
            Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            0,
            Kernel32.OPEN_EXISTING,
            Kernel32.FILE_ATTRIBUTE_NORMAL | Kernel32.FILE_FLAG_OVERLAPPED,
            0);

        if (file.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
            file.Dispose();
            return null;
        }

        if (!WinUsb.WinUsb_Initialize(file, out nint interfaceHandle))
        {
            win32Error = Marshal.GetLastWin32Error();
            file.Dispose();
            return null;
        }

        ThreadPoolBoundHandle bound;
        try
        {
            bound = ThreadPoolBoundHandle.BindHandle(file);
        }
        catch (Exception)
        {
            WinUsb.WinUsb_Free(interfaceHandle);
            file.Dispose();
            win32Error = Kernel32.ERROR_GEN_FAILURE;
            return null;
        }

        return new WinUsbIo(file, interfaceHandle, bound) { DevicePath = devicePath };
    }

    /// <summary>WinUSB interface handle, for the query entry points.</summary>
    internal nint InterfaceHandle => _interfaceHandle;

    // =====================================================================================
    // Descriptor queries
    // =====================================================================================

    /// <summary>Reads the device's connection speed (a <c>WINUSB_DEVICE_SPEED</c> value).</summary>
    public bool TryGetSpeed(out uint speed)
    {
        speed = 0;
        uint length = sizeof(uint);
        nint buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (!WinUsb.WinUsb_QueryDeviceInformation(_interfaceHandle, WinUsb.DEVICE_SPEED, ref length, buffer))
                return false;
            speed = (uint)Marshal.ReadInt32(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads the device descriptor through <c>WinUsb_QueryDeviceInformation</c>.</summary>
    public bool TryGetDeviceDescriptor(out WinUsb.USB_DEVICE_DESCRIPTOR descriptor)
    {
        descriptor = default;
        uint size = (uint)Marshal.SizeOf<WinUsb.USB_DEVICE_DESCRIPTOR>();
        uint length = size;
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!WinUsb.WinUsb_QueryDeviceInformation(_interfaceHandle, WinUsb.RAW_DEVICE_DESCRIPTOR, ref length, buffer))
                return false;

            // A short read is a truncated descriptor, which we would rather report than guess at.
            if (length < 18) return false;

            descriptor = Marshal.PtrToStructure<WinUsb.USB_DEVICE_DESCRIPTOR>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads the current interface settings and its pipes.</summary>
    public bool TryGetInterface(byte alternate, out WinUsb.USB_INTERFACE_DESCRIPTOR descriptor,
        out IReadOnlyList<WinUsb.WINUSB_PIPE_INFORMATION> pipes)
    {
        pipes = [];
        if (!WinUsb.WinUsb_QueryInterfaceSettings(_interfaceHandle, alternate, out descriptor))
            return false;

        var list = new List<WinUsb.WINUSB_PIPE_INFORMATION>(descriptor.bNumEndpoints);
        for (byte i = 0; i < descriptor.bNumEndpoints; i++)
        {
            if (WinUsb.WinUsb_QueryPipe(_interfaceHandle, alternate, i, out WinUsb.WINUSB_PIPE_INFORMATION pipe))
                list.Add(pipe);
        }
        pipes = list;
        return true;
    }

    // =====================================================================================
    // Transfers
    // =====================================================================================

    /// <summary>
    /// Runs a control transfer. <paramref name="data"/> is the data stage (empty for a
    /// no-data transfer); for an IN transfer it is filled in place.
    /// </summary>
    public async Task<(int Status, int Transferred)> ControlTransferAsync(
        WinUsb.WINUSB_SETUP_PACKET setup, Memory<byte> data, CancellationToken ct)
    {
        ThrowIfDisposed();

        const byte controlPipeId = 0;
        nint buffer = data.Length > 0 ? Marshal.AllocHGlobal(data.Length) : 0;
        try
        {
            if (data.Length > 0 && (setup.RequestType & 0x80) == 0)
                Marshal.Copy(data.ToArray(), 0, buffer, data.Length);

            (int error, int transferred) = await SubmitOverlappedAsync(
                controlPipeId,
                buffer,
                data.Length,
                (ptr, len, overlapped) => WinUsb.WinUsb_ControlTransfer(
                    _interfaceHandle, setup, ptr, (uint)len, out _, overlapped),
                ct).ConfigureAwait(false);

            if (error == 0 && data.Length > 0 && (setup.RequestType & 0x80) != 0 && transferred > 0)
            {
                var managed = new byte[transferred];
                Marshal.Copy(buffer, managed, 0, transferred);
                managed.AsSpan().CopyTo(data.Span);
            }

            return (error, transferred);
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Runs a bulk or interrupt transfer on a pipe.</summary>
    public async Task<(int Status, int Transferred)> DataTransferAsync(
        byte pipeId, byte[] buffer, bool isIn, CancellationToken ct)
    {
        ThrowIfDisposed();

        nint unmanaged = buffer.Length > 0 ? Marshal.AllocHGlobal(buffer.Length) : 0;
        try
        {
            if (buffer.Length > 0 && !isIn)
                Marshal.Copy(buffer, 0, unmanaged, buffer.Length);

            (int error, int transferred) = await SubmitOverlappedAsync(
                pipeId,
                unmanaged,
                buffer.Length,
                isIn
                    ? (ptr, len, overlapped) => WinUsb.WinUsb_ReadPipe(
                        _interfaceHandle, pipeId, ptr, (uint)len, 0, overlapped)
                    : (ptr, len, overlapped) => WinUsb.WinUsb_WritePipe(
                        _interfaceHandle, pipeId, ptr, (uint)len, 0, overlapped),
                ct).ConfigureAwait(false);

            if (error == 0 && isIn && transferred > 0)
                Marshal.Copy(unmanaged, buffer, 0, Math.Min(transferred, buffer.Length));

            return (error, transferred);
        }
        finally
        {
            if (unmanaged != 0) Marshal.FreeHGlobal(unmanaged);
        }
    }

    /// <summary>Aborts every pending transfer on a pipe. The <c>USBIP_CMD_UNLINK</c> path.</summary>
    public bool AbortPipe(byte pipeId) => WinUsb.WinUsb_AbortPipe(_interfaceHandle, pipeId);

    /// <summary>Resets a stalled pipe (the <c>onResetEp</c> equivalent).</summary>
    public bool ResetPipe(byte pipeId) => WinUsb.WinUsb_ResetPipe(_interfaceHandle, pipeId);

    /// <summary>Flushes a pipe.</summary>
    public bool FlushPipe(byte pipeId) => WinUsb.WinUsb_FlushPipe(_interfaceHandle, pipeId);

    /// <summary>Sets a pipe policy, e.g. <c>ALLOW_PARTIAL_READS</c>.</summary>
    public bool SetPipePolicy(byte pipeId, uint policyType, byte value)
    {
        nint buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, value);
            return WinUsb.WinUsb_SetPipePolicy(_interfaceHandle, pipeId, policyType, 1, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Sets the pipe transfer timeout in milliseconds; 0 disables the timeout.</summary>
    public bool SetPipeTimeout(byte pipeId, uint milliseconds)
    {
        nint buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, unchecked((int)milliseconds));
            return WinUsb.WinUsb_SetPipePolicy(_interfaceHandle, pipeId, WinUsb.PIPE_TRANSFER_TIMEOUT, sizeof(uint), buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Device-level reset (<c>onReset</c> parity).</summary>
    public bool ResetDevice() => WinUsb.WinUsb_ResetDevice(_interfaceHandle);

    // =====================================================================================
    // Overlapped plumbing
    // =====================================================================================

    private async Task<(int Error, int Transferred)> SubmitOverlappedAsync(
        byte pipeId, nint buffer, int length, StartTransfer start, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new TransferState
        {
            Completion = completion,
            Buffer = buffer,
            Length = length,
            PipeId = pipeId,
        };

        nint native = 0;
        try
        {
            // The callback is passed as pinData so the runtime keeps this delegate rooted for
            // the lifetime of the overlapped operation.
            native = AllocateOverlappedFor(state);
            _pending[native] = state;

            if (!start(buffer, length, native))
            {
                int startError = Marshal.GetLastWin32Error();

                // ERROR_IO_PENDING is the normal case for an overlapped start; anything else
                // means the operation never began.
                if (startError != Kernel32.ERROR_IO_PENDING)
                {
                    _pending.TryRemove(native, out _);
                    completion.TrySetResult(unchecked((uint)startError));
                }
            }

            using CancellationTokenRegistration registration = ct.Register(() =>
            {
                // Aborting the pipe fails the outstanding overlapped operation with
                // ERROR_OPERATION_ABORTED, which wakes the completion callback.
                WinUsb.WinUsb_AbortPipe(_interfaceHandle, pipeId);
            });

            uint result = await completion.Task.ConfigureAwait(false);
            int error = unchecked((int)result);
            int transferred = error == Kernel32.ERROR_OPERATION_ABORTED ? 0 : GetTransferred(native);
            return (error, transferred);
        }
        finally
        {
            if (native != 0)
            {
                _pending.TryRemove(native, out _);
                FreeOverlapped(native);
            }
        }
    }

    private unsafe void OnTransferComplete(uint errorCode, uint numBytes, NativeOverlapped* nativeOverlapped)
    {
        if (_pending.TryGetValue((nint)nativeOverlapped, out TransferState? state))
            state.Completion.TrySetResult(errorCode);
    }

    /// <summary>Allocates a native overlapped structure bound to this device handle.</summary>
    private unsafe nint AllocateOverlapped(IOCompletionCallback callback, object state) =>
        (nint)_bound.AllocateNativeOverlapped(callback, state, callback);

    /// <summary>
    /// Declares the completion delegate in an unsafe scope (its signature carries
    /// <see cref="NativeOverlapped"/>*) and roots it on the transfer state so the runtime keeps it
    /// alive for the lifetime of the overlapped operation.
    /// </summary>
    private unsafe nint AllocateOverlappedFor(TransferState state)
    {
        IOCompletionCallback callback = OnTransferComplete;
        state.Callback = callback;
        return AllocateOverlapped(callback, state);
    }

    /// <summary>Releases a native overlapped structure.</summary>
    private unsafe void FreeOverlapped(nint native) =>
        _bound.FreeNativeOverlapped((NativeOverlapped*)native);

    /// <summary>Reads the byte count an overlapped operation actually transferred.</summary>
    private unsafe int GetTransferred(nint native)
    {
        if (Kernel32.GetOverlappedResult(_file, native, out uint transferred, false))
            return (int)transferred;
        return 0;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>Closes the WinUSB handle and releases the device.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        nint handle = Interlocked.Exchange(ref _interfaceHandle, 0);
        if (handle != 0)
        {
            try
            {
                WinUsb.WinUsb_Free(handle);
            }
            catch
            {
                // Freeing is best effort during teardown.
            }
        }

        try
        {
            _bound.Dispose();
        }
        catch
        {
        }

        _file.Dispose();
    }

    /// <summary>Signature of a WinUSB overloaded start call.</summary>
    private delegate bool StartTransfer(nint buffer, int length, nint overlapped);
}
