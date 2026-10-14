// SPDX-License-Identifier: Apache-2.0
//
// The client's own peer list, phase 1: a JSON file the user edits through the CLI/UI, plus the
// discovery seam.
//
// ARCHITECTURE.md §7 makes DNS-SD (`_vht._tcp`, Windows DNSAPI) the discovery path, owned by
// Vht.Config (card config-workstream), and makes manual `host:port` entry permanent and always available.
// This file implements the manual half, which is what the card needs, and leaves an
// `IPeerDiscovery` seam with a documented no-op default so the config card's DNS-SD implementation
// drops in without the client changing. Nothing here invents a discovery protocol.

using System.Text.Json;
using System.Text.Json.Serialization;
using Vht.Client.Diagnostics;
using Vht.Client.Usbip;

namespace Vht.Client.Peers;

/// <summary>Discovery seam. The default implementation discovers nothing and says so.</summary>
internal interface IPeerDiscovery
{
    /// <summary>A short description of the mechanism, for `peers.list` and logs.</summary>
    string Description { get; }

    /// <summary>Returns peers found by discovery. An empty list is a normal outcome, not an error.</summary>
    Task<IReadOnlyList<Peer>> DiscoverAsync(CancellationToken ct);
}

/// <summary>
/// Placeholder until DNS-SD (`_vht._tcp`) browsing is wired in from Vht.Config. Chosen over silently
/// pretending to discover: the client's logs and `peers.list` both report that discovery is disabled.
/// </summary>
internal sealed class NullPeerDiscovery : IPeerDiscovery
{
    internal static readonly NullPeerDiscovery Instance = new();

    public string Description => "disabled (DNS-SD `_vht._tcp` browse is not wired in yet; use `peers add`)";

    public Task<IReadOnlyList<Peer>> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Peer>>(Array.Empty<Peer>());
    }
}

/// <summary>Persisted peer list. Adding a peer never removes or reorders existing ones.</summary>
internal sealed class PeerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly ClientLog _log;
    private List<Peer> _peers = new();

    internal PeerStore(string path, ClientLog log)
    {
        _path = path;
        _log = log;
        Load();
    }

    internal string Path => _path;

    /// <summary>Every configured peer, in file order.</summary>
    internal IReadOnlyList<Peer> Peers
    {
        get
        {
            lock (_sync) return _peers.ToArray();
        }
    }

    /// <summary>Adds a peer, or replaces the entry with the same name (a rename is an edit, not a second peer).</summary>
    internal void Add(Peer peer)
    {
        lock (_sync)
        {
            _peers.RemoveAll(p => string.Equals(p.Name, peer.Name, StringComparison.OrdinalIgnoreCase));
            _peers.Add(peer);
            Save();
        }
        _log.Info("peers", $"added peer '{peer}'");
    }

    /// <summary>Removes a peer by name. Returns false when there was nothing to remove.</summary>
    internal bool Remove(string name)
    {
        bool removed;
        lock (_sync)
        {
            removed = _peers.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
        }
        if (removed) _log.Info("peers", $"removed peer '{name}'");
        return removed;
    }

    /// <summary>Replaces the whole list (used when a discovery result is merged by the caller).</summary>
    internal void ReplaceAll(IEnumerable<Peer> peers)
    {
        lock (_sync)
        {
            _peers = peers.ToList();
            Save();
        }
    }

    /// <summary>
    /// Parses `host[:port]`, the form the architecture says must always work (`vht-client add host:port`).
    /// A bare peer name is accepted too, so `add lab-pc` means "lab-pc at the default port".
    /// </summary>
    internal static Peer Parse(string spec, bool useTls = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        string? explicitName = null;
        string host;
        int port = 3240;

        int at = spec.IndexOf('@', StringComparison.Ordinal);
        string remainder = spec;
        if (at > 0)
        {
            explicitName = spec[..at];
            remainder = spec[(at + 1)..];
        }

        int colon = remainder.LastIndexOf(':');
        if (colon > 0 && int.TryParse(remainder[(colon + 1)..], out int parsedPort))
        {
            host = remainder[..colon];
            port = parsedPort;
        }
        else
        {
            host = remainder;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException($"'{spec}' has no host part; use host, host:port or name@host:port.", nameof(spec));
        }

        return new Peer
        {
            Name = explicitName ?? host,
            Host = host,
            Port = port,
            UseTls = useTls,
        };
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _peers = new List<Peer>();
                return;
            }

            List<PeerDto>? loaded = JsonSerializer.Deserialize<List<PeerDto>>(File.ReadAllText(_path), JsonOptions);
            _peers = (loaded ?? new List<PeerDto>()).Select(d => d.ToPeer()).ToList();
            _log.Debug("peers", $"loaded {_peers.Count} peer(s) from {_path}");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt peer file must not stop the client from starting; it starts empty and says so.
            _peers = new List<Peer>();
            _log.Error("peers", $"could not read '{_path}'; starting with no peers. Fix or delete the file.", ex);
        }
    }

    private void Save()
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(_path, JsonSerializer.Serialize(_peers.Select(PeerDto.From), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("peers", $"could not write '{_path}'", ex);
        }
    }

    /// <summary>
    /// On-disk shape. A DTO rather than serialising <see cref="Peer"/> directly, because System.Text.Json
    /// only sees public members and the domain record deliberately keeps its members internal (the type
    /// itself is internal, so this is about honouring the serialiser's rules, not about encapsulation
    /// theatre).
    /// </summary>
    private sealed record PeerDto
    {
        public string Name { get; init; } = string.Empty;

        public string Host { get; init; } = string.Empty;

        public int Port { get; init; } = 3241;

        public bool UseTls { get; init; } = true;

        public string? SpkiPin { get; init; }

        public bool Manual { get; init; } = true;

        internal static PeerDto From(Peer peer) => new()
        {
            Name = peer.Name,
            Host = peer.Host,
            Port = peer.Port,
            UseTls = peer.UseTls,
            SpkiPin = peer.SpkiPin,
            Manual = peer.Manual,
        };

        internal Peer ToPeer() => new()
        {
            Name = Name,
            Host = Host,
            Port = Port,
            UseTls = UseTls,
            SpkiPin = SpkiPin,
            Manual = Manual,
        };
    }
}
