#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Tests .github/publish-main.sh against throwaway local repos: run with `bash .github/tests/publish-main.test.sh`.
set -u
script=$(cd "$(dirname "$0")/.." && pwd)/publish-main.sh
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0

# A remote whose main has a catalog and an override, and a clone that built a new catalog on top of it. Then, before
# the push, someone else commits $2 to main (nothing when empty).
setup() {
  rm -rf "$work/remote.git" "$work/run" "$work/other"
  git init -q --bare -b main "$work/remote.git"
  git clone -q "$work/remote.git" "$work/other" 2>/dev/null
  (cd "$work/other" && git config user.name t && git config user.email t@t &&
    mkdir -p overrides && echo '{}' > overrides/a.json && echo 'v1' > catalog.json && echo 'readme' > README.md &&
    git add -A && git commit -q -m base && git push -q origin main)
  git clone -q "$work/remote.git" "$work/run"
  (cd "$work/run" && git config user.name t && git config user.email t@t && echo 'v2' > catalog.json && git commit -qam built)
  if [ -n "$1" ]; then
    (cd "$work/other" && echo "$2" > "$1" && git add -A && git commit -q -m other && git push -q origin main)
  fi
}
publish() { (cd "$work/run" && PUSH_URL="$work/remote.git" GITHUB_STEP_SUMMARY="$work/summary" bash "$script" > "$work/out" 2>&1); }
remote_catalog() { git -C "$work/remote.git" show main:catalog.json; }
expect() {
  if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: got '$2', expected '$3'"; cat "$work/out"; failures=$((failures + 1)); fi
}

setup "" ""
publish; expect "main unchanged: published" "$?/$(remote_catalog)" "0/v2"

setup README.md "new readme"
publish; expect "main moved elsewhere: rebased and published, keeping both" "$?/$(remote_catalog)/$(git -C "$work/remote.git" show main:README.md)" "0/v2/new readme"

setup overrides/a.json '{"name":"x"}'
publish; expect "main changed an override: nothing published, run not failed" "$?/$(remote_catalog)" "0/v1"
grep -q "overrides/a.json" "$work/out"; expect "it names what changed" "$?" "0"

setup catalog.json "v9"
publish; expect "main changed the generated files: nothing published, no conflict left" "$?/$(remote_catalog)/$(git -C "$work/run" status --porcelain | wc -l)" "0/v9/0"

setup "" ""
(cd "$work/run" && PUSH_URL="$work/nowhere.git" bash "$script" > "$work/out" 2>&1); expect "main can't be reached: the run fails" "$?" "1"

[ "$failures" -eq 0 ] && echo "publish-main: all passed" || { echo "publish-main: $failures failed"; exit 1; }
