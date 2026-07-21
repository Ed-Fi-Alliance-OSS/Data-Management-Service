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

- Informs CDC stories 17-00 and 17-03; does not depend on connector implementation or
  deployment-owned CDC binding state.

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
   supplies replacement connection metadata. Make the new context observable to 18-09
   without classifying old/new source identity, comparing a CDC binding, or changing
   Kafka artifacts.
5. Create data-store-specific execution inputs without request/JWT inference.
6. Add supported appsettings examples that link to the authoritative semantics.

## Acceptance Evidence

- Tests cover empty configuration, normalized duplicate targets, unavailable stores,
  late resolution of an already-listed tenant/data store, connection-context
  replacement with health reset, unlisted late-created stores, and per-store isolation.
- Tests prove read acceleration selects no additional data stores and that adding or
  removing membership requires configuration rollout.
- Tests prove an empty per-process target list starts no projector work and duplicate
  target placement across processes remains a supported deployment choice.
- Health/routing integration proves configuration errors remain data-store-specific and
  observational.

## Out of Scope

- Projector worker implementation.
- CDC target selection, durable physical-source binding, connector registration, or
  combined CDC readiness.
- Kafka topic/message design.
