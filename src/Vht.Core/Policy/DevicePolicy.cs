using Vht.Core.Model;
using Vht.Core.Protocol;

namespace Vht.Core.Policy;

/// <summary>
/// One allow/ignore rule. Mirrors the reference product's ignore granularity
/// (RECON_AND_SPEC.md §7.5): a whole vid/pid pair, or just one device qualified by serial.
/// </summary>
public sealed record DeviceRule
{
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }

    /// <summary>When set, the rule matches only this serial number ("Just This" in the UI).</summary>
    public string? Serial { get; init; }

    /// <summary>
    /// Optional server qualifier, matching the recon's "Qualify Auto-Use by Server Name"
    /// knob: a rule recorded for one server does not leak to another.
    /// </summary>
    public string? ServerName { get; init; }

    /// <summary>True when the rule matches every serial of the vid/pid ("Ignore all devices with VendorId/ProductId").</summary>
    public bool IsVidPidWide => string.IsNullOrWhiteSpace(Serial);

    /// <summary>Does this rule apply to <paramref name="identity"/> on <paramref name="serverName"/>?</summary>
    public bool Matches(DeviceIdentity identity, string? serverName)
    {
        if (identity.VendorId != VendorId || identity.ProductId != ProductId)
            return false;

        if (!string.IsNullOrWhiteSpace(ServerName) &&
            !string.Equals(ServerName, serverName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsVidPidWide)
            return true;

        return string.Equals(Serial, identity.Serial, StringComparison.OrdinalIgnoreCase);
    }

    public string Describe() =>
        $"{(IsVidPidWide ? "vidpid" : "device")} 0x{VendorId:x4}/0x{ProductId:x4}" +
        (IsVidPidWide ? string.Empty : $" serial={Serial}") +
        (string.IsNullOrWhiteSpace(ServerName) ? string.Empty : $" server={ServerName}");

    /// <summary>Parses the recon/UI vocabulary <c>vvvv:pppp[:serial][@server]</c>.</summary>
    public static bool TryParse(string text, out DeviceRule rule)
    {
        rule = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string rest = text.Trim();
        string? server = null;
        int at = rest.IndexOf('@');
        if (at >= 0)
        {
            server = rest[(at + 1)..].Trim();
            rest = rest[..at].Trim();
        }

        string? serial = null;
        int colon = rest.IndexOf(':');
        if (colon < 0) return false;

        string vidText = rest[..colon];
        string remainder = rest[(colon + 1)..];
        colon = remainder.IndexOf(':');
        string pidText = colon < 0 ? remainder : remainder[..colon];
        if (colon >= 0) serial = remainder[(colon + 1)..].Trim();

        if (!TryParseHex16(vidText, out ushort vid) || !TryParseHex16(pidText, out ushort pid))
            return false;

        rule = new DeviceRule
        {
            VendorId = vid,
            ProductId = pid,
            Serial = string.IsNullOrWhiteSpace(serial) ? null : serial,
            ServerName = string.IsNullOrWhiteSpace(server) ? null : server,
        };
        return true;
    }

    private static bool TryParseHex16(string text, out ushort value)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return ushort.TryParse(t, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>The policy decision for one device.</summary>
public readonly struct PolicyVerdict
{
    /// <summary>Whether the device may be exported.</summary>
    public bool Allowed { get; init; }

    /// <summary><see cref="CaptureState.Ignored"/> when hidden, otherwise the device's own state.</summary>
    public CaptureState State { get; init; }

    /// <summary>Reason behind <see cref="State"/>.</summary>
    public CaptureReason Reason { get; init; }

    /// <summary>Nickname assigned to the device, if any.</summary>
    public string? Nickname { get; init; }

    /// <summary>True when a per-device password must be supplied before use.</summary>
    public bool RequiresPassword { get; init; }

    /// <summary>Whether the device is hidden from the export list.</summary>
    public bool Ignored => State == CaptureState.Ignored;

    /// <summary>Operator-facing explanation.</summary>
    public string Detail { get; init; }

    public static PolicyVerdict Allow(CaptureState state = CaptureState.Capturable)
        => new() { Allowed = true, State = state, Reason = CaptureReason.None, Detail = string.Empty };

    public static PolicyVerdict Ignore(string detail)
        => new()
        {
            Allowed = false,
            State = CaptureState.Ignored,
            Reason = CaptureReason.Policy,
            Detail = detail,
        };
}

/// <summary>
/// Per-device policy: allow list, ignore list, nicknames and per-device passwords.
/// </summary>
/// <remarks>
/// All of these are unconditional in VirtualThere. The reference product gates several of the
/// operations that edit them (<c>OnDeviceIgnore</c>, <c>OnDeviceUnignore</c>,
/// <c>OnDeviceKick</c> are licence-gated — RECON_AND_SPEC.md §1.3); there is no gate here to
/// remove because none is written. The class is thread-safe: the control plane mutates it
/// while connection threads read it.
/// </remarks>
public sealed class DevicePolicy
{
    private readonly object _gate = new();
    private readonly List<DeviceRule> _allow = new();
    private readonly List<DeviceRule> _ignore = new();
    private readonly Dictionary<string, string> _nicknames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Advertised server name, used as the qualifier for server-scoped rules.</summary>
    public string? ServerName { get; set; }

    /// <summary>Allow list. When non-empty, only matching devices are exported.</summary>
    public IReadOnlyList<DeviceRule> AllowList { get { lock (_gate) return _allow.ToArray(); } }

    /// <summary>Ignore list. Matching devices are hidden from the export list.</summary>
    public IReadOnlyList<DeviceRule> IgnoreList { get { lock (_gate) return _ignore.ToArray(); } }

    /// <summary>Adds a rule to the allow list.</summary>
    public void Allow(DeviceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            if (!_allow.Any(r => r.Matches(new DeviceIdentity(rule.VendorId, rule.ProductId, rule.Serial ?? string.Empty), rule.ServerName)))
                _allow.Add(rule);
        }
    }

    /// <summary>Removes a rule from the allow list.</summary>
    public bool RemoveAllow(DeviceRule rule)
    {
        lock (_gate) return _allow.RemoveAll(r =>
            r.VendorId == rule.VendorId && r.ProductId == rule.ProductId &&
            string.Equals(r.Serial, rule.Serial, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>Hides a device (or a whole vid/pid) from the export list.</summary>
    public void Ignore(DeviceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            bool exists = _ignore.Any(r =>
                r.VendorId == rule.VendorId && r.ProductId == rule.ProductId &&
                string.Equals(r.Serial ?? string.Empty, rule.Serial ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (!exists) _ignore.Add(rule);
        }
    }

    /// <summary>Un-hides a device.</summary>
    public bool Unignore(DeviceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            return _ignore.RemoveAll(r =>
                r.VendorId == rule.VendorId && r.ProductId == rule.ProductId &&
                string.Equals(r.Serial ?? string.Empty, rule.Serial ?? string.Empty, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    /// <summary>True when the ignore list hides <paramref name="identity"/>.</summary>
    public bool IsIgnored(DeviceIdentity identity)
    {
        lock (_gate) return _ignore.Any(r => r.Matches(identity, ServerName));
    }

    /// <summary>Assigns or clears a device nickname. An empty value clears it.</summary>
    public void SetNickname(DeviceIdentity identity, string? nickname)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(nickname)) _nicknames.Remove(identity.ToString());
            else _nicknames[identity.ToString()] = nickname.Trim();
        }
    }

    /// <summary>Returns the device nickname, or <c>null</c>.</summary>
    public string? GetNickname(DeviceIdentity identity)
    {
        lock (_gate) return _nicknames.TryGetValue(identity.ToString(), out string? n) ? n : null;
    }

    /// <summary>All recorded nicknames, keyed by device identity.</summary>
    public IReadOnlyDictionary<string, string> Nicknames
    {
        get { lock (_gate) return new Dictionary<string, string>(_nicknames, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Records a per-device password. Only the verifier is stored, never the plaintext
    /// (ARCHITECTURE.md §6.1); an empty value removes the password.
    /// </summary>
    public void SetPasswordVerifier(DeviceIdentity identity, string? verifier)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(verifier)) _passwords.Remove(identity.ToString());
            else _passwords[identity.ToString()] = verifier;
        }
    }

    /// <summary>The stored verifier for a device, or <c>null</c> when it has no password.</summary>
    public string? GetPasswordVerifier(DeviceIdentity identity)
    {
        lock (_gate) return _passwords.TryGetValue(identity.ToString(), out string? v) ? v : null;
    }

    /// <summary>True when a password protects this device.</summary>
    public bool RequiresPassword(DeviceIdentity identity) => GetPasswordVerifier(identity) is not null;

    /// <summary>
    /// Evaluates policy for a device. Order matters and matches the product's behaviour:
    /// an explicit ignore beats an allow, an allow list that exists but does not match
    /// hides the device, and a device the OS will not release is reported as such even when
    /// policy would allow it.
    /// </summary>
    public PolicyVerdict Evaluate(UsbDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        DeviceIdentity identity = device.Identity;

        if (IsIgnored(identity))
            return PolicyVerdict.Ignore($"ignored by policy ({identity})");

        DeviceRule[] allow;
        lock (_gate) allow = _allow.ToArray();
        if (allow.Length > 0 && !allow.Any(r => r.Matches(identity, ServerName)))
            return PolicyVerdict.Ignore($"not in the allow list ({identity})");

        // A device the OS will not give us stays reported as unexportable, with its reason.
        if (!device.CaptureState.IsExportable())
        {
            return new PolicyVerdict
            {
                Allowed = false,
                State = device.CaptureState,
                Reason = device.CaptureReason,
                Nickname = GetNickname(identity),
                RequiresPassword = RequiresPassword(identity),
                Detail = device.CaptureDetail,
            };
        }

        return new PolicyVerdict
        {
            Allowed = true,
            State = device.CaptureState,
            Reason = device.CaptureReason,
            Nickname = GetNickname(identity),
            RequiresPassword = RequiresPassword(identity),
            Detail = string.Empty,
        };
    }

    /// <summary>
    /// Applies the verdict to a device, returning the device as the export table and the
    /// device list should see it (nickname and password state included).
    /// </summary>
    public UsbDevice Apply(UsbDevice device)
    {
        PolicyVerdict verdict = Evaluate(device);
        return new UsbDevice
        {
            Key = device.Key,
            InstancePath = device.InstancePath,
            BusNum = device.BusNum,
            DevNum = device.DevNum,
            Speed = device.Speed,
            VendorId = device.VendorId,
            ProductId = device.ProductId,
            BcdDevice = device.BcdDevice,
            DeviceClass = device.DeviceClass,
            DeviceSubClass = device.DeviceSubClass,
            DeviceProtocol = device.DeviceProtocol,
            ConfigurationValue = device.ConfigurationValue,
            NumConfigurations = device.NumConfigurations,
            Manufacturer = device.Manufacturer,
            Product = device.Product,
            Serial = device.Serial,
            Interfaces = device.Interfaces,
            Endpoints = device.Endpoints,
            CaptureState = verdict.State,
            CaptureReason = verdict.Reason,
            CaptureDetail = verdict.Detail,
            DevicePath = device.DevicePath,
            Nickname = verdict.Nickname ?? device.Nickname,
            // A device that reports itself as password-protected is never silently downgraded
            // to an unprotected one: the policy verifier can add protection, never remove it.
            RequiresPassword = verdict.RequiresPassword || device.RequiresPassword,
        };
    }
}
