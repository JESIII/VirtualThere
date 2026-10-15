namespace Vht.Server.Devices;

/// <summary>
/// The P2 rebind step, as the capture provider sees it: can this process change a driver
/// binding at all, attempt it for one vid/pid, and <em>undo</em> it.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the question the provider must answer - "is the rebind attempted for this
/// device?" - is separable from the question only the machine can answer - "did the driver
/// store accept the INF?". The provider is tested against a recording implementation, which is
/// what pins the P2 path as reachable rather than merely configured; see
/// <c>tests/Vht.Acceptance.Tests/CaptureRebindPathTests.cs</c>.
/// </para>
/// <para>
/// <see cref="Rebind"/> and <see cref="Restore"/> are a pair and have to be implemented as one: a
/// rebind that cannot be undone is not a rebind path, it is a device this server took from the
/// operator. The prior binding is recorded - durably, see <see cref="PriorBindingStore"/> - before
/// the first call that can change a binding, and <see cref="Restore"/> puts the device back on it.
/// <see cref="RestorePending"/> is the same operation for whatever is left over: a run that was
/// killed mid-rebind, or a server stopping with a device still held.
/// </para>
/// <para>
/// <see cref="WinUsbRebinder"/> is the shipping implementation. Every member that changes a
/// binding requires elevation and refuses with that reason when it is absent.
/// </para>
/// </remarks>
public interface IDeviceRebinder
{
    /// <summary>
    /// True when this process holds the rights a rebind needs. A rebind is still attempted when
    /// this is false - the refusal is then reported with its reason instead of being silent
    /// (ARCHITECTURE.md §4.1, P2).
    /// </summary>
    bool CanRebind { get; }

    /// <summary>Attempts the P2 path for the device with this vid/pid.</summary>
    RebindResult Rebind(ushort vendorId, ushort productId);

    /// <summary>
    /// Hands the device with this vid/pid back to the driver it had before <see cref="Rebind"/>
    /// moved it, and removes the package that rebind staged.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="RestoreResult.NothingToDo"/> when nothing was ever recorded for this
    /// vid/pid, so a caller can invoke it on every release without telling them apart.
    /// </remarks>
    RestoreResult Restore(ushort vendorId, ushort productId);

    /// <summary>Records still awaiting a handback: devices this server moved and has not given back.</summary>
    int PendingHandbacks { get; }

    /// <summary>
    /// Hands back everything still outstanding, including records read from an earlier run. One
    /// result per device, so the caller can report each one honestly.
    /// </summary>
    IReadOnlyList<RestoreResult> RestorePending();
}
