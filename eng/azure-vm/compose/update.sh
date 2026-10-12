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

# The PostgreSQL 18 image keeps its cluster in a versioned PGDATA directory below a volume mounted at
# /var/lib/postgresql. A volume created before that layout holds its cluster at the volume root (it
# was mounted at /var/lib/postgresql/data), which PostgreSQL 18 ignores: recreating the container
# would start an empty cluster beside it, init-databases.sh would create empty databases, and the
# configuration-service rows behind the API keys would be gone from the running stack. Read the
# volume's own root PG_VERSION -- not the container's image tag, and also after a plain ./down.sh
# removed the container -- and refuse before deployment images are refreshed or containers recreated.
# The read-only helper container may fetch the configured PostgreSQL image; the volume is never
# changed. Any root PG_VERSION, even an empty one, is the old layout. FAIL CLOSED when the volume
# cannot be listed or read.
postgres_volume="dms-security-review_dms-sec-postgres"
if ! volumes="$(docker volume ls --quiet)"; then
  echo "ERROR: cannot list Docker volumes, so the PostgreSQL volume layout cannot be checked." >&2
  echo "       No containers were recreated." >&2
  exit 1
fi
if grep -qxF "$postgres_volume" <<<"$volumes"; then
  postgres_image="$(docker compose -f docker-compose.yml -f keycloak.yml --env-file .env config --images | grep -m1 -E '(^|/)postgres[:@]' || true)"
  layout=""
  if [ -n "$postgres_image" ]; then
    # Prints "absent", or "legacy" followed by the file's first line; exits non-zero if unreadable.
    layout="$(docker run --rm --network none --volume "$postgres_volume:/volume:ro" \
      --entrypoint sh "$postgres_image" -c \
      'if [ -e /volume/PG_VERSION ]; then v="$(head -n1 /volume/PG_VERSION)" || exit 3; echo "legacy $v"; else echo absent; fi')" ||
      layout=""
  fi
  case "$layout" in
    absent) ;;
    legacy*)
      legacy_version="${layout#legacy}"
      legacy_version="${legacy_version# }"
      echo "ERROR: the PostgreSQL volume $postgres_volume holds a PostgreSQL ${legacy_version:-(unknown version)} cluster in" >&2
      echo "       the layout used before PostgreSQL 18. The PostgreSQL 18 image this update deploys would ignore" >&2
      echo "       it and start an empty cluster, so the databases and the API keys would not carry over." >&2
      echo "       No deployment images were refreshed, no containers were recreated, and the volume is unchanged." >&2
      echo "       Back up every database (pg_dump edfi_st_config, edfi_mt_config, edfi_st, edfi_mt and" >&2
      echo "       edfi_mt_t2) and the Keycloak volume as in provision/UPDATE.md Part A, then either migrate" >&2
      echo "       the cluster to PostgreSQL 18 deliberately (pg_upgrade, or dump and restore into the new" >&2
      echo "       layout) or follow provision/REDEPLOY.md for a clean redeploy, which issues new API keys." >&2
      echo "       Removing the volume does not keep the keys. See docs/OPERATIONS.md \"Database versions\"." >&2
      exit 1
      ;;
    *)
      echo "ERROR: cannot read the layout of the PostgreSQL volume $postgres_volume." >&2
      echo "       Refusing to update: a pre-18 cluster in it would be replaced by an empty one." >&2
      echo "       No containers were recreated." >&2
      exit 1
      ;;
  esac
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

# The gateway renders its bind-mounted nginx template, and Swagger UI copies its files, only at
# container start; Compose does not recreate a container whose definition is unchanged. Restart
# both so a pulled change to either takes effect.
echo "Restarting the gateway and Swagger UI to apply their mounted configuration..."
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env restart gateway swagger-ui

echo "Update complete."
