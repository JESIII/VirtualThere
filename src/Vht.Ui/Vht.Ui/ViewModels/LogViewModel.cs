using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vht.Ui.ControlPlane;
using Vht.Ui.Mvvm;

namespace Vht.Ui.ViewModels;

/// <summary>
/// The log viewer behind both windows: a rolling buffer fed by <c>{"verb":"log.tail"}</c>,
/// with Clear / Copy to clipboard / Close exactly as the reference product's message window
/// offers, but with no vendor text and no licence surface.
/// </summary>
/// <remarks>
/// "Clear" is a UI-side operation: the buffer is emptied and a sequence high-water mark is
/// recorded so re-tailing the same history does not put the cleared lines straight back.
/// </remarks>
public sealed class LogViewModel : ObservableObject
{
    private const int MaxLines = 4000;
    private const int TailRequest = 400;

    private readonly VhtServiceClient _client;
    private readonly StringBuilder _copyBuffer = new();
    private readonly List<string> _lastTail = new();
    private bool _isVisible = true;
    private string _filter = string.Empty;

    public LogViewModel(VhtServiceClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ClearCommand = new RelayCommand(Clear);
        CopyCommand = new RelayCommand(CopyToClipboard);
        CloseCommand = new RelayCommand(() => IsVisible = false);
        OpenCommand = new RelayCommand(() => IsVisible = true);
    }

    /// <summary>Lines actually shown: the buffer, oldest first.</summary>
    public ObservableCollection<string> Lines { get; } = new();

    public RelayCommand ClearCommand { get; }

    public RelayCommand CopyCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand OpenCommand { get; }

    public Func<string, bool>? ClipboardWriter { get; set; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (Set(ref _isVisible, value))
            {
                Raise(nameof(VisibilityText));
            }
        }
    }

    public string VisibilityText => _isVisible ? "Hide log" : "Show log";

    /// <summary>Case-insensitive substring filter applied to newly arriving lines.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    public int Count => Lines.Count;

    /// <summary>
    /// Pulls the service's tail and appends only what is new. Deduplication is by overlap with
    /// the previous tail rather than by sequence number, so a service that reports plain strings
    /// behaves identically to one that reports sequenced records - and a tail window that
    /// scrolls produces no duplicates. A tail window that has rolled entirely out of the
    /// service's history (a burst larger than the window between two polls) can re-append; the
    /// window is deliberately generous to make that rare.
    /// </summary>
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        (ControlPlaneStatus status, IReadOnlyList<LogEntry> entries, ControlPlaneError? error) =
            await _client.TailLogAsync(TailRequest, cancellationToken).ConfigureAwait(true);

        if (status != ControlPlaneStatus.Ok)
        {
            LastError = error?.Message;
            return;
        }

        LastError = null;

        var incoming = new List<string>(entries.Count);
        foreach (LogEntry entry in entries)
        {
            incoming.Add(entry.Text);
        }

        AppendTail(incoming);
    }

    private void AppendTail(IReadOnlyList<string> incoming)
    {
        int overlap = 0;
        int max = System.Math.Min(_lastTail.Count, incoming.Count);
        for (int k = max; k > 0; k--)
        {
            if (SuffixMatches(incoming, k))
            {
                overlap = k;
                break;
            }
        }

        for (int i = overlap; i < incoming.Count; i++)
        {
            Append(incoming[i]);
        }

        _lastTail.Clear();
        _lastTail.AddRange(incoming);
    }

    private bool SuffixMatches(IReadOnlyList<string> incoming, int length)
    {
        int offset = _lastTail.Count - length;
        for (int i = 0; i < length; i++)
        {
            if (!string.Equals(_lastTail[offset + i], incoming[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!Matches(text))
        {
            return;
        }

        Lines.Add(text);
        _copyBuffer.AppendLine(text);

        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }

    public void AppendLocal(string text) => Append("UI " + DateTime.Now.ToString("HH:mm:ss") + " " + text);

    private bool Matches(string text) =>
        _filter.Length == 0 || text.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private void Rebuild()
    {
        var all = new List<string>(_copyBuffer.Length > 0 ? 64 : 0);
        using (var reader = new System.IO.StringReader(_copyBuffer.ToString()))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                all.Add(line);
            }
        }

        Lines.Clear();
        foreach (string line in all)
        {
            if (Matches(line))
            {
                Lines.Add(line);
            }
        }
    }

    public void Clear()
    {
        Lines.Clear();
        lock (_copyBuffer)
        {
            _copyBuffer.Clear();
        }
    }

    public string Snapshot()
    {
        lock (_copyBuffer)
        {
            return _copyBuffer.ToString();
        }
    }

    public void CopyToClipboard()
    {
        string text = Snapshot();
        if (ClipboardWriter is not null)
        {
            _ = ClipboardWriter(text);
        }
    }

    public string? LastError { get; private set; }
}
