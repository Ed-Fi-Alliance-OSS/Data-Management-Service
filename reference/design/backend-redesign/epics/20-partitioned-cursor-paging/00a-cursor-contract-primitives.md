---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S00a: Cursor Contract Primitives

## Outcome

Establish the provider-neutral typed paging, range, token, and result-boundary contracts together
with the configuration they require, so every later story shares one vocabulary before validation,
routing, planning, or execution work begins. The epic remains the source of truth for bounds,
terminal-page behavior, and configuration limits.

## Design References

- [`Public API Contract`](EPIC.md#public-api-contract)
- [`Cursor Token Contract`](EPIC.md#cursor-token-contract)
- [`Application Boundaries`](EPIC.md#application-boundaries)
- [`Configuration`](EPIC.md#configuration)

## Dependencies

- No hard dependency on another E20 story.
- This story blocks E20-S00b, E20-S02 through E20-S07, and E20-S11 wherever they consume paging,
  range, token, result-boundary, or configuration contracts.
- Existing E08 query contracts, E10 live change-version behavior, and E15 plan contracts are
  compatibility inputs.

## Implementation Scope

- Add the typed `CollectionPaging` traditional/cursor choice and `CursorRange` contract while
  retaining traditional `PaginationParameters` for tracked-change endpoints.
- Add the Core-owned token codec with the approved base64url, decimal, empty-maximum, inverted
  range, and `Int64.MaxValue` behavior.
- Define the nullable selected-keyset boundary and typed partition-result contract shapes without
  implementing provider execution.
- Add `AppSettings:DefaultPartitionCount` with its property and configured default, startup option
  validation for `DefaultPartitionCount` and `MaximumPageSize`, and the checked `long` minimum
  partition-size calculation.
- Keep token text encoding and decoding at Core's HTTP-contract boundary so backend contracts,
  planners, and compilers receive only typed ranges.

## Acceptance Evidence and Test Expectations

- Unit tests cover the complete codec grammar, round trips, bounds, malformed inputs, and
  terminal/overflow behavior.
- Contract tests prove tracked-change request models do not expose cursor paging.
- Configuration binding, default, and startup validation tests cover valid and invalid values,
  including the documented environment override.
- Contract tests prove the nullable boundary and typed partition-result shapes are provider-neutral
  and carry no token text.

## Cross-Provider and Authorization Responsibilities

- Contracts are provider-neutral and contain no PostgreSQL or SQL Server syntax.
- Token parsing must not make authorization decisions. Resource and row authorization remain
  independent inputs reapplied by later stories.

## Explicit Exclusions / Not Assigned

- Cursor precedence validation, partition validation, the ProblemDetails shell, and operation-scoped
  cursor parameter rejection belong to E20-S00b.
- Frontend path classification and repeated-parameter canonicalization belong to E20-S01.
- Candidate planning and SQL compilation belong to E20-S02, E20-S03, and E20-S06.
- Response-header execution belongs to E20-S04 and E20-S05.
- OpenAPI publication belongs to E20-S07.
