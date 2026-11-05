# API Publisher snapshot interoperability validation record

DMS 8.1.0, validated against Ed-Fi API Publisher v1.4.0 with snapshot isolation.
The record states what the harness ran, what each run proved, and what is proven by the CI suites instead.
Regenerate it with `Write-InteropValidationRecord` from the module `eng/verification/PublisherSnapshotInterop/PublisherSnapshotInterop.psm1`, passing the run folder of each engine and the release gate status with its evidence.

## Summary

| Engine | Run folder | Isolated | NoSnapshot | Started |
| --- | --- | --- | --- | --- |
| PostgreSQL | `20261105T005759Z-postgresql` | PASS (64 passed, 0 failed) | PASS (20 passed, 0 failed) | 2026-11-05 00:57:59 UTC |
| SQL Server | `20261105T010330Z-mssql` | PASS (64 passed, 0 failed) | PASS (20 passed, 0 failed) | 2026-11-05 01:03:30 UTC |

## Release gate status

The release gate for the runtime changes is tracked in `reference/design/backend-redesign/epics/10-update-tracking-change-queries/EPIC.md` under Follow-on Stories (spawned by DMS-1190).

- DMS-1369 is Done (PR #1257, `55e914e2f`).
- DMS-1371 is Done (DMS-side PR #1205, `b0b68972c`; the upstream packages are at `1.0.335`).

The gate is satisfied on main.
It is re-confirmed when the 8.1 release candidate is assembled.

## Acceptance criteria outside the per-engine runs

Cited means the criterion is proven by the named artifact, not by these runs.
A cited row is not a PASS of this record.

| # | Criterion | Outcome | Evidence |
| --- | --- | --- | --- |
| 6 | Both engines: isolated run, post-snapshot exclusion, and per-surface assertions on PostgreSQL and on SQL Server | PASS | isolated run, post-snapshot exclusion, and per-surface assertions on both engines; see each engine section |
| 7 | Per-request contract still green | cited | the CI scenarios in Per-request contract below |
| 8 | Operator workflow complete | cited | `docs/SNAPSHOTS-AND-READ-REPLICAS.md` |
| 9 | Stale 503 statement removed | cited | `docs/SNAPSHOTS-AND-READ-REPLICAS.md` and `docs/API-CLIENT-AND-INSTANCE-CONFIGURATION.md` |
| 10 | Release notes complete | cited | `docs/changelog/8.1.0.md` |
| 11 | Gate status recorded honestly | cited | Release gate status above |
| 12 | Repeatable with troubleshooting | cited | `eng/verification/PublisherSnapshotInterop/README.md` |
| 13 | Markdown and script lint clean | cited | the repository's markdown and PSScriptAnalyzer checks and the Pester step of the pull request CI |

## Per-request contract

The per-request behavior is proven by the CI suites below and is re-run at the release candidate.
The runs in this record do not re-prove it.
CI of record: `Run PostgreSQL DMS E2E (self-contained, Shard 1)`, `Run SQL Server DMS E2E (self-contained, representative)`, and the API integration suite (`Given_Postgresql_DerivativeRouting` and `Given_Mssql_DerivativeRouting`).

| Behavior | Cited scenarios |
| --- | --- |
| `405` with `Allow: GET` for POST, PUT, and DELETE that ask for a snapshot | `SnapshotAndReadReplicaRouting.feature` 06 and 07; `DerivativeRoutingScenario.cs` `It_rejects_the_invalid_mutation_shapes_that_ask_for_a_snapshot` |
| Snapshot Not Found when the snapshot is not configured | `SnapshotFailures.feature` 01; `SnapshotAndReadReplicaRouting.feature` 09 (an unknown resource keeps its endpoint `404`); `DerivativeRoutingScenario.cs` `It_returns_not_found_when_no_snapshot_is_configured` |
| Snapshot Not Found when the snapshot cannot be reached | `SnapshotFailures.feature` 02 |
| `Use-Snapshot` absent or `false` does not select the snapshot | `SnapshotAndReadReplicaRouting.feature` 01 and 08; `DerivativeRoutingScenario.cs` header-absent and header-false cases in `It_keeps_the_route_semantics_answer_for_the_invalid_shapes` and `It_leaves_a_valid_write_alone_without_a_snapshot_request` |
| Snapshot precedence over a read replica, and automatic replica selection | `SnapshotAndReadReplicaRouting.feature` 01 and 05; `DerivativeRoutingScenario.cs` `It_serves_an_eligible_read_from_the_replica` and `It_prefers_the_snapshot_over_the_replica` |
| `/deletes`, `/keyChanges`, and `availableChangeVersions` served by the selected target | `SnapshotAndReadReplicaRouting.feature` 03, 04, and 05 |

## GET-by-id

GET-by-id served from a snapshot is cited, not exercised through the Publisher: `SnapshotAndReadReplicaRouting.feature` scenario 02 proves it in CI.

Finding against `29-snapshot-support.md` section API Publisher Interoperability: the Publisher never issues a source GET-by-id against a DMS target.
That section lists GET-by-id among the requests one extraction issues, so it overstates what a Publisher extraction exercises.
The seven source surfaces a DMS extraction does issue are GET-many, `/partitions`, cursor pages, `totalCount`, `/deletes`, `/keyChanges`, and `/availableChangeVersions`.

## Engine: PostgreSQL

### Versions

| Item | Value |
| --- | --- |
| DMS image | `edfialliance/ed-fi-api:8.1.0-beta2.28` |
| DMS image digest | `sha256:0753acf8ecc6764fe73c556764d61b38f2238379c78b8c6ee2c4497d0eb1b372` |
| DMS image source revision | `bee1d2287de506043a8e73566837bd6c5810b71a` |
| Configuration Service image | `edfialliance/ed-fi-api-configuration-service@sha256:e30a1d24a8061913555822accb099cce859cc5e2069ac3fc89e9bdcc1f8cb1c6` |
| Configuration Service image digest | `sha256:e30a1d24a8061913555822accb099cce859cc5e2069ac3fc89e9bdcc1f8cb1c6` |
| Configuration Service image source revision | `362997658568acb32fa37c547b7e64bb150c5247` |
| DMS repository commit when the run started | `06d4dd2eac80` |
| Discovery version | `8.1.0` |
| Publisher image | `edfialliance/ods-api-publisher:v1.4.0@sha256:cacbeff911a5484bea98d09ea4eebdc6301ae9e12511c0e505318ea190d4d59c` |
| Publisher image digest | `sha256:cacbeff911a5484bea98d09ea4eebdc6301ae9e12511c0e505318ea190d4d59c` |
| Data Standard | `Ed-Fi 5.2.0, TPDM 1.1.0` |
| Identity provider | `self-contained` |
| Engine | `postgresql` |
| Engine version | `PostgreSQL 18.6` |
| Run started | `2026-11-05 00:57:59 UTC` |

### Non-default settings

| Setting | Value | Default | Reason |
| --- | --- | --- | --- |
| `DataStoreCacheExpirationSeconds` | `10` | `600` | a new data store or snapshot derivative takes effect in seconds, not minutes |
| `RateLimit__PermitLimit` | `100000` | `20000` | the Publisher exceeds the default request rate |
| `Serilog__MinimumLevel__Override__EdFi.DataManagementService.Core.Middleware.SelectEffectiveDataStoreTargetMiddleware` | `Debug` | `Information` | per-request data store target selection is logged |
| `SeedTemplate` | `Minimal` | `none` | source data store seed |
| `PostgreSQL max_connections` | `300` | `100` | two data stores on one DMS exhaust the default connection limit |
| `DmsImageTag` | `8.1.0-beta2.28` | `locally built image (ed-fi-api-local)` | the run validates a published image; a locally built image advertises the pinned 8.0.0 Discovery version |
| `ConfigurationServiceImage` | `edfialliance/ed-fi-api-configuration-service@sha256:e30a1d24a8061913555822accb099cce859cc5e2069ac3fc89e9bdcc1f8cb1c6` | `locally built image (ed-fi-api-config-local)` | the Configuration Service image that matches the published Ed-Fi API image |

### Harness command

```powershell
pwsh eng/verification/Invoke-PublisherSnapshotInteropCheck.ps1 -Engine postgresql -Scenario All -DmsImageTag 8.1.0-beta2.28 -ConfigurationServiceImage edfialliance/ed-fi-api-configuration-service@sha256:e30a1d24a8061913555822accb099cce859cc5e2069ac3fc89e9bdcc1f8cb1c6
```

### Acceptance criteria outcomes

| # | Criterion | Outcome | Evidence |
| --- | --- | --- | --- |
| 1 | Versions and configuration recorded | PASS | `20261105T005759Z-postgresql/manifest.json`, 9 of 9 fields recorded |
| 2 | Publisher runs isolated without the opt-out | PASS | `20261105T005759Z-postgresql/results-Isolated.json`, 9 assertions |
| 3 | Post-snapshot writes are excluded | PASS | `20261105T005759Z-postgresql/results-Isolated.json`, 12 assertions |
| 4 | Every Publisher source surface used the snapshot within one extraction | PASS | `20261105T005759Z-postgresql/results-Isolated.json`, 9 assertions |
| 5 | No-snapshot Publisher behavior recorded | PASS | `20261105T005759Z-postgresql/results-NoSnapshot.json`, 6 assertions |

### Assertions behind each outcome

Criterion 2, Publisher runs isolated without the opt-out.

- PASS: Run B exit codes (cursor run, offset run)
- PASS: Run B Primary selections for the source data store
- PASS: Run B ReadReplica selections for the source data store
- PASS: Run B selected the Snapshot for the source data store
- PASS: Run B source requests attributed to a target other than Snapshot
- PASS: Run B made no source request the DMS log left unattributed
- PASS: Run C selected the Primary for the source data store
- PASS: Run C source requests attributed to a target other than Primary
- PASS: Control flip: source selections go from Snapshot (Run B) to Primary (Run C)

Criterion 3, Post-snapshot writes are excluded.

- PASS: Run B target reflects I2
- PASS: Run C target reflects I2
- PASS: Control flip: I2 is not reflected after Run B and is reflected after Run C
- PASS: Run B target reflects U2
- PASS: Run C target reflects U2
- PASS: Control flip: U2 is not reflected after Run B and is reflected after Run C
- PASS: Run B target reflects D2
- PASS: Run C target reflects D2
- PASS: Control flip: D2 is not reflected after Run B and is reflected after Run C
- PASS: Run B target reflects K2
- PASS: Run C target reflects K2
- PASS: Control flip: K2 is not reflected after Run B and is reflected after Run C

Criterion 4, Every Publisher source surface used the snapshot within one extraction.

- PASS: Run B issued GetMany at least once (union of the cursor and offset runs)
- PASS: Run B issued Partitions at least once (union of the cursor and offset runs)
- PASS: Run B issued CursorPage at least once (union of the cursor and offset runs)
- PASS: Run B issued TotalCount at least once (union of the cursor and offset runs)
- PASS: Run B issued Deletes at least once (union of the cursor and offset runs)
- PASS: Run B issued KeyChanges at least once (union of the cursor and offset runs)
- PASS: Run B issued AvailableChangeVersions at least once (union of the cursor and offset runs)
- PASS: Run B selected the Snapshot for the source data store
- PASS: Run B source requests attributed to a target other than Snapshot

Criterion 5, No-snapshot Publisher behavior recorded.

- PASS: Publisher exit code
- PASS: availableChangeVersions status codes
- PASS: count read status codes
- PASS: The Snapshot Not Found response after the snapshot is retired equals the never-configured response
- PASS: The FATL line about source read errors is present
- PASS: The EROR count-failure line carries the Snapshot Not Found body

## Engine: SQL Server

### Versions

| Item | Value |
| --- | --- |
| DMS image | `edfialliance/ed-fi-api:8.1.0-beta2.28` |
| DMS image digest | `sha256:0753acf8ecc6764fe73c556764d61b38f2238379c78b8c6ee2c4497d0eb1b372` |
| DMS image source revision | `bee1d2287de506043a8e73566837bd6c5810b71a` |
| Configuration Service image | `edfialliance/ed-fi-api-configuration-service@sha256:e30a1d24a8061913555822accb099cce859cc5e2069ac3fc89e9bdcc1f8cb1c6` |
| Configuration Service image digest | `sha256:e30a1d24a8061913555822accb099cce859cc5e2069ac3fc89e9bdcc1f8cb1c6` |
| Configuration Service image source revision | `362997658568acb32fa37c547b7e64bb150c5247` |
| DMS repository commit when the run started | `06d4dd2eac80` |
| Discovery version | `8.1.0` |
| Publisher image | `edfialliance/ods-api-publisher:v1.4.0@sha256:cacbeff911a5484bea98d09ea4eebdc6301ae9e12511c0e505318ea190d4d59c` |
| Publisher image digest | `sha256:cacbeff911a5484bea98d09ea4eebdc6301ae9e12511c0e505318ea190d4d59c` |
| Data Standard | `Ed-Fi 5.2.0, TPDM 1.1.0` |
| Identity provider | `self-contained` |
| Engine | `mssql` |
| Engine version | `SQL Server 17.0.4075.5` |
| Run started | `2026-11-05 01:03:30 UTC` |

### Non-default settings

| Setting | Value | Default | Reason |
| --- | --- | --- | --- |
| `DataStoreCacheExpirationSeconds` | `10` | `600` | a new data store or snapshot derivative takes effect in seconds, not minutes |
| `RateLimit__PermitLimit` | `100000` | `20000` | the Publisher exceeds the default request rate |
| `Serilog__MinimumLevel__Override__EdFi.DataManagementService.Core.Middleware.SelectEffectiveDataStoreTargetMiddleware` | `Debug` | `Information` | per-request data store target selection is logged |
| `SeedTemplate` | `Minimal` | `none` | source data store seed |
| `DmsImageTag` | `8.1.0-beta2.28` | `locally built image (ed-fi-api-local)` | the run validates a published image; a locally built image advertises the pinned 8.0.0 Discovery version |
| `ConfigurationServiceImage` | `edfialliance/ed-fi-api-configuration-service@sha256:e30a1d24a8061913555822accb099cce859cc5e2069ac3fc89e9bdcc1f8cb1c6` | `locally built image (ed-fi-api-config-local)` | the Configuration Service image that matches the published Ed-Fi API image |

### Harness command

```powershell
pwsh eng/verification/Invoke-PublisherSnapshotInteropCheck.ps1 -Engine mssql -Scenario All -DmsImageTag 8.1.0-beta2.28 -ConfigurationServiceImage edfialliance/ed-fi-api-configuration-service@sha256:e30a1d24a8061913555822accb099cce859cc5e2069ac3fc89e9bdcc1f8cb1c6
```

### Acceptance criteria outcomes

| # | Criterion | Outcome | Evidence |
| --- | --- | --- | --- |
| 1 | Versions and configuration recorded | PASS | `20261105T010330Z-mssql/manifest.json`, 9 of 9 fields recorded |
| 2 | Publisher runs isolated without the opt-out | PASS | `20261105T010330Z-mssql/results-Isolated.json`, 9 assertions |
| 3 | Post-snapshot writes are excluded | PASS | `20261105T010330Z-mssql/results-Isolated.json`, 12 assertions |
| 4 | Every Publisher source surface used the snapshot within one extraction | PASS | `20261105T010330Z-mssql/results-Isolated.json`, 9 assertions |
| 5 | No-snapshot Publisher behavior recorded | PASS | `20261105T010330Z-mssql/results-NoSnapshot.json`, 6 assertions |

### Assertions behind each outcome

Criterion 2, Publisher runs isolated without the opt-out.

- PASS: Run B exit codes (cursor run, offset run)
- PASS: Run B Primary selections for the source data store
- PASS: Run B ReadReplica selections for the source data store
- PASS: Run B selected the Snapshot for the source data store
- PASS: Run B source requests attributed to a target other than Snapshot
- PASS: Run B made no source request the DMS log left unattributed
- PASS: Run C selected the Primary for the source data store
- PASS: Run C source requests attributed to a target other than Primary
- PASS: Control flip: source selections go from Snapshot (Run B) to Primary (Run C)

Criterion 3, Post-snapshot writes are excluded.

- PASS: Run B target reflects I2
- PASS: Run C target reflects I2
- PASS: Control flip: I2 is not reflected after Run B and is reflected after Run C
- PASS: Run B target reflects U2
- PASS: Run C target reflects U2
- PASS: Control flip: U2 is not reflected after Run B and is reflected after Run C
- PASS: Run B target reflects D2
- PASS: Run C target reflects D2
- PASS: Control flip: D2 is not reflected after Run B and is reflected after Run C
- PASS: Run B target reflects K2
- PASS: Run C target reflects K2
- PASS: Control flip: K2 is not reflected after Run B and is reflected after Run C

Criterion 4, Every Publisher source surface used the snapshot within one extraction.

- PASS: Run B issued GetMany at least once (union of the cursor and offset runs)
- PASS: Run B issued Partitions at least once (union of the cursor and offset runs)
- PASS: Run B issued CursorPage at least once (union of the cursor and offset runs)
- PASS: Run B issued TotalCount at least once (union of the cursor and offset runs)
- PASS: Run B issued Deletes at least once (union of the cursor and offset runs)
- PASS: Run B issued KeyChanges at least once (union of the cursor and offset runs)
- PASS: Run B issued AvailableChangeVersions at least once (union of the cursor and offset runs)
- PASS: Run B selected the Snapshot for the source data store
- PASS: Run B source requests attributed to a target other than Snapshot

Criterion 5, No-snapshot Publisher behavior recorded.

- PASS: Publisher exit code
- PASS: availableChangeVersions status codes
- PASS: count read status codes
- PASS: The Snapshot Not Found response after the snapshot is retired equals the never-configured response
- PASS: The FATL line about source read errors is present
- PASS: The EROR count-failure line carries the Snapshot Not Found body
