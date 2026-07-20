---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Explicit Local/Bootstrap Connector Registration

## Design References

- [Enablement and initial readiness sequence](../../../cdc-streaming.md#enablement-and-initial-readiness-sequence)
- [Local bootstrap and CI](../../../cdc-streaming.md#local-bootstrap-and-ci)
- [Physical source binding](../../../cdc-streaming.md#cdc-target-and-physical-source-binding)

## Outcome

Add explicit, idempotent local connector registration for a selected configured target,
and document how production-like deployment repeats the same one-shot workflow.

## Dependencies

- Depends on 17-00 through 17-02 and the projection/readiness inputs named by 17-00.

## Deliverables

1. Add `-EnableKafkaCdc` to the appropriate local/bootstrap entry points while retaining
   Kafka UI as an independent option.
2. Reuse bootstrap data-store selection and generated provider connector templates.
3. Implement idempotent Kafka Connect create/update, status polling, timeout, and
   condition-specific diagnostics.
4. Print sanitized connector/source/topic identity and integrate local teardown.
5. Expose the same workflow to E2E setup before observed test traffic begins.

## Acceptance Evidence

- Script/integration tests cover disabled, UI-only, invalid target, successful, repeated,
  timeout, and teardown flows.
- Sequence tests prove registration and readiness follow the authoritative algorithm and
  do not depend on a backfill epoch or high-watermark.
- Production-like validation rejects unsafe topic-prefix use and source/topic reuse.
- Diagnostics identify infrastructure, REST, provider setup, projector completeness,
  connector catch-up, target/source, and drift failures without secrets.

## Out of Scope

- Managed-Kafka-provider deployment automation.
- Runtime target discovery, retirement, or source replacement.
- Projector health semantics.
