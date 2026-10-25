// SPDX-License-Identifier: Apache-2.0
//
// Test doubles. These are NOT substitutes for hardware testing — the acceptance card (hardware-validation-workstream)
// runs against a real UDE driver and real USB devices. They exist so the client's own logic
// (routing, relay, attach bookkeeping, policy ordering, reconnect) is verified on every build, on a
// machine where the kernel driver cannot be loaded at all (ARCHITECTURE.md §10 risk 1).

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Vht.Client.Attach;
using Vht.Client.Usbip;

namespace Vht.Client.Tests;

/// <summary>
/// A minimal USB/IP server: answers OP_REQ_DEVLIST and OP_REQ_IMPORT and then echoes. The real server
/// is card server-workstream's; this one exists so the client's protocol and relay behaviour can be tested
/// without it, and it is deliberately byte-explicit about the frames it sends.
/// </summary>
internal sealed class FakeUsbipServer : IAsyncDisposable
{
    private TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<TcpClient, string> _importsByClient = new();
    private readonly List<UsbipDeviceRecord> _devices = new();
    private Task? _acceptLoop;
    private int _nextClientId;
    private int _boundPort;

    internal FakeUsbipServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    /// <summary>Closes the listening socket, leaving the port free: models a peer going off the network.</summary>
    internal void StopListening()
    {
        DropAllConnections();
        _listener.Stop();
    }

    /// <summary>Listens again on the same port (possible because the port was ours a moment ago).</summary>
    internal void StartListening()
    {
        _listener = new TcpListener(IPAddress.Loopback, _boundPort);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>Import replies to hand out in order; the last one repeats. Empty means "always OK".</summary>
    internal Queue<OpStatus> ImportStatuses { get; } = new();

    /// <summary>When true the server accepts a connection and then never answers (models a hung peer).</summary>
    internal bool Stall { get; set; }

    /// <summary>Busids requested via OP_REQ_IMPORT, in arrival order.</summary>
    internal ConcurrentQueue<string> RequestedBusIds { get; } = new();

    /// <summary>
    /// Nicknames to answer on VirtualThere's device-info extension (docs/SERVER.md §8). Empty
    /// means the peer has none configured; the extension still answers.
    /// </summary>
    internal Dictionary<string, string> Nicknames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When false the extension op is not answered at all and the connection is dropped instead,
    /// which is what a stock usbipd does with an op code it does not know. On by default, because
    /// VirtualThere's own server is the normal peer for this client.
    /// </summary>
    internal bool AnswersDeviceInfoOp { get; set; } = true;

    /// <summary>How many times the extension op has been asked for, so a test can prove it was sent.</summary>
    internal int DeviceInfoRequests;

    /// <summary>Sets a nickname the peer will report for one of its exported devices.</summary>
    internal FakeUsbipServer SetNickname(string busId, string nickname)
    {
        Nicknames[busId] = nickname;
        return this;
    }

    /// <summary>Connections accepted since start — the multi-device test asserts one per device.</summary>
    internal int AcceptedConnections => _clients.Count;

    /// <summary>Total connections ever accepted, including ones that ended.</summary>
    internal int TotalAccepted;

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal FakeUsbipServer AddDevice(string busId, ushort vendorId = 0x1234, ushort productId = 0x5678,
        byte deviceClass = 0x08, UsbDeviceSpeed speed = UsbDeviceSpeed.High, byte interfaces = 1)
    {
        _devices.Add(new UsbipDeviceRecord
        {
            Path = $"/sys/devices/usb1/{busId}",
            BusId = busId,
            BusNum = 1,
            DevNum = (uint)_devices.Count + 2,
            Speed = speed,
            VendorId = vendorId,
            ProductId = productId,
            BcdDevice = 0x0100,
            DeviceClass = deviceClass,
            DeviceSubClass = 0,
            DeviceProtocol = 0,
            ConfigurationValue = 1,
            NumConfigurations = 1,
            NumInterfaces = interfaces,
            Interfaces = Enumerable.Range(0, interfaces)
                .Select(_ => new UsbipInterface(deviceClass, 0, 0))
                .ToArray(),
        });
        return this;
    }

    internal FakeUsbipServer AddDevice(UsbipDeviceRecord record)
    {
        _devices.Add(record);
        return this;
    }

    /// <summary>
    /// Removes an exported device. A real USB/IP server terminates every open import of a device it
    /// is no longer exporting (the kernel's stub detaches it), so this does the same: the client's
    /// relay for that device must see its upstream end, exactly as it would against vht-server.
    /// </summary>
    internal void RemoveDevice(string busId)
    {
        _devices.RemoveAll(d => d.BusId == busId);

        foreach (KeyValuePair<TcpClient, string> import in _importsByClient.ToArray())
        {
            if (string.Equals(import.Value, busId, StringComparison.Ordinal))
            {
                import.Key.Dispose();
            }
        }
    }

    internal void Start()
    {
        _listener.Start();
        _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            int id = Interlocked.Increment(ref _nextClientId);
            Interlocked.Increment(ref TotalAccepted);
            _clients[id] = client;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeClientAsync(client, ct).ConfigureAwait(false);
                }
                finally
                {
                    _importsByClient.TryRemove(client, out _);
                    _clients.TryRemove(id, out _);
                    client.Dispose();
                }
            }, CancellationToken.None);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        NetworkStream stream = client.GetStream();

        var header = new byte[UsbipProtocol.OpCommonSize];
        if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false))
        {
            return;
        }

        if (Stall)
        {
            // Hold the connection open and silent, so a driver attach would sit in `connecting`.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return;
        }

        ushort code = (ushort)((header[2] << 8) | header[3]);
        if (code == UsbipProtocol.OpReqDevList)
        {
            var reserved = new byte[sizeof(uint)];
            await ReadExactlyAsync(stream, reserved, ct).ConfigureAwait(false);
            byte[] reply = UsbipOpWriter.BuildDevListReply(
                _devices.Select(d => (d, d.BusId)).ToList());
            await stream.WriteAsync(reply, ct).ConfigureAwait(false);
        }
        else if (code == UsbipProtocol.OpReqImport)
        {
            var busIdField = new byte[UsbipProtocol.BusIdSize];
            await ReadExactlyAsync(stream, busIdField, ct).ConfigureAwait(false);
            string busId = ReadCString(busIdField);
            RequestedBusIds.Enqueue(busId);
            _importsByClient[client] = busId;

            OpStatus status = ImportStatuses.Count > 0
                ? ImportStatuses.Dequeue()
                : (busId.StartsWith("missing", StringComparison.Ordinal) ? OpStatus.NoDevice : OpStatus.Ok);

            UsbipDeviceRecord? device = _devices.FirstOrDefault(d => d.BusId == busId);
            var reply = new byte[UsbipProtocol.ImportReplySize];
            WriteU16(reply, 0, UsbipProtocol.Version);
            WriteU16(reply, 2, UsbipProtocol.OpRepImport);
            WriteU32(reply, 4, (uint)status);
            if (device is not null)
            {
                UsbipOpWriter.WriteDeviceRecord(
                    reply.AsSpan(UsbipProtocol.OpCommonSize, UsbipProtocol.UsbipUsbDeviceSize),
                    device);
            }
            await stream.WriteAsync(reply, ct).ConfigureAwait(false);

            if (status == OpStatus.Ok)
            {
                // Echo: proves the relay carries URB bytes in both directions, not just the op frames.
                await EchoAsync(stream, ct).ConfigureAwait(false);
            }
        }
        else if (code == UsbipProtocol.OpReqVhtDevInfo && AnswersDeviceInfoOp)
        {
            Interlocked.Increment(ref DeviceInfoRequests);
            var magicField = new byte[sizeof(uint)];
            if (!await ReadExactlyAsync(stream, magicField, ct).ConfigureAwait(false))
            {
                return;
            }

            await stream.WriteAsync(BuildDeviceInfoReply(Nicknames), ct).ConfigureAwait(false);
        }

        // Any other code — including the extension on a peer that models a stock usbipd — falls out
        // of this method and the connection is disposed, which is what usbipd does with an unknown op.
    }

    /// <summary>
    /// Builds an OP_REP_VHT_DEVINFO reply straight from the documented layout, so the client's own
    /// reader is exercised against an independent writer rather than against itself.
    /// </summary>
    private static byte[] BuildDeviceInfoReply(IReadOnlyDictionary<string, string> nicknames)
    {
        List<(string BusId, byte[] Name)> named = nicknames
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => (kv.Key, System.Text.Encoding.UTF8.GetBytes(kv.Value)))
            .ToList();

        var frame = new List<byte>(UsbipProtocol.OpCommonSize + 8 + named.Count * 33);
        var header = new byte[UsbipProtocol.OpCommonSize + 8];
        WriteU16(header, 0, UsbipProtocol.Version);
        WriteU16(header, 2, UsbipProtocol.OpRepVhtDevInfo);
        WriteU32(header, 4, 0);
        WriteU32(header, UsbipProtocol.OpCommonSize, UsbipProtocol.VhtDevInfoMagic);
        WriteU32(header, UsbipProtocol.OpCommonSize + 4, (uint)named.Count);
        frame.AddRange(header);

        foreach ((string busId, byte[] name) in named)
        {
            var entry = new byte[UsbipProtocol.VhtDeviceInfoEntryHeaderSize];
            var busIdField = new byte[UsbipProtocol.BusIdSize];
            System.Text.Encoding.ASCII.GetBytes(busId, busIdField);
            Array.Copy(busIdField, entry, UsbipProtocol.BusIdSize);
            entry[UsbipProtocol.BusIdSize] = (byte)name.Length;
            frame.AddRange(entry);
            frame.AddRange(name);
        }

        return frame.ToArray();
    }

    private static async Task EchoAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            if (n <= 0) return;

            try
            {
                await stream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static string ReadCString(byte[] field)
    {
        int end = Array.IndexOf(field, (byte)0);
        if (end < 0) end = field.Length;
        return System.Text.Encoding.ASCII.GetString(field, 0, end);
    }

    private static void WriteU16(byte[] target, int offset, ushort value)
    {
        target[offset] = (byte)(value >> 8);
        target[offset + 1] = (byte)value;
    }

    private static void WriteU32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    /// <summary>Forcibly drops every live connection — the "network was unplugged" simulation.</summary>
    internal void DropAllConnections()
    {
        foreach (TcpClient client in _clients.Values)
        {
            client.Dispose();
        }
        _clients.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        DropAllConnections();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Expected.
            }
        }
        _shutdown.Dispose();
    }
}

/// <summary>
/// In-memory virtual host controller. Models exactly what the client depends on: ports are allocated
/// 1..N, a port can be refused, imported devices can be listed, and a port can vanish (the remote
/// unplug case). <see cref="PortCeiling"/> exists to test how the client *reports* a driver ceiling,
/// not to impose one: nothing in the client consults it.
/// </summary>
internal sealed class FakeDeviceController : IDeviceController
{
    private readonly object _sync = new();
    private readonly Dictionary<int, ImportedDeviceSnapshot> _ports = new();
    private int _nextPort = 1;

    internal int? PortCeiling { get; set; }

    internal bool FailNextAttach { get; set; }

    internal string? FailNextAttachReason { get; set; }

    internal int AttachCallCount { get; private set; }

    internal int DetachCallCount { get; private set; }

    internal int StopAttachAttemptsCallCount { get; private set; }

    internal List<int> DetachedPorts { get; } = new();

    public string Description => "fake in-memory controller (test double)";

    public bool IsAvailable { get; set; } = true;

    public bool ImportedDevicesQuerySucceeds { get; set; } = true;

    public event EventHandler<ImportedDeviceSnapshot>? DeviceChanged;

    public IReadOnlyList<ImportedDeviceSnapshot> GetImportedDevices()
    {
        lock (_sync) return _ports.Values.OrderBy(p => p.Port).ToArray();
    }

    public bool TryGetImportedDevices(out IReadOnlyList<ImportedDeviceSnapshot> devices)
    {
        lock (_sync)
        {
            devices = ImportedDevicesQuerySucceeds
                ? _ports.Values.OrderBy(p => p.Port).ToArray()
                : Array.Empty<ImportedDeviceSnapshot>();
            return ImportedDevicesQuerySucceeds;
        }
    }

    public DeviceAttachOutcome Attach(string host, string service, string busId, string? serial, bool onceOnly = false)
    {
        lock (_sync)
        {
            AttachCallCount++;
            if (FailNextAttach)
            {
                FailNextAttach = false;
                return DeviceAttachOutcome.Failed(FailNextAttachReason ?? "the controller refused the attach (test)");
            }

            if (PortCeiling is int ceiling && _ports.Count >= ceiling)
            {
                return DeviceAttachOutcome.Failed($"the driver refused: port ceiling of {ceiling} reached (test)");
            }

            int port = _nextPort++;
            var snapshot = new ImportedDeviceSnapshot
            {
                Port = port,
                BusId = busId,
                Host = host,
                Service = service,
                LocationHash = (uint)port * 0x9E3779B1u,
                VendorId = 0x1234,
                ProductId = 0x5678,
            };
            _ports[port] = snapshot;
            DeviceChanged?.Invoke(this, snapshot);
            return DeviceAttachOutcome.Ok(port, snapshot.LocationHash);
        }
    }

    public bool Detach(int port)
    {
        lock (_sync)
        {
            DetachCallCount++;
            DetachedPorts.Add(port);
            return _ports.Remove(port);
        }
    }

    public int DetachAll()
    {
        lock (_sync)
        {
            int count = _ports.Count;
            _ports.Clear();
            return count;
        }
    }

    public int StopAttachAttempts(string host, string service, string busId)
    {
        lock (_sync)
        {
            StopAttachAttemptsCallCount++;
            return 0;
        }
    }

    public PortCapacityReport MeasurePortCapacity(int maximumToTry = 64)
    {
        lock (_sync)
        {
            int allocated = Math.Min(maximumToTry, PortCeiling ?? maximumToTry);
            return new PortCapacityReport
            {
                Allocated = allocated,
                CeilingReached = PortCeiling is not null,
                Detail = PortCeiling is null ? "fake controller has no ceiling" : $"fake ceiling {PortCeiling}",
            };
        }
    }

    /// <summary>Simulates the driver reporting a port gone (remote device unplugged).</summary>
    internal void RemovePortExternally(int port)
    {
        lock (_sync)
        {
            _ports.Remove(port);
        }
    }

    /// <summary>Ports currently attached, for assertions.</summary>
    internal IReadOnlyList<int> Ports
    {
        get
        {
            lock (_sync) return _ports.Keys.OrderBy(p => p).ToArray();
        }
    }

    public void Dispose()
    {
    }
}
