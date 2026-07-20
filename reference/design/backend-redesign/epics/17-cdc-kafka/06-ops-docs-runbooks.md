---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add CDC Setup, Monitoring, Recovery, and Security Runbooks

## Design References

- [Authoritative relational CDC design](../../../cdc-streaming.md)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Publish operator guidance for the implemented local and production-like relational CDC
capability without redefining its architecture or contracts.

## Deliverables

1. Document local opt-in, connector registration, topic discovery, Kafka UI use, and
   troubleshooting commands.
2. Document PostgreSQL and SQL Server prerequisites, least-privilege access, provider
   artifacts, retention, restart, and cleanup.
3. Document Kafka topic/ACL/consumer operation, DMS per-database projection-health
   observation, and deployment-owned combined readiness.
4. Document connector restart, offset reset, resnapshot, topic recreation, cache rebuild,
   target migration/retirement, and explicit destructive cleanup.
5. Document binding-state location, backup, normal-stop retention, fail-closed missing
   state, explicit adoption, cleanup ordering, target/source mismatch diagnosis, and
   new-generation migration. Never instruct operators to rewrite a binding in place.
6. Document the immutable-contract cutover: freeze old-contract publication, deploy a
   new topic/`contractVersion`, discard and completely reproject cache state, take a fresh
   connector snapshot, bootstrap consumers in the new state namespace, and explicitly
   retain or retire the frozen old topic.
7. Cross-link the canonical design and both ADRs instead of repeating their normative
   tables or algorithms.

## Acceptance Evidence

- Instructions are verified against the implemented scripts, templates, and status
  output for both providers.
- Troubleshooting covers persistent projection failure, provider key/filter/order
  failure, missing target, source-resolution failure, binding mismatch, and governed
  artifacts without binding state.
- Destructive or replay-producing operations are clearly marked and never inferred from
  configuration removal.
- Local teardown instructions distinguish ordinary stop from destructive volume removal
  and remove governed artifacts before their JSON binding records.
- The cutover procedure never advances canonical `ContentVersion`, republishes an
  output-changing ETag into the old topic, or claims simultaneous old/new publication
  from the single cache row.
- Documentation distinguishes CDC from Change Queries and from response serialization.

## Out of Scope

- Cloud-provider-specific managed Kafka instructions.
- SLA/SLO commitments.
- Consumer implementation guidance beyond the public contract.
