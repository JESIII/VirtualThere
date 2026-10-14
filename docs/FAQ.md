# FAQ

### Is this a crack, a keygen or a patched copy of that commercial USB-over-IP product?

No. There is nothing to crack and nothing patched. VirtualThere is a separate program: an
independent implementation of the open **USB/IP** protocol, written from scratch in C#, using a
different (BSD-2-Clause) driver. It contains no code from the commercial product, does not speak
its protocol, and has no licence system to defeat. `LEGAL.md` names that product, states the
independence claim in full, and says what was and was not done.

### So how is it "free and unlimited" if the other product is not?

Because the limit in the other product is a product decision, not a technical one: their free tier
shares one USB device per server, and sharing more requires a paid licence. The USB/IP protocol has
no concept of a device count. VirtualThere simply does not have that concept either - there is no
counter, no cap, no key, no trial state, and no purchase prompt anywhere in the product. See
`README.md` -> "Why this exists", and `vht status`, which prints `device limit: null (no cap)`.

### Is it legal? Reverse engineering was involved.

The project reverse-engineered the *published behaviour* of a commercial product - its feature set,
config key names, service-discovery type and licence model - to know what to build, and implemented
everything independently on top of the open USB/IP standard. No code was copied, translated,
decompiled into source, or redistributed; the binaries analysed during development are not in this
repository and cannot be, because they are excluded in `.gitignore`. Re-implementing behaviour is
ordinary interoperable engineering; copying their protocol or code would not have been. The full
reasoning is `docs/RECON_AND_SPEC.md` section 9 and `LEGAL.md`.

### Can this client attach to a server running the commercial product (or vice versa)?

No, by design - they are not wire-compatible. VirtualThere implements USB/IP; the other product uses
a proprietary, undocumented protocol. Mixing them is not a goal and would be exactly the kind of
derivative work we avoid. A VirtualThere client talks to a VirtualThere server, or to a stock Linux
`usbip`-speaking server for the plaintext listener.

### Does it use that product's EasyFind cloud service?

No. Nothing resolves, calls or clones their vendor cloud matchmaker. Discovery here is local-network
DNS-SD on `_vht._tcp` plus manual `host:port` entries.

### Which devices work?

Control, bulk and interrupt transfers: mass storage, HID (keyboard/mouse/gamepad), CDC serial,
printers/scanners and vendor-specific (WinUSB/libusb) devices. **Isochronous classes do not work** in
v0.1.0: USB audio streaming and UVC webcam video. Full matrix, with what is verified and what is
merely expected: `docs/CLIENT.md` section 3.

### How many devices can I attach at once?

There is no limit in the software. Each attach takes one port on the virtual USB host controller, and
the controller's port ceiling is a property of the third-party driver, so it is *measured*
(`vht-client capacity`) rather than assumed - and nothing in the client enforces the measurement. On
the server, one USB/IP connection can hold many devices and many clients can be attached at once;
the server's own tests exercise 32 concurrent sessions and 8 devices on a single connection.

### Is my traffic encrypted?

Not in v0.1.0. `Vht.Transport` implements TLS 1.3 with certificate pinning, LZ4 compression and
authentication, and has its own 67-test suite with measured benchmarks - but it is **not yet wired
into the `vht-server`/`vht-client` executables**. Until that lands, use a trusted LAN, a VPN, or an
SSH tunnel, and do not expose port 3240 to the internet. This is `README.md` "Known limitations" 3
and `SECURITY.md`.

### Why do I need to install a driver for the client?

Windows needs a kernel component to present a device that is not physically there. That component is
the BSD-2-Clause `usbip-win2` virtual USB host controller; this project ships no driver of its own and
does not redistribute the third-party one - you install it from upstream after verifying its SHA-256
(`docs/INSTALL.md` step 4). The server side needs no driver at all.

### Does it need Administrator rights?

For the server: yes to claim devices and to install the service. For the client: yes to load the
virtual host controller driver (a one-off install); after that the tray UI runs unelevated and drives
the services over a named pipe. Commands fail with a clear message when unelevated instead of
half-working.

### Where are the config and state files?

Server (service): `%ProgramData%\VirtualThere\server.toml`. Client/tray:
`%LOCALAPPDATA%\VirtualThere\`. Override with `--config <path>` or `VHT_CONFIG`. Portable builds use
a file next to the binary when a `virtualthere.portable` marker is present. The keys are in
`docs/SERVER.md` and, for the full model, `docs/CONFIG.md`.

### Why is the config documentation in two places?

`Vht.Config` (the full schema: validation, origins, reload classification, identity) is implemented
and tested but not yet wired into the server binary, which reads its own documented subset. Both
describe the same key vocabulary; `docs/SERVER.md` lists what the service actually reads today, and
`docs/CONFIG.md` describes the model. The duplication disappears when the wiring lands.

### Why Apache-2.0 and not MIT or GPL?

Apache-2.0 is permissive like MIT but adds an explicit patent grant, which matters for software that
talks to kernel drivers and USB stacks, and it is compatible with every dependency here (MIT,
BSD-2-Clause). GPL was rejected so that a company can self-host and embed this without copyleft
obligations - the whole point is that nobody has to pay or ask permission. Reasoning in full:
`README.md` -> "Licence".

### Can I ship this commercially / in a product?

Yes, under Apache-2.0, including in a closed-source product, provided you keep the licence and
attribution notices (`LICENSE`, `NOTICE`, `THIRD_PARTY_NOTICES.md`). You are responsible for the
driver you install and for how you expose USB devices.

### Is Linux/macOS supported?

Not as a server or client in v0.1.0. `Vht.Core`, `Vht.Config` and `Vht.Transport` are platform-neutral
libraries, but the server, client and tray are Windows-only (`net10.0-windows` plus Win32 P/Invoke).
A stock Linux `usbip` client *can* attach to the plaintext listener, which is what protocol
compatibility buys you.

### It says "no exported devices" - is it broken?

Probably not. Check `vht list --local`: devices held by their own driver report
`state=CaptureUnsupported reason=driver_holds_device`, which is normal on a running desktop and means
the server cannot capture them without a rebind. See `docs/TROUBLESHOOTING.md`.

### How do I know the claims in this repository are true?

Every claim of the form "tested" is attached to a command whose output was captured, and claims that
could not be verified say so explicitly - the README's "Known limitations", `docs/CLIENT.md`'s
"Not verified here", and `docs/ARCHITECTURE.md`'s risk table all list "NOT RUN" items with reasons.
CI re-checks the machine-checkable ones (build, tests, no brand pollution, no licence machinery,
no device cap).
