using Vht.Core.Capture;
using Vht.Core.Concurrency;
using Vht.Core.Hooks;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Policy;
using Vht.Core.Protocol;
using Vht.Core.Server;
using Vht.Core.Transport;

namespace Vht.Core.Tests;

/// <summary>
/// The whole session state machine driven end to end over an in-memory transport: the
/// operation handshake, the URB loop, unlink, hotplug, kick and teardown. No socket, no
/// hardware, no Linux client — but the same code path a real client exercises.
/// </summary>
public sealed class SessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class Harness
    {
        public required VhtServerState State { get; init; }
        public required FakeCaptureProvider Capture { get; init; }
        public required FakeLink Link { get; init; }
        public required Connection Connection { get; init; }
        public required UsbIpSession Session { get; init; }
        public required Task Run { get; init; }

        public CancellationTokenSource Clock { get; } = new(Timeout);

        public CancellationToken Ct => Clock.Token;

        public Task CloseAndWaitAsync()
        {
            Link.Close();
            return Run.WaitAsync(Timeout);
        }
    }

    private static Harness Start(VhtServerState state, ushort id = 1, TransportKind kind = TransportKind.PlainTcp)
    {
        var link = new FakeLink(kind);
        Connection connection = TestConnections.New(id, kind);
        state.Connections.Add(connection);

        var session = new UsbIpSession(state, connection, link.ServerTransport);
        Task run = session.RunAsync(CancellationToken.None);

        return new Harness
        {
            State = state,
            Capture = (FakeCaptureProvider)state.Capture,
            Link = link,
            Connection = connection,
            Session = session,
            Run = run,
        };
    }

    private static async Task<VhtServerState> ExportedAsync(params UsbDevice[] devices)
    {
        (VhtServerState state, _, _) = TestServer.Create(devices);
        await state.RefreshExportsAsync(CancellationToken.None);
        return state;
    }

    private static uint DevId(UsbDevice device) => (device.BusNum << 16) | device.DevNum;

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

    // =====================================================================================
    // Handshake and transfers
    // =====================================================================================

    [Fact]
    public async Task DeviceListThenImportThenABulkInTransferRoundTrips()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendDevlistRequest();
        IReadOnlyList<UsbIpDevice> listed = await harness.Link.ReadDevlistAsync(harness.Ct);

        UsbIpDevice only = Assert.Single(listed);
        Assert.Equal(device.BusId, only.BusId);
        Assert.Equal(device.VendorId, only.VendorId);
        Assert.Equal(device.ProductId, only.ProductId);
        Assert.Equal(UsbIp.SpeedHigh, only.Speed);

        harness.Link.SendImportRequest(device.BusId);
        UsbIpDevice? imported = await harness.Link.ReadImportReplyAsync(harness.Ct);

        Assert.NotNull(imported);
        Assert.Equal(device.BusId, imported.BusId);
        Assert.Equal(1, state.Attachments.Count);

        ((FakeCaptureProvider)state.Capture).Opened[device.Key].Fill = 0x77;
        harness.Link.SendSubmit(1, DevId(device), UsbIp.DirIn, 1, numberOfPackets: UsbIp.NoIsoPackets,
            transferBufferLength: 16);

        FakeLink.UrbReply reply = await harness.Link.ReadUrbReplyAsync(harness.Ct);

        Assert.True(reply.IsSubmit);
        Assert.Equal(1u, reply.SeqNum);
        Assert.Equal(0, reply.Status);
        Assert.Equal(16u, reply.ActualLength);
        Assert.Equal(16, reply.Data.Length);
        Assert.All(reply.Data, b => Assert.Equal(0x77, b));

        await harness.CloseAndWaitAsync();

        Assert.True(harness.Session.Outcome.Listed);
        Assert.True(harness.Session.Outcome.Imported);
        Assert.Equal(BindStatus.Success, harness.Session.Outcome.BindStatus);
        Assert.Equal(1, harness.Session.Outcome.SubmitsReceived);
        Assert.Equal(1, harness.Session.Outcome.SubmitsCompleted);
    }

    [Fact]
    public async Task AnOutBulkTransferCarriesItsPayloadToTheDevice()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        byte[] payload = { 0xde, 0xad, 0xbe, 0xef };
        harness.Link.SendSubmit(7, DevId(device), UsbIp.DirOut, 2, payload);

        FakeLink.UrbReply reply = await harness.Link.ReadUrbReplyAsync(harness.Ct);

        Assert.True(reply.IsSubmit);
        Assert.Equal(0, reply.Status);
        Assert.Equal(4u, reply.ActualLength);
        Assert.Empty(reply.Data);

        FakeCapture capture = ((FakeCaptureProvider)state.Capture).Opened[device.Key];
        Assert.Equal(1, capture.DataCalls);
        Assert.Contains((byte)0x02, capture.EndpointsSeen);

        await harness.CloseAndWaitAsync();
    }

    [Fact]
    public async Task AControlTransferIsServiced()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        // A GET_DESCRIPTOR-shaped setup packet, control OUT (no data stage).
        harness.Link.SendSubmit(3, DevId(device), UsbIp.DirOut, 0, setup: 0x01000680ul);

        FakeLink.UrbReply reply = await harness.Link.ReadUrbReplyAsync(harness.Ct);

        Assert.True(reply.IsSubmit);
        Assert.Equal(0, reply.Status);
        Assert.Equal(1, ((FakeCaptureProvider)state.Capture).Opened[device.Key].ControlCalls);

        await harness.CloseAndWaitAsync();
    }

    [Fact]
    public async Task AnEmptyDeviceListIsAWellFormedReply()
    {
        VhtServerState state = await ExportedAsync();
        Harness harness = Start(state);

        harness.Link.SendDevlistRequest();
        IReadOnlyList<UsbIpDevice> listed = await harness.Link.ReadDevlistAsync(harness.Ct);

        Assert.Empty(listed);
        await harness.CloseAndWaitAsync();
    }

    // =====================================================================================
    // Refusals
    // =====================================================================================

    [Fact]
    public async Task AnImportOfAnUnknownBusIdIsRefused()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendImportRequest("9-9");
        UsbIpDevice? imported = await harness.Link.ReadImportReplyAsync(harness.Ct);

        Assert.Null(imported);
        Assert.Equal(0, state.Attachments.Count);

        await harness.CloseAndWaitAsync();
        Assert.Equal(BindStatus.Unknown, harness.Session.Outcome.BindStatus);
    }

    [Fact]
    public async Task ADeviceAlreadyHeldByAnotherClientIsRefusedAsBusy()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);

        Harness first = Start(state, id: 1);
        first.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await first.Link.ReadImportReplyAsync(first.Ct));

        Harness second = Start(state, id: 2);
        second.Link.SendImportRequest(device.BusId);
        UsbIpDevice? refused = await second.Link.ReadImportReplyAsync(second.Ct);

        Assert.Null(refused);
        await second.CloseAndWaitAsync();
        Assert.Equal(BindStatus.DeviceBusy, second.Session.Outcome.BindStatus);
        Assert.Equal(1, state.Attachments.Count);

        await first.CloseAndWaitAsync();
    }

    [Fact]
    public async Task ADeviceIgnoredAfterItWasExportedIsRefusedAtBindTime()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        // The export happened first, so the policy change must be re-evaluated at bind time.
        state.Policy.Ignore(new DeviceRule { VendorId = device.VendorId, ProductId = device.ProductId });

        harness.Link.SendImportRequest(device.BusId);
        UsbIpDevice? imported = await harness.Link.ReadImportReplyAsync(harness.Ct);

        Assert.Null(imported);
        await harness.CloseAndWaitAsync();
        Assert.Equal(BindStatus.Unauthorized, harness.Session.Outcome.BindStatus);
    }

    [Fact]
    public async Task APasswordProtectedDeviceIsNotOfferedOrImportableOnPlaintext()
    {
        UsbDevice device = TestDevices.Create(1, requiresPassword: true);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendDevlistRequest();
        Assert.Empty(await harness.Link.ReadDevlistAsync(harness.Ct));

        harness.Link.SendImportRequest(device.BusId);
        Assert.Null(await harness.Link.ReadImportReplyAsync(harness.Ct));

        await harness.CloseAndWaitAsync();
        Assert.Equal(BindStatus.PasswordRequired, harness.Session.Outcome.BindStatus);
    }

    [Fact]
    public async Task APasswordProtectedDeviceIsImportableOnTheAuthenticatedListener()
    {
        UsbDevice device = TestDevices.Create(1, requiresPassword: true);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state, kind: TransportKind.Tls);

        harness.Link.SendDevlistRequest();
        UsbIpDevice only = Assert.Single(await harness.Link.ReadDevlistAsync(harness.Ct));
        Assert.Equal(device.BusId, only.BusId);

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        await harness.CloseAndWaitAsync();
        Assert.Equal(BindStatus.Success, harness.Session.Outcome.BindStatus);
    }

    [Fact]
    public async Task ARefusingBindHookBlocksTheImport()
    {
        UsbDevice device = TestDevices.Create(1);
        var runner = new FakeProcessRunner
        {
            Outcome = new ProcessOutcome { Started = true, ExitCode = HookVocabulary.ExitNo },
        };
        var hooks = new HookRunner(new HookOptions().Set(HookEvent.OnBind, "hook.cmd"), runner,
            new StructuredLogger(LogLevel.Critical));

        (VhtServerState state, _, _) = TestServer.Create(new[] { device }, hooks: () => hooks);
        await state.RefreshExportsAsync(CancellationToken.None);

        Harness harness = Start(state);
        harness.Link.SendImportRequest(device.BusId);

        Assert.Null(await harness.Link.ReadImportReplyAsync(harness.Ct));
        await harness.CloseAndWaitAsync();

        Assert.Equal(BindStatus.Unauthorized, harness.Session.Outcome.BindStatus);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task ACaptureFailureAtBindIsReportedAsAnError()
    {
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, FakeCaptureProvider capture, _) = TestServer.Create(new[] { device });
        await state.RefreshExportsAsync(CancellationToken.None);

        capture.ForcedFailure = CaptureOpenResult.Failure(
            CaptureReason.DriverHoldsDevice, "the function driver will not release the device", 5);

        Harness harness = Start(state);
        harness.Link.SendImportRequest(device.BusId);

        Assert.Null(await harness.Link.ReadImportReplyAsync(harness.Ct));
        await harness.CloseAndWaitAsync();

        Assert.Equal(BindStatus.Error, harness.Session.Outcome.BindStatus);
        Assert.Contains("driver_holds_device", harness.Session.Outcome.BindDetail, StringComparison.Ordinal);
        Assert.Equal(0, state.Attachments.Count);
    }

    [Fact]
    public async Task ASubmitBeforeAnImportIsAProtocolErrorThatClosesTheConnection()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendSubmit(1, DevId(device), UsbIp.DirIn, 1, numberOfPackets: UsbIp.NoIsoPackets,
            transferBufferLength: 8);

        await harness.Run.WaitAsync(Timeout);

        Assert.False(harness.Session.Outcome.Imported);
        Assert.NotEmpty(harness.Session.Outcome.ProtocolErrors);
    }

    [Fact]
    public async Task AForeignProtocolVersionClosesTheConnection()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        var header = new byte[UsbIpCodec.OpHeaderSize];
        BigEndian.WriteU16(header, 0, 0x0110);
        BigEndian.WriteU16(header, 2, UsbIp.OpReqDevlist);
        harness.Link.Send(header);

        await harness.Run.WaitAsync(Timeout);

        Assert.NotEmpty(harness.Session.Outcome.ProtocolErrors);
    }

    // =====================================================================================
    // Unlink
    // =====================================================================================

    [Fact]
    public async Task AnUnlinkCancelsTheNamedUrbAndIsItsOnlyReply()
    {
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, FakeCaptureProvider capture, _) = TestServer.Create(new[] { device });
        await state.RefreshExportsAsync(CancellationToken.None);
        capture.GateCaptures = true;

        Harness harness = Start(state);
        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        harness.Link.SendSubmit(1, DevId(device), UsbIp.DirIn, 1, numberOfPackets: UsbIp.NoIsoPackets,
            transferBufferLength: 64);
        await WaitUntilAsync(() => harness.Connection.InFlight.Count == 1, harness.Ct);

        harness.Link.SendUnlink(2, DevId(device), 1);

        FakeLink.UrbReply reply = await harness.Link.ReadUrbReplyAsync(harness.Ct);

        // The unlink carries the cancelled transfer's status and the URB gets no RET_SUBMIT.
        Assert.True(reply.IsUnlink);
        Assert.Equal(2u, reply.SeqNum);
        Assert.Equal(UsbIp.StatusEconnreset, reply.Status);

        // Nothing else is written: a second read must time out rather than deliver a RET_SUBMIT.
        using var shortClock = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await harness.Link.ReadUrbReplyAsync(shortClock.Token));

        // The transfer task unwinds after the reply, so the entry clears a moment later.
        await WaitUntilAsync(() => harness.Connection.InFlight.Count == 0, harness.Ct);
        await harness.CloseAndWaitAsync();
        Assert.Equal(1, harness.Session.Outcome.UnlinksReceived);
    }
    [Fact]
    public async Task AnUnlinkForAnAlreadyCompletedUrbIsAcknowledgedWithZero()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        harness.Link.SendSubmit(1, DevId(device), UsbIp.DirIn, 1, numberOfPackets: UsbIp.NoIsoPackets,
            transferBufferLength: 8);
        Assert.True((await harness.Link.ReadUrbReplyAsync(harness.Ct)).IsSubmit);

        harness.Link.SendUnlink(2, DevId(device), 1);

        FakeLink.UrbReply reply = await harness.Link.ReadUrbReplyAsync(harness.Ct);

        Assert.True(reply.IsUnlink);
        Assert.Equal(0, reply.Status);

        await harness.CloseAndWaitAsync();
    }

    // =====================================================================================
    // Isochronous
    // =====================================================================================

    [Fact]
    public async Task AnIsochronousTransferIsRefusedWhenItWasNotNegotiated()
    {
        UsbDevice device = TestDevices.Create(1, withIsoEndpoint: true);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        // An ISO IN submit carries no transfer buffer, only four packet descriptors.
        byte[] descriptors = new byte[4 * UsbIp.IsoPacketDescriptorSize];
        harness.Link.SendSubmit(
            5, DevId(device), UsbIp.DirIn, 2,
            descriptors, numberOfPackets: 4, transferBufferLength: 0);

        byte[] header = await harness.Link.ReadExactlyAsync(UsbIp.HeaderSize, harness.Ct);

        Assert.Equal(UsbIp.RetSubmit, BigEndian.ReadU32(header, 0));
        Assert.Equal(UsbIp.StatusEpipe, BigEndian.ReadI32(header, 20));
        Assert.Equal(4u, BigEndian.ReadU32(header, 32));

        // The echoed descriptors follow the header; consume them so the stream stays framed.
        await harness.Link.ReadExactlyAsync(4 * UsbIp.IsoPacketDescriptorSize, harness.Ct);
        Assert.Equal(0, ((FakeCaptureProvider)state.Capture).Opened[device.Key].DataCalls);

        await harness.CloseAndWaitAsync();
    }

    // =====================================================================================
    // Per-connection resource guard
    // =====================================================================================

    [Fact]
    public async Task ThePerConnectionUrbGuardRefusesAQueueOverflowWithoutTouchingTheDevice()
    {
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, FakeCaptureProvider capture, _) = TestServer.Create(
            new[] { device }, configure: o => o.MaxInFlightUrbs = 1);
        await state.RefreshExportsAsync(CancellationToken.None);
        capture.GateCaptures = true;

        Harness harness = Start(state);
        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        harness.Link.SendSubmit(1, DevId(device), UsbIp.DirIn, 1, numberOfPackets: UsbIp.NoIsoPackets,
            transferBufferLength: 8);
        await WaitUntilAsync(() => harness.Connection.InFlight.Count == 1, harness.Ct);

        harness.Link.SendSubmit(2, DevId(device), UsbIp.DirIn, 1, numberOfPackets: UsbIp.NoIsoPackets,
            transferBufferLength: 8);

        FakeLink.UrbReply reply = await harness.Link.ReadUrbReplyAsync(harness.Ct);

        Assert.Equal(UsbIp.StatusEpipe, reply.Status);
        Assert.Equal(2u, reply.SeqNum);

        // The device is still attached: this was a queue-depth refusal, not a device refusal.
        Assert.Equal(1, state.Attachments.Count);
        Assert.True(state.Exports.Contains(device.Key));

        await harness.CloseAndWaitAsync();
    }

    // =====================================================================================
    // Kick and teardown
    // =====================================================================================

    [Fact]
    public async Task KickForceDetachesTheClientAndTearsTheSessionDown()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));
        Assert.Equal(1, state.Attachments.Count);

        FakeCapture capture = ((FakeCaptureProvider)state.Capture).Opened[device.Key];

        Assert.True(state.KickDevice(device.Key, out AttachRecord? kicked, out _));
        Assert.NotNull(kicked);

        // The session must actually stop, not just lose its bookkeeping entry.
        await harness.Run.WaitAsync(Timeout);

        Assert.Equal(0, state.Attachments.Count);
        Assert.Equal(1, capture.Disposals);
        Assert.Equal(1, ((FakeCaptureProvider)state.Capture).ReleaseCount);
    }

    [Fact]
    public async Task ClosingTheConnectionDetachesAndReleasesTheDevice()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        await harness.CloseAndWaitAsync();

        Assert.Equal(0, state.Attachments.Count);
        Assert.Equal(1, ((FakeCaptureProvider)state.Capture).ReleaseCount);
    }

    [Fact]
    public async Task ACancelledConnectionEndsTheSessionCleanly()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        harness.Connection.Cancellation.Cancel();

        await harness.Run.WaitAsync(Timeout);

        Assert.Equal(0, state.Attachments.Count);
    }

    [Fact]
    public async Task ASecondImportOnOneConnectionIsIdempotentForTheSameDevice()
    {
        UsbDevice device = TestDevices.Create(1);
        VhtServerState state = await ExportedAsync(device);
        Harness harness = Start(state);

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        harness.Link.SendImportRequest(device.BusId);
        Assert.NotNull(await harness.Link.ReadImportReplyAsync(harness.Ct));

        Assert.Single(state.Attachments.Snapshot());
        // The second import reuses the existing claim instead of opening a second handle.
        Assert.Equal(1, ((FakeCaptureProvider)state.Capture).OpenCount);
        await harness.CloseAndWaitAsync();
    }
}
