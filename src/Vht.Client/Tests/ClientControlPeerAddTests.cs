using Vht.Client.Control;
using Vht.Ui.ControlPlane;

namespace Vht.Client.Tests;

public sealed class ClientControlPeerAddTests
{
    [Fact]
    public async Task AddPeerAcceptsTheHostPortAndNameFieldsSentByTheClientWindow()
    {
        string pipeName = "vht-peer-add-" + Guid.NewGuid().ToString("N");
        await using var fixture = new ClientTestFixture();
        await using var control = new ClientControlServer(
            fixture.Peers, fixture.Catalog, fixture.Manager, fixture.Policy, fixture.Credentials, fixture.Log,
            pipeName);
        control.Start();

        var service = new VhtServiceClient(new ControlPlaneClient(pipeName));
        ControlPlaneResponse? response = null;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            response = await service.AddPeerAsync("192.168.1.10", 3240, "test server", CancellationToken.None);
            if (response.IsOk)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.NotNull(response);
        Assert.True(response.IsOk, response.Error?.Message ?? response.Status.ToString());
        var peer = Assert.Single(fixture.Peers.Peers);
        Assert.Equal("test server", peer.Name);
        Assert.Equal("192.168.1.10", peer.Host);
        Assert.Equal(3240, peer.Port);
    }
}
