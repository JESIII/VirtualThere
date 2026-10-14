using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vht.Config.Diagnostics;
using Vht.Config.Discovery;
using Vht.Config.Interop;
using Xunit;

namespace Vht.Config.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public void Service_type_must_be_a_dns_sd_type_and_reference_products_service_type_is_rejected()
    {
        Assert.True(ServiceTypes.ValidateServiceType("_vht._tcp", out var ok));
        Assert.Null(ok);
        Assert.True(ServiceTypes.ValidateServiceType("_vht._tcp.local", out _));

        Assert.False(ServiceTypes.ValidateServiceType("_vhusb._tcp", out _));
        Assert.False(ServiceTypes.ValidateServiceType("vht", out _));
        Assert.False(ServiceTypes.ValidateServiceType("_vht._sctp.local", out _));
        Assert.False(ServiceTypes.ValidateServiceType("_vht._tcp.example.com", out _));
        Assert.False(ServiceTypes.ValidateServiceType("", out _));
    }

    [Fact]
    public void The_configured_service_type_is_validated_and_reference_products_service_type_is_an_error()
    {
        var directory = TestSupport.NewTempDirectory("svctype");
        try
        {
            TestSupport.WriteConfig(directory, "[discovery]\nservice_type = \"_vhusb._tcp.local\"\n");
            var rejected = TestSupport.StoreFor(directory).Load();
            Assert.True(rejected.HasErrors);
            Assert.Contains(rejected.Errors, e => e.Path == "discovery.service_type" && e.Message.Contains("reserved service type"));

            TestSupport.WriteConfig(directory, "[discovery]\nservice_type = \"_vhusbx._tcp.local\"\n");
            Assert.True(TestSupport.StoreFor(directory).Load().HasErrors);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Txt_records_carry_the_frozen_keys()
    {
        var record = ServiceTextRecord.Create("WORKSHOP", tls: true, spki: "abc", capabilities: new[] { "lz4", "tls1.3" });

        Assert.Equal(1, record.RecordVersion);
        Assert.Equal("WORKSHOP", record.ServerName);
        Assert.True(record.TlsEnabled);
        Assert.Equal("abc", record.Spki);
        Assert.Equal(new[] { "lz4", "tls1.3" }, record.Capabilities);
        Assert.Contains("v=1", record.ToDescriptor());
        Assert.Contains("name=WORKSHOP", record.ToDescriptor());
        Assert.Contains("caps=lz4,tls1.3", record.ToDescriptor());

        var parsed = ServiceTextRecord.Parse(new Dictionary<string, string> { ["v"] = "1", ["name"] = "X", ["tls"] = "0" });
        Assert.Equal("X", parsed.ServerName);
        Assert.False(parsed.TlsEnabled);
        Assert.Empty(parsed.Capabilities);
    }

    [Theory]
    [InlineData("192.168.1.20:3241", "192.168.1.20", 3241)]
    [InlineData("192.168.1.20", "192.168.1.20", 3240)]
    [InlineData("[fe80::1]:3240", "fe80::1", 3240)]
    [InlineData("fe80::1", "fe80::1", 3240)]
    [InlineData(" server.local:3241 ", "server.local", 3241)]
    public void Manual_host_entries_parse_with_a_sane_default_port(string text, string host, int port)
    {
        Assert.True(ManualServerParser.TryParse(text, out var endpoint, out var error), error);
        Assert.Equal(host, endpoint!.Host);
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    [InlineData("host:0")]
    [InlineData("host:99999")]
    [InlineData("host:port")]
    [InlineData(":3240")]
    [InlineData("[fe80::1")]
    public void Malformed_manual_host_entries_are_refused_with_a_reason(string text)
    {
        Assert.False(ManualServerParser.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Malformed_manual_entries_in_the_config_are_errors()
    {
        var directory = TestSupport.NewTempDirectory("manual");
        try
        {
            TestSupport.WriteConfig(directory, "[discovery]\nmanual_servers = [\"good:3240\", \"bad:port\"]\n");
            var result = TestSupport.StoreFor(directory).Load();

            Assert.True(result.HasErrors);
            var error = result.Errors.Single();
            Assert.Contains("bad:port", error.Message);
            Assert.True(error.Location.Line > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task In_memory_discovery_registers_browses_resolves_and_unregisters()
    {
        await using var provider = new InMemoryServiceDiscovery();
        var instance = TestSupport.Instance("ALPHA");

        await provider.RegisterAsync(instance);
        var found = await provider.BrowseAsync(ServiceTypes.Vht, TimeSpan.FromMilliseconds(10));
        Assert.Single(found);
        Assert.Equal("ALPHA", found[0].ServerName);
        Assert.Equal("testsrv.local:3240", found[0].Endpoint);

        Assert.NotNull(await provider.ResolveAsync(ServiceTypes.Vht, "ALPHA"));
        Assert.Empty(await provider.BrowseAsync("_other._tcp", TimeSpan.FromMilliseconds(10)));

        await provider.UnregisterAsync("ALPHA");
        Assert.Empty(await provider.BrowseAsync(ServiceTypes.Vht, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public async Task The_coordinator_merges_discovered_and_manual_servers_without_dropping_manual_entries()
    {
        await using var provider = new InMemoryServiceDiscovery();
        provider.AddRemoteInstance(TestSupport.Instance("PEER", port: 3241, spki: "pin-value"));

        // the first entry names the same endpoint the discovered server answers on: it must not show up
        // twice (and the second entry proves a manual-only server is never dropped).
        var coordinator = new DiscoveryCoordinator(provider, new[] { "testsrv.local:3241", "10.9.9.9:3240" });
        var snapshot = await coordinator.DiscoverAsync(TimeSpan.FromMilliseconds(10));

        Assert.Equal(2, snapshot.Servers.Count);
        Assert.Contains(snapshot.Servers, s => s.Origin == DiscoveryOriginKind.Discovered && s.ServerName == "PEER");
        var manualOnly = snapshot.Servers.Single(s => s.Origin == DiscoveryOriginKind.Manual);
        Assert.Equal("10.9.9.9:3240", manualOnly.Endpoint);
        Assert.Equal("10.9.9.9:3240", manualOnly.DisplayName);
    }

    [Fact]
    public async Task Discovery_failure_degrades_to_manual_entries_rather_than_failing()
    {
        var coordinator = new DiscoveryCoordinator(null, new[] { "10.0.0.1" });
        var snapshot = await coordinator.DiscoverAsync(TimeSpan.FromMilliseconds(10));

        var server = Assert.Single(snapshot.Servers);
        Assert.Equal(DiscoveryOriginKind.Manual, server.Origin);
        Assert.Equal(3240, server.Port);
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public void The_spki_pin_is_base64url_sha256_of_the_subject_public_key_info()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=virtualthere.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var der = certificate.Export(X509ContentType.Cert);

        Assert.True(SpkiPin.TryFromCertificate(der, out var pin, out var error), error);
        Assert.NotNull(pin);
        Assert.True(SpkiPin.LooksLikePin(pin));

        var expected = Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        Assert.Equal(expected, pin);

        Assert.False(SpkiPin.TryFromCertificate(new byte[] { 1, 2, 3 }, out _, out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Fact]
    public void The_windows_provider_explains_why_it_is_unavailable_instead_of_throwing()
    {
        var provider = new WindowsDnsSdProvider();
        if (OperatingSystem.IsWindows())
        {
            // Either DNSAPI is present (pin claim and move on) or the reason is spelled out.
            Assert.True(provider.IsSupported || !string.IsNullOrWhiteSpace(provider.UnsupportedReason));
        }
        else
        {
            Assert.False(provider.IsSupported);
            Assert.Contains("Windows", provider.UnsupportedReason!);
        }
    }

    /// <summary>
    /// Live DNS-SD self-test. Registers _vht._tcp on this host and tries to discover it again through
    /// the in-box responder. Opt-in because it needs a usable network interface: run with
    /// <c>VHT_DISCOVERY_SELFTEST=1 dotnet test</c>. The result (including a failing DNS_STATUS) is the
    /// evidence for the discovery deliverable; cross-machine discovery cannot be shown from one host.
    /// </summary>
    [Fact]
    public async Task Windows_dnsapi_can_register_and_browse_this_host()
    {
        if (Environment.GetEnvironmentVariable("VHT_DISCOVERY_SELFTEST") != "1")
        {
            return; // NOT RUN: opt-in only (documented in docs/CONFIG.md)
        }

        await using var provider = new WindowsDnsSdProvider();
        Assert.True(provider.IsSupported, "DNSAPI is not usable on this host: " + provider.UnsupportedReason);

        var instance = ServiceAdvertisement.ForServer(
            serverName: "SELFTEST",
            hostName: Environment.MachineName,
            port: 3240,
            tlsEnabled: true,
            spki: null,
            capabilities: new[] { "lz4" },
            instanceName: "SELFTEST-" + Environment.ProcessId);

        await provider.RegisterAsync(instance);
        try
        {
            var found = await provider.BrowseAsync(ServiceTypes.Vht, TimeSpan.FromSeconds(6));
            Assert.True(found.Count > 0, "registered _vht._tcp but nothing was discovered - the in-box mDNS responder did not answer");

            var self = found.FirstOrDefault(f => f.InstanceName.StartsWith("SELFTEST-", StringComparison.Ordinal));
            Assert.NotNull(self);

            // The browse callback delivers a DNS_RECORD chain; these assertions are what prove the
            // PTR -> SRV -> TXT join actually worked rather than the name alone coming through.
            Assert.Equal(3240, self!.Port);
            Assert.Equal("SELFTEST", self.ServerName);
            Assert.True(self.TlsEnabled);
            Assert.Contains("lz4", self.Txt.Capabilities);
            Assert.Equal(ServiceTextRecord.CurrentVersion, self.Txt.RecordVersion);
            Assert.False(string.IsNullOrWhiteSpace(self.HostName));

            // resolve() goes through the same browse and must find the same instance
            var resolved = await provider.ResolveAsync(ServiceTypes.Vht, self.InstanceName);
            Assert.NotNull(resolved);
        }
        finally
        {
            await provider.UnregisterAsync(instance.InstanceName);
        }
    }
}