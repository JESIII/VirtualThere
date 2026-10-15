using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using Vht.Interop;
using Vht.Interop.Rebind;
using Vht.Interop.Services;

namespace Vht.Server.Devices;

/// <summary>Result of one rebind attempt.</summary>
/// <remarks>
/// This is the P2 capture path (ARCHITECTURE.md §4.1): the device is already owned by a
/// function driver, and the operator has opted in to moving it onto the inbox
/// <c>winusb.sys</c>. It installs <b>no driver of ours</b> — only an INF that selects the
/// driver Windows ships — and it needs elevation, which is why its failure modes are reported
/// rather than retried.
/// </remarks>
public readonly struct RebindResult
{
    public bool Ok { get; init; }
    public string Detail { get; init; }
    public int Win32Error { get; init; }
    public bool RebootRequired { get; init; }
    public string InfPath { get; init; }

    public static RebindResult Success(string infPath, bool rebootRequired) =>
        new() { Ok = true, InfPath = infPath, RebootRequired = rebootRequired, Detail = "rebound to winusb.sys" };

    public static RebindResult Failure(string detail, int win32Error = 0) =>
        new() { Ok = false, Detail = detail, Win32Error = win32Error };
}

/// <summary>
/// Installs a device-specific INF that binds the inbox <c>winusb.sys</c> to one vid/pid, then
/// re-selects the driver for the existing devnode.
/// </summary>
/// <remarks>
/// The provider depends on <see cref="IDeviceRebinder"/> rather than on this class, so the P2
/// decision - "is a rebind attempted at all for this device?" - is testable without a driver
/// store or an elevated token; this class is the one that actually touches the machine.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinUsbRebinder : IDeviceRebinder
{
    private const int ERROR_ACCESS_DENIED = 5;

    /// <summary>Where staged INFs are written; the operator can inspect or delete them.</summary>
    public static string DefaultWorkingDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VirtualThere", "driver");

    public WinUsbRebinder(string workingDirectory)
    {
        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? DefaultWorkingDirectory : workingDirectory;
    }

    /// <summary>Directory INF files are generated in.</summary>
    public string WorkingDirectory { get; }

    /// <summary>
    /// True when this process can change a driver binding at all. The process token is the only
    /// thing this can depend on, which is why it is a property of the rebinder rather than a
    /// constant of the caller.
    /// </summary>
    public bool CanRebind => WindowsServiceManager.IsElevated;

    /// <inheritdoc />
    public RebindResult Rebind(ushort vendorId, ushort productId)
    {
        if (!CanRebind)
        {
            return RebindResult.Failure(
                "the P2 rebind path needs an elevated process (SetupCopyOEMInf/UpdateDriverForPlugAndPlayDevices " +
                "require administrator rights); re-run the service or CLI elevated",
                ERROR_ACCESS_DENIED);
        }

        string hardwareId = DeviceRebindApi.HardwareId(vendorId, productId);
        string infName = $"vht-{vendorId:x4}{productId:x4}.inf";

        try
        {
            Directory.CreateDirectory(WorkingDirectory);
            string infPath = Path.Combine(WorkingDirectory, infName);
            File.WriteAllText(infPath, BuildInf(vendorId, productId, infName));

            // Stage the package. SetupAPI copies it into the driver store under an oemNN.inf
            // name, which is what a rollback would remove.
            var destination = new char[DeviceRebindApi.MaxInfPath];
            if (!DeviceRebindApi.SetupCopyOEMInf(
                    infPath, null, DeviceRebindApi.SPOST_PATH, 0,
                    destination, destination.Length, out _, out _))
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return RebindResult.Failure(
                    $"SetupCopyOEMInf failed for {infPath} (win32 {error}: " +
                    $"{new System.ComponentModel.Win32Exception(error).Message})", error);
            }

            string stagedName = new string(destination).TrimEnd('\0');

            if (!DeviceRebindApi.UpdateDriverForPlugAndPlayDevices(
                    0, hardwareId, infPath,
                    DeviceRebindApi.INSTALLFLAG_FORCE | DeviceRebindApi.INSTALLFLAG_NONINTERACTIVE,
                    out int rebootRequired))
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return RebindResult.Failure(
                    $"UpdateDriverForPlugAndPlayDevices failed for {hardwareId} (win32 {error}: " +
                    $"{new System.ComponentModel.Win32Exception(error).Message})", error);
            }

            return RebindResult.Success(stagedName.Length > 0 ? stagedName : infPath, rebootRequired != 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RebindResult.Failure($"could not write the rebind INF: {ex.Message}");
        }
    }

    /// <summary>
    /// The INF for one vid/pid. It selects the inbox <c>winusb.sys</c> for the whole device, so
    /// every interface ends up on WinUSB. <c>bcdDevice</c> is wildcarded deliberately: the
    /// package must survive the device being re-plugged with a different firmware revision.
    /// </summary>
    public static string BuildInf(ushort vendorId, ushort productId, string infName) =>
        $$"""
        ; VirtualThere P2 capture INF (ARCHITECTURE.md §4.1) — generated for one device.
        ; Selects the inbox winusb.sys; ships no driver of ours.
        [Version]
        Signature   = "$Windows NT$"
        Class       = USBDevice
        ClassGuid   = {88BAE032-5A81-49f0-BC3D-A4FF138216D6}
        Provider    = "VirtualThere"
        CatalogFile = {{Path.GetFileNameWithoutExtension(infName)}}.cat
        DriverVer   = 10/14/2026,1.0.0.0

        [Manufacturer]
        %Vht% = VirtualThere,NTamd64

        [VirtualThere.NTamd64]
        %VhtDevice% = VhtWinUsb, USB\VID_{{vendorId:x4}}&PID_{{productId:x4}}

        [VhtWinUsb.NT]
        Include = winusb.inf
        Needs   = WINUSB.NT

        [VhtWinUsb.NT.Services]
        Include = winusb.inf
        Needs   = WINUSB.NT.Services

        [VhtWinUsb.NT.HW]
        AddReg = VhtWinUsb.NT.HW.AddReg

        [VhtWinUsb.NT.HW.AddReg]
        ; No hardware registry policy is set: WinUSB's defaults are used as-is.

        [Strings]
        Vht       = "VirtualThere"
        VhtDevice = "VirtualThere capturable USB device ({{vendorId:x4}}:{{productId:x4}})"
        """;
}
