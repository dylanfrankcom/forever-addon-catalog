#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Is the published catalog built from the current generator inputs? Usage:
#   catalog-current.sh <built.json> [limit-seconds]    # in a full-history checkout of main
#   catalog-current.sh --self-test
# built.json (on the checker branch, written by the check workflow after each successful build) names the main commit
# the catalog was last built from. Exits 1, naming the oldest waiting change, when an input changed after that commit
# more than limit-seconds ago (default 3 hours); 0 otherwise. A missing or unreadable built.json counts as never built.

set -euo pipefail

INPUTS=(scripts/generate.cs scripts/details.cs overrides optout.json confirmed.json tags.json schema)

check() {
  local built_file=$1 limit=${2:-10800} built now waiting oldest when subject
  built=$(sed -n 's/.*"commit" *: *"\([0-9a-f]\{40\}\)".*/\1/p' "$built_file" 2>/dev/null | head -n1 || true)
  now=$(date +%s)
  if [ -n "$built" ] && git cat-file -e "$built^{commit}" 2>/dev/null; then
    waiting=$(git log --reverse --format='%H %ct %s' "$built..HEAD" -- "${INPUTS[@]}")
  else
    waiting=$(git log --reverse --format='%H %ct %s' HEAD -- "${INPUTS[@]}")
    [ -n "$built" ] || echo "No record of a build yet."
  fi
  if [ -z "$waiting" ]; then
    echo "The catalog is built from the current inputs."
    return 0
  fi
  oldest=$(head -n1 <<< "$waiting")
  when=$(cut -d' ' -f2 <<< "$oldest")
  subject=$(cut -d' ' -f3- <<< "$oldest")
  if [ $((now - when)) -le "$limit" ]; then
    echo "An input changed $(( (now - when) / 60 )) minutes ago; the catalog has until $((limit / 3600)) hours to catch up."
    return 0
  fi
  echo "The catalog is stale: \"$subject\" ($(cut -c1-7 <<< "$oldest")) changed its inputs $(( (now - when) / 3600 )) hours ago and no build has included it since. $(wc -l <<< "$waiting") change(s) waiting."
  return 1
}

self_test() {
  local dir failures=0 old new
  dir=$(mktemp -d)
  trap 'rm -rf "$dir"' RETURN
  (
    cd "$dir"
    git init -q -b main .
    git config user.name test; git config user.email test@example.com; git config core.autocrlf false
    old=$(( $(date +%s) - 6 * 3600 )); new=$(( $(date +%s) - 600 ))
    commit() { GIT_AUTHOR_DATE="@$1" GIT_COMMITTER_DATE="@$1" git commit -q --allow-empty -m "$2"; }
    mkdir -p scripts overrides
    echo a > scripts/generate.cs; git add -A; commit "$old" "generator v1"
    built=$(git rev-parse HEAD)
    expect() { if [ "$1" != "$2" ]; then echo "FAIL $3 (exit $1, wanted $2)"; return 1; fi; }
    printf '{"commit":"%s"}' "$built" > "$dir/../built-$$.json"
    rc=0; check "$dir/../built-$$.json" >/dev/null || rc=$?; expect $rc 0 "nothing since the build" || exit 1
    echo b > README.md; git add -A; commit "$old" "docs only"
    rc=0; check "$dir/../built-$$.json" >/dev/null || rc=$?; expect $rc 0 "a change outside the inputs" || exit 1
    echo c > overrides/x.json; git add -A; commit "$old" "override added"
    rc=0; out=$(check "$dir/../built-$$.json") || rc=$?; expect $rc 1 "an old input change since the build" || exit 1
    grep -q '"override added"' <<< "$out" || { echo "FAIL the waiting change is named"; exit 1; }
    printf '{"commit":"%s"}' "$(git rev-parse HEAD)" > "$dir/../built-$$.json"
    echo d > tags.json; git add -A; commit "$new" "tags changed"
    rc=0; check "$dir/../built-$$.json" >/dev/null || rc=$?; expect $rc 0 "a recent input change has time to catch up" || exit 1
    rc=0; check "$dir/../built-$$.json" 60 >/dev/null || rc=$?; expect $rc 1 "past a shorter limit" || exit 1
    rc=0; check "$dir/../no-such.json" >/dev/null || rc=$?; expect $rc 1 "never built" || exit 1
    rm -f "$dir/../built-$$.json"
  ) || failures=1
  if [ $failures -eq 0 ]; then echo "catalog-current.sh self-test passed."; else echo "catalog-current.sh self-test failed."; fi
  return $failures
}

if [ "${1:-}" = "--self-test" ]; then
  self_test
else
  check "$@"
fi
