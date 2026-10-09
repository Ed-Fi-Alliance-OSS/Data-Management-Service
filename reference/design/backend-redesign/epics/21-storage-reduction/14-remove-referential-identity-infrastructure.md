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
  `AuthorizationWriteSideEffectState.ReferentialIdentities`, and the
  `Be.Vlaanderen.Basisregisters.Generators.Guid.Deterministic` package references (and lock files)
  in `Backend.Mssql.Tests.Integration` and `Backend.Postgresql.Tests.Integration`, whose consumers
  there are `Uuidv5ParityTests` (deleted with `dms.uuidv5()`) and the RI-expectation helpers in the
  authoritative smoke tests below; the `Core` and `Core.External` references to that package go with
  `ReferentialIdFactory` in DMS-1454. The same applies to these RI writers and readers:
  - the performance harness fixtures in `src/dms/tests/EdFi.DataManagementService.Performance.Harness`
    (`PerfFixtureLoader`, `PerfDescriptorFixtureLoader`, `PerfAuthorizationSeeder`, their
    `Pgsql*`/`Mssql*` SQL classes, the smoke scenarios, `ReferentialIdentityDerivation` and its own
    UUIDv5 implementation, the matching `Performance.Harness.Tests.Unit` pins, and the harness
    README). They insert `dms.ReferentialIdentity` rows with raw SQL and do not compile against
    Core's referential-ID types, so DMS-1454 does not catch them.
  - `DocumentCacheCompletedProjectionScenario` in `EdFi.DataManagementService.Tests.Integration`,
    which snapshots `dms.ReferentialIdentity` rows, and the `SectionReferentialIdentityScenario` /
    `Given_{Postgresql,Mssql}_SectionReferentialIdentity` suites (rewrite as natural-key resolution
    coverage or delete if DMS-1451 already superseded them).
  - `MssqlGeneratedDdlAuthoritativeSmokeTests` and `PostgresqlGeneratedDdlAuthoritativeSmokeTests`,
    which compute the `dms.ReferentialIdentity` rows they expect with their own `Deterministic.Create`
    helper mirroring `ReferentialIdFactory`.

  Run both database integration estates against the transition schema with RI seeding disabled.
  Investigate an absent-RI-row failure as a surviving reader; do not fix it by reseeding.
- Second, remove `TR_<R>_ReferentialIdentity`, `dms.ReferentialIdentity`, `dms.uuidv5`, DMS-generated
  `CREATE EXTENSION pgcrypto` / `digest()` usage, RI table/index/trigger inventories, RI manifest
  entries, and the transition's unlowered `UX_Descriptor_ResourceKeyId_Uri` uniqueness.
  Retain `UX_Descriptor_UriLowered_ResourceKeyId`. DMS-1404 already removed
  `UX_Descriptor_Uri_Discriminator` and `IX_Descriptor_Discriminator_ContentVersion`;
  neither is a cleanup prerequisite here. Keep the SQL Server `dms.UniqueIdentifierTable`
  TVP type: `MssqlRepresentationRestampStore` (DMS-1318) binds it to select documents by
  `DocumentUuid` for the DocumentCacheAdmin `--document-uuid` restamp scope; its RI consumer
  (`MssqlReferenceLookupBulkStrategy`) is already gone with DMS-1454.
- Remove operational remnants: drop `dms."ReferentialIdentity"` from the TRUNCATE list in
  `eng/azure-vm/compose/seed/clone-data.sh` (the script itself stays — it is the general seed-clone
  path referenced by `grandbend.sh` and `eng/azure-vm/docs/infrastructure.md`),
  `eng/DatabaseTemplates/Template-Management.psm1` (the `CREATE EXTENSION IF NOT EXISTS "pgcrypto"`
  template-backup preamble emitted because `dms.uuidv5()` requires pgcrypto's `digest()`, plus the
  `eng/DatabaseTemplates/tests/Template-Management.Tests.ps1` pins), and a note in
  `docs/DEADLOCK-ANALYSIS.md` that its September 2024 sequence predates the relational backend and
  the RI table; leave `eng/docker-compose/OpenIddict-Crypto.psm1` and `setup-openiddict.ps1`
  untouched (CMS/OpenIddict pgcrypto). Update public DDL contracts and all generated goldens.
- Update the Northridge dataset tooling (DMS-1406), `eng/northridge/Copy-NorthridgeDataForward.ps1`,
  for this epic's final schema:
  - Stop copying `ReferentialIdentity`. Remove it from `$script:DmsDataTable`, skip the archive's RI
    rows explicitly, and update the source/target table-coverage and "ReferentialIdentity ->
    Document" integrity checks so the dropped table is expected, not reported as missing.
  - Derive the `ResourceKeyId` that DMS-1444 adds as NOT NULL to every abstract identity table by
    loading those tables through the staging schema and taking the value from `dms.Document`, as the
    script does for `dms.Descriptor.ResourceKeyId`. Those tables live in `edfi`, which the script
    today restores in bulk, so they need their own staged load. Leave the target schema unchanged.
  - Update `eng/northridge/README.md` to match. `Add-NorthridgeGapDocument.ps1` writes through the
    API, so only its help text, which lists `dms.ReferentialIdentity` among the rows the API
    produces, changes.
- Republish both Northridge artifacts on the final schema. The README treats the PostgreSQL and SQL
  Server `.7z` artifacts as a matched pair (same documents, same effective schema), and both predate
  this epic's schema. `Copy-NorthridgeDataForward.ps1` handles PostgreSQL only and there is no
  SQL Server carry-forward tool, so producing the SQL Server artifact is part of this work.
- Remove `dms.ReferentialIdentity` from `CdcDmsManagedTableInventory` (the DMS-managed table list
  the CDC connector-principal privilege audit checks against) and the CDC inventory test pins. The
  table was never published, so no CDC change stream changes.
- Delete the `ReferentialIdentityLookupCount` summary field and its per-provider RI text matchers
  from the write-session command-stream classifier; the natural-key classification
  added in DMS-1451 remains the round-trip pin.
- The public DDL-contract changes are: the `ReferentialIdentityMaintenance` trigger kind and
  `SuperclassAliasInfo` types are deleted, `IdentityElementMapping` shrinks from arity 4 to 2
  (`ScalarType`/`IsDescriptorReference` existed only for hash emission), and
  `ISqlDialect.CreateUuidv5Function` is removed — a breaking change for external `ISqlDialect`
  implementers that must be called out in the release notes.
- Delete the DMS-1445 every-resource parity guard and its fixtures together with the
  `ReferentialIdentityMaintenance` metadata it compares against; nothing else may keep a reference
  to RI trigger metadata.
- Retain DocumentCache enqueue, stamping, change-tracking, and abstract-identity triggers.
- Do not drop `pgcrypto` from an existing database because CMS/OpenIddict may own it in shared
  deployments.
- Keep Core and write-request contract changes, and their compile-time test migrations, in
  DMS-1451, DMS-1452, or DMS-1454 according to the removed contract; the DDL-contract changes above
  are this story's.
- Treat the transition-schema run as an internal checkpoint, not a separate delivery state. Do not
  merge until final removal passes.

## Acceptance Criteria

- The seeding-disabled transition schema and final schema pass on PostgreSQL and SQL Server.
- Final production-source scans find no RI reader/writer, referential-ID contract, UUIDv5
  implementation, RI trigger/table/inventory, operational truncate, CDC managed-table entry,
  template pgcrypto preamble, or `Be.Vlaanderen` package reference in any csproj, lock file, or
  `src/Directory.Packages.props`. The scans also cover `src/dms/tests` (including the performance
  harness and API integration tests), `src/dms/clis` test projects (for example
  `SchemaTools.Tests.Integration`'s `ProvisionTestHelper.cs` table list and
  `DdlProvisionMssqlTests.cs`), and `eng/northridge`, where the only permitted mentions are those
  `Copy-NorthridgeDataForward.ps1` needs to skip the archive's dropped `ReferentialIdentity` table
  and expect its absence.
- `Copy-NorthridgeDataForward.ps1` completes against the final schema with no guard bypassed or
  edited; abstract identity rows carry the `ResourceKeyId` of their owning `dms.Document` row.
- Both republished Northridge artifacts (PostgreSQL and SQL Server) are on the final schema, remain
  a matched pair, and contain no `dms.ReferentialIdentity` data.
- CDC bootstrap (publication / capture-instance) succeeds against the final schema on both providers
  and the CDC inventory test pins contain no `dms.ReferentialIdentity`.
- Tests for the retained trigger families (DocumentCache enqueue, stamping, change-tracking,
  abstract identity) pass on both providers; no test references RI trigger metadata, the removed
  parity guard, or `dms.uuidv5()`.
- The public DDL contract shows the `IdentityElementMapping` arity change and
  `Backend.Ddl.PublicContract.CompileCheck` is extended to pin `IdentityElementMapping` and
  `ISqlDialect`; manifest goldens show `identity_elements` gone with the RI trigger entries; neither
  carries `SuperclassAliasInfo` or `CreateUuidv5Function`; the release notes record the
  `ISqlDialect` breaking change.
- PostgreSQL DMS-generated DDL contains no `dms.uuidv5()`, `digest(`, or DMS-owned
  `CREATE EXTENSION pgcrypto`.
- Derived constraint inventories, manifests, and generated DDL contain no
  `UX_Descriptor_ResourceKeyId_Uri` uniqueness and retain the lowered-URI index; the previously
  removed `UX_Descriptor_Uri_Discriminator` and `IX_Descriptor_Discriminator_ContentVersion`
  remain absent.
- Preserve native int DescriptorId, unique bigint DocumentId, compact stored descriptor references,
  reconstructed URI output and ResourceKeyId history after the unlowered index is removed.
- Follow release cadence: current 8.1 changes retain `RelationalMappingVersion=v3`. The next
  legitimate bump is `v4` at the first qualifying post-8.1 mapping change, at most once per release.
  Generated DDL/mapping output is not hashed; deliberately reprovision older physical schemas
  even if startup accepts a matching fingerprint. Do not prescribe a bump per physical change.
- Rollback after DMS-1454, including after this schema removal, requires re-provisioning with the previous
  build or an explicitly designed backfill.
