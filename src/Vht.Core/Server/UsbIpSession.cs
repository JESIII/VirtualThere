using Vht.Core.Capture;
using Vht.Core.Concurrency;
using Vht.Core.Hooks;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Policy;
using Vht.Core.Protocol;
using Vht.Core.Transport;

namespace Vht.Core.Server;

/// <summary>What a session did, for the acceptance harness and the logs.</summary>
public sealed class SessionOutcome
{
    public bool Listed { get; init; }
    public bool Imported { get; init; }
    public BindStatus BindStatus { get; init; } = BindStatus.Unknown;
    public string BindDetail { get; init; } = string.Empty;
    public DeviceKey? Device { get; init; }
    public long SubmitsReceived { get; init; }
    public long SubmitsCompleted { get; init; }
    public long UnlinksReceived { get; init; }
    public IReadOnlyList<string> ProtocolErrors { get; init; } = Array.Empty<string>();

    public override string ToString() =>
        $"listed={Listed} imported={Imported} bind={BindStatus} submits={SubmitsReceived}/{SubmitsCompleted} " +
        $"unlinks={UnlinksReceived} errors={ProtocolErrors.Count}";
}

/// <summary>
/// One client connection, driven end to end: operation handshake, then the URB loop.
/// </summary>
/// <remarks>
/// <para>
/// This type is transport-agnostic on purpose. It is handed an <see cref="ITransport"/> and
/// never sees a socket, so the same code path serves the plaintext listener today, the TLS
/// listener when the transport card lands, and the unit tests' in-memory duplex stream. That
/// is what makes the N-device concurrency path testable without hardware.
/// </para>
/// <para>
/// Frame handling is strict (ARCHITECTURE.md §6.1): a frame with an unknown command, an
/// oversized length, an inconsistent ISO count or a SUBMIT before IMPORT is a protocol error
/// that closes the connection. It is never a crash and never a silent ignore.
/// </para>
/// </remarks>
public sealed class UsbIpSession
{
    private readonly VhtServerState _state;
    private readonly Connection _connection;
    private readonly ITransport _transport;
    private readonly ILogger _log;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// The transfers this connection has running, so teardown can wait for them
    /// (<see cref="TeardownAsync"/>) instead of cutting a transfer off mid-flight.
    /// </summary>
    /// <remarks>
    /// It holds only transfers that have not finished: <see cref="Track"/> drops completed ones
    /// before adding. Keeping finished transfers is what made this list the one structure that grew
    /// with the number of URBs ever served (one state machine plus its 64 KiB transfer buffer per
    /// URB), which is exactly the retained growth the acceptance soak measures.
    /// </remarks>
    private readonly List<Task> _pendingTransfers = new();
    private readonly object _pendingGate = new();

    /// <summary>
    /// Every device this connection currently holds, keyed by bus id. A connection is not
    /// limited to one: each successful import adds an entry, so a single client can attach N
    /// devices over one socket. The stock client opens one connection per device, for which
    /// this simply never grows past one entry — no wire behaviour changes for it, and no
    /// entry count is ever compared against anything.
    /// </summary>
    private readonly Dictionary<DeviceKey, Attachment> _attachments = new();

    private SessionOutcome _outcome = new();

    /// <summary>One held device: its descriptor model, its claim, and its attach record.</summary>
    private sealed class Attachment
    {
        internal required UsbDevice Device { get; init; }

        internal required IDeviceCapture Capture { get; init; }

        internal required AttachRecord Record { get; init; }

        /// <summary>Links this attach record's detach token (kick / hotplug) into the session.</summary>
        internal CancellationTokenRegistration DetachRegistration { get; set; }
    }

    /// <summary>
    /// The session's lifetime token source: caller cancellation + connection cancellation +
    /// (once a device is attached) the attach record's detach token. That last leg is what
    /// makes <c>devices.kick</c> and a hotplug removal actually stop this session instead of
    /// leaving it servicing a device it no longer owns.
    /// </summary>
    private CancellationTokenSource? _sessionLink;

    /// <summary>Per-transfer timeout. A stalled endpoint must not pin a connection forever.</summary>
    public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, isochronous transfers are refused with a clear status instead of being
    /// attempted. v1 targets control/bulk/interrupt as required and treats ISO as best-effort
    /// behind a negotiated capability (ARCHITECTURE.md risk 6); a stock plaintext peer has no
    /// way to negotiate, so ISO is reported unsupported rather than half-working.
    /// </summary>
    public bool IsoEnabled { get; init; }

    public UsbIpSession(VhtServerState state, Connection connection, ITransport transport)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _log = state.Logger;
    }

    /// <summary>The outcome of a completed session. Valid after <see cref="RunAsync"/> returns.</summary>
    public SessionOutcome Outcome => _outcome;

    /// <summary>
    /// Runs the session until the peer closes, the caller cancels, or a protocol error occurs.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _connection.Cancellation.Token);
        CancellationToken token = linked.Token;
        _sessionLink = linked;

        _log.Write(LogLevel.Info, LogEvent.ClientConnected,
            $"connection {_connection.Id} from {_connection.RemoteAddress} ({_connection.Kind})",
            new Dictionary<string, object?>
            {
                ["connection_id"] = _connection.Id.Value,
                ["client_uuid"] = _connection.ClientUuid,
                ["client_ip"] = _connection.RemoteAddress,
                ["transport"] = _connection.Kind.ToString(),
            });

        try
        {
            await FrameLoopAsync(token).ConfigureAwait(false);
        }
        catch (UsbIpProtocolException ex)
        {
            RecordProtocolError(ex.Message);
        }
        catch (EndOfStreamException)
        {
            // Normal close between frames.
        }
        catch (IOException ex)
        {
            _log.Write(LogLevel.Detail, LogEvent.ClientDisconnected,
                $"connection {_connection.Id} socket error: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // Teardown, kick or service stop.
        }
        finally
        {
            await TeardownAsync().ConfigureAwait(false);
            _state.Connections.Remove(_connection.Id);
            _log.Write(LogLevel.Info, LogEvent.ClientDisconnected,
                $"connection {_connection.Id} closed",
                new Dictionary<string, object?>
                {
                    ["connection_id"] = _connection.Id.Value,
                    ["client_ip"] = _connection.RemoteAddress,
                    ["imported"] = _outcome.Imported,
                    ["submits"] = _outcome.SubmitsReceived,
                    ["unlinks"] = _outcome.UnlinksReceived,
                });
        }
    }

    // =====================================================================================
    // Operation phase
    // =====================================================================================

    /// <summary>
    /// The connection's single read loop. Every USB/IP packet begins with the 8-byte
    /// <c>op_common</c>, so one loop can tell an operation (<c>OP_REQ_DEVLIST</c>,
    /// <c>OP_REQ_IMPORT</c>) from an URB (<c>CMD_SUBMIT</c>, <c>CMD_UNLINK</c>) and service
    /// either at any moment.
    /// </summary>
    /// <remarks>
    /// An earlier revision switched state machines after the first successful import, which
    /// made a second import a protocol error and capped a connection at one device. One loop
    /// keeps a refused import retryable (what the stock client does when it is pointed at a
    /// busy device) and lets one client hold as many devices as it imports. The stock client
    /// sends exactly one import per connection, so nothing about its stream changes.
    /// </remarks>
    private async Task FrameLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] prefix = await TransportIo.ReadExactlyAsync(_transport, UsbIpCodec.OpHeaderSize, ct)
                .ConfigureAwait(false);
            UsbIpCodec.OpHeader header = UsbIpCodec.ReadOpHeader(prefix);

            // The version field only exists on operations: an URB packet's first four bytes
            // are <c>usbip_header_basic.command</c> (1 or 2), so reading a version out of one
            // would be meaningless. Which packet this is, is decided by the code.
            if (header.Code is UsbIp.OpReqDevlist or UsbIp.OpReqImport or UsbIp.OpReqVhtDevInfo)
            {
                if (header.Version != UsbIp.ProtocolVersion)
                {
                    throw new UsbIpProtocolException(
                        $"unsupported USB/IP version 0x{header.Version:x4}; this server speaks 0x{UsbIp.ProtocolVersion:x4}");
                }

                if (header.Code == UsbIp.OpReqDevlist)
                {
                    await SendDevlistAsync(ct).ConfigureAwait(false);
                    _outcome = With(_outcome, listed: true);
                    continue;
                }

                if (header.Code == UsbIp.OpReqVhtDevInfo)
                {
                    await HandleVhtDeviceInfoAsync(ct).ConfigureAwait(false);
                    continue;
                }

                byte[] busIdBody = await TransportIo.ReadExactlyAsync(_transport, UsbIp.BusIdLength, ct)
                    .ConfigureAwait(false);
                await HandleImportAsync(UsbIpCodec.ReadImportBusId(busIdBody), ct).ConfigureAwait(false);
                continue;
            }

            // Not an operation: it is an URB. Its header is 48 bytes, of which the first 8 are
            // already in hand.
            var headerBytes = new byte[UsbIp.HeaderSize];
            prefix.CopyTo(headerBytes, 0);
            byte[] remainder = await TransportIo
                .ReadExactlyAsync(_transport, UsbIp.HeaderSize - UsbIpCodec.OpHeaderSize, ct)
                .ConfigureAwait(false);
            remainder.CopyTo(headerBytes, UsbIpCodec.OpHeaderSize);

            (UsbIpBasicHeader basic, UsbIpCmdSubmit submit, UsbIpCmdUnlink unlink) =
                UsbIpCodec.ParseUrbHeader(headerBytes);

            if (basic.Command == UsbIp.CmdUnlink)
            {
                await HandleUnlinkAsync(basic, unlink, ct).ConfigureAwait(false);
                _outcome = With(_outcome, unlinks: _outcome.UnlinksReceived + 1);
                continue;
            }

            // Submit: read the body before doing anything else, so the stream stays framed
            // even when the transfer itself is refused.
            long bodyLength = UsbIpCodec.SubmitBodyLength(basic, submit);
            if (bodyLength > UsbIpCodec.MaxFrameAllocation)
                throw new UsbIpProtocolException($"submit body {bodyLength} exceeds the frame maximum");

            byte[] body2 = bodyLength == 0
                ? Array.Empty<byte>()
                : await TransportIo.ReadExactlyAsync(_transport, (int)bodyLength, ct).ConfigureAwait(false);

            long dataLength = basic.Direction == UsbIp.DirOut
                ? Math.Min(submit.TransferBufferLength, (uint)body2.Length)
                : 0;
            int isoBytes = body2.Length - (int)dataLength;
            ReadOnlyMemory<byte> payload = body2.AsMemory(0, (int)dataLength);
            IReadOnlyList<IsoPacketDescriptor> iso = isoBytes > 0
                ? UsbIpCodec.ParseIsoDescriptors(body2.AsSpan((int)dataLength), submit.NumberOfPackets)
                : Array.Empty<IsoPacketDescriptor>();

            await HandleSubmitAsync(basic, submit, payload, iso, ct).ConfigureAwait(false);
            _outcome = With(_outcome, submits: _outcome.SubmitsReceived + 1);
        }
    }

    /// <summary>
    /// Answers VirtualThere's own <c>OP_REQ_VHT_DEVINFO</c> (docs/SERVER.md §8): the bus id and
    /// the operator's nickname for every device this connection may see.
    /// </summary>
    /// <remarks>
    /// The set of devices named here is exactly <see cref="ExportTable.Advertisable"/> for this
    /// connection's transport — the same set <c>OP_REP_DEVLIST</c> sends — so a
    /// password-protected device is not named here either, and a client can never learn a name
    /// for a device it was not offered. The stock device record is untouched: this is a separate
    /// op that a stock client neither sends nor receives.
    /// </remarks>
    private async Task HandleVhtDeviceInfoAsync(CancellationToken ct)
    {
        byte[] magicBytes = await TransportIo.ReadExactlyAsync(_transport, 4, ct).ConfigureAwait(false);
        uint magic = BigEndian.ReadU32(magicBytes, 0);
        if (magic != UsbIp.VhtDevInfoMagic)
        {
            throw new UsbIpProtocolException(
                $"OP_REQ_VHT_DEVINFO carried magic 0x{magic:x8}, expected 0x{UsbIp.VhtDevInfoMagic:x8}");
        }

        List<UsbIpCodec.VhtDeviceInfo> entries = _state.Exports
            .Advertisable(_connection.Kind)
            .Select(e => new UsbIpCodec.VhtDeviceInfo(e.Device.BusId, e.Device.Nickname))
            .ToList();

        await SendAsync(UsbIpCodec.EncodeVhtDeviceInfoReply(entries), ct).ConfigureAwait(false);

        _log.Write(LogLevel.Detail, LogEvent.DeviceEnumerated,
            $"device info (nicknames) sent to connection {_connection.Id}: " +
            $"{entries.Count(e => !string.IsNullOrEmpty(e.Nickname))} named of {entries.Count} device(s)",
            new Dictionary<string, object?>
            {
                ["connection_id"] = _connection.Id.Value,
                ["count"] = entries.Count,
                ["named"] = entries.Count(e => !string.IsNullOrEmpty(e.Nickname)),
                ["transport"] = _connection.Kind.ToString(),
            });
    }

    private async Task SendDevlistAsync(CancellationToken ct)
    {
        // §6.4 no-auth containment: a password-protected device is never advertised on the
        // plaintext listener, because USB/IP has no field to carry a password there.
        IReadOnlyList<ExportEntry> entries = _state.Exports.Advertisable(_connection.Kind);

        var devices = entries
            .Select(e => e.Device.ToUsbIpDevice())
            .ToList();

        byte[] reply = UsbIpCodec.EncodeDevlistReply(devices);
        await SendAsync(reply, ct).ConfigureAwait(false);

        _log.Write(LogLevel.Detail, LogEvent.DeviceEnumerated,
            $"device list sent to connection {_connection.Id}: {devices.Count} device(s)",
            new Dictionary<string, object?>
            {
                ["connection_id"] = _connection.Id.Value,
                ["count"] = devices.Count,
                ["transport"] = _connection.Kind.ToString(),
            });
    }

    /// <summary>
    /// Resolves a bus id, applies policy, binds the device and claims it. The connection ends
    /// up holding the device on success, and keeps running either way.
    /// </summary>
    private async Task HandleImportAsync(string busId, CancellationToken ct)
    {
        DeviceKey key = DeviceKey.FromBusId(busId);

        // Idempotent re-import on the same connection: keep the claim already held rather
        // than opening a second handle to the same device. The attach table treats this as a
        // no-op (same connection), so the original record stays in place. This is reachable
        // because imports are serviced at any point in the connection's life.
        if (_attachments.TryGetValue(key, out Attachment? already))
        {
            await SendAsync(UsbIpCodec.EncodeImportReply(already.Device.ToUsbIpDevice()), ct).ConfigureAwait(false);
            _outcome = With(_outcome, imported: true, bindStatus: BindStatus.Success, device: key);
            return;
        }

        if (!_state.Exports.TryGet(key, out ExportEntry entry))
        {
            await RefuseImportAsync(BindStatus.Unknown, $"no exported device with bus id '{busId}'", ct)
                .ConfigureAwait(false);
            return;
        }

        UsbDevice device = entry.Device;

        // §6.4: never serve a password-protected device on a transport that cannot carry one.
        if (device.RequiresPassword && _connection.Kind == TransportKind.PlainTcp)
        {
            await RefuseImportAsync(BindStatus.PasswordRequired,
                $"device '{busId}' requires a password and is not available on the plaintext listener", ct)
                .ConfigureAwait(false);
            return;
        }

        // Policy re-evaluated at bind time, not just at enumeration: an ignore recorded
        // while the device was exported takes effect immediately.
        PolicyVerdict verdict = _state.Policy.Evaluate(device);
        if (!verdict.Allowed)
        {
            await RefuseImportAsync(BindStatus.Unauthorized, verdict.Detail, ct).ConfigureAwait(false);
            return;
        }

        HookResult bindHook = await _state.RunHookAsync(
            HookEvent.OnBind, device, ct,
            _connection.ClientUuid, _connection.RemoteAddress,
            _connection.Kind != TransportKind.PlainTcp).ConfigureAwait(false);

        if (!bindHook.Allow && !bindHook.Skipped)
        {
            await RefuseImportAsync(BindStatus.Unauthorized,
                $"onBind hook refused the bind ({bindHook})", ct).ConfigureAwait(false);
            return;
        }

        var record = new AttachRecord
        {
            Key = key,
            ConnectionId = _connection.Id,
            ClientUuid = _connection.ClientUuid,
            RemoteAddress = _connection.RemoteAddress,
        };

        BindStatus status = _state.Attachments.TryAttach(key, record);
        if (status != BindStatus.Success)
        {
            string holder = _state.Attachments.TryGet(key, out AttachRecord? current)
                ? current.ConnectionId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "unknown";
            await RefuseImportAsync(status,
                $"device '{busId}' is in use by connection {holder}", ct).ConfigureAwait(false);
            return;
        }

        CaptureOpenResult open = await _state.Capture.OpenAsync(device, ct).ConfigureAwait(false);
        if (!open.Ok)
        {
            _state.Attachments.Detach(key, _connection.Id);
            _log.Write(LogLevel.Warn, LogEvent.DeviceCaptureRefused,
                $"capture of {busId} failed at bind: {open.Reason.Describe()} {open.Detail}",
                new Dictionary<string, object?>
                {
                    ["busid"] = busId,
                    ["reason"] = open.Reason.Describe(),
                    ["win32_error"] = open.Win32Error,
                });
            await RefuseImportAsync(BindStatus.Error,
                $"cannot claim the device: {open.Reason.Describe()} {open.Detail}", ct).ConfigureAwait(false);
            return;
        }

        var attachment = new Attachment
        {
            Device = device,
            Capture = open.Capture!,
            Record = record,
        };

        // A kick, an operator detach or a hotplug removal cancels this attach record. Observe
        // it here so the session actually stops instead of continuing to service a device it
        // no longer holds — this is what makes `devices.kick` a force-detach rather than a
        // bookkeeping change. The registration is released in TeardownAsync.
        if (_sessionLink is { } link)
        {
            attachment.DetachRegistration = record.Detach.Token.Register(
                static state =>
                {
                    try
                    {
                        ((CancellationTokenSource)state!).Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The session ended between the kick and this callback; nothing to do.
                    }
                },
                link);
        }

        _attachments[key] = attachment;
        _connection.Attached[key] = record;
        _state.Exports.RecordBind(key);

        await SendAsync(UsbIpCodec.EncodeImportReply(device.ToUsbIpDevice()), ct).ConfigureAwait(false);

        _log.Write(LogLevel.Info, LogEvent.BindAccepted,
            $"Device {_connection.Id.Value} [{device.VendorId:x4}:{device.ProductId:x4}] BOUND to connection {_connection.Id.Value}",
            new Dictionary<string, object?>
            {
                ["busid"] = busId,
                ["connection_id"] = _connection.Id.Value,
                ["client_ip"] = _connection.RemoteAddress,
                ["vid"] = $"{device.VendorId:x4}",
                ["pid"] = $"{device.ProductId:x4}",
                ["attached_devices_total"] = _state.Attachments.Count,
            });

        _outcome = With(_outcome, imported: true, bindStatus: BindStatus.Success, device: key);
    }

    private async Task<bool> RefuseImportAsync(BindStatus status, string detail, CancellationToken ct)
    {
        // The wire can only say "status != 0"; the BindStatus value itself is what the log,
        // the control plane and the acceptance harness report. Note that no BindStatus value
        // means "too many devices" — see BindStatus.cs for why.
        await SendAsync(UsbIpCodec.EncodeImportError(1), ct).ConfigureAwait(false);

        _log.Write(LogLevel.Warn, LogEvent.BindRefused,
            $"import refused for connection {_connection.Id}: {status} - {detail}",
            new Dictionary<string, object?>
            {
                ["connection_id"] = _connection.Id.Value,
                ["bind_status"] = status.ToString(),
                ["detail"] = detail,
                ["client_ip"] = _connection.RemoteAddress,
            });

        _outcome = With(_outcome, bindStatus: status, bindDetail: detail);
        return false;
    }

    // =====================================================================================
    // URB phase
    // =====================================================================================

    private async Task HandleSubmitAsync(
        UsbIpBasicHeader basic,
        UsbIpCmdSubmit submit,
        ReadOnlyMemory<byte> payload,
        IReadOnlyList<IsoPacketDescriptor> iso,
        CancellationToken ct)
    {
        ConnectionResources resources = _state.Resources;
        BindStatus guard = resources.Check(_connection.InFlight.Count, submit.TransferBufferLength);
        if (guard != BindStatus.Success)
        {
            // Per-connection resource refusal. Deliberately reported as ClientLimit, which is
            // about this connection's queue depth and buffer budget — not about devices.
            _log.Write(LogLevel.Warn, LogEvent.TransferError,
                $"connection {_connection.Id} refused a {submit.TransferBufferLength}-byte submit: {guard} " +
                $"(in flight {_connection.InFlight.Count}/{resources.MaxInFlightUrbs})",
                new Dictionary<string, object?>
                {
                    ["connection_id"] = _connection.Id.Value,
                    ["bind_status"] = guard.ToString(),
                    ["in_flight"] = _connection.InFlight.Count,
                });
            await SendAsync(UsbIpCodec.EncodeRetSubmit(basic, UsbIp.StatusEpipe, ReadOnlySpan<byte>.Empty), ct)
                .ConfigureAwait(false);
            return;
        }

        Attachment? attachment = ResolveAttachment(basic);
        if (attachment is null)
        {
            throw new UsbIpProtocolException(_attachments.Count == 0
                ? "USBIP_CMD_SUBMIT received before a successful import"
                : $"USBIP_CMD_SUBMIT names device id 0x{basic.DevId:x8}, which this connection has not imported");
        }

        IDeviceCapture capture = attachment.Capture;

        if (!_connection.InFlight.TryRegister(basic.SeqNum, (ushort)basic.Ep, out CancellationTokenSource cancellation))
        {
            throw new UsbIpProtocolException(
                $"duplicate in-flight sequence number {basic.SeqNum} on connection {_connection.Id}");
        }

        byte[] buffer = new byte[submit.TransferBufferLength];
        if (payload.Length > 0 && !basic.IsIn)
            payload.Span.CopyTo(buffer);

        // Each URB completes on its own task so one slow endpoint cannot block the reader,
        // and one device's stall cannot delay another device's connection (§5.3).
        Task task = RunTransferAsync(basic, submit, buffer, iso, attachment.Device, capture, cancellation, ct);
        Track(task);
    }

    /// <summary>
    /// Finds the attachment an URB's <c>devid</c> names.
    /// </summary>
    /// <remarks>
    /// <c>usbip_header_basic.devid</c> is <c>busnum &lt;&lt; 16 | devnum</c> of the device the
    /// URB is for, so it is the router when a connection holds more than one device. A peer
    /// that sends a stale or absent devid is still served when exactly one device is held,
    /// because then the target is unambiguous; anything else is a protocol error rather than
    /// a silent write to the wrong device.
    /// </remarks>
    private Attachment? ResolveAttachment(UsbIpBasicHeader basic)
    {
        foreach (Attachment candidate in _attachments.Values)
        {
            uint devId = ((uint)candidate.Device.BusNum << 16) | candidate.Device.DevNum;
            if (devId == basic.DevId) return candidate;
        }

        return _attachments.Count == 1 ? _attachments.Values.First() : null;
    }

    /// <summary>
    /// Resolves the transfer type of an endpoint from the claimed device's own descriptors.
    /// </summary>
    /// <remarks>
    /// USB/IP does not put the transfer type on the wire: <c>usbip_header_basic</c> carries the
    /// endpoint number and the direction only, and the server is expected to know what kind of
    /// pipe that endpoint is (this is exactly what <c>get_pipe()</c> in the reference server's
    /// <c>stub_rx.c</c> does). Endpoint 0 is always control. When the device reported no
    /// descriptor for the endpoint, a data pipe is assumed — control is only ever endpoint 0,
    /// and an isochronous endpoint has to be announced before ISO can be refused or served.
    /// </remarks>
    private static byte ResolveTransferType(UsbDevice device, byte endpointNumber, bool isIn)
    {
        if (endpointNumber == 0)
            return UsbIp.EpTypeControl;

        foreach (EndpointDescriptor endpoint in device.Endpoints)
        {
            if (UsbIp.EndpointNumber(endpoint.Address) != endpointNumber) continue;
            if (endpoint.IsIn != isIn) continue;
            return endpoint.TransferType;
        }

        return UsbIp.EpTypeBulk;
    }

    private async Task RunTransferAsync(
        UsbIpBasicHeader basic,
        UsbIpCmdSubmit submit,
        byte[] buffer,
        IReadOnlyList<IsoPacketDescriptor> iso,
        UsbDevice device,
        IDeviceCapture capture,
        CancellationTokenSource cancellation,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, ct);
        timeout.CancelAfter(TransferTimeout);

        int status;
        int actual = 0;
        IReadOnlyList<IsoPacketDescriptor>? outPackets = null;

        // The wire carries the endpoint *number* and the direction, never the transfer type
        // (usbip_protocol.rst, usbip_header_basic). Resolving the type against the device's own
        // endpoints is what stub_rx.c's get_pipe() does; getting this wrong sends every bulk
        // and interrupt transfer down the control path.
        byte endpointNumber = basic.EndpointNumber;
        bool isIn = basic.IsIn;
        byte epType = ResolveTransferType(device, endpointNumber, isIn);
        byte pipeAddress = (byte)(endpointNumber | (isIn ? UsbIp.EpDirIn : 0));

        try
        {
            if (epType == UsbIp.EpTypeIso && !IsoEnabled)
            {
                status = UsbIp.StatusEpipe;
                _log.Write(LogLevel.Detail, LogEvent.TransferError,
                    $"isochronous transfer on ep {endpointNumber} refused: ISO is not negotiated for this peer",
                    new Dictionary<string, object?> { ["connection_id"] = _connection.Id.Value });
            }
            else if (epType == UsbIp.EpTypeControl)
            {
                TransferResult result = await capture.ControlTransferAsync(
                    submit.Setup, submit.TransferFlags, buffer, (uint)TransferTimeout.TotalMilliseconds,
                    timeout.Token).ConfigureAwait(false);
                status = result.Status;
                actual = result.ActualLength;
            }
            else
            {
                TransferResult result = await capture.DataTransferAsync(
                    pipeAddress, submit.TransferFlags, buffer, (uint)TransferTimeout.TotalMilliseconds,
                    timeout.Token).ConfigureAwait(false);
                status = result.Status;
                actual = result.ActualLength;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status = UsbIp.StatusEconnreset;
        }
        catch (OperationCanceledException)
        {
            status = UsbIp.StatusEshutdown;
            _log.Write(LogLevel.Warn, LogEvent.TransferError,
                $"transfer timed out after {TransferTimeout.TotalSeconds:0.#}s on ep {endpointNumber}",
                new Dictionary<string, object?> { ["connection_id"] = _connection.Id.Value });
        }
        catch (Exception ex)
        {
            status = UsbIp.StatusEpipe;
            _log.Write(LogLevel.Error, LogEvent.TransferError,
                $"transfer failed on ep {endpointNumber}: {ex.Message}",
                new Dictionary<string, object?> { ["connection_id"] = _connection.Id.Value });
        }

        if (iso.Count > 0)
        {
            // Echo the descriptors back so an ISO-capable peer still sees a well-formed frame
            // even when the transfer itself was refused.
            outPackets = iso.Select(p => new IsoPacketDescriptor
            {
                Offset = p.Offset,
                Length = p.Length,
                ActualLength = 0,
                Status = 0,
            }).ToArray();
            actual = 0;
        }

        ReadOnlySpan<byte> data = status == 0 && isIn && epType != UsbIp.EpTypeIso
            ? buffer.AsSpan(0, Math.Clamp(actual, 0, buffer.Length))
            : ReadOnlySpan<byte>.Empty;

        // The transfer class of the data stage, taken from the endpoint that produced it. This is
        // the same resolution the read side already uses (ResolveTransferType), and it is what the
        // compression layer needs to decide whether the payload may be compressed at all. A reply
        // with no data stage declares Control, which sends the whole frame raw.
        PayloadKind replyKind = data.Length == 0
            ? PayloadKind.Control
            : epType switch
            {
                UsbIp.EpTypeBulk => PayloadKind.Bulk,
                UsbIp.EpTypeInterrupt => PayloadKind.Interrupt,
                UsbIp.EpTypeIso => PayloadKind.Isochronous,
                _ => PayloadKind.Control,
            };

        byte[] reply = UsbIpCodec.EncodeRetSubmit(
            basic, status, data,
            startFrame: submit.StartFrame,
            numberOfPackets: submit.NumberOfPackets,
            errorCount: 0,
            packets: outPackets,
            // An OUT transfer's payload is not echoed back, so actual_length has to be carried
            // explicitly or the client sees a successful URB that wrote nothing.
            actualLength: (uint)Math.Max(actual, 0));

        try
        {
            if (_connection.InFlight.IsUnlinked(basic.SeqNum))
            {
                // This URB was cancelled by name: the unlink path owns its reply, and sending
                // a RET_SUBMIT as well would answer the same request twice.
                _log.Write(LogLevel.Detail, LogEvent.TransferError,
                    $"URB seqnum={basic.SeqNum} was unlinked; the unlink reply is its only answer",
                    new Dictionary<string, object?> { ["connection_id"] = _connection.Id.Value });
                return;
            }

            await SendAsync(reply, replyKind, ct).ConfigureAwait(false);
            _outcome = With(_outcome, completed: _outcome.SubmitsCompleted + 1);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _connection.InFlight.Complete(basic.SeqNum);
            cancellation.Dispose();
        }
    }

    private async Task HandleUnlinkAsync(UsbIpBasicHeader basic, UsbIpCmdUnlink unlink, CancellationToken ct)
    {
        bool cancelled = _connection.InFlight.CancelForUnlink(unlink.UnlinkSeqNum);

        // Wire semantics, per usbip_protocol.rst and the reference server (stub_rx.c /
        // stub_tx.c): a URB cancelled while it is still in flight is answered by the unlink
        // alone, carrying the cancelled transfer's status (-ECONNRESET), and it never gets its
        // own RET_SUBMIT. An unlink whose target has already completed is answered with status
        // 0, because there was nothing left to cancel. That is exactly what
        // stub_enqueue_ret_unlink(sdev, seqnum, urb->status) and its
        // stub_enqueue_ret_unlink(sdev, seqnum, 0) fallback do.
        int status = cancelled ? UsbIp.StatusEconnreset : 0;
        await SendAsync(UsbIpCodec.EncodeRetUnlink(basic, status), ct).ConfigureAwait(false);

        _log.Write(LogLevel.Detail, LogEvent.TransferError,
            $"unlink seqnum={unlink.UnlinkSeqNum} on connection {_connection.Id}: {(cancelled ? "cancelled" : "already complete")}",
            new Dictionary<string, object?>
            {
                ["connection_id"] = _connection.Id.Value,
                ["unlink_seqnum"] = unlink.UnlinkSeqNum,
                ["status"] = status,
            });
    }

    // =====================================================================================
    // Teardown
    // =====================================================================================

    private async Task TeardownAsync()
    {
        // Snapshot and clear first: teardown must be idempotent, and a kick arriving while we
        // are releasing must not re-enter this path.
        Attachment[] held = _attachments.Values.ToArray();
        _attachments.Clear();

        // Released before anything else: once the session is ending, a later kick must not
        // re-enter this session's cancellation path.
        foreach (Attachment attachment in held)
        {
            try
            {
                attachment.DetachRegistration.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _connection.InFlight.CancelAll();

        Task[] pending;
        lock (_pendingGate) pending = _pendingTransfers.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // Nothing to do: the connection is going away and the writes are best-effort.
            }
        }

        _connection.InFlight.Clear();

        // Every held device is handed back: the attach record is dropped, our handle closed,
        // and the device released to whatever owned it before (the onPassToKernel parity item
        // in ARCHITECTURE.md §4.1) — one loop over the attachments, however many there are.
        foreach (Attachment attachment in held)
        {
            _state.Attachments.Detach(attachment.Record.Key, _connection.Id);
            _connection.Attached.TryRemove(attachment.Record.Key, out _);

            try
            {
                await attachment.Capture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Warn, LogEvent.DeviceReleased,
                    $"closing capture for {attachment.Record.Key.BusId} failed: {ex.Message}");
            }

            try
            {
                await _state.Capture.ReleaseAsync(attachment.Device, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Warn, LogEvent.DeviceReleased,
                    $"release of {attachment.Device.BusId} failed: {ex.Message}");
            }

            await _state.RunHookAsync(
                HookEvent.OnUnbind, attachment.Device, CancellationToken.None,
                _connection.ClientUuid, _connection.RemoteAddress,
                _connection.Kind != TransportKind.PlainTcp).ConfigureAwait(false);
        }
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    /// <summary>
    /// Writes a whole reply frame with every byte classified <see cref="PayloadKind.Control"/>.
    /// </summary>
    /// <remarks>
    /// Correct for every reply that is not a URB completion: the devlist, import and device-info
    /// bodies, unlink acknowledgements and refusal replies have no data stage, so there is nothing
    /// to classify and nothing that may be compressed.
    /// </remarks>
    private async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) =>
        await SendAsync(frame, PayloadKind.Control, ct).ConfigureAwait(false);

    /// <summary>
    /// Writes one reply frame, declaring the transfer class of its data stage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>USBIP_RET_SUBMIT</c> frame is the 48-byte URB header followed by the data stage, and the
    /// two are written as <b>two transport writes under one hold of the write lock</b>: the header as
    /// <see cref="PayloadKind.Control"/> (never compressed), the data as
    /// <paramref name="dataKind"/>. That is the whole point of the change — until it, every byte the
    /// session wrote was classified control, so the LZ4 decorator's
    /// <c>WritePayloadAsync</c> opt-in was unreachable from production and no URB ever compressed.
    /// </para>
    /// <para>
    /// Two writes are not two logical frames on the wire buffer: nothing else can interleave because
    /// the write lock is held across both, and the peer's transport reassembles by byte count, which
    /// is what <c>UsbIpSession</c> does on the read side too. A transport that does not implement
    /// <see cref="IPayloadKindTransport"/>, or one that reports
    /// <see cref="IPayloadKindTransport.CompressionActive"/> false, still gets a single write of the
    /// whole frame — byte-for-byte what this method did before the classification existed.
    /// </para>
    /// <para>
    /// The split is also skipped when the data stage could not be compressed anyway: a transfer class
    /// that is never compressed (control, isochronous) or a data stage below
    /// <see cref="IPayloadKindTransport.CompressionThresholdBytes"/>. That is the same test the LZ4
    /// decorator makes in <c>WriteFrameAsync</c>, hoisted one level so a reply the compressor would
    /// refuse does not pay a second frame header and a second CRC-32C pass for nothing. It is the
    /// reason that threshold is published on the interface rather than kept private to the decorator.
    /// </para>
    /// </remarks>
    private async ValueTask SendAsync(ReadOnlyMemory<byte> frame, PayloadKind dataKind, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (frame.Length == 0)
            {
                return;
            }

            int dataLength = frame.Length - UsbIp.HeaderSize;
            bool compressible = dataKind is PayloadKind.Bulk or PayloadKind.Interrupt;

            if (!compressible
                || _transport is not IPayloadKindTransport classified
                || !classified.CompressionActive
                || dataLength < classified.CompressionThresholdBytes)
            {
                await _transport.WriteAsync(frame, ct).ConfigureAwait(false);
                return;
            }

            await classified.WritePayloadAsync(PayloadKind.Control, frame[..UsbIp.HeaderSize], ct)
                .ConfigureAwait(false);
            await classified.WritePayloadAsync(dataKind, frame[UsbIp.HeaderSize..], ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Track(Task task)
    {
        lock (_pendingGate)
        {
            // The list exists so teardown can wait for the transfers that are still running - it is
            // a barrier, not a history. Dropping the finished ones before each add keeps it at the
            // in-flight count (never more than ConnectionResources.MaxInFlightUrbs transfers), so a
            // long run cannot accumulate one entry - and one ever-larger backing array - per URB
            // ever served. RemoveAll on a list of that size is a handful of pointer comparisons and
            // allocates nothing, which matters at tens of thousands of submits per second.
            _pendingTransfers.RemoveAll(static t => t.IsCompleted);
            _pendingTransfers.Add(task);
            _connection.PendingTransfers = _pendingTransfers.Count;
        }
    }

    private void RecordProtocolError(string message)
    {
        _outcome = With(_outcome, protocolError: message);
        _log.Write(LogLevel.Warn, LogEvent.ProtocolError,
            $"connection {_connection.Id} protocol error: {message}",
            new Dictionary<string, object?>
            {
                ["connection_id"] = _connection.Id.Value,
                ["client_ip"] = _connection.RemoteAddress,
            });
    }

    private static SessionOutcome With(
        SessionOutcome o,
        bool? listed = null,
        bool? imported = null,
        BindStatus? bindStatus = null,
        string? bindDetail = null,
        DeviceKey? device = null,
        long? submits = null,
        long? completed = null,
        long? unlinks = null,
        string? protocolError = null) => new()
        {
            Listed = listed ?? o.Listed,
            Imported = imported ?? o.Imported,
            BindStatus = bindStatus ?? o.BindStatus,
            BindDetail = bindDetail ?? o.BindDetail,
            Device = device ?? o.Device,
            SubmitsReceived = submits ?? o.SubmitsReceived,
            SubmitsCompleted = completed ?? o.SubmitsCompleted,
            UnlinksReceived = unlinks ?? o.UnlinksReceived,
            ProtocolErrors = protocolError is null
                ? o.ProtocolErrors
                : o.ProtocolErrors.Append(protocolError).ToArray(),
        };
}
