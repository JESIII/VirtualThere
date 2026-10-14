using System.Collections.Concurrent;
using Vht.Config.Diagnostics;

namespace Vht.Config.Reload;

/// <summary>
/// Watches config.toml / state.toml and reports a debounced change signal. Editors write in several
/// steps (truncate, write, rename), so a raw FileSystemWatcher fires 2-4 times per save; the debounce
/// window collapses that into one reload. <see cref="Trigger"/> exists so tests (and the CLI's
/// "\u200breload" verb) can drive the same path without touching the file system.
/// </summary>
public sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConfigDiagnostic> _diagnostics = new();
    private readonly object _gate = new();
    private readonly int _debounceMilliseconds;
    private TaskCompletionSource<bool> _signal = NewSignal();
    private CancellationTokenSource? _debounce;
    private bool _disposed;

    public ConfigWatcher(string directory, IEnumerable<string>? files = null, int debounceMilliseconds = 250)
    {
        DirectoryPath = directory;
        _debounceMilliseconds = debounceMilliseconds;
        Files = (files ?? new[] { ConfigPathResolver.ConfigFileName, ConfigPathResolver.StateFileName }).ToList();

        if (!Directory.Exists(directory))
        {
            _diagnostics.Add(new ConfigDiagnostic(
                ConfigDiagnosticSeverity.Warning,
                ConfigDiagnosticCode.PathInvalid,
                $"configuration directory '{directory}' does not exist yet; live reload is disabled until it does",
                File: directory));
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Deleted += OnChanged;
        }
        catch (Exception ex)
        {
            _diagnostics.Add(new ConfigDiagnostic(
                ConfigDiagnosticSeverity.Warning,
                ConfigDiagnosticCode.ReadError,
                $"configuration watching is unavailable ({ex.GetType().Name}: {ex.Message}); changes will only be picked up on the next start",
                File: directory));
        }
    }

    public string DirectoryPath { get; }

    public IReadOnlyList<string> Files { get; }

    /// <summary>True when the OS watcher is active; false means callers must fall back to a manual reload.</summary>
    public bool IsWatching => _watcher is not null;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Paths signalled since the last <see cref="WaitForChangeAsync"/> completion.</summary>
    public IReadOnlyCollection<string> ChangedFiles => _pending.Keys.ToList();

    public async Task<IReadOnlyList<string>> WaitForChangeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            linked.CancelAfter(limit);
        }

        var signal = _signal;
        try
        {
            await signal.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<string>();
        }

        lock (_gate)
        {
            var changed = _pending.Keys.ToList();
            _pending.Clear();
            _signal = NewSignal();
            return changed;
        }
    }

    /// <summary>Signals a change as if the file system had done it (tests, CLI reload verb).</summary>
    public void Trigger(string? file = null)
    {
        _pending[file ?? ConfigPathResolver.ConfigFileName] = 0;
        var signal = _signal;
        if (!signal.Task.IsCompleted)
        {
            signal.TrySetResult(true);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (args.Name is null || !Files.Any(f => f.Equals(args.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            var token = _debounce.Token;
            _ = Task.Delay(_debounceMilliseconds, token).ContinueWith(
                _ => Trigger(args.Name),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Renamed -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Dispose();
        }

        _debounce?.Cancel();
        _debounce?.Dispose();
        _signal.TrySetResult(false);
    }
}