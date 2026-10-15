# VirtualThere — acceptance and hardware-validation record

Owner card: `hardware-validation-workstream`. Parity target: `docs/RECON_AND_SPEC.md` §1 (the reference
product's free tier caps at **1 device per server**), `docs/FEATURE_MATRIX.md`, `docs/ARCHITECTURE.md`
§5 (no device-count concept).

This document is the honest account of what was **measured on real hardware**, what was measured on the
**software path** because the hardware path could not be reached in this environment, and what could
**not be run at all**. Every number below is reproduced by one command and is attached verbatim under
`artifacts/acceptance/`. Nothing here is simulated, estimated, or transcribed by hand.

## Reproduce

```
dotnet run --project tests/Vht.Acceptance -c Release -- all
```

Writes `artifacts/acceptance/{hardware,multi,soak,perf,classes,licence,SUMMARY}.txt` and echoes the same
lines to stdout. Exit codes: `0` claim held, `2` claim did not hold, `64` bad usage, `70` harness failed,
`78` **NOT RUN** in this environment (reason printed). A verb that cannot run exits 78 — it never
degrades into a pass.

The parity suite (integration tests over the same rig) runs with:

```
dotnet test tests/Vht.Acceptance.Tests/Vht.Acceptance.Tests.csproj -c Release
```

## 0. Headline

> **VirtualThere attached and used 12 devices simultaneously at $0; the reference product's free tier allows 1.**
>
> The reference product is named in [`docs/RECON_AND_SPEC.md`](RECON_AND_SPEC.md) §1 and in
> [`LEGAL.md`](../LEGAL.md); this record keeps to that name-once convention so the branding guard
> (`scripts/check-branding.sh`) stays meaningful.

The same run proved **17/17 parity behaviours** (enumeration, export, attach, detach, hotplug, reconnect,
per-device passwords, allow/ignore lists, nicknames, kick, auto-attach grant path, and the TLS/TLS+LZ4
listeners), found **0** enforcement-shaped licence or device-count strings in every shipped binary, and
held **8 devices for 120 s** with 0 failed URBs. *The suite has since grown to 18/18 - the nickname gap
in §9 F1 was closed after that run and its characterisation test was replaced by two tests that pin the
new behaviour. See §4.*

Stated precisely, because the distinction matters:

| Claim | Scope | Evidence |
|---|---|---|
| 12 concurrent device sessions, none refused, on real TCP against the production server | **software path** | `artifacts/acceptance/multi.txt` |
| 12 devices imported on a single connection and all answering a control transfer | **software path** | `artifacts/acceptance/multi.txt` phase 4 |
| Real USB devices exported by this machine | **0 — NOT RUN** | `artifacts/acceptance/hardware.txt` (see §2) |
| A real device usable end to end through the UDE virtual controller | **NOT RUN** — driver not installed | §2, and `docs/CLIENT.md` |

## 1. Environment

| | |
|---|---|
| Host | ZUAN / Microsoft Windows NT 10.0.26300.0 |
| Runtime | .NET 10.0.13 |
| Elevated | **False** (UAC enabled: `EnableLUA=1`, `ConsentPromptBehaviorAdmin=5`) |
| Real USB devices present | 13 |
| Real USB devices claimable by `vht-server` | **0** |
| Rig | synthetic `ICaptureProvider` over the **production** `VhtServerState`, `UsbIpSession`, USB/IP wire codec and `UsbIpTcpServer` accept loop, on a real loopback TCP socket |

Everything below the capture provider is shipping code. The single substitution is the device source,
and it is documented at every place it is used.

## 2. Hardware: **NOT RUN** — and why

`artifacts/acceptance/hardware.txt` enumerates all 13 USB devices on this host. Every one reports
`state=CaptureUnsupported reason=driver_holds_device`: each is held by a function driver (HID, mass
storage, Bluetooth, display, XHCI, …), so nothing can be claimed in user mode.

The only other capture path is the opt-in rebind (`capture.rebind.enabled`), which installs a
device-specific INF and requires an **elevated token**. Elevation on this host is an interactive UAC
desktop prompt and cannot be answered from an unattended run.

**Consequence, stated plainly:** the number of real USB devices VirtualThere can export on this machine
is **0**, and no number in this document may be read as a hardware result. The multi-device claim is a
claim about the protocol, transport and session-management software, which is a different and weaker
claim than "12 physical devices ran concurrently".

NOT RUN, each for the stated reason:

- WinUSB claim / rebind of a real device — needs an elevated shell or a device already bound to WinUSB.
- UDE virtual-controller attach (`usbip2_ude.sys`) — the driver is not installed on this host, and
  driver installation needs elevation.
- Stock Linux `usbip attach` cross-check — no `usbip`/`usbipd` in this environment (and installing one in
  WSL needs an interactive `sudo` password).
- CM hotplug notifications — both filter forms return CR 31 on this host; the live path is the ~3.07 s
  periodic rescan, which the hotplug test exercises directly (`RefreshExportsAsync`).
- Service install/start and reboot survival — needs elevation.

This card's acceptance criterion is met the honest way: the failure to run is reported as **NOT RUN with
its reason**, never as a pass.

## 3. Multi-device acceptance (`artifacts/acceptance/multi.txt`)

12 devices, 6 s of concurrent exercise, plain TCP over loopback, one device per connection:

```
phase 0 - enumeration : server advertised 12 device(s) over OP_REP_DEVLIST
phase 1 - 12 concurrent connections, one device imported on each
  attached simultaneously : 12 of 12 (import refusals: 0)
  server-side attach table : 12
phase 2 - exercising all 12 devices concurrently for 6s
  TOTAL     12 devices         4408.3 MB/s   26510.8 MiB   0 failed URBs
  per-device mean      : 367.4 MB/s
phase 3 - one connection importing every device (no per-connection device cap)
  imported on a single connection : 0   (every device already held exclusively by phase 1)
  refusal status for a held device : device_busy (exclusivity)
phase 4 - fresh rig: attach every device on a single connection, then use them all
  devices imported on one connection : 12 of 12
  devices answering a control transfer : 12 of 12
```

Two things phase 3 and phase 4 establish together:

- a refusal is **exclusivity** (`device_busy` for a device already held), never a count — the same
  connection immediately imports a *different* device;
- there is no per-connection cap either: all 12 devices import on **one** connection and all 12 answer a
  control transfer.

The ceiling observed here is the rig's configured device count, not a limit in the code: the same test
passes with 32 devices in the parity suite (`Enumeration_Reports_No_Device_Limit`, asserting
`listed.Count == 32` and `Exports.Count == 32`), and the server card's own suite holds 32 sessions with a
device each at once. The honest limit of *this* machine is the physical one in §2, not a software one.

## 4. Parity test suite (`tests/Vht.Acceptance.Tests`, 18/18 pass)

```
Passed!  - Failed: 0, Passed: 18, Skipped: 0, Total: 18, Duration: 343 ms
```

(The acceptance run below recorded 17; the characterisation test for finding F1 was replaced by the two
tests that close it - see §9 F1 - so the suite carries one more test than that run measured.)

Each test boots the production server stack on a real loopback port and speaks USB/IP to it. Coverage:

| Behaviour | Test |
|---|---|
| Device enumeration with descriptors on the wire | `Enumeration_Advertises_Every_Capturable_Device_With_Its_Descriptors` |
| No device limit (32 exports) | `Enumeration_Reports_No_Device_Limit` |
| Export → attach → bulk OUT/IN round trip | `Attach_Then_Use_Round_Trips_A_Bulk_Transfer` |
| Detach on connection close frees the device | `Detach_On_Connection_Close_Frees_The_Device_For_Another_Client` |
| A second holder is refused by exclusivity, not a cap | `A_Second_Holder_Is_Refused_By_Exclusivity_Not_By_A_Device_Limit` |
| Many devices on one connection are all usable | `Many_Devices_On_One_Connection_Are_All_Usable` |
| Hotplug add/remove changes what clients see | `Hotplug_Added_And_Removed_Devices_Change_What_Clients_See` |
| Reconnect after transport death re-imports | `Reconnect_After_Transport_Death_Succeeds_And_Reimports` |
| Ignore beats allow; a narrow allow list hides the rest | `Ignore_List_Hides_A_Device_Even_When_It_Is_Also_Allowed` |
| Allow list exports exactly the allowed devices | `Allow_List_Exports_Exactly_The_Allowed_Devices` |
| Nickname retained by policy and shown in the operator view | `Nickname_Is_Retained_By_Policy_And_Reported_By_The_Operator_View` |
| **Nickname reaches a client** through VirtualThere's own op (`OP_REP_VHT_DEVINFO`) | `The_Nickname_Reaches_A_Client_Through_VirtualTheres_Own_Op` |
| Device name is asked for on its own connection, so a stock peer is unaffected | `TheNickname_IsAskedForOnAConnectionOfItsOwn` (client suite, `src/Vht.Client/Tests`) |
| Peer without the extension still lists, with no names and no failure | `APeerWithoutTheExtension_StillLists_AndSimplyHasNoNames` (client suite) |
| Stock `OP_REP_DEVLIST` frame, byte for byte, with no field repurposed | `The_Devlist_Record_Is_Byte_For_Byte_The_Stock_Layout` |
| Per-device password: not offered on a plaintext listener | `A_Password_Protected_Device_Is_Not_Offered_On_The_Plaintext_Listener` |
| …and an unprotected device next to it still is | `A_Device_With_No_Password_Is_Still_Offered_Next_To_A_Protected_One` |
| Kick force-detaches the holder and frees the device | `Kick_Force_Detaches_The_Holder_And_Frees_The_Device` |
| TLS listener carries the same parity surface | `The_Encrypted_Listeners_Carry_The_Same_Parity_Surface(Tls)` |
| TLS+LZ4 listener carries the same parity surface | `The_Encrypted_Listeners_Carry_The_Same_Parity_Surface(TlsLz4)` |

Raw frame bytes for the nickname decision are in `artifacts/acceptance/nickname-op.txt`: the live
`OP_REP_DEVLIST` frame with no field repurposed, and the `OP_REP_VHT_DEVINFO` request/reply that carries
the name.

### Harness defects fixed while getting this suite green

These were bugs in the acceptance harness, not in the product. They are recorded because the first run of
this suite was red for reasons that had nothing to do with VirtualThere:

- **TLS/LZ4 rigs could not complete a handshake at all.** `AcceptanceRig.CreateOptions` left the plaintext
  listener as the only bound listener for encrypted rigs, and the rig's certificate was minted by
  `CreateSelfSigned`, i.e. with an **ephemeral private key** — Windows Schannel refuses a TLS 1.3
  handshake with one (`"Authentication failed because the platform does not support ephemeral keys"`),
  so every encrypted connection died with `IOException: Received an unexpected EOF`. Fixed by binding the
  TLS listener for encrypted rigs and round-tripping the certificate through PKCS#12 with
  `PersistKeySet|UserKeySet` (a user-profile key container: no elevation, no machine store, nothing
  shared with the product's own certificate). Without this, deliverable 6 (TLS/LZ4 overhead) and two
  parity tests could not have been measured at all.
- **Soak throughput read 0 for 119 of 120 seconds.** The window's byte counter was only assigned when the
  measurement task completed, so every 5 s sample printed `0 0` and only the final row was real — which
  is not a sample. Fixed with a live `ThroughputCounter`; the transcript now shows a real curve.
- **Three parity assertions encoded the wrong expectations**: `Product` on a `UsbIpDevice` is always
  empty because the 312-byte USB/IP devlist record has no string field (stock protocol), `Exports
  .Snapshot().Single()` is wrong when more than one device is exported, and the ignore/allow test
  assumed an allow list is non-exclusive. All three were corrected to the documented semantics; the
  product was not touched.

## 5. Negative test — no licence or device-count gate (`artifacts/acceptance/licence.txt`)

Two independent scans:

- **A. source** — every `.cs/.csproj/.toml/.md/.slnx` in the repo, build output and `third_party/`
  excluded.
- **B. binaries** — every `.dll`/`.exe` under `src/*/bin/Release/`, scanned as raw bytes in **both**
  UTF-8 and UTF-16LE (because .NET string literals are UTF-16). Framework DLLs, test assemblies and
  `obj/` are excluded — including them would make the count a measure of the scanner.

Terms: `max_devices`, `MAX_DEVICES_EXCEEDED`, `unlicensed`, `licence`, `license`, `purchase`, `licensee`,
`trial_expired`, `activation_key`, `dongle`.

Result:

```
binary vocabulary occurrences : 6 (quoted above, all classified)
enforcement-shaped occurrences: 0
PASS
```

Every one of the 6 occurrences is quoted with its offset and its surrounding literal, and every one is the
product **saying it has no such gate**:

```
src/Vht.Server/bin/Release/net10.0-windows/vht-server.dll 'licence' utf16 offset 71013
  context:  .capture_mode.device limit:  .device_limit.null. (no cap).licence:  .licence.listeners:…
src/Vht.Cli/bin/Release/net10.0-windows/vht.dll 'license' utf16 offset 8069
  context:  | per-device policy (all unconditional - nothing here is licensed or capped) |   ig
```

The verdict separates *vocabulary* from *enforcement*: a hit counts as enforcement-shaped only if its
literal also matches a gate-shaped phrase (`max_devices`, `MAX_DEVICES_EXCEEDED`, `BIND_UNAUTHORIZED`,
`unlicensed`, `please purchase`, `activation`, …). Generic "the word appears" is not the claim; "no code
path can refuse an export or an attach because of a licence or a device count" is, and that claim is:

1. **positively** evidenced by `multi.txt` — 12 concurrent devices, `import refusals: 0`, and the
   `status` verb reporting `device limit: null (no cap)`; and
2. **negatively** evidenced by the classification above, plus
   `tests/Vht.Core.Tests/BindStatusInvariantTests.cs`, which reflects over `Vht.Core.BindStatus` and fails
   the build if a member matching `Max.*Devices` or `Device.*Limit` ever appears.

## 6. Soak — 8 devices, 120 s (`artifacts/acceptance/soak.txt`)

```
attached 8 devices; starting soak
  t(s)   per-device MB/s*  aggregate MB/s*   managed MiB+   working MiB   failed URBs
      5               760            6087           15.7         101.8            0
     30               686            5494           42.1         140.1            0
     60               688            5507           91.8         187.2            0
     95               714            5715          179.8         261.3            0
    120               696            5569          159.1         262.3            0

SOAK SUMMARY
  duration          : 120.5s over 8 devices
  per-device MB/s   : first sample 760, last sample 696
  managed delta     : +127.7 MiB
  working-set delta : +123.0 MiB
  thread delta      : 0
  failed URBs       : 0
  server attach tbl : 8
```

`*` cumulative average since the start, sampled live every 5 s. `+` `GC.GetTotalMemory(forceFullCollection:
false)`, so it includes uncollected garbage and sawtooths; the forced-collection delta in the summary is
the retained figure.

Reading:

- **Throughput is stable**: 760 → 696 MB/s per device (a 8.4 % drift over two minutes, on a busy desktop
  that was also running a build). No disconnect, no failed URB, no attach-table loss — all 8 devices were
  still attached at the end.
- **Memory was not flat in this run — that is finding F2, now root-caused and fixed (§9).** The growth
  was the server session's teardown-barrier list keeping one entry per URB ever served, whose backing
  array doubles into the large object heap. On the same command at 600 s it reached +541.2 MiB retained
  before the fix and 0 MiB after it, at the same ~15 GiB/s of allocation churn; both directions are
  recorded verbatim in `artifacts/acceptance/soak-mem-*.txt`.

## 7. Device-class matrix (`artifacts/acceptance/classes.txt`)

Each class is enumerated, imported and driven over real TCP against the production session. "Works" means
the URB completed with status 0 and the expected byte count.

| Class | Control | Bulk | Interrupt | Isochronous | Verdict |
|---|---|---|---|---|---|
| mass-storage (0x08) | ok | ok (4096 B out/in) | n/a | n/a | **SUPPORTED** (software path) |
| HID keyboard (0x03) | ok | n/a | ok (8 B report) | n/a | **SUPPORTED** (software path) |
| CDC serial (0x02) | ok | ok | ok | n/a | **SUPPORTED** (software path) |
| vendor-specific / WinUSB (0xff) | ok | ok | ok | n/a | **SUPPORTED** (software path) |
| USB audio streaming (0x01) | ok | n/a | FAILED `status=-32` | **REFUSED `-32`** | **NOT SUPPORTED** — ISO is never negotiated, so the streaming interface cannot be exported |
| UVC webcam (0x0e) | ok | n/a | FAILED `status=-32` | **REFUSED `-32`** | **NOT SUPPORTED** — same cause |

Two classes are unsupported end to end: the **isochronous** streaming interfaces. This matches
`docs/CLIENT.md` and `docs/TRANSPORT.md`, which document isochronous as negotiated-but-unproven; the
observed failure mode is `EPIPE` (`-32`) by design, and the control interface of those devices still
works.

Also NOT RUN against real hardware (no claimable device): mass storage actually mounting and being read by
Windows, HID input reaching an application, CDC enumerated as a COM port, audio endpoint enumeration, UVC
capture by a media application. Control/bulk/interrupt classes are **supported on the software path and
unproven on hardware**.

## 8. Performance (`artifacts/acceptance/perf.txt`)

Loopback, single process, 5 s per case, 64 KiB URBs, 8 in-flight URBs per device, synthetic devices over
the production transport and USB/IP codec. Single sample per case — the transport card measured
case-to-case ranges as wide as 815–2900 MB/s for one of these, so treat these as indicative and not as
medians.

| Transport | 1 dev | 8 dev (aggregate) |
|---|---|---|
| Plain TCP, incompressible | 2174.7 MB/s | 6229.3 MB/s (778.7 MB/s per device) |
| Plain TCP, compressible | 2849.5 MB/s | 6334.0 MB/s |
| TLS, incompressible | 976.2 MB/s | 3729.5 MB/s |
| TLS, compressible | 956.2 MB/s | 3954.1 MB/s |
| TLS+LZ4, incompressible | 572.4 MB/s | 2942.3 MB/s |
| TLS+LZ4, compressible | 700.0 MB/s | 3094.3 MB/s |

- **TLS cost**: 55.1 % slower than plain TCP on a single device (2174.7 → 976.2 MB/s) at these settings.
- **LZ4 cost/benefit at this packet size**: 41.4 % slower than TLS on incompressible payloads; and on
  *compressible* payloads it was also **28.3 % slower** (976.2 → 700.0 MB/s) rather than faster. LZ4 only
  pays off with larger payloads — `docs/TRANSPORT.md` measured TLS+LZ4 at 2029 vs TLS 1417 MB/s using
  1 MiB chunks, where compression wins. At 64 KiB URBs the LZ4 CPU cost dominates. Both facts are true at
  their respective settings; this is the honest resolution, and it is why LZ4 has a per-peer off switch.
- **Scaling**: 8 devices on plain TCP gave 2.86× the throughput of 1 device (6229.3 vs 2174.7 MB/s), i.e.
  the aggregate scales sub-linearly (0.36× per-device efficiency at 8 devices) but well above 1× — a
  single device cannot saturate the loopback path.

## 9. Findings and follow-ups

Findings recorded here; **fixing them was out of the acceptance card's scope** and each was filed as a
separate card. F1 has since been closed by its own card (`t_6973f425`); the fix is described in place.

- **F1 — the operator's nickname is not carried on the wire: CLOSED.** The USB/IP `OP_REP_DEVLIST`
  record (312 bytes: 256-byte path, 32-byte bus id, then numeric fields) has no string field at all, so
  the nickname cannot travel inside it without breaking every stock parser. It now travels *beside* it,
  in VirtualThere's own op `OP_REQ_VHT_DEVINFO`/`OP_REP_VHT_DEVINFO` (`0x8f01`/`0x0f01`, magic `VHT1`),
  specified in `docs/SERVER.md` §8 with the rationale for choosing an extension over repurposing slack
  in the stock record. Evidence, all measured on this machine:
  - `The_Nickname_Reaches_A_Client_Through_VirtualTheres_Own_Op` drives the production server and the
    real client codec over loopback and reads `acceptance-stick` for bus id `1-1` while the stock record
    still reports an empty `Product`/`Manufacturer`/`Serial`.
  - `The_Devlist_Record_Is_Byte_For_Byte_The_Stock_Layout` compares the frame the live server sent
    against one built by hand from the protocol documentation: 328 bytes, identical, so **what a stock
    client sees is unchanged** (no field repurposed, no length reinterpreted).
  - `A_Password_Protected_Device_Is_Not_Offered_On_The_Plaintext_Listener` now also asserts the
    extension names nothing on the plaintext listener, so §6.4 containment holds for the new op too.
  - Client side: `src/Vht.Client/Tests/NicknameExtensionTests.cs` (4 tests) proves the name reaches the
    client's inventory and its control-plane `devices.list`, that the probe runs on a connection of its
    own, and that a peer which does not implement the extension (a stock `usbipd`, which closes the
    connection on an unknown op) still lists normally, with no names and no failure. Raw bytes:
    `artifacts/acceptance/nickname-op.txt`.
  - **NOT RUN:** a live stock Linux `usbip attach` cross-check (no `usbip`/`usbipd` in this environment,
    and installing one needs an interactive sudo password). The "stock clients are unaffected" claim
    rests on the byte-comparison test above, not on a Linux client run.
- **F2 — retained memory grows during a sustained soak: ROOT-CAUSED AND FIXED** (card `t_9b590ee0`).
  It is a real, unbounded retention bug, not GC lag, and the fix is one line of server code.

  *What grew.* `UsbIpSession._pendingTransfers` is the list teardown waits on ("let the transfers still
  running finish, at most 5 s"). It was appended to on every URB and never pruned, so it kept one entry
  per URB ever served, and its backing array grew by doubling — 65,536 → 131,072 → … → 8,388,608 slots,
  i.e. 0.5 MB → … → 64 MB per connection — in the large object heap. The soak now prints that list's
  length every `--gc-sample` seconds, and it accounts for the growth exactly:

  | measured run (frozen copy of the tree, identical command) | retained MiB `GC.GetTotalMemory(forceFullCollection: true)` at t=0 → end | entries in the pending list at end |
  |---|---|---|
  | 8 devices, 600 s, **before** (`soak-mem-before-8dev.txt`) | 1.0 → **532.3** | 37,662,641 |
  | 1 device, 300 s, **before** (`soak-mem-before-1dev.txt`) | 0.8 → **211.8** | 9,283,585 |
  | 8 devices, 600 s, **after** (`soak-mem-after-8dev.txt`) | 1.0 → **4.9–6.4, flat** | 8 (= in flight) |
  | 1 device, 300 s, **after** (`soak-mem-after-1dev.txt`) | 0.8 → **1.0–1.1, flat** | 1 (= in flight) |

  The before case was reproduced twice on the unfrozen tree as well (`soak-mem-before-repeat-*.txt`,
  +554.0 MiB / 8 devices), so it is not an artifact of one run.

  *The arithmetic closes.* 37,662,641 entries over 8 connections is 4.71 M per connection, and the next
  power of two above that is 8,388,608 slots × 8 bytes = 64 MiB per connection = **512 MiB** for eight
  connections, against 532.3 MiB retained — the remainder is the runtime baseline. The same arithmetic
  predicts the plateau between t=300 s and t=480 s (4,194,304 slots × 8 = 256 MiB against 273 MiB
  observed) and the step at t≈540 s when the count crossed the next doubling. The curve is therefore a
  staircase whose steps **double**, and its short-run slope is the URB rate × 8 bytes plus doubling
  slack — which is why the original 120 s sample read as a steady "~1.1 MiB/s" line and why it was not
  obviously unbounded.

  *What each entry costs, and why it is 8 bytes and not a state machine.* A `gcroot` on a live dump walks
  `UsbIpSession` → `List<Task>` → the 16.7 MB `Task[]` backing array → the entry. `dumparray` on that
  array shows most slots holding the **same address**, and `dumpobj` identifies it as
  `Task<VoidTaskResult>.s_cachedCompleted`: the runtime's singleton completed task, which is what
  `RunTransferAsync` returns whenever it completes synchronously (the synthetic capture answers without
  suspending). So the cost of the bug is the list's array, not one async state machine per URB — which
  is also why the process stayed at hundreds of MiB instead of tens of GiB.

  *The harness is excluded by construction, and bounded by measurement.* The retained column is read
  after a blocking full collection, so the harness's per-URB garbage (it allocates ~15 GiB/s here:
  9,272 GiB over the 600 s before-run, 9,182 GiB over the same window after it) cannot appear in it. The
  harness's own live objects — one client and one pending-URB map per device — are bounded by the same
  figure: the whole process, harness included, holds 5–6 MiB after the fix.

  *Fix.* `UsbIpSession.Track` now drops finished transfers before each add, so the teardown barrier holds
  exactly the transfers still running (≤ `ConnectionResources.MaxInFlightUrbs`), never the history. The
  list stays at 8 entries on 8 connections, the array never grows past a few slots, and `RemoveAll` over a
  list that size is allocation-free.

  *Verification.* `tests/Vht.Core.Tests` 160/160 and `tests/Vht.Acceptance.Tests` parity suite green with
  the fix in place; the soak's own throughput is unchanged (before 491 → 490 MB/s per device, after
  729 → 804 MB/s per device, the same run-to-run band the machine showed elsewhere), `failed URBs 0` in
  all four runs, and the attach table still held 8. Working set, which grew 37.7 → 633.3 MiB before, is
  flat at ~108 MiB after the first minute.

  *Not explained by this fix, and left as an observation:* the thread count still rises during a soak
  (+16 to +20 threads over 600 s, both before and after), so it is unrelated to this list and is not
  investigated here.

  *Measurement hygiene, since the tree was being edited by other cards while this card ran:* the four
  numbers above were taken in a frozen copy of the working tree, where the only difference between the
  "before" and "after" binaries is the one pruning line (`UsbIpSession.cs` sha256
  `d55b280e…` before, `bd27f130…` after). The instrumentation the soak needed (`--gc-sample <s>`,
  `--out <file>`, a post-forced-GC retained column and the pending-transfer count) is committed with it.
- **F3 — LZ4 is a net loss on small (64 KiB) URBs**, including on compressible data (§8). Either the LZ4
  threshold or the framing needs revisiting for small URBs, or the default should be off for them.
  **RESOLVED**, in two steps. The threshold was set from measurement (card t_97f18394,
  `docs/TRANSPORT.md` §6.1/§9.4: `ThresholdBytes` 1024 → 65536, the smallest swept payload size at which
  LZ4 beat plain TLS on compressible data with non-overlapping ranges in three consecutive runs). That
  measurement also showed the loss at 64 KiB was not a threshold effect at all: through the production
  session **0** frames ever compressed, at any URB size, because the payload kind never reached
  `Vht.Transport` (card t_825b4f08). Carrying it through `Vht.Core.Transport` fixed that — §9.5 now
  reports +19.4%/+22.4%/+26.1% for `TlsLz4` over `Tls` on compressible payload at 64 KiB URB, reproducing
  run to run with non-overlapping ranges, and 0 compressed frames below the threshold. Incompressible payload
  still loses on `TlsLz4` at every size (that is the CRC-32C item, `docs/TRANSPORT.md` §12 item 7); the
  per-peer answer for such a peer is `Tls`, not a threshold.
- **F4 — the acceptance and parity projects were not in `VirtualThere.slnx`.** FIXED (card
  t_02e30a1e). The solution file listed 15 of the 18 project files on disk, so
  `dotnet build/test VirtualThere.slnx` reported success while never building or running
  `tests/Vht.Acceptance` (the rig), `tests/Vht.Acceptance.Tests` (the parity suite) or
  `tools/Xtask` (the release build tool). All three are listed now, and
  `Vht.Core.Tests.SolutionFileTests` fails by name when a project file on disk is absent from the
  solution file, so the omission cannot come back silently. A solution-wide run now builds 18
  projects, compiles and runs the rig and the parity suite, and reports 0 failed across all 5
  test suites (`dotnet build VirtualThere.slnx -c Release` -> 0 warnings, 0 errors; evidence in
  `artifacts/solution/`). The project count is the number the guard enforces; the test count is
  deliberately not written down here, because every card that lands adds suites to it.
  *Correction to this finding as it was first written:* it also named `Vht.Config`, `Vht.Client` and
  `Vht.Transport` as missing from the solution file; they were not, they were listed all along, and
  that is where the "five projects" figure came from. The real gap was three projects.
- **F5 — isochronous classes are unsupported** (§7), which is a documented limitation rather than a
  defect, but it is the one class-parity item where VirtualThere is knowingly weaker than the reference
  product for audio/webcam streaming.

## 10. What this document is not

- It is not a hardware validation of any device class. No real device was ever exported on this machine
  (§2).
- It is not a claim about the UDE virtual controller end to end: `usbip2_ude.sys` is not installed here,
  and `docs/CLIENT.md` records its own unverified areas (real driver attach, driver port ceiling, driver
  install/signing). The client card's capacity probe (`vht-client capacity`) is the intended way to
  measure the port ceiling on a machine that has the driver; it has not been run.
- It is not a cross-machine test: everything here is one host over loopback. Two-machine discovery and
  attach, real network latency, MTU/fragmentation and NAT behaviour are outside this card (see the config
  card's NOT RUN note for discovery).
