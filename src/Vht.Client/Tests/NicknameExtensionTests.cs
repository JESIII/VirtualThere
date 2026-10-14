// SPDX-License-Identifier: Apache-2.0
//
// The client's half of the nickname decision (docs/SERVER.md §8). The stock USB/IP device record has
// no string field, so a peer's nickname can only reach this client through VirtualThere's own op —
// and a peer that is a stock usbipd must still list exactly as it did before the extension existed.
//
// These run over real loopback sockets against the fake peer in TestDoubles.

using Vht.Client.Bridge;
using Vht.Client.Peers;

namespace Vht.Client.Tests;

public sealed class DeviceNicknameTests
{
    [Fact]
    public async Task TheCatalog_CarriesThePeersNickname_ForTheMatchingBusIdOnly()
    {
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer peer = await fixture.AddPeerAsync("lab", "1-1", "1-2");
        peer.SetNickname("1-1", "lab-stick");

        IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();

        InventoryEntry named = entries.Single(e => e.Device.BusId == "1-1");
        Assert.Equal("lab-stick", named.Nickname);
        Assert.Equal("lab-stick", named.DisplayName);

        // A device the operator did not name keeps the video/product rendering it always had.
        InventoryEntry unnamed = entries.Single(e => e.Device.BusId == "1-2");
        Assert.Null(unnamed.Nickname);
        Assert.Equal(unnamed.Device.VendorProduct, unnamed.DisplayName);

        // The stock list is still what says which devices exist; the extension only adds names.
        Assert.Equal(2, entries.Count);
        Assert.Equal(1, peer.DeviceInfoRequests);
    }

    [Fact]
    public async Task TheNickname_IsAskedForOnAConnectionOfItsOwn()
    {
        // A peer that has never heard of the op closes the connection when it arrives (that is what
        // usbipd does), so the probe must not share the socket the device list came back on.
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer peer = await fixture.AddPeerAsync("lab", "1-1");

        await fixture.RefreshAsync();

        Assert.True(peer.TotalAccepted >= 2, $"expected a list connection and a probe connection, saw {peer.TotalAccepted}");
        Assert.Equal(1, peer.DeviceInfoRequests);
    }

    [Fact]
    public async Task APeerWithoutTheExtension_StillLists_AndSimplyHasNoNames()
    {
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer peer = await fixture.AddPeerAsync("stock", "1-1");
        peer.AnswersDeviceInfoOp = false;    // behaves like a stock usbipd: unknown op, closed socket

        IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();

        InventoryEntry device = Assert.Single(entries);
        Assert.Null(device.Nickname);
        Assert.Equal(device.Device.VendorProduct, device.DisplayName);

        // And the peer is not reported as unavailable: a peer that does not implement an extension
        // has not failed at anything.
        Assert.Empty(fixture.Catalog.UnavailablePeers);
    }

    [Fact]
    public async Task ANonAsciiNickname_SurvivesTheRoundTrip()
    {
        await using var fixture = new ClientTestFixture();
        FakeUsbipServer peer = await fixture.AddPeerAsync("lab", "1-1");
        peer.SetNickname("1-1", "Büro-Stick ★");

        IReadOnlyList<InventoryEntry> entries = await fixture.RefreshAsync();

        Assert.Equal("Büro-Stick ★", entries.Single().Nickname);
    }
}
