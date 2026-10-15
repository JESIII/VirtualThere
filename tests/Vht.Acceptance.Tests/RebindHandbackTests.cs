using Vht.Acceptance;
using Vht.Core.Capture;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Protocol;
using Vht.Core.Server;
using Vht.Interop.Rebind;
using Vht.Server.Devices;

namespace Vht.Acceptance.Tests;

/// <summary>
/// The P2 rebind is reversible: the operator's device comes back.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CaptureRebindPathTests"/> pins the rebind <em>direction</em> - that the P2 path is
/// reached at all. This suite pins the other half, which is the half the operator feels: a device
/// this server moved is put back on the driver it had, when the client detaches, when the device
/// is released, and when the server stops - and when the process died before any of that could
/// run, the next start finishes the job from the record on disk.
/// </para>
/// <para>
/// The machine is stood in for by <see cref="FakeMachine"/>, which keeps the state a real driver
/// store keeps (which INF each devnode is selected on, and which packages are staged), so
/// "rebind, then hand back, and the device is on its previous driver again" is asserted as a
/// state and not as a flag. The one thing no test here can do is move a real device: that needs
/// elevation and a device to spare, and is reported as NOT RUN in the card's transcript.
/// </para>
/// </remarks>
public sealed class RebindHandbackTests
{
    private const ushort Vid = 0x0781;
    private const ushort Pid = 0x5583;

    private const string HardwareId = @"USB\VID_0781&PID_5583";
    private const string InstanceId = @"USB\VID_0781&PID_5583\4C530001120328105553";

    /// <summary>The INF the device was on before this server touched it.</summary>
    private const string PriorInfName = "disk.inf";

    private const string PriorInfPath = @"C:\Windows\INF\disk.inf";

    /// <summary>The <c>oemNN.inf</c> name the driver store gives the package a rebind stages.</summary>
    private const string StagedInfName = "oem42.inf";

    // =====================================================================================
    // The record: written before the machine is touched, and durable
    // =====================================================================================

    [Fact]
    public void Rebind_RecordsThePriorBinding_OnDiskBeforeTheDriverIsReSelected()
    {
        FakeMachine machine = new();
        using TempDirectory directory = new();
        var rebinder = new WinUsbRebinder(machine, directory.Path);
        string? recordAtSelection = null;

        // The record has to exist *before* the binding changes: a rebind that succeeded and a
        // process that then died cannot be repaired by anything written afterwards.
        machine.OnSelectDriver = _ => recordAtSelection = rebinder.Store.FilePath is { } file && File.Exists(file)
            ? File.ReadAllText(file)
            : null;

        RebindResult result = rebinder.Rebind(Vid, Pid);

        Assert.True(result.Ok);
        Assert.Equal(StagedInfName, result.InfPath);
        Assert.False(result.RebootRequired);

        Assert.NotNull(recordAtSelection);
        Assert.Contains(PriorInfName, recordAtSelection, StringComparison.Ordinal);
        Assert.Contains(InstanceId.Replace(@"\", @"\\", StringComparison.Ordinal), recordAtSelection, StringComparison.Ordinal);

        // The order is the contract: read the binding, stage the package, move the device.
        Assert.Equal(
            new[]
            {
                $"read:{Vid:x4}:{Pid:x4}",
                $"stage:{Path.Combine(directory.Path, $"vht-{Vid:x4}{Pid:x4}.inf")}",
                $"select:{HardwareId}:{Path.Combine(directory.Path, $"vht-{Vid:x4}{Pid:x4}.inf")}",
            },
            machine.Calls);

        // And the device really is on the package the rebind staged.
        Assert.Equal(StagedInfName, machine.SelectedInfName(InstanceId));
        Assert.True(File.Exists(rebinder.Store.FilePath));
    }

    [Fact]
    public void Rebind_FailsWithoutMovingTheDevice_WhenThePriorBindingCannotBeRecorded()
    {
        FakeMachine machine = new();
        machine.Bindings.Clear();
        machine.Bindings.Add(new DeviceBinding { InstanceId = InstanceId, HardwareId = HardwareId });
        using TempDirectory directory = new();
        var rebinder = new WinUsbRebinder(machine, directory.Path);

        RebindResult result = rebinder.Rebind(Vid, Pid);

        Assert.False(result.Ok);
        Assert.Contains("could not be put back", result.Detail, StringComparison.Ordinal);

        // Nothing was staged and nothing was selected: a device we cannot put back is a device
        // this product does not take.
        Assert.Equal(new[] { $"read:{Vid:x4}:{Pid:x4}" }, machine.Calls);
        Assert.Equal(0, rebinder.PendingHandbacks);
        Assert.False(File.Exists(rebinder.Store.FilePath));
    }

    [Fact]
    public void Rebind_ReportsNoDevnode_WithoutStagingAnything()
    {
        FakeMachine machine = new();
        machine.Bindings.Clear();
        using TempDirectory directory = new();
        var rebinder = new WinUsbRebinder(machine, directory.Path);

        RebindResult result = rebinder.Rebind(Vid, Pid);

        Assert.False(result.Ok);
        Assert.Contains("nothing to move", result.Detail, StringComparison.Ordinal);
        Assert.Equal(new[] { $"read:{Vid:x4}:{Pid:x4}" }, machine.Calls);
    }

    [Fact]
    public void RecordedPriorBinding_SurvivesAServerRestart_AndIsReadBack()
    {
        FakeMachine machine = new();
        using TempDirectory directory = new();

        var firstRun = new WinUsbRebinder(machine, directory.Path);
        Assert.True(firstRun.Rebind(Vid, Pid).Ok);

        // A second rebinder over the same directory is what the next process sees: the object is
        // built from the file, not from anything the first instance still held in memory.
        var restarted = new WinUsbRebinder(machine, directory.Path, new AcceptanceRig.CapturingLogger());

        PriorBindingRecord record = Assert.Single(restarted.Store.Snapshot());
        Assert.Equal(InstanceId, record.InstanceId);
        Assert.Equal(HardwareId, record.HardwareId);
        Assert.Equal(PriorInfName, record.PriorInfName);
        Assert.Equal(PriorInfPath, record.PriorInfPath);
        Assert.Equal("disk", record.PriorService);
        Assert.Equal("Microsoft", record.PriorProvider);
        Assert.Equal(PriorBindingPhase.Rebound, record.Phase);
        Assert.Equal(StagedInfName, record.StagedInfName);
        Assert.Equal(1, restarted.PendingHandbacks);
    }

    [Fact]
    public void Store_ReportsARecordItCannotParse_InsteadOfThrowing()
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "prior-bindings.json"), "{ this is not json");
        var log = new AcceptanceRig.CapturingLogger();

        var store = new PriorBindingStore(directory.Path, log);

        Assert.Empty(store.Snapshot());
        Assert.Contains(
            log.Snapshot(),
            line => line.Contains("could not be read", StringComparison.Ordinal));
    }

    // =====================================================================================
    // The round trip: rebind, then hand back, and the device is on its previous driver
    // =====================================================================================

    [Fact]
    public void Restore_PutsTheDeviceBackOnItsPreviousDriver_AndRemovesTheStagedPackage()
    {
        FakeMachine machine = new();
        using TempDirectory directory = new();
        var rebinder = new WinUsbRebinder(machine, directory.Path);

        Assert.True(rebinder.Rebind(Vid, Pid).Ok);
        Assert.Equal(StagedInfName, machine.SelectedInfName(InstanceId));

        machine.Calls.Clear();
        RestoreResult restore = rebinder.Restore(Vid, Pid);

        Assert.True(restore.Ok);
        Assert.False(restore.NothingToDo);
        Assert.False(restore.RebootRequired);
        Assert.Equal(InstanceId, restore.InstanceId);

        // The device is back on the driver it had, and the package this server staged is gone.
        Assert.Equal(PriorInfName, machine.SelectedInfName(InstanceId));
        Assert.Equal(PriorInfPath, machine.SelectedInfPath(InstanceId));
        Assert.Empty(machine.StagedPackages);

        Assert.Equal(
            new[] { $"select:{HardwareId}:{PriorInfPath}", $"uninstall:{StagedInfName}" },
            machine.Calls);

        // Nothing is outstanding, so a shutdown has nothing left to do.
        Assert.Equal(0, rebinder.PendingHandbacks);
        Assert.False(File.Exists(rebinder.Store.FilePath));
    }

    [Fact]
    public void Restore_ForADeviceThisServerNeverMoved_TouchesNothing()
    {
        FakeMachine machine = new();
        using TempDirectory directory = new();
        var rebinder = new WinUsbRebinder(machine, directory.Path);

        RestoreResult restore = rebinder.Restore(Vid, Pid);

        Assert.True(restore.Ok);
        Assert.True(restore.NothingToDo);
        Assert.Empty(machine.Calls);
    }

    [Fact]
    public void RebindTwice_DoesNotOverwriteTheRecordedBinding()
    {
        FakeMachine machine = new();
        using TempDirectory directory = new();
        var rebinder = new WinUsbRebinder(machine, directory.Path);

        Assert.True(rebinder.Rebind(Vid, Pid).Ok);
        PriorBindingRecord before = Assert.Single(rebinder.Store.Snapshot());

        // A second rebind of the same hardware id would re-read the *current* binding - which is
        // our own package - and destroy the only copy of how to get back. With a record already
        // in flight the rebind is refused, and an operator action (detach, kick, stop) is what
        // clears it.
        machine.SelectDriverFails = true;
        RebindResult again = rebinder.Rebind(Vid, Pid);

        Assert.False(again.Ok);
        Assert.Contains("nothing is moved on top of an unfinished record", again.Detail, StringComparison.Ordinal);

        PriorBindingRecord after = Assert.Single(rebinder.Store.Snapshot());
        Assert.Equal(before.PriorInfPath, after.PriorInfPath);
        Assert.Equal(PriorBindingPhase.Rebound, after.Phase);
    }

    // =====================================================================================
    // Honest failures: no elevation, a reboot, a driver store that refuses
    // =====================================================================================

    [Fact]
    public void Restore_WithoutElevation_ReportsTheExactUndoPathAndKeepsTheRecord()
    {
        FakeMachine machine = new();
        using TempDirectory directory = new();
        var rebinder = new WinUsbRebinder(machine, directory.Path);
        Assert.True(rebinder.Rebind(Vid, Pid).Ok);

        machine.Calls.Clear();
        machine.IsElevated = false;
        RestoreResult restore = rebinder.Restore(Vid, Pid);

        Assert.False(restore.Ok);
        Assert.Equal(5, restore.Win32Error);
        Assert.Contains("pnputil /delete-driver oem42.inf /uninstall /force", restore.Detail, StringComparison.Ordinal);
        Assert.Contains(PriorInfPath, restore.Detail, StringComparison.Ordinal);

        // Nothing was touched, and the record is still there for the next start to finish.
        Assert.Empty(machine.Calls);
        Assert.Equal(1, rebinder.PendingHandbacks);
    }

    [Fact]
    public void Rebind_ThatNeedsAReboot_IsReported_SoTheCallerCanRefuseTheDevice()
    {
        FakeMachine machine = new();
        machine.RebootRequired = 1;
        using TempDirectory directory = new();
        var rebinder = new WinUsbRebinder(machine, directory.Path);

        RebindResult rebind = rebinder.Rebind(Vid, Pid);

        // The rebinder reports the fact; the policy - "do not take a device that is not in effect
        // until a restart" - is the provider's, and is asserted in the provider tests below.
        Assert.True(rebind.Ok);
        Assert.True(rebind.RebootRequired);

        // The handback of that same record says the same thing, and keeps the record so the
        // reboot finishes the job instead of pretending it is done.
        RestoreResult restore = rebinder.Restore(Vid, Pid);

        Assert.True(restore.Ok);
        Assert.True(restore.RebootRequired);
        Assert.Contains("reboot", restore.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rebinder.PendingHandbacks);

        // The package stays: the devnode is still on it until the reboot.
        Assert.Contains(StagedInfName, machine.StagedPackages);
    }

    [Fact]
    public void Restore_WhoseDriverStoreRefusalLeavesThePackage_NamesTheUndoPath()
    {
        FakeMachine machine = new();
        using TempDirectory directory = new();
        var rebinder = new WinUsbRebinder(machine, directory.Path);
        Assert.True(rebinder.Rebind(Vid, Pid).Ok);

        machine.UninstallFails = true;
        RestoreResult restore = rebinder.Restore(Vid, Pid);

        // The device itself is back where it was; the leftover package is what failed, and the
        // message says how to remove it.
        Assert.False(restore.Ok);
        Assert.Equal(PriorInfName, machine.SelectedInfName(InstanceId));
        Assert.Contains($"pnputil /delete-driver {StagedInfName} /uninstall /force", restore.Detail, StringComparison.Ordinal);
        Assert.Equal(1, rebinder.PendingHandbacks);
    }

    [Fact]
    public void RestorePending_HandsBackARecordLeftByAKilledRun()
    {
        FakeMachine machine = new();
        using TempDirectory directory = new();

        // The run that moved the device is gone; only its record is left.
        var killedRun = new WinUsbRebinder(machine, directory.Path);
        Assert.True(killedRun.Rebind(Vid, Pid).Ok);

        var nextRun = new WinUsbRebinder(machine, directory.Path);
        machine.Calls.Clear();

        RestoreResult result = Assert.Single(nextRun.RestorePending());

        Assert.True(result.Ok);
        Assert.Equal(PriorInfName, machine.SelectedInfName(InstanceId));
        Assert.Equal(0, nextRun.PendingHandbacks);
    }

    // =====================================================================================
    // The provider: detach, release and shutdown all hand the device back
    // =====================================================================================

    [Fact]
    public async Task Provider_HandsTheDeviceBack_WhenTheClientDetaches()
    {
        var rebinder = new RecordingRebinder(canRebind: true,
            restore: RestoreResult.Success(InstanceId, PriorInfPath, rebootRequired: false));
        WinUsbCaptureProvider provider = Provider(rebinder, out _);

        await provider.ReleaseAsync(RebindOfferedDevice(), CancellationToken.None);

        Assert.Equal((Vid, Pid), Assert.Single(rebinder.Restores));
    }

    [Fact]
    public async Task Provider_HandsTheDeviceBack_WhenItIsReleasedExplicitly()
    {
        // `devices.kick` cancels the attach record; the session then tears down and releases the
        // device, which is this same call. The handback must not depend on who asked.
        var rebinder = new RecordingRebinder(canRebind: true,
            restore: RestoreResult.Success(InstanceId, PriorInfPath, rebootRequired: false));
        WinUsbCaptureProvider provider = Provider(rebinder, out _);
        UsbDevice device = RebindOfferedDevice();

        await provider.ReleaseAsync(device, CancellationToken.None);
        await provider.ReleaseAsync(device, CancellationToken.None);

        Assert.Equal(2, rebinder.Restores.Count);
    }

    [Fact]
    public async Task Provider_HandsBackEveryOutstandingRecord_OnShutdown()
    {
        var rebinder = new RecordingRebinder(canRebind: true);
        rebinder.Pending.Add(RestoreResult.Success(InstanceId, PriorInfPath, rebootRequired: false));
        WinUsbCaptureProvider provider = Provider(rebinder, out _);

        // DisposeAsync is what ServerRuntime calls as the server stops.
        await provider.DisposeAsync();

        Assert.Equal(1, rebinder.RestorePendingCalls);
    }

    [Fact]
    public async Task Provider_ReportsAFailedHandback_InsteadOfSilence()
    {
        var rebinder = new RecordingRebinder(canRebind: true,
            restore: RestoreResult.Failure(
                $"re-selecting {PriorInfPath} failed (win32 5); undo by hand: pnputil /delete-driver oem42.inf /uninstall /force",
                5,
                InstanceId));
        WinUsbCaptureProvider provider = Provider(rebinder, out AcceptanceRig.CapturingLogger log);

        await provider.ReleaseAsync(RebindOfferedDevice(), CancellationToken.None);

        Assert.Contains(
            log.Snapshot(),
            line => line.Contains("handing the device back failed", StringComparison.Ordinal)
                 && line.Contains("pnputil /delete-driver oem42.inf /uninstall /force", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provider_DoesNotClaimADevice_WhenTheRebindNeedsAReboot()
    {
        var rebinder = new RecordingRebinder(canRebind: true,
            RebindResult.Success(StagedInfName, rebootRequired: true),
            RestoreResult.Success(InstanceId, PriorInfPath, rebootRequired: false));
        WinUsbCaptureProvider provider = Provider(rebinder, out AcceptanceRig.CapturingLogger log);

        CaptureOpenResult open = await provider.OpenAsync(RebindOfferedDevice(), CancellationToken.None);

        // Nothing is handed to a client: the binding does not exist until the machine restarts.
        Assert.False(open.Ok);
        Assert.Equal(CaptureReason.DriverHoldsDevice, open.Reason);
        Assert.Contains("requires a reboot", open.Detail, StringComparison.Ordinal);

        // Said before the device was taken: the refusal names the reboot, and no success line
        // claims the device was rebound.
        IReadOnlyList<string> lines = log.Snapshot();
        Assert.Contains(lines, line => line.Contains("only takes effect after a reboot", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("rebound onto the inbox winusb.sys", StringComparison.Ordinal));

        // And the device was handed straight back rather than left moved.
        Assert.Equal((Vid, Pid), Assert.Single(rebinder.Restores));
        Assert.Equal(0, provider.ReboundCount);
    }

    [Fact]
    public async Task Provider_HandsTheDeviceBack_WhenItCannotBeClaimedAfterARebind()
    {
        var rebinder = new RecordingRebinder(canRebind: true,
            restore: RestoreResult.Success(InstanceId, PriorInfPath, rebootRequired: false));
        WinUsbCaptureProvider provider = Provider(rebinder, out _);

        // The device path in these tests cannot be opened, so the rebind "succeeds" and the claim
        // after it does not: the device must not be left on the package the rebind staged.
        CaptureOpenResult open = await provider.OpenAsync(RebindOfferedDevice(), CancellationToken.None);

        Assert.False(open.Ok);
        Assert.Equal((Vid, Pid), Assert.Single(rebinder.Restores));
        Assert.Equal(0, provider.ReboundCount);
    }

    // =====================================================================================
    // The no-disturbance property: scanning never reaches the rebind
    // =====================================================================================

    [Fact]
    public async Task RefreshExports_AndHotplug_NeverReachTheRebindOrTheOpenPath()
    {
        var rebinder = new RecordingRebinder(canRebind: true);
        var log = new AcceptanceRig.CapturingLogger();
        ServerOptions options = Options();
        var provider = new WinUsbCaptureProvider(
            new FixedEnumerator(RebindOfferedDevice()), options, log, rebinder);
        var state = new VhtServerState(options, provider, log);

        // The two paths that run on their own: the start-up/periodic refresh and the hotplug
        // watcher's arrival. Neither may touch a device - that is the property the operator
        // asked for in the first place ("devices should work like normal while they are served").
        await state.RefreshExportsAsync(CancellationToken.None);
        state.ApplyHotplugChange(RebindOfferedDevice(), present: true);
        await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Empty(rebinder.Attempts);
    }

    // =====================================================================================
    // Test doubles
    // =====================================================================================

    private static ServerOptions Options() => new()
    {
        ServerName = "vht-handback-test",
        CaptureMode = "auto",
        RebindEnabled = true,
        ControlPlaneEnabled = false,
        LogLevel = LogLevel.Detail,
    };

    private static UsbDevice RebindOfferedDevice()
    {
        (CaptureState state, CaptureReason reason, string detail) =
            UsbDeviceEnumerator.ClassifyOpenFailure(5, rebindOffered: true);

        return new UsbDevice
        {
            Key = DeviceKey.FromBusId("1-3"),
            InstancePath = InstanceId,
            BusNum = 1,
            DevNum = 3,
            Speed = UsbIp.SpeedHigh,
            VendorId = Vid,
            ProductId = Pid,
            BcdDevice = 0x0100,
            DeviceClass = UsbClass.PerInterface,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            Manufacturer = "SanDisk",
            Product = "Ultra Fit",
            Serial = "4C530001120328105553",
            CaptureState = state,
            CaptureReason = reason,
            CaptureDetail = detail,
            DevicePath = @"\\?\usb#vid_0781&pid_5583#4c530001120328105553#{a5dcbf10-6530-11d2-901f-00c04fb951ed}",
        };
    }

    private static WinUsbCaptureProvider Provider(IDeviceRebinder rebinder, out AcceptanceRig.CapturingLogger log)
    {
        log = new AcceptanceRig.CapturingLogger();
        return new WinUsbCaptureProvider(new FixedEnumerator(RebindOfferedDevice()), Options(), log, rebinder);
    }

    /// <summary>
    /// Stands in for the machine: it keeps the state a driver store keeps, so a handback can be
    /// asserted as "the devnode is selected on the INF it had", not as "a method was called".
    /// </summary>
    private sealed class FakeMachine : IDeviceBindingApi
    {
        /// <summary>
        /// Which INF each devnode is selected on, as Windows records it: the full path of the
        /// package in effect, <c>C:\Windows\INF\disk.inf</c> or <c>C:\Windows\INF\oem42.inf</c>.
        /// </summary>
        private readonly Dictionary<string, string> _selected = new(StringComparer.OrdinalIgnoreCase)
        {
            [InstanceId] = PriorInfPath,
        };

        /// <summary>Source INF path → the <c>oemNN.inf</c> name the store gave it, as the store does.</summary>
        private readonly Dictionary<string, string> _staged = new(StringComparer.Ordinal);

        public List<DeviceBinding> Bindings { get; } = new()
        {
            new DeviceBinding
            {
                InstanceId = InstanceId,
                HardwareId = HardwareId,
                InfName = PriorInfName,
                InfPath = PriorInfPath,
                Service = "disk",
                Provider = "Microsoft",
            },
        };

        public List<string> Calls { get; } = new();

        public HashSet<string> StagedPackages { get; } = new(StringComparer.Ordinal);

        /// <summary>Runs inside <c>StagePackage</c>, which is how a test sees the record on disk.</summary>
        public Action<string>? OnStagePackage { get; set; }

        /// <summary>Runs inside <c>SelectDriver</c>, for order-of-operations assertions.</summary>
        public Action<string>? OnSelectDriver { get; set; }

        public bool IsElevated { get; set; } = true;

        public int RebootRequired { get; set; }

        public bool SelectDriverFails { get; set; }

        public bool UninstallFails { get; set; }

        /// <summary>The INF name the devnode is selected on right now.</summary>
        public string SelectedInfName(string instanceId) =>
            _selected.TryGetValue(instanceId, out string? path) ? Path.GetFileName(path) : string.Empty;

        /// <summary>The full INF path the devnode is selected on right now.</summary>
        public string SelectedInfPath(string instanceId) =>
            _selected.TryGetValue(instanceId, out string? path) ? path : string.Empty;

        public IReadOnlyList<DeviceBinding> ReadBindings(ushort vendorId, ushort productId)
        {
            Calls.Add($"read:{vendorId:x4}:{productId:x4}");
            return Bindings.ToArray();
        }

        public StagedPackage StagePackage(string infPath)
        {
            Calls.Add($"stage:{infPath}");
            OnStagePackage?.Invoke(infPath);
            _staged[infPath] = StagedInfName;
            StagedPackages.Add(StagedInfName);
            return StagedPackage.Success(StagedInfName, infPath, $"staged {StagedInfName}");
        }

        public bool SelectDriver(string hardwareId, string infPath, out int rebootRequired, out int win32Error)
        {
            Calls.Add($"select:{hardwareId}:{infPath}");
            OnSelectDriver?.Invoke(infPath);

            rebootRequired = RebootRequired;
            win32Error = SelectDriverFails ? 31 : 0;

            if (SelectDriverFails)
                return false;

            // A devnode selected on a package that came from the driver store names the store's
            // own INF (oemNN.inf); a devnode re-selected on an INF already in Windows\INF names
            // that one. This is the difference the handback assertions turn on.
            string selected = _staged.TryGetValue(infPath, out string? storeName)
                ? System.IO.Path.Combine(@"C:\Windows\INF", storeName)
                : infPath;

            foreach (string instance in _selected.Keys.ToArray())
                _selected[instance] = selected;

            return true;
        }

        public bool UninstallPackage(string infName, out int win32Error)
        {
            Calls.Add($"uninstall:{infName}");
            win32Error = UninstallFails ? 5 : 0;

            if (UninstallFails)
                return false;

            StagedPackages.Remove(infName);

            foreach (string source in _staged.Where(p => p.Value == infName).Select(p => p.Key).ToArray())
                _staged.Remove(source);

            return true;
        }
    }

    /// <summary>An enumerator over one device the test controls.</summary>
    private sealed class FixedEnumerator : IDeviceEnumerator
    {
        private readonly UsbDevice _device;

        public FixedEnumerator(UsbDevice device) => _device = device;

        public long Generation => 1;

        public ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<UsbDevice>>(new[] { _device });
    }

    /// <summary>
    /// Records the rebinds and handbacks it is asked for, so a test can assert that the release
    /// and shutdown paths actually call the handback.
    /// </summary>
    private sealed class RecordingRebinder : IDeviceRebinder
    {
        private readonly RebindResult _rebind;
        private readonly RestoreResult _restore;

        public RecordingRebinder(bool canRebind, RebindResult? rebind = null, RestoreResult? restore = null)
        {
            CanRebind = canRebind;
            _rebind = rebind ?? RebindResult.Success(StagedInfName, rebootRequired: false);
            _restore = restore ?? RestoreResult.NoRecord(HardwareId);
        }

        public bool CanRebind { get; }

        public List<(ushort VendorId, ushort ProductId)> Attempts { get; } = new();

        public List<(ushort VendorId, ushort ProductId)> Restores { get; } = new();

        public List<RestoreResult> Pending { get; } = new();

        public int RestorePendingCalls { get; private set; }

        public int PendingHandbacks => Pending.Count;

        public RebindResult Rebind(ushort vendorId, ushort productId)
        {
            Attempts.Add((vendorId, productId));
            return _rebind;
        }

        public RestoreResult Restore(ushort vendorId, ushort productId)
        {
            Restores.Add((vendorId, productId));
            return _restore;
        }

        public IReadOnlyList<RestoreResult> RestorePending()
        {
            RestorePendingCalls++;
            return Pending.ToArray();
        }
    }

    /// <summary>A directory that is removed when the test ends, for the record file and staged INFs.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vht-handback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
