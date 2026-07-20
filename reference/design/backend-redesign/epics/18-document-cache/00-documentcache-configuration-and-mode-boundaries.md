---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Configuration and Mode Boundaries

## Description

Add the runtime configuration surface that distinguishes optional projection, cache-backed reads, and
CDC-required projection.

`dms.DocumentCache` is optional for ordinary API correctness, but conditionally required when relational
Kafka CDC is enabled. A single `DocumentCacheEnabled` setting is not enough because cache-backed reads can
tolerate misses and stale rows while CDC cannot tolerate missing delete source rows.

## Dependencies

- Informs `17-cdc-kafka/00-documentcache-cdc-prerequisites.md`.
- Informs `17-cdc-kafka/03-bootstrap-enable-kafka-cdc.md`.
- Does not depend on Kafka connector work.

## Acceptance Criteria

- Configuration exposes separate settings for:
  - projector mode: `Disabled`, `Async`, or `CdcRequired`,
  - read acceleration enablement,
  - Kafka CDC enablement,
  - an explicit deployment-owned list of CDC targets keyed by `(tenant key, DataStoreId)`,
  - a default-off `BlockMutationsWhenNotReady` host policy.
- Startup validation rejects `KafkaCdc:Enabled = true` unless projector mode is `CdcRequired`.
- Startup validation requires a non-empty, normalized-unique target list when Kafka CDC is enabled and rejects
  `BlockMutationsWhenNotReady = true` when Kafka CDC is disabled. The target list is bound once per process.
- Startup validation allows `ReadAcceleration:Enabled = true` with projector mode `Async` or `CdcRequired`.
- Startup validation allows projector mode `Async` with read acceleration disabled for indexing/integration use
  cases.
- Startup validation allows ordinary DMS operation with projector mode `Disabled` when Kafka CDC and
  read acceleration are disabled.
- Kafka UI or Kafka infrastructure flags do not imply Kafka CDC enablement.
- The v1 projector mode remains process-wide for loaded data stores with usable connection strings. CDC
  obligations apply only to the explicit target list; per-data-store CMS values and runtime CMS additions do not
  silently opt a data store into CDC.
- Startup validates target-list uniqueness, resolves each listed target, and captures a physical source binding
  from provider-specific database identity. Tenant-key matching is case-insensitive like `IDataStoreProvider`.
  Complete connection configuration is not fingerprinted, and connection values or unsanitized physical
  identifiers are not logged, persisted, or exposed.
- Successful CMS refresh/cache-miss reload results preserve normal request routing. CDC readiness reevaluates
  only listed targets. A missing listed target is not ready; a confirmed provider/physical-database change is
  latched as `CdcSourceDriftRequiresDeployment`; credential, pool, timeout, name, route, and equivalent-alias
  changes that resolve to the same physical source are not drift. An identity-resolution failure is retryable,
  not proof of drift.
- Source readiness and drift never alter `IDataStoreSelection` or block API requests by default. CMS entries
  outside the target list are irrelevant to CDC readiness. Drift never causes automatic projector/connector
  changes or destructive cleanup.
- With `BlockMutationsWhenNotReady = true`, and only with that explicit opt-in, mutations to a configured CDC
  target return `503` while its end-to-end readiness is false. `GET`, `HEAD`, `OPTIONS`, Change Queries, and other
  read-only requests are never blocked by this policy.
- Confirmed physical-source drift remains latched until a coordinated deployment reruns physical-source
  validation, provisioning, connector registration, and DMS startup; restoring the prior CMS value does not
  clear it.
- Configuration validation and diagnostics enumerate `(tenant key, DataStoreId)` targets without depending on
  an HTTP request, JWT `DataStoreIds`, or route qualifiers.
- A configuration or readiness failure for one data store is reported against that data store and does not make
  ordinary API correctness for unrelated data stores depend on `dms.DocumentCache`.
- Configuration failures clearly distinguish unsupported combinations from missing database objects or unhealthy
  projector state.
- Documentation or appsettings examples show the supported combinations and their semantics.

## Tasks

1. Define strongly typed configuration options for DocumentCache projector mode, read acceleration, and Kafka CDC.
2. Bind and validate the options during DMS startup.
3. Bind and validate the explicit CDC target list and resolve provider-specific physical source identities.
4. Compare configured targets after successful CMS refresh/reload for readiness without changing request routing.
5. Add tests for valid and invalid configuration combinations.
6. Add startup and runtime diagnostics for invalid CDC/read-cache combinations, missing targets, identity
   resolution failures, and physical-source drift.
7. Add tests that process-wide modes enumerate all loaded tenant/data-store configurations and isolate
   per-data-store failures.
8. Add tests for non-target additions, missing targets, provider/physical-source changes, same-source credential
   and connection-setting changes, and route-only refreshes. Verify default routing remains unchanged, drift is
   latched, peers are isolated, optional blocking applies only to mutations, and GETs are never blocked.
9. Update local appsettings examples or design docs with the configuration matrix.

## Out of Scope

- Implementing the projector worker.
- Registering Kafka connectors.
- Defining Kafka topic or message shape.
