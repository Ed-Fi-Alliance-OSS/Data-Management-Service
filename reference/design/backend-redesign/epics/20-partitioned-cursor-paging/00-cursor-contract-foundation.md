---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S00: Cursor Contract Foundation

## Outcome

Establish the provider-neutral HTTP and Core contracts for traditional-versus-cursor paging,
token encoding/decoding, phase-gated validation, and configuration. The epic remains the source
of truth for exact messages, ordering, bounds, and terminal-page behavior.

## Design References

- [`Public API Contract`](EPIC.md#public-api-contract)
- [`Cursor Token Contract`](EPIC.md#cursor-token-contract)
- [`Application Boundaries`](EPIC.md#application-boundaries)
- [`Configuration`](EPIC.md#configuration)

## Dependencies

- No hard dependency on another E20 story.
- This story blocks E20-S01 through E20-S07 wherever they consume paging, token, validation, or
  configuration contracts.
- Existing E08 query contracts and E10 live change-version behavior are compatibility inputs.

## Implementation Scope

- Add the typed `CollectionPaging` traditional/cursor choice and `CursorRange` contract while
  retaining traditional `PaginationParameters` for tracked-change endpoints.
- Add the Core-owned token codec with the approved base64url, decimal, empty-maximum, inverted
  range, and `Int64.MaxValue` behavior.
- Add phase-gated cursor and partition parameter validators and the approved ProblemDetails shell.
- Define selected-keyset boundary and typed partition-result contracts without implementing
  provider execution.
- Add and validate `DefaultPartitionCount`; validate `MaximumPageSize` for partition sizing.

## Acceptance Evidence and Test Expectations

- Unit tests cover the complete codec grammar, round trips, bounds, malformed inputs, and
  terminal/overflow behavior.
- Validator tests prove syntax/range, required-relationship, and mixed-mode phase gating with
  canonical error order and exact messages.
- Traditional-only pagination failures retain their current response shell and messages.
- Configuration binding/default/startup validation tests cover valid and invalid values.
- Contract tests prove tracked-change request models do not expose cursor paging.

## Cross-Provider and Authorization Responsibilities

- Contracts are provider-neutral and contain no PostgreSQL or SQL Server syntax.
- Token parsing must not make authorization decisions. Resource and row authorization remain
  independent inputs reapplied by later stories.

## Explicit Exclusions / Not Assigned

- Frontend path classification and repeated-parameter canonicalization belong to E20-S01.
- Candidate planning and SQL compilation belong to E20-S02, E20-S03, and E20-S06.
- Response-header execution belongs to E20-S04 and E20-S05.
- OpenAPI publication belongs to E20-S07.
