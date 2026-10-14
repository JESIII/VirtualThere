# Server setup (`vht-server` / `vht serve`)

The server side of VirtualThere: enumerate this machine's USB devices, claim the ones it is allowed
to claim, export them over USB/IP, and apply policy. It runs either in the foreground or as the
Windows service `vht-server`.

## 1. Running it

```bash
vht serve                      # foreground; Ctrl+C stops it
vht serve --config D:\vht\server.toml
vht serve --capture-mode winusb-only --log-level debug
vht serve --rebind             # allow the rebind path for devices whose driver holds them
```

As a service (elevated shell):

```bash
vht install                    # registers "vht-server" and starts it
vht start | vht stop-service | vht service-status
vht uninstall
```

Service name defaults to `vht-server`; override with `--service <name>`. The service has no command
line, so its policy must come from the config file.

## 2. Config file: the `server.toml` subset

Default path: `%ProgramData%\VirtualThere\server.toml`. Write a commented starter file with:

```bash
vht config-template --out C:\ProgramData\VirtualThere\server.toml
```

The server parses a documented subset of TOML: comments (`#`), `[section]` headers, dotted keys,
strings, booleans, integers and arrays of strings. Anything it cannot parse is **reported, never
guessed at**, and unknown keys are warned about rather than fatal. The full config model (validation,
origins, reload classification, device identity, discovery) lives in `docs/CONFIG.md`; the keys below
are the ones the service itself reads today.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `server.name` | string | machine host name, upper-cased | Name advertised to clients and in discovery |
| `transport.plaintext.enabled` | bool | `true` | Enable the stock-USB/IP listener |
| `transport.plaintext.port` | int | `3240` | Its port (the stock USB/IP port, so Linux `usbip` can attach) |
| `transport.plaintext.address` | string | `0.0.0.0` | Bind address. Use `127.0.0.1` to keep it local |
| `transport.tls.enabled` | bool | `false` | Enable the second listener (see the caveat below) |
| `transport.tls.port` | int | `3241` | Its port |
| `transport.tls.address` | string | `0.0.0.0` | Its bind address |
| `capture.mode` | string | `auto` | `auto`, `winusb-only` or `prebound-only` |
| `capture.rebind.enabled` | bool | `false` | Offer the rebind path for devices whose function driver holds them |
| `capture.refuse_management_nic` | bool | `true` | Never export the NIC you are being managed over |
| `policy.allow` / `policy.allowed` | string array | `[]` | Export only these devices (empty = no restriction) |
| `policy.ignore` / `policy.ignored` | string array | `[]` | Never export these devices. An ignore beats an allow |
| `connections.max_in_flight_urbs` | int | runtime default | Back-pressure per connection |
| `connections.max_transfer_buffer` | int | runtime default | Largest single transfer accepted |
| `logging.level` | string | `info` | `debug`, `info`, `warn`, `error` |
| `logging.file` | string | none | Also write the log to this file |
| `control_plane.enabled` | bool | `true` | Serve the local named-pipe control plane |
| `control_plane.pipe_name` | string | `vht-server` | Pipe name (`\\.\pipe\vht-server`) |
| `hooks.timeout_seconds` | int | runtime default | Kill a hook script that runs longer than this |
| `hooks.script_root` | string | none | Directory hook scripts are resolved under |

Selectors are `vvvv:pppp`, `vvvv:pppp:serial` or `vvvv:pppp:serial@server`, e.g.
`policy.ignore = ["1050:0407", "0b05:19af"]`.

> **Do not enable `transport.tls.enabled` expecting encryption today.** `Vht.Transport` implements
> TLS 1.3, pinning, LZ4 and authentication with its own test suite (`docs/TRANSPORT.md`), but it is
> **not wired into the server binary yet**. v0.1.0 export is plain USB/IP. Treat the network as
> untrusted: trusted LAN, VPN or SSH tunnel only.

## 3. What gets exported, and what cannot be

A device can only be exported if the server can obtain a WinUSB handle to it in user mode. On a
running desktop, most devices are held by their own function driver, so `vht list --local` reports:

```
1-3-252f868a [3151:5029] "3151:5029" speed=unknown class=per-interface \
  state=CaptureUnsupported reason=driver_holds_device \
  detail="no WinUSB handle (win32 8); the function driver holds this device — set capture.rebind.enabled to offer the P2 rebind path"
```

That is a statement of fact, not a failure of the export path. Options:

* `capture.mode = "auto"` (default): use whatever can be claimed; skip the rest with a reason.
* `capture.mode = "winusb-only"`: only devices already bound to WinUSB.
* `capture.mode = "prebound-only"`: only devices bound to WinUSB at start-up; never rebinds.
* `capture.rebind.enabled = true` / `vht serve --rebind`: offer the rebind path for a device whose
  driver holds it. Needs elevation, and it changes which driver owns your device - read
  `docs/TROUBLESHOOTING.md` before using it on a device you depend on.

Devices are re-scanned periodically (about every 3 s) on hosts where device-change notifications are
not available, which is the hotplug path here.

## 4. Device policy, live

Policy can be changed on a running server; it is also writable in the config file for the next start.

```bash
vht status                                   # exports, attachments, connections, listeners, "no cap"
vht list
vht nickname <busid> --name "Label printer"  # friendly name shown to clients (see §8 for the wire format)
vht password <busid> --password "..."        # per-device password (TLS listener only)
vht ignore 1050:0407                         # never export
vht unignore 1050:0407
vht kick <busid>                             # force-detach whoever holds it
vht stop-server                              # graceful shutdown of a running server
```

All of these are unconditional. There is no tier, no cap and no gated verb - that is the point of the
project (`README.md`).

## 5. Script hooks

Hook scripts run on device lifecycle events: `onBind`, `onUnbind`, `onEnumeration`, `onReset`,
`onDeviceUnplug`, `onClearHalt`, `onResetEp`, `onPassToKernel`, `onChangeNickname`, plus the
client-side `onClientConnect`, `onClientDisconnect`, `OnDeviceKick`, `OnDeviceIgnore`,
`OnDeviceUnignore`, `OnServerRename`, `ClientAuthorization`, `ClientDeauthorization`,
`OnAddReverseClient`, `OnRemoveReverseClient`. Substitution variables such as `$CLIENT_ID$`,
`$CLIENT_IP$`, `$NICKNAME$`, `$DEVPATH$`, `$VENDOR_ID$` are available. Exit codes are the hook ABI.
`hooks.timeout_seconds` bounds a runaway script. The vocabulary is documented in
`docs/FEATURE_MATRIX.md` and the vocabulary source (`src/Vht.Core/Hooks/HookVocabulary.cs`).

Hook scripts are executed as the service account. Treat `hooks.script_root` like a program directory:
anything that can write there can run code as the service.

## 6. Control plane and ports

| Surface | Address | Auth | Notes |
|---|---|---|---|
| USB/IP listener | `3240/tcp` (default) | none (USB/IP has none) | stock protocol, Linux `usbip`-compatible |
| Second listener | `3241/tcp` (config key exists) | - | TLS transport not wired in yet; leave disabled |
| Control plane | `\\.\pipe\vht-server` | Windows ACL (Administrators + interactive user) | JSON lines, one request/response pair per connection, local only |

Anything that can write to the control pipe can control the service without elevation. That is by
design (the tray runs unelevated), which is why the pipe is not world-writable.

## 7. Security notes

1. **Plain USB/IP means no encryption and no authentication.** Anyone who can reach the port can
   enumerate your exports; with no per-device password, they can attach them. Firewall the port, or
   bind `transport.plaintext.address = "127.0.0.1"` and reach it over a tunnel.
2. **An attached device is the real device for the client.** Mass storage becomes a block device;
   HID becomes an input device. Share deliberately.
3. **Never export the NIC you are managed over** (`capture.refuse_management_nic` exists for this).
4. **Device passwords are a TLS-listener feature**; on the plaintext listener they are refused, never
   silently ignored.
5. Elevation is needed to claim devices and to install the service. If a command fails, it says so
   and stops - it does not fall back to a weaker mode.

## 8. Device nicknames on the wire (`_vht` op extension)

`vht nickname <busid> --name "Label printer"` stores a friendly name for a device. Where that name
goes on the wire is a deliberate decision, because **the stock USB/IP protocol has nowhere to put
it**: the `usbip_usb_device` record carried by `OP_REP_DEVLIST` and `OP_REP_IMPORT` is 312 bytes of
fixed fields (256-byte `path`, 32-byte `busid`, then `busnum`, `devnum`, `speed`, `idVendor`,
`idProduct`, `bcdDevice` and six single-byte class/configuration fields). There is no string field at
all - not for product, not for manufacturer, not for a nickname.

**Decision: the nickname travels beside the stock record, in VirtualThere's own op, never inside it.**
Rationale:

1. Every byte of those 312 bytes is specified, and third-party parsers read them at fixed offsets.
   Repurposing slack - appending the name after the NUL that terminates `path`, say - would make
   VirtualThere's frames non-conforming and would hide non-path data in a field every stock tool
   prints. A separate op cannot collide with anything: a Linux `usbip list`/`usbip attach` sees a
   device record identical to the one a stock `usbipd` sends.
2. A stock peer never sends the op, so it can never be confused by it, and VirtualThere's client only
   sends it where it is prepared for the answer to be silence.
3. Attach stays untouched: `OP_REQ_IMPORT` and every URB are byte-for-byte what they always were. The
   nickname is metadata about a device, not part of importing one.

### 8.1 Wire format

Request - `OP_REQ_VHT_DEVINFO`, code `0x8f01`, 12 bytes, no length field:

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | version `0x0111` |
| 2 | 2 | code `0x8f01` |
| 4 | 4 | `status`, 0 in a request |
| 8 | 4 | magic `VHT1` (`0x56485431`) |

Reply - `OP_REP_VHT_DEVINFO`, code `0x0f01`:

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | version `0x0111` |
| 2 | 2 | code `0x0f01` |
| 4 | 4 | `status` (`0`; a non-zero status means "no metadata", never an error) |
| 8 | 4 | magic `VHT1` |
| 12 | 4 | entry count |
| 16 | 32 | `busid`, NUL-padded ASCII |
| 48 | 1 | nickname length in UTF-8 bytes (`0` = this device has no nickname) |
| 49 | n | nickname, UTF-8 |

The last three fields repeat once per entry, in bus-id order.

### 8.2 The rules that keep it safe

- **Same device set as the stock list.** The entries name exactly the devices `OP_REP_DEVLIST`
  advertises on that same connection, so the §6.4 containment rule holds in the extension too: a
  password-protected device is not named on the plaintext listener, and no client can learn a name
  for a device it was not offered.
- **Bounded by construction.** One nickname is capped at 96 UTF-8 bytes
  (`UsbIp.VhtNicknameMaxBytes`, the same ceiling the transport handshake puts on a client name) and
  truncated on a codepoint boundary, so neither the reply length nor a client's allocation is driven
  by an unvalidated number - and the far side never decodes a split character.
- **Validated on read.** The decoder checks the magic, the entry count and every name length against
  the frame before reading anything; a hostile or corrupt reply is a protocol error, never an
  out-of-bounds read.
- **A peer that does not speak it loses nothing.** A stock `usbipd` closes the connection when an
  unknown op arrives (that is what its op switch does), so `vht-client` asks for names on a
  connection of its own and treats any failure as "this peer has no names". The device list is never
  at risk and the peer is not reported as unavailable.
- **What a stock client sees:** the stock device record, byte for byte as before, and no nickname -
  exactly what it sees against `usbipd`. The nickname is a VirtualThere-to-VirtualThere refinement.

### 8.3 What uses it

`UsbIpCodec.EncodeVhtDeviceInfoReply`/`DecodeVhtDeviceInfoReply` and
`UsbIpSession.HandleVhtDeviceInfoAsync` on the server; `UsbipOp.ReadNicknamesAsync` and
`RemoteDeviceCatalog` on the client, which asks once per refresh and carries the answer on
`InventoryEntry.Nickname`. The client control plane publishes it as `nickname` in `devices.list`, and
the UI prefers it over the vendor/product pair - so a nicknamed device shows the operator's name in
the client, not `[0781:5581]`. An absent nickname means "no name configured", never "no device".
