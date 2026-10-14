// SPDX-License-Identifier: Apache-2.0
//
// RemoteDeviceCatalog: reads every configured peer's export list and keeps the client's inventory
// and busid route table current. It is the only place that talks to a peer just to *look*.
//
// Multi-device relevance: one refresh queries as many peers as are configured, concurrently, and the
// result is a flat list. There is no cap, no "first N" and no per-peer device cap — the only bound
// is machine resources (sockets and buffers), exactly as ARCHITECTURE.md §5 requires.
//
// Hotplug: a refresh diffs against the previous snapshot and raises Changed with arrived/departed
// sets, which is what the UI and the auto-attach policy subscribe to (deliverable 5).

using Vht.Client.Bridge;
using Vht.Client.Diagnostics;
using Vht.Client.Transport;

namespace Vht.Client.Peers;

/// <summary>Reads peer device lists. Thread-safe refreshes; a failed peer is reported, not fatal.</summary>
internal sealed class RemoteDeviceCatalog : IRemoteDeviceInventory
{
    private readonly PeerStore _peers;
    private readonly IClientTransportFactory _transportFactory;
    private readonly BusidRouter _router;
    private readonly ClientLog _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _snapshotLock = new();

    private IReadOnlyList<InventoryEntry> _snapshot = Array.Empty<InventoryEntry>();
    private IReadOnlyList<PeerUnavailable> _lastFailures = Array.Empty<PeerUnavailable>();

    internal RemoteDeviceCatalog(
        PeerStore peers,
        IClientTransportFactory transportFactory,
        BusidRouter router,
        ClientLog log)
    {
        _peers = peers;
        _transportFactory = transportFactory;
        _router = router;
        _log = log;
    }

    public event EventHandler<InventoryChangedEventArgs>? Changed;

    public IReadOnlyList<InventoryEntry> Snapshot
    {
        get
        {
            lock (_snapshotLock) return _snapshot;
        }
    }

    /// <summary>Peers whose last refresh failed, with the reason — surfaced to the UI instead of hidden.</summary>
    internal IReadOnlyList<PeerUnavailable> LastFailures
    {
        get
        {
            lock (_snapshotLock) return _lastFailures;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PeerUnavailable> UnavailablePeers => LastFailures;

    public async Task<IReadOnlyList<InventoryEntry>> RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<Peer> peers = _peers.Peers;
            var routes = new List<BusidRoute>();
            var entries = new List<InventoryEntry>();
            var failures = new List<PeerUnavailable>();

            // One task per peer, all in flight together: a slow or dead peer must not delay the others
            // (§5.3's "separate connections, separate failures", applied to listing).
            var perPeer = peers.Select(peer => QueryPeerAsync(peer, ct)).ToArray();
            PeerQueryResult[] results = await Task.WhenAll(perPeer).ConfigureAwait(false);

            foreach (PeerQueryResult result in results)
            {
                if (result.Failure is not null)
                {
                    failures.Add(result.Failure);
                    continue;
                }

                foreach (var (peer, device) in result.Devices)
                {
                    routes.Add(new BusidRoute
                    {
                        RoutingKey = device.BusId,      // provisional; rewritten below once uniqueness is known
                        RemoteBusId = device.BusId,
                        Peer = peer,
                    });
                    entries.Add(new InventoryEntry { Peer = peer, Device = device, RoutingKey = device.BusId });
                }
            }

            IReadOnlyList<BusidRoute> finalRoutes = BusidRouter.BuildRoutes(
                entries.Select(e => (e.Peer, e.Device.BusId)),
                peers);
            _router.Replace(finalRoutes);

            // Re-project the inventory onto the final routing keys so the two views never disagree.
            var byKey = finalRoutes.ToDictionary(r => (r.Peer.Name, r.RemoteBusId), r => r.RoutingKey);
            InventoryEntry[] merged = entries
                .Select(e => e with
                {
                    RoutingKey = byKey.TryGetValue((e.Peer.Name, e.Device.BusId), out string? key)
                        ? key
                        : e.Device.BusId,
                })
                .OrderBy(e => e.Peer.Name, StringComparer.Ordinal)
                .ThenBy(e => e.RoutingKey, StringComparer.Ordinal)
                .ToArray();

            InventoryEntry[] previous;
            lock (_snapshotLock)
            {
                previous = _snapshot.ToArray();
                _snapshot = merged;
                _lastFailures = failures;
            }

            foreach (PeerUnavailable failure in failures)
            {
                _log.Warn("catalog", $"peer '{failure.Peer.Name}' ({failure.Peer.Endpoint}) unavailable: {failure.Reason}");
            }

            PublishDiff(previous, merged);
            return merged;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void PublishDiff(IReadOnlyList<InventoryEntry> previous, IReadOnlyList<InventoryEntry> current)
    {
        var previousKeys = previous.Select(e => e.DeviceKey).ToHashSet(StringComparer.Ordinal);
        var currentKeys = current.Select(e => e.DeviceKey).ToHashSet(StringComparer.Ordinal);

        InventoryEntry[] arrived = current.Where(e => !previousKeys.Contains(e.DeviceKey)).ToArray();
        InventoryEntry[] departed = previous.Where(e => !currentKeys.Contains(e.DeviceKey)).ToArray();

        if (arrived.Length == 0 && departed.Length == 0)
        {
            return;
        }

        foreach (InventoryEntry entry in arrived)
        {
            _log.Info("catalog", $"device arrived: {entry}");
        }
        foreach (InventoryEntry entry in departed)
        {
            _log.Info("catalog", $"device departed: {entry}");
        }

        Changed?.Invoke(this, new InventoryChangedEventArgs
        {
            Arrived = arrived,
            Departed = departed,
            Current = current,
        });
    }

    private async Task<PeerQueryResult> QueryPeerAsync(Peer peer, CancellationToken ct)
    {
        try
        {
            await using IClientTransport transport = await _transportFactory.ConnectAsync(peer, ct).ConfigureAwait(false);
            IReadOnlyList<Usbip.UsbipDeviceRecord> devices = await Usbip.UsbipOp.ListDevicesAsync(transport, ct)
                .ConfigureAwait(false);
            return new PeerQueryResult(devices.Select(d => (peer, d)).ToList(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ClientException or IOException or System.Net.Sockets.SocketException)
        {
            return new PeerQueryResult(
                Array.Empty<(Peer, Usbip.UsbipDeviceRecord)>(),
                new PeerUnavailable(peer, ex.Message));
        }
    }

    private sealed record PeerQueryResult(
        IReadOnlyList<(Peer Peer, Usbip.UsbipDeviceRecord Device)> Devices,
        PeerUnavailable? Failure);
}
