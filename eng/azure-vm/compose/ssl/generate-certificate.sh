#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Generates a self-signed certificate for local/testing use, written next to
# this script as server.crt / server.key (the names the gateway expects).
#
# On the Azure VM, use a real Let's Encrypt certificate instead (see
# ../../provision/README runbook) and either copy fullchain.pem -> server.crt and
# privkey.pem -> server.key, or symlink them.
#
# Usage: ./generate-certificate.sh [common-name]
#   common-name defaults to "localhost".
set -euo pipefail

CN="${1:-localhost}"
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

openssl req \
  -x509 -newkey rsa:2048 -nodes -days 365 \
  -keyout "${DIR}/server.key" \
  -out "${DIR}/server.crt" \
  -subj "/CN=${CN}" \
  -addext "subjectAltName=DNS:${CN},DNS:localhost"

chmod 600 "${DIR}/server.key"
echo "Self-signed certificate written to ${DIR}/server.crt (CN=${CN})."
