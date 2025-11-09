#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

usage() {
  cat <<USAGE
Usage: $(basename "$0") --pid <pid> [--profile cpu-sampling|gc-verbose] [--duration HH:MM:SS] [--outfile <path>] [--providers <list>] [--include-io]

Collects a .nettrace suitable for speedscope/PerfView analysis.
Defaults: --profile cpu-sampling --duration 00:01:00
Providers:
  --providers takes a comma-separated list of EventSource/ETW provider names.
  --include-io adds common network/DB providers: System.Net.Http,System.Net.Sockets,Npgsql
USAGE
}

PID=""
PROFILE="cpu-sampling"
DURATION="00:01:00"
OUTFILE=""
PROVIDERS=""
INCLUDE_IO=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pid) PID="$2"; shift 2;;
    --profile) PROFILE="$2"; shift 2;;
    --duration) DURATION="$2"; shift 2;;
    --outfile) OUTFILE="$2"; shift 2;;
    --providers) PROVIDERS="$2"; shift 2;;
    --include-io) INCLUDE_IO=1; shift 1;;
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

EXTRA_PROVIDERS=()
if (( INCLUDE_IO == 1 )); then
  EXTRA_PROVIDERS+=("System.Net.Http" "System.Net.Sockets" "Npgsql")
fi
if [[ -n "$PROVIDERS" ]]; then
  IFS=',' read -r -a user_providers <<< "$PROVIDERS"
  for p in "${user_providers[@]}"; do EXTRA_PROVIDERS+=("$p"); done
fi

log "Starting dotnet-trace collect PID=$PID profile=$PROFILE duration=$DURATION providers=${EXTRA_PROVIDERS[*]:-none}"
set -x
if (( ${#EXTRA_PROVIDERS[@]} > 0 )); then
  prov_csv=$(IFS=, ; echo "${EXTRA_PROVIDERS[*]}")
  "$HOME/.dotnet/tools/dotnet-trace" collect \
    --process-id "$PID" \
    --profile "$PROFILE" \
    --duration "$DURATION" \
    --providers "$prov_csv" \
    --output "$OUTFILE"
else
  "$HOME/.dotnet/tools/dotnet-trace" collect \
    --process-id "$PID" \
    --profile "$PROFILE" \
    --duration "$DURATION" \
    --output "$OUTFILE"
fi
set +x
log ".nettrace written to $OUTFILE"
