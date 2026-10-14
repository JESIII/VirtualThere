using System.Security.Cryptography;
using Vht.Config.Diagnostics;

namespace Vht.Config.Discovery;

public enum DiscoveryOriginKind
{
    Discovered = 0,
    Manual = 1,
}

/// <summary>
/// DNS-SD service type validation. The decision is frozen: <c>_vht._tcp</c>. The reference product's
/// <c>_vhusb._tcp</c> is rejected (advertising on it would make the two products see each other's
/// clients) and so is any type that is not a registered-style <c>_name._tcp</c>/<c>._udp</c> label.
/// </summary>
public static class ServiceTypes
{
    public const string Vht = "_vht._tcp";
    public const string VhtUdp = "_vht._udp";
    /// <summary>The reference product's service type (docs/RECON_AND_SPEC.md section 5). Rejected on
    /// purpose so the two products never discover each other.</summary>
    public const string ReferenceProductServiceType = "_vhusb._tcp";
    public const string Domain = "local";

    public static bool ValidateServiceType(string? serviceType, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(serviceType))
        {
            error = "the service type must not be empty";
            return false;
        }

        var value = serviceType.Trim();
        var parts = value.Split('.');

        // Accepted: _name._tcp / _name._udp with the "local" domain optional. Both spellings appear in
        // the wild: DNSAPI registration wants the full name, a config file reads better short.
        if (parts.Length is not (2 or 3)
            || parts[0].Length < 2
            || !parts[0].StartsWith('_')
            || (parts[1] != "_tcp" && parts[1] != "_udp")
            || (parts.Length == 3 && parts[2] != Domain))
        {
            error = $"'{value}' is not a DNS-SD service type of the form _name._tcp or _name._tcp.local (e.g. {Vht})";
            return false;
        }

        foreach (var ch in parts[0][1..])
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-'))
            {
                error = $"'{value}' contains '{ch}', which is not valid in a service type label";
                return false;
            }
        }

        if (string.Equals(parts[0][1..], "vhusb", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{ReferenceProductServiceType} is another product's reserved service type; VirtualThere advertises {Vht} so the two products never see each other's clients";
            return false;
        }

        return true;
    }

    public static string InstanceFullName(string instanceName, string serviceType, string domain = Domain) =>
        $"{instanceName}.{serviceType}";
}

/// <summary>
/// The TXT record published with the service. Keys are frozen in ARCHITECTURE.md 7:
/// <c>v</c> (record version), <c>name</c> (server name), <c>tls</c>, <c>spki</c> (pin, so a client can
/// confirm the certificate before opening a socket) and <c>caps</c>.
/// </summary>
public sealed record ServiceTextRecord(IReadOnlyDictionary<string, string> Entries)
{
    public const string VersionKey = "v";
    public const string NameKey = "name";
    public const string TlsKey = "tls";
    public const string SpkiKey = "spki";
    public const string CapsKey = "caps";
    public const int CurrentVersion = 1;

    public static ServiceTextRecord Empty { get; } = new(new Dictionary<string, string>());

    public string? this[string key] => Entries.TryGetValue(key, out var value) ? value : null;

    public int RecordVersion =>
        Entries.TryGetValue(VersionKey, out var raw) && int.TryParse(raw, out var version) ? version : 0;

    public string? ServerName => this[NameKey];

    public bool TlsEnabled => this[TlsKey] is "1" or "true";

    public string? Spki => this[SpkiKey];

    public IReadOnlyList<string> Capabilities =>
        this[CapsKey] is { Length: > 0 } caps
            ? caps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

    public static ServiceTextRecord Create(string serverName, bool tls, string? spki, IEnumerable<string> capabilities, int version = CurrentVersion)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VersionKey] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [NameKey] = serverName,
            [TlsKey] = tls ? "1" : "0",
        };

        if (!string.IsNullOrEmpty(spki))
        {
            entries[SpkiKey] = spki!;
        }

        var caps = capabilities.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (caps.Count > 0)
        {
            entries[CapsKey] = string.Join(',', caps);
        }

        return new ServiceTextRecord(entries);
    }

    /// <summary>The flattened "k=v" rendering, used for logging and for the non-Windows fallback.</summary>
    public string ToDescriptor() =>
        string.Join(' ', Entries.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    public static ServiceTextRecord Parse(IEnumerable<KeyValuePair<string, string>> entries) =>
        new(entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
}

/// <summary>One advertised or discovered server.</summary>
public sealed record ServiceInstance(
    string InstanceName,
    string HostName,
    int Port,
    ServiceTextRecord Txt,
    string ServiceType,
    IReadOnlyList<string> Addresses,
    DiscoveryOriginKind Origin)
{
    public string ServerName => Txt.ServerName ?? InstanceName;

    public string? Spki => Txt.Spki;

    public bool TlsEnabled => Txt.TlsEnabled;

    /// <summary>Host:port as a client would dial it. IPv6 literals get a bracket prefix.</summary>
    public string Endpoint =>
        HostName.Contains(':') && !HostName.StartsWith('[') ? $"[{HostName}]:{Port}" : $"{HostName}:{Port}";

    public string FullName => ServiceTypes.InstanceFullName(InstanceName, ServiceType);
}

public interface IServicePublisher
{
    bool IsSupported { get; }

    string? UnsupportedReason { get; }

    Task RegisterAsync(ServiceInstance instance, CancellationToken cancellationToken = default);

    Task UpdateAsync(ServiceInstance instance, CancellationToken cancellationToken = default);

    Task UnregisterAsync(string instanceName, CancellationToken cancellationToken = default);
}

public interface IServiceBrowser
{
    Task<IReadOnlyList<ServiceInstance>> BrowseAsync(string serviceType, TimeSpan duration, CancellationToken cancellationToken = default);

    Task<ServiceInstance?> ResolveAsync(string serviceType, string instanceName, CancellationToken cancellationToken = default);
}

/// <summary>A DNS-SD engine. Windows DNSAPI is the primary implementation; the in-memory one backs
/// tests and non-Windows dev hosts (ARCHITECTURE.md 7, risk 8).</summary>
public interface IServiceDiscoveryProvider : IServicePublisher, IServiceBrowser, IAsyncDisposable
{
    string Name { get; }
}

/// <summary>An endpoint a user typed, kept even when discovery is off.</summary>
public sealed record ManualServerEndpoint(string Host, int Port)
{
    public static int DefaultPort => 3240;

    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

public static class ManualServerParser
{
    public static bool TryParse(string? text, out ManualServerEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "a server address must not be empty";
            return false;
        }

        var value = text.Trim();
        string host;
        var port = ManualServerEndpoint.DefaultPort;

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
            {
                error = $"'{text}' looks like an IPv6 literal but has no closing bracket";
                return false;
            }

            host = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.StartsWith(':'))
            {
                if (!TryPort(rest[1..], out port, out error))
                {
                    return false;
                }
            }
            else if (rest.Length != 0)
            {
                error = $"unexpected text '{rest}' after '{value[..(close + 1)]}'";
                return false;
            }
        }
        else
        {
            var colon = value.LastIndexOf(':');
            // more than one colon and no brackets means a bare IPv6 literal
            if (colon >= 0 && value.IndexOf(':') != colon)
            {
                host = value;
            }
            else if (colon >= 0)
            {
                host = value[..colon];
                if (!TryPort(value[(colon + 1)..], out port, out error))
                {
                    return false;
                }
            }
            else
            {
                host = value;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            error = $"'{text}' has no host part";
            return false;
        }

        endpoint = new ManualServerEndpoint(host.Trim(), port);
        return true;
    }

    private static bool TryPort(string text, out int port, out string? error)
    {
        error = null;
        if (!int.TryParse(text.Trim(), out port) || port < 1 || port > 65535)
        {
            port = ManualServerEndpoint.DefaultPort;
            error = $"'{text}' is not a valid port (1-65535)";
            return false;
        }

        return true;
    }
}

/// <summary>
/// SPKI pinning helper (ARCHITECTURE.md 6): the TXT record carries base64url(SHA-256(SPKI)) so a client
/// can confirm the certificate it is about to trust. Computing it here means the pin is produced by the
/// same code the client checks against.
/// </summary>
public static class SpkiPin
{
    public static string Compute(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Base64Url(SHA256.HashData(subjectPublicKeyInfo));

    /// <summary>Compute the pin from a certificate (DER or PEM). Returns null with a reason when the
    /// certificate cannot be read, instead of throwing into a config load.</summary>
    public static bool TryFromCertificate(byte[] certificateBytes, out string? pin, out string? error)
    {
        pin = null;
        error = null;
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(certificateBytes);
            pin = Compute(certificate.PublicKey.ExportSubjectPublicKeyInfo());
            return true;
        }
        catch (Exception ex)
        {
            error = $"could not read the certificate to compute its SPKI pin: {ex.Message}";
            return false;
        }
    }

    public static bool LooksLikePin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != 43 && trimmed.Length != 44)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '='))
            {
                return false;
            }
        }

        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>One server a client can talk to, after merging discovery with the manual list.</summary>
public sealed record DiscoveredServer(
    string Host,
    int Port,
    string? InstanceName,
    string? ServerName,
    bool TlsEnabled,
    string? Spki,
    IReadOnlyList<string> Capabilities,
    DiscoveryOriginKind Origin,
    ServiceInstance? Instance)
{
    public string Endpoint => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public string DisplayName => ServerName ?? InstanceName ?? Endpoint;

    public bool IsPinned => !string.IsNullOrEmpty(Spki);
}

public sealed record DiscoverySnapshot(IReadOnlyList<DiscoveredServer> Servers, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public static DiscoverySnapshot Empty { get; } = new(Array.Empty<DiscoveredServer>(), Array.Empty<ConfigDiagnostic>());
}