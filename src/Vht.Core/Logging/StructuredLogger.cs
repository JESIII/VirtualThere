using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vht.Core.Logging;

/// <summary>
/// Severity levels, numbered to match the reference product's level-first logger
/// (<c>FUN_140733bb0(level, fmt, …)</c>, RECON_AND_SPEC.md §2): its licence gates log at
/// level 3 and its connection lifecycle at level 6, so <see cref="Warn"/> is 3 and
/// <see cref="Debug"/> is 6 to keep operator filters transferable.
/// </summary>
public enum LogLevel
{
    Critical = 1,
    Error = 2,
    Warn = 3,
    Info = 4,
    Detail = 5,
    Debug = 6,
    Trace = 7,
}

/// <summary>
/// Stable event ids for the structured log. Being able to filter on these is what makes the
/// server operable without a GUI.
/// </summary>
public enum LogEvent
{
    ServerStarting,
    ServerStarted,
    ServerStopping,
    ServerStopped,
    ListenerStarted,
    ListenerStopped,
    ListenerFailed,
    ConfigurationLoaded,
    DeviceEnumerated,
    DeviceCaptureRefused,
    DeviceExportAdded,
    DeviceExportRemoved,
    DeviceOpened,
    DeviceReleased,
    DeviceBindingRecorded,
    DeviceHandedBack,
    HotplugArrival,
    HotplugRemoval,
    ClientConnected,
    ClientDisconnected,
    ControlPlaneStarted,
    ControlPlaneFailed,
    ControlPlaneCommand,
    HookCompleted,
    HookFailed,
    HookTimedOut,
    HookRefused,
    HookUnknownExitCode,
    BindAccepted,
    BindRefused,
    DeviceKicked,
    DeviceIgnored,
    DeviceUnignored,
    NicknameChanged,
    AttachCountChanged,
    ProtocolError,
    TransferError,
    ServiceInstall,
    ServiceUninstall,
}

/// <summary>One structured log record.</summary>
public sealed class LogRecord
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public LogLevel Level { get; init; }
    public LogEvent Event { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Fields { get; init; } =
        new Dictionary<string, object?>();

    public string ToJsonLine()
    {
        var payload = new Dictionary<string, object?>(Fields.Count + 4)
        {
            ["ts"] = Timestamp.ToString("O"),
            ["level"] = Level.ToString(),
            ["event"] = Event.ToString(),
            ["message"] = Message,
        };
        foreach ((string k, object? v) in Fields)
            payload[k] = v;

        return JsonSerializer.Serialize(payload, JsonOptions) + "\n";
    }

    public string ToText()
    {
        var suffix = Fields.Count == 0
            ? string.Empty
            : " " + string.Join(' ',
                Fields.Where(f => f.Value is not null).Select(f => $"{f.Key}={f.Value}"));

        return $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff}Z [{(int)Level}] {Level,-8} {Event}: {Message}{suffix}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}

/// <summary>A destination for log records.</summary>
public interface ILogSink
{
    void Write(LogRecord record);
}

/// <summary>Discards everything. Used where no sink was supplied.</summary>
public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();

    public void Write(LogRecord record) { }
}

/// <summary>Writes human-readable lines to a text writer (stdout in console mode).</summary>
public sealed class TextWriterLogSink : ILogSink
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    public TextWriterLogSink(TextWriter writer) => _writer = writer;

    public void Write(LogRecord record)
    {
        lock (_gate) _writer.WriteLine(record.ToText());
    }
}

/// <summary>
/// Appends JSON lines to a file, rolling at <see cref="MaxBytes"/>. This is the sink the
/// Windows service uses when Event Log output is not wanted; it replaces the reference
/// product's <c>-r logfile</c> switch with a structured equivalent.
/// </summary>
public sealed class RollingFileLogSink : ILogSink, IDisposable
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public RollingFileLogSink(string path, long maxBytes = 8 * 1024 * 1024)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _maxBytes = maxBytes;
    }

    /// <summary>Path written to. Exposed so diagnostics can report it without guessing.</summary>
    public string Path => _path;

    public void Write(LogRecord record)
    {
        lock (_gate)
        {
            try
            {
                EnsureOpen();
                _writer!.Write(record.ToJsonLine());
                _writer.Flush();
                RollIfNeeded();
            }
            catch (IOException)
            {
                // A log that cannot be written must never take the service down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void EnsureOpen()
    {
        if (_writer is not null) return;
        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = false,
        };
    }

    private void RollIfNeeded()
    {
        if (_writer is null) return;
        if (_writer.BaseStream.Length < _maxBytes) return;

        _writer.Dispose();
        _writer = null;

        string rolled = _path + ".1";
        try
        {
            if (File.Exists(rolled)) File.Delete(rolled);
            File.Move(_path, rolled);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>Fans records out to several sinks, and applies a minimum level.</summary>
public interface ILogger
{
    LogLevel MinimumLevel { get; }
    void Write(LogLevel level, LogEvent evt, string message, IReadOnlyDictionary<string, object?>? fields = null);
}

/// <summary>
/// The server's logger. Holds a level filter and one or more sinks; every subsystem takes an
/// <see cref="ILogSink"/> so tests can assert on records without a file.
/// </summary>
public sealed class StructuredLogger : ILogger, ILogSink
{
    private readonly List<ILogSink> _sinks = new();
    private readonly object _gate = new();

    public StructuredLogger(LogLevel minimumLevel = LogLevel.Info, params ILogSink[] sinks)
    {
        MinimumLevel = minimumLevel;
        if (sinks is not null) _sinks.AddRange(sinks);
    }

    public LogLevel MinimumLevel { get; set; }

    /// <summary>Adds a sink. Returns this instance so sinks can be chained.</summary>
    public StructuredLogger AddSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate) _sinks.Add(sink);
        return this;
    }

    public void Write(
        LogLevel level,
        LogEvent evt,
        string message,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (level > MinimumLevel) return;
        Dispatch(new LogRecord { Level = level, Event = evt, Message = message, Fields = fields ?? Empty });
    }

    void ILogSink.Write(LogRecord record) => Dispatch(record);

    /// <summary>Fans a record out to every sink, applying the level filter once.</summary>
    private void Dispatch(LogRecord record)
    {
        if (record.Level > MinimumLevel) return;
        ILogSink[] sinks;
        lock (_gate) sinks = _sinks.ToArray();
        foreach (ILogSink sink in sinks)
        {
            try
            {
                sink.Write(record);
            }
            catch
            {
                // A failing sink is dropped for this record; logging never propagates failure.
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty =
        new Dictionary<string, object?>();
}

/// <summary>Collects records in memory. Used by tests to assert on log behaviour.</summary>
public sealed class InMemoryLogSink : ILogSink
{
    private readonly List<LogRecord> _records = new();
    private readonly object _gate = new();

    public IReadOnlyList<LogRecord> Records
    {
        get { lock (_gate) return _records.ToArray(); }
    }

    public void Write(LogRecord record)
    {
        lock (_gate) _records.Add(record);
    }

    public bool HasEvent(LogEvent evt)
    {
        lock (_gate) return _records.Any(r => r.Event == evt);
    }
}
