# VirtualHere Wire Protocol — USB Transfers (URBs)

**Source**: REA (Ghidra 12.1.4) over unpacked `vhusbdwinw64.exe`.  
**Scope**: How USB Request Blocks (URBs) are carried over the wire after a device is bound.

---

## 1. Architecture (OBSERVED)

- **Server-side USB I/O**: WinUSB (`WinUsb_ControlTransfer`, `WinUsb_ReadPipe`, `WinUsb_WritePipe`, `WinUsb_ResetPipe`, `WinUsb_AbortPipe`, `WinUsb_SetPipePolicy`, `WinUsb_QueryPipe`, `WinUsb_GetDescriptor`, `WinUsb_IsochReadPipe`, etc.)
- **Kernel filter**: `\\??\\GLOBALROOT\\Device\\vhfilter` (string at `0x140974d00`) — used for `onPassToKernel` hook, purpose **UNKNOWN**.
- **Per-device queue**: Each bound device has a queue at `DAT_140a5d8b0` (list head). Device worker threads (`FUN_140713590` callbacks) process URBs asynchronously.
- **Wire types for URBs**: Types **0x0e, 0x11, 0x13, 0x15, 0x17, 0x31** (and ~35 others) map to different USB transfer kinds.

---

## 2. URB Message Types (OBSERVED / INFERRED)

| Type | Hex | Likely Transfer Kind | Handler | Callback |
|---|---|---|---|---|
| 14 | 0x0e | **Device Data** (generic) | `FUN_140740910` | `FUN_140745e00` |
| 17 | 0x11 | **Echo / Keepalive** | `FUN_1407407d0` | — |
| 19 | 0x13 | **Bind / Unbind** | `FUN_140740b90` | `FUN_140745dc0` |
| 21 | 0x15 | **Bulk/Interrupt OUT** | `FUN_140740da0` | `FUN_140745d90` |
| 23 | 0x17 | **Bulk/Interrupt IN** | `FUN_140740e80` | `FUN_140745d60` |
| 49 | 0x31 | **Control Transfer** | `FUN_140741010` | `FUN_140745d30` |

**UNKNOWN**: Exact mapping of types 0x0e, 0x15, 0x17, 0x31 to control/bulk/interrupt/iso. The three callbacks (`FUN_140745d30`, `FUN_140745d60`, `FUN_140745d90`) are distinct — likely one per transfer direction/kind.

---

## 3. Control Transfers (Type 0x31 — INFERRED)

**Request** (client → server):
| Offset | Size | Field |
|---|---|---|
| 0x21 | 8 | `setup_packet` (bmRequestType, bRequest, wValue, wIndex, wLength) — standard USB 2.0 setup |
| 0x29 | N | Optional data stage (OUT) |

**Reply** (server → client, Type 0x31):
| Offset | Size | Field |
|---|---|---|
| 0x21 | 1 | `status` (0 = success, else WinUSB error) |
| 0x22 | 4 | `actual_length` |
| 0x26 | N | Data stage (IN) |

**WinUSB call**: `WinUsb_ControlTransfer` (imported).

---

## 4. Bulk / Interrupt Transfers (Types 0x15 / 0x17 — OBSERVED)

### 4.1 Common Request Payload (at wire offset 0x21)

| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | `endpoint` (u8) — USB endpoint address (0x01–0x0f, 0x81–0x8f) |
| 0x01 | 1 | `flags` (u8) — **UNKNOWN** bits |
| 0x02 | 2 | `param_1` (u16) — **UNKNOWN** |
| 0x04 | 2 | `param_2` (u16) — **UNKNOWN** |
| 0x06 | 4 | `transfer_len` (u32) — requested byte count |
| 0x0a | N | **OUT data** (only for host→device) |

### 4.2 Reply Payload (at wire offset 0x21)

| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | `status` (0 = success, else Win32/WinUSB error) |
| 0x01 | 4 | `actual_length` (u32) — bytes actually transferred |
| 0x05 | N | **IN data** (only for device→host) |

**Type 0x15** (`FUN_140740da0`): Callback `FUN_140745d90` — **INFERRED** bulk/interrupt OUT (host→device).  
**Type 0x17** (`FUN_140740e80`): Callback `FUN_140745d60` — **INFERRED** bulk/interrupt IN (device→host).

**Pipe policies** (from strings near `WinUsb_SetPipePolicy`): `RAW_IO`, `PIPE_TRANSFER_TIMEOUT`, `ALLOW_PARTIAL_READS`, `AUTO_CLEAR_STALL` — **UNKNOWN** which are set.

---

## 5. Isochronous Transfers (UNKNOWN)

- **Strings**: `"Error %u allocating iso buffer for ep %02x"` (line 1322580), `WinUsb_IsochReadPipe` imported.
- **Wire type**: **UNKNOWN** — likely one of the unanalyzed table entries.
- **Status**: **NOT SUPPORTED** in current analysis (no handler found for iso IN/OUT).

---

## 6. Special URB Operations (OBSERVED strings)

| Operation | WinUSB Call | Log String | Wire Type |
|---|---|---|---|
| **Clear Halt** | `WinUsb_AbortPipe` + `WinUsb_ResetPipe`? | `"Error %d clearing halt on ep %02x"` | UNKNOWN |
| **Reset Endpoint** | `WinUsb_ResetPipe` | `"Error %d resetting pipe on ep %02x"` | UNKNOWN |
| **Reset Device** | `WinUsb_ResetDevice` (not directly imported) | `"onReset"` hook | UNKNOWN |
| **Abort/Unlink** | `WinUsb_AbortPipe` | — | UNKNOWN |
| **Set Config/Interface** | `WinUsb_SetCurrentConfig` / `WinUsb_SetInterface` | — | UNKNOWN |
| **Get Descriptor** | `WinUsb_GetDescriptor` | — | UNKNOWN |

**Hook strings** (called from device worker):
- `onClearHalt` (`0x1409687c2`)
- `onResetEp` (`0x1409687ce`)
- `onReset` (`0x140968283`)
- `onEnumeration` (`0x1409686ab`) — near `"wheel"` / `"driving"` strings (purpose **UNKNOWN**)
- `onPassToKernel` (`0x140967203`) — related to `vhfilter` driver

---

## 7. Request/Response Sequencing (OBSERVED)

1. **Client sends URB request** (Type 0x15/0x17/0x31) with:
   - `a_field` = monotonic sequence number (client increments per logical request)
   - `endpoint`, `transfer_len`, optional data

2. **Server queues to device worker** via `DAT_140a5d8b0` list + callback (`FUN_140713590`).

3. **Device worker** (thread per device?) calls WinUSB, then sends reply via `FUN_1407463a0` with:
   - Same `a_field`, `req_id_1`, `req_id_2`, `conn_cookie`
   - `status`, `actual_length`, data (for IN)

4. **Client matches reply to request** by `a_field` (and/or `req_id_1`/`req_id_2`).

**No explicit ACK/NAK** at wire level — reliability is TCP.

**Flow control / credits**: **NOT OBSERVED** — server posts reads on interrupt endpoints proactively? **UNKNOWN**.

---

## 8. Buffer Limits & Timeouts (INFERRED)

| Limit | Value / Source |
|---|---|
| Max message size | 0x3200001 (50 MB) — both compressed & uncompressed |
| Max transfer per URB | **UNKNOWN** — likely WinUSB default (4 MB?) |
| Transfer timeout | Configurable via `WinUsb_SetPipePolicy(PIPE_TRANSFER_TIMEOUT)` — value **UNKNOWN** |
| Isochronous buffer | Allocated per `"Error %u allocating iso buffer for ep %02x"` — size **UNKNOWN** |

---

## 9. Endpoint Setup at Bind (INFERRED)

When device is bound (Type 0x13 'J'), server:
1. Opens device via WinUSB (`WinUsb_Initialize` → handle)
2. Queries interfaces (`WinUsb_QueryInterfaceSettings`)
3. For each endpoint: `WinUsb_SetPipePolicy` with:
   - `RAW_IO` = TRUE (bypass WinUSB buffering?)
   - `PIPE_TRANSFER_TIMEOUT` = config value
   - `ALLOW_PARTIAL_READS` = TRUE
   - `AUTO_CLEAR_STALL` = TRUE
4. Stores pipe handles in device context (`+0x10180` = device ID, `+0x102a0` = queue pointer)

**Strings**: `"op%u"` format (`0x14096724b`) — likely operation sequence number for URB tracking.

---

## 10. vhfilter Kernel Driver (OBSERVED)

- **Device path**: `\\??\\GLOBALROOT\\Device\\vhfilter` (`0x140974d00`)
- **Hook**: `onPassToKernel` (`0x140967203`) — called with `$DEVPATH$`, `$VENDOR_ID$`, `$PRODUCT_ID$`
- **Purpose**: **UNKNOWN** — possibly:
  - Pass-through for devices WinUSB can't claim
  - Hub/port reset coordination
  - Power management
  - Isochronous scheduling assist

**Wire impact**: **UNKNOWN** — no wire message type directly maps to `onPassToKernel`.

---

## 11. Error Code Mapping (INFERRED)

WinUSB/Win32 errors → wire `status` byte:

| Win32 Error | Wire Status | Meaning |
|---|---|---|
| 0 (ERROR_SUCCESS) | 0 | Success |
| 87 (ERROR_INVALID_PARAMETER) | 1 | Bad parameter |
| 1168 (ERROR_NOT_FOUND) | 2 | Device/endpoint not found |
| 995 (ERROR_OPERATION_ABORTED) | 3 | Transfer cancelled |
| 31 (ERROR_GEN_FAILURE) | 4 | Generic USB error |
| 1117 (ERROR_IO_DEVICE) | 5 | Device I/O error |
| 121 (ERROR_SEM_TIMEOUT) | 6 | Transfer timeout |
| Other | 0xff | Unknown error |

**Exact mapping**: **UNKNOWN** — server logs raw Win32 error codes; wire status byte encoding not fully traced.

---

## 12. Sequence Diagram — Bulk IN Transfer (Text)

```
Client                          Server (Worker)              USB Device
  |                                |                            |
  |--- Type 0x17 (Bulk IN Req) --->|                            |
  |   a_field=42, ep=0x81, len=512 |                            |
  |                                |--- WinUsb_ReadPipe ------->|
  |                                |<-- data (512 bytes) -------|
  |                                |                            |
  |<-- Type 0x17 (Bulk IN Resp) ---|                            |
  |   a_field=42, status=0,        |                            |
  |   actual_len=512, data=[...]   |                            |
  |                                |                            |
```

---

## 13. Hex Example — Bulk IN Request (Type 0x17)

```
17 00 00 00 00  78 56 34 12 00 00 00 00   # type=0x17, cookie=0x12345678
0a 00 00 00                                 # req_id_1 = 10
00 00 00 00 00 00 00 00                     # req_id_2 = 0
15 00 00 00                                 # a_field = 21
12 00 00 00                                 # payload_len = 18
81                                          # endpoint = 0x81 (IN ep 1)
00                                          # flags = 0
00 00                                       # param_1 = 0
00 00                                       # param_2 = 0
00 02 00 00                                 # transfer_len = 512
```

---

## 14. Hex Example — Bulk IN Response (Type 0x17)

```
17 00 00 00 00  78 56 34 12 00 00 00 00
0a 00 00 00
00 00 00 00 00 00 00 00
15 00 00 00
c2 00 00 00                                 # payload_len = 202 (0xc2 = 512 + overhead? compressed?)
00                                          # status = 0 (success)
00 02 00 00                                 # actual_length = 512
[512 bytes of data...]
```

---

## 15. Unknown / To Verify

| Item | Status |
|---|---|
| Exact type → transfer kind mapping (0x0e, 0x15, 0x17, 0x31) | UNKNOWN |
| Interrupt IN: does server pre-post reads or wait for client request? | UNKNOWN |
| Isochronous support (wire format, scheduling) | UNKNOWN — likely absent |
| Clear halt / reset endpoint / reset device wire types | UNKNOWN |
| Get descriptor / set config / set interface wire types | UNKNOWN |
| `onPassToKernel` / `vhfilter` wire interaction | UNKNOWN |
| Flow control / window / credits for bulk endpoints | UNKNOWN |
| `param_1`, `param_2`, `flags` in bulk request | UNKNOWN |
| WinUSB error → wire status byte exact mapping | UNKNOWN |
| Short-transfer handling (actual_length < transfer_len) | OBSERVED in reply but semantics UNKNOWN |