# Third-party notices

VirtualThere is licensed under **Apache-2.0** (see `LICENSE`). Every component listed below is
compatible with an Apache-2.0 work: MIT, BSD-2-Clause and Apache-2.0 are all permissive, carry no
copyleft obligation, and impose nothing beyond attribution. **No GPL, LGPL, AGPL, CPOL or other
non-OSI licence is present anywhere in the build.** The components that were evaluated and rejected
for licence reasons are recorded at the end of this file so the reasoning is auditable.

This list was produced from the actual dependency graph (`dotnet list package --include-transitive`
across the whole solution) as of 2026-10-14, and is checked in CI by
`scripts/check-third-party.sh` against the allow-list in `third_party/notices/allowed-packages.txt`.

## 1. Runtime and platform

| Component | Version | Licence | Used as | Distributed? |
|---|---|---|---|---|
| .NET 10 runtime and BCL (`System.*`, `SslStream`/Schannel, `System.IO.Pipes`, `System.Text.Json`) | 10.0.x | MIT | language runtime, TLS, IPC, JSON | linked into every executable |
| Windows Presentation Foundation (WPF) | .NET 10 | MIT | the management windows hosted by `vht-server` / `vht-client` | linked into `vht-server` and `vht-client` |
| Windows Forms (`System.Windows.Forms.NotifyIcon`) | .NET 10 | MIT | notification-area icon | linked into `vht-server` and `vht-client` |

## 2. Third-party packages in the build

| Component | Version | Licence | Used as | Distributed? |
|---|---|---|---|---|
| `Tomlyn` | 0.20.0 | BSD-2-Clause | TOML parsing in `Vht.Config` | **not linked into any executable yet** - `Vht.Config` and its tests only |
| `K4os.Compression.LZ4` | 1.3.8 | MIT | LZ4 block compression in `Vht.Transport` | **not linked into any executable yet** - `Vht.Transport` and its tests/bench only |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | test host | tests only |
| `xunit` | 2.9.3 | Apache-2.0 | test framework | tests only |
| `xunit.runner.visualstudio` | 3.1.4 / 3.1.5 | Apache-2.0 | test adapter | tests only |
| `xunit.abstractions`, `xunit.assert`, `xunit.core`, `xunit.extensibility.core`, `xunit.extensibility.execution`, `xunit.analyzers` | as resolved with xunit 2.9.3 / analyzers 1.18.0 | Apache-2.0 | xunit internals | tests only |
| `coverlet.collector` | 6.0.4 | MIT | coverage collection | tests only |
| `Microsoft.CodeCoverage` | 17.14.1 | MIT | coverage host | tests only |
| `Microsoft.TestPlatform.ObjectModel`, `Microsoft.TestPlatform.TestHost` | 17.14.1 | MIT | test platform | tests only |
| `Newtonsoft.Json` | 13.0.3 | MIT | transitive of `Microsoft.NET.Test.Sdk` | tests only |

> **Note on the `Distributed?` column.** At v0.1.0 the three shipped executables - `vht-server`,
> `vht-client` and `vht` - link **no third-party NuGet package at all** - their only
> dependencies are Microsoft's own BCL/WPF assemblies plus this repository's own libraries. `Tomlyn`
> and `K4os.Compression.LZ4` are used by `Vht.Config` and `Vht.Transport`, which are not yet wired
> into the executables (see "Known limitations" in `README.md`). They are listed here because they
> are in the build graph and will ship once that wiring lands.

## 3. Third-party artifacts referenced but not redistributed

| Component | Version | Licence | How it is obtained |
|---|---|---|---|
| `vadimgrn/usbip-win2` driver package (`usbip2_ude.sys`, `usbip2_filter.sys`, INF/CAT) | 0.9.8.1 | **BSD-2-Clause** | fetched by the user from the upstream project's release page; **not redistributed in this repository**. Verify against `third_party/usbip-win2-driver/SHA256SUMS` (the package's SHA-256 is `38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a`). |
| `vadimgrn/usbip-win2` upstream headers (`include/usbip/vhci.h`, `consts.h`, `proto.h`) | as of 0.9.8.1 | **BSD-2-Clause** | committed under `third_party/usbip-win2-driver/upstream-headers/` with the upstream `LICENSE.txt`; used as the ABI reference for `Vht.Client`'s IOCTL/struct declarations (re-declared in C#, not compiled). |
| USB/IP protocol specification (Linux kernel documentation, `usbip_protocol.rst`, protocol version 1.1.1 / `0x0111`) | 1.1.1 | kernel documentation licence (GPL-2.0) | **read as a specification only.** No kernel source code, header or implementation is copied into this project; the USB/IP wire format is a public interoperability interface that this project implements independently. |

The BSD-2-Clause text that both `usbip-win2` artifacts are under is reproduced verbatim in
`third_party/usbip-win2-driver/LICENSE.txt` (Copyright (c) 2021-2026, Vadym Hrynchyshyn), and is
retained in the repository as clause 1 of that licence requires.

## 4. Evaluated and rejected (recorded for auditability)

| Component | Licence | Why it is not here |
|---|---|---|
| `dorssel/usbipd-win` driver binaries | **GPL-3.0-only** | Rejected. Its Windows driver set is a GPL-3.0 redistribution of VirtualBox USB filter drivers. Apache-2.0 and GPL-3.0 cannot be combined in a single distributed work, so adopting it would have forced this project to GPL-3.0. |
| `Hardcodet.NotifyIcon.Wpf` | CPOL | Rejected: not OSI-approved, so it would make the dependency graph non-open-source. The in-box `System.Windows.Forms.NotifyIcon` is used instead. |
| The reference product's own client driver / filter driver | proprietary | Not available to us and not licence-compatible; this project writes no driver and uses only the BSD-2-Clause UDE driver above. |
| `Makaretu.Dns` (mDNS responder) | MIT | Evaluated as a DNS-SD fallback; not adopted in v0.1.0 (Windows DNSAPI is used). If adopted it is MIT and compatible. |

## 5. Compatibility statement

* Shipped/runtime components: MIT (Microsoft BCL, WPF, WinForms).
* Build-graph components: MIT, BSD-2-Clause, Apache-2.0 - all permissive.
* Third-party artifacts referenced but not redistributed: BSD-2-Clause (driver, headers).
* Specification read for interoperability: no code copied.
* No copyleft component is linked, bundled or redistributed. Therefore there is **no
  GPL-incompatible combination** in this project.
* Apache-2.0 is compatible with all of the above. Apache-2.0 is also one-way compatible with
  GPL-3.0 (not with GPL-2.0-only), which is a further reason the GPL-3.0 driver set above was
  rejected: it would have removed the ability to relicense or embed this work.

If you add a dependency, add it to `third_party/notices/allowed-packages.txt` in the same change;
CI fails the build otherwise.
