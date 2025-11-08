#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

usage() {
  cat <<USAGE
Usage: $(basename "$0") --pid <pid> [--duration HH:MM:SS]

Runs a short JSON collection to validate counter names/providers exist.
Validates presence of key counters: System.Runtime cpu-usage, ASP.NET Hosting requests-per-second, Kestrel current-connections.
USAGE
}

PID=""
DURATION="00:00:10"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pid) PID="$2"; shift 2;;
    --duration) DURATION="$2"; shift 2;;
    -h|--help) usage; exit 0;;
    *) echo "Unknown arg: $1" >&2; usage; exit 2;;
  esac
done

ensure_tools
require_pid "$PID"

dir="$(telemetry_dir)"
outfile="$dir/dotnet-counters-probe-$(timestamp).json"

if ! "$SCRIPT_DIR/collect-counters.sh" --pid "$PID" --duration "$DURATION" --outfile "$outfile"; then
  echo "Probe aborted. Another collector may already be attached. If you are running a long counters collection, skip probe validation and proceed with run-suite, or stop the existing session and re-run." >&2
  exit 1
fi

log "Validating counters in $outfile"

have_jq=0
command -v jq >/dev/null 2>&1 && have_jq=1

fail=0
check_contains() {
  local needle="$1" desc="$2"
  if (( have_jq == 1 )); then
    # dotnet-counters 9 JSON shape: { TargetProcess, StartTime, Events: [{ provider, name, value, ...}, ...] }
    if ! jq -esr --arg n "$needle" '.[0].Events | any(.[]; .name == $n)' "$outfile" >/dev/null; then
      log "Missing counter: $desc ($needle)"
      fail=1
    fi
  else
    if ! grep -q "$needle" "$outfile"; then
      log "Missing counter (grep): $desc ($needle)"
      fail=1
    fi
  fi
}

# Friendly counter names in JSON output
check_contains "CPU Usage (%)" "System.Runtime CPU Usage"
check_contains "Request Rate (Count / 1 sec)" "Microsoft.AspNetCore.Hosting requests-per-second"
check_contains "Current Connections" "Kestrel current-connections"

if (( fail == 1 )); then
  echo "Counter probe failed; see $outfile" >&2
  exit 1
fi

log "Counter probe succeeded"
