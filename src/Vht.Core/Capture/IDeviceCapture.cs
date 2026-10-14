using Vht.Core.Model;
using Vht.Core.Protocol;

namespace Vht.Core.Capture;

/// <summary>Outcome of one transfer executed against a captured device.</summary>
public readonly struct TransferResult
{
    /// <summary>Linux errno-style status; 0 on success.</summary>
    public int Status { get; init; }

    /// <summary>Bytes actually transferred.</summary>
    public int ActualLength { get; init; }

    /// <summary>True when the transfer completed successfully.</summary>
    public bool Ok => Status == 0;

    public static TransferResult Success(int length) => new() { Status = 0, ActualLength = length };

    public static TransferResult Failure(int status) => new() { Status = status, ActualLength = 0 };

    public override string ToString() => Ok ? $"ok ({ActualLength} bytes)" : $"status={Status}";
}

/// <summary>
/// A claimed USB device the server can run URBs against. Implemented by the WinUSB capture
/// path in <c>Vht.Server</c> and by test doubles in the unit-test project.
/// </summary>
/// <remarks>
/// The interface is deliberately narrow: one handle per device, one submission per endpoint,
/// cancellation by token. That is all a USB/IP server needs, and keeping it small is what
/// lets the N-device concurrency path be unit-tested without hardware.
/// </remarks>
public interface IDeviceCapture : IAsyncDisposable
{
    /// <summary>The device this handle belongs to.</summary>
    DeviceKey Key { get; }

    /// <summary>True while the underlying handle is usable.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Executes one control transfer. <paramref name="setup"/> is the 8-byte USB setup packet;
    /// <paramref name="data"/> is the data stage (length 0 for a no-data control transfer).
    /// </summary>
    ValueTask<TransferResult> ControlTransferAsync(
        ulong setup, uint transferFlags, Memory<byte> data, uint timeoutMs, CancellationToken ct);

    /// <summary>
    /// Executes a bulk or interrupt transfer on <paramref name="endpoint"/>.
    /// For an IN endpoint <paramref name="buffer"/> is filled; for OUT it is the payload.
    /// </summary>
    ValueTask<TransferResult> DataTransferAsync(
        byte endpoint, uint transferFlags, Memory<byte> buffer, uint timeoutMs, CancellationToken ct);

    /// <summary>Aborts every pending transfer on <paramref name="endpoint"/> (clear-halt path).</summary>
    ValueTask AbortPipeAsync(byte endpoint, CancellationToken ct);

    /// <summary>Resets <paramref name="endpoint"/> after a stall (onResetEp parity).</summary>
    ValueTask ResetPipeAsync(byte endpoint, CancellationToken ct);

    /// <summary>Performs a device-level reset (onReset parity).</summary>
    ValueTask ResetDeviceAsync(CancellationToken ct);
}

/// <summary>
/// Supplies capture handles for exported devices. The provider owns the policy question
/// "can this device be claimed at all", which is why the refusal reason lives here rather
/// than in the protocol layer.
/// </summary>
public interface ICaptureProvider : IAsyncDisposable
{
    /// <summary>
    /// Enumerates the local devices the server could export. Enumeration never rebinds and
    /// never writes to a device: it reports <see cref="CaptureState"/> and a reason, and the
    /// caller decides what to do with a device it cannot use (ARCHITECTURE.md §4).
    /// </summary>
    ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct);

    /// <summary>
    /// Claims <paramref name="device"/> for I/O, applying the configured capture path.
    /// Returns <c>null</c> and a reason when the device cannot be claimed.
    /// </summary>
    ValueTask<CaptureOpenResult> OpenAsync(UsbDevice device, CancellationToken ct);

    /// <summary>
    /// Hands a claimed device back: closes our handle and, when the capture path installed a
    /// device-specific driver, restores the original one (the <c>onPassToKernel</c> parity
    /// item in ARCHITECTURE.md §4.1).
    /// </summary>
    ValueTask ReleaseAsync(UsbDevice device, CancellationToken ct);
}

/// <summary>Result of a claim attempt.</summary>
public sealed class CaptureOpenResult
{
    /// <summary>The capture handle, when the claim succeeded.</summary>
    public IDeviceCapture? Capture { get; init; }

    /// <summary>Why the claim failed.</summary>
    public CaptureReason Reason { get; init; } = CaptureReason.None;

    /// <summary>Human-readable detail for the operator and the log.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Raw Win32 error code when the failure came from an OS call.</summary>
    public int Win32Error { get; init; }

    public bool Ok => Capture is not null;

    public static CaptureOpenResult Success(IDeviceCapture capture) => new() { Capture = capture };

    public static CaptureOpenResult Failure(CaptureReason reason, string detail, int win32Error = 0) =>
        new() { Reason = reason, Detail = detail, Win32Error = win32Error };
}
