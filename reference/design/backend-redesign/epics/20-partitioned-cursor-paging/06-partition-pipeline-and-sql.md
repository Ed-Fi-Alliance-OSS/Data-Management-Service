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

- [`/partitions`](../../design-docs/partitioned-cursor-paging.md#partitions)
- [`Partition validation`](../../design-docs/partitioned-cursor-paging.md#partition-validation)
- [`Partition sizing`](../../design-docs/partitioned-cursor-paging.md#partition-sizing)
- [`Partition planning`](../../design-docs/partitioned-cursor-paging.md#partition-planning)
- [`Application Boundaries`](../../design-docs/partitioned-cursor-paging.md#application-boundaries)
- [`Consistency Under Writes`](../../design-docs/partitioned-cursor-paging.md#consistency-under-writes)
- [`Approved Intentional ODS Differences`](EPIC.md#approved-intentional-ods-differences)

## Dependencies

- Hard dependencies for boundary-compiler and SQL-golden work: E20-S00a, E20-S00b, and E20-S02.
  E20-S04 remains soft for that work.
- Route activation additionally has a hard dependency on E20-S04 so `/partitions` cannot hand
  clients tokens before both regular-resource and descriptor GET-many endpoints can consume them.
- E20-S07 publishes partition paths only when this runtime pipeline lands. E20-S08a, E20-S08b, and
  E20-S10 consume the completed endpoint.

## Implementation Scope

- Activate the typed partition route with its dedicated Core pipeline and
  `IPartitionQueryHandler` backend contract.
- Apply shared resource/change-version validation, resource-action authorization, and row-level
  authorization before partition execution.
- Integrate E20-S00b's approved `number` validation and unsupported-parameter ordering together with
  E20-S00a's default count and five-page minimum sizing into the partition pipeline.
- Compile the approved PostgreSQL and SQL Server row-number/count/partition-size queries using the
  shared candidate relation and provider-appropriate mathematical ceiling arithmetic; the exact
  equivalent expression is not contractual.
- Return starting ids only, convert them to typed inclusive ranges, and token-encode them in Core.
- Support regular resources, extension resources, descriptors, route qualifiers, tenants, and
  profile routing without document hydration or profile projection.
- Reuse the existing GET-many profile content-type outcome: an explicitly requested profile whose
  resource exposes no readable content type returns the existing HTTP 405 profile method-usage
  response, while a request for which no readable profile applies implicitly proceeds unfiltered, so
  runtime enforcement agrees with E20-S07's OpenAPI omission.

## Acceptance Evidence and Test Expectations

- Core tests assert dedicated pipeline order, exact ProblemDetails, `number` precedence, canonical
  unsupported-parameter order, defaults, and empty response shape.
- SQL-golden tests cover both provider CTEs, parameter roles, one-command output, and partition
  sizing semantics without requiring one algebraic spelling of ceiling division.
- PostgreSQL and real SQL Server integration tests cover counts 1, 10, and 200; sparse/empty sets;
  filters; change versions; descriptors; and fewer-than-requested ranges.
- Sizing tests prove the returned token count never exceeds the requested `number`, including
  candidate counts that divide inexactly by `number`, where ODS's integer-division sizing reaches
  the same count only by widening its final partition, and returns an extra token only when
  `number` is omitted.
- Stable-fixture tests prove ranges are non-overlapping, final ranges are unbounded, and starts are
  actual accessible candidate ids.
- Profile tests cover the write-only profile outcome and prove it matches the collection GET
  behavior for the same resource and profile.

## Cross-Provider and Authorization Responsibilities

- Equivalent provider fixtures must produce identical typed ranges and token payloads.
- Boundaries are calculated after every supported regular-resource authorization strategy and
  after descriptor no-further/namespace authorization. No inaccessible starting id may be
  returned.

## Explicit Exclusions / Not Assigned

- OpenAPI publication belongs to E20-S07.
- The cross-strategy authorization matrix belongs to E20-S08a, ODS-comparison cases plus broad
  parity execution and multi-scenario E2E belong to E20-S08b, and performance evidence belongs to
  E20-S10.
- Document hydration, descriptor projection, links, total count, DDL, and new indexes are not part
  of this endpoint story.
