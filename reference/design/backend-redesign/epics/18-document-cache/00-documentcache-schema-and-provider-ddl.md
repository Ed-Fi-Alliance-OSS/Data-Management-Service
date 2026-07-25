---
jira: DMS-1310
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Finalize DocumentCache Schema and Provider DDL

## Design References

- **Cached document contract**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract
- **Relational data model**: reference/design/backend-redesign/design-docs/data-model.md
- **DDL generation**: reference/design/backend-redesign/design-docs/ddl-generation.md
- **Schema and query integration**: reference/design/cdc-streaming.md#schema-and-query-integration

The referenced design sections define the physical contract and provisioning behavior. This
story is only the work package for implementing them.

## Outcome

Deliver provider-equivalent cache, durable projection-work, lifecycle/safety state, and
transactional enqueue schema consumed by DocumentCache runtime and CDC work.

## Dependencies

- Depends on E02's DDL/provisioning infrastructure and E10's representation stamps.
- Unblocks integrated durable-state validation in 18-01, E18 stories 18-02 through 18-08,
  and the E19 database-source work.

## Implementation Scope

- Update the derived relational model and both provider DDL emitters for the owned data
  model sections.
- Add `DocumentProjectionWork`, its oldest-work index, constrained
  `Disabled`/`Resetting`/`Rebuilding`/`Tracking` lifecycle, and the orthogonal
  `CacheAheadRecoveryRequired` latch.
- Emit two PostgreSQL statement enqueue triggers/functions and one SQL Server set-based
  enqueue trigger, with provider-equivalent least-privilege execution. Add the SQL Server
  enqueue-enabled `*_Stamp` guard for the nested-trigger prerequisite. Reassess/remove the
  source-scan index.
- Integrate the objects with create-only provisioning, DB-apply manifests, and
  introspection.
- Update unit, snapshot, and provider-apply fixtures.
- Update provisioning documentation to link to the owning design sections.

## Acceptance Evidence

- Provider DDL snapshots and introspection tests cover the complete physical inventory
  assigned to this story.
- PostgreSQL and SQL Server DB-apply tests cover provisioning, rerun, constraint, and
  trigger behavior from the design references.
- Lifecycle constraint tests reject casing variants, leading/trailing whitespace, and
  unknown values under representative SQL Server database collations as well as
  PostgreSQL.
- Multi-row insert/update/stamp/restamp fixtures cover every lifecycle state. Forced
  enqueue errors roll back the complete canonical transaction; disabled lifecycle records
  no work; enqueue-enabled states coalesce current requirements; and unchanged
  `ContentVersion` updates leave the work row and both enqueue timestamps unchanged.
- Missing-singleton fixtures prove the enqueue trigger fails the complete canonical
  transaction rather than treating an absent `StateId = 1` row as `Disabled`.
- SQL Server fixtures prove an enqueue-enabled indirect stamp fails the complete canonical
  transaction when nested triggers are disabled, while `Disabled` writes remain
  projection-independent.
- Access tests prove canonical writers enqueue through triggers but cannot directly mutate
  work, projector writers can acknowledge, the administrative context can perform only
  the owned lifecycle/baseline/repair DML, CDC principals cannot capture work, and reruns
  preserve lifecycle, latch, cache, and pending work.
- The test and documentation changes identify the design sections they verify rather than
  reproducing their tables or rules here.

## Not Assigned to This Story

- Runtime projection and reads are assigned to later E18 stories.
- Provider capture objects, connectors, topics, and message shaping are assigned to E19.
