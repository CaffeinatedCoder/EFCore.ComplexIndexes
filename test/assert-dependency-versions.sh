#!/usr/bin/env bash
# Reads a deps.json and fails unless every EF Core package in it is at the expected version, and
# Npgsql EF too when one is given (#133).
#
#   test/assert-dependency-versions.sh <deps.json> <ef-core-version> [<npgsql-ef-version>]
#
# The deps.json is what the host loads, so it is the evidence of which patch a run tested. A
# property that did not reach the restore (a typo, a no-restore build, a cached assets file) leaves
# the floor in place, and every test still passes. Every Microsoft.EntityFrameworkCore* entry is
# checked, not a list of names, so a package that stayed behind is reported whichever it is.
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
    echo "usage: $0 <deps.json> <ef-core-version> [<npgsql-ef-version>]" >&2
    exit 2
fi

deps="$1" ef="$2" npgsql="${3:-}"
if [[ ! -f "$deps" ]]; then
    echo "FAIL: no deps.json at $deps" >&2
    exit 1
fi

# "Name/Version" keys of every package library the host resolves.
libraries="$(jq -r '.libraries | to_entries[] | select(.value.type == "package") | .key' "$deps")"

failed=0
check() {
    local pattern="$1" expected="$2" found=0
    while IFS=/ read -r name version; do
        [[ "$name" =~ $pattern ]] || continue
        found=1
        if [[ "$version" == "$expected" ]]; then
            echo "  ok: $name $version"
        else
            echo "  FAIL: $name is $version, expected $expected"
            failed=1
        fi
    done <<<"$libraries"
    if [[ "$found" == 0 ]]; then
        echo "  FAIL: no package matching $pattern in $(basename "$deps")"
        failed=1
    fi
}

echo "==> $(basename "$deps")"
check '^Microsoft\.EntityFrameworkCore(\..+)?$' "$ef"
if [[ -n "$npgsql" ]]; then
    check '^Npgsql\.EntityFrameworkCore\.PostgreSQL$' "$npgsql"
fi
exit "$failed"
