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
# PREREQ: target DB(s) provisioned with dms-schema (same ApiSchema as the source) and empty
# of documents; source DB already seeded (e.g. edfi_st via API bulk-load or grandbend.sh).
#
# Usage (from compose/):
#   ./seed/clone-data.sh edfi_st edfi_mt edfi_mt_t2   # SOURCE then TARGET(s)
#   ./seed/clone-data.sh                              # default: edfi_st -> edfi_mt edfi_mt_t2
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."   # -> compose/

[ -f .env ] || { echo "ERROR: .env not found."; exit 1; }
val() { grep -E "^$1=" .env | head -1 | cut -d= -f2- ; }
PG_CONTAINER="${PG_CONTAINER:-dms-sec-postgres}"
PG_USER="$(val POSTGRES_USER)"; PG_USER="${PG_USER:-postgres}"

SOURCE="${1:-edfi_st}"
[ $# -gt 0 ] && shift
TARGETS=("$@"); [ ${#TARGETS[@]} -eq 0 ] && TARGETS=(edfi_mt edfi_mt_t2)

echo "Source: $SOURCE  ->  Targets: ${TARGETS[*]}"
for db in "${TARGETS[@]}"; do
  [ "$db" = "$SOURCE" ] && { echo "SKIP $db (== source)"; continue; }
  echo "== Cloning $SOURCE -> $db =="
  # Clear existing documents in the target (keep the provisioned schema + fingerprint).
  docker exec "$PG_CONTAINER" psql -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$db" -c \
    'TRUNCATE dms."Document", dms."Descriptor", dms."ReferentialIdentity", dms."DocumentCache" CASCADE;'
  # Copy data only; exclude provisioning/fingerprint tables already present in the target.
  docker exec "$PG_CONTAINER" pg_dump -U "$PG_USER" -d "$SOURCE" --data-only --disable-triggers \
      --exclude-table='dms."ResourceKey"' \
      --exclude-table='dms."EffectiveSchema"' \
      --exclude-table='dms."SchemaComponent"' \
    | docker exec -i "$PG_CONTAINER" psql -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$db" -q
  echo "   done."
done
echo "Clone complete. Restart the multi-tenant DMS to clear its data-store cache if enabled."
