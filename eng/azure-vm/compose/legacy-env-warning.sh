#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Sourced by up.sh, update.sh, and reset.sh. Warns about environment variable names retired by the
# move to the official "Configuration Service" name. Docker Compose no longer reads them, so a .env
# that still sets one runs on the default or fails on the missing replacement. Mirrors
# Get-LegacyEnvironmentVariableReplacement in eng/docker-compose/env-utility.psm1, limited to the
# names this stack used, with one difference: DMS_CONFIG_IDENTITY_ENCRYPTION_KEY is reported as
# removed, because this Keycloak-only stack never reads its replacement (provision/setup-env.ps1
# deletes the old line). Only names are printed: values can be secrets and are never read.

# Prints the replacement name, prints nothing for a retired name with no replacement, or returns 1
# for a current name.
legacy_env_replacement() {
  case "$1" in
    DMS_CONFIG_USE_EMBEDDED_BASE_CLAIMS | DMS_CONFIG_CLAIMS_PATH | DMS_CONFIG_USE_CLAIMS_PATH | \
      DMS_CONFIG_DANGEROUSLY_ENABLE_DYNAMIC_CLAIMS_LOADING | DMS_CONFIG_ENABLE_APPLICATION_RESET_ENDPOINT | \
      DMS_CONFIG_IDENTITY_SCOPE | DMS_CONFIG_IDENTITY_ENCRYPTION_KEY) ;;
    DMS_CONFIG_ENABLE_CLAIMS_RELOAD) echo "CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING" ;;
    DMS_CONFIG_?*) echo "CONFIGURATION_SERVICE_${1#DMS_CONFIG_}" ;;
    CONFIG_SERVICE_?*) echo "DMS_CONFIGURATION_SERVICE_${1#CONFIG_SERVICE_}" ;;
    *) return 1 ;;
  esac
}

warn_legacy_env_name() {
  local name="$1" source="$2" replacement
  replacement="$(legacy_env_replacement "$name")" || return 0
  if [ -z "$replacement" ]; then
    echo "WARNING: Environment variable '$name' in $source is no longer read and can be removed." >&2
  else
    echo "WARNING: Environment variable '$name' in $source is no longer read. Rename it to '$replacement'." >&2
  fi
}

# Usage: warn_legacy_env_names <env-file>
warn_legacy_env_names() {
  local env_file="$1" name
  if [ -f "$env_file" ]; then
    while IFS= read -r name; do
      warn_legacy_env_name "$name" "'$env_file'"
    done < <(sed -nE 's/^[[:space:]]*(export[[:space:]]+)?([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*=.*/\2/p' "$env_file" | sort -u)
  fi
  while IFS= read -r name; do
    warn_legacy_env_name "$name" "the process environment"
  done < <(compgen -e | sort -u)
}
