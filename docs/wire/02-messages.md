# VirtualHere Wire Protocol — Message Catalog

**Source**: REA (Ghidra 12.1.4) over unpacked `vhusbdwinw64.exe`, image base `0x140000000`.  
**Dispatch**: `FUN_140749c30` (msgRx%u thread) reads `type = payload[0]`, calls handler at `DAT_140a5e1e0 + type*8`.

---

## Message Type Table (OBSERVED handlers)

| Type | Hex | Dir | Handler | Name | Payload / Behavior |
|---|---|---|---|---|---|
| 0 | 0x00 | C→S | `FUN_14073ff90` | **HandshakeReq** | Client name + 4 flag bytes; see 01-framing.md §2 |
| 1 | 0x01 | S→C | (from 0x00) | **HandshakeResp** | Server info block (724 bytes); licence error → Type 0x18 |
| 11 | 0x0b | S→C | `FUN_14073ff00` | **DeviceList?** | Allocates 33-byte msg, type=0x0b, sends via `FUN_1407463a0`; purpose **UNKNOWN** |
| 12 | 0x0c | S→C | `FUN_140749dd0` | **Ping** | 4-byte Unix timestamp at payload[0]; sent every `PingInterval` sec |
| 14 | 0x0e | C→S | `FUN_140740910` | **DeviceDataReq** | Var-len payload at 0x31; see §1 below |
| 17 | 0x11 | C→S | `FUN_1407407d0` | **Echo** | No payload; echoes req IDs (cookie, req_id_1, req_id_2, a_field) in reply |
| 19 | 0x13 | C→S | `FUN_140740b90` | **BindDevice** | `payload[0]=='J'` → bind; else unbind; see §2 |
| 21 | 0x15 | C→S | `FUN_140740da0` | **DevControl_15** | Returns Type 0x15; queues to device via `FUN_140745d90` |
| 23 | 0x17 | C→S | `FUN_140740e80` | **DevControl_17** | Returns Type 0x17; queues to device via `FUN_140745d60` |
| 24 | 0x18 | S→C | (from 0x00) | **Error** | Text error (licence: "VirtualHere USB Server Trial Edition") |
| 49 | 0x31 | C→S | `FUN_140741010` | **DevControl_31** | Returns Type 0x31; queues to device via `FUN_140745d30` |

**~35 additional handlers** in table (indices 2–10, 12, 13, 15, 16, 18–30, 32–48) — **UNKNOWN**.

---

## 1. Type 0x0e — Device Data Request (`FUN_140740910`)

**Request payload** (starts at wire offset 0x21):

| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | `subtype` (signed char) — if < 0, no data follows |
| 0x01 | 1 | `flag_1` |
| 0x02 | 2 | `param_1` (u16) |
| 0x04 | 2 | `param_2` (u16) |
| 0x06 | 2 | `data_len` (u16) — length of following data |
| 0x08 | N | `data` (raw bytes) — only present if `subtype >= 0` |

**Reply** (Type 0x0e): Same header fields echoed; response payload at 0x31:
| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | `subtype` (echoed) |
| 0x01 | 1 | `flag_1` (echoed) |
| 0x02 | 2 | `param_1` (echoed) |
| 0x04 | 2 | `param_2` (echoed) |
| 0x06 | 2 | `data_len` (u16) |
| 0x08 | N | Response data |

**Dispatch**: Queued to device's internal queue (`DAT_140a5d8b0` list) via `FUN_140713590` with callback `FUN_140745e00`.

---

## 2. Type 0x13 — Bind / Unbind Device (`FUN_140740b90`)

**Request payload** (at wire offset 0x21):

| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | **Action flag**: `'J'` (0x4a) = **Bind**, anything else = **Unbind** |
| 0x01 | 4 | `data_len` (u32) — length of device identifier string |
| 0x05 | N | `device_id` (UTF-8, NOT NUL-terminated) — format `"vvvv:pppp[:serial]"` |
| ... | 4 | `req_param` (u32) at offset 0x2a |
| 0x25 | 1 | `flag_byte` |
| 0x26 | 4 | `extra_param` (u32) at offset 0x26 |

**Bind path** (`payload[0] == 'J'`): `data_len` = string length; `device_id` parsed by `FUN_140745dc0`.

**Unbind path**: Uses first 4 bytes of `device_id` as integer handle.

**Reply** (Type 0x13): Same header; payload at 0x25:
| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | Result: 0 = success, 1 = **device in use**, other = error |
| 0x01 | 4 | `extra_param` (echoed) |
| 0x05 | 2 | `param_1` (echoed) |
| 0x07 | 2 | `param_2` (echoed) |
| 0x09 | N | Optional device info string (if bind succeeded) |

**BIND Status Codes** (from string table at `0x1409688c0` / `0x1409689a0`):

| Code (int32) | Hex | String | Meaning |
|---|---|---|---|
| -0x1ea | 0xfffffe16 | `BIND_ERROR` | Generic failure |
| -0x1e6 | 0xfffffe1a | `BIND MAX_DEVICES_EXCEEDED` | Device cap reached (licence) |
| -0x1e1 | 0xfffffe1f | `BIND_UNAUTHORIZED` | Password required / auth failed |
| ... | 0xfffffe... | `BIND_MAX_CLIENTS_EXCEEDED` | Too many clients on device |
| ... | ... | `BIND_PASSWORD_REQUIRED` | Per-device password not provided |
| ... | ... | `BIND UNKNOWN ERROR` | Catch-all |

> **OBSERVED**: The int32 codes are negative (high bit set). Exact mapping to table indices **INFERRED** from adjacency.

---

## 3. Type 0x15 / 0x17 / 0x31 — Device Control Opcodes

All three follow the same pattern: client sends request → server queues to device's internal queue (`DAT_140a5d8b0`) → device worker processes asynchronously → reply sent back via `FUN_1407463a0`.

**Common request payload** (at wire offset 0x21):
| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | `endpoint` / `pipe_id` (u8) |
| 0x01 | 1 | `flags` |
| 0x02 | 2 | `param_1` (u16) |
| 0x04 | 2 | `param_2` (u16) |
| 0x06 | 4 | `transfer_len` / `buffer_len` (u32) |
| 0x0a | N | Optional data (for OUT transfers) |

**Reply** (Type = same as request): Header echoed; payload at 0x25:
| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | `status` (0 = success, else Win32/WinUSB error) |
| 0x01 | 4 | `actual_length` (u32) |
| 0x05 | N | Data (for IN transfers) |

**Differences**:
- **Type 0x15** (`FUN_140740da0`): Callback `FUN_140745d90` — likely **bulk/interrupt OUT**
- **Type 0x17** (`FUN_140740e80`): Callback `FUN_140745d60` — likely **bulk/interrupt IN**
- **Type 0x31** (`FUN_140741010`): Callback `FUN_140745d30` — likely **control transfer** (setup packet at 0x21)

**UNKNOWN**: Exact mapping of type → USB transfer kind (control/bulk/interrupt/iso).

---

## 4. Type 0x0b — Device List Response? (`FUN_14073ff00`)

```c
undefined8 FUN_14073ff00(undefined4 param_1) {
  puVar1 = calloc(1, 0x21);      // 33-byte message
  *puVar1 = 0xb;                 // type = 0x0b
  FUN_1407463a0(param_1, puVar1); // send
  return 1;
}
```

**No payload beyond header**. Purpose **UNKNOWN** — possibly "device list changed" notification or periodic heartbeat. Sent in response to **UNKNOWN** trigger.

---

## 5. Error Message (Type 0x18)

Sent by handshake handler (`FUN_14073ff90`) when licence check fails:

| Field | Value |
|---|---|
| Type | 0x18 |
| Payload | NUL-terminated UTF-8 string: `"VirtualHere USB Server Trial Edition"` or `"Invalid License"` |
| Trigger | `strcmp(DAT_140a5f258, "unlicensed") == 0` |

---

## 6. Device Enumeration Format (INFERRED from log strings)

Log line: `"Found %s speed device [%04x:%04x] \"%s, %s\" at address %u"`

Fields observed in server log (`server.log`):
- `busid`: `"1-5fe7a04f"` (hub-port-path hash)
- `vid`: `"174c"` (hex string)
- `pid`: `"55aa"` (hex string)
- `speed`: `"unknown"` / `"high"` / `"full"` / `"low"`
- `class`: `"per-interface"` / `"hub"` / etc.
- `password`: `false`/`true`
- `new`: `true`/`false`
- `state`: `"RebindOffered"` / `"Capturable"` / `"Ignored"` / `"CaptureUnsupported"` / `"UnsafeToExport"`

**Wire format for device list**: **UNKNOWN** — likely sent as Type 0x0b or a separate type not yet analyzed.

---

## 7. Nickname / Ignore / Allow / Password (INFERRED from config strings)

Config keys (from `FUN_14070b8e0` calls):
- `DeviceNicknames` — comma-separated: `vid,pid,addr,nickname`
- `IgnoredDevices` / `IgnoredBuses` — list of `vid:pid[:serial]`
- `AllowedDevices` — allow-list
- `Password for device %s` — per-device password (max length checked)

**Wire ops**: **UNKNOWN** — likely part of Type 0x0e/0x13/0x15 family or separate types.

---

## 8. Server Rename / Client Authorization Hooks

Script hooks (require licence, gate at `strcmp(DAT_140a5f258, "unlicensed")`):
- `onClientConnect` — `$CLIENT_ID$`, `$CLIENT_IP$`, `$CONNECTION_ID$`
- `OnDeviceKick` — `$KICKER_ID$`, `$KICKER_IP$`, `$PRODUCT_SERIAL$`
- `onDeviceIgnore` / `onDeviceUnignore` — `$UNIGNORE$`
- `ClientAuthorization` — `$PASSWORD$`, `$NUM_BINDINGS$`
- `ClientDeauthorization`
- `onServerRename`

**Wire representation**: Hooks execute locally on server; no direct wire message unless hook uses `OnDeviceKick` → triggers Type 0x13 unbind.

---

## 9. Unknown Message Types (Table Indices Not Yet Analyzed)

Indices 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13, 15, 16, 18, 19, 20, 22, 24–48 → **UNKNOWN**.

Likely candidates (from string refs):
- Device add/remove notification
- Interface/endpoint descriptor fetch
- String descriptor fetch
- Hub/port status change
- Isochronous transfer setup
- Clear halt / reset endpoint / reset device
- Get configuration / set configuration
- Get/set interface
- Sync frame

---

## 10. Hex Example — Bind Request (Type 0x13)

```
13 00 00 00 00  78 56 34 12 00 00 00 00   # type=0x13, cookie=0x12345678
01 00 00 00                                 # req_id_1 = 1
00 00 00 00 00 00 00 00                     # req_id_2 = 0
05 00 00 00                                 # a_field = 5
12 00 00 00                                 # payload_len = 18
4a                                          # 'J' = bind
0c 00 00 00                                 # data_len = 12
31 37 34 63 3a 35 35 61 61 3a 31 32       # "174c:55aa:12" (device_id)
00 00 00 00                                 # req_param = 0
00                                          # flag_byte = 0
00 00 00 00                                 # extra_param = 0
```

---

## 11. Hex Example — Echo Request/Reply (Type 0x11)

**Request**:
```
11 00 00 00 00  78 56 34 12 00 00 00 00
03 00 00 00                                 # req_id_1 = 3
00 00 00 00 00 00 00 00                     # req_id_2 = 0
07 00 00 00                                 # a_field = 7
00 00 00 00                                 # payload_len = 0
```

**Reply** (identical header, type=0x11, payload_len=0):
```
11 00 00 00 00  78 56 34 12 00 00 00 00
03 00 00 00
00 00 00 00 00 00 00 00
07 00 00 00
00 00 00 00
```