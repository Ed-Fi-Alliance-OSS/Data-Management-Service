#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

usage() {
  cat <<USAGE
Usage: $(basename "$0") [--pid <pid>] [--name-pattern <regex>] [--port <port>] \
       [--counters-duration HH:MM:SS] [--trace-profile cpu-sampling|gc-verbose] [--trace-duration HH:MM:SS] [--trace-offset-seconds N]

Runs a coordinated set of collections while a load test is active:
  - dotnet-counters collect (JSON) for the full duration
  - dotnet-trace collect (profile) starting after optional offset
  - optional dumps at the end:
      TAKE_GCDUMP=1 -> collect-gcdump
      TAKE_FULL_DUMP=1 (or TAKE_DUMP_TYPE=full|heap|triage) -> collect-dump (default full)

Examples:
  $(basename "$0") --name-pattern EdFi.DataManagementService.Frontend.AspNetCore --counters-duration 00:05:00 --trace-duration 00:01:00 --trace-offset-seconds 60
USAGE
}

PID=""
NAME_PATTERN=""
PORT=""
COUNTERS_DURATION="00:05:00"
TRACE_PROFILE="cpu-sampling"
TRACE_DURATION="00:01:00"
TRACE_OFFSET=60

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pid) PID="$2"; shift 2;;
    --name-pattern) NAME_PATTERN="$2"; shift 2;;
    --port) PORT="$2"; shift 2;;
    --counters-duration) COUNTERS_DURATION="$2"; shift 2;;
    --trace-profile) TRACE_PROFILE="$2"; shift 2;;
    --trace-duration) TRACE_DURATION="$2"; shift 2;;
    --trace-offset-seconds) TRACE_OFFSET="$2"; shift 2;;
    -h|--help) usage; exit 0;;
    *) echo "Unknown arg: $1" >&2; usage; exit 2;;
  esac
done

ensure_tools

if [[ -z "$PID" ]]; then
  if [[ -n "$NAME_PATTERN" ]]; then
    PID=$(pid_from_name "$NAME_PATTERN" || true)
  fi
  if [[ -z "$PID" && -n "$PORT" ]]; then
    PID=$(pid_from_port "$PORT" || true)
  fi
fi

if [[ -z "$PID" ]]; then
  echo "ERROR: Could not determine PID. Provide --pid or --name-pattern or --port." >&2
  exit 2
fi
require_pid "$PID"

dir="$(telemetry_dir)"
log "Telemetry dir: $dir"

log "Launching counters (duration=$COUNTERS_DURATION)"
"$SCRIPT_DIR/collect-counters.sh" --pid "$PID" --duration "$COUNTERS_DURATION" &
COUNTERS_PID=$!

log "Sleeping $TRACE_OFFSET seconds before starting trace"
sleep "$TRACE_OFFSET"

log "Starting trace ($TRACE_PROFILE for $TRACE_DURATION)"
if [[ "${INCLUDE_IO:-0}" == "1" ]]; then
  "$SCRIPT_DIR/collect-trace.sh" --pid "$PID" --profile "$TRACE_PROFILE" --duration "$TRACE_DURATION" --include-io
else
  "$SCRIPT_DIR/collect-trace.sh" --pid "$PID" --profile "$TRACE_PROFILE" --duration "$TRACE_DURATION"
fi

log "Waiting for counters to finish (pid=$COUNTERS_PID)"
wait "$COUNTERS_PID"

if [[ "${TAKE_GCDUMP:-0}" == "1" ]]; then
  log "TAKE_GCDUMP=1 set; collecting gcdump"
  "$SCRIPT_DIR/collect-gcdump.sh" --pid "$PID"
fi

if [[ "${TAKE_FULL_DUMP:-0}" == "1" || -n "${TAKE_DUMP_TYPE:-}" ]]; then
  dump_type="${TAKE_DUMP_TYPE:-full}"
  log "Collecting process dump (type=$dump_type). This may pause the process."
  "$SCRIPT_DIR/collect-dump.sh" --pid "$PID" --type "$dump_type"
fi

log "Suite complete"
