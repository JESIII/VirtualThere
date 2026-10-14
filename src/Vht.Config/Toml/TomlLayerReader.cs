using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;
using Vht.Config.Diagnostics;

namespace Vht.Config.Toml;

public sealed record TomlLayerReadResult(
    FlatTable Table,
    IReadOnlyList<ConfigDiagnostic> Diagnostics,
    bool HasErrors);

/// <summary>
/// Parses one TOML layer into a <see cref="FlatTable"/> and turns every TOML-level problem into a
/// diagnostic with a real line:column. A malformed file yields errors and an empty/partial table -
/// it never throws out of a load.
/// </summary>
public static class TomlLayerReader
{
    public static TomlLayerReadResult ReadText(string text, string filePath, ConfigSourceKind kind)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var table = new FlatTable();
        var origin = new ConfigOrigin(kind, filePath, ConfigLocation.None);

        DocumentSyntax document;
        try
        {
            // Tomlyn.Toml is fully qualified: inside namespace Vht.Config.Toml, a bare `Toml` binds
            // to this namespace, not to the Tomlyn parser class.
            document = Tomlyn.Toml.Parse(text, filePath);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new ConfigDiagnostic(
                ConfigDiagnosticSeverity.Error,
                ConfigDiagnosticCode.InvalidTomlSyntax,
                $"could not parse TOML: {ex.Message}",
                File: filePath));
            return new TomlLayerReadResult(table, diagnostics, true);
        }

        var hadErrors = false;
        foreach (var message in document.Diagnostics)
        {
            var severity = message.Kind == DiagnosticMessageKind.Error
                ? ConfigDiagnosticSeverity.Error
                : ConfigDiagnosticSeverity.Warning;
            if (severity == ConfigDiagnosticSeverity.Error)
            {
                hadErrors = true;
            }

            diagnostics.Add(new ConfigDiagnostic(
                severity,
                ConfigDiagnosticCode.InvalidTomlSyntax,
                message.Message,
                Location: Location(message.Span),
                File: filePath));
        }

        if (hadErrors)
        {
            return new TomlLayerReadResult(table, diagnostics, true);
        }

        TomlTable model;
        try
        {
            model = Tomlyn.Toml.ToModel(document);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new ConfigDiagnostic(
                ConfigDiagnosticSeverity.Error,
                ConfigDiagnosticCode.InvalidTomlSyntax,
                $"could not build the TOML model: {ex.Message}",
                File: filePath));
            return new TomlLayerReadResult(table, diagnostics, true);
        }

        var spans = SyntaxSpans.Build(document);
        Flatten(model, prefix: string.Empty, table, origin, spans, diagnostics);
        return new TomlLayerReadResult(table, diagnostics, diagnostics.Exists(d => d.IsError));
    }

    public static TomlLayerReadResult ReadFile(string filePath, ConfigSourceKind kind)
    {
        string text;
        try
        {
            text = System.IO.File.ReadAllText(filePath);
        }
        catch (FileNotFoundException)
        {
            return new TomlLayerReadResult(new FlatTable(), Array.Empty<ConfigDiagnostic>(), false);
        }
        catch (DirectoryNotFoundException)
        {
            return new TomlLayerReadResult(new FlatTable(), Array.Empty<ConfigDiagnostic>(), false);
        }
        catch (Exception ex)
        {
            var diagnostic = new ConfigDiagnostic(
                ConfigDiagnosticSeverity.Error,
                ConfigDiagnosticCode.ReadError,
                $"could not read the configuration file: {ex.Message}",
                File: filePath);
            return new TomlLayerReadResult(new FlatTable(), new[] { diagnostic }, true);
        }

        return ReadText(text, filePath, kind);
    }

    private static void Flatten(
        TomlTable table,
        string prefix,
        FlatTable output,
        ConfigOrigin origin,
        IReadOnlyDictionary<string, ConfigLocation> spans,
        List<ConfigDiagnostic> diagnostics)
    {
        foreach (var (key, value) in table)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            FlattenValue(value, path, output, origin, spans, diagnostics);
        }
    }

    private static void FlattenValue(
        object? value,
        string path,
        FlatTable output,
        ConfigOrigin origin,
        IReadOnlyDictionary<string, ConfigLocation> spans,
        List<ConfigDiagnostic> diagnostics)
    {
        var effectiveOrigin = origin with { Location = spans.TryGetValue(path, out var location) ? location : origin.Location };

        switch (value)
        {
            case TomlTable nested:
                Flatten(nested, path, output, origin, spans, diagnostics);
                return;

            case TomlTableArray tableArray:
                for (var i = 0; i < tableArray.Count; i++)
                {
                    Flatten(tableArray[i], $"{path}#{i}", output, origin, spans, diagnostics);
                }

                return;

            case TomlArray array:
                if (array.Count == 0)
                {
                    output.Set(path, Array.Empty<object?>(), effectiveOrigin);
                    return;
                }

                var items = new List<object?>(array.Count);
                var allScalar = true;
                var index = 0;
                foreach (var item in array)
                {
                    if (item is TomlTable inlineTable)
                    {
                        allScalar = false;
                        Flatten(inlineTable, $"{path}#{index}", output, origin, spans, diagnostics);
                    }
                    else if (item is TomlArray || item is TomlTableArray)
                    {
                        diagnostics.Add(new ConfigDiagnostic(
                            ConfigDiagnosticSeverity.Error,
                            ConfigDiagnosticCode.UnsupportedArrayShape,
                            "nested arrays are not supported; use a flat list or a [[table]] array",
                            path,
                            effectiveOrigin.Location,
                            origin.File));
                    }
                    else
                    {
                        items.Add(item);
                    }

                    index++;
                }

                if (allScalar)
                {
                    output.Set(path, items, effectiveOrigin);
                }

                return;

            case TomlObject other:
                diagnostics.Add(new ConfigDiagnostic(
                    ConfigDiagnosticSeverity.Warning,
                    ConfigDiagnosticCode.InvalidValue,
                    $"unsupported TOML value of type {other.GetType().Name}; treated as text",
                    path,
                    effectiveOrigin.Location,
                    origin.File));
                output.Set(path, other.ToString(), effectiveOrigin);
                return;

            default:
                output.Set(path, value, effectiveOrigin);
                return;
        }
    }

    private static ConfigLocation Location(SourceSpan span) =>
        ConfigLocation.Of(span.Start.Line + 1, span.Start.Column + 1);
}

/// <summary>
/// Maps dotted paths to their source positions by walking the syntax tree. The model layer (TomlTable)
/// throws away positions, so position lookups are done against the syntax tree instead of guessing
/// line numbers from the text.
/// </summary>
internal static class SyntaxSpans
{
    public static IReadOnlyDictionary<string, ConfigLocation> Build(DocumentSyntax document)
    {
        var map = new Dictionary<string, ConfigLocation>(StringComparer.Ordinal);
        foreach (var pair in document.KeyValues)
        {
            Add(map, KeyText(pair.Key), pair.Span);
        }

        foreach (var table in document.Tables)
        {
            var tableName = table.Name;
            var name = tableName is null ? string.Empty : KeyText(tableName);
            if (name.Length != 0 && tableName is not null)
            {
                Add(map, name, tableName.Span);
            }

            foreach (var pair in table.Items)
            {
                var itemName = KeyText(pair.Key);
                var path = name.Length == 0 ? itemName : $"{name}.{itemName}";
                Add(map, path, pair.Span);
            }
        }

        return map;
    }

    private static void Add(Dictionary<string, ConfigLocation> map, string path, SourceSpan span)
    {
        if (path.Length == 0)
        {
            return;
        }

        map.TryAdd(path, ConfigLocation.Of(span.Start.Line + 1, span.Start.Column + 1));
    }

    private static string KeyText(KeySyntax? key)
    {
        if (key is null)
        {
            return string.Empty;
        }

        var text = KeyPart(key.Key);
        foreach (var dotted in key.DotKeys)
        {
            text += "." + KeyPart(dotted.Key);
        }

        return text;
    }

    private static string KeyPart(BareKeyOrStringValueSyntax? part) => part switch
    {
        null => string.Empty,
        BareKeySyntax bare => bare.Key?.Text ?? string.Empty,
        StringValueSyntax str => str.Value ?? string.Empty,
        _ => part.ToString(),
    };
}