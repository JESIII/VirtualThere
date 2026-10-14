using Vht.Config.AutoAttach;
using Vht.Config.Policy;
using Vht.Config.Toml;

namespace Vht.Config.Model;

public sealed record GeneralConfig(
    int ConfigVersion,
    ConfigRole Role,
    string LogLevel,
    string? LogFile,
    string? DataDirectory,
    bool Strict);

public sealed record ServerConfig(
    string Name,
    bool Enabled,
    bool AutoAttachToKernel,
    int PasswordMaxLength);

public sealed record DevicePolicyConfig(
    IReadOnlyList<DeviceSelector> Allow,
    IReadOnlyList<DeviceSelector> Ignore,
    bool UnignoreOnAllow)
{
    /// <summary>True when <c>policy.allow</c> is non-empty: only matching devices are exported.
    /// Stays in sync with <see cref="DevicePolicy.AllowListOnly"/>.</summary>
    public bool AllowListOnly => Allow.Count > 0;

    public DevicePolicy ToPolicy() => new(Allow, Ignore);
}

public sealed record TransportConfig(
    bool PlaintextEnabled,
    int PlaintextPort,
    bool TlsEnabled,
    int TlsPort,
    string? CertificatePath,
    bool Lz4Enabled,
    int Lz4ThresholdBytes);

public sealed record SecurityConfig(
    bool PlaintextPasswordProtectedDevices,
    string? AuthSharedSecret);

public sealed record DiscoveryConfig(
    bool Enabled,
    string ServiceType,
    string InstanceName,
    bool PublishSpki,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> ManualServers,
    bool BrowseOnStart,
    int BrowseSeconds);

public sealed record AutoAttachConfig(
    bool Enabled,
    bool QualifyByServerName,
    bool QualifyByInterface,
    int DelaySeconds,
    int RetryDelaySeconds,
    int MaxRetries)
{
    public AutoAttachPolicy ToPolicy() => new(Enabled, QualifyByServerName, QualifyByInterface, DelaySeconds, RetryDelaySeconds, MaxRetries);
}

/// <summary>
/// The effective configuration: the result of layering defaults, config.toml, state.toml, environment
/// and command-line overrides, then validating. Immutable - a reload produces a new instance, and the
/// old one can be kept for rollback.
/// </summary>
public sealed record VhtConfig(
    GeneralConfig General,
    ServerConfig Server,
    DevicePolicyConfig Policy,
    TransportConfig Transport,
    SecurityConfig Security,
    DiscoveryConfig Discovery,
    AutoAttachConfig AutoAttach,
    NicknameStore Nicknames,
    DevicePasswordStore Passwords,
    IReadOnlyDictionary<string, ConfigOrigin> Origins)
{
    /// <summary>Where a value came from, for <c>vht config show --origin</c>. Unknown paths report
    /// <see cref="ConfigSourceKind.Defaults"/>.</summary>
    public ConfigOrigin OriginOf(string path) =>
        Origins.TryGetValue(path, out var origin) ? origin : ConfigOrigin.Defaults;

    /// <summary>The plaintext listener may not carry devices that require a password, so a
    /// password-protected configuration with a plaintext listener is reported at load time.</summary>
    public bool HasPasswordProtectedDevices => Passwords.Count > 0;
}

/// <summary>Everything a load produced: the bound configuration, the flat values it came from (needed
/// for change classification) and every diagnostic.</summary>
public sealed record ConfigLoadResult(
    VhtConfig Config,
    FlatTable Values,
    IReadOnlyList<Vht.Config.Diagnostics.ConfigDiagnostic> Diagnostics,
    ConfigPaths Paths)
{
    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    public IEnumerable<Vht.Config.Diagnostics.ConfigDiagnostic> Errors => Diagnostics.Where(d => d.IsError);

    public string DescribeDiagnostics() =>
        Diagnostics.Count == 0 ? "(no diagnostics)" : string.Join(Environment.NewLine, Diagnostics.Select(d => d.Describe()));
}