namespace Vht.Server.Devices;

/// <summary>
/// The P2 rebind step, as the capture provider sees it: can this process change a driver
/// binding at all, and attempt it for one vid/pid.
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
/// <see cref="WinUsbRebinder"/> is the shipping implementation and the only code in the product
/// that changes a device's binding.
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
}
