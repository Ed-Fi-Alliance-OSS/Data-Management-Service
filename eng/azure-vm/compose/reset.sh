#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Environment-state reset: drop the app/database AND Keycloak volumes, then restart infra + CMS +
# gateway only. Keycloak must be reset too: review applications are identity-provider clients, and
# preserving them while dropping CMS would leave old API credentials valid against reassigned data-
# store IDs. The DMS services and the relational schema are NOT recreated here -- like a first
# stand-up they come last, after bootstrap + schema. This script prints those remaining steps.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

# Guard the destructive `down -v`: it permanently wipes every app/database volume and the Keycloak
# realm. Require explicit confirmation so a stray ./reset.sh cannot nuke deployment state --
# type 'reset' at the interactive prompt, or pass -y/--force (e.g. for automation).
FORCE=false
case "${1:-}" in -y | --yes | --force) FORCE=true ;; esac
if [ "$FORCE" != true ]; then
  if [ -t 0 ]; then
    read -r -p "This DROPS all app/database volumes AND the Keycloak realm. Type 'reset' to continue: " ans
    [ "$ans" = "reset" ] || { echo "Aborted."; exit 1; }
  else
    echo "ERROR: refusing to drop application, database, and Keycloak state non-interactively. Re-run with -y/--force."; exit 1
  fi
fi

echo "Dropping app/database volumes and the Keycloak realm..."
# Clear the markers BEFORE the destructive attempt: if the down is interrupted after removing some
# volumes, a surviving "complete" marker would make setup-env.ps1 skip bootstrap against wiped
# state. The reset-pending sentinel covers the opposite failure: if the down fails with the
# volumes still INTACT, absent markers alone would let a re-run bootstrap duplicate the still-live
# identity/CMS objects -- bootstrap.ps1 refuses while the sentinel exists, and it is removed only
# after the down succeeds (matches down.sh -v).
mkdir -p .bootstrap
: > .bootstrap/reset-pending
rm -f .bootstrap/bootstrap-attempted .bootstrap/bootstrap-complete
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env down -v
rm -f .bootstrap/reset-pending

# Restart infra + CMS + gateway ONLY -- NOT st-dms/mt-dms. After a -v reset the data DBs are empty
# and the relational schema is gone, so the DMS services would crash-loop on boot (they fail fast
# against an unprovisioned data DB / a CMS with no data stores). They must come up LAST, after
# bootstrap + schema, exactly like a first stand-up. --no-deps stops the gateway (which depends_on
# the DMS services) from pulling them up early; it resolves upstreams at request time.
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d --no-deps \
  postgres keycloak st-config mt-config pgadmin gateway

echo
echo "Reset complete. All prior review credentials were revoked with the Keycloak realm; rebuild in order:"
echo "  1. Re-create the realm/clients plus CMS tenants/data stores. Wait until"
echo "     https://localhost/st-config/health and /mt-config/health return 200 (the CMS"
echo "     containers were just recreated), then:"
echo "         pwsh ./bootstrap/bootstrap.ps1 -BaseUrl https://localhost -Insecure"
echo "  2. Provision the relational schema into edfi_st / edfi_mt / edfi_mt_t2"
echo "     (api-schema-tools; see ../docs/infrastructure.md \"Provisioning method\")."
echo "  3. Start the DMS services:  ./up.sh st-dms mt-dms"
