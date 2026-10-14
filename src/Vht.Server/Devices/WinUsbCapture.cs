using Vht.Core.Capture;
using Vht.Core.Model;
using Vht.Core.Protocol;
using Vht.Interop;
using Vht.Interop.Devices;

namespace Vht.Server.Devices;

/// <summary>
/// A claimed device, driven through user-mode WinUSB.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole capture surface a USB/IP server needs, and it is deliberately thin: every
/// method maps one USB/IP operation onto one WinUSB call and translates the Win32 result into
/// the errno-style status USB/IP clients expect.
/// </para>
/// <para>
/// Errors are never swallowed. A stalled pipe becomes <c>-EPIPE</c>, an aborted transfer
/// becomes <c>-ECONNRESET</c> (which is what the client's unlink path expects) and a timeout
/// becomes <c>-ETIMEDOUT</c>, so the client can act on the distinction instead of guessing.
/// </para>
/// </remarks>
public sealed class WinUsbCapture : IDeviceCapture
{
    /// <summary><c>ETIMEDOUT</c> — the transfer did not complete inside the timeout.</summary>
    public const int StatusEtimedout = -110;

    /// <summary><c>EIO</c> — an unclassified transfer failure.</summary>
    public const int StatusEio = -5;

    private readonly WinUsbIo _io;
    private int _disposed;

    public WinUsbCapture(DeviceKey key, WinUsbIo io)
    {
        _io = io ?? throw new ArgumentNullException(nameof(io));
        Key = key;
    }

    /// <inheritdoc />
    public DeviceKey Key { get; }

    /// <inheritdoc />
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _io.IsOpen;

    /// <summary>The WinUSB interface path this handle was opened on.</summary>
    public string DevicePath => _io.DevicePath;

    /// <inheritdoc />
    public async ValueTask<TransferResult> ControlTransferAsync(
        ulong setup, uint transferFlags, Memory<byte> data, uint timeoutMs, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        WinUsb.WINUSB_SETUP_PACKET packet = DecodeSetup(setup);

        try
        {
            (int status, int transferred) =
                await _io.ControlTransferAsync(packet, data, ct).ConfigureAwait(false);

            return status == 0
                ? TransferResult.Success(transferred)
                : TransferResult.Failure(Errno(status));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TransferResult.Failure(UsbIp.StatusEconnreset);
        }
        catch (Exception)
        {
            return TransferResult.Failure(StatusEio);
        }
    }

    /// <inheritdoc />
    public async ValueTask<TransferResult> DataTransferAsync(
        byte endpoint, uint transferFlags, Memory<byte> buffer, uint timeoutMs, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        bool isIn = UsbIp.IsInEndpoint(endpoint);

        // WinUSB wants a pinned managed array rather than a Memory<byte>, so the payload is
        // copied in both directions. The copy is what keeps the caller's buffer untouched on
        // failure, which is the behaviour the USB/IP reply depends on: a failed IN reports zero
        // bytes of data, never stale bytes.
        byte[] staging = isIn ? new byte[buffer.Length] : buffer.ToArray();

        try
        {
            (int status, int transferred) =
                await _io.DataTransferAsync(endpoint, staging, isIn, ct).ConfigureAwait(false);

            if (status != 0)
                return TransferResult.Failure(Errno(status));

            if (isIn && transferred > 0)
                staging.AsSpan(0, Math.Min(transferred, buffer.Length)).CopyTo(buffer.Span);

            return TransferResult.Success(Math.Min(transferred, buffer.Length));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The unlink path: the client asked for this URB to stop.
            return TransferResult.Failure(UsbIp.StatusEconnreset);
        }
        catch (Exception)
        {
            return TransferResult.Failure(StatusEio);
        }
    }

    /// <inheritdoc />
    public ValueTask AbortPipeAsync(byte endpoint, CancellationToken ct)
    {
        _io.AbortPipe(endpoint);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ResetPipeAsync(byte endpoint, CancellationToken ct)
    {
        _io.ResetPipe(endpoint);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ResetDeviceAsync(CancellationToken ct)
    {
        _io.ResetDevice();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _io.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Splits the USB/IP <c>setup</c> quadword into WinUSB's setup packet. USB/IP carries it
    /// little-endian, packed as request-type, request, value, index, length.
    /// </summary>
    public static WinUsb.WINUSB_SETUP_PACKET DecodeSetup(ulong setup)
    {
        Span<byte> bytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(bytes, setup);

        return new WinUsb.WINUSB_SETUP_PACKET
        {
            RequestType = bytes[0],
            Request = bytes[1],
            Value = (ushort)(bytes[2] | (bytes[3] << 8)),
            Index = (ushort)(bytes[4] | (bytes[5] << 8)),
            Length = (ushort)(bytes[6] | (bytes[7] << 8)),
        };
    }

    /// <summary>
    /// Maps a Win32 error from an overlapped transfer onto the errno-style status USB/IP
    /// reports. The three cases clients distinguish are timeout, cancellation and everything
    /// else.
    /// </summary>
    public static int Errno(int win32Error) => win32Error switch
    {
        0 => 0,
        121 => StatusEtimedout,          // ERROR_SEM_TIMEOUT
        1460 => StatusEtimedout,         // ERROR_TIMEOUT
        995 => UsbIp.StatusEconnreset,   // ERROR_OPERATION_ABORTED
        31 => UsbIp.StatusEpipe,         // ERROR_GEN_FAILURE (a stalled endpoint)
        1167 => StatusEio,               // ERROR_DEVICE_NOT_CONNECTED
        _ => UsbIp.StatusEpipe,
    };
}
