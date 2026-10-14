using Vht.Config.Diagnostics;
using Vht.Config.Model;

namespace Vht.Config;

/// <summary>
/// Where config.toml and state.toml live. Precedence, highest first:
/// <list type="number">
/// <item><c>--config &lt;path&gt;</c> (this always wins - explicit beats implicit);</item>
/// <item><c>VHT_CONFIG</c> environment variable;</item>
/// <item>a <c>portable.marker</c> file next to the executable - portable builds keep their config
/// beside the binary so they can be run from a USB stick;</item>
/// <item>the per-user / per-machine default for the role.</item>
/// </list>
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

    public static ConfigPaths Resolve(
        ConfigRole role,
        string? commandLinePath = null,
        string? environmentValue = null,
        string? executableDirectory = null,
        string? programDataDirectory = null,
        string? localApplicationDataDirectory = null,
        string? homeDirectory = null,
        string? xdgConfigHome = null)
    {
        executableDirectory ??= AppContext.BaseDirectory;

        // 3. portable marker next to the binary.
        string? portableMarker = null;
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var marker = Path.Combine(executableDirectory, PortableMarkerFileName);
            if (System.IO.File.Exists(marker))
            {
                portableMarker = marker;
            }
        }

        if (!string.IsNullOrWhiteSpace(commandLinePath))
        {
            return From(Path.GetFullPath(commandLinePath), ConfigSourceKind.CommandLine, portableMarker);
        }

        environmentValue ??= Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return From(Path.GetFullPath(environmentValue), ConfigSourceKind.Environment, portableMarker);
        }

        if (portableMarker is not null)
        {
            return From(executableDirectory!, ConfigSourceKind.Environment, portableMarker);
        }

        return From(DefaultDirectory(role, programDataDirectory, localApplicationDataDirectory, homeDirectory, xdgConfigHome), ConfigSourceKind.Defaults, null);
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

    private static ConfigPaths From(string fileOrDirectory, ConfigSourceKind source, string? portableMarker)
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
            configFile = Path.Combine(directory, ConfigFileName);
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