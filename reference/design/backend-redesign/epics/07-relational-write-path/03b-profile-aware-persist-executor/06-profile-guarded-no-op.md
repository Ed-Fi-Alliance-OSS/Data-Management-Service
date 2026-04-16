---
jira: DMS-1124
---

# Slice 6: Profile Guarded No-Op

## Purpose

Enable guarded no-op behavior for all profiled shapes supported by the earlier slices.

This slice is intentionally isolated because it cuts across every earlier merge shape but should not require re-review of the merge semantics themselves. The question here is narrower: when the merge says "no effective change," can the executor safely return success without DML?

## In Scope

- Comparable rowset synthesis for supported profiled shapes
- Reuse of the same merge synthesis output for execution and no-op comparison
- Guarded no-op for profiled `PUT`
- Guarded no-op for profiled `POST`-as-update
- `ContentVersion` freshness recheck before no-op success
- Stale compare handoff to the outer write-conflict behavior

## Explicitly Out Of Scope

- New merge semantics for additional shapes
- Hardening that changes semantic identity fidelity beyond current documented assumptions

## Supported After This Slice

- Unchanged profiled `PUT` and profiled `POST`-as-update may short-circuit as guarded no-op for any shape already supported by earlier slices.
- Guarded no-op compares the same effective merged rowset that execution would persist.
- Stale compares return write conflict instead of stale success.

## Still Fenced After This Slice

No functional profiled behavior should remain intentionally fenced except explicitly deferred hardening stories.

## Design Constraints

- There must not be a profile-only compare implementation separate from execution merge synthesis.
- Comparable rowsets must be produced from the same post-merge values execution would use.
- No-op success is provisional until freshness is revalidated under the executor's concurrency rules.
- A stale compare must not return success.

## Runtime Decision Matrix

### Supported profiled shape, merge result is a no-op candidate, current version still current

- Read the comparable merged rowset from the same merge synthesis used for execution.
- Revalidate observed `ContentVersion`.
- If still current, return guarded no-op success without DML.

### Supported profiled shape, merge result is a no-op candidate, current version stale

- Revalidate observed `ContentVersion`.
- If stale, return write conflict / stale-compare outcome.
- Do not return success.

### Supported profiled shape, merge result is not a no-op candidate

- Continue to real persist behavior unchanged.

## Acceptance Criteria

- Guarded no-op for profiled writes reuses the same merge-ordering and rowset-synthesis logic as execution.
- Unchanged profiled `PUT` and profiled `POST`-as-update can short-circuit successfully when current.
- Stale compares return write conflict instead of success.
- No DML-visible state or update-tracking-visible state changes occur on guarded no-op success.

## Tests Required

### Unit tests

- Comparable profiled rowset is sourced from the same merge synthesis result used for execution
- No-op candidate detection for supported profiled shapes
- Stale compare result mapping
- Distinct handling for `PUT` vs `POST`-as-update result types

### Integration tests

- `ProfileUnchangedWriteGuardedNoOp` for root-table-only supported shape
- `ProfileUnchangedWriteGuardedNoOp` for separate-table supported shape
- `ProfileUnchangedWriteGuardedNoOp` for collection-supported shape
- Stale compare conflict case for profiled `PUT`
- Stale compare conflict case for profiled `POST`-as-update

## Reviewer Focus

Reviewers for this slice should focus only on:

- reuse of merge synthesis for no-op comparison,
- comparable rowset correctness,
- freshness recheck behavior, and
- stale compare result mapping.

Reviewers should explicitly ignore:

- underlying merge correctness unless it directly changes no-op behavior.

## Leaves Behind For Next Slice

The final slice closes parity gaps and extracts remaining explicit hardening follow-ups.
