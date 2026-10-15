using System.Collections.Concurrent;
using System.Threading.Channels;
using Vht.Core.Capture;
using Vht.Core.Hooks;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Protocol;
using Vht.Core.Transport;

namespace Vht.Core.Tests;

// =========================================================================================
// In-memory duplex transport
// =========================================================================================

/// <summary>
/// A one-way byte stream with blocking reads, used as one leg of an in-memory connection.
/// It behaves like a socket for the purposes of the session: short reads are possible, a
/// completed writer looks like end-of-stream, and cancellation aborts a pending read.
/// </summary>
public sealed class ByteStream
{
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private byte[] _current = Array.Empty<byte>();
    private int _offset;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        _channel.Writer.TryWrite(data.ToArray());
    }

    public void Complete() => _channel.Writer.TryComplete();

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length == 0) return 0;

        while (true)
        {
            if (_offset < _current.Length)
            {
                int n = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsSpan(_offset, n).CopyTo(buffer.Span);
                _offset += n;
                if (_offset == _current.Length)
                {
                    _current = Array.Empty<byte>();
                    _offset = 0;
                }
                return n;
            }

            if (!await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                return 0;

            if (_channel.Reader.TryRead(out byte[]? next) && next is not null)
            {
                _current = next;
                _offset = 0;
            }
        }
    }

    public async ValueTask<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException($"stream closed after {read} of {count} byte(s)");
            read += n;
        }
        return buffer;
    }
}

/// <summary>The server side of an in-memory connection.</summary>
/// <remarks>
/// It also implements <see cref="IPayloadKindTransport"/>, which is how a session test sees the
/// classification the session now attaches to a reply's data stage. <see cref="CompressionActive"/>
/// is <b>off by default</b>, so every existing test drives the same whole-frame path it always did.
/// </remarks>
public sealed class InMemoryTransport : ITransport, IPayloadKindTransport
{
    private readonly ByteStream _inbound;
    private readonly ByteStream _outbound;
    private readonly List<(PayloadKind Kind, int Length)> _writes = new();

    public InMemoryTransport(ByteStream inbound, ByteStream outbound, TransportKind kind)
    {
        _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        Kind = kind;
    }

    public TransportKind Kind { get; }

    public TransportCapabilities Capabilities => Kind == TransportKind.PlainTcp
        ? TransportCapabilities.None
        : TransportCapabilities.HandshakeRequired;

    /// <summary>When true, the session classifies a reply's data stage and this double records it.</summary>
    public bool CompressionActive { get; set; }

    /// <inheritdoc />
    public int CompressionThresholdBytes { get; set; } = 64 * 1024;

    /// <summary>Every write this transport received, in order, as (declared class, byte count).</summary>
    public IReadOnlyList<(PayloadKind Kind, int Length)> Writes
    {
        get { lock (_writes) return _writes.ToArray(); }
    }

    /// <summary>Forgets the recorded writes, so a test can look at one phase only.</summary>
    public void ClearWrites()
    {
        lock (_writes) _writes.Clear();
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        _inbound.ReadAsync(buffer, ct);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        Record(PayloadKind.Control, buffer.Length);
        _outbound.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WritePayloadAsync(PayloadKind payloadKind, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        Record(payloadKind, payload.Length);
        _outbound.Write(payload.Span);
        return ValueTask.CompletedTask;
    }

    private void Record(PayloadKind kind, int length)
    {
        lock (_writes) _writes.Add((kind, length));
    }

    public Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct) =>
        Task.FromResult(Kind == TransportKind.PlainTcp
            ? AuthResult.Failure("in-memory plaintext transport carries no authentication")
            : AuthResult.Success());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The client side of an in-memory connection: sends USB/IP frames into the session and
/// reads the replies back. This is what lets the whole server be driven end to end with no
/// socket and no hardware.
/// </summary>
public sealed class FakeLink
{
    private readonly ByteStream _toServer = new();
    private readonly ByteStream _fromServer = new();

    /// <summary>
    /// Direction of each submitted URB, keyed by seqnum. The wire carries a data stage only
    /// for an IN transfer (usbip_recv_xbuff: the client reads nothing back for an OUT pipe),
    /// so the reader has to know which requests asked for data.
    /// </summary>
    private readonly ConcurrentDictionary<uint, uint> _submitDirection = new();

    public FakeLink(TransportKind kind = TransportKind.PlainTcp)
    {
        Kind = kind;
        ServerTransport = new InMemoryTransport(_toServer, _fromServer, kind);
    }

    public TransportKind Kind { get; }

    public InMemoryTransport ServerTransport { get; }

    // ---- sending ----------------------------------------------------------------------

    public void Send(ReadOnlySpan<byte> frame) => _toServer.Write(frame);

    public void SendDevlistRequest()
    {
        Span<byte> header = stackalloc byte[UsbIpCodec.OpHeaderSize];
        UsbIpCodec.WriteOpHeader(header, UsbIp.OpReqDevlist);
        Send(header);
    }

    public void SendImportRequest(string busId)
    {
        Span<byte> header = stackalloc byte[UsbIpCodec.OpHeaderSize];
        UsbIpCodec.WriteOpHeader(header, UsbIp.OpReqImport);
        Send(header);

        Span<byte> body = stackalloc byte[UsbIp.BusIdLength];
        UsbIpCodec.WriteImportBusId(body, busId);
        Send(body);
    }

    public void SendSubmit(
        uint seqNum,
        uint devId,
        uint direction,
        byte ep,
        ReadOnlySpan<byte> payload = default,
        ulong setup = 0,
        uint numberOfPackets = UsbIp.NoIsoPackets,
        uint? transferBufferLength = null)
    {
        _submitDirection[seqNum] = direction;
        Send(UsbIpCodec.EncodeCmdSubmit(
            seqNum, devId, direction, ep, 0, payload, setup, 0, numberOfPackets, transferBufferLength));
    }

    /// <summary>Reads raw bytes from the server's side of the connection.</summary>
    public ValueTask<byte[]> ReadExactlyAsync(int count, CancellationToken ct) =>
        _fromServer.ReadExactlyAsync(count, ct);

    public void SendUnlink(uint seqNum, uint devId, uint unlinkSeqNum, byte ep = 0) =>
        Send(UsbIpCodec.EncodeCmdUnlink(seqNum, devId, unlinkSeqNum, ep));

    /// <summary>Closes the client's half of the connection, which the session sees as EOS.</summary>
    public void Close() => _toServer.Complete();

    // ---- receiving --------------------------------------------------------------------

    public async ValueTask<UsbIpCodec.OpHeader> ReadOpHeaderAsync(CancellationToken ct)
    {
        byte[] bytes = await _fromServer.ReadExactlyAsync(UsbIpCodec.OpHeaderSize, ct).ConfigureAwait(false);
        return UsbIpCodec.ReadOpHeader(bytes);
    }

    /// <summary>Reads an <c>OP_REP_DEVLIST</c> reply and decodes every device record.</summary>
    public async ValueTask<IReadOnlyList<UsbIpDevice>> ReadDevlistAsync(CancellationToken ct)
    {
        UsbIpCodec.OpHeader op = await ReadOpHeaderAsync(ct).ConfigureAwait(false);
        Assert.Equal(UsbIp.OpRepDevlist, op.Code);

        byte[] count = await _fromServer.ReadExactlyAsync(4, ct).ConfigureAwait(false);
        uint ndev = BigEndian.ReadU32(count, 0);

        var devices = new List<UsbIpDevice>((int)ndev);
        for (uint i = 0; i < ndev; i++)
        {
            byte[] record = await _fromServer.ReadExactlyAsync(UsbIpDevice.WireSize, ct).ConfigureAwait(false);
            int offset = 0;
            UsbIpDevice device = UsbIpCodec.ReadDeviceRecord(record, ref offset);
            int interfaces = record[311];
            if (interfaces > 0)
                await _fromServer.ReadExactlyAsync(interfaces * UsbIpDevice.InterfaceWireSize, ct).ConfigureAwait(false);
            devices.Add(device);
        }
        return devices;
    }

    /// <summary>Reads an <c>OP_REP_IMPORT</c>. Returns the device on success, null on refusal.</summary>
    public async ValueTask<UsbIpDevice?> ReadImportReplyAsync(CancellationToken ct)
    {
        UsbIpCodec.OpHeader op = await ReadOpHeaderAsync(ct).ConfigureAwait(false);
        Assert.Equal(UsbIp.OpRepImport, op.Code);
        if (op.Status != 0) return null;

        byte[] record = await _fromServer.ReadExactlyAsync(UsbIpDevice.WireSize, ct).ConfigureAwait(false);
        int offset = 0;
        return UsbIpCodec.ReadDeviceRecord(record, ref offset);
    }

    /// <summary>A URB-phase reply: either a submit completion or an unlink acknowledgement.</summary>
    public readonly struct UrbReply
    {
        public uint Command { get; init; }
        public uint SeqNum { get; init; }
        public int Status { get; init; }
        public uint ActualLength { get; init; }
        public byte[] Data { get; init; }

        public bool IsSubmit => Command == UsbIp.RetSubmit;
        public bool IsUnlink => Command == UsbIp.RetUnlink;

        public override string ToString() =>
            $"cmd=0x{Command:x} seq={SeqNum} status={Status} len={ActualLength}";
    }

    public async ValueTask<UrbReply> ReadUrbReplyAsync(CancellationToken ct)
    {
        byte[] header = await _fromServer.ReadExactlyAsync(UsbIp.HeaderSize, ct).ConfigureAwait(false);
        uint command = BigEndian.ReadU32(header, 0);
        uint seqNum = BigEndian.ReadU32(header, 4);
        int status = BigEndian.ReadI32(header, 20);
        uint actual = BigEndian.ReadU32(header, 24);

        // Data follows the header only for an IN completion; an OUT URB reports its byte count
        // in actual_length and sends nothing. Assume IN when the request was not seen, which
        // keeps the reader exercising the data path by default.
        uint direction = _submitDirection.TryGetValue(seqNum, out uint recorded) ? recorded : UsbIp.DirIn;

        byte[] data = Array.Empty<byte>();
        if (command == UsbIp.RetSubmit && status == 0 && actual > 0 && direction == UsbIp.DirIn)
            data = await _fromServer.ReadExactlyAsync((int)actual, ct).ConfigureAwait(false);

        return new UrbReply
        {
            Command = command,
            SeqNum = seqNum,
            Status = status,
            ActualLength = actual,
            Data = data,
        };
    }
}

// =========================================================================================
// Capture doubles
// =========================================================================================

/// <summary>A claimed device backed by nothing but memory.</summary>
public sealed class FakeCapture : IDeviceCapture
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeCapture(DeviceKey key) => Key = key;

    public DeviceKey Key { get; }

    public bool IsOpen { get; private set; } = true;

    /// <summary>Byte value an IN transfer fills the buffer with.</summary>
    public byte Fill { get; set; } = 0xAB;

    /// <summary>When set, transfers block until <see cref="ReleaseGate"/> is called.</summary>
    public bool Gated { get; set; }

    public int ControlCalls;
    public int DataCalls;
    public int AbortCalls;
    public int ResetPipeCalls;
    public int ResetDeviceCalls;
    public int Disposals;

    public List<byte> EndpointsSeen { get; } = new();

    public void ReleaseGate() => _gate.TrySetResult();

    public async ValueTask<TransferResult> ControlTransferAsync(
        ulong setup, uint transferFlags, Memory<byte> data, uint timeoutMs, CancellationToken ct)
    {
        Interlocked.Increment(ref ControlCalls);
        await GateAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return TransferResult.Failure(UsbIp.StatusEconnreset);
        data.Span.Fill(0x5A);
        return TransferResult.Success(data.Length);
    }

    public async ValueTask<TransferResult> DataTransferAsync(
        byte endpoint, uint transferFlags, Memory<byte> buffer, uint timeoutMs, CancellationToken ct)
    {
        Interlocked.Increment(ref DataCalls);
        lock (EndpointsSeen) EndpointsSeen.Add(endpoint);

        await GateAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return TransferResult.Failure(UsbIp.StatusEconnreset);

        if (UsbIp.IsInEndpoint(endpoint)) buffer.Span.Fill(Fill);
        return TransferResult.Success(buffer.Length);
    }

    public ValueTask AbortPipeAsync(byte endpoint, CancellationToken ct)
    {
        Interlocked.Increment(ref AbortCalls);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetPipeAsync(byte endpoint, CancellationToken ct)
    {
        Interlocked.Increment(ref ResetPipeCalls);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetDeviceAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ResetDeviceCalls);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        Interlocked.Increment(ref Disposals);
        _gate.TrySetResult();
        return ValueTask.CompletedTask;
    }

    private async Task GateAsync(CancellationToken ct)
    {
        if (!Gated) return;
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>An <see cref="ICaptureProvider"/> over <see cref="FakeCapture"/> instances.</summary>
public sealed class FakeCaptureProvider : ICaptureProvider
{
    private readonly ConcurrentDictionary<DeviceKey, FakeCapture> _opened = new();

    public IReadOnlyList<UsbDevice> Devices { get; set; } = Array.Empty<UsbDevice>();

    public CaptureOpenResult? ForcedFailure { get; set; }

    /// <summary>When true, every capture handed out is gated (transfers block).</summary>
    public bool GateCaptures { get; set; }

    public int OpenCount;
    public int ReleaseCount;

    public IReadOnlyDictionary<DeviceKey, FakeCapture> Opened => _opened;

    public ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct) =>
        ValueTask.FromResult(Devices);

    public ValueTask<CaptureOpenResult> OpenAsync(UsbDevice device, CancellationToken ct)
    {
        Interlocked.Increment(ref OpenCount);

        if (ForcedFailure is { } failure) return ValueTask.FromResult(failure);

        var capture = new FakeCapture(device.Key) { Gated = GateCaptures };
        _opened[device.Key] = capture;
        return ValueTask.FromResult(CaptureOpenResult.Success(capture));
    }

    public ValueTask ReleaseAsync(UsbDevice device, CancellationToken ct)
    {
        Interlocked.Increment(ref ReleaseCount);
        _opened.TryRemove(device.Key, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>An <see cref="IDeviceEnumerator"/> whose device list the test controls.</summary>
public sealed class FakeEnumerator : IDeviceEnumerator
{
    public IReadOnlyList<UsbDevice> Devices { get; set; } = Array.Empty<UsbDevice>();

    public long Generation { get; private set; }

    public void Bump() => Generation++;

    public ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct) =>
        ValueTask.FromResult(Devices);
}

// =========================================================================================
// Hook double
// =========================================================================================

/// <summary>An <see cref="IProcessRunner"/> that answers with a scripted outcome.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public ProcessOutcome Outcome { get; set; } = new()
    {
        Started = true,
        ExitCode = HookVocabulary.ExitYes,
    };

    public List<string> Commands { get; } = new();

    public Task<ProcessOutcome> RunAsync(string commandLine, TimeSpan timeout, CancellationToken ct)
    {
        lock (Commands) Commands.Add(commandLine);
        return Task.FromResult(Outcome);
    }
}

// =========================================================================================
// Helpers
// =========================================================================================

/// <summary>Synthetic devices, so enumeration and policy are testable without hardware.</summary>
public static class TestDevices
{
    /// <summary>Pipe-style address of a bulk IN endpoint (type bits + IN bit + number).</summary>
    public static byte BulkIn(int number) => (byte)(UsbIp.EpTypeBulk | UsbIp.EpDirIn | number);

    /// <summary>Pipe-style address of a bulk OUT endpoint.</summary>
    public static byte BulkOut(int number) => (byte)(UsbIp.EpTypeBulk | number);

    /// <summary>Pipe-style address of an isochronous IN endpoint.</summary>
    public static byte IsoIn(int number) => (byte)(UsbIp.EpTypeIso | UsbIp.EpDirIn | number);

    public static UsbDevice Create(
        int index,
        ushort vendorId = 0x1234,
        ushort productId = 0x5678,
        bool requiresPassword = false,
        CaptureState state = CaptureState.Capturable,
        CaptureReason reason = CaptureReason.None,
        bool withIsoEndpoint = false)
    {
        string busId = $"1-{index}";
        return new UsbDevice
        {
            Key = DeviceKey.FromBusId(busId),
            InstancePath = $"USB\\VID_{vendorId:X4}&PID_{productId:X4}\\{index}",
            BusNum = 1,
            DevNum = (uint)index,
            Speed = UsbIp.SpeedHigh,
            VendorId = vendorId,
            ProductId = productId,
            BcdDevice = 0x0100,
            DeviceClass = UsbClass.VendorSpecific,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            Manufacturer = "VirtualThere",
            Product = $"Fake Device {index}",
            Serial = $"SN{index:D4}",
            RequiresPassword = requiresPassword,
            CaptureState = state,
            CaptureReason = reason,
            CaptureDetail = reason == CaptureReason.None ? string.Empty : reason.Describe(),
            Endpoints = withIsoEndpoint
                ? new[]
                {
                    new EndpointDescriptor { Address = BulkIn(1), MaxPacketSize = 512 },
                    new EndpointDescriptor { Address = IsoIn(2), MaxPacketSize = 1024, Interval = 1 },
                }
                : new[]
                {
                    new EndpointDescriptor { Address = BulkIn(1), MaxPacketSize = 512 },
                    new EndpointDescriptor { Address = BulkOut(2), MaxPacketSize = 512 },
                },
        };
    }

    public static UsbDevice[] Range(int count) =>
        Enumerable.Range(1, count).Select(i => Create(i)).ToArray();
}

/// <summary>Locates the repository root from the test assembly's output directory.</summary>
public static class RepoRoot
{
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VirtualThere.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"could not locate VirtualThere.slnx above {AppContext.BaseDirectory}");
    }

    /// <summary>Every C# source file of the server-side projects (Vht.Core, Vht.Server, Vht.Cli).</summary>
    public static IEnumerable<string> ServerSourceFiles()
    {
        string root = Find();
        foreach (string project in new[] { "src/Vht.Core", "src/Vht.Server", "src/Vht.Cli" })
        {
            string directory = Path.Combine(root, project);
            if (!Directory.Exists(directory)) continue;

            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;
                yield return file;
            }
        }
    }

    /// <summary>
    /// The lines of a source file with comments and XML doc lines removed, so an invariant
    /// test can tell "this string appears in a citation" from "this string appears in code".
    /// </summary>
    public static IEnumerable<(int Line, string Text)> CodeLines(string path)
    {
        int number = 0;
        foreach (string raw in File.ReadLines(path))
        {
            number++;
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("*", StringComparison.Ordinal) ||
                trimmed.StartsWith("/*", StringComparison.Ordinal))
                continue;
            yield return (number, raw);
        }
    }
}

/// <summary>Builds the shared server state used by the session tests.</summary>
public static class TestServer
{
    public static (Vht.Core.Server.VhtServerState State, FakeCaptureProvider Capture, InMemoryLogSink Log)
        Create(
            IReadOnlyList<UsbDevice> devices,
            DevicePolicyFactory? policy = null,
            HookRunnerFactory? hooks = null,
            Action<Vht.Core.Server.ServerOptions>? configure = null)
    {
        var options = new Vht.Core.Server.ServerOptions { ServerName = "test-server" };
        configure?.Invoke(options);

        var capture = new FakeCaptureProvider { Devices = devices };
        var sink = new InMemoryLogSink();
        var logger = new StructuredLogger(LogLevel.Detail, sink);

        var state = new Vht.Core.Server.VhtServerState(
            options,
            capture,
            logger,
            hooks?.Invoke(),
            policy?.Invoke());

        return (state, capture, sink);
    }

    public delegate Vht.Core.Policy.DevicePolicy DevicePolicyFactory();

    public delegate HookRunner HookRunnerFactory();
}

/// <summary>Creates a connection record for a session under test.</summary>
public static class TestConnections
{
    public static Vht.Core.Concurrency.Connection New(
        ushort id,
        TransportKind kind = TransportKind.PlainTcp,
        string remote = "127.0.0.1:5000") =>
        new()
        {
            Id = new Vht.Core.Concurrency.ConnectionId(id),
            ClientUuid = $"uuid-{id}",
            RemoteAddress = remote,
            Kind = kind,
            Cancellation = new CancellationTokenSource(),
        };
}
