# VirtualThere UI - one window per role: affordance map, branding scope, accessibility

This document covers the UI card (`ui-workstream`), which owns `src/Vht.Ui/**` and this file.
The stack (C#/.NET 10 + WPF, notification-area icon via the in-box `System.Windows.Forms.NotifyIcon`)
and the control-plane vocabulary are frozen by `docs/ARCHITECTURE.md` (sections 1 and 2.2,
Appendix C) and are not re-opened here.

## 1. One executable per role, one window each

There is no separate tray executable. `Vht.Ui` is a **library** of windows, and the two role
executables host it:

| executable | window | control pipe (default) | privileged worker |
| --- | --- | --- | --- |
| `vht-server.exe` | `VirtualThere Server` | `\\.\pipe\vht-server` | `vht-server.exe --worker` |
| `vht-client.exe` | `VirtualThere Client` | `\\.\pipe\vht-client` | `vht-client.exe --worker` |

Starting either executable with **no verb** opens its window. There is no `--mode` argument any
more: the role is a property of the executable, not of the command line. A shortcut that still
passes `--mode server|client` keeps working because the argument is accepted and ignored, but it
cannot turn one role's executable into the other role.

`--pipe <name>` overrides the pipe (used by the harness), `--port <n>` sets the server worker's
plaintext port, `--minimized` starts hidden in the notification area, `--no-elevate` starts the
worker without a consent prompt (automation only), `--start-worker` presses the real **Start**
command (server) or **Refresh** command (client) as soon as the window is up (automation only; it
exists so the worker-lifecycle test can measure a window that started its own worker without a human
clicking, and a human never passes it), `--help` prints the usage.

### Where it runs from

Both role executables are portable by construction: copy them anywhere and start them from there.
Run one from a folder and nothing is registered - no registry key, no Start-menu entry, no service -
and nothing elevates at launch: the window opens unelevated from whatever folder the executable sits
in. Configuration and state follow the folder. When `portable.marker` is beside the executables the
server reads and writes `.\config\server.toml` and `.\config\state.toml`, and the client
`.\config\config.toml`, its peers and its grants; without the marker they fall back to
`%ProgramData%\VirtualThere` and `%LOCALAPPDATA%\VirtualThere\client`. The paths that need elevation
belong to the machine, not to the folder, and stay under `%ProgramData%\VirtualThere` either way
(staged rebind INFs, TLS keys, the setup scripts' log - `docs/SERVER.md` section 2 lists them).

Neither the window nor the worker it starts needs an installed service. A service is an option,
and it is not the GUI path: `Setup-Portable.ps1 -Action Install` (or `vht install`) is what adds
it, together with the two other machine-wide items that do need elevation - the virtual host
controller driver and the firewall rules. `docs/INSTALL.md` section 0 and
`installer/PACKAGING.md` section 3 own the detail.

### The window stays unelevated; the worker does not

The window never touches WinUSB or the USB/IP driver: every action is one JSON request line on the
control pipe. The privileged half is a **separate process** started from the window:

1. **Start** (or, in the client window, **Refresh**) probes the pipe first.
2. If a control plane already answers - the installed service, a worker an earlier window started, or
   another instance's worker - the window **attaches** and starts nothing. Whatever it attached to is
   not its to stop. This is a hard requirement, not an optimisation: **adopting a live pipe never
   spawns a second worker and never raises a consent prompt**, and it is idempotent - open the window
   N times and there is still exactly one worker.
3. Only when nothing answers does the window start **its own executable** with `--worker` (nothing
   else travels with it), elevated with `Verb=runas`: Windows raises **exactly one** consent prompt,
   and the worker runs hidden with no console. It binds the pipe, and the window then attaches to it.

   On a portable folder that has never bound a port before, the worker's first bind also raises
   Windows' own **Windows Security** firewall dialog ("Do you want to allow public and private
   networks to access this app?", publisher `VirtualThere`) - not a UAC prompt, and not something the
   product can suppress for a folder it was copied into. So the first Start of a fresh copy is a
   two-dialog affair: consent for the worker, then Allow for the listener. Answering it is a required
   operator step and `installer/PACKAGING.md` section 3 documents what Cancel leaves behind; running
   `Setup-Portable.ps1 -Action Install` once adds the two port rules and a later copy still prompts.

### The pipe both halves can use

The split above works only if the pipe the **elevated** worker owns is writable by the **unelevated**
window, and that does not happen by itself. A named pipe created with no security descriptor
inherits the creating process's default DACL and carries **no mandatory integrity label of its
own**. Two different mechanisms, and which one refuses the window depends on the object
(`docs/TROUBLESHOOTING.md` has the measured descriptors):

* **The default DACL.** It names Local System, the built-in Administrators and the owner account,
  and grants Everyone and ANONYMOUS read only. The owner account is the one thing that shifts with
  elevation: under a High-integrity token it is the **Administrators group**, so the operator's own
  account is not in the DACL at all and the only ACE that matches the window's token is Everyone's
  read-only one. That is a **permission** denial, decided by the access check, and it is the one that
  was measured on the pipe this failure was reported with: the window is refused on its first request
  line and reports it as a service that is not running.
* **The absent label.** Windows treats an object with **no** label ACE as Medium, so no-write-up is
  not what refuses the window on such an object. It is still load-bearing, for two reasons: an
  object whose descriptor *does* carry a label is subject to no-write-up, and a Medium-integrity
  window then cannot write to a High-labelled object **whatever the DACL allows**; and no-write-up
  is the only thing that keeps a **Low**-integrity client out, because such a client is the
  interactive user and the ACE below admits it.

So the label is set explicitly rather than left to the creating token: the object's level is then a
property of this code rather than of whoever launched the worker.

Every control pipe in both roles is therefore created through `Vht.Interop.Control.NamedPipeSecurity`
with an explicit descriptor:

```
D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;<owner SID>)(A;;0x12019f;;;IU)S:(ML;;NW;;;ME)
```

* `D:` - Local System and the built-in Administrators keep full control, the owner account keeps full
  control, and the interactive user (`IU`) gets `0x12019f`, i.e.
  `FILE_GENERIC_READ|FILE_GENERIC_WRITE`: exactly what a request/response client of the pipe needs and
  no more.
* `S:(ML;;NW;;;ME)` - the object's mandatory label is **Medium** with no-write-up, so a
  Medium-integrity window is not blocked by the label, while a Low-integrity process still cannot
  write to the pipe.

Two properties of that code are deliberate, and neither may be simplified away:

* **It cannot break the unelevated path.** Raising an object's label above the caller's own level
  needs `SeSecurityPrivilege`, which an unelevated worker does not hold; setting one at or below it
  does not (measured on this host: a Medium token's pipe is created with `S:(ML;;NW;;;LW)` and
  `S:(ML;;NW;;;ME)` and read back carrying them, while `S:(ML;;NW;;;HI)` is refused with
  `win32 1314`). So the descriptor is normally accepted unelevated as well, and where the kernel does
  refuse it the pipe is created from the plain constructor instead - an unelevated worker's object is
  Medium by default, so both routes give the same working pair. Nothing here aborts pipe setup.
* **It reports what the object got, not what was asked for.** The DACL and the label are read back
  out of the kernel with `GetSecurityInfo` on the live handle and exposed as
  `ControlPlaneServer.PipeSecurity`, then logged at `detail` level and **warned** about when the label
  is not Medium. `LABEL_SECURITY_INFORMATION` is readable without any privilege, which is why the
  label can be checked here at all; the full SACL is not needed and is not requested.

`tests/Vht.Acceptance.Tests/ControlPipeAccessTests` covers this by opening a live pipe **by name** and
asserting the kernel's own answer - an allow ACE for the interactive user that carries write access,
and the Medium label - and then opening it for writing and driving a round trip through both real
clients (the CLI's and the window's). It asserts the object's state rather than that a helper was
called: against the previous implementation it fails on the pipe carrying no label ACE at all and on
its DACL having nothing for the interactive user. The elevated half - a High-integrity worker's pipe
deflecting a real denial - requires an interactive consent prompt, so it is not part of the automated
suite.

### Closing the window, and what happens to the worker

| The operator does | The window process | The worker |
| --- | --- | --- |
| Closes the window (X), or minimises it | hides to the notification area; still running | untouched: it keeps serving, and connected clients and in-flight transfers are unaffected |
| Picks **Exit** (window button, or the notification-area menu item) | exits for real | the worker *this* window started is stopped: the role's own shutdown exchange over the pipe (`server.stop`, or `clients.detachAll` for the client), then the process is ended. A worker it merely attached to is left running |
| Does nothing; the window process is **killed outright** (`taskkill /F`, End task, a debugger detaching) | gone, and no code of ours runs | keeps serving. Nothing reaps it: the worker is deliberately **not** tied to the window that started it |
| Does nothing; the window process **dies from an unhandled exception** | gone (a CLR abort, exit code `0xE0434352`) | **stopped**: the teardown that runs on a fatal exception is the same shutdown path Exit uses (`UiSession.Dispose` -> `Shutdown` -> the role's stop verb), so the worker goes too |
| Runs the executable again while a worker is live | opens and attaches | unchanged - no second worker, no consent prompt |

The two "does nothing" rows are not the same case, and the difference is measured rather than
assumed (both runs on the shipping `dist/portable` build, worker started with `--no-elevate` so an
unattended harness can press Start; the elevation differs in who authorises the worker, not in what
runs):

* **Killed outright.** A `TerminateProcess` of the window 1 ms after its worker bound the pipe left
  the worker alive and the pipe bound for the whole 15 s watch window - `final: worker alive=True
  pipe=True`, with `\\.\pipe\vht-m-b3` still enumerate-able. That is the row's design promise, and it
  holds for a process that gets no chance to run any code: the worker is not job-object-bound,
  parent-watched or otherwise reaped.
* **Died from an unhandled exception.** The window's own log-pane exception (card `t_9e1f5f1e`)
  killed the process, and the worker did **not** outlive it: `ServerStarted ... listeners:
  0.0.0.0:33260 (PlainTcp)` and `ControlPlaneStarted` at `22:32:31.937`, then `ServerStopping:
  stopping` at `22:32:32.324` (+0.39 s) and `ServerStopped: stopped` two milliseconds later, with the
  worker process gone ~30 ms *before* the window process disappeared. A stop the worker logs
  gracefully while the window is still alive can only be the stop verb arriving over the pipe, and
  the only code that sends it is the session's shutdown exchange - which runs because
  `UiHost.Run`'s `finally` disposes the session when `Application.Run` throws. So a crash stops the
  worker by the same route as Exit; the supervisor is still not reaping anything.
* **Exit.** Measured on the same build: `Exit` invoked at `t=2157 ms`, `worker: GONE` at `2248`,
  `pipe: GONE` at `2249`, window gone at `2279` with exit code `0`.

Consequence for the operator: after a **killed** window the worker is still serving and the next
launch adopts it; after a **crashed** window there is nothing left to adopt, and Start brings a new
worker up. Either way nothing is left running that the operator did not ask for.

The notification-area icon is the operator's persistent affordance while the window process lives:
double-click restores the window; right-click offers Open / Start / Stop / Show log / About / Exit.

**When the window process is killed outright** there is no icon left to click either, and the worker
keeps serving - by design. That becomes the operator's explicit call:

```bash
vht-server stop-server --pipe vht-server      # server: clean stop over the frozen pipe verb
vht-client                                    # client: opens the window, adopts the live worker,
                                              #   then Exit stops it
```

For the client with no window open there is no shutdown verb on its control plane, so the remaining
route is ending the process from an elevated shell (`taskkill /IM vht-client.exe /F`) - which is why
Exit is the documented way out: it is the one path that detaches cleanly and ends only the worker
belonging to that window.

`tests/Vht.Ui.Tests` asserts the lifetime rules by name: start, attach-instead-of-start (spawning no
second worker), declined consent, timeout, the graceful stop, the fallback end, "not ours to stop",
the case where the window may not end an elevated worker, a real child process that really goes away,
**closing the window does not stop the worker**, **disposing the supervisor never stops the worker**
(the killed-window row: a process that dies without running our code cannot stop anything), and
**Exit does** - which is also the crash row's route, because the fatal-exception teardown runs the
same `OnExitAsync` exchange (`WorkerLifetimeCrashTests` holds the first two and names the third).
`vht-ui-driver` asserts the same against the shipped executables (see section 7).

## 2. Affordance map (frozen verbs from Appendix C)

### Server window - `Views/ServerWindow.xaml`, `ViewModels/ServerViewModel.cs`

| UI affordance | control | verb sent |
| --- | --- | --- |
| State pill ("Stopped", "Running (plaintext :3240, TLS :3241)") | `server-state` | `server.status` (poll, 2 s) |
| Start / Stop | `start-server`, `stop-server` | `server.start`, `server.stop` |
| Refresh | `refresh` | `devices.list` (+ `server.status`) |
| Minimize to notification area | `minimize` | none (local: hides the window, keeps the tray icon) |
| Log viewer (Clear / Copy to clipboard / Close) | `toggle-log` + `Views/LogView.xaml` | `log.tail` (poll) |
| About box | `about` | none (branding + Apache-2.0 notice, no licence text) |
| Exit | `exit` | none (real shutdown) |
| Device list: nickname, `[vid:pid]`, bus ID, attached-client count, password state, status, last capture/rebind outcome | `Devices` rows | `devices.list` |
| Kick clients | `kick:<busid>` | `devices.kick` |
| Ignore / Unignore | `ignore:<busid>` (label follows state) | `devices.ignore` / `devices.unignore` |
| Rename | `nickname:<busid>` | `devices.nickname` |
| Password | `password:<busid>` | `devices.password` (empty value = remove) |

Devices the server cannot export are listed with the reason string derived from
`CaptureState`/`CaptureReason` (e.g. "Cannot capture - driver holds the device",
"Not exportable - system-critical input device") instead of being hidden - the card requires
that capture-unsupported is visible rather than silent.

The Status cell carries a second line: the outcome of the last capture/rebind attempt for that
device, in the same vocabulary as the runtime log (`devices.list` -> `capture_path` +
`capture_outcome`). The three P2 outcomes are distinguishable on the row itself - "not elevated"
(the rebind was refused before the attempt), "P2 rebind failed: <detail>" (Windows refused it),
and "rebound ... a reboot is required ..." (it succeeded but only takes effect after a reboot) -
so a device whose rebind did not succeed is never silently absent.

### Client window - `Views/ClientWindow.xaml`, `ViewModels/ClientViewModel.cs`

| UI affordance | control | verb sent |
| --- | --- | --- |
| Connection state ("Connected", "Client service not reachable") | `client-state` | `peers.list` (poll, 2 s) |
| Server list (name, `host:port`, online/offline, manual/peer) | `Servers` list | `peers.list` |
| Attach / Detach a device (one toggle button whose label follows state) | `toggle:<peer>:<busid>` | `clients.attach` / `clients.detach` |
| Auto-attach grant per device | `auto:<peer>:<busid>` checkbox | `clients.attach` with `auto=true` (writes through; the box is re-read from the next snapshot, so it never shows optimistic state) |
| Attach all devices of the selected server | "Attach all devices" button | `clients.attach` per unattached device |
| Add server by address | `add-server` | `peers.add` |
| Refresh, Minimize, Log, About, Exit | `refresh`, `minimize`, `toggle-log`, `about`, `exit` | `peers.list` / `log.tail` / local |

Reverse/peer entries carry their own status text ("last seen 4 minutes ago", "auto-attach on",
"attached as VirtualThere port N").

## 3. Deliberate absence of any licence or purchase UI

There is no licence key entry, no "unlicensed" state, no trial nag, no purchase prompt, and no
code path that can render one. Concretely:

- The only prompt window in the application is `Views/PromptWindow.xaml`, used for exactly two
  things: a device nickname and a device password. It has no other caller; `IPromptService`
  declares only `AskText` and `AskSecret`.
- No view, view model, resource or string mentions a licence, key, activation, trial, purchase,
  price or upgrade. The About box shows the product name, version, the control-pipe name, and
  the Apache-2.0 notice - not an EULA, and not a licence text box.
- The control-plane vocabulary has no licence-related verb, so the UI could not query or set one
  even if a control existed.

This absence is intentional and is the point of the card: the reference product's About dialog
with its licence text and key box is deliberately not reproduced.

## 4. Accessibility and DPI

- **PerMonitorV2**: `app.manifest` sets `dpiAwareness = PerMonitorV2` (with `dpiAware = true/pm`
  for downlevel). Layouts use `UseLayoutRounding`, star-sized columns and `WrapPanel` toolbars,
  and are legible at 100 %-200 % scaling; the screenshots in `docs/ui-shots/` were taken at
  125 %.
- **UI Automation names on every control**: each button, grid, checkbox and status text carries
  an explicit `AutomationProperties.Name`, and every per-row control carries a stable
  `AutomationProperties.AutomationId` (`kick:<busid>`, `ignore:<busid>`, `nickname:<busid>`,
  `password:<busid>`, `toggle:<peer>:<busid>`, `auto:<peer>:<busid>`) so a screen reader or a
  test can address a specific device's action. Where a control's label changes with state
  (Ignore/Unignore, Attach/Detach) the accessible name is bound to the same state, so it reads
  the action the user will perform ("Unignore", "Detach") rather than a static description.
- **Keyboard navigation**: `KeyboardNavigation.TabNavigation="Continue"` on both windows and the
  toolbar, access keys on every toolbar and dialog button (`_Start`, `_Stop`, `Re_fresh`,
  `Minimi_ze to tray`, `_Log`, `A_bout`, `E_xit`, `_OK`, `_Cancel`), Enter accepts and Escape
  cancels the prompt dialogs, and the device grids are standard focusable `DataGrid`s.
- The grid columns size explicitly rather than from their header text: a `DataGridTemplateColumn`
  with `Width="Auto"` sizes to the header word ("Actions") and clipped the buttons it contains at
  125 % scaling - found by looking at the harness screenshots, fixed by giving the two action
  columns explicit widths (285 px server, 100 px client).

## 5. No third-party packages

`Vht.Ui.csproj` has **no** `PackageReference` at all. The tray icon is the in-box
`System.Windows.Forms.NotifyIcon`, the control plane is `System.IO.Pipes` + `System.Text.Json`,
and the MVVM plumbing is ~120 lines in `Mvvm/ObservableObject.cs`. This keeps the dependency
list empty for the licence audit and avoids `Hardcodet.NotifyIcon.Wpf`, which is CPOL-licensed
and therefore not OSI-approved (ARCHITECTURE Appendix D).

## 6. Branding - scope and evidence

Every user-visible string comes from `Branding.cs` (product name "VirtualThere", window titles,
notification-area tooltips, pipe names, executable name). The shipped `src/Vht.Ui/**` tree and
its build output contain **zero** occurrences of the reference product's name:

```
$ grep -rniE "<the reference product's name>" src/Vht.Ui/                     -> 0
$ grep -rliE "<the reference product's name>" src/Vht.Ui/Vht.Ui/bin src/Vht.Ui/Vht.Ui/obj \
      src/Vht.Ui/harness/*/bin src/Vht.Ui/harness/*/obj    -> 0 files (text and binary)
```

(At the time this document was written the pattern in that grep was the literal vendor string; the
release workstream then removed the remaining occurrences from `src/Vht.Config/**`,
`src/Vht.Client/**` and the design documents, so the repository-wide brand grep is now clean except
for the recon and legal sections that are supposed to name it - see `LEGAL.md`.)

The About box, window titles, tray tooltips, prompt captions and the log header all read
"VirtualThere". The reference product is named only where it has to be: `docs/RECON_AND_SPEC.md`
(the recon record) and the legal/attribution section (`README.md`, `LEGAL.md`).

## 7. How the acceptance claim is evidenced

`src/Vht.Ui/harness/` contains two test-only projects that are not referenced by the windows and
are not shipped:

- `Vht.Ui.MockService` (`vht-mock-control-plane`) - a stand-in for both control pipes. It speaks
  exactly the frozen vocabulary, rejects unknown verbs with an error object, keeps state between
  requests, and appends every request/response pair to a JSONL transcript.
- `Vht.Ui.UiDriver` (`vht-ui-driver`) - drives the real windows through UI Automation: it finds
  controls by automation id, invokes them (`InvokePattern`, `TogglePattern`, `ValuePattern`),
  types into the prompt dialogs, checks that the window really hides and comes back for
  tray-minimize/restore, and captures PNGs of the real window rectangles. It also runs a
  **stand-alone pass**: it starts the shipped `vht-server.exe` against a pipe nothing serves,
  presses Start, and measures what the card claims - the window appears, no `ConsoleWindowClass`
  window exists for the process, a second process with the same image name appears (the worker the
  window started), the device list populates from the real server, and after Exit no process of that
  image is left over.

```
vht-ui-driver.exe --server <vht-server.exe> --client <vht-client.exe> \
                  --mock <vht-mock-control-plane.exe> --out <dir>
```

Optional: `--worker-pipe <name>`, `--worker-port <n>`, `--skip-worker-pass`.

Result of the run that produced the attached screenshots: **32 steps, 0 failures** for the two
mock-driven windows (all twelve frozen verbs exercised against the mock: `server.status`,
`server.start`, `devices.list`, `devices.nickname`, `devices.ignore`, `devices.password`,
`devices.kick`, `log.tail`, `peers.list`, `peers.add`, `clients.attach`, `clients.detach`), plus the
stand-alone pass (`standalone.*`). The driver's step log, the mock's verb summary and the raw
request transcript are written next to the screenshots.

Notes on what the driver does *not* prove: it restores the window after minimize-to-tray by
showing the real window handle (`ShowWindow(SW_RESTORE)`) and then re-acquiring the window
through UI Automation, because the notification-area popup menu is owned by Explorer, not by the
application - so the menu item itself is not clicked. The window-hide half is measured on the
real window handle, and both halves use the same window the icon would show. Everything
else (device actions, attach/detach, rename, ignore/unignore, passwords, the manual-server add)
is an automation invocation on a control in the shipped window. The stand-alone pass starts its
worker with `--no-elevate`, because nothing in an automated run can answer a consent prompt: the
elevated run differs in who authorises the same process, not in the code path.
