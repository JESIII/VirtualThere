// SPDX-License-Identifier: Apache-2.0
//
// Policy: the allow/ignore lists and the per-device auto-attach grant (deliverable 6, ARCHITECTURE.md
// §7). Two rules from that section are load-bearing and are implemented as ordering, not as comments:
//
//  1. **A grant is only recorded after authorization succeeded.** The recon shows the reference
//     product recording the grant before authorization and then complaining about it; here the
//     auto-attach path checks policy, obtains credentials, attaches, and only on a successful
//     authorized attach calls Grant(). Hence Grant is called by the attach manager, not by the UI.
//  2. **Discovery never attaches anything.** Evaluate() refuses unless a grant for that exact
//     (server, device) pair exists. A new device seen on a known server is never auto-attached.
//
// Deny always beats allow: an explicit deny is a user decision, and a broad allow list must not
// silently override it.

using System.Text.Json;
using System.Text.Json.Serialization;
using Vht.Client.Bridge;
using Vht.Client.Diagnostics;

namespace Vht.Client.Policy;

/// <summary>What the policy says about one device.</summary>
internal sealed record AutoAttachDecision
{
    internal required bool ShouldAutoAttach { get; init; }

    /// <summary>True when the device may be attached at all (manual or automatic).</summary>
    internal required bool IsAttachable { get; init; }

    internal required string Reason { get; init; }

    internal static AutoAttachDecision Auto(string reason) => new() { ShouldAutoAttach = true, IsAttachable = true, Reason = reason };

    internal static AutoAttachDecision Ask(string reason) => new() { ShouldAutoAttach = false, IsAttachable = true, Reason = reason };

    internal static AutoAttachDecision Refuse(string reason) => new() { ShouldAutoAttach = false, IsAttachable = false, Reason = reason };
}

/// <summary>
/// The allow/ignore lists plus the recorded auto-attach grants. Persisted as JSON; the TOML keys
/// `policy.allow` / `policy.ignore` translate onto <see cref="SetAllow"/> / <see cref="SetIgnore"/>
/// when Vht.Config lands (ARCHITECTURE.md App. C vocabulary mapping).
/// </summary>
internal sealed class AutoAttachPolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly ClientLog _log;
    private List<string> _allow = new();
    private List<string> _ignore = new();
    private Dictionary<string, GrantRecord> _grants = new(StringComparer.Ordinal);

    internal AutoAttachPolicy(string path, ClientLog log)
    {
        _path = path;
        _log = log;
        Load();
    }

    /// <summary>How long to wait after a peer is seen before attaching (the reference product's "attach delay").</summary>
    internal TimeSpan AttachDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait before retrying an attach attempt that failed.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Allow patterns. Empty means "no allow-list restriction".</summary>
    internal IReadOnlyList<string> AllowList
    {
        get
        {
            lock (_sync) return _allow.ToArray();
        }
    }

    /// <summary>Ignore patterns. Always win over allow and over a grant.</summary>
    internal IReadOnlyList<string> IgnoreList
    {
        get
        {
            lock (_sync) return _ignore.ToArray();
        }
    }

    internal IReadOnlyList<string> GrantedKeys
    {
        get
        {
            lock (_sync) return _grants.Values.Where(g => g.Allowed).Select(g => g.DeviceKey).ToArray();
        }
    }

    /// <summary>Denied keys are remembered too, so a user's "no" is not forgotten between sessions.</summary>
    internal bool IsExplicitlyDenied(string deviceKey)
    {
        lock (_sync) return _grants.TryGetValue(deviceKey, out GrantRecord? g) && !g.Allowed;
    }

    internal void SetAllow(IEnumerable<string> patterns)
    {
        lock (_sync)
        {
            _allow = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
            Save();
        }
        _log.Info("policy", $"allow list = [{string.Join(", ", _allow)}]");
    }

    internal void SetIgnore(IEnumerable<string> patterns)
    {
        lock (_sync)
        {
            _ignore = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
            Save();
        }
        _log.Info("policy", $"ignore list = [{string.Join(", ", _ignore)}]");
    }

    internal void AddIgnore(string pattern)
    {
        lock (_sync)
        {
            if (!_ignore.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                _ignore.Add(pattern);
                Save();
            }
        }
    }

    internal void RemoveIgnore(string pattern)
    {
        lock (_sync)
        {
            if (_ignore.RemoveAll(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Records the auto-attach grant for a (server, device) pair. Called ONLY after a successful
    /// authorized attach — see the class comment; the ordering is asserted by AutoAttachPolicyTests.
    /// </summary>
    internal void Grant(InventoryEntry entry)
    {
        lock (_sync)
        {
            _grants[entry.DeviceKey] = new GrantRecord
            {
                DeviceKey = entry.DeviceKey,
                PeerName = entry.Peer.Name,
                RemoteBusId = entry.Device.BusId,
                Allowed = true,
                GrantedUtc = DateTimeOffset.UtcNow,
            };
            Save();
        }
        _log.Info("policy", $"recorded auto-attach grant for {entry.DeviceKey}");
    }

    internal void Deny(InventoryEntry entry)
    {
        lock (_sync)
        {
            _grants[entry.DeviceKey] = new GrantRecord
            {
                DeviceKey = entry.DeviceKey,
                PeerName = entry.Peer.Name,
                RemoteBusId = entry.Device.BusId,
                Allowed = false,
                GrantedUtc = DateTimeOffset.UtcNow,
            };
            Save();
        }
        _log.Info("policy", $"recorded auto-attach denial for {entry.DeviceKey}");
    }

    internal bool Revoke(string deviceKey)
    {
        bool removed;
        lock (_sync)
        {
            removed = _grants.Remove(deviceKey);
            if (removed) Save();
        }
        return removed;
    }

    /// <summary>Evaluates one device. This is the only place the auto-attach question is answered.</summary>
    internal AutoAttachDecision Evaluate(InventoryEntry entry)
    {
        lock (_sync)
        {
            if (DeviceSelector.MatchesAny(_ignore, entry))
            {
                return AutoAttachDecision.Refuse("matched the ignore list");
            }

            if (DeviceSelector.MatchesAny(_allow, entry) == false && _allow.Count > 0)
            {
                return AutoAttachDecision.Refuse("not matched by the allow list (which is non-empty, so it is exclusive)");
            }

            if (_grants.TryGetValue(entry.DeviceKey, out GrantRecord? grant))
            {
                return grant.Allowed
                    ? AutoAttachDecision.Auto("explicit auto-attach grant for this device on this server")
                    : AutoAttachDecision.Ask("explicitly denied by the user");
            }

            return AutoAttachDecision.Ask(
                "no auto-attach grant recorded for this device on this server " +
                "(discovery alone never attaches: ARCHITECTURE.md §7)");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            PolicyState? state = JsonSerializer.Deserialize<PolicyState>(File.ReadAllText(_path), JsonOptions);
            if (state is null) return;
            _allow = state.Allow ?? new List<string>();
            _ignore = state.Ignore ?? new List<string>();
            _grants = (state.Grants ?? new List<GrantRecord>()).ToDictionary(g => g.DeviceKey, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Error("policy", $"could not read '{_path}'; starting with empty policy", ex);
            _allow = new List<string>();
            _ignore = new List<string>();
            _grants = new Dictionary<string, GrantRecord>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new PolicyState
            {
                Allow = _allow,
                Ignore = _ignore,
                Grants = _grants.Values.OrderBy(g => g.DeviceKey, StringComparer.Ordinal).ToList(),
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("policy", $"could not write '{_path}'", ex);
        }
    }

    private sealed record PolicyState
    {
        public List<string>? Allow { get; init; }

        public List<string>? Ignore { get; init; }

        public List<GrantRecord>? Grants { get; init; }
    }

    private sealed record GrantRecord
    {
        public string DeviceKey { get; init; } = string.Empty;

        public string PeerName { get; init; } = string.Empty;

        public string RemoteBusId { get; init; } = string.Empty;

        public bool Allowed { get; init; }

        public DateTimeOffset GrantedUtc { get; init; }
    }
}

/// <summary>
/// Pattern matching for the allow/ignore lists. Documented forms (case-insensitive, `*` = everything):
/// <list type="bullet">
///   <item><description><c>*</c> — every device.</description></item>
///   <item><description><c>serverName</c> — every device on that peer.</description></item>
///   <item><description><c>serverName/busid</c> — one device on one peer.</description></item>
///   <item><description><c>vid:0x1234</c>, <c>pid:0x5678</c>, <c>vid:0x1234/pid:0x5678</c>.</description></item>
///   <item><description><c>class:08</c> — a USB device class, in hex or decimal.</description></item>
/// </list>
/// An unrecognised pattern matches nothing and is reported by <see cref="Validate"/>, so a typo is
/// visible instead of silently ignoring a device forever.
/// </summary>
internal static class DeviceSelector
{
    internal static bool MatchesAny(IEnumerable<string> patterns, InventoryEntry entry)
    {
        foreach (string pattern in patterns)
        {
            if (Matches(pattern, entry)) return true;
        }
        return false;
    }

    internal static bool Matches(string pattern, InventoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string p = pattern.Trim();
        if (p == "*") return true;

        if (p.StartsWith("class:", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseByte(p[6..], out byte cls) && entry.Device.DeviceClass == cls;
        }

        if (p.Contains(':'))
        {
            return MatchesVidPid(p, entry);
        }

        int slash = p.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            string server = p[..slash];
            string busId = p[(slash + 1)..];
            return string.Equals(server, entry.Peer.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(busId, entry.Device.BusId, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(p, entry.Peer.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesVidPid(string pattern, InventoryEntry entry)
    {
        bool sawVendor = false;
        bool sawProduct = false;

        foreach (string part in pattern.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("vid:", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseU16(part[4..], out ushort vid) || entry.Device.VendorId != vid) return false;
                sawVendor = true;
            }
            else if (part.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseU16(part[4..], out ushort pid) || entry.Device.ProductId != pid) return false;
                sawProduct = true;
            }
            else
            {
                return false;
            }
        }

        return sawVendor || sawProduct;
    }

    /// <summary>Returns the patterns that match nothing recognisable, for a config-warning list.</summary>
    internal static IReadOnlyList<string> Validate(IEnumerable<string> patterns) =>
        patterns.Where(p =>
        {
            string s = p.Trim();
            if (s == "*" || s.Contains('/') || s.StartsWith("class:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return s.Contains(':') && !s.StartsWith("vid:", StringComparison.OrdinalIgnoreCase)
                                 && !s.StartsWith("pid:", StringComparison.OrdinalIgnoreCase);
        }).ToArray();

    private static bool TryParseU16(string text, out ushort value)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        }
        return ushort.TryParse(t, out value);
    }

    private static bool TryParseByte(string text, out byte value)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return byte.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        }
        return byte.TryParse(t, out value);
    }
}
