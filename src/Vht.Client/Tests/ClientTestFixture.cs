// SPDX-License-Identifier: Apache-2.0
//
// Shared test wiring: temp config dirs (never the user's real %LOCALAPPDATA%), a real loopback bridge
// on an OS-assigned port, a real catalog over fake peers, and an in-memory controller.

using System.Net.Sockets;
using Vht.Client.Attach;
using Vht.Client.Bridge;
using Vht.Client.Credentials;
using Vht.Client.Diagnostics;
using Vht.Client.Peers;
using Vht.Client.Policy;
using Vht.Client.Transport;
using Vht.Client.Usbip;

namespace Vht.Client.Tests;

internal sealed class ClientTestFixture : IAsyncDisposable
{
    private readonly List<FakeUsbipServer> _servers = new();

    internal ClientTestFixture(bool autoAttachEnabled = true, TimeSpan? reconcileInterval = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "vht-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Log = new ClientLog(console: null, filePath: null) { MinimumLevel = ClientLogLevel.Debug };
        Router = new BusidRouter();
        Peers = new PeerStore(Path.Combine(Root, "peers.json"), Log);
        Policy = new AutoAttachPolicy(Path.Combine(Root, "autoattach.json"), Log)
        {
            // Tests must not wait real seconds; the delays themselves are asserted in PolicyTests.
            AttachDelay = TimeSpan.Zero,
            RetryDelay = TimeSpan.Zero,
        };
        Credentials = new CredentialCache(Path.Combine(Root, "credentials.json"), Log);
        Transports = PlainTcpTransportFactory.Instance;
        Catalog = new RemoteDeviceCatalog(Peers, Transports, Router, Log);
        Bridge = new LoopbackBridge(new BridgeOptions { Port = 0 }, Router, Transports, Catalog, Log)
        {
            Handshake = new CredentialHandshake(Credentials, Log),
        };
        Controller = new FakeDeviceController();
        Manager = new AttachManager(Bridge, Catalog, Controller, Policy, Log, new AttachManagerOptions
        {
            AutoAttachEnabled = autoAttachEnabled,
            ReconcileInterval = reconcileInterval ?? TimeSpan.FromMilliseconds(25),
        });
    }

    internal string Root { get; }

    internal ClientLog Log { get; }

    internal BusidRouter Router { get; }

    internal PeerStore Peers { get; }

    internal AutoAttachPolicy Policy { get; }

    internal CredentialCache Credentials { get; }

    internal IClientTransportFactory Transports { get; }

    internal RemoteDeviceCatalog Catalog { get; }

    internal LoopbackBridge Bridge { get; }

    internal FakeDeviceController Controller { get; }

    internal AttachManager Manager { get; }

    internal int BridgePort => Bridge.LocalEndpoint?.Port
        ?? throw new InvalidOperationException("the bridge is not running");

    /// <summary>Starts a fake peer exporting the given busids and registers it with the client.</summary>
    internal async Task<FakeUsbipServer> AddPeerAsync(string name, params string[] busIds)
    {
        var server = new FakeUsbipServer();
        foreach (string busId in busIds)
        {
            server.AddDevice(busId);
        }
        server.Start();
        _servers.Add(server);

        Peers.Add(new Peer
        {
            Name = name,
            Host = "127.0.0.1",
            Port = server.Port,
            UseTls = false,          // the stub transport is plaintext; TLS is card transport-workstream's
            Manual = true,
        });

        await Task.CompletedTask;
        return server;
    }

    internal void StartBridge() => Bridge.Start();

    internal async Task<IReadOnlyList<InventoryEntry>> RefreshAsync() =>
        await Catalog.RefreshAsync(CancellationToken.None);

    /// <summary>Opens a loopback connection the way the UDE driver would, and imports one busid.</summary>
    internal async Task<LoopbackRelayProbe> OpenRelayAsync(string routingKey, CancellationToken ct = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", BridgePort, ct);
        NetworkStream stream = client.GetStream();

        byte[] request = UsbipOpWriter.BuildImportRequest(routingKey);
        await stream.WriteAsync(request, ct);

        var reply = new byte[UsbipProtocol.ImportReplySize];
        int read = 0;
        while (read < reply.Length)
        {
            int n = await stream.ReadAsync(reply.AsMemory(read), ct);
            if (n <= 0) throw new IOException("the bridge closed before replying to the import");
            read += n;
        }

        uint status = (uint)((reply[4] << 24) | (reply[5] << 16) | (reply[6] << 8) | reply[7]);
        return new LoopbackRelayProbe(client, stream, status, reply);
    }

    /// <summary>Polls a condition, so tests do not depend on sleeps that are either flaky or slow.</summary>
    internal static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>Async variant, for conditions that need a refresh (which performs I/O) before they can answer.</summary>
    internal static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(20);
        }
        return await condition();
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        await Bridge.DisposeAsync();
        foreach (FakeUsbipServer server in _servers)
        {
            await server.DisposeAsync();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp dir must never fail a test run.
        }
    }
}

/// <summary>One loopback connection, as the driver would hold it: imported, then usable as a stream.</summary>
internal sealed class LoopbackRelayProbe : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    internal LoopbackRelayProbe(TcpClient client, NetworkStream stream, uint status, byte[] reply)
    {
        _client = client;
        _stream = stream;
        ImportStatus = status;
        Reply = reply;
    }

    internal uint ImportStatus { get; }

    internal byte[] Reply { get; }

    /// <summary>Round-trips bytes through the relay (the fake peers echo).</summary>
    internal async Task<byte[]> EchoAsync(byte[] payload, CancellationToken ct = default)
    {
        await _stream.WriteAsync(payload, ct);
        var echoed = new byte[payload.Length];
        int read = 0;
        while (read < echoed.Length)
        {
            int n = await _stream.ReadAsync(echoed.AsMemory(read), ct);
            if (n <= 0) throw new IOException("the relay closed mid-echo");
            read += n;
        }
        return echoed;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        catch (IOException)
        {
            // Peer teardown races are expected.
        }
        _client.Dispose();
    }
}
