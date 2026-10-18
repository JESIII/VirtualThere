# VirtualThere — `vht-client`

Owner card: `client-workstream`. Frozen interfaces this implements: `docs/ARCHITECTURE.md` §3 (reuse the
`usbip-win2` UDE driver), §5 (no device-count concept), §6.2 (loopback bridge), §7 (auto-attach only
after a recorded grant), Appendix C (`ITransport`). Parity target: `docs/RECON_AND_SPEC.md`,
`docs/FEATURE_MATRIX.md`.

Code lives in `src/Vht.Client/**` (product) and `src/Vht.Client/Tests/**` (this card's tests —
`tests/` is split between the sibling cards, Appendix A). UDE P/Invoke is in
`src/Vht.Client/Interop/Ude/` under the agreed namespace `Vht.Interop.Ude`; moving it to
`src/Vht.Interop/Ude/` is a `git mv` with no code change once that project exists.

## What it does

`vht-client` connects to `vht-server` peers over USB/IP, lists their exported devices, and attaches a
selected device to this machine through a **virtual USB host controller that Windows enumerates
normally** — so the device is usable by any application (no per-app shim, no filter driver per
device). Any number of devices may be attached at once.

```
remote peer ──IClientTransport (plain TCP stub | TLS | TLS+LZ4)──▶ LoopbackBridge (127.0.0.1:3240)
        ◀── UDE driver opens ITS OWN socket to 127.0.0.1 (in kernel, WSK) ──▶
        ──▶ usbip2_ude.sys allocates a virtual port ──▶ Windows enumerates a real USB device
```

## Deliverable status

| # | Deliverable | Where | State |
|---|---|---|---|
| 1 | VHCI integration: virtual host controller Windows enumerates | `Attach/UdeDeviceController.cs`, `Interop/Ude/{UdeNative,UdeConstants,UdeBufferCodec}.cs` | Implemented. Speaks the driver's IOCTL ABI directly (`PLUGIN_HARDWARE`, `PLUGOUT_HARDWARE`, `GET_IMPORTED_DEVICES`, `STOP_ATTACH_ATTEMPTS`); never shells out to `usbip.exe`. **Requires the pinned driver to be installed** — see below. |
| 2 | USB/IP client: connect, list, attach, detach | `Peers/{PeerStore,RemoteDeviceCatalog}.cs`, `Usbip/{UsbipProtocol,UsbipOp,UsbipOpWriter,UsbipDeviceRecord}.cs`, `Bridge/LoopbackBridge.cs` | Implemented (`OP_REQ_DEVLIST`, `OP_REQ_IMPORT`, version check 0x0111, reply validation). |
| 3 | Multi-device, no cap | `Attach/AttachManager.cs`, `Attach/IDeviceController.cs` | Implemented as a property of the data structures: `ConcurrentDictionary` keyed per device, one IOCTL/port per attach, no counter, no slots, no maximum anywhere in `Vht.Client`. |
| 4 | Transfer support + honest class matrix | driver executes URBs; client relays the byte stream. Matrix below. | control/bulk/interrupt: required path, unverified on hardware here. **Isochronous: unsupported by default.** |
| 5 | Hotplug UX + clean degrade on network loss | `Attach/AttachManager.cs` (`ReconcileAsync`), `Bridge/LoopbackBridge.cs` (relay-ended events), `Control/ClientControlServer.cs` (events for the client window) | Implemented: 4 reconcile cases (port vanished, peer unreachable, peer stopped exporting, transport ended) each detach locally and queue a re-attach with a retry window; no reboot, no restart. |
| 6 | Auto-attach policy with allow/deny | `Policy/AutoAttachPolicy.cs` | Implemented: ignore list (deny), allow list (restrict), per-device grant recorded **only after** a successful attach; attach delay + retry delay configurable. |
| 7 | Per-device password | `Credentials/CredentialCache.cs` (`CredentialHandshake`), `Transport/IClientTransport.cs` | Implemented: password is looked up per device and passed into the transport handshake; plaintext transports **fail closed** rather than no-op. Prompt UI is `ui-workstream`; the real handshake is `transport-workstream`. |

CLI surface (`vht-client <verb>`): `serve`, `list`, `attach <busid>`, `detach <busid>|--all`,
`attached`, `peers list|add|remove`, `ignore`, `allow`, `capacity`, `status`.

## 1. The virtual host controller (VHCI) and its dependency

The controller is the third-party **usbip-win2 UDE driver** (`usbip2_ude.sys` + `usbip2_filter.sys`,
BSD-2-Clause, pinned in `third_party/usbip-win2-driver/` with `SHA256SUMS` and upstream headers).
No driver is written by this project (ARCHITECTURE.md §3).

* Discovery: SetupAPI enumeration of `GUID_DEVINTERFACE_USBIP_VHCI` = `{B4030C06-DC5F-4FCC-87EB-E5515A0935C0}`
  (`Interop/Ude/UdeNative.cs`).
* IOCTL codes are **derived** from the `CTL_CODE` macro, never typed in (`UdeConstants.CtlCode`):
  e.g. `PLUGIN_HARDWARE` = `0x0022E000`, `GET_IMPORTED_DEVICES` = `0x0022E008`.
* Structs and offsets are re-declared from upstream `include/usbip/vhci.h` / `consts.h`
  (`UdeBufferCodec`), with attribution kept. The `GET_IMPORTED_DEVICES` reply layout is not stated by
  the driver (the element count is implied by the byte count), so the codec **validates** the reply
  against the candidate layouts and throws rather than returning plausible garbage.
* **Fail-closed rule (§6.2) enforced in code:** `UdeDeviceController.Attach` refuses any host other
  than `127.0.0.1`, and `AttachManager.AttachAsync` refuses to attach at all when the loopback bridge
  is not running — an attach pointing at a remote host would put plaintext USB/IP on the network and
  bypass TLS/LZ4 entirely.

**Driver installation is not this card's job.** The client detects the driver is absent, says so in
one actionable line, and degrades (it does not crash, and it does not silently do something else).
Installing the driver is `packaging-workstream`'s deliverable 3 (packaging/driver deployment).

## 2. Multi-device model (deliverable 3)

There is no device-count concept in `Vht.Client`: no `max_devices`, no slots, no licence check, no
policy branch that counts. Each attach is one `PLUGIN_HARDWARE` IOCTL on its own virtual port; each
port is one USB/IP connection through the bridge. The only thing that can stop the Nth device is the
**driver's own port allocation**, which is measured rather than assumed:

* `vht-client capacity` runs `IDeviceController.MeasurePortCapacity()` — it opens a loopback listener
  that accepts and never answers (holding attaches in `connecting`, the state that occupies a port),
  attaches probes until the driver refuses, reports the number and the refusal, then releases
  everything. It is a measurement, not a limit: nothing in the client consults the result to refuse an
  attach. ARCHITECTURE.md §5/§10 risk 3 answers a ceiling with an additional controller instance.
* `AttachManagerOptions.ExpectedMinimumPorts` (default 8) only turns a low measurement into a WARNING.

## 3. Device-class matrix (honest)

Transfer types: **control**, **bulk** and **interrupt** are the required path. **Isochronous (ISO) is
unsupported by default** in v1 — ARCHITECTURE.md §10 risk 6 makes it best-effort behind a capability
flag that both ends must negotiate, and no transport negotiates it yet (the plaintext stub this card
ships declares no such capability; the flag belongs to the handshake owned by `transport-workstream`).
`vht-client` itself is transfer-type-agnostic: it relays the server-leg byte stream and leaves URB
execution to the driver, so "unsupported" here means *not provided by the assembled stack*, and it
must not be papered over.

| Device class | Transfer types exercised | Status | Evidence / why |
|---|---|---|---|
| Mass storage (BOT / UAS) | bulk + control | **Expected to work; not verified on hardware in this card** | No ISO involved; URB relay is byte-transparent. Verified end-to-end only by `hardware-validation-workstream` (real device in Disk Management). |
| HID keyboard/mouse | interrupt + control | **Expected to work; not verified on hardware in this card** | Windows' own HID stack binds to the enumerated port; no ISO. `hardware-validation-workstream` verifies usability. |
| HID gamepad / consumer control | interrupt + control | Expected to work (same path as HID) | As above. |
| CDC / serial (VCP) | bulk + interrupt + control | Expected to work; not verified | As above. |
| Vendor-specific (WinUSB/libusb) | bulk + interrupt + control | Expected to work; not verified | Depends on the peer's capture side (`server-workstream`). |
| Printer / scanner | bulk + control | Expected to work; not verified | As above. |
| **USB audio (UAC1/UAC2) streaming** | **isochronous** | **NOT SUPPORTED** | ISO is not negotiated by any transport in v1; also the UDE driver's ISO behaviour is unverified upstream. Playback/capture will not stream. |
| **UVC webcam video streaming** | **isochronous** | **NOT SUPPORTED** | Same reason. The device may enumerate, but the video stream cannot be carried. |
| USB hubs / devices requiring a port reset loop beyond the driver's model | mixed | Unverified | ARCHITECTURE.md §10 risk 5. |

Nothing in the table claims hardware verification that this card cannot perform: this machine has no
remote peer running `vht-server` yet and the UDE driver is not installed, so every "expected" row is
an inference from the data path, and `hardware-validation-workstream` is the card that must confirm or falsify it.

## 4. Verification performed here (this machine, 2026-10-14)

| Check | Command | Result |
|---|---|---|
| Product builds standalone | `dotnet build src/Vht.Client/Vht.Client.csproj` | **succeeded**, 0 warnings, 0 errors (`TreatWarningsAsErrors=true`) |
| Client test suite | `dotnet test src/Vht.Client/Tests/Vht.Client.Tests.csproj` | **45 passed, 0 failed** — attach, detach, multi-device concurrency (N devices, one connection each), hotplug, reconnect after transport death, peer going off-network, auto-attach gating, control pipe, IOCTL codec offsets, USB/IP frame layout, capability/protocol validation |
| CLI runs end-to-end | `vht-client status \| attached \| list \| peers list \| capacity \| attach 1-1` | Real process output; bridge binds **127.0.0.1:3240 only**; unknown busid refused; driver absent reported as one actionable line |
| Driver-absent behaviour | same | **Fails closed with an actionable message**; no crash, no shadow path |

Raw logs: `%LOCALAPPDATA%\hermes\kanban\workspaces\client-workstream\client-tests.txt`, `capacity.txt`.

### Defect found and fixed during this verification

Auditing `UdeBufferCodec` against the pinned upstream header (`third_party/usbip-win2-driver/upstream-headers/vhci.h`,
no `#pragma pack` — so MSVC's default natural alignment is the real layout) turned up an arithmetic
slip in the *defensive* pack(1) candidate: its `serial`/`iserial`/`wsk_events` offsets and element
stride were self-inconsistent (`serial` at 1108 while `product` already ends at 1109). The validation
in `ParseImportedDevices` compares only the layout-independent prefix, so it could not catch that
class of error, and the existing offset test built its buffers *from* those constants — a tautology.

Fixed: pack(1) candidate is now `stride 1127, devid 1097, speed 1101, vendor 1105, product 1107,
serial 1109, iserial 1125, wsk_events 1126`, and a new test
(`ImportedDeviceLayoutOffsets_FollowTheHeaderFieldWidths`) pins every offset to the header's field
widths instead of to itself. The natural layout (the one the shipped driver uses) was verified
correct as written: `devid 1100, speed 1104, vendor 1108, product 1110, serial 1112, iserial 1128,
wsk_events 1129, stride 1132`. The `plugin_hardware` (1120), `plugout_hardware` (8) and
`stop_attach_attempts` sizes were re-derived from the header and are correct.

## 5. Not verified here (and who owns it)

* **Real hardware attach** (mass storage in Disk Management, HID usable, N ≥ 2 devices at once,
  network-unplug re-attach) — `hardware-validation-workstream`. This card's tests use an in-memory controller and a
  byte-explicit fake USB/IP peer; they verify the client's own logic, not the kernel driver.
* **The driver's real port ceiling** — measured by `vht-client capacity` on a machine with the driver
  installed; the number must be reported by `hardware-validation-workstream`.
* **Driver installation/signing** — `packaging-workstream`.
* **TLS, LZ4, pinning** — `transport-workstream`; this card ships the agreed stub transport behind
  `IClientTransportFactory`, so the swap is one factory.
* **Password UI** — `ui-workstream`.

## 6. Known limitations

1. The client cannot present any device without the pinned UDE driver installed (by design — no
   driver of our own in v1).
2. Isochronous device classes (USB audio streaming, UVC video) do not work; see §3.
3. A one-shot `attach` holds the device until Ctrl+C — the device must be held by a running client
   process (the service, or the worker the client window starts) for it to stay enumerated.
4. The credential cache is the client's own JSON store at this stage; the architecture intends
   `Vht.Config`'s store to replace it (`Config/ClientPaths.cs` documents the swap).
5. The `GET_IMPORTED_DEVICES` reply layout is inferred and validated against two candidate layouts;
   both are exercised by tests, but only a real driver confirms which one ships in 0.9.8.1. This is
   the highest-value thing `hardware-validation-workstream` can confirm for this card.

## 7. Running it: one executable, one window, one hidden worker

`vht-client.exe` is both the client and its management window. Started with **no arguments** it
opens the **VirtualThere Client** window - unelevated, from whatever folder the executable sits in -
and drives the client over `\\.\pipe\vht-client`. The privileged half is a separate process: the one
that owns the UDE controller and the USB/IP connections. **Refresh** (and Start) probes the pipe
first, so a live worker is adopted rather than duplicated - a worker an earlier window started, or
the logon-task `vht-client serve`, answers and the window attaches and spawns nothing. Only when
nothing answers does the window start its own executable as `vht-client.exe --worker` with
`Verb=runas`: exactly one consent prompt, hidden, no console.

The window is a view of that pipe and nothing more, which is why it can stay unelevated. Closing it
(X, or minimising) hides it to the notification area and leaves the worker serving, so attached
devices stay enumerated. **Exit** stops the worker *that window started* - over the pipe, then by
ending the process - and leaves a worker it merely attached to alone. A window process that dies any
other way (crash, End task) leaves its worker serving too; the next launch attaches to it. `vht-client
serve` remains the unattended form and `vht-client <verb>` the scriptable one. The full lifetime table
and the pipe's security descriptor are in `docs/UI.md` section 1.