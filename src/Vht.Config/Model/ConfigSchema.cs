using Vht.Config.Diagnostics;

namespace Vht.Config.Model;

/// <summary>Role a process plays; decides the default config directory. <see cref="Auto"/> resolves
/// to <see cref="Server"/> unless a host application says otherwise.</summary>
public enum ConfigRole
{
    Auto = 0,
    Server = 1,
    Client = 2,
}

/// <summary>Value kinds used by the schema table (documentation + validation + unknown-key checks).</summary>
public enum ConfigKeyType
{
    String,
    Integer,
    Boolean,
    StringList,
    StringMap,
    TableArray,
}

/// <summary>
/// One documented key in the configuration schema. The schema is the single source of truth for:
///   * which keys exist (anything else is an unknown-key warning, or an error under <c>general.strict</c>),
///   * the type and default shown in <c>docs/CONFIG.md</c> (a test asserts the doc lists every key),
///   * whether changing the key can be applied without restarting the service (reload classification).
/// </summary>
public sealed record ConfigKeyDescriptor(
    string Path,
    ConfigKeyType Type,
    string Default,
    bool RequiresRestart,
    string Summary);

/// <summary>
/// Frozen schema. Wildcards: <c>*</c> matches exactly one path segment; <c>#*</c> matches an array
/// index segment (flattened table arrays are addressed as <c>identity.devices#0.key</c>).
/// </summary>
public static class ConfigSchema
{
    public const int CurrentConfigVersion = 1;

    public static readonly IReadOnlyList<ConfigKeyDescriptor> All = new ConfigKeyDescriptor[]
    {
        // ---- general -------------------------------------------------------------------------
        new("general.config_version", ConfigKeyType.Integer, "1", true,
            "Schema version of this file. Migrated forward automatically; a file written by a newer VirtualThere is a hard error."),
        new("general.role", ConfigKeyType.String, "auto", true,
            "\"server\", \"client\", or \"auto\". Affects the default config directory and nothing else."),
        new("general.log_level", ConfigKeyType.String, "info", false,
            "trace | debug | info | warn | error."),
        new("general.log_file", ConfigKeyType.String, "(empty)", true,
            "Log file path. Empty means platform default (Windows Event Log for the service, stderr for a console run)."),
        new("general.data_directory", ConfigKeyType.String, "(empty)", true,
            "Directory for logs and caches. state.toml always stays next to config.toml."),
        new("general.strict", ConfigKeyType.Boolean, "false", false,
            "When true, an unknown key is an error instead of a warning."),

        // ---- server -------------------------------------------------------------------------
        new("server.name", ConfigKeyType.String, "(machine host name, upper-cased)", false,
            "The advertised server name (reference-product vocabulary: ServerName). Shown in the client's server list."),
        new("server.enabled", ConfigKeyType.Boolean, "true", true,
            "Whether this process exports devices at all."),
        new("server.auto_attach_to_kernel", ConfigKeyType.Boolean, "true", false,
            "The reference product's AutoAttachToKernel: hand a device back to its kernel driver when no client is using it."),
        new("server.password_max_length", ConfigKeyType.Integer, "64", true,
            "Maximum accepted per-device password length. The reference product's limit is a runtime format argument; 64 is ours."),

        // ---- policy -------------------------------------------------------------------------
        new("policy.allow", ConfigKeyType.StringList, "[] (empty = allow everything)", false,
            "Allow list. When non-empty, ONLY matching devices are exported. Selectors: \"vid:pid\", \"vid:pid:serial\", \"serial:<s>\", \"key:<device-key>\", \"*\"."),
        new("policy.ignore", ConfigKeyType.StringList, "[]", false,
            "Ignore list. Always wins over policy.allow. An ignored device never appears in the export list."),
        new("policy.unignore_on_allow", ConfigKeyType.Boolean, "false", false,
            "When true, listing a device in policy.allow removes a matching policy.ignore entry (a warning is emitted either way)."),

        // ---- transport (behaviour owned by the transport card; schema frozen here) ----------
        new("transport.plaintext.enabled", ConfigKeyType.Boolean, "true", true,
            "Listen on the stock USB/IP port. Stock usbip peers can connect; password-protected devices are never served here."),
        new("transport.plaintext.port", ConfigKeyType.Integer, "3240", true,
            "The reference product's TCPPort. 3240 is the USB/IP default."),
        new("transport.tls.enabled", ConfigKeyType.Boolean, "true", true,
            "Listen on the TLS port."),
        new("transport.tls.port", ConfigKeyType.Integer, "3241", true,
            "The reference product's SSLPort; 3240+1 so both listeners can coexist."),
        new("transport.tls.certificate", ConfigKeyType.String, "(empty)", true,
            "PEM or PFX path for the server certificate. Empty means generate/use the per-machine self-signed certificate."),
        new("transport.tls.lz4_enabled", ConfigKeyType.Boolean, "true", false,
            "The reference product's CompressionLimit switch: negotiate LZ4 on the TLS listener."),
        new("transport.tls.lz4_threshold_bytes", ConfigKeyType.Integer, "65536", false,
            "The reference product's CompressionLimit: payloads smaller than this are never compressed. 64 KiB is the smallest payload size at which LZ4 was measured to beat plain TLS on compressible data on this host (tests/Vht.Transport.Bench sweep mode; artifacts/transport/sweep.txt)."),

        // ---- security -----------------------------------------------------------------------
        new("security.plaintext_password_protected_devices", ConfigKeyType.Boolean, "false", true,
            "MUST remain false. A password-protected device is never served on the plaintext listener; this key exists so the rule is explicit and rejectable."),
        new("security.auth_shared_secret", ConfigKeyType.String, "(empty)", false,
            "Optional server-wide shared secret (client side mirrors it). Empty disables it."),

        // ---- discovery ----------------------------------------------------------------------
        new("discovery.enabled", ConfigKeyType.Boolean, "true", false,
            "Advertise/browse via DNS-SD. Disabling it never disables manual host:port entries."),
        new("discovery.service_type", ConfigKeyType.String, "_vht._tcp", true,
            "DNS-SD service type. Deliberately not _vhusb._tcp (the reference product's type)."),
        new("discovery.instance_name", ConfigKeyType.String, "(server.name)", false,
            "DNS-SD instance name. Re-registered on change, so this is hot-reloadable."),
        new("discovery.publish_spki", ConfigKeyType.Boolean, "false", false,
            "Publish the certificate SPKI pin in the TXT record so a client can confirm the pin before opening a socket. Default false: off until the transport card can produce the pin."),
        new("discovery.capabilities", ConfigKeyType.StringList, "[]", false,
            "Advertised capability tokens (TXT caps=...). Free-form; e.g. \"lz4\", \"tls1.3\"."),
        new("discovery.manual_servers", ConfigKeyType.StringList, "[]", false,
            "Client side: persistent host:port entries, e.g. [\"192.168.1.20:3240\", \"[fe80::1]:3241\"]. Always available even when discovery is off."),
        new("discovery.browse_on_start", ConfigKeyType.Boolean, "true", false,
            "Client side: start browsing automatically at startup."),
        new("discovery.browse_seconds", ConfigKeyType.Integer, "5", false,
            "Client side: how long a single browse pass runs before results are reported as a snapshot."),

        // ---- auto attach (the \"Auto-Use\" analogue) -------------------------------------------
        new("auto_attach.enabled", ConfigKeyType.Boolean, "false", false,
            "Master switch. Even when true, a device is only attached if a grant was recorded AND authorization succeeded first."),
        new("auto_attach.qualify_by_server_name", ConfigKeyType.Boolean, "true", false,
            "The reference product's Qualify Auto-Use by Server Name: a grant is only honoured for the server it was granted for."),
        new("auto_attach.qualify_by_interface", ConfigKeyType.Boolean, "false", false,
            "The reference product's Qualify Auto-Use By Server Network Interface."),
        new("auto_attach.delay_seconds", ConfigKeyType.Integer, "3", false,
            "The reference product's Auto-Use Delay: wait this long after a device appears before attaching."),
        new("auto_attach.retry_delay_seconds", ConfigKeyType.Integer, "30", false,
            "The reference product's Auto-Use Retry Delay."),
        new("auto_attach.max_retries", ConfigKeyType.Integer, "0", false,
            "0 means unlimited retries."),
        new("auto_attach.grants", ConfigKeyType.TableArray, "[]", false,
            "Machine-managed (state.toml only). One entry per recorded grant: server, optional interface, device_key, granted_at_utc. Written only after authorization succeeded."),

        // ---- nicknames / passwords ----------------------------------------------------------
        new("nicknames", ConfigKeyType.StringMap, "(empty)", false,
            "Friendly name per device selector or device key, e.g. \"1050:0407\" = \"YubiKey\" or \"s-10500407-0123456789ab\" = \"Work token\"."),
        new("passwords", ConfigKeyType.StringMap, "(empty)", false,
            "Per-device password per selector: a plaintext value, or \"sha256:<64 hex chars>\", or \"pbkdf2:<iterations>:<salt-b64>:<hash-b64>\"."),

        // ---- identity -----------------------------------------------------------------------
        new("identity.devices", ConfigKeyType.TableArray, "[]", false,
            "Machine-managed (state.toml only). Stable device identity map; see docs/CONFIG.md \"Device identity\"."),
    };

    private static readonly Dictionary<string, ConfigKeyDescriptor> ByPath =
        All.ToDictionary(k => k.Path, StringComparer.Ordinal);

    public static bool TryGet(string path, out ConfigKeyDescriptor descriptor)
    {
        if (ByPath.TryGetValue(path, out var exact))
        {
            descriptor = exact;
            return true;
        }

        foreach (var candidate in All)
        {
            if (MatchesValue(candidate, path))
            {
                descriptor = candidate;
                return true;
            }
        }

        descriptor = null!;
        return false;
    }

    /// <summary>
    /// A scalar key matches exactly. A map or a table array also owns the extra segments the flattener
    /// produces for it: <c>nicknames.&lt;selector&gt;</c>, <c>passwords.&lt;selector&gt;</c>,
    /// <c>identity.devices#0.key</c>, <c>auto_attach.grants#3.device_key</c>. This is what keeps a
    /// machine-managed key from being reported as a typo.
    /// </summary>
    private static bool MatchesValue(ConfigKeyDescriptor candidate, string path)
    {
        if (Matches(candidate.Path, path))
        {
            return true;
        }

        var pattern = candidate.Path.Split('.');
        var segments = path.Split('.');
        if (segments.Length <= pattern.Length)
        {
            return false;
        }

        switch (candidate.Type)
        {
            case ConfigKeyType.StringMap:
                if (segments.Length != pattern.Length + 1)
                {
                    return false;
                }

                break;

            case ConfigKeyType.TableArray:
                // the index is glued to the last pattern segment: identity.devices -> identity.devices#0
                if (!segments[pattern.Length - 1].StartsWith(pattern[^1] + "#", StringComparison.Ordinal))
                {
                    return false;
                }

                break;

            default:
                return false;
        }

        for (var i = 0; i < pattern.Length - 1; i++)
        {
            if (pattern[i] != segments[i] && pattern[i] != "*")
            {
                return false;
            }
        }

        if (candidate.Type == ConfigKeyType.StringMap
            && pattern[^1] != segments[pattern.Length - 1]
            && pattern[^1] != "*")
        {
            return false;
        }

        return true;
    }

    public static bool IsKnown(string path) => TryGet(path, out _);

    /// <summary>True when a change to this path needs a service restart to take effect.</summary>
    public static bool RequiresRestart(string path) => TryGet(path, out var d) && d.RequiresRestart;

    /// <summary>Pattern match where <c>*</c> matches one segment and <c>#*</c> matches one index segment.</summary>
    public static bool Matches(string pattern, string path)
    {
        var p = pattern.Split('.');
        var s = path.Split('.');
        if (p.Length != s.Length)
        {
            return false;
        }

        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == s[i] || p[i] == "*")
            {
                continue;
            }

            if (p[i] == "#*" && s[i].StartsWith('#'))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}