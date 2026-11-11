# DMS-1438 — Runtime-safe Configuration Service provisioner for DMS template databases

## 1. Document control

| Field | Value |
| --- | --- |
| Ticket | [DMS-1438](https://edfi.atlassian.net/browse/DMS-1438) |
| Consumer | [DMS-1439](https://edfi.atlassian.net/browse/DMS-1439), the managed data-store lifecycle, which runs inside the Configuration Service background jobs of DMS-1437 |
| Status | Implemented. This record states the design as delivered. The working specification, with its revision log and review rounds, is kept outside the repository. |
| Background | [DMS-1334 spike: DMS-1438 template provisioner](../../spikes/DMS-1334/DMS-1438-template-provisioner.md) |
| Operator documentation | [CONFIGURATION.md](../../../docs/CONFIGURATION.md#dmsdatastoreprovisioning), [OPERATIONS.md](../../../docs/OPERATIONS.md#configuration-service-dms-data-store-provisioning), [CONFIGURATION-SERVICE-BACKGROUND-JOBS.md](../../../docs/CONFIGURATION-SERVICE-BACKGROUND-JOBS.md#dms-data-store-provisioning), [RUNNING-LOCALLY.md](../../../docs/RUNNING-LOCALLY.md#cms-dms-template-provisioning-integration-tests) |

## 2. Problem and scope

DMS publishes `Minimal` and `Populated` database-template packages for PostgreSQL and SQL Server, with
PowerShell and Docker restore tooling. The Configuration Service had no administrative connection, process,
staging, manifest, or attestation code, yet DMS-1439 must create and delete DMS databases from inside a
durable job, without shelling out to that tooling and without referencing DMS projects.

**In scope:** a provider-neutral, typed, asynchronous resolve, create, and delete contract in
`src/config/backend/EdFi.DmsConfigurationService.Backend`, with engine adapters in `Backend.Postgresql` and
`Backend.Mssql`. It covers authentication and validation of the package before the requested name is bound,
ownership by the record's expected `SourceIdentity`, exclusion and outcome protocols, cancellation,
timeouts, bounded cleanup, secret-safe diagnostics, retry-classifiable results, startup validation, and a
readiness gate.

**Not in scope:** an endpoint, lifecycle persistence, job handlers, or job types (DMS-1439); template
production or bootstrap changes; DMS application or schema changes; a runtime NuGet feed download;
automatic reclaim of a database left between rename and marker by a crash; automatic sweeping of leftovers
across invocations; and defense against administrative DDL run by hand on managed names while an operation
runs.

## 3. Architecture

| Component | Project | Role |
| --- | --- | --- |
| `IDmsDataStoreProvisioner`, `IDmsDataStoreProvisionerReadiness`, `IDmsTargetEngine`, request and result types, failure taxonomy | `Backend` | Provider-neutral contracts |
| `DmsDataStoreProvisioningOptions` and validator, `ProtectedDatabaseNames` | `Backend` | Settings and static validation |
| Trust policy reader, attestation verifier, restore manifest reader, canonical inventory, package stager, `TemplateArtifactCatalog` | `Backend` | Package authentication and resolution, pure C# |
| `DmsDataStoreProvisioningService` | `Backend` | The orchestrator: readiness refusal, request validation, target lock, pin check, stage, validate, rename, mark, outcome protocol, compensation, cleanup accounting |
| `DmsTargetValidator` | `Backend` | Content, schema, inventory, and security validation over rows the engine reads |
| `PostgresqlDmsTargetEngine`, `PostgresqlTargetLockSession`, `PsqlRestoreRunner` and query classes | `Backend.Postgresql` | PostgreSQL adapter |
| `MssqlDmsTargetEngine`, `MssqlTargetLockSession`, `MssqlBackupStager`, `MssqlRestorePlanner`, `MssqlStagingProbe` and query classes | `Backend.Mssql` | SQL Server adapter |
| `ConfigureDmsDataStoreProvisioning`, `DmsDataStoreProvisioningStartup`, `DisabledDmsDataStoreProvisioner` | `Frontend.AspNetCore`, `Backend` | Registration by `Provider`, startup validation, readiness |
| `IDmsProvisioningTestHooks` | `Backend` (internal) | Await points and fault injection for tests; a no-op in production |

Dependencies point Frontend → `Backend.{Postgresql,Mssql}` → `Backend`. Nothing references `src/dms`, and no
NuGet package was added.

## 4. Decisions as delivered

| Id | Decision |
| --- | --- |
| D-1 | Top-level section `DmsDataStoreProvisioning`, `Enabled` default `false`. While off, only the refusing provisioner and a `Disabled` readiness are registered. |
| D-2 | Static settings are validated with `ValidateOnStart`, so the process fails. Environmental checks set a readiness gate once at startup; every resolve, create, and delete checks it first and returns `NotReady` without touching anything. |
| D-3 | Packages come from a local directory with sibling attestations (`<id>.<version>.nupkg.attestation.json`). There is no runtime feed download. |
| D-4 | An operator trust policy at `TrustPolicyPath`, in the delivered trust-policy schema. Zero `detached-attestation` producers makes readiness `NotReady`. There is no bypass or development mode. |
| D-5 | Each operation claims a private `0700` directory, copies the package into it (`0600`), hashes the copy, verifies the attestation against that hash, and extracts the manifest and the single artifact while hashing them. The catalog file is never read again after hashing. |
| D-6 | Stage, validate, rename: the database is restored as `edfi_dms_provision_stage_<operation id>`, fully validated there, then renamed into the requested name. The name is bound only to a validated database. |
| D-7 | `dms.DataStoreIdentity.SourceIdentity` is assigned last and is the only ownership and commit marker. |
| D-7b | Outcome protocol: each mutating command runs on a session whose server identity is captured when it opens. A lost result is settled by re-reading state, ending that session first when needed, never by assuming either outcome. |
| D-8 | `ResolveArtifactAsync` returns a pin without contacting the server; DMS-1439 persists it; create and every retry present it and fail with `ArtifactChangedSinceCreate` unless the package resolved again equals it field by field. |
| D-9 | PostgreSQL 18 or later, `UTF8` `template1`, `psql` major at least `MinimumPsqlMajorVersion` (18), and the server major at least the manifest's. |
| D-10, D-10b | `psql` receives the password through a temporary `0600` `PGPASSFILE` and runs `--single-transaction` with `ON_ERROR_STOP`. Its standard error is read only into a bounded buffer to extract a SQLSTATE; it is never logged, stored, or put in an exception. PostgreSQL connection strings accept a fixed key subset. |
| D-11 | SQL Server backups must hold exactly one full backup set, with a recorded database name different from both the staging and the target name, data and log files only (at most 16), and safe logical names. Files go to the instance default paths under generated names, never to recorded paths; `REPLACE` is never used. |
| D-12 | Exclusion runs on the target server. Each operation holds a per-name lock on a dedicated holder session (PostgreSQL session advisory lock, SQL Server session application lock on `dms-provision-target:v1:<lowercase name>`). The rename into the target, fence, unfence, and drop are issued on that holder session, so losing the session removes the ability to make them. The identity marker is written on a work session opened against the verified instance and checked against it, not on the holder. A heartbeat detects loss. One execution gate serializes commands on the holder. A rename or fence whose result is lost, or whose holder is lost while it is in doubt, is settled after the lock is released, and its category comes from what settlement proves: it is transient only when the mutation provably left nothing in place (the rename did not happen and the target name is empty; the target is ours and accepts connections), and requires inspection otherwise. A loss while a fence is being reversed keeps requiring inspection, because the reversal was issued for another failure that the loss hides. A caller's cancellation reported as a lost result still propagates after settlement. |
| D-13 | Instance identity is detection, not atomicity. PostgreSQL uses `pg_database.oid` plus a claim comment (`edfi-dms-provision:v1:<operation>:<identity>`); SQL Server uses `database_id` plus a primary file named `…__<operation>.mdf`. A database replaced by hand between a check and a mutation is outside this boundary (the operational rule). |
| D-14 | A database left between rename and marker is `TargetUnverifiable` or `TargetUnowned` on retry and needs an operator; it is never reclaimed automatically. |
| D-15 | The delivered ownership, privilege, and security-state gates of the template tooling are ported as they are, and run before the rename and on every reconcile. |
| D-16 | Before a PostgreSQL replay, `edfi_dms_enqueue_owner` is created locked down, or refused if it exists in another state (`RoleStateUnsafe`). |
| D-17 | Provider tokens are `postgresql` and `mssql`; DMS-1439 maps `mssql` to `sqlserver` for its ordinary row. |
| D-18 | SQL Server staging separates writer and reader: a directory owned by the Configuration Service user, its group shared with the SQL Server identity, mode `2750`, staged files `0640`. The staged file is created with `CreateNew`, re-read and re-hashed before the restore, and re-hashed after it (`StagingIntegrityViolation`). |
| D-19 | Cleanup of what the running operation created runs on every outcome with bounded attempts. Anything left is reported in the result and logged by generated name; it never changes the outcome. Nothing is swept across invocations. |
| D-20 | `Compatibility:*` expectations are required and both allowlisted manifests must match them. |
| D-21 | At startup, SQL Server staging visibility is probed with the real staging routine and `RESTORE HEADERONLY` of a zero-byte file. |
| D-22 | Sessions are ended with a bound and proven gone by their identity (PostgreSQL `pid` with `backend_start`; SQL Server `session_id` with `login_time`). A delete fences the target, verifies exclusivity, re-verifies ownership and instance, then drops. |
| D-23 | Test seams (`IDmsProvisioningTestHooks`) inject collisions and faults at fixed points so live tests are deterministic. |

## 5. Departures and additions made during implementation

These were raised during review and accepted. They are reflected in the code and the operator documentation.

- **PostgreSQL requires a superuser administrator.** The template's final `ALTER FUNCTION … OWNER TO
  "edfi_dms_enqueue_owner"` succeeds for a non-superuser only when that owner may create in `dms`, which the
  template's security gate forbids. Readiness refuses a non-superuser (`Superuser`) and still names each
  narrower privilege. Supporting a non-superuser needs a change in template production.
- **SQL Server floor and engine comparison.** The adapter's own floor is SQL Server 2022 (major 16). Startup
  also compares the server major with the `engineVersion` of both authenticated manifests, for either
  provider, so a server too old for the packages is `NotReady` at startup instead of failing every create.
- **SQL Server role and permission rules are literal.** `dbcreator` (or `sysadmin`), `ALTER ANY DATABASE`,
  `ALTER ANY CONNECTION`, `VIEW SERVER STATE` (or `VIEW SERVER PERFORMANCE STATE`), and visibility of
  `sys.master_files` are each required and reported separately.
- **Visibility probe error numbers.** SQL Server on Linux answers a readable zero-byte file with `3254`, not
  `3241`/`3242`, so `3254` counts as visible. `3201` covers both a missing path and a file the server
  identity cannot read.
- **`ServerRejected`** (`Permanent`) was added for an administrative command the server refuses for reasons
  other than privilege.
- **Real-package tests** sign the feed packages with an ephemeral key and a trust policy written for it,
  which is the operator path, not a bypass. Restore cancellation and timeout tests hold the restore at a
  point only the test releases, instead of racing a running restore.
- **CI.** Job `run-dms-provisioning-integration-tests` runs category `DmsProvisioning` on both engines with
  the four 8.1.0 feed packages pinned by SHA-256, and fails unless every test passed (a missing lane setting
  fails under GitHub Actions instead of skipping). The regular integration job excludes the category.
- **Separate identities.** `MssqlSeparateIdentityStagingTests` (category `DmsProvisioningSeparateIdentity`,
  outside CI) runs the staging rules with real, separate Linux identities through the recipe in
  RUNNING-LOCALLY.md. Its negative case changes the directory's group to one SQL Server is not in, rather
  than restarting the server without `--group-add`.

## 6. Verification

- Unit: contracts, settings validation, trust and attestation (including parity with attestations the
  PowerShell tooling produces), manifest and canonical inventory, package staging, catalog, orchestrator
  ordering, outcomes, compensation and cleanup with fakes, `psql` runner secrecy, SQL Server restore
  planning and probe classification, startup and readiness.
- Adversarial: forged, tampered, and re-signed packages; each manifest mismatch; content and security
  contamination; protected names; injection through names; collisions injected before the rename; pin
  drift; staged-file replacement as the reader and as a writer.
- Live, both engines, both templates: create, reconcile, delete, identity, effective schema, inventory,
  security gates, cancellation, timeout, cleanup and residue, competing operations, lock loss and takeover,
  lost results of the marker and the restore, and separate-identity staging.

## 7. Open items and follow-ups

- **CI producer key.** Until a CI-signed release publishes its producer key in the tracked trust policy,
  operators sign packages with their own key (OPERATIONS.md, "Template package trust").
- **Runtime package acquisition.** Packages are placed in `ArtifactDirectory` by the deployment; a feed
  download behind the catalog seam is a possible follow-up.
- **Non-superuser PostgreSQL administrator** needs a template-production change (Section 5).
- **Log event IDs** were once duplicated: `1438_20` and `1438_21` named events of both the artifact catalog
  and the target lock holder. The lock holder's `ReleaseFailed` and `ProbeFailed` are now `1438_15` and
  `1438_16`, and a Frontend unit test fails when any two provisioning events share an id.
- **DMS-1439** owns the job handlers, persistence of the pin and expected identity, the mapping to the
  ordinary data store row, and suppressing its job types while the feature is off.
