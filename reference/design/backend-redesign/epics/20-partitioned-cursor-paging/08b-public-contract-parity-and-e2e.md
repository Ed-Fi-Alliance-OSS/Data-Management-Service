---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S08b: Public Contract, Parity, and E2E Suite

## Outcome

Demonstrate through API-level integration, end-to-end scenarios, and the E20-S11 comparison fixture
that the published cursor and partition contract behaves as approved across routes, tenants,
profiles, extensions, and descriptors, and that its differences from ODS 7.3.2 are only the approved
ones.

## Design References

- [`Public API Contract`](EPIC.md#public-api-contract)
- [`Consistency Under Writes`](EPIC.md#consistency-under-writes)
- [`Approved Intentional ODS Differences`](EPIC.md#approved-intentional-ods-differences)
- [`Completion Evidence`](EPIC.md#completion-evidence)

## Dependencies

- Hard dependencies: E20-S04, E20-S05, E20-S06, E20-S07, and the E20-S11 ODS comparison fixture and
  case definitions.
- E20-S08a covers the authorization matrix and shares its fixtures.
- E20-S10 consumes the stable scenarios and fixtures for its final performance gate where useful.

## Implementation Scope

- Add API-level integration coverage for the public parameter, header, body, and status contracts of
  cursor pages and `/partitions`, including exact validation shells and single-error behavior.
- Add stable-fixture sequential and parallel partition walks over regular resources, extension
  resources, and descriptors, with filters and live change-version bounds repeated on each request.
- Cover route qualifiers, tenant segments, profile routing including the write-only profile outcome,
  and the published OpenAPI/profile metadata from E20-S07.
- Execute E20-S11's case definitions against a DMS target and compare them with the captured ODS
  results, including the DMS half of the response-header comparison that requires E20-S04 and
  E20-S05 execution.
- Add concurrency scenarios that document, rather than overpromise, non-snapshot behavior.

## Acceptance Evidence and Test Expectations

- Stable sequential and parallel walks return every accessible fixture member exactly once with no
  overlap across ranges on PostgreSQL and representative real SQL Server coverage.
- Exact validation shells, ODS-compatible cursor precedence and single-error behavior, partition
  validation ordering, repeated-parameter and case-variant behavior, terminal empty pages, and
  response headers are asserted through HTTP.
- Every executed comparison case either matches ODS or maps to a named approved difference in the
  epic; an unmapped difference fails the suite.
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
- The pinned ODS reference stack, ODS-side capture, and approved-difference ledger belong to
  E20-S11.
- Load/latency thresholds and provider plan capture belong to E20-S10.
- Snapshot consistency is not asserted.
