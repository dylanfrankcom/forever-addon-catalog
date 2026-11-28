#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Prints a short-lived Claude API access token for this workflow run, from Workload Identity Federation: GitHub's OIDC
# token for this run (audience https://api.anthropic.com) exchanged at https://api.anthropic.com/v1/oauth/token. No API
# key is stored anywhere. The job needs `id-token: write`, and the repository variables ANTHROPIC_FEDERATION_RULE_ID and
# ANTHROPIC_ORGANIZATION_ID (optionally ANTHROPIC_SERVICE_ACCOUNT_ID and ANTHROPIC_WORKSPACE_ID) passed in as env. The
# federation rule in the Claude Console decides which repository, branch and workflow it trusts.
#
# The token is masked in the log and printed only on stdout. Exit code 1, with the reason on stderr, when anything is
# missing or refused: the caller carries on without Claude.
set -euo pipefail
for name in ACTIONS_ID_TOKEN_REQUEST_URL ACTIONS_ID_TOKEN_REQUEST_TOKEN ANTHROPIC_FEDERATION_RULE_ID ANTHROPIC_ORGANIZATION_ID; do
  if [ -z "${!name:-}" ]; then
    echo "No $name: no Claude API token." >&2
    exit 1
  fi
done
dir=$(mktemp -d "${RUNNER_TEMP:-/tmp}/claude-token.XXXXXX")
trap 'rm -rf "$dir"' EXIT
curl -sS --fail --max-time 30 -H "Authorization: Bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
  "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=https://api.anthropic.com" | jq -j '.value // empty' > "$dir/jwt"
if [ ! -s "$dir/jwt" ]; then
  echo "GitHub gave no OIDC token (does the job have id-token: write?)." >&2
  exit 1
fi
jq -n --rawfile jwt "$dir/jwt" '{
    grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer",
    assertion: $jwt,
    federation_rule_id: env.ANTHROPIC_FEDERATION_RULE_ID,
    organization_id: env.ANTHROPIC_ORGANIZATION_ID
  }
  + (if (env.ANTHROPIC_SERVICE_ACCOUNT_ID // "") != "" then { service_account_id: env.ANTHROPIC_SERVICE_ACCOUNT_ID } else {} end)
  + (if (env.ANTHROPIC_WORKSPACE_ID // "") != "" then { workspace_id: env.ANTHROPIC_WORKSPACE_ID } else {} end)' > "$dir/exchange.json"
if ! curl -sS --fail-with-body --max-time 30 https://api.anthropic.com/v1/oauth/token \
    -H "content-type: application/json" --data-binary "@$dir/exchange.json" > "$dir/answer.json"; then
  echo "The token exchange was refused (the Console's authentication history says why)." >&2
  exit 1
fi
token=$(jq -j '.access_token // empty' "$dir/answer.json")
if [ -z "$token" ]; then
  echo "The token exchange gave no token." >&2
  exit 1
fi
echo "::add-mask::$token" >&2
printf '%s' "$token"
