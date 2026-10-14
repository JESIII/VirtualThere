using Vht.Config.Diagnostics;

namespace Vht.Config.Toml;

/// <summary>
/// A flattened TOML document: every leaf value is addressed by a dotted path. Nested tables become
/// deeper paths (<c>transport.tls.port</c>); arrays of scalars stay a single value
/// (<c>policy.allow</c> = ["a","b"]); arrays of tables and <c>[[table]]</c> arrays become indexed
/// paths (<c>identity.devices#0.key</c>). Flattening is what makes layering (defaults &lt; config.toml
/// &lt; state.toml), origin tracking ("where did this value come from?") and unknown-key detection
/// one pass each instead of three ad-hoc walks.
/// </summary>
public sealed class FlatTable
{
    private readonly Dictionary<string, FlatValue> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, FlatValue> Values => _values;

    public int Count => _values.Count;

    public void Set(string path, object? value, ConfigOrigin origin) =>
        _values[path] = new FlatValue(path, value, origin);

    public bool TryGet(string path, out FlatValue value) => _values.TryGetValue(path, out value!);

    public bool Contains(string path) => _values.ContainsKey(path);

    /// <summary>All indexed groups under a table-array prefix, in index order.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, FlatValue>> TableArray(string prefix)
    {
        var indices = new SortedSet<int>();
        var head = prefix + "#";
        foreach (var key in _values.Keys)
        {
            if (!key.StartsWith(head, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = key.AsSpan(head.Length);
            var dot = rest.IndexOf('.');
            var digits = dot < 0 ? rest : rest[..dot];
            if (int.TryParse(digits, out var index))
            {
                indices.Add(index);
            }
        }

        var result = new List<IReadOnlyDictionary<string, FlatValue>>();
        foreach (var index in indices)
        {
            var bucket = new Dictionary<string, FlatValue>(StringComparer.Ordinal);
            var itemHead = $"{prefix}#{index}.";
            foreach (var (key, value) in _values)
            {
                if (key.StartsWith(itemHead, StringComparison.Ordinal))
                {
                    bucket[key[itemHead.Length..]] = value;
                }
            }

            result.Add(bucket);
        }

        return result;
    }

    /// <summary>All keys under a string-map prefix (<c>nicknames</c> -&gt; the key after the dot).</summary>
    public IReadOnlyDictionary<string, FlatValue> Map(string prefix)
    {
        var head = prefix + ".";
        var result = new Dictionary<string, FlatValue>(StringComparer.Ordinal);
        foreach (var (key, value) in _values)
        {
            if (key.StartsWith(head, StringComparison.Ordinal))
            {
                result[key[head.Length..]] = value;
            }
        }

        return result;
    }
}

/// <summary>One flattened value plus where it came from.</summary>
public sealed record FlatValue(string Path, object? Value, ConfigOrigin Origin)
{
    public bool IsList => Value is IReadOnlyList<object?>;

    public IReadOnlyList<object?> AsList() => Value as IReadOnlyList<object?> ?? Array.Empty<object?>();
}

/// <summary>Where a value came from, including the exact file position when there is one.</summary>
public sealed record ConfigOrigin(ConfigSourceKind Kind, string? File, ConfigLocation Location)
{
    public static readonly ConfigOrigin Defaults = new(ConfigSourceKind.Defaults, null, ConfigLocation.None);

    public override string ToString() =>
        Kind == ConfigSourceKind.Defaults ? "default" : $"{Kind.ToString().ToLowerInvariant()}:{File ?? "?"}{(Location.IsKnown ? $"({Location.Line}:{Location.Column})" : string.Empty)}";
}