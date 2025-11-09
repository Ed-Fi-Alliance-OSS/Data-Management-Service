**Purpose**
- Provide a repeatable, low-friction way to capture high‑signal performance telemetry from the DMS process while a load test is running.
- Artifacts are timestamped and written under `telemetry/` for later analysis.

**Tools**
- `dotnet-counters collect` (JSON output) — runtime/ASP.NET/Kestrel metrics
- `dotnet-trace collect` — CPU sampling and GC verbose traces
- `dotnet-gcdump collect` — managed heap snapshot
- `dotnet-dump collect` — process dump (triage/heap/full)

**Process Discovery**
- Preferred: `--name-pattern EdFi.DataManagementService.Frontend.AspNetCore`
- Alternate: `--port 8080` (if running with `--urls http://localhost:8080`)
- Fallback: pass `--pid <PID>` directly

**Scripts** (eng/telemetry)
- `run-suite.sh` — orchestrates a typical run: counters for full window + short trace mid‑run + optional dumps (gcdump and/or full/heap/triage process dump)
- `collect-counters.sh` — parameterized JSON counters collection
- `collect-trace.sh` — parameterized .nettrace (cpu-sampling or gc-verbose)
- `collect-gcdump.sh` — quick managed heap snapshot
- `collect-dump.sh` — on‑demand process dump (triage/heap/full)
- `probe-counters.sh` — 10s validation that counter names/providers are correct

**Default Counters Set**
- System.Runtime: `cpu-usage, working-set, gc-heap-size, time-in-gc, allocation-rate, gen-0-gc-count, gen-1-gc-count, gen-2-gc-count, threadpool-thread-count, threadpool-queue-length, monitor-lock-contention-count, exception-count`
- Microsoft.AspNetCore.Hosting: `requests-per-second, current-requests, failed-requests, request-queue-length`
- Microsoft-AspNetCore-Server-Kestrel: `current-connections, total-connections, connections-per-second`

These are encoded in `collect-counters.sh` and can be overridden via `--counters`.

**Quick Start (Smoke Validation, ~10s)**
- Ensure DMS is running under load.
- Find PID automatically and validate counters names:
  - `bash eng/telemetry/probe-counters.sh --pid $(~/.dotnet/tools/dotnet-counters ps | awk '/EdFi.DataManagementService.Frontend.AspNetCore/{print $1; exit}')`
  - Output: `telemetry/dotnet-counters-probe-YYYYMMDDhhmmss.json`

**Standard Collection (Recommended)**
- Run suite for a 5‑minute load test window, capturing a 60‑second CPU trace starting at T+60s:
  - `bash eng/telemetry/run-suite.sh --name-pattern EdFi.DataManagementService.Frontend.AspNetCore --counters-duration 00:05:00 --trace-profile cpu-sampling --trace-duration 00:01:00 --trace-offset-seconds 60`
- Artifacts generated:
  - `telemetry/dotnet-counters-YYYYMMDDhhmmss.json`
  - `telemetry/dotnet-trace-cpu-sampling-YYYYMMDDhhmmss.nettrace`
  - Optional (`TAKE_GCDUMP=1`): `telemetry/dms-YYYYMMDDhhmmss.gcdump`
  - Optional (`TAKE_FULL_DUMP=1` or `TAKE_DUMP_TYPE=full|heap|triage`): `telemetry/dms-<type>-YYYYMMDDhhmmss.dmp`
  - Optional include I/O providers: prefix with `INCLUDE_IO=1` to add System.Net.Http/System.Net.Sockets/Npgsql to the trace.

**Targeted Deep Dives**
- GC focus (if gen2/LOH growth or high GC time):
  - `bash eng/telemetry/collect-trace.sh --pid <PID> --profile gc-verbose --duration 00:01:00`
  - `bash eng/telemetry/collect-gcdump.sh --pid <PID>`
- CPU spikes or thread pool starvation:
  - `bash eng/telemetry/collect-trace.sh --pid <PID> --profile cpu-sampling --duration 00:01:00`
- I/O wait attribution (HTTP, Sockets, Npgsql):
  - `bash eng/telemetry/collect-trace.sh --pid <PID> --profile cpu-sampling --duration 00:01:00 --include-io`
  - Or custom providers: `--providers System.Net.Http,System.Net.Sockets,Npgsql`
- Full forensic snapshot (pauses process):
  - `bash eng/telemetry/collect-dump.sh --pid <PID>` (defaults to `--type full`)
  - Or with suite at end: `TAKE_FULL_DUMP=1 bash eng/telemetry/run-suite.sh ...`

**Dump Strategy (Preferred: Full dump)**
- We prefer a full process dump to capture all state. Expect a brief pause while the dump is captured.
- Alternatives: `--type heap` (less intrusive) or `gcdump` (managed heap only) if you cannot pause.
- Keep `dotnet-trace` windows short (30–90s); long traces can be large and add overhead.
- `dotnet-counters monitor` is for live viewing; for analysis we always use `collect --format json`.

**Reading Artifacts (at-a-glance)**
- Counters JSON (dotnet-counters 9 format: single JSON object with `Events` array). Examples:
  - RPS: `jq -r '.Events[] | select(.name=="Request Rate (Count / 1 sec)") | [.timestamp,.value] | @csv' telemetry/dotnet-counters-*.json`
  - CPU: `jq -r '.Events[] | select(.provider=="System.Runtime" and .name=="CPU Usage (%)") | [.timestamp,.value] | @csv' telemetry/dotnet-counters-*.json`
  - Queue length: `jq -r '.Events[] | select(.name=="ThreadPool Queue Length") | [.timestamp,.value] | @csv' telemetry/dotnet-counters-*.json`
  - GC time: `jq -r '.Events[] | select(.name|test("Time in GC")) | [.timestamp,.value] | @csv' telemetry/dotnet-counters-*.json`
  - Allocation rate: `jq -r '.Events[] | select(.name|test("Allocation Rate")) | [.timestamp,.value] | @csv' telemetry/dotnet-counters-*.json`
- Trace: open `.nettrace` in speedscope.app or PerfView for stacks; use GCStats for pauses and allocation rates.
- GCDump: open with Visual Studio or `dotnet-gcdump analyze` for heap types and sizes.

**Repeatability Checklist**
- [ ] DMS PID resolved with `run-suite.sh` via name or port
- [ ] `probe-counters.sh` passes (confirms counter names)
- [ ] Counters JSON collected for full test duration
- [ ] CPU trace captured within the load window
- [ ] Optional gcdump captured when heap anomalies suspected

**Troubleshooting**
- If `probe-counters.sh` fails: counter names may differ in your runtime; override with `collect-counters.sh --counters '<spec>'` and re‑probe.
- If collection appears to hang: avoid `monitor --list`; prefer short `collect` runs (10s) to validate.
- For containerized DMS: these scripts target host processes. If DMS runs inside Docker, run the dotnet-* tools inside the container or expose a diagnostic port.

**Why this mix**
- Counters (low overhead): trends and correlation (RPS, CPU, GC heap, queues, connections)
- Trace (medium overhead, short window): hot stacks, GC pauses, thread pool
- GCDump (low overhead): managed heap snapshot for allocation pressure
- Dump (high impact): only when you must freeze state for forensic analysis
