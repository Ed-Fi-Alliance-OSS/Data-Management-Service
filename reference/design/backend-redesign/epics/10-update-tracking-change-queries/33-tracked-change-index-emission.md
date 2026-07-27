---
jira: TBD
jira_url: TBD
---

# Story: Emit Tier-1 Auth-Check Indexes on `tracked_changes_*` Tables

## Description

Implement the Tier-1 index set proposed by spike DMS-1185 and specified in `change-queries.md` § "Indexes on the `tracked_changes*` tables": the five tracked PrimaryAssociation covering indexes and the shared-descriptor `(Discriminator, ChangeVersion)` index, derived by a new `DeriveTrackedChangeIndexInventoryPass`.

These indexes back the tracked-change arms of the four `*IncludingDeletes` authorization views (evaluated on every people-strategy `/deletes` and `/keyChanges` request for any resource) and the `Discriminator` filter every descriptor `/deletes` applies to the shared tracked-change table.
Measured improvements on PostgreSQL at 10M tombstones: 3.5-10x on view evaluation, 1.5-10x on descriptor `/deletes`, 3x on `/keyChanges` and single-school `/deletes`, at ~1 µs/row bulk-insert overhead.
One bounded PostgreSQL regression is known and disclosed in `change-queries.md` § "Indexes on the `tracked_changes*` tables" (the exact narrow-window `/deletes` fixture on the PA resources).

Per-resource securable-element indexes are explicitly out of scope (deferred pending the runtime query-shape change; see the follow-on subject-cardinality story).
This story has an evidence phase followed by a gated implementation phase.
The evidence phase owns the checked-in DMS-1185 harness and two-provider result artifact defined in `22-auth-check-indexes-on-tracked-changes.md`.
The implementation phase begins only if both providers pass every benefit, regression, write, and storage gate; SQL Server does not inherit PostgreSQL's exception.
If either provider fails, the story records the result and returns the design for review without emitting the Tier-1 set on either provider.

## Acceptance Criteria

- New `DeriveTrackedChangeIndexInventoryPass` in `Backend.RelationalModel/SetPasses`, ordered in `RelationalModelSetPasses` immediately after `DeriveAuthorizationIndexInventoryPass` and before `ApplyDialectIdentifierShorteningPass`, with an ordering comment following the file's convention.
- The pass takes the same strictness flag as `DeriveAuthorizationIndexInventoryPass` (`throwOnMissingPaLiteral` semantics), wired from `CreateStrict`/`CreateDefault`.
- Emits the five tracked PrimaryAssociation covering indexes per the table in `change-queries.md` § "Indexes on the `tracked_changes*` tables": `DbIndexKind.Authorization`, key and INCLUDE columns mapped through `TrackedChangeNameConventions.OldValueColumn` from the existing PA literals, names via `ConstraintNaming.BuildAuthorizationIndexName`.
- Per-table gating: emit only when the tracked table is present in `TrackedChangeInventory` and carries both mapped columns; strict pipeline throws on a missing literal column, default pipeline skips.
- Emits the shared-descriptor index: `DbIndexKind.Explicit`, key columns `[Discriminator, ChangeVersion]` sourced from the `SharedDescriptor` table's system columns, name via `ConstraintNaming.BuildExplicitIndexName`.
- Emits nothing else: no per-resource securable, person, or namespace indexes, and no entries for `Resource`/`ConcreteAbstract` tables beyond the five PA tables.
- No DDL-emitter or manifest-emitter code changes are needed; assert the entries flow through `RelationalModelDdlEmitter.EmitIndexes` (both dialects) and `DerivedModelSetManifestEmitter.WriteIndexes` unchanged.
- Preserve the `DbIndexInfo.IncludeColumns` and `DbIndexKind.Explicit` contracts in `compiled-mapping-set.md`, including the tracked PA covering-index use and the query-performance use of `Explicit`.
- `RelationalMappingVersion` bump with the locked-hash bless procedure.
- Unit tests (`DeriveTrackedChangeIndexInventoryPassTests`, using `CommonInventoryTestSchemaBuilder`): PA table present/absent, missing mapped column under strict (throws) and default (skips), shared-descriptor emission and its absence when the model set has no descriptor resources, missing `Discriminator` system column under strict and default, no per-resource emission, deterministic ordering, and identifier-shortening interplay for long tracked-change table names.
- Golden regeneration (`UPDATE_GOLDENS=1`, full suite): `Fixtures/authoritative/{sample,ds-5.2,ds-5.2-tpdm}` (pgsql.sql, mssql.sql, ddl.manifest.json, relational-model manifests), `Backend.Ddl.Tests.Unit/Fixtures/ddl-emission` including a new focused tracked-change-index case, `Backend.IntegrationFixtures`, and `RelationalModel.Tests.Unit` fixture families.
- `Backend.{Postgresql,Mssql}.Tests.Integration` generated-DDL authoritative smoke tests pass with the new indexes.
- Phase 1 checks in the reproducible DMS-1185 harness and raw/result artifact, executes its mandatory read/write/storage matrix on both providers, and records the isolated SQL Server live descriptor identity-index comparison. The artifact demonstrates that both providers pass before any production code or golden changes in this story begin; the story does not redefine the approved methodology or grant new exceptions.
- After emission, rerun the checked-in DMS-1185 harness against the exact implemented DDL and require the same provider-specific gates to pass. A failure blocks acceptance rather than being described as additional run-to-run noise.
