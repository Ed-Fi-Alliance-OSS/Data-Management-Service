---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S08: Authorization, Parity, and E2E Suite

## Outcome

Demonstrate through API-level integration, ODS comparison, and end-to-end scenarios that cursor
pages and partition boundaries preserve the approved public, authorization, routing, and stable
fixture semantics.

## Design References

- [`Public API Contract`](EPIC.md#public-api-contract)
- [`Consistency Under Writes`](EPIC.md#consistency-under-writes)
- [`Test Expectations`](EPIC.md#test-expectations)
- [`Completion Evidence`](EPIC.md#completion-evidence)

## Dependencies

- Hard dependencies: E20-S04, E20-S05, E20-S06, and E20-S07.
- E20-S10 consumes the stable scenarios and fixtures for its final performance gate where useful.

## Implementation Scope

- Add API-level integration coverage for public parameter/header/body contracts and route behavior.
- Add the supported regular-resource and descriptor authorization matrix for cursor and partition
  requests.
- Add stable-fixture sequential and parallel partition walks, filters, change versions,
  extensions, descriptors, profiles, route qualifiers, and multi-tenancy.
- Build an ODS 7.3 comparison fixture and record the intentional differences already approved in
  the epic.
- Add concurrency scenarios that document, rather than overpromise, non-snapshot behavior.

## Acceptance Evidence and Test Expectations

- Stable sequential and parallel walks return every accessible fixture member exactly once with
  no overlap across ranges on PostgreSQL and representative real SQL Server coverage.
- Exact validation shells, phase gating, canonical error order, repeated-parameter behavior,
  terminal empty pages, and response headers are asserted through HTTP.
- Authorization tests prove forged tokens and partition requests do not expose inaccessible data
  or inaccessible starting ids.
- ODS/DMS results match except for the explicit differences listed in the epic.
- Existing traditional paging response bodies, status codes, and `Total-Count` semantics remain
  unchanged; the additional `Next-Page-Token` header is covered as an intentional contract change.

## Cross-Provider and Authorization Responsibilities

- PostgreSQL receives the complete DMS Docker E2E walk; real SQL Server receives provider
  integration/API-level coverage for every provider-sensitive behavior.
- Cover regular-resource no-further, relationship, ownership, namespace, and view strategies
  where supported, plus descriptor no-further and namespace authorization.

## Explicit Exclusions / Not Assigned

- Fundamental contract, planner, SQL, execution, and OpenAPI implementation belongs to E20-S00
  through E20-S07.
- Load/latency thresholds and provider plan capture belong to E20-S10.
- Snapshot consistency is not asserted.
