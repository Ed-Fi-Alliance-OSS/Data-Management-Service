---
jira: DMS-1132
jira_url: https://edfi.atlassian.net/browse/DMS-1132
---

# Story: Presence-Sensitive Semantic Identity Fidelity in the Relational Write Merge

## Description

The profile-aware relational merge compares semantic identity parts against current database rows via `CompareSemanticIdentityValue` (in `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteMerge.cs`). The `SemanticIdentityPart` contract (see `src/dms/core/EdFi.DataManagementService.Core.External/Profile/AddressTypes.cs:79` and `reference/design/backend-redesign/design-docs/profiles.md` §"Scope and Row Address Derivation" line 410) explicitly preserves `IsPresent` to distinguish a missing identity member from an explicit-null identity member. `AddressDerivationEngine.ReadSemanticIdentity` (`src/dms/core/EdFi.DataManagementService.Core/Profile/AddressDerivationEngine.cs:213`) propagates that distinction through address derivation. Once semantic identity values land in the database, however, both "absent" and "explicit JSON null" collapse to SQL `NULL`, so the comparator cannot distinguish the two cases on the current-row side.

The comparator is consumed from two call sites, and each site depends on a different upstream invariant:

- **Site A — request-to-DB collection row matching (`TryMatchCurrentRowToVisibleStored`).** This site pairs Core-projected `VisibleStoredCollectionRow` entries (stored-side-derived) with current DB rows. A stored row carrying `(IsPresent=true, Value=null)` at an identity position whose DB value is also `NULL` becomes indistinguishable from another stored row carrying `(IsPresent=false, Value=null)`. The current invariant gating this is `DocumentReconstituter.EmitScalars` (`src/dms/backend/EdFi.DataManagementService.Backend.Plans/DocumentReconstituter.cs:159`), which omits null scalar columns during stored-document reconstitution, so `VisibleStoredCollectionRow` addresses derived from the reconstituted body do not reach the merge as `(IsPresent=true, Value=null)`. Request-side null pruning (`DocumentValidator.PruneNullData` at `src/dms/core/EdFi.DataManagementService.Core/Validation/DocumentValidator.cs:72`) is an adjacent invariant — it keeps the request-side candidate identity shape consistent with the stored-side key built by `BuildSemanticIdentityKeyFromRow`, but the direct current-row comparison at this site is gated by reconstitution.

- **Site B — stored-side ancestor/scope-instance resolution (`TryMatchScopeInstanceRow` → `MatchesAncestorCollectionInstance`, consumed by `ApplyStoredScopeStatesSecondPass`).** This site is stored-side only. `StoredScopeState` entries and their `AncestorCollectionInstance` chains come from stored-side address derivation over the reconstituted stored JSON. The only invariant keeping `(IsPresent=true, Value=null)` out of this path is `DocumentReconstituter.EmitScalars` (same file/line as above). Request null pruning does not apply here: no request-derived input flows into these ancestor identities.

Both sites are still fragile in isolation. If stored reconstitution ever emits explicit JSON null for an optional scalar — a future change, a different reconstitution path, or an externally supplied `ProfileAppliedWriteContext` that carries `SemanticIdentityPart(IsPresent=true, Value=null)` directly — two stored rows that differ only at that identity position would both materialize as SQL `NULL` in the database. `CompareSemanticIdentityValue` would bind the first stored row to whichever current row iterated first; the second current row would be treated as hidden and the second stored row would remain unmatched. At Site B, the analogous ambiguity could preserve or delete the wrong row under a presence-sensitive collection ancestor during second-pass scope-state handling.

This story closes the gap by either giving the merge enough DB-side fidelity to distinguish the cases, or by detecting the ambiguity deterministically before DML runs.

This is a correctness-hardening follow-on to `DMS-1124`. It is not a live bug given the stored-reconstitution invariant above; it becomes one as soon as that invariant is loosened, and the failure mode (misbound update/delete or silently mis-preserved ancestor state) is silent data corruption rather than a detectable error.

## Design Decision

Two directions satisfy this story; the implementation must pick one and justify the choice during the spike. Neither should be layered on top of the other.

1. **DB-side presence fidelity.** Extend the generated relational model so that nullable identity members participate in a presence indicator the merge can consult. Candidate mechanisms:
   - Reuse the existing synthetic presence flag columns produced by key unification (`reference/design/backend-redesign/design-docs/key-unification.md` §"Presence flag columns") for identity members where key unification already applies, and extend the mechanism to collection identity members where it does not.
   - Thread the presence flag into `MergeCurrentStateProjection` so the per-row `SemanticIdentityPart` the comparator sees on the current-row side carries an accurate `IsPresent`.
   - Require matching DDL/migration for in-place upgrades; gated on `DMS-1042` (key unification) staying the source of truth for presence semantics.

2. **Deterministic pre-merge ambiguity detection.** Keep the DB-side representation unchanged and add a validator that fails fast when the `ProfileAppliedWriteContext` would feed the comparator with multiple `VisibleStoredCollectionRow`s (or multiple `AncestorCollectionInstance`s) whose semantic identity tuples differ only by `IsPresent` at a position whose stored values all materialize as SQL `NULL`. The merge surfaces a category-5 contract mismatch (`CoreBackendContractMismatchFailure`) rather than silently misbinding.

The spike output must document why the chosen direction fits the existing design-doc invariants, and must not silently re-introduce the "first-wins" behavior that `DMS-1124` already rejected for duplicate `StoredScopeState` / `VisibleStoredCollectionRow` metadata.

## Acceptance Criteria

- Site A — `TryMatchCurrentRowToVisibleStored` never silently misbinds a visible stored collection row to the wrong current row when two stored rows differ only by `(IsPresent=true, Value=null)` vs `(IsPresent=false, Value=null)` at one semantic-identity position whose DB value is `NULL` on both current rows.
- Site B — `TryMatchScopeInstanceRow` / `MatchesAncestorCollectionInstance` never silently preserves or deletes the wrong row during `ApplyStoredScopeStatesSecondPass` under a presence-sensitive collection ancestor in the same ambiguous case.
- Behavior is identical across PostgreSQL and SQL Server; any DDL change supporting the fix applies to both dialects.
- Both sites are covered by explicit unit tests that construct `SemanticIdentityPart(IsPresent=true, Value=null)` inputs directly. Tests do not rely on the stored-reconstitution invariant (`DocumentReconstituter.EmitScalars`) or on request null pruning (`DocumentValidator.PruneNullData`) to mask the ambiguity — the comparator's guarantee must hold even if those invariants shift.
- Integration tests exercise the full profiled pipeline end-to-end for a collection whose semantic identity includes an optional member, for both Site A (current-row matching) and Site B (stored-side ancestor resolution). Fixtures must construct DB state that exercises the post-reconstitution decision path directly rather than relying on Core to prune the null for them.
- The remarks block on `CompareSemanticIdentityValue` added under `DMS-1124` is updated or removed — it currently documents the limitation as an upstream-invariant assumption (distinguishing request-side vs stored-side gating) and must no longer be authoritative once this story lands.
- If the chosen direction is "DB-side presence fidelity," the change integrates with the existing key-unification presence flag mechanism (`DMS-1042`) rather than introducing a parallel presence representation, and Site B is covered end-to-end since its presence signal must survive the full second-pass resolution path.
- If the chosen direction is "deterministic pre-merge ambiguity detection," the resulting failure is a typed `CoreBackendContractMismatchFailure` (category 5), not a raw exception, and does not downgrade to silent omission handling at either site.

## Tasks

1. Spike: compare the two design directions against the profiles.md / key-unification.md invariants, document the choice with specific file/line impact analysis for both Site A (`TryMatchCurrentRowToVisibleStored`) and Site B (`TryMatchScopeInstanceRow` → `MatchesAncestorCollectionInstance`), and confirm the choice does not weaken any invariant already enforced by `DMS-1124` (for example, duplicate-metadata rejection and scope-kind enforcement in `ProfileWriteContractValidator`).
2. Implement the chosen direction for both sites in `RelationalWriteMerge` (plus DDL/key-unification glue or validator module as required), keeping the profiled and no-profile paths aligned under the shared `DMS-984` executor path.
3. Cover Site A's comparator path and Site B's second-pass scope-state resolution path with unit tests that construct `SemanticIdentityPart(IsPresent=true, Value=null)` inputs directly, bypassing both the stored-reconstitution invariant and request null pruning.
4. Add pgsql + mssql integration tests for both sites, round-tripping a presence-sensitive collection identity through POST/PUT for Site A and exercising a presence-sensitive collection ancestor under second-pass scope-state handling for Site B.
5. Update or remove the existing `CompareSemanticIdentityValue` remarks block in `RelationalWriteMerge.cs` so it reflects the post-fix guarantee rather than citing stored reconstitution as the sole safety net; also revisit the per-call-site language (request-side vs stored-side invariants) added under `DMS-1124` so the doc stops distinguishing between live invariants and now simply asserts the post-fix guarantee.
6. Update `reference/design/backend-redesign/design-docs/profiles.md` §"Scope and Row Address Derivation" and, if the chosen direction extends presence flags, `reference/design/backend-redesign/design-docs/key-unification.md` §"Presence flag columns," so the design docs describe the post-fix behavior rather than the current fragility.
