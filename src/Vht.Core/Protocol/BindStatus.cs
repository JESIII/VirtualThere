namespace Vht.Core.Protocol;

/// <summary>
/// The complete set of ways a bind (a client attaching to an exported device) can be
/// refused. Frozen in ARCHITECTURE.md §5.4 / Appendix C; every card shares this shape.
/// </summary>
public enum BindStatus
{
    /// <summary>The device is now bound to this connection.</summary>
    Success = 0,

    /// <summary>Malformed request or an internal fault while binding.</summary>
    Error,

    /// <summary>Shared secret or device password rejected.</summary>
    Unauthorized,

    /// <summary>A device password must be supplied before this device can be used.</summary>
    PasswordRequired,

    /// <summary>
    /// One client per device. Behavioural parity with the reference product's
    /// "in use elsewhere" state; not a device-count limit.
    /// </summary>
    DeviceBusy,

    /// <summary>
    /// Per-connection URB/transfer guard (in-flight cap, buffer budget). This is a
    /// *connection* resource limit, never a cap on how many devices may be exported
    /// or attached.
    /// </summary>
    ClientLimit,

    /// <summary>Unclassified refusal; the accompanying message carries the detail.</summary>
    Unknown,

    // ---------------------------------------------------------------------------------
    // NOTE: there is deliberately no MaxDevices member.
    //
    // The reference product's equivalent slot is BIND MAX_DEVICES_EXCEEDED
    // (0x1409688e8), reached through the status table at 0x1409689b0 whose slot
    // 0x1409689d0 points at that string, with the effective cap formatted at
    // 0x14074c669 (`max_devices=%d`) / 0x14074c690 (`max_devices=unlimited`) out of the
    // licence blob seeded at 0x140980736.
    //
    // VirtualThere has no licence blob, no cap value, and no branch to add one. Adding a
    // member here would require adding the concept it names; the invariant test in
    // tests/Vht.Core.Tests/BindStatusInvariantTests.cs fails the build's test run if a
    // member matching Max.*Devices or Device.*Limit ever appears.
    // ---------------------------------------------------------------------------------
}

/// <summary>
/// Why a device is or is not being exported. Frozen in ARCHITECTURE.md Appendix C.
/// </summary>
public enum CaptureState
{
    /// <summary>Capturable (or already captured) and exportable.</summary>
    Capturable,

    /// <summary>Exporting this device could damage the running system (see CaptureReason).</summary>
    UnsafeToExport,

    /// <summary>User-mode capture is not possible for this device (see CaptureReason).</summary>
    CaptureUnsupported,

    /// <summary>
    /// A function driver holds the device, but the operator opted into the P2 rebind path
    /// (<c>capture.rebind.enabled</c> with <c>capture.mode = "auto"</c>), so the server may
    /// claim it by moving it onto the inbox <c>winusb.sys</c> at bind time (ARCHITECTURE.md
    /// §4.1). Distinct from <see cref="CaptureUnsupported"/> on purpose: that state means
    /// "never", this one means "only after a rebind the operator has already permitted".
    /// Carries <see cref="CaptureReason.DriverHoldsDevice"/>.
    /// </summary>
    RebindOffered,

    /// <summary>Currently bound to a client.</summary>
    AlreadyAttached,

    /// <summary>Hidden from the export list by policy.</summary>
    Ignored,
}

/// <summary>
/// Machine-readable reason accompanying a <see cref="CaptureState"/>. Frozen in
/// ARCHITECTURE.md Appendix C; surfaced to operators instead of silently skipping a device.
/// </summary>
public enum CaptureReason
{
    None = 0,

    /// <summary>Boot/system keyboard or mouse: Windows will not release these.</summary>
    SystemCritical,

    /// <summary>The device hosts the running OS (disk/pagefile); rebinding would be fatal.</summary>
    HostsRunningOs,

    /// <summary>The management NIC; exporting it cuts the link the export runs over.</summary>
    ManagementNetwork,

    /// <summary>A function driver holds the device and refuses to release it.</summary>
    DriverHoldsDevice,

    /// <summary>No WinUSB-reachable device interface is exposed for this device.</summary>
    NoWinUsbInterface,

    /// <summary>Only isochronous endpoints and ISO is not negotiated for this peer.</summary>
    IsoUnsupported,

    /// <summary>Excluded by the operator's allow/ignore policy.</summary>
    Policy,
}

/// <summary>Helpers for <see cref="CaptureState"/> / <see cref="CaptureReason"/>.</summary>
public static class CaptureStates
{
    /// <summary>True when the device can actually be exported to a client right now.</summary>
    public static bool IsExportable(this CaptureState state) =>
        state is CaptureState.Capturable or CaptureState.AlreadyAttached;

    /// <summary>
    /// True when the state means "the operator permitted the P2 rebind, so this device can be
    /// moved onto WinUSB and then claimed". The rebind itself happens at claim time, never
    /// during enumeration (ARCHITECTURE.md §4.1), so this is a different question from
    /// <see cref="IsExportable"/>.
    /// </summary>
    public static bool OffersRebind(this CaptureState state) =>
        state is CaptureState.RebindOffered;

    /// <summary>
    /// True when a client may be given this device: it is exportable now, or the operator
    /// permitted the rebind that makes it exportable. Every gate between enumeration and the
    /// capture provider must ask this question, not <see cref="IsExportable"/> - asking the
    /// latter in the provider is what made the P2 path unreachable.
    /// </summary>
    public static bool IsClaimable(this CaptureState state) =>
        state.IsExportable() || state.OffersRebind();

    /// <summary>
    /// The operator-facing explanation for a refusal, in the same vocabulary the
    /// acceptance matrix uses. Never returns an empty string for a non-None reason.
    /// </summary>
    public static string Describe(this CaptureReason reason) => reason switch
    {
        CaptureReason.None => "none",
        CaptureReason.SystemCritical => "system_critical",
        CaptureReason.HostsRunningOs => "hosts_running_os",
        CaptureReason.ManagementNetwork => "management_network",
        CaptureReason.DriverHoldsDevice => "driver_holds_device",
        CaptureReason.NoWinUsbInterface => "no_winusb_interface",
        CaptureReason.IsoUnsupported => "iso_unsupported",
        CaptureReason.Policy => "policy",
        _ => "unknown",
    };
}
