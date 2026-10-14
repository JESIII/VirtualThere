using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Vht.Core.Capture;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Policy;
using Vht.Core.Server;
using Vht.Server.Devices;
using Vht.Server.Net;
using Vht.Transport;

namespace Vht.Acceptance;

/// <summary>
/// Boots the production server stack — <see cref="VhtServerState"/> plus the real
/// <see cref="UsbIpTcpServer"/> accept loops on a real loopback TCP port — over a device source the
/// rig controls.
/// </summary>
/// <remarks>
/// Everything below the transport is the shipping code: the same export table, the same
/// <c>UsbIpSession</c>, the same wire codec, the same policy evaluation. The only substitution is
/// <see cref="ICaptureProvider"/>, and it is substituted because this machine cannot hand out a
/// real device (see <c>docs/ACCEPTANCE.md</c>).
/// </remarks>
public sealed class AcceptanceRig : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;

    private AcceptanceRig(
        ServerOptions options,
        ICaptureProvider capture,
        VhtServerState state,
        UsbIpTcpServer tcp,
        ILogger log,
        int port,
        BlockingServerTransportFactory? factory,
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate)
    {
        Options = options;
        Capture = capture;
        State = state;
        Tcp = tcp;
        Log = log;
        Port = port;
        TransportFactory = factory;
        Certificate = certificate;
    }

    public ServerOptions Options { get; }
    public ICaptureProvider Capture { get; }
    public VhtServerState State { get; }
    public UsbIpTcpServer Tcp { get; }
    public ILogger Log { get; }
    public int Port { get; }
    public BlockingServerTransportFactory? TransportFactory { get; }

    /// <summary>The throwaway listener certificate, when the rig is serving TLS.</summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate { get; }

    /// <summary>Diagnostic sink, so the report can quote the server's own log lines.</summary>
    public List<string> LogLines { get; } = new();

    /// <summary>Starts a rig exporting <paramref name="devices"/> on a fresh loopback port.</summary>
    public static async Task<AcceptanceRig> StartSyntheticAsync(
        IReadOnlyList<UsbDevice> devices,
        SyntheticDeviceOptions captureOptions,
        Vht.Transport.TransportKind transportKind,
        DevicePolicy? policy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);

        int port = FreeLoopbackPort();
        var capture = new SyntheticCaptureProvider(devices, captureOptions);

        ServerOptions options = CreateOptions(port, transportKind);
        var log = new CapturingLogger();
        var state = new VhtServerState(options, capture, log, hooks: null, policy: policy);

        BlockingServerTransportFactory? factory = null;
        Func<NetworkStream, Vht.Core.Transport.TransportKind, Vht.Core.Transport.ITransport>? transportFactory = null;
        System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;

        if (transportKind != Vht.Transport.TransportKind.PlainTcp)
        {
            cert = CreateCertificate();
            factory = new BlockingServerTransportFactory();
            transportFactory = factory.Build(ServerTransportOptions(transportKind, cert));
        }

        var tcp = new UsbIpTcpServer(state, transportFactory);

        var rig = new AcceptanceRig(options, capture, state, tcp, log, port, factory, cert);
        rig.Start();
        await rig.State.RefreshExportsAsync(ct).ConfigureAwait(false);
        return rig;
    }

    /// <summary>Starts a rig over a caller-supplied capture provider (so a test can hotplug).</summary>
    public static async Task<AcceptanceRig> StartWithProviderAsync(
        ICaptureProvider capture,
        Vht.Transport.TransportKind transportKind,
        DevicePolicy? policy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capture);

        int port = FreeLoopbackPort();
        ServerOptions options = CreateOptions(port, transportKind);
        var log = new CapturingLogger();
        var state = new VhtServerState(options, capture, log, hooks: null, policy: policy);

        BlockingServerTransportFactory? factory = null;
        Func<NetworkStream, Vht.Core.Transport.TransportKind, Vht.Core.Transport.ITransport>? transportFactory = null;
        System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;

        if (transportKind != Vht.Transport.TransportKind.PlainTcp)
        {
            cert = CreateCertificate();
            factory = new BlockingServerTransportFactory();
            transportFactory = factory.Build(ServerTransportOptions(transportKind, cert));
        }

        var tcp = new UsbIpTcpServer(state, transportFactory);
        var rig = new AcceptanceRig(options, capture, state, tcp, log, port, factory, cert);
        rig.Start();
        await rig.State.RefreshExportsAsync(ct).ConfigureAwait(false);
        return rig;
    }

    /// <summary>
    /// Starts a rig over the real <see cref="WinUsbCaptureProvider"/> — the same provider the
    /// shipping server builds — so the hardware probe reports what this machine actually offers.
    /// </summary>
    public static AcceptanceRig StartReal(int port, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);

        ServerOptions options = CreateOptions(port, Vht.Transport.TransportKind.PlainTcp);
        var enumerator = new UsbDeviceEnumerator(options, log);
        var capture = new WinUsbCaptureProvider(enumerator, options, log, rebinder: null);
        var state = new VhtServerState(options, capture, log);
        var tcp = new UsbIpTcpServer(state);

        var rig = new AcceptanceRig(options, capture, state, tcp, log, port, null, null);
        rig.Start();
        return rig;
    }

    private void Start()
    {
        Tcp.Bind();
        _acceptLoop = Task.Run(() => Tcp.RunAsync(_stop.Token), CancellationToken.None);
    }

    public static ServerOptions CreateOptions(int port, Vht.Transport.TransportKind kind)
    {
        bool plaintext = kind == Vht.Transport.TransportKind.PlainTcp;

        var options = new ServerOptions
        {
            ServerName = "vht-accept",
            PlaintextListenerEnabled = plaintext,
            PlaintextPort = port,
            PlaintextAddress = "127.0.0.1",
            TlsListenerEnabled = !plaintext,
            TlsPort = port,
            TlsAddress = "127.0.0.1",
            CaptureMode = "auto",
            ControlPlaneEnabled = false,
            LogLevel = LogLevel.Warn,
        };
        return options;
    }

    /// <summary>Transport options for the server end of a connection.</summary>
    public static TransportOptions ServerTransportOptions(
        Vht.Transport.TransportKind kind, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => new()
    {
        Kind = kind,
        ServerName = "vht-accept",
        ClientName = "vht-accept-server",
        Tls = new TlsOptions
        {
            // The rig mints its own certificate and hands it over explicitly, so nothing is written
            // to the machine's certificate store and no elevation is required.
            ServerCertificate = certificate,
            PinMode = PinMode.Insecure,
        },
        Lz4 = new Lz4Options { Enabled = kind == Vht.Transport.TransportKind.TlsLz4 },
    };

    /// <summary>Client-side transport options matching <paramref name="kind"/>.</summary>
    public static TransportOptions ClientTransportOptions(Vht.Transport.TransportKind kind) => new()
    {
        Kind = kind,
        ServerName = "vht-accept",
        ClientName = "vht-accept-client",
        // Loopback-only rig: PinMode.Insecure is documented for exactly this case.
        Tls = new TlsOptions { PinMode = PinMode.Insecure },
        Lz4 = new Lz4Options { Enabled = kind == Vht.Transport.TransportKind.TlsLz4 },
    };

    /// <summary>Mints a throwaway self-signed certificate for the loopback listener.</summary>
    public static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            new System.Security.Cryptography.X509Certificates.X500DistinguishedName("CN=vht-accept"),
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using System.Security.Cryptography.X509Certificates.X509Certificate2 ephemeral =
            request.CreateSelfSigned(now.AddDays(-1), now.AddDays(7));

        // Windows' Schannel refuses a TLS 1.3 handshake with an ephemeral private key
        // ("the platform does not support ephemeral keys"), and CreateSelfSigned produces exactly
        // that. Round-tripping through PKCS#12 with PersistKeySet gives the rig a certificate with a
        // persisted user-profile key container, which Schannel accepts, and keeps the rig's private
        // key out of any machine store (no elevation, nothing shared with the real product's cert).
        byte[] pfx = ephemeral.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx);
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            pfx,
            password: null,
            System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.PersistKeySet
            | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.UserKeySet);
    }

    public static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }

        await Tcp.DisposeAsync().ConfigureAwait(false);

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        await Capture.DisposeAsync().ConfigureAwait(false);
        Certificate?.Dispose();
        LogLines.Clear();
        _stop.Dispose();
    }

    /// <summary>An <see cref="ILogger"/> that keeps the lines, so the report can quote them.</summary>
    public sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();

        public LogLevel MinimumLevel => LogLevel.Detail;

        public void Write(LogLevel level, LogEvent evt, string message, IReadOnlyDictionary<string, object?>? fields = null)
        {
            lock (_gate)
            {
                LogLines.Add($"[{level}] {evt}: {message}");
                if (LogLines.Count > 20000) LogLines.RemoveRange(0, 10000);
            }
        }

        public List<string> Lines => LogLines;

        private readonly List<string> LogLines = new();

        public int Count
        {
            get { lock (_gate) return LogLines.Count; }
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate) return LogLines.ToArray();
        }
    }
}

/// <summary>Small helpers the report needs about the process itself.</summary>
public static class ProcessMetrics
{
    public static long ManagedBytes => GC.GetTotalMemory(forceFullCollection: false);

    public static long WorkingSetBytes
    {
        get
        {
            using Process p = Process.GetCurrentProcess();
            return p.WorkingSet64;
        }
    }

    public static long PrivateBytes
    {
        get
        {
            using Process p = Process.GetCurrentProcess();
            return p.PrivateMemorySize64;
        }
    }

    public static long Threads
    {
        get
        {
            using Process p = Process.GetCurrentProcess();
            return p.Threads.Count;
        }
    }

    public static string Describe() =>
        $"managed={ManagedBytes / (1024.0 * 1024.0):0.0} MiB, " +
        $"working_set={Work