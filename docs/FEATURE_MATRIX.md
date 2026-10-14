# VirtualThere — feature parity matrix

Source of truth for every claim below: `docs/RECON_AND_SPEC.md` (REA/Ghidra recon over
`vhusbdwinw64.exe` + `vhui64.exe`). Addresses here are the same absolute VAs.

Columns:
- **the reference product (free tier)** — what the free build does.
- **the reference product (paid tier)** — what a licence unlocks.
- **VirtualThere-target** — what we build. VirtualThere has **no licence tier at all**: the paid
  column is simply our baseline.
- **Hard/Easy** — implementation difficulty. *Hard* = kernel/driver-level work or real
  protocol engineering. *Easy* = config, policy, or UI work on top of an existing mechanism.

## A. Shipped behaviour

| # | Capability | the reference product (free tier) | the reference product (paid tier) | VirtualThere-target | Hard/Easy |
|---|---|---|---|---|---|
| A1 | Share USB devices from a Windows host | 1 device per server (EULA `0x14097eae0`) | unlimited / per-licence count | unlimited, always | **Hard** — needs device capture + a `vhfilter`-class driver or `usbipd-win` |
| A2 | Enforce the device cap | `BIND MAX_DEVICES_EXCEEDED` (`0x1409688e8`), cap from the licence blob field (`0x140980736`, logged as `max_devices=%d` `0x140980ce0` @ `0x14074c669`) | `max_devices=unlimited` (`0x140980cef` @ `0x14074c690`) | no cap code path exists | Easy (delete the check) |
| A3 | `onClientConnect` hook | refused: `The onClientConnect event requires a licensed server!` (`0x140980285`, gate `0x1407468e9` in `FUN_140746630`) | runs `cmd.exe /C <script>` | always runs | Easy |
| A4 | `onClientDisconnect` hook | refused (`0x1409801e3`, gate `0x140715858` in `FUN_140714520`) | runs | always runs | Easy |
| A5 | `OnDeviceKick` (force-takeover) | refused (`0x14096831b`, gate `0x140716bc9`) | runs | always runs | Easy (policy) — but see D2 |
| A6 | `OnDeviceIgnore` / `OnDeviceUnignore` | refused (`0x140968575` @ `0x140714920`, `0x1409686ee` @ `0x140742fa0`) | runs | always runs | Easy |
| A7 | `ClientAuthorization` / `ClientDeauthorization` + reverse-client hooks | refused (`0x140968807`, `0x14096844f`, `OnAddReverseClient` `0x14097ff8d`, `OnRemoveReverseClient` `0x140980048`) | runs | always runs | Easy |
| A8 | `onServerRename` | refused (`0x14097fde4`) | runs | always runs | Easy |
| A9 | Free hooks: `onBind`/`onUnbind`/`onEnumeration`/`onReset`/`onDeviceUnplug`/`onClearHalt`/`onResetEp`/`onPassToKernel`/`onChangeNickname` (`0x1409682ae`…`0x14097fed2`) | available | available | available | Easy |
| A10 | Hook ABI: script exit code = hook return value (1 = yes/allow, 0 = no; anything else logged) — `FUN_1407443c0` @ `0x1407443c0` | same | same | same contract, re-implemented | Easy |
| A11 | Hook substitution vars `$CLIENT_ID$` `$CLIENT_IP$` `$NICKNAME$` `$NEW_NICKNAME$` `$DEVPATH$` `$VENDOR_ID$` (`0x14097feb8`…) | same | same | same vocabulary | Easy |
| A12 | Device nicknames (`DeviceNicknames` `0x14097fee3`), comma-delimited blob, integrity-checked (`0x1409800ea`, `0x140980143`) | yes | yes | yes, JSON store (no comma rule needed) | Easy |
| A13 | Allow / ignore lists (`AllowedDevices` `0x1409681c3`, `IgnoredDevices` `0x140980066`), keyed by (vid,pid,serial,server) — client UI `0x1409d2366`, `0x1409d23da` | yes | yes | yes | Easy |
| A14 | Per-device password prompt (`BIND_PASSWORD_REQUIRED` `0x14096892e`; client `0x1409d2b0a`, `saveablePasswordDialog` `0x1409ccb05`, `=Remember` `0x1409d1fa8`) | yes | yes | yes, with remember option | Easy |
| A15 | Auto-discovery via mDNS/DNS-SD, service type `_vhusb._tcp` (`0x1409809ac`), DNSAPI (`0x140987bda`) | yes (disabled by `-n` `0x14097f73f`) | yes | yes | Easy–Medium (use Bonjour/avahi or our own mDNS) |
| A16 | Vendor cloud matchmaker `easyfind.<vendor domain>` (`0x140975eaa`) | yes | yes | **not cloned** — internal LAN discovery + manual `host:port` only | Easy (skip) |
| A17 | Auto-attach ("Auto-Use") policy: per device/port/server/global, qualify by server name `0x1409d31de` or interface `0x1409d3225`, delay `0x1409d3268`, retry delay `0x1409d3294` | yes | yes | yes, same knobs | Easy |
| A18 | Manual server add `host:port` (`0x1409d2bbc`), server rename (`0x1409d2a27`) | yes | yes | yes | Easy |
| A19 | Two listeners: cleartext `TCPPort` (`0x140980d9f`, log `0x140980da7`) + TLS `SSLPort` (`0x140981127`, log `0x14098115a`); mbedTLS statically linked | yes | yes | **no TLS on the wire in v1** — USB/IP is cleartext; see §C | Medium (or free, if skipped) |
| A20 | LZ4 compression with `CompressionLimit` (`0x1409804d3`; client `=Compression Threshold :` `0x1409d336a`) | yes | yes | optional, later | Medium |
| A21 | Hide/deprioritise USB devices from the host kernel (`AutoAttachToKernel` `0x1409809bd`, `vhfilter` `0x140974d00`) | yes | yes | yes | **Hard** — driver + binding work |
| A22 | URB-level forwarding: per-endpoint clear-halt/reset-pipe, iso buffers, in-flight URB accounting (`0x1409686ab`) | yes | yes | yes, via USB/IP URB model | **Hard** |
| A23 | Client attaches real devices locally (VHCI-style) with hotplug | yes | yes | yes (`usbipd attach`) | **Hard** |
| A24 | Tray UI + system-messages log viewer + `=Debug Mode` + `-r` file logging | yes (XRC `0x1409786d0`) | yes | yes | Easy |
| A25 | Windows Service install/uninstall (`-b`, Event Log source `0x14097758a`); client service too (`0x1409d1678`, `0x1409d270c`) | yes | yes | yes | Easy–Medium |
| A26 | CLI: `-b -c -h -l -i -r -q -n` (verbatim usage at `0x14097f5a0`) | yes | yes | keep the useful subset: `-c -h -r -b -q` | Easy |
| A27 | Config file + registry-backed settings (`DeviceIdMap` `0x140976440`, `DeviceInterfaceGUID` `0x140975638`) | yes | yes | config file only (portable-friendly) | Easy |
| A28 | Client licence manager (`=License...`, `0x1409d1eae`–`0x1409d1f41`) and per-server-limit client licensing | present | present | **absent** — no client licensing at all | Easy (skip) |
| A29 | Client shows server licensing to the user (`=Please purchase a license to share more devices simultaneously` `0x1409d2ccd`) | n/a | n/a | **absent** — nothing to warn about | Easy |
| A30 | Hook surface on the client (`=Custom Event Handler...` `0x1409d1b74`) | yes | yes | yes (optional) | Easy |
| A31 | Reverse connections (client accepts inbound; `Enable Reverse Connections/SSL` `0x1409d1d54`/`0x1409d1d97`) | yes | yes | v2 (needs a NAT-traversal design) | Medium–Hard |
| A32 | KVM-over-IP line (virtual dongle, HID passthrough, hub port power, alternate-host switch; `kvmip/*.cpp` paths `0x1409b48d1`–`0x1409c0ad8`, `=Attach Port 1 to Alternate Host` `0x1409b7a39`) | yes | yes | **explicitly out of scope v1** | Hard (and separate product) |

## B. What "paid" actually means for us

| Paid gate | Gate string / address | Our replacement |
|---|---|---|
| Device count | `BIND MAX_DEVICES_EXCEEDED` `0x1409688e8`; cap = licence blob field (`0x140980736`), reported at `0x14074c669` / `0x14074c690` | no count check exists |
| Client-connect/disconnect scripts | `0x140980285` / `0x1409801e3` | unconditional |
| Kick/ignore/unignore | `0x14096831b` / `0x140968575` / `0x1409686ee` | unconditional |
| Client authorization + reverse clients | `0x140968807` / `0x14096844f` / `0x14097ff8d` / `0x140980048` | unconditional |
| Server rename | `0x14097fde4` | unconditional |
| Shared plumbing | `FUN_1407443c0` @ `0x1407443c0` — one `strcmp(DAT_140a5f258,"unlicensed")` in front of every paid hook | there is no `DAT_140a5f258`; nothing to bypass |

Because every paid feature funnels through one helper plus one cap constant, "removing the gate"
is not a reverse-engineering exercise for us — we are writing new code with no gate in it. That is
why VirtualThere is only a *shipping* problem, never a *cracking* problem.

## C. Hard vs easy — the honest split

**Hard (driver-level, real engineering, do these first):**

1. **Device capture + host-side detach on Windows** (A1, A21, A22). the reference product ships a
   filter/bus driver (`\??\GLOBALROOT\Device\vhfilter`, `0x140974d00`) and rebinds devices to
   WinUSB. `usbipd-win` already solves this class of problem on Windows; our work is adopting and
   hardening it, not inventing it.
2. **Client-side virtual host controller / attach** (A23). Real devices must appear to the client
   OS. `usbipd attach` + a VHCI-class path. Nothing about this is a config problem.
3. **URB fidelity** (A22). Bulk/interrupt/iso correctness under load is where USB-over-IP products
   actually fail. The recon confirms the reference product tracks per-endpoint halts, per-endpoint resets,
   iso buffers and in-flight URB counts — that is the shape of the work, not a feature list.

**Easy (config/UX on top of the above):**

4. Everything in A3–A18, A24–A28, A30 — hooks, nicknames, allow/ignore lists, passwords,
   auto-attach policy, tray UI, service install, config file, CLI. These are ordinary application
   work: a settings file, a tray icon, and a couple of `CreateProcess` calls for hooks.
5. **The licensing story** (A2, A28, A29): zero code. We simply do not implement it, and the
   parity row that costs the reference product $ is free for us.

**Deliberately not done:**

6. Wire compatibility with the reference product (see §D).
7. `easyfind.<vendor domain>` cloud matchmaking (A16) — using their service would be parasitic, and
   it is unnecessary for LAN use.
8. KVM-over-IP (A32) — a separate product line that happens to share the client binary.

## D. Wire compatibility: explicitly NOT a goal

**VirtualThere will not be wire-compatible with the reference product, and this is a requirement, not a
limitation.** Reasons, in order:

1. **Legal.** The shipped EULA (`0x14097eae0`) forbids *"modify, translate, reverse engineer or
   decompile the Software"* and *"create derivative works based on the Software."* A byte-exact
   reimplementation of their proprietary protocol is a derivative work. Clean-room feature parity
   is not.
2. **Technical.** The protocol is undocumented and has both a cleartext listener (`TCPPort`
   `0x140980d9f`) and a TLS listener (`SSLPort` `0x140981127`) with bespoke framing on top of
   mbedTLS and LZ4; anything we deduced about framing would still be a guess, and would break on
   their next release.
3. **Strategic.** USB/IP is a published standard with a Windows implementation already in the wild.
   Building on it gives unlimited devices and a documented protocol for free.

Consequence to design around: a VirtualThere client cannot talk to a reference-product server and vice
versa. Parity is **behavioural and config-vocabulary parity** only.

## E. Acceptance checklist mapping (this card)

| Card acceptance criterion | Where satisfied |
|---|---|
| Every claim carries a hex address or is labelled INFERRED | `RECON_AND_SPEC.md` — tag used throughout; §7 states its client evidence method (raw byte scan) because Ghidra could not complete the 15.7 MB client within the tool timeout |
| `max_devices` cap path located in code (procedure + address), not just strings | `RECON_AND_SPEC.md` §1.4 — value decided in `FUN_14074bbb0` (`0x14074c669` / `0x14074c690`), enforced in `FUN_140714520` via the BIND status table at `0x1409689b0` (slot `0x1409689d0` → `0x1409688e8`), bind success at `0x140717dad` |
| Three paid-only script hooks have gate sites identified | `RECON_AND_SPEC.md` §1.3 — `OnDeviceKick`→`0x140716bc9`, `onDeviceIgnore`→`0x140714920`, `onDeviceUnignore`→`0x140742fa0`; plus `onClientConnect`→`0x1407468e9`, `onClientDisconnect`→`0x140715858` |
| Doc states plainly that VirtualThere will NOT be wire-compatible, and why | `RECON_AND_SPEC.md` §9 and §D above (EULA derivative-works clause) |
