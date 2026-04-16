# Review Checklist: Profile-Aware Shared Write Path

Use this checklist for stories that make profile metadata authoritative inside the shared relational POST/PUT executor and no-op path.

This is aimed at `DMS-1124`-class work:

- shared executor/no-op path changes instead of a profile-only fork,
- profile request/context contract changes,
- hidden-data preservation and visible-absent handling,
- profile-aware collection merge behavior,
- guarded no-op and concurrency behavior under writable profiles.

## Full Review Checklist

### Contract And Orchestration

- [ ] `WritableRequestBody` is selected once at the orchestration boundary and not recomputed inside merge or persist.
- [ ] Final create-vs-update semantics come from the executor's in-session target resolution, not from the incoming HTTP verb alone.
- [ ] Profile resolved-target execution runs after final target resolution and current-state load when required.
- [ ] Backend validates both `ProfileAppliedWriteRequest` and `ProfileAppliedWriteContext` against the compiled scope catalog.
- [ ] Validation covers unknown `JsonScope`, duplicate `JsonScope`, wrong scope kind, broken ancestor chain, and semantic identity count/path mismatches.
- [ ] Backend does not infer hidden-vs-absent from request or stored JSON alone when contract metadata exists.
- [ ] Every visible request collection row has a matching `VisibleRequestCollectionItem`.
- [ ] Every emitted `VisibleStoredCollectionRow` matches a real current DB row.
- [ ] Missing or inconsistent profile metadata fails deterministically as contract mismatch instead of late DB fallout.

### Merge Semantics

- [ ] Every non-storage-managed binding is classified as exactly one of visible/writable, hidden/preserved, clear-on-visible-absent, or storage-managed.
- [ ] Matched visible non-collection rows overlay request values onto current stored values instead of rebuilding from scratch.
- [ ] Hidden member preservation includes scalar columns, FK or descriptor bindings, key-unification canonical columns, and synthetic presence flags.
- [ ] Visible-absent separate-table scopes delete only when that is the designed behavior.
- [ ] Visible-absent inlined scopes clear only clearable visible bindings and preserve hidden-governed bindings.
- [ ] New visible root, scope, and collection item creation checks use `Creatable`; matched existing visible rows stay updatable even when create would be denied.
- [ ] Hidden scopes, hidden rows, hidden extension rows, and hidden extension child collections are preserved untouched.
- [ ] Collection matching uses compiled semantic identity plus stable parent address.
- [ ] Identity matching preserves absent-vs-present-null semantics and does not silently collapse them.
- [ ] Duplicate visible collection candidates fail deterministically before DML.
- [ ] Omitted visible rows delete only visible rows, never hidden rows.
- [ ] Second-pass logic exists for deletes that cannot be discovered from flattened request buffers alone.
- [ ] Ordinal recomputation preserves hidden-row gaps, replaces the visible subsequence in request order, appends visible inserts in the right place, and renumbers contiguously.
- [ ] No-profile and profile flows share the same merge model, or the no-profile path is only a synthetic adapter into that shared model.

### No-Op, Concurrency, And Persistence

- [ ] Guarded no-op uses the same merge synthesis output as real execution, not a compare-only side implementation.
- [ ] Comparable rowsets are produced from the same post-merge values that would actually be persisted.
- [ ] No-op success revalidates `ContentVersion` under lock before returning success.
- [ ] Stale no-op compare returns write conflict, not success.
- [ ] Per-row no-op filtering does not skip required ordinal or identity side effects.
- [ ] Delete and insert/update ordering still respects dependency order and unresolved collection item IDs.
- [ ] PostgreSQL and SQL Server both have correct batching and parameterization behavior for the changed path.
- [ ] Error mapping remains intentional: contract mismatch vs validation failure vs write conflict vs constraint violation.

### Test Matrix

- [ ] Unit tests cover contract validation failures, not only happy path.
- [ ] Unit tests cover binding classification for hidden, clearable, and storage-managed bindings.
- [ ] Unit tests cover matched-row overlay with hidden scalar, FK, descriptor, canonical, and synthetic-presence preservation.
- [ ] Unit tests cover visible-absent inlined scopes and separate-table delete behavior.
- [ ] Unit tests cover collection identity matching, duplicate detection, reverse coverage, and presence-sensitive null identity cases.
- [ ] Unit tests cover ordinal recomputation with hidden interleaving and no-previously-visible cases.
- [ ] Integration tests cover `PUT`, `POST` create-new, and `POST` resolving to existing-document.
- [ ] Integration tests cover nested collections, root extension child collections, and collection-aligned extension scopes.
- [ ] Integration tests cover update-allowed and create-denied pairs.
- [ ] Integration tests cover guarded no-op and stale no-op.
- [ ] PostgreSQL and SQL Server parity exists for behavior that could diverge by SQL shape.

### Reviewer Heuristics

- [ ] Any branch that decides create or delete from buffer presence alone deserves scrutiny.
- [ ] Any branch that uses raw request JSON or visible stored JSON instead of emitted scope or row metadata deserves scrutiny.
- [ ] Any branch that bypasses current stored values during matched updates is suspect.
- [ ] Any optimization that avoids merge synthesis for no-op comparison is suspect.
- [ ] Any new identity key builder explicitly accounts for presence semantics.
- [ ] Any new dialect-specific SQL path immediately triggers a parity test ask.

## Short PR Checklist

Use this shorter pass when reviewing a PR without doing a full design replay:

- [ ] The PR keeps one shared executor/no-op path for profile and no-profile writes.
- [ ] Final create-vs-update behavior is derived after in-session target resolution.
- [ ] The PR trusts and validates Core-emitted scope and row metadata instead of re-deriving profile semantics in backend.
- [ ] Matched updates overlay onto current stored values and preserve hidden data.
- [ ] Visible-absent behavior is correct for both separate-table and inlined scopes.
- [ ] Collection matching and delete behavior are driven by compiled semantic identity plus stable parent address.
- [ ] Presence-sensitive identity cases do not collapse absent and explicit null into the same behavior unless an upstream invariant is documented and enforced.
- [ ] Guarded no-op reuses real merge synthesis and rechecks `ContentVersion`.
- [ ] The PR adds or updates both unit coverage and integration coverage for the changed invariant.
- [ ] PostgreSQL and SQL Server parity is addressed for any path that changed SQL shape or batching.

## Primary Code Surfaces

- `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileWritePipeline.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWritePipelineMiddleware.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileWriteContractValidator.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteBindingClassifier.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteMerge.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWritePersister.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteGuardedNoOp.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/NoProfileSyntheticProfileAdapter.cs`
