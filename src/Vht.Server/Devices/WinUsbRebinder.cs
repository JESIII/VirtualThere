using System.ComponentModel;
using System.IO;
using System.Runtime.Versioning;
using Vht.Core.Logging;
using Vht.Interop.Rebind;

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

    /// <summary>
    /// True when Windows wants a restart before the new binding is in effect. A caller must not
    /// treat the device as taken in that case: the operator is told <em>before</em> anything is
    /// handed to a client, and the rebind is handed back instead.
    /// </summary>
    public bool RebootRequired { get; init; }

    public string InfPath { get; init; }

    public static RebindResult Success(string infPath, bool rebootRequired) =>
        new() { Ok = true, InfPath = infPath, RebootRequired = rebootRequired, Detail = "rebound to winusb.sys" };

    public static RebindResult Failure(string detail, int win32Error = 0) =>
        new() { Ok = false, Detail = detail, Win32Error = win32Error };
}

/// <summary>
/// Installs a device-specific INF that binds the inbox <c>winusb.sys</c> to one vid/pid,
/// re-selects the driver for the existing devnodes — and hands the device back afterwards.
/// </summary>
/// <remarks>
/// <para>
/// The provider depends on <see cref="IDeviceRebinder"/> rather than on this class, so the P2
/// decision - "is a rebind attempted at all for this device?" - is testable without a driver
/// store or an elevated token; this class is the one that actually touches the machine.
/// </para>
/// <para>
/// <b>The pair rule.</b> A rebind is only ever performed together with a way back. The prior
/// binding of every devnode the rebind will move is read and written to
/// <see cref="PriorBindingStore"/> <em>before</em> the first call that can change a binding, and
/// <see cref="Restore"/> re-selects that INF and removes the package this class staged. A device
/// that cannot be put back is a device this product must not take (ARCHITECTURE.md §4.1,
/// <c>onPassToKernel</c> parity).
/// </para>
/// <para>
/// The unit of the operation is the hardware id, because that is what
/// <c>UpdateDriverForPlugAndPlayDevices</c> re-selects on: every present instance of a vid/pid
/// moves together, and every one of them is recorded and handed back individually.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinUsbRebinder : IDeviceRebinder
{
    private const int ERROR_ACCESS_DENIED = 5;

    /// <summary>Where staged INFs are written; the operator can inspect or delete them.</summary>
    public static string DefaultWorkingDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VirtualThere", "driver");

    private readonly IDeviceBindingApi _api;

    /// <summary>Creates the shipping rebinder over the entry points that touch the machine.</summary>
    public WinUsbRebinder(string workingDirectory)
        : this(new WindowsDeviceBindingApi(), workingDirectory, null)
    {
    }

    /// <summary>As above, with a log for the record/undo lines the operator has to read.</summary>
    public WinUsbRebinder(string workingDirectory, ILogger? log)
        : this(new WindowsDeviceBindingApi(), workingDirectory, log)
    {
    }

    /// <summary>
    /// The seam the tests drive: the same decision logic over a recording machine surface.
    /// </summary>
    public WinUsbRebinder(IDeviceBindingApi api, string workingDirectory, ILogger? log = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? DefaultWorkingDirectory : workingDirectory;
        Log = log;
        Store = new PriorBindingStore(WorkingDirectory, log);
    }

    /// <summary>Directory INF files are generated in, and where the record file lives.</summary>
    public string WorkingDirectory { get; }

    /// <summary>The durable record of what this server moved and has not handed back.</summary>
    public PriorBindingStore Store { get; }

    /// <summary>Where the record/undo lines go, when there is somewhere to write them.</summary>
    public ILogger? Log { get; }

    /// <summary>
    /// True when this process can change a driver binding at all. The process token is the only
    /// thing this can depend on, which is why it is a property of the rebinder rather than a
    /// constant of the caller.
    /// </summary>
    public bool CanRebind => _api.IsElevated;

    /// <inheritdoc />
    public int PendingHandbacks => Store.Snapshot().Count;

    /// <summary>
    /// The undo path an operator can run by hand, named exactly, for the one case where the
    /// automatic handback cannot finish (no elevation, or the driver store refuses).
    /// </summary>
    public static string ManualUndoPath(PriorBindingRecord record)
    {
        string device = string.IsNullOrEmpty(record.InstanceId) ? "the device" : record.InstanceId;
        string expected = string.IsNullOrEmpty(record.PriorInfPath) ? "the driver it had before" : record.PriorInfPath;

        return string.IsNullOrEmpty(record.StagedInfName)
            ? $"{device} must be re-selected onto {expected}: use Device Manager > {device} > " +
              "Update driver > Browse my computer, or re-run the server elevated so the handback can finish"
            : $"pnputil /delete-driver {record.StagedInfName} /uninstall /force, then Device Manager > " +
              $"{device} > Uninstall device and Scan for hardware changes so it returns to {expected}";
    }

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

        // A device this server already moved is not moved twice: the record holds the binding it
        // had *before*, and a second rebind would overwrite that with our own winusb binding -
        // destroying the only copy of how to get back. A record that is still here when a client
        // asks for this device again is stale (its client is gone, and P1 did not open on the
        // device either), so the handback is retried first and the rebind starts from a clean
        // slate instead of refusing forever.
        if (Store.ForHardwareId(hardwareId).Count > 0)
        {
            RestoreResult stale = Restore(vendorId, productId);
            if (!stale.Ok)
            {
                return RebindResult.Failure(
                    $"{hardwareId} is already recorded as moved by this server and the handback of that " +
                    $"earlier rebind did not finish ({stale.Detail}); nothing is moved on top of an " +
                    "unfinished record", stale.Win32Error);
            }
        }

        // ---- the prior binding, read and persisted *before* the machine is touched -------------
        IReadOnlyList<DeviceBinding> prior = _api.ReadBindings(vendorId, productId);
        if (prior.Count == 0)
        {
            return RebindResult.Failure(
                $"no present devnode has hardware id {hardwareId}: there is nothing to move onto winusb.sys");
        }

        DeviceBinding[] restorable = prior.Where(b => b.IsUsable).ToArray();
        if (restorable.Length == 0)
        {
            // Not one of the devnodes would tell us which INF it is running, so the operator's
            // way back cannot be written down. Taking the device anyway is the failure this pair
            // exists to prevent.
            return RebindResult.Failure(
                $"{hardwareId} is present but no devnode reported the driver package it is bound to, " +
                "so the previous binding could not be recorded; refusing to move a device that could not be put back");
        }

        Store.Replace(hardwareId, restorable.Select(b => new PriorBindingRecord
        {
            InstanceId = b.InstanceId,
            HardwareId = hardwareId,
            PriorInfName = b.InfName,
            PriorInfPath = b.InfPath,
            PriorService = b.Service,
            PriorProvider = b.Provider,
            Phase = PriorBindingPhase.Recorded,
        }));

        foreach (DeviceBinding binding in restorable)
        {
            Log?.Write(LogLevel.Info, LogEvent.DeviceBindingRecorded,
                $"recorded the binding of {binding.Describe()} before rebinding it to winusb.sys",
                new Dictionary<string, object?>
                {
                    ["instance_id"] = binding.InstanceId,
                    ["prior_inf"] = binding.InfName,
                    ["prior_inf_path"] = binding.InfPath,
                    ["record_file"] = Store.FilePath,
                });
        }

        string infName = $"vht-{vendorId:x4}{productId:x4}.inf";

        try
        {
            Directory.CreateDirectory(WorkingDirectory);
            string infPath = Path.Combine(WorkingDirectory, infName);
            File.WriteAllText(infPath, BuildInf(vendorId, productId, infName));

            // Stage the package. SetupAPI copies it into the driver store under an oemNN.inf
            // name, which is what the handback removes again.
            StagedPackage staged = _api.StagePackage(infPath);
            if (!staged.Ok)
            {
                // Nothing was staged, so nothing changed: the record goes back, and the operator
                // is not left holding a note about a device this server never moved.
                Store.Remove(hardwareId);
                return RebindResult.Failure(staged.Detail, staged.Win32Error);
            }

            Store.MarkStaged(hardwareId, staged.InfName, staged.InfPath);

            if (!_api.SelectDriver(hardwareId, infPath, out int rebootRequired, out int win32Error))
            {
                // The binding did not change. The record stays: the package is in the driver
                // store, and a handback is what removes it.
                return RebindResult.Failure(
                    $"UpdateDriverForPlugAndPlayDevices failed for {hardwareId} " +
                    $"(win32 {win32Error}: {Win32Message(win32Error)})",
                    win32Error);
            }

            Store.MarkRebound(hardwareId);
            return RebindResult.Success(staged.InfName.Length > 0 ? staged.InfName : infPath, rebootRequired != 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Store.Remove(hardwareId);
            return RebindResult.Failure($"could not write the rebind INF: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public RestoreResult Restore(ushort vendorId, ushort productId)
    {
        string hardwareId = DeviceRebindApi.HardwareId(vendorId, productId);
        IReadOnlyList<PriorBindingRecord> records = Store.ForHardwareId(hardwareId);

        if (records.Count == 0)
            return RestoreResult.NoRecord(hardwareId);

        RestoreResult last = default;
        RestoreResult? firstFailure = null;

        foreach (PriorBindingRecord record in records)
        {
            last = RestoreOne(record);
            if (!last.Ok) firstFailure ??= last;
        }

        return firstFailure ?? last;
    }

    /// <inheritdoc />
    public IReadOnlyList<RestoreResult> RestorePending()
    {
        var results = new List<RestoreResult>();
        foreach (PriorBindingRecord record in Store.Snapshot())
            results.Add(RestoreOne(record));

        return results;
    }

    /// <summary>
    /// Puts one recorded device back on the INF it had before, then removes the package this
    /// server staged. The record is dropped only when both halves are done: an unfinished
    /// handback is exactly what the next start has to be able to retry.
    /// </summary>
    private RestoreResult RestoreOne(PriorBindingRecord record)
    {
        if (!CanRebind)
        {
            return RestoreResult.Failure(
                "the handback needs an elevated process (UpdateDriverForPlugAndPlayDevices requires " +
                $"administrator rights), so {record.InstanceId} is still on the driver this server staged; " +
                $"re-run elevated, or undo it by hand: {ManualUndoPath(record)}",
                ERROR_ACCESS_DENIED,
                record.InstanceId);
        }

        if (record.Phase == PriorBindingPhase.Rebound)
        {
            if (!_api.SelectDriver(record.HardwareId, record.PriorInfPath, out int rebootRequired, out int win32Error))
            {
                return RestoreResult.Failure(
                    $"re-selecting {record.PriorInfPath} for {record.InstanceId} failed " +
                    $"(win32 {win32Error}: {Win32Message(win32Error)}); the device is still on " +
                    $"{record.StagedInfName ?? "the staged package"}. Undo by hand: {ManualUndoPath(record)}",
                    win32Error,
                    record.InstanceId);
            }

            if (rebootRequired != 0)
            {
                // Honest at the point of failure: the selection is recorded by Windows but not in
                // effect, so the devnode is still on our package and the package must stay. The
                // record is kept so the next start finishes the job.
                return RestoreResult.Success(record.InstanceId, record.PriorInfPath, rebootRequired: true);
            }
        }

        // The device is back on its own driver (or was never moved): the package this server
        // copied into the driver store is now unused and is removed.
        if (!string.IsNullOrEmpty(record.StagedInfName)
            && !_api.UninstallPackage(record.StagedInfName, out int uninstallError))
        {
            return RestoreResult.Failure(
                $"{record.InstanceId} is back on {record.PriorInfPath}, but the package this server " +
                $"staged ({record.StagedInfName}) could not be removed from the driver store " +
                $"(win32 {uninstallError}: {Win32Message(uninstallError)}). Remove it by hand: " +
                ManualUndoPath(record),
                uninstallError,
                record.InstanceId);
        }

        Store.RemoveInstance(record.InstanceId);

        Log?.Write(LogLevel.Info, LogEvent.DeviceHandedBack,
            $"{record.InstanceId} handed back to {record.PriorInfPath}" +
            (string.IsNullOrEmpty(record.StagedInfName) ? string.Empty : $"; removed {record.StagedInfName}"),
            new Dictionary<string, object?>
            {
                ["instance_id"] = record.InstanceId,
                ["prior_inf"] = record.PriorInfName,
                ["staged_inf"] = record.StagedInfName,
            });

        return RestoreResult.Success(record.InstanceId, record.PriorInfPath, rebootRequired: false);
    }

    private static string Win32Message(int error) =>
        error == 0 ? "no error" : new Win32Exception(error).Message;

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
