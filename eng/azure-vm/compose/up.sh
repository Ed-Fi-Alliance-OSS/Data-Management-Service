#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Start the security-review environment. With NO args this starts the FULL stack (both DMS+CMS
# stacks + Keycloak + gateway) in dependency order -- correct for a restart/update once bootstrap +
# the relational schema already exist. For a FIRST-TIME stand-up the DMS services start AFTER bootstrap +
# schema, so use provision/setup-env.ps1 (or pass an explicit subset -- note --no-deps, without
# which `gateway` pulls the DMS services up via depends_on):
#   ./up.sh --no-deps postgres keycloak st-config mt-config pgadmin gateway   # then bootstrap + schema
#   ./up.sh st-dms mt-dms                                                      # finally the DMS services
# Extra args are passed through to `docker compose up` (e.g. a single service name).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

[ -f .env ] || { echo "ERROR: .env not found. Copy .env.example to .env and customize."; exit 1; }

# Refuse to deploy the .env.example placeholder secrets: they satisfy the complexity checks,
# so nothing downstream would reject them, and 443 is Internet-open on the VM.
if grep -qE '=.*CHANGEME' .env; then
  echo "ERROR: .env still contains CHANGEME placeholder secrets. Run ../provision/setup-env.ps1"
  echo "(generates strong secrets on first run) or replace every CHANGEME value, then retry."
  exit 1
fi

# External network shared by docker-compose.yml and keycloak.yml.
docker network inspect dms-sec >/dev/null 2>&1 || docker network create dms-sec

# Self-signed cert for local use if none present (replace with Let's Encrypt on the VM).
if [ ! -f ssl/server.crt ]; then
  PUBLIC_HOST="$(grep -E '^PUBLIC_HOST=' .env | cut -d= -f2- || true)"
  echo "No TLS cert found; generating a self-signed cert for CN=${PUBLIC_HOST:-localhost}..."
  ./ssl/generate-certificate.sh "${PUBLIC_HOST:-localhost}"
fi

# A normal restart must not launch DMS while Keycloak/CMS are still booting: DMS eagerly fetches
# its CMS token and data stores and exits fatally on a connection race. Keep this bounded so a real
# dependency failure cannot hang an update/restart forever. mt-config currently returns the exact
# tenant-required 400 below because CMS tenant resolution runs before /health (follow-up bug); a
# future CMS fix makes it return 200, which this temporary readiness gate already accepts.
wait_for_infrastructure() {
  local timeout="${DMS_STARTUP_TIMEOUT_SECONDS:-480}"
  local interval="${DMS_STARTUP_POLL_SECONDS:-5}"
  local deadline st_code mt_response mt_code mt_body mt_ready kc_code

  [[ "$timeout" =~ ^[0-9]+$ ]] || { echo "ERROR: DMS_STARTUP_TIMEOUT_SECONDS must be a non-negative integer." >&2; return 1; }
  [[ "$interval" =~ ^[0-9]+$ ]] || { echo "ERROR: DMS_STARTUP_POLL_SECONDS must be a non-negative integer." >&2; return 1; }
  deadline=$((SECONDS + timeout))

  echo "Waiting for Keycloak + config services to accept requests..."
  while :; do
    st_code="$(curl -s -k --connect-timeout 5 --max-time 10 -o /dev/null -w '%{http_code}' \
      https://localhost/st-config/health || true)"
    kc_code="$(curl -s -k --connect-timeout 5 --max-time 10 -o /dev/null -w '%{http_code}' \
      https://localhost/auth/realms/master || true)"
    mt_response="$(curl -s -k --connect-timeout 5 --max-time 10 -w $'\n%{http_code}' \
      https://localhost/mt-config/health || true)"
    mt_code="${mt_response##*$'\n'}"
    mt_body="${mt_response%$'\n'*}"
    mt_ready=false
    if [ "$mt_code" = "200" ] || {
      [ "$mt_code" = "400" ] &&
        [[ "$mt_body" == *"The 'Tenant' header is required when multi-tenancy is enabled"* ]]
    }; then
      mt_ready=true
    fi

    if [ "$st_code" = "200" ] && [ "$kc_code" = "200" ] && [ "$mt_ready" = true ]; then
      echo "Keycloak + config services ready."
      return 0
    fi
    if [ "$SECONDS" -ge "$deadline" ]; then
      echo "ERROR: Keycloak/config services were not ready within ${timeout}s" >&2
      echo "       (st-config=$st_code, mt-config=$mt_code, keycloak=$kc_code). Check './logs.sh'." >&2
      return 1
    fi
    sleep "$interval"
  done
}

# The DMS services bind-mount ./.bootstrap/ApiSchema at /app/ApiSchema and read the API schema
# exclusively from there. Docker auto-creates the directory empty if it was never staged, and a
# DMS booted against an empty schema directory fails startup/health -- refuse before that happens.
starts_dms=false
if [ "$#" -eq 0 ]; then
  starts_dms=true   # no args -> full stack, DMS included
else
  explicit_dms=false; gateway_named=false; no_deps=false; ambiguous_opt=false
  for arg in "$@"; do
    case "$arg" in
      st-dms | mt-dms) explicit_dms=true ;;
      gateway) gateway_named=true ;;
      --no-deps) no_deps=true ;;
      # Any other option makes the started set ambiguous (`./up.sh --wait` starts everything,
      # and option values can look like service names) -- be conservative and require the schema.
      -*) ambiguous_opt=true ;;
    esac
  done
  # DMS starts if named directly, if an ambiguous option might start the full stack, or if the
  # gateway is requested WITHOUT --no-deps (its depends_on then pulls st-dms/mt-dms up too).
  if [ "$explicit_dms" = true ] || [ "$ambiguous_opt" = true ]; then starts_dms=true; fi
  if [ "$gateway_named" = true ] && [ "$no_deps" != true ]; then starts_dms=true; fi
fi
# `-print -quit` (find exits after the first hit) keeps this SIGPIPE-safe: with a bare
# `find | grep -q`, grep closes the pipe on first match and, under pipefail, find's SIGPIPE
# (exit 141) would make this guard misfire as "schema missing" even when files are present.
if [ "$starts_dms" = true ] && ! find .bootstrap/ApiSchema -type f -name '*.json' -print -quit 2>/dev/null | grep -q .; then
  echo "ERROR: .bootstrap/ApiSchema is missing or has no staged schema files, so the DMS services cannot start."
  echo "Stage it first: eng/docker-compose/prepare-dms-schema.ps1 writes eng/docker-compose/.bootstrap/ApiSchema;"
  echo "copy that folder to compose/.bootstrap/ApiSchema. See ../docs/infrastructure.md 'Provisioning method'."
  exit 1
fi

compose=(docker compose -f docker-compose.yml -f keycloak.yml --env-file .env)
if [ "$#" -eq 0 ]; then
  echo "Stopping DMS services before dependency restart/update..."
  "${compose[@]}" stop st-dms mt-dms
  echo "Starting infrastructure before DMS..."
  "${compose[@]}" up -d --no-deps postgres keycloak st-config mt-config pgadmin gateway
  ./record-keycloak-image.sh
  wait_for_infrastructure
  echo "Starting DMS services..."
  "${compose[@]}" up -d st-dms mt-dms
else
  "${compose[@]}" up -d "$@"
  # Explicit first-time/subset invocations can also create Keycloak. Record it when present, but do
  # not make unrelated service-only starts fail merely because no Keycloak container exists yet.
  if docker inspect dms-sec-keycloak >/dev/null 2>&1; then
    ./record-keycloak-image.sh
  fi
fi
echo "Started. For first-time stand-up order (bootstrap + schema before the DMS services), see ../provision/README.md."
