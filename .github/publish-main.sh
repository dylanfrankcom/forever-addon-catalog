#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Pushes the check workflow's commit (HEAD: the generated catalog, on top of the main it was built from) to main, at
# $PUSH_URL. A run takes a while, so main may have moved meanwhile, and the push is refused. Then it fetches main and:
# - when the new commits don't touch what the build read (BUILD_INPUTS), rebases its commit onto them and pushes again;
# - when they do, or the rebase conflicts (another run published, say), it publishes nothing and says so: the next run
#   builds from the new main. A conflict in the generated files is never resolved.
# It fails the run only when it can't reach main at all. Tested by .github/tests/publish-main.test.sh.
set -u

url=${PUSH_URL:?PUSH_URL is required}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
inputs=(${BUILD_INPUTS:-overrides optout.json confirmed.json schema scripts})
git_() { git -c http.https://github.com/.extraheader= "$@"; }
note() { echo "$1"; echo "$1" >> "$summary"; }

built=$(git rev-parse HEAD~1)
for attempt in 1 2 3; do
  if git_ push -q "$url" HEAD:main; then
    note "Published to main."
    exit 0
  fi
  if ! git_ fetch -q --no-tags "$url" main; then
    echo "::error::The push to main was refused and main couldn't be fetched."
    exit 1
  fi
  if ! git merge-base --is-ancestor "$built" FETCH_HEAD; then
    note "main no longer contains the commit this run built from: nothing published; the next run builds from main."
    exit 0
  fi
  changed=$(git diff --name-only "$built" FETCH_HEAD -- "${inputs[@]}")
  if [ -n "$changed" ]; then
    note "main changed what this run built from ($(echo $changed | tr ' ' ',')): nothing published; the next run builds from it."
    exit 0
  fi
  if ! git rebase -q FETCH_HEAD >/dev/null 2>&1; then
    git rebase --abort >/dev/null 2>&1
    note "main changed the generated files too (another run published?): nothing published; the next run builds from main."
    exit 0
  fi
  built=$(git rev-parse HEAD~1)
done
note "main kept moving: nothing published; the next run tries again."
exit 0
