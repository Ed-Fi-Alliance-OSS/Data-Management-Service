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
correctness, but conditionally required when Kafka CDC is enabled. This story adds separate registration-
prerequisite and source-readiness checks. Registration may establish capture before initial backfill, but the
deployment cannot advertise a supported stream before the projector can safely supply it.

For CDC, `dms.DocumentCache` is eventual for upserts but mandatory for deletes. DMS must not delete
`dms.Document` unless the delete transaction has verified or materialized a corresponding cache source row, so
Debezium can observe the cache row delete and publish the Kafka tombstone.

## Acceptance Criteria

- CDC configuration has an explicit enablement setting separate from read-cache enablement and Kafka UI startup.
- When CDC is enabled, startup/bootstrap validation fails fast if `dms.DocumentCache` is not provisioned for the
  selected data store.
- When CDC is enabled, startup/bootstrap validation verifies that the DocumentCache projector mode required by
  DMS-1246 is enabled for the selected data store.
- Registration prerequisites verify that required cache/state objects, stale-write fencing, pre-delete
  materialization, provider delete-source support, and physical-database uniqueness are available. Initial
  backfill completion and steady-state projector lag are not registration prerequisites.
- Readiness is keyed by `(tenant key, DataStoreId)` and is evaluated from that data store's explicit execution
  context rather than an HTTP request's route/JWT selection.
- CDC applies only to the explicit deployment-configured target list. Completed source readiness consumes
  DMS-1246's provider-resolved physical source binding after DMS starts. A missing configured target or confirmed
  provider/physical-database drift cannot remain CDC-ready and requires coordinated deployment; CMS entries
  outside the list and same-source credential/connection-setting changes are not drift. This runtime match is not
  a pre-DMS connector-registration prerequisite, and confirmed source drift remains latched until deployment.
- CDC readiness fails for every conflicting configured target when two listed targets resolve to the same
  physical database. Physical identity comparison does not rely only on raw connection-string text, and
  diagnostics never log credentials.
- When CDC is enabled, readiness fails until the bounded initial backfill epoch has materialized a fresh
  `dms.DocumentCache` row for every still-current `dms.Document` row at or below the epoch's captured
  `BackfillTargetContentVersion`.
- When CDC is enabled, readiness also verifies normal projector lag for writes above the completed backfill
  target is within the configured threshold.
- When CDC is enabled, readiness exposes the completed backfill epoch id and target content version as the
  cutover marker used to separate bounded bootstrap coverage from live projector catch-up coverage.
- When CDC is enabled, readiness verifies that the delete path has the DMS-1246 pre-delete source-row guarantee:
  missing/stale cache rows are synchronously materialized before `dms.Document` is deleted, and failed
  materialization blocks the API delete with a retryable server-side error.
- When CDC is enabled, readiness verifies projector/backfill stale-write fencing so lower `ContentVersion`
  retries cannot overwrite newer cache rows or recreate cache rows after delete.
- The CDC readiness check exposes actionable diagnostics for:
  - missing `dms.DocumentCache`,
  - incomplete bounded initial backfill epoch,
  - projector disabled,
  - projector unhealthy,
  - projector lag above the completed backfill target,
  - unresolved current projection failures, including dead-lettered failures,
  - missing pre-delete materialization support,
  - unsupported database provider,
  - a configured target missing from CMS,
  - a retryable physical-source identity resolution failure,
  - `CdcSourceDriftRequiresDeployment` after confirmed physical-source drift.
- The CDC readiness contract does not make API correctness, authorization, normal routing, GET/query behavior, or
  Change Queries depend on `dms.DocumentCache`. A separate default-off host policy may block mutations to a
  not-ready configured CDC target; it never blocks GETs or other read-only requests.
- Delete coverage required by CDC is implemented by the DMS-1246 projector/delete design and is explicitly
  consumed here as a prerequisite.

## Tasks

1. Add a data-store-specific prerequisite/readiness abstraction that distinguishes `CanRegisterConnector` from
   completed DocumentCache source readiness.
2. Bind CDC enablement configuration separately from any read-cache or Kafka UI settings.
3. Integrate registration-prerequisite validation and later source-readiness polling into local/bootstrap
   connector registration.
4. Add checks/tests that registration is rejected when required `dms.DocumentCache` or projector state is absent,
   and that source readiness remains false until the bounded initial backfill is complete.
5. Add checks/tests that CDC enablement fails when pre-delete materialization or stale-write fencing is not
   available for the selected data store.
6. Add provider-specific physical-database identity resolution and tests for duplicate/semantically equivalent
   data-store connection targets.
7. Bind the explicit CDC target list and consume DMS-1246's source-binding signal in post-start readiness
   diagnostics without changing normal request routing.
8. Add tests that non-CDC DMS startup remains valid without `dms.DocumentCache` and retains dynamic CMS refresh.
9. Document the handoff to DMS-1246 for projector backfill, delete materialization, lag, retry, source-binding
   readiness, the optional mutation policy, and health semantics.

## Out of Scope

- Implementing the projector itself.
- Implementing connector registration.
- Publishing Kafka records.
