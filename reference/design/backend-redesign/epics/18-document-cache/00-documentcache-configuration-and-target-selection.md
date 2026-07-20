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

Implement the configuration surface and startup calculation of the effective projection
target set.

## Dependencies

- Informs CDC stories 17-00 and 17-03; does not depend on connector implementation.

## Deliverables

1. Define and bind strongly typed standalone projection, read acceleration, and CDC
   target options.
2. Derive the de-duplicated startup target set and selection reasons.
3. Create data-store-specific execution inputs without request/JWT inference.
4. Add supported appsettings examples that link to the authoritative semantics.

## Acceptance Evidence

- Tests cover each selector, all-disabled behavior, additive overlap, normalized duplicate
  targets, unavailable stores, and per-store isolation.
- Tests distinguish process-wide projection selectors from explicit CDC target selection.
- Health/routing integration proves configuration errors remain data-store-specific and
  observational.

## Out of Scope

- Projector worker implementation.
- CDC physical source binding or connector registration.
- Kafka topic/message design.
