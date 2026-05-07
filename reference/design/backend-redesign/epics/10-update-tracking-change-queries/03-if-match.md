---
jira: DMS-1005
jira_url: https://edfi.atlassian.net/browse/DMS-1005
---

# Story: Enforce `If-Match` Using Stored Representation Stamps

## Description

Implement optimistic concurrency checks using stored representation stamps for relational document resources and descriptor resources:

- For operations that support `If-Match` (`PUT`, `DELETE`, and `POST` when upsert resolves to an existing
  document), compare the client-provided `If-Match` header value to the current `_etag` computed from the canonical
  JSON form of the current resource-state representation, as defined by
  `reference/design/backend-redesign/design-docs/update-tracking.md`. Server-generated response decorations such as
  reference `link` objects are excluded from the hash and do not affect `If-Match`. This applies equally to descriptor
  resources stored through `dms.Descriptor`.
- `If-Match` is optional. When the header is absent, these operations proceed without an HTTP precondition check.
- Header matching is an exact opaque string comparison: the header value must exactly equal the current `_etag` value
  DMS would serve for the resource-state representation. The implementation must not normalize quotes, parse
  entity-tag lists, or otherwise reinterpret the value for this story.
- When `POST` with `If-Match` resolves to an insert/new document, the request fails with `412` because there is no
  current representation whose `_etag` can satisfy the precondition.
- No dependency locking is required because indirect impacts are materialized as local updates that bump the same stamp.
- `DMS-984` introduces the internal `ContentVersion` freshness recheck for the shared guarded no-op fast path, and
  `DMS-1124` reuses that result for profiled no-op outcomes; this story owns HTTP `If-Match` comparison and `412`
  mapping for changed writes, deletes, `POST` upsert-as-update, and stale-no-op outcomes.

## Acceptance Criteria

- When `If-Match` is absent, `PUT`, `DELETE`, and `POST` upsert-as-update continue through the existing relational
  and descriptor write/delete paths without an HTTP precondition check.
- When `If-Match` exactly equals the current `_etag`, the changed write/delete proceeds and a guarded no-op may
  short-circuit after the internal `ContentVersion` freshness recheck succeeds on the shared no-profile or profiled
  executor path.
- When `If-Match` does not exactly match, the request fails with the appropriate HTTP error semantics (e.g.,
  precondition failed / `412`).
- When `POST` resolves to a new document and the request includes `If-Match`, the request fails with `412`; DMS does
  not ignore the header and does not treat it as an insert precondition success.
- If the shared guarded no-op executor path introduced by `DMS-984` and extended by `DMS-1124` reports that a no-op
  decision became stale before the guarded short-circuit step and `If-Match` was supplied, the request fails rather
  than returning success based on stale data.
- The check is resource-state-sensitive and reflects dependency identity changes.
- Descriptor `PUT`, descriptor `DELETE`, and descriptor `POST` upsert-as-update enforce the same optional exact-match
  `If-Match` semantics as relational document resources.

## Tasks

1. Implement a "read current document and compute `_etag` from its resource-state representation" path usable by
   `PUT`, `DELETE`, and `POST` upsert-as-update handlers prior to write/delete, for both relational document resources
   and descriptor resources. The computation follows update-tracking canonicalization, including exclusion of `link`.
   Because DMS-990 already introduced the read metadata canonicalization path, this story must update/reuse that
   metadata-removal logic so `link` is removed alongside `id`, `_etag`, and `_lastModifiedDate` everywhere `_etag` is
   computed or compared.
2. Implement optional exact opaque-string `If-Match` comparison and stale-no-op handling paths usable by changed
   writes, deletes, and guarded no-op handlers, consuming the internal freshness result produced by the shared
   `DMS-984` executor path and reused by `DMS-1124`.
3. Add tests for:
   1. absent `If-Match` proceeds without a precondition check,
   2. exact match success,
   3. exact mismatch failure,
   4. `POST` insert/new-document resolution with `If-Match` fails with `412`,
   5. descriptor `PUT`, descriptor `DELETE`, and descriptor `POST` upsert-as-update use the same semantics,
   6. mismatch caused by dependency identity change,
   7. at least one PostgreSQL and one SQL Server relational integration test proving a cascaded referenced identity
      change changes the dependent `_etag` and causes stale `If-Match` to return `412`,
   8. existing `If-Match` E2E scenarios switched to the relational backend without changing the scenario coverage,
   9. canonical metadata removal excludes `link` so the same resource-state surface produces the same `_etag` whether
      links are present or stripped,
   10. `If-Match` succeeds when the client-supplied `_etag` was obtained with
       `DataManagement:ResourceLinks:Enabled=true` and the write check runs with links stripped,
   11. `If-Match` succeeds when the client-supplied `_etag` was obtained with
       `DataManagement:ResourceLinks:Enabled=false` and the write check runs against a link-bearing
       intermediate document, proving link inclusion/exclusion does not affect the precondition for
       the same resource-state surface, and
   12. stale no-op compare reported by the shared guarded no-op executor path (`DMS-984`, reused by `DMS-1124`) that
      is rejected by the guarded `If-Match` recheck.

## Clarifying Questions and Answers

## Questions 1

  1. For profiled requests, should If-Match compare against the full resource _etag, or the profile-projected representation _etag that a client would have received from a profiled GET? This affects
     both comparison and the ETag returned from successful profiled writes.
  2. On a stale guarded no-op, should If-Match cause an immediate 412, or should we re-read/recompute the current _etag and only return 412 if the header no longer matches?
  3. Should the If-Match check lock the target dms.Document row through the write/delete transaction for changed writes too? The existing freshness checker locks only guarded no-op rechecks, and
     relational DELETE has the If-Match work explicitly deferred at src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs:330.
  4. What error precedence do you want for POST with If-Match that resolves to a new document but also has other request problems, such as invalid references or profile validation failures? Should
     target resolution + 412 win before deeper validation?
  5. For PUT/DELETE with If-Match against a missing document, should existing 404 behavior remain, with the special “new document + If-Match = 412” rule applying only to POST upsert insert resolution?
  6. Are you comfortable adding an explicit UpsertFailureETagMisMatch result and handler mapping to 412? UpdateResult and DeleteResult already have ETag mismatch cases, but UpsertResult currently does
     not.
  7. Should descriptor writes be refactored to carry headers through DescriptorWriteRequest and use the same precondition/freshness helpers as relational resources? The current descriptor request
     contract does not include headers at src/dms/backend/EdFi.DataManagementService.Backend/DescriptorWriteContracts.cs:18.
  8. Should blank or whitespace If-Match be treated as “present and mismatched” or effectively absent? The frontend currently drops blank header values before they reach backend code.
  9. For test placement, do you want the existing resource If-Match feature scenarios moved to relational E2E only, while descriptor/post-insert/cascade/stale-no-op cases live mostly in backend
     integration tests? That seems like the right cost/coverage split unless you want more of this at full E2E level.


## Answers 1

  1. Profiled requests: compare against the profile-projected _etag the client would have received from a profiled GET, and return that same profile-surface ETag on successful profiled writes. This
     matches update-tracking.md, which says readable-profile responses recompute _etag from the projected document.
  2. Stale guarded no-op: follow the profile/concurrency design. Stale no-op + present If-Match should return 412 immediately. The story wording should say “if the guarded no-op freshness check is
     stale and If-Match was supplied.” Without If-Match, abandon the no-op fast path and re-evaluate/retry.
  3. Locking: yes, for operations that supply If-Match, lock the target dms.Document row through the transaction before comparing and before changed write/delete DML. This is still row-local locking,
     not dependency locking. It prevents a check-then-write race where another write changes the representation after the ETag comparison.
  4. POST create-new with If-Match precedence: once the request is valid enough to resolve the target identity, if POST resolves to create-new and If-Match is present, return 412 before deeper backend
     validation such as references or profile creatability. Earlier pipeline failures like malformed JSON/content type/profile header can still win.
  5. PUT/DELETE missing target: keep existing 404. The special “new document + If-Match = 412” rule should apply only to POST upsert resolving to insert.
  6. Upsert result: yes, add UpsertFailureETagMisMatch and map it to 412, matching existing UpdateFailureETagMisMatch and DeleteFailureETagMisMatch. Use the existing spelling for consistency unless the
     team wants a broader cleanup.
  7. Descriptor path: yes, refactor descriptors to use the same precondition helper as relational resources. Prefer passing a typed precondition value instead of all headers if practical; otherwise add
     headers to DescriptorWriteRequest and descriptor delete.
  8. Blank/whitespace If-Match: if it reaches backend, treat it as present and mismatched. Do not trim or reinterpret. If the frontend drops blank headers today, that naturally becomes absent.
  9. Test placement: the proposed split is right. Keep existing resource If-Match scenarios as relational E2E coverage. Put descriptor, POST insert-with-If-Match, cascade-induced mismatch, and stale
     no-op coverage mostly in backend unit/integration tests, with explicit PostgreSQL and SQL Server integration coverage for the cascade case.


## Questions 2

  1. Should we introduce a first-class backend contract like IfMatchPrecondition / WritePrecondition, or is it acceptable for relational code to read Headers["If-Match"] directly? This affects
     RelationalWriteExecutorRequest, descriptor writes, and descriptor delete.
  2. Should the authoritative If-Match check always happen inside the write/delete transaction after target re-resolution and row lock, with the repository’s initial target lookup treated as advisory
     only?
  3. For stale guarded no-op with If-Match present, should the repository bypass the current stale-no-op retry loop and return 412 on the first stale outcome?
  4. When If-Match is absent and stale guarded no-op occurs, is the current single retry enough, or should this story broaden it to a more explicit re-evaluate/retry policy?
  5. Should this story also fix descriptor POST-as-update no-op stamping, or leave that to DMS-1008? The current descriptor POST-as-update path appears to always issue update/stamp work.
  6. Are profiled descriptor writes in scope for this story’s “profile-projected _etag” rule, or only relational-table resources?
  7. If a DELETE request carries profile media-type headers, should If-Match compare against the full resource _etag, the profile-projected _etag, or should profile headers be ignored for DELETE?
  8. For existing-target mismatch, should 412 take precedence over deeper backend failures such as invalid references, profile creatability, and authorization, once the target is resolved? The story
     answers this for POST create-new, but not for existing-target mismatch.

## Answers 2

  1. Use a typed precondition contract. Add something like IfMatchPrecondition / WritePrecondition, built once from request headers with exact opaque-string semantics. Do not have relational or
     descriptor code read Headers["If-Match"] directly. Pass the typed value through RelationalWriteExecutorRequest, DescriptorWriteRequest, and a descriptor delete request shape.
  2. Yes, the authoritative check belongs inside the transaction. Treat the repository’s first target lookup as advisory. For present If-Match, re-resolve or confirm the target in the write/delete
     transaction, lock dms.Document, compute the current resource-state _etag, compare, then keep the lock through DML/delete/commit. This matches the earlier answer in reference/design/backend-redesign/
     epics/10-update-tracking-change-queries/03-if-match.md:95.
  3. Yes, stale guarded no-op plus If-Match should bypass retry and return 412. The current repository retry loop retries stale no-op unconditionally at src/dms/backend/
     EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs:700. Change that so retry only applies when the precondition is absent.
  4. Keep the absent-If-Match retry narrow for this story. The design says stale without If-Match should re-evaluate current state, and the existing single retry is a reasonable scoped implementation.
     Broader retry policy belongs in the executor/retry stories, not DMS-1005.
  5. Do not absorb all of DMS-1008, but fix the overlap if touched. DMS-1008 owns descriptor stamp/journal correctness, including no-op descriptor updates (reference/design/backend-redesign/epics/10-
     update-tracking-change-queries/06-descriptor-stamping.md:10). But DMS-1005 will already need current descriptor state for If-Match; if practical, use that helper to stop descriptor POST-as-update
     from always issuing update/stamp work, which currently happens in src/dms/backend/EdFi.DataManagementService.Backend/DescriptorWriteHandler.cs:411.
  6. Profiled descriptor writes should follow the same rule only when they are actually supported. If DMS accepts a profiled descriptor write, compare against the profile-projected descriptor
     representation, because descriptor reads already apply readable profile projection. If writable descriptor profiles are not supported, fail earlier as unsupported/invalid profile usage rather than
     silently comparing the full _etag.
  7. DELETE should use the full-resource _etag. A DELETE removes the whole resource, not just the profile-visible surface. Ignoring profile media-type headers for DELETE precondition purposes is safer
     than allowing a profile-projected _etag to authorize deletion after hidden data changed.
  8. Precedence: auth should not be overridden by 412; most deeper write validation can be. Suggested order: malformed request/content type/profile usage and missing target still win; authorization
     should still return 403 before exposing an ETag mismatch for an existing target. After target exists and the caller is authorized, If-Match mismatch should short-circuit before reference
     resolution, profile creatability, merge validation, and DML.

## Questions 3

  1. Should DataManagement:ResourceLinks:Enabled affect If-Match comparison, or should reference link objects be treated as response decoration excluded from the _etag in both flag states?
  2. For profiled PUT/POST, should Core pass the readable-profile projection context explicitly into the typed precondition contract, so backend never infers the comparison surface from writable-
     profile data?
  3. Are writable descriptor profiles expected to work today? If yes, DescriptorWriteRequest likely needs profile/precondition surface plumbing too. If no, should profiled descriptor writes fail before
     If-Match logic?
  4. For descriptor writes, should we refactor them onto IRelationalWriteSessionFactory and the shared transaction/session pattern, or is a single command batch with row lock + compare + update
     acceptable?
  5. Should descriptor POST upsert-as-update no-op stamping be fixed as part of DMS-1005, or treated as a DMS-1008 follow-up unless the refactor makes it trivial?
  6. For existing-target If-Match mismatch, do you want us to strictly reorder the executor so precondition checking happens before reference resolution/profile creatability validation, matching the
     clarification, even though that changes the current executor flow?
  7. For multiple If-Match header values collapsed by the frontend into one string, should backend treat the whole string as the opaque value and mismatch, rather than rejecting it as malformed?
  8. Do you have a preferred cascade fixture/resource pair for the PostgreSQL + SQL Server integration tests proving dependency identity change causes stale If-Match to return 412? If not, I’ll pick
     the smallest existing backend fixture that exercises an allowIdentityUpdates identity cascade in both dialects.

## Answers 3

  1. ResourceLinks flag should not affect If-Match. `_etag` is a resource-state validator, not a response-decoration
     validator, and reference `link` objects are derived from persisted reference state. Compare against the same
     link-excluding `_etag` in both flag states. This aligns with reference/design/backend-redesign/design-docs/
     update-tracking.md and reference/design/backend-redesign/design-docs/link-injection.md.
  2. Yes, Core should pass the readable comparison surface explicitly. Backend should receive a typed precondition contract like IfMatchPrecondition plus a representation surface/context, not infer
     from writable profile data. That contract should carry the opaque header value and readable profile projection context when applicable. It does not need resource-link mode for `If-Match`, because
     `link` is excluded from `_etag` derivation in both flag states. This matches the profile design boundary that Core owns profile semantics and Backend consumes Core-supplied outputs in
     reference/design/backend-redesign/design-docs/profiles.md:128.
  3. Treat writable descriptor profiles as unsupported unless deliberately scoped now. Current descriptor write contracts have no profile or precondition surface plumbing in src/dms/backend/
     EdFi.DataManagementService.Backend/DescriptorWriteContracts.cs:16. If descriptor profile writes are not expected today, fail them before If-Match logic. If they are expected, add the same typed
     precondition/profile surface to descriptor writes rather than silently comparing against the full descriptor representation.
  4. Prefer moving descriptors onto the shared transaction/session pattern. A single SQL batch with row lock + compare + update can be correct, but DMS-1005 already needs the same row-local lock/check/
     write semantics described in reference/design/backend-redesign/design-docs/transactions-and-concurrency.md:336. I’d refactor descriptors enough to use a shared session/helper for lock, materialize
     current representation, compare, DML, and commit. It does not need to become the whole relational resource executor.
  5. Fix descriptor POST-as-update no-op stamping if it falls out cheaply. Descriptor upsert currently always performs the update path in src/dms/backend/EdFi.DataManagementService.Backend/
     DescriptorWriteHandler.cs:411. Since If-Match needs current-state comparison anyway, it is reasonable to short-circuit unchanged descriptor POST-as-update while touching this code. Keep broader
     descriptor no-op/journal hardening in DMS-1008 unless this refactor makes it trivial.
  6. Yes, reorder existing-target mismatch checks before deeper validation. After route/schema/profile media-type handling, target identity resolution, existence, and authorization, a present If-Match
     should be checked inside the write transaction before reference resolution, profile creatability validation, merge, and DML. That preserves the intended 412 behavior without hiding 404/
     authorization failures.
  7. Treat multiple/collapsed If-Match values as opaque. Backend should compare exactly the string it receives. No parsing, splitting, trimming, or malformed-header rejection. If the frontend passes
     abc, def or any collapsed value, it simply mismatches unless it exactly equals the current _etag.
  8. Use the small referential identity fixture for cascade tests. Preferred pair: Student -> ResourceA from the small/referential-identity fixtures already used by PostgreSQL and SQL Server tests.
     Change Student.studentUniqueId with allowIdentityUpdates=true, verify dependent ResourceA gets a new _etag, then assert stale If-Match on ResourceA returns 412. Use ResourceB only if you want a
     second dependent; ResourceA is enough for the story.
