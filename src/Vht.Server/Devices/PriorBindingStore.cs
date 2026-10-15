using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vht.Core.Logging;

namespace Vht.Server.Devices;

/// <summary>How far the rebind of one device got before this record was written.</summary>
public enum PriorBindingPhase
{
    /// <summary>The prior binding is recorded; nothing has been changed on the machine.</summary>
    Recorded,

    /// <summary>The driver was re-selected: the devnode is on a package this server staged.</summary>
    Rebound,
}

/// <summary>
/// What one device was bound to before this server moved it, so the move can be undone.
/// </summary>
/// <remarks>
/// <para>
/// This is the record ARCHITECTURE.md §4.1 asks for ("record the original INF/driver before
/// rebinding"). It is written to disk <em>before</em> the driver store is touched, because the
/// failure it exists for - a rebind that succeeds and a process that then dies - cannot be
/// repaired by anything written afterwards.
/// </para>
/// <para>
/// The key is the device instance id, not the vid/pid: one rebind covers every present instance
/// of a vid/pid, and each of them needs its own handback.
/// </para>
/// </remarks>
public sealed record PriorBindingRecord
{
    /// <summary>Device instance id, e.g. <c>USB\VID_0781&amp;PID_5583\4C530001120328105553</c>.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>The hardware id the rebind matched: <c>USB\VID_xxxx&amp;PID_yyyy</c>.</summary>
    public string HardwareId { get; init; } = string.Empty;

    /// <summary>The INF that was in effect, by name.</summary>
    public string PriorInfName { get; init; } = string.Empty;

    /// <summary>The path of that INF; this is what the handback re-selects.</summary>
    public string PriorInfPath { get; init; } = string.Empty;

    /// <summary>The bound service before the rebind, e.g. <c>disk</c>.</summary>
    public string PriorService { get; init; } = string.Empty;

    /// <summary>The driver package's provider before the rebind.</summary>
    public string PriorProvider { get; init; } = string.Empty;

    /// <summary>The package this server staged, once the driver store assigned it a name.</summary>
    public string? StagedInfName { get; init; }

    /// <summary>Where that package came from, for the log line that names it.</summary>
    public string? StagedInfPath { get; init; }

    public PriorBindingPhase Phase { get; init; } = PriorBindingPhase.Recorded;

    public DateTimeOffset RecordedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>One line an operator (or a log) can read: what was there, and what we did.</summary>
    public string Describe() =>
        $"{InstanceId} was on {PriorInfName} (service {PriorService}, provider {PriorProvider}); " +
        $"this server staged {StagedInfName ?? "nothing"}";
}

/// <summary>
/// The durable list of prior bindings, in the rebinder's working directory. It survives a
/// server restart, which is the point: a device this server moved has to be given back by the
/// next run even if this one was killed.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation is flushed immediately (write-to-temporary, then replace), so the file on disk
/// is never a half-written record. The file is small - one entry per device this server has
/// moved - and it holds nothing secret: device instance ids and INF names.
/// </para>
/// <para>
/// An unreadable or corrupt file is reported and treated as empty rather than throwing: a server
/// that refuses to start because of a note-to-self file is worse than one that logs the loss.
/// </para>
/// </remarks>
public sealed class PriorBindingStore
{
    private const string FileName = "prior-bindings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },

        // The file is meant to be read by a person: an escaped '&' in a device instance id is
        // exactly the kind of noise that makes an operator reach for pnputil instead.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger? _log;
    private readonly object _gate = new();
    private List<PriorBindingRecord> _records;

    public PriorBindingStore(string workingDirectory, ILogger? log = null)
    {
        DirectoryPath = string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory;
        FilePath = Path.Combine(DirectoryPath, FileName);
        _log = log;
        _records = Load();
    }

    /// <summary>Directory the INFs and this record file live in.</summary>
    public string DirectoryPath { get; }

    /// <summary>The record file itself.</summary>
    public string FilePath { get; }

    /// <summary>Every record, oldest first.</summary>
    public IReadOnlyList<PriorBindingRecord> Snapshot()
    {
        lock (_gate) return _records.ToArray();
    }

    /// <summary>Every record for one hardware id, in record order.</summary>
    public IReadOnlyList<PriorBindingRecord> ForHardwareId(string hardwareId)
    {
        lock (_gate)
        {
            return _records
                .Where(r => string.Equals(r.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    /// <summary>
    /// Replaces the records for one hardware id with the freshly read prior bindings. Called
    /// before the machine is touched; the write completes before this returns.
    /// </summary>
    public void Replace(string hardwareId, IEnumerable<PriorBindingRecord> records)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => string.Equals(r.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase));
            _records.AddRange(records);
            Flush();
        }
    }

    /// <summary>Notes the package name the driver store assigned the rebind's INF.</summary>
    public void MarkStaged(string hardwareId, string stagedInfName, string stagedInfPath)
    {
        lock (_gate)
        {
            _records = _records
                .Select(r => string.Equals(r.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase)
                    ? r with { StagedInfName = stagedInfName, StagedInfPath = stagedInfPath }
                    : r)
                .ToList();
            Flush();
        }
    }

    /// <summary>Notes that the driver was re-selected: the devnode is on our package now.</summary>
    public void MarkRebound(string hardwareId)
    {
        lock (_gate)
        {
            _records = _records
                .Select(r => string.Equals(r.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase)
                    ? r with { Phase = PriorBindingPhase.Rebound }
                    : r)
                .ToList();
            Flush();
        }
    }

    /// <summary>Forgets every record for one hardware id: the device has been handed back.</summary>
    public void Remove(string hardwareId)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => string.Equals(r.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase));
            Flush();
        }
    }

    /// <summary>Forgets one device's record: that device specifically has been handed back.</summary>
    public void RemoveInstance(string instanceId)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => string.Equals(r.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
            Flush();
        }
    }

    private List<PriorBindingRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new List<PriorBindingRecord>();

            string json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return new List<PriorBindingRecord>();

            var records = JsonSerializer.Deserialize<List<PriorBindingRecord>>(json, JsonOptions);
            if (records is null || records.Count == 0)
                return new List<PriorBindingRecord>();

            _log?.Write(LogLevel.Info, LogEvent.DeviceHandedBack,
                $"{records.Count} prior driver binding(s) recorded by an earlier run are being " +
                $"read back from {FilePath}",
                new Dictionary<string, object?> { ["file"] = FilePath, ["records"] = records.Count });

            return records;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Reported, not fatal: the file is a note to ourselves, and a server that will not
            // start because it cannot read one is a worse failure than losing the note.
            _log?.Write(LogLevel.Error, LogEvent.DeviceCaptureRefused,
                $"the prior-binding record at {FilePath} could not be read ({ex.Message}); " +
                "a device the server moved in an earlier run cannot be handed back automatically");
            return new List<PriorBindingRecord>();
        }
    }

    /// <summary>
    /// Writes the whole list out, atomically with respect to readers: the temporary file is
    /// complete before it replaces the real one.
    /// </summary>
    private void Flush()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            if (_records.Count == 0)
            {
                // No outstanding handback: the file's absence is the honest state, and it keeps
                // an empty file from being mistaken for a record that failed to parse.
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }

            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_records, JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Write(LogLevel.Error, LogEvent.DeviceCaptureRefused,
                $"the prior-binding record at {FilePath} could not be written ({ex.Message}); " +
                "if this server moves a device now, an earlier run's record is all that can hand it back");
        }
    }
}
