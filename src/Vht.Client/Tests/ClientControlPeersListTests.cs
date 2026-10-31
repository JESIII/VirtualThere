// SPDX-License-Identifier: Apache-2.0
//
// The client window draws its entire right-hand side from `{"verb":"peers.list"}`: the server list's
// online/offline state, and the device list of the selected server. This test reads that verb back
// through the window's own reader (VhtServiceClient -> PeerSnapshot), which is the boundary that was
// broken, rather than asserting on the raw JSON.
//
// The defect it covers: the client's control plane answered `peers.list` with only the peer's name,
// endpoint and TLS flags. Nothing carried `online`, `status` or the nested `devices` array, so the
// window showed every configured server as **offline** and its device list was always empty, however
// well the peer was answering. It was invisible to the UI suite because the mock service implements
// the verb in full (see docs/ui-shots/mock-transcript.jsonl), and invisible in the runtime because a
// peer with no devices and a peer that was never queried look the same on an empty response.

using System.Net;
using System.Net.Sockets;
using Vht.Client.Control;
using Vht.Ui.ControlPlane;
using Vht.Ui.Model;

namespace Vht.Client.Tests;

public sealed class ClientControlPeersListTests
{
    [Fact]
    public async Task PeersListCarriesTheDevicesAndTheOnlineStateTheClientWindowDraws()
    {
        string pipeName = "vht-peers-list-" + Guid.NewGuid().ToString("N");
        await using var fixture = new ClientTestFixture();

        // One peer that answers and exports a device.
        await fixture.AddPeerAsync("lab", "1-2");

        // One peer nothing answers for. Its port is free because it was just released, so the refresh
        // fails with a connection refusal rather than with a bind conflict.
        int unreachablePort = ReserveFreePort();
        fixture.Peers.Add(new Peer
        {
            Name = "gone",
            Host = "127.0.0.1",
            Port = unreachablePort,
            UseTls = false,
        });

        fixture.StartBridge();
        await fixture.RefreshAsync();

        await using var control = new ClientControlServer(
            fixture.Peers, fixture.Catalog, fixture.Manager, fixture.Policy, fixture.Credentials, fixture.Log,
            pipeName);
        control.Start();

        var service = new VhtServiceClient(new ControlPlaneClient(pipeName));
        IReadOnlyList<PeerSnapshot> peers = [];
        for (int attempt = 0; attempt < 50; attempt++)
        {
            var response = await service.ListPeersAsync(CancellationToken.None);
            if (response.Status == ControlPlaneStatus.Ok)
            {
                peers = response.Peers;
                break;
            }

            await Task.Delay(50);
        }

        Assert.Equal(2, peers.Count);

        // The reachable peer: online, and carrying the device the window lists under it.
        PeerSnapshot reachable = Assert.Single(peers, p => p.Name == "lab");
        Assert.True(reachable.Online, "a peer that answered its last refresh must read as online");
        Assert.Equal("online", reachable.StatusText);

        RemoteDeviceSnapshot device = Assert.Single(reachable.Devices);
        Assert.Equal("1-2", device.BusId);

        // The unreachable peer: offline, with the reason that says why, and no invented devices.
        PeerSnapshot unreachable = Assert.Single(peers, p => p.Name == "gone");
        Assert.False(unreachable.Online, "a peer that failed its last refresh must read as offline");
        Assert.NotEqual("online", unreachable.StatusText);
        Assert.Empty(unreachable.Devices);
    }

    /// <summary>An OS-assigned port that was free when it was asked for.</summary>
    private static int ReserveFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint!).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}