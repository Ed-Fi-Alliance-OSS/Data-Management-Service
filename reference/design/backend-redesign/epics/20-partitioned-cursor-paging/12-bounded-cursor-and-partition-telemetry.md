---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S12: Bounded Cursor and Partition Telemetry

## Outcome

Add production-safe, bounded telemetry for traditional paging, cursor execution, and partition
planning without coupling instrumentation delivery to the large-fixture performance matrix.

## Design References

- [`Bounded Telemetry`](EPIC.md#bounded-telemetry)
- [`Performance Invariants and Evidence`](EPIC.md#performance-invariants-and-evidence)
- [`Risks and Guardrails`](EPIC.md#risks-and-guardrails)

## Dependencies

- Hard dependencies: E20-S04, E20-S05, and E20-S06 for the completed regular-resource,
  descriptor, and partition execution paths being instrumented.
- Existing E12 observability conventions are reusable inputs. E20-S10 measurement and threshold
  evaluation are independent and do not block this production work.

## Implementation Scope

- Emit bounded metrics or structured events for paging mode, requested and returned page size,
  requested and returned partition count, duration, provider, command category, and outcome.
- Use stable low-cardinality names and dimensions shared across regular resources and descriptors
  where their execution semantics are equivalent.
- Cover success, validation rejection, early-empty selection, execution failure, and terminal-page
  outcomes without adding database commands or changing response behavior.
- Never record raw token text, decoded cursor bounds, filter names or values, client identity, or
  candidate identifiers.
- Document event/metric definitions, units, allowed dimension values, and aggregation intent for
  operators.

## Acceptance Evidence and Test Expectations

- Unit tests prove emitted names, units, dimensions, outcome classification, and omission of
  disallowed data for traditional, cursor, and partition paths.
- Cardinality review confirms resource names, token-derived values, candidate ids, client ids, and
  arbitrary exception text are not dimensions.
- Integration tests prove instrumentation adds no database command or roundtrip and does not alter
  status, headers, or response bodies.
- Provider tests confirm equivalent PostgreSQL and SQL Server operations use the same logical
  telemetry contract while retaining the bounded provider dimension.

## Cross-Provider and Authorization Responsibilities

- Telemetry is provider-neutral except for the explicit bounded provider and command-category
  dimensions.
- Authorization outcomes may be classified only with bounded success/failure categories; claims,
  identities, ownership values, namespaces, and accessible candidate ids are never recorded.

## Explicit Exclusions / Not Assigned

- Large-fixture provisioning, benchmark iteration, plan capture, thresholds, and regression
  reporting belong to E20-S10.
- Dashboards, alert thresholds, paid APM integration, production capacity sizing, and raw-query
  logging are not assigned.
