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
The evidence phase owns the checked-in DMS-1185 harness and two-provider result artifact defined below.
The implementation phase begins only if both providers pass every benefit, regression, write, and storage gate; SQL Server does not inherit PostgreSQL's exception.
If either provider fails, the story records the result and returns the design for review without emitting the Tier-1 set on either provider.

## Evidence Protocol and Gates

The harness may extend the cross-provider workflow from `../12-ops-guardrails/04-performance-benchmarks.md`, but its artifact must pin the tracked-change-specific schema, seed cardinalities, exact query text and parameters, qualifying-row counts, database image/version, statistics preparation, and cache-preparation policy.

The mandatory matrix covers:

- all four `*IncludingDeletes` views at single-subject and district-scale subject cardinalities;
- descriptor `/deletes` at full and narrow `ChangeVersion` windows;
- regular-resource `/deletes` at full and narrow windows for both a PrimaryAssociation resource and a non-PrimaryAssociation resource using a people strategy;
- `/keyChanges` for those same resource classes and subject cardinalities;
- bulk tombstone/key-change inserts and the resulting candidate-index storage.

For every read or write A/B shape:

1. Use the same provisioned data and statistics for baseline and candidate.
2. Run five unmeasured warm-ups per variant.
3. Record twenty measured pairs, alternating `A → B` and `B → A` order.
4. Record raw elapsed time, the provider execution plan, PostgreSQL buffer counts or SQL Server logical reads, and the returned/qualifying row counts.
5. Use the median of the twenty paired `candidate / baseline` elapsed-time ratios as the gate.
6. Treat a run as noisy when either variant's median absolute deviation divided by its median exceeds 15%; rerun the full comparison once, and leave the decision blocked if the second run is also noisy.

Provider-specific read gates:

- PostgreSQL: every tracked PA index is used by at least one corresponding `*IncludingDeletes` arm, and the four-view matrix demonstrates at least a 20% improvement in median elapsed time or buffer reads. The shared descriptor candidate demonstrates at least a 20% improvement in median elapsed time or buffer reads in at least one descriptor `/deletes` window. Every measured read shape has a median elapsed-time ratio at or below 1.20 except the exact checked-in narrow-window PA-resource `/deletes` fixture, whose anti-join flip is permitted only at or below 5.0. No other shape may acquire that per-row join-filter/nested-loop form.
- SQL Server: no PostgreSQL exception is inherited. Every tracked PA index is used by at least one corresponding `*IncludingDeletes` arm as a covering seek without lookups, and the four-view matrix demonstrates at least a 20% improvement in median elapsed time or logical reads. The shared descriptor candidate demonstrates at least a 20% improvement in median elapsed time or logical reads in at least one descriptor `/deletes` window. Every measured read shape has a median elapsed-time ratio at or below 1.20.
- Live descriptor identity candidate on SQL Server: defer it only if it improves neither median elapsed time nor logical reads by at least 20% in either required descriptor-probe shape. If it reaches that threshold without causing another measured read shape to exceed 1.20, adopt it for SQL Server through the dialect-aware inventory.

Provider-specific cost gates:

- PostgreSQL: the Tier-1 candidate's bulk-write median elapsed-time ratio is at or below 1.85, and its added index storage is at or below 40% of the five PA plus shared-descriptor tracked-table size.
- SQL Server: the Tier-1 candidate's bulk-write median elapsed-time ratio is at or below 2.00, and its added index storage is at or below 50% of the corresponding tracked-table size.

Both providers must pass all applicable benefit, regression, and cost gates before the provider-neutral Tier-1 implementation begins.
A failure blocks emission on both providers until the design is revised with reviewed evidence; the result is not waived as run-to-run noise.
The 1.20 regression ceiling and 20% minimum benefit require a material improvement while allowing bounded unrelated-plan variation; PostgreSQL's 5.0 exception rounds the measured 4.7x result upward, and its 1.85/40% cost ceilings bound the measured 1.69x write ratio and approximately 30% storage with explicit margin.
SQL Server uses the more conservative 2.00/50% cost ceilings because no provider measurement exists yet.
Absolute times are evidence, not gates, because they vary across hosts and CPU architectures.

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
- Phase 1 checks in the reproducible DMS-1185 harness and raw/result artifact, executes the mandatory read/write/storage matrix above on both providers, and records the isolated SQL Server live descriptor identity-index comparison. The artifact demonstrates that both providers pass before any production code or golden changes in this story begin; the story does not grant exceptions beyond those defined above.
- After emission, rerun the checked-in DMS-1185 harness against the exact implemented DDL and require the same provider-specific gates to pass. A failure blocks acceptance rather than being described as additional run-to-run noise.
