---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Configuration and Target Selection

## Design References

- [Configuration and projection target selection](../../../cdc-streaming.md#configuration-and-projection-target-selection)

## Outcome

Implement the single explicit DMS projection-target configuration surface and the
target-resolution lifecycle.

## Dependencies

- May be implemented alongside 18-00 and informs 18-04 through 18-06 and CDC stories
  19-00 and 19-04; it does not depend on connector implementation or deployment-owned CDC
  binding state.

## Deliverables

1. Define and bind strongly typed `DataManagement:DocumentCache:Targets` entries and the
   independent `ReadAcceleration:Enabled` use-path gate.
   Treat the list as process-local configuration so deployments designate projector hosts
   through target placement rather than another enablement flag.
2. Validate normalized uniqueness and create one logical execution context for every
   explicit startup target without selecting every loaded data store.
3. Keep unresolved configured targets visible and retry their CMS resolution on the
   bounded supervisor interval or after CMS refresh; never discover unlisted targets.
4. Replace a resolved target's execution context and reset its health evidence when CMS
   supplies replacement connection metadata. Make the new context observable to 18-06
   without classifying old/new source identity, comparing a CDC binding, or changing
   Kafka artifacts.
5. For each resolved SQL Server target, read
   `sys.databases.is_read_committed_snapshot_on` and make `READ_COMMITTED_SNAPSHOT ON` a
   fail-closed projection/cache-use prerequisite. Supply a reusable same-open-connection
   validation step for 18-04 and 18-05 so a new comparison operation cannot rely only on a
   stale startup observation. A false or unreadable result leaves the target visible but
   ineligible and unhealthy, starts no projector or cache use, and is retried on the bounded
   supervisor lifecycle. Do not alter the database option, fail canonical relational API
   traffic, or impose the prerequisite on unlisted SQL Server data stores or PostgreSQL.
6. Create data-store-specific execution inputs without request/JWT inference.
7. Add supported appsettings examples that link to the authoritative semantics.

## Acceptance Evidence

- Tests cover empty configuration, normalized duplicate targets, unavailable stores,
  late resolution of an already-listed tenant/data store, connection-context
  replacement with health reset, unlisted late-created stores, and per-store isolation.
- Tests prove read acceleration selects no additional data stores and that adding or
  removing membership requires configuration rollout.
- Tests prove an empty per-process target list starts no projector work and duplicate
  target placement across processes remains a supported deployment choice.
- SQL Server tests cover RCSI enabled, disabled, unreadable, enabled after retry, and a
  replacement connection context with a different result. They prove an ineligible target
  starts no projection or cache use while canonical API traffic and eligible peer targets
  continue; PostgreSQL and unlisted SQL Server stores have no RCSI prerequisite.
- Health/routing integration proves configuration errors remain data-store-specific and
  observational.

## Out of Scope

- Projector worker implementation.
- CDC target selection, durable physical-source binding, connector registration, or
  combined CDC readiness.
- Kafka topic/message design.
