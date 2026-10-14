# Legal position and attribution

This file is the plain-language legal statement for VirtualThere. It is not a licence (that is
`LICENSE`), and it is not legal advice.

## 1. What VirtualThere is

VirtualThere is an **independent, clean-room implementation of the open USB/IP standard**. It lets a
Windows machine export its USB devices over the network and another Windows machine attach them as
local devices, using the publicly documented USB/IP protocol (Linux `usbip` / `usbip_protocol.rst`,
protocol version 1.1.1) and the BSD-2-Clause `usbip-win2` virtual USB host controller driver.

## 2. What VirtualThere is not

Stated plainly, because it matters:

* **It contains no VirtualHere code.** No VirtualHere source, binary, driver, string table, resource
  or data file is copied, bundled, linked, translated, decompiled into source, or redistributed
  anywhere in this repository. Nothing here is a derivative work of VirtualHere.
* **It is not wire-compatible with VirtualHere**, and this is a design requirement rather than a
  gap. VirtualHere's protocol is proprietary and undocumented; re-implementing it byte-for-byte
  would be the kind of derivative work their EULA forbids, and it would mean tracking a moving
  target across their releases. A VirtualThere client cannot attach to a VirtualHere server, and
  vice versa.
* **It is not affiliated with VirtualHere Pty. Ltd.** in any way - no sponsorship, endorsement,
  partnership, or code contribution in either direction.
* **It does not use VirtualHere's services.** Their vendor-operated cloud matchmaker hostname is not
  called, not resolved, and not cloned; discovery here is local-network DNS-SD plus manual
  `host:port` entries.
* **It does not reproduce VirtualHere's licensing mechanism.** There is no licence key, no activation,
  no trial state, no device counter and no purchase prompt anywhere in the product or the UI.

## 3. How the compatibility information was obtained

VirtualThere aims for parity of **behaviour and configuration vocabulary**, not of bytes. The
information behind that parity was gathered by observing the *published behaviour* of the reference
product - its feature set, its configuration key names, its service-discovery type, and the shape of
its licensing model - and by reading the public USB/IP specification. That work is recorded, with
evidence and addresses, in `docs/RECON_AND_SPEC.md`, whose section 9 ("Approach and legal position")
and section 1 ("Licensing model") are the authoritative statement of the position summarised here.

Two consequences worth stating explicitly:

* **The reference binaries are not part of this project.** They are not in this repository, are
  excluded by `.gitignore` (`unpacked/`, `GhidraProj/`), and are not redistributed in any archive or
  release.
* **No proprietary component was adopted to save work.** The driver this project consumes is the
  BSD-2-Clause `usbip-win2` driver, used unmodified and attributed; no VirtualHere driver or filter
  is used, shipped or re-signed.

## 4. Names and trademarks

"VirtualHere" is the name of a third-party product; it, and any associated marks, belong to their
owner (VirtualHere Pty. Ltd.). It is used in this repository only where a factual reference is
necessary - the recon record (`docs/RECON_AND_SPEC.md`), this statement and the attribution section
of `README.md` - and never as part of VirtualThere's own branding, UI, executable names, service
names, pipe names, configuration vocabulary or build output. VirtualThere claims no rights in it and
implies no endorsement.

"VirtualThere" is the name of this project. No trademark claim is made.

## 5. Third-party components

Every third-party component in the build, and every third-party artifact the instructions tell you
to fetch, is listed with its version, licence and provenance in `THIRD_PARTY_NOTICES.md`. All of
them are MIT, BSD-2-Clause or Apache-2.0; no copyleft component is linked, bundled or redistributed.

## 6. Your responsibilities

Installing the `usbip-win2` driver is your decision on your machine; it is a kernel-mode driver and
requires an elevated install. Sharing USB devices over a network can expose storage, input devices
and authentication tokens - see the security notes in `docs/SERVER.md` and `docs/CLIENT.md` before
exposing a server beyond localhost. The software is provided under Apache-2.0, without warranty.
