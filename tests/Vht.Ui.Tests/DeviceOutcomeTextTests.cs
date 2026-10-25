using Vht.Ui.Model;

namespace Vht.Ui.Tests;

/// <summary>
/// The window's per-device text: what the operator reads on a row about the last capture/rebind
/// attempt, and the requirement that a device that could not be exported still says why.
/// </summary>
/// <remarks>
/// These are the strings the server window binds to (the second line of the Status cell and its
/// tooltip). The vocabulary is the server's - the same three rebind outcomes the runtime log
/// distinguishes - so the window and the log cannot tell two different stories about one attempt.
/// </remarks>
public sealed class DeviceOutcomeTextTests
{
    [Theory]
    [InlineData("driver_holds_device", CaptureReason.DriverHoldsDevice)]
    [InlineData("system_critical", CaptureReason.SystemCritical)]
    [InlineData("no_winusb_interface", CaptureReason.NoWinUsbInterface)]
    public void Machine_readable_capture_reasons_parse(string value, CaptureReason expected)
    {
        Assert.Equal(expected, CaptureText.ParseReason(value));
    }

    private static DeviceSnapshot Device(
        string? outcome = null, string? path = null, bool exported = true, CaptureState state = CaptureState.RebindOffered) =>
        new()
        {
            Key = "1-12",
            BusId = "1-12",
            VendorId = 0x0531,
            ProductId = 0x0101,
            Product = "Intuos Draw",
            Vendor = "Wacom",
            CaptureState = state,
            CaptureReason = CaptureReason.DriverHoldsDevice,
            CaptureDetail = "the device's function driver holds it exclusively",
            CapturePath = path,
            CaptureOutcome = outcome,
            Exported = exported,
        };

    [Fact]
    public void NoAttemptYet_LeavesTheOutcomeLineEmpty_AndTheTooltipIsTheReasonAlone()
    {
        DeviceSnapshot device = Device();

        Assert.Equal(string.Empty, device.OutcomeText);
        Assert.Equal("the device's function driver holds it exclusively", device.DetailText);
    }

    [Fact]
    public void NotElevated_IsShownOnTheRow_WithThePathThatCouldNotRun()
    {
        const string outcome =
            "capture.rebind.enabled is set but this process is not elevated, so the P2 rebind path cannot run";
        DeviceSnapshot device = Device(outcome, "P2");

        Assert.Equal(outcome, device.OutcomeText);
        Assert.Contains("not elevated", device.OutcomeText, StringComparison.Ordinal);

        // The tooltip says which path ran, so "not elevated" is not mistaken for the P1 attempt.
        Assert.Contains("Last capture (P2):", device.DetailText, StringComparison.Ordinal);
        Assert.Contains(outcome, device.DetailText, StringComparison.Ordinal);
        Assert.Contains("the device's function driver holds it exclusively", device.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void RebindFailed_IsShownOnTheRow_WithItsDetail()
    {
        const string outcome = "P2 rebind failed: SetupCopyOEMInf failed (win32 740)";
        DeviceSnapshot device = Device(outcome, "P2");

        Assert.Equal(outcome, device.OutcomeText);
        Assert.Contains("win32 740", device.OutcomeText, StringComparison.Ordinal);
    }

    [Fact]
    public void RebootRequired_IsShownOnTheRow_AndTheRowIsStillNotExportable()
    {
        DeviceSnapshot device = Device(
            "rebound onto the inbox winusb.sys (oem42.inf); a reboot is required before the binding is effective",
            "P2");

        Assert.Contains("a reboot is required", device.OutcomeText, StringComparison.Ordinal);
        Assert.False(device.Exportable);
    }

    [Fact]
    public void ADeviceTheServerWillNotExport_IsStillARow_AndKeepsItsReason()
    {
        DeviceSnapshot device = Device(
            outcome: null,
            exported: false,
            state: CaptureState.UnsafeToExport);

        Assert.False(device.Exported);

        // The row's status line is the reason, so the device is not "missing": it is listed and it
        // says why it is not offered.
        Assert.False(string.IsNullOrWhiteSpace(device.StatusText));
        Assert.False(string.IsNullOrWhiteSpace(device.DetailText));
    }
}
