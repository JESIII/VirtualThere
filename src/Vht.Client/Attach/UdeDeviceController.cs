// SPDX-License-Identifier: Apache-2.0
//
// The real virtual host controller: the usbip-win2 UDE driver, driven through its IOCTL ABI.
//
// ARCHITECTURE.md §3.3: no shelling out to usbip.exe, no parsing its text output — the client speaks
// the driver's IOCTL ABI. §6.2: every attach is addressed to the loopback bridge, and this class
// refuses to do otherwise, because an attach pointing at a remote host would put plaintext USB/IP on
// the network and bypass TLS/LZ4 entirely.

using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using Vht.Client.Diagnostics;
using Vht.Interop.Ude;

namespace Vht.Client.Attach;

/// <summary>
/// Drives the UDE host-controller driver. One open handle for the whole client; every attach is one
/// IOCTL, so N devices are N IOCTLs on N ports with nothing shared to grow (§5).
/// </summary>
internal sealed class UdeDeviceController : IDeviceController
{
    /// <summary>The only host an attach may target: the bridge is loopback-only (§6.2).</summary>
    internal const string LoopbackHost = "127.0.0.1";

    private const int ErrorInsufficientBuffer = 122;
    private const int InitialReadCapacity = 8;
    private const int MaxReadAttempts = 4;

    private readonly ClientLog _log;
    private readonly SafeFileHandle? _handle;
    private readonly string? _unavailableReason;
    private ImportedDeviceLayout _layout = ImportedDeviceLayout.Natural;

    private bool _disposed;

    internal UdeDeviceController(ClientLog log)
    {
        _log = log;

        IReadOnlyList<string> paths;
        try
        {
            paths = UdeNative.EnumerateVhciInterfacePaths();
        }
        catch (Win32Exception ex)
        {
            _unavailableReason = $"could not enumerate the UDE device interface: {ex.Message}";
            _log.Error("ude", _unavailableReason, ex);
            return;
        }

        if (paths.Count == 0)
        {
            _unavailableReason =
                "the usbip-win2 UDE host-controller driver (GUID_DEVINTERFACE_USBIP_VHCI) is not installed. " +
                "Install the driver package described in docs/INSTALL.md " +
                "— without it no remote device can be presented to Windows.";
            _log.Warn("ude", _unavailableReason);
            return;
        }

        try
        {
            _handle = UdeNative.OpenForIoctl(paths[0]);
            _log.Info("ude", $"opened the virtual host controller at {paths[0]}");
        }
        catch (Win32Exception ex)
        {
            _unavailableReason = ex.Message;
            _log.Error("ude", _unavailableReason, ex);
        }
    }

    public string Description => _handle is { IsInvalid: false }
        ? "usbip-win2 UDE driver (usbip2_ude.sys) via IOCTL"
        : $"unavailable: {_unavailableReason ?? "unknown"}";

    public bool IsAvailable => _handle is { IsInvalid: false };

    public event EventHandler<ImportedDeviceSnapshot>? DeviceChanged;

    public DeviceAttachOutcome Attach(string host, string service, string busId, string? serial, bool onceOnly = false)
    {
        ThrowIfUnavailable();

        if (!string.Equals(host, LoopbackHost, StringComparison.Ordinal))
        {
            // §6.2/§6.3: the driver's socket must never be the remote hop.
            return DeviceAttachOutcome.Failed(
                $"refusing to attach {busId} to '{host}': the driver leg must target the loopback bridge " +
                $"({LoopbackHost}), otherwise plaintext USB/IP would leave the machine.");
        }

        byte[] buffer = UdeBufferCodec.BuildPluginHardware(host, service, busId, serial, wskEvents: false);
        uint code = onceOnly ? UdeConstants.IoctlPluginHardwareOnce : UdeConstants.IoctlPluginHardware;

        try
        {
            UdeNative.Ioctl(_handle!, code, buffer, buffer, $"plugin_hardware({busId})");
        }
        catch (Win32Exception ex)
        {
            _log.Error("ude", $"plugin_hardware failed for {busId} on {host}:{service}", ex);
            return DeviceAttachOutcome.Failed(ex.Message);
        }

        (int port, uint locationHash) = UdeBufferCodec.ParsePluginHardwareResult(buffer);
        if (port < 1)
        {
            // The driver reports "port >= 1 or zero if an error".
            return DeviceAttachOutcome.Failed(
                $"the driver allocated no port for {busId} (port={port}, location_hash=0x{locationHash:x8})");
        }

        _log.Info("ude", $"attached {busId} on virtual port {port} (location_hash 0x{locationHash:x8})");
        DeviceChanged?.Invoke(this, new ImportedDeviceSnapshot
        {
            Port = port,
            BusId = busId,
            Host = host,
            Service = service,
            LocationHash = locationHash,
        });
        return DeviceAttachOutcome.Ok(port, locationHash);
    }

    public bool Detach(int port)
    {
        ThrowIfUnavailable();
        byte[] buffer = UdeBufferCodec.BuildPlugoutHardware(port);
        try
        {
            UdeNative.Ioctl(_handle!, UdeConstants.IoctlPlugoutHardware, buffer, buffer, $"plugout_hardware({port})");
            _log.Info("ude", $"detached virtual port {port}");
            return true;
        }
        catch (Win32Exception ex)
        {
            _log.Warn("ude", $"plugout_hardware failed for port {port}: {ex.Message}");
            return false;
        }
    }

    public int DetachAll()
    {
        if (!IsAvailable) return 0;
        int count = GetImportedDevices().Count;
        byte[] buffer = UdeBufferCodec.BuildPlugoutHardware(UdeConstants.PortAll);
        try
        {
            UdeNative.Ioctl(_handle!, UdeConstants.IoctlPlugoutHardware, buffer, buffer, "plugout_hardware(all)");
            _log.Info("ude", $"detached all virtual ports ({count} were in use)");
        }
        catch (Win32Exception ex)
        {
            _log.Warn("ude", $"plugout_hardware(all) failed: {ex.Message}");
        }
        return count;
    }

    public int StopAttachAttempts(string host, string service, string busId)
    {
        ThrowIfUnavailable();
        byte[] buffer = UdeBufferCodec.BuildStopAttachAttempts(host, service, busId, _layout);
        try
        {
            UdeNative.Ioctl(_handle!, UdeConstants.IoctlStopAttachAttempts, buffer, buffer,
                $"stop_attach_attempts({busId})");
            return UdeBufferCodec.ParseStopAttachAttemptsCount(buffer, _layout);
        }
        catch (Win32Exception ex)
        {
            _log.Warn("ude", $"stop_attach_attempts failed for {busId}: {ex.Message}");
            return 0;
        }
    }

    public IReadOnlyList<ImportedDeviceSnapshot> GetImportedDevices()
    {
        if (!IsAvailable) return Array.Empty<ImportedDeviceSnapshot>();

        int capacity = InitialReadCapacity;
        for (int attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            byte[] buffer = new byte[UdeBufferCodec.GetImportedDevicesBufferSize(capacity, _layout)];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)buffer.Length);

            int returned;
            try
            {
                returned = UdeNative.Ioctl(_handle!, UdeConstants.IoctlGetImportedDevices, buffer, buffer,
                    "get_imported_devices");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorInsufficientBuffer)
            {
                // The driver's self-size field carries the size it needs; grow to fit and retry.
                uint required = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
                if (required <= (uint)buffer.Length || required > (uint)(UdeBufferCodec.GetImportedDevicesBufferSize(4096, _layout)))
                {
                    _log.Warn("ude", $"get_imported_devices asked for {required} bytes (buffer was {buffer.Length}); " +
                                     "that is not a usable size, giving up rather than retrying blindly");
                    return Array.Empty<ImportedDeviceSnapshot>();
                }

                capacity = (int)((required - UdeBufferCodec.GetImportedDevicesHeaderOffset)
                                 / (uint)ImportedDeviceLayout.Packed.Stride) + 1;
                continue;
            }
            catch (Win32Exception ex)
            {
                _log.Warn("ude", $"get_imported_devices failed: {ex.Message}");
                return Array.Empty<ImportedDeviceSnapshot>();
            }

            return DecodeImportedDevices(buffer, returned);
        }

        return Array.Empty<ImportedDeviceSnapshot>();
    }

    private IReadOnlyList<ImportedDeviceSnapshot> DecodeImportedDevices(byte[] buffer, int byteCount)
    {
        int payload = Math.Max(0, byteCount - UdeBufferCodec.GetImportedDevicesHeaderOffset);

        // The element count is not in the reply; it is implied by the byte count and the element
        // layout, which the driver never states. Try each candidate and keep the one that validates
        // (UdeBufferCodec.ParseImportedDevices does the field validation).
        foreach (ImportedDeviceLayout layout in ImportedDeviceLayout.Candidates)
        {
            int elements = payload / layout.Stride;
            if (elements == 0 && payload > 0)
            {
                continue;
            }

            try
            {
                (IReadOnlyList<UdeImportedDevice> devices, ImportedDeviceLayout detected) =
                    UdeBufferCodec.ParseImportedDevices(buffer, elements);
                _layout = detected;
                return devices.Select(d => new ImportedDeviceSnapshot
                {
                    Port = d.Port,
                    BusId = d.BusId,
                    Host = d.Host,
                    Service = d.Service,
                    LocationHash = d.LocationHash,
                    VendorId = d.VendorId,
                    ProductId = d.ProductId,
                    Serial = d.Serial,
                }).ToArray();
            }
            catch (UdeLayoutException)
            {
                // Try the next candidate layout.
            }
        }

        _log.Warn("ude", $"could not interpret a {byteCount}-byte get_imported_devices reply under either known layout");
        return Array.Empty<ImportedDeviceSnapshot>();
    }

    /// <summary>
    /// ARCHITECTURE.md §10 risk 3: the third-party driver's per-controller port ceiling is unverified.
    /// This measures it instead of assuming it, and it deliberately does NOT cap anything: the caller
    /// decides what to do with the number (the architecture's answer is "instantiate another
    /// controller", never "limit devices in our code").
    /// </summary>
    public PortCapacityReport MeasurePortCapacity(int maximumToTry = 64)
    {
        ThrowIfUnavailable();
        if (maximumToTry < 1) throw new ArgumentOutOfRangeException(nameof(maximumToTry));

        // A listener that accepts and then never answers holds the driver's attaches in `connecting`,
        // which is the state that occupies a port. That makes the probe measure port allocation
        // without needing real remote devices.
        var stall = new TcpListener(IPAddress.Loopback, 0);
        stall.Start();
        int stallPort = ((IPEndPoint)stall.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var accepted = new List<TcpClient>();

        Task acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient client = await stall.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                    lock (accepted) accepted.Add(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Expected on shutdown.
            }
        });

        var ports = new List<int>();
        string detail = "no refusal seen";
        bool ceilingReached = false;

        try
        {
            for (int i = 0; i < maximumToTry; i++)
            {
                // Busid must be plausible and alnum-ish; the driver only routes it back to us.
                string busId = $"capacityprobe{i}";
                DeviceAttachOutcome outcome = Attach(LoopbackHost, stallPort.ToString(), busId, serial: null, onceOnly: true);
                if (!outcome.Success)
                {
                    ceilingReached = true;
                    detail = $"refused at attempt {i + 1}: {outcome.FailureReason}";
                    break;
                }
                ports.Add(outcome.Port);
            }
        }
        finally
        {
            foreach (int port in ports)
            {
                try
                {
                    Detach(port);
                }
                catch (ClientException ex)
                {
                    _log.Warn("ude", $"capacity probe: detach of port {port} failed: {ex.Message}");
                }
            }
            DetachAll();

            cts.Cancel();
            acceptLoop.Wait(TimeSpan.FromSeconds(2));
            lock (accepted)
            {
                foreach (TcpClient client in accepted)
                {
                    client.Dispose();
                }
                accepted.Clear();
            }
            stall.Stop();
        }

        return new PortCapacityReport
        {
            Allocated = ports.Count,
            CeilingReached = ceilingReached,
            Detail = detail,
        };
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable)
        {
            throw new ClientAttachException(
                $"the virtual host controller is unavailable: {_unavailableReason ?? "unknown reason"}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (IsAvailable)
            {
                // Leave the machine as we found it: an orphaned virtual port would keep a remote
                // device claimed after the client exits.
                DetachAll();
            }
        }
        catch (Exception ex) when (ex is ClientException or Win32Exception)
        {
            _log.Warn("ude", $"detach-all on shutdown failed: {ex.Message}");
        }

        _handle?.Dispose();
    }
}
