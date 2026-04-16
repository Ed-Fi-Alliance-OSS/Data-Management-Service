---
jira: DMS-1124
jira_url: https://edfi.atlassian.net/browse/DMS-1124
---

# Story: Apply Profile-Aware Merge, Hidden-Data Preservation, and Creatability in the Persist Executor

## Planning Split

This planning branch recuts `DMS-1124` into seven serial design slices so the team can review and land the work in smaller PRs without reopening the entire profiled write path on every pass.

The goals of the recut are:

- keep one shared executor path introduced by `DMS-984`,
- allow narrow deterministic fences for unsupported profiled shapes until their slice lands,
- separate routing/contract concerns from merge semantics,
- separate non-collection behavior from collection behavior, and
- separate guarded no-op and parity hardening from base merge support.

This document becomes the umbrella index for the slice docs under:

- `reference/design/backend-redesign/epics/07-relational-write-path/03b-profile-aware-persist-executor/`

## Shared Constraints

All slices inherit these constraints:

- `DMS-1124` extends the shared `DMS-984` executor/no-op path; it does not introduce a separate profile-only runtime pipeline.
- `WritableRequestBody` selection remains an orchestration-boundary concern from `DMS-1123`.
- Final create-vs-update behavior comes from in-session target resolution.
- Core-owned request/context metadata remains authoritative; backend does not re-evaluate writable-profile rules.
- Unsupported profiled shapes may remain fenced until their owning slice lands, but the fence must be deterministic and narrow.

## Slice Order

1. [01-executor-routing-and-slice-fencing.md](03b-profile-aware-persist-executor/01-executor-routing-and-slice-fencing.md)
   - Goal: route profiled writes through the shared executor correctly and replace the broad story fence with narrow slice fences
   - After merge: correct routing, target outcome, contract validation, and deterministic unsupported-shape fences
   - Still fenced: all successful profiled merge/persist behavior except root-creatability rejection
2. [02-root-table-only-profile-merge.md](03b-profile-aware-persist-executor/02-root-table-only-profile-merge.md)
   - Goal: support profiled writes whose persisted behavior is confined to the root table
   - After merge: root-row overlay, hidden root preservation, inlined clear/preserve behavior, root-table key-unification and synthetic presence behavior
   - Still fenced: separate-table non-collection scopes, all collections, descendant extension collections, guarded no-op
3. [03-separate-table-profile-merge.md](03b-profile-aware-persist-executor/03-separate-table-profile-merge.md)
   - Goal: support separate-table non-collection scopes and root-level separate-table extension rows
   - After merge: visible-present insert/update, visible-absent delete, hidden separate-table preservation, update-allowed/create-denied behavior for matched vs new scopes
   - Still fenced: all collections and descendant collection-aligned behavior, guarded no-op
4. [04-top-level-collection-merge.md](03b-profile-aware-persist-executor/04-top-level-collection-merge.md)
   - Goal: support top-level collection merge only
   - After merge: visible-row update/delete/insert semantics with hidden-row preservation and top-level ordinal behavior
   - Still fenced: nested collections, root-level extension child collections, collection-aligned extension child collections, guarded no-op
5. [05-nested-and-extension-collection-merge.md](03b-profile-aware-persist-executor/05-nested-and-extension-collection-merge.md)
   - Goal: add nested collections plus root-level and collection-aligned extension child collections
   - After merge: full descendant-aware collection behavior for supported shapes
   - Still fenced: guarded no-op if not yet landed
6. [06-profile-guarded-no-op.md](03b-profile-aware-persist-executor/06-profile-guarded-no-op.md)
   - Goal: enable profiled guarded no-op using the same merge synthesis as execution
   - After merge: unchanged profiled `PUT` and `POST`-as-update may short-circuit safely
   - Still fenced: only intentionally deferred hardening cases
7. [07-parity-and-hardening.md](03b-profile-aware-persist-executor/07-parity-and-hardening.md)
   - Goal: close parity gaps and extract remaining explicit follow-ups
   - After merge: pgsql/mssql parity closure for supported scenarios and explicit hardening handoff

## Scenario Ownership Map

- `ProfileRootCreateRejectedWhenNonCreatable` — Slice 1
- `ProfileHiddenInlinedColumnPreservation` — Slice 2
- Inlined `ProfileVisibleButAbsentNonCollectionScope` — Slice 2
- Separate-table `ProfileVisibleButAbsentNonCollectionScope` — Slice 3
- `ProfileHiddenExtensionRowPreservation` — Slice 3
- Non-collection update-allowed/create-denied pair — Slice 3
- Top-level `ProfileVisibleRowUpdateWithHiddenRowPreservation` — Slice 4
- Top-level `ProfileVisibleRowDeleteWithHiddenRowPreservation` — Slice 4
- Nested / root-level extension child / collection-aligned extension child variants of `ProfileVisibleRowUpdateWithHiddenRowPreservation` — Slice 5
- `ProfileHiddenExtensionChildCollectionPreservation` — Slice 5
- `ProfileUnchangedWriteGuardedNoOp` — Slice 6
- pgsql/mssql parity closure and explicit `DMS-1132` handoff — Slice 7

## Notes For Review

- Each slice doc defines:
  - what becomes supported after that slice,
  - what remains fenced,
  - what tests are required, and
  - what the next slice inherits.
- The intent is that each PR is independently mergeable because it leaves the runtime in a coherent state.
- `DMS-1132` remains the named follow-on for presence-sensitive semantic identity fidelity unless that work is explicitly pulled into a later `DMS-1124` slice.
