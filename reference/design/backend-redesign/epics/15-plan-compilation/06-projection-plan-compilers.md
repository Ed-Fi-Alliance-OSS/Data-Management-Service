---
jira: TBD
jira_url: TBD
---

# Story: Compile Projection Plans (Descriptor URI + Identity Projection)

## Description

Compile the additional SQL and plan metadata required for projection steps that sit alongside hydration:

- **Descriptor URI projection**: batched lookup `(DescriptorId, Uri)` for all descriptor ids referenced by a page.
- **Identity projection**: inventory/SQL required to project identity values deterministically from relational storage
  (including abstract targets) for referential-id computation and/or diagnostics.

Design references:

- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (projection plan usage)
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (descriptor + identity projection rules)
- `reference/design/backend-redesign/design-docs/update-tracking.md` (identity/representation versioning context)

## Acceptance Criteria

- Compiled plans include deterministic projection SQL and metadata for:
  - descriptor URI projection (page-batched; no N+1 joins),
  - identity projection (including abstract targets where applicable).
- Projection plan SQL is canonicalized and stable for a fixed selection key.
- Unit tests validate deterministic output and that projection plans reference only embedded model elements.

## Tasks

1. Implement descriptor projection plan compilation as an additional batched query/result set shape.
2. Implement identity projection plan compilation (including abstract targets) sufficient for downstream consumers.
3. Add unit tests for deterministic output and model reference integrity.

