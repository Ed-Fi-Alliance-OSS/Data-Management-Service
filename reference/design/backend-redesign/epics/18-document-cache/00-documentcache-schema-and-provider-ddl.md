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
  enqueue trigger, with provider-equivalent least-privilege execution. SQL Server
  `*_Stamp` triggers do not inspect the server-level nested-trigger setting.
  Reassess/remove the source-scan index.
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
- Access tests prove canonical writers enqueue through triggers but cannot directly mutate
  work, projector writers can acknowledge, the administrative context can perform only
  the owned lifecycle/baseline/repair DML, CDC principals cannot capture work, and reruns
  preserve lifecycle, latch, cache, and pending work.
- The test and documentation changes identify the design sections they verify rather than
  reproducing their tables or rules here.

## Not Assigned to This Story

- Runtime projection and reads are assigned to later E18 stories.
- Provider capture objects, connectors, topics, and message shaping are assigned to E19.

## Clarifying Questions and Answers

### Questions 1

1. What is the production database-principal model for the canonical writer, projector,
   projection administrator, CDC reader, and PostgreSQL non-login enqueue owner: separate
   credentials, per-connection role switching, or another mechanism? Specify the stable
   role/user names, whether `ddl emit`/`ddl provision` creates them or receives existing
   names, and which later story wires each runtime context.
2. Which SQL Server enqueue-trigger execution model is normative: same-owner ownership
   chaining or a narrowly scoped `EXECUTE AS` principal? What exact owner/user and
   ownership/grant inventory must deterministic snapshots, manifests, and introspection
   assert?
3. Should the final desired inventory remove the existing
   `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt` index and prohibit any
   `dms.Document(ContentVersion, DocumentId)` projector-discovery index, leaving queue
   discovery solely on
   `IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId`?
4. The general DDL design permits existence-check recovery from partial runs, while the
   CDC integration design says legacy or missing E18 inventory is ineligible and must not
   be repaired in place. For both standalone emitted SQL and `ddl provision`, which
   missing or mismatched E18 states must fail before mutation, and which, if any, may be
   completed as a recoverable initial partial apply?
5. Does this story's requirement that CDC principals cannot capture work mean only that
   ordinary E18 DDL emits no work-table access for such principals, with publication/CDC
   capture exclusion owned and tested by 19-01, or must 18-00 add provider-capture
   prevention and tests despite the stated E19 boundary?
6. Do enqueue failures need stable provider-specific identifiers, such as a PostgreSQL
   SQLSTATE and SQL Server error number, for missing/unreadable lifecycle state and other
   enqueue failures, or is generic statement failure plus rollback sufficient until a
   later diagnostics story?

### Answers 1

1. Keep the existing single DMS data-store credential for canonical writes, projection,
   and projection administration. These are capabilities inside one trusted DMS process,
   not separate production database identities in v1; do not add connection strings or
   per-connection role switching. `ddl emit`/`ddl provision` creates only the
   PostgreSQL `NOLOGIN` `edfi_dms_enqueue_owner` required by the hardened
   `SECURITY DEFINER` enqueue functions. Deployment continues to supply the DMS login,
   and 19-01 owns the separate deployment-supplied CDC login and its grants. Later E18
   stories reuse the resolved DMS connection and do not add another principal model.
   Provider fixtures may use restricted test principals to prove trigger encapsulation,
   but this story must not turn those fixtures into production credential provisioning.
2. Use SQL Server's same-owner ownership chain. Keep the trigger and the three referenced
   `dms` tables under the existing `dms` schema owner, use only static schema-qualified
   references, and do not add `EXECUTE AS` or an enqueue user. Snapshots and introspection
   need to prove the trigger definition and absence of `EXECUTE AS`; functional access
   tests prove that a restricted writer can fire it without direct work-table DML.
   Deployment-specific owner names and a general owner/grant catalog do not belong in the
   manifest.
3. Yes. Remove
   `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt` and do not add a
   `dms.Document(ContentVersion, DocumentId)` index. Ordinary discovery uses only
   `IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId`; explicit baseline, rebuild, and
   scrub operations scan `dms.Document` in primary-key order.
4. Follow the existing create-only provisioning contract rather than adding an E18 drift
   reconciler. Do not bump `RelationalMappingVersion` from `v2` to `v3`; `v2` was just
   introduced and is the correct mapping version for the next DMS release. A new apply or
   same-hash rerun may create missing objects and refresh
   replaceable generated functions/triggers using the existing existence-check patterns.
   An incompatible existing table, column, or constraint fails the transaction instead of
   being altered. `ddl provision` retains its existing preflight failures for a different
   schema hash or an existing `EffectiveSchema` table with no singleton; standalone SQL
   retains its insert-if-missing behavior. No broader object-by-object check must run
   before the first statement because the single transaction already guarantees no
   durable partial mutation. E19 separately requires new-database workflow provenance and
   a complete current E18 inventory before CDC setup; partial completion or a successful
   rerun does not by itself make an older database CDC-eligible.
5. The requirement is limited to the E18 boundary: grant no CDC principal access to
   `DocumentProjectionWork` and emit no capture object for it. Story 19-01 owns and tests
   publication/capture exclusion, CDC-reader grants, and provider metadata.
6. Generic provider failure plus complete rollback is sufficient. Give an absent or
   invalid lifecycle singleton a clear deterministic message, but do not make its
   PostgreSQL SQLSTATE or SQL Server error number a public contract. Let permission,
   constraint, deadlock, timeout, and storage errors retain their native identifiers;
   tests assert the useful lifecycle diagnostic and rollback, not a new error taxonomy.
