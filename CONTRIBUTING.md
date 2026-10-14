# Contributing to VirtualThere

Thanks for wanting to help. This project exists so that sharing more than one USB device does not
cost money (`README.md` -> "Why this exists"), and contributions that keep it small, honest and
permissive are the ones that fit.

By contributing you agree that your contribution is licensed under the project's Apache-2.0 licence
(inbound = outbound). There is no CLA and no copyright assignment. Adding `Signed-off-by:` to your
commits (DCO) is welcome but not required.

## Ground rules

1. **Be honest about what is verified.** This project's whole value is that it does not bluff. A pull
   request that claims hardware verification must contain the actual output. A pull request that
   cannot verify something should say so in the description, exactly as `docs/CLIENT.md` and
   `docs/ARCHITECTURE.md` do. "Not run" is an acceptable result; "passes" without evidence is not.
2. **No licence machinery, ever.** No device counter, no cap, no key, no activation, no nag, no paid
   tier - not even behind a flag. CI enforces this with greps over source, UI and build output; see
   `.github/workflows/ci.yml`.
3. **No third-party product's name in code, UI or build output.** The project's own branding is
   mirrored in executable names, service name (`vht-server`), pipe names (`vht-server`,
   `vht-client`) and UI strings. The reference product may be named only in
   `docs/RECON_AND_SPEC.md` and in the legal/attribution documents (`README.md`, `LEGAL.md`,
   `THIRD_PARTY_NOTICES.md`). CI fails the build otherwise.
4. **Do not copy code from a product you are not allowed to copy from.** Read
   `docs/RECON_AND_SPEC.md` section 9 before you touch anything protocol-related. Behavioural parity
   yes; byte-for-byte reimplementation of a proprietary protocol, no.
5. **No new dependency without a licence entry.** Add the package to
   `third_party/notices/allowed-packages.txt` and to `THIRD_PARTY_NOTICES.md` in the same change.
   Permissive licences only (MIT / BSD-2 / Apache-2.0); anything copyleft or non-OSI (GPL, LGPL,
   AGPL, CPOL, SSPL, ...) will be rejected.

## Build and test

```bash
dotnet build VirtualThere.slnx -c Release
dotnet test  VirtualThere.slnx -c Release
```

Both must be clean before a pull request: **0 warnings** (projects set `TreatWarningsAsErrors=true`)
and **0 failing tests**. Include the command output in the pull request description.

Run a single suite while iterating:

```bash
dotnet test tests/Vht.Core.Tests/Vht.Core.Tests.csproj
```

## Where things live

| Path | Contents |
|---|---|
| `src/Vht.Core` | USB/IP codec, URB model, device model, bind status, policy, hooks, logging, plain TCP transport |
| `src/Vht.Config` | TOML config schema + validation, persistence, device identity, DNS-SD |
| `src/Vht.Transport` | `ITransport` and its implementations: plain TCP, TLS, TLS+LZ4, auth/pinning |
| `src/Vht.Interop` | All P/Invoke: SetupAPI, CfgMgr32, WinUSB, SCM, DNSAPI, UDE IOCTLs, hotplug |
| `src/Vht.Server` | Service host, device capture, USB/IP server, named-pipe control plane |
| `src/Vht.Client` | Peer store, loopback bridge, attach manager via the UDE driver, client control plane |
| `src/Vht.Cli` | The `vht` operator CLI and the service-mode entry point |
| `src/Vht.Ui` | WPF tray + server/client management windows, plus the UI-automation harness |
| `tests/**`, `src/*/Tests/**` | xUnit suites; `tests/Vht.Acceptance` is the real-hardware harness |
| `docs/**` | The design record and the user documentation |
| `third_party/**` | Pinned upstream headers, licence text and hashes; **no third-party binaries** |

Conventions:

* C# 13 / .NET 10, `nullable enable`, file-scoped namespaces, one type per file where practical.
* All P/Invoke lives under a `Interop/` folder so the unsafe surface stays greppable.
* Comments explain **why**, not what. If a decision is non-obvious, say what it protects against.
* Never commit secrets, certificates, private keys (`*.key.pem` is ignored) or the reverse-engineering
  working set (`unpacked/`, `GhidraProj/` are ignored and must stay that way).

## Pull request checklist

* [ ] `dotnet build VirtualThere.slnx -c Release` - 0 warnings, 0 errors
* [ ] `dotnet test VirtualThere.slnx -c Release` - all green, output pasted in the description
* [ ] No vendor string added to code/UI/build output (the CI grep passes)
* [ ] No licence/cap vocabulary added anywhere
* [ ] New dependency: added to `third_party/notices/allowed-packages.txt` **and**
      `THIRD_PARTY_NOTICES.md`
* [ ] Documentation updated when behaviour, keys, ports or limitations change
* [ ] Claims in the description are backed by real command output

## Reporting bugs

Use the issue templates. A useful report contains: Windows version, `dotnet --version`, the exact
commands, what you expected, what happened, and the relevant log line (the server and client log to
the console and, with `--log`, to a file; `vht status` and `vht-client status` summarise state).
Never paste device passwords or full device serials into an issue.
