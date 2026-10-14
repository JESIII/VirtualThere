// SPDX-License-Identifier: Apache-2.0
//
// namespace Vht.Interop.Ude — the client's P/Invoke surface for the usbip-win2 UDE host-controller
// driver. ARCHITECTURE.md §3.3 requires exactly this: talk to the driver's IOCTL ABI directly,
// re-declaring the structs and CTL_CODEs from the upstream BSD-2-Clause headers, never shelling out
// to usbip.exe and never parsing its text output.
//
// Upstream provenance (see third_party/usbip-win2-driver/ for the pinned package + licence):
//   vadimgrn/usbip-win2, BSD-2-Clause
//   include/usbip/vhci.h  — GUID_DEVINTERFACE_USBIP_VHCI, ioctl::function, ioctl structs
//   include/usbip/consts.h — BUS_ID_SIZE, SERIAL_BUFSZ, op_status_t, tcp_port
//   include/usbip/proto.h  — usbip::header (48 bytes), request_type
//
// NOTE ON PLACEMENT: ARCHITECTURE.md App. A puts UDE bindings in `src/Vht.Interop/Ude/` and flags
// `src/Vht.Interop/**` as a hotspot owned by card server-workstream (additive-only: new files in your own
// sub-namespace). That project does not exist yet, so these files live under this card's own write
// scope (`src/Vht.Client/Interop/Ude/`) with the agreed namespace `Vht.Interop.Ude`. Moving them to
// `src/Vht.Interop/Ude/` is a `git mv` with no code change and is noted in the handoff; nothing here
// edits or shadows a file the server card owns.

namespace Vht.Interop.Ude;

/// <summary>
/// Constants and IOCTL codes for the UDE host-controller driver, re-declared from upstream
/// include/usbip/vhci.h and include/usbip/consts.h (BSD-2-Clause, attribution above).
/// </summary>
internal static class UdeConstants
{
    /// <summary>DEFINE_GUID(GUID_DEVINTERFACE_USBIP_VHCI) — the device interface the driver exposes.</summary>
    internal static readonly Guid DeviceInterfaceVhci = new("B4030C06-DC5F-4FCC-87EB-E5515A0935C0");

    /// <summary>BUS_ID_SIZE — size of the busid field in every IOCTL struct and in OP_REQ_IMPORT.</summary>
    internal const int BusIdSize = 32;

    /// <summary>SERIAL_BUFSZ — size of the serial field in the IOCTL structs.</summary>
    internal const int SerialBufSize = 16;

    /// <summary>NI_MAXHOST in ws2def.h — size of the host field. The driver's struct says 1025.</summary>
    internal const int HostBufSize = 1025;

    /// <summary>NI_MAXSERV — size of the service (port-as-string) field.</summary>
    internal const int ServiceBufSize = 32;

    /// <summary>Protocol version the driver speaks (USBIP_VERSION 0x0111); it will refuse anything else.</summary>
    internal const int UsbipVersion = 0x111;

    // CTL_CODE(FILE_DEVICE_UNKNOWN, id, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)
    private const uint FileDeviceUnknown = 0x0000_0022;
    private const uint MethodBuffered = 0;
    private const uint FileReadData = 0x0001;
    private const uint FileWriteData = 0x0002;
    private const int FunctionPluginHardware = 0x800;

    /// <summary>Re-declaration of the CTL_CODE macro, so every code below is derived, never typed in.</summary>
    internal static uint CtlCode(int function) =>
        (FileDeviceUnknown << 16) | ((FileReadData | FileWriteData) << 14) | ((uint)function << 2) | MethodBuffered;

    // ioctl::function values (12-bit function field), upstream include/usbip/vhci.h.
    internal const int PluginHardware = FunctionPluginHardware;              // 0x800
    internal const int PlugoutHardware = FunctionPluginHardware + 1;         // 0x801
    internal const int GetImportedDevices = FunctionPluginHardware + 2;      // 0x802
    internal const int SetPersistent = FunctionPluginHardware + 3;           // 0x803
    internal const int GetPersistent = FunctionPluginHardware + 4;           // 0x804
    internal const int StopAttachAttempts = FunctionPluginHardware + 5;      // 0x805
    internal const int PluginHardwareOnce = FunctionPluginHardware + 6;      // 0x806
    internal const int PlugoutHardwareAndReattach = FunctionPluginHardware + 7; // 0x807 (driver internal)

    internal static readonly uint IoctlPluginHardware = CtlCode(PluginHardware);               // 0x0022E000
    internal static readonly uint IoctlPlugoutHardware = CtlCode(PlugoutHardware);             // 0x0022E004
    internal static readonly uint IoctlGetImportedDevices = CtlCode(GetImportedDevices);       // 0x0022E008
    internal static readonly uint IoctlSetPersistent = CtlCode(SetPersistent);                 // 0x0022E00C
    internal static readonly uint IoctlGetPersistent = CtlCode(GetPersistent);                 // 0x0022E010
    internal static readonly uint IoctlStopAttachAttempts = CtlCode(StopAttachAttempts);       // 0x0022E014
    internal static readonly uint IoctlPluginHardwareOnce = CtlCode(PluginHardwareOnce);       // 0x0022E018

    /// <summary>enum { PORT_ALL_CLOSEONLY = -2, PORT_ALL } — plugout_hardware.port special values.</summary>
    internal const int PortAllCloseOnly = -2;

    internal const int PortAll = -1;

    /// <summary>
    /// pack_attach_flags(once, wsk_events) from upstream vhci.h: bit 0 = wsk_events, bit 1 = once.
    /// Unknown flag bits are ignored by the driver, so we only ever set these two.
    /// </summary>
    internal static uint PackAttachFlags(bool once, bool wskEvents) =>
        ((once ? 1u : 0u) << 1) | (wskEvents ? 1u : 0u);
}

/// <summary>vhci::state — the driver's imported-device state machine.</summary>
internal enum UdeDeviceState
{
    Unplugged = 0,
    Connecting = 1,
    Connected = 2,
    Plugged = 3,
    Disconnected = 4,
    Unplugging = 5,
}

/// <summary>One entry of a GET_IMPORTED_DEVICES reply.</summary>
internal sealed record UdeImportedDevice
{
    internal required int Port { get; init; }
    internal required uint LocationHash { get; init; }
    internal required string BusId { get; init; }
    internal required string Service { get; init; }
    internal required string Host { get; init; }
    internal uint DevId { get; init; }
    internal uint Speed { get; init; }
    internal ushort VendorId { get; init; }
    internal ushort ProductId { get; init; }
    internal string Serial { get; init; } = string.Empty;

    /// <summary>The (host, service) pair identifies which peer an attached port belongs to.</summary>
    internal string Endpoint => $"{Host}:{Service}";

    public override string ToString() =>
        $"port {Port} {BusId} {Host}:{Service} devid {DevId} [{VendorId:x4}:{ProductId:x4}]";
}
