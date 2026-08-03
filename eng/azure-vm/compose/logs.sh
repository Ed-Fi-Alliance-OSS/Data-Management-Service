#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Tail logs. Pass a service name to follow just one (e.g. ./logs.sh mt-dms).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env logs -f "$@"
