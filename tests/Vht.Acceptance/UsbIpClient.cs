using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Vht.Core.Protocol;
using Vht.Transport;

namespace Vht.Acceptance;

/// <summary>One completed URB, as the server answered it.</summary>
public sealed record UrbCompletion(uint SeqNum, int Status, uint ActualLength, byte[] Data)
{
    public bool Ok => Status == 0;
}

/// <summary>The result of an <c>OP_REQ_IMPORT</c>.</summary>
public sealed record ImportOutcome(uint Status, UsbIpDevice? Device, uint DevId)
{
    public bool Ok => Status == 0 && Device is not null;

    /// <summary>The USB/IP status name, for the report.</summary>
    public string StatusName => Status switch
    {
        0 => "success",
        1 => "error (EBUSY/generic)",
        2 => "device_not_found",
        3 => "device_busy",
        4 => "device_error",
        5 => "device_unlicenseable (reserved; not produced by VirtualThere)",
        6 => "device_requires_kernel (reserved; not produced by VirtualThere)",
        7 => "device_password_required",
        _ => $"unknown({Status})",
    };
}

/// <summary>
/// A USB/IP 1.1.1 client, spoken directly over a <see cref="Vht.Transport.ITransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// The wire format is the stock one — <c>usbip_protocol.rst</c>, version 0x0111 — so an
/// acceptance run against this client is an acceptance run against anything that speaks USB/IP.
/// The framing is done with the project's own <see cref="UsbIpCodec"/>, i.e. the same encoder the
/// server parses with, which is a self-consistency check rather than an independent one; the
/// independent cross-check (a stock <c>usbip</c> binary) is recorded as NOT RUN in
/// <c>docs/ACCEPTANCE.md</c>.
/// </para>
/// <para>
/// The client deliberately knows nothing about devices: it imports bus ids and moves buffers.
/// </para>
/// </remarks>
public sealed class UsbIpClient : IAsyncDisposable
{
    private readonly Vht.Transport.ITransport _transport;
    private readonly ConcurrentDictionary<uint, PendingUrb> _pending = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private uint _nextSeq;
    private long _rxBytes;
    private long _txBytes;

    private readonly record struct PendingUrb(bool IsIn, byte EndpointType, uint RequestedLength);

    private UsbIpClient(Vht.Transport.ITransport transport) => _transport = transport;

    public long BytesReceived => Interlocked.Read(ref _rxBytes);
    public long BytesSent => Interlocked.Read(ref _txBytes);
    /// <summary>Wire-level counters for the transport in use, when it publishes any.</summary>
    public TransportStatistics? Statistics => _transport switch
    {
        Lz4Transport lz4 => lz4.Statistics,
        TlsTransport tls => tls.Statistics,
        PlainTcpTransport plain => plain.Statistics,
        _ => null,
    };

    /// <summary>Connects and completes whatever handshake the transport kind requires.</summary>
    public static async Task<UsbIpClient> ConnectAsync(
        string host, int port, TransportOptions options, CancellationToken ct)
    {
        Vht.Transport.ITransport transport = await TransportFactory
            .ConnectAsync(host, port, options.WithSharedAuthState(), options.ClientCredential, ct)
            .ConfigureAwait(false);
        return new UsbIpClient(transport);
    }

    // -------------------------------------------------------------------------------------
    // Operation phase
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The raw bytes of the last <c>OP_REP_DEVLIST</c> reply as they came off the wire, op header
    /// included. Exists so a test can compare the server's device record against an independently
    /// hand-built stock layout byte for byte instead of trusting a parser.
    /// </summary>
    public ReadOnlyMemory<byte> LastDevlistFrame { get; private set; } = ReadOnlyMemory<byte>.Empty;

    /// <summary><c>OP_REQ_DEVLIST</c> → every device the server is willing to advertise.</summary>
    public async Task<IReadOnlyList<UsbIpDevice>> ListDevicesAsync(CancellationToken ct)
    {
        byte[] request = new byte[UsbIpCodec.OpHeaderSize];
        UsbIpCodec.WriteOpHeader(request, UsbIp.OpReqDevlist);
        await WriteAsync(request, ct).ConfigureAwait(false);

        byte[] header = await ReadExactlyAsync(UsbIpCodec.OpHeaderSize, ct).ConfigureAwait(false);
        UsbIpCodec.OpHeader op = UsbIpCodec.ReadOpHeader(header);
        if (op.Code != UsbIp.OpRepDevlist)
            throw new InvalidOperationException($"expected OP_REP_DEVLIST, got 0x{op.Code:x4}");
        UsbIpCodec.ValidateVersion(op);

        byte[] countBytes = await ReadExactlyAsync(4, ct).ConfigureAwait(false);
        uint count = BigEndian.ReadU32(countBytes, 0);

        var frame = new List<byte>(UsbIpCodec.OpHeaderSize + 4 + (int)count * UsbIpDevice.WireSize);
        frame.AddRange(header);
        frame.AddRange(countBytes);

        var devices = new List<UsbIpDevice>((int)Math.Min(count, 4096));
        for (uint i = 0; i < count; i++)
        {
            // A device record is 312 bytes and its interface records TRAIL it; the count is the
            // last byte of the record, so the interface block can only be sized after reading it.
            byte[] record = await ReadExactlyAsync(UsbIpDevice.WireSize, ct).ConfigureAwait(false);
            int interfaces = record[UsbIpDevice.WireSize - 1];
            byte[] group = record;

            if (interfaces > 0)
            {
                byte[] trailing = await ReadExactlyAsync(interfaces * UsbIpDevice.InterfaceWireSize, ct)
                    .ConfigureAwait(false);
                group = new byte[record.Length + trailing.Length];
                record.CopyTo(group, 0);
                trailing.CopyTo(group, record.Length);
            }

            frame.AddRange(group);

            int offset = 0;
            devices.Add(UsbIpCodec.ReadDeviceRecord(group, ref offset));
        }

        LastDevlistFrame = frame.ToArray();
        return devices;
    }

    /// <summary>
    /// <c>OP_REQ_VHT_DEVINFO</c> → the operator's nickname for each exported device
    /// (docs/SERVER.md §8). This is VirtualThere's own op, not stock USB/IP: the stock 312-byte
    /// device record has no string field, so the nickname travels beside it.
    /// </summary>
    /// <remarks>
    /// The reply is parsed here from the documented layout with explicit offsets rather than with
    /// the project's own decoder, so this is an independent read of the bytes the server put on the
    /// wire. Devices with no nickname are absent from the result.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> ListNicknamesAsync(CancellationToken ct)
    {
        byte[] request = new byte[UsbIpCodec.OpHeaderSize + 4];
        UsbIpCodec.WriteOpHeader(request, UsbIp.OpReqVhtDevInfo);
        BinaryPrimitives.WriteUInt32BigEndian(
            request.AsSpan(UsbIpCodec.OpHeaderSize), UsbIp.VhtDevInfoMagic);
        await WriteAsync(request, ct).ConfigureAwait(false);

        byte[] head = await ReadExactlyAsync(UsbIpCodec.OpHeaderSize + 8, ct).ConfigureAwait(false);
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(head);
        ushort code = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
        uint status = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        if (version != UsbIp.ProtocolVersion || code != UsbIp.OpRepVhtDevInfo)
        {
            throw new InvalidOperationException(
                $"expected version 0x{UsbIp.ProtocolVersion:x4} / OP_REP_VHT_DEVINFO, " +
                $"got 0x{version:x4} / 0x{code:x4}");
        }
        if (status != 0)
            throw new InvalidOperationException($"OP_REP_VHT_DEVINFO refused with status {status}");

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(UsbIpCodec.OpHeaderSize));
        if (magic != UsbIp.VhtDevInfoMagic)
            throw new InvalidOperationException($"device info frame magic 0x{magic:x8}, expected VHT1");

        uint count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(UsbIpCodec.OpHeaderSize + 4));
        if (count > 4096)
            throw new InvalidOperationException($"implausible device info count {count}");

        var nicknames = new Dictionary<string, string>(StringComparer.Ordinal);
        for (uint i = 0; i < count; i++)
        {
            byte[] entry = await ReadExactlyAsync(UsbIp.BusIdLength + 1, ct).ConfigureAwait(false);
            int end = Array.IndexOf(entry, (byte)0);
            string busId = System.Text.Encoding.ASCII.GetString(entry, 0, end < 0 ? UsbIp.BusIdLength : end);
            int nameLength = entry[UsbIp.BusIdLength];
            if (nameLength > UsbIp.VhtNicknameMaxBytes)
                throw new InvalidOperationException($"{busId}: name length {nameLength} out of range");
            if (nameLength == 0)
                continue;

            byte[] name = await ReadExactlyAsync(nameLength, ct).ConfigureAwait(false);
            nicknames[busId] = System.Text.Encoding.UTF8.GetString(name);
        }

        return nicknames;
    }

    /// <summary><c>OP_REQ_IMPORT</c>. A refusal is returned, never thrown.</summary>
    public async Task<ImportOutcome> ImportAsync(string busId, CancellationToken ct)
    {
        byte[] header = new byte[UsbIpCodec.OpHeaderSize];
        UsbIpCodec.WriteOpHeader(header, UsbIp.OpReqImport);

        byte[] body = new byte[UsbIp.BusIdLength];
        UsbIpCodec.WriteImportBusId(body, busId);

        await WriteAsync(header, ct).ConfigureAwait(false);
        await WriteAsync(body, ct).ConfigureAwait(false);

        byte[] reply = await ReadExactlyAsync(UsbIpCodec.OpHeaderSize, ct).ConfigureAwait(false);
        UsbIpCodec.OpHeader op = UsbIpCodec.ReadOpHeader(reply);
        if (op.Code != UsbIp.OpRepImport)
            throw new InvalidOperationException($"expected OP_REP_IMPORT, got 0x{op.Code:x4}");

        if (op.Status != 0)
            return new ImportOutcome(op.Status, null, 0);

        byte[] record = await ReadExactlyAsync(UsbIpDevice.WireSize, ct).ConfigureAwait(false);

        // usbip_protocol.rst: OP_REP_IMPORT is op_common + usbip_usb_device (312 bytes) with NO
        // trailing interface records, unlike OP_REP_DEVLIST. ReadDeviceRecord implements the
        // DEVLIST rule, so the interface count is taken out before handing the record over.
        //
        // FINDING (follow-up card): VhtServerState renders the import reply with
        // bNumInterfaces copied from the device rather than zeroed, so this server's own reader
        // rejects its own writer for any device with interfaces. The stock Linux client reads a
        // fixed 312 bytes and ignores the field, so it is invisible to a kernel peer and visible
        // only to a conforming parser like this one.
        int advertisedInterfaces = record[UsbIpDevice.WireSize - 1];
        byte[] normalised = (byte[])record.Clone();
        normalised[UsbIpDevice.WireSize - 1] = 0;

        int offset = 0;
        UsbIpDevice device = UsbIpCodec.ReadDeviceRecord(normalised, ref offset);
        device.NumInterfaces = (byte)advertisedInterfaces;

        uint devId = (device.BusNum << 16) | (device.DevNum & 0xffff);
        return new ImportOutcome(0, device, devId);
    }

    // -------------------------------------------------------------------------------------
    // URB phase
    // -------------------------------------------------------------------------------------

    /// <summary>Submits one URB and returns its completion. One call, one outstanding URB.</summary>
    public async Task<UrbCompletion> SubmitAsync(
        uint devId,
        byte endpoint,
        ReadOnlyMemory<byte> payload,
        ulong setup,
        uint transferFlags,
        CancellationToken ct)
    {
        bool isIn = UsbIp.IsInEndpoint(endpoint);
        uint seq = Interlocked.Increment(ref _nextSeq);

        _pending[seq] = new PendingUrb(
            isIn,
            UsbIp.TransferType(endpoint),
            isIn ? (uint)payload.Length : (uint)payload.Length);

        byte[] frame = UsbIpCodec.EncodeCmdSubmit(
            seq,
            devId,
            isIn ? UsbIp.DirIn : UsbIp.DirOut,
            endpoint,
            transferFlags,
            isIn ? ReadOnlySpan<byte>.Empty : payload.Span,
            setup,
            startFrame: 0,
            numberOfPackets: UsbIp.NoIsoPackets,
            transferBufferLength: (uint)payload.Length);

        await WriteAsync(frame, ct).ConfigureAwait(false);
        return await ReadCompletionAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Submits <paramref name="count"/> URBs of <paramref name="length"/> bytes without
    /// waiting, then collects every completion. This is what produces the concurrency numbers.</summary>
    public async Task<IReadOnlyList<UrbCompletion>> SubmitManyAsync(
        uint devId,
        byte endpoint,
        int length,
        int count,
        uint transferFlags,
        CancellationToken ct)
    {
        bool isIn = UsbIp.IsInEndpoint(endpoint);
        var buffer = new byte[length];

        for (int i = 0; i < count; i++)
        {
            uint seq = Interlocked.Increment(ref _nextSeq);
            _pending[seq] = new PendingUrb(isIn, UsbIp.TransferType(endpoint), (uint)length);

            byte[] frame = UsbIpCodec.EncodeCmdSubmit(
                seq,
                devId,
                isIn ? UsbIp.DirIn : UsbIp.DirOut,
                endpoint,
                transferFlags,
                isIn ? ReadOnlySpan<byte>.Empty : buffer,
                setup: 0,
                startFrame: 0,
                numberOfPackets: UsbIp.NoIsoPackets,
                transferBufferLength: (uint)length);

            await WriteAsync(frame, ct).ConfigureAwait(false);
        }

        var results = new List<UrbCompletion>(count);
        for (int i = 0; i < count; i++)
            results.Add(await ReadCompletionAsync(ct).ConfigureAwait(false));

        return results;
    }

    /// <summary>A control-IN transfer, for the descriptor path.</summary>
    public Task<UrbCompletion> ControlInAsync(uint devId, uint request, ushort value, ushort index, int length, CancellationToken ct)
    {
        ulong setup = BuildSetup(0x80, (byte)request, value, index, (ushort)length);
        return SubmitAsync(devId, 0x00, new byte[length], setup, 0, ct);
    }

    /// <summary>A control-OUT transfer with a data stage.</summary>
    public Task<UrbCompletion> ControlOutAsync(uint devId, uint request, ushort value, ushort index, byte[] data, CancellationToken ct)
    {
        ulong setup = BuildSetup(0x00, (byte)request, value, index, (ushort)data.Length);
        return SubmitAsync(devId, 0x00, data, setup, 0, ct);
    }

    /// <summary>
    /// Submits an isochronous IN URB. The rig uses this to prove the refusal path: the server
    /// answers <c>EPIPE</c> because ISO is never negotiated (<c>UsbIpSession.IsoEnabled</c> is
    /// false on every live connection).
    /// </summary>
    public async Task<UrbCompletion> SubmitIsoAsync(
        uint devId, byte endpoint, uint packets, uint packetBytes, CancellationToken ct)
    {
        uint seq = Interlocked.Increment(ref _nextSeq);
        _pending[seq] = new PendingUrb(IsIn: true, UsbIp.EpTypeIso, packets * packetBytes);

        // An ISO submit carries one 16-byte descriptor per packet after the (zero-length for an
        // IN) transfer buffer.
        byte[] descriptors = new byte[packets * UsbIp.IsoPacketDescriptorSize];
        for (int i = 0; i < packets; i++)
        {
            int o = i * UsbIp.IsoPacketDescriptorSize;
            BinaryPrimitives.WriteUInt32BigEndian(descriptors.AsSpan(o, 4), (uint)(i * packetBytes));
            BinaryPrimitives.WriteUInt32BigEndian(descriptors.AsSpan(o + 4, 4), packetBytes);
        }

        byte[] frame = UsbIpCodec.EncodeCmdSubmit(
            seq, devId, UsbIp.DirIn, endpoint, 0, descriptors, setup: 0,
            startFrame: 0, numberOfPackets: packets, transferBufferLength: packets * packetBytes);

        await WriteAsync(frame, ct).ConfigureAwait(false);
        return await ReadCompletionAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Cancels an outstanding URB by seqnum and returns the unlink acknowledgement.</summary>
    public async Task<UrbCompletion> UnlinkAsync(uint devId, uint targetSeq, byte endpoint, CancellationToken ct)
    {
        uint seq = Interlocked.Increment(ref _nextSeq);
        _pending[seq] = new PendingUrb(IsIn: false, UsbIp.EpTypeControl, 0);
        byte[] frame = UsbIpCodec.EncodeCmdUnlink(seq, devId, targetSeq, endpoint);
        await WriteAsync(frame, ct).ConfigureAwait(false);
        return await ReadCompletionAsync(ct).ConfigureAwait(false);
    }

    private static ulong BuildSetup(byte requestType, byte request, ushort value, ushort index, ushort length)
    {
        Span<byte> raw = stackalloc byte[8];
        raw[0] = requestType;
        raw[1] = request;
        BinaryPrimitives.WriteUInt16LittleEndian(raw[2..], value);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[4..], index);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[6..], length);
        return BinaryPrimitives.ReadUInt64LittleEndian(raw);
    }

    /// <summary>Measures bulk-IN throughput for <paramref name="seconds"/>, returning MB/s.</summary>
    /// <param name="live">
    /// When supplied, is updated as each URB completes so a caller can sample progress mid-run
    /// (the soak transcript prints a row every 5 s instead of one number at the end).
    /// </param>
    public async Task<ThroughputSample> MeasureBulkInAsync(
        uint devId, byte endpoint, int urbBytes, TimeSpan duration, CancellationToken ct,
        ThroughputCounter? live = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(duration);

        const int Concurrency = 8;
        long total = 0;
        int urbs = 0;
        var stopwatch = Stopwatch.StartNew();
        var gate = new SemaphoreSlim(Concurrency, Concurrency);
        var failures = 0;

        try
        {
            var workers = new List<Task>();
            for (int w = 0; w < Concurrency; w++)
            {
                workers.Add(Task.Run(async () =>
                {
                    while (!deadline.IsCancellationRequested)
                    {
                        await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
                        try
                        {
                            UrbCompletion completion = await SubmitAsync(
                                devId, endpoint, new byte[urbBytes], 0, 0, deadline.Token).ConfigureAwait(false);
                            if (completion.Ok)
                            {
                                Interlocked.Add(ref total, completion.ActualLength);
                                Interlocked.Increment(ref urbs);
                                live?.Record(completion.ActualLength);
                            }
                            else
                            {
                                Interlocked.Increment(ref failures);
                                live?.RecordFailure();
                            }
                        }
                        catch (OperationCanceledException) { }
                        finally
                        {
                            try { gate.Release(); } catch (ObjectDisposedException) { }
                        }
                    }
                }, CancellationToken.None));
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        stopwatch.Stop();
        double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        long bytes = Interlocked.Read(ref total);
        return new ThroughputSample(
            bytes,
            seconds,
            bytes / seconds / (1024 * 1024),
            Volatile.Read(ref urbs),
            Volatile.Read(ref failures),
            urbBytes);
    }

    public async ValueTask DisposeAsync()
    {
        _readGate.Dispose();
        _writeGate.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------------------
    // Framing
    // -------------------------------------------------------------------------------------

    private async Task<UrbCompletion> ReadCompletionAsync(CancellationToken ct)
    {
        // The header and the data stage that follows it must be read as one unit: if the gate were
        // released between them another worker would read this reply's payload as its own header.
        await _readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] header = await ReadRawAsync(UsbIp.HeaderSize, ct).ConfigureAwait(false);
            uint command = BigEndian.ReadU32(header, 0);
            uint seq = BigEndian.ReadU32(header, 4);
            int status = BigEndian.ReadI32(header, 20);
            uint actual = BigEndian.ReadU32(header, 24);

            _pending.TryRemove(seq, out PendingUrb pending);

            if (command == UsbIp.RetUnlink)
                return new UrbCompletion(seq, status, actual, Array.Empty<byte>());

            if (command != UsbIp.RetSubmit)
                throw new InvalidOperationException($"unexpected URB reply command 0x{command:x}");

            // The data stage exists only for a successful IN completion of a non-ISO endpoint; the
            // server is the authority on the length it wrote, and the client already knows the
            // direction and transfer type from the request it sent.
            bool hasDataStage = status == 0
                && pending.IsIn
                && pending.EndpointType != UsbIp.EpTypeIso
                && actual > 0;

            if (!hasDataStage)
                return new UrbCompletion(seq, status, actual, Array.Empty<byte>());

            byte[] data = await ReadRawAsync((int)actual, ct).ConfigureAwait(false);
            return new UrbCompletion(seq, status, actual, data);
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        // Serialised: a socket write is not atomic for the whole buffer across concurrent callers,
        // and two interleaved submits would corrupt the framing for every later URB on the
        // connection. Multiple URBs stay in flight because the read path is not gated by this.
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _transport.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        Interlocked.Add(ref _txBytes, buffer.Length);
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        await _readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadRawAsync(count, ct).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes without taking the read gate.</summary>
    private async Task<byte[]> ReadRawAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int read = 0;

        while (read < count)
        {
            int n = await _transport.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException($"transport closed after {read} of {count} byte(s)");
            read += n;
        }

        Interlocked.Add(ref _rxBytes, count);
        return buffer;
    }
}

/// <summary>One throughput measurement: raw counters plus the derived MB/s.</summary>
public sealed record ThroughputSample(
    long Bytes, double Seconds, double MegabytesPerSecond, int Urbs, int Failures, int UrbBytes)
{
    public override string ToString() =>
        $"{MegabytesPerSecond:0.0} MB/s ({Bytes / (1024.0 * 1024.0):0.0} MiB in {Seconds:0.00}s, " +
        $"{Urbs} URBs of {UrbBytes} B, {Failures} failed)";
}

/// <summary>
/// A running byte/URB/failure tally a measurement updates as it goes, so a long soak can be sampled
/// while it is still running instead of only knowing the answer at the end.
/// </summary>
public sealed class ThroughputCounter
{
    private long _bytes;
    private int _urbs;
    private int _failures;

    /// <summary>Bytes transferred so far.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>URBs completed so far.</summary>
    public int Urbs => Volatile.Read(ref _urbs);

    /// <summary>URBs that completed with a non-zero status so far.</summary>
    public int Failures => Volatile.Read(ref _failures);

    /// <summary>Records one successful completion.</summary>
    public void Record(long bytes)
    {
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Increment(ref _urbs);
    }

    /// <summary>Records one failed completion.</summary>
    public void RecordFailure() => Interlocked.Increment(ref _failures);
}
