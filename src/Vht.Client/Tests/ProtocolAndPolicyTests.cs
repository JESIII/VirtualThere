// SPDX-License-Identifier: Apache-2.0
//
// Wire-format and policy tests. These are the tests that catch a byte-offset mistake or a policy
// ordering mistake — the two classes of bug that are invisible in a running system until a device
// misbehaves in a way nobody can explain.

using System.Reflection;
using System.Text.RegularExpressions;
using Vht.Client.Attach;
using Vht.Client.Bridge;
using Vht.Client.Credentials;
using Vht.Client.Diagnostics;
using Vht.Client.Peers;
using Vht.Client.Policy;
using Vht.Client.Transport;
using Vht.Client.Usbip;
using Vht.Interop.Ude;

namespace Vht.Client.Tests;

public sealed class UsbipCodecTests
{
    [Fact]
    public void ImportRequest_MatchesTheProtocolLayout()
    {
        byte[] request = UsbipOpWriter.BuildImportRequest("1-2.3");

        Assert.Equal(UsbipProtocol.ImportRequestSize, request.Length);
        Assert.Equal(40, request.Length);

        // op_common: version 0x0111 and code 0x8003, both big-endian.
        Assert.Equal(0x01, request[0]);
        Assert.Equal(0x11, request[1]);
        Assert.Equal(0x80, request[2]);
        Assert.Equal(0x03, request[3]);
        Assert.Equal(0u, BigEndian.ReadU32(request.AsSpan(4)));

        // busid starts at offset 8 and is NUL-padded to 32 bytes.
        Assert.Equal("1-2.3", BigEndian.ReadCString(request.AsSpan(UsbipProtocol.ImportBusIdOffset, 32)));
        Assert.Equal(0, request[8 + "1-2.3".Length]);
    }

    [Fact]
    public void DeviceRecord_RoundTripsThroughWriterAndParser()
    {
        var device = new UsbipDeviceRecord
        {
            Path = "/sys/devices/pci0000:00/usb1/1-2",
            BusId = "1-2",
            BusNum = 1,
            DevNum = 4,
            Speed = UsbDeviceSpeed.Super,
            VendorId = 0x0bda,
            ProductId = 0x8153,
            BcdDevice = 0x3000,
            DeviceClass = 0x00,
            DeviceSubClass = 0x01,
            DeviceProtocol = 0x02,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            NumInterfaces = 2,
        };

        var buffer = new byte[UsbipProtocol.UsbipUsbDeviceSize];
        UsbipOpWriter.WriteDeviceRecord(buffer, device);
        UsbipDeviceRecord parsed = UsbipOp.ParseDeviceRecord(buffer);

        Assert.Equal(device.Path, parsed.Path);
        Assert.Equal(device.BusId, parsed.BusId);
        Assert.Equal(device.BusNum, parsed.BusNum);
        Assert.Equal(device.DevNum, parsed.DevNum);
        Assert.Equal(device.Speed, parsed.Speed);
        Assert.Equal(device.VendorId, parsed.VendorId);
        Assert.Equal(device.ProductId, parsed.ProductId);
        Assert.Equal(device.BcdDevice, parsed.BcdDevice);
        Assert.Equal(device.DeviceClass, parsed.DeviceClass);
        Assert.Equal(device.DeviceSubClass, parsed.DeviceSubClass);
        Assert.Equal(device.DeviceProtocol, parsed.DeviceProtocol);
        Assert.Equal(device.ConfigurationValue, parsed.ConfigurationValue);
        Assert.Equal(device.NumConfigurations, parsed.NumConfigurations);
        Assert.Equal(device.NumInterfaces, parsed.NumInterfaces);
    }

    [Fact]
    public void DevListReply_DeclaresItsLengthAndDeviceCount()
    {
        var device = new UsbipDeviceRecord { Path = "/dev/x", BusId = "3-1", NumInterfaces = 2 };
        byte[] frame = UsbipOpWriter.BuildDevListReply(new[] { (device, "3-1") });

        Assert.Equal(UsbipProtocol.DevListRequestSize + UsbipProtocol.UsbipUsbDeviceSize
                     + (2 * UsbipProtocol.UsbipUsbInterfaceSize), frame.Length);
        Assert.Equal(UsbipProtocol.OpRepDevList, BigEndian.ReadU16(frame.AsSpan(2)));
        Assert.Equal(1u, BigEndian.ReadU32(frame.AsSpan(8)));
        Assert.Equal("3-1", BigEndian.ReadCString(
            frame.AsSpan(UsbipProtocol.DevListRequestSize + UsbipProtocol.DevPathMax, UsbipProtocol.BusIdSize)));
    }

    [Fact]
    public async Task DevListReply_WithTheWrongVersion_IsRejected()
    {
        var transport = new ScriptedTransport(new byte[]
        {
            0x01, 0x00,                                // version 0x0100 (not 0x0111)
            0x00, 0x05,                                // OP_REP_DEVLIST
            0x00, 0x00, 0x00, 0x00,                    // status
            0x00, 0x00, 0x00, 0x00,                    // ndev
        });

        var ex = await Assert.ThrowsAsync<UsbipProtocolException>(
            () => UsbipOp.ListDevicesAsync(transport, CancellationToken.None));
        Assert.Contains("version", ex.Message);
    }

    [Fact]
    public async Task DevListReply_WithAReplyWhereARequestWasExpected_IsRejected()
    {
        var transport = new ScriptedTransport(new byte[]
        {
            0x01, 0x11,
            0x80, 0x05,                                // OP_REQ_DEVLIST — a request op-code in a reply slot
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        });

        var ex = await Assert.ThrowsAsync<UsbipProtocolException>(
            () => UsbipOp.ListDevicesAsync(transport, CancellationToken.None));
        Assert.Contains("op-code", ex.Message);
    }

    [Fact]
    public async Task DevListReply_WithAnAbsurdDeviceCount_IsRefusedRatherThanAllocated()
    {
        var transport = new ScriptedTransport(new byte[]
        {
            0x01, 0x11,                                // version
            0x00, 0x05,                                // OP_REP_DEVLIST
            0x00, 0x00, 0x00, 0x00,                    // status
            0xFF, 0xFF, 0xFF, 0xFF,                    // ndev = 4 294 967 295
        });

        var ex = await Assert.ThrowsAsync<UsbipProtocolException>(
            () => UsbipOp.ListDevicesAsync(transport, CancellationToken.None));
        Assert.Contains("parse bound", ex.Message);
    }
}

public sealed class UdeBufferCodecTests
{
    [Fact]
    public void PluginHardware_HasTheDocumentedOffsets()
    {
        byte[] buffer = UdeBufferCodec.BuildPluginHardware(
            host: "127.0.0.1",
            service: "3240",
            busId: "1-2.3",
            serial: "SN42",
            wskEvents: false);

        Assert.Equal(UdeBufferCodec.PluginHardwareSize, buffer.Length);
        Assert.Equal(1120, buffer.Length);

        // base.size is written little-endian (native), not big-endian: this is an ABI field, not a
        // USB/IP wire field, and the distinction is exactly what this test pins down.
        Assert.Equal(1120u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0)));
        Assert.Equal("1-2.3", ReadFixed(buffer, UdeBufferCodec.PluginBusIdOffset, 32));
        Assert.Equal("3240", ReadFixed(buffer, UdeBufferCodec.PluginServiceOffset, 32));
        Assert.Equal("127.0.0.1", ReadFixed(buffer, UdeBufferCodec.PluginHostOffset, 1025));
        Assert.Equal("SN42", ReadFixed(buffer, UdeBufferCodec.PluginSerialOffset, 16));
        Assert.Equal(0, buffer[UdeBufferCodec.PluginWskEventsOffset]);

        // The struct is a C++ multiple-inheritance struct: `imported_device_location` starts at 4.
        Assert.Equal(4, UdeBufferCodec.PluginPortOffset);
        Assert.Equal(8, UdeBufferCodec.PluginLocationHashOffset);
        Assert.Equal(12, UdeBufferCodec.PluginBusIdOffset);
        Assert.Equal(44, UdeBufferCodec.PluginServiceOffset);
        Assert.Equal(76, UdeBufferCodec.PluginHostOffset);
        Assert.Equal(1101, UdeBufferCodec.PluginSerialOffset);
        Assert.Equal(1117, UdeBufferCodec.PluginWskEventsOffset);
    }

    [Fact]
    public void PlugoutHardware_CarriesThePortAndSelfSize()
    {
        byte[] all = UdeBufferCodec.BuildPlugoutHardware(UdeConstants.PortAll);
        Assert.Equal(-1, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(4)));
        Assert.Equal(8u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(0)));
    }

    [Fact]
    public void IoctlCodes_AreDerivedFromTheCtlCodeMacro()
    {
        // CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, function, METHOD_BUFFERED, FILE_READ_DATA|FILE_WRITE_DATA)
        Assert.Equal(0x0022E000u, UdeConstants.IoctlPluginHardware);
        Assert.Equal(0x0022E004u, UdeConstants.IoctlPlugoutHardware);
        Assert.Equal(0x0022E008u, UdeConstants.IoctlGetImportedDevices);
        Assert.Equal(0x0022E014u, UdeConstants.IoctlStopAttachAttempts);
        Assert.Equal(0x0022E018u, UdeConstants.IoctlPluginHardwareOnce);
    }

    [Fact]
    public void ImportedDevices_AreParsedUnderEitherCandidateLayout()
    {
        foreach (ImportedDeviceLayout layout in ImportedDeviceLayout.Candidates)
        {
            byte[] buffer = BuildImportedDevicesBuffer(layout, (1, "1-2"), (2, "2-1"));
            (IReadOnlyList<UdeImportedDevice> devices, ImportedDeviceLayout detected) =
                UdeBufferCodec.ParseImportedDevices(buffer, 2);

            Assert.Equal(layout.Stride, detected.Stride);
            Assert.Equal(2, devices.Count);
            Assert.Equal(1, devices[0].Port);
            Assert.Equal("1-2", devices[0].BusId);
            Assert.Equal("127.0.0.1", devices[0].Host);
            Assert.Equal("3240", devices[0].Service);
            Assert.Equal(2, devices[1].Port);
            Assert.Equal("2-1", devices[1].BusId);
        }
    }

    [Fact]
    public void ImportedDevices_WithAnUnknownLayout_ThrowRatherThanReturnGarbage()
    {
        var buffer = new byte[4 + (3 * 1132) + 37];
        // Contents deliberately meaningless: no port in 1..1024 anywhere.
        Assert.Throws<UdeLayoutException>(() => UdeBufferCodec.ParseImportedDevices(buffer, 3));
    }

    [Fact]
    public void BufferSizes_DeriveFromTheLayout()
    {
        Assert.Equal(4 + (4 * 1132), UdeBufferCodec.GetImportedDevicesBufferSize(4, ImportedDeviceLayout.Natural));
        Assert.Equal(4 + (4 * 1127), UdeBufferCodec.GetImportedDevicesBufferSize(4, ImportedDeviceLayout.Packed));
    }

    /// <summary>
    /// The candidate layouts are hand-written offset tables, and `ParseImportedDevices` builds its
    /// array from them — so a slip in one offset silently shifts every element after the first. This
    /// pins each offset to the field widths the upstream header declares (vhci.h:
    /// `int port; ULONG location_hash; char busid[32]; char service[32]; char host[1025];` then
    /// `UINT32 devid; usb_device_speed speed; UINT16 vendor; UINT16 product; char serial[16];
    /// UCHAR iserial; bool wsk_events;`), so the arithmetic is checked against the header, not
    /// against itself.
    /// </summary>
    [Fact]
    public void ImportedDeviceLayoutOffsets_FollowTheHeaderFieldWidths()
    {
        Assert.Equal(8, UdeBufferCodec.DevBusIdOffset);
        Assert.Equal(40, UdeBufferCodec.DevServiceOffset);
        Assert.Equal(72, UdeBufferCodec.DevHostOffset);
        Assert.Equal(1097, UdeBufferCodec.DevLayoutIndependentPrefix);

        foreach (ImportedDeviceLayout layout in ImportedDeviceLayout.Candidates)
        {
            // Fields after the layout-independent prefix, packed back-to-back in declaration order.
            // `devid` is UINT32, so under MSVC's default rules it starts at the next 4-byte boundary
            // after `host` ends (1100); pack(1) puts it immediately after (1097).
            int expectedDevId = layout == ImportedDeviceLayout.Packed
                ? UdeBufferCodec.DevLayoutIndependentPrefix
                : ((UdeBufferCodec.DevLayoutIndependentPrefix + 3) / 4) * 4;
            Assert.Equal(expectedDevId, layout.DevIdOffset);
            Assert.Equal(layout.SpeedOffset, layout.DevIdOffset + 4);          // UINT32 devid
            Assert.Equal(layout.VendorOffset, layout.SpeedOffset + 4);         // usb_device_speed (int)
            Assert.Equal(layout.ProductOffset, layout.VendorOffset + 2);       // UINT16 vendor
            Assert.Equal(layout.SerialOffset, layout.ProductOffset + 2);       // UINT16 product
            Assert.Equal(layout.ISerialOffset, layout.SerialOffset + 16);      // char serial[SERIAL_BUFSZ]
            Assert.Equal(layout.WskEventsOffset, layout.ISerialOffset + 1);    // UCHAR iserial

            // Only the trailing padding differs: MSVC default pads the element to 4 bytes (the struct's
            // own alignment, from its UINT32 members); pack(1) does not.
            int end = layout.WskEventsOffset + 1;                              // bool wsk_events
            int expectedStride = layout == ImportedDeviceLayout.Packed ? end : ((end + 3) / 4) * 4;
            Assert.Equal(expectedStride, layout.Stride);
            Assert.True(layout.Stride >= end, $"{layout.Name}: stride {layout.Stride} < element size {end}");
        }
    }

    private static byte[] BuildImportedDevicesBuffer(ImportedDeviceLayout layout, params (int Port, string BusId)[] devices)
    {
        var buffer = new byte[UdeBufferCodec.GetImportedDevicesBufferSize(devices.Length, layout)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(0), (uint)buffer.Length);

        for (int i = 0; i < devices.Length; i++)
        {
            int start = UdeBufferCodec.GetImportedDevicesHeaderOffset + (i * layout.Stride);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                buffer.AsSpan(start + UdeBufferCodec.DevPortOffset), devices[i].Port);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                buffer.AsSpan(start + UdeBufferCodec.DevLocationHashOffset), 0xABCD0000u + (uint)i);
            WriteFixed(buffer, start + UdeBufferCodec.DevBusIdOffset, 32, devices[i].BusId);
            WriteFixed(buffer, start + UdeBufferCodec.DevServiceOffset, 32, "3240");
            WriteFixed(buffer, start + UdeBufferCodec.DevHostOffset, 1025, "127.0.0.1");
        }

        return buffer;
    }

    private static void WriteFixed(byte[] buffer, int offset, int width, string value)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static string ReadFixed(byte[] buffer, int offset, int width)
    {
        int end = Array.IndexOf(buffer, (byte)0, offset, width);
        if (end < 0) end = offset + width;
        return System.Text.Encoding.ASCII.GetString(buffer, offset, end - offset);
    }
}

public sealed class AutoAttachPolicyTests
{
    private static InventoryEntry Entry(string peer = "lab", string busId = "1-2", ushort vid = 0x1234,
        ushort pid = 0x5678, byte deviceClass = 0x08)
    {
        var peerRecord = new Peer { Name = peer, Host = "10.0.0.5", Port = 3240, UseTls = false };
        return new InventoryEntry
        {
            Peer = peerRecord,
            Device = new UsbipDeviceRecord
            {
                Path = "/dev/x",
                BusId = busId,
                VendorId = vid,
                ProductId = pid,
                DeviceClass = deviceClass,
            },
            RoutingKey = busId,
        };
    }

    [Fact]
    public void DiscoveryAloneNeverAutoAttaches()
    {
        using var scope = new TempPolicy();
        AutoAttachDecision decision = scope.Policy.Evaluate(Entry());

        Assert.False(decision.ShouldAutoAttach);
        Assert.True(decision.IsAttachable);          // a manual attach is still allowed
        Assert.Contains("no auto-attach grant", decision.Reason);
    }

    [Fact]
    public void AGrantMakesTheSameDeviceAutoAttach_AndNothingElse()
    {
        using var scope = new TempPolicy();
        scope.Policy.Grant(Entry());

        Assert.True(scope.Policy.Evaluate(Entry()).ShouldAutoAttach);
        Assert.False(scope.Policy.Evaluate(Entry(busId: "1-3")).ShouldAutoAttach);
        Assert.False(scope.Policy.Evaluate(Entry(peer: "other")).ShouldAutoAttach);
    }

    [Fact]
    public void AnExplicitDenialBeatsAGrant()
    {
        using var scope = new TempPolicy();
        scope.Policy.Grant(Entry());
        scope.Policy.Deny(Entry());

        AutoAttachDecision decision = scope.Policy.Evaluate(Entry());
        Assert.False(decision.ShouldAutoAttach);
        Assert.True(scope.Policy.IsExplicitlyDenied(Entry().DeviceKey));
    }

    [Fact]
    public void IgnoreListBeatsAllowListAndAGrant()
    {
        using var scope = new TempPolicy();
        scope.Policy.SetAllow(new[] { "*" });
        scope.Policy.Grant(Entry());
        scope.Policy.AddIgnore("lab/1-2");

        AutoAttachDecision decision = scope.Policy.Evaluate(Entry());
        Assert.False(decision.IsAttachable);
        Assert.Contains("ignore list", decision.Reason);
    }

    [Fact]
    public void ANonEmptyAllowListIsExclusive()
    {
        using var scope = new TempPolicy();
        scope.Policy.SetAllow(new[] { "vid:0x1234" });

        Assert.True(scope.Policy.Evaluate(Entry()).IsAttachable);
        Assert.False(scope.Policy.Evaluate(Entry(vid: 0x9999)).IsAttachable);

        scope.Policy.SetAllow(new[] { "class:03" });
        Assert.True(scope.Policy.Evaluate(Entry(deviceClass: 0x03)).IsAttachable);
        Assert.False(scope.Policy.Evaluate(Entry(deviceClass: 0x08)).IsAttachable);

        scope.Policy.SetAllow(new[] { "lab" });
        Assert.True(scope.Policy.Evaluate(Entry()).IsAttachable);
        Assert.False(scope.Policy.Evaluate(Entry(peer: "wshop")).IsAttachable);
    }

    [Fact]
    public void UnrecognisedPatternsAreReported()
    {
        Assert.Single(DeviceSelector.Validate(new[] { "nonsense:thing" }));
        Assert.Empty(DeviceSelector.Validate(new[] { "*", "lab", "lab/1-2", "vid:0x1234", "class:08" }));
    }
}

public sealed class CredentialTests
{
    [Fact]
    public void TheRememberChoiceIsPersisted_ButNeverTheSecret()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vht-cred-{Guid.NewGuid():N}.json");
        var log = new ClientLog(console: null);

        var cache = new CredentialCache(path, log);
        cache.Remember("lab/10.0.0.5:3240/1-2", "hunter2", persistChoice: true);

        string onDisk = File.ReadAllText(path);
        Assert.DoesNotContain("hunter2", onDisk);
        Assert.Contains("1-2", onDisk);

        var reloaded = new CredentialCache(path, log);
        Assert.True(reloaded.IsRemembered("lab/10.0.0.5:3240/1-2"));
        Assert.Null(reloaded.GetPassword("lab/10.0.0.5:3240/1-2"));

        File.Delete(path);
    }

    [Fact]
    public async Task ThePlaintextStubTransportFailsClosed_AndTheHandshakeDoesNotPretendOtherwise()
    {
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var peer = new Peer { Name = "plain", Host = "127.0.0.1", Port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port, UseTls = false };

        await using IClientTransport transport = await PlainTcpTransportFactory.Instance.ConnectAsync(peer, CancellationToken.None);
        Assert.Equal(TransportCapabilities.None, transport.Capabilities);

        ClientAuthResult result = await transport.AuthenticateAsync(new ClientAuthRequest(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("plaintext", result.FailureReason);
    }

    [Fact]
    public async Task ATlsPeerIsRefusedRatherThanDowngraded()
    {
        var peer = new Peer { Name = "secure", Host = "127.0.0.1", Port = 3241, UseTls = true };

        var ex = await Assert.ThrowsAsync<ClientTransportException>(
            () => PlainTcpTransportFactory.Instance.ConnectAsync(peer, CancellationToken.None));
        Assert.Contains("downgrade", ex.Message);
    }
}

public sealed class PeerStoreTests
{
    [Theory]
    [InlineData("192.168.1.10:3240", "192.168.1.10", "192.168.1.10", 3240)]
    [InlineData("lab-pc", "lab-pc", "lab-pc", 3240)]
    [InlineData("bench@10.0.0.7:3241", "bench", "10.0.0.7", 3241)]
    public void SpecsParseIntoPeers(string spec, string expectedName, string expectedHost, int expectedPort)
    {
        Peer peer = PeerStore.Parse(spec);
        Assert.Equal(expectedName, peer.Name);
        Assert.Equal(expectedHost, peer.Host);
        Assert.Equal(expectedPort, peer.Port);
    }

    [Fact]
    public void PeersSurviveARoundTrip_AndACorruptFileStartsEmptyInsteadOfFailing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vht-peers-{Guid.NewGuid():N}.json");
        var log = new ClientLog(console: null);

        var store = new PeerStore(path, log);
        store.Add(PeerStore.Parse("lab@10.0.0.5:3240"));
        store.Add(PeerStore.Parse("wshop@10.0.0.6:3240"));

        var reloaded = new PeerStore(path, log);
        Assert.Equal(2, reloaded.Peers.Count);
        Assert.Equal("lab", reloaded.Peers[0].Name);

        File.WriteAllText(path, "{ not json at all");
        var recovered = new PeerStore(path, log);
        Assert.Empty(recovered.Peers);

        File.Delete(path);
    }
}

/// <summary>
/// ARCHITECTURE.md §5.4's machine-checked invariants: the concept "maximum number of devices" must not
/// exist in this codebase. Two checks — reflection over the client assembly and a source grep — so a
/// future contributor adding either one fails the build rather than shipping a cap.
/// </summary>
public sealed class DeviceCountInvariantTests
{
    [Fact]
    public void NoMemberOfTheClientAssemblyIsNamedLikeADeviceLimit()
    {
        var pattern = new Regex("Max.*Devices|Device.*Limit|MaxDevices|MaxDeviceCount", RegexOptions.IgnoreCase);
        Assembly assembly = typeof(AttachManager).Assembly;

        var offenders = new List<string>();
        foreach (Type type in assembly.GetTypes())
        {
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                                                          | BindingFlags.Instance | BindingFlags.Static
                                                          | BindingFlags.DeclaredOnly))
            {
                if (pattern.IsMatch(member.Name))
                {
                    offenders.Add($"{type.FullName}.{member.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoClientSourceEnforcesALicenceOrDeviceGate()
    {
        string root = LocateClientSourceRoot();
        string[] forbidden = { "max_devices", "MAX DEVICES EXCEEDED", "unlicensed", "purchase a licence",
                               "purchase a license", "licence key", "license key" };

        var hits = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;   // this test's own strings are not the product
            }

            string text = File.ReadAllText(file);
            foreach (string needle in forbidden)
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"{file}: {needle}");
                }
            }
        }

        Assert.Empty(hits);
    }

    /// <summary>Walks up from the test binary until the client's own source directory is found.</summary>
    private static string LocateClientSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Vht.Client");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Vht.Client.csproj")))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "could not locate src/Vht.Client from " + AppContext.BaseDirectory +
            "; the invariant test needs the source tree to grep.");
    }
}

/// <summary>A policy bound to a throwaway file, disposed with the test.</summary>
internal sealed class TempPolicy : IDisposable
{
    private readonly string _path;

    internal TempPolicy()
    {
        _path = Path.Combine(Path.GetTempPath(), $"vht-policy-{Guid.NewGuid():N}.json");
        Policy = new AutoAttachPolicy(_path, new ClientLog(console: null));
    }

    internal AutoAttachPolicy Policy { get; }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Nothing to do.
        }
    }
}

/// <summary>An IClientTransport over a fixed buffer, so codec paths can be driven without a socket.</summary>
internal sealed class ScriptedTransport : IClientTransport
{
    private int _position;

    internal ScriptedTransport(byte[] buffer) => Buffer = buffer;

    internal byte[] Buffer { get; }

    public TransportKind Kind => TransportKind.PlainTcp;

    public TransportCapabilities Capabilities => TransportCapabilities.None;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int available = Math.Min(buffer.Length, Buffer.Length - _position);
        if (available <= 0)
        {
            return ValueTask.FromResult(0);
        }

        Buffer.AsMemory(_position, available).CopyTo(buffer);
        _position += available;
        return ValueTask.FromResult(available);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) => ValueTask.CompletedTask;

    public Task<ClientAuthResult> AuthenticateAsync(ClientAuthRequest request, CancellationToken ct) =>
        Task.FromResult(ClientAuthResult.Failed("plaintext"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
