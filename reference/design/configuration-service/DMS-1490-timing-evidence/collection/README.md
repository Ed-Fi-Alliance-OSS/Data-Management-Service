# DMS-1490 A5 timing collection recipe

The procedure for measuring the narrow credential lookup (spec §17, step A5.5) at both HTTP endpoints.

**Nothing in this folder has been run for A5.** Pilots, planning and final runs each need their own approval. The recipe stops after the pilots and their planning analysis. It never proceeds to a final run on its own.

The scripts here produce evidence. They are not part of the product, and no ordinary test or build runs them.

| File | Role |
| --- | --- |
| `prepare-target.ps1` | Extracts one pinned commit with `git archive` into an empty folder that neither lies inside nor contains any worktree, refusing before it creates anything. With `-BuildHostRelease`, it also builds the Configuration Service in Release there and fingerprints its production assemblies into `target.json`. |
| `lane-postgresql.ps1` | One PostgreSQL run: builds the target's `cs-local` stack without cache, verifies the deployment, runs the collector once, verifies the run and the deployment again, then tears the stack down |
| `lane-localdb.ps1` | One SQL Server run on LocalDB, with the target's Configuration Service as a host process: recreates the database, starts a new process with a controlled launch environment, verifies, runs the collector once, verifies again, then stops the process |
| `CollectionLane.psm1` | The checks both lanes and the preparer rely on: path normalization, root and worktree separation, exact fingerprints, pre-registered settings, deployment comparison, collector-run verification, the power reader and power condition P |
| `collection.Tests.ps1` | Pester checks of the lanes' failure paths, the power checks and the preparer's folder refusals, with synthetic command output and synthetic power readings. No deployment, database or collector is used. One test reads the host's power status once, to check only that the native structure marshals. |

Each lane exits **0 only when every stage succeeded**. Any failure exits 1 after cleanup. Failure stages include: a power refusal, setup, an unhealthy service, an unexpected setting value, a missing or malformed fingerprint, a collector that failed or ran anything other than the fixture's two tests, power condition P unmet after the collector exits, missing or mismatched samples, a deployment that changed during collection, and a failed teardown or process stop.

## Power condition P (protocol E1, partly implemented)

Spec §18.1. Each lane reads `GetSystemPowerStatus` and `powercfg /getactivescheme`. P holds only when:

- the native call succeeded;
- `ACLineStatus` is 1 (online);
- `SystemStatusFlag` is 0 (battery saver off);
- the scheme query succeeded;
- the scheme is Balanced (`381b4222-f694-41f0-9685-ff5bb260df2e`).

Unknown (255) and every failure are not P.

| Check | When | Not P |
| --- | --- | --- |
| `before_setup` | Before any stack, LocalDB or database work | **Refusal**: nothing was set up and nothing is collected |
| `before_launch` | Immediately before the collector process starts | **Refusal**: the stack is torn down or the process stopped, and nothing is collected |
| `after_exit` | As soon as the collector exits, whatever its outcome | The attempt **fails** (`power-after-exit`), recorded beside any other failure |

- **The collector launch is the attempt boundary.** Provenance records `attempt_started`.
- **Refusals are not attempts.** Each is appended as one JSON line to `refusals.log` in the run's staging folder (`<Stage>\pilot` or `<Stage>\final`), with the check, the unmet parts of P and the raw power fields. The file is never rewritten. After correcting the environment, for example by plugging in, the same run may be started again.
- **What is recorded:** every check, met or not, in `power_checks` in provenance. The scheme query's own output is never recorded, only a fixed reason code for a failure. Exception text from a failed observation goes to `<Work>\runs` only.
- **What is not yet implemented:** the sampler's readiness and continuous coverage (§18.2). Provenance therefore records `environment_protocol.complete = false`, and no run of these lanes is a complete E1 run. No collection is authorised with them.

## Identities

| Role | Commit | Notes |
| --- | --- | --- |
| A5 target (the gate) | `b2b8d5df47e5e48c46f9ca26fe0f2a58b787c399` | The credential path on both shipped repositories |
| Baseline (descriptive only) | `9a19802ddae9b23b103cd92dd6c27a8a48814cb7` | Application code of `2c70ee881`, as measured in P3.2 |
| Collector and recipe | The commit that holds this recipe at run time | Each run records its own `collector.commit`, `lane_script_sha256` and `lane_module_sha256`; the collector checkout must be clean |
| Analysis | `../analyze_timing.py`, method `1`, SHA-256 `27d881d666a429f60e6cdd047d5bd258c87502491bc86b31869d500f87596b9d` | Unchanged |

Targets are always built from `git archive` extracts in their own folders. The collector checkout's source is never switched to a target.

## Prerequisites

- Windows, PowerShell 7, the .NET 10 SDK, Git, `tar`, and Python 3.12 with numpy 2.5.2.
- **PostgreSQL lane:** Docker Desktop running; no `cs-local` container and no `ed-fi-api-config-local` image present.
- **SQL Server lane:** LocalDB with `sqllocaldb` and `sqlcmd` on the path. The default instance name is `DMS1490`.
- **Host state:** idle, plugged in, battery saver off and the Balanced plan active; the lanes refuse otherwise (see Power condition P). No other collection, simulation, analysis or build runs during a collection.

## Folders

Before anything is created, the folders are checked as follows:

- **Lanes:** each refuses a staging, work or target folder that equals, contains or lies inside the collector checkout. It also refuses a staging folder that equals, contains or lies inside the work folder or the target. The work folder and the target may share a parent, as `<Work>\runs` and `<Work>\targets\…` do.
- **`prepare-target.ps1`:** refuses a destination that equals, contains or lies inside any worktree registered to the repository, as `git worktree list` reports them.
- **Paths:** every folder is first resolved to its full path, relative to the current location. That form is used both for the checks and for every later file operation. The 8.3 short names of existing segments are expanded. A path through an existing junction or symbolic link is refused, because it lets two strings name the same folder.

Every run of a stage therefore leaves the checkout clean, and the source-cleanliness check stays meaningful for the next run.

| Name | Meaning |
| --- | --- |
| `<Repository>` | The collector checkout, clean, at the collector commit |
| `<Collection>` | `<Repository>\reference\design\configuration-service\DMS-1490-timing-evidence\collection` |
| `<Work>` | Private, outside the repository, never committed: `targets\a5`, `targets\baseline`, `runs` (database files, service and collector output, setup logs, exception text) |
| `<Stage>` | The evidence staging folder, outside the repository and outside `<Work>`. It holds only samples, headers, provenance and the lanes' status logs, with `pilot` and `final` subfolders. |
| `<Evidence>` | `<Repository>\reference\design\configuration-service\DMS-1490-timing-evidence`. It receives the reviewed staging files in `a5-pilot` or `a5-final` only after a stage ends. |

## Secrets

The lanes read secrets only from the target's `eng/docker-compose/.env.config.e2e` (PostgreSQL, through the target's own setup script) and `.env.config.mssql.e2e` (SQL Server). Provenance names their keys, never their values.

`<Stage>` receives only:

- timing samples and headers;
- provenance with the exact settings allowlist (`AppSettings__Datastore`, `AppSettings__IdentityProvider`, `AppSettings__MultiTenancy`, `IdentitySettings__ClientSecretHashingIterations`, `IdentitySettings__AllowRegistration`), container or process identity, and the SHA-256 of `EdFi.DmsConfigurationService.Frontend.AspNetCore.dll`, `.Backend.dll`, `.Backend.OpenIddict.dll`, `.Backend.Postgresql.dll` and `.Backend.Mssql.dll` as measured;
- status logs that hold fixed status lines, checked counts and exception type names.

The collector's own output and results file, setup logs, service output and exception text go to `<Work>\runs`. Before staging files are copied into the repository, they are scanned for the env files' secret values and for credential patterns. That scan is an additional check; the folder separation is the primary one.

## Stage 0: prepare (no measurement)

```
pwsh -NoProfile -File <Collection>\prepare-target.ps1 -RepositoryRoot <Repository> -Commit b2b8d5df47e5e48c46f9ca26fe0f2a58b787c399 -Destination <Work>\targets\a5 -BuildHostRelease
if ($LASTEXITCODE -ne 0) { throw 'Stop: preparing the A5 target failed.' }
pwsh -NoProfile -File <Collection>\prepare-target.ps1 -RepositoryRoot <Repository> -Commit 9a19802ddae9b23b103cd92dd6c27a8a48814cb7 -Destination <Work>\targets\baseline -BuildHostRelease
if ($LASTEXITCODE -ne 0) { throw 'Stop: preparing the baseline target failed.' }
dotnet build <Repository>\src\config\tests\EdFi.DmsConfigurationService.Tests.E2E\EdFi.DmsConfigurationService.Tests.E2E.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Stop: building the collector failed.' }
dotnet build-server shutdown
git -C <Repository> status --porcelain   # must print nothing
```

`.dockerignore` excludes `**/bin/` and `**/obj/`, so one prepared folder per target serves both lanes.

## Stage 1: pilots (A5 target only; needs its own approval)

| Run | Lane | Label | Rounds | Warm-up | Seed |
| --- | --- | --- | --- | --- | --- |
| 1 | PostgreSQL | `pilot-a5` | 200 | 1,200 | 301 |
| 2 | SQL Server | `pilot-a5` | 200 | 1,200 | 302 |

```
pwsh -NoProfile -File <Collection>\lane-postgresql.ps1 -RunLabel pilot-a5 -Rounds 200 -WarmupRounds 1200 -Seed 301 -OutputDir <Stage>\pilot -CollectorRoot <Repository> -TargetRoot <Work>\targets\a5 -WorkRoot <Work>\runs
if ($LASTEXITCODE -ne 0) { throw 'Stop: the PostgreSQL pilot failed; keep everything and report it.' }
pwsh -NoProfile -File <Collection>\lane-localdb.ps1 -RunLabel pilot-a5 -Rounds 200 -WarmupRounds 1200 -Seed 302 -OutputDir <Stage>\pilot -CollectorRoot <Repository> -TargetRoot <Work>\targets\a5 -WorkRoot <Work>\runs
if ($LASTEXITCODE -ne 0) { throw 'Stop: the SQL Server pilot failed; keep everything and report it.' }
```

Planning, offline, after both pilots. Run the Python commands from `<Collection>`, with outputs under `<Stage>\pilot\analysis` and every command in `commands.log`. Compute all candidate N before any choice; this is A1's rule, the smallest candidate that meets both the precision heuristic and all three C2 expectations under the committed generator.

```
python -B ..\analyze_timing.py selftest
python -B ..\analyze_timing.py analyze  --csv <pilot csv> --delta 250
python -B ..\analyze_timing.py analyze  --csv <pilot csv> --delta 250 --arm-a unapproved_correct_secret
python -B ..\analyze_timing.py plan     --csv <pilot csv> --delta 250
python -B ..\analyze_timing.py fit      --csv <pilot csv>
python -B ..\analyze_timing.py simulate --rounds <500|1000|2000|4000> --delta 250 --mu <fit> --sigma <fit> --rho <fit> --drift-sd <fit> --replicates 500 --resamples 5000 --seed 1
```

Simulations never overlap a collection. After the stage, run the secret scan over `<Stage>\pilot`. Then copy exactly its files (samples, headers, provenance, status logs, `analysis\`) into `<Evidence>\a5-pilot` and commit them, with a checksum list.

**Stop here.** Submit the pilot results, diagnostics and the proposed final N and L per backend to the board, and wait for the pre-registration to be approved.

## Stage 2: finals (only after the pre-registration is approved)

N per backend comes from the approved plan, with `L = ceil(N^(1/3))`. Each run gets a fresh stack (PostgreSQL) or a recreated database and new process (SQL Server). Every command is followed by the same exit-status check, which stops the sequence.

| Order | Lane | Target | Label | Seed |
| --- | --- | --- | --- | --- |
| 1 | PostgreSQL | A5 | `final-a5` | 303 |
| 2 | PostgreSQL | baseline | `final-baseline` | 305 |
| 3 | SQL Server | baseline | `final-baseline` | 306 |
| 4 | SQL Server | A5 | `final-a5` | 304 |

The commands are the Stage 1 commands with `-RunLabel`, `-Rounds <approved N>`, `-WarmupRounds 1200`, `-Seed`, `-TargetRoot <Work>\targets\a5` or `<Work>\targets\baseline`, and `-OutputDir <Stage>\final`.

Analysis per run uses the fixed method:

- `analyze` at δ = 250 µs for absent − wrong_secret and for unapproved − wrong_secret, with both L and 2L;
- `..\p3.2-final\analysis\diagnostics-by-quarter.py`;
- `observer`.

After the stage: the secret scan, then copy into `<Evidence>\a5-final`.

**The gate is the A5 runs alone.** The baseline runs are a descriptive comparison under the same recorded conditions, not causal isolation.

## Rules

- One attempt per run. A nonzero exit is a stop: the staging files, provenance and private diagnostics are kept and brought back for review. Nothing is retried.
- No measured round is discarded. Warm-up is not extended after a run, and no seed, tolerance or N is chosen after seeing a result.
- Quarter diagnostics and both block lengths are reported. An unresolved diagnostic goes back for review.
- If either backend's A5 final run is NOT EQUIVALENT or INCONCLUSIVE: record it and stop.
- If both are EQUIVALENT, AC2 is reported as "residual bound met at δ; acceptance pending" and goes to the Q18 product decision, together with the observer model and its assumptions. A bounded median difference is not reported as literal indistinguishability.

## Checking the recipe without collecting

```
pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.7.1; Invoke-Pester -Path <Collection>\collection.Tests.ps1 -Output Detailed"
```
