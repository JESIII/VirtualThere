using System.Globalization;
using System.Text;

namespace Vht.Config.Toml;

/// <summary>
/// Minimal, deterministic TOML emitter for the machine-managed state file. It only has to handle the
/// shapes this library writes (scalars, string lists, tables and table-arrays addressed by
/// <c>path#index</c>), so it is deliberately small and fully unit-tested rather than being a general
/// serializer. Output is stable: sections and keys are sorted, so a save with no semantic change
/// produces a byte-identical file.
/// </summary>
public static class TomlWriter
{
    public static string Write(IEnumerable<KeyValuePair<string, object?>> values, string? header = null)
    {
        var entries = values.ToList();
        var plain = new SortedDictionary<string, SortedDictionary<string, object?>>(StringComparer.Ordinal);
        var indexed = new SortedDictionary<string, SortedDictionary<int, SortedDictionary<string, object?>>>(StringComparer.Ordinal);

        foreach (var (path, value) in entries)
        {
            if (value is null)
            {
                continue;
            }

            var segments = path.Split('.');
            var sectionSegments = segments[..^1];
            var field = segments[^1];

            // an index marker may appear on any segment; canonical form is <section>#<n>.<field>
            var index = -1;
            var sectionParts = new List<string>();
            foreach (var segment in sectionSegments)
            {
                var hash = segment.IndexOf('#');
                if (hash >= 0 && int.TryParse(segment.AsSpan(hash + 1), out var parsed))
                {
                    index = parsed;
                    sectionParts.Add(segment[..hash]);
                }
                else
                {
                    sectionParts.Add(segment);
                }
            }

            var section = string.Join('.', sectionParts);
            if (index >= 0)
            {
                if (!indexed.TryGetValue(section, out var byIndex))
                {
                    byIndex = new SortedDictionary<int, SortedDictionary<string, object?>>();
                    indexed[section] = byIndex;
                }

                if (!byIndex.TryGetValue(index, out var fields))
                {
                    fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                    byIndex[index] = fields;
                }

                fields[field] = value;
            }
            else
            {
                if (!plain.TryGetValue(section, out var fields))
                {
                    fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                    plain[section] = fields;
                }

                fields[field] = value;
            }
        }

        var text = new StringBuilder();
        if (!string.IsNullOrEmpty(header))
        {
            foreach (var line in header.Split('\n'))
            {
                text.Append("# ").Append(line.TrimEnd('\r')).Append('\n');
            }

            text.Append('\n');
        }

        // top-level scalars first
        if (plain.TryGetValue(string.Empty, out var topLevel))
        {
            foreach (var (key, value) in topLevel)
            {
                text.Append(Key(key)).Append(" = ").Append(Value(value)).Append('\n');
            }

            plain.Remove(string.Empty);
        }

        var sections = new SortedSet<string>(plain.Keys, StringComparer.Ordinal);
        sections.UnionWith(indexed.Keys);

        foreach (var section in sections)
        {
            if (plain.TryGetValue(section, out var fields))
            {
                if (text.Length > 0 && text[^1] != '\n')
                {
                    text.Append('\n');
                }

                text.Append('[').Append(SectionKey(section)).Append(']').Append('\n');
                foreach (var (key, value) in fields)
                {
                    text.Append(Key(key)).Append(" = ").Append(Value(value)).Append('\n');
                }
            }

            if (!indexed.TryGetValue(section, out var byIndex))
            {
                continue;
            }

            foreach (var (_, itemFields) in byIndex)
            {
                text.Append('\n').Append("[[").Append(SectionKey(section)).Append("]]").Append('\n');
                foreach (var (key, value) in itemFields)
                {
                    text.Append(Key(key)).Append(" = ").Append(Value(value)).Append('\n');
                }
            }
        }

        return text.ToString();
    }

    public static string Key(string key) => IsBareKey(key) ? key : Quote(key);

    private static string SectionKey(string section) =>
        string.Join('.', section.Split('.').Select(Key));

    private static bool IsBareKey(string key)
    {
        if (key.Length == 0)
        {
            return false;
        }

        foreach (var ch in key)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static string Value(object? value) => value switch
    {
        null => "''",
        string text => Quote(text),
        bool flag => flag ? "true" : "false",
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IReadOnlyList<object?> list => "[" + string.Join(", ", list.Where(i => i is not null).Select(Value)) + "]",
        _ => Quote(value.ToString() ?? string.Empty),
    };

    private static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}