using Vht.Config.Diagnostics;
using Vht.Config.Model;
using Vht.Config.Toml;

namespace Vht.Config.Migration;

/// <summary>
/// Config-version migration. The rule the card asks for is "schema + migration story documented;
/// a malformed config produces a clear error, not a crash", so:
/// <list type="bullet">
/// <item>version &lt; 1: legacy single-level keys (the reference product's vocabulary: <c>ServerName</c>,
/// <c>TCPPort</c>, <c>AllowedDevices</c>...) are renamed to their version-1 paths and the values are
/// coerced to the documented type;</item>
/// <item>version == 1: nothing to do;</item>
/// <item>version &gt; 1: hard error - a newer build wrote this file and guessing would lose data.</item>
/// </list>
/// Migration is applied to the merged table before binding, so a migrated value is validated exactly
/// like a native one.
/// </summary>
public static class ConfigMigration
{
    public const int LegacyVersion = 0;

    /// <summary>Legacy key (lower-cased, punctuation removed) -&gt; version-1 path.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyKeyMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["servername"] = "server.name",
        ["alloweddevices"] = "policy.allow",
        ["ignoreddevices"] = "policy.ignore",
        ["tcpport"] = "transport.plaintext.port",
        ["sslport"] = "transport.tls.port",
        ["compressionlimit"] = "transport.tls.lz4_threshold_bytes",
        ["autoattachtokernel"] = "server.auto_attach_to_kernel",
        ["mdnsservicename"] = "discovery.instance_name",
        ["mdnsenabled"] = "discovery.enabled",
        ["autofind"] = "discovery.enabled",
        ["loglevel"] = "general.log_level",
        ["logfile"] = "general.log_file",
        ["password"] = "security.auth_shared_secret",
    };

    /// <summary>Applies migrations in place. Returns the diagnostics produced (never throws).</summary>
    public static IReadOnlyList<ConfigDiagnostic> Apply(FlatTable table)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var version = ReadVersion(table);

        if (version == ConfigSchema.CurrentConfigVersion)
        {
            return diagnostics;
        }

        if (version > ConfigSchema.CurrentConfigVersion)
        {
            // the binder reports the error with better wording; the migration stays silent.
            return diagnostics;
        }

        // ---- version 0 -> 1 ---------------------------------------------------------------
        var renames = new List<(string From, string To, FlatValue Value)>();
        foreach (var (path, value) in table.Values)
        {
            if (path.Contains('.') || path.Contains('#'))
            {
                continue;
            }

            var normalized = new string(path.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (LegacyKeyMap.TryGetValue(normalized, out var target) && !table.Contains(target))
            {
                renames.Add((path, target, value));
            }
        }

        foreach (var (from, to, value) in renames)
        {
            var coerced = Coerce(value.Value, to, diagnostics, value.Origin);
            table.Set(to, coerced, value.Origin with { Location = value.Origin.Location });
            diagnostics.Add(new ConfigDiagnostic(
                ConfigDiagnosticSeverity.Warning,
                ConfigDiagnosticCode.MigrationApplied,
                $"legacy key '{from}' was renamed to '{to}' (config_version 0 -> 1); update the file to silence this",
                to,
                value.Origin.Location,
                value.Origin.File));
        }

        if (table.Contains("general.config_version"))
        {
            var origin = table.TryGet("general.config_version", out var existing) ? existing.Origin : ConfigOrigin.Defaults;
            table.Set("general.config_version", (long)ConfigSchema.CurrentConfigVersion, origin);
        }

        if (diagnostics.Count > 0)
        {
            diagnostics.Add(new ConfigDiagnostic(
                ConfigDiagnosticSeverity.Warning,
                ConfigDiagnosticCode.MigrationApplied,
                $"configuration migrated from config_version {version} to {ConfigSchema.CurrentConfigVersion}",
                "general.config_version"));
        }

        return diagnostics;
    }

    public static int ReadVersion(FlatTable table)
    {
        if (!table.TryGet("general.config_version", out var value))
        {
            // No explicit version: treat a file with only unknown top-level keys as legacy (0),
            // otherwise as current. This keeps a modern file that simply omits the key working.
            var hasLegacyKey = table.Values.Keys.Any(path =>
                !path.Contains('.')
                && LegacyKeyMap.ContainsKey(new string(path.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()));
            return hasLegacyKey ? LegacyVersion : ConfigSchema.CurrentConfigVersion;
        }

        return value.Value switch
        {
            long number => (int)number,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => ConfigSchema.CurrentConfigVersion,
        };
    }

    private static object? Coerce(object? value, string targetPath, List<ConfigDiagnostic> diagnostics, ConfigOrigin origin)
    {
        if (value is null || !ConfigSchema.TryGet(targetPath, out var descriptor))
        {
            return value;
        }

        switch (descriptor.Type)
        {
            case ConfigKeyType.Integer when value is string text:
                return long.TryParse(text.Trim(), out var number) ? number : value;
            case ConfigKeyType.Boolean when value is string text:
                if (bool.TryParse(text.Trim(), out var flag))
                {
                    return flag;
                }

                if (text.Trim() is "1" or "yes" or "on")
                {
                    return true;
                }

                if (text.Trim() is "0" or "no" or "off")
                {
                    return false;
                }

                return value;
            case ConfigKeyType.StringList when value is string text:
                // the reference product stores lists comma-delimited; keep that readable on migration only when
                // the value clearly is a list, so a single entry is not split.
                return text.Contains(',')
                    ? text.Split(',').Select(item => (object?)item.Trim()).Where(item => (string?)item is { Length: > 0 }).ToList()
                    : value;
            default:
                return value;
        }
    }
}