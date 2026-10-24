# DMS-1490 A5 timing collection recipe

The procedure for measuring the narrow credential lookup (spec §17, step A5.5) at both HTTP endpoints.

**Nothing in this folder has been run for A5.** Pilots, planning and final runs each need their own approval. The recipe stops after the pilots and their planning analysis. It never proceeds to a final run on its own.

The scripts here produce evidence. They are not part of the product, and no ordinary test or build runs them.

| File | Role |
| --- | --- |
| `prepare-target.ps1` | Extracts one pinned commit with `git archive` into an empty folder outside every worktree. With `-BuildHostRelease`, it also builds the Configuration Service in Release there and fingerprints its production assemblies into `target.json`. |
| `lane-postgresql.ps1` | One PostgreSQL run. Builds the target's `cs-local` stack without cache, records provenance before and after, runs the collector once, then tears the stack down. |
| `lane-localdb.ps1` | One SQL Server run on LocalDB, with the target's Configuration Service as a host process. Recreates the database, starts a new process, records provenance before and after, runs the collector once, then stops the process. |

## Identities

| Role | Commit | Notes |
| --- | --- | --- |
| A5 target (the gate) | `b2b8d5df47e5e48c46f9ca26fe0f2a58b787c399` | The credential path on both shipped repositories |
| Baseline (descriptive only) | `9a19802ddae9b23b103cd92dd6c27a8a48814cb7` | Application code of `2c70ee881`, as measured in P3.2 |
| Collector and recipe | The commit that adds this folder | Each run records its own `collector.commit`; the collector worktree must be clean |
| Analysis | `../analyze_timing.py`, method `1`, SHA-256 `27d881d666a429f60e6cdd047d5bd258c87502491bc86b31869d500f87596b9d` | Unchanged |

Targets are always built from `git archive` extracts in their own folders. The collector worktree's source is never switched to a target.

## Prerequisites

- Windows, PowerShell 7, the .NET 10 SDK, Git, `tar`, and Python 3.12 with numpy 2.5.2.
- **PostgreSQL lane:** Docker Desktop running; no `cs-local` container and no `ed-fi-api-config-local` image present.
- **SQL Server lane:** LocalDB with `sqllocaldb` and `sqlcmd` on the path. The default instance name is `DMS1490`.
- **Host state:** idle, on AC power, with the power plan recorded. No other collection, simulation, analysis or build runs during a collection.

## Paths

| Name | Meaning |
| --- | --- |
| `<Repository>` | The collector worktree, clean, at the collector commit |
| `<Collection>` | `<Repository>\reference\design\configuration-service\DMS-1490-timing-evidence\collection` |
| `<Work>` | A folder outside the repository, never committed: targets, database files, service output, setup logs, exception text |
| `<Pilot>` / `<Final>` | Evidence folders in the repository: `…\DMS-1490-timing-evidence\a5-pilot` and `…\a5-final` |

## Secrets

The lanes read secrets only from the target's `eng/docker-compose/.env.config.e2e` (PostgreSQL, through the target's own setup script) and `.env.config.mssql.e2e` (SQL Server). Provenance names their keys, never their values.

Evidence holds only:

- timing samples and headers;
- provenance with the exact settings allowlist (`AppSettings__Datastore`, `AppSettings__IdentityProvider`, `AppSettings__MultiTenancy`, `IdentitySettings__ClientSecretHashingIterations`, `IdentitySettings__AllowRegistration`), container or process identity, and the SHA-256 of `EdFi.DmsConfigurationService.Frontend.AspNetCore.dll`, `.Backend.dll`, `.Backend.OpenIddict.dll`, `.Backend.Postgresql.dll` and `.Backend.Mssql.dll` as measured;
- lane logs that name an exception's type only.

Full exception text, setup logs and service output stay in `<Work>`. Before anything is committed, the evidence folder is scanned for the env files' secret values and for credential patterns.

## Stage 0: prepare (no measurement)

```
pwsh -NoProfile -File <Collection>\prepare-target.ps1 -RepositoryRoot <Repository> -Commit b2b8d5df47e5e48c46f9ca26fe0f2a58b787c399 -Destination <Work>\targets\a5 -BuildHostRelease
pwsh -NoProfile -File <Collection>\prepare-target.ps1 -RepositoryRoot <Repository> -Commit 9a19802ddae9b23b103cd92dd6c27a8a48814cb7 -Destination <Work>\targets\baseline -BuildHostRelease
dotnet build <Repository>\src\config\tests\EdFi.DmsConfigurationService.Tests.E2E\EdFi.DmsConfigurationService.Tests.E2E.csproj -c Release
dotnet build-server shutdown
```

`.dockerignore` excludes `**/bin/` and `**/obj/`, so one prepared folder per target serves both lanes.

## Stage 1: pilots (A5 target only; needs its own approval)

| Run | Lane | Label | Rounds | Warm-up | Seed |
| --- | --- | --- | --- | --- | --- |
| 1 | PostgreSQL | `pilot-a5` | 200 | 1,200 | 301 |
| 2 | SQL Server | `pilot-a5` | 200 | 1,200 | 302 |

```
pwsh -NoProfile -File <Collection>\lane-postgresql.ps1 -RunLabel pilot-a5 -Rounds 200 -WarmupRounds 1200 -Seed 301 -OutputDir <Pilot> -CollectorRoot <Repository> -TargetRoot <Work>\targets\a5 -WorkRoot <Work>\runs
pwsh -NoProfile -File <Collection>\lane-localdb.ps1 -RunLabel pilot-a5 -Rounds 200 -WarmupRounds 1200 -Seed 302 -OutputDir <Pilot> -CollectorRoot <Repository> -TargetRoot <Work>\targets\a5 -WorkRoot <Work>\runs
```

Planning, offline, after both pilots. Run the Python commands from `<Collection>`. Compute all candidate N before any choice. These are A1's rule: the smallest candidate that meets both the precision heuristic and all three C2 expectations under the committed generator.

```
python -B ..\analyze_timing.py selftest
python -B ..\analyze_timing.py analyze  --csv <pilot csv> --delta 250
python -B ..\analyze_timing.py analyze  --csv <pilot csv> --delta 250 --arm-a unapproved_correct_secret
python -B ..\analyze_timing.py plan     --csv <pilot csv> --delta 250
python -B ..\analyze_timing.py fit      --csv <pilot csv>
python -B ..\analyze_timing.py simulate --rounds <500|1000|2000|4000> --delta 250 --mu <fit> --sigma <fit> --rho <fit> --drift-sd <fit> --replicates 500 --resamples 5000 --seed 1
```

Every command and output is saved under `<Pilot>\analysis`, with `commands.log`. Simulations never overlap a collection.

**Stop here.** Submit the pilot results, diagnostics and the proposed final N and L per backend to the board, and wait for the pre-registration to be approved.

## Stage 2: finals (only after the pre-registration is approved)

N per backend comes from the approved plan, with `L = ceil(N^(1/3))`. Each run gets a fresh stack (PostgreSQL) or a recreated database and new process (SQL Server).

| Order | Lane | Target | Label | Seed |
| --- | --- | --- | --- | --- |
| 1 | PostgreSQL | A5 | `final-a5` | 303 |
| 2 | PostgreSQL | baseline | `final-baseline` | 305 |
| 3 | SQL Server | baseline | `final-baseline` | 306 |
| 4 | SQL Server | A5 | `final-a5` | 304 |

The commands are the Stage 1 commands with `-RunLabel`, `-Rounds <approved N>`, `-WarmupRounds 1200`, `-Seed`, `-TargetRoot <Work>\targets\a5` or `<Work>\targets\baseline`, and `-OutputDir <Final>`.

Analysis per run uses the fixed method:

- `analyze` at δ = 250 µs for absent − wrong_secret and for unapproved − wrong_secret, with both L and 2L;
- `..\p3.2-final\analysis\diagnostics-by-quarter.py`;
- `observer`.

**The gate is the A5 runs alone.** The baseline runs are a descriptive comparison under the same recorded conditions, not causal isolation.

## Rules

- One attempt per run. A setup failure, an aborted collection, a contract mismatch or a missing provenance check is recorded, kept and brought back for review. It is not retried.
- No measured round is discarded. Warm-up is not extended after a run, and no seed, tolerance or N is chosen after seeing a result.
- Quarter diagnostics and both block lengths are reported. An unresolved diagnostic goes back for review.
- If either backend's A5 final run is NOT EQUIVALENT or INCONCLUSIVE: record it and stop.
- If both are EQUIVALENT, AC2 is reported as "residual bound met at δ; acceptance pending" and goes to the Q18 product decision, together with the observer model and its assumptions. A bounded median difference is not reported as literal indistinguishability.
