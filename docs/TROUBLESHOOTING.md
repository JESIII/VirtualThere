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

### Where the server's runtime log is, and what the rebind lines in it mean

The server always writes a structured log. `[logging] file = ""` in `server.toml` means **the
default location**, not "no file":

```
<resolved config directory>\logs\server.log
```

where `<resolved config directory>` is the one rule the whole product uses to find its
configuration (`--config`, then `VHT_CONFIG`, then a `portable.marker` beside the executable, then
the installed default): `.\config\logs\server.log` in a portable folder, and
`%ProgramData%\VirtualThere\logs\server.log` otherwise. A portable build therefore logs inside its
own folder and never touches the machine-wide directory. The file is JSON lines, capped at 8 MiB,
rolling one generation to `server.log.1`. `vht status` prints it, whether or not a server is
answering:

```
runtime log:   C:\ProgramData\VirtualThere\logs\server.log
log written:   2026-10-15 02:00:12 (19844 bytes)
```

A portable folder prints its own directory instead - that is the marker's whole effect, and the
reason a portable copy neither reads nor writes the machine-wide one:

```
runtime log:   G:\Tools\VirtualThere\config\logs\server.log
log written:   never (no server has started from this configuration yet)
```

`log written:   never ...` means the line above is where the log *would* go - check that it is the
directory you expect, because that is the whole diagnosis when a portable and an installed copy
disagree about which configuration they read.

An explicit `logging.file` (or `--log-file`) is a **second** destination. The default sink is never
dropped: the pre-configuration lines exist only in it.

#### A window-launched worker: two records, one directory

A GUI-launched worker is a `WinExe`, and the window cannot show you its output in every case, which is
why two files matter and why they land in the same directory:

* `logs\server.log` - the worker's own runtime log, as above. It is opened before the configuration
  file is even read, so the worker's whole account of starting up is in it - including the lines it
  wrote before it bound the control pipe - and it is the **only** place an *elevated* worker's lines
  ever appear: `ShellExecuteEx` hands the window no handles to redirect, and a `WinExe` has no console
  to inherit.
* `logs\worker-<pid>.log` - the window's durable record of one launch, opened and written before
  anything else can fail. Both roles produce one (the client window's record lives under the
  client's resolved config directory), and it survives the worker *and* the window.

The record's first line is the launch record, and it says which channel the worker's output has:

```
2026-10-18 22:03:03.490Z [pid 9728] launched unelevated: G:\Repos\VirtualThere\src\Vht.Server\bin\Debug\net10.0-windows\vht-server.exe --worker --pipe vht-card-de4b0a3f-b --port 33240; its stdout and stderr are captured below
2026-10-18 22:03:03.551Z [pid 9728] 2026-10-18 22:03:03.543Z [4] Info     ServerStarting: runtime log: C:\ProgramData\VirtualThere\logs\server.log path=C:\ProgramData\VirtualThere\logs\server.log
```

An elevated launch's record is the same shape with the other sentence and nothing below it (pid and
time illustrative):

```
2026-10-18 22:14:40.002Z [pid 14088] launched elevated (ShellExecuteEx, verb=runas): C:\ProgramData\VirtualThere\vht-server.exe --worker --pipe vht-server; stdout and stderr cannot be redirected by this window - the worker writes its own runtime log in the default log directory ('vht status' prints the path)
```

For an **unelevated** launch every line the worker writes is appended below the record, stamped with
the time and the worker's pid, `stderr: ` prefixed, and flushed as it arrives - so a worker killed
mid-sentence leaves everything it managed to write, and a line visible in the window's log pane is
already on disk. The file is appended, never truncated: a reused pid keeps the earlier launch's
record above the new one. The window's in-memory pane keeps the last 200 lines; the file keeps all
of them.

#### A worker that exited before binding the control pipe

The window says where to look, and that sentence is the shortest route to the record:

```
the worker (pid 9728) exited before it bound the control pipe; the launch is recorded in C:\ProgramData\VirtualThere\logs\worker-9728.log
```

* `; the launch is recorded in <file>` - the window had nothing to redirect (an elevated launch), so
  the file holds the launch record only. `server.log` has what the worker said after it opened its
  own log; if it died before that, the record is all there is, and its pid and launch time are what
  tie a crash in the Windows event log to this launch.
* `; every line it wrote is also kept in <file>` - the launch was unelevated, and the file holds the
  whole tail plus everything the worker said before it bound the pipe.

If the window is gone as well, list the directory and open the newest record:

```
Get-ChildItem C:\ProgramData\VirtualThere\logs\worker-*.log |
    Sort-Object LastWriteTime -Descending | Select-Object -First 5 Name, LastWriteTime
```

(`vht status` reports only `server.log`'s written state; the `worker-*.log` files sit in the
directory it prints.) There is nothing else left to ask - the process is gone and the pipe never
answered - so this file, and the worker's own runtime log, are the whole post-mortem.

#### The three outcomes of a P2 rebind

The three outcomes of a P2 rebind (`capture.rebind.enabled = true`) are distinguishable in the
runtime log, and each names the `busid` it applies to (the lines are JSON, with `win32_error`, `inf`
and `reboot_required` fields where they apply). Filter the file on the bus id of the device that
never appeared and read one of:

| line | meaning |
|---|---|
| `<busid>: capture.rebind.enabled is set but this process is not elevated, so the P2 rebind path cannot run` | The server process is not elevated: a worker started without a consent prompt (`--no-elevate`), or `vht serve --rebind` from an ordinary shell. Nothing changed on the device: start the server window and accept the consent prompt, or run `vht-server serve --rebind` from an elevated shell. |
| `<busid>: P2 rebind failed: <detail>` (with `win32_error` when there is one) | The rebind was attempted and Windows refused. The device is untouched and still held by its original driver. `<detail>` is the real Win32 failure, not a summary. |
| `<busid>: rebound onto the inbox winusb.sys (<inf>)` | The rebind succeeded and the device is being claimed now. |
| `<busid>: the rebind onto the inbox winusb.sys (<inf>) only takes effect after a reboot, so the device is not being taken; reboot and attach it again` | The rebind only exists after a restart. Nothing was taken and the device was handed straight back - reboot, then attach again. |

The `rebound` case never leaves a half-bound device behind: a rebinding that cannot be used is
followed by the hand-back line for the same bus id.

The window's device row states the same outcome as the second line of its Status cell, taken from
the same record: without the `<busid>: ` prefix for the not-elevated and failed cases, and for the
reboot case as `rebound onto the inbox winusb.sys (<inf>); a reboot is required before the binding is
effective, so the device was not taken - reboot the machine and attach it again`. Matching the row to
the log line is how you confirm you are reading the attempt for the device that never appeared.

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

For a portable folder, put these settings in `<portable folder>\config\server.toml` and restart the
server worker:

```toml
[capture]
mode = "auto"
rebind.enabled = true
```

This only offers P2 for devices held by a function driver; it does not make system-critical devices
safe to export or conjure a WinUSB interface where none exists. Try it only with a device you can
afford to have rebound away from its current Windows driver.

### `vht list --local` shows `state=RebindOffered reason=driver_holds_device`

That is the rebind being offered, which is what you asked for with `capture.rebind.enabled` /
`--rebind`. The device is still driver-held; the rebind runs when a client claims it, so this state
means "exportable once a client attaches", not "exportable now" (`docs/SERVER.md` §3.1).

If a client then attaches and the rebind does not happen, the runtime log says which of the three
outcomes it was; the exact lines, and what each one means, are in
"Where the server's runtime log is, and what the rebind lines in it mean" above. With `1-3` as the
bus id, the four lines are:

* `1-3: rebound onto the inbox winusb.sys (oem42.inf)` - P2 ran and Windows accepted the INF. The
  device is now on WinUSB.
* `1-3: the rebind onto the inbox winusb.sys (oem42.inf) only takes effect after a reboot, so the
  device is not being taken; reboot and attach it again` - as above, but Windows applies the new
  binding only after a restart, so the device was handed straight back.
* `1-3: P2 rebind failed: <detail>` - the INF could not be staged or the driver re-select failed;
  the Win32 error is in the detail. Nothing was changed.
* `1-3: capture.rebind.enabled is set but this process is not elevated, so the P2 rebind path cannot
  run` - the server is running unelevated. Restart it from an elevated shell (or let the service run
  it); nothing was changed.

A held device that states `CaptureUnsupported` instead has **not** been offered the rebind - check
`capture.rebind.enabled` and `capture.mode` (the rebind is only offered under `auto`; `prebound-only`
never rebinds) with `vht status`.

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

### The server stopped when the window disappeared

Two different endings, and they look the same from the outside only until you check the pipe:

* window **killed** (End task, `taskkill /F`, a debugger detaching): the worker keeps serving, and
  `vht status` still answers. Start the window again and it adopts the worker, or `vht-server
  stop-server --pipe vht-server` stops it.
* window **crashed** (an unhandled exception - `.NET Runtime` event 1026 in the Application log): the
  crash teardown runs the normal shutdown exchange, so the worker was stopped with the window. There
  is no orphan to clean up; press Start in a new window. If it keeps happening, the exception is in
  that event; also check the worker's own record, `logs\worker-<pid>.log`, beside the server's
  runtime log (`docs/TROUBLESHOOTING.md` section on log locations).

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
