---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S06: Partition Pipeline and SQL

## Outcome

Expose authorized regular-resource and descriptor `/partitions` operations that calculate typed,
balanced cursor ranges in one identifiers-only database command.

## Design References

- [`/partitions`](EPIC.md#partitions)
- [`Partition planning`](EPIC.md#partition-planning)
- [`Application Boundaries`](EPIC.md#application-boundaries)
- [`Consistency Under Writes`](EPIC.md#consistency-under-writes)

## Dependencies

- Hard dependencies: E20-S00, E20-S01, E20-S02, and E20-S03.
- Soft dependencies: E20-S04 and E20-S05 provide completed collection execution patterns but do
  not block boundary compiler work.
- E20-S08 and E20-S09 consume the completed endpoint.

## Implementation Scope

- Activate the typed partition route with its dedicated Core pipeline and
  `IPartitionQueryHandler` backend contract.
- Apply shared resource/change-version validation, resource-action authorization, and row-level
  authorization before partition execution.
- Integrate E20-S00's approved `number` validation, unsupported-parameter ordering, default count,
  and five-page minimum sizing into the partition pipeline.
- Compile the approved PostgreSQL and SQL Server row-number/count/partition-size queries using the
  shared candidate relation and overflow-safe ceiling arithmetic.
- Return starting ids only, convert them to typed inclusive ranges, and token-encode them in Core.
- Support regular resources, extension resources, descriptors, route qualifiers, tenants, and
  profile routing without document hydration or profile projection.

## Acceptance Evidence and Test Expectations

- Core tests assert dedicated pipeline order, exact ProblemDetails, `number` precedence, canonical
  unsupported-parameter order, defaults, and empty response shape.
- SQL-golden tests cover both provider CTEs, parameter roles, one-command output, and arithmetic
  edge cases.
- PostgreSQL and real SQL Server integration tests cover counts 1, 10, and 200; sparse/empty sets;
  filters; change versions; descriptors; and fewer-than-requested ranges.
- Stable-fixture tests prove ranges are non-overlapping, final ranges are unbounded, and starts are
  actual accessible candidate ids.

## Cross-Provider and Authorization Responsibilities

- Equivalent provider fixtures must produce identical typed ranges and token payloads.
- Boundaries are calculated after every supported regular-resource authorization strategy and
  after descriptor no-further/namespace authorization. No inaccessible starting id may be
  returned.

## Explicit Exclusions / Not Assigned

- OpenAPI publication belongs to E20-S07.
- Broad ODS parity, multi-scenario E2E, and performance evidence belong to E20-S08 and E20-S09.
- Document hydration, descriptor projection, links, total count, DDL, and new indexes are not part
  of this endpoint story.
