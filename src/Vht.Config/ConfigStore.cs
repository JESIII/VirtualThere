using System.Globalization;
using Vht.Config.AutoAttach;
using Vht.Config.DeviceIdentity;
using Vht.Config.Diagnostics;
using Vht.Config.Migration;
using Vht.Config.Model;
using Vht.Config.Reload;
using Vht.Config.Toml;

namespace Vht.Config;

/// <summary>
/// Owns the two files and the layering between them:
/// <code>
///   config.toml   operator-authored. Parsed, never written by VirtualThere.
///   state.toml    machine-managed. Overlays config.toml (nicknames set at runtime, passwords,
///                 manual servers, auto-attach grants, the device identity map).
///   VHT_* env     overlays both (VHT_SERVER__NAME=..., VHT_DATA_DIR=...).
///   --set k=v     highest precedence (command line).
/// </code>
/// Effective value order: defaults &lt; config.toml &lt; state.toml &lt; environment &lt; command line, and
/// every value keeps the origin that produced it so <c>vht config show --origin</c> can explain itself.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private readonly Dictionary<string, object?> _stateValues = new(StringComparer.Ordinal);
    private readonly List<ConfigDiagnostic> _pendingDiagnostics = new();
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly IReadOnlyDictionary<string, string?>? _commandLineOverrides;
    private ConfigWatcher? _watcher;

    public ConfigStore(
        ConfigPaths paths,
        ConfigRole role = ConfigRole.Auto,
        string? hostName = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        IReadOnlyDictionary<string, string?>? commandLineOverrides = null)
    {
        Paths = paths;
        Role = role;
        HostName = string.IsNullOrWhiteSpace(hostName) ? SafeHostName() : hostName!;
        _environment = environment ?? EnvironmentSnapshot();
        _commandLineOverrides = commandLineOverrides;
    }

    public ConfigPaths Paths { get; }

    public ConfigRole Role { get; }

    public string HostName { get; }

    public ConfigLoadResult? Current { get; private set; }

    public DeviceIdMap IdentityMap { get; private set; } = new();

    public IReadOnlyList<AutoAttachGrant> AutoAttachGrants { get; private set; } = Array.Empty<AutoAttachGrant>();

    /// <summary>Start watching both files. The returned watcher raises a debounced signal; call
    /// <see cref="Reload"/> and <see cref="ConfigReloadClassifier"/> to turn that into a plan.</summary>
    public ConfigWatcher StartWatching(int debounceMilliseconds = 250)
    {
        _watcher?.Dispose();
        _watcher = new ConfigWatcher(Paths.Directory, new[] { Path.GetFileName(Paths.ConfigFile), Path.GetFileName(Paths.StateFile) }, debounceMilliseconds);
        return _watcher;
    }

    // ------------------------------------------------------------------ loading

    public ConfigLoadResult Load()
    {
        var diagnostics = new List<ConfigDiagnostic>(_pendingDiagnostics);

        var fileLayer = TomlLayerReader.ReadFile(Paths.ConfigFile, ConfigSourceKind.File);
        diagnostics.AddRange(fileLayer.Diagnostics);

        var stateLayer = TomlLayerReader.ReadFile(Paths.StateFile, ConfigSourceKind.Overlay);
        diagnostics.AddRange(stateLayer.Diagnostics);

        var environmentLayer = ReadEnvironment(_environment, diagnostics);
        var commandLineLayer = ReadOverrides(_commandLineOverrides, diagnostics);

        var merged = Merge(fileLayer.Table, stateLayer.Table, environmentLayer, commandLineLayer);

        // remember the machine-managed parts of the state file so a save does not lose them
        _stateValues.Clear();
        foreach (var (path, value) in stateLayer.Table.Values)
        {
            _stateValues[path] = value.Value;
        }

        IdentityMap = DeviceIdMap.Load(stateLayer.Table, diagnostics);
        AutoAttachGrants = AutoAttachPolicy.Load(stateLayer.Table);

        diagnostics.AddRange(ConfigMigration.Apply(merged));

        var (config, bindingDiagnostics) = ConfigBinder.Bind(merged, HostName, diagnostics);
        diagnostics = bindingDiagnostics.ToList();

        if (config.Policy.UnignoreOnAllow && config.Policy.Ignore.Count > 0)
        {
            var allow = config.Policy.Allow.Select(a => a.Raw).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var filtered = config.Policy.Ignore.Where(i => !allow.Contains(i.Raw)).ToList();
            if (filtered.Count != config.Policy.Ignore.Count)
            {
                config = config with { Policy = config.Policy with { Ignore = filtered } };
            }
        }

        Current = new ConfigLoadResult(config, merged, diagnostics, Paths);
        return Current;
    }

    /// <summary>
    /// Reload and report what changed relative to the previous load. Callers apply the hot changes and
    /// surface <see cref="ConfigReloadPlan.RequiresRestart"/> instead of pretending the port moved.
    /// </summary>
    public (ConfigLoadResult Result, ConfigReloadPlan Plan) Reload()
    {
        var previous = Current;
        var result = Load();
        var plan = previous is null ? ConfigReloadPlan.Empty : ConfigReloadClassifier.Classify(previous.Values, result.Values);
        return (result, plan);
    }

    // ------------------------------------------------------------------ state mutations

    /// <summary>Rename a device. Persisted to state.toml, so it survives a restart and is visible to
    /// clients through the control plane.</summary>
    public void SetNickname(string selectorOrKey, string nickname)
    {
        ApplyToState(config => config with { Nicknames = config.Nicknames.With(selectorOrKey, nickname) });
        _stateValues[$"nicknames.{NicknameStore.NormalizeKey(selectorOrKey)}"] = nickname;
    }

    public void RemoveNickname(string selectorOrKey)
    {
        var key = NicknameStore.NormalizeKey(selectorOrKey);
        ApplyToState(config => config with { Nicknames = config.Nicknames.Without(key) });
        RemoveStatePrefix($"nicknames.{key}");
    }

    public void SetDevicePassword(DevicePassword password)
    {
        var key = NicknameStore.NormalizeKey(password.Selector);
        ApplyToState(config => config with { Passwords = config.Passwords.With(password with { Selector = key }) });
        _stateValues[$"passwords.{key}"] = password.Serialize();
    }

    public void RemoveDevicePassword(string selectorOrKey)
    {
        var key = NicknameStore.NormalizeKey(selectorOrKey);
        ApplyToState(config => config with { Passwords = config.Passwords.Without(key) });
        RemoveStatePrefix($"passwords.{key}");
    }

    public void AddManualServer(string hostPort)
    {
        if (!Discovery.ManualServerParser.TryParse(hostPort, out var endpoint, out var error))
        {
            throw new ArgumentException(error, nameof(hostPort));
        }

        var list = CurrentServerList();
        var normalized = endpoint!.ToString();
        if (!list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(normalized);
            list.Sort(StringComparer.OrdinalIgnoreCase);
        }

        _stateValues["discovery.manual_servers"] = list.Cast<object?>().ToList();
        ApplyToState(config => config with { Discovery = config.Discovery with { ManualServers = list } });
    }

    public bool RemoveManualServer(string hostPort)
    {
        var list = CurrentServerList();
        var removed = list.RemoveAll(item => string.Equals(item, hostPort.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            _stateValues["discovery.manual_servers"] = list.Cast<object?>().ToList();
            ApplyToState(config => config with { Discovery = config.Discovery with { ManualServers = list } });
        }

        return removed;
    }

    /// <summary>
    /// Record an auto-attach grant. The authorization result is a required argument: a grant is only
    /// ever written after the server confirmed the client may use the device.
    /// </summary>
    public AutoAttachGrantResult RecordAutoAttachGrant(AutoAttachGrantRequest request, bool authorizationSucceeded, DateTimeOffset? now = null)
    {
        var result = AutoAttachPolicy.RecordGrant(request, authorizationSucceeded, now ?? DateTimeOffset.UtcNow);
        if (!result.Recorded || result.Grant is null)
        {
            return result;
        }

        var grants = AutoAttachGrants
            .Where(g => !(string.Equals(g.DeviceKey, result.Grant.DeviceKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(g.ServerName, result.Grant.ServerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(g.ServerInterface, result.Grant.ServerInterface, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        grants.Add(result.Grant);
        AutoAttachGrants = grants;
        return result;
    }

    public void RemoveAutoAttachGrant(string deviceKey, string? serverName = null)
    {
        AutoAttachGrants = AutoAttachGrants
            .Where(g => !(string.Equals(g.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase)
                && (serverName is null || string.Equals(g.ServerName, serverName, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>Render the state file that <see cref="Save"/> would write (dry run / tests).</summary>
    public string RenderState() => TomlWriter.Write(StateValuesForWrite(), StateHeader);

    /// <summary>Write state.toml atomically (temp file + replace). Returns the diagnostics, never throws
    /// for a content problem.</summary>
    public IReadOnlyList<ConfigDiagnostic> Save()
    {
        var diagnostics = new List<ConfigDiagnostic>();
        try
        {
            Directory.CreateDirectory(Paths.Directory);
            var temporary = Path.Combine(Paths.Directory, Path.GetFileName(Paths.StateFile) + ".tmp");
            File.WriteAllText(temporary, RenderState());
            File.Move(temporary, Paths.StateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new ConfigDiagnostic(
                ConfigDiagnosticSeverity.Error,
                ConfigDiagnosticCode.WriteError,
                $"could not write the state file: {ex.Message}",
                File: Paths.StateFile));
        }

        _pendingDiagnostics.Clear();
        _pendingDiagnostics.AddRange(diagnostics);
        return diagnostics;
    }

    private IEnumerable<KeyValuePair<string, object?>> StateValuesForWrite()
    {
        var skip = new[] { "identity.devices#", "auto_attach.grants#" };
        foreach (var (path, value) in _stateValues.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (skip.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            yield return new KeyValuePair<string, object?>(path, value);
        }

        foreach (var pair in IdentityMap.ToFlatValues())
        {
            yield return pair;
        }

        foreach (var pair in AutoAttachPolicy.ToFlatValues(AutoAttachGrants))
        {
            yield return pair;
        }
    }

    private static readonly string StateHeader =
        "VirtualThere state file - MACHINE MANAGED.\n" +
        "Written by vht-server / vht-client. Runtime changes (nicknames, passwords, manual servers,\n" +
        "auto-attach grants, the device identity map) live here and overlay config.toml.\n" +
        "Hand edits are overwritten on the next save: put your settings in config.toml instead.\n" +
        "Layering: defaults < config.toml < state.toml < VHT_* environment < command line. See docs/CONFIG.md.";

    private void ApplyToState(Func<VhtConfig, VhtConfig> mutate)
    {
        if (Current is null)
        {
            Load();
        }

        Current = Current! with { Config = mutate(Current!.Config) };
    }

    private void RemoveStatePrefix(string prefix)
    {
        foreach (var key in _stateValues.Keys.Where(k => string.Equals(k, prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _stateValues.Remove(key);
        }
    }

    private List<string> CurrentServerList() =>
        (Current?.Config.Discovery.ManualServers ?? Array.Empty<string>()).ToList();

    // ------------------------------------------------------------------ layers

    public static FlatTable Merge(params FlatTable[] layers)
    {
        var merged = new FlatTable();
        foreach (var layer in layers)
        {
            foreach (var (path, value) in layer.Values)
            {
                merged.Set(path, value.Value, value.Origin);
            }
        }

        return merged;
    }

    /// <summary>VHT_* environment overrides. <c>VHT_SERVER__NAME</c> -&gt; <c>server.name</c>;
    /// <c>VHT_DATA_DIR</c> is accepted as an alias for <c>general.data_directory</c>. Values are coerced
    /// to the schema type, so a typo is a diagnostic instead of a silent string.</summary>
    public static FlatTable ReadEnvironment(IReadOnlyDictionary<string, string?> environment, List<ConfigDiagnostic> diagnostics)
    {
        var table = new FlatTable();
        foreach (var (name, raw) in environment)
        {
            if (raw is null || !name.StartsWith("VHT_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Equals(ConfigPathResolver.EnvironmentVariable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = name.Equals(ConfigPathResolver.DataDirectoryVariable, StringComparison.OrdinalIgnoreCase)
                ? "general.data_directory"
                : name[4..].ToLowerInvariant().Replace("__", ".");

            if (!ConfigSchema.IsKnown(path))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    ConfigDiagnosticSeverity.Warning,
                    ConfigDiagnosticCode.UnknownKey,
                    $"environment variable {name} does not map to a known configuration key ({path}); it is ignored"));
                continue;
            }

            var origin = new ConfigOrigin(ConfigSourceKind.Environment, null, ConfigLocation.None);
            table.Set(path, Coerce(raw, path, diagnostics, origin, name), origin);
        }

        return table;
    }

    private static FlatTable ReadOverrides(IReadOnlyDictionary<string, string?>? overrides, List<ConfigDiagnostic> diagnostics)
    {
        var table = new FlatTable();
        if (overrides is null)
        {
            return table;
        }

        foreach (var (path, raw) in overrides)
        {
            var origin = new ConfigOrigin(ConfigSourceKind.CommandLine, null, ConfigLocation.None);
            if (!ConfigSchema.IsKnown(path))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    ConfigDiagnosticSeverity.Error,
                    ConfigDiagnosticCode.UnknownKey,
                    $"'{path}' is not a known configuration key",
                    path));
                continue;
            }

            if (raw is null)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    ConfigDiagnosticSeverity.Error,
                    ConfigDiagnosticCode.TypeMismatch,
                    $"'{path}' was set without a value",
                    path,
                    origin.Location,
                    origin.File));
                continue;
            }

            table.Set(path, Coerce(raw, path, diagnostics, origin, path), origin);
        }

        return table;
    }

    private static object? Coerce(string raw, string path, List<ConfigDiagnostic> diagnostics, ConfigOrigin origin, string source)
    {
        if (!ConfigSchema.TryGet(path, out var descriptor))
        {
            return raw;
        }

        switch (descriptor.Type)
        {
            case ConfigKeyType.Boolean:
                if (bool.TryParse(raw, out var flag))
                {
                    return flag;
                }

                diagnostics.Add(new ConfigDiagnostic(
                    ConfigDiagnosticSeverity.Error,
                    ConfigDiagnosticCode.TypeMismatch,
                    $"{source} must be true or false, found '{raw}'",
                    path,
                    origin.Location,
                    origin.File));
                return null;
            case ConfigKeyType.Integer:
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    return number;
                }

                diagnostics.Add(new ConfigDiagnostic(
                    ConfigDiagnosticSeverity.Error,
                    ConfigDiagnosticCode.TypeMismatch,
                    $"{source} must be an integer, found '{raw}'",
                    path,
                    origin.Location,
                    origin.File));
                return null;
            case ConfigKeyType.StringList:
                return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(item => (object?)item)
                    .ToList();
            default:
                return raw;
        }
    }

    public static IReadOnlyDictionary<string, string?> EnvironmentSnapshot()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                result[key] = entry.Value as string;
            }
        }

        return result;
    }

    private static string SafeHostName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (Exception)
        {
            return "VIRTUALTHERE";
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}