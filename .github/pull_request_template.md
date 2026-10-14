## What this changes

<!-- One paragraph. What behaviour changed, and why. -->

## How it was verified

<!-- Paste the real command output. "Not run" is acceptable; "works" without evidence is not. -->

```
$ dotnet build VirtualThere.slnx -c Release
$ dotnet test  VirtualThere.slnx -c Release
```

## Checklist

- [ ] `dotnet build VirtualThere.slnx -c Release` - 0 warnings, 0 errors
- [ ] `dotnet test VirtualThere.slnx -c Release` - all green (output pasted above)
- [ ] No reference-product name added to code, UI or build output
      (`bash scripts/check-branding.sh` passes)
- [ ] No licence, device-cap, trial or purchase vocabulary added anywhere
- [ ] New dependency: added to `third_party/notices/allowed-packages.txt` **and**
      `THIRD_PARTY_NOTICES.md` (`bash scripts/check-third-party.sh` passes)
- [ ] No code, string table or protocol detail copied from a product we are not allowed to copy from
      (`docs/RECON_AND_SPEC.md` section 9)
- [ ] Docs updated where behaviour, config keys, ports or limitations changed
- [ ] No secrets, certificates, private keys or reverse-engineering artifacts committed

## Related

<!-- Issue numbers, related cards, follow-ups you deliberately left out of this change. -->
