# Troubleshooting

Symptom -> cause -> fix. Every entry here is a real message or behaviour from this codebase, not a
guess. Versions: v0.1.0, Windows 11, .NET SDK 10.0.402.

---

### `server is not answering: no answer from \\.\pipe\vht-server within 10s; is the service running?`

The CLI could not open the control pipe. Either no server is running, or it is running under a
different account than your shell (the pipe is ACL'd to Administrators and the interactive user).

* Start one: `vht serve` (foreground) or `vht start` (service; elevated).
* Check the service: `vht service-status`.
* Override the pipe: `vht status --pipe <name>` (must match `control_plane.pipe_name`).

### `vht list --local` shows `state=CaptureUnsupported reason=driver_holds_device`

The device is owned by its own function driver, so the server cannot get a WinUSB handle (the message
names the Win32 error, e.g. `win32 8` or `win32 31`). This is normal for keyboards, mice, disks,
webcams and internal devices on a running desktop - on the machine used to write this, all 13 USB
devices were in this state, so an unelevated `vht list` legitimately exports nothing.

Options, in order of preference:

1. Export a device that is already free (many vendor-specific dev boards bind to WinUSB).
2. Run elevated and set `capture.mode = "auto"` with `capture.rebind.enabled = true` (or
   `vht serve --rebind`). This offers the rebind path, which **changes which driver owns the device**
   - do not do this to your only input device.
3. Accept it as a documented limitation (README "Known limitations" 8). A server-side filter driver
   is future work.

### `the usbip-win2 UDE host-controller driver (GUID_DEVINTERFACE_USBIP_VHCI) is not installed`

Client side: no virtual host controller, so no device can be presented. Install the upstream
`usbip-win2` 0.9.8.1 package — see `docs/INSTALL.md` step 4. Verify the SHA-256 first. The client
fails closed here on purpose: it will not attach through some other path.

### `attach` refused: "the loopback bridge is not running"

The client refuses to attach a device to a remote host directly, because that would put plain USB/IP
on the wire and bypass the transport layer entirely. Run `vht-client serve` (which starts the
loopback bridge on `127.0.0.1:3240`) and attach through it, or use `vht-tray --mode client`.

### `attach` refused: peer is not on `127.0.0.1`

Same rule, `UdeDeviceController.Attach` level: the UDE driver must connect to the local bridge, never
to a remote address. Do not "fix" this by changing the host - it is the security boundary.

### `peer '<name>' is configured with use_tls = true, but the only transport compiled in is plain TCP`

`vht-client peers add --tls` marks a peer as TLS, but v0.1.0 ships plain TCP only (`Vht.Transport`
is not wired into the client yet). Either add the peer without `--tls`, or wait for the wiring. The
client refuses to silently downgrade - that is deliberate.

### A device enumerated, but there is no data / the stream stutters or never starts

Check the transfer type. **Isochronous classes are not supported in v0.1.0**: USB audio streaming
(UAC1/UAC2) and UVC webcam video will not carry data, although the device may enumerate. Control,
bulk and interrupt classes (storage, HID, CDC serial, printers, vendor-specific) are the supported
path. See the matrix in `docs/CLIENT.md` section 3.

### `vht-client capacity` reports fewer ports than expected

`ExpectedMinimumPorts` (default 8) only turns a low measurement into a WARNING; nothing is enforced.
The measurement is the driver's real per-controller port ceiling. To go higher, add a second
controller instance rather than changing the client.

### `install` / `uninstall` / `start` fails with access denied

Service registration and device capture require an elevated shell. The command says so and stops;
there is no partial install. Re-run from an Administrator shell.

### The server binds but the export list is empty, and no client ever sees anything

Work through, in order:

1. `vht list --local` - are any devices in a capturable state? (see the `driver_holds_device` entry
   above).
2. `vht status` - does the listener line show the address:port you expect, and does it say
   `PlainTcp`?
3. Firewall: is that port reachable from the client? `Test-NetConnection <server> -Port 3240`.
4. Client: is the peer really configured? `vht-client peers list`, then `vht-client list`.
5. Bind address: `transport.plaintext.address = "0.0.0.0"` is needed for remote clients;
   `127.0.0.1` is local-only.

### Hotplug does not react immediately (takes ~3 seconds)

On hosts where device-change notifications are unavailable, the server falls back to a periodic
rescan of about 3 s. It is a deliberate, documented fallback, not a hang. This was the live path on
the development machine (both CM notification forms returned `CR 31`).

### `vht` or `vht-client` prints a stale `transport:` line in `status`

`vht-client status` prints what is compiled in, not what is configured: `plain TCP (Vht.Transport's
TLS/LZ4 layer is not wired into the client yet)`. If you expected TLS, see the TLS entry above.

### Discovery finds nothing (no auto-find)

DNS-SD browsing is not wired into the client in v0.1.0; the client reports discovery as disabled and
you add peers manually (`vht-client peers add host:port`). The service type is `_vht._tcp` and
deliberately not the reference product's type, so the two products never see each other's clients.

### The tray window is blank, or a dialog never appears

The tray is a separate process talking to the service over the pipe. If the server is not running,
the window shows an empty list rather than a protocol window. Start the server first
(`vht serve` or `vht start`), then re-open the tray cut. If a prompt dialog does not appear, check
that the service is answering: `vht status`.

### Something is attached and I cannot reconnect to it

A device can be claimed by one client at a time. From the server: `vht kick <busid>` force-detaches
the holder. From the client: `vht-client detach --all`. A crashed client releases its claim when its
connection ends.

### Build or test failures

* Use the SDK the project targets: `dotnet --version` must be `10.0.x`.
* `TreatWarningsAsErrors=true` means a warning fails the build - that is intentional.
* Whole solution: `dotnet build VirtualThere.slnx -c Release` then
  `dotnet test VirtualThere.slnx -c Release`. Expected: 0 warnings, 340 tests passing.
* The UI harness projects (`src/Vht.Ui/harness/**`) are test-only drivers, not shipped components.
