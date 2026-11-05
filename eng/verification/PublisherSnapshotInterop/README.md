# API Publisher snapshot interoperability harness

This harness proves that Ed-Fi API Publisher extracts from a DMS source with snapshot isolation, without `--ignoreIsolation`, on PostgreSQL and on SQL Server.
It runs by hand at release validation, not in CI.
Its unit tests, [`PublisherSnapshotInterop.Tests.ps1`](../tests/PublisherSnapshotInterop.Tests.ps1), run in CI with Docker and the Publisher mocked.

The entry point is [`Invoke-PublisherSnapshotInteropCheck.ps1`](../Invoke-PublisherSnapshotInteropCheck.ps1), and `Get-Help` on it describes every parameter.
The operator workflow the harness exercises is in [Snapshots and read replicas](../../../docs/SNAPSHOTS-AND-READ-REPLICAS.md).
The record of each release validation is in [`results/`](results/).

## What a run proves

A run starts one DMS stack on one database engine, seeds a source data store, and runs the pinned `edfialliance/ods-api-publisher:v1.4.0` image DMS to DMS on the stack's network.

- **Isolated** changes the source before and after it creates and registers a snapshot of it.
  The Publisher then runs without `--ignoreIsolation`, once with cursor paging and once with offset paging.
  Every source request must be served by the snapshot, all seven source surfaces must be exercised (GET-many, `/partitions`, cursor pages, `totalCount`, `/deletes`, `/keyChanges`, and `/availableChangeVersions`), and no post-snapshot change may reach the target.
  A control repeats both runs with `--ignoreIsolation=true` and must see the post-snapshot changes.
  If the control does not see them, the scenario fails, because the checks could not tell the two runs apart.
- **NoSnapshot** runs the Publisher without `--ignoreIsolation` against a source that has no snapshot.
  The run must fail the way the release notes describe: Snapshot Not Found on every read, exit code `2`, and nothing written to the target.

DMS logs which data store target (Primary or Snapshot) served each request, and the harness attributes every Publisher request from that log.
Every assertion goes to the run folder with its expected and observed values, and the script exits `1` when any assertion fails.

## Prerequisites

- PowerShell 7.
- Docker, with no containers in the `dms-local` or `dms-published` compose projects.
  The repository hardcodes those projects and their container names, so the harness refuses to start while either one has containers.
- Free memory in the Docker VM: about 5 GB for a SQL Server run, and much less for PostgreSQL.
  A SQL Server run holds four full DMS databases on one instance, which needs SQL Server's stock `MSSQL_MEMORY_LIMIT_MB` of 4096.
- Free host ports: 8080 for DMS, 8081 for the Configuration Service, and 5435 for PostgreSQL or 1435 for SQL Server.
  DMS can move to another port; see [A port is taken](#a-port-is-taken).
- On an arm64 host, such as a Mac with Apple silicon, Docker must be able to run amd64 images.
  The published Ed-Fi API and Configuration Service images, the Publisher image, and the SQL Server image are amd64 only and run under emulation there.
- A checkout whose relational mapping matches the DMS image under test.
  The harness provisions every data store with the SchemaTools built from this checkout, so check out the image's source revision or a later commit with the same relational mapping.

The harness makes these changes itself and records each one in the run's non-default settings:

- PostgreSQL `max_connections` goes from 100 to 300, because two data stores on one DMS exhaust the default.
- The DMS data store cache expires after 10 seconds instead of 600, so a new data store or snapshot takes effect in seconds.
- The DMS rate limit goes from 20000 to 100000 requests, because the Publisher exceeds the default.
- DMS logs its data store target selection at `Debug`, which is how each request is attributed.
- Every target gets the source's `schoolYearTypes`, because the Publisher never publishes them.

## Choosing the images

A release validation runs published images.
A locally built image advertises the Discovery version that `src/dms/Directory.Build.props` pins (8.0.0), not the version being released.

Both images are amd64 only, so pull them with `--platform linux/amd64`; on an arm64 host a plain pull fails with `no matching manifest for linux/arm64/v8`.

1. Pick the `edfialliance/ed-fi-api` tag to validate, and read the source revision it was built from:

   ```shell
   docker pull --platform linux/amd64 edfialliance/ed-fi-api:8.1.0-beta2.28
   docker image inspect edfialliance/ed-fi-api:8.1.0-beta2.28 \
     --format '{{index .Config.Labels "org.opencontainers.image.revision"}}'
   ```

2. Find the last Configuration Service change at or before that revision:

   ```shell
   git log -1 --format=%H <revision> -- src/config
   ```

3. Find the `edfialliance/ed-fi-api-configuration-service` image built from that commit, and pin it by digest.
   The Configuration Service publishes its own tags, which do not follow the Ed-Fi API tags, so its image cannot be derived from the Ed-Fi API tag.
   The `pre` tag is its newest prerelease.

   ```shell
   docker pull --platform linux/amd64 edfialliance/ed-fi-api-configuration-service:pre
   docker image inspect edfialliance/ed-fi-api-configuration-service:pre \
     --format '{{index .Config.Labels "org.opencontainers.image.revision"}} {{index .RepoDigests 0}}'
   ```

   Use the image when its revision is the commit from step 2.

## Running it

Run from the repository root in PowerShell 7, one engine at a time, and tear the stack down between engines.
This is the release validation for 8.1.0:

```powershell
$cs = "edfialliance/ed-fi-api-configuration-service@sha256:" +
    "e30a1d24a8061913555822accb099cce859cc5e2069ac3fc89e9bdcc1f8cb1c6"

./eng/verification/Invoke-PublisherSnapshotInteropCheck.ps1 -Engine postgresql -Scenario All `
    -DmsImageTag 8.1.0-beta2.28 -ConfigurationServiceImage $cs

# Tear down (see Tearing down), then:
./eng/verification/Invoke-PublisherSnapshotInteropCheck.ps1 -Engine mssql -Scenario All `
    -DmsImageTag 8.1.0-beta2.28 -ConfigurationServiceImage $cs
```

Without `-DmsImageTag`, the harness runs the locally built `ed-fi-api-local` and `ed-fi-api-config-local` images, and `-Rebuild` builds them first.
That is useful while changing the harness, but it is not release evidence, because of the Discovery version.

| `-Scenario` | What runs |
| --- | --- |
| `StackOnly` | The stack, the target data store, and the clients; no Publisher run. |
| `Isolated` | The snapshot isolation proof and its control. |
| `NoSnapshot` | The Publisher against a source with no snapshot. |
| `All` (default) | `Isolated`, then `NoSnapshot`, on one stack. |

Other parameters:

- `-SkipStackStart` reuses a stack an earlier invocation started, after checking it is usable and seeded.
- `-ExpectationOverride` replaces a pinned expectation with a wrong one, such as `@{ PublisherExitCode = 1 }`, to prove the verifier can fail: the matching assertion must report FAIL and the script must exit `1`.
- `-PublisherImage` and `-RunRoot` override the pinned Publisher image and the folder that holds the runs.

## What a pass looks like

Abridged output of the PostgreSQL run in the 8.1.0 record, with the repository path shortened:

```text
[interop] Starting the postgresql stack with bootstrap-published-dms.ps1 (seed template Minimal).
[interop] Raising PostgreSQL max_connections to 300.
[interop] Copied 47 schoolYearTypes from the source to the target.
[interop] Verified DMS logs 'Selected Primary target for data store 1'.
[interop] Isolated 2: arranging the source (baseline into both targets, then the snapshot and the changes).
[interop] Arrangement 3: creating and registering the snapshot.
[interop] Snapshot state 'Active' observed after 0.6 seconds.
[interop] Arrangement 4: post-snapshot changes on the primary (I2, U2, D2, K2).
[interop] Isolated B: Run B (snapshot, no --ignoreIsolation), cursor paging.
[interop] Publisher run 'b-cursor' exited 0 after 5.7 s.
[interop] Isolated C: Run C (control, --ignoreIsolation=true), cursor paging.
[interop] Publisher run 'c-cursor' exited 0 after 5.9 s.
[interop] PASS: Run B selected the Snapshot for the source data store (expected True, observed True)
[interop] PASS: Run B target reflects I2 (expected False, observed False)
[interop] PASS: Run C target reflects I2 (expected True, observed True)
[interop] NoSnapshot 4: Publisher run without --ignoreIsolation into the fresh target.
[interop] Publisher run 'no-snapshot' exited 2 after 4.7 s.
[interop] PASS: Publisher exit code (expected 2, observed 2)
[interop] PASS: DMS count reads equal the count reads the Publisher logged (expected 384, observed 384)
[interop] Run folder: <repository>/eng/verification/PublisherSnapshotInterop/runs/20261105T005759Z-postgresql
```

A pass is exit code `0` with every assertion line reading `PASS`: 64 assertions for `Isolated` and 20 for `NoSnapshot`.
A failure ends with `[interop] FAILED: <n> assertion(s) did not match: <names>` and exit code `1`.

## Run time

Measured with `-Scenario All` on the published `8.1.0-beta2.28` images, on a Mac with Apple silicon and an 8 GB Docker VM, with the images already pulled:

| Engine | First run | Second run |
| --- | --- | --- |
| PostgreSQL | 5 min 23 s | 5 min 8 s |
| SQL Server | 14 min 18 s | 13 min 45 s |

Most of each run is starting the stack and provisioning its four databases; on that Mac the SQL Server image runs under emulation.
Each Publisher run takes 4 to 30 seconds.

## Results

Each run writes a folder `runs/<UTC timestamp>-<engine>/`, which git ignores:

- `stack.env` is the environment file the stack ran with: `eng/docker-compose/.env.example` plus the harness's settings.
  It holds the stack's development credentials, so never commit it.
- `manifest.json` holds the versions, image digests and source revisions, the non-default settings, and the data store and client ids.
  It never holds keys, secrets, tokens, or connection strings.
- `results-Isolated.json` and `results-NoSnapshot.json` (`results.json` when one scenario runs) hold every assertion: its name, expected and observed values, outcome, and evidence.
- `publisher-<run>/` holds each Publisher run: its redacted arguments, console and Publisher logs, exit code, and the DMS requests attributed to it.

The validation record is generated from one run folder per engine and committed to `results/`.
The runs cannot observe the release gate, so its status (`Satisfied` or `Pending`) and one line of evidence per gate item are inputs.
Check each gate item when you write the record; this is the 8.1.0 record's input:

```powershell
Import-Module ./eng/verification/PublisherSnapshotInterop/PublisherSnapshotInterop.psm1 -Force
$runs = "./eng/verification/PublisherSnapshotInterop/runs"
Write-InteropValidationRecord -RunFolder "$runs/<postgresql run>", "$runs/<mssql run>" `
    -GateStatus Satisfied -GateEvidence @(
        "DMS-1369 is Done (PR #1257, ``55e914e2f``).",
        "DMS-1371 is Done (DMS-side PR #1205, ``b0b68972c``; the upstream packages are at ``1.0.335``)."
    )
```

It writes `results/validation-<Discovery version>.md`, for example `validation-8.1.0.md`.
It refuses two runs of one engine, runs that advertise different DMS versions or used different Publisher images, and any run made with `-ExpectationOverride`.
It also refuses to write a record that holds a secret, a connection string, or a host path.

## Tearing down

From `eng/docker-compose`, with the run's environment file:

```powershell
# Published images (-DmsImageTag)
pwsh ./bootstrap-published-dms.ps1 -d -v -EnvironmentFile <run folder>/stack.env [-DatabaseEngine mssql]

# Locally built images
pwsh ./bootstrap-local-dms.ps1 -d -v -EnvironmentFile <run folder>/stack.env [-DatabaseEngine mssql]
```

`-v` removes the volumes, so the next run starts from a fresh, seeded source.

## Troubleshooting

### The harness refuses to start a stack

`Refusing to start a stack: 'dms-local' or 'dms-published' containers already exist` means an earlier stack is still there.
Tear it down, or pass `-SkipStackStart` to reuse a stack an earlier invocation started.

### A port is taken

When 8080 is taken on the host, compose fails with `port is already allocated`.
Move DMS to a free port in the shell that runs the harness:

```powershell
$env:DMS_HTTP_PORTS = "8090"
```

Compose takes a variable set in the shell over the environment file, so the harness writes this one into `stack.env` too, addresses DMS and points the Publisher at the new port, and records it as a non-default setting.
For the same reason, do not set other stack variables, such as `DMS_IMAGE_TAG`, in that shell: compose would use them instead of the harness's values.

The Configuration Service cannot move: the stack's own URLs for it name port 8081, so DMS could not reach it.
The harness refuses a moved `CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS`, so free port 8081 instead.
The database ports, 5435 and 1435, must be free too.

### SQL Server error 701 or a provisioning timeout

`SQL Server ran out of memory compiling the schema DDL (error 701)` means SQL Server could not compile the largest schema DDL batch of one of its four databases.
A retry does not clear it.
Leave `MSSQL_MEMORY_LIMIT_MB` at its 4096 default, because the third or fourth database fails below it.
Then free memory in the Docker VM and start a new run.
On an 8 GB Docker VM shared with other long-running stacks, SQL Server lowers its own memory target, and the same batch fails with error 701 or a 300-second timeout on every run until those stacks stop.

### DMS answers 503 after provisioning

A 503 with `EffectiveSchemaHash mismatch for data store <id>` in the DMS log means the checkout's SchemaTools provisioned a relational mapping different from the image's.
Check out the image's source revision, or a later commit with the same relational mapping, and start a new run.

### Discovery reads the wrong version

`The published image tagged '<tag>' should advertise a <line>.x Discovery version, but Discovery reads '<version>'` means the container answering on the DMS port is not the image named by `-DmsImageTag`.
Look for another stack on that port, or for a `DMS_IMAGE_TAG` set in the shell.

### The Publisher does not send `Use-Snapshot`

The Publisher adds `Use-Snapshot: true` to its source requests only when the source's Discovery `version` is 7 or higher.
A DMS that advertised a lower version would be read from its Primary, and `Isolated` would fail `Run B selected the Snapshot for the source data store`.
Every DMS 8 image advertises 8, and the Discovery document at the DMS root shows it.

### Snapshot waits and the data store cache

DMS caches data store configuration, for 600 seconds by default.
The harness sets 10 seconds, so a registered snapshot reads as Active, and a removed one as Absent, within about 11 seconds.
A stack the harness did not start keeps the default, so its snapshot waits time out with `Timed out after <n> seconds waiting for the snapshot state`.
Outside the harness, reads reach a newly registered snapshot only after the cache expires, which took about 559 seconds on a default stack.
With `DataStoreCacheRefreshEnabled` set to `false`, or a cache expiration of `0` or less, they reach it only after DMS restarts.

### What the Publisher logs without a snapshot

The `NoSnapshot` scenario pins what an operator sees when the Publisher runs against a DMS source with no snapshot and no `--ignoreIsolation`.
Abridged, the Publisher logs:

```text
[WARN] ...EdFiApiSourceCurrentChangeVersionProvider - Unable to get current change version from source API
       at '...availableChangeVersions' (response status: NotFound). Full synchronization will always be
       performed against this source, ...
[INFO] ...EdFiApiSourceCapabilities - Source API does not expose /partitions (status 404); offset/limit
       paging will be used.
[EROR] ...EdFiApiSourceTotalCountProvider - /ed-fi/achievementCategoryDescriptors: NotFound - Unable to
       obtain total count due to request failure. ... Response content: {"detail":"Snapshot not found.", ...
[FATL] ...ChangeProcessor - ... PublishingFailedException: Processing completed, but 384 source read
       error(s) mean an unknown number of documents was never attempted (of 384 error(s) in total).
```

The Publisher exits `2` and writes nothing to the target.
Register a snapshot of the source first, as the operator guide describes, or run with `--ignoreIsolation=true` and accept an extraction without isolation.

### The seed load hits the `Stack empty` race

The seed loader sometimes fails with `Stack empty`, a known BulkLoadClient race.
The harness retries the seed up to three times and logs each retry; if every retry fails, tear down and start a new run.

### Other `-SkipStackStart` refusals

- `-SkipStackStart found PostgreSQL max_connections below 300` means the stack was not started by the harness; start one without `-SkipStackStart`.
- `The source has no schoolYearTypes` means the source is not seeded; start a new stack on a clean volume.

### The Publisher gets 429 responses outside the harness

The harness raises the DMS rate limit, because the Publisher exceeds the default of 20000 requests.
Against a stack with the default limit, raise `RateLimit__PermitLimit` or run the Publisher with `--enableRateLimit=true`.
