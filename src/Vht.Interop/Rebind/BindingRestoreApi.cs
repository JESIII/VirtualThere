using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vht.Interop.Rebind;

/// <summary>
/// The driver-store entry points the P2 <em>handback</em> needs: removing the package the rebind
/// staged, so the machine is left as it was found.
/// </summary>
/// <remarks>
/// <para>
/// A rebind stages <c>vht-xxxxyyyy.inf</c> through <c>SetupCopyOEMInf</c>, which the driver store
/// to take a name like <c>oem42.inf</c>. Re-selecting the original INF gives the device back; this
/// removes the leftover package. It is the difference between "the device works again" and "the
/// device works again but the operator's driver store still carries our INF".
/// </para>
/// <para>
/// Requires administrator rights, like every other call on this path, and fails with
/// <c>ERROR_ACCESS_DENIED</c> (5) otherwise - reported, never retried.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class BindingRestoreApi
{
    /// <summary><c>SUOI_FORCEDELETE</c> — delete the package even if a device still uses it.</summary>
    public const uint SUOI_FORCEDELETE = 0x00000001;

    /// <summary>
    /// Removes a driver package from the driver store by the <c>oemNN.inf</c> name the store
    /// assigned it. <paramref name="force"/> maps to <see cref="SUOI_FORCEDELETE"/>.
    /// </summary>
    [LibraryImport("setupapi.dll", EntryPoint = "SetupUninstallOEMInfW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupUninstallOEMInf(string infFileName, uint flags, nint reserved);
}
