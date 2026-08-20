---
jira: DMS-1450
jira_url: https://edfi.atlassian.net/browse/DMS-1450
epic: DMS-1402
---

# Story: Implement the Natural-Key Resolver Behind an Internal Seam

## Outcome

Implement and verify the complete natural-key resolver without changing production dependency
injection, middleware composition, or public resolver-facing contracts.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1449 — natural-key SQL builders and cardinality contracts](07-natural-key-sql-builders-and-cardinality-contracts.md).
- Together with DMS-1446, this story blocks DMS-1451.

## Implementation Scope

- Implement `NaturalKeyReferenceResolver` with the DMS-1449 builders, structural memo, shared typed-value
  conversion, ordinal result mapping, target compatibility checks, and composite-embeddability seams.
- Introduce internal structural request/result keys alongside the still-active RI contracts.
- Introduce the internal `ReferenceLookupKey` record struct (`(target resource, DocumentIdentity)`)
  and the resolved document-reference map/factory contract (`IResolvedDocumentReferenceMap` /
  `IResolvedDocumentReferenceMapFactory`). The factory is the only construction path and installs
  the resolver's structural comparer; `ReferenceLookupKey` default equality is never authoritative.
  These types stay internal here; DMS-1451 re-points `ResolvedReferenceSet` and its consumers.
- Do not change `Add{Postgresql,Mssql}ReferenceResolver()`, production middleware composition,
  descriptor extraction/lowercasing, or public resolver-facing contracts.

## Acceptance Criteria

- Direct resolver unit and database integration suites pass on both providers for scalar, descriptor,
  concrete, abstract, missing, incompatible-target, and multiple-match cases.
- Separate `DocumentIdentity` arrays with identical ordered elements use the same structural memo
  entry.
- No public write-pipeline contract exposes or accepts `IReadOnlyDictionary<ReferenceLookupKey, long>`
  (or any dictionary keyed on `DocumentIdentity`) with default equality. A unit test builds the map
  through the factory and resolves two structurally identical `DocumentIdentity` instances backed by
  different arrays to the same `DocumentId`.
- The composite command factory can embed the new lookup statement.
- Production command-stream and RI resolver tests remain unchanged.
