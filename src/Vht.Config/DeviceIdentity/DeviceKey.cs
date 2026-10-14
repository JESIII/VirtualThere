using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vht.Config.DeviceIdentity;

/// <summary>
/// What a device enumeration can actually give us. Windows fills these from the device instance id
/// and the USB descriptors; the identity code below must work with a partial descriptor because
/// cheap hubs, keyboards and counterfeit sticks routinely report an empty serial number.
/// </summary>
public sealed record DeviceDescriptor(
    string InstanceId,
    ushort VendorId,
    ushort ProductId,
    ushort BcdDevice,
    string? SerialNumber = null,
    string? Product = null,
    string? Manufacturer = null,
    string? PortPath = null,
    string? ClassTriple = null)
{
    public string VidPid => $"{VendorId:x4}:{ProductId:x4}";

    /// <summary>A serial only counts when it is present AND plausible. See <see cref="SerialSanitizer"/>.</summary>
    public string? UsableSerial => SerialSanitizer.Normalize(SerialNumber);
}

/// <summary>
/// Serial-number hygiene. Real devices ship placeholder serials ("0000000000", "Default string",
/// "To Be Filled By O.E.M.") or fixed ones ("1234567890"); treating those as identity would make every
/// unit of that model collapse onto one key. Normalisation also makes the key filesystem/UI friendly:
/// trimmed, upper-cased, whitespace runs collapsed to '-', characters outside [A-Z0-9._-] dropped,
/// capped at 48 characters with a short hash appended when truncated (so two long serials sharing a
/// prefix stay distinct).
/// </summary>
public static class SerialSanitizer
{
    public const int MaxSerialLength = 48;

    private static readonly string[] KnownBogus =
    {
        "default string",
        "to be filled by o.e.m.",
        "to be filled by oem",
        "system serial number",
        "none",
        "n/a",
        "na",
        "unknown",
        "serial",
        "usb",
        "0",
        "0000",
        "00000000",
        "0000000000000000",
        "0123456789abcdef",
        "1234567890",
        "123456789012",
        "abcdef",
        "xxxxxxx",
        "ffffffffffff",
    };

    public static bool IsPlausible(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return false;
        }

        var trimmed = serial.Trim();
        if (trimmed.Length < 3)
        {
            return false;
        }

        // all-one-character values ("000000000000", "ffff") carry no identity.
        if (trimmed.Distinct().Count() == 1)
        {
            return false;
        }

        return !KnownBogus.Contains(trimmed.ToLowerInvariant());
    }

    /// <summary>Returns the normalised serial, or null when there is no usable serial.</summary>
    public static string? Normalize(string? serial)
    {
        if (!IsPlausible(serial))
        {
            return null;
        }

        var trimmed = serial!.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '_')
            {
                builder.Append(char.ToUpperInvariant(ch));
                continue;
            }

            // Anything else separates the parts of a serial ("5f3a 88c1-0001", "abc/123"). Runs of
            // separators collapse to a single '-', which keeps the key readable and filesystem-safe.
            if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var normalized = builder.ToString().Trim('-');
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length <= MaxSerialLength)
        {
            return normalized;
        }

        var hash = ShortHash(normalized);
        return normalized[..(MaxSerialLength - 9)] + "-" + hash;
    }

    internal static string ShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes)[..8];
    }
}

/// <summary>How a device key was derived. Ordered strongest to weakest; the fallback chain is
/// <see cref="Serial"/> -&gt; <see cref="Fingerprint"/> -&gt; <see cref="Topology"/> -&gt; adopted from the
/// persisted map (<see cref="DeviceIdMap"/>).</summary>
public enum DeviceIdentityTier
{
    Serial = 0,
    Fingerprint = 1,
    Topology = 2,
}

/// <summary>
/// The stable device key. Formats (all lower case, documented in docs/CONFIG.md):
/// <code>
///   s-1050-0407-0123456789AB          serial number present and plausible
///   f-1050-0407-0400-1a2b3c4d         no serial: vid, pid, bcdDevice and an 8-hex fingerprint
///   t-9c2f1a4b                        no serial and no distinguishing strings: port-path hash
///   &lt;key&gt;~2                           disambiguator for an ambiguous key in one snapshot
/// </code>
/// The key survives replug and hub-port change for tiers Serial and Fingerprint unconditionally, and
/// for the Topology tier whenever the persisted <see cref="DeviceIdMap"/> has seen the device before
/// (see DeviceIdMap.TryAdoptOrphan).
/// </summary>
public sealed record DeviceKey(string Value, DeviceIdentityTier Tier, string? Serial = null, string? Fingerprint = null, string? PortPath = null, int Disambiguator = 0)
{
    public const string SerialPrefix = "s-";
    public const string FingerprintPrefix = "f-";
    public const string TopologyPrefix = "t-";

    public bool IsDisambiguated => Disambiguator > 0;

    public override string ToString() => Value;

    public static DeviceKey ForSerial(DeviceDescriptor descriptor, string normalizedSerial) =>
        new($"{SerialPrefix}{descriptor.VendorId:x4}-{descriptor.ProductId:x4}-{normalizedSerial}",
            DeviceIdentityTier.Serial,
            Serial: normalizedSerial,
            PortPath: descriptor.PortPath);

    public static DeviceKey ForFingerprint(DeviceDescriptor descriptor)
    {
        var fingerprint = FingerprintOf(descriptor);
        return new($"{FingerprintPrefix}{descriptor.VendorId:x4}-{descriptor.ProductId:x4}-{descriptor.BcdDevice:x4}-{fingerprint}",
            DeviceIdentityTier.Fingerprint,
            Fingerprint: fingerprint,
            PortPath: descriptor.PortPath);
    }

    public static DeviceKey ForTopology(DeviceDescriptor descriptor) =>
        new($"{TopologyPrefix}{ShortHash(descriptor.PortPath ?? descriptor.InstanceId)}",
            DeviceIdentityTier.Topology,
            PortPath: descriptor.PortPath);

    public DeviceKey WithDisambiguator(int disambiguator) => this with
    {
        Disambiguator = disambiguator,
        Value = $"{Value}~{disambiguator}",
    };

    /// <summary>Hash over everything that distinguishes one unit from another of the same model,
    /// excluding anything topology-derived (so it survives a hub-port change).</summary>
    public static string FingerprintOf(DeviceDescriptor descriptor)
    {
        var canonical = string.Join('|', new[]
        {
            descriptor.VendorId.ToString("x4", CultureInfo.InvariantCulture),
            descriptor.ProductId.ToString("x4", CultureInfo.InvariantCulture),
            descriptor.BcdDevice.ToString("x4", CultureInfo.InvariantCulture),
            (descriptor.Manufacturer ?? string.Empty).Trim().ToLowerInvariant(),
            (descriptor.Product ?? string.Empty).Trim().ToLowerInvariant(),
            (descriptor.ClassTriple ?? string.Empty).Trim().ToLowerInvariant(),
        });
        return ShortHash(canonical);
    }

    internal static string ShortHash(string input) => SerialSanitizer.ShortHash(input);

    public static DeviceKey Parse(string value)
    {
        var core = value;
        var disambiguator = 0;
        var tilde = value.LastIndexOf('~');
        if (tilde > 0 && int.TryParse(value.AsSpan(tilde + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            disambiguator = parsed;
            core = value[..tilde];
        }

        if (core.StartsWith(SerialPrefix, StringComparison.Ordinal))
        {
            var first = core.IndexOf('-', SerialPrefix.Length);
            var second = first < 0 ? -1 : core.IndexOf('-', first + 1);
            var serial = second < 0 ? null : core[(second + 1)..];
            return new DeviceKey(value, DeviceIdentityTier.Serial, Serial: serial, Disambiguator: disambiguator);
        }

        if (core.StartsWith(FingerprintPrefix, StringComparison.Ordinal))
        {
            var parts = core.Split('-');
            var fingerprint = parts.Length >= 5 ? parts[4] : null;
            return new DeviceKey(value, DeviceIdentityTier.Fingerprint, Fingerprint: fingerprint, Disambiguator: disambiguator);
        }

        return new DeviceKey(value, DeviceIdentityTier.Topology, Disambiguator: disambiguator);
    }
}

/// <summary>A descriptor plus the key we resolved for it.</summary>
public sealed record ResolvedDevice(DeviceDescriptor Descriptor, DeviceKey Key, bool AdoptedFromMap, string Reason);