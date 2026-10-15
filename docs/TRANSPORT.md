# VirtualThere transport — TLS, LZ4 and authentication

Wire-format and behaviour reference for `Vht.Transport`, the layer that carries USB/IP bytes between
`vht-server` and `vht-client` (ARCHITECTURE.md §6, §8, Appendix C). Everything below is implemented in
`src/Vht.Transport/**` and exercised by `tests/Vht.Transport.Tests/**` and
`tests/Vht.Transport.Bench/**`.

Status: implemented and measured on this machine (Windows 11, .NET 10.0.13). The benchmark numbers in
§9 are real output, not projections.

---

## 1. Where the transport sits

USB/IP framing is owned by `Vht.Core` and never inspected here. `Vht.Transport` is a duplex byte pipe
with three flavours:

| Kind | Class | Port | Handshake | Auth | Compression |
|---|---|---|---|---|---|
| `PlainTcp` | `PlainTcpTransport` | 3240 | none | impossible (fails closed) | none |
| `Tls` | `TlsTransport` | 3241 | mandatory | optional (shared secret / per-device password) | none |
| `TlsLz4` | `TlsTransport` + `Lz4Transport` decorator | 3241 | mandatory | as `Tls` | LZ4 block, negotiated |

Two listeners, per ARCHITECTURE.md §6.3: the plaintext listener exists so an unmodified Linux
`usbip attach` works, and the TLS listener carries everything else. The hard rule from §6.4 is enforced
in code, not documentation: `TransportOptions.Validate()` **throws** if a `PlainTcp` configuration
carries a shared secret, a per-device password or a client credential, so a password-protected export
cannot be constructed for the plaintext listener at all.

Where TLS terminates is not obvious and matters: the client's UDE driver does its own TCP in kernel via
WSK, so `vht-client` terminates TLS in a userspace loopback bridge and the plaintext leg only ever
exists on 127.0.0.1 (§6.2). `TlsTransport` is therefore used by *both* programs — server-facing on the
client, client-facing on the server.

`TransportFactory` is the only construction path, so no caller can forget the handshake or the
compression decision:

- `TransportFactory.ConnectAsync(host, port, options, credential, ct)`
- `TransportFactory.AcceptAsync(stream, options, remoteEndPoint, ct)`
- `TransportListener.Start(options, address, port)` — accept loop that performs the handshake off the
  accept thread (bounded at 32 concurrent handshakes) and publishes only fully-handshaken transports;
  refusals surface on the `Rejected` event.

Every returned transport has already completed the version/capability handshake, or the call threw.

---

## 2. Frame conventions

- **All integers are big-endian**, matching USB/IP's own convention on the wire.
- Frames exchanged *after* the client hello are prefixed with a 4-byte big-endian length, and that
  length is clamped to `HandshakeProtocol.MaxFrameBytes` (512) **before** any allocation. The client
  hello is the single exception: its size is structurally determined and it is read field by field.
- Every length and enum field is validated on decode. Decode never allocates from an unvalidated
  length; the only variable-size allocations are the client name (≤ 96 bytes) and busid (≤ 64 bytes).
- Magic is ASCII `VHT1` (`0x56 0x48 0x54 0x31`). Version is `1`; the accepted range is `MinVersion..Version`
  = `1..1`, so an unknown version is a refusal, not a negotiation.

---

## 3. The `_vht` version/capability handshake

Performed immediately after the TLS handshake, before a single USB/IP byte.

> There is a second, unrelated `_vht` extension **below** this layer, in the USB/IP op phase: it is
> `OP_REQ_VHT_DEVINFO`/`OP_REP_VHT_DEVINFO` (`0x8f01`/`0x0f01`), which carries the operator's device
> nicknames beside the stock device record. It exists on the plaintext listener too, because that is
> the connection a client lists devices on. Specified in `docs/SERVER.md` §8.

### 3.1 Client hello (no outer length prefix, 51 bytes + name + busid)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `VHT1` |
| 4 | 2 | protocol version (u16, big-endian) |
| 6 | 2 | reserved flags — must be 0 |
| 8 | 4 | `TransportCapabilities` bitmask of the client |
| 12 | 4 | `FeatureFlags` the client can use |
| 16 | 1 | `AuthMethod` the client intends to use |
| 17 | 1 | client-name length (≤ 96) |
| 18 | n | client name, UTF-8 |
| 18+n | 1 | busid length (≤ 64) |
| 19+n | m | busid, UTF-8 |
| 19+n+m | 32 | fresh client nonce |

### 3.2 Server hello (length-prefixed, exactly 84 bytes)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `VHT1` |
| 4 | 2 | protocol version |
| 6 | 2 | reserved flags — must be 0 |
| 8 | 4 | `TransportCapabilities` the server offers |
| 12 | 4 | `FeatureFlags` negotiated (intersection) |
| 16 | 4 | `lz4ThresholdBytes` (0 when LZ4 is not in play) |
| 20 | 4 | `maxPayloadFrameBytes` (bounds both directions, default 4 MiB) |
| 24 | 4 | `pbkdf2Iterations` for the negotiated method |
| 28 | 4 | `AuthStatus` — the server's verdict on the hello |
| 32 | 32 | fresh server nonce |
| 64 | 16 | PBKDF2 salt for the negotiated method (zeroed when method is `None`) |
| 80 | 1 | `AuthMethod` the server requires |
| 81 | 1 | reserved — must be 0 |

The server's `Status` is `Success` for a well-formed hello. A client that receives a non-`Success`
status aborts with `AuthenticationFailed` rather than continuing.

### 3.3 Capability and feature bitmasks

`TransportCapabilities` (what a session *is*): `HandshakeRequired` 0x01, `Lz4` 0x02,
`DevicePassword` 0x04, `SharedSecret` 0x08, `PeerPinning` 0x10, `Isochronous` 0x20.

`FeatureFlags` (the wire bitmask that is intersected): `Lz4` 0x01, `Isochronous` 0x02,
`DevicePassword` 0x04, `SharedSecret` 0x08.

### 3.4 Anti-downgrade rules (acceptance criterion)

A session cannot be negotiated downward. Five distinct mechanisms, each with a test:

1. **First four bytes must be `VHT1`.** A stock USB/IP client opens with `OP_REQ_DEVLIST`
   (`0x80 0x05 ...`), which fails the magic comparison on the first frame — rejected with
   `TransportErrorCode.HandshakeRequired`, never reinterpreted as a frame length and never accepted as
   a stock peer on the TLS listener. *(`StockUsbipBytesAreRejectedAsAHandshakeSkip`,
   `PeerThatSkipsTheHandshakeIsRejected`.)*
2. **TLS record sniffing before Schannel.** The TLS listener reads the first five bytes of the
   connection and requires a plausible TLS handshake record (`0x16 0x03 0x0X`, length 1..16384); the
   consumed bytes are replayed through `PrefixedStream` so `SslStream` still sees the stream from byte
   zero. Byte zero decides it, so a stock peer is rejected in milliseconds instead of holding a
   handshake slot until the timeout. *(`RawTcpPeerIsRejectedByTheTlsListener`.)*
3. **Version floor.** A hello outside `1..1` is refused (`HandshakeRequired`), not downgraded.
   *(`UnsupportedVersionIsRejected`.)*
4. **Capability contradiction check.** `TlsTransport.ValidateNoDowngrade` refuses a server hello that
   contradicts the client hello: a credential offered against a server that answers `AuthMethod.None`
   (which would silently skip authentication), the server claiming no shared-secret or per-device
   support after the client offered one, or the server advertising LZ4 support while not negotiating it.
5. **No code path continues without the handshake.** It runs inside the factory before a transport
   instance exists; there is no "skip" flag, on either end.

`PlainTcp` sessions report `Capabilities = None`. `ITransport.AuthenticateAsync` on them returns
`AuthStatus.Unauthorized` — never a silent success — and the same request through
`TransportOptions.Validate()` cannot even be constructed. *(`PlainTextTransportFailsClosedOnAuthentication`,
`PlaintextCannotCarryAuthentication`, `PlaintextCannotCarryAClientCredential`.)*

---

## 4. TLS (Schannel via `SslStream`)

- **Stack:** .NET's `SslStream`, i.e. Schannel on Windows. No mbedTLS, no vendored crypto, no CA-file
  ceremony in v1 (ARCHITECTURE.md §6.1, §6.5).
- **Protocols:** TLS 1.3 with TLS 1.2 as a floor by default (Schannel only speaks TLS 1.3 on
  Windows 11 / Server 2022, while the driver we consume supports Windows 10 1903+).
  `TlsOptions.RequireTls13` refuses anything below 1.3 after the handshake, and `EnforceProtocolFloor`
  refuses anything below 1.2 unconditionally.
- **Client certificates:** none in v1 (`ClientCertificateRequired = false`); identity is the pinned key
  plus the credential.

### 4.1 Server certificate, generated on first run

`ServerCertificateManager.LoadOrCreate(directory, serverName, out created, allowInsecureKeyStorage)`
generates **ECDSA P-256**, self-signed, 10-year validity, `critical` basic-constraints (CA = false),
key-usage `DigitalSignature | KeyEncipherment`, EKU `serverAuth` (1.3.6.1.5.5.7.3.1), SAN covering the
configured server name, `localhost`, `127.0.0.1` and `::1`, plus a subject-key-identifier.

Files: `server.crt.pem` and `server.key.pem` (unencrypted PKCS#8 — there is no second secret to protect
the key with, so the ACL *is* the protection) under `%ProgramData%\VirtualThere\tls` by default. The
key is written with `FileShare.None` and immediately hardened; hardening is done with in-box
`icacls.exe` (`/inheritance:r /grant:r <domain>\<user>:F`) so that `Vht.Transport` stays a portable
`net10.0` library with no Windows-only package dependency. **Failure to harden is a hard error**, unless
`transport.tls.allow_insecure_key_storage = true` explicitly accepts the risk — an unreadable key is an
outage, a world-readable key is a compromise. *(`CertificateIsGeneratedOnFirstRunWithRestrictedPermissions`.)*

PEM-loaded certificates are re-imported through PKCS#12 once so Schannel gets a stable private-key
handle; the PKCS#12 blob is zeroed after use.

### 4.2 Client pinning (trust on first use)

`TlsOptions.PinMode` — the default for `vht-client`:

| Mode | Behaviour |
|---|---|
| `TrustOnFirstUse` | First certificate for a peer is recorded as an **SPKI SHA-256** pin; every later connection must present the same key. A *changed* key is refused. |
| `Strict` | Refuses any peer with no recorded pin, and any changed pin. The unattended/headless mode. |
| `Insecure` | Accepts anything (`Reason` says pinning is disabled). Loopback-bridge internal hop and tests only. |

The pin is `base64url(SHA-256(SubjectPublicKeyInfo))`, 43 characters, unpadded — the same value
published in the `_vht._tcp` DNS-SD TXT record so discovery can confirm a pin before connecting (§7).
Pinning the SPKI rather than the whole certificate means a certificate renewal with the *same* key
stays trusted and a key change never does.

Store: `JsonPinStore` (default `%LOCALAPPDATA%\VirtualThere\known_peers.json`), human-readable on
purpose, written atomically via `.tmp` + `File.Move`. A **corrupt** store is not treated as "no pins":
that would downgrade every `Strict` peer to unpinned, so `IPinStoreHealth.Healthy` goes false and a
`Strict` client refuses to connect. A missing store is likewise a refusal in both `TrustOnFirstUse` and
`Strict`, never a silent trust. *(`ChangedKeyIsRefusedEvenInTrustOnFirstUse`, `StrictModeRefusesAnUnpinnedPeer`,
`MissingStoreIsRefusedRatherThanSilentlyTrusting`, `CorruptPinStoreIsRefused`,
`WrongPinnedFingerprintFailsClosed`, `StrictModeRefusesUnpinnedServer`.)*

---

## 5. Authentication

Optional in the sense that a server may configure none; when configured it is mandatory and fails
closed. Two methods, chosen by the client in its hello and confirmed by the server:

- `SharedSecret` — server-wide secret;
- `DevicePassword` — per-device password addressed by busid.

`VerifierSet.ResolveRequiredMethod` means a client that asks for *nothing* still gets the server's
strongest configured method; "no method" is never a free pass on a server that has secrets configured.

### 5.1 Verifier derivation

No plaintext secret is stored or transmitted.

```
V = PBKDF2-HMAC-SHA256(secret, salt, iterations, dkLen = 32)         # salt: 16 bytes, per secret
```

`iterations` defaults to **600 000** (`SecretVerifier.DefaultIterations`, ARCHITECTURE.md §6.1),
advertised to the client in the server hello so both ends derive the same `V`. Verifiers are derived
**once per secret and cached** in `VerifierSet`, because 600 000 iterations is deliberately expensive
and must not be paid per connection. *(`DefaultWorkFactorIsSixHundredThousand`.)*

**Honest limitation, stated rather than hidden:** `V` is password-equivalent — anyone who can read the
store can authenticate. That is inherent to a verifier-based challenge-response where both ends derive
the same key from the same salt; the mitigations are file permissions (`SecureFileStore`), the PBKDF2
work factor against the plaintext, and the fact that the exchange only happens inside TLS. SRP would
remove the property at the cost of a bespoke protocol and is out of v1 scope.

### 5.2 Challenge-response

```
clientProof = HMAC-SHA256(V, "vht-auth-client-v1" ‖ clientNonce ‖ serverNonce ‖ method ‖ busid)
serverProof = HMAC-SHA256(V, "vht-auth-server-v1" ‖ clientNonce ‖ serverNonce ‖ method ‖ busid)
```

Both nonces are fresh 32-byte values per attempt, and both are covered by the MAC, so a captured proof
is useless on a second connection and cannot be replayed against a different peer. Distinct labels mean
the two proofs are not interchangeable, so a rogue server cannot complete the exchange by reflecting the
client's own proof — the client verifies the server proof and refuses to trust the peer if it is wrong.
The `busid` field is bound into the proof, so a proof for one device cannot be replayed for another.
*(`SameSecretAndSaltProduceTheSameProof`, `DifferentSaltOrNonceOrDeviceIdChangesTheProof`,
`ClientAndServerProofsAreNotInterchangeable`.)*

**Auth request frame** (length-prefixed, 41 bytes + busid):

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | frame tag `0xA1` |
| 1 | 4 | magic `VHT1` |
| 5 | 2 | version |
| 7 | 1 | `AuthMethod` |
| 8 | 1 | busid length |
| 9 | n | busid |
| 9+n | 32 | client proof |

**Auth reply frame** (length-prefixed, exactly 49 bytes): tag `0xA1`, magic, version u16, `AuthStatus`
i32, `AuthMethod` u8, reserved u8, `retryAfterMs` i32, server proof 32 bytes.

**Verification is constant-time:** `SecretVerifier.Verify` uses
`CryptographicOperations.FixedTimeEquals`; a length mismatch returns false immediately (proof length is
fixed by the protocol, so it is not secret). *(`VerificationRejectsWrongAndTruncatedProofs`.)*

**No device-existence oracle:** an unknown busid is answered with a *dummy* verifier whose derived key
is random, so the challenge is byte-indistinguishable from a real one and the proof simply fails. The
dummy salt is deterministic per busid so probes cannot detect the difference across attempts.
*(`UnknownDeviceGetsADummyChallengeButIsNotKnown`, `DummyVerifierNeverMatchesARealOne`.)*

### 5.3 Fail-closed behaviour

`TlsTransport` holds an `AuthState` of `Pending`, `Authenticated` or `Failed`:

- Unauthenticated read/write → `TransportErrorCode.InvalidState`;
- Failed authentication → `TransportErrorCode.AuthenticationFailed`, and the session is **permanently
  unusable** — not merely an error the caller might ignore;
- A malformed auth frame consumes a rate-limit token and is answered `Malformed`.

*(`DeviceTrafficBeforeAuthenticationIsRefused`, `WrongSharedSecretFailsClosed`,
`WrongDevicePasswordAndUnknownDeviceBothFailClosed`, `CorrectSharedSecretAuthenticatesBothEnds`,
`CorrectDevicePasswordAuthenticates`.)*

### 5.4 Rate limiting and lockout

`AuthThrottle` (three controls, because each covers a different attack; one shared instance per
listener, so limits span connections instead of resetting on reconnect):

| Control | Default | Stops |
|---|---|---|
| Per-peer token bucket (keyed by IP, no ephemeral port) | burst 5, refill 12/min | one host hammering a password |
| Global token bucket | burst 64, refill 120/min | a botnet spreading the same rate over many source addresses |
| Exponential lockout | after 5 consecutive failures, `2s × 2ⁿ` up to 5 min | turning a slow guess into an increasingly expensive one |

A peer's failure history expires after 15 min without a new failure, and a success clears it. A
throttled attempt is answered with `AuthStatus.Throttled` plus a `retryAfterMs` hint, and the client
surfaces it as `AuthResult.RetryAfter`. Time is injected (`TimeProvider`), so the behaviour is tested
without sleeping. *(`BurstIsEnforcedPerPeer`, `TokensRefillOverTime`,
`RepeatedFailuresLockThePeerOutExponentially`, `GlobalBucketStopsDistributedGuessing`,
`SuccessClearsFailureHistory`, `FailureHistoryExpires`, `RepeatedFailuresAreThrottledAcrossConnections`.)*

---

## 6. LZ4 compression

**Algorithm:** LZ4 **block** format via `K4os.Compression.LZ4` (upstream LZ4 semantics, managed
implementation). Not the reference product's code — the parity target names LZ4, the library is the
mainstream one.

**Negotiated, never assumed.** Enabled only when the session kind is `TlsLz4`, the local policy enables
it, *and* the peer's hello requested it; `TransportFactory.ApplyCompression` layers `Lz4Transport` only
over a `TlsTransport` where `Lz4Negotiated` is true. `Lz4Transport`'s constructor refuses to wrap a
plaintext transport, or a TLS transport that did not negotiate LZ4. That is the per-peer off switch: a
peer that does not ask for compression simply gets a plain TLS session. *(`CompressedFrameCannotBeLayeredOnPlaintext`.)*

**Applied only to declared payload frames.** `WriteAsync` treats its argument as `PayloadKind.Control`
and never compresses it; only `WritePayloadAsync(PayloadKind.Bulk|Interrupt, …)` can opt a frame in, and
only at or above `Lz4Options.ThresholdBytes` (default **64 KiB**). USB/IP headers therefore cannot be
compressed by accident. *(`Lz4LeavesControlTrafficAndSmallPayloadsRaw`,
`DefaultThresholdLeavesOneByteUnderItRawAndCompressesAtIt`,
`LoweringTheThresholdCompressesPayloadsTheDefaultWouldLeaveRaw`.)*

**The transport's opt-in now reaches the session.** Until card **t_825b4f08** the classification stopped
at the `Vht.Transport` boundary: `IPayloadCompressionTransport.WritePayloadAsync` had no caller outside
that assembly, the frozen `Vht.Core.Transport.ITransport` the USB/IP session writes through had no
payload-kind parameter, and so **every** reply frame — data stage included — was written as
`PayloadKind.Control` and never compressed. That is what §9.5 measured: 0 compressed frames at every URB
size, compressible and incompressible alike, and a constant 6–32% loss for `TlsLz4` that no
`ThresholdBytes` value could change.

The kind is now carried through the session:

- `Vht.Core.Transport` gains a narrow **optional** interface, `IPayloadKindTransport`, in the same
  additive style as `IPeerInfo` — the frozen `ITransport` member set is untouched, so nothing that
  already implements it breaks, and `PayloadKind`'s members are identical to `Vht.Transport.PayloadKind`
  so an adapter between the two is a cast. It publishes `CompressionActive` and
  `CompressionThresholdBytes` as well as the classified write.
- `UsbIpSession.SendAsync` writes a `USBIP_RET_SUBMIT` frame as **two classified writes under one hold of
  its write lock**: the 48-byte URB header as `Control`, the data stage as the endpoint's own transfer
  class, which is the same `ResolveTransferType` resolution the read side already performs (bulk,
  interrupt, isochronous, or control). One lock hold means no other reply can interleave, and the peer
  reassembles by byte count exactly as it did with one write.
- **Headers stay uncompressed by construction.** A split only ever puts the data stage in its own frame;
  the header is written whole, as `Control`, and `Lz4Transport.WriteFrameAsync` refuses to compress that
  class. A transport that does not implement the optional interface, or reports `CompressionActive`
  false, gets one plain write of the whole frame — byte-for-byte the pre-change behaviour.
- **A refused payload pays nothing.** The split is skipped when the data stage could not be compressed
  anyway: control and isochronous are never compressed, and a stage below `CompressionThresholdBytes` is
  written as part of the whole frame. Hoisting that test out of the decorator is why the threshold is
  published on the interface: without it, every sub-threshold reply would have paid a second frame header
  and a second CRC-32C pass to have the compressor refuse it. The consequence is visible in §9.5 — the
  `TlsLz4` arm at 4 KiB (frame counts and `wire/payload` ratio unchanged from the pre-change run) is
  unaffected by this work, on compressible and incompressible payload alike.

*(`ABulkInDataStageAtOrAboveTheThresholdIsDeclaredBulkWhileTheUrbHeaderStaysControl` in
`tests/Vht.Core.Tests/SessionTests.cs`: at the threshold a bulk-IN reply is two writes — `Control`/48
bytes then `Bulk`/4096 — one byte under it is a single `Control` write of the whole frame, and a control
transfer's 8 KiB data stage is likewise one `Control` write.)*

### 6.1 The threshold is 64 KiB, and this is why

The old default was **1024**, inherited from the reference product's `CompressionLimit` vocabulary. That
value was never measured here, and it was wrong: it offered every 1–32 KiB payload to LZ4, which is
exactly the region where LZ4 loses to plain TLS. The sweep in §9.4 (median of 7 runs per case, range
recorded, three consecutive sweeps preserved at `artifacts/transport/sweep-run{1,2,3}.txt`) puts the
crossover **between 32 KiB and 64 KiB** on compressible payload:

| Payload | LZ4 vs TLS, compressible (three runs) |
|---|---|
| 4 KiB | −8.4%, −12.7%, −10.6% |
| 8 KiB | −6.5%, −25.9%, −18.0% |
| 16 KiB | +7.3% (outlier run), −26.3%, −33.1% |
| 32 KiB | −1.9%, −4.2%, −0.2% — break-even |
| **64 KiB** | **+24.9%, +21.2%, +23.3%** — non-overlapping ranges in every run |
| 256 KiB | +44.2%, +48.6%, +44.0% |
| 1 MiB | +50.8%, +43.1%, +47.3% |

**Decision: `Lz4Options.ThresholdBytes` = 65536** (`transport.tls.lz4_threshold_bytes`, config default
raised alongside it). The rule applied: *the smallest swept payload size at which LZ4 beat plain TLS on
compressible data with ranges that did not overlap the TLS arm, in every run.* That is 64 KiB. The
16 KiB row in the first run is quoted rather than dropped because it is a counterexample to a naive
reading: its TLS median sat on a range of 442–1389 MB/s, so a single-sample sign there would have been
noise. The 32–64 KiB span was not swept; it is treated as a loss, not assumed a win, which is why the
threshold is not set lower.

**What the threshold does not buy.** Only the compression *attempt* is skipped. A frame that falls back
to raw still computes CRC-32C over the uncompressed bytes, so incompressible payload loses at every
size regardless of the threshold (−21% to −60% measured, §9.4). That overhead is the CRC-32C hardware
item, filed separately; a threshold cannot address it. Choosing `Tls` over `TlsLz4` is what handles a
peer whose traffic is genuinely random.

**Measured, but not acted on: the production path never compresses.** §9.5 sweeps the URB size at the
layer finding F3 was measured at, through the shipping `VhtServerState` / `UsbIpSession` / USB/IP codec
with the server session's own LZ4 counters read out. Every `TlsLz4` case emitted **0 compressed
frames** — at 4 KiB, 64 KiB, 256 KiB and 1 MiB URB, compressible and incompressible alike — so
`TlsLz4` cost 6–32% over `Tls` with no compression ever attempted and no payload-size crossover at all.
The cause is visible in the code: `IPayloadCompressionTransport.WritePayloadAsync` is implemented and
tested in `Vht.Transport`, but nothing outside that assembly calls it. The frozen `Vht.Core.Transport`
interface the USB/IP session writes through (`UsbIpSession.SendAsync` → `ITransport.WriteAsync`) has no
payload-kind parameter, so a reply frame is always classified `Control` and the compression opt-in is
unreachable from production. Raising a threshold cannot fix that; it needs the payload classification
carried through the session. Filed as follow-up card **t_825b4f08** rather than done here, because it
changes a shared interface rather than a policy constant.

**Framing** — `[u8 type][u32 compressed_len][u32 uncompressed_len][u32 crc32c][body]`, 13-byte header,
big-endian integers:

| Field | Meaning |
|---|---|
| `type` | `0` = raw (payload verbatim), `1` = LZ4 block |
| `compressed_len` | bytes of body on the wire; must equal `uncompressed_len` when `type` = raw |
| `uncompressed_len` | bytes the body decodes to; bounded 1..`maxPayloadFrameBytes` |
| `crc32c` | CRC-32C (Castagnoli) over the **uncompressed** payload bytes |

**Why it cannot corrupt a stream:**

- The CRC is over the *uncompressed* bytes, so tampering with the compressed body, truncating a frame,
  or a corrupt compression stream all fail the check and raise `IntegrityCheckFailed` — the reader never
  yields a short or scrambled read. The session dies.
- The declared uncompressed length is validated against a hard maximum **before** any buffer is sized, and
  the decompressed length must match the declared one exactly — so a hostile frame cannot act as a
  memory bomb.
- A writer that cannot beat raw emits a raw frame, so enabling compression never makes incompressible
  data larger beyond the fixed 13-byte header (or slower in wire terms).
- Frames are never larger than `min(local max, server-advertised max)`; `WritePayloadAsync` splits larger
  writes across frames.

*(`CorruptedFrameIsDetectedByTheReader`, `TruncatedFrameIsDetected`,
`CorruptedFrameFromAHostilePeerKillsTheLiveSession`, `ImpossibleDeclaredLengthIsRefusedAsAMemoryBomb`,
`RawFrameWithMismatchedLengthsIsRefused`, `UnknownFrameTypeIsRejected`, `FrameHeaderIsThirteenBytesBigEndian`,
`Lz4CompressesBulkPayloadAndRoundTrips`, `IncompressiblePayloadFallsBackToRaw`.)*

**CRC-32C implementation.** Hand-rolled (the `System.IO.Hashing.Crc32` package implements the IEEE
polynomial, not Castagnoli) as slice-by-8: eight 256-entry tables, eight bytes per iteration, with a
byte-at-a-time tail, and a published test vector (`CRC("123456789") = 0xE3069283`). The vectorised
SSE4.2 / ARM-CRC instruction would be faster and is deliberately not used — it is architecture-specific
and needs a dispatch path that cannot be exercised everywhere. It is cross-checked against an
independent bit-at-a-time reference over every residue length 0..16, randomised split points for the
two-segment overload, and a 1 MiB buffer. *(`MatchesThePublishedTestVector`,
`SliceByEightMatchesAByteAtATimeReference`.)*

> The slice-by-8 form is not cosmetic: the byte-at-a-time version measured as a **2.7× regression**
> against plain TLS on incompressible data (see §9). Fixing it raised `TlsLz4` compressible throughput
> from ~550 MB/s to ~2.0 GB/s and incompressible from ~350 MB/s to ~820 MB/s on this machine.

---

## 7. Discovery handshake (cross-reference)

The SPKI pin computed here (`ServerCertificateManager.ComputeSpkiPin`) is the value published in the
`_vht._tcp` DNS-SD TXT record, so a client can confirm a pin before connecting. Discovery itself lives in
`Vht.Config` (ARCHITECTURE.md §7) and is out of this document's scope; the pin function is the contract
between the two.

---

## 8. Configuration surface

`TransportOptions` is one validated record (`Validate()` is called by every factory entry point):

| Field | Default | Meaning |
|---|---|---|
| `Kind` | `Tls` | `PlainTcp` / `Tls` / `TlsLz4` |
| `ServerName` | `VirtualThere` | Log identity, certificate CN/SAN, client pin key |
| `ClientName` | machine name | Sent in the client hello |
| `HandshakeTimeout` | 20 s | Whole TLS + hello + auth exchange |
| `Lz4.Enabled` | `true` | Per-peer switch *within* a `TlsLz4` session |
| `Lz4.ThresholdBytes` | 65536 (64 KiB) | `transport.tls.lz4_threshold_bytes`; measured crossover, §6.1 |
| `Lz4.MaxPayloadFrameBytes` | 4 MiB | Bounds both directions; 1 KiB..64 MiB |
| `Authentication.SharedSecret` | null | Server-wide secret |
| `Authentication.DevicePasswords` | empty | busid → password (≤ 4096 entries) |
| `Authentication.Pbkdf2Iterations` | 600 000 | Work factor |
| `Authentication.RateLimit` | see §5.4 | Token buckets + lockout |
| `Tls.Protocols` | TLS 1.3 \| 1.2 | Enabled protocols |
| `Tls.RequireTls13` | false | Refuse below 1.3 |
| `Tls.CertificateDirectory` | `%ProgramData%\VirtualThere\tls` | Auto-generated certificate |
| `Tls.AllowInsecureKeyStorage` | false | Accept a key that could not be ACL-hardened |
| `Tls.PinMode` | `TrustOnFirstUse` | Client trust policy |
| `Tls.PinStore` | null | Client pin store (required for TOFU/Strict) |
| `RequestIsochronous` | true | Best-effort, §10 risk 6 |

`WithSharedAuthState()` returns a copy with one `VerifierSet` and one `AuthThrottle`, and
`TransportListener.Start` applies it, so PBKDF2 is paid once and limits span connections. A server that
builds its own transports must call it once per listener. *(`SharedAuthStateIsCreatedOnce`.)*

---

## 9. Benchmarks (this machine)

Reproduce with:

```
dotnet run -c Release --project tests/Vht.Transport.Bench/Vht.Transport.Bench.csproj -- artifacts/transport
```

Method: loopback only (127.0.0.1), 256 MiB per throughput case in 1 MiB chunks, fresh connection per
run, 5 runs per case with the **median** reported alongside the observed range. Handshake: 30 samples
after 3 warmups, over one listener (so the client's pin key is stable and TOFU genuinely records on the
first connection and verifies on later ones). Host: ZUAN, Windows 11 build 10.0.26300, 16 cores, .NET
10.0.13, 256 MiB transfer per run.

Raw output is preserved at `artifacts/transport/bench.txt` and `artifacts/transport/run.log`.

### 9.1 Handshake latency (transport only; PBKDF2 work factor lowered to 2 000)

| Kind | mean | p50 | p95 |
|---|---|---|---|
| `PlainTcp` (TCP connect) | 0.25 ms | 0.24 ms | 0.34 ms |
| `Tls` (TLS + `_vht` hello) | 2.64 ms | 2.61 ms | 2.85 ms |
| `TlsLz4` (TLS + `_vht` hello) | 2.60 ms | 2.58 ms | 2.81 ms |

So TLS costs ~2.4 ms over a raw TCP connect on loopback, and LZ4 negotiation adds nothing measurable
(it is eight bytes in a frame that was already being exchanged).

### 9.2 Full authenticated handshake (real work factor)

TLS + `_vht` hello + HMAC challenge-response with a shared secret at the production 600 000 PBKDF2
iterations, 5 samples: **mean 73.1 ms, p50 59.3 ms, p95 124.2 ms**. The dominant term is the client's
PBKDF2 derivation, which is the point of the work factor; the TLS and handshake machinery is the ~2.6 ms
from §9.1.

### 9.3 Throughput (MB/s, median of 5; range in parentheses)

| Kind | compressible payload | incompressible payload |
|---|---|---|
| `PlainTcp` | **2347** (815–2900) | **1689** (1427–5864) |
| `Tls` | **1417** (1384–1441) | **1470** (1396–1501) |
| `TlsLz4` | **2029** (1854–2097) | **817** (761–855) |

Compressible payload for the compressed case: 256 frames, 256 compressed, **1 122 560 wire bytes for
268 435 456 payload bytes — a 239:1 ratio**, i.e. ~239× less on the wire than the raw payload.

Reading these honestly:

- **The numbers are noisy.** The plaintext case in particular ranged 1427–5864 MB/s across repetitions
  while TLS varied by only ~7%. Writer and reader share cores with the record layer and the collector on
  one loopback interface, so a single run is not reproducible. Medians are quoted for that reason;
  treat ±30% as the resolution of these figures rather than the three significant digits shown.
- **TLS costs throughput** versus plaintext at these sizes (1417 vs 2347 MB/s compressible): the record
  layer copies and encrypts every byte. On a real LAN or WAN the wire is the bottleneck and this
  disappears.
- **Compression pays exactly when the payload is compressible**, and costs when it is not: `TlsLz4`
  beats `Tls` on compressible data (2029 vs 1417 MB/s, because 239× fewer bytes hit the socket) and is
  ~1.8× slower on incompressible data (817 vs 1470 MB/s) because every byte pays CRC-32C plus a copy for
  nothing. This is precisely why compression is negotiated per peer and can be switched off, and why the
  threshold exists: a peer streaming camera video or random keys should run `Tls`, not `TlsLz4`.
- These are loopback upper bounds. USB device throughput (bulk transfers on real hardware) is an order
  of magnitude below any of these numbers, so the compression decision on real devices will be about CPU
  cost and wire cost on the actual link, not about these figures.

### 9.4 Payload-size sweep — the crossover behind the 64 KiB threshold

Reproduce with:

```
dotnet run -c Release --project tests/Vht.Transport.Bench/Vht.Transport.Bench.csproj -- sweep artifacts/transport
```

Method: the same real sockets, Schannel handshake and framing as §9.3, but the payload size is swept
instead of fixed at 1 MiB, and each case is repeated 7 times with the median and the observed range
recorded. The LZ4 policy is held fixed at `ThresholdBytes = 0` for the sweep — otherwise the sweep would
be measuring the threshold's own effect and the crossover it exists to find would be invisible by
construction. Three consecutive sweeps are preserved verbatim as
`artifacts/transport/sweep-run{1,2,3}.txt`; `artifacts/transport/sweep.txt` is the last of them.
64 MiB of payload per run, 1 device, loopback.

`TlsLz4` vs `Tls`, MB/s medians (range in brackets), run 3:

| Payload | `Tls` compressible | `TlsLz4` compressible | Δ | `Tls` incompressible | `TlsLz4` incompressible | Δ |
|---|---|---|---|---|---|---|
| 4 KiB | 429.6 (363.8–434.7) | 384.0 (191.5–395.3) | −10.6% | 451.6 (428.8–461.0) | 346.8 (337.3–351.3) | −23.2% |
| 8 KiB | 841.7 (795.1–863.3) | 689.9 (643.7–700.2) | −18.0% | 826.8 (801.8–839.8) | 558.5 (522.9–566.2) | −32.4% |
| 16 KiB | 1475.8 (1435.0–1526.2) | 987.3 (905.3–1069.4) | −33.1% | 1372.3 (749.7–1514.0) | 581.7 (525.8–611.0) | −57.6% |
| 32 KiB | 1462.5 (735.1–1535.3) | 1459.9 (1352.0–1523.4) | −0.2% | 1436.3 (1379.3–1546.2) | 698.8 (556.3–746.1) | −51.3% |
| **64 KiB** | 1445.4 (1158.2–1553.7) | **1782.5 (1708.8–1859.7)** | **+23.3%** | 1540.7 (1480.7–1556.4) | 823.6 (803.5–852.2) | −46.5% |
| 256 KiB | 1533.8 (1498.4–1571.5) | **2209.4 (2071.1–2259.5)** | **+44.0%** | 1481.5 (1444.4–1552.0) | 891.6 (883.1–900.0) | −39.8% |
| 1 MiB | 1533.2 (1469.0–1568.7) | **2258.8 (2171.4–2300.3)** | **+47.3%** | 1509.5 (1445.3–1550.9) | 862.5 (739.7–891.8) | −42.9% |

The compression ratio is real and large at every size (`wire/payload` was 0.071 at 4 KiB and 0.004 at
1 MiB — the swept compressible pattern is highly redundant), so the sub-64 KiB loss is **not** a
compression failure: it is the fixed per-frame cost (LZ4 encode, CRC-32C over the uncompressed bytes,
two copies) exceeding a socket saving that is small in absolute terms. That is exactly why the crossover
is a payload-size function and not a property of the data.

Cross-run stability (compressible Δ in the three preserved runs): 4 KiB −8.4/−12.7/−10.6, 8 KiB
−6.5/−25.9/−18.0, 16 KiB +7.3/−26.3/−33.1, 32 KiB −1.9/−4.2/−0.2, **64 KiB +24.9/+21.2/+23.3**, 256 KiB
+44.2/+48.6/+44.0, 1 MiB +50.8/+43.1/+47.3. The 16 KiB `+7.3%` is the one outlier and it is reported
rather than discarded: its TLS median was drawn from a 442–1389 MB/s range, so the sign there is noise.
At 64 KiB and above the ranges do not overlap between the arms in any run. **The crossover is between
32 KiB and 64 KiB.**

**NOT RUN in this sweep:** payload sizes between 32 KiB and 64 KiB. The threshold is set at 64 KiB
precisely so the unswept region is excluded rather than guessed at.

Timing caveat, recorded because it is material: this host was running several unrelated .NET worker
processes throughout (20 `dotnet`/`vht-*` processes at the time of the recorded runs), which is why
individual ranges are as wide as they are. The two arms of each comparison are interleaved
(same size, back to back) so load affects both, and the conclusion rests on the *ranges* rather than on
the medians alone.

### 9.5 URB-size sweep through the production path — compression now engages

Reproduce with:

```
dotnet run -c Release --project tests/Vht.Acceptance/Vht.Acceptance.csproj -- perf-sweep
```

Method: the shipping server stack (`VhtServerState`, `UsbIpSession`, the USB/IP codec, the production
`Vht.Transport` code) over real loopback TCP, one synthetic device, bulk-IN reads at the swept URB size
with 8 in flight, 4 s per case, 5 repeats, median and range recorded. The `compressed` column is the
**server session's own** `Lz4Transport.Statistics.CompressedFrames` for that case. Transcripts: three
consecutive runs, `artifacts/acceptance/perf-sweep.txt` (the table below) plus
`artifacts/acceptance/perf-sweep-run1.txt` and `perf-sweep-run2.txt`.

| URB | `Tls` comp. | `TlsLz4` comp. | Δ | `Tls` inc. | `TlsLz4` inc. | Δ | compressed frames (comp. / inc.) |
|---|---|---|---|---|---|---|---|
| 4 KiB | 238.1 (227.3–248.2) | 206.3 (199.0–207.9) | −13.3% | 239.4 (209.5–243.6) | 215.7 (205.3–219.0) | −9.9% | **0 / 0** |
| 64 KiB | 869.5 (852.6–909.8) | 1096.4 (1026.8–1112.5) | **+26.1%** | 849.6 (803.1–866.7) | 541.8 (532.8–556.1) | −36.2% | **70 371 / 0** |
| 256 KiB | 908.5 (850.1–931.8) | 1117.4 (1051.7–1152.3) | **+23.0%** | 903.7 (858.2–918.9) | 616.3 (599.3–617.4) | −31.8% | **17 939 / 0** |
| 1 MiB | 1045.7 (1023.0–1096.6) | 1637.1 (1623.1–1655.5) | **+56.5%** | 1075.9 (1064.0–1103.1) | 765.3 (751.5–795.6) | −28.9% | **6 576 / 0** |

The 64 KiB row is the acceptance criterion this card was filed against, and it reproduces run to run
with ranges that never overlap:

| Run | `Tls` comp. 64 KiB | `TlsLz4` comp. 64 KiB | Δ | compressed frames |
|---|---|---|---|---|
| 1 (`perf-sweep-run1.txt`) | 785.5 (753.0–798.2) | 938.0 (865.5–945.0) | +19.4% | 60 215 |
| 2 (`perf-sweep-run2.txt`) | 801.5 (760.5–818.4) | 981.0 (939.0–988.8) | +22.4% | 62 917 |
| 3 (`perf-sweep.txt`) | 869.5 (852.6–909.8) | 1096.4 (1026.8–1112.5) | +26.1% | 70 371 |

Three things are worth reading off this table, because each is a specific claim:

1. **Compression engages at and above the 64 KiB threshold, and nowhere below it.** The 4 KiB row emits
   **0** compressed frames on compressible payload — the data stage is below `Lz4Options.ThresholdBytes`,
   so `UsbIpSession` writes the reply as one whole frame, exactly as it did before this card, and the
   `TlsLz4` column there is pure framing (13 bytes per frame, `wire/payload` 1.003) and costs the same
   ~10–13% it always did. Nothing about the sub-threshold path was changed to make the large sizes win.
2. **On compressible payload LZ4 now beats plain TLS end to end** — +19.4%/+22.4%/+26.1% at 64 KiB,
   +18.8%/+19.6%/+23.0% at 256 KiB and +35.5%/+34.7%/+56.5% at 1 MiB across the three runs — and the
   wire/payload ratio collapses from 1.000 to 0.004–0.009, which is the compression itself, visible from
   the server's own counters rather than inferred from throughput alone. This is the §9.4 result (LZ4
   beats TLS by +21..25% on compressible payload at 64 KiB at the transport layer) reproduced through
   the production stack, which is what §9.5 previously could not do.
3. **On incompressible payload `TlsLz4` still loses**, and more than it did when it never compressed:
   −9.9%/−11.1%/−9.7% at 4 KiB (the unchanged path) against −36.2%/−44.2%/−35.6% at 64 KiB and
   −31.8%/−32.3%/−31.9% at 256 KiB. Offering a payload to LZ4 is what costs this: the data stage is now
   its own frame, so an incompressible URB pays a second 13-byte header and a second CRC-32C pass for a
   compression attempt that loses and falls back to raw. That penalty is the CRC-32C hardware item in
   §12 item 7 and is unaffected by any threshold; a peer whose traffic is genuinely random should be on
   `Tls`, which is the per-peer off switch, not a threshold value.

**What this section said before this card.** It reported a reproduction of finding F3 in
`docs/ACCEPTANCE.md` §8 that went further than F3 did: `TlsLz4` was 6–32% slower than `Tls` at *every*
URB size, compressible and incompressible alike, because **not one frame was ever compressed** —
`IPayloadCompressionTransport.WritePayloadAsync` had no caller outside `Vht.Transport` and the frozen
`Vht.Core.Transport.ITransport` the session wrote through carried no payload kind, so every reply byte
was classified `Control`. That measurement was correct and is preserved in git; the rows above are what
the same harness reports now that the kind is carried through the session (§6.1). The old conclusion —
"there is no payload-size crossover in the production path, and no threshold value can change it" — was
a statement about a missing interface, not about the threshold, and is now false.

---

## 10. Dependencies and licences

Every dependency of `Vht.Transport`, with the licence its package metadata declares (verified from the
`.nuspec` in the local NuGet cache). VirtualThere is Apache-2.0; nothing here is copyleft, and nothing
here is a licence we cannot ship in an Apache-2.0 product.

| Dependency | Version | Licence | Used by | Notes |
|---|---|---|---|---|
| `.NET` / `System.*` runtime (incl. `SslStream`, `System.Security.Cryptography`) | 10.0 | MIT | `Vht.Transport` | The TLS stack is Schannel via the OS; no crypto library is redistributed |
| `K4os.Compression.LZ4` | 1.3.8 | MIT | `Vht.Transport` (runtime) | The only third-party runtime dependency. Upstream LZ4 block format |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | tests only | Not shipped |
| `xunit`, `xunit.core`, `xunit.assert`, `xunit.extensibility.*`, `xunit.abstractions` | 2.9.3 / 2.0.3 | Apache-2.0 | tests only | Not shipped |
| `xunit.runner.visualstudio` | 3.1.4 (3.1.5 restored) | Apache-2.0 | tests only | Not shipped |
| `Newtonsoft.Json` | 9.0.1 | MIT | tests only (transitive, test host) | Not shipped |
| `icacls.exe` (Windows in-box) | Windows 11 | OS component | `SecureFileStore` | Not redistributed; invoked from the OS |
| Schannel / TLS 1.3 (Windows in-box) | Windows 11 | OS component | `SslStream` | Not redistributed |

Deliberately **rejected**, to avoid a licence problem or a fork we would have to maintain:

- **mbedTLS** (used by the parity target) — Apache-2.0 and therefore *compatible*, but a vendored C
  crypto stack, and unnecessary when the platform already provides a maintained TLS 1.3 implementation.
- **`System.IO.Hashing.Crc32`** — MIT, but implements the IEEE polynomial, not Castagnoli; a different
  checksum is worse than 60 lines of our own code.
- **`usbipd-win`** — GPL-3.0, and its VirtualBox-derived driver binaries are not redistributable.
  Recorded in ARCHITECTURE.md Appendix D; the client reuses usbip-win2's BSD-2-Clause UDE driver
  instead (`third_party/usbip-win2-driver`), which is a transport consumer, not a transport dependency.
- **`System.IO.FileSystem.AccessControl`** — MIT, but Windows-only, and `Vht.Transport` is deliberately
  a portable `net10.0` library; in-box `icacls.exe` avoids the dependency.
- **Bonjour / mDNS NuGet packages** — not needed; in-box Windows DNSAPI is used for `_vht._tcp`.

---

## 11. Verification summary

`dotnet test tests/Vht.Transport.Tests/Vht.Transport.Tests.csproj` — **69 tests, 0 failures** (covered:
handshake framing and hostile framing, capability/downgrade refusals, TLS sessions with Schannel,
certificate generation and permissions, SPKI pinning in all three modes, corrupted/truncated pin store,
shared-secret and per-device auth success and failure, constant-time proof verification, token-bucket and
lockout behaviour across connections, plaintext fail-closed, LZ4 round-trip, raw fallback, frame
tampering, memory-bomb bounds, the threshold boundary, and CRC-32C against an independent reference).
`dotnet test tests/Vht.Acceptance.Tests/Vht.Acceptance.Tests.csproj` — **18 tests, 0 failures**.
`dotnet test tests/Vht.Core.Tests/Vht.Core.Tests.csproj` — **160 tests, 0 failures**, including the
session-level classification test added by card **t_825b4f08**.

Mapping to this card's acceptance criteria:

| Criterion | Evidence |
|---|---|
| A session cannot be downgraded; a peer that skips the handshake is rejected | §3.4 mechanisms 1–5; `PeerThatSkipsTheHandshakeIsRejected`, `StockUsbipBytesAreRejectedAsAHandshakeSkip`, `RawTcpPeerIsRejectedByTheTlsListener`, `BadMagicIsRejected`, `UnsupportedVersionIsRejected` |
| Tampering with compressed frames is detected | 13-byte header + CRC over uncompressed bytes; `CorruptedFrameIsDetectedByTheReader`, `TruncatedFrameIsDetected`, `CorruptedFrameFromAHostilePeerKillsTheLiveSession` |
| Wrong password / wrong pinned fingerprint fails closed, repeats are throttled | `WrongSharedSecretFailsClosed`, `WrongDevicePasswordAndUnknownDeviceBothFailClosed`, `WrongPinnedFingerprintFailsClosed`, `RepeatedFailuresAreThrottledAcrossConnections`, `RepeatedFailuresLockThePeerOutExponentially` |
| MB/s plain vs TLS vs TLS+LZ4, and handshake ms | §9 |
| The LZ4 threshold is a measured value, not a copied default | §6.1 and §9.4; `DefaultThresholdAndFrameLimitsMatchTheArchitecture`, `DefaultThresholdLeavesOneByteUnderItRawAndCompressesAtIt`, `LoweringTheThresholdCompressesPayloadsTheDefaultWouldLeaveRaw` |
| Every dependency and its licence listed | §10 |
| **t_825b4f08**: a bulk-IN transfer through the production session shows non-zero `CompressedFrames` at or above the threshold URB size and zero below it, read out of the live session | §9.5 (0 compressed frames at 4 KiB, 70 371 of 211 118 frames at 64 KiB, counters read via `BlockingServerTransportFactory.Accepted`); `ABulkInDataStageAtOrAboveTheThresholdIsDeclaredBulkWhileTheUrbHeaderStaysControl` |
| **t_825b4f08**: end-to-end throughput at 64 KiB URBs on compressible payload beats the plain `Tls` arm with non-overlapping ranges over repeated runs | §9.5 three-run table: +19.4% (785.5 [753.0–798.2] → 938.0 [865.5–945.0]), +22.4% (801.5 [760.5–818.4] → 981.0 [939.0–988.8]) and +26.1% (869.5 [852.6–909.8] → 1096.4 [1026.8–1112.5]) |

---

## 12. Limitations and follow-ups

Honest gaps in v1, in priority order:

1. **The client half of the compression opt-in is still unclassified** (§6.1, §9.5). The *server* session
   now declares its reply data stages (`UsbIpSession` → `IPayloadKindTransport`), which is what makes
   `TlsLz4` a win on compressible bulk-IN traffic. The client's own OUT payloads are written whole
   through the client-side interface, which mirrors the frozen one and has the same gap, so an
   OUT-heavy session still compresses only one direction. The server-side change is the one the
   acceptance criterion covered; the client side is the natural next card, not something this one
   silently half-did. (Wire compatibility is unaffected either way: the classification changes nothing
   the peer parses.)
2. **The verifier is password-equivalent** (§5.1). Accepted for v1; SRP or a PAKE would remove it.
3. **No client certificates.** Client identity is the pinned server key plus a credential; the server
   does not identify the *client* cryptographically beyond the shared secret.
4. **TLS 1.2 is still accepted by default** because Schannel's TLS 1.3 floor is Windows 11 / Server 2022.
   Set `Tls.RequireTls13` where the fleet allows it.
5. **Insecure pin mode is reachable in configuration.** It is intended for the loopback bridge hop only;
   the config layer should gate it behind an explicit acknowledgement key (ARCHITECTURE.md §6.5).
6. **The rate limiter keys on IP**, so peers behind one NAT share a bucket, and an attacker with many
   addresses still relies on the global bucket.
7. **CRC-32C is software-only** (§6). Hardware CRC would lift the ~1.8× incompressible penalty — the
   penalty a threshold cannot touch, because a raw frame pays the CRC too.
8. **Benchmark variance is high** (§9.3, §9.4). A pinned-core, single-run-per-process harness would give
   tighter numbers; the current figures are medians with the range shown rather than a clean curve, and
   the whole sweep was recorded on a host carrying unrelated worker processes.
9. **The 32–64 KiB span is unswept** (§9.4). The threshold sits at the lower edge of the measured win
   rather than in the middle of the unknown region.
10. **No isochronous-specific testing.** The feature is negotiated but best-effort only (§10 risk 6 of
   ARCHITECTURE.md) and unproven against real isochronous devices.
