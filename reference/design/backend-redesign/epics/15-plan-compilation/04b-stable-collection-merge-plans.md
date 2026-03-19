---
jira: DMS-1108
jira_url: https://edfi.atlassian.net/browse/DMS-1108
---

# Story: Retrofit Write Plans for Stable-Identity Collection Merge Semantics

## Description

Retrofit the already-completed write-plan/compiler layer so collection tables compile to stable-identity merge semantics instead of parent-scope replace semantics.

This story updates the completed plan contract and compiler work in epic `15` to match:

- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/profiles.md`

The current compiler and contract surface still assume:

- child/collection tables are replaced with `DeleteByParentSql` + bulk insert, and
- compiled write-time keys are parent scope plus `Ordinal`.

That is incompatible with profile-aware writes, which require:

- matched collection rows to keep their stable internal identity,
- deletes to target only omitted visible rows,
- inserts to reserve new `CollectionItemId` values only for unmatched rows, and
- deterministic post-merge sibling ordering that preserves hidden stored rows.

## Acceptance Criteria

### Executor-facing contract changes

- Collection-capable table plans no longer treat `DeleteByParentSql` as the primary collection DML contract.
- Non-root 1:1 scopes keep the existing `InsertSql` / `UpdateSql` / `DeleteByParentSql` contract.
- Collection/common-type/extension-collection plans carry deterministic executor-facing metadata sufficient to:
  - identify and preserve the stable row identity of matched existing rows,
  - update matched rows in place,
  - delete omitted visible rows by stable row identity rather than by parent scope,
  - reserve new `CollectionItemId` values only for unmatched inserts, and
  - recompute `Ordinal` using the same deterministic post-merge ordering rule used by no-op detection.

### Stable-identity compilation

- Write-plan compilation consumes the `CollectionItemId`-based relational model shape from `DMS-1103`.
- `CollectionMergePlan.SemanticIdentityBindings` compile directly from the non-empty semantic identity already derived for the scope from `arrayUniquenessConstraints`; plan compilation does not invent a runtime fallback key.
- Collection/common-type extension scope plans align to base-row stable identity rather than ancestor ordinals.
- Plan metadata is sufficient for runtime code to compare current rows and post-merge rows without SQL parsing.
- If the upstream relational model for a persisted multi-item collection scope does not expose a non-empty semantic identity, plan compilation fails deterministically instead of emitting a merge plan with ambiguous matching semantics.

### Compare-order and guarded no-op metadata

- Collection/common-type/extension-collection plans carry deterministic executor-facing metadata sufficient to:
  - project hydrated current rows into write-plan compare order without SQL parsing,
  - compare hydrated current rows to request-derived/post-merge row buffers in stored/writable space,
  - determine the deterministic sibling ordering used for collection equality and guarded no-op detection, and
  - identify which stored/writable columns participate in no-op candidate comparison.
- `ColumnBindings` remain the authoritative parameter/value ordering for collection writes and the authoritative compare ordering for stable-identity collection merge execution.
- Where the retrofit requires additional table/model/read-projection metadata so executors can consume hydrated rows by ordinal and compare them against the revised collection plan contract, that metadata remains deterministic and pack-serializable.

### Determinism and serialization

- Plan manifests, normalized DTO/codecs, and any mapping-pack-facing plan serialization remain deterministic after the contract change.
- PostgreSQL and SQL Server write-plan output remains canonical and stable for the same selection key.

### Testing

- Unit tests cover:
  - top-level and nested collection merge-plan metadata,
  - collection-aligned `_ext` scopes,
  - deterministic stable-identity DML output,
  - compare-order / no-op candidate projection cases,
  - normalized contract round-tripping, and
  - compare-order metadata used by guarded no-op detection.
- Fixture/golden outputs are updated to reflect the stable-identity collection write-plan contract.

## Tasks

1. Extend the write-plan contract types to distinguish collection merge operations from non-root 1:1 delete-by-parent behavior, including the deterministic compare/no-op metadata required for stable-identity collection scopes.
2. Update write-plan compilation to emit deterministic stable-identity collection DML/metadata from the `CollectionItemId`-based relational model, including the metadata needed to project hydrated current rows into write-plan compare order and the non-empty semantic identity bindings already derived from `arrayUniquenessConstraints`.
3. Preserve the existing non-root 1:1 scope contract while removing replace-semantics assumptions for collections and keeping non-collection presence/absence behavior intact.
4. Update manifest emission, normalized DTO/codecs, and any pack-facing serialization for the revised contract and compare/no-op metadata.
5. Add unit tests and golden fixtures that lock down stable-identity collection plan compilation, compare-order invariants, and no-op candidate projection behavior for both dialects.
