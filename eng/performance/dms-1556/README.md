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
  `-Cold`, `-ValidateBodies`). Per Q18 the in-flight interval runs from semaphore
  admission through response-body completion, the summary reports the measured peak
  overlap, and runs are labeled `catalog-87x87` / `stress-256x128` / `workload-TxN`.
- `Start-DiagnosticsSamplers.ps1` — timed background samplers: `pg_stat_activity`
  (~250 ms) and `pg_stat_io`/`pg_stat_bgwriter` (~1 s) via in-container psql loops,
  `docker stats` stream, Windows host disk counters (the runner equivalent is
  `/proc/diskstats`), and a dotnet-monitor `/livemetrics` capture. Start immediately
  before a burst with a `-DurationSeconds` covering it.
- `Get-CmsThreadPoolMinLimit.ps1` — Q17 mechanism: `/dump?type=Full` via the sidecar,
  analyzed with the **pinned** `dotnet-dump` version's `threadpool` command inside an
  Alpine SDK container (the CMS image is linux-musl). The script verifies the
  `Worker Min Limit` label in the output instead of assuming it, records the parsed
  value plus `docker inspect` of `DOTNET_ThreadPool_ForceMinWorkerThreads` /
  `DOTNET_PROCESSOR_COUNT`, and must be run **outside timed burst windows** so dump
  collection does not contaminate timeout evidence.

## Typical experiment run (spec §3.4)

```powershell
cd eng/performance/dms-1556
./Invoke-CmsProfileBurst.ps1 -Seed -ProfileCount 87
./Get-CmsThreadPoolMinLimit.ps1                       # outside the timed window
Start-Job { ./Start-DiagnosticsSamplers.ps1 -DurationSeconds 120 -Label e1-run1 }
./Invoke-CmsProfileBurst.ps1 -TotalRequests 87 -MaxConcurrency 87 -Rounds 5 -Cold -ValidateBodies
```

PostgreSQL log evidence (M-conn) comes from `docker logs dms-postgresql` over the burst
window once `local-postgresql-diagnostics.yml` is applied.
