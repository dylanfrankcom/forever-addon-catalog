#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Pushes the checked-out `checker` branch from the current folder (the workflows' `data` worktree). The discover, check
# and reports workflows each write their own files there but don't share a concurrency group, so a push can be refused
# because another one landed first: then it rebases onto the new tip and tries again, at most three times.
set -euo pipefail
for attempt in 1 2 3; do
  if git push origin checker; then
    exit 0
  fi
  if [ "$attempt" -eq 3 ]; then
    echo "The checker branch moved three times while pushing: giving up." >&2
    exit 1
  fi
  echo "The checker branch moved: rebasing onto it and trying again." >&2
  git pull --rebase --no-tags -q origin checker
done
