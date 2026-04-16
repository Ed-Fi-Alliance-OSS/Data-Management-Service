---
jira: DMS-1124
---

# Slice 1: Executor Routing And Slice Fencing

## Purpose

Introduce the executor-side orchestration required for profiled relational writes without yet claiming full profile-aware merge support.

This slice does four things:

- routes profiled writes through the shared `DMS-984` executor path,
- resolves final create-vs-update outcome in-session before applying profile-specific decisions,
- validates Core-emitted profile request/context metadata against compiled backend scope metadata, and
- replaces the one-big-story fence with a deterministic slice fence that can be removed one scope family at a time by later slices.

The goal is to make later PRs small and mergeable while preserving current safety for unsupported profiled shapes.

## In Scope

- Consumption of `ProfileAppliedWriteRequest` and `ProfileAppliedWriteContext` in the shared executor path
- Final target resolution for `POST` create-new vs `POST`-as-update before profile decisions are applied
- Root creatability enforcement only when final target outcome is create-new
- Contract validation for request-side and stored-side profile metadata
- Deterministic slice-fence behavior for unsupported profiled persist shapes
- No-profile behavior must remain unchanged

## Explicitly Out Of Scope

- Successful profiled merge/persist behavior for any persisted shape beyond root-creatability rejection
- Hidden-data preservation
- Inlined-scope clear vs preserve semantics
- Separate-table profile semantics
- Collection merge semantics
- Profile guarded no-op behavior

## Supported After This Slice

- No-profile writes continue to use the existing shared executor path unchanged
- Profiled `POST` create-new requests can be rejected correctly when root creatability is false
- Profiled `POST`-as-update and `PUT` requests reach contract validation and final target classification correctly
- Unsupported profiled writes fail deterministically with a slice fence instead of the broad temporary story fence

## Still Fenced After This Slice

All profiled relational writes that would require merge or persist behavior beyond root-creatability rejection remain fenced, including:

- root-row hidden preservation,
- inlined-scope clear/preserve semantics,
- separate-table non-collection scope handling,
- collection/common-type/extension collection handling, and
- profiled guarded no-op.

## Design Constraints

- The backend must not re-evaluate profile rules or writable member visibility.
- `WritableRequestBody` selection remains an orchestration-boundary concern.
- Final create-vs-update outcome must come from the executor's in-session target resolution, not from the incoming HTTP method alone.
- Contract validation must happen before merge logic consumes profile metadata.
- Slice fences must be deterministic and narrow enough that later slices can remove them independently.
- Unsupported profiled shapes must fail before DML.
- The shared `DMS-984` executor path remains the only runtime path; later slices remove fences rather than introducing a separate profile-only executor.

## Runtime Decision Matrix

### No profile applies

- Behavior remains unchanged from `DMS-984`.
- No slice fence is involved.

### Writable profile applies and final target outcome is create-new

- Execute resolved-target profile step after final target outcome is known.
- If `RootResourceCreatable` is false, reject before any insert DML.
- If `RootResourceCreatable` is true, continue to slice-shape evaluation.
- If the profiled shape is not supported by the currently landed slices, return a deterministic slice fence.

### Writable profile applies and final target outcome is existing-document

- Load current state and reconstituted document as required by the resolved-target profile handoff.
- Validate `ProfileAppliedWriteRequest` and `ProfileAppliedWriteContext` against compiled scope metadata.
- If validation fails, return deterministic contract-mismatch failure.
- If validation succeeds but the profiled shape is not supported by the currently landed slices, return a deterministic slice fence.

## Slice Fence Contract

This slice replaces the broad story-level fence with a narrower runtime fence.

### Shape classification

The executor must classify each profiled write into one of:

- supported by landed slices,
- valid profile contract but not yet supported by landed slices, or
- invalid profile contract.

### Fence behavior

For "valid contract but not yet supported" shapes:

- fail before merge synthesis or DML,
- use a deterministic failure message that names the unsupported slice family,
- preserve existing safety by not attempting partial profiled DML.

### Contract mismatch behavior

For invalid profile contract:

- fail deterministically as contract mismatch,
- do not collapse into the slice fence,
- do not proceed to merge/persist.

## Acceptance Criteria

- Profiled writes enter the shared executor path rather than failing at the old global placeholder fence.
- `POST` create-new vs `POST`-as-update is determined by final in-session target resolution.
- Root creatability is enforced only when final target outcome is create-new.
- Request-side and stored-side profile metadata are validated against the compiled scope catalog before merge logic consumes them.
- Unknown `JsonScope`, broken ancestor chain, wrong scope kind, and semantic-identity ordering mismatches fail deterministically.
- Unsupported profiled shapes fail with deterministic slice-fence messages before DML.
- No-profile behavior remains unchanged.

## Tests Required

### Unit tests

- Final target resolution differentiates:
  - `POST` create-new,
  - `POST` resolving to existing-document, and
  - `PUT` existing-document.
- Root creatability rejection occurs only for final create-new outcome.
- Contract mismatch tests cover:
  - unknown `JsonScope`,
  - ancestor-chain mismatch,
  - wrong scope kind,
  - semantic-identity mismatch.
- Slice-fence tests cover:
  - valid profiled request with unsupported persisted shape,
  - supported no-profile request not using the fence,
  - contract mismatch not being downgraded to slice fence.

### Integration tests

- Profiled `POST` create rejected when root is non-creatable
- Profiled `POST`-as-update reaches executor routing and returns slice fence rather than old placeholder fence
- Profiled `PUT` reaches executor routing and returns slice fence rather than old placeholder fence

## Reviewer Focus

Reviewers for this slice should focus only on:

- final target outcome handling,
- request/context contract handoff,
- contract validation,
- root creatability timing, and
- slice-fence classification.

Reviewers should explicitly ignore:

- overlay correctness,
- binding classification,
- collection merge behavior,
- ordinal behavior, and
- guarded no-op.

## Leaves Behind For Next Slice

The next slice removes the fence only for root-table-only profiled writes where all persisted behavior is confined to the root row and root-hosted inlined scopes.

That slice owns:

- root-row overlay,
- hidden root-column preservation,
- visible-absent inlined-scope clearing,
- hidden FK/descriptor preservation, and
- key-unification / synthetic presence preservation on the root row.
