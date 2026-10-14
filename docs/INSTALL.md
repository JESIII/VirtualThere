# Install

VirtualThere v0.1.0 ships three ways. The first two need no compiler:

| Path | What it is | Elevation |
|---|---|---|
| **Installer** | `VirtualThere-0.1.0-x64.msi` — both apps, the `vht-server` service, the virtual-controller driver, the firewall rules, the client logon task and the Start-menu shortcuts | one UAC prompt for the install |
| **Portable folder** | `dist/portable/` — run the executables straight from the folder: no registry, no service. The machine-wide parts (driver, firewall, optional service) come from `Setup-Portable.ps1` | none to run; one prompt for `Setup-Portable.ps1 -Action Install` |
| **From source** | clone and `dotnet build` (sections 1–2 and 5 below) | as the two above |

The MSI and the executables are **unsigned** here — no code-signing certificate exists in this
environment — so Windows shows its usual "unknown publisher" prompt. The kernel driver is not ours:
it is the vendor's own signed package, consumed verbatim (section 4).

`installer/PACKAGING.md` is the packaging document: what each artifact contains, the driver and
elevation model, the versioning/update story, and — section 8 — exactly what was verified on a real
machine and what still needs a clean VM.

## 0. The one-command release build

```powershell
./build.ps1 release        # tests, portable build, MSI, manifest, SHA-256 of every artifact
# identical to: dotnet run --project tools/Xtask -- release
```

Artifacts land in `dist/`: `dist/installer/VirtualThere-<version>-x64.msi` and `dist/portable/`
(plus `release-manifest.txt` and `SHA256SUMS`, listing every file with its size and hash). Needs the
.NET SDK 10 and network access on the first run, to fetch the pinned WiX 5.0.2 toolset and its two
extensions.

```bash
msiexec /i VirtualThere-0.1.0-x64.msi              # install (interactive; no wizard pages)
msiexec /i VirtualThere-0.1.0-x64.msi /qn          # silent
msiexec /x VirtualThere-0.1.0-x64.msi /qn          # uninstall: service, driver, firewall rules, files
```

The portable folder is used in place: copy `dist/portable/` anywhere (it is not under
`%ProgramFiles%` and is not registered with the SCM) and run `vht-server.exe`, `vht-client.exe`,
`vht-tray.exe` or `vht.exe` from there. Configuration stays in `./config` next to the binaries,
because the folder contains `portable.marker`.

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
and the test run reports **348 passed, 0 failed** across `Vht.Core.Tests` (155), `Vht.Config.Tests`
(77), `Vht.Transport.Tests` (67) and `Vht.Client.Tests` (49).

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

Then run the driver package **from an elevated shell** and follow its prompts — the MSI from section 0
does this step for you, hash-checked first, via `scripts\Install-Driver.ps1`. The driver is
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
