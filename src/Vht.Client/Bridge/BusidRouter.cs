// SPDX-License-Identifier: Apache-2.0
//
// Busid routing: how a busid the UDE driver asks for maps back to a peer and that peer's own busid.
//
// Why this exists: the driver opens *one* USB/IP connection per attach, to a fixed (host, port) that
// we choose, and asks for a busid (ARCHITECTURE.md §3.2/§6.2). All attaches therefore point at the
// loopback bridge, and the bridge is what decides which remote peer a given busid belongs to. For
// one peer the key is simply the peer's busid; when two peers export the same busid, the key is
// qualified as `busid@peer-name` so both remain attachable at once.
//
// The `@` is safe here: it never reaches the USB/IP wire, it is only the string handed to the driver
// as the import busid and read back out of the OP_REQ_IMPORT the driver sends to the bridge.

using System.Collections.Concurrent;

namespace Vht.Client.Bridge;

/// <summary>One route: a busid the client will accept on the bridge, and where it really lives.</summary>
internal sealed record BusidRoute
{
    /// <summary>The key handed to the driver and read back out of the driver's OP_REQ_IMPORT.</summary>
    internal required string RoutingKey { get; init; }

    /// <summary>The peer's own busid, sent verbatim on the peer leg.</summary>
    internal required string RemoteBusId { get; init; }

    internal required Peer Peer { get; init; }

    public override string ToString() => $"{RoutingKey} -> {Peer.Name}:{RemoteBusId}";
}

/// <summary>
/// The route table. Rebuilt whenever the catalogs change (peer added/removed, devices arriving or
/// leaving). Deliberately free of any notion of how many routes exist beyond "all of them".
/// </summary>
internal sealed class BusidRouter
{
    private readonly ConcurrentDictionary<string, BusidRoute> _byKey = new(StringComparer.Ordinal);

    /// <summary>Every current route.</summary>
    internal IReadOnlyCollection<BusidRoute> Routes => _byKey.Values.ToArray();

    /// <summary>Replaces the whole table atomically enough for our purposes (readers copy on use).</summary>
    internal void Replace(IEnumerable<BusidRoute> routes)
    {
        var incoming = routes.ToArray();
        var keys = new HashSet<string>(incoming.Select(r => r.RoutingKey), StringComparer.Ordinal);

        foreach (string existing in _byKey.Keys)
        {
            if (!keys.Contains(existing))
            {
                _byKey.TryRemove(existing, out _);
            }
        }

        foreach (BusidRoute route in incoming)
        {
            _byKey[route.RoutingKey] = route;
        }
    }

    /// <summary>The default peer used for a bare OP_REQ_DEVLIST and for unqualified keys.</summary>
    internal Peer? DefaultPeer { get; set; }

    /// <summary>
    /// Resolves a routing key. Exact match first (that is the qualified form), then an unqualified
    /// busid that is unique across peers. An ambiguous busid is reported as such rather than guessed
    /// at — attaching the wrong physical device is worse than refusing.
    /// </summary>
    internal BusidResolution Resolve(string routingKey)
    {
        if (_byKey.TryGetValue(routingKey, out BusidRoute? exact))
        {
            return BusidResolution.Found(exact);
        }

        BusidRoute[] matches = _byKey.Values
            .Where(r => string.Equals(r.RemoteBusId, routingKey, StringComparison.Ordinal))
            .ToArray();

        return matches.Length switch
        {
            1 => BusidResolution.Found(matches[0]),
            0 => BusidResolution.NotFound(routingKey),
            _ => BusidResolution.Ambiguous(routingKey, matches.Select(m => m.Peer.Name)),
        };
    }

    /// <summary>
    /// Computes routing keys for a peer's device list, qualifying with `busid@peerName` only when the
    /// busid is not unique across peers, so the common case stays visually identical to the reference
    /// products' plain busid.
    /// </summary>
    internal static IReadOnlyList<BusidRoute> BuildRoutes(
        IEnumerable<(Peer Peer, string BusId)> devices,
        IEnumerable<Peer> allPeers)
    {
        var tuples = devices.ToArray();
        var duplicateBusIds = tuples
            .GroupBy(d => d.BusId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var used = new HashSet<string>(StringComparer.Ordinal);

        var routes = new List<BusidRoute>(tuples.Length);
        foreach ((Peer peer, string busId) in tuples)
        {
            string key = duplicateBusIds.Contains(busId) ? $"{busId}@{peer.Name}" : busId;
            if (!used.Add(key))
            {
                // Two peers with the same name, or a peer whose name collides with a qualified key.
                // Keep scanning for a free suffix rather than silently dropping a device.
                int suffix = 2;
                string candidate;
                do
                {
                    candidate = $"{key}#{suffix++}";
                }
                while (!used.Add(candidate));
                key = candidate;
            }

            routes.Add(new BusidRoute { RoutingKey = key, RemoteBusId = busId, Peer = peer });
        }

        _ = allPeers; // reserved: interface-qualified routes (ARCHITECTURE.md §7, config-workstream)
        return routes;
    }
}

/// <summary>Outcome of resolving a routing key.</summary>
internal readonly record struct BusidResolution
{
    internal BusidRoute? Route { get; private init; }
    internal string? NotFoundKey { get; private init; }
    internal string[]? AmbiguousPeerNames { get; private init; }

    internal bool IsFound => Route is not null;
    internal bool IsAmbiguous => AmbiguousPeerNames is not null;

    internal static BusidResolution Found(BusidRoute route) => new() { Route = route };

    internal static BusidResolution NotFound(string key) => new() { NotFoundKey = key };

    internal static BusidResolution Ambiguous(string key, IEnumerable<string> peers) =>
        new() { NotFoundKey = key, AmbiguousPeerNames = peers.ToArray() };

    internal string Describe() => IsFound
        ? $"route {Route}"
        : IsAmbiguous
            ? $"busid '{NotFoundKey}' is ambiguous; it exists on: {string.Join(", ", AmbiguousPeerNames!)}. " +
              "Attach the qualified key instead."
            : $"no peer exports busid '{NotFoundKey}'.";
}
