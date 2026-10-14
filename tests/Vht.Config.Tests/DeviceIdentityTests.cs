using Vht.Config.DeviceIdentity;
using Xunit;

namespace Vht.Config.Tests;

/// <summary>
/// Device identity: stable across replug and hub-port change, with a documented fallback chain for
/// devices that report no serial number.
/// </summary>
public sealed class DeviceIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_serial_number_is_the_primary_identity_and_survives_a_port_change()
    {
        var resolver = new DeviceIdentityResolver(new DeviceIdMap());
        var first = resolver.Resolve(new[] { TestSupport.SerialDevice(port: "USB(1)#PORT0003") }, Now).Single();
        var moved = resolver.Resolve(new[] { TestSupport.SerialDevice(port: "USB(9)#PORT0011") }, Now).Single();

        Assert.Equal(DeviceIdentityTier.Serial, first.Key.Tier);
        Assert.Equal("s-1050-0407-0123456789AB", first.Key.Value);
        Assert.Equal(first.Key.Value, moved.Key.Value);
    }

    [Fact]
    public void A_serial_less_device_keeps_its_key_when_it_moves_to_another_port()
    {
        var resolver = new DeviceIdentityResolver(new DeviceIdMap());
        var first = resolver.Resolve(new[] { TestSupport.SerialLessDevice(port: "USB(1)#PORT0004") }, Now).Single();

        Assert.Equal(DeviceIdentityTier.Fingerprint, first.Key.Tier);
        Assert.StartsWith("f-090c-1000-1100-", first.Key.Value);

        var moved = resolver.Resolve(new[] { TestSupport.SerialLessDevice(port: "USB(7)#PORT0021") }, Now).Single();
        Assert.Equal(first.Key.Value, moved.Key.Value);
        Assert.False(moved.AdoptedFromMap, "the fingerprint alone is enough: no adoption was needed");
    }

    [Fact]
    public void A_device_with_no_serial_and_no_strings_is_stable_on_the_same_port()
    {
        var resolver = new DeviceIdentityResolver(new DeviceIdMap());
        var first = resolver.Resolve(new[] { TestSupport.AnonymousDevice(port: "USB(2)#PORT0007") }, Now).Single();
        var again = resolver.Resolve(new[] { TestSupport.AnonymousDevice(port: "USB(2)#PORT0007") }, Now).Single();

        Assert.Equal(DeviceIdentityTier.Topology, first.Key.Tier);
        Assert.Equal(first.Key.Value, again.Key.Value);
        Assert.True(again.AdoptedFromMap, "the map must own the key on the second sighting");
    }

    [Fact]
    public void An_anonymous_device_moved_to_another_port_adopts_its_previous_key_when_unambiguous()
    {
        var map = new DeviceIdMap();
        var resolver = new DeviceIdentityResolver(map);
        var first = resolver.Resolve(new[] { TestSupport.AnonymousDevice(port: "USB(2)#PORT0007") }, Now).Single();

        // the device is unplugged from port 7 and appears on port 9; nothing else changed.
        var moved = resolver.Resolve(new[] { TestSupport.AnonymousDevice(port: "USB(3)#PORT0009") }, Now).Single();

        Assert.Equal(first.Key.Value, moved.Key.Value);
        Assert.True(moved.AdoptedFromMap);
        Assert.Contains("adopted", moved.Reason);
    }

    [Fact]
    public void An_anonymous_device_moved_while_a_sibling_is_missing_does_not_steal_an_identity()
    {
        var map = new DeviceIdMap();
        var resolver = new DeviceIdentityResolver(map);

        // two identical anonymous devices of the same kind, on ports 7 and 8
        resolver.Resolve(new[]
        {
            TestSupport.AnonymousDevice(port: "USB(2)#PORT0007"),
            TestSupport.AnonymousDevice(port: "USB(2)#PORT0008"),
        }, Now);

        // one is unplugged and one new sighting appears on port 9: ambiguous, so no adoption.
        var result = resolver.Resolve(new[]
        {
            TestSupport.AnonymousDevice(port: "USB(2)#PORT0007"),
            TestSupport.AnonymousDevice(port: "USB(3)#PORT0009"),
        }, Now).Single(d => d.Descriptor.PortPath == "USB(3)#PORT0009");

        Assert.False(result.AdoptedFromMap);
        Assert.Equal(DeviceIdentityTier.Topology, result.Key.Tier);
    }

    [Fact]
    public void Two_identical_serial_less_devices_get_distinct_deterministic_keys()
    {
        var resolver = new DeviceIdentityResolver(new DeviceIdMap());
        var descriptors = new[]
        {
            TestSupport.SerialLessDevice(port: "USB(2)#PORT0007"),
            TestSupport.SerialLessDevice(port: "USB(2)#PORT0008"),
        };

        var first = resolver.Resolve(descriptors, Now);
        Assert.Equal(2, first.Select(d => d.Key.Value).Distinct().Count());
        Assert.Single(first, d => d.Key.IsDisambiguated);

        // the assignment must not depend on enumeration order
        var reversed = new DeviceIdentityResolver(new DeviceIdMap()).Resolve(descriptors.Reverse().ToArray(), Now);
        var byPort = reversed.ToDictionary(d => d.Descriptor.PortPath!, d => d.Key.Value);
        foreach (var device in first)
        {
            Assert.Equal(device.Key.Value, byPort[device.Descriptor.PortPath!]);
        }
    }

    [Theory]
    [InlineData("000000000000")]
    [InlineData("Default string")]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("1234567890")]
    [InlineData("abcdef")]
    [InlineData("   ")]
    [InlineData("AB")]
    public void Placeholder_serials_are_treated_as_absent(string serial)
    {
        Assert.Null(SerialSanitizer.Normalize(serial));

        var resolver = new DeviceIdentityResolver(new DeviceIdMap());
        var device = resolver.Resolve(new[] { TestSupport.SerialDevice(serial) }, Now).Single();
        Assert.NotEqual(DeviceIdentityTier.Serial, device.Key.Tier);
    }

    [Fact]
    public void Serial_normalisation_is_uppercase_and_strips_separators()
    {
        Assert.Equal("5F3A-88C1-0001", SerialSanitizer.Normalize("  5f3a 88c1-0001  "));
        Assert.Equal("ABC-123", SerialSanitizer.Normalize("abc/123"));
        // a long serial is truncated with a hash suffix, keeping the key <= 48 characters. The value
        // must not be a single repeated character: all-same-character serials are placeholders and are
        // treated as absent (docs/CONFIG.md, serial hygiene).
        var longSerial = new string('a', 199) + "b";
        var truncated = SerialSanitizer.Normalize(longSerial);
        Assert.NotNull(truncated);
        Assert.True(truncated!.Length <= 48);
        Assert.NotEqual(SerialSanitizer.Normalize(new string('a', 198) + "c"), truncated);
    }

    [Fact]
    public void The_identity_map_round_trips_through_the_state_file()
    {
        var directory = TestSupport.NewTempDirectory("identity");
        try
        {
            var store = TestSupport.StoreFor(directory);
            store.Load();

            var resolver = new DeviceIdentityResolver(store.IdentityMap);
            var resolved = resolver.Resolve(new[] { TestSupport.SerialLessDevice(port: "USB(1)#PORT0004") }, Now).Single();
            store.Save();

            var state = File.ReadAllText(Path.Combine(directory, ConfigPathResolver.StateFileName));
            Assert.Contains("[[identity.devices]]", state);
            Assert.Contains(resolved.Key.Value, state);

            var reloaded = TestSupport.StoreFor(directory);
            reloaded.Load();
            Assert.Equal(1, reloaded.IdentityMap.Count);
            Assert.True(reloaded.IdentityMap.TryResolveAlias(resolved.Key.Value, out var entry));
            Assert.Equal(0x090C, entry.VendorId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}