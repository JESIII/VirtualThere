# VirtualHere Recon + VirtualThere Target Spec

Provenance: REA (Ghidra 12.1.4 provider) over `unpacked/vhusbdwinw64.exe` (server) and `unpacked/vhui64.exe` (client).
Method note: both shipped binaries are **UPX-packed**; they were unpacked with UPX 5.0.2 before analysis.
Analyzing the packed originals yields junk (86 procedures / 99 strings). The unpacked binaries are the real targets.
Every claim below is tagged **OBSERVED** (with a hex address) or **INFERRED**. Addresses are absolute
virtual addresses in image base `0x140000000`.

Evidence method differs by binary, and the difference matters:

- **Server:** REA/Ghidra — defined strings, procedures, xrefs, instruction-level checks. Method:
  `open_binary` → `search_strings` / `xrefs` / `resolve_containing_procedure` / `procedure_pseudo_code`
  / `inspect_native_instruction` / `read_bytes`.
- **Client:** Ghidra auto-analysis of the 15.7 MB client exceeded the MCP call timeout (300 s) on
  every attempt, so the client findings come from a **raw byte scan of the unpacked PE** (section
  headers parsed, file offset → VA computed against `ImageBase`), which is why client addresses are
  tagged OBSERVED-bytes rather than OBSERVED-defined-string. The addresses are still exact VAs a
  reader can verify with `read_bytes` on the same file. Client *procedure* reconstruction is
  therefore absent and the client section makes no claim that requires it.

| Artifact | SHA-256 | Role |
|---|---|---|
| `GhidraProj/vhusbdwinw64.exe` (packed) | `6ae762d598800f0f11ba4f317f09697177a569bada586cc518baabfc4e043eec` | server, UPX |
| `unpacked/vhusbdwinw64.exe` | `a225edfc14a5bd076912067748462dc4876960d37d7dddb9007d2900beb7e83a` | server, **analysis target** |
| `GhidraProj/vhui64.exe` (packed) | `186a646bbeb11de0da0aea02a2c8f954870517381c4c685b6c443816f773f304` | client, UPX |
| `unpacked/vhui64.exe` | `aa3713fdab7aa63dd5e8d4c3b68940f395bdbce09cacd7f7c0d0c97808ec03ab` | client, unpacked |

Unpacked server: 20,124 procedures, 24,826 defined strings, 9 segments (`.text` 7.68 MB).
Unpacked client: 7 sections (`.text` 9.80 MB raw, `.rdata` 3.26 MB, `.data` 2.09 MB), 67,520 printable
strings ≥5 chars in the byte scan.

---

## 1. Licensing model (OBSERVED — this is the whole reason for the project)

### 1.1 What the licence text says

- Free tier = **one USB device per server**. Verbatim from the embedded EULA
  (`0x14097eae0`): *"Install and use the Software on a Windows machine to share a single USB
  device (with no payment required). To share more than one USB device simultaneously from a
  single server you must purchase a License."*
- NAS variant is worse: 1 device **and** a 10-day time limit (`0x14097e590`).
- Third variant, Linux/OSX (`0x14097f040`): 1 device, no time limit.
- Unlicensed branding is explicit, not hidden: `VirtualHere USB Server Trial Edition`
  (`0x14097fdb0`).

### 1.2 Licence state, blob format, and the gate helper

- **Licence state global:** `DAT_140a5f258`, a C string. Every gate is one `strcmp` against the
  literal `"unlicensed"` (`0x140980803`). OBSERVED in the generic gate helper `FUN_1407443c0`
  (`0x1407443c0`, 1467 bytes): it begins with `strcmp(DAT_140a5f258, "unlicensed")`; on equality it
  logs and returns 0. The same construct appears in the accept path at `0x1407468e9`.
- **Licence blob format:** seed default at `0x140980736` —
  `unlicensed,1,MCACDkn0jww6R5WOIjFqU/apAg4Um+mDkU2TBcC7fA1FrA==` → `"<state>,<device-count>,<base64 signature>"`.
  Parse template `"unlicensed,"` at `0x140980c04`, used at `0x14074c0b7`. Success logs
  `Server licensed to=%s %s` (`0x140980d05`); failures log `Invalid License`
  (`0x14098080e`/`0x140980820`) and `Failed to create license` (`0x1409807a6`).
  **The free-tier cap of 1 is carried as the `1` field of the seed blob rather than a hardcoded
  comparison** (seed string OBSERVED; mechanism INFERRED, see §1.4).
- **Generic gate helper `FUN_1407443c0` is the single choke point for every paid script hook.**
  - `param_1` = hook name, `param_2` = script text, `param_3` = connection id.
  - Unlicensed → logs `Must purchase this server to use %s!` (`0x14097ffa6`, call `0x140744467`),
    returns 0; the hook silently does nothing.
  - Licensed → substitutes `$CLIENT_ID$` (`FUN_1407339e0`) and `$CLIENT_IP$` (`inet_ntop`), builds
    `cmd.exe /C <script>` and runs it via `CreateProcessW` (`0x140744789`) +
    `WaitForSingleObject` + `GetExitCodeProcess`.
  - **Hook ABI:** exit code 1 = success/return-1, exit code 0 = "no"/return-0, anything else logs
    `Unknown return value %d from %s`. Hooks are shell scripts whose exit code *is* the return value.

### 1.3 Every licensing gate, with gate string AND code site

| Gate string | String addr | Referencing code addr | Containing procedure |
|---|---|---|---|
| `Must purchase this server to use %s!` (generic) | `0x14097ffa6` | `0x140744453` | `FUN_1407443c0` @ `0x1407443c0` |
| `Must purchase this server to use OnDeviceKick!` | `0x14096831b` | `0x140716bc9` | `FUN_140714520` @ `0x140714520` |
| `Must purchase this server to use onDeviceIgnore!` | `0x140968575` | `0x140714920` | `FUN_140714520` @ `0x140714520` |
| `Must purchase this server to use onDeviceUnignore!` | `0x1409686ee` | `0x140742fa0` | not resolved (outside `FUN_140714520`) |
| `The onClientConnect event requires a licensed server!` | `0x140980285` | `0x1407468e9` | `FUN_140746630` @ `0x140746630` |
| `The onClientDisconnect event requires a licensed server!` | `0x1409801e3` | `0x140715858` | `FUN_140714520` @ `0x140714520` |
| `Must purchase this server to use ClientAuthorization!` | `0x140968807` | not resolved | INFERRED: via `FUN_1407443c0` |
| `Must purchase this server to use ClientDeauthorization!` | `0x14096844f` | not resolved | INFERRED: via `FUN_1407443c0` |
| `Must purchase this server to use onServerRename!` | `0x14097fde4` | not resolved | INFERRED: via `FUN_1407443c0` |

`FUN_140714520` is a 15,557-byte non-contiguous command/connection handler holding three of the
five paid gates plus the device-bind path. The client-accept path `FUN_140746630` (1340 bytes)
logs `%s connected as connection %u %s`, looks the hook up with
`FUN_14070b8e0("onClientConnect",0,1)`, applies the licence gate, and otherwise dispatches the hook
onto a queued worker (`FUN_140746b70`) so a slow script does not block the accept path.

### 1.4 The `max_devices` cap path — located in code, not just in strings

- **Where the cap value is decided: `FUN_14074bbb0` (`0x14074bbb0`–`0x14074d696`, 6887 bytes).**
  This single function loads the configuration and the licence, and formats the effective cap:
  - `"unlicensed,"` template used at `0x14074c0b7` (string `0x140980c04`)
  - default seed blob used at `0x14074bcd2` (string `0x140980736`)
  - `"max_devices=%d"` formatted at `0x14074c669` (string `0x140980ce0`)
  - `"max_devices=unlimited"` formatted at `0x14074c690` (string `0x140980cef`)
- **Where the cap is enforced: the BIND status family, surfaced by `FUN_140714520`.**
  Contiguous string block `0x1409688c0`–`0x1409689b0` holds
  `BIND_ERROR` (`0x1409688dd`), **`BIND MAX_DEVICES_EXCEEDED` (`0x1409688e8`)**,
  `BIND_UNAUTHORIZED` (`0x140968902`), `BIND_MAX_CLIENTS_EXCEEDED` (`0x140968914`),
  `BIND_PASSWORD_REQUIRED` (`0x14096892e`), `BIND UNKNOWN ERROR` (`0x140968945`), followed by
  `Warning, stopping DeviceManager while there are bound devices` (the tail of the same block).
  The status→message table sits at `0x1409689b0` (OBSERVED by `read_bytes`: six little-endian
  pointers `0x140968945, 0x140968914, 0x140968902, 0x140968945, 0x1409688e8, 0x1409688dd`, so slot
  `0x1409689d0` = `BIND MAX_DEVICES_EXCEEDED`), preceded at `0x1409689a0` by negative int32 status
  codes `0xfffffe16`, `0xfffffe1a`, `0xfffffe1f` (OBSERVED bytes; the exact code→message pairing is
  INFERRED from adjacency).
  `FUN_140714520` references that table at `0x140716b62` and `0x140716b85` (instruction-level check:
  `0x140716b85` = `MOV qword ptr [RSP+0x30],RAX` carrying a DATA reference to `0x140968945`, i.e.
  building the argument array for the BIND-status response/log), and the successful bind
  `Device %u [%04x:%04x] BOUND to connection %u` (`0x1409682b5`) is referenced from `0x140717dad`
  in the same procedure.
  → **bind accept/reject, including the device-cap rejection, is in `FUN_140714520`; the cap value
  it compares against comes from `FUN_14074bbb0`.**
- The licence state is re-read at each gate (`DAT_140a5f258`), so the cap is checked per bind
  rather than cached into the connection object.
- **Cross-check from the client:** the client renders a `MAX DEVICES :` field in Server Properties
  (`0x1409d223d`), i.e. the effective cap is shipped to the client. That is the surface on which a
  user sees the free version's 1-device limit.

**VirtualThere requirement:** unlimited devices, and all of the above gates removed. That is the
minimum bar for "supports more devices than the free version".

## 2. Architecture (OBSERVED)

- **USB capture on Windows:** WinUSB (`WINUSB.DLL`, `WinUsb_QueryDeviceInformation`, `WinUsb_Free`)
  plus SetupAPI (`SetupDiCreateDeviceInfoList`, `SetupDiEnumDeviceInterfaces`,
  `SetupDiGetDeviceInterfaceDetailW`, `SetupDiSetSelectedDevice`, …) and CfgMgr32
  (`CM_Get_Device_IDW`, `CM_Get_Device_Interface_ListW`).
- **Kernel component:** a filter/bus driver is present — device path
  `\??\GLOBALROOT\Device\vhfilter` (`0x140974d00`). Rebinding a device to WinUSB is done by
  `bindWinUSBToDevice` (log strings `0x1409756ac`–`0x140975894`).
- **Hotplug:** `RegisterDeviceNotificationW` / `UnregisterDeviceNotification`.
- **Per-connection threading:** accept path `FUN_140746630` allocates a 0x2a0-byte connection
  record, assigns a 16-bit connection id (monotonic, never 0), calls `UuidCreate` for a
  per-connection UUID, and starts named queued workers `msgTx%u` / `msgRx%u`
  (name formats at `0x1409804c1`, `0x1409804c6`; third format `msgRx%u` is built inline).
- **Logging:** level-first logger `FUN_140733bb0(level, fmt, …)`; licence gates log at level 3,
  connection lifecycle at level 6.
- **Windows service:** full SCM implementation, `-b` install/uninstall, Event Log source
  `SYSTEM\CurrentControlSet\services\eventlog\VirtualHere USB Server` (`0x14097758a`), service
  display name `VirtualHere USB Server` (`0x1409812ec`), tray title `VirtualHere USB Sharing`
  (`0x140980978`).
- **UI:** wxWidgets (embedded XRC at `0x1409786d0`), tray-based server window; the client is a
  wxWidgets GUI app.
- **Transfers:** URB-level model — `Error %d clearing halt on ep %02x …`,
  `Error %d resetting pipe on ep %02x …`, `Error %u allocating iso buffer for ep %02x …`,
  `… outstanding in-flight URBs` (`0x1409686ab`). Related event names: `onClearHalt`
  (`0x1409687c2`), `onResetEp` (`0x1409687ce`), `onReset` (`0x140968283`), `onEnumeration`
  (`0x14096825a`), `onBind` (`0x1409682ae`), `onUnbind` (`0x14096828b`), `onPassToKernel`
  (`0x140968203`), `onDeviceUnplug` (`0x140968669`).
- **Binding:** `Device %u [%04x:%04x] BOUND to connection %u` (`0x1409682b5`);
  `BIND MAX_DEVICES_EXCEEDED` (`0x1409688e8`) ← the free-tier cap manifests here.

## 3. Transport & security (OBSERVED)

- **Two listeners, not one.** `Listening on %s at TCP port %d %s` (`0x140980da7`) **and**
  `Listening on %s at TCP SSL port %d %s` (`0x14098115a`); config keys `TCPPort` (`0x140980d9f`)
  and `SSLPort` (`0x140981127`). A cleartext listener is normal operation and TLS is *additional* —
  the wire is **not** unconditionally encrypted. The client mirrors this: `=SSL Port :`
  (`0x1409d3b03`) and separate `Enable Reverse Connections` / `Enable Reverse SSL Connections`
  (`0x1409d1d54` / `0x1409d1d97`).
- **mbedTLS**, statically linked (PEM/PKCS#5/PKCS#12/X509/RSA error strings and module names at
  `0x14075bafa`–`0x14079a711`, e.g. `x509_crt.c` `0x140758396`, `x509_verify_cert` `0x140757df4`).
  Client side carries its own copy (`PEM - Private key password can't be empty` `0x1409600a3`,
  `PKCS12 - …` `0x1409604ee`).
- **LZ4** compression — named in the embedded EULA (`0x14097eae0`); `CompressionLimit`
  (`0x1409804d3`) server-side and `=Compression Threshold :` (`0x1409d336a`) client-side.
- **Default port numbers are NOT present as strings.** A whole-inventory search for `7575` matched
  only an unrelated SVG literal. "Default port 7575" is INFERRED from vendor documentation, not
  observed here.

## 4. Configuration surface (OBSERVED)

Config-file / registry keys observed as strings:

| Key | Address | Role (name observed; behaviour INFERRED unless noted) |
|---|---|---|
| `ServerName` | `0x14097fc7c` | advertised server name; renaming is gated (`onServerRename` `0x14097fde4`) |
| `DeviceNicknames` | `0x14097fee3` | nickname blob, integrity-checked (`serial nickname length wrong %u %u` `0x1409800ea`; self-healing `Corrupt nicknames in server configuration (%s), deleting...` `0x140980143`) |
| `IgnoredDevices` | `0x140980066` | ignore list |
| `AllowedDevices` | `0x1409681c3` | allow list |
| `DeviceIdMap` | `0x140976440` | stable device-id mapping |
| `CompressionLimit` | `0x1409804d3` | LZ4 threshold |
| `AutoAttachToKernel` | `0x1409809bd` | auto-bind device to kernel driver (code xrefs `0x14070d056`, `0x14074acde`, `0x14074afc5`, `0x14074ba85`) |
| `MDNSServiceName` | `0x14098099c` | Bonjour instance name |
| `_vhusb._tcp` | `0x1409809ac` | Bonjour service type |
| `TCPPort` | `0x140980d9f` | cleartext listen port |
| `SSLPort` | `0x140981127` | TLS listen port |
| `password` | `0x140980cd7` | password (see `Password for device %s is too long, max %d chars`, `0x1409681d2`) |
| `unlicensed,` | `0x140980c04` | licence blob template (§1.2) |

Registry paths observed: `SYSTEM\CurrentControlSet\Control\COM Name Arbiter\Devices`
(`0x140974ec6`), `SYSTEM\CurrentControlSet\services\eventlog\VirtualHere USB Server`
(`0x14097758a`), `DeviceInterfaceGUID` (`0x140975638`), `DeviceParameters` (`0x140975585`).

Note: the string `MaxDevices` does **not** exist anywhere in the server image (case-insensitive
literal search returns nothing), so the cap is not settable under that spelling in this build; the
observable cap surface is the licence blob field plus the `max_devices=` startup log lines.

**Command line (OBSERVED, decoded verbatim from `0x14097f5a0`–`0x14097f7a5`):**

```
usage: %s [-bhliqnu] [-c <configuration file>] [-r <log file>]
   -b uninstall as a Windows Service
   -b install as a Windows Service
   -c configuration file
   -h display this usage
   -l display license text
   -i prefer ipv6 (dual/stack)
   -r log to file instead of Windows Event Log
   -q when combined with -b will not popup any dialogs
   -n dont use Bonjour to broadcast server existence
```
Cross-checks: `Bonjour is disabled due to the -n argument given to the server` (`0x14097da7c`);
`VirtualHere USB Server is already running in the background as a Windows Service.` (`0x14097f958`);
`-b` result strings `0x14097fb14` / `0x14097fb58`; `VirtualHere USB Server is running...press CTRL-C to stop`
(`0x14097fb9e`); banners `VirtualHere USB Server for %s` (`0x14097f7a5`) and
`Use USB Devices over the network` (`0x14097f7c5`).

## 5. Discovery (OBSERVED)

- mDNS/DNS-SD via Windows DNSAPI — `DnsServiceConstructInstance` (`0x140987b8a`),
  `DnsServiceDeRegister` (`0x140987ba8`), `DnsServiceFreeInstance` (`0x140987bc0`),
  `DnsServiceRegister` (`0x140987bda`), `DnsServiceRegisterCancel` (`0x140987bf0`).
- Service type `_vhusb._tcp` (`0x1409809ac`); instance name from `MDNSServiceName` (`0x14098099c`).
- Failure is non-fatal and explicit: `There was an error registering this USB Server with Bonjour.
  Client Auto-Find functionality will not work` (`0x14097da12`).
- Cloud rendezvous hostname `easyfind.virtualhere.com` (`0x140975eaa`) — a vendor-operated
  matchmaker for NAT'd clients (behaviour INFERRED from the hostname alone). The client exposes it
  as `=EASYFIND :` (`0x1409d233e`) with states `=EasyFind is disabled` (`0x1409d30c3`) and
  `=EasyFind unavailable from this server` (`0x1409d30fc`), and has a manual fallback:
  `=Specify USB Server...` (`0x1409d1586`) with `=Enter the Server to add in the format host:port`
  (`0x1409d2bbc`).
- Bonjour is a hard dependency of auto-find on the client: `=Auto-find functionality requires
  Bonjour.\n\nWould you like to install this now?` (`0x1409d2e0f`);
  `=Auto-find functionality will be unavailable.\nYou will need to manually specify servers to
  connect.` (`0x1409d34f6`).

## 6. Script hooks (complete observed list)

**Paid** (see §1.3): `OnDeviceKick` (`0x14096814e`), `OnDeviceIgnore` (`0x14096818b`),
`OnDeviceUnignore` (`0x14096819a`), `onClientConnect` (`0x140980275`),
`onClientDisconnect` (`0x1409801a1`), `OnServerRename` (`0x14097fdd5`),
`ClientAuthorization` / `ClientDeauthorization` (gate strings `0x140968807` / `0x14096844f`),
`OnAddReverseClient` (`0x14097ff8d`), `OnRemoveReverseClient` (`0x140980048`).

**Free:** `onBind` (`0x1409682ae`), `onUnbind` (`0x14096828b`), `onEnumeration` (`0x14096825a`),
`onReset` (`0x140968283`), `onDeviceUnplug` (`0x140968669`), `onClearHalt` (`0x1409687c2`),
`onResetEp` (`0x1409687ce`), `onPassToKernel` (`0x140968203`), `onChangeNickname` (`0x14097fed2`).

**Substitution variables observed:** `$CLIENT_ID$`, `$CLIENT_IP$` (in `FUN_1407443c0`),
`$NICKNAME$` (`0x14097feb8`), `$NEW_NICKNAME$` (`0x14097fec3`), `$DEVPATH$`, `$VENDOR_ID$`,
`$PROD…` (device block near `0x140969a10`), `%04x` id formatting.

The client has its own hook surface: `=Custom Event Handler...` (`0x1409d1b74`) in the device
context menu — i.e. the hook concept exists on both ends.

## 7. Client binary (`vhui64.exe`) — what the server does not reveal

sha256 `aa3713fdab7aa63dd5e8d4c3b68940f395bdbce09cacd7f7c0d0c97808ec03ab` (unpacked, 15.7 MB).
All addresses in this section are OBSERVED-bytes (raw scan, see the header note).

### 7.1 The client is a hub-oriented tree UI, not a flat device list

Menu (`0x1409d1553`–`0x1409d1799`): `=Auto-Find USB Servers`, `=Specify USB Server...`,
`=Auto-Use All Devices`, `=Start minimized`, `=Always On Top`, `=System Messages`,
`=Install Client as a Service` (`0x1409d1678`), `=Uninstall Client Service` (`0x1409d16b1`),
`=License...` (`0x1409d16e3`), `=Debug Mode`, `=Advanced Settings...`, `=About`, `=Exit`.
Hub-level menu items (resource names): `AUTO_FIND_HUBS_MENU_ITEM` (`0x1409c18b7`),
`SPECIFY_HUBS_MENU_ITEM` (`0x1409c1929`), `AUTO_USE_DEVICES_ON_HUB_MENU_ITEM` (`0x1409c1f22`),
`USE_DEVICES_ON_HUB_MENU_ITEM` (`0x1409c1fbb`), `SHOW_HUBS_MENU_ITEM` (`0x1409c202f`),
`HUB_PROPERTIES_MENU_ITEM` (`0x1409c26b5`), `=Show Hubs` (`0x1409d183f`).
Device/server context menu (`0x1409d19c6`–`0x1409d1bd6`): `=Use this device`, `=Stop using this
device`, `=In use elsewhere...`, `=Disconnect from user`, `=Auto-Use Device` /
`=Auto-Use Port` / `=Auto-Use Device/Port`, `=Rename...`, `=Ignore...`,
`=Custom Event Handler...`, `=Hot-Keys...`, `=Device Properties`, `=Hub Properties`,
`=Server Properties`, `=Use All on this Server`, `=Auto-Use all on this Server`.

### 7.2 Attach / detach UX and the exclusivity rules

- Verbs: `=Use this device` (`0x1409d19c6`) / `=Stop using this device` (`0x1409d19f1`);
  bulk variants `=Use All on this Server` (`0x1409d180d`).
- Per-device state strings rendered in the tree: `=You are using this device` (`0x1409d2a6e`),
  `=In use elsewhere` (`0x1409d2aa5`), `=No one is using this device` (`0x1409d2ad7`),
  `(In use by %s at %s)` (`0x1409d3786`), `(In use by you)` (`0x1409d37b1`),
  `(In use elsewhere)` (`0x1409d37d9`).
- A device can be claimed by exactly one client at a time, and the loser can request a takeover:
  `=In use elsewhere...` (`0x1409d1a26`) then `=Disconnect from user` (`0x1409d1a59`), confirmed by
  `=Are you sure you want to disconnect %s from %s?` (`0x1409d3a27`). The refusal path is
  `=You dont have permission to perform this action` (`0x1409d3abc`).
  → this is the server's `OnDeviceKick` feature seen from the client side. (Gate at
  `0x140716bc9`, §1.3.)
- Ordering rule enforced in the UI: `=Please stop using the device before ignoring it`
  (`0x1409d281f`) — ignore/unignore operates on unclaimed devices only.
- Connect lifecycle strings: `=Connecting to USB server...` (`0x1409d2b4e`),
  `=There was a error using this device.\n%s` (`0x1409d2599`).
- Quit guard: `=You are currently using USB devices.\n\nAre you sure you want to quit?`
  (`0x1409d26a2`); service-install guard: `=You must stop using all USB devices before installing
  the client as a service` (`0x1409d270c`).

### 7.3 Auto-Use policy (the "auto-attach" feature, fully parameterised)

- Scope: `=Auto-Use All Devices` (`0x1409d15be`), `=Auto-Use all on this Server` (`0x1409d17d0`),
  `=Auto-Use Device` (`0x1409d1a8d`), `=Auto-Use Port` (`0x1409d1abb`),
  `=Auto-Use Device/Port` (`0x1409d1aec`) — i.e. auto-use is recorded at device, port, server or
  global granularity. Turning it off produces distinct confirmations: `=(Auto-Use now off for this
  device/port)` (`0x1409d246c`), `…for this device` (`0x1409d24b2`), `…for this port`
  (`0x1409d24f1`), `…for this server` (`0x1409d252d`), `=(Auto-Use all is now off)`
  (`0x1409d2565`).
- Disambiguation: `=Qualify Auto-Use by Server Name` (`0x1409d31de`) and
  `=Qualify Auto-Use By Server Network Interface` (`0x1409d3225`), backed by checkbox resources
  `autoUseQualifyNameCheckBox` (`0x1409c51da`) and `autoUseQualifyInterfaceCheckBox`
  (`0x1409c500f`). → auto-use is keyed on *which server*, not just the device id, otherwise a
  laptop moving between networks would grab the wrong device.
- Timing knobs: `=Auto-Use Delay :` (`0x1409d3268`, ctrl `settingsAutoUseDelayTextCtrl`
  `0x1409c5551`) and `=Auto-Use Retry Delay :` (`0x1409d3294`, ctrl
  `settingsAutoUseRetryDelayTextCtrl` `0x1409c5a56`).
- Authorization interaction: `=Note, you are auto-using server '%s' but you are not authorized to
  use attached device '%s'` (`0x1409d262b`) — auto-use is recorded before authorization succeeds,
  and the client reports the mismatch when the server refuses.
- Auto-find is Bonjour-dependent (§5).

### 7.4 Per-device password flow

- Trigger string: `=Please enter the password to use this device` (`0x1409d2b0a`), dialog title
  resource `ENTER_PASSWORD` (`0x1409d2af4` → XRC `0x1409ccbab`), label resource `PASSWORD_LABEL`
  (`0x1409ccff6`).
- The dialog is `saveablePasswordDialog` (`0x1409ccb05`) with a `passwordTextCtrl`
  (`0x1409cd0f6`) styled `wxTE_PASSWORD` (`0x1409cd130`) and a `=Remember` control
  (`0x1409d1fa8`) → the client caches device passwords.
- Two other credential prompts exist and must not be conflated with it:
  server/HTTPS-style `=Username :` / `=Password :` (`0x1409d3a6e` / `0x1409d3a90`),
  and `=Enter the SSL certificate password:` (`0x1409d2c6d`) with
  `=Client Certificate File :` (`0x1409d3140`) / `=Certificate Authority File :` (`0x1409d317a`).
- Server side of this flow: `BIND_PASSWORD_REQUIRED` (`0x14096892e`) is the status the client
  receives, and `Password for device %s is too long, max %d chars` (`0x1409681d2`) is the
  server-side validation rule (the max length is a runtime format argument, so the literal limit is
  not visible as a constant).

### 7.5 Other client features the server does not reveal

- **Client-side licence manager:** `=License...` → `LICENSE_MANAGER_DIALOG_TITLE`
  (`0x1409d1e8e`), `=Current Licenses` (`0x1409d1eae`), `=_Enter Licence(s)` (`0x1409d1edf`),
  `=_Purchase` (`0x1409d1f13`), `=Paste License here` (`0x1409d1f41`),
  `LICENSE_NOT_CONNECTED_MSG` = `=You are not currently connected to any servers.\n\nLicense
  information will appear here when this client is connected to a server` (`0x1409d35c1`).
  The client is licensed too (INFERRED: client licences lift a limit on how many servers one client
  may use; the exact client-side gate was not located — see the method note, the client exceeds the
  analysis budget). What *is* observed is the client relaying server licensing to the user:
  `=Please purchase a license to share more devices simultaneously` (`0x1409d2ccd`) and
  `=This server trial has expired, please purchase a license to continue using.` (`0x1409d2d27`).
- **Reverse connections (server is behind NAT, client accepts inbound):** `=Enable Reverse
  Connections` (`0x1409d1d54`), `=Enable Reverse SSL Connections` (`0x1409d1d97`),
  `=Specify Reverse Clients` (`0x1409d1ddd`), and
  `=Enter the address of the client to reverse connect to:` (`0x1409d367a`).
  Server-side counterparts: `OnAddReverseClient` (`0x14097ff8d`) /
  `OnRemoveReverseClient` (`0x140980048`), both in the paid block.
- **Ignore/Unignore UI:** `=Ignore...` (`0x1409d1b47`), `IGNORED DEVICES :` (`0x1409d3928`),
  `=Ignored Devices` (`0x1409d398b`), table header `Vendor Id\tProduct Id\tSerial Number`
  (`0x1409d39be`), `=Unignore` (`0x1409d39ff` / `0x1409d395f`), and the two ignore granularities:
  `=Ignore all devices with VendorId/ProductId of 0x%04x/0x%04x on this server?` (`0x1409d2366`)
  vs `=Ignore all devices with VendorId/ProductId of 0x%04x/0x%04x?\n\nOR\n\nJust this device
  (qualified by serial %s)?` (`0x1409d23da`), with `=Just This` (`0x1409d3767`).
  → ignore lists are (vid,pid,serial,server) tuples; this maps 1:1 onto the server's
  `AllowedDevices` (`0x1409681c3`) / `IgnoredDevices` (`0x140980066`).
- **Nickname rules:** `=Please enter a nickname for this device` (`0x1409d299f`) and
  `=Sorry, the nickname cannot contain a comma.` (`0x1409d29e2`) — a comma is the blob separator
  server-side (§4), which is the give-away that the nickname store is a comma-delimited record.
- **Server rename:** `=Please enter a name for this USB Server` (`0x1409d2a27`) — gated server-side
  (`onServerRename`).
- **Transport/telemetry knobs:** `=Transport` (`0x1409d3344`), `=Compression Threshold :`
  (`0x1409d336a`) with `=bytes` (`0x1409d3397`), `=Ping` (`0x1409d32c3`),
  `=Server Ping Period :` (`0x1409d32e2`), `=Server Ping Timeout :` (`0x1409d3312`),
  `=Latency (Low & Consistent is best)` (`0x1409d2faf`), `=(Latency)` (`0x1409d2f4d`).
- **Server Properties fields** (what the client learns from a server): `=MAX DEVICES :`
  (`0x1409d223d`), `=EASYFIND :` (`0x1409d233e`), `=CONNECTION ID :` (`0x1409d2273`),
  `=SERIAL NUMBER :` (`0x1409d22ab`), `=INTERFACE :` (`0x1409d22e4`), `=STATE :` (`0x1409d2312`),
  `=VERSION :` / `=ADDRESS :` / `=CONNECTED FOR :` / `=NAME :`.
  Device Properties fields: `=Name :`, `=Vendor :`, `=Product :`, `=Vendor ID :`, `=Product ID `,
  `=Serial :`, `=Address :` (`0x1409d2007`–`0x1409d211b`).
- **Hot keys:** `=Setup Hot Keys` (`0x1409d1cb2`), `=Hot Key to Use:` (`0x1409d2d89`),
  `=Hot Key to Stop Using:` (`0x1409d2db4`).
- **Logging / debug:** `=Log to file` (`0x1409d3b92`), `=Logging data to %s` (`0x1409d3bb6`),
  `=This log has been redirected to %s` (`0x1409d2774`), `=The log file %s cannot be opened (%s)`
  (`0x1409d27b4`), `=Debug Mode` (`0x1409d1709`).
- **Self-update:** `=Please wait, checking for updates...` (`0x1409d286d`),
  `=A client update is available at %s` (`0x1409d28b0`),
  `=You currently have the latest version of the client.` (`0x1409d28f3`),
  `=There was an error checking for the latest version.` (`0x1409d2947`).
- **Localisation:** the UI string block is repeated per language
  (e.g. `LICENSE_MENU_ITEM` at `0x1409d16cb`, `0x1409d4030`, …; French `=Auto-Utilisation …`
  `0x1409d66fa`), plus `=Language to Use for this Client` (`0x1409d382d`).
- **Client service install** mirrors the server's: `=%s service installed and started successfully.
  Run the client again to control the service` (`0x1409d2e7e`), `=Successfully removed the %s
  service` (`0x1409d2ef6`), and `=Installing as a service from a network drive is not
  supported…` (`0x1409d3c68`).

### 7.6 KVM-over-IP subsystem present inside the client (OBSERVED-bytes)

Nine build paths leak the client's source layout, all under one subsystem directory:
`/media/development/virtualhere/kvmip/Dongle.cpp` (`0x1409b48d1`), `KVMGlobals.h` (`0x1409b5ebb`),
`EmulatorDongle.cpp` (`0x1409b7339`), `HIDHelper.cpp` (`0x1409b7537`), `HubManager.cpp`
(`0x1409b7685`), `KVMIP.cpp` (`0x1409b8be4`), `KVMPanel.cpp` (`0x1409ba770`), `KVMViewer.cpp`
(`0x1409bdc6d`), `KeyboardPassthrough.cpp` (`0x1409c0ad8`). Supporting UX: `=Hub Control`
(`0x1409b7a59`), `=Rebooted Hub` (`0x1409b776c`), `=Hub Reset` (`0x1409b7828`),
`=Attach Port 1 to Alternate Host` (`0x1409b7a39`), `=Failed to send HUB request for power switch,
is the hub still connected?` (`0x1409b7a75`), `=Failed to switch USB port power…` (`0x1409b7b01`),
`=Image Format` (`0x1409d3be3`) / `=View` (`0x1409d3c0b`) / `=Stop Viewing` (`0x1409d3c31`),
`=Cannot retrieve format for video device attached` (`0x1409ba892`).
Reading: VirtualHere ships a KVM-over-IP / remote-console product line (virtual dongles, HID
passthrough, hub port power + alternate-host switching) inside the same client. It is out of scope
for VirtualThere v1 (INFERRED as a scoping decision, not observed as a licence gate) but it is a
real "feature" in any parity conversation, and `Attach Port 1 to Alternate Host` proves the hub
control path is the same mechanism a "kick"/re-plug feature rides on.

## 8. URB / transfer model (OBSERVED strings, INFERRED semantics)

The server is a **USB proxy, not a device emulator**: it claims the device through WinUSB / the
`vhfilter` bus driver and forwards URB-shaped operations to the client.
- Endpoint-granular error handling: clear-halt and reset-pipe are per endpoint (`ep %02x`).
- Isochronous transfers get dedicated buffers (`Error %u allocating iso buffer for ep %02x …`).
- Backpressure is tracked as `N outstanding in-flight URBs` (`0x1409686ab`).
- Which of control/bulk/interrupt/iso is used where is not stated in strings; that the client
  drives URBs and the server executes them ~1:1 is INFERRED from the endpoint/halt/iso vocabulary.
  No specific packet-layout claim is made anywhere in this document — that would be invented.
- Shutdown is explicit rather than abrupt: `shutdown start on%s connection` (`0x1409802e5`) on the
  server; `Shutdown send direction error: %d. Broken server? Proceeding as if everything is ok.`
  (`0x14097fa44`) on the client.

## 9. Approach & legal position (READ THIS)

The shipped EULA forbids *"modify, translate, reverse engineer or decompile the Software"* and
*"create derivative works based on the Software."* Therefore:

- We do **not** ship or port any VirtualHere code, and we do **not** aim for wire compatibility
  with their proprietary protocol. Doing so would be a derivative work and is not something to
  open-source. **VirtualThere will NOT be wire-compatible with VirtualHere — by design.** The
  protocol is proprietary and undocumented, and per §3 it has both TLS and cleartext variants with
  a bespoke framing; re-implementing it byte-for-byte is exactly the "create derivative works" the
  EULA forbids, and it would mean tracking a moving target across their releases.
- REA is used here the way clean-room engineering uses it: to enumerate **features, config
  semantics and the licensing model** so VirtualThere reaches functional parity — not to lift
  implementation.
- VirtualThere is built on the **open USB/IP** standard (Linux `usbip` protocol; Windows already
  has `usbipd-win`). That gives an unbounded device count, a documented wire protocol, and no
  encumbrance. Parity is then re-implementing the *experience*: nicknames, ignore/allow lists,
  auto-discovery/auto-attach, per-device passwords, hotplug/kick, script hooks, tray UI.
- "Parity" means parity of **behaviour and configuration vocabulary**, never parity of bytes on the
  wire. A VirtualThere client cannot attach to a VirtualHere server and vice versa.

## 10. Parity checklist (summary)

The authoritative, filled-in matrix is `docs/FEATURE_MATRIX.md`; short form:

| VirtualHere behaviour | Free? | VirtualThere target |
|---|---|---|
| Share 1 device | yes | unlimited |
| Share >1 device | paid | **free, unlimited** |
| onClientConnect / onClientDisconnect hooks | paid | free |
| OnDeviceKick / Ignore / Unignore | paid | free |
| Device nicknames | yes | yes |
| Allowed/ignored device lists | yes | yes |
| Per-device password | yes | yes |
| Auto-discovery (DNS-SD) + auto-attach | yes | yes |
| Hotplug / port cycle | yes | yes |
| TLS encryption + LZ4 compression | yes | yes |
| Tray UI, service install, portable build | yes | yes |
