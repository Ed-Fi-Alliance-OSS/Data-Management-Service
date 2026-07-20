---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Configuration Boundaries

## Description

Add the runtime configuration surface that separates optional asynchronous projection,
cache-backed reads, and Kafka CDC enablement.

`dms.DocumentCache` is optional for ordinary API correctness and conditionally required
for Kafka document upserts. The projector has one enabled behavior, `Async`; CDC does not
add a special projector mode or delete-path behavior.

## Dependencies

- Informs `17-cdc-kafka/00-documentcache-cdc-prerequisites.md`.
- Informs `17-cdc-kafka/03-bootstrap-enable-kafka-cdc.md`.
- Does not depend on Kafka connector work.

## Acceptance Criteria

- Configuration exposes separate settings for:
  - projector mode: `Disabled` or `Async`,
  - read acceleration enablement,
  - Kafka CDC enablement.
- Startup validation rejects `KafkaCdc:Enabled = true` unless projector mode is `Async`.
- Startup validation allows `ReadAcceleration:Enabled = true` only with projector mode
  `Async`.
- Startup validation allows projector mode `Async` with read acceleration disabled for
  indexing/integration/CDC use cases.
- Startup validation allows ordinary DMS operation with projector mode `Disabled` when
  Kafka CDC and read acceleration are disabled.
- Kafka UI or Kafka infrastructure flags do not imply Kafka CDC enablement.
- The v1 projector mode remains process-wide for loaded data stores with usable connection
  strings. The projector does not infer targets from HTTP requests, JWT `DataStoreIds`,
  or route qualifiers.
- Kafka CDC's explicit target list, physical source binding, provider key setup,
  connector registration, and readiness live in the CDC/Kafka epic rather than creating
  more DocumentCache projector modes.
- Projector or CDC readiness failures never alter `IDataStoreSelection` and never block
  API reads or mutations. In particular, cache health cannot block API deletion.
- Configuration or health failures for one data store are reported against that data
  store and do not make ordinary API correctness for unrelated data stores depend on
  `dms.DocumentCache`.
- Documentation or appsettings examples show the supported combinations and semantics.

## Tasks

1. Define strongly typed configuration options for DocumentCache projector mode, read
   acceleration, and Kafka CDC.
2. Bind and validate the options during DMS startup.
3. Add tests for valid and invalid configuration combinations.
4. Add tests that process-wide asynchronous projection enumerates loaded
   tenant/data-store configurations and isolates per-data-store failures.
5. Update local appsettings examples and design docs with the simplified configuration
   matrix.

## Out of Scope

- Implementing the projector worker.
- Binding CDC target/source identity.
- Registering Kafka connectors.
- Defining Kafka topic or message shape.
