#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Data reset: drop the app + database volumes and restart infra + CMS + gateway only, while
# KEEPING the Keycloak realm/clients (keycloak.yml is not passed to `down -v`, so its volume is
# preserved). The DMS services and the relational schema are NOT recreated here -- like a first
# stand-up they come last, after bootstrap + schema. This script prints those remaining steps.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

# Guard the destructive `down -v`: it wipes every app + database volume (edfi_st/mt/... data is
# permanently lost). Require an explicit confirmation so a stray ./reset.sh cannot nuke data --
# type 'reset' at the interactive prompt, or pass -y/--force (e.g. for automation).
FORCE=false
case "${1:-}" in -y | --yes | --force) FORCE=true ;; esac
if [ "$FORCE" != true ]; then
  if [ -t 0 ]; then
    read -r -p "This DROPS all app + database volumes (data lost; Keycloak realm kept). Type 'reset' to continue: " ans
    [ "$ans" = "reset" ] || { echo "Aborted."; exit 1; }
  else
    echo "ERROR: refusing to drop all app + database volumes non-interactively. Re-run with -y/--force."; exit 1
  fi
fi

echo "Dropping app + database volumes (Keycloak realm is preserved)..."
docker compose -f docker-compose.yml --env-file .env down -v

# The config DB was just dropped, so bootstrap must run again: remove the completion sentinel
# so a setup-env.ps1 re-run re-bootstraps (matching the manual step printed below).
rm -f .bootstrap/bootstrap-complete

# Restart infra + CMS + gateway ONLY -- NOT st-dms/mt-dms. After a -v reset the data DBs are empty
# and the relational schema is gone, so the DMS services would crash-loop on boot (they fail fast
# against an unprovisioned data DB / a CMS with no data stores). They must come up LAST, after
# bootstrap + schema, exactly like a first stand-up. --no-deps stops the gateway (which depends_on
# the DMS services) from pulling them up early; it resolves upstreams at request time.
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d --no-deps \
  postgres keycloak st-config mt-config pgadmin gateway

echo
echo "Reset complete. The Keycloak realm + clients survived; finish the rebuild in order:"
echo "  1. Re-create CMS clients/tenants/data stores (Keycloak is already set up). Wait until"
echo "     https://localhost/st-config/health and /mt-config/health return 200 (the CMS"
echo "     containers were just recreated), then:"
echo "         pwsh ./bootstrap/bootstrap.ps1 -SkipKeycloak -BaseUrl https://localhost -Insecure"
echo "  2. Provision the relational schema into edfi_st / edfi_mt / edfi_mt_t2"
echo "     (api-schema-tools; see docs/infrastructure.md \"Provisioning method\")."
echo "  3. Start the DMS services:  ./up.sh st-dms mt-dms"
