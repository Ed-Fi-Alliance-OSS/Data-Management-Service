#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Stop the environment. Pass -v to also delete volumes (DROPS the Keycloak realm
# and all databases). For a data-only reset that KEEPS Keycloak, use reset.sh.
# -v is confirmed interactively (same contract as reset.sh, which guards a strictly
# LESS destructive drop); pass -y/--force for automation.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

FORCE=false
ARGS=()
for arg in "$@"; do
  case "$arg" in
    -y | --yes | --force) FORCE=true ;;
    *) ARGS+=("$arg") ;;
  esac
done
wants_volumes=false
for arg in "${ARGS[@]}"; do
  case "$arg" in -v | --volumes) wants_volumes=true ;; esac
done
if [ "$wants_volumes" = true ] && [ "$FORCE" != true ]; then
  if [ -t 0 ]; then
    read -r -p "This DROPS ALL volumes, INCLUDING the Keycloak realm and every database. Type 'down' to continue: " ans
    [ "$ans" = "down" ] || { echo "Aborted."; exit 1; }
  else
    echo "ERROR: refusing to drop all volumes non-interactively. Re-run with -y/--force."; exit 1
  fi
fi

docker compose -f docker-compose.yml -f keycloak.yml --env-file .env down "${ARGS[@]}"

# If volumes were dropped (-v), the config + Keycloak state is gone, so bootstrap must run again.
# Remove both bootstrap markers so a later setup-env.ps1 re-bootstraps instead of trusting a stale
# "complete" marker against empty databases (matches reset.sh).
if [ "$wants_volumes" = true ]; then
  rm -f .bootstrap/bootstrap-attempted .bootstrap/bootstrap-complete
fi
