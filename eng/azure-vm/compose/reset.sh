#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Data reset: drop the app + database volumes and restart with fresh schemas,
# while KEEPING the Keycloak realm/clients (keycloak.yml is not passed to
# `down -v`, so its volume is preserved). Re-run bootstrap afterward.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

echo "Dropping app + database volumes (Keycloak realm is preserved)..."
docker compose -f docker-compose.yml --env-file .env down -v

./up.sh

echo
echo "Reset complete. Re-run bootstrap to recreate clients/tenants/data stores:"
echo "    pwsh ./bootstrap/bootstrap.ps1"
