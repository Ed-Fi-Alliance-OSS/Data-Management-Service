#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Data reset: drop the app + database volumes and restart infra + CMS + gateway only, while
# KEEPING the Keycloak realm/clients (keycloak.yml is not passed to `down -v`, so its volume is
# preserved). The DMS services and the relational schema are NOT recreated here -- like a first
# stand-up they come last, after bootstrap + schema. This script prints those remaining steps.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

echo "Dropping app + database volumes (Keycloak realm is preserved)..."
docker compose -f docker-compose.yml --env-file .env down -v

# Restart infra + CMS + gateway ONLY -- NOT st-dms/mt-dms. After a -v reset the data DBs are empty
# and the relational schema is gone, so the DMS services would crash-loop on boot (they fail fast
# against an unprovisioned data DB / a CMS with no data stores). They must come up LAST, after
# bootstrap + schema, exactly like a first stand-up. --no-deps stops the gateway (which depends_on
# the DMS services) from pulling them up early; it resolves upstreams at request time.
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d --no-deps \
  postgres keycloak st-config mt-config pgadmin gateway

echo
echo "Reset complete. The Keycloak realm + clients survived; finish the rebuild in order:"
echo "  1. Re-create CMS clients/tenants/data stores (Keycloak is already set up):"
echo "         pwsh ./bootstrap/bootstrap.ps1 -SkipKeycloak -BaseUrl https://localhost -Insecure"
echo "  2. Provision the relational schema into edfi_st / edfi_mt / edfi_mt_t2"
echo "     (dms-schema; see docs/infrastructure.md \"Provisioning method\")."
echo "  3. Start the DMS services:  ./up.sh st-dms mt-dms"
