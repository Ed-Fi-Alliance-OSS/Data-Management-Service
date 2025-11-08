#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

usage() {
  cat <<USAGE
Usage: $(basename "$0") --pid <pid> [--outfile <path>]

Collects a managed heap gc dump (.gcdump). Non-invasive.
USAGE
}

PID=""
OUTFILE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pid) PID="$2"; shift 2;;
    --outfile) OUTFILE="$2"; shift 2;;
    -h|--help) usage; exit 0;;
    *) echo "Unknown arg: $1" >&2; usage; exit 2;;
  esac
done

ensure_tools
require_pid "$PID"

dir="$(telemetry_dir)"
if [[ -z "$OUTFILE" ]]; then
  OUTFILE="$dir/dms-$(timestamp).gcdump"
else
  mkdir -p "$(dirname "$OUTFILE")"
fi

log "Collecting gcdump for PID=$PID"
set -x
"$HOME/.dotnet/tools/dotnet-gcdump" collect \
  --process-id "$PID" \
  --output "$OUTFILE"
set +x
log "gcdump written to $OUTFILE"

