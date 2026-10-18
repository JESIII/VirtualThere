using System;

namespace Vht.Ui.Model;

/// <summary>
/// One exported device as reported by <c>{"verb":"devices.list"}</c> on the vht-server
/// control pipe. Immutable snapshot: the view models rebuild on every poll.
/// </summary>
public sealed class DeviceSnapshot
{
    public required string Key { get; init; }

    public string? BusId { get; init; }

    public string? Nickname { get; init; }

    public int VendorId { get; init; }

    public int ProductId { get; init; }

    public string? Vendor { get; init; }

    public string? Product { get; init; }

    /// <summary>Number of clients currently attached to this device (0 or 1 in v1).</summary>
    public int AttachedClients { get; init; }

    public CaptureState CaptureState { get; init; }

    public CaptureReason CaptureReason { get; init; }

    public bool Ignored { get; init; }

    public bool PasswordProtected { get; init; }

    /// <summary>
    /// False for a device the server saw but will not advertise (capture-unsupported, unsafe,
    /// ignored). Such a device is still listed - with its reason - so it never silently
    /// disappears from the window.
    /// </summary>
    public bool Exported { get; init; } = true;

    /// <summary>The enumerator's operator-facing explanation for its verdict, when there is one.</summary>
    public string? CaptureDetail { get; init; }

    /// <summary>Which capture path the last attempt used: <c>P1</c>, <c>P2</c>, or <c>none</c>.</summary>
    public string? CapturePath { get; init; }

    /// <summary>
    /// What the last capture/rebind attempt did, in the vocabulary the server distinguishes -
    /// <c>not elevated</c>, <c>P2 rebind failed: &lt;detail&gt;</c>,
    /// <c>rebound ... a reboot is required ...</c>. Null when nothing was attempted yet.
    /// </summary>
    public string? CaptureOutcome { get; init; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Nickname)
            ? (string.IsNullOrWhiteSpace(Product) ? "(unnamed device)" : Product!)
            : Nickname!;

    /// <summary>Identity in the form the card requires next to the nickname.</summary>
    public string Identity => "[" + VendorProductText + "]";

    public string VendorProductText =>
        (VendorId & 0xFFFF).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) +
        ":" +
        (ProductId & 0xFFFF).ToString("x4", System.Globalization.CultureInfo.InvariantCulture);

    public string AttachedText => AttachedClients switch
    {
        0 => "none",
        1 => "1 client",
        _ => AttachedClients.ToString(System.Globalization.CultureInfo.InvariantCulture) + " clients",
    };

    public string StatusText => CaptureText.StatusText(CaptureState, CaptureReason, Ignored);

    /// <summary>
    /// The per-device capture/rebind outcome line, empty when no attempt was made. This is what
    /// tells the operator <em>which</em> of P1/P2 ran and what came of it - including the three
    /// rebind outcomes ("not elevated", "P2 rebind failed: ...", "rebound ... a reboot is
    /// required ...") that would otherwise only exist in the runtime log.
    /// </summary>
    public string OutcomeText => CaptureOutcome is null ? string.Empty : CaptureOutcome;

    /// <summary>Status tooltip: the full reason, plus the capture outcome when there is one.</summary>
    public string DetailText => CaptureOutcome is null
        ? CaptureText.DescribeReason(CaptureReason)
        : CaptureText.DescribeReason(CaptureReason) + "\n\nLast capture (" +
          (CapturePath ?? "none") + "): " + CaptureOutcome;

    public bool Exportable => !Ignored && CaptureState == CaptureState.Capturable;
}

/// <summary>A remote server the client knows about, from <c>{"verb":"peers.list"}</c>.</summary>
public sealed class PeerSnapshot
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? Address { get; init; }

    public int Port { get; init; }

    /// <summary>Free-form connection status reported by the client service.</summary>
    public string? Status { get; init; }

    public bool Online { get; init; }

    /// <summary>True for a peer that was added by hand via <c>peers.add</c>.</summary>
    public bool Manual { get; init; }

    public System.Collections.Generic.IReadOnlyList<RemoteDeviceSnapshot> Devices { get; init; } =
        Array.Empty<RemoteDeviceSnapshot>();

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? (Address ?? Id) : Name!;

    public string AddressText => string.IsNullOrWhiteSpace(Address)
        ? "(discovered)"
        : Port > 0 ? Address + ":" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : Address!;

    public string StatusText => string.IsNullOrWhiteSpace(Status)
        ? (Online ? "online" : "offline")
        : Status!;
}

/// <summary>An exported device on a remote peer, as seen by the client UI.</summary>
public sealed class RemoteDeviceSnapshot
{
    public required string BusId { get; init; }

    public string? Nickname { get; init; }

    public string? Product { get; init; }

    public int VendorId { get; init; }

    public int ProductId { get; init; }

    /// <summary>True when this device is currently attached on the local machine.</summary>
    public bool Attached { get; init; }

    /// <summary>True when the client service may attach it without asking again.</summary>
    public bool AutoAttach { get; init; }

    public bool PasswordRequired { get; init; }

    public string? Status { get; init; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Nickname)
            ? (string.IsNullOrWhiteSpace(Product) ? "(unnamed device)" : Product!)
            : Nickname!;

    public string Identity => "[" +
        (VendorId & 0xFFFF).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) + ":" +
        (ProductId & 0xFFFF).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) + "]";

    public string AttachText => Attached ? "Attached" : "Not attached";

    public string StatusText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>(3) { AttachText };
            if (AutoAttach)
            {
                parts.Add("auto-attach on");
            }

            if (PasswordRequired)
            {
                parts.Add("password required");
            }

            if (!string.IsNullOrWhiteSpace(Status))
            {
                parts.Add(Status!);
            }

            return string.Join(" - ", parts);
        }
    }
}
