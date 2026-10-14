using System.Reflection;

namespace Vht.Core;

/// <summary>
/// Single source of truth for the product version shared by every VirtualThere binary
/// (ARCHITECTURE.md §9 "Versioning").
/// </summary>
public static class VhtVersion
{
    public const string Version = "0.1.0";

    /// <summary>Product name used in banners, service display name and Event Log source.</summary>
    public const string ProductName = "VirtualThere";

    /// <summary>USB/IP protocol version this server speaks: 1.1.1 (kernel doc, 0x0111).</summary>
    public const ushort UsbIpProtocolVersion = 0x0111;

    /// <summary>Human-readable version string, e.g. <c>VirtualThere 0.1.0 (USB/IP 1.1.1)</c>.</summary>
    public static string Banner =>
        $"{ProductName} {Version} (USB/IP 1.1.1)";

    public static string Informational =>
        typeof(VhtVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? Version;
}
