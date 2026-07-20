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
  - Kafka CDC enablement.
- Startup validation rejects `KafkaCdc:Enabled = true` unless projector mode is `CdcRequired`.
- Startup validation allows `ReadAcceleration:Enabled = true` with projector mode `Async` or `CdcRequired`.
- Startup validation allows projector mode `Async` with read acceleration disabled for indexing/integration use
  cases.
- Startup validation allows ordinary DMS operation with projector mode `Disabled` when Kafka CDC and
  read acceleration are disabled.
- Kafka UI or Kafka infrastructure flags do not imply Kafka CDC enablement.
- The v1 options are process-wide: projector mode and Kafka CDC requirements apply to every loaded data store
  with a usable connection string; per-data-store CMS overrides are not silently inferred.
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
3. Add tests for valid and invalid configuration combinations.
4. Add startup diagnostics for invalid CDC/read-cache combinations.
5. Add tests that process-wide modes enumerate all loaded tenant/data-store configurations and isolate
   per-data-store failures.
6. Update local appsettings examples or design docs with the configuration matrix.

## Out of Scope

- Implementing the projector worker.
- Registering Kafka connectors.
- Defining Kafka topic or message shape.
