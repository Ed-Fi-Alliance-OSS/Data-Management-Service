# DMS-1556 investigation tooling

Harness and diagnostics recipes for the DMS-1556 investigation (CMS profile requests
return HTTP 500 during concurrent catalog loading). The plan, experiment definitions, and
decision gates live in
`reference/design/configuration-service/DMS-1556-cms-signing-key-resolution-under-load.md`
(§3); this folder is the tooling it names in step 0.1.

Captured evidence lands in `./artifacts/` (gitignored). Nothing here touches production
code or configuration.

## Stack

The experiment environment is the dms-local E2E stack (spec §3.1): from
`src/dms/tests/EdFi.DataManagementService.Tests.E2E`, `pwsh ./teardown-local-dms.ps1`
then `pwsh ./setup-local-dms.ps1 -EnvironmentFile ./.env.e2e` (self-contained identity,
PostgreSQL, database-backed signing keys). CMS answers at `http://127.0.0.1:8081/config`.
Always address it as `127.0.0.1`, not `localhost`: on Windows the dual-stack fallback adds
~2 s to every new connection to the 127.0.0.1-bound port — a client-side artifact that
would contaminate latency evidence (measured during the 0.1 smoke).

For harness/diagnostics smoke work, the lighter CMS-only stack is equivalent: from
`eng/docker-compose`, `pwsh ./start-local-config.ps1 -EnvironmentFile ./.env.e2e
-IdentityProvider self-contained`.

## Diagnostics overlays

Both overlays are additive compose files in `eng/docker-compose/`. Apply them with the
stack's own `-f` list, `--env-file`, and project name, then recreate the affected
services. For the CMS-only stack:

```powershell
cd eng/docker-compose
docker compose -f postgresql.yml -f local-config.yml -f keycloak.yml `
  -f local-config-diagnostics.yml -f local-postgresql-diagnostics.yml `
  --env-file .env.e2e -p cs-local up -d db config cms-monitor
```

For the dms-local stack, use that stack's `-f` list and `-p dms-local`.

- `local-config-diagnostics.yml` — CMS `DOTNET_DiagnosticPorts` (connect + suspend mode)
  plus a digest-pinned `dotnet-monitor` sidecar in listen mode, `--no-auth`, run as root
  (the in-process call-stacks socket is created by the CMS user and is otherwise
  unreachable across containers), bound to `127.0.0.1:52323`. Suspend mode is required:
  `/stacks` depends on in-process features the monitor can only inject while the runtime
  waits at startup, so CMS start blocks until the sidecar is up under this overlay.
  Endpoints used by the investigation (all take an explicit `pid` from `/processes` —
  the target is not marked default):
  - `GET /processes` — pid discovery
  - `GET /livemetrics?durationSeconds=N` — System.Runtime / ASP.NET Core counters
  - `GET /stacks?pid=P` — managed stacks (in-process features enabled in the overlay)
  - `GET /dump?pid=P&type=Full` — full dump for `threadpool` analysis (Q17)
- `local-postgresql-diagnostics.yml` — `log_connections`, `log_disconnections`,
  `log_min_duration_statement=250`, `log_lock_waits`, `log_checkpoints`. It restates the
  base `max_locks_per_transaction=256` because compose replaces `command` wholesale.

## Scripts

- `Invoke-CmsProfileBurst.ps1` — seed profiles (`-Seed -ProfileCount 87`, idempotent,
  writes `artifacts/profile-manifest.json` with id + definition + SHA-256 per profile)
  and replay the DMS catalog burst (`-TotalRequests`, `-MaxConcurrency`, `-Rounds`,
  `-InterRoundDelaySeconds`, `-Cold`, `-ValidateBodies`, `-WithSamplers`). Each round
  summary records its `startUtc`/`endUtc` so captures can be sliced per round; the
  optional inter-round gap (default 0, as in E1) keeps sub-second warm rounds separable
  in the 1 s counters. `-StackCaptureOffsetsSeconds`/`-StackCaptureRounds` (with
  `-WithSamplers`) capture dotnet-monitor `/stacks` at fixed offsets into chosen rounds
  on a thread job; each capture takes ~2.4 s and its requested/completed times are
  recorded in the round summary. Per Q18 the in-flight interval runs from
  semaphore admission through response-body completion, the summary reports the measured
  peak overlap, and runs are labeled `catalog-87x87` / `stress-256x128` / `workload-TxN`.
  Each request runs under one deadline (`-RequestTimeoutSeconds`) started at admission
  and covering both the send and the body read, so a stalled response body cannot hold a
  semaphore slot past the deadline; body validation is ordinal. **Cold-run order**: the
  token is minted BEFORE the restart and reused afterwards (unauthenticated `/health`
  readiness only), so token issuance does not warm CMS's database connections between
  the restart and the measured burst; the order is recorded in the summary's
  `coldPreparation` block. **`-WithSamplers`** makes the harness the coordinator:
  restart → new-process discovery → sampler readiness → burst → coverage validation,
  and the run FAILS (after writing the summary) if the required captures do not span
  the whole burst window.
- `dms-1556-samplers.psm1` / `Start-DiagnosticsSamplers.ps1` — the sampler set (module
  plus a standalone wrapper): `pg_stat_activity` grouped by datname/usename/
  application_name/state/wait_event_type/backend_type plus per-database `numbackends`
  (~250 ms) and `pg_stat_io`/`pg_stat_bgwriter` (~1 s), each over ONE persistent psql
  `\watch` connection whose application_name carries the shared attribution prefix plus
  a per-set unique suffix (`dms1556-sampler-activity-<id>` / `dms1556-sampler-io-<id>`),
  so cleanup terminates only its own set's sessions and overlapping sampler sets cannot
  end each other's captures; a timestamped `docker stats` stream; Windows host disk counters
  (the runner equivalent is `/proc/diskstats`); and the dotnet-monitor `/livemetrics`
  capture. The required captures are explicit: pg-activity, pg-io, and docker-stats
  always; host-disk on Windows (reported as unsupported elsewhere); livemetrics when
  the monitor is required. Readiness (every required capture producing data) gates the
  burst, and the post-run validation applies data-presence and burst-window coverage to
  every required capture — for livemetrics **per required provider**, so one early
  `Npgsql` sample does not pass merely because `System.Runtime` continues through the
  burst. A skipped or failed capture is a reported `requiredFailures` entry, never a
  silent gap. Lifecycle: callers own the sampler set under try/finally;
  `Remove-DmsSamplerSet` is the idempotent emergency cleanup (a no-op after a graceful
  `Stop-DmsSamplerSet`) and never masks the original error, and `Start-DmsSamplerSet`
  cleans up its own partial startups. **`/livemetrics` is the capture that supplies the
  required runtime counters**: `System.Runtime` (thread pool, lock contention, CPU, GC,
  working set), `Microsoft.AspNetCore.Hosting` (current/failed requests), and `Npgsql`
  (connection pool), at the overlay's 1 s counter interval.

### Sampler connections and M-conn

The PostgreSQL samplers hold exactly two persistent connections for the whole window,
self-identified as `application_name=dms1556-sampler-activity-<id>` and
`dms1556-sampler-io-<id>` (a unique suffix per sampler set), both to `dbname=postgres` —
not to the CMS database. For M-conn:

- `log_connections` lines for the samplers are the two session starts per run; exclude
  them by the `dms1556-sampler-` application-name prefix (or by database `postgres`).
- Per-database `numbackends` for the CMS database is unaffected by the samplers; the
  `postgres` database rows carry the constant +2 sampler offset.
- `pg_stat_activity` rows retain datname/usename/application_name, so CMS connections
  and sampler connections are directly distinguishable in the capture itself.
- `Get-CmsThreadPoolMinLimit.ps1` — Q17 mechanism: `/dump?type=Full` via the sidecar,
  analyzed with the **pinned** `dotnet-dump` version's `threadpool` command inside an
  Alpine SDK container (the CMS image is linux-musl). The script verifies the
  `Worker Min Limit` label in the output instead of assuming it, records the parsed
  value plus `docker inspect` of `DOTNET_ThreadPool_ForceMinWorkerThreads` /
  `DOTNET_PROCESSOR_COUNT`, and must be run **outside timed burst windows** so dump
  collection does not contaminate timeout evidence.

- `Invoke-E1Baseline.ps1` — step 0.3 driver: `-Runs` cold+warm E1 runs for one resource
  profile, per-run logs, dump, classification (`e1-<profile>-index.json`).
- `Invoke-E2Sweep.ps1` — step 0.4 driver: the E2 warm concurrency sweep. For every point
  (`-Points`, default the spec's seven `TxN` pairs) it restarts CMS, runs an untimed
  serial warm-up (87 requests at `-WarmupConcurrency 1`, recorded as `…-warm`), then the
  timed burst (`…-burst`: `-Rounds 5`, `-InterRoundDelaySeconds 3`, samplers, body
  validation), captures the point's CMS/PostgreSQL logs, resolves the CMS container IP,
  measures the host↔Docker-VM clock offset, analyzes every round, and takes one dump
  outside the timed windows. Repetitions alternate ascending/descending point order.
  Refuses to start when the container CPU limits do not match `-ResourceProfile`.
  Outputs `e2-<profile>-index.json` and the flat `e2-<profile>-rounds.csv`.
- `dms-1556-analysis.psm1` — pure window analysis over the captures, used by the E2
  driver and usable on any retained run: M-conn from `log_connections` (connections the
  CMS container IP created to the CMS database, samplers and every other client
  reported separately), `pg_stat_activity` backends/states/waits, livemetrics
  thread-pool and Npgsql pool counters, docker-stats CPU/BlockIO, `pg_stat_io` deltas,
  host disk, PostgreSQL errors/slow statements/checkpoints, and CMS warning/error lines
  classified by *scope* (request-scoped via `RequestPath`, background, unattributed).
  Scope says where a line was emitted, not what caused a response status.

- `Set-Dms1556StackCondition.ps1` — step 0.5: recomposes CMS/PostgreSQL into one
  condition (`baseline`, `e3-certificates`, `e4-threads`, `e5-pool16`, `headroom`); each
  differs from `baseline` by exactly one overlay (`eng/docker-compose/local-control-*.yml`,
  `local-postgresql-headroom.yml`). `-RecreateDb` also recreates PostgreSQL and then
  restarts CMS (a recreated server terminates pooled connections); `-ClaimsMountSource`
  keeps the `.e2e-claims` mount of a stack provisioned by `build-dms.ps1 E2ETest`. It
  always brings up the dotnet-monitor sidecar first (CMS waits for it in suspend mode).
- `Invoke-ControlBatch.ps1` — step 0.5 driver: one block of runs for one condition,
  refusing to start unless the live container state matches the condition. Workloads
  `cold-87x87` (E1 shape), `warm-87x87` (serial warm-up, then the first concurrent burst),
  `warm-256x128` (stress; keep in its own blocks). Every run restarts CMS; round 1 gets
  managed stacks at `-StackCaptureOffsetsSeconds` (default 2/8/14 s) aligned with
  thread-pool threads/queue, pool busy, and connections received/authorized at the
  capture instant; a dump follows outside the timed windows. Outputs
  `e5-<block>-<condition>-index.json`, `-rounds.csv`, `-stacks.csv`.
- `Invoke-Step05Sequence.ps1` — the matched-pair sequence as run for step 0.5.
- `Invoke-E7Shard.ps1` — E7 overlay variant: runs a DMS E2E shard directly (same
  test-process context as `build-dms.ps1 E2ETest`, `--no-build`) against an already
  provisioned stack with samplers over the whole run, then summarizes the CMS side
  (profile bursts, handshakes, thread pool, pool, log scopes, trx counts).

### Recomposing the resource profile

The E1/E2 stacks were composed with `.env.e2e` and `DMS_CONFIG_LOG_LEVEL=Debug`; confirm
with `docker compose … config --hash config,db` against the running containers' labels
before recreating. P-runner-approx:

```powershell
cd eng/docker-compose
$env:DMS_CONFIG_LOG_LEVEL = 'Debug'
docker compose -f postgresql.yml -f local-config.yml -f local-config-diagnostics.yml `
  -f local-postgresql-diagnostics.yml -f local-resource-runner-approx.yml `
  --env-file .env.e2e -p dms-local up -d --no-deps db config
```

Omit `local-resource-runner-approx.yml` (and recreate the same two services) for P-dev.

## Typical experiment run (spec §3.4)

```powershell
cd eng/performance/dms-1556
./Invoke-CmsProfileBurst.ps1 -Seed -ProfileCount 87
./Get-CmsThreadPoolMinLimit.ps1                       # outside the timed window (Q17)
./Invoke-CmsProfileBurst.ps1 -TotalRequests 87 -MaxConcurrency 87 -Rounds 5 -Cold `
    -ValidateBodies -WithSamplers -SamplerDurationSeconds 240
```

The single coordinated command sequences: token mint → CMS restart → `/health`
readiness → sampler start against the restarted process → sampler readiness → rounds →
sampler stop → coverage validation. The summary JSON embeds the sampler report
(`samplerReport`), the burst window, and the cold preparation order; the run exits
non-zero if the required captures (livemetrics with System.Runtime /
Microsoft.AspNetCore.Hosting / Npgsql, pg-activity with sampler attribution) do not
cover the burst window. `Start-DiagnosticsSamplers.ps1` remains for ad-hoc observation
outside a burst.

PostgreSQL log evidence (M-conn) comes from `docker logs dms-postgresql` over the burst
window once `local-postgresql-diagnostics.yml` is applied; exclude the two
`dms1556-sampler-*` connections as described above.
