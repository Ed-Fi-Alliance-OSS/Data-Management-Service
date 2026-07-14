---
jira: DMS-1271
jira_url: https://edfi.atlassian.net/browse/DMS-1271
---

# Story: Add a Database-Template Restore Branch to Bootstrap

## Description

Add an explicit bootstrap path that materializes a DMS datastore from a published database-template package
before CMS, identity, schema provisioning, or DMS startup touches the environment.

Template restore is distinct from API seed delivery. Do not overload `-LoadSeedData`: that switch means
BulkLoadClient delivery through the running API, while restore replaces or recreates a physical database from
a package artifact.

`DMS-1255` supplies the baseline template packages and the `-DbOnly` startup phase used by this story. This
story owns the narrow producer-and-consumer extension that adds a machine-readable restore manifest outside
the database artifact. The shared template tooling captures the live metadata immediately before backup,
creates and hashes the database artifact, then packages the completed manifest beside the `.sql` or `.bak`;
general package publication remains under `DMS-1255` ownership. This extension also proves that the package
contains DMS datastore state only; a database template is never a CMS or identity-state backup.

## Restore Sequence

The wrapper performs the following sequence in restore mode:

1. Resolve the database-template package and validate its package identity, restore manifest, artifact hash,
   engine, Data Standard version, template kind, DMS-only content profile, and internally consistent
   effective-schema metadata. Normalize and validate the selected target name, including the reserved-database
   denylist and the separate-CMS exclusion, before creating any workspace state.
2. Materialize the requested schema, claims, and seed handoff into a new candidate workspace that is not the
   active `.bootstrap` tree and is never bind-mounted. Invoke the authoritative preparation phases so
   `prepare-dms-schema.ps1`, rather than the wrapper, computes the expected `EffectiveSchemaHash` and records
   any engine-defined physical-schema version in the candidate manifest.
3. Compare the package manifest with the candidate bootstrap manifest. The Data Standard version, selected
   core/extension project inventory, `ApiSchemaFormatVersion`, `EffectiveSchemaHash`, relational mapping
   version, engine, and any engine-defined physical-schema version must match exactly. On mismatch, remove the
   candidate and leave the active workspace and selected database untouched.
4. Prove that the entire selected DMS stack is stopped before replacing or reusing active restore workspace
   state. A failed or indeterminate stop proof leaves the active workspace and target untouched.
5. If the active workspace is absent or differs from the verified candidate, replace the complete
   `.bootstrap` tree with the candidate. If it already matches, discard the candidate and reuse the complete
   active tree. Never rewrite only a schema or manifest subtree.
6. Run `start-*-dms.ps1 -DbOnly` to start only the selected database service and wait for readiness.
7. Revalidate target safety against the live engine catalog. Restore the artifact into a generated scratch
   database, validate its effective schema and DMS-only catalog contents, remove the scratch database, and only
   then replace the explicitly selected physical DMS datastore database.
8. Run `start-*-dms.ps1 -InfraOnly` to initialize identity and CMS.
9. Run `configure-local-data-store.ps1` for the restored datastore.
10. Skip `provision-dms-schema.ps1` for that datastore because the template already contains the generated
   schema and seed data.
11. Run `start-*-dms.ps1 -DmsOnly`.
12. Run supplemental API seed only when an explicit, compatible option requests it.

The wrapper owns when restore happens. An engine-dispatched restore module owns how the package is restored.

## Engine-Specific Restore

- Both engines first materialize the package into a generated, non-user-selectable scratch database. The
  scratch name uses a safe product prefix plus an unpredictable suffix, passes the same reserved-name checks,
  and is removed on success or failure. Selected-target replacement cannot begin until scratch validation
  succeeds.
- PostgreSQL resolves the package's SQL artifact, replays it into the scratch database, validates it, then
  drops/recreates the selected datastore database and replays the same hash-verified SQL into the target.
- MSSQL resolves the package's `.bak`, validates logical file metadata, and performs `RESTORE ... WITH MOVE`
  into the scratch database. After validation, it restores the same hash-verified backup into the selected
  datastore with target-specific `MOVE` paths and `REPLACE`.
- Feed resolution and an explicit `-PackageDirectory` path are both supported so the workflow can be tested
  before or without publication to the configured feed.
- The external restore manifest contains the package id/version, engine, Data Standard version, template kind,
  selected core/extension project inventory, `ApiSchemaFormatVersion`, `EffectiveSchemaHash`, relational
  mapping version, engine version, database compatibility level, physical `DocumentJson` column type, any
  engine-defined physical-schema version such as `MssqlPhysicalSchemaVersion`, the fixed `DmsDatastoreOnly`
  content profile, a canonical schema/object inventory and its SHA-256, artifact filename, and artifact
  SHA-256.
- Immediately before backup, the producer reads schema fields from `dms.EffectiveSchema` and provider fields
  from the live catalog. It fails unless the source is a dedicated DMS datastore database whose canonical
  inventory contains only the `dms` schema, effective-schema-derived resource schemas, and explicitly
  documented engine support objects. In particular, `dmscs`, Configuration Service/OpenIddict tables, copied
  clients or keys, and unexpected database principals are forbidden. This gate also applies to direct local
  use of the shared template helper, whose default database name may otherwise identify the shared local
  database.
- The consumer rejects a missing, malformed, mismatched, or hash-invalid manifest before starting the database
  service or changing the active workspace. After `-DbOnly`, it independently derives the scratch database's
  canonical inventory, compares it with the manifest, verifies `dms.EffectiveSchema` against both the manifest
  and candidate bootstrap manifest, and enforces the DMS-only exclusions before touching the target. Feed and
  `-PackageDirectory` packages use identical producer/consumer checks; local origin is not a trust bypass.
- A legacy package without the external manifest is not eligible for target replacement. If legacy-package
  compatibility is later required, it must use a safe scratch restore followed by `dms.EffectiveSchema`
  validation before any destructive operation against the selected datastore.

## Topology and Target Safety

- Normalize database names case-insensitively and reject PostgreSQL `postgres`, `template0`, and `template1`,
  plus SQL Server `master`, `model`, `msdb`, and `tempdb`. Apply this denylist during parameter validation and
  again against the live catalog before scratch creation, `DROP`, or `RESTORE`; generic identifier validation
  is not a substitute for reserved-name validation.
- Fail before workspace replacement unless the complete selected stack is proven stopped; checking only
  whether CMS or DMS points at the eventual restore target is insufficient because either service may still
  have `.bootstrap` content bind-mounted.
- Fail before target restore if CMS or DMS is running against the target. Failure to prove either safety
  condition leaves the existing workspace and database untouched.
- In shared-database mode, restore only a package proven to have the `DmsDatastoreOnly` content profile, and do
  so in the `-DbOnly` window before CMS creates a fresh schema and clients. No CMS or identity record from the
  package may survive into the initialized environment.
- In separate-database mode, restore only the selected DMS datastore; never replace the CMS database.
- One invocation restores one physical database. Additional school-year, district, or tenant datastores are
  restored by explicit repeated invocations, followed by datastore registration/configuration.
- Empty schema databases continue to use `provision-dms-schema.ps1`; template-backed databases use restore.

If the requested Data Standard or extension set differs from active staged state, prepare and validate a
complete candidate first, then replace the complete workspace only after the package cross-check and stop proof
above. Partial manifest invalidation or subtree replacement is not permitted because schema, claims, and seed
state form one handoff and may still be bind-mounted.

## Acceptance Criteria

- Bootstrap exposes an explicit restore option separate from `-LoadSeedData`.
- Restore validates the external manifest and artifact hash, safely prepares the complete workspace, then
  follows `-DbOnly -> scratch validation -> target restore -> -InfraOnly -> configure -> -DmsOnly`; it skips
  schema provisioning only for restored targets.
- Before Docker startup or active-workspace replacement, restore proves that the package's Data Standard,
  core/extension inventory, `ApiSchemaFormatVersion`, `EffectiveSchemaHash`, relational mapping version, and
  any engine-defined physical-schema version match the authoritative candidate workspace.
- PostgreSQL SQL replay and MSSQL backup restore both materialize Minimal and Populated packages correctly.
- Target validation rejects every reserved PostgreSQL/SQL Server database and prevents an MSSQL restore from
  replacing a separate CMS database before any workspace or database mutation.
- Producers reject shared or contaminated sources. Consumers scratch-restore and reject packages containing
  `dmscs`, CMS/OpenIddict state, unexpected principals, or objects outside the manifest's DMS-only inventory
  before replacing the target, including packages supplied through `-PackageDirectory`.
- Package resolution or external-manifest validation failures occur before Docker startup and leave the
  workspace and target untouched. Target-selection or restore failures stop before CMS/DMS startup.
- Existing non-restore bootstrap behavior is unchanged.
- A mismatched or incomplete workspace is replaced only after the complete stack is proven stopped; the whole
  `.bootstrap` tree is removed and schema, claims, and seed handoff state are regenerated together.
- Tests cover wrapper sequencing, candidate/package hash and project-inventory mismatches, reserved database
  names for both engines, local-package resolution, engine-specific scratch and target command construction,
  manifest/artifact validation before Docker activity, DMS-only producer and consumer gates, scratch cleanup,
  failed stop proof, full-workspace replacement, bind-mount safety, shared/separate topology behavior, default
  targeting, repeated single-target operation, and the distinction between restore and API seed delivery.
- Live validation covers PostgreSQL and MSSQL with Minimal and Populated templates and supported Data Standard
  versions, including different extension selections, effective-schema validation, contaminated-package
  rejection before target replacement, and served data.

## Non-Goals

- A persisted multi-database restore control plane or resumable workflow.
- Restoring multiple physical databases implicitly from CMS routing state.
- Replacing API-based supplemental seed delivery.
- General package publication workflow ownership; `DMS-1255` retains that ownership while this story adds only
  the restore-manifest producer/consumer contract required for safe restore.

## Design References

- [`01-local-database-topology-parity.md`](01-local-database-topology-parity.md)
- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md)
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md)
