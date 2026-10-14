using Vht.Config.Diagnostics;
using Vht.Config.Model;
using Vht.Config.Toml;

namespace Vht.Config.Reload;

/// <summary>One changed key between two effective configurations.</summary>
public sealed record ConfigChange(
    string Path,
    object? OldValue,
    object? NewValue,
    ConfigSourceKind OldSource,
    ConfigSourceKind NewSource,
    bool RequiresRestart,
    string Reason)
{
    public string Describe()
    {
        var oldText = Value(OldValue);
        var newText = Value(NewValue);
        var action = RequiresRestart ? "RESTART REQUIRED" : "applied live";
        return $"{Path}: {oldText} -> {newText} [{OldSource.ToString().ToLowerInvariant()} -> {NewSource.ToString().ToLowerInvariant()}] ({action}{Reason})";
    }

    private static string Value(object? value) => value switch
    {
        null => "(unset)",
        string text => text.Length == 0 ? "\"\"" : text,
        bool flag => flag ? "true" : "false",
        long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IReadOnlyList<object?> list => "[" + string.Join(", ", list.Select(i => i?.ToString() ?? "null")) + "]",
        _ => value.ToString() ?? "(null)",
    };
}

public sealed record ConfigReloadPlan(IReadOnlyList<ConfigChange> Changes)
{
    public static ConfigReloadPlan Empty { get; } = new(Array.Empty<ConfigChange>());

    public bool HasChanges => Changes.Count > 0;

    public bool RequiresRestart => Changes.Any(c => c.RequiresRestart);

    public IReadOnlyList<ConfigChange> RestartRequired =>
        Changes.Where(c => c.RequiresRestart).ToList();

    public IReadOnlyList<ConfigChange> HotChanges =>
        Changes.Where(c => !c.RequiresRestart).ToList();

    public string Describe()
    {
        if (!HasChanges)
        {
            return "no changes";
        }

        var lines = Changes.Select(c => "  " + c.Describe()).ToList();
        var header = RequiresRestart
            ? $"configuration changed: {RestartRequired.Count} key(s) need a restart, {HotChanges.Count} applied live"
            : $"configuration changed: {HotChanges.Count} key(s) applied live, no restart needed";
        return header + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Decides whether a configuration change can be applied to a running service. The classification is
/// driven by the schema (<see cref="ConfigKeyDescriptor.RequiresRestart"/>) so the documented table in
/// docs/CONFIG.md and the code can never disagree.
/// </summary>
public static class ConfigReloadClassifier
{
    public static ConfigReloadPlan Classify(FlatTable oldValues, FlatTable newValues)
    {
        var changes = new List<ConfigChange>();

        foreach (var (path, newValue) in newValues.Values)
        {
            var oldValue = oldValues.TryGet(path, out var existing) ? existing : null;
            if (oldValue is null)
            {
                changes.Add(new ConfigChange(
                    path,
                    null,
                    newValue.Value,
                    ConfigSourceKind.Defaults,
                    newValue.Origin.Kind,
                    ConfigSchema.RequiresRestart(path),
                    ConfigSchema.RequiresRestart(path) ? "; new key" : string.Empty));
                continue;
            }

            if (!SameValue(oldValue.Value, newValue.Value) || oldValue.Origin.Kind != newValue.Origin.Kind)
            {
                changes.Add(new ConfigChange(
                    path,
                    oldValue.Value,
                    newValue.Value,
                    oldValue.Origin.Kind,
                    newValue.Origin.Kind,
                    ConfigSchema.RequiresRestart(path),
                    string.Empty));
            }
        }

        foreach (var (path, oldValue) in oldValues.Values)
        {
            if (!newValues.Contains(path))
            {
                changes.Add(new ConfigChange(
                    path,
                    oldValue.Value,
                    null,
                    oldValue.Origin.Kind,
                    ConfigSourceKind.Defaults,
                    ConfigSchema.RequiresRestart(path),
                    "; key removed"));
            }
        }

        return new ConfigReloadPlan(changes.OrderBy(c => c.Path, StringComparer.Ordinal).ToList());
    }

    private static bool SameValue(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is IReadOnlyList<object?> leftList && right is IReadOnlyList<object?> rightList)
        {
            return leftList.Count == rightList.Count && leftList.SequenceEqual(rightList);
        }

        return left.Equals(right);
    }
}