---
jira: DMS-1453
jira_url: https://edfi.atlassian.net/browse/DMS-1453
epic: DMS-1402
---

# Story: Extend Collection Duplicate Detection and Add the Generic Conflict Fallback

## Outcome

Close the local string-scalar gap in the existing storage-resolved collection duplicate check and
preserve an ODS-compatible 409 response for otherwise-unmapped unique constraint violations.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [Collection duplicate detection](../../design-docs/natural-key-resolution.md#collection-duplicate-detection)
- [Transactions and concurrency](../../design-docs/transactions-and-concurrency.md)
- [Flattening and reconstitution](../../design-docs/flattening-reconstitution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1451 — resolver and Core contract cutover](09-natural-key-resolver-and-core-contract-cutover.md).
- May run in parallel with DMS-1452.
- Together with DMS-1452, this story blocks DMS-1454.

## Implementation Scope

- `RelationalWriteFlattener` already dedupes collection siblings per parent scope on materialized
  semantic identity (resolved `DescriptorId`/`DocumentId` literals plus local scalars) via
  `ObjectValueArrayComparer` and rejects collisions with a path-attributed 400. Do not add a second
  validator. Extend that comparison so local string-scalar semantic-key members use the
  schema-contract-derived comparer from DMS-1443 (`OrdinalIgnoreCase` under the SQL Server identity
  collation, `Ordinal` on PostgreSQL); reference and descriptor members keep resolved-ID equality.
- Align the flattener's duplicate-item message with Core's duplicate-item wording so clients see one
  response shape regardless of which layer detected the duplicate; the path attribution is already
  in place.
- Add the ODS-compatible 409 fallback only for dialect-confirmed unique constraint violations that do
  not have a more specific mapping (`RelationalWriteConstraintResolver` `Unresolved` on a unique
  violation).
- Keep transient, deadlock, and timeout behavior covered by DMS-1400 unchanged.
- Update the write-flow design sketches so the flattener's storage-resolved duplicate check is shown
  after reference/descriptor resolution and before storage binding, no-op detection, or collection
  DML.

## Acceptance Criteria

- The per-provider duplicate-detection matrix passes: on SQL Server, case-variant duplicate local
  string-scalar members (for example two `electronicMails` differing only in the casing of
  `electronicMailAddress`) return the path-attributed 400 duplicate-item response instead of the
  pre-existing unmapped 5xx; on PostgreSQL the same payload succeeds with both items.
- The case-variant duplicate-descriptor E2E scenario passes on both engines as a regression guard for
  existing flattener behavior; it is tagged `@MssqlRepresentative` so the SQL Server lane runs it.
- Duplicate-item responses from Core and from the flattener share one message shape.
- An unmapped, dialect-confirmed unique violation returns the generic ODS-compatible 409 instead of a
  5xx.
- Non-unique database failures do not use the generic conflict fallback.
- Updated design sketches show the storage-resolved duplicate check in the required sequence.
