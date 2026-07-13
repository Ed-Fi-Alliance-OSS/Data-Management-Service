#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Record the image that actually created/started the persisted Keycloak H2 realm. update.sh uses
# this reference after a plain down removes the container but preserves the volume, so it can reject
# an unsafe image-pin change instead of applying it to realm state whose version is then unknowable.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

container="${KEYCLOAK_CONTAINER:-dms-sec-keycloak}"
ref_file=".bootstrap/keycloak-image"

if ! image="$(docker inspect --format '{{.Config.Image}}' "$container" 2>/dev/null)" || [ -z "$image" ]; then
  echo "ERROR: cannot record the deployed Keycloak image: container '$container' is unavailable." >&2
  exit 1
fi

mkdir -p "$(dirname "$ref_file")"
tmp="$(mktemp "${ref_file}.tmp.XXXXXX")"
trap 'rm -f "$tmp"' EXIT
printf '%s\n' "$image" > "$tmp"
mv -f "$tmp" "$ref_file"
trap - EXIT

echo "Recorded deployed Keycloak image: $image"
