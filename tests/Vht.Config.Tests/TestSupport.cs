using System.Text;
using Vht.Config.DeviceIdentity;
using Vht.Config.Diagnostics;
using Vht.Config.Discovery;
using Vht.Config.Model;

namespace Vht.Config.Tests;

/// <summary>Shared helpers: temp directories that clean up, and sample descriptors.</summary>
public static class TestSupport
{
    public static string NewTempDirectory(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vht-config-tests", label + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static ConfigPaths PathsFor(string directory) => new(
        Path.Combine(directory, ConfigPathResolver.ConfigFileName),
        Path.Combine(directory, ConfigPathResolver.StateFileName),
        directory,
        ConfigSourceKind.Defaults,
        null);

    public static void WriteConfig(string directory, string content) =>
        File.WriteAllText(Path.Combine(directory, ConfigPathResolver.ConfigFileName), content, new UTF8Encoding(false));

    public static void WriteState(string directory, string content) =>
        File.WriteAllText(Path.Combine(directory, ConfigPathResolver.StateFileName), content, new UTF8Encoding(false));

    public static ConfigStore StoreFor(string directory, IReadOnlyDictionary<string, string?>? environment = null) =>
        new(PathsFor(directory), ConfigRole.Server, "TESTSERVER", environment ?? new Dictionary<string, string?>());

    /// <summary>A device with a real serial number.</summary>
    public static DeviceDescriptor SerialDevice(string serial = "0123456789AB", string port = "USB(1)#PORT0003") => new(
        InstanceId: $@"USB\VID_1050&PID_0407\{serial}",
        VendorId: 0x1050,
        ProductId: 0x0407,
        BcdDevice: 0x0100,
        SerialNumber: serial,
        Product: "YubiKey OTP+FIDO",
        Manufacturer: "Yubico",
        PortPath: port,
        ClassTriple: "03-00-00");

    /// <summary>A cheap device with no serial but usable strings.</summary>
    public static DeviceDescriptor SerialLessDevice(string port = "USB(1)#PORT0004", string product = "USB 2.0 Stick") => new(
        InstanceId: $@"USB\VID_090C&PID_1000\{port}",
        VendorId: 0x090C,
        ProductId: 0x1000,
        BcdDevice: 0x1100,
        SerialNumber: null,
        Product: product,
        Manufacturer: "Generic",
        PortPath: port,
        ClassTriple: "08-06-50");

    /// <summary>The worst case: no serial and no strings at all.</summary>
    public static DeviceDescriptor AnonymousDevice(string port = "USB(2)#PORT0007") => new(
        InstanceId: $@"USB\VID_1A2B&PID_0001\{port}",
        VendorId: 0x1A2B,
        ProductId: 0x0001,
        BcdDevice: 0x0000,
        SerialNumber: null,
        Product: null,
        Manufacturer: null,
        PortPath: port);

    public static ServiceInstance Instance(string name = "TESTSERVER", int port = 3240, string? spki = null) =>
        ServiceAdvertisement.ForServer(
            name,
            "testsrv.local",
            port,
            tlsEnabled: true,
            spki,
            new[] { "lz4" },
            instanceName: name);
}