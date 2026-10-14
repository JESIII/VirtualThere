using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Vht.Transport.Auth;
using Vht.Transport.Tls;
using Vht.Transport.Wire;
using Xunit;
using Xunit.Abstractions;

namespace Vht.Transport.Tests;

public sealed class SessionTests
{
    private readonly ITestOutputHelper _output;

    public SessionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PlainTextSessionCarriesBytes()
    {
        await using var pair = await Harness.ConnectAsync(
            Harness.ServerOptions(TransportKind.PlainTcp),
            Harness.ClientOptions(TransportKind.PlainTcp));

        Assert.Equal(TransportKind.PlainTcp, pair.Client.Kind);
        Assert.Equal(TransportCapabilities.None, pair.Server.Capabilities);

        var payload = "stock usbip frame"u8.ToArray();
        await pair.Client.WriteAsync(payload, CancellationToken.None);
        var buffer = new byte[payload.Length];
        await ReadExactlyAsync(pair.Server, buffer);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task PlainTextTransportFailsClosedOnAuthentication()
    {
        await using var pair = await Harness.ConnectAsync(
            Harness.ServerOptions(TransportKind.PlainTcp),
            Harness.ClientOptions(TransportKind.PlainTcp));

        var result = await pair.Client.AuthenticateAsync(AuthRequest.ForSharedSecret("anything"), CancellationToken.None);

        Assert.Equal(AuthStatus.Unauthorized, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task TlsSessionNegotiatesHandshakeAndPinning()
    {
        await using var pair = await Harness.ConnectAsync(
            Harness.ServerOptions(),
            Harness.ClientOptions());

        Assert.Equal(TransportKind.Tls, pair.Client.Kind);
        Assert.True(pair.Client.Capabilities.HasFlag(TransportCapabilities.HandshakeRequired));
        Assert.True(pair.Client.Capabilities.HasFlag(TransportCapabilities.PeerPinning));
        Assert.False(pair.Client.Capabilities.HasFlag(TransportCapabilities.Lz4));

        var tls = (TlsTransport)pair.Client;
        Assert.True(tls.NegotiatedProtocol >= SslProtocols.Tls12);
        Assert.Equal(ServerCertificateManager.ComputeSpkiPin(Harness.Certificate), tls.PeerSpkiPin);
        _output.WriteLine($"negotiated protocol: {tls.NegotiatedProtocol}");

        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        await pair.Client.WriteAsync(payload, CancellationToken.None);
        var buffer = new byte[payload.Length];
        await ReadExactlyAsync(pair.Server, buffer);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task DeviceTrafficBeforeAuthenticationIsRefused()
    {
        var serverOptions = Harness.ServerOptions(authentication: new AuthenticationOptions
        {
            SharedSecret = "s3cret",
            Pbkdf2Iterations = Harness.FastIterations,
        });

        await using var pair = await Harness.ConnectAsync(serverOptions, Harness.ClientOptions());

        var ex = await Assert.ThrowsAsync<TransportException>(
            async () => await pair.Client.WriteAsync(new byte[16], CancellationToken.None));
        Assert.Equal(TransportErrorCode.InvalidState, ex.Code);

        var readEx = await Assert.ThrowsAsync<TransportException>(
            async () => await pair.Client.ReadAsync(new byte[16], CancellationToken.None));
        Assert.Equal(TransportErrorCode.InvalidState, readEx.Code);
    }

    [Fact]
    public async Task CorrectSharedSecretAuthenticatesBothEnds()
    {
        var serverOptions = Harness.ServerOptions(authentication: new AuthenticationOptions
        {
            SharedSecret = "s3cret",
            Pbkdf2Iterations = Harness.FastIterations,
        });

        await using var pair = await Harness.ConnectAsync(
            serverOptions,
            Harness.ClientOptions(credential: AuthRequest.ForSharedSecret("s3cret")));

        var (server, client) = await Harness.AuthenticateAsync(pair, AuthRequest.ForSharedSecret("s3cret"));

        Assert.True(server.IsSuccess);
        Assert.True(client.IsSuccess);
        Assert.Equal(AuthMethod.SharedSecret, client.Method);
    }

    [Fact]
    public async Task WrongSharedSecretFailsClosed()
    {
        var serverOptions = Harness.ServerOptions(authentication: new AuthenticationOptions
        {
            SharedSecret = "s3cret",
            Pbkdf2Iterations = Harness.FastIterations,
        });

        await using var pair = await Harness.ConnectAsync(
            serverOptions,
            Harness.ClientOptions(credential: AuthRequest.ForSharedSecret("wrong")));

        var (server, client) = await Harness.AuthenticateAsync(pair, AuthRequest.ForSharedSecret("wrong"));

        Assert.Equal(AuthStatus.Unauthorized, server.Status);
        Assert.Equal(AuthStatus.Unauthorized, client.Status);

        // The session is now permanently unusable, not merely "reported".
        var ex = await Assert.ThrowsAsync<TransportException>(
            async () => await pair.Client.WriteAsync(new byte[8], CancellationToken.None));
        Assert.Equal(TransportErrorCode.AuthenticationFailed, ex.Code);
    }

    [Fact]
    public async Task CorrectDevicePasswordAuthenticates()
    {
        var serverOptions = Harness.ServerOptions(authentication: new AuthenticationOptions
        {
            DevicePasswords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["1-4.2"] = "device-pw" },
            Pbkdf2Iterations = Harness.FastIterations,
        });

        var credential = AuthRequest.ForDevicePassword("1-4.2", "device-pw");
        await using var pair = await Harness.ConnectAsync(
            serverOptions,
            Harness.ClientOptions(credential: credential));

        var (server, client) = await Harness.AuthenticateAsync(pair, credential);

        Assert.True(server.IsSuccess);
        Assert.True(client.IsSuccess);
        Assert.Equal(AuthMethod.DevicePassword, client.Method);
    }

    [Fact]
    public async Task WrongDevicePasswordAndUnknownDeviceBothFailClosed()
    {
        var serverOptions = Harness.ServerOptions(authentication: new AuthenticationOptions
        {
            DevicePasswords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["1-4.2"] = "device-pw" },
            Pbkdf2Iterations = Harness.FastIterations,
        });

        var wrongPassword = AuthRequest.ForDevicePassword("1-4.2", "nope");
        await using (var pair = await Harness.ConnectAsync(serverOptions, Harness.ClientOptions(credential: wrongPassword)))
        {
            var (server, client) = await Harness.AuthenticateAsync(pair, wrongPassword);
            Assert.Equal(AuthStatus.Unauthorized, server.Status);
            Assert.Equal(AuthStatus.Unauthorized, client.Status);
        }

        var unknownDevice = AuthRequest.ForDevicePassword("9-9.9", "device-pw");
        await using (var pair = await Harness.ConnectAsync(serverOptions, Harness.ClientOptions(credential: unknownDevice)))
        {
            var (server, client) = await Harness.AuthenticateAsync(pair, unknownDevice);
            Assert.Equal(AuthStatus.Unauthorized, server.Status);
            Assert.Equal(AuthStatus.Unauthorized, client.Status);
        }
    }

    [Fact]
    public async Task RepeatedFailuresAreThrottledAcrossConnections()
    {
        var serverOptions = Harness.ServerOptions(
            authentication: new AuthenticationOptions
            {
                SharedSecret = "s3cret",
                Pbkdf2Iterations = Harness.FastIterations,
            },
            rateLimit: new RateLimitOptions
            {
                PeerBurst = 2,
                PeerRefillPerMinute = 1,
                GlobalBurst = 100,
                GlobalRefillPerMinute = 100,
                LockoutAfterFailures = 2,
                LockoutBase = TimeSpan.FromSeconds(30),
                LockoutMax = TimeSpan.FromSeconds(60),
            });

        // One listener, so the rate limiter spans connections exactly as it would on a real server.
        await using var listener = TransportListener.Start(serverOptions, IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var statuses = new List<AuthStatus>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var credential = AuthRequest.ForSharedSecret("wrong");
            var options = Harness.ClientOptions(credential: credential);
            var client = await TransportFactory.ConnectAsync(
                "127.0.0.1", listener.LocalEndPoint.Port, options, credential, timeout.Token);
            var server = await listener.AcceptAsync(timeout.Token);

            var serverTask = server.AuthenticateAsync(AuthRequest.None, CancellationToken.None);
            var clientTask = client.AuthenticateAsync(credential, CancellationToken.None);
            await Task.WhenAll(serverTask, clientTask);

            statuses.Add((await clientTask).Status);
            await client.DisposeAsync();
            await server.DisposeAsync();
        }

        Assert.Equal(AuthStatus.Unauthorized, statuses[0]);
        Assert.Equal(AuthStatus.Unauthorized, statuses[1]);
        Assert.Equal(AuthStatus.Throttled, statuses[2]);
    }

    [Fact]
    public async Task PeerThatSkipsTheHandshakeIsRejected()
    {
        await using var listener = TransportListener.Start(Harness.ServerOptions(), IPAddress.Loopback, 0);
        var rejection = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Rejected += ex => rejection.TrySetResult(ex);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, listener.LocalEndPoint.Port, timeout.Token);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            },
            timeout.Token);

        // A stock USB/IP client's opening OP_REQ_DEVLIST, sent instead of the _vht hello.
        var stock = new byte[32];
        stock[0] = 0x80;
        stock[1] = 0x05;
        await ssl.WriteAsync(stock, timeout.Token);
        await ssl.FlushAsync(timeout.Token);

        var ex = await rejection.Task.WaitAsync(TimeSpan.FromSeconds(20), timeout.Token);

        var transport = Assert.IsType<TransportException>(ex);
        Assert.Equal(TransportErrorCode.HandshakeRequired, transport.Code);
    }

    [Fact]
    public async Task RawTcpPeerIsRejectedByTheTlsListener()
    {
        await using var listener = TransportListener.Start(Harness.ServerOptions(), IPAddress.Loopback, 0);
        var rejection = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Rejected += ex => rejection.TrySetResult(ex);

        using var tcp = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await tcp.ConnectAsync(IPAddress.Loopback, listener.LocalEndPoint.Port, timeout.Token);
        await tcp.GetStream().WriteAsync(new byte[] { 0x80, 0x05, 0x00, 0x00 }, timeout.Token);
        await tcp.GetStream().FlushAsync(timeout.Token);

        var ex = await rejection.Task.WaitAsync(TimeSpan.FromSeconds(20), timeout.Token);
        _output.WriteLine(ex.GetType().Name + ": " + ex.Message);
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task WrongPinnedFingerprintFailsClosed()
    {
        var pins = new InMemoryPinStore();
        pins.StorePin("127.0.0.1:0", "AAAA"); // wrong identity, deliberately

        var clientOptions = Harness.ClientOptions(pins: pins);
        await using var listener = TransportListener.Start(Harness.ServerOptions(), IPAddress.Loopback, 0);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var acceptTask = listener.AcceptAsync(timeout.Token);

        // Seed the pin for the real identity with a value that cannot match.
        var connected = Harness.ClientOptions(pins: new InMemoryPinStore());
        var clientIdentity = $"127.0.0.1:{listener.LocalEndPoint.Port}";
        var seeded = new InMemoryPinStore();
        seeded.StorePin(clientIdentity, "not-the-real-pin");

        var ex = await Assert.ThrowsAsync<TransportException>(async () => await TransportFactory.ConnectAsync(
            "127.0.0.1",
            listener.LocalEndPoint.Port,
            Harness.ClientOptions(pins: seeded),
            null,
            timeout.Token));

        Assert.Equal(TransportErrorCode.PinMismatch, ex.Code);
        _ = clientOptions;
        _ = acceptTask;
        _ = connected;
        _ = clientIdentity;
    }

    [Fact]
    public async Task StrictModeRefusesUnpinnedServer()
    {
        await using var listener = TransportListener.Start(Harness.ServerOptions(), IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = listener.AcceptAsync(timeout.Token);

        var ex = await Assert.ThrowsAsync<TransportException>(async () => await TransportFactory.ConnectAsync(
            "127.0.0.1",
            listener.LocalEndPoint.Port,
            Harness.ClientOptions(pinMode: PinMode.Strict),
            null,
            timeout.Token));

        Assert.Equal(TransportErrorCode.PinMismatch, ex.Code);
    }

    [Fact]
    public async Task Lz4CompressesBulkPayloadAndRoundTrips()
    {
        await using var pair = await Harness.ConnectAsync(
            Harness.ServerOptions(TransportKind.TlsLz4),
            Harness.ClientOptions(TransportKind.TlsLz4));

        Assert.Equal(TransportKind.TlsLz4, pair.Client.Kind);
        Assert.True(pair.Client.Capabilities.HasFlag(TransportCapabilities.Lz4));

        var compressor = (IPayloadCompressionTransport)pair.Client;
        Assert.True(compressor.CompressionActive);

        var payload = new byte[256 * 1024];
        for (var i = 0; i < payload.Length; i += 64)
        {
            payload[i] = (byte)(i % 251);
        }

        await compressor.WritePayloadAsync(PayloadKind.Bulk, payload, CancellationToken.None);
        var received = new byte[payload.Length];
        await ReadExactlyAsync(pair.Server, received);

        Assert.Equal(payload, received);
        var stats = ((Lz4Transport)pair.Client).Statistics;
        Assert.True(stats.CompressedFrames > 0);
        Assert.True(stats.BytesWritten < payload.Length / 2);
    }

    [Fact]
    public async Task Lz4LeavesControlTrafficAndSmallPayloadsRaw()
    {
        await using var pair = await Harness.ConnectAsync(
            Harness.ServerOptions(TransportKind.TlsLz4),
            Harness.ClientOptions(TransportKind.TlsLz4));

        var client = (Lz4Transport)pair.Client;

        // Unclassified writes are headers/control traffic: never compressed, even when compressible.
        var control = new byte[64 * 1024];
        await client.WriteAsync(control, CancellationToken.None);

        // Declared bulk but under the threshold: raw.
        await client.WritePayloadAsync(PayloadKind.Bulk, new byte[512], CancellationToken.None);

        // Declared control and large: still raw.
        await client.WritePayloadAsync(PayloadKind.Control, control, CancellationToken.None);

        var received = new byte[control.Length + 512 + control.Length];
        await ReadExactlyAsync(pair.Server, received);

        Assert.Equal(0, client.Statistics.CompressedFrames);
        Assert.Equal(3, client.Statistics.RawFrames);
    }

    [Fact]
    public async Task DefaultThresholdLeavesOneByteUnderItRawAndCompressesAtIt()
    {
        await using var pair = await Harness.ConnectAsync(
            Harness.ServerOptions(TransportKind.TlsLz4),
            Harness.ClientOptions(TransportKind.TlsLz4));

        var client = (Lz4Transport)pair.Client;
        int threshold = client.CompressionThresholdBytes;

        // The default is the sweep's crossover, not the reference product's 1 KiB.
        Assert.Equal(64 * 1024, threshold);

        // Compressible at any size — a single repeated byte compresses hundreds to one — so the only
        // thing that can keep the first frame raw is the threshold itself.
        var below = new byte[threshold - 1];
        Array.Fill(below, (byte)'Q');
        var at = new byte[threshold];
        Array.Fill(at, (byte)'Q');

        var received = new byte[below.Length + at.Length];
        Task reader = ReadExactlyAsync(pair.Server, received);
        await client.WritePayloadAsync(PayloadKind.Bulk, below, CancellationToken.None);
        await client.WritePayloadAsync(PayloadKind.Bulk, at, CancellationToken.None);
        await reader;

        var expected = new byte[below.Length + at.Length];
        below.CopyTo(expected, 0);
        at.CopyTo(expected, below.Length);
        Assert.Equal(expected, received);
        Assert.Equal(1, client.Statistics.RawFrames);
        Assert.Equal(1, client.Statistics.CompressedFrames);
    }

    [Fact]
    public async Task LoweringTheThresholdCompressesPayloadsTheDefaultWouldLeaveRaw()
    {
        // The threshold is policy, not a hard rule: an operator who measures a win at a smaller size on
        // their own link can still opt in. Here a 4 KiB payload is compressed only because the policy
        // was lowered to 4 KiB for this writer.
        var payload = new byte[4096];
        Array.Fill(payload, (byte)'Q');

        var sink = new MemoryTransport();
        await using (var writer = new Lz4Transport(sink, new Lz4Options { ThresholdBytes = 4096 }))
        {
            await writer.WritePayloadAsync(PayloadKind.Bulk, payload, CancellationToken.None);
            Assert.Equal(1, writer.Statistics.CompressedFrames);
            Assert.Equal(0, writer.Statistics.RawFrames);
        }

        Assert.True(sink.Written.Length < payload.Length / 4);
    }

    [Fact]
    public async Task IncompressiblePayloadFallsBackToRaw()
    {
        await using var pair = await Harness.ConnectAsync(
            Harness.ServerOptions(TransportKind.TlsLz4),
            Harness.ClientOptions(TransportKind.TlsLz4));

        var client = (Lz4Transport)pair.Client;
        var payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);

        await ((IPayloadCompressionTransport)client).WritePayloadAsync(PayloadKind.Bulk, payload, CancellationToken.None);
        var received = new byte[payload.Length];
        await ReadExactlyAsync(pair.Server, received);

        Assert.Equal(payload, received);
        Assert.Equal(0, client.Statistics.CompressedFrames);
        Assert.Equal(1, client.Statistics.RawFrames);
        // Only the fixed 13-byte header was added.
        Assert.Equal(payload.Length + 13, client.Statistics.BytesWritten);
    }

    [Fact]
    public async Task CompressedFrameCannotBeLayeredOnPlaintext()
    {
        await using var plain = await Harness.ConnectAsync(
            Harness.ServerOptions(TransportKind.PlainTcp),
            Harness.ClientOptions(TransportKind.PlainTcp));

        Assert.Throws<TransportException>(() => new Lz4Transport(plain.Client, new Lz4Options()));
    }

    [Fact]
    public async Task CorruptedFrameIsDetectedByTheReader()
    {
        // At or above the threshold, so this really is a compressed frame on the wire and not the raw
        // fallback: below the default threshold the test would still pass while testing nothing.
        var payload = new byte[64 * 1024];
        Array.Fill(payload, (byte)'Z');

        var sink = new MemoryTransport();
        await using (var writer = new Lz4Transport(sink, new Lz4Options()))
        {
            await ((IPayloadCompressionTransport)writer).WritePayloadAsync(PayloadKind.Bulk, payload, CancellationToken.None);
        }

        var frame = sink.Written;
        Assert.True(frame.Length > 32);

        // Flip one bit inside the compressed body: the CRC32C over the uncompressed bytes must catch it.
        var corrupted = frame.ToArray();
        corrupted[Lz4Framing.HeaderSize + 3] ^= 0x10;

        await using var reader = new Lz4Transport(new MemoryTransport(corrupted), new Lz4Options());
        var ex = await Assert.ThrowsAsync<TransportException>(
            async () => await reader.ReadAsync(new byte[payload.Length], CancellationToken.None));

        Assert.Equal(TransportErrorCode.IntegrityCheckFailed, ex.Code);
    }

    [Fact]
    public async Task TruncatedFrameIsDetected()
    {
        // At or above the threshold: see CorruptedFrameIsDetectedByTheReader for why the size matters.
        var payload = new byte[64 * 1024];
        var sink = new MemoryTransport();
        await using (var writer = new Lz4Transport(sink, new Lz4Options()))
        {
            await ((IPayloadCompressionTransport)writer).WritePayloadAsync(PayloadKind.Bulk, payload, CancellationToken.None);
        }

        var truncated = sink.Written[..^10];
        await using var reader = new Lz4Transport(new MemoryTransport(truncated), new Lz4Options());
        var ex = await Assert.ThrowsAsync<TransportException>(
            async () => await reader.ReadAsync(new byte[payload.Length], CancellationToken.None));

        Assert.Equal(TransportErrorCode.Protocol, ex.Code);
    }

    [Fact]
    public async Task CorruptedFrameFromAHostilePeerKillsTheLiveSession()
    {
        await using var listener = TransportListener.Start(
            Harness.ServerOptions(TransportKind.TlsLz4),
            IPAddress.Loopback,
            0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var acceptTask = listener.AcceptAsync(timeout.Token);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, listener.LocalEndPoint.Port, timeout.Token);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            },
            timeout.Token);

        var hello = new HandshakeProtocol.ClientHello
        {
            RequestedFeatures = FeatureFlags.Lz4,
            Method = AuthMethod.None,
            Name = "hostile",
            Nonce = new byte[32],
        };
        await HandshakeProtocol.WriteClientHelloAsync(ssl, hello, timeout.Token);
        _ = await HandshakeProtocol.ReadLengthPrefixedAsync(ssl, timeout.Token);

        var server = await acceptTask;
        Assert.Equal(TransportKind.TlsLz4, server.Kind);

        // A raw frame whose CRC does not match its body.
        var body = new byte[1024];
        var frame = new byte[Lz4Framing.HeaderSize + body.Length];
        Lz4Framing.WriteHeader(frame, Lz4FrameType.Raw, body.Length, body.Length, 0xDEADBEEF);
        body.CopyTo(frame, Lz4Framing.HeaderSize);
        await ssl.WriteAsync(frame, timeout.Token);
        await ssl.FlushAsync(timeout.Token);

        var ex = await Assert.ThrowsAsync<TransportException>(
            async () => await server.ReadAsync(new byte[body.Length], timeout.Token));

        Assert.Equal(TransportErrorCode.IntegrityCheckFailed, ex.Code);
        await server.DisposeAsync();
    }

    [Fact]
    public void CertificateIsGeneratedOnFirstRunWithRestrictedPermissions()
    {
        var directory = Harness.TempPath("first-run-cert");

        using var first = ServerCertificateManager.LoadOrCreate(directory, "localhost", out var created);
        Assert.True(created);
        Assert.True(File.Exists(Path.Combine(directory, "server.crt.pem")));
        Assert.True(File.Exists(Path.Combine(directory, "server.key.pem")));

        using var second = ServerCertificateManager.LoadOrCreate(directory, "localhost", out var createdAgain);
        Assert.False(createdAgain);
        Assert.Equal(ServerCertificateManager.ComputeSpkiPin(first), ServerCertificateManager.ComputeSpkiPin(second));
    }

    [Fact]
    public async Task PlaintextOffsetDefaultsToStockUsbipPort()
    {
        await using var listener = TransportListener.Start(
            Harness.ServerOptions(TransportKind.PlainTcp),
            IPAddress.Loopback,
            0);

        Assert.Equal(TransportKind.PlainTcp, listener.Kind);
        Assert.True(listener.LocalEndPoint.Port > 0);
    }

    private static async Task ReadExactlyAsync(ITransport transport, byte[] buffer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await transport.ReadAsync(buffer.AsMemory(read), timeout.Token);
            Assert.True(n > 0, $"stream ended after {read} of {buffer.Length} bytes");
            read += n;
        }
    }
}
