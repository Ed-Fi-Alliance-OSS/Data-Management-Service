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

Production-like deployment automation repeats this one-shot workflow for every target in the explicit CDC target
list. Runtime discovery, addition, removal, and physical-source replacement are not part of v1.

## Acceptance Criteria

- Local/bootstrap scripts expose an explicit CDC flag, recommended as `-EnableKafkaCdc`.
- `-EnableKafkaUI` starts Kafka UI only and does not register DMS source connectors.
- When CDC is enabled, bootstrap starts Kafka and Kafka Connect if they are not already running.
- The selected data store must be present in the deployment-configured CDC target list; bootstrap does not infer
  opt-in from CMS membership.
- Connector registration runs only after:
  - the data store is selected,
  - the target database is provisioned,
  - `dms.DocumentCache` and `dms.Document` are provisioned,
  - projector mode is `Async`,
  - stale-write fencing is available,
  - provider-specific two-table capture, `DocumentUuid` keys, PostgreSQL replica
    identity, and source-operation filtering are applied or validated.
- Connector registration establishes capture before projector reconciliation and before
  E2E or deployment writes that must be observed by CDC.
- CDC is advertised as ready only after:
  - the selected data store is in the explicit deployment target list and still resolves to its startup physical
    source binding,
  - the exact current `dms.Document`/`dms.DocumentCache` mismatch count is zero,
  - connector snapshot/catch-up has reached a source position at or after the
    zero-mismatch observation,
  - connector lag is within threshold.
- Bootstrap diagnostics include mismatch count and oldest mismatch age; they do not use
  a backfill epoch or maximum projected version as a cutover marker.
- Connector registration is idempotent for the same selected data store and connector name.
- Bootstrap prints the connector name, provider, database, instance key, and target topic.
- Non-local bootstrap requires a deployment-unique topic prefix and rejects the local `edfi.dms` default unless
  the broker is explicitly declared dedicated to this deployment.
- Teardown removes local connector registrations and Kafka state when the local stack is torn down with volumes.
- E2E setup can opt into CDC and register the connector before test writes are issued.
- Failure messages identify whether the problem is Kafka infrastructure, connector REST API, database CDC setup,
  DocumentCache registration prerequisites, nonzero mismatch count/age, connector
  snapshot/catch-up, invalid two-table key/filter setup, missing
  configured target, retryable source-identity resolution,
  `CdcSourceDriftRequiresDeployment`, or connector validation.

## Tasks

1. Add the public `-EnableKafkaCdc` parameter to the appropriate local/bootstrap entry points.
2. Reuse existing bootstrap data-store selection so connector registration targets the selected data store.
3. Add provider-specific connector registration using the templates from Story 02.
4. Add idempotent create/update behavior for connector REST registration.
5. Add connector status polling with a clear timeout and failure diagnostics.
6. Update local teardown to remove connector state when appropriate.
7. Add script tests or integration tests for flag behavior and registration sequencing.
8. Document how production-like automation repeats the one-shot workflow for each explicitly listed target.

## Out of Scope

- Publishing production deployment automation for managed Kafka providers.
- Replacing the Kafka Connect image.
- Defining the projector's CDC health semantics.
- Runtime discovery, addition, removal, or automatic replacement of CDC data stores.
