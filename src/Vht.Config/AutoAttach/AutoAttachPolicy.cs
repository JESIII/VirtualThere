using Vht.Config.DeviceIdentity;
using Vht.Config.Toml;

namespace Vht.Config.AutoAttach;

/// <summary>
/// One recorded auto-attach grant (the reference product's "Auto-Use"). Recorded per (server, device) with the
/// qualifiers the recon documents: server name and/or network interface.
/// </summary>
public sealed record AutoAttachGrant(
    string DeviceKey,
    string? ServerName,
    string? ServerInterface,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset? LastUsedUtc = null)
{
    /// <summary>0-100: how specific this grant is (used only to order candidates deterministically).</summary>
    public int Specificity => (ServerName is null ? 0 : 1) + (ServerInterface is null ? 0 : 1);
}

public sealed record AutoAttachDecision(bool Attach, string Reason);

public sealed record AutoAttachGrantRequest(
    string DeviceKey,
    string? ServerName,
    string? ServerInterface,
    bool DeviceRequiresPassword,
    string? KnownPassword);

public sealed record AutoAttachGrantResult(bool Recorded, string Reason, AutoAttachGrant? Grant = null);

/// <summary>
/// Client-side auto-attach policy. The important behaviour is what it refuses:
/// <list type="bullet">
/// <item>a device is attached automatically only when a grant exists for that exact device key;</item>
/// <item>a grant is honoured only for the server it was granted for (when qualifying by name) and/or
/// the interface it was granted on (when qualifying by interface);</item>
/// <item>a device seen for the first time is never auto-attached;</item>
/// <item>a grant for a password-protected device without a remembered password is not usable, because
/// <c>BIND_PASSWORD_REQUIRED</c> cannot be answered unattended.</item>
/// </list>
/// The reference product records the grant before authorization succeeds and then reports "you are
/// auto-using server '%s' but you are not authorized to use attached device '%s'" (0x1409d262b).
/// <see cref="RecordGrant"/> takes the authorization result as an argument and refuses to record on
/// failure - authorization first, record second.
/// </summary>
public sealed class AutoAttachPolicy
{
    public AutoAttachPolicy(
        bool enabled,
        bool qualifyByServerName,
        bool qualifyByInterface,
        int delaySeconds,
        int retryDelaySeconds,
        int maxRetries)
    {
        Enabled = enabled;
        QualifyByServerName = qualifyByServerName;
        QualifyByInterface = qualifyByInterface;
        DelaySeconds = delaySeconds;
        RetryDelaySeconds = retryDelaySeconds;
        MaxRetries = maxRetries;
    }

    public bool Enabled { get; }

    public bool QualifyByServerName { get; }

    public bool QualifyByInterface { get; }

    public int DelaySeconds { get; }

    public int RetryDelaySeconds { get; }

    public int MaxRetries { get; }

    public AutoAttachDecision Evaluate(
        string deviceKey,
        string? serverName,
        string? serverInterface,
        IReadOnlyList<AutoAttachGrant> grants,
        bool deviceRequiresPassword,
        bool passwordKnown)
    {
        if (!Enabled)
        {
            return new AutoAttachDecision(false, "auto-attach is disabled (auto_attach.enabled = false)");
        }

        if (QualifyByServerName && serverName is null)
        {
            return new AutoAttachDecision(false, "auto_attach.qualify_by_server_name is set but the server name is unknown");
        }

        if (QualifyByInterface && serverInterface is null)
        {
            return new AutoAttachDecision(false, "auto_attach.qualify_by_interface is set but the interface is unknown");
        }

        var candidate = grants
            .Where(g => string.Equals(g.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase))
            .Where(g => !QualifyByServerName || Matches(g.ServerName, serverName))
            .Where(g => !QualifyByInterface || Matches(g.ServerInterface, serverInterface))
            .OrderByDescending(g => g.Specificity)
            .ThenBy(g => g.GrantedAtUtc)
            .FirstOrDefault();

        if (candidate is null)
        {
            return new AutoAttachDecision(false, "no auto-attach grant for this device on this server (a new device is never auto-attached)");
        }

        if (deviceRequiresPassword && !passwordKnown)
        {
            return new AutoAttachDecision(false, "the device is password protected and no password is remembered, so it cannot be attached unattended");
        }

        return new AutoAttachDecision(true, $"granted {candidate.GrantedAtUtc:u} for server '{candidate.ServerName ?? "(any)"}'");
    }

    /// <summary>Records a grant, but only after authorization succeeded (see the class remarks).</summary>
    public static AutoAttachGrantResult RecordGrant(AutoAttachGrantRequest request, bool authorizationSucceeded, DateTimeOffset now)
    {
        if (!authorizationSucceeded)
        {
            return new AutoAttachGrantResult(false, "authorization did not succeed for this device; the grant was NOT recorded");
        }

        if (string.IsNullOrWhiteSpace(request.DeviceKey))
        {
            return new AutoAttachGrantResult(false, "a grant needs a device key");
        }

        var grant = new AutoAttachGrant(request.DeviceKey, request.ServerName, request.ServerInterface, now);
        return new AutoAttachGrantResult(true, "grant recorded after successful authorization", grant);
    }

    private static bool Matches(string? recorded, string? current) =>
        string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<AutoAttachGrant> Load(FlatTable state)
    {
        var grants = new List<AutoAttachGrant>();
        foreach (var item in state.TableArray("auto_attach.grants"))
        {
            var key = GetString(item, "device_key");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            grants.Add(new AutoAttachGrant(
                key,
                GetString(item, "server"),
                GetString(item, "server_interface"),
                // A grant without a parsable timestamp still names the device; timestamp it at the
                // epoch rather than dropping it (ordering by Specificity, then time).
                ParseTime(GetString(item, "granted_at_utc")) ?? DateTimeOffset.UnixEpoch,
                ParseTime(GetString(item, "last_used_utc"))));
        }

        return grants;
    }

    public static IEnumerable<KeyValuePair<string, object?>> ToFlatValues(IReadOnlyList<AutoAttachGrant> grants)
    {
        for (var i = 0; i < grants.Count; i++)
        {
            var prefix = $"auto_attach.grants#{i}";
            yield return new KeyValuePair<string, object?>($"{prefix}.device_key", grants[i].DeviceKey);
            yield return new KeyValuePair<string, object?>($"{prefix}.server", grants[i].ServerName);
            yield return new KeyValuePair<string, object?>($"{prefix}.server_interface", grants[i].ServerInterface);
            yield return new KeyValuePair<string, object?>($"{prefix}.granted_at_utc", grants[i].GrantedAtUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            yield return new KeyValuePair<string, object?>($"{prefix}.last_used_utc", grants[i].LastUsedUtc?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, FlatValue> item, string field) =>
        item.TryGetValue(field, out var value) ? value.Value as string : null;

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}