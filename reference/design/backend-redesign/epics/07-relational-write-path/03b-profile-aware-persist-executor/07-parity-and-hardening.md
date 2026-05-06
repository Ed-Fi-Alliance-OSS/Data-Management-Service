---
jira: DMS-1124
---

# Slice 7: Parity And Hardening

## Purpose

Close the planning sequence by:

- verifying pgsql and mssql parity for the supported profiled runtime shapes,
- tightening remaining contract/runtime hardening that is still required for safe merge, and
- extracting any remaining non-blocking fragility into explicit named follow-ups instead of leaving it as silent debt.

This slice exists to keep the earlier slices focused on semantic support rather than letting every review thread expand into "and also parity/hardening/cleanup."

Earlier slices still own parity expectations for any SQL-sensitive behavior they introduce. This final slice is the branch-wide parity audit and gap-closure pass, not the first point where parity becomes required.

## In Scope

- PostgreSQL and SQL Server parity review for supported profiled shapes
- Dialect-sensitive batching, parameterization, and locking checks where behavior could diverge
- Remaining reverse-coverage or contract-hardening work required for safe merge
- Explicit follow-up extraction for unresolved but non-blocking risks
- Explicit handoff to `DMS-1132` / `../07-semantic-identity-presence-fidelity.md` for presence-sensitive semantic identity fidelity unless that work is intentionally absorbed here
- Decision on profile vs no-profile collection ordinal-base alignment. Slice 6 (`06-profile-guarded-no-op.md`) ships profile-aware guarded no-op while the no-profile flatten path stamps 0-based ordinals (`RelationalWriteFlattener` `RequestOrder`) and the profile collection walker stamps 1-based `finalOrdinal = i + 1`. Documents created via the no-profile path therefore cannot reach the guarded no-op short-circuit on a later identical profiled PUT — the merged ordinals differ from the stored ordinals and the executor falls through to real collection DML. Slice 6's top-level-collection integration fixture acknowledges this by seeding through the profiled POST path (see `PostgresqlProfileGuardedNoOpTests.cs` lines 666-674). This slice owns the decision to either (a) align ordinal bases on one side, (b) document and accept the first-write DML cost on pre-profile data, or (c) plan a backfill/normalization migration.
- Decision on whether to extract the no-profile `TableStateBuilder`'s collection row sort (`OrderCollectionRowsIfFullyBound` / `OrderCollectionAlignedExtensionScopeRowsIfFullyBound` / `OrderRowsByBindingIndexesIfFullyBound` plus the `BoundRowComparer` it depends on) into a shared `RelationalWriteMergeSupport` helper and apply it from `ProfileTableStateBuilder.Build()`. The profile path currently relies on the planner's emission order matching DB hydration ordinal order for `RelationalWriteGuardedNoOp.IsNoOpCandidate`'s positional `SequenceEqual` to fire correctly — Slice 6 ships that alignment as a coincidence-of-implementation rather than an enforced invariant. The synthesizer-level fixture `Given_Synthesizer_TopLevelCollection_All_Matched_With_Hidden_Interleaving_Is_NoOp` (added in `RelationalWriteProfileMergeSynthesizerTests.cs`) pins the property under test, but does not enforce it through a sort step. The candidate helpers total ~113 LOC including `BoundRowComparer`, putting the extraction over the threshold for slice-scoped hardening. Slice 7 owns the decision to extract and unify, accept the implicit alignment, or replace it with an alternative invariant such as a builder-side identity-based pairing.

## Explicitly Out Of Scope

- Broad refactor/cleanup unrelated to correctness
- New feature scope beyond the supported profiled slices

## Supported After This Slice

- Supported profiled runtime scenarios have explicit pgsql/mssql parity expectations.
- Dialect-sensitive code paths have the minimum required coverage to support merge confidence.
- Residual non-blocking risks are documented as explicit follow-ups rather than carried invisibly by `DMS-1124`.

## Design Constraints

- Parity is not just "tests exist"; the branch must either demonstrate equivalent behavior or document why a difference is expected.
- Hardening work that is not a merge blocker must become an explicit follow-up rather than expanding the slice indefinitely.
- Presence-sensitive semantic identity fragility should stay tied to `DMS-1132` unless this slice explicitly changes the merge guarantee.

## Acceptance Criteria

- The supported profiled runtime baseline passes on both PostgreSQL and SQL Server.
- Dialect-sensitive batching/locking/parameterization behavior has explicit coverage or explicit review rationale.
- Remaining unresolved correctness assumptions are documented as explicit follow-ups, not hidden in comments or oral history.
- `DMS-1132` / `../07-semantic-identity-presence-fidelity.md` remains the named follow-on for presence-sensitive semantic identity fidelity unless the implementation truly closes that gap here.

## Tests Required

### Integration tests

- Supported-slice parity pass for pgsql
- Supported-slice parity pass for mssql
- Any dialect-sensitive batch/parameter-limit cases added by the supported slices
- Any dialect-sensitive lock/freshness behavior relevant to profiled guarded no-op
- Literal three-level update-allowed/create-denied chain at the provider level (parents → children → grandchildren). Slice 5 delivers this at the synthesizer level (`Given_three_level_chain_with_update_allowed_at_levels_1_and_2_create_denied_at_level_3`) and at the HTTP layer with a two-level matched-update / create-denied-child chain. As part of this slice's parity audit, decide whether the literal three-level provider fixture is merge-blocking; if yes, add a new `IntegrationFixtures/profile-nested-three-level-chain` fixture plus pgsql/mssql provider plumbing and a three-level rejection scenario. Otherwise explicitly document why synthesizer coverage plus two-level provider rejection coverage is sufficient.

### Documentation / review outputs

- Explicit list of non-blocking follow-ups extracted from the branch review
- Explicit statement of whether `DMS-1132` remains open and why

## Reviewer Focus

Reviewers for this slice should focus only on:

- pgsql/mssql parity,
- dialect-sensitive risk,
- whether remaining hardening items are true merge blockers or safe follow-ups, and
- whether follow-up extraction is explicit enough.

Reviewers should explicitly ignore:

- re-review of already accepted semantic slices unless a parity issue reveals a real correctness gap.

## Leaves Behind

After this slice, `DMS-1124` should have:

- a complete serial design plan,
- an explicit merge-blocker vs follow-up boundary, and
- a named handoff for any unresolved hardening such as `DMS-1132`.

## Audit Results

The Slice 7 audit walked the Scenario Ownership Map at `03b-profile-aware-persist-executor.md`
lines 67-81 and diff-walked five dialect-sensitive areas of the cumulative `DMS-1124` work
in `origin/main` (commits `329e9193` through `b62b8342` — Slices 1 through 6). The two passes
cross-checked each other: scenario-walk caught design-promised behaviors lacking dialect
coverage; diff-walk caught dialect-sensitive code lacking parity tests.

After the in-slice fixes listed below, all 23 supported-scenario rows resolve to either
`both` (parity demonstrated on PostgreSQL and SQL Server) or `n/a` (this slice itself).
Counts: 22 scenarios `both` (20 already paired pre-slice plus the 2 ex-`fix-in-slice`
items resolved by the mssql guarded no-op port), 0 `fix-in-slice` remaining, 1 `n/a`
(this slice's own deliverable row). The audit also reviewed pre-existing
PostgreSQL-only no-profile relational-write tests and descriptor tests; these are
outside `DMS-1124`'s scope and not merge risks (see `## Reviewed Non-Blockers`).
The only named handoff from this slice is `DMS-1132`.

## Parity Matrix

| Scenario | Slice | Pgsql Test | Mssql Test | Classification |
|----------|-------|-----------|-----------|----------------|
| `ProfileRootCreateRejectedWhenNonCreatable` | 1 | `PostgresqlProfileExecutorRoutingTests.cs` :: `Given_A_Profiled_Post_Create_Where_Root_Is_Not_Creatable` | `MssqlProfileExecutorRoutingTests.cs` :: `Given_A_Mssql_Profiled_Post_Create_Where_Root_Is_Not_Creatable` | both |
| `ProfileHiddenInlinedColumnPreservation` | 2 | `PostgresqlProfileRootTableOnlyMergeTests.cs` :: `Given_A_Profiled_Put_With_Hidden_Inlined_Column_Preservation` | `MssqlProfileRootTableOnlyMergeTests.cs` :: `Given_A_Mssql_Profiled_Put_With_Hidden_Inlined_Column_Preservation` | both |
| Inlined `ProfileVisibleButAbsentNonCollectionScope` | 2 | `PostgresqlProfileRootTableOnlyMergeFixtureTests.cs` :: `Given_A_Profiled_Put_With_VisibleAbsent_Inlined_Scope_Clears_Clearable_And_Preserves_Hidden` | `MssqlProfileRootTableOnlyMergeFixtureTests.cs` :: `Given_A_Mssql_Profiled_Put_With_VisibleAbsent_Inlined_Scope_Clears_Clearable_And_Preserves_Hidden` | both |
| Separate-table `ProfileVisibleButAbsentNonCollectionScope` | 3 | `PostgresqlProfileSeparateTableMergeFixtureTests.cs` :: `Given_A_ProfiledUpdate_With_VisibleAbsent_SeparateTableScope_DeletesIt` | `MssqlProfileSeparateTableMergeFixtureTests.cs` :: `Given_A_Mssql_ProfiledUpdate_With_VisibleAbsent_SeparateTableScope_DeletesIt` | both |
| `ProfileHiddenExtensionRowPreservation` | 3 | `PostgresqlProfileSeparateTableMergeFixtureTests.cs` :: `Given_A_ProfiledUpdate_With_Hidden_Extension_Row_PreservesIt` | `MssqlProfileSeparateTableMergeFixtureTests.cs` :: `Given_A_Mssql_ProfiledUpdate_With_Hidden_Extension_Row_PreservesIt` | both |
| Non-collection update-allowed / create-denied pair | 3 | `PostgresqlProfileSeparateTableMergeFixtureTests.cs` :: `Given_A_ProfiledUpsert_With_Creatable_False_ForNewSeparateTableScope_Rejects` + `Given_A_ProfiledUpdate_WithExistingSeparateTableScope_And_Creatable_False_AllowsUpdate` | `MssqlProfileSeparateTableMergeFixtureTests.cs` :: `Given_A_Mssql_ProfiledUpsert_With_Creatable_False_ForNewSeparateTableScope_Rejects` + `Given_A_Mssql_ProfiledUpdate_WithExistingSeparateTableScope_And_Creatable_False_AllowsUpdate` | both |
| Top-level `ProfileVisibleRowUpdateWithHiddenRowPreservation` | 4 | `PostgresqlProfileTopLevelCollectionMergeTests.cs` :: `Given_A_Postgresql_Profiled_TopLevelCollection_Merge` (test methods within fat fixture) | `MssqlProfileTopLevelCollectionMergeTests.cs` :: `Given_A_Mssql_Profiled_TopLevelCollection_Merge` | both |
| Top-level `ProfileVisibleRowDeleteWithHiddenRowPreservation` | 4 | `PostgresqlProfileTopLevelCollectionMergeTests.cs` :: `Given_A_Postgresql_Profiled_TopLevelCollection_Merge` | `MssqlProfileTopLevelCollectionMergeTests.cs` :: `Given_A_Mssql_Profiled_TopLevelCollection_Merge` | both |
| Top-level `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` + update-allowed / create-denied pair | 4 | `PostgresqlProfileTopLevelCollectionMergeTests.cs` :: `Given_A_Postgresql_Profiled_TopLevelCollection_Merge` (`top-level-collection-create-denied-update-put`, `top-level-collection-create-denied-insert-put` cases) | `MssqlProfileTopLevelCollectionMergeTests.cs` :: `Given_A_Mssql_Profiled_TopLevelCollection_Merge` (matching mssql cases) | both |
| Nested variant of `ProfileVisibleRowUpdateWithHiddenRowPreservation` | 5 | `PostgresqlProfileNestedCollectionMergeTests.cs` :: `Given_a_ProfileNested_put_request_updating_visible_children_with_a_hidden_sibling_in_storage` | `MssqlProfileNestedCollectionMergeTests.cs` :: `Given_a_ProfileNested_put_request_updating_visible_children_with_a_hidden_sibling_in_storage` | both |
| Root-level extension child variant of `ProfileVisibleRowUpdateWithHiddenRowPreservation` | 5 | `PostgresqlProfileTopLevelCollectionReferenceBackedMergeTests.cs` :: `Given_A_Postgresql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge` + `PostgresqlProfileNestedCollectionMergeTests.cs` :: `Given_a_ProfileNested_put_request_updating_root_extension_child_collection` | `MssqlProfileTopLevelCollectionReferenceBackedMergeTests.cs` :: `Given_A_Mssql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge` + `MssqlProfileNestedCollectionMergeTests.cs` :: `Given_a_ProfileNested_put_request_updating_root_extension_child_collection` | both |
| Collection-aligned extension child variant of `ProfileVisibleRowUpdateWithHiddenRowPreservation` | 5 | `PostgresqlProfileCollectionAlignedExtensionMergeTests.cs` :: `Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_modifying_an_aligned_extension_child_value` (+ siblings) | `MssqlProfileCollectionAlignedExtensionMergeTests.cs` :: `Given_a_ProfileCollectionAlignedExtension_update_request_modifying_an_aligned_extension_child_value` (+ siblings) | both |
| Nested variant of `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` (incl. update-allowed/create-denied chain) | 5 | `PostgresqlProfileNestedCollectionMergeTests.cs` :: `Given_a_ProfileNested_put_request_with_creatable_false_on_children_rejects_new_children` | `MssqlProfileNestedCollectionMergeTests.cs` :: `Given_a_ProfileNested_put_request_with_creatable_false_on_children_rejects_new_children` | both |
| Root-level extension child variant of `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` | 5 | `PostgresqlProfileTopLevelCollectionReferenceBackedMergeTests.cs` :: `Given_A_Postgresql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge` (insert-rejected cases inside fixture) | `MssqlProfileTopLevelCollectionReferenceBackedMergeTests.cs` :: `Given_A_Mssql_Profiled_TopLevelCollection_ReferenceBackedIdentity_Merge` | both |
| Collection-aligned extension child variant of `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` | 5 | `PostgresqlProfileCollectionAlignedExtensionMergeTests.cs` :: `Given_a_ProfileCollectionAlignedExtension_update_request_for_a_non_creatable_aligned_extension_scope_with_no_matching_stored_row` | `MssqlProfileCollectionAlignedExtensionMergeTests.cs` :: `Given_a_ProfileCollectionAlignedExtension_update_request_for_a_non_creatable_aligned_extension_scope_with_no_matching_stored_row` | both |
| `ProfileHiddenExtensionChildCollectionPreservation` | 5 | `PostgresqlProfileNestedCollectionMergeTests.cs` :: `Given_a_ProfileNested_put_request_with_hidden_root_extension_scope_preserves_children` | `MssqlProfileNestedCollectionMergeTests.cs` :: `Given_a_ProfileNested_put_request_with_hidden_root_extension_scope_preserves_children` | both |
| `ProfileUnchangedWriteGuardedNoOp` — root-only PUT | 6 | `PostgresqlProfileGuardedNoOpTests.cs` :: `Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape` | `MssqlProfileGuardedNoOpTests.cs` :: `Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Root_Only_Shape` | both |
| `ProfileUnchangedWriteGuardedNoOp` — root-only POST-as-update | 6 | `PostgresqlProfileGuardedNoOpTests.cs` :: `Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Post_As_Update_With_Root_Only_Shape` | `MssqlProfileGuardedNoOpTests.cs` :: `Given_A_Mssql_Relational_Profile_Guarded_No_Op_Post_As_Update_With_Root_Only_Shape` | both |
| `ProfileUnchangedWriteGuardedNoOp` — stale PUT | 6 | `PostgresqlProfileGuardedNoOpTests.cs` :: `Given_A_Postgresql_Relational_Profile_Stale_Guarded_No_Op_Put` | `MssqlProfileGuardedNoOpTests.cs` :: `Given_A_Mssql_Relational_Profile_Stale_Guarded_No_Op_Put` | both |
| `ProfileUnchangedWriteGuardedNoOp` — stale POST-as-update | 6 | `PostgresqlProfileGuardedNoOpTests.cs` :: `Given_A_Postgresql_Relational_Profile_Stale_Guarded_No_Op_Post_As_Update` | `MssqlProfileGuardedNoOpTests.cs` :: `Given_A_Mssql_Relational_Profile_Stale_Guarded_No_Op_Post_As_Update` | both |
| `ProfileUnchangedWriteGuardedNoOp` — separate-table PUT | 6 | `PostgresqlProfileGuardedNoOpTests.cs` :: `Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Separate_Table_Shape` | `MssqlProfileGuardedNoOpTests.cs` :: `Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Separate_Table_Shape` | both |
| `ProfileUnchangedWriteGuardedNoOp` — top-level-collection PUT | 6 | `PostgresqlProfileGuardedNoOpTests.cs` :: `Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Top_Level_Collection_Shape` | `MssqlProfileGuardedNoOpTests.cs` :: `Given_A_Mssql_Relational_Profile_Guarded_No_Op_Put_With_Top_Level_Collection_Shape` | both |
| pgsql/mssql parity closure and explicit `DMS-1132` handoff | 7 | n/a | n/a | n/a |

## Hardening Decisions

### Profile / no-profile collection ordinal-base alignment

- Decision: Align profile path → 0-based.
- Rationale: DMS internal collection ordinals are 0-based storage positions
  derived from JSON array position, not Ed-Fi semantic sequence values. The
  no-profile flatten path and current-state hydration tests already treat the
  column as 0-based; the profile path was the outlier. Aligning profile avoids
  a storage-convention migration and avoids latent surprise for reviewers/operators
  inspecting persisted state.
- Implementation: `Profile/ProfileCollectionWalker.cs` `finalOrdinal = i` (was
  `i + 1`). XML doc on `ProfileCollectionMatchedRowOverlay.StampOrdinal` updated
  to "0-based storage position". See commit `8928bec3`.
- Future option: If a product decision later requires 1-based internal ordinals,
  that work must own backfill, compatibility notes, and dialect coverage as a
  separate story.

### Sort-helper extraction for guarded no-op row ordering

- Decision: Extract `OrderCollectionRowsForComparisonIfFullyBound`,
  `OrderCollectionAlignedExtensionScopeRowsForComparisonIfFullyBound`,
  `IsCollectionAlignedExtensionScope`, and `BoundRowComparer` into
  `RelationalWriteMergeSupport`. Both no-profile `TableStateBuilder.Build()` and
  `ProfileTableStateBuilder.Build()` apply the shared sort.
- Rationale: Guarded no-op's positional `SequenceEqual` precondition was
  previously satisfied by the profile planner's coincidence-of-implementation
  emission order. Now enforced by a sort step in both builders.
- Implementation: See commit `73085356`.

### Three-level provider fixture

- Decision: not required / no follow-up.
- Rationale: The persister consumes `mergeResult.TablesInDependencyOrder` as a
  flat `ImmutableArray`, not a recursive tree, so a 3-level chain merely adds one
  more loop iteration over the same provider plumbing. No depth-sensitive code
  fires only at 3-level. The synthesizer's depth-sensitive walker is already
  covered by the Slice 5 fixture
  `Given_three_level_chain_with_update_allowed_at_levels_1_and_2_create_denied_at_level_3`.
  Provider-plumbing risk did not surface in the audit.

## Fixed In This Slice

- Profile collection ordinal-base alignment to 0-based —
  `Profile/ProfileCollectionWalker.cs:415`, `Profile/ProfileCollectionMatchedRowOverlay.cs`
  XML doc, profile test ordinal sweep across pgsql + mssql + Backend.Tests.Unit
  (eight files), new pgsql regression `PostgresqlProfileGuardedNoOpOrdinalAlignmentTests`.
  Commit `8928bec3`.
- Test-comment policy cleanup on `PostgresqlProfileGuardedNoOpOrdinalAlignmentTests`
  to drop slice/Jira/workstream refs from header and XML docs. Commit `257e02ee`.
- Shared collection row ordering for guarded no-op —
  `RelationalWriteMergeSupport.cs` (added shared sort + comparer),
  `RelationalWriteNoProfileMerge.cs` (delegated, ~119 LOC removed),
  `Profile/ProfileCollectionWalker.cs::ProfileTableStateBuilder.Build()` (applies shared sort),
  unit tests `RelationalWriteMergeSupportRowOrderingTests`,
  `Profile/ProfileTableStateBuilderOrderingTests`, and a synthesizer-test
  fixture-builder fix to seed the parent-locator binding with a real `long`.
  Commit `73085356`.
- MSSQL guarded no-op parity for separate-table and top-level collection shapes —
  `MssqlProfileGuardedNoOpTests.cs` (two new fixtures + two intermediate base
  classes mirroring the pgsql side; pre-existing file header rewritten to drop
  stale slice references), new `MssqlProfileGuardedNoOpOrdinalAlignmentTests`
  (mssql twin of the cross-path regression). Commit `aa0a4951`.
- MSSQL generated-DDL baseline snapshot path separator preservation —
  `MssqlGeneratedDdlBaselineDatabase.BuildSnapshotPath()` now derives the snapshot
  path using the separator from SQL Server's physical file path instead of the host
  OS path separator, with regression coverage in
  `MssqlGeneratedDdlBaselineDatabaseTests`. Commit `7cdb030f`.

## Reviewed Non-Blockers

The audit considered several pre-existing parity gaps adjacent to `DMS-1124` and
confirmed each is outside this story's scope, is not a merge risk, and does not
require a new follow-up Jira from this slice:

- Legacy PostgreSQL-only no-profile relational-write integration tests
  (`PostgresqlRelationalWriteCollectionReorderTests.cs`,
  `PostgresqlRelationalWriteCreateBaselineTests.cs`,
  `PostgresqlRelationalWriteGuardedNoOpTests.cs`,
  `PostgresqlRelationalWriteMultiBatchCollectionTests.cs`,
  `PostgresqlRelationalWritePostAsUpdateSmokeTests.cs`,
  `PostgresqlRelationalWriteRollbackSafetyTests.cs`,
  `PostgresqlRelationalWriteUpdateSemanticsTests.cs`) lack mssql twins. The gap
  pre-dates `DMS-1124`; the dialect-emission code itself is exercised by the
  profile-side suite on both dialects.
- Legacy PostgreSQL-only descriptor tests
  (`PostgresqlDescriptorProjectionAliasTests.cs`,
  `PostgresqlDescriptorWriteTests.cs`) lack mssql twins. Both pre-date `DMS-1124`;
  descriptor projection, pipeline, collection projection, and referential
  identity all already have symmetric pgsql/mssql integration suites.

A literal three-level provider fixture is also reviewed-and-not-required for
this slice; the rationale is documented under `## Hardening Decisions`.

The only named handoff from this slice is `DMS-1132`, as documented below.

## DMS-1132 Handoff

`DMS-1132` (`../07-semantic-identity-presence-fidelity.md`) remains the named
follow-on for presence-sensitive semantic identity fidelity. Slice 7 did not
change the executor-facing identity-presence contract; this slice fixed
already-supported behavior (ordinal-base alignment, shared row ordering for
guarded no-op, mssql guarded no-op parity, and mssql snapshot-path hardening)
and explicitly did not pull `DMS-1132` work into `DMS-1124`. The
identity-matching fragility documented in `DMS-1132` is unchanged by this slice.
