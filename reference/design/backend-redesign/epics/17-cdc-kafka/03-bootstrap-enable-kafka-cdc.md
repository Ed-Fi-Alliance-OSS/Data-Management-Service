---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Explicit Local/Bootstrap Connector Registration

## Design References

- [Enablement and initial readiness sequence](../../../cdc-streaming.md#enablement-and-initial-readiness-sequence)
- [Local bootstrap and CI](../../../cdc-streaming.md#local-bootstrap-and-ci)
- [Deployment-owned physical source binding](../../../cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)

## Outcome

Add explicit, idempotent local topic/ACL provisioning and connector registration for a
selected configured target, using deployment-owned binding state, and document how
production-like deployment repeats the same one-shot workflow with its durable state
backend.

## Dependencies

- Depends on 17-00 through 17-02, the published transform from 17-02a, and the
  projection/readiness inputs named by 17-00.

## Deliverables

1. Add `-EnableKafkaCdc` and optional `-CdcBindingStatePath` to the appropriate
   local/bootstrap entry points while retaining Kafka UI as an independent option.
2. Reuse bootstrap data-store selection and generated provider connector templates.
3. Require the selected deployment target to be present in DMS
   `DocumentCache:Targets`, and reserve or exact-match its immutable binding before
   creating governed artifacts.
4. Create or validate the topic with exactly `cleanup.policy=compact` and the binding's
   fixed partition count. Reject any cleanup policy that includes `delete`. Provision and
   idempotently validate literal, binding-scoped topic ACLs for the deployment-supplied
   connector and instance consumer principals, plus their required consumer-group ACLs;
   do not emit shared-topic, wildcard-topic, or cross-instance consumer grants.
5. Implement idempotent Kafka Connect create/update, external combined-status polling,
   timeout, and condition-specific diagnostics. ACL verification must complete before
   connector registration and before combined readiness can pass.
6. Print sanitized binding-generation/connector/source/topic identity. Retain binding
   and artifacts on normal stop; remove artifacts before binding state during explicit
   destructive volume teardown.
7. Expose the same workflow to E2E setup before observed test traffic begins.

## Acceptance Evidence

- Script/integration tests cover disabled, UI-only, invalid or unprojected target,
  successful, repeated, timeout, normal-stop retention, missing binding around existing
  artifacts, and destructive teardown flows.
- Sequence tests prove binding reservation precedes artifact creation and that external
  combined readiness follows the authoritative algorithm without a backfill epoch or
  high-watermark.
- Production-like validation rejects unsafe topic-prefix use, immutable binding rewrite,
  in-place topic partition-count changes, time/delete retention on the v1 topic, and
  source/topic-generation reuse.
- Broker-backed integration tests enable Kafka authorization and prove ACL provisioning
  is repeatable, a configured instance consumer can read its own literal topic, and that
  principal is denied when it attempts to read a peer instance topic.
- Diagnostics identify infrastructure, REST, provider setup, projector completeness,
  connector catch-up, target/source binding, ACL authorization, and mismatch failures
  without secrets.

## Out of Scope

- Managed-Kafka-provider deployment automation.
- Runtime target discovery, retirement, or source replacement.
- Projector health semantics.
- In-place source rebinding or topic-generation reuse.
