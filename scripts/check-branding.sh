#!/usr/bin/env bash
# CI guard: no foreign branding, no licence machinery.
#
# Two independent checks, because the two failure modes need different evidence:
#
#   1. TEXT: the reference product's name may appear only in the files that are *supposed* to name
#      it - the recon record and the legal/attribution documents. Anywhere else (source, UI, build
#      scripts, docs, configuration) it is a build failure.
#   2. BINARY: the shipped assemblies are scanned for licence-gate vocabulary. Comments cannot
#      produce a hit here, so a match means a real string or identifier shipped - which is exactly
#      the class of thing this project must never have.
#
# Usage: scripts/check-branding.sh          (from the repository root)

set -uo pipefail

fail=0
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

# Files allowed to name the third-party product: the recon record (which documents what it does and
# what its licence model is) and the legal/attribution documents (the project brief is the origin
# statement of that same recon effort).
allowed_re='^(README\.md|LEGAL\.md|NOTICE|THIRD_PARTY_NOTICES\.md|IDEA\.md|docs/RECON_AND_SPEC\.md)$'
# This script itself is excluded: it names the product in the pattern it searches for. The packaging
# verifier (tools/Xtask/VerifyCommand.cs) is excluded for exactly the same reason: the name is one of
# the forbidden tokens it scans a built MSI for, so it has to be spelled out there.
self_re='^scripts/check-branding\.sh$|^tools/Xtask/VerifyCommand\.cs$'

echo "== 1. foreign branding in text files =="
hits=$(grep -rIl -i -e 'virtualhere' . \
        --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj \
        --exclude-dir=unpacked --exclude-dir=GhidraProj --exclude-dir=dist 2>/dev/null | sed 's|^\./||' | sort -u)

unexpected=0
for f in $hits; do
    if printf '%s\n' "$f" | grep -qE "$self_re"; then
        continue
    fi
    if ! printf '%s\n' "$f" | grep -qE "$allowed_re"; then
        echo "  FAIL: $f names the reference product"
        unexpected=1
        fail=1
    fi
done

if [ "$unexpected" -eq 0 ]; then
    echo "  ok: $(printf '%s\n' $hits | grep -c . ) allowed file(s) name it, nothing else does"
    for f in $hits; do echo "    allowed: $f"; done
fi

echo "== 2. licence-gate vocabulary in shipped assemblies =="
vocab='max_devices|MAX_DEVICES_EXCEEDED|MAX DEVICES EXCEEDED|unlicensed|purchase a licen[cs]e|licen[cs]e key|activation key|trial expired'
bins=$(find src -type f \( -name '*.dll' -o -name '*.exe' \) \
        -path '*/bin/*' -not -path '*/Tests/*' 2>/dev/null)
if [ -z "$bins" ]; then
    echo "  SKIP: no build output found (build first, or run 'dotnet build' in CI before this step)"
else
    # shellcheck disable=SC2086
    found=$(grep -lEi "$vocab" $bins 2>/dev/null)
    if [ -n "$found" ]; then
        echo "  FAIL: licence vocabulary found in shipped output:"
        printf '    %s\n' $found
        fail=1
    else
        echo "  ok: no licence-gate vocabulary in $(printf '%s\n' $bins | grep -c .) shipped assembly/binaries"
    fi
fi

if [ "$fail" -ne 0 ]; then
    echo "check-branding: FAILED"
    exit 1
fi
echo "check-branding: PASSED"
