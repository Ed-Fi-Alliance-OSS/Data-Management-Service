---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1246
---

# Story: Reconcile Dynamic Multi-Instance CDC Connectors

## Description

Add the deployment-owned reconciliation contract for connectors in a DMS process whose tenant/data-store
inventory can change without restart.

The reconciler compares CMS inventory, provider-specific physical database identity, per-data-store
`DocumentCache` readiness, and Kafka Connect registrations. Its logical key is
`(deployment key, tenant key, DataStoreId)`; tenant identity is administrative only and is not published in
topics or records.

## Dependencies

- Depends on `17-00-documentcache-cdc-prerequisites.md`, `17-02-connector-template-generation.md`, and the
  per-data-store projector/readiness work in `18-document-cache/03-async-projector-worker.md` and
  `18-document-cache/09-documentcache-health-readiness-and-telemetry.md`.
- Reuses the idempotent Kafka Connect registration behavior from
  `17-03-bootstrap-enable-kafka-cdc.md`.

## Acceptance Criteria

- Reconciliation enumerates all configured tenant/data-store entries independently of HTTP JWTs and route
  qualifiers.
- A newly discovered data store remains not ready until schema, bounded backfill, projector health, physical
  database uniqueness, and provider CDC prerequisites pass; it then receives one logical connector and topic.
- A connection string, provider, or resolved physical database change stops the old connector before replacement
  and requires the new source's backfill/snapshot procedure before readiness returns.
- A route-qualifier-only change does not rename the topic or restart the connector while `DataStoreId` and the
  physical database identity are unchanged.
- Removal stops the connector only after the configured confirmation policy. It does not automatically delete
  topics, offsets, ACLs, PostgreSQL slots/publications, or SQL Server capture state.
- Two active data-store records that resolve to the same physical database are rejected; the reconciler does not
  publish the same `dms.DocumentCache` under two independently authorized instance topics.
- Reconciliation is idempotent and isolates failures so one unavailable database or connector does not prevent
  healthy peers from converging.
- Connector status is exposed per `(tenant key, DataStoreId)`, with an optional aggregate that cannot hide the
  individual results.
- Tests cover add, connection/provider replacement, route-only change, removal/reattachment, duplicate physical
  targets, transient CMS failure, and mixed healthy/unhealthy tenants.

## Tasks

1. Define the desired/observed connector inventory and reconciliation state machine.
2. Integrate tenant/data-store discovery, per-data-store CDC readiness, and physical database identity checks.
3. Reuse connector templates and idempotent Kafka Connect create/update/stop operations.
4. Add configurable confirmation/retention behavior for missing data stores without destructive cleanup.
5. Expose per-data-store reconciliation status, last error, and last successful convergence time.
6. Add provider and Kafka Connect integration tests for the lifecycle transitions.

## Out of Scope

- Automatic deletion of Kafka topics, offsets, ACLs, or database CDC artifacts.
- Cloud-provider-specific deployment controllers.
- Projector implementation; DMS-1246 owns that lifecycle.
