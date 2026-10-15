# VirtualThere — packaging, installation and release (card `t_428614c4`)

This document is the deployment model: what the installer does, what the portable build is, how the
virtual USB controller driver is deployed and removed, how elevation is handled, how a release is
built and how versions move forward. It is deliberately explicit about what has been **verified on
this machine** and what still needs an elevated shell or a clean VM — section 8 lists both.

Owner of this directory: `installer/**`, `tools/Xtask/**`, `build.ps1`, `third_party/**`
(ARCHITECTURE.md Appendix A). Nothing here edits `src/**`.

---

## 1. Artifacts and the one build command

```bash
dotnet run --project tools/Xtask -- release      # tests, portable build, MSI, manifest, hashes
```

`tools/Xtask` is the build entry point fixed by ARCHITECTURE.md §9. It is a build-time tool: it is
never shipped. `build.ps1` is a thin wrapper around the same command for a PowerShell prompt.

`release` does, in order:

1. **test** — the four test projects (`Vht.Core.Tests`, `Vht.Config.Tests`, `Vht.Transport.Tests`,
   `src/Vht.Client/Tests/Vht.Client.Tests.csproj`). A failing test stops the release.
2. **portable** (`xtask portable`) — publishes the four products as **self-contained single-file**
   executables for `win-x64` into `dist/portable/`, plus the driver payload, the setup script and the
   `portable.marker` file that makes the config live next to the binaries.
3. **installer** (`xtask installer`) — builds the MSI with WiX v5 from `installer/VirtualThere.wxs`,
   using `dist/portable/*.exe` as its payload.
4. **hash** (`xtask hash`) — writes `dist/release-manifest.txt` (every artifact with size and
   SHA-256) and `dist/SHA256SUMS`.

Resulting layout (sizes for 0.1.0 are in `dist/release-manifest.txt`, which is generated, not typed
by hand):

```
dist/
  portable/
    vht-server.exe            server, self-contained single file (no .NET install needed)
    vht-client.exe            client
    vht-tray.exe              tray / management UI (server and client modes)
    vht.exe                   operator command line
    portable.marker           config and state stay in this folder
    driver/                   pinned third-party driver package + its licence + its SHA256SUMS
    scripts/                  the privileged setup scripts (see section 3 and 5)
    Setup-Portable.ps1        makes a portable folder fully functional (wrapper over scripts/)
    PACKAGING.md              this file
  installer/
    VirtualThere-<version>-x64.msi
  release-manifest.txt        path, size, SHA-256 of every artifact
  SHA256SUMS                  sha256sum-format list of the same files
```

Two reasons the MSI carries the **self-contained** build rather than a small framework-dependent one:
a clean Windows machine has no .NET 10 desktop runtime, and the card's acceptance test is "run
installer → both apps present" with **no manual configuration**. The cost is size (~174 MB of
executables plus the 26 MB driver package), which `xtask release` reports honestly rather than hides.
`xtask portable --framework-dependent` produces the small variant for size-sensitive cases; it then
requires the .NET 10 Desktop Runtime and is not what `release` builds.

## 2. What the installer creates

| Item | Where / what |
|---|---|
| `vht-server.exe`, `vht-client.exe`, `vht-tray.exe`, `vht.exe` | `%ProgramFiles%\VirtualThere` |
| `vht-server` service | display name **VirtualThere USB Server**, `LocalSystem`, automatic start, description "Shares this machine's USB devices over USB/IP. Free and unlimited." |
| Virtual host controller driver | the pinned upstream package, installed through its own silent switches by `scripts\Install-Driver.ps1` (section 4) |
| Windows Firewall | two inbound TCP rules — `VirtualThere USB/IP (TCP 3240)` and `VirtualThere USB/IP TLS (TCP 3241)` — scoped to `vht-server.exe`, all profiles; Windows Installer removes them on uninstall |
| Client logon task | scheduled task **VirtualThere Client**, at logon, highest run level, runs `vht-client.exe serve` (section 3) |
| Tray logon entry | `HKLM\...\CurrentVersion\Run` value `VirtualThereTray` → `vht-tray.exe --mode server --minimized` |
| Start menu | folder **VirtualThere**: "VirtualThere Server", "VirtualThere Client", "VirtualThere Command Line" |
| Add/Remove Programs | product **VirtualThere**, publisher VirtualThere, version from the single version source (section 6), comment "Free and unlimited, Apache-2.0. No licence key and no device limit." |
| Registry | `HKLM\SOFTWARE\VirtualThere` — `InstallDir` |
| Docs | `%ProgramFiles%\VirtualThere\docs\PACKAGING.md` (this file) |
| Driver notices | `%ProgramFiles%\VirtualThere\driver\usbip-win2-LICENSE.txt`, `usbip-win2-SHA256SUMS` |

The installation is per-machine (`Scope="perMachine"`), so Windows Installer shows the UAC prompt for
the installer itself; nothing else in the product ever needs one (section 3).

Nothing in the package — page, string, shortcut, registry value, file name — names another vendor or
mentions a licence key, activation, purchase or "unlicensed" state. `xtask verify` proves that by
decompiling the built MSI and scanning every table plus every shipped binary (section 8).

## 3. Elevation model

The privilege boundary is deliberate and each half fails loudly rather than half-working:

| Component | Privilege | Why |
|---|---|---|
| `vht-server` service | `LocalSystem`, automatic start | claiming USB devices through WinUSB and binding the listening ports |
| `vht-client` process | Administrator (via the logon task) | the UDE host controller's device interface is opened with `GENERIC_READ|GENERIC_WRITE` and the driver enforces Administrator; `Vht.Client.Interop.Ude.UdeNative.OpenForIoctl` fails with an explicit message naming that requirement |
| `vht-tray` (both modes) | unelevated | it only speaks the control pipe; the privileged work lives in the services |
| installer | elevated once (UAC) | service registration, driver package, firewall rules, logon task |
| portable setup | elevated once, by hand | same three machine-wide items, see section 5 |

**Why a logon task and not a service for the client.** ARCHITECTURE.md §1 allows either, but the
client binary has no service-control dispatcher in v1: it runs `vht-client serve` in the foreground,
so registering it with the SCM would fail with error 1053 ("did not respond in a timely fashion").
The installer therefore creates a logon task with the **highest run level**, which is created once by
an already-elevated installer and then needs no prompt at any logon:

```
schtasks /query /tn "VirtualThere Client" /fo list /v
```

`scripts\Install-ClientTask.ps1` implements this and has a documented fallback: if the highest run
level is refused (policy, or an unelevated caller), it retries without it, logs a warning, and the
client then reports "the client needs Administrator to load/use the virtual host controller" instead
of silently misbehaving. That fallback path is exercised by the unelevated test run recorded in
section 8.

**Unattended / SYSTEM-context deployment.** When the MSI is started by a service account (an SCCM or
Intune SYSTEM deployment), there is no interactive user to own a logon task. The driver, service and
firewall parts still work; `Install-ClientTask.ps1` detects the machine/service account and exits with
a clear message instead of creating a broken task. Deploy the client with a Scheduled Task Group
Policy in that case, pointing it at `vht-client.exe serve` with "Run with highest privileges".

**Fail-loud contract.** Every privileged operation reachable by a user reproduces the behaviour the
card asks for (the reference product's warn-and-restart-elevated message is its analogue):

* `vht-server install` / `uninstall` / `start` / `stop-service` unelevated → "this operation needs an
  elevated shell (run it from an Administrator prompt)" (`Vht.Interop.Services.WindowsServiceManager`).
* `vht-client attach` unelevated, driver installed → the driver-interface message above; driver absent
  → "the usbip-win2 UDE host-controller driver (GUID_DEVINTERFACE_USBIP_VHCI) is not installed…".
* `Setup-Portable.ps1 -Action Install|Uninstall` unelevated → prints the exact elevated command to
  re-run and exits **3**, distinct from a real failure (1), having changed nothing.
* `Install-Driver.ps1` unelevated → the same message and exit **3**, before the vendor package is
  touched (so an unelevated run can never raise the vendor installer's own UAC prompt).
* `Uninstall-Driver.ps1` unelevated → the same message and exit 1.

## 4. Driver deployment and signing

VirtualThere writes no kernel driver (ARCHITECTURE.md §3): the client-side virtual host controller is
the upstream **usbip-win2** UDE driver (`usbip2_ude.sys` + `usbip2_filter.sys`, BSD-2-Clause),
consumed as the vendor's own signed package pinned in `third_party/usbip-win2-driver/`:

| Item | Value |
|---|---|
| Package | `USBip-0.9.8.1-x64.exe` (upstream version 0.9.8.1) |
| SHA-256 (pinned, also in `SHA256SUMS`) | `38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a` |
| Licence | BSD-2-Clause, `LICENSE.txt`, shipped verbatim next to the package |
| Signing | the vendor's own catalog signature; we never re-sign, patch or repack the package |

`scripts\Install-Driver.ps1`:

1. re-computes the SHA-256 and **refuses to execute** the package on a mismatch;
2. is idempotent — if a driver package matching the upstream token is already in the driver store,
   it does nothing (this is what makes a repair install, and a re-install after an uninstall, safe);
3. runs the vendor package with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP-` and waits;
   exit codes 0/6/1641/3010 are accepted (the last three mean "restart pending", which is logged);
4. verifies the result in the driver store (`pnputil /enum-drivers`, matching the upstream token in
   the original/provider name) and, as a second signal, in
   `HKLM\SYSTEM\CurrentControlSet\Services`. If the vendor package claims success but neither signal
   is present, the script dumps the full driver list into `%ProgramData%\VirtualThere\logs\driver-install.log`
   and exits 1, which fails the installation loudly.

Logs: `%ProgramData%\VirtualThere\logs\driver-install.log`, plus the vendor's own
`driver-vendor-install.log` (its `/LOG=` switch).

### Signing steps for a driver of our own (documented, not needed in v1)

Risk 1 in ARCHITECTURE.md: this machine has no `cl.exe`, no WDK and no certificate, and a kernel
driver needs either attestation signing (EV code-signing certificate + Partner Center hardware
dashboard; the submission is free, the certificate is not) or WHQL. If VirtualThere ever ships its
own INF/filter:

1. **Never modify the upstream package.** Shipping it verbatim keeps the vendor's catalog valid;
   re-signing a modified package needs our own submission and invalidates their attribution.
2. For our own INF: obtain an **EV code-signing certificate** (hardware token / HSM-backed), install
   the WDK, build the driver, create a catalog with `Inf2Cat /os:10_X64`, sign the `.cat` with
   `signtool sign /fd sha256 /tr <timestamp-url> /td sha256 /a <file>.cat`, and submit the package to
   the Partner Center hardware dashboard for **attestation signing** (Microsoft re-signs it; the
   vendor-neutral result installs without test mode on Windows 10 1607+).
3. **Test-mode caveat, stated plainly:** an unsigned or self-signed driver package is *not* accepted
   by a normal Windows 10/11 machine. Installing one requires either `bcdedit /set testsigning on`
   (with Secure Boot disabled, and Windows shows a "Test Mode" watermark) or the F8/Shift-restart
   "Disable driver signature enforcement" path, which lasts one boot. Neither is acceptable for
   released software; they are dev-machine affordances only, and this build does not use them.
4. Because v1 consumes the vendor's already-signed package, VirtualThere's own artifact signing is
   limited to the MSI and the executables (`signtool` with a code-signing certificate, optionally
   SHA-256 + timestamped). Those are equally **not** signed in this environment — no certificate is
   available here — and Windows will show the usual "unknown publisher" prompt on the MSI. That is a
   release-engineering gap of this machine, not of the package design: sign the MSI in
   `xtask release` by adding a `signtool` step once a certificate exists.

## 5. Driver removal, and the escape hatch

`scripts\Uninstall-Driver.ps1` runs on uninstall and has three passes plus a verification, because
"no orphaned driver" is an acceptance criterion:

1. the vendor's own uninstaller, found in the registry (any of the three Uninstall hives, matched on
   display name or install location carrying the upstream token), run with `/VERYSILENT`; because
   Inno's uninstaller copies itself to `%TEMP%` and returns early, the script waits for the original
   file to disappear rather than only for the process to exit;
2. a **pnputil backstop**: every driver package in the store whose original or provider name carries
   the upstream token is deleted with `pnputil /delete-driver <oemNN.inf> /uninstall /force`, which
   also removes the device nodes using it;
3. orphan cleanup: an upstream service key is deleted only when no driver package remains (i.e. only
   when it really is an orphan);
4. verification: if a matching package is still present, the script prints the manual recovery
   commands and exits 1, which makes the uninstall fail loudly rather than leave a silent orphan.

Idempotent, like the installer: on a machine that never had the driver it exits 0.

Escape hatch, for a machine where the driver must be left alone (another product's copy of the same
upstream driver in use):

```
msiexec /x VirtualThere-<version>-x64.msi SKIPDRIVERUNINSTALL=1
```

The firewall rules, the service, the logon task and every installed file are still removed; only
step above is skipped. Logs: `%ProgramData%\VirtualThere\logs\driver-uninstall.log`.

## 6. Versioning and the update story

* **One version for everything.** `<Version>` in the repository's `Directory.Build.props` is the
  single source: it flows into the four assemblies (with `AssemblyVersion`/`FileVersion`/
  `InformationalVersion`), into the MSI `ProductVersion`, into the artifact file name
  (`VirtualThere-<version>-x64.msi`) and into the release manifest. Bumping one line moves the whole
  release. `0.1.0` today.
* **Stable identity.** `UpgradeCode` `63376925-1CE3-4FD2-89BD-B884E45EA7E2` is fixed for the product
  and must never change; component GUIDs are explicit and stable, so a major upgrade reuses the same
  components and the same service object instead of leaking duplicates.
* **Upgrade path.** `<MajorUpgrade AllowSameVersionUpgrades="yes">` — installing a newer MSI over an
  older one stops the service, closes the tray and the client process
  (`util:CloseApplication`), replaces the files, restarts the service and re-creates the driver only
  if it is missing. Same-version reinstall is allowed (repair). Downgrades are refused with an
  explicit message; uninstall first if a downgrade is genuinely needed.
* **Auto-update: manual for v1 — stated, not implied.** VirtualThere 0.1.0 ships **no updater**. The
  documented procedure is: run the newer `VirtualThere-<version>-x64.msi` (it upgrades in place), or
  for a portable folder replace the executables (close the tray first; the client task keeps running
  the old path until it is replaced). There is no phone-home, no update service and no version check
  client — which also means no network dependency and nothing to disable.
* A future updater would need: an update feed (signed manifest), signature verification of the MSI
  before installing (the reason section 4's signing gap matters), and a way to restart the client task
  and the service. That is a separate card's scope; nothing in this packaging prevents it.

## 7. Known limitations, stated rather than hidden

1. **PowerShell is required for the privileged steps.** The MSI's custom actions run the shipped
   `.ps1` files with `-NoProfile -ExecutionPolicy Bypass`. A machine with a WDAC/AppLocker policy that
   forces PowerShell **Constrained Language** mode can block them; the installer then fails with the
   script's own message and exit code instead of half-installing. Every step is also available as a
   manual command (section 5 and section 8) for such a machine.
2. **The driver INF names inside the vendor package are not published upstream**, so the presence
   checks match on the upstream product's own token (`usbip`) rather than on exact INF names — and on
   a **word boundary** (`\busbip`), because Windows ships an in-box service named `mausbip` ("MA-USB
   IP Filter Driver", `mausbhost.inf`) whose name *contains* `usbip`. The bare substring made the
   presence check report the driver as installed on a machine that never had it, and would have made
   the uninstaller delete that in-box component's service key as if it were our orphan; both were
   caught by running the scripts on this machine and are fixed. If a future upstream release renames
   its INF without that token, the check would need updating — it fails loudly rather than silently,
   by design.
3. **Reboot-pending is not forced.** If the vendor package reports that a restart is needed, the
   install still succeeds and the log says so; the client reports the driver as unavailable until the
   restart.
4. **The tray runs unelevated while the client runs elevated.** Whether an unelevated process can
   write to a named pipe created by an elevated one under the default security descriptor is
   process-integrity dependent; the architecture assumes it works (§1: "lets the UI run unelevated").
   It is **not verified on this machine** (it needs an elevated client). If it turns out to be
   blocked, the tray's client mode needs the pipe's DACL set explicitly, or the tray has to be started
   elevated — whichever the UI card prefers.
5. **Config is left in place on uninstall.** `%ProgramData%\VirtualThere` (server) and
   `%LOCALAPPDATA%\VirtualThere\client` (client) hold configuration, peers, grants and logs. Removing
   them is not the uninstaller's job (silently deleting user data is worse than leaving it), and the
   uninstall acceptance test only requires the service, driver, firewall rules and binaries to go.
6. **No icons or branding artwork** in the package: the icon set lives in the UI card's scope.
   Shortcuts therefore use the tray executable's default icon.

## 8. Verification: what was run, and what needs an elevated shell

Every result below was produced by two commands on Windows 11 (build 26300) with .NET SDK 10.0.402,
starting from `xtask clean`:

```
dotnet run --project tools/Xtask -- release     # exit 0
dotnet run --project tools/Xtask -- verify      # exit 0, "verify: PASS"
```

Raw console output: `artifacts/packaging/release-and-verify.log`. Re-captured deployment evidence:
`artifacts/packaging/` — every file carries the capture timestamp, the host and the **elevation state
of the shell it was captured in** (this one is *not* elevated).

* **The release build, end to end.** The four test projects pass; the four products publish as
  self-contained single-file executables; the pinned vendor driver package is verified against its
  `SHA256SUMS` before it is copied; the MSI builds with WiX 5.0.2; `dist/release-manifest.txt` and
  `dist/SHA256SUMS` list all 16 artifacts (373.3 MiB total) with sizes and SHA-256.
* **`xtask verify` → PASS** (`dist/verify/verify-report.txt`). `wix msi decompile` of the built MSI,
  then: the expected tables exist (`ServiceInstall` for `vht-server`, both firewall rules with the
  documented names and ports, the three shortcuts, the tray logon entry, all three custom actions, the
  pinned driver payload, the shipped packaging document); the package carries **no** wizard dialog
  table, no `LicenseAgreementDlg`, no `LicenseAcceptedCheckBox`, no `WixUILicenseRtf`, no `PIDKEY` and
  no `WIXUI_INSTALLDIR`; and neither the MSI, its decompiled source, nor the four shipped executables
  contain any of the forbidden tokens (the reference product's name, licence/activation/purchase
  wording, device-count wording).
* **The MSI's own file table, laid out without installing anything.**
  `msiexec /a VirtualThere-0.1.0-x64.msi /qn TARGETDIR=<scratch>` — an *administrative install*: no
  elevation, no service, no driver, just the payload. This is how the "both apps present" half of the
  acceptance criterion is checked here, with no clean VM available:
  `artifacts/packaging/msi-administrative-install.txt`.
* **The portable build, run from a non-installed location.** The whole folder was copied to
  `%LOCALAPPDATA%\hermes\cache\scratch\vht-portable-run` (not `%ProgramFiles%`, not registered with the
  SCM) and executed there: `vht-server version` / `list --local` / `config-template`, `vht status`,
  `vht list --local`, `vht-client status`, `vht-client capacity`. `list --local` enumerated this
  machine's 13 real USB devices and reported "no device limit". Files: `portable-*.txt`.
* **Elevation behaviour, unelevated, changing nothing:** `vht-server install` (actionable
  access-denied message), `Setup-Portable.ps1 -Action Install|Uninstall` (exit **3** plus the exact
  elevated command), `-Action Install -WhatIf` (prints every privileged command, exit 0),
  `-Action Status` (read-only, exit 0), `Install-Driver.ps1` (exit 3, before the vendor package is
  touched), `Install-Driver.ps1 -Status` (correctly reports the driver absent), `Uninstall-Driver.ps1`
  (exit 1), `Install-ClientTask.ps1` (the documented highest-run-level fallback, then "could not create
  the logon task at all"), `Remove-ClientTask.ps1` (idempotent, exit 0). Files: `driver-*.txt`,
  `client-task-*.txt`, `elevation-server-*.txt`, `portable-install-*.txt`.
* **The machine state those checks read:** `netsh advfirewall` finds no rule, `sc query vht-server`
  returns 1060 (no such service), `pnputil /enum-drivers` has no upstream driver package, and
  `schtasks /query "VirtualThere Client"` finds no task. Files: `firewall-rule-query.txt`,
  `service-query.txt`, `driver-store-usbip-hits.txt`, `client-task-query.txt`.

**Defects this run found and fixed** — each was an observed failure, not a hypothetical one:

1. `Setup-Portable.ps1` contained a C-style `\"` escape inside a PowerShell double-quoted string, so
   the file **did not parse**: every portable action died before executing. (Found by parsing all five
   scripts with PowerShell's own parser instead of by eye.)
2. `Setup-Portable.ps1 -Action Status` aborted with "unhandled failure" on a machine with no client
   task: PowerShell 5.1 turns redirected native stderr into a *terminating* error under
   `$ErrorActionPreference = 'Stop'`, and `schtasks` writes "cannot find the task" to stderr.
3. `Setup-Portable.ps1 -Action Install -WhatIf` aborted reading `$failed.Count`, because
   `return $failed` unrolls an empty array to `$null`. Fixed with `return ,$failed` and
   `@($failed).Count`.
4. The driver presence check matched the **substring** `usbip`, which also matches the in-box
   `mausbip` service ("MA-USB IP Filter Driver", `mausbhost.inf`). On this machine that made
   `Install-Driver.ps1 -Status` claim the driver was installed, made a real install a no-op, and would
   have made the uninstaller delete a Windows component's service key. Both scripts now match on a
   word boundary (`\busbip`); see section 7.2.
5. `Install-Driver.ps1` had no elevation gate at all: run unelevated it would have launched the vendor
   installer and its UAC prompt. It now exits 3 with the actionable message, like `Setup-Portable.ps1`.
6. `Uninstall-Driver.ps1` carried a stray `Array` statement, which PowerShell reported as an
   unrecognised command before the script body ran.
7. `xtask hash` swept `xtask verify`'s scratch directory into the manifest when `verify` ran before
   `hash`. `dist/verify/` is now excluded from artifact collection.

**Not run here — requires an elevated shell and/or a clean VM, and is reported as unverified rather
than assumed:**

* `msiexec /i VirtualThere-0.1.0-x64.msi` on a clean machine (this shell is not elevated, and no VM is
  available): the full acceptance path "run installer → both apps present → server exports a device →
  client attaches it". The elevated checklist is below.
* Driver installation and removal (both need Administrator).
* Firewall rule creation/removal (`netsh advfirewall` writes need Administrator).
* Service registration/start/reboot survival.
* The tray-versus-elevated-client pipe question in section 7.4.

Elevated checklist for whoever has the clean VM (each command is also the manual equivalent of the
corresponding custom action):

```powershell
# 1. install (expected: %ProgramFiles%\VirtualThere with four exes, service running, two firewall
#    rules, driver present, logon task present, Start-menu folder present)
msiexec /i VirtualThere-0.1.0-x64.msi /l*v install.log
sc.exe query vht-server
netsh advfirewall firewall show rule name="VirtualThere USB/IP (TCP 3240)"
pnputil /enum-drivers | Select-String '\busbip'
schtasks /query /tn "VirtualThere Client" /fo list /v
& "$env:ProgramFiles\VirtualThere\vht-server.exe" list          # devices offered
& "$env:ProgramFiles\VirtualThere\vht-client.exe" peers add <server>:3240
& "$env:ProgramFiles\VirtualThere\vht-client.exe" attach <busid>
# 2. uninstall, then verify nothing was left behind
msiexec /x VirtualThere-0.1.0-x64.msi /l*v uninstall.log
sc.exe query vht-server                                        # 1060: service does not exist
netsh advfirewall firewall show rule name="VirtualThere USB/IP (TCP 3240)"   # "No rules match"
pnputil /enum-drivers | Select-String '\busbip'                    # no match
schtasks /query /tn "VirtualThere Client"                      # ERROR: cannot find
Test-Path "$env:ProgramFiles\VirtualThere"                     # False
# 3. re-install must succeed again (the acceptance criterion)
msiexec /i VirtualThere-0.1.0-x64.msi
```
