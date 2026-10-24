# VirtualHere Wire Protocol — Framing & Connection Layer

**Source**: REA (Ghidra 12.1.4) over unpacked `vhusbdwinw64.exe` (server), image base `0x140000000`.  
**Status**: v0.1.0 — all offsets and types are **OBSERVED** unless marked **INFERRED** or **UNKNOWN**.

---

## 1. Listener Setup (OBSERVED)

| Parameter | Value / Function | Notes |
|---|---|---|
| Default plain TCP port | **7575** (`0x1d97`) | `FUN_14074d9d0` line 1333515; override via `TCPPort` config |
| Default SSL/TLS port | **7574** (`0x1d96`) | `FUN_14074d9d0` line 1334763; override via `SSLPort` config |
| Bind address | Config `NetworkInterface` (empty = all) | `FUN_14070ba90("NetworkInterface")` |
| TLS library | **mbedTLS** (not OpenSSL/schannel) | Strings `mbedtls_ssl_*` throughout `ssl_msg.c` |
| Cert/key files | Config `SSLCertFile`, `SSLKeyFile`, `SSLCAFile`, `SSLCRLFile` | Loaded in `FUN_14074e930` (lines 1333968-1334050) |
| Client cert verification | Optional, controlled by `SSLUseClientCerts` | `FUN_14074f400` line 1334387 |
| Cipher/min-version | mbedTLS defaults (not explicitly configured) | **UNKNOWN** — no explicit config found |

**Two listeners run simultaneously** (plain + TLS) — `FUN_14074d9d0` (plain) and `FUN_14074eca0` (reverse/SSL client). The plain listener is created first; TLS listener is added when cert files are present and licensed.

---

## 2. Post-Connect Byte Exchange (OBSERVED)

**Client speaks first** — sends **Type 0x00 (Handshake Request)** immediately after TCP/TLS connect.

No banner, no hello, no nonce. The handshake request *is* the first packet.

**Handshake Request (Type 0x00) payload layout** (from `FUN_14073ff90` at param_2):

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0x00 | 8 | Connection header fields (opaque, echoed in reply) | Copied from transport layer |
| 0x05 | 8 | `conn_cookie` (64-bit) | Echoed in reply at +0x05 |
| 0x0d | 4 | `req_id_1` (u32) | Echoed in reply at +0x0d |
| 0x11 | 8 | `req_id_2` (u64) | Echoed in reply at +0x11 |
| 0x19 | 4 | `a_field` (u32) | **Echoed in all replies**; client increments per request |
| 0x1d | 4 | **Payload length** (u32, little-endian) | Must be ≤ 0x7f for basic handshake |
| 0x21 | ≤0x3f | `client_name` (UTF-8, NUL-terminated, max 63 bytes) | Stored at connection record +0x4c |
| 0x61 | ≤0x3f | `extra_name` (UTF-8, max 63 bytes) | Stored at +0x6c; purpose **UNKNOWN** |
| 0xa1 | 1 | `flag_1` | Stored at +0x14c; **UNKNOWN** meaning |
| 0xa2 | 1 | `flag_2` | Stored at +0x299; **UNKNOWN** |
| 0xa3 | 1 | `flag_3` | Stored at +0x14d; **UNKNOWN** |
| 0xa4 | 1 | `flag_4` | Stored at +0x29b; **UNKNOWN** |

**Handshake Response (Type 0x01)** — sent by server via `FUN_1407463a0`:

| Offset | Size | Field |
|---|---|---|
| 0x00 | 1 | Message type = 0x01 |
| 0x05 | 8 | Echoed `conn_cookie` |
| 0x0d | 4 | Echoed `req_id_1` |
| 0x11 | 8 | Echoed `req_id_2` |
| 0x19 | 4 | Echoed `a_field` |
| 0x1d | 4 | Payload length = **0x2d4** (724 bytes) |
| 0x21 | 0x2d4 | Server info block (see §2.1) |

### 2.1 Server Info Block (Type 0x01 payload) — **OBSERVED**

| Offset (from 0x21) | Size | Field |
|---|---|---|
| 0x00 | 4 | Reply size field (0x2d4) |
| 0x04 | 8 | Echoed `conn_cookie` |
| 0x0c | 4 | Echoed `req_id_1` |
| 0x10 | 8 | Echoed `req_id_2` |
| 0x18 | 4 | Echoed `a_field` |
| 0x1c | 1 | Protocol version / capability byte = 0x08 (observed) |
| 0x1d | 2 | 0x0804 (flags) |
| 0x1f | 8 | Reserved / zero |
| 0x22 | 128 | `ServerName` (UTF-8, NUL-terminated; `$HOSTNAME$`/$SERIAL$ expanded) |
| 0xa2 | 256 | Hostname (UTF-8) |
| 0x1a2 | 2 | `max_devices` from licence (u16; 0 = unlimited) |
| 0x1a4 | 1 | `RemoteAdmin` enabled flag |
| 0x1a5 | 1 | `EasyFind` enabled flag |
| 0x1a6 | 16 | `EasyFindId` (length-prefixed) |
| 0x1b6 | 4 | `EasyFindPin` (length-prefixed) |
| 0x1ba | 1 | `DisableEasyFind` flag |
| 0x1bb | 1 | `HideClientInfo` flag |
| 0x1bc | ... | Additional config flags |

**Licence error path**: if `DAT_140a5f258 == "unlicensed"`, server sends **Type 0x18** (error) with text `"VirtualHere USB Server Trial Edition"` or `"Invalid License"` (lines 1323001-1323018).

---

## 3. Wire Header Layout (OBSERVED)

Every message on the wire has a **33-byte (0x21) header** followed by payload.

| Offset | Size | Field | Endianness | Meaning |
|---|---|---|---|---|
| 0x00 | 1 | **Message Type** | — | Dispatch key (index into handler table at `DAT_140a5e1e0`) |
| 0x01 | 4 | Reserved / zero | LE | Always 0 in captures |
| 0x05 | 8 | `conn_cookie` | LE | Per-connection opaque cookie (from handshake) |
| 0x0d | 4 | `req_id_1` | LE | Request identifier 1 (client-chosen) |
| 0x11 | 8 | `req_id_2` | LE | Request identifier 2 (client-chosen) |
| 0x19 | 4 | **`a_field`** | LE | **Monotonic request sequence** (client increments per logical request) |
| 0x1d | 4 | **Payload Length** | LE | **Compressed** payload size in bytes (0 = no payload) |
| 0x21 | N | **Payload** | — | Compressed (if > CompressionLimit) or raw |

**Total wire size** = `0x21 + payload_length`.

**Max message size**: both compressed and uncompressed sizes must be `< 0x3200001` (50 MB) — checked at `FUN_140748ac0` lines 1330061, 1330117.

**Byte order**: All multi-byte integers are **little-endian** (x86 native).

---

## 4. Compression (OBSERVED)

- **Algorithm**: **LZ4** (block format, not frame) — decompression logic in `FUN_140748ac0` lines 1330149-1330330 (complex sliding-window decoder matching LZ4 block structure).
- **Trigger**: Messages with `payload_length > CompressionLimit` are compressed before send.
- **Config**: `CompressionLimit` (u32, default **UNKNOWN** — read via `FUN_14070ba90("CompressionLimit")` at line 1328635).
- **On wire**: Compressed flag is **not explicitly marked** in the header; the receiver attempts LZ4 decode on *every* message with `payload_length > 0` and falls back to raw if decode fails (inferred from decompression loop structure).
- **Uncompressed size**: Not transmitted; decoder reconstructs from LZ4 block tokens.

**UNKNOWN**: Whether client→server messages are also compressed, or only server→client.

---

## 5. Encryption / Integrity (OBSERVED)

| Layer | Protection |
|---|---|
| TLS (mbedTLS) | Full record-layer encryption + AEAD when SSL listener used |
| Plain TCP | **None** — no HMAC, no checksum, no obfuscation beyond LZ4 |
| Payload | No additional encryption/signing |

**No per-message integrity check** on plain TCP. The 4-byte payload length is the only framing guard.

---

## 6. Keepalive / Ping / Timeouts (OBSERVED)

| Mechanism | Details |
|---|---|
| **Ping interval** | Config `PingInterval` (seconds, default **4**) — read in `FUN_140749dd0` line 1330909, `FUN_140749f70` line 1330994 |
| **Ping message** | Type **0x0c**, payload = 4-byte **Unix timestamp** (seconds since epoch) at offset 0x21 |
| **Ping sender** | Server → client, from `FUN_140749dd0` (msgRx%u thread, but actually a timer thread) every `PingInterval` seconds |
| **Rx timeout** | Server tracks last activity per connection; if `difftime(now, last_rx) > PingInterval` → marks connection for close (`FUN_140749f70` lines 1331037-1331054) |
| **Tx timeout** | Separate thread `FUN_140749f70` also checks send-side stalls |
| **Graceful disconnect** | Peer sends zero-byte TCP FIN; server logs `"Connection %d remotely disconnected gracefully (rx msg size)"` (line 1330788) |
| **Abrupt disconnect** | `WSAGetLastError() == 0x6c` (WSAECONNRESET) → logs `"abruptly"` (line 1330792) |

---

## 7. Per-Connection Queues (OBSERVED — wire-relevant only)

The 0x2a0-byte connection record (allocated in `FUN_140746630`) contains three queue heads at offsets:

| Offset | Queue | Purpose (wire-relevant) |
|---|---|---|
| +0x10c | **TX Queue** | Messages waiting to be sent (type, a_field, size). Drained by `FUN_140746ef0` (msgTx%u thread). |
| +0x110 | **RX Queue** | Incoming messages awaiting dispatch. Drained by `FUN_140749c30` (msgRx%u thread). |
| +0x148 | **URB Queue** | USB transfer requests (URBs). Drained by `FUN_140748ac0` / `FUN_140749dd0`. |

**Priority**: Not observed on wire — all three are FIFO. The TX thread (`FUN_140746ef0`) logs `"tx:t=%d,a=%u,s=%u"` (type, a_field, wire_size) for each dequeued message.

---

## 8. Message Type Catalog (Index into `DAT_140a5e1e0`)

| Type | Hex | Dir | Handler | Description |
|---|---|---|---|---|
| 0 | 0x00 | C→S | `FUN_14073ff90` | Handshake / login request |
| 1 | 0x01 | S→C | (sent by 0x00 handler) | Handshake response / server info |
| 11 | 0x0b | S→C | `FUN_14073ff00` | **UNKNOWN** — sends type 0x0b, no payload |
| 12 | 0x0c | S→C | `FUN_140749dd0` | **Ping** (timestamp) |
| 14 | 0x0e | C→S | `FUN_140740910` | Device data request (has var-len payload at 0x31) |
| 17 | 0x11 | C→S | `FUN_1407407d0` | **Echo/ack** — copies req IDs, no payload |
| 19 | 0x13 | C→S | `FUN_140740b90` | **Bind/Unbind** device (checks `payload[0]=='J'` for bind) |
| 21 | 0x15 | C→S | `FUN_140740da0` | Device control (returns type 0x15) |
| 23 | 0x17 | C→S | `FUN_140740e80` | Device control (returns type 0x17) |
| 24 | 0x18 | S→C | (sent by 0x00 handler) | **Error** (licence / protocol) |
| 49 | 0x31 | C→S | `FUN_140741010` | Device control (returns type 0x31) |

**Full catalog** (message payloads, request/response pairs, device enumeration format) → see `docs/wire/02-messages.md`.

---

## 9. Unknown / To Verify

| Item | Status |
|---|---|
| Exact `CompressionLimit` default value | UNKNOWN (config key exists, default not found in decompile) |
| TLS cipher suite / min version | UNKNOWN (mbedTLS defaults used implicitly) |
| Meaning of handshake flags at 0xa1–0xa4 | UNKNOWN |
| Purpose of `extra_name` at 0x61 | UNKNOWN |
| Whether client→server messages are compressed | UNKNOWN |
| Exact LZ4 block format variant (block vs frame, checksum) | INFERRED block, **UNKNOWN** checksum |
| Full set of message types (table has ~45 entries, only 11 analyzed) | PARTIAL |
| Device enumeration payload format (Type 0x0b response?) | UNKNOWN |
| URB/message type mapping for USB transfers | See `docs/wire/03-urb.md` |

---

## 10. Hex Example — Handshake Request (Type 0x00)

```
00 00 00 00 00  00 00 00 00 00 00 00 00   # header: type=0, reserved, cookie=0
00 00 00 00                                 # req_id_1 = 0
00 00 00 00 00 00 00 00                     # req_id_2 = 0
01 00 00 00                                 # a_field = 1
2a 00 00 00                                 # payload_len = 42 (0x2a)
4d 79 43 6c 69 65 6e 74 00                  # "MyClient\0" at 0x21
... (rest padded/truncated)
```

---

## 11. Hex Example — Ping (Type 0x0c, Server→Client)

```
0c 00 00 00 00  78 56 34 12 00 00 00 00   # type=0xc, cookie=0x12345678
00 00 00 00                                 # req_id_1
00 00 00 00 00 00 00 00                     # req_id_2
00 00 00 00                                 # a_field = 0 (pings don't increment)
04 00 00 00                                 # payload_len = 4
7b 9a 7e 67                                 # timestamp = 0x677e9a7b (Unix time)
```