using Vht.Acceptance;
using Vht.Core.Capture;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Protocol;
using Vht.Core.Server;
using Vht.Server.Control;
using Vht.Server.Devices;

namespace Vht.Acceptance.Tests;

/// <summary>
/// The per-device capture/rebind <em>outcome</em> is surfaced, not just the enumeration verdict.
/// </summary>
/// <remarks>
/// <para>
/// The reported defect: a device whose P2 rebind was refused, failed, or only lands after a reboot
/// simply vanished from the server window, so the operator had no way to tell "this device cannot
/// be exported" from "this device was not offered" from "the rebind ran and Windows said no". The
/// server already distinguished these outcomes in its runtime log; this suite pins the path from
/// the point that decides the outcome (<see cref="WinUsbCaptureProvider"/>) through the state
/// (<see cref="VhtServerState.CaptureOutcomes"/>) to the wire (<c>devices.list</c>), which is what
/// the window renders on each row.
/// </para>
/// <para>
/// Nothing here touches a real device: the rebinder is a recorder and the interface path is one the
/// provider fails to open, which is exactly the state a driver-held device is in.
/// </para>
/// </remarks>
public sealed class DeviceCaptureOutcomeTests
{
    private const ushort Vid = 0x0531;
    private const ushort Pid = 0x0101;
    private const int ErrorAccessDenied = 5;

    private const string HeldInterfacePath =
        @"\\?\usb#vid_0531&pid_0101#5&1a2b3c&0&1#{a5dcbf10-6530-11d2-901f-00c04fb951ed}";

    private static ServerOptions Options(bool rebind) => new()
    {
        ServerName = "vht-outcome-test",
        CaptureMode = "auto",
        RebindEnabled = rebind,
        ControlPlaneEnabled = false,
        LogLevel = LogLevel.Detail,
    };

    /// <summary>A driver-held device classified by the production rule, so a rebind is offered.</summary>
    private static UsbDevice DriverHeldDevice(string busId = "1-3")
    {
        (CaptureState state, CaptureReason reason, string detail) =
            UsbDeviceEnumerator.ClassifyOpenFailure(ErrorAccessDenied, rebindOffered: true);

        return new UsbDevice
        {
            Key = DeviceKey.FromBusId(busId),
            InstancePath = @"USB\VID_0531&PID_0101\5&1a2b3c&0&1",
            BusNum = 1,
            DevNum = 3,
            Speed = UsbIp.SpeedHigh,
            VendorId = Vid,
            ProductId = Pid,
            BcdDevice = 0x0100,
            DeviceClass = UsbClass.PerInterface,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            Manufacturer = "Wacom",
            Product = "Intuos Draw",
            Serial = "5&1a2b3c&0&1",
            CaptureState = state,
            CaptureReason = reason,
            CaptureDetail = detail,
            DevicePath = HeldInterfacePath,
        };
    }

    /// <summary>A device refused for a reason no rebind can fix.</summary>
    private static UsbDevice KeyboardDevice()
    {
        (CaptureState state, CaptureReason reason, string detail) =
            UsbDeviceEnumerator.ClassifyOpenFailure(ErrorAccessDenied, rebindOffered: false);

        return new UsbDevice
        {
            Key = DeviceKey.FromBusId("1-9"),
            InstancePath = @"USB\VID_046D&PID_C31C\6&1234abcd&0&1",
            BusNum = 1,
            DevNum = 9,
            Speed = UsbIp.SpeedLow,
            VendorId = 0x046d,
            ProductId = 0xc31c,
            DeviceClass = UsbClass.Hid,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            Product = "USB Keyboard",
            CaptureState = CaptureState.UnsafeToExport,
            CaptureReason = CaptureReason.SystemCritical,
            CaptureDetail = "Keyboard class device; exporting the console input device is never allowed",
            DevicePath = HeldInterfacePath,
        };
    }

    private static WinUsbCaptureProvider Provider(
        UsbDevice device,
        ServerOptions options,
        IDeviceRebinder rebinder,
        CaptureOutcomeLog outcomes,
        out AcceptanceRig.CapturingLogger log)
    {
        log = new AcceptanceRig.CapturingLogger();
        return new WinUsbCaptureProvider(new FixedEnumerator(device), options, log, rebinder, outcomes);
    }

    private static CaptureOutcome SingleOutcome(CaptureOutcomeLog outcomes) => Assert.Single(outcomes.Snapshot());

    // =====================================================================================
    // The three outcomes the server distinguishes, recorded per device
    // =====================================================================================

    [Fact]
    public async Task NotElevated_IsRecordedAsThisDevicesOutcome_WithThePathThatCouldNotRun()
    {
        var outcomes = new CaptureOutcomeLog();
        var rebinder = new RecordingRebinder(canRebind: false);
        WinUsbCaptureProvider provider = Provider(
            DriverHeldDevice(), Options(rebind: true), rebinder, outcomes, out _);

        UsbDevice device = Assert.Single(await provider.EnumerateAsync(CancellationToken.None));
        CaptureOpenResult open = await provider.OpenAsync(device, CancellationToken.None);

        Assert.False(open.Ok);
        Assert.Empty(rebinder.Attempts);

        CaptureOutcome outcome = SingleOutcome(outcomes);
        Assert.Equal(device.Key, outcome.Key);
        Assert.Equal("P2", outcome.Path);
        Assert.Equal(CaptureOutcomeStatus.NotElevated, outcome.Status);
        Assert.Contains("not elevated", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RebindFailed_IsRecordedAsThisDevicesOutcome_WithTheWin32Detail()
    {
        var outcomes = new CaptureOutcomeLog();
        var rebinder = new RecordingRebinder(
            canRebind: true, RebindResult.Failure("SetupCopyOEMInf failed for oem42.inf (win32 740)", 740));
        WinUsbCaptureProvider provider = Provider(
            DriverHeldDevice(), Options(rebind: true), rebinder, outcomes, out _);

        UsbDevice device = Assert.Single(await provider.EnumerateAsync(CancellationToken.None));
        await provider.OpenAsync(device, CancellationToken.None);

        CaptureOutcome outcome = SingleOutcome(outcomes);
        Assert.Equal("P2", outcome.Path);
        Assert.Equal(CaptureOutcomeStatus.RebindFailed, outcome.Status);
        Assert.StartsWith("P2 rebind failed: ", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("win32 740", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RebootRequired_IsRecordedAsThisDevicesOutcome_AndSaysSoInTheOperatorsWords()
    {
        var outcomes = new CaptureOutcomeLog();
        var rebinder = new RecordingRebinder(
            canRebind: true, RebindResult.Success(@"oem42.inf", rebootRequired: true));
        WinUsbCaptureProvider provider = Provider(
            DriverHeldDevice(), Options(rebind: true), rebinder, outcomes, out _);

        UsbDevice device = Assert.Single(await provider.EnumerateAsync(CancellationToken.None));
        CaptureOpenResult open = await provider.OpenAsync(device, CancellationToken.None);

        // The device could not be taken this run, but the attempt is not a failure to report
        // silently: the operator has to be told a reboot is what stands between them and the device.
        Assert.False(open.Ok);

        CaptureOutcome outcome = SingleOutcome(outcomes);
        Assert.Equal("P2", outcome.Path);
        Assert.Equal(CaptureOutcomeStatus.RebootRequired, outcome.Status);
        Assert.Contains("rebound", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("a reboot is required", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReboundButNotYetClaimable_EndsAsClaimAfterRebindFailed_AndSaysItWasHandedBack()
    {
        var outcomes = new CaptureOutcomeLog();
        var rebinder = new RecordingRebinder(canRebind: true);
        WinUsbCaptureProvider provider = Provider(
            DriverHeldDevice(), Options(rebind: true), rebinder, outcomes, out _);

        UsbDevice device = Assert.Single(await provider.EnumerateAsync(CancellationToken.None));
        await provider.OpenAsync(device, CancellationToken.None);

        // The enumeration is re-run after a rebind because the interface path changes; here the
        // fixed enumerator hands back the same, still-unopenable interface, so the claim fails and
        // the device is handed back. The last outcome must describe that, not the intermediate
        // success - the row has to tell the operator what state the device is in now.
        Assert.Single(rebinder.Attempts);

        CaptureOutcome outcome = SingleOutcome(outcomes);
        Assert.Equal("P2", outcome.Path);
        Assert.Equal(CaptureOutcomeStatus.ClaimAfterRebindFailed, outcome.Status);
        Assert.Contains("rebound onto the inbox winusb.sys", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("handed back", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusedBeforeAnyPath_StillGetsAnOutcome_SoTheReasonIsPerDevice()
    {
        var outcomes = new CaptureOutcomeLog();
        var rebinder = new RecordingRebinder(canRebind: true);
        WinUsbCaptureProvider provider = Provider(
            KeyboardDevice(), Options(rebind: true), rebinder, outcomes, out _);

        UsbDevice device = Assert.Single(await provider.EnumerateAsync(CancellationToken.None));
        await provider.OpenAsync(device, CancellationToken.None);

        CaptureOutcome outcome = SingleOutcome(outcomes);
        Assert.Equal("none", outcome.Path);
        Assert.Equal(CaptureOutcomeStatus.Refused, outcome.Status);
        Assert.Contains("never allowed", outcome.Detail, StringComparison.Ordinal);
    }

    // =====================================================================================
    // The state: a refused device is observed, and unplugging forgets its story
    // =====================================================================================

    [Fact]
    public async Task ADeviceThatCannotBeExported_IsStillObserved_SoItCannotVanishFromTheView()
    {
        var outcomes = new CaptureOutcomeLog();
        var options = Options(rebind: false);
        var log = new AcceptanceRig.CapturingLogger();
        UsbDevice keyboard = KeyboardDevice();
        var provider = new WinUsbCaptureProvider(new FixedEnumerator(keyboard), options, log, null, outcomes);
        var state = new VhtServerState(options, provider, log, captureOutcomes: outcomes);

        int exported = await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Equal(0, exported);
        Assert.Empty(state.Exports.Snapshot());

        UsbDevice observed = Assert.Single(state.ObservedDevices());
        Assert.Equal(keyboard.Key, observed.Key);
        Assert.Equal(CaptureReason.SystemCritical, observed.CaptureReason);
    }

    [Fact]
    public async Task UnpluggingADevice_ForgetsItsOutcome_SoAReplugStartsWithACleanStory()
    {
        var outcomes = new CaptureOutcomeLog();
        var options = Options(rebind: true);
        var log = new AcceptanceRig.CapturingLogger();
        UsbDevice device = DriverHeldDevice();
        var provider = new WinUsbCaptureProvider(
            new FixedEnumerator(device), options, log, new RecordingRebinder(canRebind: false), outcomes);
        var state = new VhtServerState(options, provider, log, captureOutcomes: outcomes);

        await state.RefreshExportsAsync(CancellationToken.None);
        await provider.OpenAsync(device, CancellationToken.None);
        Assert.Single(outcomes.Snapshot());

        state.ApplyHotplugChange(device, present: false);

        Assert.Empty(state.ObservedDevices());
        Assert.Empty(outcomes.Snapshot());
    }

    // =====================================================================================
    // The wire: what the window actually receives
    // =====================================================================================

    [Fact]
    public async Task DevicesList_CarriesThePerDeviceOutcome_ForExportedAndRefusedDevicesAlike()
    {
        string pipeName = "vht-outcome-pipe-" + Guid.NewGuid().ToString("N");
        var outcomes = new CaptureOutcomeLog();
        var options = new ServerOptions
        {
            ServerName = "vht-outcome-test",
            CaptureMode = "auto",
            RebindEnabled = true,
            ControlPlaneEnabled = false,
            ControlPlanePipeName = pipeName,
            LogLevel = LogLevel.Detail,
        };

        var log = new AcceptanceRig.CapturingLogger();
        UsbDevice held = DriverHeldDevice();
        UsbDevice keyboard = KeyboardDevice();
        var provider = new WinUsbCaptureProvider(
            new MultiEnumerator(held, keyboard),
            options,
            log,
            new RecordingRebinder(canRebind: false),
            outcomes);
        var state = new VhtServerState(options, provider, log, captureOutcomes: outcomes);

        await state.RefreshExportsAsync(CancellationToken.None);

        // The claim that fails for the rebind-offered device, exactly as OP_REQ_IMPORT does it.
        await state.Capture.OpenAsync(held, CancellationToken.None);

        await using var control = new ControlPlaneServer(state, ring: null, stopServer: null);
        control.Start();
        await WaitForPipeAsync(pipeName);

        using var client = new ControlClient(pipeName, TimeSpan.FromSeconds(5));
        System.Text.Json.Nodes.JsonObject list = await ListWithRetryAsync(client);

        var rows = list["devices"]!.AsArray()
            .Select(node => node!.AsObject())
            .ToDictionary(row => row["busid"]!.GetValue<string>(), StringComparer.Ordinal);

        // 1. The refused device is listed at all - the defect was that it was omitted.
        Assert.True(rows.ContainsKey("1-9"), "the refused device is missing from devices.list");
        System.Text.Json.Nodes.JsonObject refused = rows["1-9"];
        Assert.False(refused["exported"]!.GetValue<bool>());
        Assert.Equal("UnsafeToExport", refused["state"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(refused["detail"]!.GetValue<string>()));

        // 2. The device whose rebind was refused is listed with the outcome the provider recorded.
        Assert.True(rows.ContainsKey("1-3"), "the rebind-offered device is missing from devices.list");
        System.Text.Json.Nodes.JsonObject driverHeld = rows["1-3"];
        Assert.True(driverHeld["exported"]!.GetValue<bool>());
        Assert.Equal("P2", driverHeld["capture_path"]!.GetValue<string>());
        string outcome = driverHeld["capture_outcome"]!.GetValue<string>();
        Assert.Contains("not elevated", outcome, StringComparison.Ordinal);
    }

    private static async Task WaitForPipeAsync(string pipeName)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists($@"\\.\pipe\{pipeName}"))
            {
                // A pipe with a listener is what a client can connect to; name existence is the
                // closest cheap probe, and the retrying client below is the real check.
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException($"no control pipe named '{pipeName}' appeared within 5 s");
    }

    /// <summary>
    /// One successful open consumes the server's pipe instance, and the accept loop publishes the
    /// next one asynchronously; retrying the request is what makes this deterministic.
    /// </summary>
    private static async Task<System.Text.Json.Nodes.JsonObject> ListWithRetryAsync(ControlClient client)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return client.ListDevices();
            }
            catch (ControlPlaneException ex)
            {
                last = ex;
                await Task.Delay(100);
            }
        }

        throw last ?? new InvalidOperationException("devices.list never succeeded");
    }

    /// <summary>An enumerator over one fixed device.</summary>
    private sealed class FixedEnumerator : IDeviceEnumerator
    {
        private readonly UsbDevice _device;

        public FixedEnumerator(UsbDevice device) => _device = device;

        public long Generation => 1;

        public ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<UsbDevice>>(new[] { _device });
    }

    /// <summary>An enumerator over several fixed devices.</summary>
    private sealed class MultiEnumerator : IDeviceEnumerator
    {
        private readonly IReadOnlyList<UsbDevice> _devices;

        public MultiEnumerator(params UsbDevice[] devices) => _devices = devices;

        public long Generation => 1;

        public ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct) =>
            ValueTask.FromResult(_devices);
    }

    private sealed class RecordingRebinder : IDeviceRebinder
    {
        private readonly RebindResult _result;

        public RecordingRebinder(bool canRebind, RebindResult? result = null)
        {
            CanRebind = canRebind;
            _result = result ?? RebindResult.Success(@"oem42.inf", rebootRequired: false);
        }

        public bool CanRebind { get; }

        public List<(ushort VendorId, ushort ProductId)> Attempts { get; } = new();

        public RebindResult Rebind(ushort vendorId, ushort productId)
        {
            Attempts.Add((vendorId, productId));
            return _result;
        }

        public RestoreResult Restore(ushort vendorId, ushort productId) =>
            RestoreResult.NoRecord($@"USB\VID_{vendorId:X4}&PID_{productId:X4}");

        public IReadOnlyList<RestoreResult> RestorePending() => Array.Empty<RestoreResult>();

        public int PendingHandbacks => 0;
    }
}
