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

`DMS-1255` supplies the template packages and the `-DbOnly` startup phase used by this story.

## Restore Sequence

The wrapper performs the following sequence in restore mode:

1. Stage the schema and claims workspace.
2. Run `start-*-dms.ps1 -DbOnly` to start only the selected database service and wait for readiness.
3. Resolve and validate the database-template package.
4. Restore one explicitly selected physical DMS datastore database.
5. Run `start-*-dms.ps1 -InfraOnly` to initialize identity and CMS.
6. Run `configure-local-data-store.ps1` for the restored datastore.
7. Skip `provision-dms-schema.ps1` for that datastore because the template already contains the generated
   schema and seed data.
8. Run `start-*-dms.ps1 -DmsOnly`.
9. Run supplemental API seed only when an explicit, compatible option requests it.

The wrapper owns when restore happens. An engine-dispatched restore module owns how the package is restored.

## Engine-Specific Restore

- PostgreSQL resolves the package's SQL artifact, drops/recreates the selected datastore database, and replays
  the SQL into the new database.
- MSSQL resolves the package's `.bak`, validates logical file metadata, and performs `RESTORE ... WITH MOVE,
  REPLACE` into the selected datastore database.
- Feed resolution and an explicit `-PackageDirectory` path are both supported so the workflow can be tested
  before or without publication to the configured feed.
- Package identity, engine, Data Standard version, template kind, and effective-schema metadata are validated
  before destructive database work begins.

## Topology and Target Safety

- Fail before restore if CMS or DMS is already running against the target.
- In shared-database mode, restore the shared database only in the `-DbOnly` window, before CMS creates its
  schema and clients.
- In separate-database mode, restore only the selected DMS datastore; never replace the CMS database.
- One invocation restores one physical database. Additional school-year, district, or tenant datastores are
  restored by explicit repeated invocations, followed by datastore registration/configuration.
- Empty schema databases continue to use `provision-dms-schema.ps1`; template-backed databases use restore.

If schema staging changes the Data Standard or extension set, invalidate the dependent claims and seed
manifest sections before the claims-ready gate. Reusing stale claims/seed state across schema sets is not
permitted.

## Acceptance Criteria

- Bootstrap exposes an explicit restore option separate from `-LoadSeedData`.
- Restore follows `-DbOnly -> restore -> -InfraOnly -> configure -> -DmsOnly` and skips schema provisioning
  only for restored targets.
- PostgreSQL SQL replay and MSSQL backup restore both materialize Minimal and Populated packages correctly.
- Target validation prevents an MSSQL restore from replacing a separate CMS database.
- Package resolution, metadata validation, target selection, or restore failures stop before CMS/DMS startup.
- Existing non-restore bootstrap behavior is unchanged.
- Changing the staged schema set invalidates dependent claims/seed manifest state.
- Tests cover wrapper sequencing, parameter validation, local-package resolution, engine-specific command
  construction, shared/separate topology behavior, default targeting, repeated single-target operation, and
  the distinction between restore and API seed delivery.
- Live validation covers PostgreSQL and MSSQL with Minimal and Populated templates and supported Data Standard
  versions, including effective-schema validation and served data.

## Non-Goals

- A persisted multi-database restore control plane or resumable workflow.
- Restoring multiple physical databases implicitly from CMS routing state.
- Replacing API-based supplemental seed delivery.
- Publishing template packages; `DMS-1255` owns package production.

## Design References

- [`01-local-database-topology-parity.md`](01-local-database-topology-parity.md)
- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md)
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md)
