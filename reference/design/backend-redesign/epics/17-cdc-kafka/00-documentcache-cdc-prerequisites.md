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

## Acceptance Criteria

- CDC configuration has an explicit enablement setting separate from read-cache enablement and Kafka UI startup.
- When CDC is enabled, startup/bootstrap validation fails fast if `dms.DocumentCache` is not provisioned for the
  selected data store.
- When CDC is enabled, startup/bootstrap validation verifies that the DocumentCache projector mode required by
  DMS-1246 is enabled for the selected data store.
- The CDC readiness check exposes actionable diagnostics for:
  - missing `dms.DocumentCache`,
  - projector disabled,
  - projector unhealthy,
  - projector lag above the configured threshold,
  - unsupported database provider.
- The CDC readiness contract does not make API write correctness, authorization, normal GET/query behavior, or
  Change Queries depend on `dms.DocumentCache`.
- Delete coverage required by CDC is delegated to the DMS-1246 projector design and is explicitly consumed here
  as a prerequisite.

## Tasks

1. Add a CDC readiness abstraction that reports data-store-specific readiness for connector registration.
2. Bind CDC enablement configuration separately from any read-cache or Kafka UI settings.
3. Integrate readiness validation into local/bootstrap connector registration.
4. Add tests that CDC enablement fails when `dms.DocumentCache` or required projector state is absent.
5. Add tests that non-CDC DMS startup remains valid without `dms.DocumentCache`.
6. Document the handoff to DMS-1246 for projector backfill, delete materialization, lag, retry, and health
   semantics.

## Out of Scope

- Implementing the projector itself.
- Implementing connector registration.
- Publishing Kafka records.
