using System.Collections.Concurrent;
using System.Text;
using Vht.Core.Logging;

namespace Vht.Server.Logging;

/// <summary>
/// Keeps the most recent log lines in memory so the control plane's <c>log.tail</c> verb can
/// serve them without reading the rolling file.
/// </summary>
/// <remarks>
/// Bounded by construction (<see cref="Capacity"/> lines), because an unbounded diagnostics
/// buffer in a long-running service is a memory leak with extra steps.
/// </remarks>
public sealed class RingBufferLogSink : ILogSink
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly int _capacity;

    public RingBufferLogSink(int capacity = 500)
    {
        _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    /// <summary>Maximum number of retained lines.</summary>
    public int Capacity => _capacity;

    /// <summary>Lines currently retained.</summary>
    public int Count => _lines.Count;

    /// <inheritdoc />
    public void Write(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _lines.Enqueue(record.ToText());
        while (_lines.Count > _capacity && _lines.TryDequeue(out _))
        {
        }
    }

    /// <summary>The most recent <paramref name="count"/> lines, oldest first.</summary>
    public IReadOnlyList<string> Tail(int count)
    {
        string[] all = _lines.ToArray();
        if (count <= 0 || all.Length <= count)
            return all;

        return all[^count..];
    }

    /// <summary>Every retained line as one string, for a control-plane reply.</summary>
    public string TailText(int count)
    {
        var builder = new StringBuilder();
        foreach (string line in Tail(count))
            builder.Append(line).Append('\n');

        return builder.ToString();
    }
}
