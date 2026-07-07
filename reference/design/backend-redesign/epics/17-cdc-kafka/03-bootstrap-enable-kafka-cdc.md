---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Explicit Local/Bootstrap Connector Registration

## Description

Add an explicit local/bootstrap opt-in for relational Kafka CDC connector registration.

Kafka and Kafka UI can be useful infrastructure without CDC. Connector registration must therefore be controlled
by a separate flag and must use the selected data-store context from the bootstrap flow instead of hard-coded
database names.

## Acceptance Criteria

- Local/bootstrap scripts expose an explicit CDC flag, recommended as `-EnableKafkaCdc`.
- `-EnableKafkaUI` starts Kafka UI only and does not register DMS source connectors.
- When CDC is enabled, bootstrap starts Kafka and Kafka Connect if they are not already running.
- Connector registration runs only after:
  - the data store is selected,
  - the target database is provisioned,
  - `dms.DocumentCache` CDC readiness passes,
  - initial `dms.DocumentCache` backfill is complete for existing documents,
  - the CDC-mode pre-delete materialization guarantee is available for the selected data store,
  - provider-specific CDC DDL/setup is applied or validated.
- Connector registration is idempotent for the same selected data store and connector name.
- Bootstrap prints the connector name, provider, database, instance key, and target topic.
- Teardown removes local connector registrations and Kafka state when the local stack is torn down with volumes.
- E2E setup can opt into CDC and register the connector before test writes are issued.
- Failure messages identify whether the problem is Kafka infrastructure, connector REST API, database CDC setup,
  DocumentCache readiness, incomplete backfill, missing pre-delete materialization support, or connector
  validation.

## Tasks

1. Add the public `-EnableKafkaCdc` parameter to the appropriate local/bootstrap entry points.
2. Reuse existing bootstrap data-store selection so connector registration targets the selected data store.
3. Add provider-specific connector registration using the templates from Story 02.
4. Add idempotent create/update behavior for connector REST registration.
5. Add connector status polling with a clear timeout and failure diagnostics.
6. Update local teardown to remove connector state when appropriate.
7. Add script tests or integration tests for flag behavior and registration sequencing.

## Out of Scope

- Publishing production deployment automation for managed Kafka providers.
- Replacing the Kafka Connect image.
- Defining the projector's CDC health semantics.
