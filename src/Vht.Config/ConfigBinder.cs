using System.Globalization;
using Vht.Config.Diagnostics;
using Vht.Config.Discovery;
using Vht.Config.Model;
using Vht.Config.Policy;
using Vht.Config.Toml;

namespace Vht.Config;

/// <summary>
/// Turns a flattened, layered value table into a validated <see cref="VhtConfig"/>. Every problem
/// becomes a diagnostic carrying the originating file and line, and binding never throws: a malformed
/// config must produce a clear error and a non-zero exit code, not a stack trace.
/// </summary>
public static class ConfigBinder
{
    public static (VhtConfig Config, IReadOnlyList<ConfigDiagnostic> Diagnostics) Bind(
        FlatTable values,
        string? defaultServerName = null,
        IReadOnlyList<ConfigDiagnostic>? priorDiagnostics = null)
    {
        var context = new BindingContext(values, priorDiagnostics);
        var general = BindGeneral(context);
        var server = BindServer(context, defaultServerName);
        var policy = BindPolicy(context);
        var transport = BindTransport(context);
        var security = BindSecurity(context);
        var discovery = BindDiscovery(context, server);
        var autoAttach = BindAutoAttach(context);
        var nicknames = BindNicknames(context);
        var passwords = BindPasswords(context, server);

        CrossValidate(context, general, transport, security, discovery, policy, passwords);

        context.ReportUnknownKeys(general.Strict);

        var config = new VhtConfig(
            general,
            server,
            policy,
            transport,
            security,
            discovery,
            autoAttach,
            nicknames,
            passwords,
            context.Origins);

        return (config, context.Diagnostics);
    }

    private static GeneralConfig BindGeneral(BindingContext context)
    {
        var version = context.Int("general.config_version", ConfigSchema.CurrentConfigVersion, 0, 10_000);
        if (version > ConfigSchema.CurrentConfigVersion)
        {
            context.Error(
                "general.config_version",
                ConfigDiagnosticCode.UnsupportedConfigVersion,
                $"this file was written by a newer VirtualThere (config_version = {version}); this build understands up to {ConfigSchema.CurrentConfigVersion}. Refusing to guess.");
        }

        var role = context.Enum("general.role", ConfigRole.Auto, MapRole);
        var logLevel = context.String("general.log_level", "info", required: false)!.Trim().ToLowerInvariant();
        if (!LogLevels.Contains(logLevel))
        {
            context.Error("general.log_level", ConfigDiagnosticCode.InvalidValue, $"'{logLevel}' is not one of {string.Join(", ", LogLevels)}");
            logLevel = "info";
        }

        return new GeneralConfig(
            version,
            role,
            logLevel,
            context.NullableString("general.log_file"),
            context.NullableString("general.data_directory"),
            context.Bool("general.strict", false));
    }

    private static ServerConfig BindServer(BindingContext context, string? defaultServerName)
    {
        var name = context.NullableString("server.name");
        if (name is not null)
        {
            name = name.Trim();
            if (name.Length == 0)
            {
                context.Error("server.name", ConfigDiagnosticCode.InvalidValue, "server.name must not be empty");
                name = null;
            }
            else if (name.Length > 64)
            {
                context.Error("server.name", ConfigDiagnosticCode.ValueOutOfRange, "server.name is longer than 64 characters");
                name = name[..64];
            }
        }

        name ??= string.IsNullOrWhiteSpace(defaultServerName) ? "VIRTUALTHERE" : defaultServerName!.Trim().ToUpperInvariant();

        return new ServerConfig(
            name,
            context.Bool("server.enabled", true),
            context.Bool("server.auto_attach_to_kernel", true),
            context.Int("server.password_max_length", 64, 8, 256));
    }

    private static DevicePolicyConfig BindPolicy(BindingContext context)
    {
        var allow = BindSelectors(context, "policy.allow");
        var ignore = BindSelectors(context, "policy.ignore");
        var policy = new DevicePolicyConfig(allow, ignore, context.Bool("policy.unignore_on_allow", false));
        foreach (var conflict in policy.ToPolicy().DescribeConflicts())
        {
            context.Warning("policy.ignore", ConfigDiagnosticCode.PolicyConflict, conflict);
        }

        if (policy.UnignoreOnAllow)
        {
            context.Warning(
                "policy.unignore_on_allow",
                ConfigDiagnosticCode.PolicyConflict,
                "policy.unignore_on_allow is set: a device listed in policy.allow will drop a matching policy.ignore entry at load time");
        }

        return policy;
    }

    private static List<DeviceSelector> BindSelectors(BindingContext context, string path)
    {
        var raw = context.StringList(path, Array.Empty<string>());
        var selectors = new List<DeviceSelector>(raw.Count);
        foreach (var entry in raw)
        {
            if (DeviceSelector.TryParse(entry, out var selector, out var error))
            {
                selectors.Add(selector);
            }
            else
            {
                context.Error(path, ConfigDiagnosticCode.InvalidValue, error!);
            }
        }

        return selectors;
    }

    private static TransportConfig BindTransport(BindingContext context) => new(
        context.Bool("transport.plaintext.enabled", true),
        context.Port("transport.plaintext.port", 3240),
        context.Bool("transport.tls.enabled", true),
        context.Port("transport.tls.port", 3241),
        context.NullableString("transport.tls.certificate"),
        context.Bool("transport.tls.lz4_enabled", true),
        context.Int("transport.tls.lz4_threshold_bytes", 1024, 0, 16 * 1024 * 1024));

    private static SecurityConfig BindSecurity(BindingContext context) => new(
        context.Bool("security.plaintext_password_protected_devices", false),
        context.NullableString("security.auth_shared_secret"));

    private static DiscoveryConfig BindDiscovery(BindingContext context, ServerConfig server)
    {
        var serviceType = (context.NullableString("discovery.service_type") ?? "_vht._tcp").Trim();
        var instance = context.NullableString("discovery.instance_name")?.Trim();
        if (string.IsNullOrEmpty(instance))
        {
            instance = server.Name;
        }

        var capabilities = context.StringList("discovery.capabilities", Array.Empty<string>());
        var manual = context.StringList("discovery.manual_servers", Array.Empty<string>());

        return new DiscoveryConfig(
            context.Bool("discovery.enabled", true),
            serviceType,
            instance,
            context.Bool("discovery.publish_spki", false),
            capabilities,
            manual,
            context.Bool("discovery.browse_on_start", true),
            context.Int("discovery.browse_seconds", 5, 1, 300));
    }

    private static AutoAttachConfig BindAutoAttach(BindingContext context) => new(
        context.Bool("auto_attach.enabled", false),
        context.Bool("auto_attach.qualify_by_server_name", true),
        context.Bool("auto_attach.qualify_by_interface", false),
        context.Int("auto_attach.delay_seconds", 3, 0, 3600),
        context.Int("auto_attach.retry_delay_seconds", 30, 1, 86_400),
        context.Int("auto_attach.max_retries", 0, 0, 1_000_000));

    private static NicknameStore BindNicknames(BindingContext context)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context.Map("nicknames"))
        {
            if (!NicknameStore.TryNormalizeKey(key, out var normalized, out var error))
            {
                context.Warning($"nicknames.{key}", ConfigDiagnosticCode.InvalidValue, $"ignored: {error}");
                continue;
            }

            var nickname = value.Value as string;
            if (string.IsNullOrWhiteSpace(nickname))
            {
                context.Warning($"nicknames.{key}", ConfigDiagnosticCode.InvalidValue, "ignored: the nickname is empty");
                continue;
            }

            entries[normalized] = nickname!;
        }

        return new NicknameStore(entries);
    }

    private static DevicePasswordStore BindPasswords(BindingContext context, ServerConfig server)
    {
        var entries = new Dictionary<string, DevicePassword>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context.Map("passwords"))
        {
            if (!NicknameStore.TryNormalizeKey(key, out var normalized, out var error))
            {
                context.Warning($"passwords.{key}", ConfigDiagnosticCode.InvalidValue, $"ignored: {error}");
                continue;
            }

            var raw = value.Value as string;
            if (raw is null)
            {
                context.Error($"passwords.{key}", ConfigDiagnosticCode.TypeMismatch, "a per-device password must be a string");
                continue;
            }

            if (!DevicePassword.TryParse(normalized, raw, out var password, out var passwordError))
            {
                context.Error($"passwords.{key}", ConfigDiagnosticCode.InvalidValue, passwordError!);
                continue;
            }

            if (password.Encoding == PasswordEncoding.Plaintext && password.Value.Length > server.PasswordMaxLength)
            {
                context.Error(
                    $"passwords.{key}",
                    ConfigDiagnosticCode.ValueOutOfRange,
                    $"the password is {password.Value.Length} characters; the limit is server.password_max_length = {server.PasswordMaxLength} (the reference product reports 'Password for device %s is too long, max %d chars')");
                continue;
            }

            entries[normalized] = password;
        }

        return new DevicePasswordStore(entries);
    }

    private static void CrossValidate(
        BindingContext context,
        GeneralConfig general,
        TransportConfig transport,
        SecurityConfig security,
        DiscoveryConfig discovery,
        DevicePolicyConfig policy,
        DevicePasswordStore passwords)
    {
        _ = general;

        if (transport.PlaintextEnabled && transport.TlsEnabled && transport.PlaintextPort == transport.TlsPort)
        {
            context.Error(
                "transport.tls.port",
                ConfigDiagnosticCode.InvalidValue,
                $"both listeners are enabled but share port {transport.PlaintextPort}; they must differ");
        }

        if (security.PlaintextPasswordProtectedDevices)
        {
            context.Error(
                "security.plaintext_password_protected_devices",
                ConfigDiagnosticCode.InvalidValue,
                "this key must remain false: a password-protected device is never served on the plaintext listener (ARCHITECTURE.md 6.3). The key exists so the rule is explicit and enforceable.");
        }

        if (passwords.Count > 0 && transport.PlaintextEnabled)
        {
            context.Warning(
                "transport.plaintext.enabled",
                ConfigDiagnosticCode.PolicyConflict,
                $"{passwords.Count} device password(s) are configured; those devices are served on the TLS listener only, so the plaintext listener will refuse them with Unauthorized rather than serve them without a password");
        }

        if (!ServiceTypes.ValidateServiceType(discovery.ServiceType, out var serviceTypeError))
        {
            context.Error("discovery.service_type", ConfigDiagnosticCode.InvalidValue, serviceTypeError!);
        }
        else if (discovery.ServiceType.StartsWith("_vhusb", StringComparison.OrdinalIgnoreCase))
        {
            context.Error(
                "discovery.service_type",
                ConfigDiagnosticCode.InvalidValue,
                "_vhusb._tcp is another product's reserved service type; VirtualThere advertises _vht._tcp so the two products never see each other's clients");
        }

        foreach (var capability in discovery.Capabilities)
        {
            if (capability.Contains(',') || string.IsNullOrWhiteSpace(capability))
            {
                context.Warning("discovery.capabilities", ConfigDiagnosticCode.InvalidValue, $"'{capability}' is not a usable capability token (no commas, not empty)");
            }
        }

        foreach (var manual in discovery.ManualServers)
        {
            if (!Discovery.ManualServerParser.TryParse(manual, out _, out var manualError))
            {
                context.Error("discovery.manual_servers", ConfigDiagnosticCode.InvalidValue, $"manual server '{manual}' is unusable: {manualError}");
            }
        }

        if (policy.AllowListOnly && policy.Ignore.Count > 0)
        {
            context.Warning(
                "policy.allow",
                ConfigDiagnosticCode.PolicyConflict,
                "policy.allow is non-empty (allow-list mode) and policy.ignore is also set; policy.ignore still wins for any device in both lists");
        }
    }

    private static readonly string[] LogLevels = { "trace", "debug", "info", "warn", "error" };

    private static (bool Ok, ConfigRole Value) MapRole(string value)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                return (true, ConfigRole.Auto);
            case "server":
            case "service":
                return (true, ConfigRole.Server);
            case "client":
            case "tray":
                return (true, ConfigRole.Client);
            default:
                return (false, ConfigRole.Auto);
        }
    }

    private sealed class BindingContext
    {
        private readonly FlatTable _values;
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConfigOrigin> _origins = new(StringComparer.Ordinal);
        private readonly List<ConfigDiagnostic> _diagnostics = new();

        public BindingContext(FlatTable values, IReadOnlyList<ConfigDiagnostic>? priorDiagnostics)
        {
            _values = values;
            if (priorDiagnostics is not null)
            {
                _diagnostics.AddRange(priorDiagnostics);
            }
        }

        public IReadOnlyList<ConfigDiagnostic> Diagnostics => _diagnostics;

        public IReadOnlyDictionary<string, ConfigOrigin> Origins => _origins;

        // ------------------------------------------------------------- typed accessors

        public string? NullableString(string path)
        {
            if (!Take(path, out var value))
            {
                return null;
            }

            if (value.Value is string text)
            {
                return text.Length == 0 ? null : text;
            }

            if (value.Value is null)
            {
                return null;
            }

            if (value.Value is IReadOnlyList<object?> list)
            {
                Error(path, ConfigDiagnosticCode.TypeMismatch, "expected a string, found a list");
                return null;
            }

            Error(path, ConfigDiagnosticCode.TypeMismatch, $"expected a string, found {Describe(value.Value)}");
            return null;
        }

        public string String(string path, string fallback, bool required = true)
        {
            if (!Take(path, out var value))
            {
                return fallback;
            }

            if (value.Value is string text)
            {
                return text;
            }

            Error(path, ConfigDiagnosticCode.TypeMismatch, $"expected a string, found {Describe(value.Value)}");
            return fallback;
        }

        public bool Bool(string path, bool fallback)
        {
            if (!Take(path, out var value))
            {
                return fallback;
            }

            if (value.Value is bool flag)
            {
                return flag;
            }

            if (value.Value is string text && bool.TryParse(text, out var parsed))
            {
                return parsed;
            }

            Error(path, ConfigDiagnosticCode.TypeMismatch, $"expected true or false, found {Describe(value.Value)}");
            return fallback;
        }

        public int Int(string path, int fallback, int min, int max)
        {
            if (!Take(path, out var value))
            {
                return fallback;
            }

            long number;
            switch (value.Value)
            {
                case long l:
                    number = l;
                    break;
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    number = parsed;
                    break;
                default:
                    Error(path, ConfigDiagnosticCode.TypeMismatch, $"expected an integer, found {Describe(value.Value)}");
                    return fallback;
            }

            if (number < min || number > max)
            {
                Error(path, ConfigDiagnosticCode.ValueOutOfRange, $"{number} is outside the accepted range {min}..{max}");
                return fallback;
            }

            return (int)number;
        }

        public int Port(string path, int fallback) => Int(path, fallback, 1, 65535);

        public IReadOnlyList<string> StringList(string path, IReadOnlyList<string> fallback)
        {
            if (!Take(path, out var value))
            {
                return fallback;
            }

            if (value.Value is IReadOnlyList<object?> list)
            {
                var result = new List<string>(list.Count);
                foreach (var item in list)
                {
                    if (item is string text)
                    {
                        result.Add(text);
                    }
                    else
                    {
                        Error(path, ConfigDiagnosticCode.TypeMismatch, $"expected a list of strings; found {Describe(item)}");
                    }
                }

                return result;
            }

            if (value.Value is string single)
            {
                // a bare string is a one-element list (handy for env overrides)
                return new[] { single };
            }

            Error(path, ConfigDiagnosticCode.TypeMismatch, $"expected a list of strings, found {Describe(value.Value)}");
            return fallback;
        }

        public IReadOnlyDictionary<string, FlatValue> Map(string prefix)
        {
            var entries = _values.Map(prefix);
            foreach (var key in entries.Keys)
            {
                _used.Add($"{prefix}.{key}");
                _origins[$"{prefix}.{key}"] = entries[key].Origin;
            }

            return entries;
        }

        public T Enum<T>(string path, T fallback, Func<string, (bool Ok, T Value)> parse)
            where T : struct
        {
            if (!Take(path, out var value))
            {
                return fallback;
            }

            if (value.Value is not string text)
            {
                Error(path, ConfigDiagnosticCode.TypeMismatch, $"expected a string, found {Describe(value.Value)}");
                return fallback;
            }

            var (ok, parsed) = parse(text);
            if (!ok)
            {
                Error(path, ConfigDiagnosticCode.InvalidValue, $"'{text}' is not a valid value");
                return fallback;
            }

            return parsed;
        }

        // ------------------------------------------------------------- reporting

        public void Error(string path, ConfigDiagnosticCode code, string message) =>
            _diagnostics.Add(Build(ConfigDiagnosticSeverity.Error, code, message, path));

        public void Warning(string path, ConfigDiagnosticCode code, string message) =>
            _diagnostics.Add(Build(ConfigDiagnosticSeverity.Warning, code, message, path));

        private ConfigDiagnostic Build(ConfigDiagnosticSeverity severity, ConfigDiagnosticCode code, string message, string path)
        {
            var origin = _origins.TryGetValue(path, out var o) ? o : ConfigOrigin.Defaults;
            return new ConfigDiagnostic(severity, code, message, path, origin.Location, origin.File);
        }

        /// <summary>Marks a path as consumed and records its origin. Returns false when absent.</summary>
        private bool Take(string path, out FlatValue value)
        {
            _used.Add(path);
            if (_values.TryGet(path, out var found))
            {
                value = found;
                _origins[path] = found.Origin;
                return true;
            }

            value = null!;
            return false;
        }

        /// <summary>
        /// Anything left over is either a machine-managed key (identity map, auto-attach grants - those
        /// live in state.toml and are read by their own components) or a typo. Everything else is an
        /// unknown-key warning, or an error under general.strict.
        /// </summary>
        public void ReportUnknownKeys(bool strict)
        {
            foreach (var (path, value) in _values.Values)
            {
                if (_used.Contains(path))
                {
                    continue;
                }

                if (IsMachineManaged(path))
                {
                    _origins[path] = value.Origin;
                    continue;
                }

                var diagnostic = new ConfigDiagnostic(
                    strict ? ConfigDiagnosticSeverity.Error : ConfigDiagnosticSeverity.Warning,
                    ConfigDiagnosticCode.UnknownKey,
                    $"unknown configuration key '{path}'; it is ignored (set general.strict = true to make this an error)",
                    path,
                    value.Origin.Location,
                    value.Origin.File);
                _diagnostics.Add(diagnostic);
            }
        }

        private static bool IsMachineManaged(string path) =>
            path.StartsWith("identity.", StringComparison.Ordinal)
            || path.StartsWith("auto_attach.grants", StringComparison.Ordinal)
            || path.StartsWith("state.", StringComparison.Ordinal);

        private static string Describe(object? value) => value switch
        {
            null => "nothing",
            bool b => b ? "true" : "false",
            long l => $"the integer {l}",
            double d => $"the number {d}",
            string s => $"the string '{s}'",
            IReadOnlyList<object?> list => $"a list of {list.Count} item(s)",
            _ => $"a {value.GetType().Name}",
        };
    }
}