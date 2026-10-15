# Troubleshooting

Symptom -> cause -> fix. Every entry here is a real message or behaviour from this codebase, not a
guess. Versions: v0.1.0, Windows 11, .NET SDK 10.0.402.

---

### `server is not answering: no answer from \\.\pipe\vht-server within 10s; is the service running?`

The CLI could not open the control pipe. Either no server is running, or it is running under a
different account than your shell. The pipe's DACL grants Local System, the built-in Administrators
and the interactive user read/write (`docs/UI.md` section 1), so "a different account" means an
account that is not in that set.

* Start one: `vht serve` (foreground) or `vht start` (service; elevated).
* Check the service: `vht service-status`.
* Override the pipe: `vht status --pipe <name>` (must match `control_plane.pipe_name`).

An **access denial does not read like this**: it throws
`System.UnauthorizedAccessException: Access to the path is denied` from
`NamedPipeClientStream.TryConnect`. This message means nothing answered. If a service is running and
you still see it, read the next entry - one of the two messages is wrong, and it may be this one.

### The worker is running (elevated, one consent prompt), yet the window says it is not

Symptom: `vht-server.exe` is open, **Start** was pressed, the single UAC prompt was accepted, the
elevated worker is running and serving - and the window's state pill stays on *Not running*, its
device list stays empty, and its connection line reads
`No answer from \\.\pipe\vht-server - ...`. Meanwhile `vht status --pipe vht-server` **fails from an
unelevated shell** with:

```
Unhandled exception. System.UnauthorizedAccessException: Access to the path is denied.
   at System.IO.Pipes.NamedPipeClientStream.TryConnect(Int32 timeout)
   at Vht.Server.Control.ControlClient.Send(JsonObject request)
```

while the identical command from an **elevated** shell prints the full status. That asymmetry is the
diagnosis: the pipe is there, and it is refusing the window.

Cause: a named pipe created with **no security descriptor** inherits the creating token's default
DACL and carries **no mandatory integrity label of its own**. What the kernel reported for the pipe
this failure was produced with - an **elevated** worker owning `\\.\pipe\vht-server`, read from an
unelevated process with `GetSecurityInfo`:

```
owner|group|dacl: O:BAG:<operator SID>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;BA)(A;;FR;;;WD)(A;;FR;;;AN)
control=0x8004 SE_SACL_PRESENT=False    mandatory label: (none)
open for read : OK      open for write: UnauthorizedAccessException: Access is denied.
```

**The DACL is what refused the window, and that is the measured cause.** The owner of a
High-integrity token is the **Administrators group**, so the operator's own account does not appear
in that DACL at all; the only ACE that matches the window's token is `Everyone`'s read-only one
(`FR`). The write is refused by the access check, which is a **permission** denial, not a missing
service.

The label is the second thing this fix sets, and it is a requirement of the design rather than the
mechanism that produced this report. An object with **no** label ACE is treated as Medium by Windows,
so no-write-up does not apply to the object above - but an object whose descriptor *does* carry a
label is subject to it, and a Medium-integrity window then cannot write to a High-labelled object
**whatever the DACL allows**. The explicit Medium label also makes the object's level independent of
the creating token, and no-write-up is the only thing that keeps a **Low**-integrity client out: such
a client is the interactive user, so the DACL alone would admit it.

Fix: the pipe is created with an explicit descriptor, which grants the interactive user read/write on
the pipe and lowers the object's mandatory label to Medium:

```
D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;<owner SID>)(A;;0x12019f;;;IU)S:(ML;;NW;;;ME)
```

That descriptor is `Vht.Interop.Control.NamedPipeSecurity`, used by the control plane of both roles
(`docs/UI.md` section 1). `0x12019f` is FILE_GENERIC_READ|FILE_GENERIC_WRITE - what a client of the
pipe needs, and no more.

If this class of failure ever comes back, ask the object rather than the log:

* With `logging.level = "detail"` the worker logs what the kernel reports for its own pipe, read back
  from the live handle: `control pipe security: O:...G:...D:(...)S:AI(ML;;NW;;;ME)
  label=S:AI(ML;;NW;;;ME)`. If the label is not Medium it logs a **warning** instead, and that
  warning is the thing to search for.
* The same read works from any unelevated process: `GetSecurityInfo` with
  `LABEL_SECURITY_INFORMATION` needs no privilege (the full SACL does, and is not needed here), and
  reading the DACL needs `READ_CONTROL` on the pipe, which the descriptor above grants.
* `Access is denied` **is** the answer in this situation. It does not mean "the service is not
  running".

For comparison, the same read against a **fixed** pipe (unelevated worker, same shell):

```
owner|group|dacl: O:<operator SID>G:<operator SID>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;<operator SID>)(A;;0x12019f;;;IU)S:AI(ML;;NW;;;ME)
control=0x8814 SE_SACL_PRESENT=True     mandatory label: 'S:AI(ML;;NW;;;ME)'
open for read : OK      open for write: OK
```

**The window's own message was a second, independent defect.** The window's client connects
asynchronously with a connect timeout, and every failure of that path - including an access denial -
arrived at the operator as *"no listener on \\.\pipe\vht-server - is the service running?"*. A pipe
that exists and refuses you was reported as a service that is not there, which is why this looked
like a startup problem for as long as it did. The wording now states both possibilities, and a real
denial reports `\\.\pipe\vht-server refused access: ...`. The CLI client never had this misdirection:
it connects synchronously with `PipeOptions.None` and surfaces the Win32 error it gets.

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
loopback bridge on `127.0.0.1:3240`) and attach through it, or use the client window (`vht-client.exe`).

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

### The management window is blank, or a dialog never appears

The window is a separate process talking to the privileged side over the pipe. If nothing is serving
the pipe, the window shows an empty list rather than an error. Press **Start** in the server window
(one consent prompt; the worker it starts binds the pipe), start the service with `vht start`, or run
`vht serve`; then press Refresh. If a prompt dialog does not appear, check that the control plane is
answering: `vht status`.

### Closing the server window did not stop the server

That is the designed behaviour: closing the window hides it to the notification area and the
privileged worker keeps serving. Use **Exit** (window button, or the notification-area icon's Exit
item) to stop the worker this window started, or, if the window process is gone, `vht-server
stop-server --pipe vht-server`. See `docs/UI.md` section 1.

### Something is attached and I cannot reconnect to it

A device can be claimed by one client at a time. From the server: `vht kick <busid>` force-detaches
the holder. From the client: `vht-client detach --all`. A crashed client releases its claim when its
connection ends.

### `xtask portable`: `dist/portable is in use and cannot be replaced safely.`

The full message, with the process and pid named:

```
xtask: dist/portable is in use and cannot be replaced safely.

  locked: G:\Repos\VirtualThere\dist\portable\vht-server.exe
  holder: vht-server.exe (pid 45184, G:\Repos\VirtualThere\dist\portable\vht-server.exe)

  A server or client started from this folder keeps its own executable open, so a
  publish would delete the folder and then fail on the locked file. Nothing has been
  deleted: the folder is byte-for-byte what it was. See docs/TROUBLESHOOTING.md.

  Stop the process and run the command again (elevated shell if it is elevated):
      taskkill /pid 45184 /t /f
```

`holder: not attributable to a VirtualThere process` appears instead when the lock is real but the
image path cannot be read (an elevated worker asked from an unelevated shell); the `locked:` lines
name the file, which is the actionable part.

Cause: Windows refuses to delete or overwrite the image file of a running process. `dist/portable` is
a program directory as much as it is an artifact directory - the documented portable workflow runs
`vht-server.exe` straight out of it, and a portable server started with **Start** runs a `--worker`
from the same folder - so this is the normal state of the machine, not an edge case.

`xtask portable` (and `xtask clean`, for `dist/portable` and `dist/installer`) now checks every file
in the folder *before* deleting anything: the folder's files are probed for a foreign handle, and the
candidate processes (`vht-server.exe`, `vht-client.exe`, `vht.exe`) are reported by name and pid when
their image lives in that folder - a copy running from `%ProgramFiles%` or from
`src/Vht.Server/bin/...` is not blamed for it. The new artifact set is assembled in
`dist/publish/portable` and swapped into place with two renames, so a failure at any point - this
refusal, a failed `dotnet publish`, a stuck swap - leaves the folder exactly as it was.

That is the whole point: the earlier behaviour deleted `dist/portable` first and *then* failed on the
locked file, which removed every other file in the folder and left a stale `vht-server.exe` behind.
A failed build silently destroyed a working portable install, and the exit was an unhandled
`UnauthorizedAccessException` rather than a diagnosis.

Fix: stop the named process and run the command again.

* `taskkill /pid <pid> /t /f` - `/t` also stops the elevated worker the window started. From an
  **unelevated** shell this fails with `Access is denied` when the process is elevated (the window's
  **Stop**, or Ctrl+C in the console the server was started from, is the unelevated way out).
* A `vht-client.exe` kept alive by the logon task: `schtasks /end /tn "VirtualThere Client"`.
* Then `dotnet run --project tools/Xtask -- portable`. `dist/portable` is untouched until the new set
  is complete, so there is nothing to clean up after a refusal.

### Build or test failures

* Use the SDK the project targets: `dotnet --version` must be `10.0.x`.
* `TreatWarningsAsErrors=true` means a warning fails the build - that is intentional.
* Whole solution: `dotnet build VirtualThere.slnx -c Release` then
  `dotnet test VirtualThere.slnx -c Release`. Expected: 0 warnings, 0 errors, 20 projects built,
  and 0 failed across all 7 test suites. A project file that exists on disk but is missing from
  `VirtualThere.slnx` is not built and not tested at all; `SolutionFileTests` fails on that
  instead of letting the run go green.
* Rebuilding while the acceptance rig (`vht-accept`) is running fails with `MSB3026`/`MSB3027`: the
  running rig holds its own output directory open. Wait for it to exit, then rebuild.
* The UI harness projects (`src/Vht.Ui/harness/**`) are test-only drivers, not shipped components.
