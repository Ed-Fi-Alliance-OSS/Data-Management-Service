# DMS-1124 Slice 2 — Follow-Up Work

This file captures cleanup items surfaced while closing Slice 2 (root-table-only profile merge) that were consciously **not** done in the slice itself. None are merge blockers for Slice 2; each is worth addressing when adjacent work naturally touches the area. Delete entries from this file as they land.

Sibling to `EPIC.md` and the existing `dms-1124-review-summary.md`. Lives in git (not under `docs/superpowers/`) so reviewers and future implementers can find it.

## Context

Slice 2 commits on branch `DMS-1124-2`:

```
1272b51c  [DMS-1124] Slice 2 task 7 supplement: Synthetic fixture for deferred acceptance scenarios
ef80fe2c  [DMS-1124] Slice 2 task 7: Integration tests for root-table-only profile merge
23613816  [DMS-1124] Slice 2 task 6: Wire profile merge path in executor with shape gate
12065eaa  [DMS-1124] Slice 2 task 5: Compose profile merge synthesizer with resolver context slim
3af414d9  [DMS-1124] Slice 2 task 4: Add post-overlay key-unification resolver
6e0fbf67  [DMS-1124] Slice 2 task 3: Add governance rules and root-table binding classifier
33d011cc  [DMS-1124] Slice 2 task 2: Extract flattener helpers for resolver reuse
2b1c8bb4  [DMS-1124] Slice 2 task 1: Generalize merge result contract
```

All six Slice 2 acceptance scenarios are proven end-to-end on both dialects (PostgreSQL + SQL Server). Unit tests 552 / DDL goldens 749 / pgsql integration 263 / mssql integration 178, all green.

## Items

### 1. Extract `Profile/ProfileScopeMatching.cs` once a third caller exists

**What.** `BuildCandidateScopeSet`, `TryMatchLongestScope`, and `StripScopePrefix` are duplicated verbatim between `ProfileRootTableBindingClassifier.cs` and `ProfileRootKeyUnificationResolver.cs`.

**Why not now.** Two identical copies aren't a maintenance hazard yet — the surface is small, the semantics are simple, and both sites have strong test coverage that would catch divergence. Extracting a shared helper before a third caller exists risks premature generalization.

**When.** Extract when Slice 3 (separate-table profile merge) adds a third classifier/resolver that needs the same longest-prefix scope resolution.

### 2. Slim `ProfileRootKeyUnificationContext` to drop `WritePlan`

**What.** `ProfileRootKeyUnificationContext` carries `WritePlan` because the resolver needs it only to call `FlatteningResolvedReferenceLookupSet.Create(writePlan, resolvedReferences)` internally. If the synthesizer pre-built the lookup set, the context would not need `WritePlan`.

**Why not now.** Task 5's synthesizer already builds the lookup set once per call (see `RelationalWriteProfileMerge.cs` around line 198), but keeps `WritePlan` on the context so the resolver's unit tests can construct their own lookup sets without writing a full plumbing path through the synthesizer. Moving the lookup-set construction entirely to the synthesizer would churn the Task 4 resolver tests without a correctness gain.

**When.** When a future refactor naturally restructures the synthesizer/resolver seam — e.g., Slice 3's resolver equivalent — take the opportunity to remove the residual `WritePlan` field from the context.

### 3. Consolidate `ConfigurableStoredStateProjectionInvoker` and `RootOnlyStoredStateProjectionInvoker` to `Backend.Tests.Common`

**What.** Two stored-state-projection-invoker test doubles exist, duplicated across the pgsql and mssql integration test projects:

- `RootOnlyStoredStateProjectionInvoker` (Slice 1) — empty `ProfileAppliedWriteContext` for routing tests.
- `ConfigurableStoredStateProjectionInvoker` (Slice 2) — per-scope visibility + hidden paths for the acceptance scenarios.

Both are tiny (~50 lines each) and mirror the same duplication pattern Slice 1 already established.

**Why not now.** Consolidating into `Backend.Tests.Common` is mechanical but cross-cutting across two test projects. The current duplication is intentionally aligned with Slice 1's convention; changing it during Slice 2 would broaden the diff beyond the slice's focus.

**When.** Before Slice 3 lands its own stored-state-projection scenarios (which will almost certainly duplicate again into the same two projects). Take it as one small cleanup commit ahead of Slice 3.

## PR prep (not a code item)

Before opening the Slice 2 PR, delete the local-only working artifacts under `docs/superpowers/` per the standing rule:

- `docs/superpowers/specs/2026-04-17-slice-2-root-table-only-profile-merge-design.md`
- `docs/superpowers/plans/2026-04-17-dms-1124-slice2-root-table-only-profile-merge.md`

They should already show as untracked in `git status` (never committed). Deleting the files leaves the branch ready for the PR.
