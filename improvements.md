# Recommended Improvements for Backend Profile Support

## Purpose

This document lists recommended documentation and story updates for backend profile support in the backend redesign work.

Scope:

- profile-aware relational writes
- profile-scoped collection merges
- hidden data preservation
- Core/backend contract clarity
- test/story alignment for PostgreSQL and SQL Server

Out of scope:

- batch API design documents under `reference/design/batch-draft`

## Highest Priority Actions

### 1. Define the concrete Core/backend profile write contract

Update the design docs so the profile write contract is explicit and normative, not just conceptual.

Recommended changes:

- Define a concrete request/context contract for profiled writes in `reference/design/backend-redesign/design-docs/profiles.md`.
- Align the code-shaped contract example in `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` to that richer contract.
- Tighten `reference/design/backend-redesign/epics/07-relational-write-path/01b-profile-write-context.md` so implementation cannot stop at filtered JSON plus implicit behavior.

The contract should explicitly include:

- `WritableRequestBody`
- a root-resource creatability decision for profiled creates
- per-scope visibility state with three distinct outcomes:
  - visible and present
  - visible and absent
  - hidden
- a stable addressing scheme for scope instances that backend can line up with compiled scopes/table plans
- collection visibility data sufficient to identify visible persisted rows by compiled semantic identity
- the metadata required to preserve hidden columns and hidden inlined values on matched rows/scopes

### 2. Formalize hidden-column preservation

The current design says hidden columns and hidden inlined values must be preserved, but it does not define the execution contract that makes this reliable.

Recommended changes:

- Add a normative rule for collection rows, non-collection scopes, and inlined parent/root-row columns.
- Choose one explicit model and document it:
  - Core supplies per-scope/per-member visibility metadata, or
  - backend overlays visible request values onto stored row values using compiled bindings and current-state data
- Define how backend distinguishes `hidden` from `visible-but-absent` for inlined common types and extension columns.
- Update `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md` to require tests for hidden inlined-column preservation, not just hidden row preservation.

### 3. Formalize the creatability algorithm

Creatability is currently assigned to Core, but the decision procedure is not defined precisely enough for future Core stories.

Recommended changes:

- Add a normative decision table or algorithm in `reference/design/backend-redesign/design-docs/profiles.md`.
- Cover these cases explicitly:
  - new resource instance
  - new 1:1 child scope
  - new collection item
  - new nested/common-type scope
  - new extension scope
  - new extension collection item
- Specify how hidden required members affect creatability.
- Distinguish update-of-existing-visible-data from create-of-new-visible-data.
- Include worked examples for accepted and rejected cases.

### 4. Clarify semantic identity derivation and validation

The docs require every persisted multi-item collection scope to compile a non-empty semantic identity, including cases where raw `arrayUniquenessConstraints` metadata may be empty, but the derivation rule is not stated clearly enough.

Recommended changes:

- State exactly how compiled collection identity is derived when raw `arrayUniquenessConstraints` metadata is empty.
- If the supported path relies on MetaEd-derived identity semantics, say so directly and identify the relevant source metadata or validator assumptions.
- If no non-empty semantic identity can be derived, require validation/compilation failure before runtime write execution.
- Make the support boundary for MetaEd-generated models explicit in the design docs, not only in compatibility findings.

## Important Clarifications

### 5. Fix wording in transactions and concurrency

The current language should make it unambiguous that backend uses a Core-projected stored body and does not evaluate profile predicates itself.

Recommended changes:

- Update `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` so it says backend derives visible stored rows from Core-projected stored state.
- Keep the prohibition on backend-owned profile evaluation explicit.

### 6. Expand deterministic hidden-gap ordering examples

The normative rule exists, but additional examples would reduce implementation drift and test ambiguity.

Recommended changes:

- Add examples for:
  - no previously visible row
  - deleting all visible rows while hidden rows remain
  - visible updates plus inserts with hidden rows interleaved
  - nested collection scopes
  - extension child collections
- Reuse the same ordering language in:
  - `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
  - `reference/design/backend-redesign/design-docs/summary.md`
  - story acceptance criteria where ordering is asserted

### 7. Add explicit extension child table key examples

The current rules imply the intended key shape, but an explicit example would remove ambiguity for downstream runtime work.

Recommended changes:

- Add one root-level extension child-collection example.
- Add one collection-aligned extension child-collection example.
- For each example, list the required keys explicitly:
  - child row `CollectionItemId`
  - root `..._DocumentId`
  - parent-scope key aligned to the immediate stable parent identity

### 8. Surface the E15 to E07 critical-path dependency in E07 docs

The dependency is already captured centrally, but it should also be visible where the runtime write work is introduced.

Recommended changes:

- Keep `reference/design/backend-redesign/epics/DEPENDENCIES.md` as the source of truth.
- Add a short note in `reference/design/backend-redesign/epics/07-relational-write-path/EPIC.md` and `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md` that stable collection merge-plan work is a prerequisite for runtime merge execution.

## Test and Story Enhancements

### 9. Keep and refine the existing profile test scope

The current story set already points in the right direction. The remaining work is to make the scenario set compact, explicit, and reusable.

Recommended baseline scenarios:

- no-profile write behavior
- full-surface collection reorder
- profiled visible-row update with hidden-row preservation
- profiled visible-row delete with hidden-row preservation
- profiled visible-but-absent non-collection scope behavior
- hidden inlined-column preservation
- profiled create rejection when the root is non-creatable
- profiled visible-scope/item insert rejection when non-creatable
- hidden `_ext` row preservation
- hidden extension child-collection preservation
- unchanged profiled writes with guarded no-op behavior

### 10. Add a shared scenario matrix across docs and stories

A small matrix would keep design docs, integration tests, and parity fixtures aligned.

Recommended changes:

- Add a compact feature-by-scenario matrix to one of the test-migration docs.
- Reuse the same scenario names in:
  - `reference/design/backend-redesign/epics/13-test-migration/01-backend-integration-tests.md`
  - `reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md`
  - relevant write-path stories

### 11. Align names and examples across the design set

The same contract and behavior should be described with the same names everywhere.

Recommended changes:

- Ensure `profiles.md`, `flattening-reconstitution.md`, `compiled-mapping-set.md`, and `summary.md` use the same terms for:
  - `WritableRequestBody`
  - `VisibleStoredBody`
  - scope visibility states
  - creatability
  - deterministic post-merge ordering
- Remove any code-shaped snippets that no longer reflect the intended contract.

## Suggested File Updates

Recommended primary update targets:

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`
- `reference/design/backend-redesign/design-docs/extensions.md`
- `reference/design/backend-redesign/design-docs/summary.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/01b-profile-write-context.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/EPIC.md`
- `reference/design/backend-redesign/epics/13-test-migration/01-backend-integration-tests.md`
- `reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md`

## Exit Criteria Before Starting E07 Runtime Merge Execution

Before the runtime write-path executor work starts, the design and story set should answer all of the following clearly:

- What exact request/context contract does backend receive from Core for profiled writes?
- How does the executor preserve hidden columns and hidden inlined data?
- How is creatability decided for every scope and item type?
- How is semantic collection identity compiled in all supported models?
- How are extension child collections keyed under collection-aligned scopes?
- What exact profile scenarios must pass on both PostgreSQL and SQL Server?
