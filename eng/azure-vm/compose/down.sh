#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Stop the environment. Pass -v to also delete volumes (DROPS the Keycloak realm
# and all databases). For a data-only reset that KEEPS Keycloak, use reset.sh.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

docker compose -f docker-compose.yml -f keycloak.yml --env-file .env down "$@"
