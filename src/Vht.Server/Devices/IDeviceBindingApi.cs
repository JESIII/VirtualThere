using Vht.Interop.Rebind;

namespace Vht.Server.Devices;

/// <summary>The driver-store package a rebind staged, under the name the store assigned it.</summary>
public readonly struct StagedPackage
{
    public bool Ok { get; init; }

    /// <summary>The <c>oemNN.inf</c> name the store assigned, e.g. <c>oem42.inf</c>.</summary>
    public string InfName { get; init; }

    /// <summary>The same package's path, for a log line the operator can act on.</summary>
    public string InfPath { get; init; }

    public int Win32Error { get; init; }
    public string Detail { get; init; }

    public static StagedPackage Success(string infName, string infPath, string detail) =>
        new() { Ok = true, InfName = infName, InfPath = infPath, Detail = detail };

    public static StagedPackage Failure(string detail, int win32Error = 0) =>
        new() { Ok = false, Detail = detail, Win32Error = win32Error };
}

/// <summary>
/// The machine surface the P2 rebind and its handback stand on: read a devnode's current
/// binding, stage a package, re-select a driver, remove a package.
/// </summary>
/// <remarks>
/// <para>
/// This exists for the same reason <see cref="IDeviceRebinder"/> does, one level down: the
/// <em>decision</em> "record the prior binding, then move the device, then hand it back" is what
/// has to be tested, and it must be testable without a driver store or an elevated token. The
/// shipping implementation is <see cref="WindowsDeviceBindingApi"/>; the tests drive
/// <see cref="WinUsbRebinder"/> against a recording implementation.
/// </para>
/// <para>
/// Every member except <see cref="ReadBindings"/> can change the machine, and every one of them
/// requires elevation. <see cref="ReadBindings"/> is read-only and is safe from any path.
/// </para>
/// </remarks>
public interface IDeviceBindingApi
{
    /// <summary>True when this process holds the rights the write calls need.</summary>
    bool IsElevated { get; }

    /// <summary>
    /// What every present devnode with this hardware id is bound to right now. Read-only.
    /// </summary>
    IReadOnlyList<DeviceBinding> ReadBindings(ushort vendorId, ushort productId);

    /// <summary>Copies an INF into the driver store, returning the <c>oemNN.inf</c> it took.</summary>
    StagedPackage StagePackage(string infPath);

    /// <summary>
    /// Points the devnodes matching <paramref name="hardwareId"/> at
    /// <paramref name="infPath"/>. <paramref name="rebootRequired"/> is set when Windows wants a
    /// restart before the selection is effective.
    /// </summary>
    bool SelectDriver(string hardwareId, string infPath, out int rebootRequired, out int win32Error);

    /// <summary>Removes a staged package from the driver store by its <c>oemNN.inf</c> name.</summary>
    bool UninstallPackage(string infName, out int win32Error);
}
