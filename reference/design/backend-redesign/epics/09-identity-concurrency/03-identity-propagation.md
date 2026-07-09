---
jira: DMS-999
jira_url: https://edfi.atlassian.net/browse/DMS-999
---

# Story: Identity Propagation via Cascades (No Application Closure Traversal)

## Description

Implement strict identity maintenance for identity updates without application-managed identity closure traversal:

- Persist identity-component referenced identity values as columns and enforce full-composite FKs. Inventory intrinsic
  target lineages, derive least-fixed-point receiver-demanded anchors, and form physical FK
  candidates only after canonical storage mapping and
  de-duplication. PostgreSQL directly receives fixed actions without DMS classification. SQL Server alone derives
  statement-scoped value-flow proofs and globally selects actions that satisfy both value-flow safety and error 1785,
  including safely breakable cycles (see `design-docs/mssql-cascading.md`).
- Use per-resource triggers to recompute `dms.ReferentialIdentity` row-locally when identity projection values change (including changes caused by cascaded updates to identity-component propagated identity columns).
- Execute certified same-statement stable-target resolution inside the write transaction for eligible
  PUT-by-`DocumentUuid` misses. This story owns current-row correlation/locking, instance-scoped overrides, exact
  persisted-row locator handoff to merge execution, and cache-bypassing post-write verification; E07-S01 remains the
  ordinary bulk resolver and E15-S04/S04b own plan compilation.

Align with:
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`
- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/mssql-cascading.md` (shared full-composite FK/anchor derivation + SQL
  Server-only value-flow/action selection)

## Acceptance Criteria

- After commit, there is no window where `dms.ReferentialIdentity` is stale for any impacted document.
- Identity updates propagate transitively via native cascades, without application traversal or an identity-value
  propagation trigger.
- Every public identity component, reference-backed component retargeting, and simultaneous component change is supported
  for accepted v1 models. Each target intrinsically stores its reference-backed lineage values; each incoming site's
  anchor demand begins empty and gains only receiver validity/correlation requirements.
- Demands propagate only through downstream identity/constraint consumers. Equal demanded subsets share deterministic
  propagation-key/`RefKey` variants keyed by `AnchorSetId`; each reference emits one full FK and hard provider key limits
  are validated. Omission requires universal no-demand proof, not sampled mutation behavior.
- PostgreSQL is never pruned, topology-classified, or failed by DMS because of cascade topology; it uses fixed
  full-composite actions.
- SQL Server safely breaks cycles and duplicate paths when changed-target and receiver-carrier proofs cover every mutation
  case. A carrier may be the zero-hop initiating write.
- SQL Server infeasibility or deterministic work-limit exhaustion throws `RelationalModelDerivationException` with
  ordered structured errors distinct from the success artifact/manifest.
- Certified runtime resolution runs only after minimal stored-value authorization and full current-state loading. Normal
  lookup always wins; eligible misses are batched by exact plan with typed JSON-recordset inputs containing origin,
  materialized occurrence values, and submitted public identity values. Correlation locks and returns the exact receiver
  locator, stored target id, and unchanged target values; merge execution reasserts that locator. Post-write verification
  proves the future referential id, same target id, and demanded anchors before commit. POST/create/new/absent/deferred-
  recursive/ambiguous/stale cases fail closed without a synthesized cache entry.
- Integration tests demonstrate:
  - an upstream identity change causes dependent referential identities to update in the same transaction,
  - a reference-backed identity component can retarget (for example Session School A to School B), with
    `CourseOffering -> Session` carrying the School anchor because its receiver is also constrained by
    `CourseOffering -> School`, while an unrelated Session referrer remains on the empty-demand variant,
  - all identity components can change in one write, and
  - an accepted anchor-bearing `CycleA`/`CycleB` provider fixture on both dialects retargets two independently replaceable
    reference-backed identity lineages together, then retargets both while changing a primitive identity component. SQL
    Server asserts the safe cycle break's combined mutation case and complete certificate-composition proof; PostgreSQL
    uses its fixed full-cascade cycle without classification. PUT-by-`DocumentUuid` runs through the DMS API path with
    A's future referential id absent before the statement, selects an exact certified same-statement plan, preserves A's
    stable target `DocumentId`, and verifies the future id/full anchor vector before commit.
  - a retained acyclic `R -> T -> RChild` provider fixture correlates multiple existing child occurrences by complete
    persisted occurrence identity, including an ordinary materialized component and a same-deferred-site correlated
    target `DocumentId`, and reasserts each returned `CollectionItemId` during merge with bounded set-wise commands.

## Tasks

1. Emit/validate DDL for identity-component propagation:
   - inventory/store each target's intrinsic reference-backed identity lineages and initialize each incoming site's
     demand set empty,
   - add an anchor only for receiver full-FK validity/correlation, propagate demand only through downstream
     identity/constraint consumers to a least fixed point, and reuse an existing local `..._DocumentId` only when
     complete identity equivalence and presence are proved; otherwise add a dedicated stored local anchor,
   - group equal demanded subsets into propagation-key/`RefKey` variants using a stable `AnchorSetId`, and
     fail deterministically when the minimal required key exceeds a hard provider limit,
   - map logical references to canonical storage columns and de-duplicate by physical FK identity before assigning
     actions; physical identity excludes action/mode,
   - keep public identity values, ordered site-demanded anchors, and target `DocumentId` in every selected full
     propagation vector; omit an intrinsic target anchor only with universal no-demand proof across every mutation case,
   - assign PostgreSQL's fixed `CASCADE`/`NO ACTION` actions without DMS value-flow validation,
   - on SQL Server, derive typed mutation routes/proofs and globally select `NativeCascade`, `NoPropagation`, or
     `ImmutableNoAction` so the final action graph is legal under error 1785 and satisfies every value-flow obligation;
     SCC/cycle edges are decision variables, not automatic failures,
   - attach an ordered `CoverageCertificates` list to each success-only `NoPropagation` decision keyed by physical FK id
     and selected `AnchorSetId`, covering every complete `MutationCaseId`. Each certificate contains a changed-target
     route, receiver-carrier route (`OriginWrite` or native FK hops), complete selected-vector equality, separate
     origin/receiver row correlation, presence implication, and constraint timing. Primitive proofs may be reused only
     through typed `SubsetCompositionProof`; missing composition fails as `UnprovedSubsetComposition`,
   - retain the full composite key on every engine; there is no `DocumentId`-only shape or identity-value propagation
     trigger.
2. Implement transactional certified resolution for plan-eligible PUT misses: stage only non-deferred materialized write
   bindings after ordinary resolution, batch canonical correlation commands, select the exact origin/case, build the full
   future vector from proved changed origins/locked unchanged target columns/stored terminal id, carry the returned stable
   receiver locator through merge binding, and batch cache-bypassing post-write verification before commit.
3. Emit per-resource triggers to maintain `dms.ReferentialIdentity` transactionally on identity projection changes, recomputing `ReferentialId` using the engine UUIDv5 helper (`E02-S06`).
4. Integrate identity-stamp behavior (`IdentityVersion/IdentityLastModifiedAt`) with trigger maintenance.
5. Add integration tests for a small identity dependency chain scenario plus the direct CycleB and retained acyclic
   collection API cases on both providers, including fan-out disambiguation and every negative fail-closed branch.
6. Share a versioned conformance corpus with MetaEd (METAED-1667), including identity-lineage anchors, shared-column
   independent parents, optional co-presence, row correlation, abstract-identity trigger boundaries, safely breakable
   cycles, zero-hop carriers, and SQL Server 1785 cases. Every fixture has separate `metaEd`, `dmsPostgresql`, and
   `dmsSqlServer` expected outcomes; only SQL Server outcomes contain physical decisions/certificates.
7. Keep cross-table/root-to-child equality propagation out of this story; it is separate future work and cannot satisfy a
   reference-FK coverage obligation.
8. Test deterministic solver bounds with an adversarial graph and distinguish
   `CascadeClassificationComplexityExceeded` from `NoSafeSqlServerAssignment`.
9. Keep `RelationalMappingVersion = v1`; do not add migration, legacy-pack compatibility, or physical-model-hash work.
