using Vht.Config.Toml;

namespace Vht.Config.DeviceIdentity;

public sealed record DeviceIdentity(DeviceKey Key, DeviceDescriptor Descriptor, bool AdoptedFromMap, string Reason)
{
    public bool IsAdopted => AdoptedFromMap;

    public override string ToString() => Key.Value;
}

/// <summary>
/// Turns a snapshot of descriptors into stable keys. Deterministic for a given snapshot: the
/// disambiguator assigned to an ambiguous key depends only on the sorted port paths, so a replug into
/// the same port keeps the same suffix and a replug into a different port is repaired by the map.
/// </summary>
public sealed class DeviceIdentityResolver
{
    private readonly DeviceIdMap _map;

    public DeviceIdentityResolver(DeviceIdMap map) => _map = map;

    public DeviceIdMap Map => _map;

    /// <summary>Resolve one snapshot. <paramref name="learn"/> persists new sightings into the map.</summary>
    public IReadOnlyList<DeviceIdentity> Resolve(IReadOnlyList<DeviceDescriptor> descriptors, DateTimeOffset now, bool learn = true)
    {
        // The keys this snapshot claims on its own terms (serial, else fingerprint, else port path).
        // They decide whether a device that reappears on another port may be mapped onto a persisted
        // entry: see DeviceIdMap.TryAdoptOrphan.
        var presentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            presentKeys.Add(DirectKey(descriptor).Value);
        }

        var provisional = new List<(DeviceDescriptor Descriptor, DeviceKey Key, bool Adopted, string Reason)>(descriptors.Count);

        foreach (var descriptor in descriptors)
        {
            provisional.Add(ResolveOne(descriptor, presentKeys, now, learn));
        }

        // Disambiguate collisions: two identical serial-less sticks enumerate with the same
        // fingerprint. Order by port path so the assignment is stable per port.
        var result = new List<DeviceIdentity>(provisional.Count);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (descriptor, key, adopted, reason) in provisional
                     .OrderBy(p => p.Descriptor.PortPath ?? p.Descriptor.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            var finalKey = key;
            var note = reason;
            if (seen.TryGetValue(key.Value, out var count))
            {
                seen[key.Value] = count + 1;
                finalKey = key.WithDisambiguator(count + 1);
                note += $"; disambiguated as {finalKey.Value} (another device in this snapshot has the same key)";
            }
            else
            {
                seen[key.Value] = 1;
            }

            if (learn && !string.Equals(finalKey.Value, key.Value, StringComparison.Ordinal))
            {
                // remember the derived key too, so the disambiguated identity survives a replug
                // onto the same port.
                var entry = _map.Observe(key.Value, descriptor, now);
                if (finalKey.Disambiguator > 0)
                {
                    _map.AddOrReplace(entry with
                    {
                        Aliases = entry.Aliases.Concat(new[] { finalKey.Value }).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList(),
                    });
                }
            }

            presentKeys.Add(finalKey.Value);
            result.Add(new DeviceIdentity(finalKey, descriptor, adopted, note));
        }

        return result;
    }

    private (DeviceDescriptor Descriptor, DeviceKey Key, bool Adopted, string Reason) ResolveOne(
        DeviceDescriptor descriptor,
        IReadOnlyCollection<string> presentKeys,
        DateTimeOffset now,
        bool learn)
    {
        // Tier 0: serial number. Strongest and cheapest.
        var serial = descriptor.UsableSerial;
        if (serial is not null)
        {
            var key = DeviceKey.ForSerial(descriptor, serial);
            if (learn)
            {
                var entry = _map.Observe(key.Value, descriptor, now);
                if (!string.Equals(entry.Key, key.Value, StringComparison.Ordinal))
                {
                    return (descriptor, DeviceKey.Parse(entry.Key), true, $"serial number, adopted persisted key {entry.Key}");
                }
            }

            return (descriptor, key, false, "serial number");
        }

        // Tier 1: fingerprint over vid/pid/bcdDevice + strings.
        var hasFingerprintMaterial = !string.IsNullOrWhiteSpace(descriptor.Product)
            || !string.IsNullOrWhiteSpace(descriptor.Manufacturer)
            || !string.IsNullOrWhiteSpace(descriptor.ClassTriple);

        if (hasFingerprintMaterial)
        {
            var key = DeviceKey.ForFingerprint(descriptor);
            if (_map.TryResolveAlias(key.Value, out var mapped))
            {
                var adoptedEntry = learn ? _map.Observe(mapped.Key, descriptor, now) : mapped;

                // The fingerprint alone derived this key, so the identity counts as "adopted" only when
                // the persisted map is what changed it.
                if (!string.Equals(adoptedEntry.Key, key.Value, StringComparison.Ordinal))
                {
                    return (descriptor, DeviceKey.Parse(adoptedEntry.Key), true, $"fingerprint, adopted persisted key {adoptedEntry.Key}");
                }
            }
            else if (learn)
            {
                var entry = _map.Observe(key.Value, descriptor, now);
                if (!string.Equals(entry.Key, key.Value, StringComparison.Ordinal))
                {
                    return (descriptor, DeviceKey.Parse(entry.Key), true, $"fingerprint, adopted persisted key {entry.Key}");
                }
            }

            return (descriptor, key, false, "fingerprint (device has no serial number)");
        }

        // Tier 2: nothing but the port path. Try the map first (adoption across a port change), then
        // fall back to the port-path key.
        var topologyKey = DeviceKey.ForTopology(descriptor);
        if (_map.TryResolveAlias(topologyKey.Value, out var known))
        {
            if (learn)
            {
                known = _map.Observe(known.Key, descriptor, now);
            }

            return (descriptor, DeviceKey.Parse(known.Key), true, $"port path, adopted persisted key {known.Key}");
        }

        if (_map.TryAdoptOrphan(descriptor, presentKeys, now, out var orphan))
        {
            return (descriptor, DeviceKey.Parse(orphan.Key), true, $"serial-less device with no strings; adopted the only missing same-model entry {orphan.Key} (port path changed)");
        }

        if (learn)
        {
            _map.Observe(topologyKey.Value, descriptor, now);
        }

        return (descriptor, topologyKey, false, "port path only (device reports no serial number and no strings); identity is stable only while the device stays on the same port");
    }

    /// <summary>The key this descriptor yields on its own, without consulting the persisted map. Used to
    /// tell "this snapshot owns this key" apart from "the map told us this key".</summary>
    private static DeviceKey DirectKey(DeviceDescriptor descriptor)
    {
        if (descriptor.UsableSerial is { } serial)
        {
            return DeviceKey.ForSerial(descriptor, serial);
        }

        var hasFingerprintMaterial = !string.IsNullOrWhiteSpace(descriptor.Product)
            || !string.IsNullOrWhiteSpace(descriptor.Manufacturer)
            || !string.IsNullOrWhiteSpace(descriptor.ClassTriple);

        return hasFingerprintMaterial ? DeviceKey.ForFingerprint(descriptor) : DeviceKey.ForTopology(descriptor);
    }
}