#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Restore an Ed-Fi "Grand Bend" POPULATED DATABASE TEMPLATE into a DMS data database.
# This is the "quick restore" path: no API bulk-load, no rate limiter, no XSD download
# (fastest seeding, and it avoids the bulk-load rate-limiter tuning entirely).
#
# ⚠️ RELATIONAL BACKEND REQUIRED. This stack runs the DMS *relational* backend
# (per-resource edfi.* tables + a dms.EffectiveSchema fingerprint). Every
# EdFi.Api.Populated.Template.* build postdates the DS52 relational cutover (DMS-1159,
# 2026-06-09) and is relational; the LEGACY DOCUMENT-STORE dumps shipped under the retired
# EdFi.Dms.Populated.Template.* package ids (only dms.Document/Reference/Alias; no edfi.*
# tables, no dms.EffectiveSchema) and are INCOMPATIBLE — restoring one into a relational DB
# fails the EffectiveSchema check. This script GUARDS against that and refuses a
# document-store dump regardless of the package id it came from.
#
# The template's EffectiveSchema must also match the running image's ApiSchema surface
# (Data Standard + extensions); a mismatch makes DMS reject the database at startup.
#
# Restore into a FRESH, EMPTY database (no 'dms' schema yet), BEFORE starting DMS. Do NOT
# pre-provision the target with api-schema-tools for this path — the template creates the schema.
# (The DMS never deploys schema on startup; it validates the restored dms.EffectiveSchema.)
#
# To seed the OTHER data DBs from an ALREADY-seeded one (how multi-tenant was populated
# here), use seed/clone-data.sh instead.
#
# Usage (from compose/):
#   docker network create dms-sec 2>/dev/null || true   # the compose network is external
#   docker compose --env-file .env up -d postgres
#   ./seed/grandbend.sh                    # restore into edfi_st ONLY (single-tenant)
#   ./seed/grandbend.sh edfi_st edfi_mt edfi_mt_t2   # all three data DBs
# NOTE: the no-arg form seeds edfi_st only; the multi-tenant DBs (edfi_mt, edfi_mt_t2) still
# need their own restore (list them above) or a clone-data.sh copy before mt-dms is usable.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."   # -> compose/

[ -f .env ] || { echo "ERROR: .env not found."; exit 1; }
# `|| true`: under `set -o pipefail` a no-match grep exits 1 and would abort the assignment
# before the caller's `${VAR:-default}` fallback can apply. Swallow it so absent keys fall back.
# Also strip matching surrounding quotes: docker compose strips them from .env values, so a
# hand-quoted value must parse identically here or the two consumers diverge.
val() {
  v="$(grep -E "^$1=" .env | tail -1 | cut -d= -f2- || true)"
  case "$v" in \"*\") v="${v#\"}"; v="${v%\"}" ;; \'*\') v="${v#\'}"; v="${v%\'}" ;; esac
  printf '%s' "$v"
}

PG_CONTAINER="${PG_CONTAINER:-dms-sec-postgres}"
PG_USER="$(val POSTGRES_USER)"; PG_USER="${PG_USER:-postgres}"
PKG_ID="$(val DATABASE_TEMPLATE_PACKAGE_ID)"; PKG_ID="${PKG_ID:-EdFi.Api.Populated.Template.PostgreSql.5.2.0}"
PKG_VER="$(val DATABASE_TEMPLATE_PACKAGE_VERSION)"; PKG_VER="${PKG_VER:-latest}"
FEED="https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3"

DBS=("$@"); [ ${#DBS[@]} -eq 0 ] && DBS=(edfi_st)
RESTORED=()
SKIPPED=()
idlower="$(echo "$PKG_ID" | tr '[:upper:]' '[:lower:]')"

if [ "$PKG_VER" = "latest" ]; then
  # The flat2 index's versions array is NOT reliably sorted (observed newest-first on Azure
  # Artifacts), so pick the highest by version sort instead of trusting element order.
  PKG_VER="$(curl -fsSL "$FEED/flat2/$idlower/index.json" \
    | python3 -c 'import json,sys; print("\n".join(json.load(sys.stdin)["versions"]))' \
    | sort -V | tail -1)"
fi

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
echo "Downloading $PKG_ID $PKG_VER ..."
curl -fSL "$FEED/flat2/$idlower/$PKG_VER/$idlower.$PKG_VER.nupkg" -o "$work/tmpl.nupkg"
python3 - "$work/tmpl.nupkg" "$work/pkg" <<'PY'
import sys, zipfile
zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])
PY
# -print -quit (stop at first match) instead of `| head -1`: under pipefail, head exiting
# early SIGPIPEs find (exit 141) and would abort a valid run when the package has several .sql.
sql="$(find "$work/pkg" -iname '*.sql' -print -quit)"
[ -n "$sql" ] || { echo "ERROR: no .sql found in package."; exit 1; }
echo "Template SQL: $(basename "$sql")"

# --- GUARD: refuse the legacy document-store template on a relational backend ----------
# A relational template creates per-resource edfi.* tables and the dms.EffectiveSchema
# fingerprint table; the legacy document-store template has neither.
if ! grep -qiE 'create schema edfi|create table edfi\.' "$sql" ||
   ! grep -qi 'effectiveschema' "$sql"; then
  cat >&2 <<EOF
ERROR: '$PKG_ID' $PKG_VER looks like the LEGACY DOCUMENT-STORE template (no edfi.* tables
       and no dms.EffectiveSchema). This stack runs the RELATIONAL backend, which rejects
       it. Use an EdFi.Api.Populated.Template.* build (the document-store dumps shipped
       under the retired EdFi.Dms.* ids) or seed via API bulk-load + seed/clone-data.sh.
       See ../docs/infrastructure.md.
EOF
  exit 2
fi

docker cp "$sql" "$PG_CONTAINER:/tmp/grandbend.sql"

# Templates own their projection-enqueue trigger functions through the cluster-wide NOLOGIN role
# edfi_dms_enqueue_owner, so the restore fails with 'role "edfi_dms_enqueue_owner" does not exist'
# on a fresh cluster. Ensure it (locked down) first; mirrors Initialize-PostgresqlTemplateRestoreGlobalRole
# in eng/DatabaseTemplates/Template-Management.psm1, which the official restore path runs.
docker exec -i "$PG_CONTAINER" psql -v ON_ERROR_STOP=1 -U "$PG_USER" -d postgres >/dev/null <<'SQL' ||
DO $$
DECLARE
    _owner_role oid := pg_catalog.to_regrole('edfi_dms_enqueue_owner');
BEGIN
    IF _owner_role IS NULL THEN
        CREATE ROLE "edfi_dms_enqueue_owner" WITH NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
    ELSIF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_roles roles
        WHERE roles.oid = _owner_role
          AND (roles.rolcanlogin OR roles.rolinherit OR roles.rolsuper OR roles.rolcreatedb OR roles.rolcreaterole OR roles.rolreplication OR roles.rolbypassrls)
    ) THEN
        RAISE EXCEPTION 'PostgreSQL role edfi_dms_enqueue_owner exists but is not locked down as NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS. Drop or repair the role before restoring template packages.';
    ELSIF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members memberships
        WHERE memberships.member = _owner_role
          AND (memberships.admin_option OR memberships.inherit_option OR memberships.set_option)
    ) THEN
        RAISE EXCEPTION 'PostgreSQL role edfi_dms_enqueue_owner must not hold outgoing privilege-bearing memberships before restoring template packages.';
    END IF;
END
$$;
SQL
  { echo "ERROR: failed to ensure PostgreSQL role 'edfi_dms_enqueue_owner' in $PG_CONTAINER." >&2; exit 1; }

# Every restore is an independent writable data store, so it needs its own source identity rather
# than the one baked into the template (otherwise edfi_st and both tenant DBs report one physical
# source). Mirrors Invoke-RestoredDataStoreIdentitySourceIdentityReseed in Template-Management.psm1.
RESEED_SQL="$(cat <<'SQL'
DO $$
DECLARE
    _updated_count integer;
BEGIN
    UPDATE "dms"."DataStoreIdentity"
    SET "SourceIdentity" = gen_random_uuid()
    WHERE "DataStoreIdentitySingletonId" = 1;

    GET DIAGNOSTICS _updated_count = ROW_COUNT;
    IF _updated_count <> 1 THEN
        RAISE EXCEPTION 'Restored database is missing the dms.DataStoreIdentity singleton row.';
    END IF;
END
$$;
SQL
)"

for db in "${DBS[@]}"; do
  exists="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$db" -tAc "SELECT 1 FROM pg_namespace WHERE nspname='dms'" 2>/dev/null || true)"
  if [ "$exists" = "1" ]; then
    echo "SKIP $db: 'dms' schema already present (reset volumes to reseed)."
    SKIPPED+=("$db")
    continue
  fi
  echo "Restoring Grand Bend (relational) into $db ..."
  # --single-transaction wraps the -f restore and the -c reseed together: an interrupted/failed
  # restore or reseed rolls back entirely, so the DB is left with NO 'dms' schema. That keeps the
  # skip-guard above honest -- a failed attempt is retried, not silently skipped as "already seeded".
  docker exec "$PG_CONTAINER" psql -v ON_ERROR_STOP=1 --single-transaction -U "$PG_USER" -d "$db" \
    -f /tmp/grandbend.sql -c "$RESEED_SQL"
  RESTORED+=("$db")
done
if [ ${#RESTORED[@]} -gt 0 ]; then echo "Restored this run: ${RESTORED[*]}."; fi
if [ ${#SKIPPED[@]} -gt 0 ]; then echo "Skipped this run: ${SKIPPED[*]}."; fi
echo "The DMS never deploys schema on startup. Start only services whose required databases contain"
echo "the intended schema/data: st-dms needs edfi_st; mt-dms needs BOTH edfi_mt and edfi_mt_t2."
