using Vht.Config.AutoAttach;
using Vht.Config.DeviceIdentity;
using Vht.Config.Diagnostics;
using Vht.Config.Migration;
using Vht.Config.Toml;
using Xunit;

namespace Vht.Config.Tests;

public sealed class CredentialsAndAutoAttachTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nicknames_resolve_most_specific_first()
    {
        var store = new NicknameStore(new Dictionary<string, string>
        {
            ["1050:0407"] = "by vid:pid",
            ["1050:0407:0123456789AB"] = "by serial",
            ["s-1050-0407-0123456789ab"] = "by key",
            ["product:USB 2.0 Stick"] = "by product",
        });

        Assert.Equal("by key", store.Resolve(TestSupport.SerialDevice(), "s-1050-0407-0123456789AB"));
        Assert.Equal("by serial", store.Resolve(TestSupport.SerialDevice(), "s-other"));
        Assert.Equal("by vid:pid", store.Resolve(TestSupport.SerialDevice("FFFFFFFFFFFF"), "s-x"));
        Assert.Equal("by product", store.Resolve(TestSupport.SerialLessDevice(), "f-x"));
        Assert.Null(store.Resolve(TestSupport.AnonymousDevice(), "t-x"));
        Assert.Equal("t-x (1a2b:0001)", store.DisplayName(TestSupport.AnonymousDevice(), "t-x"));
    }

    [Fact]
    public void Passwords_support_plaintext_sha256_and_pbkdf2_and_never_leak_the_secret()
    {
        Assert.True(DevicePassword.TryParse("1050:0407", "plain", out var plain, out _));
        Assert.Equal(PasswordEncoding.Plaintext, plain.Encoding);
        Assert.True(plain.Verify("plain"));
        Assert.False(plain.Verify("Plain"));

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("s3cret"u8));
        Assert.True(DevicePassword.TryParse("1050:0407", "sha256:" + hash, out var sha, out _));
        Assert.True(sha.Verify("s3cret"));
        Assert.False(sha.Verify("nope"));
        Assert.DoesNotContain("s3cret", sha.Serialize());

        var pbkdf2 = DevicePassword.CreatePbkdf2("1050:0407", "s3cret", iterations: 1000);
        Assert.StartsWith("pbkdf2:1000:", pbkdf2.Serialize());
        Assert.True(pbkdf2.Verify("s3cret"));
        Assert.False(pbkdf2.Verify("nope"));
        Assert.True(DevicePassword.TryParse("x", pbkdf2.Serialize(), out var roundTripped, out _));
        Assert.True(roundTripped.Verify("s3cret"));

        Assert.False(DevicePassword.TryParse("x", "sha256:nothex", out _, out var badHash));
        Assert.Contains("64 hexadecimal", badHash!);
        Assert.False(DevicePassword.TryParse("x", "pbkdf2:nope", out _, out var badPbkdf2));
        Assert.Contains("pbkdf2:", badPbkdf2!);
    }

    [Fact]
    public void The_password_store_matches_the_same_way_the_nickname_store_does()
    {
        var store = DevicePasswordStore.Empty.With(new DevicePassword("1050:0407", PasswordEncoding.Plaintext, "v"));
        Assert.True(store.HasPassword(TestSupport.SerialDevice(), "whatever"));
        Assert.True(store.Verify(TestSupport.SerialDevice(), "whatever", "v"));
        Assert.False(store.Verify(TestSupport.SerialDevice(), "whatever", null));
        Assert.False(store.Verify(TestSupport.SerialDevice(), "whatever", "w"));
        Assert.False(store.Verify(TestSupport.AnonymousDevice(), "t-1", "v"));
    }

    [Fact]
    public void A_password_too_long_for_the_documented_limit_is_an_error()
    {
        var directory = TestSupport.NewTempDirectory("pwlen");
        try
        {
            TestSupport.WriteConfig(directory, "[server]\npassword_max_length = 8\n\n[passwords]\n\"1050:0407\" = \"0123456789\"\n");
            var result = TestSupport.StoreFor(directory).Load();

            Assert.True(result.HasErrors);
            Assert.Contains(result.Errors, e => e.Code == ConfigDiagnosticCode.ValueOutOfRange && e.Message.Contains("8"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Auto_attach_requires_a_recorded_grant_and_refuses_new_devices()
    {
        var policy = new AutoAttachPolicy(true, qualifyByServerName: true, qualifyByInterface: false, 3, 30, 0);
        var grants = new[] { new AutoAttachGrant("s-1050-0407-0123456789AB", "SERVER-A", null, Now) };

        var granted = policy.Evaluate("s-1050-0407-0123456789AB", "SERVER-A", null, grants, false, false);
        Assert.True(granted.Attach);

        var otherServer = policy.Evaluate("s-1050-0407-0123456789AB", "SERVER-B", null, grants, false, false);
        Assert.False(otherServer.Attach);
        Assert.Contains("no auto-attach grant", otherServer.Reason);

        var newDevice = policy.Evaluate("s-9999-9999-FFFF", "SERVER-A", null, grants, false, false);
        Assert.False(newDevice.Attach);

        var passwordProtected = policy.Evaluate("s-1050-0407-0123456789AB", "SERVER-A", null, grants, true, false);
        Assert.False(passwordProtected.Attach);
        Assert.Contains("password protected", passwordProtected.Reason);
    }

    [Fact]
    public void Auto_attach_can_qualify_by_network_interface()
    {
        var policy = new AutoAttachPolicy(true, qualifyByServerName: false, qualifyByInterface: true, 0, 30, 0);
        var grants = new[] { new AutoAttachGrant("d1", null, "eth0", Now) };

        Assert.True(policy.Evaluate("d1", null, "eth0", grants, false, false).Attach);
        Assert.False(policy.Evaluate("d1", null, "wlan0", grants, false, false).Attach);
        Assert.False(policy.Evaluate("d1", null, null, grants, false, false).Attach);
    }

    [Fact]
    public void Disabled_auto_attach_reports_itself_as_disabled()
    {
        var policy = new AutoAttachPolicy(false, true, false, 0, 0, 0);
        var decision = policy.Evaluate("d", "s", null, new[] { new AutoAttachGrant("d", "s", null, Now) }, false, false);
        Assert.False(decision.Attach);
        Assert.Contains("disabled", decision.Reason);
    }
}

public sealed class MigrationTests
{
    [Fact]
    public void Legacy_single_level_keys_are_renamed_and_coerced()
    {
        var directory = TestSupport.NewTempDirectory("migrate");
        try
        {
            TestSupport.WriteConfig(directory, "ServerName = \"legacy\"\nTCPPort = \"4000\"\nAllowedDevices = \"1050:0407, 090c:1000\"\nAutoAttachToKernel = \"no\"\n");
            var result = TestSupport.StoreFor(directory).Load();

            Assert.False(result.HasErrors);
            Assert.Equal("legacy", result.Config.Server.Name);
            Assert.Equal(4000, result.Config.Transport.PlaintextPort);
            Assert.Equal(2, result.Config.Policy.Allow.Count);
            Assert.False(result.Config.Server.AutoAttachToKernel);
            Assert.Contains(result.Diagnostics, d => d.Code == ConfigDiagnosticCode.MigrationApplied && d.Path == "server.name");
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("config_version 0 to 1"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_current_version_file_is_not_migrated()
    {
        var table = new FlatTable();
        table.Set("general.config_version", 1L, ConfigOrigin.Defaults);
        table.Set("server.name", "x", ConfigOrigin.Defaults);

        Assert.Empty(ConfigMigration.Apply(table));
        Assert.Equal(1, ConfigMigration.ReadVersion(table));
    }

    [Fact]
    public void The_version_is_read_from_the_merged_table_even_when_it_is_a_string()
    {
        var table = new FlatTable();
        table.Set("general.config_version", "2", ConfigOrigin.Defaults);
        Assert.Equal(2, ConfigMigration.ReadVersion(table));
    }
}

public sealed class TomlWriterTests
{
    [Fact]
    public void Keys_that_are_not_bare_keys_are_quoted_and_round_trip()
    {
        var values = new List<KeyValuePair<string, object?>>
        {
            new("nicknames.1050:0407", "YubiKey"),
            new("nicknames.product:USB 2.0 Stick", "Stick \"with\" quotes\nand newline"),
            new("discovery.manual_servers", new List<object?> { "10.0.0.1:3240" }),
            new("auto_attach.grants#0.device_key", "s-1050-0407-0123456789AB"),
        };

        var text = TomlWriter.Write(values, "header line");
        Assert.Contains("[nicknames]", text);
        Assert.Contains("\"1050:0407\" = \"YubiKey\"", text);
        Assert.Contains("\\n", text);
        Assert.Contains("[[auto_attach.grants]]", text);

        var read = TomlLayerReader.ReadText(text, "state.toml", ConfigSourceKind.Overlay);
        Assert.False(read.HasErrors);

        var nicknames = read.Table.Map("nicknames");
        Assert.Equal("YubiKey", nicknames["1050:0407"].Value);
        Assert.Equal("Stick \"with\" quotes\nand newline", nicknames["product:USB 2.0 Stick"].Value);

        var grants = read.Table.TableArray("auto_attach.grants");
        Assert.Single(grants);
        Assert.Equal("s-1050-0407-0123456789AB", grants[0]["device_key"].Value);
    }

    [Fact]
    public void Writing_is_deterministic()
    {
        var values = new List<KeyValuePair<string, object?>>
        {
            new("b.key", 1L),
            new("a.key", "x"),
            new("m.a", true),
        };

        Assert.Equal(TomlWriter.Write(values), TomlWriter.Write(values.AsEnumerable().Reverse().ToList()));
    }

    [Fact]
    public void Null_values_are_omitted_rather_than_written_as_empty_strings()
    {
        var text = TomlWriter.Write(new List<KeyValuePair<string, object?>> { new("a.b", null), new("a.c", "keep") });
        Assert.DoesNotContain("b =", text);
        Assert.Contains("c = \"keep\"", text);
    }
}