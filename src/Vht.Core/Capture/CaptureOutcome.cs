using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Vht.Core.Model;

namespace Vht.Core.Capture;

/// <summary>
/// What the last capture/rebind attempt for one device actually did, in the vocabulary the
/// capture provider already distinguishes. This is the operator-facing per-device status the
/// server window shows: a device whose P2 rebind was refused, failed, or only lands after a
/// reboot must say so on its row instead of sitting there silently (or missing).
/// </summary>
/// <remarks>
/// The wording rules use the same vocabulary as the log lines in <c>WinUsbCaptureProvider</c>
/// - the window and the runtime log must never tell two different stories about the same
/// attempt. <see cref="Path"/> names which of the capture paths ran (P1 prebound, P2 rebind,
/// or none), and <see cref="Detail"/> is a complete operator-facing sentence, not a code.
/// </remarks>
public sealed record CaptureOutcome
{
    /// <summary>The device the attempt was made for.</summary>
    public required DeviceKey Key { get; init; }

    /// <summary>Bus id, kept so the record reads well outside a table lookup.</summary>
    public required string BusId { get; init; }

    /// <summary>Which path ran: <c>P1</c>, <c>P2</c>, or <c>none</c> (refused before any path).</summary>
    public required string Path { get; init; }

    /// <summary>Short token from <see cref="CaptureOutcomeStatus"/>; the <see cref="Detail"/> text is what the operator reads.</summary>
    public required string Status { get; init; }

    /// <summary>Complete operator-facing sentence describing the outcome.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// The outcome vocabulary. These are <em>not</em> the frozen <c>CaptureState</c> members
/// (ARCHITECTURE.md Appendix C): those describe what enumeration saw, this describes what a
/// claim attempt did afterwards. The three the capture path distinguishes - and the three the
/// window must surface - are <see cref="NotElevated"/>, <see cref="RebindFailed"/> and
/// <see cref="RebootRequired"/>.
/// </summary>
public static class CaptureOutcomeStatus
{
    /// <summary>P1 claimed the device on the interface it already had.</summary>
    public const string Claimed = "claimed";

    /// <summary>P2 moved the device onto the inbox winusb.sys and the device was claimed.</summary>
    public const string Rebound = "rebound";

    /// <summary>P2 was refused before the attempt: the process is not elevated.</summary>
    public const string NotElevated = "not elevated";

    /// <summary>P2 ran and Windows refused; nothing changed.</summary>
    public const string RebindFailed = "rebind failed";

    /// <summary>P2 succeeded but only takes effect after a reboot, so the device was not taken.</summary>
    public const string RebootRequired = "reboot required";

    /// <summary>P2 moved the binding but the device still could not be claimed; it was handed back.</summary>
    public const string ClaimAfterRebindFailed = "claim after rebind failed";

    /// <summary>Refused before any capture path ran (unsafe, unclaimable, or the offer was withdrawn).</summary>
    public const string Refused = "refused";
}

/// <summary>
/// Last capture/rebind outcome per device, shared between the capture provider (which writes
/// it) and the control plane (which reports it to the server window). One entry per device,
/// replaced on every attempt; a device that was never attempted has no entry at all.
/// </summary>
public sealed class CaptureOutcomeLog
{
    private readonly ConcurrentDictionary<DeviceKey, CaptureOutcome> _outcomes = new();

    /// <summary>Records the outcome of one attempt, replacing any earlier one for the device.</summary>
    public void Record(CaptureOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _outcomes[outcome.Key] = outcome;
    }

    /// <summary>The last outcome for one device, if an attempt was ever made.</summary>
    public bool TryGet(DeviceKey key, [NotNullWhen(true)] out CaptureOutcome? outcome) =>
        _outcomes.TryGetValue(key, out outcome);

    /// <summary>Every recorded outcome, for status reporting and tests.</summary>
    public IReadOnlyList<CaptureOutcome> Snapshot() => _outcomes.Values.ToArray();

    /// <summary>
    /// Drops a device's outcome. Called when the device leaves the machine: a re-plugged
    /// device starts with no outcome rather than inheriting the previous one's story.
    /// </summary>
    public void Forget(DeviceKey key) => _outcomes.TryRemove(key, out _);
}
