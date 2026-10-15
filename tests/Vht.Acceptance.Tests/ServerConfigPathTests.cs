using Vht.Config;
using Vht.Config.Diagnostics;
using Vht.Server.Config;

namespace Vht.Acceptance.Tests;

/// <summary>
/// Where the <b>server</b> keeps <c>server.toml</c>.
/// </summary>
/// <remarks>
/// <para>
/// The defect this covers: the server assembled <c>%ProgramData%\VirtualThere\server.toml</c> itself,
/// so a portable <c>vht-server.exe</c> - shipped in a folder that contains <c>portable.marker</c> -
/// read and wrote a machine-wide file outside the folder it was unzipped into. Two portable copies on
/// one machine shared it, and an unelevated server usually could not create the directory at all, so
/// the settings silently never persisted.
/// </para>
/// <para>
/// A client-only test does not close this, so these assert the server's own resolution:
/// <see cref="ServerConfigLoader.Resolve"/> must redirect to <c>./config/server.toml</c> when the
/// marker is beside the executable (the directory <c>Vht.Client/ClientPaths</c> and the shipped
/// <c>portable.marker</c> both name), keep the installed <c>%ProgramData%</c> default when it is not,
/// and let an explicit path win over both.
/// </para>
/// </remarks>
public sealed class ServerConfigPathTests
{
    [Fact]
    public void A_marker_beside_the_executable_puts_the_servers_config_in_that_folder()
    {
        string root = NewDirectory("server-portable");
        try
        {
            File.WriteAllText(Path.Combine(root, ConfigPathResolver.PortableMarkerFileName), string.Empty);

            ConfigPaths paths = ServerConfigLoader.Resolve(executableDirectory: root, environmentValue: null);

            Assert.Equal(Path.Combine(root, "config", "server.toml"), paths.ConfigFile);
            Assert.Equal(Path.Combine(root, "config"), paths.Directory);
            Assert.Equal(Path.Combine(root, "config", ConfigPathResolver.StateFileName), paths.StateFile);
            Assert.Equal(ConfigSourceKind.Portable, paths.Source);
            Assert.Equal(Path.Combine(root, ConfigPathResolver.PortableMarkerFileName), paths.PortableMarker);

            // The whole point: nothing of the server's lands in the machine-wide location.
            string machineWide = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (machineWide.Length > 0)
                Assert.DoesNotContain(machineWide, paths.ConfigFile, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Removing_the_marker_restores_the_machine_wide_default()
    {
        string root = NewDirectory("server-marker-removed");
        try
        {
            string programData = Path.Combine(root, "programdata");
            string marker = Path.Combine(root, ConfigPathResolver.PortableMarkerFileName);

            File.WriteAllText(marker, string.Empty);
            ConfigPaths portable = ServerConfigLoader.Resolve(
                executableDirectory: root,
                environmentValue: null,
                programDataDirectory: programData);
            Assert.Equal(Path.Combine(root, "config", "server.toml"), portable.ConfigFile);

            File.Delete(marker);
            ConfigPaths installed = ServerConfigLoader.Resolve(
                executableDirectory: root,
                environmentValue: null,
                programDataDirectory: programData);

            Assert.Equal(Path.Combine(programData, "VirtualThere", "server.toml"), installed.ConfigFile);
            Assert.Equal(ConfigSourceKind.Defaults, installed.Source);
            Assert.Null(installed.PortableMarker);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Without_a_marker_the_default_path_is_still_the_installed_one()
    {
        string root = NewDirectory("server-default-created");
        try
        {
            string programData = Path.Combine(root, "programdata");
            Assert.Equal(
                Path.Combine(programData, "VirtualThere", "server.toml"),
                ServerConfigLoader.Resolve(programDataDirectory: programData).ConfigFile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_explicit_path_or_VHT_CONFIG_wins_over_the_marker()
    {
        string root = NewDirectory("server-explicit");
        string? previous = Environment.GetEnvironmentVariable(ConfigPathResolver.EnvironmentVariable);
        try
        {
            File.WriteAllText(Path.Combine(root, ConfigPathResolver.PortableMarkerFileName), string.Empty);

            var fromEnvironment = ServerConfigLoader.Resolve(
                executableDirectory: root,
                environmentValue: Path.Combine(root, "elsewhere", "server.toml"));
            Assert.Equal(Path.Combine(root, "elsewhere", "server.toml"), fromEnvironment.ConfigFile);
            Assert.Equal(ConfigSourceKind.Environment, fromEnvironment.Source);
            // the marker is still reported, so a caller can say what it found
            Assert.NotNull(fromEnvironment.PortableMarker);

            var fromCommandLine = ServerConfigLoader.Resolve(
                commandLinePath: Path.Combine(root, "explicit.toml"),
                executableDirectory: root,
                environmentValue: Path.Combine(root, "elsewhere", "server.toml"));
            Assert.Equal(Path.Combine(root, "explicit.toml"), fromCommandLine.ConfigFile);
            Assert.Equal(ConfigSourceKind.CommandLine, fromCommandLine.Source);

            // and the process-wide variable is honoured when the caller passes nothing (it was not
            // honoured at all before this fix: the server hardcoded the %ProgramData% path)
            Environment.SetEnvironmentVariable(ConfigPathResolver.EnvironmentVariable, Path.Combine(root, "env.toml"));
            Assert.Equal(Path.Combine(root, "env.toml"), ServerConfigLoader.Resolve(executableDirectory: root).ConfigFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigPathResolver.EnvironmentVariable, previous);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewDirectory(string label)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "vht-server-config-tests", label + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
