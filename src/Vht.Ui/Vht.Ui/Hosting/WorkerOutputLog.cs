using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Vht.Ui.Hosting;

/// <summary>
/// The durable record of one worker launch: <c>&lt;log directory&gt;\worker-&lt;pid&gt;.log</c>.
/// </summary>
/// <remarks>
/// <para>
/// The window's in-memory tail (<see cref="WorkerOutputBuffer"/>) dies with the window, and an
/// elevated worker's streams cannot be redirected at all - so anything that must survive a worker
/// killed before it binds the control pipe has to reach a file as it is written. This is that file:
/// one per launch, named by the worker's pid, in the same default log directory the worker's own
/// runtime log uses (<c>&lt;resolved config directory&gt;\logs</c>), so both records land together.
/// </para>
/// <para>
/// Every line is stamped with the time and the worker's pid (the header names the launch itself),
/// and each line is flushed as it arrives: a worker that is killed mid-sentence leaves everything
/// it managed to write on disk. Both launch modes get the header - it is the only thing the window
/// can write for an elevated worker, whose handles <c>ShellExecuteEx</c> keeps to itself; that
/// worker's own output goes to its runtime log instead (<c>vht status</c> prints the path).
/// </para>
/// <para>
/// Capture is a diagnostic, never a dependency: a log that cannot be opened (a directory the
/// window may not create, a disk that is full) is quietly dropped and the launch proceeds exactly
/// as it did before this file existed.
/// </para>
/// </remarks>
internal sealed class WorkerOutputLog : IDisposable
{
    private readonly object _gate = new();
    private readonly int _pid;
    private StreamWriter? _writer;

    private WorkerOutputLog(string path, int pid, StreamWriter writer)
    {
        Path = path;
        _pid = pid;
        _writer = writer;
    }

    /// <summary>The file being written, for the failure message that tells an operator where to look.</summary>
    internal string Path { get; }

    /// <summary>
    /// Opens <c>worker-&lt;pid&gt;.log</c> in <paramref name="directory"/> and writes the launch
    /// header. Returns null - without throwing - when there is no directory or the file cannot be
    /// opened; the caller keeps launching either way.
    /// </summary>
    internal static WorkerOutputLog? TryCreate(string? directory, int pid, string launchRecord)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(
                directory, "worker-" + pid.ToString(CultureInfo.InvariantCulture) + ".log");

            // Appended, not truncated: a pid can be reused, and the earlier launch's record is
            // still worth keeping next to it.
            var writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };

            var log = new WorkerOutputLog(path, pid, writer);
            log.Append(launchRecord);
            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Appends one stamped line. <paramref name="isError"/> prefixes it so the two streams stay
    /// distinguishable, the same way <see cref="WorkerOutputBuffer"/> does. Null means "that stream
    /// ended" and is dropped. Failures after the file was opened are swallowed for the same reason
    /// the open failure is: a log must never take the launch - or the reader thread draining the
    /// worker's pipes - down with it.
    /// </summary>
    internal void Append(string? line, bool isError = false)
    {
        if (line is null)
        {
            return;
        }

        string text = isError ? "stderr: " + line : line;
        string stamped = Timestamp() + " [pid " + _pid.ToString(CultureInfo.InvariantCulture) + "] " + text;

        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(stamped);
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _writer = null;
            }
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

    /// <summary>UTC, to the millisecond, in the shape the rest of the product's log lines use.</summary>
    private static string Timestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";
}
