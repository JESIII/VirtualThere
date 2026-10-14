#!/usr/bin/env bash
# CI guard: every package in the build graph is on the licence allow-list, and every allow-list
# entry is documented in THIRD_PARTY_NOTICES.md. This is the mechanism ARCHITECTURE.md risk 12
# ("dependency licence drift") asks for: a new package cannot enter the build silently.
#
# Usage: scripts/check-third-party.sh          (from the repository root, after 'dotnet restore')

set -uo pipefail

fail=0
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

allow_list="third_party/notices/allowed-packages.txt"
notices="THIRD_PARTY_NOTICES.md"

if [ ! -f "$allow_list" ]; then
    echo "check-third-party: FAILED - $allow_list is missing"
    exit 1
fi

echo "== packages resolved by the build =="
resolved=$(dotnet list VirtualThere.slnx package --include-transitive 2>/dev/null \
    | sed -n 's/^ *> *\([A-Za-z0-9][A-Za-z0-9._-]*\) *\([0-9][^ ]*\).*/\1/p' \
    | tr 'A-Z' 'a-z' | sort -u)

if [ -z "$resolved" ]; then
    echo "check-third-party: FAILED - 'dotnet list package' returned nothing (restore first?)"
    exit 1
fi

allowed=$(sed -e 's/#.*//' -e '/^[[:space:]]*$/d' "$allow_list" | awk '{print tolower($1)}' | sort -u)

echo "== unexpected packages =="
unexpected=0
for p in $resolved; do
    if ! printf '%s\n' "$allowed" | grep -qx "$p"; then
        echo "  FAIL: $p is in the build graph but not in $allow_list"
        unexpected=1
        fail=1
    fi
done
[ "$unexpected" -eq 0 ] && echo "  ok: all $(printf '%s\n' $resolved | grep -c .) resolved package(s) are allow-listed"

echo "== allow-list entries missing from $notices =="
missing=0
for p in $allowed; do
    if ! grep -qi -- "$p" "$notices"; then
        echo "  FAIL: $p is allow-listed but not documented in $notices"
        missing=1
        fail=1
    fi
done
[ "$missing" -eq 0 ] && echo "  ok: every allow-listed package is documented"

echo "== copyleft check on the allow-list itself =="
# Strip comments first: the header legitimately *names* the forbidden licences.
if sed -e 's/#.*//' -e '/^[[:space:]]*$/d' "$allow_list" | grep -qiE '\b(GPL|LGPL|AGPL|CPOL|SSPL|CDDL|EUPL)\b'; then
    echo "  FAIL: $allow_list lists a package under a copyleft or non-OSI licence"
    fail=1
else
    echo "  ok: no package on the allow-list carries a copyleft licence"
fi

if [ "$fail" -ne 0 ]; then
    echo "check-third-party: FAILED"
    exit 1
fi
echo "check-third-party: PASSED"
