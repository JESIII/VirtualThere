using Vht.Config.Diagnostics;
using Vht.Config.Model;

namespace Vht.Config;

/// <summary>
/// Where config.toml and state.toml live. Precedence, highest first:
/// <list type="number">
/// <item><c>--config &lt;path&gt;</c> (this always wins - explicit beats implicit);</item>
/// <item><c>VHT_CONFIG</c> environment variable;</item>
/// <item>a <c>portable.marker</c> file next to the executable - portable builds keep their config
/// in the executable's own folder so they can be run from a USB stick, and two copies
/// unzipped to different places cannot share one machine-wide file;</item>
/// <item>the per-user / per-machine default for the role.</item>
/// </list>
/// Any role may ask for a different file name inside the resolved directory
/// (<see cref="ConfigPathResolver.Resolve"/>'s <c>configFileName</c>): the server keeps
/// <c>server.toml</c> where the rest of the product keeps <c>config.toml</c>. The <i>directory</i>
/// rule above is the same for every caller either way.
/// Defaults: Windows service <c>%ProgramData%\VirtualThere\config.toml</c>; Windows client
/// <c>%LOCALAPPDATA%\VirtualThere\config.toml</c>; Linux/macOS
/// <c>$XDG_CONFIG_HOME/virtualthere/config.toml</c> (or <c>~/.config/...</c>) and
/// <c>~/Library/Application Support/VirtualThere/config.toml</c>. Paths are never assembled with
/// Windows-only APIs, so this library and its tests run on any OS.
/// </summary>
public sealed record ConfigPaths(
    string ConfigFile,
    string StateFile,
    string Directory,
    ConfigSourceKind Source,
    string? PortableMarker)
{
    public static string DirectoryOf(string fileOrDirectory)
    {
        if (System.IO.Directory.Exists(fileOrDirectory))
        {
            return Path.GetFullPath(fileOrDirectory);
        }

        // a path with no extension and no existing parent is still treated as a file only when the
        // caller explicitly named config.toml; otherwise assume a directory.
        var name = Path.GetFileName(fileOrDirectory);
        if (name.Length == 0 || name.Equals(ConfigPathResolver.ConfigFileName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.GetDirectoryName(fileOrDirectory) ?? ".");
        }

        return Path.GetFullPath(fileOrDirectory);
    }
}

public static class ConfigPathResolver
{
    public const string ConfigFileName = "config.toml";
    public const string StateFileName = "state.toml";
    public const string PortableMarkerFileName = "portable.marker";

    public const string EnvironmentVariable = "VHT_CONFIG";
    public const string DataDirectoryVariable = "VHT_DATA_DIR";
    public const string AppFolderName = "VirtualThere";

    /// <param name="role">Whose default applies when nothing else resolves.</param>
    /// <param name="commandLinePath">An explicit <c>--config</c>; a file path or a directory.</param>
    /// <param name="environmentValue"><c>VHT_CONFIG</c>; <c>null</c> reads the process environment.</param>
    /// <param name="executableDirectory">Where the marker is looked for; defaults to
    /// <see cref="AppContext.BaseDirectory"/>. The portable marker's <i>presence</i> is the only thing
    /// that matters - removing the file restores the per-role default.</param>
    /// <param name="configFileName">File name to use inside the resolved directory. Defaults to
    /// <see cref="ConfigFileName"/>; the server passes <c>server.toml</c>. It never applies when the
    /// caller named a file explicitly (<paramref name="commandLinePath"/> /
    /// <paramref name="environmentValue"/> ending in <c>.toml</c>).</param>
    public static ConfigPaths Resolve(
        ConfigRole role,
        string? commandLinePath = null,
        string? environmentValue = null,
        string? executableDirectory = null,
        string? programDataDirectory = null,
        string? localApplicationDataDirectory = null,
        string? homeDirectory = null,
        string? xdgConfigHome = null,
        string? configFileName = null)
    {
        executableDirectory ??= AppContext.BaseDirectory;
        configFileName = string.IsNullOrWhiteSpace(configFileName) ? ConfigFileName : configFileName!;

        // 3. portable marker next to the binary. The marker redirects the directory to the executable's own folder, which
        //    is what keeps a portable build's config and state out of %ProgramData%/%LOCALAPPDATA%. What
        //    it governs is the directory and nothing else - the privileged paths (staged rebind INFs,
        //    TLS keys, the setup scripts' log) are not resolved here and stay machine-wide; see
        //    docs/SERVER.md section 2. Vht.Client/ClientPaths resolves the same directory for the client
        //    role.
        string? portableMarker = null;
        string? portableDirectory = null;
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var marker = Path.Combine(executableDirectory, PortableMarkerFileName);
            if (System.IO.File.Exists(marker))
            {
                portableMarker = marker;
                portableDirectory = executableDirectory;
            }
        }

        if (!string.IsNullOrWhiteSpace(commandLinePath))
        {
            return From(Path.GetFullPath(commandLinePath), ConfigSourceKind.CommandLine, portableMarker, configFileName);
        }

        environmentValue ??= Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return From(Path.GetFullPath(environmentValue), ConfigSourceKind.Environment, portableMarker, configFileName);
        }

        if (portableDirectory is not null)
        {
            return From(portableDirectory, ConfigSourceKind.Portable, portableMarker, configFileName);
        }

        return From(DefaultDirectory(role, programDataDirectory, localApplicationDataDirectory, homeDirectory, xdgConfigHome), ConfigSourceKind.Defaults, null, configFileName);
    }

    public static string DefaultDirectory(
        ConfigRole role,
        string? programDataDirectory = null,
        string? localApplicationDataDirectory = null,
        string? homeDirectory = null,
        string? xdgConfigHome = null)
    {
        if (OperatingSystem.IsWindows())
        {
            programDataDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            localApplicationDataDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var root = role switch
            {
                ConfigRole.Client => localApplicationDataDirectory,
                ConfigRole.Server => programDataDirectory,
                _ => programDataDirectory,
            };

            if (string.IsNullOrWhiteSpace(root))
            {
                root = localApplicationDataDirectory;
            }

            return Path.Combine(root ?? ".", AppFolderName);
        }

        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(homeDirectory ?? ".", "Library", "Application Support", AppFolderName);
        }

        xdgConfigHome ??= Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDirectory = string.IsNullOrWhiteSpace(xdgConfigHome)
            ? Path.Combine(homeDirectory ?? ".", ".config")
            : xdgConfigHome!;

        return Path.Combine(baseDirectory, AppFolderName.ToLowerInvariant());
    }

    private static ConfigPaths From(string fileOrDirectory, ConfigSourceKind source, string? portableMarker, string configFileName)
    {
        var path = Expand(fileOrDirectory);
        string directory;
        string configFile;

        if (path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            configFile = Path.GetFullPath(path);
            directory = Path.GetDirectoryName(configFile) ?? ".";
        }
        else
        {
            directory = ConfigPaths.DirectoryOf(path);
            configFile = Path.Combine(directory, configFileName);
        }

        if (directory.Length == 0)
        {
            directory = ".";
        }

        return new ConfigPaths(configFile, Path.Combine(directory, StateFileName), directory, source, portableMarker);
    }

    private static string Expand(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Length == 1 ? home : Path.Combine(home, expanded[2..]);
        }

        return expanded;
    }
}