# Story: Project Reference Identity Values (Concrete + Abstract Targets)

## Description

Implement read-time projection of reference identity values into returned JSON:

- For concrete reference targets, project identity fields from the referenced document’s root table columns.
- For abstract reference targets, project identity fields via `{schema}.{AbstractResource}_View` union views.

Align with `reference/design/backend-redesign/data-model.md` (“Abstract identity views for polymorphic references”).

## Acceptance Criteria

- Reference objects in responses contain identity fields derived from the current referenced rows (no rewrite cascades).
- Abstract-target references use the union view and return the abstract identity fields in the correct order.
- Membership/type validation for abstract references is enforced (batchable) during reads as needed.
- Integration tests cover at least one abstract reference scenario.

## Tasks

1. Implement projection queries for reference identities (batch by referenced `DocumentId` set).
2. Implement abstract-target projection using union views and deterministic casts.
3. Integrate projections into reconstitution so reference objects are populated without per-reference queries.
4. Add tests for concrete and abstract reference projection correctness.

