using System.Globalization;
using Vht.Config.Diagnostics;
using Vht.Config.Toml;

namespace Vht.Config.DeviceIdentity;

/// <summary>
/// One persisted identity mapping. This is the reference product's <c>DeviceIdMap</c>: the file that lets a
/// serial-less device keep the same key when it is moved to another hub port.
/// </summary>
public sealed record DeviceIdMapEntry(
    string Key,
    ushort VendorId,
    ushort ProductId,
    ushort BcdDevice,
    string? Serial,
    string? Fingerprint,
    string? LastPortPath,
    IReadOnlyList<string> Aliases,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    string? Product,
    string? Manufacturer)
{
    /// <summary>Identity signature used for orphan adoption: two sighting are "the same kind of
    /// device" when vid/pid/bcdDevice and the human-readable strings agree.</summary>
    public bool SameKindAs(DeviceDescriptor descriptor) =>
        VendorId == descriptor.VendorId
        && ProductId == descriptor.ProductId
        && BcdDevice == descriptor.BcdDevice
        && string.Equals(Fingerprint, DeviceKey.FingerprintOf(descriptor), StringComparison.Ordinal);

    public DeviceIdMapEntry WithLastSeen(DeviceDescriptor descriptor, DateTimeOffset now)
    {
        var aliases = new HashSet<string>(Aliases, StringComparer.Ordinal) { Key };
        var topology = DeviceKey.ForTopology(descriptor).Value;
        aliases.Add(topology);
        if (descriptor.UsableSerial is { } serial)
        {
            aliases.Add(DeviceKey.ForSerial(descriptor, serial).Value);
        }

        aliases.Add(DeviceKey.ForFingerprint(descriptor).Value);
        aliases.Remove(Key);

        return this with
        {
            LastSeenUtc = now,
            LastPortPath = descriptor.PortPath ?? LastPortPath,
            Serial = descriptor.UsableSerial ?? Serial,
            Product = descriptor.Product ?? Product,
            Manufacturer = descriptor.Manufacturer ?? Manufacturer,
            Aliases = aliases.OrderBy(a => a, StringComparer.Ordinal).ToList(),
        };
    }
}

/// <summary>
/// The persisted identity map, with the lookup/adoption rules that make identity stable.
/// Persistence lives here (<see cref="Load"/> / <see cref="Write"/>) so the state file is written by
/// one component; the store decides when to flush.
/// </summary>
public sealed class DeviceIdMap
{
    private readonly Dictionary<string, DeviceIdMapEntry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliasToKey = new(StringComparer.Ordinal);

    public DeviceIdMap()
    {
    }

    public DeviceIdMap(IEnumerable<DeviceIdMapEntry> entries)
    {
        foreach (var entry in entries)
        {
            AddOrReplace(entry);
        }
    }

    public int Count => _byKey.Count;

    public IReadOnlyCollection<DeviceIdMapEntry> Entries => _byKey.Values;

    public IReadOnlyDictionary<string, string> AliasIndex => _aliasToKey;

    public bool TryGet(string key, out DeviceIdMapEntry entry) => _byKey.TryGetValue(key, out entry!);

    /// <summary>Resolve any key form (primary or alias) to the entry that owns it.</summary>
    public bool TryResolveAlias(string keyOrAlias, out DeviceIdMapEntry entry)
    {
        if (_byKey.TryGetValue(keyOrAlias, out entry!))
        {
            return true;
        }

        if (_aliasToKey.TryGetValue(keyOrAlias, out var primary) && _byKey.TryGetValue(primary, out entry!))
        {
            return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>
    /// Learn a sighting. Returns the entry now owning this key (existing or new) so the caller can
    /// return a key that is stable across runs.
    /// </summary>
    public DeviceIdMapEntry Observe(string key, DeviceDescriptor descriptor, DateTimeOffset now)
    {
        if (TryResolveAlias(key, out var existing))
        {
            var updated = existing.WithLastSeen(descriptor, now);
            if (!string.Equals(updated.Key, key, StringComparison.Ordinal))
            {
                // the alias form was seen again; keep the primary key, learn the alias.
                updated = updated with { Aliases = updated.Aliases.Concat(new[] { key }).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList() };
            }

            AddOrReplace(updated);
            return updated;
        }

        var entry = new DeviceIdMapEntry(
            Key: key,
            VendorId: descriptor.VendorId,
            ProductId: descriptor.ProductId,
            BcdDevice: descriptor.BcdDevice,
            Serial: descriptor.UsableSerial,
            Fingerprint: DeviceKey.FingerprintOf(descriptor),
            LastPortPath: descriptor.PortPath,
            Aliases: BuildAliases(key, descriptor),
            FirstSeenUtc: now,
            LastSeenUtc: now,
            Product: descriptor.Product,
            Manufacturer: descriptor.Manufacturer);

        AddOrReplace(entry);
        return entry;
    }

    /// <summary>
    /// The documented heuristic for a device that exposes nothing but its port path: when it reappears
    /// on a *different* port, no fingerprint can match it. If exactly one previously-known entry of the
    /// same kind (vid/pid/bcdDevice + strings) is missing from the current snapshot, we conclude the
    /// device moved and adopt that entry's key. Ambiguity (0 or &gt;1 candidate) means no adoption and a
    /// new key - reported to the caller as such rather than guessed.
    /// </summary>
    public bool TryAdoptOrphan(DeviceDescriptor descriptor, IReadOnlyCollection<string> presentKeys, DateTimeOffset now, out DeviceIdMapEntry adopted)
    {
        var sameKind = _byKey.Values.Where(e => e.SameKindAs(descriptor)).ToList();

        // Two identical units are indistinguishable. When another unit of this kind is attached in the
        // same snapshot, mapping the new sighting onto a missing entry would be a guess about which
        // physical unit moved, so it is refused (the caller reports a new key instead).
        if (sameKind.Any(e => presentKeys.Contains(e.Key)))
        {
            adopted = null!;
            return false;
        }

        var candidates = sameKind
            .Where(e => !presentKeys.Contains(e.Key))
            .Where(e => string.Equals(e.LastPortPath, descriptor.PortPath, StringComparison.OrdinalIgnoreCase) == false)
            .ToList();

        if (candidates.Count != 1)
        {
            adopted = null!;
            return false;
        }

        adopted = candidates[0].WithLastSeen(descriptor, now);
        AddOrReplace(adopted);
        return true;
    }

    /// <summary>Drop entries not seen for <paramref name="maxAge"/>. Keeps the map from growing forever.</summary>
    public int Prune(DateTimeOffset now, TimeSpan maxAge)
    {
        var stale = _byKey.Values.Where(e => now - e.LastSeenUtc > maxAge).Select(e => e.Key).ToList();
        foreach (var key in stale)
        {
            _byKey.Remove(key);
            foreach (var alias in _aliasToKey.Where(kv => kv.Value == key).Select(kv => kv.Key).ToList())
            {
                _aliasToKey.Remove(alias);
            }
        }

        return stale.Count;
    }

    public void AddOrReplace(DeviceIdMapEntry entry)
    {
        _byKey[entry.Key] = entry;
        _aliasToKey[entry.Key] = entry.Key;
        foreach (var alias in entry.Aliases)
        {
            if (!string.Equals(alias, entry.Key, StringComparison.Ordinal))
            {
                _aliasToKey[alias] = entry.Key;
            }
        }
    }

    private static List<string> BuildAliases(string key, DeviceDescriptor descriptor)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal)
        {
            DeviceKey.ForTopology(descriptor).Value,
            DeviceKey.ForFingerprint(descriptor).Value,
        };

        if (descriptor.UsableSerial is { } serial)
        {
            aliases.Add(DeviceKey.ForSerial(descriptor, serial).Value);
        }

        aliases.Remove(key);
        return aliases.OrderBy(a => a, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- persistence

    /// <summary>Load from an already-flattened state layer (<c>[[identity.devices]]</c>).</summary>
    public static DeviceIdMap Load(FlatTable table, List<ConfigDiagnostic>? diagnostics = null)
    {
        var entries = new List<DeviceIdMapEntry>();
        foreach (var item in table.TableArray("identity.devices"))
        {
            var key = GetString(item, "key");
            if (string.IsNullOrWhiteSpace(key))
            {
                diagnostics?.Add(new ConfigDiagnostic(
                    ConfigDiagnosticSeverity.Warning,
                    ConfigDiagnosticCode.InvalidValue,
                    "identity entry without a key was skipped",
                    "identity.devices.key",
                    item.TryGetValue("key", out var k) ? k.Origin.Location : ConfigLocation.None,
                    item.TryGetValue("key", out var k2) ? k2.Origin.File : null));
                continue;
            }

            entries.Add(new DeviceIdMapEntry(
                Key: key,
                VendorId: ParseHex(GetString(item, "vid")),
                ProductId: ParseHex(GetString(item, "pid")),
                BcdDevice: ParseHex(GetString(item, "bcd")),
                Serial: GetString(item, "serial"),
                Fingerprint: GetString(item, "fingerprint"),
                LastPortPath: GetString(item, "last_port_path"),
                Aliases: GetStringList(item, "aliases"),
                FirstSeenUtc: ParseTime(GetString(item, "first_seen_utc")),
                LastSeenUtc: ParseTime(GetString(item, "last_seen_utc")),
                Product: GetString(item, "product"),
                Manufacturer: GetString(item, "manufacturer")));
        }

        return new DeviceIdMap(entries);
    }

    /// <summary>Flat path/value pairs ready for the state writer (<c>identity.devices#N.field</c>).</summary>
    public IEnumerable<KeyValuePair<string, object?>> ToFlatValues()
    {
        var index = 0;
        foreach (var entry in _byKey.Values.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var prefix = $"identity.devices#{index}";
            yield return new KeyValuePair<string, object?>($"{prefix}.key", entry.Key);
            yield return new KeyValuePair<string, object?>($"{prefix}.vid", entry.VendorId.ToString("x4", CultureInfo.InvariantCulture));
            yield return new KeyValuePair<string, object?>($"{prefix}.pid", entry.ProductId.ToString("x4", CultureInfo.InvariantCulture));
            yield return new KeyValuePair<string, object?>($"{prefix}.bcd", entry.BcdDevice.ToString("x4", CultureInfo.InvariantCulture));
            yield return new KeyValuePair<string, object?>($"{prefix}.serial", entry.Serial);
            yield return new KeyValuePair<string, object?>($"{prefix}.fingerprint", entry.Fingerprint);
            yield return new KeyValuePair<string, object?>($"{prefix}.last_port_path", entry.LastPortPath);
            yield return new KeyValuePair<string, object?>($"{prefix}.aliases", entry.Aliases.Cast<object?>().ToList());
            yield return new KeyValuePair<string, object?>($"{prefix}.first_seen_utc", entry.FirstSeenUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            yield return new KeyValuePair<string, object?>($"{prefix}.last_seen_utc", entry.LastSeenUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            yield return new KeyValuePair<string, object?>($"{prefix}.product", entry.Product);
            yield return new KeyValuePair<string, object?>($"{prefix}.manufacturer", entry.Manufacturer);
            index++;
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, FlatValue> item, string field) =>
        item.TryGetValue(field, out var value) ? value.Value as string : null;

    private static IReadOnlyList<string> GetStringList(IReadOnlyDictionary<string, FlatValue> item, string field)
    {
        if (!item.TryGetValue(field, out var value) || value.Value is not IReadOnlyList<object?> list)
        {
            return Array.Empty<string>();
        }

        return list.OfType<string>().ToList();
    }

    private static ushort ParseHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : (ushort)0;
    }

    private static DateTimeOffset ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
}