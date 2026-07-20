---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Configuration and Target Selection

## Description

Add the runtime configuration surface that separates optional asynchronous projection,
cache-backed reads, and per-data-store Kafka CDC targets.

`dms.DocumentCache` is optional for ordinary API correctness and conditionally required
for Kafka document upserts. Projector execution is derived from the capabilities that
need it; there is no projector mode or process-wide Kafka CDC enablement flag.

## Dependencies

- Informs `17-cdc-kafka/00-documentcache-cdc-prerequisites.md`.
- Informs `17-cdc-kafka/03-bootstrap-enable-kafka-cdc.md`.
- Does not depend on Kafka connector work.

## Acceptance Criteria

- Configuration exposes separate settings for:
  - standalone projection/indexing: `DocumentCache:Enabled`,
  - read acceleration enablement,
  - `KafkaCdc:Targets`, with no separate Kafka CDC enabled flag.
- `DocumentCache:Enabled = true` selects every loaded data store with a usable connection
  string for standalone asynchronous projection.
- `ReadAcceleration:Enabled = true` selects every loaded data store with a usable
  connection string for asynchronous projection even when `DocumentCache:Enabled` is
  false.
- Every `KafkaCdc:Targets` entry selects exactly that data store for asynchronous
  projection even when both process-wide settings are false.
- The effective projection target set is the de-duplicated union of the targets selected
  by those three capabilities, and only one logical loop runs per selected data store.
- Ordinary DMS operation starts no reconciliation loops when `DocumentCache:Enabled` and
  `ReadAcceleration:Enabled` are false and `KafkaCdc:Targets` is empty.
- Kafka target entries are unique after tenant-key normalization. An empty list means
  Kafka CDC is disabled; there is no boolean/list combination to validate.
- All combinations of the three selectors are valid and additive; defaults are false,
  false, and an empty target list.
- Kafka UI or Kafka infrastructure flags do not add Kafka CDC targets.
- The projector does not infer targets from HTTP requests, JWT `DataStoreIds`, route
  qualifiers, or unlisted CMS inventory.
- Kafka CDC's explicit target list, physical source binding, provider key setup,
  connector registration, and readiness live in the CDC/Kafka epic.
- Projector or CDC readiness failures never alter `IDataStoreSelection` and never block
  API reads or mutations. In particular, cache health cannot block API deletion.
- Configuration or health failures for one data store are reported against that data
  store and do not make ordinary API correctness for unrelated data stores depend on
  `dms.DocumentCache`.
- Documentation or appsettings examples show the supported combinations and semantics.

## Tasks

1. Define strongly typed configuration options for standalone DocumentCache projection,
   read acceleration, and Kafka CDC targets.
2. Bind the options and derive the de-duplicated effective projection target set during
   DMS startup.
3. Add tests for each selection source, all-disabled behavior, additive overlap, and
   normalized duplicate Kafka targets.
4. Add tests that process-wide standalone/read projection enumerates loaded data stores,
   target-only CDC projection excludes unlisted stores, and per-data-store failures are
   isolated.
5. Update local appsettings examples and design docs with the simplified configuration
   contract.

## Out of Scope

- Implementing the projector worker.
- Binding CDC target/source identity.
- Registering Kafka connectors.
- Defining Kafka topic or message shape.
