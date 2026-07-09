---
jira: DMS-982
jira_url: https://edfi.atlassian.net/browse/DMS-982
---

# Story: Bulk Reference and Descriptor Resolution (Write-Time Validation)

## Description

Implement request-scoped resolution and validation for all extracted references during POST/PUT:

- Deduplicate referential ids across the request.
- Resolve `ReferentialId → DocumentId` in bulk via `dms.ReferentialIdentity`.
- Preserve eligible ordinary lookup misses with their exact binding and concrete occurrence so the later transactional
  identity-propagation stage can apply a compiled `SameStatementReferenceResolutionPlan`. This story defines the resolver
  handoff and certified override contract; E09-S03 owns current-row locking, correlation, and post-write verification.
- Resolve every internal identity-lineage anchor required by the selected write plan. An anchor is the `DocumentId` of
  the source reference that supplies a reference-backed identity component; it may reuse an explicitly resolved sibling
  reference or require a projection from the resolved target row.
- Validate descriptors via `dms.Descriptor` (and expected discriminator/type in application code where required).
- Provide actionable error reporting that includes the reference’s concrete JSON location.

Align with `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (“Reference Validation”).

## Acceptance Criteria

- Ordinary referential-id and lineage lookups are performed in bulk with no query per reference instance. The handoff for
  eligible misses preserves enough binding/occurrence information for E09-S03 to batch certified work by exact plan.
- Missing referenced documents cause an error identifying the concrete JSON path unless the caller explicitly enables
  the certified-miss handoff and the binding policy permits the later transactional stage to evaluate that instance.
- Descriptor validation ensures the referenced `DocumentId` is present in `dms.Descriptor` (and optionally matches the expected discriminator).
- Resolution uses per-request memoization so duplicates do not cause duplicate work.
- `ResolvedReferenceSet` supplies target `DocumentId` values plus a map keyed by
  `(referenced DocumentId, IdentityLineageId)` for demanded lineage anchors. The binding and concrete ordinal path first
  resolve the target row; repeated collection occurrences that resolve to the same row reuse the same anchor entry.
- Certified future-target results are instance-scoped overrides keyed by binding and concrete ordinal path. Each retains
  its site/origin/case, submitted future referential id, stable target id, and predicted complete vector; it never enters
  the normal referential-id/target-lineage maps or a cache.
- Anchor projection is compiled globally by E15-S06 from the finalized target model and stored in `MappingSet`/mapping packs. After
  target resolution, required plans execute set-wise by target table/variant in bounded projection batches. They never
  introduce per-reference-instance or per-collection-row queries.
- Normal lookup always wins for a true retarget. This story does not read current rows, reuse a stored id, synthesize a
  future resolution, or execute post-write verification. Its unresolved-instance handoff is consumed only by E09-S03;
  absent/new/ordinary invalid references retain the existing fail-closed response.

## Authorization Batching Consideration

Authorization is out of scope for this story, but orchestration must preserve two ordered hooks: minimal stored-value
authorization runs before request-reference validation or certified correlation, and proposed-value authorization runs
after the final resolved/profile-aware merge. Provider commands may share a transport batch only when execution remains
short-circuited at the stored gate on denial; batching must not evaluate request-dependent work early. See
`reference/design/backend-redesign/design-docs/auth.md`.

## Tasks

1. Implement a request-scoped resolver that accepts extracted references and compiled lineage requirements and returns
   target `DocumentId`s plus required source-reference anchor `DocumentId`s.
2. Implement dialect-specific bulk lookup patterns (IN/TVP/array) with parameter-limit handling.
3. Execute the E15-S06 set-based intrinsic-lineage projections for demanded `(target, AnchorSetId)` variants and populate
   `ResolvedReferenceSet.LineageAnchorDocumentIdByTargetAndLineage` after target resolution.
4. Define the resolver handoff and `ResolvedReferenceSet` certified-override shape used by E09-S03, while keeping this
   story's implementation limited to ordinary lookup/projection and explicit unresolved-instance classification.
5. Implement descriptor validation queries and discriminator checks.
6. Add unit/integration tests covering:
   1. dedupe behavior,
   2. missing reference failure with path,
   3. descriptor type mismatch failure,
   4. reuse of an equivalent explicit sibling `..._DocumentId` anchor, and
   5. batched target-row projection for an inherited anchor that is not explicit at the local reference site,
   6. two collection occurrences with one binding but different ordinal paths resolve different target/anchor pairs, and
   7. duplicate target rows across occurrences reuse one projected anchor lookup with no N+1 query, and
   8. an eligible policy-marked miss is handed off with its exact binding/concrete occurrence while an ordinary typo or
      ineligible binding fails with the normal path-specific error.
   End-to-end CycleB, locking, correlation, and post-write verification tests belong to E09-S03. POST remains on normal
   identity-based upsert/create resolution and cannot use a future identity to locate an existing subject.
