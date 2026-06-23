#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Reusable API smoke sampler for an ST + MT (tenant1/tenant2) DMS deployment. Tokens each
# environment and reads a spread of resources, then prints discovery + a keyed query.
# Self-signed cert -> curl -k. Read-only (token + GET); makes no changes.
#
# Fill the FQDN + the three key:secret pairs below, or export them. The live values for a
# specific deployment live in that deployment's PRIVATE credentials doc / vault, NOT here.
#
# Usage:
#   FQDN=your-host ST_CREDS='key:secret' T1_CREDS='key:secret' T2_CREDS='key:secret' ./sample-all.sh
set -uo pipefail
FQDN="${FQDN:-your-label.eastus.cloudapp.azure.com}"
ST_CREDS="${ST_CREDS:-REPLACE_ST_KEY:REPLACE_ST_SECRET}"
T1_CREDS="${T1_CREDS:-REPLACE_TENANT1_KEY:REPLACE_TENANT1_SECRET}"
T2_CREDS="${T2_CREDS:-REPLACE_TENANT2_KEY:REPLACE_TENANT2_SECRET}"

# Token from a DMS token endpoint.  token <token-url> <key:secret>
token() {
  curl -sk -X POST "$1" -u "$2" -d grant_type=client_credentials \
    | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])'
}

# Read a spread of resources using $TOK and base URL $B.
sample() {
  for r in localEducationAgencies schools students staffs courses sections \
           studentSchoolAssociations studentSectionAssociations grades gradeLevelDescriptors; do
    printf '%-30s %s\n' "$r" "$(curl -sk -H "Authorization: Bearer $TOK" "$B/$r?limit=3" \
      | python3 -c 'import sys,json;d=json.load(sys.stdin);print(len(d),"rec; first:",(d[0].get("schoolId") or d[0].get("studentUniqueId") or d[0].get("id")) if d else "[]")' 2>/dev/null)"
  done
}

echo "===== single-tenant ====="
TOK=$(token "https://$FQDN/st-dms/oauth/token" "$ST_CREDS"); B="https://$FQDN/st-dms/data/ed-fi"
curl -sk "https://$FQDN/st-dms/" | python3 -m json.tool | head -20      # discovery
sample
echo "-- keyed query: students?studentUniqueId=604821 --"
curl -sk -H "Authorization: Bearer $TOK" "$B/students?studentUniqueId=604821" | python3 -m json.tool

echo; echo "===== multi-tenant / tenant1 ====="
TOK=$(token "https://$FQDN/mt-dms/oauth/token" "$T1_CREDS"); B="https://$FQDN/mt-dms/tenant1/2025/data/ed-fi"
curl -sk "https://$FQDN/mt-dms/tenant1/2025/" | python3 -m json.tool | head -20
sample

echo; echo "===== multi-tenant / tenant2 ====="
TOK=$(token "https://$FQDN/mt-dms/oauth/token" "$T2_CREDS"); B="https://$FQDN/mt-dms/tenant2/2025/data/ed-fi"
curl -sk "https://$FQDN/mt-dms/tenant2/2025/" | python3 -m json.tool | head -20
sample
