---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1246
---

# Story: Wire CDC Enablement to `dms.DocumentCache` Projector Guarantees

## Description

Make CDC enablement depend on the `dms.DocumentCache` implementation contract finalized by DMS-1246.

Relational CDC uses `dms.DocumentCache` as the capture source. That means the cache is optional for normal API
correctness, but conditionally required when Kafka CDC is enabled. This story adds the readiness checks and
configuration boundaries needed so connector registration cannot advertise a supported stream before the
projector can safely supply it.

For CDC, `dms.DocumentCache` is eventual for upserts but mandatory for deletes. DMS must not delete
`dms.Document` unless the delete transaction has verified or materialized a corresponding cache source row, so
Debezium can observe the cache row delete and publish the Kafka tombstone.

## Acceptance Criteria

- CDC configuration has an explicit enablement setting separate from read-cache enablement and Kafka UI startup.
- When CDC is enabled, startup/bootstrap validation fails fast if `dms.DocumentCache` is not provisioned for the
  selected data store.
- When CDC is enabled, startup/bootstrap validation verifies that the DocumentCache projector mode required by
  DMS-1246 is enabled for the selected data store.
- When CDC is enabled, readiness fails until initial backfill has materialized a fresh `dms.DocumentCache` row
  for every existing `dms.Document` row.
- When CDC is enabled, readiness verifies that the delete path has the DMS-1246 pre-delete source-row guarantee:
  missing/stale cache rows are synchronously materialized before `dms.Document` is deleted, and failed
  materialization blocks the API delete with a retryable server-side error.
- When CDC is enabled, readiness verifies projector/backfill stale-write fencing so lower `ContentVersion`
  retries cannot overwrite newer cache rows or recreate cache rows after delete.
- The CDC readiness check exposes actionable diagnostics for:
  - missing `dms.DocumentCache`,
  - incomplete initial backfill,
  - projector disabled,
  - projector unhealthy,
  - projector lag above the configured threshold,
  - unresolved current projection failures, including dead-lettered failures,
  - missing pre-delete materialization support,
  - unsupported database provider.
- The CDC readiness contract does not make API write correctness, authorization, normal GET/query behavior, or
  Change Queries depend on `dms.DocumentCache`.
- Delete coverage required by CDC is implemented by the DMS-1246 projector/delete design and is explicitly
  consumed here as a prerequisite.

## Tasks

1. Add a CDC readiness abstraction that reports data-store-specific readiness for connector registration.
2. Bind CDC enablement configuration separately from any read-cache or Kafka UI settings.
3. Integrate readiness validation into local/bootstrap connector registration.
4. Add checks/tests that CDC enablement fails when `dms.DocumentCache`, initial backfill, or required projector
   state is absent.
5. Add checks/tests that CDC enablement fails when pre-delete materialization or stale-write fencing is not
   available for the selected data store.
6. Add tests that non-CDC DMS startup remains valid without `dms.DocumentCache`.
7. Document the handoff to DMS-1246 for projector backfill, delete materialization, lag, retry, and health
   semantics.

## Out of Scope

- Implementing the projector itself.
- Implementing connector registration.
- Publishing Kafka records.
