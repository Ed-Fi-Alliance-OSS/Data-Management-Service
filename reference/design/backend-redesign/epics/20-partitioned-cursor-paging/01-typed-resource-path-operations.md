---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S01: Typed Resource Path Operations

## Outcome

Represent collection, by-id, and partition paths explicitly so `/partitions` is never parsed as a
document UUID, while preserving existing invalid-route behavior and repeated-query semantics.

## Design References

- [`Public API Contract`](EPIC.md#public-api-contract)
- [`Application Boundaries`](EPIC.md#application-boundaries)
- [`Risks and Guardrails`](EPIC.md#risks-and-guardrails)

## Dependencies

- Hard dependency: E20-S00 for reserved parameter names and validation-boundary contracts.
- E20-S06 owns activation of the dedicated partition pipeline and endpoint behavior.

## Implementation Scope

- Add typed `ResourcePathOperation` collection, by-id, and partition cases.
- Update path parsing, request state, route semantics, logging classification, and API dispatch to
  consume the typed operation.
- Canonicalize `pageToken`, `pageSize`, and partition `number` at the HTTP boundary.
- Preserve last-value-wins behavior across repeated parameters, including case variants, without
  dictionary collisions or HTTP 500 responses.
- Preserve the existing invalid-UUID result for unknown third segments and unmatched behavior for
  additional segments.

## Acceptance Evidence and Test Expectations

- Unit tests cover every typed path case, unknown child segments, extra segments, route qualifiers,
  and tenant-prefixed paths.
- Frontend tests prove repeated exact-name and case-variant query parameters choose the last value
  in request order.
- Regression tests cover existing GET-many, GET-by-id, write, delete, and tracked-change routing.
- No incomplete `/partitions` endpoint is externally exposed before E20-S06 supplies its pipeline.

## Cross-Provider and Authorization Responsibilities

- Routing is provider-neutral.
- Typed dispatch must preserve the existing authentication, resource-action authorization,
  profile, tenant, and datastore-resolution boundaries for collection and by-id operations.

## Explicit Exclusions / Not Assigned

- Token parsing and validation rules belong to E20-S00.
- Candidate queries and provider SQL belong to E20-S02, E20-S03, and E20-S06.
- Partition response generation belongs to E20-S06.
