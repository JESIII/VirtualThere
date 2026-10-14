using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Vht.Core.Capture;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Protocol;
using Vht.Core.Server;
using Vht.Interop;
using Vht.Interop.Devices;

namespace Vht.Server.Devices;

/// <summary>
/// Enumerates this machine's USB devices with SetupAPI, reads their descriptors through
/// WinUSB, and reports — per device — whether the server can actually claim it.
/// </summary>
/// <remarks>
/// <para>
/// Enumeration is deliberately read-only in the driver-binding sense: it opens each device's
/// device interface to read descriptors, which never re-selects a driver, and it reports a
/// device it cannot open instead of trying to take it (ARCHITECTURE.md §4). The only code in
/// the product that changes a binding is <see cref="WinUsbRebinder"/>, behind an explicit
/// opt-in.
/// </para>
/// <para>
/// The classification rules are the v1 capture policy:
/// <list type="bullet">
///   <item>Keyboard and mouse class devices are <see cref="CaptureState.UnsafeToExport"/> /
///   <see cref="CaptureReason.SystemCritical"/> — exporting the input the console needs is the
///   one failure that can lock an operator out of the machine.</item>
///   <item>USB network adapters are refused while
///   <c>capture.refuse_management_nic</c> is set, because exporting the link the export runs
///   over cuts the session.</item>
///   <item>Everything else that opens on WinUSB is capturable; everything else that does not is
///   <see cref="CaptureState.CaptureUnsupported"/> with the precise
///   <see cref="CaptureReason"/> (a driver-holds-device device is offered to the rebind path
///   only when the operator opted in).</item>
/// </list>
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UsbDeviceEnumerator : IDeviceEnumerator
{
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_FILE_NOT_FOUND = 2;

    /// <summary>String-descriptor read cap: the protocol allows 255, strings are far shorter.</summary>
    private const int MaxStringDescriptorLength = 255;

    private readonly ServerOptions _options;
    private readonly ILogger _log;
    private long _generation;

    public UsbDeviceEnumerator(ServerOptions options, ILogger log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public long Generation => Volatile.Read(ref _generation);

    /// <summary>Bumps the generation counter; the hotplug watcher calls this on every event.</summary>
    public long BumpGeneration() => Interlocked.Increment(ref _generation);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<UsbDevice>> EnumerateAsync(CancellationToken ct) =>
        ValueTask.FromResult(Enumerate());

    /// <summary>Synchronous enumeration, for the CLI and the tests.</summary>
    public IReadOnlyList<UsbDevice> Enumerate()
    {
        var found = new List<UsbDevice>();
        var usedBusIds = new HashSet<string>(StringComparer.Ordinal);

        Guid interfaceClass = Guids.DeviceInterfaceUsbDevice;
        nint set = SetupApi.SetupDiGetClassDevs(
            in interfaceClass, null, 0, SetupApi.DIGCF_PRESENT | SetupApi.DIGCF_DEVICEINTERFACE);

        if (set == new nint(-1))
        {
            int error = Marshal.GetLastWin32Error();
            _log.Write(LogLevel.Error, LogEvent.DeviceEnumerated,
                $"SetupDiGetClassDevs failed (win32 {error}); no devices enumerated",
                new Dictionary<string, object?> { ["win32_error"] = error });
            return found;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SetupApi.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SetupApi.SP_DEVICE_INTERFACE_DATA>(),
                };

                if (!SetupApi.SetupDiEnumDeviceInterfaces(set, 0, in interfaceClass, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ERROR_NO_MORE_ITEMS)
                    {
                        _log.Write(LogLevel.Warn, LogEvent.DeviceEnumerated,
                            $"SetupDiEnumDeviceInterfaces stopped at index {index} (win32 {error})",
                            new Dictionary<string, object?> { ["win32_error"] = error });
                    }
                    break;
                }

                UsbDevice? device = ReadOne(set, ref interfaceData, usedBusIds);
                if (device is not null)
                    found.Add(device);
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    private UsbDevice? ReadOne(
        nint set,
        ref SetupApi.SP_DEVICE_INTERFACE_DATA interfaceData,
        HashSet<string> usedBusIds)
    {
        // Ask for the required detail size, then read the interface path and the devnode in one
        // call. The devnode is what CfgMgr32 needs for the instance id, which is the device's
        // stable identity across hotplug cycles.
        _ = SetupApi.SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, 0, 0, out int requiredSize, 0);
        if (requiredSize <= 0)
            requiredSize = 1024;
        nint detail = Marshal.AllocHGlobal(requiredSize);
        int devInfoSize = Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>();
        nint devInfoBuffer = Marshal.AllocHGlobal(devInfoSize);
        try
        {
            // cbSize is the only field the caller must fill in, and it is not the managed
            // struct size: it is 4 + pointer alignment (6 on x86, 8 on x64).
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            var devInfo = new SetupApi.SP_DEVINFO_DATA { cbSize = devInfoSize };
            Marshal.StructureToPtr(devInfo, devInfoBuffer, false);

            if (!SetupApi.SetupDiGetDeviceInterfaceDetail(
                    set, ref interfaceData, detail, requiredSize, out _, devInfoBuffer))
            {
                int error = Marshal.GetLastWin32Error();
                _log.Write(LogLevel.Detail, LogEvent.DeviceEnumerated,
                    $"SetupDiGetDeviceInterfaceDetail failed (win32 {error}); device skipped",
                    new Dictionary<string, object?> { ["win32_error"] = error });
                return null;
            }

            devInfo = Marshal.PtrToStructure<SetupApi.SP_DEVINFO_DATA>(devInfoBuffer);
            string devicePath = Marshal.PtrToStringUni(detail + 4) ?? string.Empty;
            string instancePath = InstanceId(devInfo.DevInst);
            string className = ReadProperty(set, ref devInfo, SetupApi.SPDRP_CLASS);
            string friendlyName = ReadProperty(set, ref devInfo, SetupApi.SPDRP_FRIENDLYNAME);
            string service = ReadProperty(set, ref devInfo, SetupApi.SPDRP_DRIVER);

            (string busId, uint devNum) = BusIdDerivation.FromInstancePath(instancePath);
            if (!usedBusIds.Add(busId))
            {
                // Two devices hashed to the same id: keep both visible by suffixing the port.
                int attempt = 2;
                string candidate;
                do
                {
                    candidate = $"{busId}-{attempt++}";
                }
                while (!usedBusIds.Add(candidate) && attempt < 64);

                busId = candidate;
            }

            return Build(devicePath, instancePath, className, friendlyName, service, busId, devNum);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warn, LogEvent.DeviceEnumerated,
                $"enumeration of one device failed: {ex.Message}");
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
            Marshal.FreeHGlobal(devInfoBuffer);
        }
    }

    private UsbDevice Build(
        string devicePath,
        string instancePath,
        string className,
        string friendlyName,
        string service,
        string busId,
        uint devNum)
    {
        UsbDevice Role(
            CaptureState state,
            CaptureReason reason,
            string detail,
            WinUsb.USB_DEVICE_DESCRIPTOR descriptor = default,
            uint speed = UsbIp.SpeedUnknown,
            string? manufacturer = null,
            string? product = null,
            string serial = "",
            IReadOnlyList<UsbIpInterface>? interfaces = null,
            IReadOnlyList<EndpointDescriptor>? endpoints = null) => new()
            {
                Key = new DeviceKey(busId, instancePath),
                InstancePath = instancePath,
                BusNum = BusIdDerivation.BusNum,
                DevNum = devNum,
                Speed = speed,
                VendorId = descriptor.idVendor != 0 ? descriptor.idVendor : FallbackVendorId(instancePath),
                ProductId = descriptor.idProduct != 0 ? descriptor.idProduct : FallbackProductId(instancePath),
                BcdDevice = descriptor.bcdDevice,
                DeviceClass = descriptor.bDeviceClass,
                DeviceSubClass = descriptor.bDeviceSubClass,
                DeviceProtocol = descriptor.bDeviceProtocol,
                ConfigurationValue = 1,
                NumConfigurations = descriptor.bNumConfigurations == 0 ? (byte)1 : descriptor.bNumConfigurations,
                Manufacturer = manufacturer ?? string.Empty,
                Product = product ?? friendlyName,
                Serial = serial,
                Interfaces = interfaces ?? Array.Empty<UsbIpInterface>(),
                Endpoints = endpoints ?? Array.Empty<EndpointDescriptor>(),
                CaptureState = state,
                CaptureReason = reason,
                CaptureDetail = detail,
                DevicePath = devicePath,
            };

        // ---- refuse-export rules (never a licence or a count; all three are physical safety) --
        if (IsClass(className, "Keyboard") || IsClass(className, "Mouse"))
        {
            return Role(CaptureState.UnsafeToExport, CaptureReason.SystemCritical,
                $"{className} class device; exporting the console input device is never allowed");
        }

        if (IsClass(className, "Net") && _options.RefuseManagementNic)
        {
            return Role(CaptureState.UnsafeToExport, CaptureReason.ManagementNetwork,
                "USB network adapter refused because capture.refuse_management_nic is set: " +
                "exporting a NIC can cut the link the export runs over");
        }

        // ---- capture probe: open the interface read-only and read descriptors ----------------
        WinUsbIo? io = WinUsbIo.Open(devicePath, out int openError);
        if (io is null)
        {
            CaptureReason reason = openError switch
            {
                ERROR_ACCESS_DENIED => CaptureReason.DriverHoldsDevice,
                ERROR_FILE_NOT_FOUND => CaptureReason.NoWinUsbInterface,
                _ => CaptureReason.DriverHoldsDevice,
            };

            bool rebindOffered = _options.RebindEnabled && _options.CaptureMode is "auto" or "prebound-only";

            return Role(CaptureState.CaptureUnsupported, reason,
                reason == CaptureReason.DriverHoldsDevice
                    ? $"no WinUSB handle (win32 {openError}); the function driver holds this device" +
                      (rebindOffered
                          ? " and capture.rebind.enabled offers the P2 rebind path"
                          : " — set capture.rebind.enabled to offer the P2 rebind path")
                    : $"device interface could not be opened (win32 {openError})");
        }

        try
        {
            bool haveDescriptor = io.TryGetDeviceDescriptor(out WinUsb.USB_DEVICE_DESCRIPTOR descriptor);

            uint speed = UsbIp.SpeedUnknown;
            if (io.TryGetSpeed(out uint winUsbSpeed))
                speed = MapSpeed(winUsbSpeed);

            string manufacturer = haveDescriptor
                ? ReadStringDescriptor(io, descriptor.iManufacturer)
                : string.Empty;
            string product = haveDescriptor
                ? ReadStringDescriptor(io, descriptor.iProduct)
                : string.Empty;
            string serial = haveDescriptor
                ? ReadStringDescriptor(io, descriptor.iSerialNumber)
                : string.Empty;

            if (string.IsNullOrEmpty(serial))
            {
                int lastSlash = instancePath.LastIndexOf('\\');
                serial = lastSlash >= 0 && lastSlash + 1 < instancePath.Length
                    ? instancePath[(lastSlash + 1)..]
                    : string.Empty;
            }

            var interfaces = new List<UsbIpInterface>(1);
            var endpoints = new List<EndpointDescriptor>();
            if (io.TryGetInterface(0, out WinUsb.USB_INTERFACE_DESCRIPTOR interfaceDescriptor,
                    out IReadOnlyList<WinUsb.WINUSB_PIPE_INFORMATION> pipes))
            {
                interfaces.Add(new UsbIpInterface
                {
                    InterfaceClass = interfaceDescriptor.bInterfaceClass,
                    InterfaceSubClass = interfaceDescriptor.bInterfaceSubClass,
                    InterfaceProtocol = interfaceDescriptor.bInterfaceProtocol,
                });

                foreach (WinUsb.WINUSB_PIPE_INFORMATION pipe in pipes)
                {
                    endpoints.Add(new EndpointDescriptor
                    {
                        Address = pipe.PipeId,
                        MaxPacketSize = pipe.MaximumPacketSize,
                        Interval = pipe.Interval,
                    });
                }
            }

            if (string.Equals(service, "usbhub", StringComparison.OrdinalIgnoreCase))
            {
                return Role(CaptureState.CaptureUnsupported, CaptureReason.NoWinUsbInterface,
                    "device is a USB hub, not an exportable function device",
                    descriptor, speed, manufacturer, product, serial, interfaces, endpoints);
            }

            if (!haveDescriptor)
            {
                // WinUSB answered but could not produce a device descriptor. The device is still
                // claimable (Windows reported its speed and pipes), so it stays exportable, with
                // the missing identity re-derived from the instance path.
                _log.Write(LogLevel.Warn, LogEvent.DeviceEnumerated,
                    $"{busId}: WinUSB could not read the device descriptor; identity falls back to the instance path");
            }

            return Role(
                CaptureState.Capturable, CaptureReason.None, string.Empty,
                descriptor, speed,
                manufacturer, string.IsNullOrWhiteSpace(product) ? friendlyName : product,
                serial, interfaces, endpoints);
        }
        finally
        {
            io.Dispose();
        }
    }

    private ushort FallbackVendorId(string instancePath) =>
        BusIdDerivation.TryParseVidPid(instancePath, out ushort vid, out _) ? vid : (ushort)0;

    private ushort FallbackProductId(string instancePath) =>
        BusIdDerivation.TryParseVidPid(instancePath, out _, out ushort pid) ? pid : (ushort)0;

    private static bool IsClass(string className, string expected) =>
        string.Equals(className, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a <c>WINUSB_DEVICE_SPEED</c> value onto the USB/IP speed codes.</summary>
    public static uint MapSpeed(uint winUsbSpeed) => winUsbSpeed switch
    {
        1 => UsbIp.SpeedLow,
        2 => UsbIp.SpeedFull,
        3 => UsbIp.SpeedHigh,
        4 => UsbIp.SpeedSuper,
        _ => UsbIp.SpeedUnknown,
    };

    /// <summary>
    /// Reads a UTF-16 string descriptor over a control transfer on the default pipe. Returns an
    /// empty string for index 0 (no string) and on any failure: a missing product string is a
    /// cosmetic gap, never a reason to refuse a device.
    /// </summary>
    public static string ReadStringDescriptor(WinUsbIo io, byte index, ushort languageId = 0x0409)
    {
        if (index == 0) return string.Empty;

        var setup = new WinUsb.WINUSB_SETUP_PACKET
        {
            RequestType = 0x80,                      // device-to-host, standard, device
            Request = 0x06,                          // GET_DESCRIPTOR
            Value = (ushort)((0x03 << 8) | index),   // STRING descriptor
            Index = languageId,
            Length = MaxStringDescriptorLength,
        };

        var buffer = new byte[MaxStringDescriptorLength];
        try
        {
            (int status, int transferred) =
                io.ControlTransferAsync(setup, buffer, CancellationToken.None).GetAwaiter().GetResult();

            if (status != 0 || transferred < 4) return string.Empty;

            int declared = buffer[0];
            if (declared < 4 || declared > transferred) declared = transferred;

            // bLength, bDescriptorType, then UTF-16LE code units.
            return Encoding.Unicode.GetString(buffer, 2, declared - 2).TrimEnd('\0');
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string InstanceId(uint devInst, uint flags = 0)
    {
        if (CfgMgr32.CM_Get_Device_ID_Size(out int length, devInst, flags) != CfgMgr32.CR_SUCCESS || length <= 0)
            return string.Empty;

        var buffer = new char[length + 1];
        if (CfgMgr32.CM_Get_Device_IDW(devInst, buffer, buffer.Length, flags) != CfgMgr32.CR_SUCCESS)
            return string.Empty;

        return new string(buffer).TrimEnd('\0');
    }

    private static string ReadProperty(nint set, ref SetupApi.SP_DEVINFO_DATA devInfo, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupApi.SetupDiGetDeviceRegistryProperty(
                set, ref devInfo, property, out _, buffer, buffer.Length, out int required) || required <= 0)
            return string.Empty;

        int bytes = Math.Min(required, buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, bytes).TrimEnd('\0');
    }

    /// <summary>
    /// The recon's enumeration line and the capture verdict for every device, as one string —
    /// the body of <c>vht list</c> and of the control plane's <c>devices.list</c>.
    /// </summary>
    public static string DescribeAll(IReadOnlyList<UsbDevice> devices)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Found {devices.Count} USB device(s)");
        foreach (UsbDevice device in devices)
            builder.Append('\n').Append(device.Describe());
        return builder.ToString();
    }
}
