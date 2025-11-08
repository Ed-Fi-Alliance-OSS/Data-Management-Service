#!/usr/bin/env bash
set -euo pipefail

# Shared helpers for telemetry scripts

ensure_tools() {
  local missing=()
  for t in "$HOME/.dotnet/tools/dotnet-counters" "$HOME/.dotnet/tools/dotnet-trace" \
           "$HOME/.dotnet/tools/dotnet-gcdump" "$HOME/.dotnet/tools/dotnet-dump"; do
    if [[ ! -x "$t" ]]; then
      missing+=("$t")
    fi
  done
  if (( ${#missing[@]} > 0 )); then
    echo "Installing missing dotnet diagnostics tools: ${missing[*]}" >&2
    dotnet tool install -g dotnet-counters >/dev/null 2>&1 || true
    dotnet tool install -g dotnet-trace >/dev/null 2>&1 || true
    dotnet tool install -g dotnet-gcdump >/dev/null 2>&1 || true
    dotnet tool install -g dotnet-dump >/dev/null 2>&1 || true
  fi
}

timestamp() {
  date +%Y%m%d%H%M%S
}

telemetry_dir() {
  local dir="telemetry"
  mkdir -p "$dir"
  echo "$dir"
}

pid_from_name() {
  local name_filter="$1"
  "$HOME/.dotnet/tools/dotnet-counters" ps | awk -v pat="$name_filter" '$0 ~ pat {print $1; exit}'
}

pid_from_port() {
  local port="$1"
  # Linux: use lsof if available
  if command -v lsof >/dev/null 2>&1; then
    lsof -iTCP:"$port" -sTCP:LISTEN -P -n | awk 'NR>1 {print $2; exit}' || true
  else
    ss -lntp | awk -v p=":"port '$4 ~ p {print $NF}' | sed -E 's/.*pid=([0-9]+),.*/\1/' | head -n1 || true
  fi
}

require_pid() {
  local pid="$1"
  if [[ -z "$pid" || ! -d "/proc/$pid" ]]; then
    echo "ERROR: Invalid or missing PID '$pid'" >&2
    exit 2
  fi
}

log() {
  echo "[telemetry] $(date -Iseconds) $*" >&2
}

