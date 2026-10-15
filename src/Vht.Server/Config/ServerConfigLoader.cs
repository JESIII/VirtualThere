using System.Collections.Concurrent;
using Vht.Config;
using Vht.Config.Model;
using Vht.Core.Logging;
using Vht.Core.Server;

namespace Vht.Server.Config;

/// <summary>
/// Reads the subset of TOML the server needs at start-up.
/// </summary>
/// <remarks>
/// <para>
/// The config card (<c>config-workstream</c>) owns the full document model, its schema validation and
/// the round-tripping writer. This loader exists so the <i>service</i> can be configured today:
/// a Windows service is started by the SCM with no command line, so its policy (the ignore
/// list, the capture mode, the ports) has to come from a file. The key names are the frozen
/// vocabulary from ARCHITECTURE.md Appendix C, so a document this loader accepts is a document
/// the config card will also accept.
/// </para>
/// <para>
/// Deliberately supported: comments, <c>[section]</c> headers, dotted keys, strings, booleans,
/// integers and arrays of strings. Deliberately not supported: nested inline tables, dates,
/// floats and multi-line strings — none of which the server's keys use. Anything unparseable is
/// reported, never guessed at, and unknown keys are warned about rather than fatal.
/// </para>
/// </remarks>
public static class ServerConfigLoader
{
    /// <summary>The file name the server keeps its configuration in.</summary>
    public const string FileName = "server.toml";

    /// <summary>Sub-directory of the resolved config directory the server's runtime log lives in.</summary>
    public const string LogDirectoryName = "logs";

    /// <summary>The server's runtime log file name, inside <see cref="LogDirectoryName"/>.</summary>
    public const string LogFileName = "server.log";

    /// <summary>
    /// Where the server writes its runtime log when nothing names a file: the same resolved
    /// directory the configuration came from, in <c>logs/server.log</c> - <c>./config/logs</c> in a
    /// portable folder, <c>%ProgramData%\VirtualThere\logs</c> otherwise.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Resolve"/> rather than assembled, so the log cannot end up somewhere
    /// the marker does not govern. Note that <c>%ProgramData%\VirtualThere\logs</c> is also where
    /// the setup scripts put <c>portable-setup.log</c>: the server's file is <c>server.log</c>, so
    /// the two never collide.
    /// </remarks>
    public static string DefaultLogFile(ConfigPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.Directory, LogDirectoryName, LogFileName);
    }

    /// <summary>
    /// Resolves where <c>server.toml</c> lives, through the product-wide rule
    /// (<see cref="ConfigPathResolver"/>): <c>--config</c>, then <c>VHT_CONFIG</c>, then a
    /// <c>portable.marker</c> beside the executable (which puts the file in <c>./config</c> in that
    /// folder), then the installed default <c>%ProgramData%\VirtualThere\server.toml</c>.
    /// </summary>
    /// <remarks>
    /// The server used to assemble the <c>%ProgramData%</c> path itself, which made a portable
    /// <c>vht-server.exe</c> read and write a machine-wide file outside the folder it was unzipped
    /// into - and two portable copies share that one file. This is the same resolution the client and
    /// the rest of the product use, so all three now agree: the marker's presence redirects the
    /// directory, its absence leaves the installed default alone (the MSI/service case).
    /// </remarks>
    public static ConfigPaths Resolve(
        string? commandLinePath = null,
        string? environmentValue = null,
        string? executableDirectory = null,
        string? programDataDirectory = null,
        string? localApplicationDataDirectory = null) =>
        ConfigPathResolver.Resolve(
            ConfigRole.Server,
            commandLinePath,
            environmentValue,
            executableDirectory,
            programDataDirectory,
            localApplicationDataDirectory,
            configFileName: FileName);

    /// <summary>Default config path when <c>--config</c> and <c>VHT_CONFIG</c> are not given.</summary>
    public static string DefaultPath => Resolve().ConfigFile;

    /// <summary>Applies a TOML file onto <paramref name="options"/>; reports problems.</summary>
    public static IReadOnlyList<string> Apply(string path, ServerOptions options, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        if (!File.Exists(path))
            return problems;

        Dictionary<string, string> values;
        try
        {
            values = Parse(File.ReadAllLines(path), problems);
        }
        catch (IOException ex)
        {
            problems.Add($"could not read {path}: {ex.Message}");
            return problems;
        }

        foreach ((string key, string raw) in values)
        {
            if (!TryApply(key, raw, options, out string? detail))
                problems.Add(detail ?? $"unknown or malformed key '{key}'");
        }

        log?.Write(LogLevel.Info, LogEvent.ConfigurationLoaded,
            $"configuration loaded from {path}: {values.Count} key(s), {problems.Count} problem(s)",
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["keys"] = values.Count,
                ["problems"] = problems.Count,
            });

        return problems;
    }

    /// <summary>
    /// Parses the supported TOML subset into a flat <c>section.key -&gt; raw value</c> map.
    /// </summary>
    public static Dictionary<string, string> Parse(IEnumerable<string> lines, List<string> problems)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = string.Empty;

        foreach (string original in lines)
        {
            string line = StripComment(original).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                problems.Add($"line is not a key/value pair: '{original.Trim()}'");
                continue;
            }

            string key = line[..equals].Trim().Trim('"');
            string value = line[(equals + 1)..].Trim();

            if (key.Length == 0)
            {
                problems.Add($"empty key in line '{original.Trim()}'");
                continue;
            }

            // TOML semantics: the key is resolved *relative to the current section*, dotted or not, so
            // `rebind.enabled` under `[capture]` is `capture.rebind.enabled`. A key that already names
            // the section (`capture.mode` under `[capture]`) is left alone, so both spellings are
            // accepted. Dropping the section for a dotted left-hand side silently made the generated
            // template's `capture.rebind.enabled` unreachable (it arrived as `rebind.enabled`, which no
            // case accepts), which is exactly the defect this rule closes.
            string full = section.Length == 0 ||
                          key.StartsWith(section + ".", StringComparison.OrdinalIgnoreCase)
                ? key
                : $"{section}.{key}";

            if (values.ContainsKey(full))
                problems.Add($"duplicate key '{full}'; the last value wins");

            values[full] = value;
        }

        return values;
    }

    private static bool TryApply(string key, string raw, ServerOptions options, out string? detail)
    {
        detail = null;

        string text = Unquote(raw);
        bool isBool = bool.TryParse(text, out bool flag);
        bool isInt = int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int number);

        switch (key.ToLowerInvariant())
        {
            case "server.name":
            case "general.server_name":
                options.ServerName = text;
                return true;

            // Mirrors the config card's `general.strict`: an unknown key becomes fatal for the load
            // rather than a warning. Off by default so an old file with a stray key still starts.
            case "general.strict":
                if (!isBool) goto default;
                options.StrictConfig = flag;
                return true;

            case "transport.plaintext.enabled":
                if (!isBool) goto default;
                options.PlaintextListenerEnabled = flag;
                return true;

            case "transport.plaintext.port":
                if (!isInt) goto default;
                options.PlaintextPort = number;
                return true;

            case "transport.plaintext.address":
                options.PlaintextAddress = text;
                return true;

            case "transport.tls.enabled":
                if (!isBool) goto default;
                options.TlsListenerEnabled = flag;
                return true;

            case "transport.tls.port":
                if (!isInt) goto default;
                options.TlsPort = number;
                return true;

            case "transport.tls.address":
                options.TlsAddress = text;
                return true;

            case "capture.mode":
                options.CaptureMode = text;
                return true;

            case "capture.rebind.enabled":
                if (!isBool) goto default;
                options.RebindEnabled = flag;
                return true;

            case "capture.refuse_management_nic":
                if (!isBool) goto default;
                options.RefuseManagementNic = flag;
                return true;

            case "policy.allow":
            case "policy.allowed":
            {
                options.AllowList.Clear();
                options.AllowList.AddRange(ReadStringArray(raw, out detail));
                return detail is null;
            }

            case "policy.ignore":
            case "policy.ignored":
            {
                options.IgnoreList.Clear();
                options.IgnoreList.AddRange(ReadStringArray(raw, out detail));
                return detail is null;
            }

            case "connections.max_in_flight_urbs":
                if (!isInt) goto default;
                options.MaxInFlightUrbs = number;
                return true;

            case "connections.max_transfer_buffer":
                if (!isInt) goto default;
                options.MaxTransferBuffer = number;
                return true;

            case "logging.level":
                if (!Enum.TryParse(text, ignoreCase: true, out LogLevel level)) goto default;
                options.LogLevel = level;
                return true;

            case "logging.file":
                // Empty means "the default location", not "no file" (see ServerOptions.LogFile).
                options.LogFile = text.Length == 0 ? null : text;
                return true;

            case "control_plane.enabled":
                if (!isBool) goto default;
                options.ControlPlaneEnabled = flag;
                return true;

            case "control_plane.pipe_name":
                options.ControlPlanePipeName = text;
                return true;

            case "hooks.timeout_seconds":
                if (!isInt) goto default;
                options.Hooks.Timeout = TimeSpan.FromSeconds(number);
                return true;

            case "hooks.script_root":
                options.Hooks.AllowedScriptRoots.Clear();
                options.Hooks.AllowedScriptRoots.AddRange(ReadStringArray(raw, out detail));
                return detail is null;

            default:
                // hooks.scripts.<hookName> = "path" — the hook vocabulary is the key space.
                if (key.StartsWith("hooks.scripts.", StringComparison.OrdinalIgnoreCase))
                {
                    string hookName = key["hooks.scripts.".Length..];
                    if (!Core.Hooks.HookVocabulary.TryParse(hookName, out Core.Hooks.HookEvent hook))
                    {
                        detail = $"'{hookName}' is not a hook in the frozen vocabulary";
                        return false;
                    }

                    options.Hooks.Scripts[Core.Hooks.HookVocabulary.Name(hook)] = text;
                    return true;
                }

                detail = $"unknown configuration key '{key}'; it is not a key of the generated template " +
                         $"('vht config-template'), so it is ignored";
                return false;
        }
    }

    private static IEnumerable<string> ReadStringArray(string raw, out string? detail)
    {
        detail = null;

        string trimmed = raw.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
        {
            detail = $"expected an array of strings, got '{raw}'";
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (string part in trimmed[1..^1].Split(','))
        {
            string item = Unquote(part.Trim());
            if (item.Length > 0)
                items.Add(item);
        }

        return items;
    }

    private static string Unquote(string value)
    {
        string text = value.Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            return text[1..^1];

        return text;
    }

    /// <summary>Removes a <c>#</c> comment that is not inside a quoted string.</summary>
    private static string StripComment(string line)
    {
        bool inDouble = false;
        bool inSingle = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '#' && !inDouble && !inSingle) return line[..i];
        }

        return line;
    }

    /// <summary>A commented template of every key the loader accepts.</summary>
    public static string Template => """
        # VirtualThere server configuration.
        # Key names are the frozen vocabulary in ARCHITECTURE.md Appendix C.

        [server]
        name = "vht-server"

        [transport.plaintext]
        # Stock USB/IP. Enabled by default so a stock usbip client can attach with no setup.
        enabled = true
        address = "0.0.0.0"
        port = 3240

        [transport.tls]
        # Served only once the transport card supplies its implementation.
        enabled = false
        address = "0.0.0.0"
        port = 3241

        [capture]
        # auto | winusb-only | prebound-only
        mode = "auto"
        # P2: install an INF selecting the inbox winusb.sys for a device Windows already owns.
        # Needs an elevated process; off by default because it changes a driver binding.
        rebind.enabled = false
        refuse_management_nic = true

        [policy]
        # allow = [] means "everything not ignored". Rule form: vvvv:pppp[:serial][@server]
        allow = []
        ignore = []

        [connections]
        # Per-connection guards. These bound one connection's memory; they are not device caps.
        max_in_flight_urbs = 256
        max_transfer_buffer = 16777216

        [logging]
        level = "Info"
        # "" means the default location, which is not optional: <config dir>\logs\server.log,
        # i.e. ./config/logs/server.log in a portable folder. Naming a file adds a second
        # destination; the default is always kept, because the lines the worker writes before it
        # reads this file only exist there.
        file = ""

        [control_plane]
        enabled = true
        pipe_name = "vht-server"

        [hooks]
        timeout_seconds = 10
        script_root = []
        # scripts.onBind = "C:\\path\\to\\onBind.cmd"
        """;
}
