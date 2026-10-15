using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vht.Interop.Rebind;
using Vht.Interop.Services;

namespace Vht.Server.Devices;

/// <summary>
/// The shipping <see cref="IDeviceBindingApi"/>: the SetupAPI and newdev entry points the P2
/// rebind and its handback actually call.
/// </summary>
/// <remarks>
/// Thin by design - no policy lives here. Whether a device should be moved, whether the prior
/// binding is worth recording and what to do when a restore fails are decisions
/// <see cref="WinUsbRebinder"/> makes; this class only reports what the machine said, including
/// the win32 error, so the refusal that reaches the operator carries a real reason.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceBindingApi : IDeviceBindingApi
{
    /// <inheritdoc />
    public bool IsElevated => WindowsServiceManager.IsElevated;

    /// <inheritdoc />
    public IReadOnlyList<DeviceBinding> ReadBindings(ushort vendorId, ushort productId) =>
        PriorBindingQuery.ReadBindings(vendorId, productId);

    /// <inheritdoc />
    public StagedPackage StagePackage(string infPath)
    {
        var destination = new char[DeviceRebindApi.MaxInfPath];

        if (!DeviceRebindApi.SetupCopyOEMInf(
                infPath, null, DeviceRebindApi.SPOST_PATH, 0,
                destination, destination.Length, out _, out _))
        {
            int error = Marshal.GetLastWin32Error();
            return StagedPackage.Failure(
                $"SetupCopyOEMInf failed for {infPath} (win32 {error}: {new Win32Exception(error).Message})",
                error);
        }

        string staged = new string(destination).TrimEnd('\0');
        return StagedPackage.Success(staged, infPath, $"staged {staged}");
    }

    /// <inheritdoc />
    public bool SelectDriver(string hardwareId, string infPath, out int rebootRequired, out int win32Error)
    {
        bool ok = DeviceRebindApi.UpdateDriverForPlugAndPlayDevices(
            0, hardwareId, infPath,
            DeviceRebindApi.INSTALLFLAG_FORCE | DeviceRebindApi.INSTALLFLAG_NONINTERACTIVE,
            out rebootRequired);

        win32Error = ok ? 0 : Marshal.GetLastWin32Error();

        // The out parameter is only meaningful when the call succeeded; Windows leaves it
        // untouched on failure, and a stale non-zero value would be reported as a pending reboot
        // that nobody asked for.
        if (!ok) rebootRequired = 0;

        return ok;
    }

    /// <inheritdoc />
    public bool UninstallPackage(string infName, out int win32Error)
    {
        if (BindingRestoreApi.SetupUninstallOEMInf(infName, BindingRestoreApi.SUOI_FORCEDELETE, 0))
        {
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }
}
