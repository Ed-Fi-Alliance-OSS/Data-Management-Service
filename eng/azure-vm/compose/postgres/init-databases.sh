#!/bin/sh
# SPDX-License-Identifier: Apache-2.0
#
# Runs once on first PostgreSQL startup (empty data volume). Creates the empty
# per-stack databases. The DMS *data* DBs use the relational backend and are
# provisioned OUT OF BAND by the dms-schema tool (the DMS services run with
# DeployDatabaseOnStartup=false); the CMS DBs deploy their schema on startup.
#
#   edfi_st         single-tenant DMS data
#   edfi_st_config  single-tenant Config Service
#   edfi_mt         multi-tenant tenant1 DMS data
#   edfi_mt_config  multi-tenant Config Service
#   edfi_mt_t2      multi-tenant tenant2 DMS data (physically isolated from tenant1)
#   edfi_mt_t1      created but currently unused (reserved)
set -e

for db in edfi_st edfi_st_config edfi_mt edfi_mt_config edfi_mt_t1 edfi_mt_t2; do
  echo "Creating database ${db}..."
  psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER}" -c "CREATE DATABASE ${db};"
done

echo "Database initialization complete."
