# SPDX-License-Identifier: Apache-2.0
#
# Sourced by up.sh, update.sh and reset.sh: the Compose files they pass to every `docker compose` call.
# An explicit -f stops Compose from reading docker-compose.override.yml and COMPOSE_FILE, so the optional,
# gitignored reader credential override (see reader.compose.override.example.yml) is selected here, last,
# whenever it exists. ../provision/setup-env.ps1 makes the same selection.
# shellcheck disable=SC2034 # used by the scripts that source this file
compose_files=(-f docker-compose.yml -f keycloak.compose.yml)
if [ -f reader.compose.override.yml ]; then
  compose_files+=(-f reader.compose.override.yml)
fi
