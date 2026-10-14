namespace Vht.Core.Model;

/// <summary>
/// Stable identity of one exported device.
/// </summary>
/// <remarks>
/// The <see cref="BusId"/> is the USB/IP-visible identity and the key the export and attach
/// tables are indexed by. A device is uniquely identified across a hotplug cycle by the
/// instance path from CfgMgr32 (<c>CM_Get_Device_IDW</c>), which is why
/// <see cref="InstancePath"/> is carried alongside it.
/// </remarks>
public readonly record struct DeviceKey(string BusId, string InstancePath)
{
    /// <summary>Creates a key from a bus id alone, for tests and synthetic devices.</summary>
    public static DeviceKey FromBusId(string busId) => new(busId, string.Empty);

    public bool Equals(DeviceKey other) =>
        string.Equals(BusId, other.BusId, StringComparison.Ordinal);

    public override int GetHashCode() => BusId.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => BusId;
}

/// <summary>
/// The vid/pid/serial tuple used by ignore/allow policy and by device nicknames. Mirrors the
/// reference product's <c>Vendor Id / Product Id / Serial Number</c> table plus the server
/// qualifier the recon documents for auto-use (RECON_AND_SPEC.md §7.5).
/// </summary>
public readonly record struct DeviceIdentity(ushort VendorId, ushort ProductId, string Serial)
{
    /// <summary>True when the device reported a usable serial number.</summary>
    public bool HasSerial => !string.IsNullOrWhiteSpace(Serial);

    /// <summary>Policy match key without the server qualifier.</summary>
    public string VpKey => $"{VendorId:x4}:{ProductId:x4}";

    public override string ToString() =>
        HasSerial ? $"{VpKey}:{Serial}" : VpKey;
}

/// <summary>USB class codes that the server reasons about explicitly.</summary>
public static class UsbClass
{
    public const byte PerInterface = 0x00;
    public const byte Audio = 0x01;
    public const byte Communications = 0x02;
    public const byte Hid = 0x03;
    public const byte Physical = 0x05;
    public const byte Image = 0x06;
    public const byte Printer = 0x07;
    public const byte MassStorage = 0x08;
    public const byte Hub = 0x09;
    public const byte CdcData = 0x0a;
    public const byte SmartCard = 0x0b;
    public const byte Video = 0x0e;
    public const byte Health = 0x0f;
    public const byte AudioVideo = 0x10;
    public const byte Billboard = 0x11;
    public const byte Wireless = 0xe0;
    public const byte Misc = 0xef;
    public const byte Application = 0xfe;
    public const byte VendorSpecific = 0xff;

    /// <summary>Human-readable class name used in the honest device-class matrix.</summary>
    public static string Name(byte deviceClass) => deviceClass switch
    {
        PerInterface => "per-interface",
        Audio => "audio",
        Communications => "communications",
        Hid => "hid",
        Physical => "physical",
        Image => "image/uvc",
        Printer => "printer",
        MassStorage => "mass-storage",
        Hub => "hub",
        CdcData => "cdc-data",
        SmartCard => "smart-card",
        Video => "video",
        Health => "health",
        AudioVideo => "audio-video",
        Billboard => "billboard",
        Wireless => "wireless",
        Misc => "misc",
        Application => "application",
        VendorSpecific => "vendor-specific",
        _ => $"class-0x{deviceClass:x2}",
    };
}
