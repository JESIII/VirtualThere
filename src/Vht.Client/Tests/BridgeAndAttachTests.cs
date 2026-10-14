// SPDX-License-Identifier: Apache-2.0
//
// The tests that prove the headline claim at the client's layer: many remote devices, attached at the
// same time, each usable through its own relay — plus detach, hotplug, and the two failure paths
// (transport loss, peer loss) with their re-attach behaviour.
//
// These run against real loopback sockets and real USB/IP framing. The virtual host controller is the
// in-memory double, because a UDE driver cannot be loaded on this build machine (ARCHITECTURE.md §10
// risk 1); the acceptance card (hardware-validation-workstream) is where the same paths run on real hardware.

using System.Diagnostics;
using System.Text;
using Vht.Client.Attach;
using Vht.Client.Bridge;
using Vht.Client.Control;
using Vht.Client.Usbip;

namespace Vht.Client.Tests;

public sealed class LoopbackBridgeTests
{
    [Fact]
    public async Task TheBridgeBindsLoopbackOnly()
    {
        await using var fixture = new ClientTestFixture();
        fixture.StartBridge();

        var options = new BridgeOptions { Address = "0.0.0.0", Port = 0 };
        var bridge = new LoopbackBridge(options, fixture.Router, fixture.Transports, fixture.Catalog, fixture.Log);

        var ex = Assert.Throws<InvalidOperationException>(() => bridge.Start());
        Assert.Contains("loopback", ex.Message);
        await bridge.DisposeAsync();
    }

    [Fact]
    public async Task AnImportIsRelayedToTheRightPeer_AndCarriesThatPeersNativeBusId()
    {
        await using var fixture = new ClientTestFixture();
        await fixture.AddPeerAsync("lab", "1-2");
        fixture.StartBridge();
        await fixture.RefreshAsync();

        await using LoopbackRelayProbe relay = await fixture.OpenRelayAsync("1-2");

        Assert.Equal(0u, relay.ImportStatus);
        // The reply the driver sees is the peer's own OP_REP_IMPORT, relayed verbatim.
        Assert.Equal(UsbipProtocol.OpRepImport, BigEndian.ReadU16(relay.Reply.AsSpan(2)));

        byte[] echoed = await relay.EchoAsync(Encoding.ASCII.GetBytes("urb payload"));
        Assert.Equal("urb payload", Encoding.ASCII.GetString(echoed));
    }

    [Fact]
    public async Task TwoPeersExportingTheSameBusIdGetDistinctRoutingKeys()
    {
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer first = await fixture.AddPeerAsync("lab", "1-2");
        FakeUsbipServer second = await fixture.AddPeerAsync("wshop", "1-2");
        fixture.StartBridge();

        IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.RoutingKey == "1-2@lab");
        Assert.Contains(entries, e => e.RoutingKey == "1-2@wshop");

        await using LoopbackRelayProbe labRelay = await fixture.OpenRelayAsync("1-2@lab");
        await using LoopbackRelayProbe wshopRelay = await fixture.OpenRelayAsync("1-2@wshop");
        Assert.Equal(0u, labRelay.ImportStatus);
        Assert.Equal(0u, wshopRelay.ImportStatus);

        // Each relay went to its own peer, and each peer saw its own plain busid on the wire.
        Assert.Contains("1-2", first.RequestedBusIds);
        Assert.Contains("1-2", second.RequestedBusIds);
        Assert.DoesNotContain(second.RequestedBusIds, b => b.Contains('@'));
    }

    [Fact]
    public async Task AnUnknownBusIdIsRefusedAndNothingIsConnected()
    {
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer server = await fixture.AddPeerAsync("lab", "1-2");
        fixture.StartBridge();
        await fixture.RefreshAsync();

        // The probe will fail: the bridge closes the loopback connection without a reply, by design.
        int connectionsBefore = server.TotalAccepted;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.OpenRelayAsync("9-9"));

        // No new peer connection and no relay: the refusal happened locally, it was not forwarded.
        Assert.Equal(connectionsBefore, server.TotalAccepted);
        Assert.Equal(0, fixture.Bridge.RelayCount);
    }

    [Fact]
    public async Task ADeviceListRequestIsAnsweredFromEveryPeerAtOnce()
    {
        await using var fixture = new ClientTestFixture();
        await fixture.AddPeerAsync("lab", "1-2", "1-3");
        await fixture.AddPeerAsync("wshop", "2-1");
        fixture.StartBridge();

        var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("127.0.0.1", fixture.BridgePort);
        using (client)
        {
            var stream = client.GetStream();
            await stream.WriteAsync(UsbipOpWriter.BuildDevListRequest());

            var header = new byte[UsbipProtocol.DevListRequestSize];
            await ReadExactly(stream, header);
            Assert.Equal(UsbipProtocol.OpRepDevList, BigEndian.ReadU16(header.AsSpan(2)));
            Assert.Equal(3u, BigEndian.ReadU32(header.AsSpan(8)));

            var record = new byte[UsbipProtocol.UsbipUsbDeviceSize];
            var interfaceRecord = new byte[UsbipProtocol.UsbipUsbInterfaceSize];
            var keys = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                await ReadExactly(stream, record);
                keys.Add(BigEndian.ReadCString(record.AsSpan(UsbipProtocol.DevPathMax, UsbipProtocol.BusIdSize)));

                // Each usbip_usb_device is followed by num_interfaces usbip_usb_interface records, which
                // is the field the writer leaves as the record's last byte. Skipping them is what keeps
                // the records from drifting (the reply is a variable-length stream, not a fixed array).
                int interfaces = record[UsbipProtocol.UsbipUsbDeviceSize - 1];
                for (int j = 0; j < interfaces; j++)
                {
                    await ReadExactly(stream, interfaceRecord);
                }
            }

            Assert.Equal(new[] { "1-2", "1-3", "2-1" }, keys.OrderBy(k => k, StringComparer.Ordinal));
        }
    }

    private static async Task ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n <= 0) throw new IOException("stream closed");
            read += n;
        }
    }
}

public sealed class AttachManagerTests
{
    [Fact]
    public async Task ManyDevicesAttachAndAreUsableAtOnce()
    {
        const int perPeer = 4;
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer first = await fixture.AddPeerAsync("lab", Enumerable.Range(0, perPeer).Select(i => $"1-{i + 2}").ToArray());
        FakeUsbipServer second = await fixture.AddPeerAsync("wshop", Enumerable.Range(0, perPeer).Select(i => $"2-{i + 2}").ToArray());
        fixture.StartBridge();

        IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();
        Assert.Equal(perPeer * 2, entries.Count);

        // Attach all of them concurrently — the multi-device requirement, exercised rather than assumed.
        AttachedDevice[] attached = await Task.WhenAll(entries.Select(e =>
            fixture.Manager.AttachAsync(e, AttachReason.Manual, CancellationToken.None)));

        Assert.Equal(perPeer * 2, fixture.Manager.AttachedCount);
        Assert.Equal(perPeer * 2, attached.Select(a => a.Port).Distinct().Count());
        Assert.Equal(perPeer * 2, fixture.Controller.Ports.Count);

        // Every device gets its own relay, and every relay carries bytes in both directions.
        var relays = new List<LoopbackRelayProbe>();
        try
        {
            foreach (AttachedDevice device in attached)
            {
                relays.Add(await fixture.OpenRelayAsync(device.RoutingKey));
            }

            for (int i = 0; i < relays.Count; i++)
            {
                byte[] payload = Encoding.ASCII.GetBytes($"device-{i}-payload");
                Assert.Equal(payload, await relays[i].EchoAsync(payload));
            }

            Assert.Equal(perPeer * 2, fixture.Bridge.RelayCount);
            Assert.Equal(perPeer, first.RequestedBusIds.Count);
            Assert.Equal(perPeer, second.RequestedBusIds.Count);
        }
        finally
        {
            foreach (LoopbackRelayProbe relay in relays)
            {
                await relay.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DetachingOneDeviceLeavesTheOthersAttached()
    {
        await using var fixture = new ClientTestFixture();
        await fixture.AddPeerAsync("lab", "1-2", "1-3", "1-4");
        fixture.StartBridge();

        IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();
        foreach (InventoryEntry entry in entries)
        {
            await fixture.Manager.AttachAsync(entry, AttachReason.Manual, CancellationToken.None);
        }

        InventoryEntry victim = entries[1];
        AttachedDevice attachedVictim = fixture.Manager.AttachedDevices[victim.DeviceKey];

        Assert.True(fixture.Manager.Detach(victim.DeviceKey));
        Assert.Equal(2, fixture.Manager.AttachedCount);
        Assert.Contains(attachedVictim.Port, fixture.Controller.DetachedPorts);
        Assert.DoesNotContain(attachedVictim.Port, fixture.Controller.Ports);
        Assert.False(fixture.Manager.Detach(victim.DeviceKey));   // already gone
    }

    [Fact]
    public async Task AttachFailsClosedWhenTheBridgeIsNotRunning()
    {
        await using var fixture = new ClientTestFixture();
        await fixture.AddPeerAsync("lab", "1-2");
        IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();

        // Bridge deliberately not started: the only way to attach would be to point the driver at the
        // remote host, which §6.2 forbids.
        var ex = await Assert.ThrowsAsync<ClientAttachException>(
            () => fixture.Manager.AttachAsync(entries[0], AttachReason.Manual, CancellationToken.None));
        Assert.Contains("bridge is not running", ex.Message);
        Assert.Equal(0, fixture.Controller.AttachCallCount);
    }

    [Fact]
    public async Task ARefusedAttachCancelsThePendingAttemptAndDoesNotRetryInATightLoop()
    {
        await using var fixture = new ClientTestFixture();
        await fixture.AddPeerAsync("lab", "1-2");
        fixture.StartBridge();
        IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();

        fixture.Controller.FailNextAttach = true;
        fixture.Controller.FailNextAttachReason = "no free port";

        var ex = await Assert.ThrowsAsync<ClientAttachException>(
            () => fixture.Manager.AttachAsync(entries[0], AttachReason.Manual, CancellationToken.None));
        Assert.Contains("no free port", ex.Message);
        Assert.Equal(1, fixture.Controller.StopAttachAttemptsCallCount);
        Assert.Equal(0, fixture.Manager.AttachedCount);
    }

    [Fact]
    public async Task AutoAttachHappensOnlyAfterAGrant_AndTheGrantIsRecordedOnlyAfterSuccess()
    {
        await using var fixture = new ClientTestFixture();
        await fixture.AddPeerAsync("lab", "1-2");
        fixture.StartBridge();
        await fixture.RefreshAsync();

        // No grant yet: the reconcile loop must attach nothing.
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        Assert.Empty(fixture.Policy.GrantedKeys);
        Assert.Equal(0, fixture.Manager.AttachedCount);

        // Now grant it, and let the loop do the work.
        fixture.Policy.Grant((await fixture.RefreshAsync())[0]);
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Manager.AttachedCount);
        Assert.Single(fixture.Policy.GrantedKeys);
        // Exactly one attach went to the controller: the loop must not have retried a device it already
        // attached (devlist refreshes also count as peer connections, so those are not the signal here).
        Assert.Equal(1, fixture.Controller.AttachCallCount);
    }

    [Fact]
    public async Task TheAutoAttachLoopAttachesAndDetachesOnHotplug()
    {
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer server = await fixture.AddPeerAsync("lab", "1-2");
        fixture.StartBridge();
        InventoryEntry entry = (await fixture.RefreshAsync())[0];
        fixture.Policy.Grant(entry);

        fixture.Manager.Start();
        Assert.True(await ClientTestFixture.WaitForAsync(() => fixture.Manager.AttachedCount == 1));

        // A second device appears on the peer, granted on the fly: it must be picked up.
        server.AddDevice("1-3");
        Assert.True(await ClientTestFixture.WaitForAsync(async () =>
        {
            IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();
            InventoryEntry? newDevice = entries.FirstOrDefault(e => e.Device.BusId == "1-3");
            if (newDevice is null) return false;
            fixture.Policy.Grant(newDevice);
            return true;
        }));

        Assert.True(await ClientTestFixture.WaitForAsync(() => fixture.Manager.AttachedCount == 2));

        // The device goes away at the peer: the port disappears and the client stops claiming it.
        server.RemoveDevice("1-3");
        await fixture.RefreshAsync();
        Assert.True(await ClientTestFixture.WaitForAsync(() => fixture.Manager.AttachedCount == 1));
    }

    [Fact]
    public async Task ADeadTransportMarksTheDeviceForReAttach_AndItComesBackWithoutARestart()
    {
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer server = await fixture.AddPeerAsync("lab", "1-2");
        fixture.StartBridge();
        InventoryEntry entry = (await fixture.RefreshAsync())[0];
        fixture.Policy.Grant(entry);

        fixture.Manager.Start();
        Assert.True(await ClientTestFixture.WaitForAsync(() => fixture.Manager.AttachedCount == 1));
        int firstPort = fixture.Manager.AttachedDevices.Values.Single().Port;

        // The driver's leg: a real relay through the bridge to the peer. This is what a transport loss
        // actually kills, so it has to exist for the test to mean anything.
        await using LoopbackRelayProbe relay = await fixture.OpenRelayAsync("1-2");
        Assert.True(await ClientTestFixture.WaitForAsync(() =>
            fixture.Manager.AttachedDevices.Values.Single().IsRelayLive));

        // Watch the two events that must both happen, instead of racing a poll against them: the
        // transport dies, the device is detached locally, and then it comes back on a new port — all
        // without anything being restarted.
        var detached = new TaskCompletionSource<AttachedDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reattached = new TaskCompletionSource<AttachedDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Manager.DeviceDetached += (_, device) => detached.TrySetResult(device);
        fixture.Manager.DeviceAttached += (_, device) => reattached.TrySetResult(device);

        // The transport dies (the peer dropped the connection): the attach manager must not pretend
        // the device is still there, and must not require a machine restart to get it back.
        server.DropAllConnections();

        AttachedDevice gone = await detached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("1-2", gone.RoutingKey);
        Assert.False(gone.IsRelayLive);

        AttachedDevice back = await reattached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("1-2", back.RoutingKey);
        Assert.Equal(1, fixture.Manager.AttachedCount);
        Assert.NotEqual(firstPort, back.Port);
    }

    [Fact]
    public async Task APeerGoingOffTheNetworkDetachesItsDevicesOnly()
    {
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer lab = await fixture.AddPeerAsync("lab", "1-2");
        await fixture.AddPeerAsync("wshop", "2-1");
        fixture.StartBridge();

        IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();
        foreach (InventoryEntry entry in entries)
        {
            fixture.Policy.Grant(entry);
            await fixture.Manager.AttachAsync(entry, AttachReason.AutoAttach, CancellationToken.None);
        }
        Assert.Equal(2, fixture.Manager.AttachedCount);

        // lab stops answering entirely.
        lab.StopListening();
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Manager.AttachedCount);
        Assert.Equal("wshop", fixture.Manager.AttachedDevices.Values.Single().PeerName);
        Assert.NotEmpty(fixture.Manager.PendingAttaches);

        // lab comes back: the pending device is re-attached, still with nothing restarted.
        lab.StartListening();
        Assert.True(await ClientTestFixture.WaitForAsync(async () =>
        {
            await fixture.Manager.ReconcileAsync(CancellationToken.None);
            return fixture.Manager.AttachedCount == 2;
        }));
    }
}

public sealed class ControlPlaneTests
{
    [Fact]
    public async Task UnknownVerbsAreRejectedWithAnErrorObject()
    {
        await using var fixture = new ClientTestFixture();
        await using var control = Build(fixture);

        string response = await control.DispatchAsync("{\"verb\":\"devices.explode\"}", CancellationToken.None);
        Assert.Contains("\"ok\":false", response);
        Assert.Contains("unknown verb", response);
    }

    [Fact]
    public async Task MalformedJsonIsRejectedNotThrown()
    {
        await using var fixture = new ClientTestFixture();
        await using var control = Build(fixture);

        string response = await control.DispatchAsync("{ not json", CancellationToken.None);
        Assert.Contains("\"ok\":false", response);
        Assert.Contains("malformed JSON", response);
    }

    [Fact]
    public async Task TheVocabularyListsDevicesPeersAndAttachesThem()
    {
        await using var fixture = new ClientTestFixture();
        await fixture.AddPeerAsync("lab", "1-2");
        fixture.StartBridge();
        await using var control = Build(fixture);

        await fixture.RefreshAsync();

        string peers = await control.DispatchAsync("{\"verb\":\"peers.list\"}", CancellationToken.None);
        Assert.Contains("lab", peers);

        string devices = await control.DispatchAsync("{\"verb\":\"devices.list\"}", CancellationToken.None);
        Assert.Contains("1-2", devices);
        Assert.Contains("mass-storage", devices);

        string attach = await control.DispatchAsync("{\"verb\":\"clients.attach\",\"busid\":\"1-2\"}", CancellationToken.None);
        Assert.Contains("\"attached\":true", attach);
        Assert.Equal(1, fixture.Manager.AttachedCount);

        string status = await control.DispatchAsync("{\"verb\":\"server.status\"}", CancellationToken.None);
        Assert.Contains("\"attachedCount\":1", status);

        string detach = await control.DispatchAsync("{\"verb\":\"clients.detach\",\"busid\":\"1-2\"}", CancellationToken.None);
        Assert.Contains("\"detached\":true", detach);
        Assert.Equal(0, fixture.Manager.AttachedCount);
    }

    [Fact]
    public async Task TheControlPipeAnswersOverARealNamedPipe()
    {
        await using var fixture = new ClientTestFixture();
        await fixture.AddPeerAsync("lab", "1-2");
        fixture.StartBridge();

        await using var control = Build(fixture);
        control.Start();

        using var client = new System.IO.Pipes.NamedPipeClientStream(
            ".", ClientControlServer.PipeName, System.IO.Pipes.PipeDirection.InOut);
        await client.ConnectAsync(5000);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await writer.WriteLineAsync("{\"verb\":\"server.status\"}");
        string? line = await reader.ReadLineAsync();
        Assert.NotNull(line);
        Assert.Contains("\"ok\":true", line);
    }

    private static ClientControlServer Build(ClientTestFixture fixture) => new(
        fixture.Peers, fixture.Catalog, fixture.Manager, fixture.Policy, fixture.Credentials, fixture.Log);
}
