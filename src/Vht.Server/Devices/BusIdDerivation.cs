using System.Globalization;

namespace Vht.Server.Devices;

/// <summary>
/// Derives the USB/IP bus id for a Windows device instance path.
/// </summary>
/// <remarks>
/// <para>
/// USB/IP clients treat the bus id as an opaque handle: they list it, then hand it back on
/// import. What matters is therefore that it is (a) stable across re-enumerations of the same
/// physical device, (b) unique on this machine, and (c) at most
/// <see cref="Vht.Core.Protocol.UsbIp.BusIdLength"/> characters. The reference product derives
/// its ids from the same instance path; the exact string is not part of the protocol.
/// </para>
/// <para>
/// Shape: <c>1-&lt;port&gt;-&lt;hash8&gt;</c> when the instance suffix carries a port number
/// (the common <c>7&amp;1b2c3d4e&amp;0&amp;2</c> form, where the trailing component is the hub
/// port), otherwise <c>1-&lt;hash8&gt;</c>. The hash of the full instance path is what keeps two
/// devices behind different hubs from colliding on the same port number, and it is computed
/// with FNV-1a so it never changes between runs or .NET versions.
/// </para>
/// </remarks>
public static class BusIdDerivation
{
    /// <summary>The single logical USB bus a Windows host presents to USB/IP clients.</summary>
    public const uint BusNum = 1;

    /// <summary>Returns (bus id, device number) for an instance path.</summary>
    public static (string BusId, uint DevNum) FromInstancePath(string instancePath)
    {
        uint port = TryParsePort(instancePath, out uint parsed) ? parsed : 0;
        string hash = Fnv1a8(instancePath);

        string busId = port > 0
            ? string.Create(CultureInfo.InvariantCulture, $"1-{port}-{hash}")
            : $"1-{hash}";

        // Defensive: BusIdLength is 32 and the longest form above is 19, but the bounds check
        // is what keeps a hand-edited or synthetic instance path from producing a frame the
        // codec would have to truncate.
        if (busId.Length > Vht.Core.Protocol.UsbIp.BusIdLength)
            busId = busId[..Vht.Core.Protocol.UsbIp.BusIdLength];

        return (busId, port > 0 ? port : 1);
    }

    /// <summary>
    /// Parses the video/pid pair out of an instance path
    /// (<c>USB\VID_046D&amp;PID_C52B\...</c>). Returns false when the path is not a USB id form.
    /// </summary>
    public static bool TryParseVidPid(string instancePath, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;

        int vid = instancePath.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vid < 0 || vid + 8 > instancePath.Length) return false;
        if (!ushort.TryParse(instancePath.AsSpan(vid + 4, 4), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out vendorId))
            return false;

        int pid = instancePath.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (pid < 0 || pid + 8 > instancePath.Length) return false;
        return ushort.TryParse(instancePath.AsSpan(pid + 4, 4), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out productId);
    }

    /// <summary>
    /// The trailing component of an instance suffix is the hub port on most USB stacks
    /// (<c>&amp;0&amp;2</c> = port 2). Returns false when the suffix has no such component.
    /// </summary>
    private static bool TryParsePort(string instancePath, out uint port)
    {
        port = 0;
        if (string.IsNullOrEmpty(instancePath)) return false;

        int lastSlash = instancePath.LastIndexOf('\\');
        if (lastSlash < 0 || lastSlash + 1 >= instancePath.Length) return false;

        string suffix = instancePath[(lastSlash + 1)..];
        int lastAmp = suffix.LastIndexOf('&');
        string tail = lastAmp >= 0 ? suffix[(lastAmp + 1)..] : suffix;

        return uint.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port > 0 && port < 128;
    }

    /// <summary>FNV-1a over the UTF-16 characters of the instance path, rendered as 8 hex chars.</summary>
    public static string Fnv1a8(string text)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;

        uint hash = offset;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }
}
