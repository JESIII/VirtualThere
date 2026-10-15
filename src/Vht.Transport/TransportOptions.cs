using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Vht.Transport;

/// <summary>How a client decides whether to trust a server certificate (ARCHITECTURE.md §6.1, §6.5).</summary>
public enum PinMode
{
    /// <summary>
    /// Accept any certificate. Loopback testing and the loopback bridge's own hop only; never a
    /// default, and never reachable from a config file without an explicit acknowledgement key.
    /// </summary>
    Insecure = 0,

    /// <summary>
    /// Trust on first use: the first certificate seen for a peer is recorded by SPKI SHA-256 and every
    /// later connection must present the same key. The default for <c>vht-client</c>.
    /// </summary>
    TrustOnFirstUse = 1,

    /// <summary>Refuse any peer without a recorded pin, and refuse a changed pin. The unattended/headless mode.</summary>
    Strict = 2,
}

/// <summary>LZ4 policy. Mirrors the reference product's <c>CompressionLimit</c> semantics under our own vocabulary.</summary>
public sealed record Lz4Options
{
    /// <summary>Whether the client offers LZ4 and the server accepts it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Payloads below this many bytes are never compressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>64 KiB, measured, not chosen.</b> The payload-size sweep in
    /// <c>tests/Vht.Transport.Bench</c> (<c>sweep</c> mode; every run preserved at
    /// <c>artifacts/transport/sweep-run*.txt</c>) swept 4/8/16/32/64/256 KiB and 1 MiB on this host,
    /// compressible and incompressible, median of 7 runs per case with the observed range. On
    /// <em>compressible</em> payload, LZ4 versus plain TLS, three consecutive sweeps:
    /// </para>
    /// <list type="bullet">
    /// <item><description>4 KiB: −8.4%, −12.7%, −10.6%</description></item>
    /// <item><description>8 KiB: −6.5%, −25.9%, −18.0%</description></item>
    /// <item><description>16 KiB: +7.3% (one outlier run), −26.3%, −33.1%</description></item>
    /// <item><description>32 KiB: −1.9%, −4.2%, −0.2% — break-even</description></item>
    /// <item><description><b>64 KiB: +24.9%, +21.2%, +23.3%</b> — with non-overlapping ranges every run</description></item>
    /// <item><description>256 KiB: +44.2%, +48.6%, +44.0%</description></item>
    /// <item><description>1 MiB: +50.8%, +43.1%, +47.3%</description></item>
    /// </list>
    /// <para>
    /// The sign flips between 32 KiB and 64 KiB, so 64 KiB is the smallest payload size at which
    /// compression was measured to win — and it wins with ranges that do not overlap the TLS arm, in
    /// every run. The previous default (1024) put every 1–32 KiB payload squarely in the measured-loss
    /// region; payloads below 64 KiB are now left raw by construction, and only sizes the measurement
    /// supports are offered to LZ4. The 32–64 KiB region was not swept, so it is treated as a loss
    /// rather than assumed a win. See <c>docs/TRANSPORT.md</c> §6 and §9.4.
    /// </para>
    /// <para>
    /// <b>What this threshold does not fix.</b> Incompressible payload loses at every size (−21% to
    /// −60% measured) because a frame pays CRC-32C and a copy whether or not it ends up compressed, and
    /// the threshold only skips the compression <em>attempt</em>. That cost is the CRC-32C item filed
    /// separately, not something a threshold can address.
    /// </para>
    /// <para>
    /// <b>The value now applies on the shipping path too.</b> Until card t_825b4f08 it did not: the
    /// USB/IP session classified every reply byte as control, so this threshold was never consulted and
    /// no URB ever compressed. <c>UsbIpSession</c> now declares a reply's data stage as its endpoint's
    /// transfer class through <c>Vht.Core.Transport.IPayloadKindTransport</c>, so a payload at or above
    /// this value is offered to LZ4 in production and one below it is written whole (see
    /// <c>docs/TRANSPORT.md</c> §6.1 and §9.5).
    /// </para>
    /// </remarks>
    public int ThresholdBytes { get; init; } = 64 * 1024;

    /// <summary>Largest payload frame either peer will send or accept. Bounds decompression.</summary>
    public int MaxPayloadFrameBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Validates the policy.</summary>
    public void Validate()
    {
        if (ThresholdBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ThresholdBytes), "threshold cannot be negative");
        }

        if (MaxPayloadFrameBytes is < 1024 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxPayloadFrameBytes),
                "max payload frame must be between 1 KiB and 64 MiB");
        }
    }
}

/// <summary>TLS policy for either end.</summary>
public sealed record TlsOptions
{
    /// <summary>
    /// Enabled protocols. Default is TLS 1.3 with TLS 1.2 as a floor, because Schannel only speaks
    /// TLS 1.3 on Windows 11 / Server 2022 while the client driver we consume supports Windows 10
    /// 1903+. Set <see cref="RequireTls13"/> to refuse anything below 1.3.
    /// </summary>
    public SslProtocols Protocols { get; init; } = SslProtocols.Tls13 | SslProtocols.Tls12;

    /// <summary>When true, a session negotiated below TLS 1.3 is rejected after the handshake.</summary>
    public bool RequireTls13 { get; init; }

    /// <summary>Server certificate to present. When null, one is generated/loaded from <see cref="CertificateDirectory"/>.</summary>
    public X509Certificate2? ServerCertificate { get; init; }

    /// <summary>Where the auto-generated server certificate lives.</summary>
    public string? CertificateDirectory { get; init; }

    /// <summary>
    /// Permit key material that could not be ACL-hardened. False by default: an unprotected private key
    /// is a hard failure rather than a warning, unless an operator explicitly accepts the risk.
    /// </summary>
    public bool AllowInsecureKeyStorage { get; init; }

    /// <summary>How a client treats the server certificate. Ignored server-side.</summary>
    public PinMode PinMode { get; init; } = PinMode.TrustOnFirstUse;

    /// <summary>Where client pins are recorded. Ignored server-side.</summary>
    public Auth.IPinStore? PinStore { get; init; }

    /// <summary>Default certificate directory: <c>%ProgramData%\VirtualThere\tls</c>.</summary>
    public static string DefaultCertificateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VirtualThere",
        "tls");

    /// <summary>Resolves the certificate directory, falling back to the default.</summary>
    public string ResolveCertificateDirectory() =>
        string.IsNullOrWhiteSpace(CertificateDirectory) ? DefaultCertificateDirectory : CertificateDirectory;

    /// <summary>Validates the policy.</summary>
    public void Validate()
    {
        if (Protocols == SslProtocols.None)
        {
            throw new ArgumentOutOfRangeException(nameof(Protocols), "at least one TLS protocol must be enabled");
        }

        if (!Enum.IsDefined(PinMode))
        {
            throw new ArgumentOutOfRangeException(nameof(PinMode));
        }
    }
}

/// <summary>Everything the factory and the transports need, in one validated record.</summary>
public sealed record TransportOptions
{
    /// <summary>Which transport to build. Defaults to plain TLS (no compression) — compression is opt-in per session.</summary>
    public TransportKind Kind { get; init; } = TransportKind.Tls;

    /// <summary>Server identity used for logs, the certificate's SAN and the client's pin key.</summary>
    public string ServerName { get; init; } = "VirtualThere";

    /// <summary>Client name sent in the client hello.</summary>
    public string ClientName { get; init; } = Environment.MachineName;

    /// <summary>How long the whole TLS + hello + authentication exchange may take.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>LZ4 policy.</summary>
    public Lz4Options Lz4 { get; init; } = new();

    /// <summary>Server-side authentication material. Ignored by a client except for <see cref="AuthenticationOptions.Pbkdf2Iterations"/>.</summary>
    public Auth.AuthenticationOptions Authentication { get; init; } = new();

    /// <summary>TLS policy.</summary>
    public TlsOptions Tls { get; init; } = new();

    /// <summary>Ask the server for isochronous support. Best-effort (ARCHITECTURE.md §10 risk 6).</summary>
    public bool RequestIsochronous { get; init; } = true;

    /// <summary>
    /// Credential a client declares in its client hello. Ignored server-side. It must be known before
    /// the hello is sent because the server answers with that method's salt.
    /// </summary>
    public AuthRequest ClientCredential { get; init; } = AuthRequest.None;

    /// <summary>
    /// Shared authentication rate limiter. A server must set one instance for the whole listener, so
    /// the token bucket and lockout span connections instead of resetting on every reconnect.
    /// </summary>
    public Auth.AuthThrottle? Throttle { get; init; }

    /// <summary>
    /// Shared verifier cache. A server must set one instance so the PBKDF2 work factor is paid once per
    /// secret rather than once per connection.
    /// </summary>
    public Auth.VerifierSet? Verifiers { get; init; }

    /// <summary>
    /// Returns a copy of this policy with <see cref="Throttle"/> and <see cref="Verifiers"/> populated,
    /// so state is shared across every connection the resulting options are used for.
    /// </summary>
    public TransportOptions WithSharedAuthState() => this with
    {
        Verifiers = Verifiers ?? new Auth.VerifierSet(Authentication),
        Throttle = Throttle ?? new Auth.AuthThrottle(Authentication.RateLimit),
    };

    /// <summary>Default listening/connecting port for a kind: 3240 plaintext, 3241 TLS (§6.3).</summary>
    public static int DefaultPort(TransportKind kind) => kind == TransportKind.PlainTcp ? 3240 : 3241;

    /// <summary>Validates every sub-policy and the cross-field rules fixed by the architecture.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (string.IsNullOrWhiteSpace(ServerName))
        {
            throw new ArgumentException("server name must be set", nameof(ServerName));
        }

        if (HandshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout));
        }

        Lz4.Validate();
        Tls.Validate();
        Authentication.Validate();

        if (Kind == TransportKind.PlainTcp && Authentication.RequiresAuthentication)
        {
            // ARCHITECTURE.md §6.4, no-auth containment: a password-protected export is never served
            // on the plaintext listener. The transport refuses to be constructed in that shape.
            throw new ArgumentException(
                "plaintext transport cannot carry authentication; a password-protected export is never served on the plaintext listener");
        }

        if (Kind == TransportKind.PlainTcp && ClientCredential.Method != AuthMethod.None)
        {
            throw new ArgumentException(
                "plaintext transport cannot carry a credential; a password-protected export is never served on the plaintext listener",
                nameof(ClientCredential));
        }

        if (Kind != TransportKind.TlsLz4 && Lz4.Enabled && Kind == TransportKind.PlainTcp)
        {
            throw new ArgumentException("LZ4 is impossible on the plaintext listener: there is no handshake to negotiate it");
        }
    }
}
