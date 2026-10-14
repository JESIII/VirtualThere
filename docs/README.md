# VirtualThere documentation

Two kinds of document live here, and it helps to know which you are reading.

**User documentation** - what you need to install, configure, run and fix:

| Document | Contents |
|---|---|
| [`INSTALL.md`](INSTALL.md) | Prerequisites, build, driver install with hash verification, service install, first run, uninstall |
| [`SERVER.md`](SERVER.md) | Server setup, the `server.toml` subset the service reads, capture modes, policy, hooks, ports, security notes |
| [`CLIENT.md`](CLIENT.md) | Client setup, attach/detach, the multi-device model, hotplug/reconnect, credential flow, device-class matrix |
| [`CONFIG.md`](CONFIG.md) | Config reference for the full model (`Vht.Config`): every key, default, type, validation rule, identity fallback, reload classification |
| [`TRANSPORT.md`](TRANSPORT.md) | The TLS/LZ4/authentication layer: wire format, handshake, anti-downgrade rules, measured benchmarks |
| [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md) | Symptom -> cause -> fix, from real messages in this codebase |
| [`FAQ.md`](FAQ.md) | Short answers, including the legal and profitability questions |

**Engineering record** - how this was designed and what is verified:

| Document | Contents |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | The frozen architecture: components, decisions, invariants, risk register, appendices (write scopes, protocol vocabulary, third-party components) |
| [`RECON_AND_SPEC.md`](RECON_AND_SPEC.md) | The clean-room recon record (behaviour, config surface, licensing model) and the parity target, including the legal position in section 9 |
| [`FEATURE_MATRIX.md`](FEATURE_MATRIX.md) | Feature-parity checklist: reference product free tier vs paid tier vs what VirtualThere does |
| [`UI.md`](UI.md) | UI affordance map, branding scope, accessibility, harness evidence, screenshots in `ui-shots/` |
| [`ACCEPTANCE.md`](ACCEPTANCE.md) | What was measured on real hardware, what was measured on the software path, and what was NOT RUN — multi-device, soak, device-class matrix, performance, and the no-licence-gate negative test |

## About the engineering record

`ARCHITECTURE.md`, `RECON_AND_SPEC.md`, `FEATURE_MATRIX.md` and `UI.md` are the **original design
record** of the v0.1.0 build. They are published because they are the honest account of what was
verified, what is inferred, and what was deliberately not done - including the evidence behind the
legal position.

Two conventions in those documents may look odd to an outside reader:

* Labels such as `server-workstream`, `client-workstream`, `transport-workstream` name the parallel
  **build stages** the work was partitioned into. They are provenance, not components: the code they
  produced lives under `src/` and is described by the user documentation above.
* Hex addresses and quoted strings appear throughout `RECON_AND_SPEC.md`. They are evidence from the
  behavioural recon. The binaries they refer to are **not part of this project** - they are excluded
  by `.gitignore` and are not redistributed in any form (`LEGAL.md`).

Point-in-time statements ("not yet verified", "in progress") are as of the v0.1.0 source release; the
README's "Known limitations" is the live list.

## Claim discipline

This project tries never to state something as verified that is not. Where a claim rests on a command,
the command and its output are recorded; where verification was impossible in the build environment,
the document says **NOT RUN** with the reason and names what would be needed to close it. Expect to
see phrases like "expected to work; not verified on hardware" - they mean exactly that.
