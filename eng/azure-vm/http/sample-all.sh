#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Reusable API smoke sampler for an ST + MT (tenant1/tenant2) DMS deployment. Tokens each
# environment and reads a spread of resources, then prints discovery + a keyed query.
# Self-signed cert -> curl -k. Read-only (token + GET); makes no changes.
# Exits nonzero if any token, discovery, or resource read fails (bad HTTP status or non-JSON
# body), so it doubles as a handoff check.
#
# Fill the FQDN + the three key:secret pairs below, or export them. The live values for a
# specific deployment live in that deployment's PRIVATE credentials doc / vault, NOT here.
#
# Usage:
#   FQDN=your-host ST_CREDS='key:secret' T1_CREDS='key:secret' T2_CREDS='key:secret' ./sample-all.sh
set -euo pipefail
FQDN="${FQDN:-your-label.eastus.cloudapp.azure.com}"
REALM="${REALM:-edfi}"
ST_CREDS="${ST_CREDS:-REPLACE_ST_KEY:REPLACE_ST_SECRET}"
T1_CREDS="${T1_CREDS:-REPLACE_TENANT1_KEY:REPLACE_TENANT1_SECRET}"
T2_CREDS="${T2_CREDS:-REPLACE_TENANT2_KEY:REPLACE_TENANT2_SECRET}"

# All three apps authenticate against the shared Keycloak realm. (Don't take the token URL from
# /mt-dms discovery: it wrongly appends /{tenant}/{schoolYear} -- ../docs/infrastructure.md
# issue 11.) The /{st,mt}-dms/oauth/token proxy also works.
KC_TOKEN="https://$FQDN/auth/realms/$REALM/protocol/openid-connect/token"

FAILURES=0

# Token from an OAuth token endpoint; aborts the run on failure because every check
# after a dead token would just cascade.  token <token-url> <key:secret>
token() {
  local tok
  # python stderr silenced: on failure the explicit ERROR below aborts the run, so the
  # JSONDecodeError traceback from an empty/non-JSON response adds nothing.
  tok=$(curl -skf -X POST "$1" -u "$2" -d grant_type=client_credentials \
    | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])' 2>/dev/null) || {
    echo "ERROR: token request failed at $1 (check credentials and that the stack is up)" >&2
    exit 1
  }
  [ -n "$tok" ] || { echo "ERROR: empty access_token from $1" >&2; exit 1; }
  printf '%s' "$tok"
}

# Read a spread of resources using $TOK and base URL $B. Failures (non-200, non-JSON) are
# reported per resource and counted instead of silently printing a blank sample.
sample() {
  local r resp code body row
  for r in localEducationAgencies schools students staffs courses sections \
           studentSchoolAssociations studentSectionAssociations grades gradeLevelDescriptors; do
    if ! resp=$(curl -sk -w '\n%{http_code}' -H "Authorization: Bearer $TOK" "$B/$r?limit=3"); then
      printf '%-30s %s\n' "$r" "ERROR (request failed)"
      FAILURES=$((FAILURES + 1))
      continue
    fi
    code="${resp##*$'\n'}"
    body="${resp%$'\n'*}"
    if [ "$code" = "200" ] && row=$(printf '%s' "$body" | python3 -c \
      'import sys,json;d=json.load(sys.stdin);print(len(d),"rec; first:",(d[0].get("schoolId") or d[0].get("studentUniqueId") or d[0].get("id")) if d else "[]")'); then
      printf '%-30s %s\n' "$r" "$row"
    else
      printf '%-30s %s\n' "$r" "ERROR (HTTP $code or non-JSON body)"
      FAILURES=$((FAILURES + 1))
    fi
  done
}

# Discovery document (pretty-printed, truncated).  discovery <url>
discovery() {
  local body
  if body=$(curl -skf "$1") && printf '%s\n' "$body" | python3 -m json.tool | sed -n '1,20p'; then
    :
  else
    echo "ERROR: discovery failed or returned non-JSON at $1"
    FAILURES=$((FAILURES + 1))
  fi
}

echo "===== single-tenant ====="
TOK=$(token "$KC_TOKEN" "$ST_CREDS"); B="https://$FQDN/st-dms/data/ed-fi"
discovery "https://$FQDN/st-dms/"
sample
echo "-- keyed query: students?studentUniqueId=604821 --"
if ! curl -skf -H "Authorization: Bearer $TOK" "$B/students?studentUniqueId=604821" | python3 -m json.tool; then
  echo "ERROR: keyed query failed"
  FAILURES=$((FAILURES + 1))
fi

echo; echo "===== multi-tenant / tenant1 ====="
TOK=$(token "$KC_TOKEN" "$T1_CREDS"); B="https://$FQDN/mt-dms/tenant1/2025/data/ed-fi"
discovery "https://$FQDN/mt-dms/tenant1/2025/"
sample

echo; echo "===== multi-tenant / tenant2 ====="
TOK=$(token "$KC_TOKEN" "$T2_CREDS"); B="https://$FQDN/mt-dms/tenant2/2025/data/ed-fi"
discovery "https://$FQDN/mt-dms/tenant2/2025/"
sample

echo
if [ "$FAILURES" -gt 0 ]; then
  echo "SMOKE SAMPLE FAILED: $FAILURES check(s) failed."
  exit 1
fi
echo "Smoke sample passed."
