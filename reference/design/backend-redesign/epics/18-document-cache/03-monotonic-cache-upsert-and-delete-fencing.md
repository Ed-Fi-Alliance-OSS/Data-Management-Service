---
jira: DMS-1313
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Implement Monotonic Cache Upsert and Post-Delete Fencing

## Design References

- **Freshness and reconciliation**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation
- **Cached document contract**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md

The referenced design sections define cache-write ordering, concurrency, lifecycle, and
publication implications. This story is only the work package for implementing them.

## Outcome

Implement one provider-equivalent atomic cache-write/conditional-acknowledgement component
shared by queue processing and optional direct fill.

## Dependencies

- Depends on 18-00, 18-02, E10 representation stamps, and E11 delete behavior.
- Unblocks the 18-04 projector and 18-05 cache-backed read path.

## Implementation Scope

- Add the provider-specific cache DML and transaction adapters.
- Integrate the writer with the materializer result and projection safety state.
- Classify current source/cache/work in one statement; suppress stale candidates; let
  current durable `S = C = W` acknowledge work regardless of candidate version; and leave
  missing/behind cache work pending when the candidate is not current. Repeat the required
  source/work predicates on cache DML and source/cache/work predicates on acknowledgement;
  the classification result alone does not authorize later DML.
- Latch only current cache-ahead state after reclassification. Leave mismatched work
  pending for explicit conditional scrub/rebuild repair without setting the latch.
- Commit cache write and matching work deletion together. Cover equal-version fast
  acknowledgement, enqueue-versus-ack races, newer-work preservation, delete/post-delete
  fencing, crash windows, and duplicate writers.
- Hold the shared lifecycle-state lock through commit; obey the exclusive `Resetting`
  fence and provider-equivalent lock order. Measure same-document canonical-writer wait
  and retry complete canonical transactions after enqueue-related deadlock/serialization
  failures.
- Route projector and direct-fill writes through the shared component.
- Add sanitized outcome metrics and performance coverage.

## Acceptance Evidence

- PostgreSQL and SQL Server concurrency tests cover the writer interleavings and outcomes
  required by the referenced design sections.
- Provider tests cover integration with schema constraints, delete lifecycle, and safety
  state.
- Crash and concurrency tests prove no work-row lock spans materialization/backoff/I/O,
  cache and acknowledgement are atomic, stale candidates never write, and only current
  `C > S` sets the latch. A canonical commit between classification and conditional DML
  cannot erase or acknowledge its newer work.
- Performance evidence compares the required projector and direct-fill workload modes.

## Not Assigned to This Story

- Queue paging and administrative recovery orchestration are assigned to 18-04.
- Consumer ordering behavior is assigned to the Kafka contract and E19 verification.
