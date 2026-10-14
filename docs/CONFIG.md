# VirtualThere configuration

`config.toml` is written by hand. `state.toml` is machine-managed (nicknames set at runtime, passwords,
manual servers, auto-attach grants, the device identity map) and overlays it. Both are TOML.

## Why TOML

* Human-editable with the properties this file needs: real types (integers stay integers, so
  `port = 3240` cannot silently become the string "3240"), arrays (`policy.allow = ["1050:0407"]`),
  nested tables that mirror the key hierarchy, and comments for the "what changed / why" trail an
  operator leaves behind.
* One parser on every platform: `Tomlyn` (BSD-2-Clause), no per-platform configuration API, so the
  same file is valid on Windows and on a dev host.
* Positions. The parser reports line and column for every value, which is what lets a diagnostic say
  `config.toml(12,9): transport.tls.port must be an integer, found 'not a port'` instead of "invalid
  config". Spans are collected from the syntax tree (the model layer drops positions), so this holds
  for nested tables too.
* Rejected alternatives: Windows INI has no types and no nesting (and a `;` comment clash with our
  selectors); JSON cannot carry comments, which is the one thing an operator-authored file needs; XML
  is loud and position-poor; a .NET `appsettings.json`-style schema would tie the format to the host.
  the reference product's own format is a flat key/value blob with comma-delimited lists and a
  "nickname cannot contain a comma" rule; TOML removes that whole class of bug.

## Where the files live

| Role | Default | Override |
| --- | --- | --- |
| Server (service) | `%ProgramData%/VirtualThere/config.toml` | `--config <path>` or `VHT_CONFIG` |
| Client / tray | `%LOCALAPPDATA%/VirtualThere/config.toml` | `--config <path>` or `VHT_CONFIG` |
| Portable | `<directory of the executable>/config.toml` (used automatically when a `virtualthere.portable` marker file sits next to the binary) | - |

`state.toml` always lives next to `config.toml` - it belongs to the same installation. Precedence for
the location itself is: command line, then `VHT_CONFIG`, then the portable marker, then the per-role
default. `general.data_directory` moves logs and caches only, never the config file.

## Layering and origins

Effective value order, weakest first:

1. built-in defaults,
2. `config.toml`,
3. `state.toml`,
4. `VHT_*` environment variables (`VHT_SERVER__NAME` -> `server.name`, double underscore separates
   path segments; `VHT_DATA_DIR` is an alias for `general.data_directory`),
5. `--set path=value` on the command line.

Every value remembers which layer produced it, so `vht config show --origin` answers "why is the port
4000?" with `file:.../config.toml(4,8)`. Values from environment and command line are coerced to the
schema type first, so a typo is a diagnostic and not a silently-stringly-typed value.

## Keys

Defaults, type and reload behaviour for every key. "Restart" is the column that matters: a change to a
key marked *yes* cannot be applied to a running service (see "Reload" below).

### general

| Key | Type | Default | Restart | Notes |
| --- | --- | --- | --- | --- |
| `general.config_version` | integer | `1` | yes | Schema version. Migrated forward automatically; **a file written by a newer VirtualThere is a hard error and the service refuses to start** rather than guessing at unknown semantics. |
| `general.role` | string | `auto` | yes | `server`, `client` or `auto`. Chooses the default config directory; nothing else. |
| `general.log_level` | string | `info` | no | `trace`, `debug`, `info`, `warn` or `error`. |
| `general.log_file` | string | (empty) | yes | Log file path. Empty selects the platform default (Windows Event Log for the service, stderr for a console run). |
| `general.data_directory` | string | (empty) | yes | Directory for logs and caches. `state.toml` always stays beside `config.toml`. |
| `general.strict` | boolean | `false` | no | When true an unknown key is an error instead of a warning. Use it in CI to catch typos. |

### server

| Key | Type | Default | Restart | Notes |
| --- | --- | --- | --- | --- |
| `server.name` | string | machine host name, upper-cased | no | Advertised server name (the reference product's `ServerName`); shown in the client's server list and in the DNS-SD TXT record. Trimmed, upper-cased, max 64 characters. |
| `server.enabled` | boolean | `true` | yes | Whether this process exports devices at all. |
| `server.auto_attach_to_kernel` | boolean | `true` | no | the reference product's `AutoAttachToKernel`: hand a device back to its kernel driver while no client is using it. |
| `server.password_max_length` | integer | `64` | yes | Maximum accepted per-device password length (8..256). |

### policy (server-side export filters)

| Key | Type | Default | Restart | Notes |
| --- | --- | --- | --- | --- |
| `policy.allow` | list of selectors | `[]` (empty = everything) | no | Allow list. When non-empty, **only** matching devices are exported. |
| `policy.ignore` | list of selectors | `[]` | no | Ignore list. Always wins over `policy.allow`: an ignored device never appears in the export list, even if it is also allowed. |
| `policy.unignore_on_allow` | boolean | `false` | no | When true, a device listed in `policy.allow` drops a matching `policy.ignore` entry at load time (a warning is still emitted, so the conflict is visible). |

Selector grammar, used by `policy.allow`, `policy.ignore`, `[nicknames]` and `[passwords]`:

```
*                          any device
1050:0407                  vendor id : product id
1050:*                     any product from that vendor
*:0407                     any vendor with that product id
1050:0407:0123456789AB     vid : pid : serial number
serial:0123456789AB        any device carrying that serial number
vid:1050                   any device from a vendor
pid:0407                   any device with that product id
key:s-1050-0407-0123456789AB   the stable device key (strongest, survives string changes)
```

### transport

| Key | Type | Default | Restart | Notes |
| --- | --- | --- | --- | --- |
| `transport.plaintext.enabled` | boolean | `true` | yes | Listen on the stock USB/IP port so stock peers can connect. Password-protected devices are never served here. |
| `transport.plaintext.port` | integer | `3240` | yes | the reference product's `TCPPort`; 3240 is the USB/IP default. |
| `transport.tls.enabled` | boolean | `true` | yes | Listen on the TLS port. |
| `transport.tls.port` | integer | `3241` | yes | the reference product's `SSLPort`; 3240+1 so both listeners coexist. Must differ from the plaintext port when both are enabled. |
| `transport.tls.certificate` | string | (empty) | yes | PEM/PFX path. Empty means generate/use the per-machine self-signed certificate. |
| `transport.tls.lz4_enabled` | boolean | `true` | no | the reference product's compression switch: negotiate LZ4 on the TLS listener. |
| `transport.tls.lz4_threshold_bytes` | integer | `1024` | no | the reference product's `CompressionLimit`: payloads smaller than this are never compressed. |

### security

| Key | Type | Default | Restart | Notes |
| --- | --- | --- | --- | --- |
| `security.plaintext_password_protected_devices` | boolean | `false` | yes | **Must stay false.** The key exists so the rule is explicit and rejectable: a password-protected device is never served on the plaintext listener. Setting it true is a configuration error. |
| `security.auth_shared_secret` | string | (empty) | no | Optional server-wide shared secret, mirrored on the client. Empty disables it. |

### discovery

| Key | Type | Default | Restart | Notes |
| --- | --- | --- | --- | --- |
| `discovery.enabled` | boolean | `true` | no | Advertise/browse with DNS-SD. Disabling it never disables manual `host:port` entries. |
| `discovery.service_type` | string | `_vht._tcp` | yes | Service type, `_name._tcp` or `_name._tcp.local`. Deliberately **not** `_vhusb._tcp` (the reference product's type) so the two products never see each other's clients; `_vhusb` is rejected as a validation error. |
| `discovery.instance_name` | string | `server.name` | no | DNS-SD instance name. Re-registered on change, so it is hot-reloadable. |
| `discovery.publish_spki` | boolean | `false` | no | Publish the certificate SPKI pin in the TXT record so a client can confirm the certificate before opening a socket. Off by default until the transport layer can produce the pin. |
| `discovery.capabilities` | list of strings | `[]` | no | Advertised capability tokens (`caps=` in the TXT record), e.g. `["lz4", "tls1.3"]`. Tokens may not contain commas. |
| `discovery.manual_servers` | list of strings | `[]` | no | Client side: persistent `host:port` entries, e.g. `["192.168.1.20:3240", "[fe80::1]:3241"]`. Always available, even with discovery off; a manual entry that names an already-discovered endpoint is merged instead of duplicated. |
| `discovery.browse_on_start` | boolean | `true` | no | Client side: browse automatically at startup. |
| `discovery.browse_seconds` | integer | `5` | no | Client side: how long one browse pass runs before results are reported as a snapshot. |

TXT record keys (frozen): `v` (record version, currently `1`), `name`, `tls` (`1`/`0`), `spki`
(base64url SHA-256 of the certificate's SubjectPublicKeyInfo), `caps`. A record whose `v` is newer than
this build is reported as a warning and its unknown fields are treated as absent.

### auto_attach (client-side "Auto-Use")

| Key | Type | Default | Restart | Notes |
| --- | --- | --- | --- | --- |
| `auto_attach.enabled` | boolean | `false` | no | Master switch. Even when true, a device is attached only if a grant exists **and** authorization succeeded first. |
| `auto_attach.qualify_by_server_name` | boolean | `true` | no | the reference product's "Qualify Auto-Use by Server Name": a grant is honoured only for the server it was granted for. |
| `auto_attach.qualify_by_interface` | boolean | `false` | no | the reference product's "Qualify Auto-Use By Server Network Interface". |
| `auto_attach.delay_seconds` | integer | `3` | no | the reference product's Auto-Use Delay: wait this long after a device appears before attaching. |
| `auto_attach.retry_delay_seconds` | integer | `30` | no | the reference product's Auto-Use Retry Delay. |
| `auto_attach.max_retries` | integer | `0` | no | `0` means unlimited retries. |
| `auto_attach.grants` | array of tables | `[]` | no | **Machine-managed (state.toml).** One entry per grant: `device_key`, optional `server`, optional `server_interface`, `granted_at_utc`, `last_used_utc`. Written only after authorization succeeded - the reference product records the grant first and complains later that the client was never authorized (`0x1409d262b`); this implementation records authorization first and the grant second. |

### nicknames, passwords and the identity map

| Key | Type | Default | Restart | Notes |
| --- | --- | --- | --- | --- |
| `nicknames` | table (selector = name) | (empty) | no | Friendly name per selector or device key, e.g. `"1050:0407" = "YubiKey"` or `"key:s-1050-0407-0123456789ab" = "Work token"`. Because TOML tables have no delimiter, a nickname may contain any character - unlike the reference product's `DeviceNicknames`, which cannot hold a comma. Lookup order, most specific first: exact device key, `vid:pid:serial`, `vid:pid`, `product:<name>`. |
| `passwords` | table (selector = secret) | (empty) | no | Per-device password, as `"1050:0407" = "hunter2"`, `"sha256:<64 hex chars>"` or `"pbkdf2:<iterations>:<salt-b64>:<hash-b64>"`. Everything but the plaintext form is a verifier; `vht config set-password` generates a PBKDF2 entry (SHA-256, 100000 iterations). An empty or malformed entry is a configuration error, never a silent lockout. Devices listed here are served on the TLS listener only. |
| `identity.devices` | array of tables | `[]` | no | **Machine-managed (state.toml).** The device identity map (the reference product's `DeviceIdMap`). See below. |

`nicknames` and `passwords` are usually written by the running process into `state.toml` (renaming a
device in the UI, remembering a password), but both may also be authored by hand in `config.toml`; the
state file wins because it is the more specific layer.

## Device identity

Two machines must agree on *which* device is which, and the key must survive a replug and a move to a
different hub port. The identity map (`identity.devices` in `state.toml`) is what makes that possible.

Key formats (lower case):

```
s-1050-0407-0123456789AB          serial number present and plausible
f-1050-0407-0400-1a2b3c4d         no serial: vid, pid, bcdDevice and an 8-hex fingerprint
t-9c2f1a4b                        no serial and nothing else to hash: port-path hash
<key>~2                           disambiguator for an ambiguous key within one snapshot
```

Fallback chain, strongest first:

1. **Serial number** - primary identity. It survives a port change unconditionally.
   Serial hygiene: placeholders are treated as *absent* (`Default string`, `To be filled by O.E.M.`,
   `1234567890`, all-same-character strings, anything shorter than 3 characters). Surviving serials are
   upper-cased, separators (whitespace, `/`, `-`) collapse to a single `-`, other characters are
   dropped, and a serial longer than 48 characters is truncated with an 8-hex suffix of its SHA-256 so
   two long serials sharing a prefix stay distinct.
2. **Fingerprint** - hash of `vid|pid|bcdDevice|manufacturer|product|class triple`. No serial is used
   here on purpose, so the key survives a hub-port change. Two identical serial-less units of the same
   model get the same key and are separated by the disambiguator: within one snapshot the devices are
   ordered by port path, so the suffix is assigned deterministically and a replug into the same port
   reproduces it. The disambiguated key is persisted as an alias of the map entry.
3. **Port path** - the device exposes no serial and no strings. The key is a hash of the port path.
   This is stable while the device stays on that port. When it moves, the map repairs it: if the
   snapshot contains no other device of the same kind and exactly one persisted entry of that kind is
   missing, the moved device adopts that entry's key (reported as `AdoptedFromMap` with the reason).
   If two identical units exist - one attached, one missing - adoption is **refused**, because which
   physical unit moved is a guess; the device gets a fresh port-path key and the ambiguity is visible
   in the logs rather than silently mis-assigning slots.
4. **Adopted from the map** - aliases make any of the above forms resolvable: a device may be referred
   to by its primary key or by any alias (`s-`, `f-`, `t-` form) it has ever had. Nickname and password
   selectors resolve against the same map.

Practical consequence: any device can be addressed with `key:<device key>` in `policy.allow`,
`policy.ignore`, `[nicknames]` or `[passwords]`, and that reference keeps working across replugs,
port moves and (for the fingerprint tier) firmware updates that change the product string.

## Reload

Changing a key and calling `vht config reload` (or letting the service's file watcher debounce) applies
everything that is safe to apply without dropping connections:

* **Hot** - `server.name`, `server.auto_attach_to_kernel`, `policy.*`, `nicknames`, `passwords`,
  `security.auth_shared_secret`, `discovery.*`, `transport.tls.lz4_*`, `auto_attach.*`,
  `general.log_level`, `general.strict`. Nicknames, filters and passwords take effect on the next
  enumeration or attach; DNS-SD re-registration happens on the spot.
* **Restart required** - `general.config_version`, `general.role`, `general.log_file`,
  `general.data_directory`, `server.enabled`, `server.password_max_length`,
  `transport.plaintext.*`, `transport.tls.enabled`, `transport.tls.port`,
  `transport.tls.certificate`, `security.plaintext_password_protected_devices`,
  `discovery.service_type`. A listener cannot be moved while clients are attached, and the service
  account's log destination is fixed at start-up.

The reload path never applies a half-broken file. The classification is computed by comparing the
flattened value tables before and after, so `vht config reload` prints the live changes and then says
`RESTART REQUIRED for: transport.tls.port` instead of pretending the port moved. Which keys require a
restart is not a separate list that can drift: it is the `RequiresRestart` column of the schema table in
`src/Vht.Config/Model/ConfigSchema.cs`, the same table that produces the key list above.

## Schema, validation and migration

* The schema is one table (`ConfigSchema.All`) and is the single source of truth for which keys exist,
  their type and default, and whether a change needs a restart. A unit test asserts that every schema
  key appears in this document, so this file cannot silently drift from the code.
* Unknown keys are warnings by default (`general.strict = true` promotes them to errors). Machine-managed
  paths (`identity.*`, `auto_attach.grants#...`) are never reported as unknown, because they legitimately
  live in `state.toml` and are read by their own components.
* `config_version` is the migration hook. `0` (or a file with only the reference product's legacy single-level
  keys and no version) is migrated to version 1: `ServerName` -> `server.name`, `TCPPort` ->
  `transport.plaintext.port`, `SSLPort` -> `transport.tls.port`, `AllowedDevices` -> `policy.allow`,
  `IgnoredDevices` -> `policy.ignore`, `CompressionLimit` -> `transport.tls.lz4_threshold_bytes`,
  `AutoAttachToKernel` -> `server.auto_attach_to_kernel`, `AutoFind` -> `discovery.enabled`,
  `MdnsServiceName` -> `discovery.instance_name`, `LogLevel` -> `general.log_level`,
  `LogFile` -> `general.log_file`, `Password` -> `security.auth_shared_secret`; values are coerced to
  the documented type and each rename emits a warning naming the old and new key. A file with a *newer*
  version is refused with an actionable message - never parsed on a best-effort basis.
* A malformed file never crashes a load: TOML syntax errors, wrong types, out-of-range numbers,
  unparsable selectors and unparsable manual server entries all become diagnostics carrying file, line
  and column, and the process exits non-zero (or refuses the reload) after printing them. For example:

  ```
  config.toml(4,8): error: transport.tls.port must be an integer, found 'not a port'
  config.toml(7,1): error: 70000 is outside the accepted range 1..65535
  ```

* `state.toml` is written atomically (temporary file plus replace) and is machine-managed: hand edits to
  it are overwritten on the next save. Put settings in `config.toml` instead.

## Testing the discovery path

`DnsServiceRegister`/`DnsServiceBrowse` are exercised by `tests/Vht.Config.Tests/DiscoveryTests.cs`. The
live self-test is opt-in because it needs a usable network interface:

```
VHT_DISCOVERY_SELFTEST=1 dotnet test tests/Vht.Config.Tests
```

It registers `_vht._tcp` for this host, browses for it again and asserts the whole join: instance name,
SRV port, the `name`/`tls`/`caps`/`v` TXT keys, and that `ResolveAsync` finds the same instance. That is
the strongest evidence a single host can produce. Two-machine discovery is verified by running the server
on one machine and `vht discover` on another; manual `host:port` entry always remains available as the
fallback, including when DNSAPI is missing entirely (the reason is then reported, not thrown).

### The DNSAPI contract, as measured

The bindings in `src/Vht.Config/Interop/WindowsDnsSd.cs` encode facts that are not obvious from the
documentation and that each cost a live failure to find. They are asserted by the self-test above; do not
"tidy" them:

| Behaviour | Detail |
| --- | --- |
| Struct layout | `DNS_SERVICE_REGISTER_REQUEST` = `Version, InterfaceIndex, pServiceInstance, pRegisterCompletionCallback, pQueryContext, hCredentials, unicastEnabled`; `DNS_SERVICE_BROWSE_REQUEST` = `Version, InterfaceIndex, QueryName, pBrowseCallback, pQueryContext`. Any other order makes the runtime read a null slot as a pointer and return DNS_STATUS 87. |
| Success code | Both calls return `DNS_REQUEST_PENDING` (9506), not 0 - they are asynchronous. 0 is also accepted. |
| Required parameters | `pRegisterCompletionCallback` must be non-null (register) and `pCancel` must be non-null (browse). Passing null to either returns 87 instead of starting the operation. Register tolerates a null cancel; browse does not. |
| Browse callback payload | A `DNS_RECORD` list (`pNext` chain), not a `DNS_SERVICE_INSTANCE`. The caller frees it with `DnsRecordListFree(record, DnsFreeRecordList)`. Registration is the only path that uses `DnsServiceConstructInstance`/`DnsServiceFreeInstance`. |
| Record offsets (64-bit) | `DNS_RECORDW`: `pNext` +0, `pName` +8, `wType` +16, `wDataLength` +18, `Flags` +20, `dwTtl` +24, `dwReserved` +28, `Data` +32. |
| TXT | `DNS_TXT_DATAW` is `{ DWORD dwStringCount; PWSTR pStringArray[1]; }` - the string array is **inline**, so string *i* is the pointer at `Data+8+i*sizeof(PWSTR)`. Treating `Data+8` as an array base dereferences a string pointer as a pointer and faults. |
| SRV | `DNS_SRV_DATAW` is `{ PWSTR pNameTarget; WORD wPriority; WORD wWeight; WORD wPort; WORD Pad; }` - the port is the **third** WORD (`Data+12`); `Data+8` is `wPriority`, normally 0. |
| Names | The registered instance is `NAME._vht._tcp.local` and the browse query is `_vht._tcp.local`. A name without `.local` registers but is never discoverable. The host name must be a name, not an address literal. |

There is no "cancel browse" entry point, so `WindowsDnsSdProvider` keeps one long-lived browse per
service type: the first `BrowseAsync` starts it and later calls return a snapshot of what has accumulated.
The callback, its GCHandle and the cancel buffer stay rooted on the provider until it is disposed, because
DNSAPI can call the callback after `BrowseAsync` returns.
