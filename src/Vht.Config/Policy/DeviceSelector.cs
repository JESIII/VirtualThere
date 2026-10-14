using System.Globalization;
using Vht.Config.DeviceIdentity;

namespace Vht.Config.Policy;

/// <summary>
/// A device selector as written in <c>policy.allow</c> / <c>policy.ignore</c> / <c>[nicknames]</c> /
/// <c>[passwords]</c>. Grammar (documented in docs/CONFIG.md):
/// <code>
///   *                      any device
///   1050:0407              vendor id : product id
///   1050:*                 wildcard product
///   *:0407                 wildcard vendor
///   1050:0407:0123ABCD     vid : pid : serial number
///   serial:0123ABCD        any device carrying that serial
///   key:s-1050-0407-0123ABCD   the stable device key (strongest and most explicit form)
///   vid:1050               any device from a vendor
/// </code>
/// the reference product keys its allow/ignore lists on (vid,pid,serial,server). The selector keeps that
/// vocabulary and adds the explicit <c>key:</c> form, which is the only form that survives a firmware
/// update changing the product string.
/// </summary>
public sealed record DeviceSelector(
    string Raw,
    DeviceSelectorKind Kind,
    ushort? VendorId = null,
    ushort? ProductId = null,
    string? Serial = null,
    string? DeviceKey = null)
{
    public static readonly DeviceSelector Any = new("*", DeviceSelectorKind.Any);

    public static bool TryParse(string? text, out DeviceSelector selector, out string? error)
    {
        selector = Any;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty selector";
            return false;
        }

        var raw = text.Trim();
        if (raw == "*")
        {
            selector = Any;
            return true;
        }

        if (raw.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
        {
            var key = raw[4..].Trim();
            if (key.Length == 0)
            {
                error = "key: selector needs a device key";
                return false;
            }

            selector = new DeviceSelector(raw, DeviceSelectorKind.Key, DeviceKey: key);
            return true;
        }

        if (raw.StartsWith("serial:", StringComparison.OrdinalIgnoreCase))
        {
            var serialText = raw[7..].Trim();
            if (serialText.Length == 0)
            {
                error = "serial: selector needs a serial number";
                return false;
            }

            selector = new DeviceSelector(raw, DeviceSelectorKind.Serial, Serial: serialText.ToUpperInvariant());
            return true;
        }

        if (raw.StartsWith("vid:", StringComparison.OrdinalIgnoreCase))
        {
            var vid = raw[4..].Trim();
            if (!TryParseId(vid, out var vendor))
            {
                error = $"'{vid}' is not a 4-digit hexadecimal vendor id";
                return false;
            }

            selector = new DeviceSelector(raw, DeviceSelectorKind.Vendor, VendorId: vendor);
            return true;
        }

        if (raw.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
        {
            var pid = raw[4..].Trim();
            if (!TryParseId(pid, out var product))
            {
                error = $"'{pid}' is not a 4-digit hexadecimal product id";
                return false;
            }

            selector = new DeviceSelector(raw, DeviceSelectorKind.Product, ProductId: product);
            return true;
        }

        var parts = raw.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            error = $"'{raw}' is not a valid selector; expected vid:pid, vid:pid:serial, serial:<s>, key:<k>, vid:<v>, pid:<p> or *";
            return false;
        }

        if (!TryParseId(parts[0], out var vendorId) || !TryParseId(parts[1], out var productId))
        {
            error = $"'{raw}' must use 4-digit hexadecimal ids, e.g. 1050:0407, 1050:* or *:0407";
            return false;
        }

        string? serial = null;
        if (parts.Length == 3)
        {
            var value = parts[2].Trim();
            if (value.Length == 0)
            {
                error = $"'{raw}' has an empty serial field";
                return false;
            }

            serial = value == "*" ? null : value.ToUpperInvariant();
        }

        var kind = (vendorId, productId, serial) switch
        {
            (null, null, _) => DeviceSelectorKind.Any,
            (not null, null, _) => DeviceSelectorKind.VendorProduct,
            (null, not null, _) => DeviceSelectorKind.VendorProduct,
            _ => serial is null ? DeviceSelectorKind.VendorProduct : DeviceSelectorKind.VendorProductSerial,
        };

        selector = new DeviceSelector(raw, kind, vendorId, productId, serial);
        return true;
    }

    public bool Matches(DeviceDescriptor descriptor, string deviceKey)
    {
        switch (Kind)
        {
            case DeviceSelectorKind.Any:
                return true;
            case DeviceSelectorKind.Key:
                return string.Equals(DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase);
            case DeviceSelectorKind.Serial:
                return string.Equals(Serial, descriptor.UsableSerial, StringComparison.OrdinalIgnoreCase);
            case DeviceSelectorKind.Vendor:
                return VendorId == descriptor.VendorId;
            case DeviceSelectorKind.Product:
                return ProductId == descriptor.ProductId;
        }

        if (VendorId is { } vid && vid != descriptor.VendorId)
        {
            return false;
        }

        if (ProductId is { } pid && pid != descriptor.ProductId)
        {
            return false;
        }

        if (Serial is { } serial && !string.Equals(serial, descriptor.UsableSerial, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseId(string text, out ushort? value)
    {
        value = null;
        if (text.Trim() == "*")
        {
            return true;
        }

        if (ushort.TryParse(text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    public string Describe() => Kind switch
    {
        DeviceSelectorKind.Any => "any device",
        DeviceSelectorKind.Key => $"device key {DeviceKey}",
        DeviceSelectorKind.Serial => $"serial {Serial}",
        DeviceSelectorKind.Vendor => $"vendor {VendorId:x4}",
        DeviceSelectorKind.Product => $"product {ProductId:x4}",
        DeviceSelectorKind.VendorProductSerial => $"device {VendorId:x4}:{ProductId:x4}:{Serial}",
        _ => $"device {VendorId:x4}:{ProductId:x4}",
    };
}

public enum DeviceSelectorKind
{
    Any = 0,
    VendorProduct = 1,
    VendorProductSerial = 2,
    Serial = 3,
    Key = 4,
    Vendor = 5,
    Product = 6,
}

/// <summary>The server's export decision for one device, with the reason that produced it.</summary>
public sealed record DeviceExportDecision(bool Exported, string Reason, bool AllowListOnly)
{
    public static DeviceExportDecision Export(string reason) => new(true, reason, false);

    public static DeviceExportDecision Refuse(string reason, bool allowListOnly = false) => new(false, reason, allowListOnly);
}

/// <summary>
/// Enforces <c>policy.allow</c> / <c>policy.ignore</c> server-side, at export time. Rules, in order:
/// <list type="number">
/// <item>a device matching <c>policy.ignore</c> is never exported - ignore always wins;</item>
/// <item>when <c>policy.allow</c> is non-empty, only matching devices are exported (allow-list mode);</item>
/// <item>when <c>policy.allow</c> is empty, everything not ignored is exported.</item>
/// </list>
/// The result is a refusal with a human-readable reason, so the UI can say why instead of silently
/// hiding a device.
/// </summary>
public sealed class DevicePolicy
{
    public DevicePolicy(IReadOnlyList<DeviceSelector> allow, IReadOnlyList<DeviceSelector> ignore)
    {
        Allow = allow;
        Ignore = ignore;
    }

    public IReadOnlyList<DeviceSelector> Allow { get; }

    public IReadOnlyList<DeviceSelector> Ignore { get; }

    public bool AllowListOnly => Allow.Count > 0;

    public DeviceExportDecision Evaluate(DeviceDescriptor descriptor, string deviceKey)
    {
        var ignored = Ignore.FirstOrDefault(selector => selector.Matches(descriptor, deviceKey));
        if (ignored is not null)
        {
            return DeviceExportDecision.Refuse($"ignored by policy.ignore rule '{ignored.Raw}'");
        }

        if (!AllowListOnly)
        {
            return new DeviceExportDecision(true, "exported (policy.allow is empty and the device is not ignored)", false);
        }

        var allowed = Allow.FirstOrDefault(selector => selector.Matches(descriptor, deviceKey));
        return allowed is not null
            ? new DeviceExportDecision(true, $"exported by policy.allow rule '{allowed.Raw}'", true)
            : DeviceExportDecision.Refuse("not listed in policy.allow, which is non-empty (allow-list mode)", true);
    }

    /// <summary>Static conflicts worth telling the operator about: a selector in both lists.</summary>
    public IReadOnlyList<string> DescribeConflicts()
    {
        var conflicts = new List<string>();
        foreach (var allow in Allow)
        {
            foreach (var ignore in Ignore)
            {
                if (string.Equals(allow.Raw, ignore.Raw, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add($"'{allow.Raw}' appears in both policy.allow and policy.ignore; policy.ignore wins");
                }
            }
        }

        return conflicts;
    }
}