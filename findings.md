# Review Findings

Items below are organized with validation and addressed status. No code remediation was performed in this pass.

## Finding 1: Nested collections below an inlined non-collection object appear unsupported

- Source: Agent 1, Finding 1
- Validation: valid
- Addressed: addressed

`src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionWalker.cs:1104` treats a child collection as direct only when the remainder after the parent scope has no `.`. That misses shapes like `$.parents[*].detail.children[*]`, where `detail` is an inlined object. `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteFlattener.cs:109` has the same practical limitation by grouping child plans under the immediate JSON parent scope. But `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs:274` now opens all current slice families, so this shape is no longer fenced. If that inlined-object topology is valid per the design, request/current rows can be skipped or coverage checks can run in the wrong effective context. Either keep this family fenced/fail closed, or drive child enumeration from the compiled topology so inlined scopes can be traversed.

Validation reasoning: Valid. `RelationalWriteFlattener.BuildCollectionChildPlansByParentScope` derives the parent key for `$.parents[*].detail.children[*]` as `$.parents[*].detail`, but `MaterializeCollectionCandidates` is only re-entered with table-backed parent scopes such as `$` and `$.parents[*]`; no call is made for the inlined non-collection scope itself. The profile walker has the same gap: `EnumerateDirectChildCollectionScopes` only yields a child when `IsDirectTopologicalChild` sees exactly one additional path segment, so from `$.parents[*]` it rejects `$.parents[*].detail.children[*]` because the remainder `detail.children[*]` contains another dot. `ScopeTopologyIndex` and the executor no longer fence this away because nested collection scopes classify as `NestedAndExtensionCollections`, and `DefaultRelationalWriteExecutor` currently passes that family unconditionally.

## Finding 2: Semantic identity matching collapses absent values and explicit JSON nulls

- Source: Agent 1, Finding 2
- Validation: valid
- Addressed: addressed

`src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionPlanner.cs:473`, `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionWalker.cs:1006`, and `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionRowHiddenPathExpander.cs:186` all key identity parts as value JSON or `"null"`. Several synthetic paths also set `IsPresent` from `rawValue is not null`, so present-null becomes absent. The design docs call out presence-sensitive `SemanticIdentityPart` fidelity; with the current keying, two rows can incorrectly match or be rejected as duplicates when one identity member is missing and the other is explicitly null. This should be centralized into one structural identity-key helper that includes presence plus canonical value.

Validation reasoning: Valid. The Core contract explicitly carries presence on `SemanticIdentityPart`, and the Core structural comparer includes `IsPresent`, but the backend planner, walker, and hidden-path expander derive identity keys from only `Value?.ToJsonString() ?? "null"`. That makes `IsPresent: false, Value: null` and `IsPresent: true, Value: null` produce the same key. The walker also synthesizes semantic identity parts from raw relational values with `IsPresent: rawValue is not null`, which converts present-null to absent. No shared presence-aware key helper or compensating path was found.

## Finding 3: The slice fence is now dead code for existing families

- Source: Agent 1, Finding 3
- Validation: valid
- Addressed: addressed

`src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs:274` returns true for every `RequiredSliceFamily` enum value, making the later unsupported-slice branch and `BuildSliceFenceResult` effectively unreachable. If all current slices are intentionally enabled, removing the fence path would simplify the executor and avoid suggesting unsupported shapes are still protected.

Validation reasoning: Valid. `RequiredSliceFamily` currently has exactly four defined families, and `DefaultRelationalWriteExecutor` maps each one to `true` in the fence switch. The only remaining switch arm throws for an unhandled future value, so execution can either pass the fence or throw before reaching the later `if (!fencePassed)` block. There is no current path that sets `fencePassed` to `false`, making `BuildSliceFenceResult` unreachable for existing families.

## Finding 4: Semantic identity key duplication is a source of null/presence risk

- Source: Agent 1, Simplicity Note 1
- Validation: valid
- Addressed: addressed

The semantic identity key logic is duplicated across planner, walker, and hidden-path expander; that duplication is also the source of the null/presence issue above. The reviewer suggested extracting it before adding more slice behavior.

Validation reasoning: Valid. `SemanticIdentityPart` explicitly preserves missing-vs-explicit-null with `IsPresent`, but the planner, walker, and hidden-path expander each build private string keys from only `Value?.ToJsonString() ?? "null"`, so an absent identity member and an explicit JSON null produce the same key. Nearby working code compares `RelativePath`, `IsPresent`, and serialized value together, so the duplicated key builders are inconsistent with intended identity semantics. No shared presence-aware helper is currently used.

## Finding 5: Physical identity extraction and parent projection duplication should be cleaned up

- Source: Agent 1, Simplicity Note 2
- Validation: valid
- Addressed: addressed

There is smaller duplication around physical identity extraction and parent projection in `ProfileCollectionWalker` / `RelationalWriteProfileMerge`; not a blocker, but worth cleaning once the correctness issues are addressed.

Validation reasoning: Valid as a cleanup/maintainability finding. `ProfileCollectionWalker` and `RelationalWriteProfileMerge` each define a private `ExtractPhysicalRowIdentityValues` helper with the same responsibility and nearly identical implementation, and the shared helper classes have not absorbed that logic. Parent identity projection is also repeated in the walker: one path builds the child-relevant parent lookup inline, while another uses a separate helper with the same slot-map projection shape. This is not a demonstrated correctness bug, but the cleanup has not been addressed.

Resolution validation: A shared `RelationalWriteMergeSupport.ExtractPhysicalRowIdentityValues(TableWritePlan, IReadOnlyList<FlattenedWriteValue>)` helper now drives physical-row-identity extraction for the no-profile synthesizer, profile-merge synthesizer, and profile collection walker; the three private duplicates in `RelationalWriteNoProfileMerge.cs`, `Profile/ProfileCollectionWalker.cs`, and `Profile/RelationalWriteProfileMerge.cs` were removed and every call site routes through the shared helper. The walker's inline child-relevant parent lookup projection around the current collection row lookup was replaced with a call to the existing `ProjectParentValuesForChildLookup` helper, leaving one walker-local source of truth for that projection (kept walker-local because it takes the walker-internal `ProfileCollectionWalkerContext` and `ResolveParentKeyPartSlotsForChild` only feeds it). Behavior-preserving: 0 build warnings, full backend unit suite 1125 / 1125 passing.

## Finding 6: Mirrored collection-aligned extension scopes can be skipped

- Source: Agent 2, Finding 1
- Validation: not valid
- Addressed: addressed

`src/dms/backend/EdFi.DataManagementService.Backend/Relational/RelationalWriteFlattener.cs:192` explicitly supports mirrored aligned scopes like `$._ext.sample.addresses[*]._ext.sample`, but `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionWalker.cs:1133` only discovers aligned extension child scopes when `childScope.StartsWith(parentScope)`. Mirrored scopes start at `$._ext...`, so they are not treated as direct children of `$.addresses[*]`. That means aligned extension rows can be preserved/skipped instead of merged. Existing aligned-extension tests appear to cover only the non-mirrored shape. The reviewer suggested fixing this by reusing the flattener's parent-scope/attachment logic or compiled locator metadata, and adding coverage for mirrored collection-aligned extension rows and nested child collections under them.

Validation reasoning: Not valid against current code. The flattener maps mirrored collection-aligned scopes back to the base parent scope, and `ProfileCollectionWalker` no longer relies only on `childScope.StartsWith(parentScope)` for `CollectionExtensionScope`; it accepts either the standard aligned shape or the mirrored `$._ext.<ext>.<parent>._ext.<ext>` shape. Mirrored request-node resolution also navigates from the request root using the parent candidate ordinal path. `ProfileCollectionWalkerMirroredScopeTests` covers the mirrored pair.

## Finding 7: Hidden descendant scope state may be lost for collection-aligned extension separate tables

- Source: Agent 2, Finding 2
- Validation: not valid
- Addressed: addressed

In `src/dms/backend/EdFi.DataManagementService.Backend/Profile/RelationalWriteProfileMerge.cs:692`, aligned extension separate-scope classification is synthesized with only the exact request/stored scope. The direct classifier then builds candidate scopes only from those exact scopes in `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileBindingClassificationCore.cs:376`. If a hidden member lives in an inlined descendant scope under the aligned extension, that descendant `StoredScopeState` is not considered, so hidden data can be classified as writable and overwritten or cleared. Root extensions and collection rows have broader/folded context, but this path does not. Add the descendant inlined scope states for the same scope instance/ancestor context, or fold their hidden paths into the aligned scope before classification and key unification.

Validation reasoning: Not valid against current code. The cited exact-scope-only path has been replaced: `BuildUpdateState` now collects inlined descendant scope states for the same separate-table instance and passes that envelope to both the classifier and key-unification resolver. The collector filters to strict descendants owned by the same physical table and the same ancestor collection-instance chain, and the classifier/resolver include those descendant request/stored scopes in candidate-scope matching and lookup. Bindings under an aligned extension descendant scope use the descendant `StoredScopeState.HiddenMemberPaths` or hidden visibility instead of falling through to the aligned parent scope.

## Finding 8: Ancestor canonicalization has a scope-wide fallback that can choose the wrong parent context

- Source: Agent 2, Finding 3
- Validation: not valid
- Addressed: addressed

`src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionWalker.cs:1640` builds current rows by `JsonScope` only, then `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionWalker.cs:1732` uses that to canonicalize visible-stored ancestor keys. Nested collection identity is only unique within its parent, so two parent instances with the same child scalar identity can collide or leave non-canonical ancestors unmatched. That can become especially visible for delete-by-absence, where a missed visible-stored bucket can preserve rows that should be deleted. This should be parent-context-aware, using the ancestor chain/physical parent identity rather than a scope-wide lookup.

Validation reasoning: Not valid against current code. Although `BuildCurrentRowsByJsonScope` still exists and some comments mention a scope-wide fallback, the active ancestor-canonicalization path builds and passes `currentRowsByJsonScopeAndParent`, derives raw and canonical target parent addresses for each ancestor, and resolves descriptor/document-reference ancestor identities inside the matching `(ancestorJsonScope, canonicalTargetParentAddress)` partition. Descriptor and document-reference fallbacks both require that parent partition and fail closed when it is unavailable. Focused tests for same identity/natural key under two parent instances passed.

## Finding 9: Correctness-critical key serialization is duplicated

- Source: Agent 2, Finding 4
- Validation: valid
- Addressed: addressed

`BuildSemanticIdentityKey` / `BuildCandidateIdentityKey` are repeated across `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionPlanner.cs:473`, `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionWalker.cs:1006`, and `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionRowHiddenPathExpander.cs:186`. These need to stay exactly aligned for merge correctness. The reviewer suggested extracting one internal helper and using it everywhere.

Validation reasoning: Valid. The same key serialization contract is implemented independently in the planner, walker, and hidden-path expander, and the code relies on those strings matching exactly for dictionary lookups between stored-row identities, visible request item addresses, and request candidates. Walker comments explicitly state that its helpers mirror the planner, confirming cross-file coupling rather than isolated local formatting. No extracted shared helper currently handles these keys.

## Finding 10: Some story/CP comments are now stale

- Source: Agent 2, Finding 5
- Validation: valid
- Addressed: addressed

Example: `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionWalker.cs:612` still says aligned-extension preservation "lands in CP3", but the code below already handles aligned scopes. There are several CP/task timeline comments like this. They make the merge code harder to audit and should be removed or rewritten as stable behavior comments.

Validation reasoning: Valid. `WalkChildrenPreserveMode` still says aligned-extension preservation "lands in CP3" and that "for now only collection children are preserved", but the method already detects `DbTableKind.CollectionExtensionScope`, calls `PreserveAlignedExtensionScope`, and that helper preserves the aligned extension row plus recurses in preserve mode. Nearby normal-mode comments also say collection-extension children arrive "after CP3" even though the branch dispatches them immediately below.

Resolution validation: Production profile-merge comments and diagnostics now use stable behavior descriptions instead of story/timeline labels. The stale `WalkChildrenPreserveMode` remarks now state that preserve mode handles both collection child scopes and collection-aligned extension scopes, preserving current rows and recursing into descendants. Related production comments and exception strings in the backend/core profile write path were cleaned of `Slice`, CP/task, and future-work language, and the test helper `Slice4Builders` was renamed to `ProfileCollectionMergeTestDoubles` with references updated. Verification: `dotnet csharpier format` ran on touched files, `rg -n "Slice" src/dms -g "*.cs" -g "!*Tests*"` returned no production matches, `rg -n "Slice4Builders" src/dms` returned no matches, `git diff --check` passed, and the backend unit test suite passed 1125 / 1125.

## Finding 11: Hidden descendant scope expansion may match the wrong nested collection parent

- Source: Agent 3, Finding 1
- Severity: medium
- Validation: valid
- Addressed: addressed

`src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionRowHiddenPathExpander.cs:117` only matches an inlined descendant scope to a collection row by the last ancestor's collection scope and semantic identity. For nested collections, that identity is only stable within its parent. If two different parents each have a child row with the same semantic identity, hidden descendant paths from one parent can be folded onto the other. That violates the story's ancestor-address matching requirement. The fix should key expansion by the full row address: parent ancestor chain plus the row semantic identity. Add a test with two parent collection rows, each containing a same-identity child row, and a hidden inlined descendant under only one parent.

Validation reasoning: Valid. `ProfileCollectionRowHiddenPathExpander.Expand` is invoked with rows already scoped to one `(childScope, parentAddress)` bucket, but it scans global `StoredScopeStates` and buckets hidden-path additions only by the descendant state's last ancestor collection scope plus semantic identity. For nested collections, two different parent rows can each contain a child row with the same semantic identity; a descendant state under parent B/child X will match parent A/child X because the expander never compares the full parent ancestor chain or full `CollectionRowAddress`. Existing expander tests cover different child identities, not same child identity under different parents.

Resolution validation: The expander now reconstructs each descendant state's owning row as a full `CollectionRowAddress` (collection scope, parent `ScopeInstanceAddress` with the descendant ancestor chain minus its last entry, and the last ancestor's `SemanticIdentityInOrder`) and buckets hidden-path additions in a `Dictionary<CollectionRowAddress, List<string>>(CollectionRowAddressComparer.Instance)`. Lookup uses each row's own `Address`, so descendants whose parent ancestor chain differs are excluded by structural address equality. New regression coverage in `ProfileCollectionRowHiddenPathExpanderTests` (`Given_Same_Child_Identity_Under_Different_Parents` and `Given_Same_Child_Identity_Under_Same_Parent`) exercises a `P1 -> Shared` row against a `P2 -> Shared` descendant and against a `P1 -> Shared` descendant; the existing top-level, deeper-inlined, presence-aware, and separate-table guards all continue to pass (9/9 tests).

## Finding 12: Slice fence switch and related Slice 2/root-only comment are stale

- Source: Agent 3, Finding 2
- Severity: low
- Validation: valid
- Addressed: not addressed

`src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs:274` has a slice fence switch where every current `RequiredSliceFamily` returns true, so `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs:286` and `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs:661` are dead for current code. This should be simplified now that Slice 5 is open. The following comment at `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs:292` is also stale Slice 2/root-only language.

Validation reasoning: Valid. The current `RequiredSliceFamily` enum has only four values, and the executor switch returns `true` for all four while the default case throws instead of producing `false`; the classifier only returns those enum values, so `if (!fencePassed)` and its only `BuildSliceFenceResult(...)` call are dead for current code. The nearby comments confirm Slice 5 opened collection and nested/extension collection handling, but the post-fence comment still says "Slice-2 classification passed" and describes the synthesizer as root-only.

## Finding 13: Semantic identity key generation should be centralized before DMS-1132

- Source: Agent 3, Finding 3
- Severity: low
- Validation: valid
- Addressed: addressed

Semantic identity key generation is duplicated in `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionPlanner.cs:473`, `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionWalker.cs:1006`, and `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionRowHiddenPathExpander.cs:186`. This is worth centralizing before DMS-1132 changes presence-sensitive identity behavior, otherwise the absence/null fix will need coordinated edits in multiple private helpers.

Validation reasoning: Valid. Semantic identity key generation is still duplicated across private helpers in the planner, walker, and hidden-path expander, and those helpers currently key only on serialized `Value`, collapsing missing identity parts and explicit JSON null into the same `"null"` key. That conflicts with `SemanticIdentityPart.IsPresent`, which preserves missing-vs-null semantics. The walker citation is slightly stale; the duplicated walker helpers are now around lines 1042 and 1055 rather than line 1006. No code path clearly centralizes this key generation or addresses the presence-sensitive behavior.

## Finding 14: Hidden inlined scope loss was not found by one reviewer

- Source: Agent 3, Note 1
- Validation: valid
- Addressed: addressed

One reviewer treated absent-vs-explicit-null semantic identity fidelity as out of scope for this review because the DMS-1132 design explicitly owns that hardening. They did not find evidence that whole hidden inlined scopes are lost: Core's `StoredSideExistenceLookupBuilder` emits all canonical member paths for hidden scopes.

Validation reasoning: Valid as a reviewer note. The DMS-1132 design explicitly owns absent-vs-explicit-null semantic identity fidelity, and no evidence was found that whole hidden inlined scopes are lost. Core emits `StoredScopeState.HiddenMemberPaths` for hidden non-collection scopes using all `CanonicalScopeRelativeMemberPaths`, including hidden/absent descendant scopes under collection items. Backend consumes those emitted paths: collection-row descendants are folded into row-level hidden paths before matched-row overlay, and separate/root-extension inlined descendants are collected into `ProfileSeparateScopeDescendantStates` so classification marks their bindings `HiddenPreserved`. Focused tests for these paths passed.

## Finding 15: Row hidden path expansion ignores the full ancestor chain

- Source: Agent 4, Finding 1
- Severity: high
- Validation: valid
- Addressed: addressed

`src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionRowHiddenPathExpander.cs:126` matches descendant hidden paths to rows using only the last ancestor's collection scope plus semantic identity. It ignores the rest of the ancestor chain for the current parent being walked. Two different parents can legally have child rows with the same semantic identity, so hidden paths from `P2 -> child A` can be folded onto `P1 -> child A`, causing wrong preservation or skipped visible updates. This violates the story's wrong-parent / ancestor-context requirement.

Validation reasoning: Valid. `ProfileCollectionRowHiddenPathExpander` builds descendant hidden-path additions using only the descendant state's last ancestor semantic identity, then applies those additions to rows using only each row's own semantic identity. It never compares the earlier ancestor chain or the current parent scope address. The caller does parent-bucket `VisibleStoredCollectionRows`, but it passes the full `ProfileAppliedContext.StoredScopeStates` into the expander for every parent walk, so a stored descendant state for `P2 -> child A` can be considered while expanding the `P1` bucket and will match `P1 -> child A` by child identity alone.

Resolution validation: Expansion now matches the full structural `CollectionRowAddress` — collection scope, parent `ScopeInstanceAddress` (including the parent's full ancestor collection chain), and the row's `SemanticIdentityInOrder` — instead of just the last ancestor's identity. The expander derives the row address from each descendant `StoredScopeState` by taking the descendant ancestor chain less its last entry (which pins the row inside the current collection) and using `ProfileCollectionWalker.ComputeParentJsonScope(collectionScope)` for the parent JSON scope, then keys additions in a `Dictionary<CollectionRowAddress, List<string>>` backed by `CollectionRowAddressComparer.Instance`. The same rule covers direct nested, deeper nested, extension-child, and inlined-parent cases because they all reduce to the same structural address comparison. Regression coverage was added in `ProfileCollectionRowHiddenPathExpanderTests` for same child identity under different parents (negative) and under the same parent (positive), in addition to the existing tests for top-level, deeper-inlined, presence-aware, and separate-table behavior. All 9 expander tests and the full 1124-test backend unit suite pass.

## Finding 16: Nested and extension collections are enabled unconditionally while traversal remains direct-child based

- Source: Agent 4, Finding 2
- Severity: high
- Validation: valid
- Addressed: addressed (same fix as Finding 1)

The executor now passes `NestedAndExtensionCollections` unconditionally at `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs:274`, but traversal is still string-direct-child based. `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteFlattener.cs:126` keys collection plans by immediate JSON parent, and `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileCollectionWalker.cs:1104` rejects child paths with another dotted segment. Collections under inlined non-collection parents, which the compiled contract can represent via `ImmediateParentJsonScope`, will be classified as supported but not flattened/walked correctly.

Validation reasoning: Valid. The compiled/profile side can model collection scopes whose immediate parent is an inlined non-collection scope, but backend traversal still only enters root, collection, root-extension, and aligned-extension table scopes. `RelationalWriteFlattener` groups collection plans by the immediate JSON parent (`scopeSegments[..^2]`) while recursive `MaterializeCollectionCandidates` is only called from table-backed traversal contexts, so a collection under `$.parentObject` or `$.parents[*].detail` is not reached. `ProfileCollectionWalker` has the same gap: it enumerates only collection/aligned-extension table plans and rejects child scopes with an extra dotted segment. Since the executor now allows `NestedAndExtensionCollections`, these shapes are no longer fenced but are still not flattened/walked correctly.

## Finding 17: Shared flattener semantic identity behavior changed outside the Slice 5 story

- Source: Agent 4, Finding 3
- Severity: medium
- Validation: valid
- Addressed: addressed

Slice-5 fix re-introduced `CollectionWriteCandidate.SemanticIdentityInOrder`, made the flattener probe per-binding presence from the source JSON node, and re-keyed flattener duplicate detection through the shared presence-aware helper, so a request collection with one missing-identity item and one explicit-null-identity item is no longer collapsed before reaching the merge layer. The previously suspected downstream no-profile merge gap is now addressed: no-profile matching uses the shared presence-aware semantic identity key. `ProfileCollectionWalker` stored-row handling remains aligned with the current DB-projected row model by treating persisted identity bindings as present, including persisted nulls.

Validation reasoning: Valid originally, but stale against the current workspace. The flattener restores request-side presence fidelity before duplicate detection, profile merge uses `CollectionWriteCandidate.SemanticIdentityInOrder`, and the no-profile merge now matches collection rows with `SemanticIdentityKeys.BuildKey(requestSemanticIdentity)` rather than raw `object?[]` semantic identity values. Current DB-projected rows are converted to `SemanticIdentityPart` with `IsPresent: true`, so an explicit JSON null request identity can match a persisted SQL NULL row while a missing identity part cannot. Regression coverage exists in `RelationalWriteNoProfileMergeSynthesizerTests.It_distinguishes_missing_from_explicit_null_when_matching_collection_current_rows`.

## Finding 18: Slice fence code is unreachable

- Source: Agent 4, Finding 4
- Severity: low
- Validation: valid
- Addressed: not addressed

The slice fence code is now dead: every current `RequiredSliceFamily` maps to true, making the `if (!fencePassed)` branch and `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs:661` unreachable. Either remove it or introduce an explicit future/unsupported family.

Validation reasoning: Valid. The current fence switch maps every declared `RequiredSliceFamily` value to `true`, and the classifier only returns those declared families. That means `if (!fencePassed)` cannot be reached for any current classifier output; an unexpected enum value would hit the switch default and throw instead of returning `false`. `BuildSliceFenceResult` is still present and only called from that unreachable branch.

## Finding 19: There is avoidable duplication and unrelated test churn

- Source: Agent 4, Finding 5
- Severity: low
- Validation: valid
- Addressed: not addressed

`src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/IntegrationFixtureGoldenTests.cs:13` has two nearly identical fixture classes. `RecordingLogger<T>` is duplicated in `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/TestSupport/DeleteTestHelpers.cs:65` while the original still exists in `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Common/RecordingLogger.cs:15`. The branch also removes delete-path FK diagnostic assertions, which looks unrelated to this story.

Validation reasoning: Valid for the avoidable duplication/test-churn concern. `IntegrationFixtureGoldenTests.cs` repeats the same fixture setup/assertion body across three classes even though `DdlGoldenFixtureTestBase` already provides the shared golden-fixture pattern used by nearby tests, and `DeleteTestHelpers.cs` defines a second `RecordingLogger<T>` while the original still exists in `Backend.Tests.Common`. The branch also removed `InternalsVisibleTo` for the unit test assembly from the common test project, which explains the local copy but is still churn around test support. The narrower subclaim that FK diagnostic assertions were removed was not supported; those assertions still exist in the delete tests, and the branch diff only changes logger namespace/imports.
