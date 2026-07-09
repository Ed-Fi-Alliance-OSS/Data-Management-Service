#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Start the security-review environment. With NO args this starts the FULL stack (both DMS+CMS
# stacks + Keycloak + gateway) -- correct for a restart/update once bootstrap + the relational
# schema already exist. For a FIRST-TIME stand-up the DMS services must start AFTER bootstrap +
# schema, so use provision/setup-env.ps1 (or pass an explicit subset:
#   ./up.sh postgres keycloak st-config mt-config pgadmin gateway   # then bootstrap + schema
#   ./up.sh st-dms mt-dms                                           # finally the DMS services
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

# The DMS services bind-mount ./.bootstrap/ApiSchema at /app/ApiSchema and read the API schema
# exclusively from there. Docker auto-creates the directory empty if it was never staged, and a
# DMS booted against an empty schema directory fails startup/health -- refuse before that happens.
starts_dms=false
if [ "$#" -eq 0 ]; then
  starts_dms=true   # no args -> full stack, DMS included
else
  for arg in "$@"; do
    case "$arg" in
      st-dms | mt-dms) starts_dms=true ;;
      # A compose option makes the started set ambiguous (`./up.sh --wait` starts everything,
      # and option values look like service names) -- be conservative and require the schema.
      -*) starts_dms=true ;;
    esac
  done
fi
if [ "$starts_dms" = true ] && ! find .bootstrap/ApiSchema -type f -name '*.json' 2>/dev/null | grep -q .; then
  echo "ERROR: .bootstrap/ApiSchema is missing or has no staged schema files, so the DMS services cannot start."
  echo "Stage it first: eng/docker-compose/prepare-dms-schema.ps1 writes eng/docker-compose/.bootstrap/ApiSchema;"
  echo "copy that folder to compose/.bootstrap/ApiSchema. See ../docs/infrastructure.md 'Provisioning method'."
  exit 1
fi

docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d "$@"
echo "Started. For first-time stand-up order (bootstrap + schema before the DMS services), see provision/README.md."
