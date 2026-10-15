# Security policy

## Reporting a vulnerability

Use GitHub's **private vulnerability reporting**: repository -> *Security* -> *Advisories* ->
*Report a vulnerability*. That channel is visible only to maintainers.

Please do not open a public issue for a security problem until a fix or mitigation is available.

Include: what the issue is, how to reproduce it, which component (`vht-server`, `vht-client`,
`vht-server`, `vht-client`, `vht`, or a library), the version/commit, and what an attacker gains. We aim to
acknowledge within a few days and to agree a disclosure timeline with you before publishing anything.

## Supported versions

This is a pre-1.0 project: only the current `main` branch and the latest tagged release receive
security fixes.

## What is in scope

* `Vht.Server`, `Vht.Client`, `Vht.Core`, `Vht.Config`, `Vht.Transport`, `Vht.Interop`, `Vht.Ui`,
  `Vht.Cli` - all code in this repository.
* The **default configuration**'s security properties.
* The threat model documented in `docs/ARCHITECTURE.md` section 7 (assets, attackers, mitigations).

## What is out of scope

* The third-party `usbip-win2` driver (BSD-2-Clause, upstream). Report those issues upstream. It is
  not redistributed here.
* The Windows USB stack, Schannel, or the .NET runtime: report upstream.
* Physical-access attacks, and anything that requires an already-elevated local administrator.

## Security posture you must know before deploying

These are properties of v0.1.0, not vulnerabilities, and they are stated here so nobody is surprised:

1. **Traffic is unencrypted and unauthenticated in the shipped binaries.** `Vht.Transport` implements
   TLS 1.3 with certificate pinning, LZ4 and authentication, and has its own test suite, but it is
   **not yet wired into `vht-server` or `vht-client`**. The executables therefore speak plain USB/IP,
   which has no authentication of its own: anyone who can reach the listener can enumerate exports
   and, if the server has no per-device password, attach devices. **Run VirtualThere on a trusted
   network only**, or tunnel it (VPN, SSH, WireGuard). Do not port-forward it.
2. **Attaching a device gives a remote peer real access to it.** An attached mass-storage device is a
   block device on the client; an attached HID device is an input device. Treat a VirtualThere server
   like a file share with keys to your hardware.
3. **Device passwords are enforced on the TLS listener only.** `vht --password` is documented as
   TLS-listener-only; on the plaintext listener it is refused rather than silently ignored.
4. **The control plane is local-only.** `vht-server` and `vht-client` expose JSON-lines named pipes
   (`\\.\pipe\vht-server`, `\\.\pipe\vht-client`) ACL'd to Administrators and the interactive user.
   Anything that can write to those pipes can control the service without elevation; that is by
   design, and it is why the pipes are not world-writable.
5. **No privilege escalation is claimed.** The server runs as a service and needs elevation for
   device capture and service install; the tray UI is designed to run unelevated and talks over the
   pipe. If you find a path from unelevated to elevated, that is in scope - report it.
