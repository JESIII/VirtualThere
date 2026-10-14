# Install

VirtualThere v0.1.0 is a **source release**: there is no installer and no prebuilt download yet
(packaging is in progress). You build it with the .NET SDK and, for the *client*, install one
third-party driver.

Everything below was exercised on Windows 11 (build 26300) with .NET SDK 10.0.402.

## 1. Prerequisites

| Requirement | Why | Check |
|---|---|---|
| Windows 10/11 x64 | `vht-server`, `vht-client`, `vht-tray` target `net10.0-windows` | `winver` |
| .NET SDK 10.0 | builds and runs everything | `dotnet --version` -> `10.0.x` |
| Git | cloning | `git --version` |
| **Client only:** the `usbip-win2` virtual USB host controller driver | presents a remote device to Windows; it is a kernel-mode driver, so installing it needs an elevated shell | step 4 below |
| **Server only:** elevation for capture and service install | claiming a USB device in user mode and registering a service both need Administrator | step 5 below |

## 2. Get the source and build it

```bash
git clone https://github.com/JESIII/VirtualThere.git
cd VirtualThere
dotnet build VirtualThere.slnx -c Release
dotnet test  VirtualThere.slnx -c Release
```

Expected: build succeeds with **0 warnings, 0 errors** (projects set `TreatWarningsAsErrors=true`),
and the test run reports **340 passed, 0 failed** across `Vht.Core.Tests` (151), `Vht.Config.Tests`
(77), `Vht.Transport.Tests` (67) and `Vht.Client.Tests` (45).

Executables:

| Binary | Path |
|---|---|
| `vht.exe` (CLI, and the service-mode entry point) | `src/Vht.Cli/bin/Release/net10.0-windows/vht.exe` |
| `vht-server.exe` | `src/Vht.Server/bin/Release/net10.0-windows/vht-server.exe` |
| `vht-client.exe` | `src/Vht.Client/bin/Release/net10.0-windows/vht-client.exe` |
| `vht-tray.exe` | `src/Vht.Ui/Vht.Ui/bin/Release/net10.0-windows/vht-tray.exe` |

Put `vht.exe`'s directory on `PATH` for convenience, or call the binaries by full path.

## 3. First run (server, no service, no config)

```bash
vht list --local        # what this machine has, and whether each device can be captured
vht serve               # export them; Ctrl+C to stop
```

In another shell:

```bash
vht status              # exports, attachments, listeners, "device limit: null (no cap)"
vht list
```

If `vht list --local` reports `state=CaptureUnsupported reason=driver_holds_device` for a device,
that device's own driver currently owns it. That is the normal case for keyboards, mice, webcams and
disks on a running desktop. See `docs/SERVER.md` (capture modes and the rebind option) and
`docs/TROUBLESHOOTING.md`.

## 4. Client only: install the virtual USB host controller driver

The client presents a remote device through the third-party **usbip-win2** UDE driver
(`usbip2_ude.sys`, optionally with its companion filter `usbip2_filter.sys`), version **0.9.8.1**,
licensed **BSD-2-Clause**. It is **not redistributed in this repository** - fetch it from the upstream
project's release page (`vadimgrn/usbip-win2`) and check the hash before installing.

```bash
# Expected hash, from third_party/usbip-win2-driver/SHA256SUMS
# 38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a *USBip-0.9.8.1-x64.exe

sha256sum USBip-0.9.8.1-x64.exe          # in Git Bash
# or:  certutil -hashfile USBip-0.9.8.1-x64.exe SHA256
```

Then run the installer **from an elevated shell** and follow its prompts. The driver is
attestation-signed upstream; on a machine with Secure Boot and Driver Signature Enforcement on, an
unsigned *modified* driver would not load - which is exactly why this project ships the upstream
package unmodified and does not re-sign anything.

Verify it is present:

```bash
vht-client status
# controller: ok ...        (rather than: "the usbip-win2 UDE host-controller driver ... is not installed")
```

Uninstalling the driver is done from the same upstream package or from Device Manager (View ->
Show hidden devices -> USB devices -> "VirtualThere/usbip2 UDE"). Whatever you install, it is your
machine and your decision: read `LEGAL.md` section 6.

## 5. Optional: install the server as a Windows service

Elevated shell, on the machine that owns the devices:

```bash
vht config-template --out C:\ProgramData\VirtualThere\server.toml
# edit the file (see docs/SERVER.md), then:
vht install             # registers the service "vht-server" and starts it
vht service-status
```

The service reads `%ProgramData%\VirtualThere\server.toml` by default. Override with
`--config <path>` or the `VHT_CONFIG` environment variable.

Uninstall:

```bash
vht stop-service
vht uninstall
```

## 6. First attach (client)

On the machine that wants the device, with the driver installed (step 4):

```bash
vht-client peers add 192.168.1.10:3240     # the server's address:port
vht-client list                            # devices the peer exports
vht-client attach 1-3-252f868a             # attach it; Ctrl+C detaches
vht-client attached                        # what the virtual controller has imported
```

Or use the tray app (`vht-tray --mode client`). Both are described in `docs/CLIENT.md`.

**Before you point this at a network you do not control:** v0.1.0 speaks plain USB/IP with no
encryption and no authentication. Keep it on a trusted LAN, or tunnel it. See the security notes in
`README.md` ("Known limitations" 3) and `SECURITY.md`.

## 7. Uninstall

```bash
# client side
vht-client detach --all

# server side (elevated)
vht stop-service
vht uninstall
# then delete the config/state directories if you want them gone:
#   %ProgramData%\VirtualThere     (service config + state)
#   %LOCALAPPDATA%\VirtualThere    (client/tray config + state)
```

If you installed the UDE driver, remove it with the upstream package's uninstaller. VirtualThere
never deletes or replaces a driver it did not install.
