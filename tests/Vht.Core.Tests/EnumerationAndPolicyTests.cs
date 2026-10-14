using Vht.Core.Capture;
using Vht.Core.Concurrency;
using Vht.Core.Model;
using Vht.Core.Policy;
using Vht.Core.Protocol;
using Vht.Core.Server;
using Vht.Core.Transport;

namespace Vht.Core.Tests;

/// <summary>
/// Enumeration, export-table and policy behaviour: what the server is willing to export, what
/// it refuses and why, and the policy hooks the reference product gates behind payment.
/// </summary>
public sealed class EnumerationAndPolicyTests
{
    // =====================================================================================
    // Enumeration output format
    // =====================================================================================

    [Fact]
    public void DescribeFoundMatchesTheReconLineForLine()
    {
        UsbDevice device = TestDevices.Create(3);

        Assert.Equal(
            "Found high speed device [1234:5678] \"Fake Device 3, VirtualThere\" at address 3",
            device.DescribeFound());
    }

    [Theory]
    [InlineData(UsbIp.SpeedLow, "low")]
    [InlineData(UsbIp.SpeedFull, "full")]
    [InlineData(UsbIp.SpeedHigh, "high")]
    [InlineData(UsbIp.SpeedSuper, "super")]
    [InlineData(UsbIp.SpeedSuperPlus, "superplus")]
    [InlineData(UsbIp.SpeedUnknown, "unknown")]
    public void DescribeFoundNamesTheSpeedTheWayTheReconDoes(uint speed, string expected)
    {
        UsbDevice device = TestDevices.Create(1);
        var withSpeed = new UsbDevice { Key = device.Key, Speed = speed, VendorId = 1, ProductId = 2 };

        Assert.Contains($"Found {expected} speed device", withSpeed.DescribeFound(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceWithNoProductStringStillDescribesItself()
    {
        var device = new UsbDevice { Key = DeviceKey.FromBusId("1-1"), VendorId = 0xabcd, ProductId = 0xef01 };

        Assert.Equal("abcd:ef01", device.DisplayName);
        Assert.Contains("[abcd:ef01]", device.DescribeFound(), StringComparison.Ordinal);
    }

    // =====================================================================================
    // RefreshExportsAsync
    // =====================================================================================

    [Fact]
    public async Task EveryEnumeratedDeviceIsExported()
    {
        UsbDevice[] devices = TestDevices.Range(50);
        (VhtServerState state, _, _) = TestServer.Create(devices);

        int exported = await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Equal(50, exported);
        Assert.Equal(50, state.Exports.Count);
        foreach (UsbDevice device in devices)
            Assert.True(state.Exports.Contains(device.Key), $"{device.BusId} was not exported");
    }

    [Fact]
    public async Task ADeviceThatIsNoLongerPresentIsUnexported()
    {
        UsbDevice[] all = TestDevices.Range(3);
        (VhtServerState state, FakeCaptureProvider capture, _) = TestServer.Create(all);

        await state.RefreshExportsAsync(CancellationToken.None);
        Assert.Equal(3, state.Exports.Count);

        capture.Devices = new[] { all[0], all[1] };
        int exported = await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Equal(2, exported);
        Assert.False(state.Exports.Contains(all[2].Key));
    }

    [Fact]
    public async Task ADeviceThatDisappearsIsForceDetachedFromItsClient()
    {
        UsbDevice[] all = TestDevices.Range(2);
        (VhtServerState state, FakeCaptureProvider capture, _) = TestServer.Create(all);
        await state.RefreshExportsAsync(CancellationToken.None);

        var record = new AttachRecord
        {
            Key = all[0].Key,
            ConnectionId = new ConnectionId(1),
            ClientUuid = "uuid-1",
            RemoteAddress = "127.0.0.1:5000",
        };
        Assert.Equal(BindStatus.Success, state.Attachments.TryAttach(all[0].Key, record));

        capture.Devices = new[] { all[1] };
        await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Empty(state.Attachments.Snapshot());
        Assert.True(record.Detach.IsCancellationRequested, "the holder's detach token must be cancelled");
    }

    [Fact]
    public async Task TheExportGenerationAdvancesOnEveryRefresh()
    {
        (VhtServerState state, _, _) = TestServer.Create(TestDevices.Range(1));

        long first = state.ExportGeneration;
        await state.RefreshExportsAsync(CancellationToken.None);

        Assert.True(state.ExportGeneration > first);
    }

    [Fact]
    public async Task AnUnexportableDeviceStaysVisibleWithItsRefusalReason()
    {
        UsbDevice blocked = TestDevices.Create(1, state: CaptureState.CaptureUnsupported,
            reason: CaptureReason.NoWinUsbInterface);
        (VhtServerState state, _, _) = TestServer.Create(new[] { blocked });

        int exported = await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Equal(0, exported);
        Assert.False(state.Exports.Contains(blocked.Key));
    }

    // =====================================================================================
    // Hotplug
    // =====================================================================================

    [Fact]
    public async Task HotplugArrivalAndRemovalUpdateTheExportTableWithoutARescan()
    {
        (VhtServerState state, _, _) = TestServer.Create(Array.Empty<UsbDevice>());
        await state.RefreshExportsAsync(CancellationToken.None);
        Assert.Empty(state.Exports.Snapshot());

        UsbDevice device = TestDevices.Create(9);
        state.ApplyHotplugChange(device, present: true);
        Assert.True(state.Exports.Contains(device.Key));

        state.ApplyHotplugChange(device, present: false);
        Assert.False(state.Exports.Contains(device.Key));
    }

    [Fact]
    public void HotplugRemovalAlsoDetachesTheHolder()
    {
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device });
        state.ApplyHotplugChange(device, present: true);

        var record = new AttachRecord
        {
            Key = device.Key,
            ConnectionId = new ConnectionId(4),
            ClientUuid = "uuid-4",
            RemoteAddress = "127.0.0.1:6000",
        };
        state.Attachments.TryAttach(device.Key, record);

        state.ApplyHotplugChange(device, present: false);

        Assert.Empty(state.Attachments.Snapshot());
        Assert.True(record.Detach.IsCancellationRequested);
    }

    [Fact]
    public async Task TheExportCountChangedEventFiresOncePerRefresh()
    {
        (VhtServerState state, _, _) = TestServer.Create(TestDevices.Range(4));
        int notifications = 0;
        state.ExportCountChanged += _ => notifications++;

        await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Equal(1, notifications);
    }

    // =====================================================================================
    // Policy: ignore / allow — the hooks the reference product gates behind payment
    // =====================================================================================

    [Fact]
    public void IgnoreHidesADeviceAndIsUnconditional()
    {
        UsbDevice device = TestDevices.Create(1);
        var policy = new DevicePolicy { ServerName = "test" };
        policy.Ignore(new DeviceRule { VendorId = 0x1234, ProductId = 0x5678 });

        PolicyVerdict verdict = policy.Evaluate(device);

        Assert.False(verdict.Allowed);
        Assert.True(verdict.Ignored);
        Assert.Equal(CaptureState.Ignored, verdict.State);
        Assert.Contains("ignored by policy", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIgnoredDeviceIsNotExported()
    {
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device });

        state.Policy.Ignore(new DeviceRule { VendorId = 0x1234, ProductId = 0x5678 });
        int exported = await state.RefreshExportsAsync(CancellationToken.None);

        Assert.Equal(0, exported);
        Assert.False(state.Exports.Contains(device.Key));
    }

    [Fact]
    public void UnignoreRestoresTheDevice()
    {
        UsbDevice device = TestDevices.Create(1);
        var policy = new DevicePolicy();
        var rule = new DeviceRule { VendorId = 0x1234, ProductId = 0x5678 };

        policy.Ignore(rule);
        Assert.False(policy.Evaluate(device).Allowed);

        Assert.True(policy.Unignore(rule));
        Assert.True(policy.Evaluate(device).Allowed);
    }

    [Fact]
    public void AnAllowListHidesEveryDeviceItDoesNotName()
    {
        UsbDevice listed = TestDevices.Create(1, vendorId: 0x1111, productId: 0x2222);
        UsbDevice other = TestDevices.Create(2, vendorId: 0x3333, productId: 0x4444);
        var policy = new DevicePolicy();
        policy.Allow(new DeviceRule { VendorId = 0x1111, ProductId = 0x2222 });

        Assert.True(policy.Evaluate(listed).Allowed);
        Assert.False(policy.Evaluate(other).Allowed);
        Assert.Contains("not in the allow list", policy.Evaluate(other).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyAllowListAllowsEverythingNotIgnored()
    {
        var policy = new DevicePolicy();
        Assert.True(policy.Evaluate(TestDevices.Create(1)).Allowed);
    }

    [Fact]
    public void ASerialQualifiedRuleOnlyMatchesThatSerial()
    {
        var policy = new DevicePolicy();
        policy.Ignore(new DeviceRule { VendorId = 0x1234, ProductId = 0x5678, Serial = "SN0001" });

        Assert.True(policy.IsIgnored(new DeviceIdentity(0x1234, 0x5678, "SN0001")));
        Assert.False(policy.IsIgnored(new DeviceIdentity(0x1234, 0x5678, "SN0002")));
    }

    [Fact]
    public void ApplyCarriesTheNicknameAndThePasswordStateOntoTheDevice()
    {
        UsbDevice device = TestDevices.Create(1);
        var policy = new DevicePolicy();
        policy.SetNickname(device.Identity, "My Widget");
        policy.SetPasswordVerifier(device.Identity, "pbkdf2$abc");

        UsbDevice applied = policy.Apply(device);

        Assert.Equal("My Widget", applied.Nickname);
        Assert.Equal("My Widget", applied.DisplayName);
        Assert.True(applied.RequiresPassword);
        Assert.True(policy.RequiresPassword(device.Identity));
    }

    [Fact]
    public void AnIgnoredDeviceKeepsTheReasonItWasRefused()
    {
        var policy = new DevicePolicy();
        policy.Ignore(new DeviceRule { VendorId = 0x1234, ProductId = 0x5678 });

        UsbDevice applied = policy.Apply(TestDevices.Create(1));

        Assert.Equal(CaptureState.Ignored, applied.CaptureState);
        Assert.False(applied.CaptureState.IsExportable());
    }

    // =====================================================================================
    // Device rules
    // =====================================================================================

    [Theory]
    [InlineData("1234:5678", 0x1234, 0x5678, null, null)]
    [InlineData("0x1234:0x5678", 0x1234, 0x5678, null, null)]
    [InlineData("1234:5678:SN0001", 0x1234, 0x5678, "SN0001", null)]
    [InlineData("1234:5678@serverA", 0x1234, 0x5678, null, "serverA")]
    [InlineData("1234:5678:SN0001@serverA", 0x1234, 0x5678, "SN0001", "serverA")]
    [InlineData(" 1234 : 5678 ", 0x1234, 0x5678, null, null)]
    public void DeviceRuleParsesTheReconVocabulary(
        string text, int vid, int pid, string? serial, string? server)
    {
        Assert.True(DeviceRule.TryParse(text, out DeviceRule rule));
        Assert.Equal((ushort)vid, rule.VendorId);
        Assert.Equal((ushort)pid, rule.ProductId);
        Assert.Equal(serial, rule.Serial);
        Assert.Equal(server, rule.ServerName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234")]
    [InlineData("zzzz:5678")]
    [InlineData("1234:zzzz")]
    [InlineData("12345:5678")]
    public void DeviceRuleRejectsMalformedText(string text)
    {
        Assert.False(DeviceRule.TryParse(text, out _));
    }

    [Fact]
    public void AVidPidWideRuleMatchesEverySerial()
    {
        Assert.True(DeviceRule.TryParse("1234:5678", out DeviceRule rule));
        Assert.True(rule.IsVidPidWide);
        Assert.True(rule.Matches(new DeviceIdentity(0x1234, 0x5678, "anything"), null));
    }

    [Fact]
    public void AServerQualifiedRuleOnlyMatchesThatServer()
    {
        Assert.True(DeviceRule.TryParse("1234:5678@serverA", out DeviceRule rule));

        Assert.True(rule.Matches(new DeviceIdentity(0x1234, 0x5678, ""), "serverA"));
        Assert.False(rule.Matches(new DeviceIdentity(0x1234, 0x5678, ""), "serverB"));
        Assert.False(rule.Matches(new DeviceIdentity(0x1234, 0x5678, ""), null));
    }

    [Fact]
    public void ServerOptionsBuildAPolicyFromTheConfiguredRuleStrings()
    {
        var options = new ServerOptions { ServerName = "srv" };
        options.IgnoreList.Add("1234:5678");
        options.AllowList.Add("dead:beef");

        DevicePolicy policy = options.BuildPolicy();

        Assert.Single(policy.IgnoreList);
        Assert.Single(policy.AllowList);
        Assert.True(policy.IsIgnored(new DeviceIdentity(0x1234, 0x5678, "x")));
    }

    [Fact]
    public void ServerOptionsValidationReportsEveryBadRule()
    {
        var options = new ServerOptions();
        options.IgnoreList.Add("not-a-rule");

        IReadOnlyList<string> problems = options.Validate();

        Assert.Contains(problems, p => p.Contains("not-a-rule", StringComparison.Ordinal));
    }

    [Fact]
    public void ServerOptionsValidationAcceptsTheDefaultConfiguration()
    {
        var options = new ServerOptions { ServerName = "srv" };
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("winusb-only")]
    [InlineData("prebound-only")]
    public void ServerOptionsValidationAcceptsEveryDocumentedCaptureMode(string mode)
    {
        var options = new ServerOptions { ServerName = "srv", CaptureMode = mode };
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ServerOptionsValidationRejectsAnUnknownCaptureMode()
    {
        var options = new ServerOptions { ServerName = "srv", CaptureMode = "magic" };
        Assert.Contains(options.Validate(), p => p.Contains("capture.mode", StringComparison.Ordinal));
    }

    [Fact]
    public void ServerOptionsValidationRejectsTwoListenersOnOnePort()
    {
        var options = new ServerOptions
        {
            ServerName = "srv",
            PlaintextPort = 3240,
            TlsListenerEnabled = true,
            TlsPort = 3240,
        };

        Assert.Contains(options.Validate(), p => p.Contains("must differ", StringComparison.Ordinal));
    }

    [Fact]
    public void ListenersFollowTheEnabledFlags()
    {
        var options = new ServerOptions { PlaintextPort = 3240, TlsListenerEnabled = true, TlsPort = 3241 };

        IReadOnlyList<(TransportKind Kind, string Address, int Port)> listeners = options.Listeners();

        Assert.Equal(2, listeners.Count);
        Assert.Equal(TransportKind.PlainTcp, listeners[0].Kind);
        Assert.Equal(3240, listeners[0].Port);
        Assert.Equal(TransportKind.Tls, listeners[1].Kind);
        Assert.Equal(3241, listeners[1].Port);
    }

    // =====================================================================================
    // The headline: the export table has no capacity
    // =====================================================================================

    [Fact]
    public void TheExportTableAcceptsThousandsOfDevices()
    {
        var table = new ExportTable();

        for (int i = 0; i < 5000; i++)
            table.AddOrUpdate(TestDevices.Create(i), i);

        Assert.Equal(5000, table.Count);
    }

    [Fact]
    public void TheAttachTableAcceptsThousandsOfSimultaneousAttachments()
    {
        var table = new AttachTable();

        for (int i = 0; i < 5000; i++)
        {
            var record = new AttachRecord
            {
                Key = DeviceKey.FromBusId($"1-{i}"),
                ConnectionId = new ConnectionId((ushort)(i % 60000 + 1)),
                ClientUuid = $"uuid-{i}",
                RemoteAddress = "127.0.0.1:5000",
            };

            Assert.Equal(BindStatus.Success, table.TryAttach(record.Key, record));
        }

        Assert.Equal(5000, table.Count);
    }

    [Fact]
    public void TheOnlyRefusalAnAttachCanProduceIsExclusivity()
    {
        var table = new AttachTable();
        DeviceKey key = DeviceKey.FromBusId("1-1");

        var first = new AttachRecord
        {
            Key = key,
            ConnectionId = new ConnectionId(1),
            ClientUuid = "uuid-1",
            RemoteAddress = "127.0.0.1:5000",
        };
        Assert.Equal(BindStatus.Success, table.TryAttach(key, first));

        // A second connection asking for the same device is refused for exclusivity...
        var second = new AttachRecord
        {
            Key = key,
            ConnectionId = new ConnectionId(2),
            ClientUuid = "uuid-2",
            RemoteAddress = "127.0.0.1:5001",
        };
        Assert.Equal(BindStatus.DeviceBusy, table.TryAttach(key, second));

        // ...a re-attach on the same connection is idempotent...
        Assert.Equal(BindStatus.Success, table.TryAttach(key, first));

        // ...and a different device is never refused, no matter how many are held.
        for (int i = 2; i < 500; i++)
        {
            var other = new AttachRecord
            {
                Key = DeviceKey.FromBusId($"1-{i}"),
                ConnectionId = new ConnectionId(2),
                ClientUuid = "uuid-2",
                RemoteAddress = "127.0.0.1:5001",
            };
            Assert.Equal(BindStatus.Success, table.TryAttach(other.Key, other));
        }
    }

    [Fact]
    public void TheOnlyDeviceSideRefusalInTheConnectionGuardIsAQueueDepth()
    {
        var resources = new ConnectionResources(maxInFlightUrbs: 4, maxTransferBuffer: 1024);

        Assert.Equal(BindStatus.Success, resources.Check(0, 1024));
        Assert.Equal(BindStatus.Success, resources.Check(3, 1024));
        Assert.Equal(BindStatus.ClientLimit, resources.Check(4, 1024));
        Assert.Equal(BindStatus.ClientLimit, resources.Check(0, 1025));

        // The guard takes no device count at all: the same in-flight depth is judged
        // identically no matter how many devices this connection holds.
        Assert.Equal(BindStatus.Success, resources.Check(3, 512));
    }

    // =====================================================================================
    // Kick
    // =====================================================================================

    [Fact]
    public void KickForceDetachesWhoeverHoldsTheDevice()
    {
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device });

        var record = new AttachRecord
        {
            Key = device.Key,
            ConnectionId = new ConnectionId(3),
            ClientUuid = "uuid-3",
            RemoteAddress = "127.0.0.1:7000",
        };
        state.Attachments.TryAttach(device.Key, record);

        bool kicked = state.KickDevice(device.Key, out AttachRecord? kickedRecord, out string reason);

        Assert.True(kicked);
        Assert.Empty(reason);
        Assert.NotNull(kickedRecord);
        Assert.Equal(new ConnectionId(3), kickedRecord.ConnectionId);
        Assert.True(record.Detach.IsCancellationRequested);
        Assert.Empty(state.Attachments.Snapshot());
    }

    [Fact]
    public void KickOnAnUnattachedDeviceReportsWhyItDidNothing()
    {
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device });

        bool kicked = state.KickDevice(device.Key, out AttachRecord? record, out string reason);

        Assert.False(kicked);
        Assert.Null(record);
        Assert.Contains("not currently attached", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void KickIsNotGatedByPolicyOrByDeviceCount()
    {
        UsbDevice device = TestDevices.Create(1);
        (VhtServerState state, _, _) = TestServer.Create(new[] { device });

        // Even an ignored device can be kicked: the two hooks are independent, and neither
        // consults any licence state (there is none to consult).
        state.Policy.Ignore(new DeviceRule { VendorId = device.VendorId, ProductId = device.ProductId });

        var record = new AttachRecord
        {
            Key = device.Key,
            ConnectionId = new ConnectionId(1),
            ClientUuid = "uuid-1",
            RemoteAddress = "127.0.0.1:5000",
        };
        state.Attachments.TryAttach(device.Key, record);

        Assert.True(state.KickDevice(device.Key, out _, out _));
    }

    // =====================================================================================
    // Advertisement rules
    // =====================================================================================

    [Fact]
    public void APasswordProtectedDeviceIsNeverAdvertisedOnThePlaintextListener()
    {
        UsbDevice open = TestDevices.Create(1);
        UsbDevice locked = TestDevices.Create(2, requiresPassword: true);

        var table = new ExportTable();
        table.AddOrUpdate(open, 1);
        table.AddOrUpdate(locked, 1);

        IReadOnlyList<ExportEntry> plaintext = table.Advertisable(TransportKind.PlainTcp);
        IReadOnlyList<ExportEntry> tls = table.Advertisable(TransportKind.Tls);

        Assert.Single(plaintext);
        Assert.Equal(open.Key, plaintext[0].Device.Key);
        Assert.Equal(2, tls.Count);
    }

    [Fact]
    public void EveryDeviceIsAdvertisedOnTlsRegardlessOfCount()
    {
        var table = new ExportTable();
        for (int i = 0; i < 300; i++)
            table.AddOrUpdate(TestDevices.Create(i, requiresPassword: i % 2 == 0), 1);

        Assert.Equal(300, table.Advertisable(TransportKind.Tls).Count);
        Assert.Equal(150, table.Advertisable(TransportKind.PlainTcp).Count);
    }

    [Fact]
    public void RecordBindCountsBindsOnTheExportedDevice()
    {
        var table = new ExportTable();
        UsbDevice device = TestDevices.Create(1);
        table.AddOrUpdate(device, 1);

        table.RecordBind(device.Key);
        table.RecordBind(device.Key);

        Assert.True(table.TryGet(device.Key, out ExportEntry entry));
        Assert.Equal(2, entry.BindCount);
    }
}
