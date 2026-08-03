#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Clone the DATA of an already-seeded DMS data database into one or more target data
# databases that have ALREADY been provisioned with the SAME relational schema (same
# image / same dms.EffectiveSchema hash).
#
# This is how the multi-tenant tenants were populated in this environment. NOTE: the MT XSD
# metadata bug that originally forced this (DMS-1230) is now fixed upstream (:pre >= 2026-06-24),
# so the BulkLoadClient CAN seed /mt-dms directly. This clone path is kept as a FASTER
# alternative -- copying the already-seeded single-tenant data beats re-bulk-loading each tenant.
#
# It copies document/descriptor data only (data-only, triggers disabled) and EXCLUDES the
# schema-fingerprint / provisioning tables (EffectiveSchema, SchemaComponent, ResourceKey),
# which already exist (identical) in the provisioned target.
#
# PREREQ: target DB(s) provisioned with api-schema-tools (same ApiSchema as the source) and empty
# of documents; source DB already seeded (e.g. edfi_st via API bulk-load or grandbend.sh).
#
# Usage (from compose/):
#   ./seed/clone-data.sh edfi_st edfi_mt edfi_mt_t2   # SOURCE then TARGET(s)
#   ./seed/clone-data.sh                              # default: edfi_st -> edfi_mt edfi_mt_t2
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."   # -> compose/

[ -f .env ] || { echo "ERROR: .env not found."; exit 1; }
# `|| true`: under `set -o pipefail` a no-match grep exits 1 and would abort the assignment
# before the caller's `${VAR:-default}` fallback can apply. Swallow it so absent keys fall back.
# Also strip matching surrounding quotes to parse .env values the way docker compose does.
val() {
  v="$(grep -E "^$1=" .env | tail -1 | cut -d= -f2- || true)"
  case "$v" in \"*\") v="${v#\"}"; v="${v%\"}" ;; \'*\') v="${v#\'}"; v="${v%\'}" ;; esac
  printf '%s' "$v"
}
PG_CONTAINER="${PG_CONTAINER:-dms-sec-postgres}"
PG_USER="$(val POSTGRES_USER)"; PG_USER="${PG_USER:-postgres}"

SOURCE="${1:-edfi_st}"
[ $# -gt 0 ] && shift
TARGETS=("$@"); [ ${#TARGETS[@]} -eq 0 ] && TARGETS=(edfi_mt edfi_mt_t2)

echo "Source: $SOURCE  ->  Targets: ${TARGETS[*]}"

# Dump the source to a file inside the container FIRST, and only proceed if pg_dump succeeds.
# Do NOT pipe pg_dump straight into psql: a mid-dump pg_dump failure just closes the pipe, which
# psql cannot distinguish from a normal end-of-input, so psql would COMMIT the TRUNCATE plus the
# partial data and pipefail would report the failure only afterwards -- destroying the target.
# Dumping to a file makes a dump failure abort here (set -e), before any target is touched.
DUMP=/tmp/clone-data.$$.sql
trap 'docker exec "$PG_CONTAINER" rm -f "$DUMP" 2>/dev/null || true' EXIT
echo "Dumping $SOURCE ..."
docker exec "$PG_CONTAINER" pg_dump -U "$PG_USER" -d "$SOURCE" --data-only --disable-triggers \
    --exclude-table='dms."ResourceKey"' \
    --exclude-table='dms."EffectiveSchema"' \
    --exclude-table='dms."SchemaComponent"' \
    -f "$DUMP"

for db in "${TARGETS[@]}"; do
  [ "$db" = "$SOURCE" ] && { echo "SKIP $db (== source)"; continue; }
  echo "== Cloning $SOURCE -> $db =="
  # TRUNCATE + restore in ONE transaction: any error (incl. \i failing) rolls back the truncate
  # too, so a failed clone leaves the target's prior data intact instead of empty/half-loaded.
  docker exec -i "$PG_CONTAINER" psql -v ON_ERROR_STOP=1 --single-transaction -U "$PG_USER" -d "$db" -q <<SQL
TRUNCATE dms."Document", dms."Descriptor", dms."ReferentialIdentity", dms."DocumentCache" CASCADE;
\i $DUMP
SQL
  echo "   done."
done
echo "Clone complete. Restart the multi-tenant DMS to clear its data-store cache if enabled."
