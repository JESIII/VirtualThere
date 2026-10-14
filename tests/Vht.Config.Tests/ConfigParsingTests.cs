using Vht.Config.Diagnostics;
using Vht.Config.Model;
using Vht.Config.Policy;
using Xunit;

namespace Vht.Config.Tests;

/// <summary>
/// Schema + parsing + defaults + malformed-input behaviour. The card's acceptance criterion is
/// "a malformed config produces a clear error, not a crash", so several of these tests assert on the
/// diagnostics rather than on an exception.
/// </summary>
public sealed class ConfigParsingTests : IDisposable
{
    private readonly string _directory = TestSupport.NewTempDirectory("parse");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Defaults_are_applied_when_no_file_exists()
    {
        var store = TestSupport.StoreFor(_directory);
        var result = store.Load();

        Assert.False(result.HasErrors);
        Assert.Equal(ConfigSchema.CurrentConfigVersion, result.Config.General.ConfigVersion);
        Assert.Equal("TESTSERVER", result.Config.Server.Name);
        Assert.Equal(3240, result.Config.Transport.PlaintextPort);
        Assert.Equal(3241, result.Config.Transport.TlsPort);
        Assert.Equal(1024, result.Config.Transport.Lz4ThresholdBytes);
        Assert.Equal(Discovery.ServiceTypes.Vht, result.Config.Discovery.ServiceType);
        Assert.Equal(5, result.Config.Discovery.BrowseSeconds);
        Assert.Equal(64, result.Config.Server.PasswordMaxLength);
        Assert.True(result.Config.Server.Enabled);
        Assert.False(result.Config.AutoAttach.Enabled);
        Assert.Equal(ConfigSourceKind.Defaults, result.Config.OriginOf("transport.tls.port").Kind);
    }

    [Fact]
    public void Valid_file_is_read_and_every_origin_is_recorded()
    {
        TestSupport.WriteConfig(_directory, """
            [general]
            config_version = 1
            log_level = "debug"

            [server]
            name = "  workshop-pi  "

            [policy]
            allow = ["1050:0407"]
            ignore = ["dead:beef"]

            [transport.plaintext]
            port = 4000
            """);

        var result = TestSupport.StoreFor(_directory).Load();

        Assert.False(result.HasErrors);
        Assert.Equal("workshop-pi", result.Config.Server.Name);
        Assert.Equal("debug", result.Config.General.LogLevel);
        Assert.Equal(4000, result.Config.Transport.PlaintextPort);
        Assert.Single(result.Config.Policy.Allow);
        Assert.Single(result.Config.Policy.Ignore);

        var origin = result.Config.OriginOf("transport.plaintext.port");
        Assert.Equal(ConfigSourceKind.File, origin.Kind);
        Assert.EndsWith("config.toml", origin.File);
        Assert.True(origin.Location.Line > 0, "the origin must carry a real line number");
    }

    [Fact]
    public void Unknown_key_is_a_warning_by_default_and_an_error_under_strict()
    {
        TestSupport.WriteConfig(_directory, "servername_typo = \"x\"\n[server]\nnam = \"y\"\n");
        var relaxed = TestSupport.StoreFor(_directory).Load();

        Assert.False(relaxed.HasErrors);
        Assert.Contains(relaxed.Diagnostics, d => d.Code == ConfigDiagnosticCode.UnknownKey && d.Path == "servername_typo");
        Assert.Contains(relaxed.Diagnostics, d => d.Code == ConfigDiagnosticCode.UnknownKey && d.Path == "server.nam");

        TestSupport.WriteConfig(_directory, "[general]\nstrict = true\n\n[server]\nnam = \"y\"\n");
        var strict = TestSupport.StoreFor(_directory).Load();

        Assert.True(strict.HasErrors);
        Assert.Contains(strict.Errors, d => d.Code == ConfigDiagnosticCode.UnknownKey && d.Path == "server.nam");
    }

    [Theory]
    [InlineData("port = \n", "invalid TOML")]
    [InlineData("[transport.plaintext\nport = 1\n", "invalid TOML")]
    [InlineData("port = not-a-number\n", "invalid TOML")]
    public void Malformed_toml_reports_an_error_with_a_position_and_never_throws(string content, string _)
    {
        TestSupport.WriteConfig(_directory, content);
        var result = TestSupport.StoreFor(_directory).Load();

        Assert.True(result.HasErrors);
        var error = result.Errors.First();
        Assert.Equal(ConfigDiagnosticCode.InvalidTomlSyntax, error.Code);
        Assert.True(error.Location.Line > 0, "a syntax error must point at a line");
        Assert.EndsWith("config.toml", error.File!);
        Assert.Contains("config.toml", error.Describe());
    }

    [Fact]
    public void Type_mismatch_and_range_violations_are_errors_with_the_key_and_line()
    {
        TestSupport.WriteConfig(_directory, """
            [transport.tls]
            port = "not a port"

            [transport.plaintext]
            port = 70000

            [discovery]
            browse_seconds = true
            """);

        var result = TestSupport.StoreFor(_directory).Load();

        Assert.True(result.HasErrors);
        var typeMismatch = result.Errors.Single(e =>
            e.Code == ConfigDiagnosticCode.TypeMismatch && e.Path == "transport.tls.port");
        Assert.Equal("transport.tls.port", typeMismatch.Path);
        Assert.True(typeMismatch.Location.Line > 0);

        var range = result.Errors.Single(e => e.Code == ConfigDiagnosticCode.ValueOutOfRange);
        Assert.Equal("transport.plaintext.port", range.Path);
        Assert.Contains("1..65535", range.Message);

        // the bad value must not silently become the fallback without saying so.
        Assert.Equal(3241, result.Config.Transport.TlsPort);
    }

    [Fact]
    public void A_newer_config_version_is_refused_with_a_clear_message()
    {
        TestSupport.WriteConfig(_directory, "[general]\nconfig_version = 99\n");
        var result = TestSupport.StoreFor(_directory).Load();

        Assert.True(result.HasErrors);
        var error = result.Errors.Single(e => e.Code == ConfigDiagnosticCode.UnsupportedConfigVersion);
        Assert.Contains("99", error.Message);
        Assert.Contains("Refusing to guess", error.Message);
    }

    [Fact]
    public void Machine_managed_keys_are_not_reported_as_unknown()
    {
        TestSupport.WriteConfig(_directory, "[identity]\n# hand-written entries are unusual but must not be an unknown key\n");
        var result = TestSupport.StoreFor(_directory).Load();

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == ConfigDiagnosticCode.UnknownKey);
    }

    [Fact]
    public void Password_protected_devices_on_the_plaintext_listener_are_rejected_and_the_plaintext_warning_is_emitted()
    {
        TestSupport.WriteConfig(_directory, """
            [security]
            plaintext_password_protected_devices = true

            [passwords]
            "1050:0407" = "hunter2"
            """);

        var result = TestSupport.StoreFor(_directory).Load();

        Assert.Contains(result.Errors, e => e.Path == "security.plaintext_password_protected_devices");
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == ConfigDiagnosticSeverity.Warning
            && d.Path == "transport.plaintext.enabled"
            && d.Message.Contains("TLS listener only"));
    }

    [Fact]
    public void Every_schema_key_is_documented_in_docs_config_md()
    {
        var document = File.ReadAllText(FindRepositoryFile("docs/CONFIG.md"));
        var missing = ConfigSchema.All
            .Where(key => !document.Contains(key.Path, StringComparison.Ordinal))
            .Select(key => key.Path)
            .ToList();

        Assert.True(missing.Count == 0, "these schema keys are missing from docs/CONFIG.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void Schema_matching_covers_maps_and_table_arrays()
    {
        Assert.True(ConfigSchema.IsKnown("nicknames"));
        Assert.True(ConfigSchema.IsKnown("nicknames.1050:0407"));
        Assert.True(ConfigSchema.IsKnown("passwords.s-1050-0407-0123456789ab"));
        Assert.True(ConfigSchema.IsKnown("identity.devices#0.key"));
        Assert.True(ConfigSchema.IsKnown("auto_attach.grants#3.device_key"));
        Assert.False(ConfigSchema.IsKnown("nicknames2"));
        Assert.False(ConfigSchema.IsKnown("totally.unknown"));
    }

    internal static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"could not locate {relativePath} above {AppContext.BaseDirectory}");
    }
}