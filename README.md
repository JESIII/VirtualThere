# VirtualThere

Share the USB devices plugged into one Windows machine with another Windows machine, over the
network, **with no device limit and no licence**.

VirtualThere is an independent implementation of the open **USB/IP** protocol (protocol version
1.1.1). A device plugged into a *server* machine is exported over the LAN; a *client* machine
attaches it through a virtual USB host controller, so Windows enumerates it as a real device and any
application can use it - no per-application shim, no per-device driver of ours.

It is licensed **Apache-2.0**, it is not affiliated with any commercial USB-over-IP product, and it
contains no third-party product's code (`LEGAL.md`).

| Component | Executable | What it does |
|---|---|---|
| Server | `vht-server.exe`, driven by `vht.exe` | Enumerates this machine's USB devices, claims them in user mode (WinUSB), exports them over USB/IP, applies policy (nicknames, allow/ignore, per-device password, script hooks) |
| Client | `vht-client.exe` | Connects to peers, lists their exports, attaches/detaches devices through the virtual host controller, hotplug + reconnect, auto-attach policy |
| CLI | `vht.exe` | Operator surface for the server: `serve`, `status`, `list`, `kick`, `ignore`, `nickname`, `password`, service install/start/stop |
| Server window | `vht-server.exe` | The USB/IP server **and** its management window: start it with no arguments and the window opens, with a notification-area icon, device/peer lists and the log viewer. Clicking Start launches the same executable as a hidden, elevated worker (one consent prompt) that binds the control pipe |
| Client window | `vht-client.exe` | The client **and** its management window: same shape, for attaching remote devices. The window itself stays unelevated and reaches the privileged worker over a local named pipe |

Status: **v0.1.0.** It builds clean, every suite the solution covers passes, and the server, CLI and client windows
run for real on Windows 11 with .NET 10 - but it has not yet been through real hardware attach. One
command (`./build.ps1 release`) produces an MSI installer and a no-install portable folder; both are
**unsigned**, and neither has been exercised on a clean machine here. Read
[Known limitations](#known-limitations) before you rely on it.

---

## Why this exists

The commercial product in this space ships a free tier that is deliberately cut down to **one shared
USB device per server**. Their EULA grants the free tier only for "a single USB device"; sharing a
second device simultaneously requires a paid licence - and the paid gates do not stop at the device
count. Script hooks, forcing a client off a device (`OnDeviceKick`) and per-device ignore rules are
all licence-gated too, and the free build reports itself as a trial.

That is a product decision, not a technical one: the USB/IP protocol has no notion of a device
count, and the cap is implemented as a comparison against a field in a licence blob.

VirtualThere exists so that the device count is not a product feature at all.

## Free and unlimited - permanently

* **No licence key, activation, trial state, nag screen or purchase prompt** exists anywhere in the
  code, the UI, the CLI or the control-plane vocabulary.
* **No device cap code path.** There is no counter, no slot table, no `max_devices` and no branch
  that compares a device count against anything. `vht status` prints `device limit: null (no cap)`
  as a deliberate statement rather than an unknown.
* **Every hook and policy verb is unconditional**: kick, ignore/unignore, nicknames, passwords and
  the `onClientConnect`/`onClientDisconnect` script hooks run for everyone.
* **Apache-2.0 means it cannot be taken closed.** You can fork it, embed it, ship it commercially or
  fix it yourself.

The absence of the licence machinery is machine-checked, not asserted: a repository-wide grep for
licence vocabulary in code, UI and build output finds nothing, and the CI workflow in
`.github/workflows/ci.yml` re-runs that check on every push.

## Install

VirtualThere v0.1.0 ships two ways: an **MSI installer** and a **no-install portable folder**. Both
are produced by the one release build, and both are **unsigned** in this environment (no code-signing
certificate is available here), so Windows shows its usual "unknown publisher" prompt. Neither has
been run on a clean machine yet - the packaging document says exactly what was and was not verified
([`installer/PACKAGING.md`](installer/PACKAGING.md) section 8).

The **portable folder** is used in place: copy `dist/portable/` anywhere and run `vht-server.exe`,
`vht-client.exe` or `vht.exe` from it. That folder contains `portable.marker`, so both executables
keep everything they write in `config\` beside themselves - `server.toml` for the server,
`config.toml`, peers and grants for the client - and never touch `%ProgramData%` or `%LOCALAPPDATA%`.
Two portable copies therefore cannot share one configuration file, and removing the folder removes
everything it wrote. Delete `portable.marker` and the installed defaults apply again (server
`%ProgramData%\VirtualThere\server.toml`, client `%LOCALAPPDATA%\VirtualThere\client`).

**From source**

**Prerequisites**

* Windows 10/11 x64.
* .NET SDK 10.0 (the projects target `net10.0` / `net10.0-windows`). `dotnet --version` should
  report `10.0.x`.
* For the client only: the third-party `usbip-win2` virtual USB host controller driver. It is
  BSD-2-Clause and is **not redistributed here** - fetch it from the upstream project
  (`vadimgrn/usbip-win2`, release 0.9.8.1) and verify the SHA-256 in
  `third_party/usbip-win2-driver/SHA256SUMS` before installing. Installing a kernel-mode driver
  needs an elevated shell. Step-by-step instructions, including how to check the hash:
  [`docs/INSTALL.md`](docs/INSTALL.md).

**Build**

```bash
git clone https://github.com/JESIII/VirtualThere.git
cd VirtualThere
dotnet build VirtualThere.slnx -c Release
dotnet test  VirtualThere.slnx -c Release     # expect 0 failed, every suite in the solution
```

Executables land in `src/Vht.Cli/bin/Release/net10.0-windows/vht.exe`,
`src/Vht.Server/bin/Release/net10.0-windows/vht-server.exe`,
`src/Vht.Client/bin/Release/net10.0-windows/vht-client.exe` and
`src/Vht.Server/bin/Release/<tfm>/vht-server.exe`. Add the directory holding `vht.exe`
to `PATH`, or use full paths.

## Use

### On the machine whose devices you want to share (server)

```bash
vht config-template --out C:\ProgramData\VirtualThere\server.toml   # writes a commented starter file
# no --out: writes to the path the server will read (%ProgramData%, or .\config in a portable folder)
vht serve                                                          # foreground; Ctrl+C to stop
```

Run it as a Windows service instead (elevated shell):

```bash
vht install          # installs and starts the "vht-server" service
vht service-status
vht stop-service
vht uninstall
```

Inspect and control a running server from any shell on the same machine:

```bash
vht status                      # exports, attachments, listeners, "device limit: null (no cap)"
vht list                        # exported devices (busid, vid:pid, state)
vht list --local                # enumerate this machine's USB devices without a server
vht nickname <busid> --name "Label printer"
vht password <busid> --password "s3cret"
vht ignore 1050:0407            # hide a device from the export list
vht unignore 1050:0407
vht kick <busid>                # force-detach whichever client holds it
vht stop-server
```

The server's plaintext listener defaults to TCP **3240**, the stock USB/IP port, so a Linux `usbip`
client can attach to it. The release-verification run in `docs/SERVER.md` used a different port only
to avoid colliding with anything already bound. See [`docs/SERVER.md`](docs/SERVER.md) for the config
file, ports, capture modes and the script-hook ABI.

### On the machine that wants to use the devices (client)

```bash
vht-client peers add 192.168.1.10:3240     # or name@host:port
vht-client list                            # what the peers export
vht-client attach 1-3-252f868a             # attach; Ctrl+C detaches
vht-client attached                        # what the controller has imported
vht-client detach --all
vht-client capacity                        # measure the driver's per-controller port ceiling
vht-client status                          # one-line health
vht-client serve                           # long-running mode (the service and the window's worker use this)
```

Or use the two management windows, which drive both sides over a named pipe:

```bash
vht-server                  # server manager window (and the server worker behind it)
vht-client                  # client device manager window
```

Full walkthrough with screenshots of both windows: [`docs/CLIENT.md`](docs/CLIENT.md) and
[`docs/UI.md`](docs/UI.md).

## Supported device classes

Control, bulk and interrupt transfers are the supported path. **Isochronous is not supported in
v0.1.0**, so streaming classes do not work yet.

| Device class | Transfers | State |
|---|---|---|
| Mass storage (BOT/UAS), printer/scanner | bulk + control | Expected to work; not yet verified on real hardware |
| HID keyboard/mouse, gamepad, consumer control | interrupt + control | Expected to work; not yet verified on real hardware |
| CDC serial (VCP) | bulk + interrupt + control | Expected to work; not yet verified on real hardware |
| Vendor-specific (WinUSB/libusb) apps | bulk + interrupt + control | Expected to work; not yet verified on real hardware |
| **USB audio (UAC1/UAC2) streaming** | **isochronous** | **Not supported** |
| **UVC webcam video streaming** | **isochronous** | **Not supported** (the camera may enumerate; video will not stream) |
| Hubs and devices needing port-reset loops beyond the driver's model | mixed | Unverified |

The full, evidence-tagged matrix is in [`docs/CLIENT.md`](docs/CLIENT.md) section 3.

## Known limitations

Stated plainly - these are the honest boundaries of v0.1.0:

1. **The installer and the executables are unsigned.** No code-signing certificate and no kernel driver
   of our own exist in this environment. `./build.ps1 release` produces the MSI and the portable
   folder, but the clean-machine install path (run installer -> export a device -> attach it) has not
   been run here; see [`installer/PACKAGING.md`](installer/PACKAGING.md) sections 4 and 8.
2. **Windows only.** `Vht.Core`, `Vht.Config` and `Vht.Transport` are platform-neutral libraries,
   but the two management executables and the CLI are Windows-only.
3. **Traffic is unencrypted in the shipped binaries.** `Vht.Transport` implements TLS 1.3, pinning,
   LZ4 and authentication, with its own test suite - but it is **not wired into `vht-server` or
   `vht-client` yet**, so the executables speak plain USB/IP. USB/IP has no authentication of its
   own. Do not expose a server to an untrusted network: use a trusted LAN, a VPN, or an SSH tunnel.
   `vht --password` is documented as TLS-listener-only for the same reason (it is refused, never
   silently ignored, on the plaintext listener).
4. **No real-hardware attach has been verified yet.** The passing tests cover the codec, the
   client's attach logic against a fake USB/IP peer, the transport, the config model, the control
   plane and the UI harness - not a physical device enumerating on a second machine. Hardware
   validation is in progress; until it lands, treat "attaches a real device" as unproven.
5. **The client needs the third-party UDE driver** (BSD-2-Clause, upstream, not redistributed here).
   Without it the client reports the driver is missing and refuses to attach - it does not crash or
   fall back to something else.
6. **The controller's port ceiling is unknown.** It is measured rather than assumed
   (`vht-client capacity`) and nothing in the client enforces a limit, but the real number must be
   measured on a machine with the driver installed.
7. **Isochronous device classes do not work** (see the table above).
8. **Server capture depends on the device's driver.** A device whose function driver holds it cannot
   be claimed in user mode without a rebind path; `vht list --local` says so per device, with the
   Win32 error and the reason. On the machine used to develop this, all 13 USB devices were held by
   their drivers, so an unelevated `vht list` legitimately exports nothing.
9. **Hotplug falls back to a periodic rescan** (about 3 s) on hosts where device-change
   notifications are unavailable; and `install`/`uninstall` and any driver rebind need an elevated
   shell.
10. **The config library and the transport library are not wired in.** The server reads a
    documented subset of TOML with its own loader (`docs/SERVER.md`), and the richer schema in
    `docs/CONFIG.md` describes `Vht.Config` as it stands. They are kept aligned by key vocabulary,
    not by code yet.
11. **Discovery is not wired into the client yet**; add peers with `vht-client peers add`.

## Licence

VirtualThere is released under the **Apache License 2.0** (`LICENSE`). Why this licence:

* **It is OSI-approved and permissive** - no copyleft, so you can embed VirtualThere in a commercial
  product, ship it in a fleet, or relicense your own modifications.
* **It grants patents explicitly** (section 3). For software that talks to kernel drivers and USB
  stacks, an explicit patent grant matters more than it does for a script collection; MIT and
  BSD-2-Clause leave that to implication.
* **It is compatible with every dependency in the build.** All of them are MIT, BSD-2-Clause or
  Apache-2.0 (see `THIRD_PARTY_NOTICES.md`). The driver this project consumes is BSD-2-Clause, which
  the Apache-2.0 terms accommodate with attribution only.
* **It is one-way compatible with GPL-3.0** but not GPL-2.0-only. This matters because the obvious
  alternative driver set for this problem (`usbipd-win`'s GPL-3.0 binaries) was rejected precisely so
  that VirtualThere would not be forced into a copyleft licence by a dependency - see
  `THIRD_PARTY_NOTICES.md` section 4.
* **It keeps the option to relicense later** if a contributor agreement is ever added, which a
  copyleft licence would not.

GPL was considered and rejected for the opposite reason to the usual one: the point of this project
is that anyone can self-host it, for free and without strings - including inside a company that
would never approve GPL obligations. `LICENSE`, `NOTICE` and `THIRD_PARTY_NOTICES.md` carry the
licence terms and attributions. Contributions are accepted under Apache-2.0 (see
`CONTRIBUTING.md`).

## Legal and attribution

VirtualThere is an **independent clean-room implementation** built on the **open USB/IP standard**
(Linux `usbip` / `usbip_protocol.rst`, protocol version 1.1.1) plus the BSD-2-Clause `usbip-win2`
virtual USB host controller driver.

* It **contains no VirtualHere code** - no source, binary, driver, string table, resource or data
  file of theirs is copied, bundled, linked or redistributed here.
* It is **not wire-compatible with VirtualHere**, by design: their protocol is proprietary and
  undocumented, and re-implementing it byte-for-byte would be a derivative work their EULA forbids.
* It is **not affiliated with VirtualHere Pty. Ltd.** - no sponsorship, endorsement or contribution
  in either direction. The name is used only where a factual comparison is required.
* It **does not use their cloud matchmaker service** and does **not reproduce their licensing
  mechanism**.
* The reference binaries analysed during development are **not part of this repository** and are
  excluded by `.gitignore`.

The evidence and the full reasoning are in **`docs/RECON_AND_SPEC.md` section 9 ("Approach and legal
position")** and section 1 ("Licensing model"); the short version is `LEGAL.md`. Trademarks belong to
their respective owners.

## Documentation

| Document | Contents |
|---|---|
| [`docs/INSTALL.md`](docs/INSTALL.md) | Prerequisites, build, driver install and hash verification, service install, first run, uninstall |
| [`docs/SERVER.md`](docs/SERVER.md) | Server setup, the `server.toml` subset, ports, capture modes, hooks, security notes |
| [`docs/CLIENT.md`](docs/CLIENT.md) | Client setup, attach/detach, multi-device model, hotplug/reconnect, device-class matrix |
| [`docs/CONFIG.md`](docs/CONFIG.md) | Config reference: every key, default, type and validation rule (the `Vht.Config` schema) |
| [`docs/TRANSPORT.md`](docs/TRANSPORT.md) | The TLS/LZ4/authentication layer: wire format, handshake, benchmark numbers |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | The frozen architecture: components, decisions, invariants, risks |
| [`docs/RECON_AND_SPEC.md`](docs/RECON_AND_SPEC.md) | The clean-room recon record and the parity target, including the legal position |
| [`docs/FEATURE_MATRIX.md`](docs/FEATURE_MATRIX.md) | Feature-parity checklist against the reference product, free vs paid tiers |
| [`docs/UI.md`](docs/UI.md) | UI affordance map, branding scope, accessibility, harness evidence |
| [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) | Symptom-to-cause fixes for the failure modes seen in practice |
| [`docs/FAQ.md`](docs/FAQ.md) | Short answers, including "is this a crack?" and "does it talk to VirtualHere?" |
| [`LEGAL.md`](LEGAL.md) / [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) | Legal position and attribution; dependency licences |
