using Vht.Core.Capture;
using Vht.Core.Model;
using Vht.Core.Policy;
using Vht.Core.Protocol;

namespace Vht.Acceptance.Tests;

/// <summary>
/// The parity suite: every behaviour the reference product's free tier ships (or gates) is driven
/// against the production server stack over a real loopback TCP connection.
/// </summary>
/// <remarks>
/// These are integration tests, not unit tests: each one boots <c>VhtServerState</c> plus the real
/// <c>UsbIpTcpServer</c> accept loop and speaks USB/IP to it. See
/// <c>tests/Vht.Acceptance</c> for why the device source is synthetic on this host
/// (<c>artifacts/acceptance/hardware.txt</c>: 0 of 13 real devices are WinUSB-claimable).
/// </remarks>
public sealed class ParityTests
{
    private static readonly SyntheticDeviceOptions Options = new() { Compressible = false };

    private static IReadOnlyList<UsbDevice> Devices(int count) =>
        Enumerable.Range(0, count)
            .Select(i => SyntheticDevices.Build(SyntheticDevices.All[i % SyntheticDevices.All.Count], i))
            .ToArray();

    private static async Task<AcceptanceRig> StartAsync(
        IReadOnlyList<UsbDevice>? devices = null, DevicePolicy? policy = null) =>
        await AcceptanceRig.StartSyntheticAsync(
            devices ?? Devices(3), Options, Vht.Transport.TransportKind.PlainTcp, policy, CancellationToken.None);

    private static Task<UsbIpClient> ConnectAsync(AcceptanceRig rig) =>
        UsbIpClient.ConnectAsync(
            "127.0.0.1", rig.Port, AcceptanceRig.ClientTransportOptions(Vht.Transport.TransportKind.PlainTcp),
            CancellationToken.None);

    // -------------------------------------------------------------------------------------
    // Device enumeration
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task Enumeration_Advertises_Every_Capturable_Device_With_Its_Descriptors()
    {
        await using AcceptanceRig rig = await StartAsync();
        await using UsbIpClient client = await ConnectAsync(rig);

        IReadOnlyList<UsbIpDevice> listed = await client.ListDevicesAsync(CancellationToken.None);

        Assert.Equal(3, listed.Count);
        UsbIpDevice first = listed.Single(d => d.BusId == "1-1");
        Assert.Equal(0x0781, first.VendorId);
        Assert.Equal(0x5581, first.ProductId);
        Assert.Equal(UsbIp.SpeedHigh, first.Speed);
        Assert.NotEmpty(first.Product);
    }

    [Fact]
    public async Task Enumeration_Reports_No_Device_Limit()
    {
        // 32 concurrent devices: the reference product's free tier stops at 1, and its paid tier at
        // the licence's max_devices. Nothing between the wire and the export table compares a count.
        await using AcceptanceRig rig = await StartAsync(Devices(32));
        await using UsbIpClient client = await ConnectAsync(rig);

        IReadOnlyList<UsbIpDevice> listed = await client.ListDevicesAsync(CancellationToken.None);

        Assert.Equal(32, listed.Count);
        Assert.Equal(32, rig.State.Exports.Count);
    }

    // -------------------------------------------------------------------------------------
    // Export / attach / detach
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task Attach_Then_Use_Round_Trips_A_Bulk_Transfer()
    {
        await using AcceptanceRig rig = await StartAsync();
        await using UsbIpClient client = await ConnectAsync(rig);

        ImportOutcome import = await client.ImportAsync("1-1", CancellationToken.None);
        Assert.True(import.Ok, import.StatusName);
        Assert.Equal(1, rig.State.Attachments.Count);

        var payload = new byte[1024];
        Random.Shared.NextBytes(payload);

        UrbCompletion outUrb = await client.SubmitAsync(import.DevId, 0x22, payload, 0, 0, CancellationToken.None);
        Assert.True(outUrb.Ok);
        Assert.Equal(1024u, outUrb.ActualLength);

        UrbCompletion inUrb = await client.SubmitAsync(import.DevId, 0xA1, new byte[1024], 0, 0, CancellationToken.None);
        Assert.True(inUrb.Ok);
        Assert.Equal(1024u, inUrb.ActualLength);
        Assert.Equal(1024, inUrb.Data.Length);
    }

    [Fact]
    public async Task Detach_On_Connection_Close_Frees_The_Device_For_Another_Client()
    {
        await using AcceptanceRig rig = await StartAsync();

        UsbIpClient first = await ConnectAsync(rig);
        Assert.True((await first.ImportAsync("1-1", CancellationToken.None)).Ok);
        Assert.Equal(1, rig.State.Attachments.Count);

        await first.DisposeAsync();
        await WaitUntilAsync(() => rig.State.Attachments.Count == 0);

        await using UsbIpClient second = await ConnectAsync(rig);
        ImportOutcome again = await second.ImportAsync("1-1", CancellationToken.None);
        Assert.True(again.Ok, "an explicit detach did not release the device");
    }

    [Fact]
    public async Task A_Second_Holder_Is_Refused_By_Exclusivity_Not_By_A_Device_Limit()
    {
        await using AcceptanceRig rig = await StartAsync();

        await using UsbIpClient a = await ConnectAsync(rig);
        Assert.True((await a.ImportAsync("1-1", CancellationToken.None)).Ok);

        await using UsbIpClient b = await ConnectAsync(rig);
        ImportOutcome refused = await b.ImportAsync("1-1", CancellationToken.None);

        Assert.False(refused.Ok);
        Assert.Equal(1u, refused.Status); // device_busy / error, never a count refusal

        // ...and the same connection can still take a *different* device, which is what makes this
        // exclusivity rather than a cap.
        Assert.True((await b.ImportAsync("1-2", CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Many_Devices_On_One_Connection_Are_All_Usable()
    {
        await using AcceptanceRig rig = await StartAsync(Devices(8));
        await using UsbIpClient client = await ConnectAsync(rig);

        var live = new List<uint>();
        foreach (UsbIpDevice device in await client.ListDevicesAsync(CancellationToken.None))
        {
            ImportOutcome outcome = await client.ImportAsync(device.BusId, CancellationToken.None);
            Assert.True(outcome.Ok, $"{device.BusId}: {outcome.StatusName}");
            live.Add(outcome.DevId);
        }

        Assert.Equal(8, live.Count);
        Assert.Equal(8, rig.State.Attachments.Count);

        foreach (uint devId in live)
        {
            UrbCompletion control = await client.ControlInAsync(devId, 0x06, 0x0100, 0, 18, CancellationToken.None);
            Assert.True(control.Ok);
            Assert.Equal(18u, control.ActualLength);
        }
    }

    // -------------------------------------------------------------------------------------
    // Hotplug and reconnect
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task Hotplug_Added_And_Removed_Devices_Change_What_Clients_See()
    {
        var provider = new MutableCaptureProvider(Array.Empty<UsbDevice>());

        await using AcceptanceRig rig = await AcceptanceRig.StartWithProviderAsync(
            provider, Vht.Transport.TransportKind.PlainTcp, policy: null, CancellationToken.None);

        Assert.Equal(0, rig.State.Exports.Count);

        // This is the path the server's periodic rescan calls: CM hotplug notifications return
        // CR 31 on this host, so the rescan is the live mechanism (see artifacts/acceptance/hardware.txt).
        provider.Add(SyntheticDevices.Build(SyntheticDevices.All[0], 99));
        await rig.State.RefreshExportsAsync(CancellationToken.None);
        await using (UsbIpClient afterAdd = await ConnectAsync(rig))
            Assert.Single(await afterAdd.ListDevicesAsync(CancellationToken.None));

        provider.Remove("1-100");
        await rig.State.RefreshExportsAsync(CancellationToken.None);
        await using (UsbIpClient afterRemove = await ConnectAsync(rig))
            Assert.Empty(await afterRemove.ListDevicesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reconnect_After_Transport_Death_Succeeds_And_Reimports()
    {
        await using AcceptanceRig rig = await StartAsync();

        UsbIpClient first = await ConnectAsync(rig);
        Assert.True((await first.ImportAsync("1-1", CancellationToken.None)).Ok);

        // Hard death of the connection, not a graceful detach.
        await first.DisposeAsync();
        await WaitUntilAsync(() => rig.State.Attachments.Count == 0);

        await using UsbIpClient second = await ConnectAsync(rig);
        ImportOutcome outcome = await second.ImportAsync("1-1", CancellationToken.None);
        Assert.True(outcome.Ok);

        UrbCompletion control = await second.ControlInAsync(outcome.DevId, 0x06, 0x0100, 0, 18, CancellationToken.None);
        Assert.True(control.Ok);
    }

    // -------------------------------------------------------------------------------------
    // Ignore / allow lists
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task Ignore_List_Hides_A_Device_Even_When_It_Is_Also_Allowed()
    {
        var policy = new DevicePolicy();
        Assert.True(DeviceRule.TryParse("0781:5581", out DeviceRule rule));
        policy.Allow(rule);
        policy.Ignore(rule);

        await using AcceptanceRig rig = await StartAsync(policy: policy);
        await using UsbIpClient client = await ConnectAsync(rig);

        IReadOnlyList<UsbIpDevice> listed = await client.ListDevicesAsync(CancellationToken.None);

        Assert.DoesNotContain(listed, d => d.BusId == "1-1");
        Assert.Equal(2, listed.Count);
        Assert.False((await client.ImportAsync("1-1", CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Allow_List_Exports_Exactly_The_Allowed_Devices()
    {
        var policy = new DevicePolicy();
        Assert.True(DeviceRule.TryParse("046d:c31c", out DeviceRule hid));
        policy.Allow(hid);

        await using AcceptanceRig rig = await StartAsync(policy: policy);
        await using UsbIpClient client = await ConnectAsync(rig);

        IReadOnlyList<UsbIpDevice> listed = await client.ListDevicesAsync(CancellationToken.None);

        Assert.Single(listed);
        Assert.Equal(0x046d, listed[0].VendorId);
    }

    // -------------------------------------------------------------------------------------
    // Nicknames
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task Nickname_Is_Retained_By_Policy_And_Reported_By_The_Operator_View()
    {
        var policy = new DevicePolicy();
        var identity = new DeviceIdentity(0x0781, 0x5581, "07815581-0000");
        policy.SetNickname(identity, "acceptance-stick");

        await using AcceptanceRig rig = await StartAsync(policy: policy);

        UsbDevice exported = rig.State.Exports.Snapshot().Single().Device;
        Assert.Equal("acceptance-stick", exported.Nickname);
        Assert.Equal("acceptance-stick", exported.DisplayName);
        Assert.Equal("acceptance-stick", policy.GetNickname(identity));
    }

    [Fact]
    public async Task Known_Gap_Nickname_Is_Not_Yet_Carried_On_The_USB_IP_Wire()
    {
        // CHARACTERISATION TEST for a real parity gap, filed as a follow-up card rather than
        // fixed here (this card's scope excludes fixes).
        //
        // The reference product shows the operator's nickname to the client as the device name.
        // VirtualThere keeps the nickname and shows it in its own views (previous test) but
        // UsbDevice.ToUsbIpDevice() sends the raw product string, so a stock USB/IP client still
        // sees "VirtualThere acceptance mass-storage #0".
        //
        // If this test starts failing, the gap has been closed: delete it and update
        // docs/ACCEPTANCE.md §Parity.
        var policy = new DevicePolicy();
        var identity = new DeviceIdentity(0x0781, 0x5581, "07815581-0000");
        policy.SetNickname(identity, "acceptance-stick");

        await using AcceptanceRig rig = await StartAsync(policy: policy);
        await using UsbIpClient client = await ConnectAsync(rig);

        IReadOnlyList<UsbIpDevice> listed = await client.ListDevicesAsync(CancellationToken.None);
        UsbIpDevice device = listed.Single(d => d.BusId == "1-1");

        Assert.Equal("VirtualThere acceptance mass-storage #0", device.Product);
    }

    // -------------------------------------------------------------------------------------
    // Per-device passwords
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Password_Protected_Device_Is_Not_Offered_On_The_Plaintext_Listener()
    {
        var policy = new DevicePolicy();
        var identity = new DeviceIdentity(0x0781, 0x5581, "07815581-0000");
        policy.SetPasswordVerifier(identity, "verifier-placeholder");

        await using AcceptanceRig rig = await StartAsync(policy: policy);
        await using UsbIpClient client = await ConnectAsync(rig);

        IReadOnlyList<UsbIpDevice> listed = await client.ListDevicesAsync(CancellationToken.None);
        Assert.DoesNotContain(listed, d => d.BusId == "1-1");

        // Even knowing the bus id, the import is refused rather than served unprotected.
        ImportOutcome refused = await client.ImportAsync("1-1", CancellationToken.None);
        Assert.False(refused.Ok);

        int unavailable = rig.State.Exports.Snapshot().Count(e => e.Device.RequiresPassword);
        Assert.Equal(1, unavailable);
    }

    [Fact]
    public async Task A_Device_With_No_Password_Is_Still_Offered_Next_To_A_Protected_One()
    {
        var policy = new DevicePolicy();
        policy.SetPasswordVerifier(new DeviceIdentity(0x0781, 0x5581, "07815581-0000"), "verifier-placeholder");

        await using AcceptanceRig rig = await StartAsync(policy: policy);
        await using UsbIpClient client = await ConnectAsync(rig);

        IReadOnlyList<UsbIpDevice> listed = await client.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(2, listed.Count);
    }

    // -------------------------------------------------------------------------------------
    // Kick
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task Kick_Force_Detaches_The_Holder_And_Frees_The_Device()
    {
        await using AcceptanceRig rig = await StartAsync();

        await using UsbIpClient holder = await ConnectAsync(rig);
        Assert.True((await holder.ImportAsync("1-1", CancellationToken.None)).Ok);

        bool kicked = rig.State.Attachments.Kick(DeviceKey.FromBusId("1-1"), out _);

        Assert.True(kicked);
        Assert.Equal(0, rig.State.Attachments.Count);

        await using UsbIpClient other = await ConnectAsync(rig);
        Assert.True((await other.ImportAsync("1-1", CancellationToken.None)).Ok);
    }

    // -------------------------------------------------------------------------------------
    // Transports
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Vht.Transport.TransportKind.Tls)]
    [InlineData(Vht.Transport.TransportKind.TlsLz4)]
    public async Task The_Encrypted_Listeners_Carry_The_Same_Parity_Surface(Vht.Transport.TransportKind kind)
    {
        await using AcceptanceRig rig = await AcceptanceRig.StartSyntheticAsync(
            Devices(2), Options, kind, policy: null, CancellationToken.None);

        await using UsbIpClient client = await UsbIpClient.ConnectAsync(
            "127.0.0.1", rig.Port, AcceptanceRig.ClientTransportOptions(kind), CancellationToken.None);

        Assert.Equal(2, (await client.ListDevicesAsync(CancellationToken.None)).Count);

        ImportOutcome outcome = await client.ImportAsync("1-1", CancellationToken.None);
        Assert.True(outcome.Ok);

        UrbCompletion control = await client.ControlInAsync(outcome.DevId, 0x06, 0x0100, 0, 18, CancellationToken.None);
        Assert.True(control.Ok);
    }

    // -------------------------------------------------------------------------------------

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("condition was not reached within 5s");
    }

    /// <summary>An <see cref="ICaptureProvider"/> whose device list the test changes at runtime.</summary>
    private sealed class MutableCaptureProvider : ICaptureProvider
    {
        private readonly List<UsbDevice> _devices;

        public MutableCaptureProvider(IEnumerable<UsbDevice> devices) => _devices = devices.ToList();

        public void Add(UsbDevice device) => _devices.Add(device);

        public void Remove(string busId) => _devices.RemoveAll(d => d.BusId == busId);

        public ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<UsbDevice>>(_devices.ToArray());

        public ValueTask<CaptureOpenResult> OpenAsync(UsbDevice device, CancellationToken ct) =>
            ValueTask.FromResult(CaptureOpenResult.Success(new SyntheticCapture(device, Options)));

        public ValueTask ReleaseAsync(UsbDevice device, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
