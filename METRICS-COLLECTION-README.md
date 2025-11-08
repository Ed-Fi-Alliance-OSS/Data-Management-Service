# Metrics Collection for DMS (ASP.NET Core + .NET Runtime)

This guide shows how to capture high‑signal performance metrics from the DMS process using `dotnet-counters` with JSON output. It includes runtime (GC/ThreadPool) and API‑level (requests/connections) meters that we publish from the app.

## What’s included in the app

The frontend wires minimal meters so counters are available even when ASP.NET/Kestrel built‑ins aren’t present:

- Requests (Meter `Microsoft.AspNetCore.Hosting`)
  - `current-requests` (UpDownCounter)
  - `total-requests` (Counter)
  - `failed-requests` (Counter)
  - `requests-per-second` (ObservableGauge)
  - Code: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/RequestMetricsMiddleware.cs`
- Connections (Meter `Microsoft.AspNetCore.Server.Kestrel`)
  - `current-connections` (UpDownCounter)
  - `total-connections` (Counter)
  - `connections-per-second` (ObservableGauge)
  - Code: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/KestrelConnectionMetricsMiddleware.cs`
- Middleware registration in pipeline (before request logging):
  - `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs`

Restart the DMS process after these are present to begin emitting meters.

## Prerequisites

- .NET diagnostics tools installed as global tools:

```bash
# Install once (or update)
dotnet tool install --global dotnet-counters || dotnet tool update --global dotnet-counters
```

Ensure `~/.dotnet/tools` is on your `PATH` or invoke tools with the full path.

## Find the DMS PID

```bash
# Replace the grep if the path/name differs
ps -eo pid,cmd | grep -F "EdFi.DataManagementService.Frontend.AspNetCore" | grep -v grep
# Or store for scripts
ps -eo pid,cmd | awk '/EdFi.DataManagementService.Frontend.AspNetCore/{print $1; exit}' > telemetry/current_dms_pid.txt
```

## Collect 5 minutes of JSON METERS (recommended)

Captures ASP.NET requests and Kestrel connections alongside .NET runtime metrics at 5‑second intervals:

```bash
PID=$(cat telemetry/current_dms_pid.txt)
~/.dotnet/tools/dotnet-counters collect \
  --process-id ${PID} \
  --counters \
    Microsoft.AspNetCore.Hosting[current-requests,total-requests,failed-requests,requests-per-second],\
    Microsoft.AspNetCore.Server.Kestrel[current-connections,total-connections,connections-per-second],\
    System.Runtime[cpu-usage,threadpool-thread-count,threadpool-queue-length,gc-heap-size,working-set,monitor-lock-contention-count] \
  --format json \
  --refresh-interval 5 \
  --duration 00:05:00 \
  --output telemetry/aspnet-kestrel-meters-$(date +%Y%m%d%H%M%S)
```

- Output: `telemetry/aspnet-kestrel-meters-<timestamp>.json`
- Counters are emitted as METERS (preferred in .NET 8+). EventCounters can be collected too (see below) but may not be present.

## Optional: Collect EventCounters (if the app exposes them)

```bash
PID=$(cat telemetry/current_dms_pid.txt)
~/.dotnet/tools/dotnet-counters collect \
  --process-id ${PID} \
  --counters \
    EventCounters\Microsoft.AspNetCore.Hosting[requests-per-second,current-requests,request-queue-length,failed-requests],\
    EventCounters\Microsoft-AspNetCore-Server-Kestrel[current-connections,total-connections,connections-per-second],\
    System.Runtime[cpu-usage,threadpool-thread-count,threadpool-queue-length,gc-heap-size,working-set,monitor-lock-contention-count] \
  --format json \
  --refresh-interval 5 \
  --duration 00:05:00 \
  --output telemetry/aspnet-kestrel-eventcounters-$(date +%Y%m%d%H%M%S)
```

If EventCounters and Meters share the same provider name, don’t mix prefixed and unprefixed variants in a single `--counters` string. Prefer one approach per provider.

## Quick JSON post‑processing examples

Derive RPS/CPS from meters we emit (already included as `requests-per-second` and `connections-per-second`), or use totals’ deltas.

```bash
# Show last 5 samples of requests-per-second
jq -r '.Events[] | select(.provider=="Microsoft.AspNetCore.Hosting" and .name=="requests-per-second") | [.timestamp, .value] | @tsv' telemetry/aspnet-kestrel-meters-*.json | tail -n 5

# Compute RPS from total-requests deltas (per 5s interval)
jq -r '.Events[] | select(.provider=="Microsoft.AspNetCore.Hosting" and .name=="total-requests") | [.timestamp, .value] | @tsv' telemetry/aspnet-kestrel-meters-*.json \
| awk 'BEGIN{prev=0} {if (NR>1){dt=5; dr=$2-prev; printf("%s\t%.2f\n", $1, dr/dt)} prev=$2}' | tail -n 10

# ThreadPool queue length and CPU trend (last 10 samples)
jq -r '.Events[] | select((.provider=="System.Runtime" and .name=="ThreadPool Queue Length") or (.name=="CPU Usage (%)")) | [.timestamp, .name, .value] | @tsv' telemetry/aspnet-kestrel-meters-*.json | tail -n 20
```

## Troubleshooting

- "Concurrent sessions are not supported": Only one `dotnet-counters` session can target the process at a time. Kill existing sessions (`pkill -f dotnet-counters`) before starting a new one.
- No ASP.NET/Kestrel counters in JSON:
  - Ensure the service was restarted after adding these middlewares:
    - `RequestMetricsMiddleware` and `KestrelConnectionMetricsMiddleware` in `Program.cs`.
  - Use Meters provider names exactly as shown. EventCounters may not be present in some builds.
- Histogram limit warning: `dotnet-counters` defaults to tracking 10 histograms. Increase with `--maxHistograms` if needed (uses more memory in the target process).
- ANSI output in `monitor`: prefer `collect --format json` for machine‑readable output (recommended).

## Related runtime telemetry

- Short runtime health check during load (interactive table):

```bash
~/.dotnet/tools/dotnet-counters monitor --process-id ${PID} \
  System.Runtime[cpu-usage,working-set,gc-heap-size,threadpool-thread-count,threadpool-queue-length]
```

- CPU/stack traces for deep analysis:

```bash
~/.dotnet/tools/dotnet-trace collect --process-id ${PID} \
  --providers Microsoft-AspNetCore-Server-Kestrel,System.Runtime,Npgsql \
  --duration 00:02:00 --output telemetry/dms-concurrency-$(date +%Y%m%d%H%M%S).nettrace
```

## Output locations

All examples write into a local `telemetry/` folder in the repo root. Adjust paths as needed for your environment or CI runs.

---

Use these captures to correlate request/connection pressure with runtime behavior (GC/TP/locks) and database activity (e.g., `pg_stat_activity`).
