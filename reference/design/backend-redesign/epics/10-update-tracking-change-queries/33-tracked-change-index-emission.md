---
jira: TBD
jira_url: TBD
---

# Story: Emit Tier-1 Auth-Check Indexes on `tracked_changes_*` Tables

## Description

Implement the Tier-1 index set decided by spike DMS-1185 and specified normatively in `change-queries.md` § "Indexes on the `tracked_changes*` tables": the five tracked PrimaryAssociation covering indexes and the shared-descriptor `(Discriminator, ChangeVersion)` index, derived by a new `DeriveTrackedChangeIndexInventoryPass`.

These indexes back the tracked-change arms of the four `*IncludingDeletes` authorization views (evaluated on every people-strategy `/deletes` and `/keyChanges` request for any resource) and the `Discriminator` filter every descriptor `/deletes` applies to the shared tracked-change table.
Measured improvements on PostgreSQL at 10M tombstones: 3.5-10x on view evaluation, 1.5-10x on descriptor `/deletes`, 3x on `/keyChanges` and single-school `/deletes`, at ~1 µs/row bulk-insert overhead.
One bounded regression is known, accepted, and disclosed in `change-queries.md` § "Indexes on the `tracked_changes*` tables" (narrow-window `/deletes` on the PA resources); the spike's measurements are PostgreSQL-only, so this story also owns producing the SQL Server evidence.

Per-resource securable-element indexes are explicitly out of scope (deferred pending the runtime query-shape change; see the follow-on subject-preresolution story).

## Acceptance Criteria

- New `DeriveTrackedChangeIndexInventoryPass` in `Backend.RelationalModel/SetPasses`, ordered in `RelationalModelSetPasses` immediately after `DeriveAuthorizationIndexInventoryPass` and before `ApplyDialectIdentifierShorteningPass`, with an ordering comment following the file's convention.
- The pass takes the same strictness flag as `DeriveAuthorizationIndexInventoryPass` (`throwOnMissingPaLiteral` semantics), wired from `CreateStrict`/`CreateDefault`.
- Emits the five tracked PrimaryAssociation covering indexes per the table in `change-queries.md` § "Indexes on the `tracked_changes*` tables": `DbIndexKind.Authorization`, key and INCLUDE columns mapped through `TrackedChangeNameConventions.OldValueColumn` from the existing PA literals, names via `ConstraintNaming.BuildAuthorizationIndexName`.
- Per-table gating: emit only when the tracked table is present in `TrackedChangeInventory` and carries both mapped columns; strict pipeline throws on a missing literal column, default pipeline skips.
- Emits the shared-descriptor index: `DbIndexKind.Explicit`, key columns `[Discriminator, ChangeVersion]` sourced from the `SharedDescriptor` table's system columns, name via `ConstraintNaming.BuildExplicitIndexName`.
- Emits nothing else: no per-resource securable, person, or namespace indexes, and no entries for `Resource`/`ConcreteAbstract` tables beyond the five PA tables.
- No DDL-emitter or manifest-emitter code changes are needed; assert the entries flow through `RelationalModelDdlEmitter.EmitIndexes` (both dialects) and `DerivedModelSetManifestEmitter.WriteIndexes` unchanged.
- Extend the `DbIndexInfo.IncludeColumns` doc comment in `compiled-mapping-set.md` (which enumerates the non-null INCLUDE users) to name the tracked PA covering indexes.
- `RelationalMappingVersion` bump with the locked-hash bless procedure.
- Unit tests (`DeriveTrackedChangeIndexInventoryPassTests`, using `CommonInventoryTestSchemaBuilder`): PA table present/absent, missing mapped column under strict (throws) and default (skips), shared-descriptor emission, no per-resource emission, deterministic ordering, and identifier-shortening interplay for long tracked-change table names.
- Golden regeneration (`UPDATE_GOLDENS=1`, full suite): `Fixtures/authoritative/{sample,ds-5.2,ds-5.2-tpdm}` (pgsql.sql, mssql.sql, ddl.manifest.json, relational-model manifests), `Backend.Ddl.Tests.Unit/Fixtures/ddl-emission` including a new focused tracked-change-index case, `Backend.IntegrationFixtures`, and `RelationalModel.Tests.Unit` fixture families.
- `Backend.{Postgresql,Mssql}.Tests.Integration` generated-DDL authoritative smoke tests pass with the new indexes.
- SQL Server A/B evidence for the Tier-1 set, using the spike's benchmark shapes translated to T-SQL, covering all four read surfaces: `*IncludingDeletes` view evaluation, descriptor `/deletes`, regular-resource `/deletes` (full and narrow `ChangeVersion` windows, including the shape that regressed on PostgreSQL), and `/keyChanges`; plus bulk tombstone insert overhead and index storage, completing the spike's cost quantification on the second dialect. Results gate against the acceptance envelope in `change-queries.md` § "Indexes on the `tracked_changes*` tables".
- PostgreSQL verification that the known narrow-window `/deletes` regression on the PA resources remains within the acceptance envelope defined in `change-queries.md` § "Indexes on the `tracked_changes*` tables" (at most 5x relative and 3 seconds absolute at the 10M-tombstone benchmark scale; no other measured shape regresses beyond run-to-run noise); record the measurement.
