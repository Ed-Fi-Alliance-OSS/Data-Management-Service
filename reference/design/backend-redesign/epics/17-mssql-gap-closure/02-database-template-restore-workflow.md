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
general package publication remains under `DMS-1255` ownership.

## Restore Sequence

The wrapper performs the following sequence in restore mode:

1. Resolve the database-template package and validate its package identity, restore manifest, artifact hash,
   engine, Data Standard version, template kind, and effective-schema metadata before Docker startup or any
   destructive database work.
2. Prove that the entire selected DMS stack is stopped before creating, replacing, or reusing restore
   workspace state. A failed or indeterminate stop proof leaves the existing workspace and target untouched.
3. Compare the requested schema set with the staged workspace. If the workspace is absent, stage schema,
   claims, and seed handoff state. If it is mismatched or incomplete, remove the whole `.bootstrap` tree and
   regenerate schema, claims, and seed handoff state together. Never rewrite only a schema or manifest subtree.
4. Run `start-*-dms.ps1 -DbOnly` to start only the selected database service and wait for readiness.
5. Revalidate target safety now that the database is reachable, then restore one explicitly selected physical
   DMS datastore database.
6. Run `start-*-dms.ps1 -InfraOnly` to initialize identity and CMS.
7. Run `configure-local-data-store.ps1` for the restored datastore.
8. Skip `provision-dms-schema.ps1` for that datastore because the template already contains the generated
   schema and seed data.
9. Run `start-*-dms.ps1 -DmsOnly`.
10. Run supplemental API seed only when an explicit, compatible option requests it.

The wrapper owns when restore happens. An engine-dispatched restore module owns how the package is restored.

## Engine-Specific Restore

- PostgreSQL resolves the package's SQL artifact, drops/recreates the selected datastore database, and replays
  the SQL into the new database.
- MSSQL resolves the package's `.bak`, validates logical file metadata, and performs `RESTORE ... WITH MOVE,
  REPLACE` into the selected datastore database.
- Feed resolution and an explicit `-PackageDirectory` path are both supported so the workflow can be tested
  before or without publication to the configured feed.
- The external restore manifest contains the package id/version, engine, Data Standard version, template kind,
  `ApiSchemaFormatVersion`, `EffectiveSchemaHash`, relational mapping version, engine version, database
  compatibility level, physical `DocumentJson` column type, artifact filename, and artifact SHA-256. The
  package producer reads the schema fields from `dms.EffectiveSchema` and the provider-specific fields from the
  live database catalog immediately before creating the backup. The consumer rejects a missing, malformed,
  mismatched, or hash-invalid manifest before starting the database service or touching the selected target.
- A legacy package without the external manifest is not eligible for target replacement. If legacy-package
  compatibility is later required, it must use a safe scratch restore followed by `dms.EffectiveSchema`
  validation before any destructive operation against the selected datastore.

## Topology and Target Safety

- Fail before workspace replacement unless the complete selected stack is proven stopped; checking only
  whether CMS or DMS points at the eventual restore target is insufficient because either service may still
  have `.bootstrap` content bind-mounted.
- Fail before target restore if CMS or DMS is running against the target. Failure to prove either safety
  condition leaves the existing workspace and database untouched.
- In shared-database mode, restore the shared database only in the `-DbOnly` window, before CMS creates its
  schema and clients.
- In separate-database mode, restore only the selected DMS datastore; never replace the CMS database.
- One invocation restores one physical database. Additional school-year, district, or tenant datastores are
  restored by explicit repeated invocations, followed by datastore registration/configuration.
- Empty schema databases continue to use `provision-dms-schema.ps1`; template-backed databases use restore.

If the requested Data Standard or extension set differs from staged state, remove and regenerate the complete
workspace only after the stop proof above. Partial manifest invalidation or subtree replacement is not
permitted because schema, claims, and seed state form one handoff and may still be bind-mounted.

## Acceptance Criteria

- Bootstrap exposes an explicit restore option separate from `-LoadSeedData`.
- Restore validates the external manifest and artifact hash, safely prepares the complete workspace, then
  follows `-DbOnly -> restore -> -InfraOnly -> configure -> -DmsOnly`; it skips schema provisioning only for
  restored targets.
- PostgreSQL SQL replay and MSSQL backup restore both materialize Minimal and Populated packages correctly.
- Target validation prevents an MSSQL restore from replacing a separate CMS database.
- Package resolution or external-manifest validation failures occur before Docker startup and leave the
  workspace and target untouched. Target-selection or restore failures stop before CMS/DMS startup.
- Existing non-restore bootstrap behavior is unchanged.
- A mismatched or incomplete workspace is replaced only after the complete stack is proven stopped; the whole
  `.bootstrap` tree is removed and schema, claims, and seed handoff state are regenerated together.
- Tests cover wrapper sequencing, parameter validation, local-package resolution, engine-specific command
  construction, manifest/artifact validation before Docker activity, failed stop proof, full-workspace
  replacement, bind-mount safety, shared/separate topology behavior, default targeting, repeated single-target
  operation, and the distinction between restore and API seed delivery.
- Live validation covers PostgreSQL and MSSQL with Minimal and Populated templates and supported Data Standard
  versions, including effective-schema validation and served data.

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
