#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

usage() {
  cat <<USAGE
Usage: $(basename "$0") --pid <pid> [--type triage|heap|full] [--outfile <path>]

Collects a process dump with dotnet-dump. WARNING: --type full may pause the target process.
Default: --type triage
USAGE
}

PID=""
TYPE="full"
OUTFILE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pid) PID="$2"; shift 2;;
    --type) TYPE="$2"; shift 2;;
    --outfile) OUTFILE="$2"; shift 2;;
    -h|--help) usage; exit 0;;
    *) echo "Unknown arg: $1" >&2; usage; exit 2;;
  esac
done

ensure_tools
require_pid "$PID"

dir="$(telemetry_dir)"
if [[ -z "$OUTFILE" ]]; then
  OUTFILE="$dir/dms-${TYPE}-$(timestamp).dmp"
else
  mkdir -p "$(dirname "$OUTFILE")"
fi

log "Collecting dotnet-dump type=$TYPE for PID=$PID"
set -x
"$HOME/.dotnet/tools/dotnet-dump" collect \
  --process-id "$PID" \
  --type "$TYPE" \
  --output "$OUTFILE"
set +x
log "dump written to $OUTFILE"
