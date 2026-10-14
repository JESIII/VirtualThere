using Vht.Core.Capture;
using Vht.Core.Model;
using Vht.Core.Protocol;

namespace Vht.Acceptance;

/// <summary>
/// A USB device the acceptance rig can export without any hardware: descriptors that look like a
/// real member of one USB class, and a capture handle that answers control/bulk/interrupt URBs
/// with real bytes.
/// </summary>
/// <remarks>
/// This exists because the machine this rig runs on has no WinUSB-claimable device and no
/// elevated token (see <c>docs/ACCEPTANCE.md</c>, "Hardware: NOT RUN"). The rig therefore
/// exercises everything from the USB/IP wire inward against production code, and reports the
/// hardware-dependent part as NOT RUN rather than papering over it. A synthetic device is
/// <i>not</i> a claim about real hardware; it is a claim about the software path.
/// </remarks>
public static class SyntheticDevices
{
    /// <summary>One class template: a device descriptor and the endpoints that make it real.</summary>
    public sealed record Template(
        string Name,
        ushort VendorId,
        ushort ProductId,
        byte DeviceClass,
        byte DeviceSubClass,
        byte DeviceProtocol,
        IReadOnlyList<EndpointDescriptor> Endpoints,
        IReadOnlyList<UsbIpInterface> Interfaces,
        bool SupportsBulk,
        bool SupportsInterrupt,
        bool IsochronousOnly);

    // WinUSB's WINUSB_PIPE_INFORMATION.PipeId packs the transfer type into bits 4-6 of the
    // address (00 control, 01 isochronous, 10 bulk, 11 interrupt), which is exactly what
    // UsbIp.TransferType reads back. A synthetic endpoint must use the same packing or the
    // session resolves it as a control pipe.
    private static EndpointDescriptor Ep(byte address, ushort maxPacket, byte interval = 0) =>
        new() { Address = address, MaxPacketSize = maxPacket, Interval = interval };

    private const byte PipeBulkIn = 0x81 | 0x20;
    private const byte PipeBulkOut = 0x02 | 0x20;
    private const byte PipeInterruptIn = 0x81 | 0x30;
    private const byte PipeIsoIn = 0x81 | 0x10;

    private static UsbIpInterface Iface(byte cls, byte sub = 0, byte proto = 0) =>
        new() { InterfaceClass = cls, InterfaceSubClass = sub, InterfaceProtocol = proto };

    /// <summary>The five classes the acceptance matrix has to speak about, plus two ISO-only ones.</summary>
    public static readonly IReadOnlyList<Template> All = new[]
    {
        // 08/06/50 = SCSI transparent command set over bulk-only transport: a flash stick.
        new Template("mass-storage", 0x0781, 0x5581, UsbClass.MassStorage, 0x06, 0x50,
            new[] { Ep(PipeBulkIn, 512), Ep(PipeBulkOut, 512) },
            new[] { Iface(UsbClass.MassStorage, 0x06, 0x50) },
            SupportsBulk: true, SupportsInterrupt: false, IsochronousOnly: false),

        // 03/01/01 = boot keyboard: interrupt IN, no bulk.
        new Template("hid-keyboard", 0x046d, 0xc31c, UsbClass.Hid, 0x01, 0x01,
            new[] { Ep(PipeInterruptIn, 8, 10) },
            new[] { Iface(UsbClass.Hid, 0x01, 0x01) },
            SupportsBulk: false, SupportsInterrupt: true, IsochronousOnly: false),

        // 02/02/01 = CDC-ACM: a bulk data pair plus an interrupt notification endpoint.
        new Template("cdc-serial", 0x2341, 0x0043, UsbClass.Communications, 0x02, 0x01,
            new[] { Ep(PipeBulkIn, 64), Ep(PipeBulkOut, 64), Ep(PipeInterruptIn, 16, 16) },
            new[] { Iface(UsbClass.Communications, 0x02, 0x01), Iface(UsbClass.CdcData, 0x00, 0x00) },
            SupportsBulk: true, SupportsInterrupt: true, IsochronousOnly: false),

        // 01/01/00 = UAC1 audio control; the streaming interface is ISO-only. The interrupt
        // endpoint is number 2 because endpoint numbers are unique within a configuration: reusing
        // number 1 would make ResolveTransferType return the ISO type for the control pipe too.
        new Template("usb-audio-iso", 0x1235, 0x8210, UsbClass.Audio, 0x01, 0x00,
            new[] { Ep(PipeIsoIn, 192, 1), Ep(0x82 | 0x30, 16, 8) },
            new[] { Iface(UsbClass.Audio, 0x01, 0x00) },
            SupportsBulk: false, SupportsInterrupt: true, IsochronousOnly: true),

        // 0e/01/00 = UVC video control; the stream endpoint is ISO-only.
        new Template("uvc-webcam-iso", 0x046d, 0x0825, UsbClass.Video, 0x01, 0x00,
            new[] { Ep(PipeIsoIn, 1024, 1), Ep(0x82 | 0x30, 16, 8) },
            new[] { Iface(UsbClass.Video, 0x01, 0x00) },
            SupportsBulk: false, SupportsInterrupt: true, IsochronousOnly: true),

        // ff/00/00 = vendor-specific WinUSB board: the "unknown device" case.
        new Template("vendor-winlbusb", 0x2e8a, 0x000a, UsbClass.VendorSpecific, 0x00, 0x00,
            new[] { Ep(PipeBulkIn, 64), Ep(PipeBulkOut, 64), Ep(PipeInterruptIn, 8, 8) },
            new[] { Iface(UsbClass.VendorSpecific, 0x00, 0x00) },
            SupportsBulk: true, SupportsInterrupt: true, IsochronousOnly: false),
    };

    /// <summary>Builds the <see cref="UsbDevice"/> the export table will advertise.</summary>
    public static UsbDevice Build(Template template, int index)
    {
        ArgumentNullException.ThrowIfNull(template);

        string serial = $"{template.VendorId:x4}{template.ProductId:x4}-{index:0000}";
        string busId = $"1-{index + 1}";

        return new UsbDevice
        {
            Key = DeviceKey.FromBusId(busId),
            InstancePath = $@"USB\VID_{template.VendorId:X4}&PID_{template.ProductId:X4}\{serial}",
            BusNum = 1,
            DevNum = (uint)(index + 2),
            Speed = UsbIp.SpeedHigh,
            VendorId = template.VendorId,
            ProductId = template.ProductId,
            BcdDevice = 0x0100,
            DeviceClass = template.DeviceClass,
            DeviceSubClass = template.DeviceSubClass,
            DeviceProtocol = template.DeviceProtocol,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            Manufacturer = "VirtualThere Acceptance",
            Product = $"VirtualThere acceptance {template.Name} #{index}",
            Serial = serial,
            Interfaces = template.Interfaces,
            Endpoints = template.Endpoints,
            CaptureState = CaptureState.Capturable,
            CaptureReason = CaptureReason.None,
            DevicePath = $@"\\?\acceptance#vid_{template.VendorId:x4}#pid_{template.ProductId:x4}#{serial}",
        };
    }
}

/// <summary>What a synthetic device does with the URBs the server hands it.</summary>
public sealed class SyntheticDeviceOptions
{
    /// <summary>Bytes an IN bulk URB may return per second. 0 = unthrottled (memory speed).</summary>
    public long ThrottleBytesPerSecond { get; init; }

    /// <summary>
    /// <c>false</c> fills IN buffers from a PRNG so the payload is incompressible; <c>true</c>
    /// repeats a short pattern so LZ4 has something to win on.
    /// </summary>
    public bool Compressible { get; init; }

    /// <summary>When set, an IN bulk URB larger than this is refused, so the rig can prove an
    /// error path rather than only the happy path.</summary>
    public int? FailAboveBytes { get; init; }

    /// <summary>Milliseconds of artificial per-URB latency (models a slow USB bus).</summary>
    public int LatencyMs { get; init; }
}

/// <summary>
/// A real <see cref="IDeviceCapture"/> over synthetic state: control transfers answer the
/// standard descriptor requests, bulk transfers move bytes, interrupt transfers answer with a
/// short report, and the counters are what the acceptance report quotes.
/// </summary>
public sealed class SyntheticCapture : IDeviceCapture
{
    private readonly byte[] _pattern = new byte[4096];
    private readonly SyntheticDeviceOptions _options;
    private readonly UsbDevice _device;
    private long _bytesIn;
    private long _bytesOut;
    private long _urbCount;
    private int _controlTransfers;
    private int _interruptTransfers;
    private int _stalls;

    public SyntheticCapture(UsbDevice device, SyntheticDeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _options = options;
        // A 4 KiB repeating pattern: compressible by ~1000x with LZ4, which is the point.
        for (int i = 0; i < _pattern.Length; i++) _pattern[i] = (byte)((i % 251) + 1);
    }

    public DeviceKey Key => _device.Key;

    public bool IsOpen { get; private set; } = true;

    internal long BytesIn => Interlocked.Read(ref _bytesIn);
    internal long BytesOut => Interlocked.Read(ref _bytesOut);
    internal long UrbCount => Interlocked.Read(ref _urbCount);
    internal int ControlTransfers => Volatile.Read(ref _controlTransfers);
    internal int InterruptTransfers => Volatile.Read(ref _interruptTransfers);
    internal int Stalls => Volatile.Read(ref _stalls);

    /// <summary>
    /// Mirrors <c>UsbIpSession.ResolveTransferType</c>: the server hands the capture a pipe address
    /// (<c>endpoint | 0x80 for IN</c>) with the transfer-type bits already stripped, so the type
    /// has to be resolved against the device's own endpoint list exactly as the session does.
    /// </summary>
    private byte ResolveTransferType(byte endpointNumber, bool isIn)
    {
        if (endpointNumber == 0) return UsbIp.EpTypeControl;

        foreach (EndpointDescriptor endpoint in _device.Endpoints)
        {
            if (UsbIp.EndpointNumber(endpoint.Address) != endpointNumber) continue;
            if (endpoint.IsIn != isIn) continue;
            return endpoint.TransferType;
        }

        return UsbIp.EpTypeBulk;
    }

    public async ValueTask<TransferResult> ControlTransferAsync(
        ulong setup, uint transferFlags, Memory<byte> data, uint timeoutMs, CancellationToken ct)
    {
        Interlocked.Increment(ref _urbCount);
        Interlocked.Increment(ref _controlTransfers);
        await DelayAsync(ct).ConfigureAwait(false);

        byte requestType = (byte)(setup & 0xff);
        byte request = (byte)((setup >> 8) & 0xff);
        ushort value = (ushort)((setup >> 16) & 0xffff);
        ushort index = (ushort)((setup >> 32) & 0xffff);
        ushort length = (ushort)((setup >> 48) & 0xffff);
        bool deviceToHost = (requestType & 0x80) != 0;

        // GET_DESCRIPTOR: answer with a plausible descriptor of the requested type so a client
        // that parses it (rather than only relaying it) sees something consistent.
        if (request == 0x06)
        {
            byte type = (byte)(value >> 8);
            byte[] descriptor = type switch
            {
                0x01 => new byte[] { 18, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x40, 0x8a, 0x2e, 0x0a, 0x00, 0x00, 0x01, 0x01, 0x02, 0x03, 0x01 },
                0x02 => new byte[] { 9, 0x02, 0x19, 0x00, 0x01, 0x01, 0x00, 0x80, 0x32 },
                0x03 => System.Text.Encoding.Unicode.GetBytes("VirtualThere"),
                _ => new byte[] { 2, type },
            };
            int n = Math.Min(data.Length, descriptor.Length);
            descriptor.AsSpan(0, n).CopyTo(data.Span);
            return TransferResult.Success(n);
        }

        // SET_ADDRESS / SET_CONFIGURATION / class requests with no data stage: ack.
        if (!deviceToHost || length == 0)
            return TransferResult.Success(0);

        // Unknown IN request: zero the buffer, which is what a stub device does.
        data.Span.Clear();
        _ = index;
        return TransferResult.Success(data.Length);
    }

    public async ValueTask<TransferResult> DataTransferAsync(
        byte endpoint, uint transferFlags, Memory<byte> buffer, uint timeoutMs, CancellationToken ct)
    {
        Interlocked.Increment(ref _urbCount);
        await DelayAsync(ct).ConfigureAwait(false);

        bool isIn = UsbIp.IsInEndpoint(endpoint);
        byte type = ResolveTransferType(UsbIp.EndpointNumber(endpoint), isIn);

        if (type == UsbIp.EpTypeIso)
        {
            // The rig never reaches here (the session refuses ISO before capture), but a capture
            // that silently succeeded on an ISO pipe would hide that refusal.
            Interlocked.Increment(ref _stalls);
            return TransferResult.Failure(UsbIp.StatusEpipe);
        }

        if (type == UsbIp.EpTypeInterrupt)
            Interlocked.Increment(ref _interruptTransfers);

        if (_options.FailAboveBytes is { } limit && buffer.Length > limit)
        {
            Interlocked.Increment(ref _stalls);
            return TransferResult.Failure(UsbIp.StatusEpipe);
        }

        if (!isIn)
        {
            Interlocked.Add(ref _bytesOut, buffer.Length);
            return TransferResult.Success(buffer.Length);
        }

        Fill(buffer.Span);
        Interlocked.Add(ref _bytesIn, buffer.Length);
        return TransferResult.Success(buffer.Length);
    }

    /// <summary>Fills an IN buffer with either compressible pattern or PRNG noise.</summary>
    public void Fill(Span<byte> destination)
    {
        if (_options.Compressible)
        {
            int written = 0;
            while (written < destination.Length)
            {
                int n = Math.Min(_pattern.Length, destination.Length - written);
                _pattern.AsSpan(0, n).CopyTo(destination[written..]);
                written += n;
            }
            return;
        }

        System.Random.Shared.NextBytes(destination);
    }

    private async ValueTask DelayAsync(CancellationToken ct)
    {
        if (_options.ThrottleBytesPerSecond > 0)
        {
            // Cheap pacing: 512 KiB (or 1 ms) granularity is enough to model a USB bus without
            // making the soak test clock-bound on the timer resolution.
            await Task.Delay(1, ct).ConfigureAwait(false);
            return;
        }

        if (_options.LatencyMs > 0)
            await Task.Delay(_options.LatencyMs, ct).ConfigureAwait(false);
    }

    public ValueTask AbortPipeAsync(byte endpoint, CancellationToken ct)
    {
        Interlocked.Increment(ref _stalls);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetPipeAsync(byte endpoint, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask ResetDeviceAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out <see cref="SyntheticCapture"/> instances for a fixed device list.</summary>
public sealed class SyntheticCaptureProvider : ICaptureProvider
{
    private readonly Dictionary<DeviceKey, SyntheticCapture> _open = new();
    private readonly object _gate = new();
    private readonly SyntheticDeviceOptions _options;
    private int _opens;
    private int _releases;

    public SyntheticCaptureProvider(IReadOnlyList<UsbDevice> devices, SyntheticDeviceOptions options)
    {
        Devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IReadOnlyList<UsbDevice> Devices { get; }

    public int OpenCount => Volatile.Read(ref _opens);
    public int ReleaseCount => Volatile.Read(ref _releases);

    public ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct) =>
        ValueTask.FromResult(Devices);

    public ValueTask<CaptureOpenResult> OpenAsync(UsbDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        Interlocked.Increment(ref _opens);

        var capture = new SyntheticCapture(device, _options);
        lock (_gate) _open[device.Key] = capture;
        return ValueTask.FromResult(CaptureOpenResult.Success(capture));
    }

    public ValueTask ReleaseAsync(UsbDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        Interlocked.Increment(ref _releases);
        lock (_gate) _open.Remove(device.Key);
        return ValueTask.CompletedTask;
    }

    /// <summary>The handle for a bus id, for reading its counters, or null when not claimed.</summary>
    public SyntheticCapture? Handle(string busId)
    {
        lock (_gate)
            return _open.TryGetValue(DeviceKey.FromBusId(busId), out SyntheticCapture? capture) ? capture : null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
