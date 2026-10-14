using Vht.Core.Concurrency;
using Vht.Core.Model;
using Vht.Core.Protocol;
using Vht.Core.Server;

namespace Vht.Core.Tests;

/// <summary>
/// The headline requirement: <b>N devices attached simultaneously, with no cap.</b>
/// </summary>
/// <remarks>
/// These tests drive N independent sessions against one <see cref="VhtServerState"/>, each on
/// its own in-memory transport, each importing a different device and running real URB traffic
/// through the same code path the plaintext listener uses. Nothing here consults a licence, and
/// no count is ever compared against a limit — see <c>BindStatusInvariantTests</c> for the
/// machine-checked proof that no such limit exists in the vocabulary or the source.
/// </remarks>
public sealed class ConcurrencyTests
{
    private const int DeviceCount = 32;

    private sealed class Fleet
    {
        public required VhtServerState State { get; init; }
        public required UsbDevice[] Devices { get; init; }
        public required FakeLink[] Links { get; init; }
        public required Connection[] Connections { get; init; }
        public required UsbIpSession[] Sessions { get; init; }
        public required Task[] Runs { get; init; }
        public required CancellationTokenSource Clock { get; init; }

        public async Task ShutdownAsync()
        {
            foreach (FakeLink link in Links) link.Close();
            await Task.WhenAll(Runs).WaitAsync(TimeSpan.FromSeconds(15));
            Clock.Dispose();
        }
    }

    private static async Task<Fleet> DeployAsync(int count)
    {
        UsbDevice[] devices = TestDevices.Range(count);
        (VhtServerState state, _, _) = TestServer.Create(devices);
        await state.RefreshExportsAsync(CancellationToken.None);

        var links = new FakeLink[count];
        var connections = new Connection[count];
        var sessions = new UsbIpSession[count];
        var runs = new Task[count];

        for (int i = 0; i < count; i++)
        {
            links[i] = new FakeLink();
            connections[i] = TestConnections.New((ushort)(i + 1), remote: $"127.0.0.1:{5000 + i}");
            state.Connections.Add(connections[i]);
            sessions[i] = new UsbIpSession(state, connections[i], links[i].ServerTransport);
            runs[i] = sessions[i].RunAsync(CancellationToken.None);
        }

        return new Fleet
        {
            State = state,
            Devices = devices,
            Links = links,
            Connections = connections,
            Sessions = sessions,
            Runs = runs,
            Clock = new CancellationTokenSource(TimeSpan.FromSeconds(30)),
        };
    }

    private static uint DevId(UsbDevice device) => (device.BusNum << 16) | device.DevNum;

    /// <summary>Lists, imports and runs <paramref name="urbCount"/> bulk IN transfers.</summary>
    private static async Task RunClientAsync(
        FakeLink link, UsbDevice device, int deviceCount, int urbCount, CancellationToken ct)
    {
        link.SendDevlistRequest();
        IReadOnlyList<UsbIpDevice> listed = await link.ReadDevlistAsync(ct);
        Assert.Equal(deviceCount, listed.Count);

        link.SendImportRequest(device.BusId);
        UsbIpDevice? imported = await link.ReadImportReplyAsync(ct);
        Assert.NotNull(imported);
        Assert.Equal(device.BusId, imported.BusId);

        for (uint seq = 1; seq <= urbCount; seq++)
        {
            link.SendSubmit(seq, DevId(device), UsbIp.DirIn, 1,
                numberOfPackets: UsbIp.NoIsoPackets, transferBufferLength: 16);

            FakeLink.UrbReply reply = await link.ReadUrbReplyAsync(ct);

            Assert.True(reply.IsSubmit);
            Assert.Equal(seq, reply.SeqNum);
            Assert.Equal(0, reply.Status);
            Assert.Equal(16u, reply.ActualLength);
            Assert.Equal(16, reply.Data.Length);
        }
    }

    // =====================================================================================
    // N at once
    // =====================================================================================

    [Fact]
    public async Task ThirtyTwoDevicesAreAttachedSimultaneouslyAndAllServeTraffic()
    {
        Fleet fleet = await DeployAsync(DeviceCount);
        CancellationToken ct = fleet.Clock.Token;

        await Task.WhenAll(Enumerable.Range(0, DeviceCount).Select(i =>
            RunClientAsync(fleet.Links[i], fleet.Devices[i], DeviceCount, urbCount: 3, ct)));

        // Every device is attached at the same time. This is the count that a device-cap
        // implementation would have refused; here it is simply the number of devices present.
        Assert.Equal(DeviceCount, fleet.State.Attachments.Count);
        Assert.Equal(DeviceCount, fleet.State.Connections.Count);
        Assert.Equal(DeviceCount, fleet.State.Exports.Count);

        await fleet.ShutdownAsync();

        foreach (UsbIpSession session in fleet.Sessions)
        {
            Assert.True(session.Outcome.Imported, session.Outcome.ToString());
            Assert.Equal(BindStatus.Success, session.Outcome.BindStatus);
            Assert.Equal(3, session.Outcome.SubmitsCompleted);
        }

        Assert.Empty(fleet.State.Attachments.Snapshot());
    }

    [Fact]
    public async Task EachSessionReadsBackItsOwnDevicesData()
    {
        Fleet fleet = await DeployAsync(DeviceCount);
        CancellationToken ct = fleet.Clock.Token;

        var capture = (FakeCaptureProvider)fleet.State.Capture;

        // Import first (that is what creates each capture), then give every device a
        // distinguishable payload before any traffic runs.
        foreach (int i in Enumerable.Range(0, DeviceCount))
        {
            fleet.Links[i].SendImportRequest(fleet.Devices[i].BusId);
            Assert.NotNull(await fleet.Links[i].ReadImportReplyAsync(ct));
        }

        foreach (UsbDevice device in fleet.Devices)
            capture.Opened[device.Key].Fill = (byte)(device.DevNum & 0xff);

        await Task.WhenAll(Enumerable.Range(0, DeviceCount).Select(async i =>
        {
            FakeLink link = fleet.Links[i];
            UsbDevice device = fleet.Devices[i];

            link.SendSubmit(1, DevId(device), UsbIp.DirIn, 1,
                numberOfPackets: UsbIp.NoIsoPackets, transferBufferLength: 8);

            FakeLink.UrbReply reply = await link.ReadUrbReplyAsync(ct);

            Assert.Equal(0, reply.Status);
            Assert.All(reply.Data, b => Assert.Equal((byte)(device.DevNum & 0xff), b));
        }));

        await fleet.ShutdownAsync();
    }

    [Fact]
    public async Task EverySimultaneousImportIsAcceptedAndNoneIsRefused()
    {
        Fleet fleet = await DeployAsync(DeviceCount);
        CancellationToken ct = fleet.Clock.Token;

        await Task.WhenAll(Enumerable.Range(0, DeviceCount).Select(async i =>
        {
            fleet.Links[i].SendImportRequest(fleet.Devices[i].BusId);
            Assert.NotNull(await fleet.Links[i].ReadImportReplyAsync(ct));
        }));

        Assert.Equal(DeviceCount, fleet.State.Attachments.Count);
        Assert.All(fleet.Sessions, s => Assert.Equal(BindStatus.Success, s.Outcome.BindStatus));

        await fleet.ShutdownAsync();
    }

    // =====================================================================================
    // Independence: one device's trouble is not another's
    // =====================================================================================

    [Fact]
    public async Task KickingHalfTheClientsLeavesTheRestServingTraffic()
    {
        const int Half = DeviceCount / 2;
        Fleet fleet = await DeployAsync(DeviceCount);
        CancellationToken ct = fleet.Clock.Token;

        await Task.WhenAll(Enumerable.Range(0, DeviceCount).Select(async i =>
        {
            fleet.Links[i].SendImportRequest(fleet.Devices[i].BusId);
            Assert.NotNull(await fleet.Links[i].ReadImportReplyAsync(ct));
        }));

        Assert.Equal(DeviceCount, fleet.State.Attachments.Count);

        for (int i = 0; i < Half; i++)
            Assert.True(fleet.State.KickDevice(fleet.Devices[i].Key, out _, out _));

        // The kicked sessions must actually end, not merely lose their table entry.
        await Task.WhenAll(fleet.Runs.Take(Half)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(Half, fleet.State.Attachments.Count);

        // The survivors keep serving: a kick on one device is not a server-wide event.
        for (int i = Half; i < DeviceCount; i++)
        {
            fleet.Links[i].SendSubmit(1, DevId(fleet.Devices[i]), UsbIp.DirIn, 1,
                numberOfPackets: UsbIp.NoIsoPackets, transferBufferLength: 4);

            FakeLink.UrbReply reply = await fleet.Links[i].ReadUrbReplyAsync(ct);

            Assert.Equal(0, reply.Status);
            Assert.Equal(4u, reply.ActualLength);
        }

        await fleet.ShutdownAsync();
    }

    [Fact]
    public async Task AStalledDeviceDoesNotDelayAnotherDevicesConnection()
    {
        Fleet fleet = await DeployAsync(2);
        CancellationToken ct = fleet.Clock.Token;

        var capture = (FakeCaptureProvider)fleet.State.Capture;

        fleet.Links[0].SendImportRequest(fleet.Devices[0].BusId);
        Assert.NotNull(await fleet.Links[0].ReadImportReplyAsync(ct));

        fleet.Links[1].SendImportRequest(fleet.Devices[1].BusId);
        Assert.NotNull(await fleet.Links[1].ReadImportReplyAsync(ct));

        // Device 1's transfers block forever; device 2 is free-running.
        capture.Opened[fleet.Devices[0].Key].Gated = true;

        fleet.Links[0].SendSubmit(1, DevId(fleet.Devices[0]), UsbIp.DirIn, 1,
            numberOfPackets: UsbIp.NoIsoPackets, transferBufferLength: 8);

        // Device 2 completes while device 1 is still stalled.
        fleet.Links[1].SendSubmit(1, DevId(fleet.Devices[1]), UsbIp.DirIn, 1,
            numberOfPackets: UsbIp.NoIsoPackets, transferBufferLength: 8);

        FakeLink.UrbReply second = await fleet.Links[1].ReadUrbReplyAsync(ct);
        Assert.Equal(0, second.Status);

        // Nothing has been written for the stalled device yet.
        using var shortClock = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await fleet.Links[0].ReadUrbReplyAsync(shortClock.Token));

        await fleet.ShutdownAsync();
    }

    // =====================================================================================
    // Exclusivity is the only refusal
    // =====================================================================================

    [Fact]
    public async Task ExactlyOneOfManyRacingClientsWinsADevice()
    {
        const int Racers = 8;
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device });
        await state.RefreshExportsAsync(CancellationToken.None);

        var links = new FakeLink[Racers];
        var sessions = new UsbIpSession[Racers];
        var runs = new Task[Racers];

        for (int i = 0; i < Racers; i++)
        {
            links[i] = new FakeLink();
            Connection connection = TestConnections.New((ushort)(i + 1));
            state.Connections.Add(connection);
            sessions[i] = new UsbIpSession(state, connection, links[i].ServerTransport);
            runs[i] = sessions[i].RunAsync(CancellationToken.None);
        }

        using var clock = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        int[] winners = await Task.WhenAll(Enumerable.Range(0, Racers).Select(async i =>
        {
            links[i].SendImportRequest(device.BusId);
            UsbIpDevice? imported = await links[i].ReadImportReplyAsync(clock.Token);
            return imported is null ? 0 : 1;
        }));

        Assert.Equal(1, winners.Sum());
        Assert.Single(state.Attachments.Snapshot());

        foreach (FakeLink link in links) link.Close();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(state.Attachments.Snapshot());
    }

    [Fact]
    public async Task OneConnectionMayHoldManyDevicesAtOnce()
    {
        const int PerConnection = 8;
        UsbDevice[] devices = TestDevices.Range(PerConnection);
        (VhtServerState state, _, _) = TestServer.Create(devices);
        await state.RefreshExportsAsync(CancellationToken.None);

        var link = new FakeLink();
        Connection connection = TestConnections.New(1);
        state.Connections.Add(connection);
        var session = new UsbIpSession(state, connection, link.ServerTransport);
        Task run = session.RunAsync(CancellationToken.None);

        using var clock = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        foreach (UsbDevice device in devices)
        {
            link.SendImportRequest(device.BusId);
            Assert.NotNull(await link.ReadImportReplyAsync(clock.Token));
        }

        Assert.Equal(PerConnection, state.Attachments.Count);
        Assert.Equal(PerConnection, state.Attachments.ForConnection(connection.Id).Count);

        link.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(state.Attachments.Snapshot());
    }

    // =====================================================================================
    // Scale: the tables themselves have no ceiling
    // =====================================================================================

    [Fact]
    public async Task TheExportTableAndAttachTableHoldThousandsOfDevicesAtOnce()
    {
        const int Huge = 2000;

        (VhtServerState state, _, _) = TestServer.Create(TestDevices.Range(Huge));
        int exported = await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Equal(Huge, exported);
        Assert.Equal(Huge, state.Exports.Count);

        for (int i = 0; i < Huge; i++)
        {
            UsbDevice device = TestDevices.Create(i + 1);
            var record = new AttachRecord
            {
                Key = device.Key,
                ConnectionId = new ConnectionId((ushort)(i % 60000 + 1)),
                ClientUuid = $"uuid-{i}",
                RemoteAddress = "127.0.0.1:5000",
            };

            Assert.Equal(BindStatus.Success, state.Attachments.TryAttach(device.Key, record));
        }

        Assert.Equal(Huge, state.Attachments.Count);
    }

    [Fact]
    public void TheConnectionRegistryHasNoCeilingEither()
    {
        var registry = new ConnectionRegistry();

        for (int i = 0; i < 1000; i++)
        {
            Connection connection = TestConnections.New((ushort)(i + 1));
            registry.Add(connection);
        }

        Assert.Equal(1000, registry.Count);
    }
}
