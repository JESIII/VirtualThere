using Vht.Core.Model;

namespace Vht.Core.Capture;

/// <summary>
/// Enumerates local USB devices. Implemented over SetupAPI/CfgMgr32 in <c>Vht.Server</c>;
/// the unit tests substitute a fake so enumeration policy can be tested without hardware.
/// </summary>
public interface IDeviceEnumerator
{
    /// <summary>
    /// Returns every physically present USB device that is a candidate for export, together
    /// with the reason any of them cannot be exported. Implementations must not touch
    /// driver bindings.
    /// </summary>
    ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct);

    /// <summary>
    /// A monotonically increasing generation counter bumped on every hotplug event, so the
    /// export table can tell a stale descriptor cache from a fresh one.
    /// </summary>
    long Generation { get; }
}
