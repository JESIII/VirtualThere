using System.Collections.Concurrent;
using Vht.Core.Protocol;

namespace Vht.Core.Concurrency;

/// <summary>
/// Tracks the URBs a single connection has in flight, so an
/// <c>USBIP_CMD_UNLINK</c> can cancel exactly the transfer it names and nothing else.
/// </summary>
/// <remarks>
/// This is the bookkeeping USB/IP's unlink semantics require, and the thing the reference
/// product logs as <c>N outstanding in-flight URBs</c> (RECON_AND_SPEC.md §8). It is
/// <b>per connection</b>: one device's stalled stream cannot delay another's, and the cap in
/// <see cref="ConnectionResources"/> is a resource guard on a single connection, never a limit
/// on how many devices the server may export or attach (ARCHITECTURE.md §5.3).
/// </remarks>
public sealed class InFlightUrbTable
{
    private sealed class Entry
    {
        public required uint SeqNum { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required ushort Endpoint { get; init; }
        public int Completed;

        /// <summary>Set when a <c>USBIP_CMD_UNLINK</c> cancelled this URB by name.</summary>
        public int Unlinked;
    }

    private readonly ConcurrentDictionary<uint, Entry> _entries = new();

    /// <summary>Number of URBs currently in flight on this connection.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Registers a URB. Returns <c>false</c> when the same sequence number is already in
    /// flight, which is a client-side protocol error rather than something to tolerate.
    /// </summary>
    public bool TryRegister(uint seqNum, ushort endpoint, out CancellationTokenSource cancellation)
    {
        var entry = new Entry
        {
            SeqNum = seqNum,
            Endpoint = endpoint,
            Cancellation = new CancellationTokenSource(),
        };

        if (!_entries.TryAdd(seqNum, entry))
        {
            entry.Cancellation.Dispose();
            cancellation = null!;
            return false;
        }

        cancellation = entry.Cancellation;
        return true;
    }

    /// <summary>Marks a URB complete and releases its registration.</summary>
    public bool Complete(uint seqNum)
    {
        if (!_entries.TryRemove(seqNum, out Entry? entry))
            return false;

        Dispose(entry);
        return true;
    }

    /// <summary>
    /// Cancels the URB named by a <c>USBIP_CMD_UNLINK</c>. Returns <c>true</c> when a live URB
    /// was cancelled, <c>false</c> when it had already completed (the caller then answers the
    /// unlink with status 0, which is what the kernel does for an unlink that arrives after
    /// the transfer finished).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Cancel"/> because a URB cancelled <i>by name</i> must not also
    /// emit its own <c>USBIP_RET_SUBMIT</c>: the unlink reply is the only answer that request
    /// gets (usbip_protocol.rst; <c>stub_recv_cmd_unlink</c> repurposes the pending request so
    /// that <c>stub_tx</c> sends a <c>RET_UNLINK</c> and nothing else).
    /// </remarks>
    public bool CancelForUnlink(uint seqNum)
    {
        if (!_entries.TryGetValue(seqNum, out Entry? entry))
            return false;

        Interlocked.Exchange(ref entry.Unlinked, 1);
        try
        {
            entry.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        return true;
    }

    /// <summary>True when the URB is in flight and was cancelled by a <c>CMD_UNLINK</c>.</summary>
    public bool IsUnlinked(uint seqNum) =>
        _entries.TryGetValue(seqNum, out Entry? entry) && Volatile.Read(ref entry.Unlinked) != 0;

    /// <summary>
    /// Cancels the URB named by <paramref name="seqNum"/> if it is still in flight.
    /// Returns <c>true</c> when a live URB was cancelled; <c>false</c> means it had already
    /// completed.
    /// </summary>
    public bool Cancel(uint seqNum)
    {
        if (!_entries.TryGetValue(seqNum, out Entry? entry))
            return false;

        try
        {
            entry.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        return true;
    }

    /// <summary>Cancels every in-flight URB on this connection (teardown, kick, unplug).</summary>
    public int CancelAll()
    {
        int cancelled = 0;
        foreach (Entry entry in _entries.Values)
        {
            try
            {
                entry.Cancellation.Cancel();
                cancelled++;
            }
            catch (ObjectDisposedException)
            {
            }
        }
        return cancelled;
    }

    /// <summary>Sequence numbers currently in flight.</summary>
    public IReadOnlyList<uint> Outstanding() => _entries.Keys.ToArray();

    /// <summary>Releases every registration without cancelling (used during graceful teardown).</summary>
    public void Clear()
    {
        foreach (uint seqNum in _entries.Keys.ToArray())
        {
            if (_entries.TryRemove(seqNum, out Entry? entry))
                Dispose(entry);
        }
    }

    private static void Dispose(Entry entry)
    {
        if (Interlocked.Exchange(ref entry.Completed, 1) != 0) return;
        try
        {
            entry.Cancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// Per-connection resource guard.
/// </summary>
/// <remarks>
/// Bounds unmanaged memory, not device count. The formula the README documents is
/// <c>in_flight_cap × max_transfer_size</c> per endpoint, if every bound were simultaneously
/// in flight — which is a deliberately pessimistic ceiling, and is the reason the numbers here
/// are small while the device count is unbounded (ARCHITECTURE.md §5.4 "Practical ceiling").
/// </remarks>
public sealed class ConnectionResources
{
    public ConnectionResources(int maxInFlightUrbs = 256, int maxTransferBuffer = UsbIp.MaxTransferBuffer)
    {
        MaxInFlightUrbs = maxInFlightUrbs > 0
            ? maxInFlightUrbs
            : throw new ArgumentOutOfRangeException(nameof(maxInFlightUrbs));
        MaxTransferBuffer = maxTransferBuffer > 0
            ? maxTransferBuffer
            : throw new ArgumentOutOfRangeException(nameof(maxTransferBuffer));
    }

    /// <summary>Maximum URBs one connection may have outstanding at once.</summary>
    public int MaxInFlightUrbs { get; }

    /// <summary>Maximum size of a single transfer buffer on one connection.</summary>
    public int MaxTransferBuffer { get; }

    /// <summary>
    /// Checks whether a submit may proceed. This is the <see cref="BindStatus.ClientLimit"/>
    /// guard: a refusal here is about the connection's queue depth, never about how many
    /// devices exist or are attached.
    /// </summary>
    public BindStatus Check(int inFlightUrbs, uint transferBufferLength)
    {
        if (transferBufferLength > MaxTransferBuffer)
            return BindStatus.ClientLimit;
        if (inFlightUrbs >= MaxInFlightUrbs)
            return BindStatus.ClientLimit;
        return BindStatus.Success;
    }

    /// <summary>Worst-case buffer footprint of one connection, in bytes.</summary>
    public long WorstCaseFootprint => (long)MaxInFlightUrbs * MaxTransferBuffer;
}
