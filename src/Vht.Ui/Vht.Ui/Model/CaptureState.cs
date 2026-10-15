using System;

namespace Vht.Ui.Model;

/// <summary>
/// Wire form of the frozen capture-state enum (docs/ARCHITECTURE.md Appendix C).
/// The UI keeps its own copy of the enum on purpose: the tray must compile while the
/// server/client/config subsystem cards are still in flight, exactly as Appendix A
/// note 1 anticipates. The <em>names</em> are the contract; the values are not used.
/// </summary>
public enum CaptureState
{
    Capturable,
    UnsafeToExport,
    CaptureUnsupported,
    RebindOffered,
    AlreadyAttached,
    Ignored,
}

/// <summary>
/// Wire form of the frozen capture-reason enum (docs/ARCHITECTURE.md Appendix C section 4.3).
/// Every member here maps to one documented user-visible explanation, and every one of them
/// is surfaced by the UI: a device the server cannot export is never silently missing.
/// </summary>
public enum CaptureReason
{
    None,
    SystemCritical,
    HostsRunningOs,
    ManagementNetwork,
    DriverHoldsDevice,
    NoWinUsbInterface,
    IsoUnsupported,
    Policy,
}

/// <summary>Human wording for the frozen enums. Never invents a state the enum lacks.</summary>
public static class CaptureText
{
    public static CaptureState ParseState(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out CaptureState parsed)
            ? parsed
            : CaptureState.CaptureUnsupported;
    }

    public static CaptureReason ParseReason(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out CaptureReason parsed)
            ? parsed
            : CaptureReason.None;
    }

    public static string DescribeReason(CaptureReason reason)
    {
        return reason switch
        {
            CaptureReason.None => "No blocking reason reported.",
            CaptureReason.SystemCritical =>
                "System-critical input device: Windows will not release the system keyboard or mouse.",
            CaptureReason.HostsRunningOs =>
                "This device hosts the running operating system or page file.",
            CaptureReason.ManagementNetwork =>
                "Exporting this network adapter would cut the link this server is managed over.",
            CaptureReason.DriverHoldsDevice =>
                "The device's function driver holds it exclusively and will not release it.",
            CaptureReason.NoWinUsbInterface =>
                "No WinUSB-compatible interface is available on this device.",
            CaptureReason.IsoUnsupported =>
                "Isochronous endpoints only: export is best-effort and may drop transfers.",
            CaptureReason.Policy => "Excluded by server policy (ignore list or allow list).",
            _ => "Unrecognised capture reason reported by the service.",
        };
    }

    /// <summary>Short status shown in the device list.</summary>
    public static string StatusText(CaptureState state, CaptureReason reason, bool ignored)
    {
        if (ignored || state == CaptureState.Ignored)
        {
            return "Ignored";
        }

        return state switch
        {
            CaptureState.Capturable => "Exportable",
            CaptureState.RebindOffered => "Exportable after a rebind - " + ShortReason(reason),
            CaptureState.AlreadyAttached => "Exportable (attached elsewhere)",
            CaptureState.UnsafeToExport => "Not exportable - " + ShortReason(reason),
            CaptureState.CaptureUnsupported => "Cannot capture - " + ShortReason(reason),
            CaptureState.Ignored => "Ignored",
            _ => "Unknown",
        };
    }

    private static string ShortReason(CaptureReason reason)
    {
        return reason switch
        {
            CaptureReason.SystemCritical => "system-critical input device",
            CaptureReason.HostsRunningOs => "hosts the running OS",
            CaptureReason.ManagementNetwork => "management network adapter",
            CaptureReason.DriverHoldsDevice => "driver holds the device",
            CaptureReason.NoWinUsbInterface => "no WinUSB interface",
            CaptureReason.IsoUnsupported => "isochronous endpoints",
            CaptureReason.Policy => "excluded by policy",
            CaptureReason.None => "no reason reported",
            _ => "unrecognised reason",
        };
    }
}
