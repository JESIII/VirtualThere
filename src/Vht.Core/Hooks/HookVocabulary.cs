using Vht.Core.Model;

namespace Vht.Core.Hooks;

/// <summary>
/// The hook vocabulary, kept identical to the reference product's so configuration and
/// operator knowledge transfer (ARCHITECTURE.md Appendix C).
/// </summary>
/// <remarks>
/// Every hook below is unconditional in VirtualThere. The reference product gates
/// <c>OnDeviceKick</c>, <c>OnDeviceIgnore</c>, <c>OnDeviceUnignore</c>,
/// <c>onClientConnect</c>, <c>onClientDisconnect</c>, <c>OnServerRename</c>,
/// <c>ClientAuthorization</c>/<c>ClientDeauthorization</c> and the reverse-client hooks behind
/// a licence (RECON_AND_SPEC.md §1.3). There is no gate function here and no licence state to
/// consult; a hook that is configured runs.
/// </remarks>
public enum HookEvent
{
    /// <summary>Free in the reference product. A device is being bound to a client.</summary>
    OnBind,

    /// <summary>Free. A device is being released.</summary>
    OnUnbind,

    /// <summary>Free. The export list was (re)enumerated.</summary>
    OnEnumeration,

    /// <summary>Free. A device-level reset is starting.</summary>
    OnReset,

    /// <summary>Free. A pipe halt is being cleared.</summary>
    OnClearHalt,

    /// <summary>Free. An endpoint is being reset.</summary>
    OnResetEp,

    /// <summary>Free. A physical device disappeared.</summary>
    OnDeviceUnplug,

    /// <summary>Free. A device is being handed back to its original driver.</summary>
    OnPassToKernel,

    /// <summary>Free. A device nickname changed.</summary>
    OnChangeNickname,

    /// <summary>Licence-gated in the reference product; unconditional here.</summary>
    OnClientConnect,

    /// <summary>Licence-gated in the reference product; unconditional here.</summary>
    OnClientDisconnect,

    /// <summary>Licence-gated in the reference product; unconditional here. A client asked to be detached.</summary>
    OnDeviceKick,

    /// <summary>Licence-gated in the reference product; unconditional here.</summary>
    OnDeviceIgnore,

    /// <summary>Licence-gated in the reference product; unconditional here.</summary>
    OnDeviceUnignore,

    /// <summary>Licence-gated in the reference product; unconditional here.</summary>
    OnServerRename,

    /// <summary>Asked to authorise a client identity before allowing a bind.</summary>
    ClientAuthorization,

    /// <summary>An authorised client is being deauthorised.</summary>
    ClientDeauthorization,
}

/// <summary>Hook names, substitution variables and the exit-code ABI.</summary>
public static class HookVocabulary
{
    /// <summary>The exact script name of a hook, as it appears in a script path or config key.</summary>
    public static string Name(this HookEvent hook) => hook switch
    {
        HookEvent.OnBind => "onBind",
        HookEvent.OnUnbind => "onUnbind",
        HookEvent.OnEnumeration => "onEnumeration",
        HookEvent.OnReset => "onReset",
        HookEvent.OnClearHalt => "onClearHalt",
        HookEvent.OnResetEp => "onResetEp",
        HookEvent.OnDeviceUnplug => "onDeviceUnplug",
        HookEvent.OnPassToKernel => "onPassToKernel",
        HookEvent.OnChangeNickname => "onChangeNickname",
        HookEvent.OnClientConnect => "onClientConnect",
        HookEvent.OnClientDisconnect => "onClientDisconnect",
        HookEvent.OnDeviceKick => "OnDeviceKick",
        HookEvent.OnDeviceIgnore => "OnDeviceIgnore",
        HookEvent.OnDeviceUnignore => "OnDeviceUnignore",
        HookEvent.OnServerRename => "OnServerRename",
        HookEvent.ClientAuthorization => "ClientAuthorization",
        HookEvent.ClientDeauthorization => "ClientDeauthorization",
        _ => hook.ToString(),
    };

    /// <summary>Parses a hook name, case-insensitively.</summary>
    public static bool TryParse(string name, out HookEvent hook)
    {
        foreach (HookEvent candidate in Enum.GetValues<HookEvent>())
        {
            if (string.Equals(candidate.Name(), name, StringComparison.OrdinalIgnoreCase))
            {
                hook = candidate;
                return true;
            }
        }
        hook = default;
        return false;
    }

    /// <summary>Exit code meaning "yes" / "allow" / "return -1" in the reference ABI.</summary>
    public const int ExitYes = 1;

    /// <summary>Exit code meaning "no" / "deny" / "return 0".</summary>
    public const int ExitNo = 0;

    /// <summary>
    /// Hooks whose result can depend on <b>who the client is</b>. These are refused on an
    /// unauthenticated plaintext connection rather than run with a spoofable identity
    /// (ARCHITECTURE.md risk 9, §6.4).
    /// </summary>
    public static bool RequiresAuthenticatedPeer(this HookEvent hook) =>
        hook is HookEvent.ClientAuthorization or HookEvent.ClientDeauthorization
            or HookEvent.OnClientConnect or HookEvent.OnClientDisconnect;
}

/// <summary>
/// Context passed to a hook: the substitution values plus the device it concerns.
/// </summary>
public sealed class HookContext
{
    /// <summary>Per-connection id, substituted for <c>$CLIENT_ID$</c>.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Client address, substituted for <c>$CLIENT_IP$</c>.</summary>
    public string ClientIp { get; init; } = string.Empty;

    /// <summary>Current nickname, substituted for <c>$NICKNAME$</c>.</summary>
    public string Nickname { get; init; } = string.Empty;

    /// <summary>New nickname, substituted for <c>$NEW_NICKNAME$</c>.</summary>
    public string NewNickname { get; init; } = string.Empty;

    /// <summary>Device path, substituted for <c>$DEVPATH$</c>.</summary>
    public string DevicePath { get; init; } = string.Empty;

    /// <summary>Vendor id as <c>%04x</c>, substituted for <c>$VENDOR_ID$</c>.</summary>
    public string VendorId { get; init; } = string.Empty;

    /// <summary>Product id as <c>%04x</c>, substituted for <c>$PRODUCT_ID$</c>.</summary>
    public string ProductId { get; init; } = string.Empty;

    /// <summary>The device the event concerns, when there is one.</summary>
    public UsbDevice? Device { get; init; }

    /// <summary>True when the originating connection proved its identity (TLS listener).</summary>
    public bool PeerAuthenticated { get; init; }

    /// <summary>Builds a context from a device and connection facts.</summary>
    public static HookContext For(UsbDevice device, string clientId = "", string clientIp = "",
        bool peerAuthenticated = false) => new()
        {
            Device = device,
            ClientId = clientId,
            ClientIp = clientIp,
            DevicePath = device.SysPath,
            VendorId = $"{device.VendorId:x4}",
            ProductId = $"{device.ProductId:x4}",
            Nickname = device.Nickname ?? string.Empty,
            PeerAuthenticated = peerAuthenticated,
        };
}

/// <summary>Outcome of running one hook.</summary>
public readonly struct HookResult
{
    /// <summary>Explicit parameterless constructor: the initialized properties below require one.</summary>
    public HookResult()
    {
        Allow = false;
        Detail = string.Empty;
        Output = string.Empty;
    }

    /// <summary>True when the hook answered "yes", or when the event ignores the answer.</summary>
    public bool Allow { get; init; }

    /// <summary>Process exit code; <c>null</c> when the hook did not run.</summary>
    public int? ExitCode { get; init; }

    /// <summary>True when the hook was not run at all (not configured, refused, timed out).</summary>
    public bool Skipped { get; init; }

    /// <summary>True when the exit code was neither yes nor no.</summary>
    public bool UnknownExitCode { get; init; }

    /// <summary>Why the hook was skipped, or the raw failure detail.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Captured stdout/stderr, for the log.</summary>
    public string Output { get; init; } = string.Empty;

    public static HookResult NotConfigured => new() { Allow = true, Skipped = true, Detail = "no script configured" };

    public static HookResult Refused(string why) => new() { Allow = false, Skipped = true, Detail = why };

    public override string ToString() =>
        Skipped ? $"skipped ({Detail})" : $"exit={ExitCode} allow={Allow}{(UnknownExitCode ? " (unknown exit code)" : string.Empty)}";
}
