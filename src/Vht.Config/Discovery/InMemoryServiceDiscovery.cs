using Vht.Config.Diagnostics;

namespace Vht.Config.Discovery;

/// <summary>
/// In-memory DNS-SD engine: a registry that behaves like a LAN with no network. It is a shipped
/// component (not test-only) so a dev host without DNSAPI, and the acceptance harness, can exercise the
/// full discovery code path - register, browse, resolve, unregister, TXT decoding - deterministically.
/// </summary>
public sealed class InMemoryServiceDiscovery : IServiceDiscoveryProvider
{
    private readonly Dictionary<string, ServiceInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _spki = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "in-memory";

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public int RegisteredCount => _instances.Count;

    public Task RegisterAsync(ServiceInstance instance, CancellationToken cancellationToken = default)
    {
        _instances[instance.FullName] = instance with { Origin = DiscoveryOriginKind.Discovered };
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ServiceInstance instance, CancellationToken cancellationToken = default)
    {
        _instances[instance.FullName] = instance with { Origin = DiscoveryOriginKind.Discovered };
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        foreach (var key in _instances.Keys.Where(k => k.StartsWith(instanceName + ".", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _instances.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ServiceInstance>> BrowseAsync(string serviceType, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var matches = _instances.Values
            .Where(i => string.Equals(i.ServiceType, serviceType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.InstanceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<ServiceInstance>>(matches);
    }

    public Task<ServiceInstance?> ResolveAsync(string serviceType, string instanceName, CancellationToken cancellationToken = default)
    {
        var key = ServiceTypes.InstanceFullName(instanceName, serviceType);
        return Task.FromResult(_instances.TryGetValue(key, out var instance) ? instance : null);
    }

    /// <summary>Simulate a peer appearing on the LAN.</summary>
    public void AddRemoteInstance(ServiceInstance instance) =>
        _instances[instance.FullName] = instance with { Origin = DiscoveryOriginKind.Discovered };

    public void RemoveRemoteInstance(string serviceType, string instanceName) =>
        _instances.Remove(ServiceTypes.InstanceFullName(instanceName, serviceType));

    public ValueTask DisposeAsync()
    {
        _instances.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Merges what discovery found with the operator's manual <c>host:port</c> entries. Manual entries are
/// never dropped - that is the "manual host entry must still work" requirement - and a server that is
/// both discovered and listed manually appears once, preferring the discovered metadata (server name,
/// pin, capabilities) while keeping the origin that proves it was also entered by hand.
/// </summary>
public sealed class DiscoveryCoordinator
{
    private readonly IServiceDiscoveryProvider? _provider;
    private readonly IReadOnlyList<string> _manualServers;
    private readonly string _serviceType;

    public DiscoveryCoordinator(IServiceDiscoveryProvider? provider, IReadOnlyList<string> manualServers, string serviceType = ServiceTypes.Vht)
    {
        _provider = provider;
        _manualServers = manualServers;
        _serviceType = serviceType;
    }

    public async Task<DiscoverySnapshot> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var servers = new List<DiscoveredServer>();

        if (_provider is not null && _provider.IsSupported)
        {
            try
            {
                var discovered = await _provider.BrowseAsync(_serviceType, duration, cancellationToken).ConfigureAwait(false);
                foreach (var instance in discovered)
                {
                    if (instance.Txt.RecordVersion > ServiceTextRecord.CurrentVersion)
                    {
                        diagnostics.Add(new ConfigDiagnostic(
                            ConfigDiagnosticSeverity.Warning,
                            ConfigDiagnosticCode.InvalidValue,
                            $"'{instance.InstanceName}' advertises TXT record version {instance.Txt.RecordVersion}, which this build does not understand; treating unknown fields as absent"));
                    }

                    servers.Add(new DiscoveredServer(
                        instance.HostName,
                        instance.Port,
                        instance.InstanceName,
                        instance.Txt.ServerName,
                        instance.Txt.TlsEnabled,
                        instance.Spki,
                        instance.Txt.Capabilities,
                        DiscoveryOriginKind.Discovered,
                        instance));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ConfigDiagnostic(
                    ConfigDiagnosticSeverity.Warning,
                    ConfigDiagnosticCode.ReadError,
                    $"discovery failed ({ex.GetType().Name}: {ex.Message}); falling back to manual host entries"));
            }
        }
        else if (_provider is not null)
        {
            diagnostics.Add(new ConfigDiagnostic(
                ConfigDiagnosticSeverity.Warning,
                ConfigDiagnosticCode.PathInvalid,
                $"DNS-SD is unavailable on this host ({_provider.UnsupportedReason ?? "unknown reason"}); manual host entries still work"));
        }

        foreach (var manual in _manualServers)
        {
            if (!ManualServerParser.TryParse(manual, out var endpoint, out var error))
            {
                diagnostics.Add(new ConfigDiagnostic(
                    ConfigDiagnosticSeverity.Error,
                    ConfigDiagnosticCode.InvalidValue,
                    $"manual server '{manual}' could not be parsed: {error}",
                    "discovery.manual_servers"));
                continue;
            }

            var existing = servers.FirstOrDefault(s =>
                s.Port == endpoint!.Port
                && (string.Equals(s.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)
                    || s.Instance?.Addresses.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase) == true));

            if (existing is not null)
            {
                continue;
            }

            servers.Add(new DiscoveredServer(
                endpoint!.Host,
                endpoint.Port,
                null,
                null,
                false,
                null,
                Array.Empty<string>(),
                DiscoveryOriginKind.Manual,
                null));
        }

        return new DiscoverySnapshot(
            servers.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Endpoint, StringComparer.OrdinalIgnoreCase).ToList(),
            diagnostics);
    }
}

/// <summary>
/// Builds the advertisement for a server role. Kept here (not in the server) so both the server and its
/// tests use one definition of what is published.
/// </summary>
public static class ServiceAdvertisement
{
    public static ServiceInstance ForServer(
        string serverName,
        string hostName,
        int port,
        bool tlsEnabled,
        string? spki,
        IEnumerable<string> capabilities,
        string? instanceName = null,
        string serviceType = ServiceTypes.Vht,
        IReadOnlyList<string>? addresses = null,
        DiscoveryOriginKind origin = DiscoveryOriginKind.Discovered)
    {
        var name = string.IsNullOrWhiteSpace(instanceName) ? serverName : instanceName!;
        return new ServiceInstance(
            name,
            hostName,
            port,
            ServiceTextRecord.Create(serverName, tlsEnabled, spki, capabilities),
            serviceType,
            addresses ?? Array.Empty<string>(),
            origin);
    }
}