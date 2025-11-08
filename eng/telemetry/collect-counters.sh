#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

usage() {
  cat <<USAGE
Usage: $(basename "$0") --pid <pid> [--duration HH:MM:SS] [--outfile <path>] [--counters <spec>]

Collects JSON EventCounters suitable for offline analysis.

Defaults:
  --duration 00:05:00
  --counters "System.Runtime[cpu-usage,working-set,gc-heap-size,threadpool-thread-count,threadpool-queue-length,monitor-lock-contention-count],EventCounters\\Microsoft.AspNetCore.Hosting[requests-per-second,current-requests,failed-requests,request-queue-length],EventCounters\\Microsoft-AspNetCore-Server-Kestrel[current-connections,total-connections,connections-per-second]"

Examples:
  $(basename "$0") --pid 12345 --duration 00:05:00
  $(basename "$0") --pid 12345 --counters 'System.Runtime[cpu-usage]'
USAGE
}

PID=""
DURATION="00:05:00"
OUTFILE=""
COUNTERS_DEFAULT='System.Runtime[cpu-usage,working-set,gc-heap-size,threadpool-thread-count,threadpool-queue-length,monitor-lock-contention-count],EventCounters\Microsoft.AspNetCore.Hosting[requests-per-second,current-requests,failed-requests,request-queue-length],EventCounters\Microsoft-AspNetCore-Server-Kestrel[current-connections,total-connections,connections-per-second]'
COUNTERS_SPEC="$COUNTERS_DEFAULT"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pid) PID="$2"; shift 2;;
    --duration) DURATION="$2"; shift 2;;
    --outfile) OUTFILE="$2"; shift 2;;
    --counters) COUNTERS_SPEC="$2"; shift 2;;
    -h|--help) usage; exit 0;;
    *) echo "Unknown arg: $1" >&2; usage; exit 2;;
  esac
done

ensure_tools
require_pid "$PID"

dir="$(telemetry_dir)"
if [[ -z "$OUTFILE" ]]; then
  OUTFILE="$dir/dotnet-counters-$(timestamp).json"
else
  mkdir -p "$(dirname "$OUTFILE")"
fi

log "Starting dotnet-counters collect for PID=$PID duration=$DURATION"
set -x
"$HOME/.dotnet/tools/dotnet-counters" collect \
  --process-id "$PID" \
  --counters "$COUNTERS_SPEC" \
  --duration "$DURATION" \
  --format json \
  --output "$OUTFILE"
set +x
if [[ ! -s "$OUTFILE" ]]; then
  echo "ERROR: dotnet-counters did not produce output file ($OUTFILE). Is there another collection session already attached to PID $PID?" >&2
  exit 1
fi
log "Counters written to $OUTFILE"
