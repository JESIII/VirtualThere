using Vht.Acceptance;
using Vht.Core.Capture;
using Vht.Core.Concurrency;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Policy;
using Vht.Core.Protocol;
using Vht.Core.Server;
using Vht.Server.Devices;

namespace Vht.Acceptance.Tests;

/// <summary>
/// The P2 rebind path is <em>reachable</em>: a driver-held device the operator offered a rebind
/// for must arrive at the rebind attempt instead of being refused before the attempt is made.
/// </summary>
/// <remarks>
/// <para>
/// Regression suite for the deadlock where <c>capture.rebind.enabled</c> was inert by
/// construction: the enumerator reported every driver-held device as
/// <see cref="CaptureState.CaptureUnsupported"/>, and the capture provider returned on an
/// <c>IsExportable()</c> bail placed <em>above</em> the P2 block. Both halves are asserted here,
/// at the level where each lives - the classification rule that names the state, and the
/// provider/server path that has to act on it.
/// </para>
/// <para>
/// The assertions are about the <em>attempt</em>, not about a flag: a recorder is injected as
/// the rebinder and the tests fail unless <c>Rebind(vid, pid)</c> was actually called. Nothing
/// here touches a real device: the device path is a string the provider fails to open, which is
/// exactly the state a driver-held device is in.
/// </para>
/// </remarks>
public sealed class CaptureRebindPathTests
{
    private const ushort Vid = 0x046d;
    private const ushort Pid = 0x0aaa;

    /// <summary><c>ERROR_ACCESS_DENIED</c>: what <c>CreateFileW</c> returns on a driver-held
    /// device interface, i.e. the error the enumerator classifies.</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary><c>ERROR_FILE_NOT_FOUND</c>: an interface that is not there at all.</summary>
    private const int ErrorFileNotFound = 2;

    private const string HeldInterfacePath =
        @"\\?\usb#vid_046d&pid_0aaa#5&2f868a&0&1#{a5dcbf10-6530-11d2-901f-00c04fb951ed}";

    private static ServerOptions Options(bool rebind, string captureMode = "auto") => new()
    {
        ServerName = "vht-rebind-test",
        CaptureMode = captureMode,
        RebindEnabled = rebind,
        ControlPlaneEnabled = false,
        LogLevel = LogLevel.Detail,
    };

    /// <summary>
    /// A device classified exactly the way <see cref="UsbDeviceEnumerator"/> classifies it, so
    /// the tests exercise the production rule rather than a hand-written state.
    /// </summary>
    private static UsbDevice DriverHeldDevice(bool rebindOffered, int openError = ErrorAccessDenied)
    {
        (CaptureState state, CaptureReason reason, string detail) =
            UsbDeviceEnumerator.ClassifyOpenFailure(openError, rebindOffered);

        return new UsbDevice
        {
            Key = DeviceKey.FromBusId("1-3"),
            InstancePath = @"USB\VID_046D&PID_0AAA\5&2f868a&0&1",
            BusNum = 1,
            DevNum = 3,
            Speed = UsbIp.SpeedHigh,
            VendorId = Vid,
            ProductId = Pid,
            BcdDevice = 0x0100,
            DeviceClass = UsbClass.PerInterface,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            Manufacturer = "Logitech",
            Product = "USB Receiver",
            Serial = "5&2f868a&0&1",
            CaptureState = state,
            CaptureReason = reason,
            CaptureDetail = detail,
            DevicePath = HeldInterfacePath,
        };
    }

    /// <summary>A device refused for a reason no rebind can fix.</summary>
    private static UsbDevice UnsafeDevice() => new()
    {
        Key = DeviceKey.FromBusId("1-4"),
        InstancePath = @"USB\VID_046D&PID_C31C\6&1234abcd&0&1",
        BusNum = 1,
        DevNum = 4,
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

    private static WinUsbCaptureProvider Provider(
        UsbDevice device, ServerOptions options, IDeviceRebinder rebinder, out AcceptanceRig.CapturingLogger log)
    {
        log = new AcceptanceRig.CapturingLogger();
        return new WinUsbCaptureProvider(new FixedEnumerator(device), options, log, rebinder);
    }

    // =====================================================================================
    // The state: driver-held, but a rebind is offered and permitted
    // =====================================================================================

    [Fact]
    public void ClassifyOpenFailure_OffersRebindOnlyWhenPermitted_AndNamesTheState()
    {
        (CaptureState offered, CaptureReason offeredReason, string offeredDetail) =
            UsbDeviceEnumerator.ClassifyOpenFailure(ErrorAccessDenied, rebindOffered: true);

        // The state name is the contract: "driver-held, but claiming it means a rebind first"
        // must be distinguishable from "unordered user-mode capture is impossible".
        Assert.Equal("RebindOffered", offered.ToString());
        Assert.NotEqual(CaptureState.CaptureUnsupported, offered);
        Assert.Equal(CaptureReason.DriverHoldsDevice, offeredReason);

        Assert.True(offered.OffersRebind());
        Assert.True(offered.IsClaimable());

        // Not exportable *yet*: the device is still held by its function driver. The rebind is
        // what makes it exportable, and that is the whole point of the separate state.
        Assert.False(offered.IsExportable());

        (CaptureState refused, CaptureReason refusedReason, string refusedDetail) =
            UsbDeviceEnumerator.ClassifyOpenFailure(ErrorAccessDenied, rebindOffered: false);

        Assert.Equal("CaptureUnsupported", refused.ToString());
        Assert.Equal(CaptureReason.DriverHoldsDevice, refusedReason);
        Assert.False(refused.OffersRebind());
        Assert.False(refused.IsClaimable());
        Assert.False(refused.IsExportable());

        // Both refusals stay honest about what the operator can do about them.
        Assert.Contains("capture.rebind.enabled offers the P2 rebind path", offeredDetail, StringComparison.Ordinal);
        Assert.Contains("set capture.rebind.enabled", refusedDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyOpenFailure_NeverOffersRebindForADeviceWithNoInterface()
    {
        (CaptureState state, CaptureReason reason, _) =
            UsbDeviceEnumerator.ClassifyOpenFailure(ErrorFileNotFound, rebindOffered: true);

        Assert.Equal(CaptureState.CaptureUnsupported, state);
        Assert.Equal(CaptureReason.NoWinUsbInterface, reason);
        Assert.False(state.IsClaimable());
    }

    // =====================================================================================
    // The provider: the P2 attempt is reached
    // =====================================================================================

    [Fact]
    public async Task DriverHeldDevice_WithRebindEnabled_ReachesTheP2RebindAttempt()
    {
        var rebinder = new RecordingRebinder(canRebind: true);
        WinUsbCaptureProvider provider = Provider(
            DriverHeldDevice(rebindOffered: true), Options(rebind: true), rebinder, out AcceptanceRig.CapturingLogger log);

        UsbDevice device = Single(await provider.EnumerateAsync(CancellationToken.None));
        Assert.Equal(CaptureState.RebindOffered, device.CaptureState);

        await provider.OpenAsync(device, CancellationToken.None);

        // The defect: this list was empty, because the provider returned on IsExportable()
        // before the P2 block. Asserting the attempt - not a flag - is what keeps it fixed.
        (ushort vendorId, ushort productId) = Assert.Single(rebinder.Attempts);
        Assert.Equal(Vid, vendorId);
        Assert.Equal(Pid, productId);
        Assert.Contains(
            log.Snapshot(),
            line => line.Contains("rebound onto the inbox winusb.sys", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DriverHeldDevice_WhoseRebindFails_ReportsTheFailureAndItsReason()
    {
        var rebinder = new RecordingRebinder(canRebind: true, RebindResult.Failure("SetupCopyOEMInf failed (win32 740)", 740));
        WinUsbCaptureProvider provider = Provider(
            DriverHeldDevice(rebindOffered: true), Options(rebind: true), rebinder, out AcceptanceRig.CapturingLogger log);

        CaptureOpenResult open = await provider.OpenAsync(
            Single(await provider.EnumerateAsync(CancellationToken.None)), CancellationToken.None);

        Assert.Single(rebinder.Attempts);
        Assert.False(open.Ok);
        Assert.Equal(CaptureReason.DriverHoldsDevice, open.Reason);
        Assert.Contains("SetupCopyOEMInf failed (win32 740)", open.Detail, StringComparison.Ordinal);
        Assert.Contains(
            log.Snapshot(),
            line => line.Contains("P2 rebind failed: SetupCopyOEMInf failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DriverHeldDevice_WithRebindEnabled_ButNotElevated_SaysSoInsteadOfRebinding()
    {
        var rebinder = new RecordingRebinder(canRebind: false);
        WinUsbCaptureProvider provider = Provider(
            DriverHeldDevice(rebindOffered: true), Options(rebind: true), rebinder, out AcceptanceRig.CapturingLogger log);

        CaptureOpenResult open = await provider.OpenAsync(
            Single(await provider.EnumerateAsync(CancellationToken.None)), CancellationToken.None);

        Assert.Empty(rebinder.Attempts);
        Assert.False(open.Ok);
        Assert.Contains(
            log.Snapshot(),
            line => line.Contains("not elevated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DriverHeldDevice_WithRebindWithdrawn_IsRefusedWithTheEnumeratorsReason()
    {
        // The state says a rebind was offered; the configuration no longer does. The refusal must
        // name that, not fall through to a silent P1 failure.
        var rebinder = new RecordingRebinder(canRebind: true);
        WinUsbCaptureProvider provider = Provider(
            DriverHeldDevice(rebindOffered: true),
            Options(rebind: false),
            rebinder,
            out _);

        CaptureOpenResult open = await provider.OpenAsync(
            Single(await provider.EnumerateAsync(CancellationToken.None)), CancellationToken.None);

        Assert.Empty(rebinder.Attempts);
        Assert.False(open.Ok);
        Assert.Equal(CaptureReason.DriverHoldsDevice, open.Reason);
        Assert.Contains("capture.rebind.enabled offers the P2 rebind path", open.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DriverHeldDevice_WithRebindDisabled_IsStillRefusedWithItsReason()
    {
        var rebinder = new RecordingRebinder(canRebind: true);
        WinUsbCaptureProvider provider = Provider(
            DriverHeldDevice(rebindOffered: false), Options(rebind: false), rebinder, out _);

        UsbDevice device = Single(await provider.EnumerateAsync(CancellationToken.None));
        Assert.Equal(CaptureState.CaptureUnsupported, device.CaptureState);

        CaptureOpenResult open = await provider.OpenAsync(device, CancellationToken.None);

        Assert.Empty(rebinder.Attempts);
        Assert.False(open.Ok);
        Assert.Equal(CaptureReason.DriverHoldsDevice, open.Reason);
        Assert.Contains("set capture.rebind.enabled", open.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceRefusedForANonRebindReason_NeverReachesTheRebindAttempt()
    {
        var rebinder = new RecordingRebinder(canRebind: true);
        WinUsbCaptureProvider provider = Provider(UnsafeDevice(), Options(rebind: true), rebinder, out _);

        CaptureOpenResult open = await provider.OpenAsync(
            Single(await provider.EnumerateAsync(CancellationToken.None)), CancellationToken.None);

        Assert.Empty(rebinder.Attempts);
        Assert.False(open.Ok);
        Assert.Equal(CaptureReason.SystemCritical, open.Reason);
        Assert.Contains("never allowed", open.Detail, StringComparison.Ordinal);
    }

    // =====================================================================================
    // The gates above the provider: policy and the export table
    // =====================================================================================

    [Fact]
    public void Policy_AllowsARebindOfferedDevice_SoTheClaimCanReachTheProvider()
    {
        var policy = new DevicePolicy();
        UsbDevice device = DriverHeldDevice(rebindOffered: true);

        PolicyVerdict verdict = policy.Evaluate(device);

        Assert.True(verdict.Allowed);
        Assert.Equal(CaptureState.RebindOffered, verdict.State);
        Assert.Equal(CaptureReason.DriverHoldsDevice, verdict.Reason);

        // The same device without the offer is hidden from clients, with its reason.
        PolicyVerdict refused = policy.Evaluate(DriverHeldDevice(rebindOffered: false));
        Assert.False(refused.Allowed);
        Assert.Equal(CaptureState.CaptureUnsupported, refused.State);
    }

    [Fact]
    public async Task Server_AdvertisesARebindOfferedDevice_AndClaimsItThroughP2()
    {
        var rebinder = new RecordingRebinder(canRebind: true);
        ServerOptions options = Options(rebind: true);
        var log = new AcceptanceRig.CapturingLogger();
        var provider = new WinUsbCaptureProvider(
            new FixedEnumerator(DriverHeldDevice(rebindOffered: true)), options, log, rebinder);
        var state = new VhtServerState(options, provider, log);

        int exported = await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Equal(1, exported);
        ExportEntry entry = Assert.Single(state.Exports.Snapshot());
        Assert.Equal(CaptureState.RebindOffered, entry.Device.CaptureState);
        Assert.Contains(
            log.Snapshot(),
            line => line.Contains("the P2 rebind runs when a client attaches", StringComparison.Ordinal));

        // What UsbIpSession does on OP_REQ_IMPORT.
        await state.Capture.OpenAsync(entry.Device, CancellationToken.None);

        Assert.Single(rebinder.Attempts);
    }

    private static UsbDevice Single(IReadOnlyList<UsbDevice> devices) => Assert.Single(devices);

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
    /// Records every rebind it is asked to perform, so a test can assert the P2 path was
    /// <em>entered</em> rather than only that the configuration was read.
    /// </summary>
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
    }
}
