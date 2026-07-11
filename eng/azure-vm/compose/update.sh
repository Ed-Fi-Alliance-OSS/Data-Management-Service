#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Update the environment in place: pull the latest repo config and container
# images, then recreate changed containers. Databases and the Keycloak realm are
# preserved (volumes are not touched).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

if git -C . rev-parse --git-dir >/dev/null 2>&1; then
  echo "Pulling latest repo config..."
  git pull --ff-only || echo "WARNING: git pull skipped (local changes or detached HEAD)."
fi

echo "Pulling latest images..."
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env pull

echo "Recreating changed containers..."
# Route through up.sh so the ApiSchema-staged guard applies: a full-stack `up -d` here (gateway
# depends_on pulls the DMS services) would otherwise crash-loop st-dms/mt-dms against an empty
# /app/ApiSchema on an environment that was provisioned but never had its schema staged.
./up.sh

echo "Update complete."
