// SPDX-License-Identifier: Apache-2.0
//
// The client's view of "what do my peers export right now". The bridge answers OP_REQ_DEVLIST from
// this, the attach manager lists devices from it, and the UI's device list comes from its events.
// One abstraction, so hotplug is observed in exactly one place.

using Vht.Client.Usbip;

namespace Vht.Client.Bridge;

/// <summary>A device a peer exports, together with the routing key used to attach it here.</summary>
internal sealed record InventoryEntry
{
    internal required Peer Peer { get; init; }
    internal required UsbipDeviceRecord Device { get; init; }

    /// <summary>The key the driver is given: the peer's busid, or `busid@peer` when not unique.</summary>
    internal required string RoutingKey { get; init; }

    /// <summary>True when the key carries a `@peer` qualifier because two peers export the same busid.</summary>
    internal bool IsQualified => RoutingKey.Contains('@', StringComparison.Ordinal);

    /// <summary>
    /// The operator's nickname for this device on its peer, when the peer implements
    /// VirtualThere's device-info extension (docs/SERVER.md §8). <see langword="null"/> when the
    /// operator set none, or when the peer is a stock usbipd — the stock 312-byte device record
    /// has no string field for one, so there is nowhere else this could have come from.
    /// </summary>
    internal string? Nickname { get; init; }

    /// <summary>What the UI shows for this device: the nickname when there is one, else the vendor/product pair.</summary>
    internal string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? Device.VendorProduct : Nickname!;

    /// <summary>
    /// Device identity used by the policy layer (auto-attach grants, allow/deny lists). Deliberately
    /// spans peer identity and the *remote* busid: a grant is for a specific device on a specific
    /// server (ARCHITECTURE.md §7), and a device that moves to a different busid is a different device.
    /// </summary>
    internal string DeviceKey => BuildDeviceKey(Peer, Device.BusId);

    internal static string BuildDeviceKey(Peer peer, string remoteBusId) =>
        $"{peer.Name}/{peer.Host}:{peer.Port}/{remoteBusId}";

    public override string ToString() =>
        $"{RoutingKey} on {Peer.Name} {Device.VendorProduct} {Device.ClassName}";
}

/// <summary>
/// Source of the current device inventory. Implementations may cache; <see cref="RefreshAsync"/> is
/// the call that re-reads the peers.
/// </summary>
internal interface IRemoteDeviceInventory
{
    /// <summary>Reads every configured peer's device list and returns the merged inventory.</summary>
    Task<IReadOnlyList<InventoryEntry>> RefreshAsync(CancellationToken ct);

    /// <summary>The last successful refresh, without touching the network.</summary>
    IReadOnlyList<InventoryEntry> Snapshot { get; }

    /// <summary>
    /// Peers that failed their last refresh, with the reason. This is how the attach manager learns a
    /// peer went away (deliverable 5) — an empty inventory because a server is unreachable must be
    /// distinguishable from an empty inventory because it exports nothing.
    /// </summary>
    IReadOnlyList<PeerUnavailable> UnavailablePeers { get; }

    /// <summary>Raised when a device appears, vanishes, or changes on a peer.</summary>
    event EventHandler<InventoryChangedEventArgs>? Changed;
}

/// <summary>A peer that could not be queried, and why.</summary>
internal sealed record PeerUnavailable(Peer Peer, string Reason);

/// <summary>What changed between two refreshes (deliverable 5: hotplug UX).</summary>
internal sealed record InventoryChangedEventArgs
{
    internal required IReadOnlyList<InventoryEntry> Arrived { get; init; }
    internal required IReadOnlyList<InventoryEntry> Departed { get; init; }
    internal required IReadOnlyList<InventoryEntry> Current { get; init; }

    internal bool IsEmpty => Arrived.Count == 0 && Departed.Count == 0;
}
