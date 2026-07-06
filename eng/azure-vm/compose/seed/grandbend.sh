#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Restore an Ed-Fi "Grand Bend" POPULATED DATABASE TEMPLATE into a DMS data database.
# This is the "quick restore" path: no API bulk-load, no rate limiter, no XSD download
# (fastest seeding, and it avoids the bulk-load rate-limiter tuning entirely).
#
# ⚠️ RELATIONAL BACKEND REQUIRED. This stack runs the DMS *relational* backend
# (per-resource edfi.* tables + a dms.EffectiveSchema fingerprint). You MUST use a
# RELATIONAL build of EdFi.Dms.Populated.Template.PostgreSql.5.2.0 — i.e. a package built
# after the DS52 relational cutover (DMS-1159, 2026-06-09). The older builds (e.g. 0.7.x)
# are the LEGACY DOCUMENT-STORE format (only dms.Document/Reference/Alias; no edfi.* tables,
# no dms.EffectiveSchema) and are INCOMPATIBLE — restoring one into a relational DB fails
# the EffectiveSchema check. This script GUARDS against that and refuses a document-store dump.
#
# The template's EffectiveSchema must also match the running image's ApiSchema surface
# (Data Standard + extensions); a mismatch makes DMS reject the database at startup.
#
# Restore into a FRESH, EMPTY database (no 'dms' schema yet), BEFORE starting DMS. Do NOT
# pre-provision the target with dms-schema for this path — the template creates the schema.
# (DMS is pinned to AppSettings__DeployDatabaseOnStartup=false in docker-compose.yml.)
#
# To seed the OTHER data DBs from an ALREADY-seeded one (how multi-tenant was populated
# here), use seed/clone-data.sh instead.
#
# Usage (from compose/):
#   docker compose --env-file .env up -d postgres
#   ./seed/grandbend.sh                    # restore into edfi_st
#   ./seed/grandbend.sh edfi_st edfi_mt    # specific database(s)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."   # -> compose/

[ -f .env ] || { echo "ERROR: .env not found."; exit 1; }
# `|| true`: under `set -o pipefail` a no-match grep exits 1 and would abort the assignment
# before the caller's `${VAR:-default}` fallback can apply. Swallow it so absent keys fall back.
val() { grep -E "^$1=" .env | head -1 | cut -d= -f2- || true ; }

PG_CONTAINER="${PG_CONTAINER:-dms-sec-postgres}"
PG_USER="$(val POSTGRES_USER)"; PG_USER="${PG_USER:-postgres}"
PKG_ID="$(val DATABASE_TEMPLATE_PACKAGE_ID)"; PKG_ID="${PKG_ID:-EdFi.Dms.Populated.Template.PostgreSql.5.2.0}"
PKG_VER="$(val DATABASE_TEMPLATE_PACKAGE_VERSION)"; PKG_VER="${PKG_VER:-latest}"
FEED="https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3"

DBS=("$@"); [ ${#DBS[@]} -eq 0 ] && DBS=(edfi_st)
idlower="$(echo "$PKG_ID" | tr '[:upper:]' '[:lower:]')"

if [ "$PKG_VER" = "latest" ]; then
  PKG_VER="$(curl -fsSL "$FEED/flat2/$idlower/index.json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["versions"][-1])')"
fi

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
echo "Downloading $PKG_ID $PKG_VER ..."
curl -fSL "$FEED/flat2/$idlower/$PKG_VER/$idlower.$PKG_VER.nupkg" -o "$work/tmpl.nupkg"
python3 - "$work/tmpl.nupkg" "$work/pkg" <<'PY'
import sys, zipfile
zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])
PY
sql="$(find "$work/pkg" -iname '*.sql' | head -1)"
[ -n "$sql" ] || { echo "ERROR: no .sql found in package."; exit 1; }
echo "Template SQL: $(basename "$sql")"

# --- GUARD: refuse the legacy document-store template on a relational backend ----------
# A relational template creates per-resource edfi.* tables and the dms.EffectiveSchema
# fingerprint table; the legacy document-store template has neither.
if ! grep -qiE 'create schema edfi|create table edfi\.|effectiveschema' "$sql"; then
  cat >&2 <<EOF
ERROR: '$PKG_ID' $PKG_VER looks like the LEGACY DOCUMENT-STORE template (no edfi.* tables
       and no dms.EffectiveSchema). This stack runs the RELATIONAL backend, which rejects
       it. Use a relational build (post DMS-1159, 2026-06-09) or seed via API bulk-load +
       seed/clone-data.sh. See docs/infrastructure.md.
EOF
  exit 2
fi

docker cp "$sql" "$PG_CONTAINER:/tmp/grandbend.sql"
for db in "${DBS[@]}"; do
  exists="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$db" -tAc "SELECT 1 FROM pg_namespace WHERE nspname='dms'" 2>/dev/null || true)"
  if [ "$exists" = "1" ]; then
    echo "SKIP $db: 'dms' schema already present (reset volumes to reseed)."
    continue
  fi
  echo "Restoring Grand Bend (relational) into $db ..."
  docker exec "$PG_CONTAINER" psql -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$db" -f /tmp/grandbend.sql
done
echo "Done. (DMS is pinned to DeployDatabaseOnStartup=false; start the DMS services now.)"
