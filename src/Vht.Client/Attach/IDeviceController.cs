// SPDX-License-Identifier: Apache-2.0
//
// The controller abstraction: "make Windows see a virtual USB device on port N". One real
// implementation (UdeDeviceController, over the usbip-win2 IOCTL ABI) and one in-memory fake used by
// the tests, so attach/detach/multi-device/reconnect logic is verified without a kernel driver
// installed on the build machine (ARCHITECTURE.md §10 risk 1/3 — the driver genuinely cannot be
// loaded here; the fake is not a substitute for hardware testing, it is what makes the *logic* testable).

namespace Vht.Client.Attach;

/// <summary>Why an attach happened — the auto path has different rules from the manual one.</summary>
internal enum AttachReason
{
    /// <summary>The user asked for this device.</summary>
    Manual,

    /// <summary>Policy said so: a recorded grant for this (server, device) pair.</summary>
    AutoAttach,

    /// <summary>A re-attach after a transport loss or a peer restart.</summary>
    Reconnect,
}

/// <summary>A device the driver has on a virtual port.</summary>
internal sealed record AttachedDevice
{
    internal required string DeviceKey { get; init; }
    internal required string RoutingKey { get; init; }
    internal required string PeerName { get; init; }
    internal required int Port { get; init; }
    internal required uint LocationHash { get; init; }
    internal required DateTimeOffset AttachedUtc { get; init; }
    internal required AttachReason Reason { get; init; }

    /// <summary>True while the bridge has a live relay for this device.</summary>
    internal bool IsRelayLive { get; init; }

    public override string ToString() => $"{RoutingKey} on port {Port} ({PeerName}, {Reason})";
}

/// <summary>Result of one attach request against the driver.</summary>
internal sealed record DeviceAttachOutcome
{
    internal required bool Success { get; init; }
    internal int Port { get; init; }
    internal uint LocationHash { get; init; }
    internal string? FailureReason { get; init; }

    internal static DeviceAttachOutcome Ok(int port, uint locationHash) =>
        new() { Success = true, Port = port, LocationHash = locationHash };

    internal static DeviceAttachOutcome Failed(string reason) => new() { Success = false, FailureReason = reason };
}

/// <summary>An imported device as the driver reports it (attach state, not device state).</summary>
internal sealed record ImportedDeviceSnapshot
{
    internal required int Port { get; init; }
    internal required string BusId { get; init; }
    internal required string Host { get; init; }
    internal required string Service { get; init; }
    internal uint LocationHash { get; init; }
    internal ushort VendorId { get; init; }
    internal ushort ProductId { get; init; }
    internal string Serial { get; init; } = string.Empty;
}

/// <summary>
/// The virtual host controller, as the client needs it. Implementations must not impose any limit on
/// the number of attached devices (ARCHITECTURE.md §5): every method here is per-device.
/// </summary>
internal interface IDeviceController : IDisposable
{
    /// <summary>Human-readable description of the backing controller, for `status` and logs.</summary>
    string Description { get; }

    /// <summary>True when the controller is usable (driver present and opened).</summary>
    bool IsAvailable { get; }

    /// <summary>Every device currently imported into the controller.</summary>
    IReadOnlyList<ImportedDeviceSnapshot> GetImportedDevices();

    /// <summary>Imports a device. `host`/`service` are the loopback bridge endpoint, never a peer.</summary>
    DeviceAttachOutcome Attach(string host, string service, string busId, string? serial, bool onceOnly = false);

    /// <summary>Detaches one port. Returns false when the port was not attached.</summary>
    bool Detach(int port);

    /// <summary>Detaches every port — the shutdown path.</summary>
    int DetachAll();

    /// <summary>
    /// Cancels pending attach attempts for (host, service, busid) and returns how many were canceled.
    /// Needed because a failed attach can leave a port in `connecting`, holding a slot.
    /// </summary>
    int StopAttachAttempts(string host, string service, string busId);

    /// <summary>
    /// Measures how many ports this controller can allocate, by attempting attaches to an address that
    /// cannot complete, and reports the number reached before the driver refused. This is the
    /// measurement ARCHITECTURE.md §10 risk 3 asks for (report the driver's real ceiling rather than
    /// assuming one); it measures *allocation*, not successful attachment.
    /// </summary>
    PortCapacityReport MeasurePortCapacity(int maximumToTry = 64);

    /// <summary>Raised when the driver reports a port changing state.</summary>
    event EventHandler<ImportedDeviceSnapshot>? DeviceChanged;
}

/// <summary>What the port-capacity probe found.</summary>
internal sealed record PortCapacityReport
{
    internal required int Allocated { get; init; }
    internal required bool CeilingReached { get; init; }
    internal required string Detail { get; init; }

    public override string ToString() =>
        CeilingReached
            ? $"driver allocated {Allocated} concurrent ports before refusing ({Detail})"
            : $"driver allocated {Allocated} concurrent ports without refusing ({Detail})";
}
