using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Vht.Transport;
using Vht.Transport.Auth;
using Vht.Transport.Tls;

namespace Vht.Transport.Bench;

/// <summary>
/// Measures the two numbers the transport card has to report: MB/s on loopback for each transport
/// flavour, and the cost of the TLS + <c>_vht</c> handshake.
/// </summary>
/// <remarks>
/// Everything here runs over the real sockets, the real Schannel handshake and the real framing — no
/// mock stream — because the point of the number is what a caller can actually get. Loopback is the
/// fastest case a user can see; a LAN or WAN hop adds latency that no transport change removes.
/// </remarks>
internal static class Program
{
    private const int ChunkBytes = 1 * 1024 * 1024;
    private const int TransferBytes = 256 * 1024 * 1024;
    private const int HandshakeSamples = 30;
    private const int WarmupHandshakes = 3;

    /// <summary>
    /// Throughput is measured more than once per case because a single loopback run is not
    /// reproducible: the writer and reader share cores with the TLS record layer and the collector, and
    /// repeated runs on this machine vary by more than 2x. The median of five is reported, with the
    /// observed range, rather than a single lucky or unlucky number.
    /// </summary>
    private const int ThroughputRepeats = 5;

    /// <summary>Real work factor, used for the authenticated-handshake measurement (never lowered in production).</summary>
    private const int RealWorkFactor = SecretVerifier.DefaultIterations;

    /// <summary>
    /// Five samples is enough for a figure dominated by a deliberate ~hundreds-of-milliseconds KDF; more
    /// would only measure the machine's thermal state.
    /// </summary>
    private const int AuthenticatedSamples = 5;

    private static X509Certificate2? _serverCertificate;

    private static async Task<int> Main(string[] args)
    {
        var outputDirectory = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "vht-transport-bench");

        Directory.CreateDirectory(outputDirectory);

        // Loaded once, exactly as a long-running server does. Resolving the certificate inside every
        // accept (the alternative the transport also supports) re-reads the PEM and re-imports the key
        // through Schannel, and it shows up as ~20 ms of handshake time per connection.
        _serverCertificate = ServerCertificateManager.LoadOrCreate(
            Path.Combine(outputDirectory, "cert"),
            "localhost",
            out _);

        var results = new List<Result>();
        var lines = new List<string>();

        void Emit(string line)
        {
            Console.WriteLine(line);
            lines.Add(line);
        }

        Emit($"vht-transport-bench  started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Emit($"host: {Environment.MachineName}  os: {Environment.OSVersion.VersionString}");
        Emit($"cores: {Environment.ProcessorCount}  runtime: {Environment.Version}");
        Emit($"loopback only (127.0.0.1)  transfer: {TransferBytes / (1024 * 1024)} MiB per case  chunk: {ChunkBytes / 1024} KiB");
        Emit(string.Empty);

        foreach (var kind in new[] { TransportKind.PlainTcp, TransportKind.Tls, TransportKind.TlsLz4 })
        {
            var handshake = await MeasureHandshakeAsync(kind, outputDirectory).ConfigureAwait(false);
            var compressible = await MeasureThroughputAsync(kind, compressible: true, outputDirectory).ConfigureAwait(false);
            var random = await MeasureThroughputAsync(kind, compressible: false, outputDirectory).ConfigureAwait(false);

            results.Add(new Result(kind, handshake, compressible, random));
        }

        var authenticated = await MeasureAuthenticatedHandshakeAsync(outputDirectory).ConfigureAwait(false);

        Emit("kind      handshake mean/p50/p95 (ms)   MB/s compressible (median min-max)      MB/s incompressible (median min-max)");
        Emit("--------  ---------------------------   -----------------------------------     ----------------------------------");
        foreach (var r in results)
        {
            Emit(string.Format(
                "{0,-9} {1,8:F2} / {2,6:F2} / {3,6:F2}      {4,7:F1} {5,7:F1}-{6,7:F1}      {7,7:F1} {8,7:F1}-{9,7:F1}",
                r.Kind,
                r.HandshakeMean,
                r.HandshakeP50,
                r.HandshakeP95,
                r.Compressible.Median,
                r.Compressible.Min,
                r.Compressible.Max,
                r.Random.Median,
                r.Random.Min,
                r.Random.Max));
        }

        Emit(string.Empty);
        Emit("authenticated handshake (TLS + _vht hello + HMAC challenge-response, shared secret, "
            + $"{RealWorkFactor:N0} PBKDF2 iterations), {AuthenticatedSamples} samples:");
        Emit(string.Format(
            "  mean {0:F1} ms   p50 {1:F1} ms   p95 {2:F1} ms   (of which PBKDF2-HMAC-SHA256 on the client is the dominant term)",
            authenticated.Mean,
            authenticated.P50,
            authenticated.P95));

        Emit(string.Empty);
        foreach (var r in results)
        {
            if (r.Kind != TransportKind.TlsLz4)
            {
                // Frame counters exist only on the compressed leg; the other kinds write the payload
                // verbatim, so the wire cost is the payload cost.
                Emit($"{r.Kind}: no per-frame counters (no framing layer), {TransferBytes / (1024 * 1024)} MiB payload per run");
                continue;
            }

            Emit($"{r.Kind}: last compressible run = {r.Compressible.RawFrames + r.Compressible.CompressedFrames} frames, "
                + $"{r.Compressible.CompressedFrames} compressed, "
                + $"wire bytes {r.Compressible.WireBytes:N0} for {r.Compressible.PayloadBytes:N0} payload bytes "
                + $"(ratio {(double)r.Compressible.PayloadBytes / Math.Max(1, r.Compressible.WireBytes):F1}x)");
        }

        var reportPath = Path.Combine(outputDirectory, "bench.txt");
        await File.WriteAllLinesAsync(reportPath, lines).ConfigureAwait(false);
        Emit(string.Empty);
        Emit($"raw report: {reportPath}");

        return 0;
    }

    private static async Task<HandshakeResult> MeasureHandshakeAsync(TransportKind kind, string outputDirectory)
    {
        // One listener, many connections: the client's pin key is host:port, so reusing the same
        // listener means TOFU genuinely records on the first connection and verifies on every later one
        // (a fresh port each time would make the pin check vacuous).
        var certificateDirectory = Path.Combine(outputDirectory, "cert");
        var serverOptions = Options(kind, certificateDirectory, server: true);
        var clientOptions = Options(kind, certificateDirectory, server: false);
        await using var listener = TransportListener.Start(serverOptions, IPAddress.Loopback, 0);
        var port = listener.LocalEndPoint.Port;

        for (var i = 0; i < WarmupHandshakes; i++)
        {
            await using var warm = await ConnectAsync(listener, port, clientOptions).ConfigureAwait(false);
        }

        var samples = new double[HandshakeSamples];
        for (var i = 0; i < HandshakeSamples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await using var pair = await ConnectAsync(listener, port, clientOptions).ConfigureAwait(false);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(samples);
        return new HandshakeResult(
            samples.Average(),
            samples[samples.Length / 2],
            samples[Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.95) - 1)]);
    }

    /// <summary>
    /// The full cost a user waits for on a TLS listener: TCP, the Schannel handshake, the <c>_vht</c>
    /// hello exchange, and the HMAC challenge-response at the real PBKDF2 work factor. The throughput
    /// table's handshake column deliberately uses a low work factor so it measures the transport and
    /// not the KDF; this is the number that includes the KDF.
    /// </summary>
    private static async Task<HandshakeResult> MeasureAuthenticatedHandshakeAsync(string outputDirectory)
    {
        var certificateDirectory = Path.Combine(outputDirectory, "cert");
        const string Secret = "bench-shared-secret";
        var credential = AuthRequest.ForSharedSecret(Secret);

        var serverOptions = Options(TransportKind.Tls, certificateDirectory, server: true) with
        {
            Authentication = new AuthenticationOptions
            {
                SharedSecret = Secret,
                Pbkdf2Iterations = RealWorkFactor,
            },
        };

        await using var listener = TransportListener.Start(serverOptions, IPAddress.Loopback, 0);
        var clientOptions = Options(TransportKind.Tls, certificateDirectory, server: false) with
        {
            ClientCredential = credential,
        };

        var samples = new double[AuthenticatedSamples];
        for (var i = 0; i < AuthenticatedSamples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var client = await TransportFactory
                .ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, clientOptions, credential, CancellationToken.None)
                .ConfigureAwait(false);
            var server = await listener.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
            var serverTask = server.AuthenticateAsync(AuthRequest.None, CancellationToken.None);
            var clientTask = client.AuthenticateAsync(credential, CancellationToken.None);
            await Task.WhenAll(serverTask, clientTask).ConfigureAwait(false);

            var clientResult = await clientTask.ConfigureAwait(false);
            if (!clientResult.IsSuccess)
            {
                throw new InvalidOperationException($"bench authentication failed: {clientResult.Status} {clientResult.Message}");
            }

            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await client.DisposeAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
        }

        Array.Sort(samples);
        return new HandshakeResult(
            samples.Average(),
            samples[samples.Length / 2],
            samples[Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.95) - 1)]);
    }

    private static async Task<ThroughputSummary> MeasureThroughputAsync(TransportKind kind, bool compressible, string outputDirectory)
    {
        var samples = new List<ThroughputResult>(ThroughputRepeats);
        for (var repeat = 0; repeat < ThroughputRepeats; repeat++)
        {
            samples.Add(await MeasureThroughputOnceAsync(kind, compressible, outputDirectory).ConfigureAwait(false));
        }

        var ordered = samples.OrderBy(s => s.MegabytesPerSecond).ToList();
        return new ThroughputSummary(
            ordered[ordered.Count / 2],
            ordered[0].MegabytesPerSecond,
            ordered[^1].MegabytesPerSecond);
    }

    private static async Task<ThroughputResult> MeasureThroughputOnceAsync(TransportKind kind, bool compressible, string outputDirectory)
    {
        var payload = new byte[ChunkBytes];
        if (compressible)
        {
            // Highly compressible, and stable across the run so LZ4 has something to win on.
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 251);
            }
        }
        else
        {
            Random.Shared.NextBytes(payload);
        }

        await using var pair = await ConnectAsync(kind, outputDirectory).ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var chunks = TransferBytes / ChunkBytes;
        var received = 0L;
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[ChunkBytes];
            while (received < TransferBytes)
            {
                var read = await pair.Server.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;
            }
        });

        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < chunks; i++)
        {
            if (kind == TransportKind.TlsLz4 && pair.Client is IPayloadCompressionTransport compression)
            {
                await compression.WritePayloadAsync(PayloadKind.Bulk, payload, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await pair.Client.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            }
        }

        await reader.ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(start);

        var stats = (pair.Client as Lz4Transport)?.Statistics;
        return new ThroughputResult(
            TransferBytes / (1024.0 * 1024.0) / elapsed.TotalSeconds,
            elapsed,
            stats?.BytesWritten ?? 0,
            stats?.PayloadBytesWritten ?? 0,
            stats?.RawFrames ?? 0,
            stats?.CompressedFrames ?? 0);
    }

    private static async Task<Pair> ConnectAsync(TransportKind kind, string outputDirectory)
    {
        var certificateDirectory = Path.Combine(outputDirectory, "cert");
        var listener = TransportListener.Start(Options(kind, certificateDirectory, server: true), IPAddress.Loopback, 0);
        var pair = await ConnectAsync(listener, listener.LocalEndPoint.Port, Options(kind, certificateDirectory, server: false))
            .ConfigureAwait(false);
        return pair with { OwnsListener = true };
    }

    private static async Task<Pair> ConnectAsync(TransportListener listener, int port, TransportOptions clientOptions)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var acceptTask = listener.AcceptAsync(timeout.Token);
        var client = await TransportFactory
            .ConnectAsync("127.0.0.1", port, clientOptions, clientOptions.ClientCredential, timeout.Token)
            .ConfigureAwait(false);
        var server = await acceptTask.ConfigureAwait(false);
        return new Pair(listener, client, server, ownsListener: false);
    }

    private static TransportOptions Options(TransportKind kind, string certificateDirectory, bool server) => new()
    {
        Kind = kind,
        ServerName = "localhost",
        ClientName = "vht-transport-bench",
        HandshakeTimeout = TimeSpan.FromSeconds(20),
        Lz4 = new Lz4Options { Enabled = kind == TransportKind.TlsLz4 },
        Authentication = new AuthenticationOptions { Pbkdf2Iterations = 2_000 },
        Tls = new TlsOptions
        {
            CertificateDirectory = certificateDirectory,
            ServerCertificate = server ? _serverCertificate : null,
            PinMode = PinMode.TrustOnFirstUse,
            PinStore = server ? null : new InMemoryPinStore(),
            Protocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        },
    };

    private sealed record HandshakeResult(double Mean, double P50, double P95);

    private sealed record ThroughputResult(
        double MegabytesPerSecond,
        TimeSpan Elapsed,
        long WireBytes,
        long PayloadBytes,
        long RawFrames,
        long CompressedFrames);

    private sealed record ThroughputSummary(ThroughputResult MedianSample, double Min, double Max)
    {
        public double Median => MedianSample.MegabytesPerSecond;

        public long WireBytes => MedianSample.WireBytes;

        public long PayloadBytes => MedianSample.PayloadBytes;

        public long RawFrames => MedianSample.RawFrames;

        public long CompressedFrames => MedianSample.CompressedFrames;
    }

    private sealed record Result(
        TransportKind Kind,
        HandshakeResult Handshake,
        ThroughputSummary Compressible,
        ThroughputSummary Random)
    {
        public double HandshakeMean => Handshake.Mean;

        public double HandshakeP50 => Handshake.P50;

        public double HandshakeP95 => Handshake.P95;

        public double CompressibleMBps => Compressible.Median;

        public double RandomMBps => Random.Median;
    }

    private sealed record Pair : IAsyncDisposable
    {
        private readonly TransportListener _listener;

        public Pair(TransportListener listener, ITransport client, ITransport server, bool ownsListener)
        {
            _listener = listener;
            OwnsListener = ownsListener;
            Client = client;
            Server = server;
        }

        public bool OwnsListener { get; init; }

        public ITransport Client { get; }

        public ITransport Server { get; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            await Server.DisposeAsync().ConfigureAwait(false);
            if (OwnsListener)
            {
                await _listener.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
