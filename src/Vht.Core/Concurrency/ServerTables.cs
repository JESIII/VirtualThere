using System.Collections.Concurrent;
using Vht.Core.Model;
using Vht.Core.Protocol;

namespace Vht.Core.Concurrency;

/// <summary>Per-connection identifier. Monotonic, never zero (recon parity: 16-bit connection ids).</summary>
public readonly record struct ConnectionId(ushort Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>One live client connection.</summary>
public sealed class Connection
{
    public required ConnectionId Id { get; init; }

    /// <summary>Per-connection UUID, mirroring the reference product's <c>UuidCreate</c> per connection.</summary>
    public required string ClientUuid { get; init; }

    /// <summary>Remote address, for logs and hook substitution.</summary>
    public required string RemoteAddress { get; init; }

    /// <summary>Which transport this connection speaks.</summary>
    public required Transport.TransportKind Kind { get; init; }

    /// <summary>Cancelled on socket close, detach, kick or service stop.</summary>
    public required CancellationTokenSource Cancellation { get; init; }

    /// <summary>When the connection was accepted.</summary>
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Devices this connection currently holds. One client per device, but a connection may hold several.</summary>
    public ConcurrentDictionary<DeviceKey, AttachRecord> Attached { get; } = new();

    /// <summary>URBs in flight on this connection, keyed by the request sequence number.</summary>
    public InFlightUrbTable InFlight { get; } = new();

    /// <summary>Total URBs this connection has submitted, for diagnostics.</summary>
    public long SubmitsTotal;

    /// <summary>
    /// Transfers this connection has started but not yet finished, for diagnostics. It is a
    /// concurrency counter, not a limit: it is bounded by how many transfers one connection may
    /// have running at once (see <see cref="ConnectionResources.MaxInFlightUrbs"/>), and it is the
    /// number the session's teardown barrier waits on. A value that grows with the number of URBs
    /// ever served — rather than staying at the in-flight count — is a retention bug, which is why
    /// the acceptance soak prints it.
    /// </summary>
    public int PendingTransfers;

    public override string ToString() =>
        $"conn {Id} ({RemoteAddress}, {Kind}, devices={Attached.Count})";
}

/// <summary>Records that a device is bound to a connection.</summary>
public sealed class AttachRecord
{
    public required DeviceKey Key { get; init; }
    public required ConnectionId ConnectionId { get; init; }
    public required string ClientUuid { get; init; }
    public required string RemoteAddress { get; init; }
    public DateTimeOffset AttachedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Cancelled to force-detach this device (kick, unplug, service stop).</summary>
    public CancellationTokenSource Detach { get; init; } = new();

    /// <summary>True once the attach has been torn down, so a double-detach is a no-op.</summary>
    public int Detached;

    public override string ToString() => $"{Key} -> conn {ConnectionId} ({RemoteAddress})";
}

/// <summary>An exported device and its cached descriptors.</summary>
public sealed class ExportEntry
{
    public required UsbDevice Device { get; init; }

    /// <summary>Hotplug generation this descriptor cache was built at.</summary>
    public long Generation { get; init; }

    /// <summary>When the device entered the export table.</summary>
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Number of times the device has been bound to a client since export.</summary>
    public long BindCount;

    /// <summary>Human-readable description used by <c>vht list</c> and the control plane.</summary>
    public string Describe() => Device.Describe();
}

/// <summary>
/// Every capturable device the server knows about.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no capacity here and no device-count field anywhere in this type.</b> The
/// dictionary grows to as many devices as the machine has; the only limits anywhere in the
/// server are per-connection (<see cref="ConnectionResources"/>) and per-machine OS resources.
/// That is the deliberate structural difference from the reference product, whose equivalent
/// rejection is <c>BIND MAX_DEVICES_EXCEEDED</c> reached through the status table at
/// <c>0x1409689b0</c> (ARCHITECTURE.md §5.4).
/// </para>
/// </remarks>
public sealed class ExportTable
{
    private readonly ConcurrentDictionary<DeviceKey, ExportEntry> _entries = new();

    /// <summary>Number of exported devices. Unbounded by design; reported for diagnostics only.</summary>
    public int Count => _entries.Count;

    /// <summary>Adds or replaces an exported device. Never refuses for any reason.</summary>
    public ExportEntry AddOrUpdate(UsbDevice device, long generation)
    {
        ArgumentNullException.ThrowIfNull(device);
        var entry = new ExportEntry { Device = device, Generation = generation };
        _entries[device.Key] = entry;
        return entry;
    }

    /// <summary>Removes a device from the export list (unplug, ignore, capture lost).</summary>
    public bool Remove(DeviceKey key, out ExportEntry? entry) => _entries.TryRemove(key, out entry);

    /// <summary>Looks up a device by bus id.</summary>
    public bool TryGet(DeviceKey key, out ExportEntry entry) => _entries.TryGetValue(key, out entry!);

    /// <summary>True when the bus id is exported.</summary>
    public bool Contains(DeviceKey key) => _entries.ContainsKey(key);

    /// <summary>A snapshot of every exported device.</summary>
    public IReadOnlyList<ExportEntry> Snapshot() => _entries.Values.ToArray();

    /// <summary>
    /// The devices that may be advertised on <paramref name="kind"/>.
    /// </summary>
    /// <remarks>
    /// The only filter is the §6.4 no-auth containment rule: a device carrying a per-device
    /// password is never advertised on the plaintext listener, because USB/IP has no field to
    /// carry one and the server could not enforce it. This is an authentication boundary, not
    /// a count. Every other device is advertised on both listeners.
    /// </remarks>
    public IReadOnlyList<ExportEntry> Advertisable(Transport.TransportKind kind)
    {
        if (kind != Transport.TransportKind.PlainTcp)
            return Snapshot();

        return _entries.Values.Where(e => !e.Device.RequiresPassword).ToArray();
    }

    /// <summary>Marks a bind against a device's counters.</summary>
    public void RecordBind(DeviceKey key)
    {
        if (_entries.TryGetValue(key, out ExportEntry? entry))
            Interlocked.Increment(ref entry.BindCount);
    }
}

/// <summary>
/// Which connection, if any, currently holds each device. Zero or one holder per device.
/// </summary>
/// <remarks>
/// Exclusivity here is the reference product's "in use elsewhere" behaviour, not a device
/// limit: the number of devices that may be attached <i>simultaneously</i> is the number of
/// distinct devices exported, each on its own connection. Nothing in this type can refuse a
/// bind because too many devices are already attached.
/// </remarks>
public sealed class AttachTable
{
    private readonly ConcurrentDictionary<DeviceKey, AttachRecord> _records = new();

    /// <summary>Number of devices currently attached to a client.</summary>
    public int Count => _records.Count;

    /// <summary>
    /// Attempts to bind <paramref name="key"/> to <paramref name="record"/>. Returns
    /// <see cref="BindStatus.Success"/>, or <see cref="BindStatus.DeviceBusy"/> when another
    /// connection holds the device. Note the complete absence of any device-count branch: the
    /// only rejection reachable here is exclusivity.
    /// </summary>
    public BindStatus TryAttach(DeviceKey key, AttachRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        while (true)
        {
            if (_records.TryAdd(key, record))
                return BindStatus.Success;

            if (!_records.TryGetValue(key, out AttachRecord? existing))
                continue; // raced with a detach; retry

            if (existing.ConnectionId == record.ConnectionId)
                return BindStatus.Success; // idempotent re-attach on the same connection

            return BindStatus.DeviceBusy;
        }
    }

    /// <summary>Detaches a device held by <paramref name="connectionId"/>.</summary>
    public bool Detach(DeviceKey key, ConnectionId connectionId)
    {
        if (!_records.TryGetValue(key, out AttachRecord? record))
            return false;
        if (record.ConnectionId != connectionId)
            return false;

        if (!_records.TryRemove(new KeyValuePair<DeviceKey, AttachRecord>(key, record)))
            return false;

        MarkDetached(record);
        return true;
    }

    /// <summary>
    /// Force-detaches whoever holds <paramref name="key"/> regardless of which connection it
    /// is. This is the kick path; it always succeeds when the device is attached. In the
    /// reference product this operation is behind the <c>OnDeviceKick</c> licence gate
    /// (RECON_AND_SPEC.md §1.3); here it is unconditional.
    /// </summary>
    public bool Kick(DeviceKey key, out AttachRecord? record)
    {
        if (!_records.TryRemove(key, out record))
            return false;

        MarkDetached(record!);
        return true;
    }

    /// <summary>Every attachment belonging to one connection.</summary>
    public IReadOnlyList<AttachRecord> ForConnection(ConnectionId connectionId) =>
        _records.Values.Where(r => r.ConnectionId == connectionId).ToArray();

    /// <summary>Removes and cancels every attachment of a connection (socket close, service stop).</summary>
    public IReadOnlyList<AttachRecord> DetachAll(ConnectionId connectionId)
    {
        var removed = new List<AttachRecord>();
        foreach (AttachRecord record in _records.Values)
        {
            if (record.ConnectionId != connectionId) continue;
            if (_records.TryRemove(new KeyValuePair<DeviceKey, AttachRecord>(record.Key, record)))
            {
                MarkDetached(record);
                removed.Add(record);
            }
        }
        return removed;
    }

    /// <summary>Current holder of a device, if any.</summary>
    public bool TryGet(DeviceKey key, out AttachRecord record) => _records.TryGetValue(key, out record!);

    /// <summary>A snapshot of every attachment.</summary>
    public IReadOnlyList<AttachRecord> Snapshot() => _records.Values.ToArray();

    private static void MarkDetached(AttachRecord record)
    {
        if (Interlocked.Exchange(ref record.Detached, 1) != 0)
            return;

        try
        {
            record.Detach.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>Every live client connection.</summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<ConnectionId, Connection> _connections = new();
    private int _next;

    /// <summary>Number of live connections.</summary>
    public int Count => _connections.Count;

    /// <summary>Allocates a fresh connection id. Ids start at 1 and are never reused in a process lifetime.</summary>
    public ConnectionId NextId()
    {
        int value = Interlocked.Increment(ref _next);
        return new ConnectionId((ushort)value);
    }

    /// <summary>Registers a connection.</summary>
    public void Add(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connections[connection.Id] = connection;
    }

    /// <summary>Removes a connection.</summary>
    public bool Remove(ConnectionId id) => _connections.TryRemove(id, out _);

    /// <summary>Looks up a connection.</summary>
    public bool TryGet(ConnectionId id, out Connection connection) => _connections.TryGetValue(id, out connection!);

    /// <summary>A snapshot of every connection.</summary>
    public IReadOnlyList<Connection> Snapshot() => _connections.Values.ToArray();
}
