namespace Vht.Config.Diagnostics;

/// <summary>Where a value came from. Precedence, lowest to highest:
/// Defaults &lt; File (config.toml) &lt; Overlay (state.toml) &lt; Environment &lt; CommandLine.</summary>
public enum ConfigSourceKind
{
    Defaults = 0,
    File = 1,
    Overlay = 2,
    Environment = 3,
    CommandLine = 4,

    /// <summary>A <c>portable.marker</c> beside the executable - config and state stay in
    /// <c>./config</c> in that folder.</summary>
    Portable = 5,
}

public enum ConfigDiagnosticSeverity
{
    Warning = 0,
    Error = 1,
}

public enum ConfigDiagnosticCode
{
    InvalidTomlSyntax,
    TypeMismatch,
    ValueOutOfRange,
    InvalidValue,
    UnknownKey,
    MachineManagedKey,
    PolicyConflict,
    UnsupportedConfigVersion,
    MigrationApplied,
    FileNotFound,
    ReadError,
    WriteError,
    PathInvalid,
    HostNameUnavailable,
    UnsupportedArrayShape,
}

/// <summary>A 1-based line/column, or <see cref="None"/> when the value is not present in any file
/// (i.e. it is a built-in default).</summary>
public readonly record struct ConfigLocation(int Line, int Column)
{
    public static readonly ConfigLocation None = new(0, 0);

    public bool IsKnown => Line > 0;

    public static ConfigLocation Of(int line, int column) => new(line, column);

    public override string ToString() => IsKnown ? $"{Line}:{Column}" : "-";
}

/// <summary>
/// A single validation/parsing message. Messages are written so that a human with the config file
/// open can act on them without reading the source: file, 1-based line:column, dotted key, and what
/// was expected. Errors never throw at the caller - they are collected (see <see cref="ConfigLoadException"/>
/// for the one place a throw is still appropriate: catastrophic I/O failure).
/// </summary>
public sealed record ConfigDiagnostic(
    ConfigDiagnosticSeverity Severity,
    ConfigDiagnosticCode Code,
    string Message,
    string? Path = null,
    ConfigLocation Location = default,
    string? File = null)
{
    public bool IsError => Severity == ConfigDiagnosticSeverity.Error;

    public string Describe()
    {
        var file = File ?? "<defaults>";
        var where = Location.IsKnown ? $"{Location.Line}:{Location.Column}" : "-";
        var path = Path is null ? string.Empty : $" [{Path}]";
        return $"{file}({where}): {Severity.ToString().ToLowerInvariant()} {Code}: {Message}{path}";
    }

    public override string ToString() => Describe();
}

/// <summary>Thrown only for failures that make further work meaningless (unreadable file, unwritable
/// state directory). Configuration *content* problems are reported as diagnostics instead, because a
/// malformed config must produce a clear error and a non-zero exit code - never a crash.</summary>
public sealed class ConfigLoadException : Exception
{
    public ConfigLoadException(string message)
        : base(message)
    {
    }

    public ConfigLoadException(string message, Exception inner)
        : base(message, inner)
    {
    }
}