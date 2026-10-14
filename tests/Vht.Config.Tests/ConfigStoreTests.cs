using Vht.Config.AutoAttach;
using Vht.Config.Diagnostics;
using Vht.Config.Model;
using Xunit;

namespace Vht.Config.Tests;

/// <summary>
/// Path resolution, layering, persistence and reload classification - i.e. the "renaming a device
/// persists across server restart" acceptance criterion plus the restart/no-restart story.
/// </summary>
public sealed class ConfigStoreTests
{
    [Fact]
    public void Path_precedence_is_command_line_then_environment_then_portable_then_default()
    {
        var temporary = TestSupport.NewTempDirectory("paths");
        try
        {
            var explicitFile = Path.Combine(temporary, "explicit.toml");
            var environmentFile = Path.Combine(temporary, "env.toml");

            var fromCommandLine = ConfigPathResolver.Resolve(ConfigRole.Server, explicitFile, environmentFile, temporary);
            Assert.Equal(explicitFile, fromCommandLine.ConfigFile);
            Assert.Equal(ConfigSourceKind.CommandLine, fromCommandLine.Source);

            var fromEnvironment = ConfigPathResolver.Resolve(ConfigRole.Server, null, environmentFile, temporary);
            Assert.Equal(environmentFile, fromEnvironment.ConfigFile);
            Assert.Equal(ConfigSourceKind.Environment, fromEnvironment.Source);

            // a portable.marker next to the binary keeps config beside the binary
            var portable = Path.Combine(temporary, "portable");
            Directory.CreateDirectory(portable);
            File.WriteAllText(Path.Combine(portable, ConfigPathResolver.PortableMarkerFileName), string.Empty);
            var portablePaths = ConfigPathResolver.Resolve(ConfigRole.Server, null, null, portable);
            Assert.Equal(Path.Combine(portable, ConfigPathResolver.ConfigFileName), portablePaths.ConfigFile);
            Assert.NotNull(portablePaths.PortableMarker);

            var defaults = ConfigPathResolver.Resolve(
                ConfigRole.Server,
                null,
                null,
                Path.Combine(temporary, "nolmarker"),
                programDataDirectory: Path.Combine(temporary, "programdata"),
                localApplicationDataDirectory: Path.Combine(temporary, "localappdata"),
                homeDirectory: temporary,
                xdgConfigHome: null);

            Assert.Equal(Path.Combine(temporary, "programdata", "VirtualThere", ConfigPathResolver.ConfigFileName), defaults.ConfigFile);
            Assert.Equal(ConfigSourceKind.Defaults, defaults.Source);

            var clientDefaults = ConfigPathResolver.Resolve(
                ConfigRole.Client,
                null,
                null,
                Path.Combine(temporary, "nomarker"),
                programDataDirectory: Path.Combine(temporary, "programdata"),
                localApplicationDataDirectory: Path.Combine(temporary, "localappdata"),
                homeDirectory: temporary,
                xdgConfigHome: null);
            Assert.StartsWith(Path.Combine(temporary, "localappdata"), clientDefaults.ConfigFile);

            // state.toml always sits next to config.toml
            Assert.Equal(Path.GetDirectoryName(defaults.ConfigFile), Path.GetDirectoryName(defaults.StateFile));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public void Environment_overrides_beat_the_files_and_are_coerced_to_the_schema_type()
    {
        var directory = TestSupport.NewTempDirectory("env");
        try
        {
            TestSupport.WriteConfig(directory, "[server]\nname = \"from-file\"\n");
            var store = TestSupport.StoreFor(directory, new Dictionary<string, string?>
            {
                ["VHT_SERVER__NAME"] = "from-env",
                ["VHT_TRANSPORT__TLS__LZ4_THRESHOLD_BYTES"] = "4096",
                ["VHT_DISCOVERY__MANUAL_SERVERS"] = "10.0.0.5:3240, 10.0.0.6",
                ["VHT_NOT__A__KEY"] = "ignored",
            });

            var result = store.Load();

            Assert.Equal("from-env", result.Config.Server.Name);
            Assert.Equal(4096, result.Config.Transport.Lz4ThresholdBytes);
            Assert.Equal(new[] { "10.0.0.5:3240", "10.0.0.6" }, result.Config.Discovery.ManualServers);
            Assert.Equal(ConfigSourceKind.Environment, result.Config.OriginOf("server.name").Kind);
            Assert.Contains(result.Diagnostics, d => d.Code == ConfigDiagnosticCode.UnknownKey && d.Message.Contains("VHT_NOT__A__KEY"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void State_overlays_the_config_file_without_rewriting_it()
    {
        var directory = TestSupport.NewTempDirectory("overlay");
        try
        {
            TestSupport.WriteConfig(directory, "[general]\nlog_level = \"warn\"\n\n[server]\nname = \"file-name\"\n");
            TestSupport.WriteState(directory, "[server]\nname = \"state-name\"\n");
            var configBefore = File.ReadAllText(Path.Combine(directory, ConfigPathResolver.ConfigFileName));

            var result = TestSupport.StoreFor(directory).Load();

            Assert.Equal("state-name", result.Config.Server.Name);
            Assert.Equal(ConfigSourceKind.Overlay, result.Config.OriginOf("server.name").Kind);
            Assert.Equal(ConfigSourceKind.File, result.Config.OriginOf("general.log_level").Kind);
            Assert.Equal(configBefore, File.ReadAllText(Path.Combine(directory, ConfigPathResolver.ConfigFileName)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_nickname_persists_across_a_restart_and_shows_in_the_device_list_name()
    {
        var directory = TestSupport.NewTempDirectory("nickname");
        try
        {
            var store = TestSupport.StoreFor(directory);
            store.Load();
            store.SetNickname("1050:0407", "Work security key");
            var save = store.Save();
            Assert.Empty(save);

            // "restart": a brand new store over the same directory
            var restarted = TestSupport.StoreFor(directory);
            var result = restarted.Load();

            Assert.False(result.HasErrors);
            var name = result.Config.Nicknames.DisplayName(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB");
            Assert.Equal("Work security key", name);

            // and an explicit device-key nickname wins over the vid:pid entry
            restarted.SetNickname("key:s-1050-0407-0123456789AB", "Personal key");
            restarted.Save();
            var third = TestSupport.StoreFor(directory);
            third.Load();
            Assert.Equal("Personal key", third.Current!.Config.Nicknames.DisplayName(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Manual_servers_and_grants_persist_and_grants_require_authorization_first()
    {
        var directory = TestSupport.NewTempDirectory("state");
        try
        {
            var store = TestSupport.StoreFor(directory);
            store.Load();
            store.AddManualServer("192.168.1.20:3241");
            store.AddManualServer("192.168.1.20:3241"); // idempotent
            store.AddManualServer("[fe80::1]:3240");
            store.SetDevicePassword(Vht.Config.DevicePassword.CreatePbkdf2("1050:0407", "hunter2", iterations: 1000));

            var refused = store.RecordAutoAttachGrant(
                new AutoAttachGrantRequest("s-1050-0407-0123456789AB", "TESTSERVER", null, false, null),
                authorizationSucceeded: false);
            Assert.False(refused.Recorded);
            Assert.Empty(store.AutoAttachGrants);

            var recorded = store.RecordAutoAttachGrant(
                new AutoAttachGrantRequest("s-1050-0407-0123456789AB", "TESTSERVER", null, false, null),
                authorizationSucceeded: true);
            Assert.True(recorded.Recorded);
            Assert.Single(store.AutoAttachGrants);

            store.Save();

            var restarted = TestSupport.StoreFor(directory);
            var result = restarted.Load();

            Assert.Equal(new[] { "192.168.1.20:3241", "[fe80::1]:3240" }, result.Config.Discovery.ManualServers.OrderBy(x => x));
            Assert.Single(restarted.AutoAttachGrants);
            Assert.Equal("s-1050-0407-0123456789AB", restarted.AutoAttachGrants[0].DeviceKey);

            var password = result.Config.Passwords.Find(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB");
            Assert.NotNull(password);
            Assert.Equal(PasswordEncoding.Pbkdf2, password!.Encoding);
            Assert.True(password.Verify("hunter2"));
            Assert.False(password.Verify("hunter3"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_reload_plan_separates_live_changes_from_restart_required_ones()
    {
        var directory = TestSupport.NewTempDirectory("reload");
        try
        {
            TestSupport.WriteConfig(directory, "[transport.tls]\nport = 3241\n\n[nicknames]\n\"1050:0407\" = \"before\"\n");
            var store = TestSupport.StoreFor(directory);
            store.Load();

            TestSupport.WriteConfig(directory, "[transport.tls]\nport = 4001\n\n[nicknames]\n\"1050:0407\" = \"after\"\n");
            var (result, plan) = store.Reload();

            Assert.False(result.HasErrors);
            Assert.True(plan.HasChanges);
            Assert.True(plan.RequiresRestart);
            Assert.Contains(plan.RestartRequired, c => c.Path == "transport.tls.port");
            Assert.Contains(plan.HotChanges, c => c.Path == "nicknames.1050:0407");
            Assert.Contains("RESTART REQUIRED", plan.Describe());
            Assert.Equal(4001, result.Config.Transport.TlsPort);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_watcher_reports_a_change_after_a_save()
    {
        var directory = TestSupport.NewTempDirectory("watch");
        try
        {
            TestSupport.WriteConfig(directory, "[server]\nname = \"one\"\n");
            var store = TestSupport.StoreFor(directory);
            store.Load();
            using var watcher = store.StartWatching(debounceMilliseconds: 50);

            if (!watcher.IsWatching)
            {
                // no OS watcher available: the manual trigger path must still work
                watcher.Trigger();
                Assert.NotEmpty(await watcher.WaitForChangeAsync(TimeSpan.FromSeconds(1)));
                return;
            }

            File.AppendAllText(Path.Combine(directory, ConfigPathResolver.ConfigFileName), "\n[general]\nlog_level = \"trace\"\n");
            var changed = await watcher.WaitForChangeAsync(TimeSpan.FromSeconds(10));
            Assert.NotEmpty(changed);

            var (result, plan) = store.Reload();
            Assert.Contains(plan.HotChanges, c => c.Path == "general.log_level");
            Assert.Equal("trace", result.Config.General.LogLevel);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temporary_file_behind()
    {
        var directory = TestSupport.NewTempDirectory("atomic");
        try
        {
            var store = TestSupport.StoreFor(directory);
            store.Load();
            store.SetNickname("1050:0407", "x");
            store.Save();

            Assert.True(File.Exists(Path.Combine(directory, ConfigPathResolver.StateFileName)));
            Assert.False(File.Exists(Path.Combine(directory, ConfigPathResolver.StateFileName + ".tmp")));
            var state = File.ReadAllText(Path.Combine(directory, ConfigPathResolver.StateFileName));
            Assert.Contains("MACHINE MANAGED", state);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}