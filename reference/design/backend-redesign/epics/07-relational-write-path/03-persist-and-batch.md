---
jira: DMS-984
jira_url: https://edfi.atlassian.net/browse/DMS-984
---

# Story: Persist Row Buffers with Stable-Identity Merge Semantics (Batching, Limits, Transactions)

## Description

Persist flattened row buffers to the database in a single transaction, establishing the shared executor foundation used by both full-surface writes and the profiled follow-on:

Dependency note: `reference/design/backend-redesign/epics/DEPENDENCIES.md` is the canonical dependency map, and this story is on the `E15-S04b` / `DMS-1108` critical path. Runtime merge execution here consumes the retrofitted stable-identity collection merge-plan contract from `reference/design/backend-redesign/epics/15-plan-compilation/04b-stable-collection-merge-plans.md`; it must not be implemented against the older delete-by-parent / `Ordinal`-based collection plan shape.

This story also absorbs the request-scoped relational command-boundary follow-on left intentionally out of `DMS-983`: persistence orchestration introduces the executor-owned relational connection/transaction boundary required for `DMS-984`, so the runtime path no longer depends on the DMS-983 resolver seam that opens an independent connection per command. `DMS-984` consumes the `DMS-983` terminal-stage handoff that already carries target context, selected body, resolved references, the root row, root-extension rows, collection candidates, and candidate-attached aligned-scope data.

The profile-aware merge, hidden-data preservation, and creatability work that previously lived here is split to follow-on story `DMS-1124` / `reference/design/backend-redesign/epics/07-relational-write-path/03b-profile-aware-persist-executor.md`. This story keeps only the executor mechanics and full-surface/no-profile runtime behavior that other backend stories need on the critical path before the profiled runtime branch is ready.

- For `PUT`, and for `POST` when upsert resolves to an existing document, compare the current persisted rowset to the post-merge rowset the executor would actually write and skip DML when they are identical.
- Guarded no-op comparison must reuse the same merge-ordering and post-merge rowset-synthesis logic as the real executor, either directly or through a shared helper built from the same executor-facing merge metadata.
- Insert/update `dms.Document`, resource root rows, and separate-table 1:1/common-type/extension rows when a change exists for full-surface/no-profile writes.
- For non-collection scopes (root-adjacent, nested/common-type, and extension scopes) on the no-profile path, use standard full-surface present/absent semantics:
  - insert newly present separate-table rows,
  - update matched separate-table rows and inlined parent/root-row bindings,
  - delete separate-table rows omitted from the request, and
  - clear compiled bindings for omitted inlined scopes rather than preserving hidden data.
- For collection/common-type/extension collection tables on the no-profile path, use stable-identity merge semantics:
  - match stored rows to request candidates by compiled semantic identity,
  - update matched rows in place,
  - delete omitted rows,
  - reserve new `CollectionItemId` values only for unmatched inserts, and
  - consume the non-empty compiled semantic identity guaranteed by `E15-S04b` / `DMS-1108`, where runtime/write-plan compilation already opted into the strict relational-model pipeline from `DMS-1103`; runtime does not derive a fallback match key when that upstream prerequisite is missing.
- Recompute `Ordinal` using the deterministic post-merge sibling-order rule defined in the design docs.
- Respect dialect parameter limits and implement batching to avoid N+1 patterns.
- Guard the no-op fast path by revalidating the observed `ContentVersion` before returning success; public `If-Match` header semantics remain owned by `DMS-1005`.

## Shared Runtime Scenario Baseline

The runtime executor stories and downstream test-migration stories should reuse the same compact scenario names when describing write-path coverage:

`reference/design/backend-redesign/epics/13-test-migration/02-parity-and-fixtures.md` carries the compact feature-by-scenario matrix that maps these names to shared fixture and parity coverage. This story is the source of truth for the no-profile runtime scenarios; `DMS-1124` / `03b-profile-aware-persist-executor.md` is the source of truth for the profiled runtime scenarios.

- `NoProfileWriteBehavior` — control case with no writable profile; proves the write path still behaves as the normal full-surface upsert/update path, including omitted non-collection scopes and no-profile `_ext` data.
- `FullSurfaceCollectionReorder` — no-profile/full-surface collection reorder; matched rows keep stable `CollectionItemId` values while `Ordinal` changes.

## Acceptance Criteria

- POST/PUT runs in a single transaction and either commits all changed rows or rolls back fully on failure.
- Runtime persistence owns a request-scoped relational connection/transaction boundary suitable for executor-driven writes, rather than depending on the DMS-983 one-command-one-connection resolver seam.
- `PUT` and POST-as-update short-circuit as successful no-ops when the comparable stored/writable rowset is unchanged on the no-profile path.
- No-op detection piggybacks on the existing current-state load and does not require a dedicated “did anything change?” roundtrip.
- Guarded no-op comparison reuses the same merge-ordering and post-merge rowset-synthesis logic as execution, either by invoking the same helper or a shared helper built from the same executor-facing metadata.
- Before returning a no-op result, the executor revalidates that the observed `ContentVersion` is still current; stale compares are surfaced to the outer concurrency layer instead of returning success on stale state.
- Collection/common-type rows preserve existing stable identity for matched rows and reserve new `CollectionItemId` values only for unmatched inserts.
- No-profile non-collection writes retain standard full-surface semantics: separate-table 1:1/common-type/_ext rows insert, update, or delete according to request presence, and omitted inlined scopes clear their compiled bindings instead of preserving hidden values.
- No-profile collection writes retain stable-identity merge behavior, and `FullSurfaceCollectionReorder` proves an ordinal-only reorder updates matched rows in place instead of falling back to delete+insert.
- `NoProfileWriteBehavior` covers at least one omitted non-collection scope and one no-profile `_ext` case in addition to ordinary changed-write behavior.
- Bulk operations avoid N+1 insert/update patterns.
- Implementation works on both PostgreSQL and SQL Server with appropriate batching/parameterization behavior.
- Profile-aware merge, hidden-data preservation, creatability enforcement, and profiled guarded no-op behavior remain out of scope here and are owned by `DMS-1124`.

## Authorization Batching Consideration

Authorization is out of scope for this story, but the transaction and batching structure should be designed to allow authorization check statements to be prepended within the same roundtrip. For POST, auth checks are batched into the roundtrip that creates the `dms.Document` row; for PUT, auth checks and current-state loading run in the roundtrip that precedes the guarded no-op / persist step. See `reference/design/backend-redesign/design-docs/auth.md` §"Performance improvements over ODS" (POST roundtrip #3, PUT roundtrip #3).

## Tasks

1. Introduce the request-scoped relational connection/transaction boundary required by the persist executor so the runtime path no longer assumes the DMS-983 resolver's one-command-one-connection seam when coordinating prerequisite lookups and DML.
2. Implement rowset comparison for existing-document update flows by reusing the same stable-identity merge and post-merge ordering logic as the real executor, or a shared helper built from the same executor-facing merge metadata.
3. Implement a guarded no-op fast path that revalidates the observed `ContentVersion` before short-circuiting and returns a stale-compare outcome to the outer concurrency layer when freshness is lost.
4. Implement a write executor that applies the compiled `ResourceWritePlan` table-by-table in dependency order when a change exists for the full-surface/no-profile path, including standard present/absent handling for separate-table and inlined non-collection scopes.
5. Implement stable-identity collection/common-type merge execution for full-surface/no-profile writes, including matched-row update, visible-row delete, batched `CollectionItemId` reservation for inserts, and shared executor-facing merge metadata reusable by later profile-aware logic.
6. Implement deterministic post-merge `Ordinal` recomputation aligned to the no-op comparison path.
7. Implement bulk insert batching with dialect-specific limits and strategies.
8. Add integration tests for the shared no-profile runtime baseline above:
   - `NoProfileWriteBehavior`, including one changed resource with nested collections, one omitted non-collection scope case, and one no-profile `_ext` case, and
   - `FullSurfaceCollectionReorder`, proving matched rows keep stable identity while `Ordinal` changes.
