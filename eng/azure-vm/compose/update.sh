#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Update the environment in place: pull the latest repo config and container
# images, then recreate changed containers. Databases and the Keycloak realm are
# preserved (volumes are not touched).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

if [ "${SKIP_GIT:-0}" != "1" ] && git -C . rev-parse --git-dir >/dev/null 2>&1; then
  echo "Pulling latest repo config..."
  # Abort on a failed fast-forward: pulling newer :pre images (below) against stale compose/nginx
  # config risks an incompatible mix. Resolve the divergence, or set SKIP_GIT=1 to refresh images
  # only (e.g. when intentionally running local edits to the deployment files).
  if ! git pull --ff-only; then
    echo "ERROR: 'git pull --ff-only' failed (local changes or diverged history). Resolve it, or" >&2
    echo "       re-run with SKIP_GIT=1 to pull images against the current checkout." >&2
    exit 1
  fi
fi

# Keycloak's persisted dev-file H2 database cannot cross image-pin changes (see keycloak.yml).
# If the pull above brought a compose config with a different Keycloak pin, recreating the
# container below would apply it to the unmigratable volume -- refuse and point at the redeploy.
# The deployed reference comes from the container when it exists, else from the reference file
# this script persists (a plain ./down.sh removes the container but keeps the volume). When the
# volume exists and neither source is available, FAIL CLOSED: an unverifiable pin must not be
# applied to live realm state.
configured_keycloak="$(docker compose -f docker-compose.yml -f keycloak.yml --env-file .env config --images | grep -m1 -i 'keycloak' || true)"
keycloak_ref_file=".bootstrap/keycloak-image"
current_keycloak="$(docker inspect --format '{{.Config.Image}}' dms-sec-keycloak 2>/dev/null || true)"
if [ -z "$current_keycloak" ] && [ -f "$keycloak_ref_file" ]; then
  current_keycloak="$(cat "$keycloak_ref_file")"
fi
if docker volume inspect dms-security-review_dms-sec-keycloak >/dev/null 2>&1; then
  if [ -z "$current_keycloak" ]; then
    echo "ERROR: the Keycloak H2 volume exists but its deployed image cannot be determined" >&2
    echo "       (no dms-sec-keycloak container and no $keycloak_ref_file). Refusing to update:" >&2
    echo "       a changed pin would be applied to unmigratable realm state. Verify the configured" >&2
    echo "       pin matches the volume's Keycloak version, or use provision/REDEPLOY.md." >&2
    exit 1
  fi
  if [ -n "$configured_keycloak" ] && [ "$current_keycloak" != "$configured_keycloak" ]; then
    echo "ERROR: this update changes the Keycloak image ($current_keycloak -> $configured_keycloak)," >&2
    echo "       but the persisted H2 realm volume cannot be migrated across Keycloak images." >&2
    echo "       No containers were recreated. Follow provision/REDEPLOY.md for a clean redeploy." >&2
    exit 1
  fi
fi

echo "Pulling latest images..."
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env pull

echo "Recreating changed containers..."
# Route through up.sh so the ApiSchema-staged guard applies: a full-stack `up -d` here (gateway
# depends_on pulls the DMS services) would otherwise crash-loop st-dms/mt-dms against an empty
# /app/ApiSchema on an environment that was provisioned but never had its schema staged.
./up.sh

# up.sh records the image from the now-deployed Keycloak container (rather than trusting the
# configured string) before it starts the DMS services. That reference survives a plain down.

echo "Update complete."
