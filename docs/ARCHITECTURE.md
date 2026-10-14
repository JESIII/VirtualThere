# VirtualThere — Architecture

Status: **frozen for v1**. This document answers every architectural question the subsystem cards
depend on. Each of the 10 required sections states a *decision*, not a menu. Where a decision rests
on reverse-engineering evidence, the address points at `docs/RECON_AND_SPEC.md` (same absolute VAs).
Where something is not yet verified, it says so and names the card that must verify it.

Scope reminder (from the card, not negotiable): VirtualThere is an **open USB/IP** implementation.
It is **not wire-compatible with the reference product** and contains no code from the reference product — parity is
behavioural and configuration-vocabulary parity only (`RECON_AND_SPEC.md` §9, `FEATURE_MATRIX.md` §D).

Environment this was decided on (verified on this machine, 2026-10-14):

| Tool | State |
|---|---|
| .NET SDK | **10.0.402 — installed** |
| `cargo` / `rustc` / `rustup` | absent |
| `cl.exe` / `msbuild` / `cmake` | absent (no MSVC build tools, no WDK) |
| `G:\Repos\VirtualThere` | exists, holds `docs/`, `unpacked/`, `GhidraProj/` — **not yet a git repo**, no remote |

## Decision register

| # | Question | Decision |
|---|---|---|
| 1 | Component map | `vht-server`, `vht-client`, `vht-tray` + shared `vht-core` (+ `vht-config`, `vht-transport`, `vht-interop`) — §1 |
| 2 | Language/runtime | **C# / .NET 10 (LTS)**; WPF for UI — §2 |
| 3 | Client virtual-device plumbing | **Reuse `usbip-win2`'s UDE driver, BSD-2-Clause, pinned prebuilt**; no driver of our own in v1 — §3 |
| 4 | Server device capture | **User-mode WinUSB claim + rebind**; no VirtualThere kernel driver in v1; documented capture gaps — §4 |
| 5 | Unlimited concurrency | No device-count concept exists anywhere in `vht-core`; per-connection/per-device guards only — §5 |
| 6 | Security | TLS 1.3 (SslStream/Schannel) + TOFU SPKI pinning + HMAC challenge-response (PBKDF2 verifier); two listeners with a strict rule that plaintext can never carry a password-protected export — §6 |
| 7 | Discovery | DNS-SD service type **`_vht._tcp`** via Windows DNSAPI, manual `host:port` always available, auto-attach never automatic without a recorded per-device grant — §7 |
| 8 | Compression | Negotiated **LZ4 block** (K4os, MIT) above a threshold on bulk/interrupt payloads only — §8 |
| 9 | Repo/build | Single `.sln`, TFM-pinned, `dotnet run --project tools/Xtask -- release` as the one build command — §9 |
| 10 | Risks | Signed-driver wall, capture gaps, iso, USB 3.x, driver port ceiling — §10 |

---

## 1. Component map

Four shipped binaries, one shared library layer. Names are the product surface; nothing is named
after another vendor.

| Component | Kind | Responsibility | Owner card |
|---|---|---|---|
| `vht-server` | Windows service (`Vht.Server`), also runnable as a console app for portable/debug use | Enumerate local USB devices, claim them user-mode (WinUSB), export them over USB/IP, run policy hooks, serve discovery | `server-workstream` |
| `vht-client` | Windows service or user process (`Vht.Client`) | Connect to remote servers, list exports, attach/detach through the virtual host controller, auto-attach policy, loopback transport bridge | `client-workstream` |
| `vht-tray` | One WPF executable, two modes (`Vht.Ui`) | Tray icon + management windows for *both* server and client; it drives the services over a local IPC channel, it does not implement protocol | `ui-workstream` |
| `Vht.Core` | Library | USB/IP codec, URB model, device model, policy (allow/ignore/nickname/password), hook runner, structured logging | `server-workstream` |
| `Vht.Config` | Library | Config schema + validation, persistence, device identity, DNS-SD discovery | `config-workstream` |
| `Vht.Transport` | Library | `ITransport` and its three implementations (plain TCP, TLS, TLS+LZ4) | `transport-workstream` |
| `Vht.Interop` | Library | All P/Invoke: SetupAPI, CfgMgr32, WinUSB, SCM, DNSAPI, UDE driver IOCTLs | `server-workstream` (additive-only to others) |

Data flow, server side:

```
USB device ──WinUSB(WinUsb_Initialize)──▶ Vht.Server capture ──URB queue──▶ Vht.Core codec
        ──▶ Vht.Transport (TLS | TLS+LZ4 | plain) ──▶ network
```

Client side (see §3 for why this shape):

```
network ──▶ Vht.Client loopback bridge (TLS + LZ4 termination) ──▶ 127.0.0.1:3240 (plain USB/IP)
        ──▶ usbip2_ude.sys (third-party UDE driver, in-kernel USB/IP client) ──▶ Windows USB stack
                                                                              └─▶ device driver / app
```

`vht-tray` talks to `vht-server`/`vht-client` over a **named pipe** (`\\.\pipe\vht-server`,
`\\.\pipe\vht-client`) with a JSON-lines control protocol; the tray never touches WinUSB or the
driver directly. That keeps the security-sensitive privilege boundary in the services and lets the
UI run unelevated.

Naming/branding rule for all four: no "the reference product" string anywhere in code, UI, installer or build
output (`ui-workstream` and `release-workstream` grep for it), no licence UI, no purchase prompt, no
"unlicensed" state.

---

## 2. Language / runtime decision

**Decision: C# on .NET 10 (LTS), one language for server, client, tray and CLI. UI framework: WPF
with a WinForms `NotifyIcon` for the tray. Deterministic, TFM-pinned, `nullable enable`,
`TreatWarningsAsErrors`.**

The card says the driver/binding path is the deciding factor, so that is where the argument starts.

### 2.1 The driver/binding path does not force C++ — it forces *consuming* a prebuilt driver

Every Windows USB/IP or USB-over-IP product on this platform sits on a kernel component that
user-mode cannot replace:

- the reference product ships `vhfilter` — device path `\??\GLOBALROOT\Device\vhfilter`
  (`RECON_AND_SPEC.md` §2, address `0x140974d00`) — plus `bindWinUSBToDevice`.
- The only licence-compatible Windows client driver, `usbip-win2`'s `usbip2_ude.sys`
  (+ companion upper filter `usbip2_filter.sys`), is C++/WDK, **BSD-2-Clause**, WHLK-certified or
  attestation-signed, and its *binaries* are what we ship (§3).
- Writing our own driver is out of v1 scope and, on this machine, impossible to build: no
  `cl.exe`, no WDK, no `cmake` (§10, risk 1).

So in either language the kernel side is **someone else's signed binary**. What the deciding factor
actually selects is the language of the **user-mode binding surface**: SetupAPI, CfgMgr32, WinUSB,
the SCM, DNSAPI and the UDE driver's IOCTL ABI. All six are plain Win32/WDK-ABI C APIs, reachable
from C# by `LibraryImport`/`DllImport` and from Rust by `windows-rs`. Neither language is
advantaged by the ABI, so the tie is broken by other hard evidence:

### 2.2 Why C# over Rust (and over C++)

1. **Toolchain readiness, verified above.** The .NET 10 SDK is already installed on the machine that
   must produce the release artifacts; there is no Rust toolchain at all, and Rust's TLS options
   need a C toolchain that is likewise absent (`aws-lc-rs` requires cmake + a C compiler, `ring`
   requires a C compiler — neither exists here, so even `rustup` would not be enough). Every build
   card would start with a multi-gigabyte prerequisite install. `dotnet build` works today.
2. **The reference implementation for our exact server-side problem is C#/.NET.**
   `dorssel/usbipd-win` — the Windows USB/IP server that already solves enumeration, claim, service
   hosting, firewall rule and WSL attach — is a **.NET** application. Its *code* is unusable for us
   (GPL-3.0-only, §3.4), but the platform facts it puts beyond doubt (that P/Invoke to SetupAPI +
   WinUSB + CfgMgr32 is a sound way to build this, that a .NET service can hold and export USB
   devices, that `TcpListener` on 3240 is the interop default) transfer directly.
3. **Memory safety is not a Rust-vs-C# differentiator here.** Both are memory-safe managed
   runtimes. The class of bug Rust would add protection against over C# is *marshalling* bugs
   (mostly `Unsafe`/`Span` misuse on buffers), not use-after-free of the kind that motivates Rust
   for C++ rewrites.
4. **TLS without a vetted-crypto build.** `SslStream` uses Schannel: TLS 1.3 for free, OS certificate
   store, OS-patchable, no third-party crypto library to licence-audit, no C build dependency. In
   Rust this is the exact place where the missing C toolchain bites.
5. **UI, tray, service and installer are first-class.** WPF gives per-monitor-v2 DPI, UI Automation
   accessibility, tray via `NotifyIcon`, service hosting via
   `Microsoft.Extensions.Hosting.WindowsServices` — all of which `ui-workstream` and `packaging-workstream` must
   deliver. The equivalent Rust stack (`eframe` + `tray-icon` + Windows service crate) is workable
   but its accessibility story is markedly weaker, and the card requires accessible UI.
6. **Maintainer fit.** The repo owner works in C# professionally (`Wizards II`, VB→C# migration).
   For a project whose deliverable is "open source it and keep it alive", that is a real
   engineering asset, and it is not offset by any Rust advantage we can measure.

### 2.3 Rejected alternatives, with the reason

- **C++**: closest to the driver headers and to what the reference product itself is (its client leaks
  `kvmip/*.cpp` build paths — `RECON_AND_SPEC.md` §7.6), and the only language in which we could
  rebuild a driver. Rejected because (a) we do not rebuild the driver (§3), (b) no compiler or WDK
  on the machine, so no card could build or run anything, and (c) it reintroduces memory-safety risk
  in exactly the packet-parsing/URB code that matters most. C++ remains the language of the
  third-party driver we consume, and of nothing we author.
- **Rust**: the strongest alternative and genuinely better on single-binary distribution. Rejected
  on toolchain readiness (2.2.1), the TLS/crypto build dependency, UI accessibility, and maintainer
  fit. If this decision is ever revisited, the deciding experiment is: `cargo build` a
  `SslStream`-equivalent on a clean machine with only rustup installed.
- **Reusing `usbipd-win` as the server core** (the option the upstream recon card suggested:
  "usbipd-win already owns the Windows device-capture problem"): rejected on licence grounds, see
  §3.4. Note the rejection is *not* about GPL being "un-open-sourceable" — GPL-3.0 is OSI-approved
  and would satisfy the brief — but because `usbipd-win`'s device capture is not its own code: its
  `Drivers/` tree is a **binary redistribution of VirtualBox 7.2.20's USB filter drivers under
  GPL-3.0** (its own `Drivers/README.md` is a "Declaration of License Compliance" conforming to
  GPLv3 §6c). Adopting that means adopting a third party's kernel binaries, their update cadence,
  their GPL-3.0 obligations, and giving up the right to ship VirtualThere under a permissive
  licence.

**Licence consequence, decided here so the release card does not have to guess: VirtualThere is
released under Apache-2.0** (explicit patent grant, no copyleft, embeddable). Every component in §3,
§8 and Appendix D is compatible with that. `release-workstream` records it and ships `THIRD_PARTY_NOTICES`.

---

## 3. Virtual device plumbing on Windows (client side)

**Decision: reuse the `usbip-win2` UDE driver. Do not write a driver. Ship the upstream driver
package verbatim, pinned by version and SHA-256, with its BSD-2-Clause licence text and provenance
recorded in `third_party/`.**

### 3.1 Why a UDE-style driver is required at all

To make a remote device *usable by any Windows application*, the device must appear on a real USB
host-controller stack, not behind an app-level shim. Windows 10 1903+ provides exactly the right
primitive: **UDE (USB Device Emulation)** — "developing Windows drivers for emulated USB host
controllers and devices" — which lets a driver present a virtual host controller whose ports
enumerate as ordinary USB devices. `usbip-win2` is an implementation of that, and it is
protocol-complete against USB/IP v1.1.1.

### 3.2 The chosen component, with licence and provenance

| Item | Value |
|---|---|
| Project | `vadimgrn/usbip-win2` — "USB/IP Client for Windows" |
| Licence | **BSD-2-Clause** (permissive — compatible with Apache-2.0, needs attribution only) |
| Components consumed | `usbip2_ude.sys` (UDE host-controller driver), `usbip2_filter.sys` (device upper filter), their signed INF/cat package |
| Signing | WHLK-certified **or** attestation-signed; signing made possible by the Open Source Codesigning Initiative |
| Requirement | Windows 10 x64 1903 (18362)+, or Windows 11 ARM64; server must speak USB/IP **v1.1.1** |
| Released as | v0.9.8.1 (2026-09-27) at time of writing |
| Verified facts used | network I/O happens **in the driver** via Winsock Kernel (WSK); `usbip.exe attach` reports "successfully attached to port N"; `-all` detaches all |

Two consequences that shape the rest of the design:

1. **The driver owns the socket.** `usbip2_ude.sys` opens the TCP connection to the USB/IP server
   itself, in kernel, using WSK. Therefore **TLS and LZ4 cannot live in the driver** — kernel-side
   TLS with a Rust/C# library is not a thing. See §6.2 for the loopback-bridge resolution; this is
   the single most important interface consequence in this document.
2. **The device-count ceiling, if any, is the driver's port model, not ours.** Every attach occupies
   a virtual port. The number of ports per controller instance is not documented in the upstream
   README and is **not verified** — §10 risk 3 makes measuring it an explicit acceptance target.

### 3.3 How `vht-client` drives it

`vht-client` does **not** shell out to `usbip.exe` and does not parse its text output. It speaks the
driver's IOCTL ABI directly from `Vht.Interop.Ude`, re-declaring the structs and `CTL_CODE` values
from the upstream BSD-2 headers (header re-declaration + attribution is a licence-compatible use; we
keep the upstream headers in `third_party/usbip-win2-driver/include/` for provenance). `usbip.exe`
is kept only as an optional debug cross-check binary.

Needed operations, in driver terms: enumerate imported devices and their locations, start an attach
attempt for (host, busid, port), report attach progress/result, stop attach attempts, detach one
device or all, and subscribe to device events for hotplug UX (`client-workstream` deliverable 5).

### 3.4 Alternatives evaluated

| Option | Verdict |
|---|---|
| `usbip-win2` UDE driver (BSD-2) | **Chosen.** Permissive, maintained, signed, multi-device, USB/IP 1.1.1 conformant. |
| Write our own UDE/WDF driver | Rejected for v1: needs WDK + MSVC (absent), driver signing (§10 risk 1), and Driver Verifier-class testing effort. Revisit only if upstream refuses a change we require. |
| the reference product's client driver | Not available to us and not licence-compatible. |
| `usbipd-win`'s VirtualBox filter drivers (GPL-3.0 binaries) | Rejected: GPL-3.0 binary redistribution of VirtualBox code conflicts with the Apache-2.0 decision in §2.3, and those drivers solve the *server* side, not the client side. |
| No driver — application-level USB shim | Rejected: violates the card's requirement that an attached device be usable by any app (`client-workstream` deliverable 1). |

---

## 4. Server-side device export

**Decision: user-mode capture. `vht-server` claims devices through WinUSB using SetupAPI/CfgMgr32
for enumeration and `WinUsb_Initialize` for I/O. VirtualThere ships no kernel driver in v1. Devices
that user-mode cannot capture are enumerated, reported as `CaptureUnsupported` with a reason, and
left untouched — the server never breaks a device it cannot export.**

This is the same technique the reference products use for the claim step (the reference product's recon shows
`WINUSB.DLL` + `WinUsb_QueryDeviceInformation` + `SetupDiEnumDeviceInterfaces` + `CM_Get_Device_IDW`,
`RECON_AND_SPEC.md` §2), minus the filter driver.

### 4.1 Capture paths, in order of preference

1. **P1 — already on a WinUSB stack.** The device's interface is exposed with a WinUSB-compatible
   device interface GUID (vendor-provided WinUSB driver, or a user who bound it with an existing
   tool). Claim with `WinUsb_Initialize` on the interface path. Zero driver work, zero
   re-enumeration, instant handback on unbind.
2. **P2 — rebind to WinUSB with a VirtualThere device-specific INF.** For devices Windows has bound
   to a class driver, install our own device-specific INF (`USB\VID_xxxx&PID_xxxx`) that binds the
   **inbox** `winusb.sys`. Install via `DiInstallDriver`/`pnputil /add-driver`, requiring
   Administrator. Handback = `DI_FUNCTION`-driven re-install of the original INF (record the original
   `DriverInfPath`/`DriverVersion` before rebinding, so `onPassToKernel` parity is possible: name it
   and implement it).
   *This is the only step in the whole design that has a signing dependency, because Windows
   requires a signed driver package for the INF's catalog.* See §10 risk 1 and the fallback below.
3. **P3 — thermal fallback when signing is unavailable.** `vht-server` exports only P1 devices and
   additionally accepts devices pre-bound by an *external* tool the user already has. It reports
   clearly what it cannot capture and why. This fallback exists so that `server-workstream` is never
   blocked by a certificate purchase, and so the acceptance tests in `hardware-validation-workstream` can run on real
   hardware today.

The mode is a config key (`capture.mode = auto | winusb-only | prebound-only`), default `auto`.

### 4.2 Enumeration

`SetupDiGetClassDevs(GUID_DEVINTERFACE_USB_DEVICE)` → `SetupDiEnumDeviceInterfaces` →
`SetupDiGetDeviceInterfaceDetailW` (buffer with the required-size query) → `CM_Get_Device_IDW` for a
stable instance path, `CM_Get_Parent` for hub topology, and a `WinUsb_QueryDeviceInformation` +
`WinUsb_QueryInterfaceSettings` pass for speed/descriptor strings. Reuse the recon's log vocabulary
for human-readable output so the two products can be compared field for field:
`Found %s speed device [%04x:%04x] "%s, %s" at address %u` (`RECON_AND_SPEC.md` §4 vocabulary).

Hotplug: `CM_Register_Notification` (per-interface-class, `CM_NOTIFY_FILTER`) rather than
`RegisterDeviceNotificationW`, so the service gets arrivals/removals without a window handle.

### 4.3 What user-mode cannot capture (documented, and surfaced in the UI)

| Class | Why user-mode fails | Consequence |
|---|---|---|
| Boot/system keyboard & mouse (HID) | Windows refuses to replace `kbdhid`/`mouhid` for input devices the session depends on | Not exportable in v1. Non-system HIDs (gamepads, keypads, vendor HID) are exportable via P1/P2. |
| Root hubs, host controllers | Part of the machine's own USB stack | Never exportable; filtered out at enumeration. |
| The device hosting the running OS / pagefile | Rebinding removes the running system's disk | Refused; explicit config allow-list override required. |
| NICs the management link depends on | Exporting it cuts the network the export runs over | Refused by default with a specific message. |
| Composite/IAD devices where only one interface is wanted | A device-specific INF binds the whole device | v1 exports the whole device or nothing; per-interface export needs a function-level INF (v2). |
| Devices whose function driver holds exclusive access and refuses to release | No filter driver to interpose | Reported as `CaptureUnsupported: driver_holds_device`. |
| Devices with isochronous endpoints above WinUSB's practical limits | See §10 risk 4 | Best-effort; classes affected are named in `client-workstream`'s honest matrix. |

the reference product's answers to these are `AutoAttachToKernel` (`0x1409809bd`), `onPassToKernel`
(`0x140968203`) and the `vhfilter` filter driver (`0x140974d00`). We implement the *policy*
(handback, refusal with a reason) in user-mode, and defer the filter-driver variant to a scoped v2
card, only if `hardware-validation-workstream` shows the user-mode gaps hurt a device class we care about.

---

## 5. Unlimited-device concurrency model

**Decision: the concept "maximum number of devices" does not exist in VirtualThere's codebase. Not
as a config key, not as an enum member, not as a constant. What exists is per-device exclusivity
(one client per device, parity with `Device ... is in use elsewhere`) and per-connection resource
caps (in-flight URBs, buffers, sockets). Those are bounded by machine resources, never by a count
of devices.**

### 5.1 Data structures

Per server process:

```
ExportTable   : ConcurrentDictionary<DeviceKey, ExportEntry>    // every capturable device
AttachTable   : ConcurrentDictionary<DeviceKey, AttachRecord>   // 0 or 1 client per device
ConnectionSet : ConcurrentDictionary<ConnectionId, Connection>  // every live client socket
```

- `ExportEntry` = device identity + capture handle (`SafeFileHandle` to the WinUSB interface) +
  descriptor cache + policy verdict + hotplug generation counter.
- `AttachRecord` = connection id, client id (UUID per connection, mirroring the recon's
  `UuidCreate` per connection), attach timestamp, cancel token.
- `Connection` = the `ITransport` stream, one reader task, one writer task, a per-endpoint submit
  queue, an in-flight URB table keyed by `seqnum`, and a cancellation source.

### 5.2 Lifecycle of one attached device

1. Client sends `OP_REQ_IMPORT` for one busid **on its own TCP/TLS connection** (USB/IP's model is
   one connection per imported device — this is why unlimited devices "just works": there is no
   shared session object to grow, only more connections).
2. Server resolves busid → `DeviceKey`, checks policy: allow/ignore list → `BP_DEVICE_IGNORED`;
   password → `BP_PASSWORD_REQUIRED`; already attached → `BP_DEVICE_BUSY`; otherwise binds:
   `AttachTable[key] = record`, replies `OP_REP_IMPORT {status: 0}`.
3. Runtime: client sends `USBIP_CMD_SUBMIT` (1) per URB and `USBIP_CMD_UNLINK` (2); server answers
   `USBIP_RET_SUBMIT` (3) / `USBIP_RET_UNLINK` (4). Per device the server holds one WinUSB handle
   and one per-endpoint queue; in-flight URBs are tracked so `unlink` can cancel precisely
   (`RECON_AND_SPEC.md` §8 shows the reference product tracking `N outstanding in-flight URBs`, and USB/IP's
   documented unlink semantics require exactly this bookkeeping).
4. Teardown: socket close, explicit detach, kick, device unplug or service stop → cancel tokens fire,
   in-flight URBs complete with `-ECONNRESET`-equivalent, WinUSB handle closed, export re-advertised
   if the device is still physically present.

### 5.3 Separate connections, separate failures

One device's stalled URB stream cannot delay another's: each connection has its own reader and
writer task, its own bounded queue, and its own timeout. Backpressure is per endpoint (bounded
in-flight cap × max transfer size); exceeding it is a per-connection error
(`BP_TRANSFER_QUEUE_FULL`), never a global one.

### 5.4 Where the reference product's `max_devices` cap would have lived — and why nothing replaces it

Recon located the cap in code, not just in strings:

- the **value** is decided in `FUN_14074bbb0` — formatted as `max_devices=%d` at `0x14074c669`
  (string `0x140980ce0`), or `max_devices=unlimited` at `0x14074c690` (`0x140980cef`), sourced from
  the licence blob's count field (`0x140980736`);
- the **enforcement** is a bind-status rejection in `FUN_140714520`, whose status→message table sits
  at `0x1409689b0` with slot `0x1409689d0` → `BIND MAX_DEVICES_EXCEEDED` (`0x1409688e8`);
- the effective cap is shipped to clients and displayed as `MAX DEVICES :` (`0x1409d223d`).

The structural analogue in VirtualThere is the bind-status enum in `Vht.Core`, and it is the whole
point of this section that the analogue is *smaller*:

```csharp
// Vht.Core/Protocol/BindStatus.cs  —  the complete set of ways a bind can be refused.
internal enum BindStatus
{
    Success = 0,
    Error,             // malformed request / internal fault
    Unauthorized,      // shared secret or device password rejected
    PasswordRequired,  // a device password must be supplied
    DeviceBusy,        // one client per device, parity with "in use elsewhere"
    ClientLimit,       // per-connection URB/transfer guard, NOT a device cap
    Unknown,
    // NOTE: there is deliberately no MaxDevices member.
    // the reference product's equivalent slot is BIND MAX_DEVICES_EXCEEDED (0x1409688e8),
    // reached via the status table at 0x1409689b0 slot 0x1409689d0, with the cap
    // value formatted at 0x14074c669 / 0x14074c690 out of the licence blob.
    // VirtualThere has no licence blob, no cap value, and no branch to add one.
}
```

Two enforced invariants, both machine-checked, both feeding `hardware-validation-workstream`'s negative test:

1. **Test A (reflection):** `Enum.GetNames<BindStatus>()` must not contain any member matching
   `Max.*Devices|Device.*Limit`, and no `const int` in `Vht.*` may be named or documented as a device
   maximum.
2. **Test B (source + binary grep):** no source file and no built assembly may contain
   `max_devices`, `MAX DEVICES EXCEEDED`, `unlicensed`, or a purchase/licence prompt string. Legal
   documents (`THIRD_PARTY_NOTICES`, `README` legal section, `docs/RECON_AND_SPEC.md`) are excluded
   by path — they are *supposed* to name the other product's licence model, and `release-workstream` relies
   on that same scoping.

Practical ceiling, stated honestly: the limit becomes OS resources — one socket and a few hundred KB
of buffers per device. The README documents the formula (`in_flight_cap × max_transfer_size` per
endpoint) and the service's recommended handle limit. There is no number we invent and no number we
enforce.

---

## 6. Security model

**Decision: TLS 1.3 via Schannel (`SslStream`), certificate pinning by SPKI hash with
trust-on-first-use, and an HMAC challenge-response for a server-wide shared secret and per-device
passwords. Two listeners. Plaintext USB/IP is supported for stock-client interop but is structurally
incapable of serving a password-protected export and can never carry the extended handshake.**

### 6.1 Threats in scope

| Threat | Control |
|---|---|
| Passive LAN sniffing of device traffic (e.g. a keyboard's keystrokes, a disk's data) | TLS 1.3 on the TLS listener; per-device passwords force the TLS listener (§6.4) |
| Rogue server impersonation (MITM at attach time) | Server cert generated on first run; client pins the SPKI hash on first use and refuses a changed pin unless the user re-consents |
| Brute force of a device password | `PBKDF2-HMAC-SHA256`, 600 000 iterations, 16-byte per-device salt, verifier stored (never the password); HMAC challenge-response with a fresh 32-byte nonce per attempt, `CryptographicOperations.FixedTimeEquals`, token-bucket rate limiting per client IP and globally, exponential lockout |
| Malformed USB/IP frames from a hostile peer (the server parses attacker-controlled packets) | Length-prefixed parsing with hard maximums, no allocation from an unvalidated length field, a strict state machine per connection (a `CMD_SUBMIT` before `OP_REQ_IMPORT` is a protocol error, not a crash), fuzz + corrupted-frame tests |
| Downgrade to plaintext | §6.4: the extended handshake exists **only** on the TLS listener; there is no code path from a TLS session to the plaintext listener; capability negotiation is mandatory and a peer that skips it is rejected (`transport-workstream` acceptance criterion) |
| Hook scripts are a privilege-escalation surface | Hooks run as the service account; the config documents it in bold, adds a per-hook timeout (default 10 s) with kill, an allow-list of hook paths, and an option to run as a configured low-privilege user |
| Service is remotely attackable before auth | Only the two protocol listeners and the named-pipe control channel are exposed; the pipe is ACL'd to Administrators + the interactive user; the firewall rule is installed by the installer (`packaging-workstream`) |

### 6.2 Where TLS terminates, given the driver owns the socket

`usbip2_ude.sys` opens the USB/IP TCP connection itself (WSK, in kernel). Therefore:

```
vht-client                                  remote host
┌───────────────────────────┐   TLS 1.3 (+ optional LZ4)   ┌──────────────┐
│ attach target = 127.0.0.1 │ ───────────────────────────▶ │  vht-server  │
│ loopback bridge :3240     │                              └──────────────┘
└───────────────────────────┘
        ▲ plain USB/IP over loopback only
        │
   usbip2_ude.sys  (kernel, WSK)
```

**Decision: `vht-client` runs a loopback bridge.** It listens on `127.0.0.1:3240` (USB/IP's
conventional port, which the upstream driver defaults to), and for each inbound loopback connection
it resolves the requested busid to a remote `(host, port)`, opens the TLS session to that host, and
relays frames with LZ4 negotiated on the server leg only.

Why this is not a hack but the right layering:

- It is the **only** place TLS can terminate, given a WSK-in-kernel USB/IP client.
- It puts the plaintext USB/IP leg on a **loopback interface that is not reachable off-box**, so the
  weak leg is physically local — a stronger property than the reference product's two-listener model, where
  the cleartext listener is remotely reachable.
- It gives exactly one process to hold secrets, apply rate limiting, and log auth decisions.
- The driver's own plaintext framing is never exposed to the network.

Names and ports: the bridge binds `127.0.0.1` only (never `0.0.0.0`), and the bridge refuses to run
if `transport.loopback.enabled = false` while the driver is still pointed at loopback.

### 6.3 Listener layout (parity with the observed two-listener model, without cloning it)

the reference product runs a cleartext listener and a TLS listener side by side (`TCPPort` `0x140980d9f` and
`SSLPort` `0x140981127`, log lines `0x140980da7` / `0x14098115a`) — TLS is *additional*, not a
replacement. We keep that shape and fix its weakness:

| Listener | Default port | Protocol | Auth | Purpose |
|---|---|---|---|---|
| TLS | `3241/tcp` | USB/IP v1.1.1 inside TLS 1.3 with a mandatory capability handshake | shared secret + per-device password | the normal path for `vht-client` |
| Plaintext | `3240/tcp` | stock USB/IP v1.1.1, nothing else | none possible (USB/IP has no auth) | stock Linux `usbip attach` interop (`server-workstream` acceptance), LAN parity |

The plaintext listener's default is `enabled = true` for interop, and it is subject to one hard
rule:

> **§6.4 Rule (no-auth containment): a device with a per-device password is never advertised or
> attached on the plaintext listener.** It is filtered out of `OP_REP_DEVLIST` there and cannot be
> imported there. Passwords are only meaningful on the TLS listener, where the extended handshake
> carries them.

Port 3240 is chosen so a stock client works with zero flags (`usbip attach -r <host> -b <busid>`);
the README notes the conflict if `usbipd-win` is installed on the same machine, and both ports are
config keys so either can be moved.

### 6.5 Compared with the reference product's approach (not copied)

the reference product statically links **mbedTLS** (PEM/PKCS#5/PKCS#12/X509 strings, `x509_crt.c`
`0x140758396`) and exposes client-certificate/CA-file knobs (`=Client Certificate File :`
`0x1409d3140`, `=Certificate Authority File :` `0x1409d317a`). We deliberately do not adopt either
the library or its configuration surface: Schannel means no vendored crypto to patch, and
self-signed + SPKI pinning is a better default for a LAN device-sharing tool than a CA-file
ceremony. Client certificates are *not* in v1 (a follow-up card can add them; the pinning layer is
the natural place). Nothing here is derived from their implementation — it is an independent design
over the same problem, per `RECON_AND_SPEC.md` §9.

---

## 7. Discovery

**Decision: DNS-SD with service type `_vht._tcp`, implemented through Windows DNSAPI
(`DnsServiceRegister` + browse/resolve on the client), plus a permanent manual `host:port` entry
path. Auto-attach requires a recorded per-device grant; discovery alone never attaches anything.**

- **Service type: `_vht._tcp`.** Deliberately *not* `_vhusb._tcp` (the reference product's type,
  `RECON_AND_SPEC.md` §5 — using it would be parasitic and would advertise us to their clients) and
  not an invented `_usbip._tcp`, which is not a registered service type and would collide with
  anyone else's guess. `_vht._tcp.local` is ours.
- **TXT records:** `v=1`, `name=<ServerName>`, `tls=1`, `spki=<base64url SHA-256 of the server
  cert's SubjectPublicKeyInfo>`, `caps=<comma list>`. Publishing the SPKI lets a client confirm a pin
  during discovery *before* it ever opens a socket, and is what makes first-use TOFU low-friction.
- **Implementation:** Windows DNSAPI P/Invoke (`DnsServiceRegister` / `DnsServiceDeRegister` /
  `DnsServiceConstructInstance`, and browse/resolve on the client side) in `Vht.Interop.DnsApi`.
  Rationale, and a deliberate improvement over the reference product: the reference product's auto-find is a
  hard dependency on **Bonjour** — its client says *"Auto-find functionality requires Bonjour …
  Would you like to install this now?"* (`0x1409d2e0f`) and fails non-fatally at the server
  (*"There was an error registering this USB Server with Bonjour"* `0x14097da12`). Windows DNSAPI is
  in-box on Windows 10 1703+ and needs no third-party install, so VirtualThere's auto-find works on a
  clean machine. A `_vht` mDNS responder (`Makaretu.Dns`, MIT) is the portable fallback for
  non-Windows/dev test hosts — see §10 risk 8.
- **Manual entry always works:** `vht-client add host:port` writes a persistent entry; the tray
  offers the same as "Specify USB Server…"-equivalent ("Add server…"). Discovery failure degrades to
  manual, never to "no feature".
- **Auto-attach policy (the "Auto-Use" analogue):** recorded per (server, device key) with the
  qualifiers the recon documents — qualify by server name, qualify by network interface, attach
  delay and retry delay (`0x1409d31de`, `0x1409d3225`, `0x1409d3268`, `0x1409d3294`). Decision:
  auto-attach fires only for a device the user explicitly granted, on the server identity they
  granted it for; a new device seen on a known server is *never* auto-attached. The recon shows the
  reference product recording the grant before authorization succeeded and then complaining
  (`0x1409d262b`); we check authorization first and record only on success.
- **No cloud rendezvous.** `easyfind.<vendor domain>` (`0x140975eaa`) is not cloned, not called, and
  not needed for LAN use (`FEATURE_MATRIX.md` A16). Remote access beyond a LAN is a v2 problem and
  gets its own card if it is ever wanted.

---

## 8. Compression

**Decision: negotiated LZ4 *block* compression over payload frames only, applied to bulk and
interrupt transfer data, above a configurable threshold, never to control transfers or headers.
Disabled for any peer that does not negotiate it, and impossible on the plaintext listener because
the plaintext listener has no handshake to negotiate with.**

- **Library:** `K4os.Compression.LZ4` (MIT). Same algorithm family as the reference product's LZ4
  (`RECON_AND_SPEC.md` §3 — LZ4 is named in the reference product's own EULA), independent implementation, no
  shared code.
- **Vocabulary parity:** the reference product exposes `CompressionLimit` (`0x1409804d3`) server-side
  and `=Compression Threshold :` / `=bytes` (`0x1409d336a` / `0x1409d3397`) client-side, i.e. a
  byte-count threshold below which it does not bother. VirtualThere keeps the same semantic under
  `transport.tls.lz4_threshold_bytes` (default `1024`).
- **Scope:** only the data payload of `USBIP_CMD_SUBMIT` (OUT) and `USBIP_RET_SUBMIT` (IN) frames on
  bulk and interrupt endpoints. Control transfers are small and latency-critical; compressing them
  costs more than it saves. Headers are never compressed (the codec must be able to route a frame
  without decompressing it).
- **Framing:** each payload frame carries `[u8 type][u32 compressed_len][u32 uncompressed_len][u32
  crc32c]` followed by the body; `type` distinguishes raw from LZ4. If LZ4 fails to beat raw
  (`compressed_len >= uncompressed_len`), the frame is sent raw with the raw type — so compression can
  never make a transfer slower on incompressible data (encrypted archives, already-compressed JPEGs).
- **Integrity and safety:** CRC32C per frame plus the TLS AEAD underneath; a frame whose CRC fails
  is a connection error, never a silent truncation. Decompression is bounded by
  `uncompressed_len` with a hard maximum, so a hostile frame cannot be a memory bomb.
  `transport-workstream` must test corrupted frames (its acceptance criterion) — §10 risk 6 keeps that
  honest.
- **Never negotiated into the plaintext path:** stock USB/IP peers must see a byte-exact stock
  stream, so `_vht` framing exists only inside the TLS listener's post-handshake stream.

---

## 9. Repository layout and build system

**Decision: one solution, one build command, TFM pinned, driver consumed as a pinned binary
artifact. `dotnet run --project tools/Xtask -- release` produces every release artifact; CI runs the
same target.**

```
VirtualThere/
  VirtualThere.sln
  Directory.Build.props        # TargetFramework, nullable, warnings-as-errors, deterministic, SourceLink
  Directory.Packages.props     # central, pinned package versions
  LICENSE                      # Apache-2.0 (§2.3)
  src/
    Vht.Core/                  # USB/IP codec, URB model, device model, BindStatus, policy, hooks, logging   [server-workstream]
    Vht.Config/                # TOML schema + validation, persistence, device identity, DNS-SD               [config-workstream]
    Vht.Transport/             # ITransport: PlainTcp / Tls / TlsLz4                                          [transport-workstream]
    Vht.Interop/               # SetupAPI, CfgMgr32, WinUSB, SCM, DNSAPI, UDE IOCTL bindings                 [server-workstream, additive-only]
    Vht.Server/                # Windows service + capture + USB/IP server + pipe control plane               [server-workstream]
    Vht.Client/                # loopback bridge + attach/detach via UDE + auto-attach + pipe control plane  [client-workstream]
    Vht.Ui/                    # WPF tray + server/client management windows, consumes the pipe API           [ui-workstream]
    Vht.Cli/                   # `vht` verbs: serve, attach, detach, list, add, kick, ignore, nickname…
  src/Vht.*/Interop/           # P/Invoke kept in these folders only, so unsafe surface is greppable
  tests/
    Vht.Core.Tests/            # codec, URB model, policy, hook ABI, BindStatus invariants (§5.4)
    Vht.Transport.Tests/       # handshake, downgrade rejection, corrupted frames, rate limiting
    Vht.Config.Tests/          # schema, defaults, malformed-config errors, device identity fallbacks
    Vht.Acceptance/            # real-hardware multi-device harness, soak, no-licence-gate grep  [hardware-validation-workstream]
  third_party/
    usbip-win2-driver/         # pinned driver package + LICENSE.txt + SHA256SUMS + provenance README
    notices/THIRD_PARTY_NOTICES
  installer/                   # WiX v4 project: service, tray, driver install, firewall rule, uninstall
  tools/Xtask/                 # build entry point (restore → build → test → package → hash)
  docs/                        # ARCHITECTURE.md, RECON_AND_SPEC.md, FEATURE_MATRIX.md, CONFIG.md, TRANSPORT.md
  build.ps1                    # thin wrapper: dotnet run --project tools/Xtask -- %*
```

Rules that keep parallel workers out of each other's way are in Appendix A.

Build system details:

- **TFM:** `net10.0-windows` for `Vht.Interop`, `Vht.Server`, `Vht.Client`, `Vht.Ui`, `Vht.Cli`;
  `net10.0` for `Vht.Core`, `Vht.Config`, `Vht.Transport` so the protocol/config/transport layers
  stay testable without Windows-specific references.
- **Packages:** centrally pinned in `Directory.Packages.props`; every entry must appear in
  `third_party/notices/THIRD_PARTY_NOTICES` with its licence (Appendix D).
- **Portable build:** `Xtask portable` publishes self-contained single-file `vht-server.exe`,
  `vht-client.exe`, `vht-tray.exe` + `vht.exe` into `dist/portable/`; no installer, no service
  registration — the server runs as a console app, which is the documented "portable Windows server"
  parity item (`packaging-workstream`).
- **Config location:** `%ProgramData%\VirtualThere\config.toml` for the service,
  `%LOCALAPPDATA%\VirtualThere\config.toml` for the client/tray, each overridable by
  `--config <path>` and `VHT_CONFIG`. Portable builds keep config next to the binary when a
  `portable.marker` file is present.
- **Versioning:** one version for all binaries from `Vht.Core` `AssemblyInformationalVersion`;
  auto-update = manual for v1, stated plainly in the README (`packaging-workstream` deliverable 6).

---

## 10. Risk register

Severity: **H** blocks a card's acceptance criteria, **M** degrades parity, **L** cosmetic/declared.
"Verify in" names the card that must produce evidence.

| # | Risk | Sev | What we know | Mitigation / decision | Verify in |
|---|---|---|---|---|---|
| 1 | **Kernel driver signing** | H | No `cl.exe`, no WDK, no cert on this machine. Microsoft's attestation-signing prerequisites require an **EV code-signing certificate** and a Partner Center hardware-dashboard account; EV is the only accepted path for driver submissions (Microsoft Learn: *Attestation sign Windows drivers*, *Driver code signing requirements*) | Never build a driver in v1. Consume `usbip-win2`'s already-signed package (§3). If our own driver is ever needed, budget the EV certificate, and note that the *attestation submission itself* is free while the certificate is not. `packaging-workstream` documents the exact steps and ships unsigned/test-mode builds with the caveat, as its card allows | `packaging-workstream` |
| 2 | **Driver redistribution terms** | M | Upstream driver is BSD-2-Clause, so redistribution is allowed with attribution; the *catalog* signature is theirs | Ship the upstream package **verbatim**, never re-package; pin version + SHA-256 in `third_party/usbip-win2-driver/SHA256SUMS`, include `LICENSE.txt`, and record provenance. Re-signing a modified package would require our own attestation submission | `packaging-workstream` |
| 3 | **Third-party driver device ceiling** | H | Attach allocates a virtual port ("successfully attached to port 1"); the upstream README does not state a port limit per controller instance — **unverified** | Measure it (`hardware-validation-workstream` wants ≥8). If a ceiling exists: instantiate additional controller instances, and if that is not possible, report the ceiling honestly and contribute upstream. This is the one place a device count can come from in VirtualThere, and it is outside our code | `hardware-validation-workstream` |
| 4 | **WinUSB claim conflicts / rebind damage** | H | Reference products both need a filter driver to make this painless; our user-mode path P2 re-enumerates the device | Enumerate first, never rebind silently: a device is only rebound when the operator opts in (config or per-device action). Record the original INF/driver before rebinding and implement handback (`onPassToKernel` parity). Refuse-and-report for the classes in §4.3 | `server-workstream` |
| 5 | **Devices user-mode cannot capture** | M | §4.3 list; the reference product's filter driver covers more | Declared gap, surfaced in the UI with a reason string, not a silent skip. v2 filter-driver card only if measured demand exists | `server-workstream`, `ui-workstream` |
| 6 | **Isochronous transfer support** | M | USB/IP carries ISO natively (`number_of_packets`, `iso_packet_descriptor`, `start_frame` — kernel protocol doc). WinUSB's ISO support exists — Microsoft documents "Send USB Isochronous Transfers From a WinUSB Desktop App" and "isochronous transfers are supported in Windows 8.1 or later" — but third-party reports document real limits (libusb issue #541: frame-length/1 ms constraints). The UDE side's ISO behaviour is **unverified** | v1 targets control/bulk/interrupt as *required*, ISO as *best-effort* behind a runtime capability flag that must be negotiated by both ends. Affected classes (USB audio streaming, UVC video) are named as unsupported-by-default in the honest device-class matrix rather than silently half-working | `client-workstream`, `hardware-validation-workstream` |
| 7 | **USB 3.x / SuperSpeed high-bandwidth endpoints** | M | Protocol and endpoint sizes are fine in principle; no evidence gathered | Test on a USB 3.x device and a high-bandwidth interrupt endpoint; report measured numbers. Tune the in-flight cap per endpoint speed | `hardware-validation-workstream` |
| 8 | **mDNS 5353 contention on Windows** | L | Windows 10+ runs its own mDNS responder; whether a third-party process can reliably bind 5353 alongside it is **unverified** | Primary path is in-box DNSAPI (§7), which delegates to the OS responder instead of competing for the socket; a userspace responder is only a dev/non-Windows fallback | `config-workstream` |
| 9 | **Hook scripts run with service privilege** | M | Reference behaviour: hooks are `cmd.exe /C <script>` invoked by the service, with an exit-code ABI and `$CLIENT_ID$`/`$CLIENT_IP$` substitution (`FUN_1407443c0` @ `0x1407443c0`) | Same ABI and vocabulary (behavioural parity), hardened: per-hook timeout with kill, path allow-list, documented privilege warning, optional low-privilege run-as. Never invoked on the plaintext listener's unauthenticated path | `server-workstream` |
| 10 | **Codec/framing bugs reachable from the network** | H | The server parses attacker-controlled USB/IP frames and (optionally) LZ4 frames | Bounded parsing, no allocation from unchecked lengths, per-connection state machine, fuzz corpus in `tests/Vht.Core.Tests/Fuzz`, corrupted-frame tests required by `transport-workstream` | `server-workstream`, `transport-workstream` |
| 11 | **No git repo, no CI, no remote yet** (verified) | L | `G:\Repos\VirtualThere` is not a git repository and has no remote | `release-workstream` initialises it, adds CI that runs `Xtask release`, and reports publication blocked on credentials rather than claiming a push | `release-workstream` |
| 12 | **Dependency licence drift** | L | Crate/NuGet graphs change | CI job: `dotnet list package --include-transitive` diffed against an allow-list in `third_party/notices/`. Any new licence must be added deliberately | `release-workstream` |
| 13 | **Parallel workers colliding in one repo** | M | All five subsystem cards are `assignee=default` with **scratch** workspaces, yet the docs and packages live in `G:\Repos\VirtualThere` | Appendix A assigns each card a disjoint write scope in the shared repo and marks the one hotspot (`Vht.Interop`) additive-only. Implementers must `cd G:\Repos\VirtualThere`; the card's scratch directory is for throwaway test output only | all cards |

---

## Appendix A — Dependency graph and write-scope ownership

Card ids are the real board ids. Everything listed is already created on the board; this appendix
exists so the parent can confirm parallel work is safe.

```
recon-workstream  RECON_AND_SPEC.md + FEATURE_MATRIX.md            [DONE]
     │
     ▼
architecture-workstream  ARCHITECTURE.md  (this document)                 ← freezes the interfaces below
     │
     ├──────────────┬───────────────┬───────────────┬────────────────────┐
     ▼              ▼               ▼               ▼                    ▼
server-workstream     config-workstream      transport-workstream      client-workstream          ui-workstream
vht-server     vht-config      vht-transport   vht-client          vht-ui
(core+server)  (config+disco)  (TLS+LZ4+auth)  (attach+multi)      (tray/UI)
     │              │               │               │                    │
     ├──────────────┴───────────────┴───────────────┴────────────────────┤
     ▼                                                                   ▼
hardware-validation-workstream  vht-acceptance (multi-device parity, soak, no-gate proof)   packaging-workstream  vht-packaging
     │              (parents: 0dd90168, 0505b536, 72c9add2, 82eca965)    │  (parents: 0dd90168, 2344c15e, 82eca965)
     └──────────────────────────────┬──────────────────────────────────────┘
                                    ▼
                        release-workstream  vht-release (repo, licence, docs, publish)
```

Parallel safety rests on five interfaces frozen *here*, so no two cards decide the same thing:

| Frozen interface | Shape fixed in | Consumed by |
|---|---|---|
| `BindStatus`, URB model, device model, hook event vocabulary + exit-code ABI, codec | §5.4, Appendix C | server, client, transport, UI |
| `ITransport` (3 implementations) | Appendix C | server, client, transport |
| Config schema keys + TOML format + device identity | Appendix C, §9 | config, server, client, UI, packaging |
| Control-plane pipe protocol (`\\.\pipe\vht-{server,client}`, JSON lines) | §1, Appendix C | UI, server, client |
| Driver boundary: `Vht.Interop.Ude` IOCTL surface + loopback bridge port | §3.3, §6.2 | client, packaging, acceptance |

**Write scopes** (a card may create and edit files only under its own paths; anything else needs a
comment on that card first — this is the answer to risk 13):

| Card | Owns | May read, must not edit |
|---|---|---|
| `server-workstream` | `src/Vht.Core/**`, `src/Vht.Interop/**`, `src/Vht.Server/**`, `src/Vht.Cli/**`, `tests/Vht.Core.Tests/**` | everything else |
| `config-workstream` | `src/Vht.Config/**`, `tests/Vht.Config.Tests/**` | `Vht.Core` types |
| `transport-workstream` | `src/Vht.Transport/**`, `tests/Vht.Transport.Tests/**`, `docs/TRANSPORT.md` | `Vht.Core` types |
| `client-workstream` | `src/Vht.Client/**` | `Vht.Interop`, `Vht.Transport`, `Vht.Config` |
| `ui-workstream` | `src/Vht.Ui/**`, `docs/UI.md` | all service code |
| `packaging-workstream` | `installer/**`, `tools/Xtask/**`, `build.ps1`, `third_party/**` | all `src/**` |
| `hardware-validation-workstream` | `tests/Vht.Acceptance/**`, `docs/ACCEPTANCE.md` | all `src/**` |
| `release-workstream` | `README.md`, `CONTRIBUTING.md`, `LICENSE`, `.github/**`, `docs/user/**`, `third_party/notices/**` | all `src/**` |

**Hotspot flag:** `src/Vht.Interop/**` (risk 13). Three cards need P/Invoke (server: WinUSB/SetupAPI;
client: UDE IOCTLs; packaging: driver install). Rule: it is owned by `server-workstream`; other cards add
**new files in a sub-namespace** (`Vht.Interop.Ude`, `Vht.Interop.DriverInstall`) and never edit an
existing file there. If that rule breaks down, the interop library gets split into its own cards
before more work lands on it.

Two ordering notes for the dispatcher/parent:

1. `Vht.Core` is the shared dependency. It is owned by `server-workstream`; the other four cards are written
   against the shapes fixed in Appendix C, not against code that must exist first. If `server-workstream`
   has not landed, the others compile against their own copies of the minimal types in their test
   projects — no card waits on another for a build.
2. All five cards should work in `G:\Repos\VirtualThere` (the repo holding the docs), not in their
   own scratch directories, despite `workspace_kind=scratch`. Scratch is for build output only.

## Appendix B — Bootstrap: starting `vht-server` from this document alone

Concrete first milestone for `server-workstream`, executable on this machine (needs Administrator):

1. `dotnet new sln -n VirtualThere` in `G:\Repos\VirtualThere`; `Directory.Build.props` with
   `net10.0`/`net10.0-windows` and nullable+warnings-as-errors.
2. `dotnet new classlib -o src/Vht.Core`, `-o src/Vht.Interop`, `-o src/Vht.Server`;
   `dotnet new xunit -o tests/Vht.Core.Tests`; add all to the solution.
3. In `Vht.Core`, implement the protocol codec first, against
   `https://docs.kernel.org/usb/usbip_protocol.html` (version `0x0111`; the documented example dump
   is a ready-made golden test vector):
   - `usbip_header_basic` (20 bytes: command, seqnum, devid, direction, ep), big-endian fields;
   - commands: `OP_REQ_DEVLIST 0x8005`, `OP_REP_DEVLIST 0x0005`, `OP_REQ_IMPORT 0x8003`,
     `OP_REP_IMPORT 0x0003`, `USBIP_CMD_SUBMIT 1`, `USBIP_CMD_UNLINK 2`, `USBIP_RET_SUBMIT 3`,
     `USBIP_RET_UNLINK 4` (verify the exact op codes against the kernel doc before freezing them in
     code, and take the devlist/import reply layouts from the same page);
   - `USBIP_CMD_SUBMIT` layout: `transfer_flags @0x14`, `transfer_buffer_length @0x18`,
     `start_frame @0x1c` (ISO only, else 0), `number_of_packets @0x20` (`0xffffffff` when not ISO),
     `interval @0x24`, `setup @0x28` (8 bytes), then the transfer buffer then ISO descriptors;
   - `DNAME`-style device records for the devlist reply: path, busid, busnum, devnum, speed, vid,
     pid, bcdDevice, class/subclass/protocol, configuration value, and counts.
4. In `Vht.Interop`, the WinUSB capture surface: `SetupDiGetClassDevs(GUID_DEVINTERFACE_USB_DEVICE)`,
   `SetupDiEnumDeviceInterfaces`, `SetupDiGetDeviceInterfaceDetailW` (two-call size query),
   `CM_Get_Device_IDW`, `CreateFileW` on the interface path, `WinUsb_Initialize`,
   `WinUsb_QueryInterfaceSettings` / `WinUsb_QueryPipe`, `WinUsb_ControlTransfer`,
   `WinUsb_ReadPipe`/`WinUsb_WritePipe` (overlapped), `WinUsb_AbortPipe`/`WinUsb_ResetPipe`,
   `CM_Register_Notification` for hotplug. All in `Vht.Interop/WinUsb/`.
5. `Vht.Server` skeleton: `BackgroundService` hosting a `TcpListener`, one accept loop per listener
   (§6.3 ports), one task per connection running the state machine
   `handshake → OP_REQ_DEVLIST/IMPORT → submit loop → teardown`; a `DeviceExporter` bound to the
   `ExportTable`/`AttachTable` of §5.1; `Console.WriteLine` logging first, `EventLog`/file logging
   after.
6. First proof, before any TLS work: `usbip list -r <this machine>` from a Linux client (or WSL with
   the `usbip` client package) lists the exported devices, and `usbip attach -r <this machine> -b
   <busid>` attaches two different local devices simultaneously. That single test is the project's
   headline requirement and it needs none of the transport card's work — it is why the transport
   card is stubbed behind `ITransport` with a `PlainTcpTransport` implementation from day one.
7. Service wrapper last: `-b install|uninstall` (SCM via `Vht.Interop.ServiceControl`), then
   `Microsoft.Extensions.Hosting.WindowsServices` for the service host, matching the observed
   CLI subset `-c -h -r -b -q` (`FEATURE_MATRIX.md` A26).

## Appendix C — Frozen interfaces

Protocol/state enums and their refusal members, as fixed for all cards (see §5.4 for the
deliberate absence of a device-count member):

```csharp
public enum BindStatus { Success, Error, Unauthorized, PasswordRequired, DeviceBusy, ClientLimit, Unknown }
public enum CaptureState { Capturable, UnsafeToExport, CaptureUnsupported, AlreadyAttached, Ignored }
public enum CaptureReason { None, SystemCritical, HostsRunningOs, ManagementNetwork, DriverHoldsDevice, NoWinUsbInterface, IsoUnsupported, Policy }
```

Transport abstraction (wire behaviour is `transport-workstream`'s to define; the shape is fixed here):

```csharp
public interface ITransport : IAsyncDisposable
{
    TransportKind Kind { get; }                 // PlainTcp | Tls | TlsLz4
    TransportCapabilities Capabilities { get; } // HandshakeRequired, Lz4, DevicePassword, SharedSecret
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);
    Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct);
}
```

Rules attached to it: `TlsLz4` may not exist without `Tls`; `PlainTcp` reports
`Capabilities = None` and `AuthenticateAsync` must fail closed with `Unauthorized` rather than no-op;
`HandshakeRequired` is always true for `Tls`/`TlsLz4`.

Hook vocabulary (behavioural parity, same exit-code ABI — `1` = yes/allow, `0` = no, anything else
logged): `onBind`, `onUnbind`, `onEnumeration`, `onReset`, `onClearHalt`, `onResetEp`,
`onDeviceUnplug`, `onPassToKernel`, `onChangeNickname`, `onClientConnect`, `onClientDisconnect`,
`OnDeviceKick`, `OnDeviceIgnore`, `OnDeviceUnignore`, `OnServerRename`. Substitutions: `$CLIENT_ID$`,
`$CLIENT_IP$`, `$NICKNAME$`, `$NEW_NICKNAME$`, `$DEVPATH$`, `$VENDOR_ID$`, `$PRODUCT_ID$`. All are
unconditional in VirtualThere — the reference product gates several of them behind a licence
(`RECON_AND_SPEC.md` §1.3); we have no gate to remove because we never write one.

Control plane (`\\.\pipe\vht-server`, `\\.\pipe\vht-client`), JSON lines, one request/response pair
per line: `{"verb":"devices.list"}`, `devices.kick`, `devices.ignore`, `devices.unignore`,
`devices.nickname`, `devices.password`, `server.status`, `server.start`, `server.stop`,
`log.tail`, `clients.attach`, `clients.detach`, `peers.add`, `peers.list`. Unknown verbs are
rejected with an error object, never ignored.

Config format: **TOML**, versioned (`[general] config_version = 1`), validated on load, unknown keys
warn (not fail). Rationale: comments survive round-trips, no delimiter-in-value class of bug (the
reference product's nickname store is comma-delimited, hence its "nickname cannot contain a comma"
rule at `0x1409d29e2`), and it is the format the USB/IP audience already edits by hand. Keys mirror
the observed configuration vocabulary where a user would expect it (`ServerName` → `server.name`,
`AllowedDevices`/`IgnoredDevices` → `policy.allow`/`policy.ignore`, `TCPPort`/`SSLPort` →
`transport.plaintext.port`/`transport.tls.port`) — vocabulary parity, not a copy of its blob layout.

## Appendix D — Third-party components and licences

Every component below is compatible with VirtualThere shipping under **Apache-2.0** (§2.3). The
release card must copy this table into `THIRD_PARTY_NOTICES` and keep it current; the CI licence
check (risk 12) is the mechanism.

| Component | Version at decision time | Licence | Used as | Compatible? |
|---|---|---|---|---|
| `vadimgrn/usbip-win2` driver package (`usbip2_ude.sys`, `usbip2_filter.sys`, INF/cat) | v0.9.8.1 (2026-09-27) | **BSD-2-Clause** | the client-side virtual USB host controller, shipped verbatim + attributed | Yes (permissive, attribution only) |
| `vadimgrn/usbip-win2` headers (IOCTL/struct declarations) | same | BSD-2-Clause | re-declared in `Vht.Interop.Ude` | Yes (attribution) |
| USB/IP protocol specification (Linux kernel documentation) | v1.1.1 (`0x0111`) | specification, GPL-2.0 docs | read as the protocol reference; **no kernel code copied** | Yes (specification, not code) |
| .NET 10 runtime / BCL (`SslStream`/Schannel, `System.IO.Pipes`, `System.Text.Json`) | 10.0.x | MIT | language runtime + TLS + IPC | Yes |
| WPF / WinForms (`NotifyIcon`) | .NET 10 | MIT | UI + tray | Yes |
| `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.x | MIT | service host + lifecycle | Yes |
| `K4os.Compression.LZ4` | latest at pin | MIT | LZ4 block compression (§8) | Yes |
| `Tomlyn` | latest at pin | BSD-2-Clause | TOML config parsing | Yes |
| `Makaretu.Dns` (fallback only) | latest at pin | MIT | mDNS responder where DNSAPI is unavailable | Yes |
| `H.NotifyIcon.Wpf` **or** WinForms `NotifyIcon` | latest at pin | MIT / MIT | tray icon | Yes |
| WiX Toolset v4 (installer authoring) | 4.x | MS-RL | **build-time tool only**, not redistributed in any artifact | Yes (not distributed) |
| xUnit / Microsoft.NET.Test.Sdk (test projects) | latest at pin | Apache-2.0 / MIT | tests only, not shipped | Yes |
| **Not used — recorded because they were evaluated:** `dorssel/usbipd-win` | 5.x | **GPL-3.0-only** | rejected — its drivers are a GPL-3.0 binary redistribution of VirtualBox 7.2.20 USB filter drivers; would force GPL-3.0 on VirtualThere | No (deliberately avoided) |
| **Not used:** the reference product's mbedTLS configuration surface | — | — | rejected in favour of Schannel (§6.5) | n/a |
| **Not used:** `Hardcodet.NotifyIcon.Wpf` | — | CPOL (not OSI-approved) | rejected for the tray | No |

## Appendix E — What this document deliberately does not decide

Kept out so no two cards decide the same thing, each with its owning card:

- Wire-format byte details of the `_vht` extension handshake and LZ4 frame layout → `transport-workstream`
  (`docs/TRANSPORT.md`), within the §8 constraints.
- Exact TOML key names beyond the vocabulary mapping in Appendix C, and the device-identity fallback
  chain for serial-less devices → `config-workstream`.
- UI layout, iconography, branding copy → `ui-workstream`.
- Installer technology details and the release-build target's internal steps → `packaging-workstream`.
- Whether a VirtualThere kernel filter driver is ever warranted → deferred to a future card, gated
  on `hardware-validation-workstream`'s measured capture gaps (§4.3, risk 1/5).
