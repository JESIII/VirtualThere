// SPDX-License-Identifier: Apache-2.0
//
// Client logging. Structured, small, and deliberately able to serve `log.tail` on the control plane
// (ARCHITECTURE.md App. C) without pulling in a logging framework — the client is a service-shaped
// process whose output the tray and the acceptance harness both read back.

using System.Collections.Concurrent;

namespace Vht.Client.Diagnostics;

internal enum ClientLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

internal sealed record ClientLogEntry(DateTimeOffset TimestampUtc, ClientLogLevel Level, string Category, string Message)
{
    public override string ToString() =>
        $"{TimestampUtc:yyyy-MM-dd HH:mm:ss.fff}Z {Level.ToString().ToUpperInvariant(),-7} {Category}: {Message}";
}

/// <summary>
/// Thread-safe logger: console + a bounded in-memory ring (for `log.tail`) + an optional file.
/// The ring is bounded so a runaway loop cannot exhaust memory — a log buffer is not a data store.
/// </summary>
internal sealed class ClientLog
{
    private const int RingCapacity = 2000;

    private readonly ConcurrentQueue<ClientLogEntry> _ring = new();
    private readonly object _fileLock = new();
    private readonly TextWriter? _console;
    private readonly string? _filePath;
    private int _ringCount;

    internal ClientLog(TextWriter? console = null, string? filePath = null)
    {
        _console = console;
        _filePath = filePath;
    }

    internal ClientLogLevel MinimumLevel { get; set; } = ClientLogLevel.Info;

    internal void Debug(string category, string message) => Write(ClientLogLevel.Debug, category, message);

    internal void Info(string category, string message) => Write(ClientLogLevel.Info, category, message);

    internal void Warn(string category, string message) => Write(ClientLogLevel.Warning, category, message);

    internal void Error(string category, string message) => Write(ClientLogLevel.Error, category, message);

    internal void Error(string category, string message, Exception ex) =>
        Write(ClientLogLevel.Error, category, $"{message}: {ex.GetType().Name}: {ex.Message}");

    private void Write(ClientLogLevel level, string category, string message)
    {
        var entry = new ClientLogEntry(DateTimeOffset.UtcNow, level, category, message);

        _ring.Enqueue(entry);
        if (Interlocked.Increment(ref _ringCount) > RingCapacity)
        {
            if (_ring.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _ringCount);
            }
        }

        if (level >= MinimumLevel)
        {
            _console?.WriteLine(entry);
        }

        if (_filePath is not null)
        {
            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                    File.AppendAllText(_filePath, entry + Environment.NewLine);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log target that cannot be written must never take the client down with it.
            }
        }
    }

    /// <summary>The most recent entries, oldest first — the `log.tail` verb's payload.</summary>
    internal IReadOnlyList<ClientLogEntry> Tail(int count)
    {
        if (count <= 0) return Array.Empty<ClientLogEntry>();
        ClientLogEntry[] all = _ring.ToArray();
        return all.Length <= count ? all : all[^count..];
    }
}
