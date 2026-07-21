---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1246
---

# Story: Add Deployment-Owned CDC Binding and Readiness

## Design References

- [Configuration and projection targets](../../../cdc-streaming.md#configuration-and-projection-target-selection)
- [Projection health and deployment-owned CDC readiness](../../../cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness)
- [Provider source-position barrier](../../../cdc-streaming.md#provider-source-position-barrier)
- [Deployment-owned physical source binding](../../../cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Add the deployment-owned durable binding state and status operation that combine DMS
per-database projection health with E17-owned provider, topic, and connector checks.

## Dependencies

- Consumes the explicit target contract from 18-00 and projection health from 18-09.
- Provider-adapter implementation consumes the heartbeat/capture artifacts from 17-01
  and the pinned connector configuration and source-offset shapes from 17-02.
- Supplies binding and readiness behavior to 17-03; does not implement the projector or
  connector REST registration.

## Deliverables

1. Define the deployment CDC target input and require each selected target to be present
   in DMS `DocumentCache:Targets` without adding Kafka-specific runtime DMS options.
2. Obtain the database-owned physical-source fingerprint through the DMS current-source
   observation contract from 18-09; do not normalize or fingerprint connection metadata
   independently. Detect target aliases with the same reported fingerprint that conflict
   with topic-per-instance isolation.
3. Define the versioned immutable binding-record schema, including the positive fixed
   topic partition count and positive signed 32-bit `maxRecordBytes` established from the
   maximum supported link-bearing materialized envelope and its pinned Kafka framing,
   and a state-store abstraction with atomic
   create/compare-and-set behavior. Provide the single-controller local JSON implementation
   under `.cdc-state/bindings`, its Git ignore rule, and optional
   `-CdcBindingStatePath`; do not write binding state into the bootstrap manifest.
4. Enforce fail-closed creation, exact-match retry, artifact-without-state rejection,
   immutable lifetime, cleanup ordering, and new-generation source migration. Define the
   explicit guarded rotation operation for a rollback or restore that replaces an
   existing source; it changes `dms.DataStoreIdentity.SourceIdentity` only as part of
   reserving a new binding generation/topic and is never an ordinary setup retry. A newly
   created independent target restored from a template, clone, or copied backup receives a
   new UUID before binding creation under the provisioning/restore contract.
5. Validate provider tables including the clear `dms.DocumentCacheState` latch, opt-in
   `dms.CdcHeartbeat` singleton, projected `StreamEtag`, keys, replica/capture setup,
   topic, ACL, `maxRecordBytes`, effective broker request/record-batch/replica-fetch
   compatibility, and installed source-operation shaping against the binding before
   registration. This story defines the ACL and size readiness checks; 17-03 owns
   provisioning, idempotent live validation, and broker-backed
   authorization/maximum-record coverage.
6. Implement per-target and deployment aggregate status by combining the binding, DMS
   current-source projection health, including the durable cache-ahead recovery latch,
   connector configuration and task state, snapshot/catch-up, the provider source-position
   barrier, and lag checks. Implement the PostgreSQL adapter by capturing
   `pg_current_wal_lsn()` after the zero-audit health response and comparing its unsigned
   64-bit value with committed Debezium `lsn_proc`. Implement the SQL Server adapter by
   reading `HeartbeatSequence` after that response, locating a later update after-image
   in its CDC capture instance, and comparing its commit/change LSN and event serial with
   committed Debezium `commit_lsn`, `change_lsn`, and `event_serial_no`.
7. Obtain committed source offsets from
   `GET /connectors/{connectorName}/offsets`, select exactly the source partition matching
   the bound database, and fail closed for an unsupported endpoint, snapshot/null or
   malformed position, missing/ambiguous partition, source mismatch, or provider parse
   failure. Connector/task status, Kafka topic offsets, elapsed time, and lag are not
   substitutes. After catch-up, require a second ready DMS health observation for the
   same source fingerprint. A failed task or missing/conflicting
   `errors.tolerance=none` keeps combined readiness false regardless of offset or lag
   observations.
8. Emit sanitized, condition-specific diagnostics without changing DMS request routing.

## Acceptance Evidence

- State tests cover atomic first creation, exact-match retry, immutable mismatch including
  attempted partition-count or `maxRecordBytes` changes, artifacts without state,
  normal-stop retention, destructive cleanup ordering, and generation migration.
- Provider tests cover equivalent physical aliases, conflicting targets, missing or
  malformed `dms.DataStoreIdentity`, transient identity-resolution failure, missing
  targets, guarded identity rotation/new-generation recovery, and confirmed binding
  mismatch without a DMS-owned drift latch.
- Readiness tests cover binding, migration, projection, exact provider position parsing
  and ordering, a barrier captured after the zero audit, committed connector
  snapshot/catch-up, idle-source heartbeat advancement, second projection-health
  observation, cache-ahead latching that remains false-ready after source equality,
  explicit `errors.tolerance=none`, producer/topic/broker size alignment with
  `maxRecordBytes`, failed connector task state despite otherwise acceptable offset/lag
  observations, lag, per-target isolation, and aggregate results. They reject missing,
  malformed, snapshot, wrong-source, and multiple source-offset responses and prove that
  running/lag status cannot pass a connector that is below the barrier.
- API integration tests prove every reported CDC/projector failure remains observational,
  including deletion with unavailable cache state.

## Out of Scope

- Projector implementation.
- Kafka Connect REST registration.
- Publishing Kafka records.
- A new production state service; production integrations adapt the existing deployment
  state backend.
- In-place rebinding or topic reuse for a different physical source.
