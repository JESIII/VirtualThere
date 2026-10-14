using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Vht.Transport.Auth;
using Vht.Transport.Tls;

namespace Vht.Transport.Tests;

/// <summary>Shared fixtures for the transport test suite.</summary>
internal static class Harness
{
    /// <summary>PBKDF2 work factor used by tests. The production default is asserted separately (one slow test).</summary>
    public const int FastIterations = 2_000;

    /// <summary>Scratch directory for generated material.</summary>
    public static string ScratchDirectory { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "vht-transport-tests");

    private static readonly Lazy<X509Certificate2> LazyCertificate = new(() =>
        ServerCertificateManager.LoadOrCreate(ScratchDirectory, "localhost", out _, allowInsecureKeyStorage: true));

    /// <summary>Server certificate generated on first use.</summary>
    public static X509Certificate2 Certificate => LazyCertificate.Value;

    /// <summary>Creates a unique scratch path for a test.</summary>
    public static string TempPath(string name)
    {
        var path = System.IO.Path.Combine(ScratchDirectory, "cases", $"{name}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Server-side options.</summary>
    public static TransportOptions ServerOptions(
        TransportKind kind = TransportKind.Tls,
        AuthenticationOptions? authentication = null,
        Lz4Options? lz4 = null,
        RateLimitOptions? rateLimit = null,
        bool requireTls13 = false) => new()
        {
            Kind = kind,
            ServerName = "localhost",
            HandshakeTimeout = TimeSpan.FromSeconds(20),
            Lz4 = lz4 ?? new Lz4Options { Enabled = kind == TransportKind.TlsLz4 },
            Authentication = (authentication ?? new AuthenticationOptions()) with
            {
                Pbkdf2Iterations = authentication?.Pbkdf2Iterations ?? FastIterations,
                RateLimit = rateLimit ?? authentication?.RateLimit ?? new RateLimitOptions(),
            },
            Tls = new TlsOptions
            {
                ServerCertificate = kind == TransportKind.PlainTcp ? null : Certificate,
                RequireTls13 = requireTls13,
            },
        };

    /// <summary>Client-side options.</summary>
    public static TransportOptions ClientOptions(
        TransportKind kind = TransportKind.Tls,
        AuthRequest? credential = null,
        IPinStore? pins = null,
        PinMode pinMode = PinMode.TrustOnFirstUse,
        Lz4Options? lz4 = null) => new()
        {
            Kind = kind,
            ServerName = "localhost",
            ClientName = "test-client",
            HandshakeTimeout = TimeSpan.FromSeconds(20),
            Lz4 = lz4 ?? new Lz4Options { Enabled = kind == TransportKind.TlsLz4 },
            ClientCredential = credential ?? AuthRequest.None,
            Tls = new TlsOptions
            {
                PinMode = pinMode,
                PinStore = pins ?? new InMemoryPinStore(),
            },
        };

    /// <summary>Connects a client to a fresh listener and returns both ends.</summary>
    public static async Task<Pair> ConnectAsync(
        TransportOptions serverOptions,
        TransportOptions clientOptions,
        CancellationToken ct = default)
    {
        var listener = TransportListener.Start(serverOptions, IPAddress.Loopback, 0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var acceptTask = listener.AcceptAsync(timeout.Token);
        var client = await TransportFactory
            .ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, clientOptions, clientOptions.ClientCredential, timeout.Token)
            .ConfigureAwait(false);
        var server = await acceptTask.ConfigureAwait(false);

        return new Pair(listener, client, server);
    }

    /// <summary>Runs the authentication exchange on both ends concurrently.</summary>
    public static async Task<(AuthResult Server, AuthResult Client)> AuthenticateAsync(Pair pair, AuthRequest clientRequest)
    {
        var serverTask = pair.Server.AuthenticateAsync(AuthRequest.None, CancellationToken.None);
        var clientTask = pair.Client.AuthenticateAsync(clientRequest, CancellationToken.None);
        await Task.WhenAll(serverTask, clientTask).ConfigureAwait(false);
        return (await serverTask.ConfigureAwait(false), await clientTask.ConfigureAwait(false));
    }
}

/// <summary>A connected client/server pair plus the listener that produced it.</summary>
internal sealed class Pair : IAsyncDisposable
{
    public Pair(TransportListener listener, ITransport client, ITransport server)
    {
        Listener = listener;
        Client = client;
        Server = server;
    }

    public TransportListener Listener { get; }

    public ITransport Client { get; }

    public ITransport Server { get; }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        await Listener.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Time provider the tests drive by hand, so lockout and refill behaviour is deterministic.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

/// <summary>
/// An in-memory stand-in for a TLS transport, used to drive <see cref="Lz4Transport"/> directly so
/// corrupted frames can be injected deterministically instead of hoping to corrupt a live socket.
/// </summary>
internal sealed class MemoryTransport : ITransport
{
    private readonly MemoryStream _readable;
    private readonly MemoryStream _writable = new();

    public MemoryTransport(byte[]? readFrom = null) => _readable = new MemoryStream(readFrom ?? []);

    public TransportKind Kind => TransportKind.Tls;

    public TransportCapabilities Capabilities => TransportCapabilities.HandshakeRequired | TransportCapabilities.Lz4;

    public byte[] Written => _writable.ToArray();

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        _readable.ReadAsync(buffer, ct);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        _writable.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct) =>
        Task.FromResult(AuthResult.NoneRequired);

    public ValueTask DisposeAsync()
    {
        _readable.Dispose();
        _writable.Dispose();
        return ValueTask.CompletedTask;
    }
}
