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

# A value in the override replaces the same setting from .env, and the .env placeholder checks do not read the
# override, so an entry still holding an example value (CHANGEME_...) would silently replace a working credential.
# Refuses, naming only the file and line numbers (never their text), while a non-comment line assigns a value that
# starts with CHANGEME_. Comment lines are ignored; a missing override passes. up.sh, update.sh and reset.sh call it
# before any Docker call; ../provision/setup-env.ps1 applies the same pattern.
refuse_reader_override_placeholders() {
  local lines
  [ -f reader.compose.override.yml ] || return 0
  lines="$(grep -nE "^[[:space:]]*[^#[:space:]].*[:=][[:space:]]*[\"']?CHANGEME_" reader.compose.override.yml |
    cut -d: -f1 | paste -sd, - || true)"
  [ -z "$lines" ] && return 0
  echo "ERROR: reader.compose.override.yml still holds example CHANGEME_ values (line(s) ${lines//,/, })." >&2
  echo "       A value there replaces the same setting from .env, so the Configuration Services would start with" >&2
  echo "       the placeholder credentials. Replace each one you need with the real value and delete the entries" >&2
  echo "       you do not need (see reader.compose.override.example.yml), or remove the file to use .env only." >&2
  echo "       No services were started, recreated or removed." >&2
  return 1
}
