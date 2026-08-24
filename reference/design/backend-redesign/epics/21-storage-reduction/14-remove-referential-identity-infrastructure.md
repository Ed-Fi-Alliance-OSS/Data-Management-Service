---
jira: DMS-1456
jira_url: https://edfi.atlassian.net/browse/DMS-1456
epic: DMS-1402
---

# Story: Remove ReferentialIdentity Fixtures, Maintenance, and Infrastructure

## Outcome

Complete the storage reduction by proving that no runtime reader depends on RI rows and then
atomically removing all remaining DMS-owned ReferentialIdentity and UUIDv5 infrastructure.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1454 — descriptor write cutover and UUIDv5 cleanup](12-descriptor-write-cutover-and-uuidv5-cleanup.md).
- Depends on [DMS-1455 — Change Query descriptor identity cutover](13-change-query-descriptor-identity-cutover.md).
- This is the final story in the natural-key/ReferentialIdentity removal chain.

## Implementation Scope

- Complete the work as one trunk-green story with two required validation stages.
- First, update integration database setup, generated fixture data, and shared seed helpers so ordinary
  suites neither insert nor depend on RI rows: `MaterializedDocumentFixtureSeeder`
  (`AddReferentialIdentityCommands`), `MaterializedDocumentFixtureCatalog`
  (`ReferentialIdentityRows` / `MaterializedDocumentSourceReferentialIdentityRow`),
  `AuthorizationWriteSideEffectState.ReferentialIdentityRows`, and the
  `Be.Vlaanderen.Basisregisters.Generators.Guid.Deterministic` package references (and lock files)
  in `Backend.Mssql.Tests.Integration` and `Backend.Postgresql.Tests.Integration`. Run both database integration estates against the
  transition schema with RI seeding disabled. Investigate an absent-RI-row failure as a surviving
  reader; do not fix it by reseeding.
- Second, remove `TR_<R>_ReferentialIdentity`, `dms.ReferentialIdentity`, `dms.uuidv5`, DMS-generated
  `CREATE EXTENSION pgcrypto` / `digest()` usage, the SQL Server `dms.UniqueIdentifierTable` TVP type, RI
  table/index/trigger inventories, RI manifest entries, and legacy
  `UX_Descriptor_Uri_Discriminator` uniqueness.
- Remove operational remnants: drop `dms."ReferentialIdentity"` from the TRUNCATE list in
  `eng/azure-vm/compose/seed/clone-data.sh` (the script itself stays — it is the general seed-clone
  path referenced by `grandbend.sh` and `eng/azure-vm/docs/infrastructure.md`),
  `eng/DatabaseTemplates/Template-Management.psm1` (the `CREATE EXTENSION IF NOT EXISTS "pgcrypto"`
  template-backup preamble emitted "because `dms.uuidv5()` requires `digest()`", plus its
  `Template-Management.Tests.ps1` pins), and the `FindDocumentByReferentialId` step in
  `docs/DEADLOCK-ANALYSIS.md`; leave `eng/docker-compose/OpenIddict-Crypto.psm1` and
  `setup-openiddict.ps1` untouched (CMS/OpenIddict pgcrypto). Update public
  DDL contracts and all generated goldens.
- Remove `dms.ReferentialIdentity` from `CdcDmsManagedTableInventory` (the DMS-managed CDC table
  list that drives the PostgreSQL publication and SQL Server capture instances) and its CDC goldens.
  This is a public CDC contract change — the RI change stream disappears for downstream consumers —
  and must be recorded in the release notes.
- Delete the `ReferentialIdentityLookupCount` summary field and its `dms."ReferentialIdentity"`
  text matcher from the write-session command-stream classifier; the natural-key classification
  added in DMS-1451 remains the round-trip pin. The public DDL-contract changes are: the
  `ReferentialIdentityMaintenance` trigger kind and `SuperclassAliasInfo` types are deleted,
  `IdentityElementMapping` shrinks from arity 4 to 2 (`ScalarType`/`IsDescriptorReference` existed
  only for hash emission), and `ISqlDialect.CreateUuidv5Function` is removed — a breaking change for
  Managed-API dialect implementers that must be called out in the release notes.
- Delete the DMS-1445 every-resource parity guard and its fixtures together with the
  `ReferentialIdentityMaintenance` metadata it compares against; nothing else may keep a reference
  to RI trigger metadata.
- Retain DocumentCache enqueue, stamping, change-tracking, and abstract-identity triggers.
- Do not drop `pgcrypto` from an existing database because CMS/OpenIddict may own it in shared
  deployments.
- Keep production contract changes and compile-time test migrations in DMS-1451, DMS-1452, or DMS-1454 according to
  the removed contract.
- Treat the transition-schema run as an internal checkpoint, not a separate delivery state. Do not
  merge until final removal passes.

## Acceptance Criteria

- The seeding-disabled transition schema and final schema pass on PostgreSQL and SQL Server.
- Final production-source scans find no RI reader/writer, referential-ID contract, UUIDv5
  implementation, RI trigger/table/TVP/inventory, operational truncate, CDC managed-table entry,
  template pgcrypto preamble, or `Be.Vlaanderen` package reference in any csproj or lock file.
- CDC bootstrap (publication / capture-instance) succeeds against the final schema on both providers
  and the CDC inventory goldens contain no `dms.ReferentialIdentity`.
- Retained trigger-family parity tests pass on both providers; no test references RI trigger
  metadata or the removed parity guard.
- The manifest schema and golden diffs show the `IdentityElementMapping` arity change and no
  `SuperclassAliasInfo` or `CreateUuidv5Function` in the public contracts; the release notes record
  the `ISqlDialect` breaking change.
- PostgreSQL DMS-generated DDL contains no `dms.uuidv5()`, `digest(`, or DMS-owned
  `CREATE EXTENSION pgcrypto`.
- Derived constraint inventories, manifests, and generated DDL contain no
  `UX_Descriptor_Uri_Discriminator` uniqueness.
- Version/hash expectations retain the confirmed unreleased `v3` mapping version and re-bless the
  current schema-hash pins.
- Rollback after DMS-1454, including after this schema removal, requires re-provisioning with the previous
  build or an explicitly designed backfill.
