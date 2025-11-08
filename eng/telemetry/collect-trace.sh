#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

usage() {
  cat <<USAGE
Usage: $(basename "$0") --pid <pid> [--profile cpu-sampling|gc-verbose] [--duration HH:MM:SS] [--outfile <path>]

Collects a .nettrace suitable for speedscope/PerfView analysis.
Defaults: --profile cpu-sampling --duration 00:01:00
USAGE
}

PID=""
PROFILE="cpu-sampling"
DURATION="00:01:00"
OUTFILE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pid) PID="$2"; shift 2;;
    --profile) PROFILE="$2"; shift 2;;
    --duration) DURATION="$2"; shift 2;;
    --outfile) OUTFILE="$2"; shift 2;;
    -h|--help) usage; exit 0;;
    *) echo "Unknown arg: $1" >&2; usage; exit 2;;
  esac
done

ensure_tools
require_pid "$PID"

dir="$(telemetry_dir)"
if [[ -z "$OUTFILE" ]]; then
  OUTFILE="$dir/dotnet-trace-${PROFILE}-$(timestamp).nettrace"
else
  mkdir -p "$(dirname "$OUTFILE")"
fi

log "Starting dotnet-trace collect PID=$PID profile=$PROFILE duration=$DURATION"
set -x
"$HOME/.dotnet/tools/dotnet-trace" collect \
  --process-id "$PID" \
  --profile "$PROFILE" \
  --duration "$DURATION" \
  --output "$OUTFILE"
set +x
log ".nettrace written to $OUTFILE"

