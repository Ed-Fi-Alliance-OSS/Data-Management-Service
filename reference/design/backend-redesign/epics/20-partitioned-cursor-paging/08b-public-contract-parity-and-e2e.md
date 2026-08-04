---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S08b: Public Contract, Parity, and E2E Suite

## Outcome

Demonstrate through API-level integration, end-to-end scenarios, and static ODS-comparison cases
that the published cursor and partition contract behaves as approved across routes, tenants,
profiles, extensions, and descriptors, and that its differences from ODS 7.3.2 are only the approved
ones.

The ODS comparison is expressed as static expected values, not as a live reference deployment. The
epic's worked precedence table and approved-difference list were established by reading the pinned
ODS 7.3.2 sources cited in its `Compatibility Baseline`, so those tables are the reference; standing
up and automating an ODS 7.3.2 API is explicitly out of scope.

## Design References

- [`Public API Contract`](EPIC.md#public-api-contract)
- [`Consistency Under Writes`](EPIC.md#consistency-under-writes)
- [`Approved Intentional ODS Differences`](EPIC.md#approved-intentional-ods-differences)
- [`Completion Evidence`](EPIC.md#completion-evidence)

## Dependencies

- Hard dependencies: E20-S04, E20-S06, and E20-S07.
- E20-S08a covers the authorization matrix and shares its fixtures.
- E20-S10 consumes the stable scenarios and fixtures for its final performance gate where useful.

## Implementation Scope

- Add API-level integration coverage for the public parameter, header, body, and status contracts of
  cursor pages and `/partitions`, including exact validation shells and single-error behavior.
- Add stable-fixture sequential and parallel partition walks over regular resources, extension
  resources, and descriptors, with filters and live change-version bounds repeated on each request.
- Cover route qualifiers, tenant segments, profile routing including the write-only profile outcome,
  and the published OpenAPI/profile metadata from E20-S07.
- Own the ODS-comparison case definitions as static expected values derived from the epic's worked
  precedence table and approved-difference list, execute them against a DMS target, and assert each
  case either matches the recorded ODS behavior or maps to a named approved difference. This includes
  the response-header cases, where ODS gates the header on hydrated body count and DMS gates it on a
  non-null selected-keyset maximum.
- Retain the case definitions and results in a machine-readable form carrying the reference-version
  identity, so a future reviewer can see which ODS version the expectations describe.
- Add concurrency scenarios that document, rather than overpromise, non-snapshot behavior.

## Acceptance Evidence and Test Expectations

- Stable sequential and parallel walks return every accessible fixture member exactly once with no
  overlap across ranges on PostgreSQL and representative real SQL Server coverage.
- Exact validation shells, ODS-compatible cursor precedence and single-error behavior, partition
  validation ordering, repeated-parameter and case-variant behavior, terminal empty pages, and
  response headers are asserted through HTTP.
- Every executed comparison case either matches the recorded ODS behavior or maps to a named
  approved difference in the epic; an unmapped difference fails the suite.
- The harness implements every row of the epic's worked precedence table, asserting each listed DMS
  message and each recorded ODS parity/difference outcome, including exactly one error whenever DMS
  rejects the request.
- The partition token count never exceeds the requested `number`, and requesting more partitions
  than the minimum size allows returns fewer tokens rather than an error.
- Existing traditional paging response bodies, status codes, and `Total-Count` semantics remain
  unchanged; the additional `Next-Page-Token` header is covered as an intentional contract change.

## Cross-Provider and Authorization Responsibilities

- PostgreSQL receives the complete DMS Docker E2E walk; real SQL Server receives provider
  integration and API-level coverage for every provider-sensitive behavior.
- Authorization is exercised only as far as the public contract requires. The supported-strategy
  matrix belongs to E20-S08a.

## Explicit Exclusions / Not Assigned

- The cross-strategy authorization matrix and forged-range negative cases belong to E20-S08a.
- Fundamental contract, planner, SQL, execution, and OpenAPI implementation belongs to E20-S00a
  through E20-S07.
- A live or automated ODS 7.3.2 reference deployment is out of scope; the epic's source-derived
  tables are the reference.
- Load/latency thresholds and provider plan capture belong to E20-S10.
- Snapshot consistency is not asserted.
