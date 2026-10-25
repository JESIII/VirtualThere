// SPDX-License-Identifier: Apache-2.0
//
// AttachManager: the client's brain. It owns the set of attached devices, applies policy, drives the
// virtual host controller, follows hotplug, and handles the two failure paths the card calls out —
// a transport that dies (local detach + re-attach) and a peer that disappears (clean degrade + a
// re-attach path with no reboot).
//
// Multi-device (deliverable 3) is a property of the data structures, not a feature: `_attached` is a
// ConcurrentDictionary keyed by device, every operation is per-device, and there is no counter, no
// "slots" concept and no maximum anywhere in this file. The only thing that can stop the Nth device
// is the kernel driver's own port allocation, which is measured (not assumed) by
// IDeviceController.MeasurePortCapacity and reported to the acceptance card.

using System.Collections.Concurrent;
using Vht.Client.Bridge;
using Vht.Client.Diagnostics;
using Vht.Client.Policy;

namespace Vht.Client.Attach;

/// <summary>Tuning knobs for the attach loop.</summary>
internal sealed record AttachManagerOptions
{
    /// <summary>How often the inventory is re-read and the desired state re-applied.</summary>
    internal TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Ports the driver should be able to allocate before we consider the machine constrained. Used
    /// only for a warning at startup — never to refuse an attach (ARCHITECTURE.md §5, §10 risk 3).
    /// </summary>
    internal int ExpectedMinimumPorts { get; init; } = 8;

    /// <summary>Set false to make the client manual-only (the auto-attach loop then never attaches).</summary>
    internal bool AutoAttachEnabled { get; init; } = true;
}

/// <summary>Result of one reconcile pass, for tests and `status`.</summary>
internal sealed record ReconcileReport
{
    internal int AutoAttached { get; init; }
    internal int Reconnected { get; init; }
    internal int DetachedOnPeerLoss { get; init; }
    internal int DetachedOnDeviceRemoval { get; init; }
    internal int AttachFailures { get; init; }
    internal IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>An attach waiting to be retried (peer down, or a previous attempt failed).</summary>
internal sealed record PendingAttach
{
    internal required string DeviceKey { get; init; }
    internal required string RoutingKey { get; init; }
    internal required string PeerName { get; init; }
    internal required AttachReason OriginalReason { get; init; }
    internal required DateTimeOffset NotBeforeUtc { get; init; }
    internal required string Reason { get; init; }
}

internal sealed class AttachManager : IAsyncDisposable
{
    private readonly LoopbackBridge _bridge;
    private readonly IRemoteDeviceInventory _inventory;
    private readonly IDeviceController _controller;
    private readonly AutoAttachPolicy _policy;
    private readonly ClientLog _log;
    private readonly AttachManagerOptions _options;

    private readonly ConcurrentDictionary<string, AttachedDevice> _attached = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingAttach> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttemptUtc = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    internal AttachManager(
        LoopbackBridge bridge,
        IRemoteDeviceInventory inventory,
        IDeviceController controller,
        AutoAttachPolicy policy,
        ClientLog log,
        AttachManagerOptions? options = null)
    {
        _bridge = bridge;
        _inventory = inventory;
        _controller = controller;
        _policy = policy;
        _log = log;
        _options = options ?? new AttachManagerOptions();

        _bridge.RelayStarted += OnRelayStarted;
        _bridge.RelayEnded += OnRelayEnded;
    }

    internal event EventHandler<AttachedDevice>? DeviceAttached;

    internal event EventHandler<AttachedDevice>? DeviceDetached;

    internal event EventHandler<(AttachedDevice Device, string Reason)>? AttachFailed;

    /// <summary>Every attached device, keyed by policy device key. Read it; do not try to size it.</summary>
    internal IReadOnlyDictionary<string, AttachedDevice> AttachedDevices => _attached;

    internal int AttachedCount => _attached.Count;

    /// <summary>Attaches waiting for a retry window or a peer coming back.</summary>
    internal IReadOnlyCollection<PendingAttach> PendingAttaches => _pending.Values.ToArray();

    internal bool IsBridgeRunning => _bridge.IsRunning;

    /// <summary>The loopback endpoint the driver is pointed at, or null when the bridge is stopped.</summary>
    internal string? BridgeEndpoint => _bridge.LocalEndpoint?.ToString();

    /// <summary>What the virtual host controller is (or why it is unavailable).</summary>
    internal string ControllerDescription => _controller.Description;

    /// <summary>What the controller currently has imported — the ground truth for "is it attached".</summary>
    internal IReadOnlyList<ImportedDeviceSnapshot> ControllerSnapshot() => _controller.GetImportedDevices();

    /// <summary>Starts the reconcile loop (hotplug, auto-attach, reconnect).</summary>
    internal void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_shutdown.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ReconcileReport report = await ReconcileAsync(ct).ConfigureAwait(false);
                if (report.AutoAttached + report.Reconnected + report.DetachedOnPeerLoss
                    + report.DetachedOnDeviceRemoval + report.AttachFailures > 0)
                {
                    _log.Debug("attach",
                        $"reconcile: {report.AutoAttached} auto, {report.Reconnected} reconnected, " +
                        $"{report.DetachedOnPeerLoss} detached (peer), {report.DetachedOnDeviceRemoval} detached (device), " +
                        $"{report.AttachFailures} failed");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is ClientException or IOException or System.Net.Sockets.SocketException)
            {
                _log.Warn("attach", $"reconcile pass failed: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(_options.ReconcileInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One desired-state pass: read the inventory, auto-attach anything granted, detach anything whose
    /// peer or device went away, and retry anything pending. Safe to call concurrently with itself
    /// (the catalog serialises refreshes; the dictionaries are concurrent).
    /// </summary>
    internal async Task<ReconcileReport> ReconcileAsync(CancellationToken ct)
    {
        IReadOnlyList<InventoryEntry> entries;
        try
        {
            entries = await _inventory.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ClientException or IOException or System.Net.Sockets.SocketException)
        {
            _log.Warn("attach", $"inventory refresh failed: {ex.Message}");
            entries = _inventory.Snapshot;
        }

        var notes = new List<string>();
        int detachedOnPeerLoss = 0;
        int detachedOnDeviceRemoval = 0;
        int autoAttached = 0;
        int reconnected = 0;
        int failures = 0;

        // --- 1. did the driver lose a device? (remote unplug, or the peer dropped the import) ------
        bool haveControllerSnapshot = _controller.TryGetImportedDevices(
            out IReadOnlyList<ImportedDeviceSnapshot> importedDevices);
        var livePorts = haveControllerSnapshot
            ? importedDevices.Select(d => d.Port).ToHashSet()
            : null;
        foreach (AttachedDevice device in _attached.Values.ToArray())
        {
            // A failed controller query means its state is unknown, not that all ports vanished.
            if (livePorts is null || livePorts.Contains(device.Port))
            {
                continue;
            }

            if (_attached.TryRemove(device.DeviceKey, out AttachedDevice? removed))
            {
                detachedOnDeviceRemoval++;
                notes.Add($"{removed.RoutingKey}: the virtual port disappeared (device unplugged remotely or the " +
                          "peer dropped the import); it will be re-attached when it is exported again");
                _log.Info("attach", notes[^1]);
                DeviceDetached?.Invoke(this, removed);
                _pending.TryRemove(removed.DeviceKey, out _);
            }
        }

        // --- 2. peers that stopped answering: detach their devices locally (clean degrade) ---------
        var unavailablePeers = _inventory.UnavailablePeers
            .Select(u => u.Peer.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (AttachedDevice device in _attached.Values.ToArray())
        {
            if (!unavailablePeers.Contains(device.PeerName))
            {
                continue;
            }

            if (!_attached.TryRemove(device.DeviceKey, out AttachedDevice? removed))
            {
                continue;
            }

            _controller.Detach(removed.Port);
            detachedOnPeerLoss++;
            notes.Add($"peer '{removed.PeerName}' is unreachable; detached {removed.RoutingKey} locally " +
                      "(no reboot needed, re-attach is automatic when the peer answers again)");
            _log.Warn("attach", notes[^1]);
            DeviceDetached?.Invoke(this, removed);

            _pending[removed.DeviceKey] = new PendingAttach
            {
                DeviceKey = removed.DeviceKey,
                RoutingKey = removed.RoutingKey,
                PeerName = removed.PeerName,
                OriginalReason = removed.Reason,
                NotBeforeUtc = DateTimeOffset.UtcNow + _policy.RetryDelay,
                Reason = "peer was unreachable",
            };
        }

        // --- 2b. the peer is answering, but it no longer exports a device we believe is attached ----
        // Hotplug has two halves, and the driver only reports one of them: when the *port* vanishes
        // step 1 catches it, but a device unplugged at the peer while the driver still holds a port
        // (or a peer that stopped exporting without dropping the socket) leaves our bookkeeping
        // claiming a device that is gone. Reconcile against the export list so the client's view and
        // the peer's are the same view. A device on an *unreachable* peer is not this case — step 2
        // owns it, so a transient query failure cannot be mistaken for a removal.
        var exportedKeys = entries.Select(e => e.DeviceKey).ToHashSet(StringComparer.Ordinal);
        foreach (AttachedDevice device in _attached.Values.ToArray())
        {
            if (unavailablePeers.Contains(device.PeerName) || exportedKeys.Contains(device.DeviceKey))
            {
                continue;
            }

            if (!_attached.TryRemove(device.DeviceKey, out AttachedDevice? removed))
            {
                continue;
            }

            _controller.Detach(removed.Port);
            detachedOnDeviceRemoval++;
            notes.Add($"{removed.RoutingKey}: peer '{removed.PeerName}' no longer exports it; detached " +
                      $"locally (a device the peer has stopped offering is not attached any more)");
            _log.Info("attach", notes[^1]);
            DeviceDetached?.Invoke(this, removed);

            _pending[removed.DeviceKey] = new PendingAttach
            {
                DeviceKey = removed.DeviceKey,
                RoutingKey = removed.RoutingKey,
                PeerName = removed.PeerName,
                OriginalReason = removed.Reason,
                NotBeforeUtc = DateTimeOffset.UtcNow + _policy.RetryDelay,
                Reason = "the peer stopped exporting this device",
            };
        }

        // --- 3. retry pending attaches whose window has opened -------------------------------------
        var entriesByKey = entries.ToDictionary(e => e.DeviceKey, StringComparer.Ordinal);
        foreach (PendingAttach pending in _pending.Values.ToArray())
        {
            if (pending.NotBeforeUtc > DateTimeOffset.UtcNow)
            {
                continue;
            }

            if (!entriesByKey.TryGetValue(pending.DeviceKey, out InventoryEntry? entry))
            {
                // Still not exported (or the peer is still down): leave it pending.
                continue;
            }

            try
            {
                await AttachAsync(entry, AttachReason.Reconnect, ct).ConfigureAwait(false);
                _pending.TryRemove(pending.DeviceKey, out _);
                reconnected++;
            }
            catch (ClientException ex)
            {
                failures++;
                RetryLater(entry, AttachReason.Reconnect, ex.Message);
                notes.Add($"re-attach of {entry.RoutingKey} failed: {ex.Message}");
            }
        }

        // --- 4. auto-attach: only devices with a recorded grant, and only if enabled --------------
        if (_options.AutoAttachEnabled)
        {
            foreach (InventoryEntry entry in entries)
            {
                if (_attached.ContainsKey(entry.DeviceKey) || _pending.ContainsKey(entry.DeviceKey))
                {
                    continue;
                }

                AutoAttachDecision decision = _policy.Evaluate(entry);
                if (!decision.ShouldAutoAttach)
                {
                    continue;
                }

                try
                {
                    await AttachAsync(entry, AttachReason.AutoAttach, ct).ConfigureAwait(false);
                    autoAttached++;
                }
                catch (ClientException ex)
                {
                    failures++;
                    RetryLater(entry, AttachReason.AutoAttach, ex.Message);
                    notes.Add($"auto-attach of {entry.RoutingKey} failed: {ex.Message}");
                }
            }
        }

        return new ReconcileReport
        {
            AutoAttached = autoAttached,
            Reconnected = reconnected,
            DetachedOnPeerLoss = detachedOnPeerLoss,
            DetachedOnDeviceRemoval = detachedOnDeviceRemoval,
            AttachFailures = failures,
            Notes = notes,
        };
    }

    /// <summary>
    /// Attaches one device. Fails closed: a stopped bridge or an unavailable controller refuses the
    /// attach rather than doing something clever (ARCHITECTURE.md §6.2).
    /// </summary>
    internal async Task<AttachedDevice> AttachAsync(InventoryEntry entry, AttachReason reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_attached.TryGetValue(entry.DeviceKey, out AttachedDevice? existing))
        {
            return existing;
        }

        AutoAttachDecision decision = _policy.Evaluate(entry);
        if (!decision.IsAttachable)
        {
            throw new ClientAttachException($"refusing to attach {entry.RoutingKey}: {decision.Reason}");
        }

        if (reason is AttachReason.AutoAttach or AttachReason.Reconnect && !decision.ShouldAutoAttach
            && !_pending.ContainsKey(entry.DeviceKey))
        {
            throw new ClientAttachException(
                $"refusing to auto-attach {entry.RoutingKey}: {decision.Reason}");
        }

        if (!_bridge.IsRunning)
        {
            throw new ClientAttachException(
                "the loopback bridge is not running, so an attach would have to point the driver at a " +
                "remote host — which would put plaintext USB/IP on the network. Refusing (fail closed, " +
                "ARCHITECTURE.md §6.2). Start the client service first.");
        }

        if (!_controller.IsAvailable)
        {
            throw new ClientAttachException($"the virtual host controller is unavailable: {_controller.Description}");
        }

        // Record the attempt before doing it, so a failing device cannot hot-loop the retry path.
        _lastAttemptUtc[entry.DeviceKey] = DateTimeOffset.UtcNow;

        if (reason == AttachReason.AutoAttach && _policy.AttachDelay > TimeSpan.Zero)
        {
            // The reference product exposes an attach delay; without it, a server flapping at boot would
            // get hammered. Manual attaches do not wait.
            await Task.Delay(_policy.AttachDelay, ct).ConfigureAwait(false);
        }

        int bridgePort = _bridge.LocalEndpoint?.Port
            ?? throw new ClientAttachException("the bridge has no listening endpoint");

        DeviceAttachOutcome outcome = _controller.Attach(
            LoopbackBridgeHost,
            bridgePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entry.RoutingKey,
            serial: null,
            onceOnly: false);

        if (!outcome.Success)
        {
            // A failed attach can leave the port in `connecting`; cancel it so the slot is freed and a
            // later retry is not refused for a reason that no longer exists.
            _controller.StopAttachAttempts(
                LoopbackBridgeHost,
                bridgePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.RoutingKey);
            string reasonText = outcome.FailureReason ?? "the controller refused the attach";
            AttachFailed?.Invoke(this, (new AttachedDevice
            {
                DeviceKey = entry.DeviceKey,
                RoutingKey = entry.RoutingKey,
                PeerName = entry.Peer.Name,
                Port = 0,
                LocationHash = 0,
                AttachedUtc = DateTimeOffset.UtcNow,
                Reason = reason,
            }, reasonText));
            throw new ClientAttachException(reasonText);
        }

        var attached = new AttachedDevice
        {
            DeviceKey = entry.DeviceKey,
            RoutingKey = entry.RoutingKey,
            PeerName = entry.Peer.Name,
            Port = outcome.Port,
            LocationHash = outcome.LocationHash,
            AttachedUtc = DateTimeOffset.UtcNow,
            Reason = reason,
            IsRelayLive = false,
        };

        _attached[entry.DeviceKey] = attached;
        _log.Info("attach", $"attached {entry.RoutingKey} on port {outcome.Port} ({reason})");

        // The grant is recorded AFTER a successful attach — never before authorization (ARCHITECTURE.md §7).
        if (reason == AttachReason.AutoAttach)
        {
            _policy.Grant(entry);
        }

        DeviceAttached?.Invoke(this, attached);
        return attached;
    }

    /// <summary>Detaches one device by policy key. Returns false when it was not attached.</summary>
    internal bool Detach(string deviceKey)
    {
        if (!_attached.TryRemove(deviceKey, out AttachedDevice? device))
        {
            return false;
        }

        _pending.TryRemove(deviceKey, out _);
        bool ok = _controller.Detach(device.Port);
        if (!ok)
        {
            _log.Warn("attach", $"the controller refused to detach {device.RoutingKey} (port {device.Port})");
        }
        _log.Info("attach", $"detached {device.RoutingKey} (port {device.Port})");
        DeviceDetached?.Invoke(this, device);
        return true;
    }

    /// <summary>Detaches everything — shutdown, or the user's "detach all".</summary>
    internal int DetachAll()
    {
        int count = 0;
        foreach (string key in _attached.Keys.ToArray())
        {
            if (Detach(key))
            {
                count++;
            }
        }
        count += _controller.DetachAll();
        return count;
    }

    /// <summary>
    /// Local detach for every device on one peer, without touching the others. This is the atomic
    /// operation behind "unplugging the network degrades cleanly" that does not disturb unrelated peers.
    /// </summary>
    internal int DetachPeer(string peerName)
    {
        int count = 0;
        foreach (AttachedDevice device in _attached.Values.ToArray())
        {
            if (string.Equals(device.PeerName, peerName, StringComparison.OrdinalIgnoreCase) && Detach(device.DeviceKey))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Measures the driver's port ceiling (ARCHITECTURE.md §10 risk 3) and logs it honestly.</summary>
    internal PortCapacityReport MeasurePortCapacity()
    {
        PortCapacityReport report = _controller.MeasurePortCapacity();
        if (report.CeilingReached && report.Allocated < _options.ExpectedMinimumPorts)
        {
            _log.Warn("attach",
                $"the virtual host controller reached its ceiling at {report.Allocated} ports, below the " +
                $"{_options.ExpectedMinimumPorts} the acceptance target wants ({report.Detail}). VirtualThere " +
                "does not cap devices; the answer is an additional controller instance (ARK §5/§10 risk 3).");
        }
        else
        {
            _log.Info("attach", report.ToString());
        }
        return report;
    }

    private void RetryLater(InventoryEntry entry, AttachReason reason, string failure)
    {
        _pending[entry.DeviceKey] = new PendingAttach
        {
            DeviceKey = entry.DeviceKey,
            RoutingKey = entry.RoutingKey,
            PeerName = entry.Peer.Name,
            OriginalReason = reason,
            NotBeforeUtc = DateTimeOffset.UtcNow + _policy.RetryDelay,
            Reason = failure,
        };
    }

    private void OnRelayStarted(object? sender, RelaySessionInfo info)
    {
        AttachedDevice? match = _attached.Values.FirstOrDefault(d =>
            string.Equals(d.RoutingKey, info.RoutingKey, StringComparison.Ordinal)
            && string.Equals(d.PeerName, info.PeerName, StringComparison.Ordinal));

        if (match is not null)
        {
            _attached[match.DeviceKey] = match with { IsRelayLive = true };
        }
    }

    private void OnRelayEnded(object? sender, RelayEndedEventArgs e)
    {
        AttachedDevice? match = _attached.Values.FirstOrDefault(d =>
            string.Equals(d.RoutingKey, e.Session.RoutingKey, StringComparison.Ordinal)
            && string.Equals(d.PeerName, e.Session.PeerName, StringComparison.Ordinal));

        if (match is null)
        {
            return;
        }

        _log.Warn("attach",
            $"the transport for {match.RoutingKey} ended ({e.Reason}); marking it for re-attach. " +
            "Nothing on the machine needs restarting for this.");

        if (_attached.TryRemove(match.DeviceKey, out AttachedDevice? removed))
        {
            DeviceDetached?.Invoke(this, removed with { IsRelayLive = false });
            _pending[match.DeviceKey] = new PendingAttach
            {
                DeviceKey = match.DeviceKey,
                RoutingKey = match.RoutingKey,
                PeerName = match.PeerName,
                OriginalReason = match.Reason,
                NotBeforeUtc = DateTimeOffset.UtcNow + _policy.RetryDelay,
                Reason = $"transport ended: {e.Reason}",
            };
        }
    }

    /// <summary>The only host the driver may be pointed at (ARCHITECTURE.md §6.2).</summary>
    internal const string LoopbackBridgeHost = "127.0.0.1";

    public async ValueTask DisposeAsync()
    {
        _bridge.RelayStarted -= OnRelayStarted;
        _bridge.RelayEnded -= OnRelayEnded;

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        DetachAll();
        _shutdown.Dispose();
        _controller.Dispose();
    }
}
