#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Start the full security-review environment (both stacks + Keycloak + gateway).
# Extra args are passed through to `docker compose up` (e.g. a single service name).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

[ -f .env ] || { echo "ERROR: .env not found. Copy .env.example to .env and customize."; exit 1; }

# External network shared by docker-compose.yml and keycloak.yml.
docker network inspect dms-sec >/dev/null 2>&1 || docker network create dms-sec

# Self-signed cert for local use if none present (replace with Let's Encrypt on the VM).
if [ ! -f ssl/server.crt ]; then
  PUBLIC_HOST="$(grep -E '^PUBLIC_HOST=' .env | cut -d= -f2- || true)"
  echo "No TLS cert found; generating a self-signed cert for CN=${PUBLIC_HOST:-localhost}..."
  ./ssl/generate-certificate.sh "${PUBLIC_HOST:-localhost}"
fi

docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d "$@"
echo "Started. Next: run ./bootstrap/bootstrap.ps1 to provision clients, tenants, and data stores."
