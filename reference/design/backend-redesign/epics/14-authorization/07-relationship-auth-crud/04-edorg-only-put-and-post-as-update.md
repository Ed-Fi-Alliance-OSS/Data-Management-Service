---
jira: DMS-1163
jira_url: https://edfi.atlassian.net/browse/DMS-1163
---

# Slice 4: EdOrg-only PUT And POST-as-update

## Purpose

Complete EdOrg-only relationship CRUD authorization by implementing stored-then-proposed checks for PUT and POST requests that resolve to an existing resource.

This is the riskiest EdOrg-only operation slice because it intersects target resolution, `If-Match`, current-state loading, guarded no-op, and profile-aware write behavior.

## In Scope

- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for PUT.
- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for POST-as-update.
- Stored-value authorization before update/no-op success can be returned.
- Proposed-value authorization when identifying authorization values can change.
- Integration with current-state loading and guarded no-op so unauthorized callers cannot bypass authorization with an unchanged request body.
- PostgreSQL and SQL Server SQL generation/execution coverage.
- Minimal relationship authorization failure mapping compatible with Slice 6 hardening.

## Explicitly Out Of Scope

- POST create-new behavior already owned by Slice 3.
- GET-by-id and DELETE behavior beyond preserving Slice 2.
- People-involved relationship authorization execution.
- Exact final ProblemDetails wording, hint aggregation, and EdOrg claim formatting hardening.
- Performance optimizations that skip proposed authorization because values are unchanged unless the existing executor already exposes a reliable comparison without additional risk.

## Runtime Behavior

### PUT

- Resolve the target document by id using the existing PUT flow.
- Authorize currently stored EdOrg values before applying update behavior.
- Authorize proposed EdOrg values when the resource allows updates to identifying authorization values.
- Abort before update or guarded no-op success if either required authorization check fails.
- Continue to existing reference resolution, current-state loading, no-op detection, profile merge, and persist behavior only after required relationship checks pass.

### POST-as-update

- Resolve POST target identity using the established POST upsert flow.
- If the target exists, follow the same stored-then-proposed authorization model as PUT.
- If the target does not exist, Slice 3 owns create-new behavior.

## Acceptance Criteria

- PUT succeeds only when the caller is authorized for stored values and, when required, proposed values.
- POST-as-update succeeds only when the caller is authorized for stored values and, when required, proposed values.
- Stored-value authorization executes before update DML, profile merge persistence, or guarded no-op success.
- Proposed-value authorization executes after request/reference data is available and before update DML.
- A caller authorized for proposed values but unauthorized for stored values is denied.
- A caller authorized for stored values but unauthorized for proposed values is denied when proposed authorization is required.
- Unauthorized PUT and POST-as-update do not update `dms.Document`, resource rows, child rows, update-tracking stamps, or referential identity data.
- Guarded no-op cannot return success before stored-value authorization passes.
- `If-Match` and target current-state interactions preserve the final authorization-before-mutation behavior required by `auth.md` and the namespace authorization deferred-guard notes.
- Inverted strategy tests prove stored and proposed checks both swap Source/Target hierarchy filtering.
- Missing/null stored EdOrg values produce relationship invalid-data failure metadata.
- Missing/null proposed EdOrg values produce relationship element-required failure metadata.
- PostgreSQL and SQL Server both abort unauthorized update batches with the established AUTH1 mechanism.

## Tests Required

### Unit tests

- PUT check ordering: stored check before proposed check before persist/no-op.
- POST-as-update check ordering mirrors PUT after target resolution.
- Stored-authorized/proposed-unauthorized denial.
- Stored-unauthorized/proposed-authorized denial.
- Guarded no-op still requires stored authorization.
- `If-Match` interaction tests cover stale etag, matching etag, and unauthorized existing target behavior.
- Normal and inverted Source/Target metadata is applied to both stored and proposed checks.

### Backend integration tests

- PostgreSQL and SQL Server authorized PUT updates a document.
- PostgreSQL and SQL Server unauthorized stored-value PUT leaves rows unchanged.
- PostgreSQL and SQL Server unauthorized proposed-value PUT leaves rows unchanged.
- PostgreSQL and SQL Server authorized POST-as-update updates a document.
- PostgreSQL and SQL Server unauthorized POST-as-update leaves rows unchanged.
- No-op PUT/POST-as-update with unauthorized stored values returns 403 rather than success.

### E2E tests

- Focused DMS E2E coverage with real claim-set/token wiring for authorized and unauthorized PUT.
- Focused DMS E2E coverage for POST-as-update when feasible with existing fixtures.

## Reviewer Focus

Reviewers should focus on ordering and side effects. The correct implementation denies unauthorized existing targets before any update, no-op success, profile merge, or persistence side effect can escape.

## Clarifying Questions and Answers

### Questions 1

1. If the effective PUT or POST-as-update authorization set includes a supported EdOrg relationship strategy plus any known-but-not-enabled strategy kind from Slice 1, such as People relationship, NamespaceBased, OwnershipBased, or custom view-based, should Slice 4 fail fast through the same temporary not-implemented/fail-closed surface used by Slices 2 and 3 rather than partially authorizing with only the EdOrg subset?
2. Should Slice 4 extend the PUT `UpdateResult` contract, and reuse or adjust the POST `UpsertResult` contract, with explicit temporary not-implemented, security-configuration, and relationship-not-authorized variants carrying `RelationshipAuthorizationFailure` for both stored-value and proposed-value denials?
3. What exact operation order should Slice 4 enforce around stored authorization, reference/descriptor resolution, current-state loading, profile merge, proposed authorization, `If-Match`, guarded no-op, and update DML? In particular, should stored-value relationship failure short-circuit before request-dependent reference resolution or proposed-value extraction?
4. For an existing target where stored-value authorization passes but `If-Match` is stale and proposed-value authorization would fail, which response should win: `412` after stored authorization, or proposed-value relationship `403` before precondition failure is exposed?
5. For writable-profile PUT and POST-as-update, should proposed-value relationship authorization bind from the final merged root row that would be persisted or no-op compared, including stored hidden profile values, rather than from the raw request body or an earlier pre-merge row buffer?
6. What is the precise gate for running proposed-value authorization in Slice 4: always for EdOrg-only update attempts, only when authorization-subject bindings are identity-updatable or writable, or only when the finalized proposed value differs from the stored value and a reliable comparison already exists?
7. Should stored authorization, `If-Match` comparison, proposed authorization, no-op freshness checking, and update DML share one locked transaction/session or `ContentVersion` guard so they observe the same stored target state, with tests asserting the behavioral boundary rather than a specific one-command topology?
8. For POST requests that resolve to an existing target during Slice 4, should empty EdOrg claims retain Slice 3's pre-reference/target-detection `403` short-circuit, or should existing-target stored authorization be attempted and mapped after target resolution?

### Answers 1

1. Fail fast. Slice 4 remains an EdOrg-only vertical slice. If operation-neutral planning finds any known-but-not-enabled strategy kind from Slice 1, such as People relationship, `NamespaceBased`, `OwnershipBased`, or a convention-matching custom view-based strategy, return the same explicit temporary not-implemented/fail-closed staging result used by Slices 2 and 3. Do not partially authorize with only the EdOrg subset. True security-configuration failures still win as configuration failures, and `NoFurtherAuthorizationRequired` remains a no-op.
2. Extend the PUT `UpdateResult` contract now, and keep/adjust the POST `UpsertResult` contract so both operations explicitly model temporary not-implemented, security-configuration, and relationship-not-authorized outcomes. Relationship-not-authorized results must carry the external `RelationshipAuthorizationFailure` DTO used by Slices 2 and 3, with value-source metadata distinguishing stored-value failures from proposed-value failures. Do not route these intentional outcomes through exceptions, generic unknown failures, or string-only result surfaces.
3. Enforce this order for existing-target PUT and POST-as-update: operation-neutral planning; target existence resolution; shared write-session/guard setup and current stored target observation; stored-value relationship authorization; request-dependent reference/descriptor resolution; current-state materialization needed for profile merge/no-op comparison; finalized profile-aware merge rowset construction; proposed-value relationship authorization when required; `If-Match` comparison; guarded no-op freshness check; then update DML. A stored-value relationship failure must short-circuit before request-dependent reference resolution, proposed-value extraction, profile merge persistence, guarded no-op success, or update DML.
4. Proposed-value relationship `403` wins. After stored-value authorization succeeds, Slice 4 should still authorize the finalized proposed authorization values before exposing an existing-target `If-Match` mismatch. If proposed authorization fails and the supplied `If-Match` is also stale, return relationship-not-authorized/403, not 412. If both stored and proposed authorization pass, preserve the normal `If-Match` 412 behavior before no-op success or DML.
5. Yes. For writable-profile PUT and POST-as-update, proposed-value relationship authorization must bind from the finalized merged root row or rowset that the executor would persist and that guarded no-op comparison would compare. That includes stored values preserved because they are hidden by the active profile. Do not authorize from raw JSON or an earlier pre-merge row buffer. If the finalized proposed authorization value is missing/null, bind it as null and let the relationship `AUTH1` failure map to proposed element-required metadata.
6. Run proposed-value relationship authorization whenever the EdOrg relationship authorization plan has any proposed-value subject whose finalized write value can be produced for the update operation. Do not skip it merely because the finalized value appears unchanged; Slice 4 should only omit proposed authorization when planning proves there is no applicable proposed-value EdOrg subject for the operation. Difference-based skipping is a later optimization unless the existing executor already exposes a reliable, shared comparison with no additional authorization risk.
7. Yes. Stored authorization, current-state loading, proposed authorization, `If-Match`, guarded no-op freshness, and update DML must share a locked write transaction/session or an equivalent `ContentVersion` guard/retry boundary so they cannot authorize against one stored target state and mutate or return no-op success against another. One provider-specific command is acceptable but not required. Tests should assert the behavioral boundary, response precedence, stale-guard behavior, and unchanged rows on authorization failure rather than requiring a specific one-command topology.
8. Retain Slice 3's empty-claims short-circuit for POST. After operation-neutral planning succeeds and the relationship plan requires EdOrg claims, an empty normalized EdOrg claim list should return relationship-not-authorized/403 with no-claims metadata before reference/descriptor resolution or create-vs-existing target detection. Do not attempt existing-target stored authorization for POST when there are no EdOrg claims, because doing target resolution first would let no-claim callers probe references or natural-key existence. With non-empty claims, POST-as-update follows the stored-then-proposed existing-target flow above.

### Questions 2

1. For POST-as-update, should "target existence resolution" before stored-value authorization be limited to the request resource's own `ReferentialId`/identity lookup, with all other request-dependent reference and descriptor validation deferred until after stored authorization passes?
2. If POST-as-update target existence resolution itself requires reference-dependent work that can fail, which response should win for an existing target the caller is not authorized to stored values: the reference/descriptor failure, or the stored-value relationship `403`?
3. Does Slice 4 intentionally supersede DMS-1005's existing-target ordering so reference-resolution, profile-contract, creatability, or merge-synthesis failures needed to finalize proposed authorization may surface before a stale `If-Match` when proposed authorization passes?
4. Should the request-dependent reference/descriptor resolution that produces proposed authorization parameters run inside the same locked transaction/session or `ContentVersion` guard as stored authorization, proposed authorization, `If-Match`, guarded no-op, and update DML?
5. If a profiled PUT or POST-as-update would hit a deterministic profile slice fence or Core/backend profile contract mismatch while stored-value relationship authorization would fail, should the stored relationship `403` win before the profile/fence result is exposed?

### Answers 2

1. Yes. For POST-as-update, the pre-stored-authorization target existence step should be limited to the request resource's own identity/`ReferentialId` lookup and the minimal canonicalization needed to decide whether this POST targets an existing document. Do not run general outbound reference or descriptor validation before stored-value authorization. Once an existing target is identified, stored-value authorization gates all other request-dependent reference/descriptor work.
2. Stored-value relationship `403` wins for a confirmed existing target. If the target can be identified, do not expose reference/descriptor failures for other request data until stored authorization passes. If the minimal identity lookup cannot establish an existing target at all, the request is not in the POST-as-update branch yet; after the Slice 3 empty-claims/planning guards, it should follow the create-new/reference-validation behavior for unresolved identity inputs.
3. Yes, but only for work required to produce the finalized proposed authorization values. Slice 4 intentionally moves reference/descriptor resolution, profile contract validation, creatability checks, and merge synthesis that are necessary to build the final proposed rowset ahead of `If-Match`. Those failures, or a proposed-value relationship `403`, may surface before a stale `If-Match`. After stored authorization passes, the proposed rowset is finalized, and proposed authorization passes, `If-Match` should run before any remaining validation or persistence work not needed for proposed authorization.
4. Yes. Request-dependent reference/descriptor resolution whose outputs feed proposed authorization must run inside the same locked write transaction/session or an equivalent `ContentVersion` guard/revalidation boundary as stored authorization, proposed authorization, `If-Match`, guarded no-op, and update DML. If some lookup remains as earlier advisory work, it must be revalidated or re-resolved under the guarded boundary before proposed authorization and mutation.
5. Yes. For an existing target, stored-value relationship authorization must run before profile slice-fence classification, Core/backend profile contract validation, merge synthesis, or guarded no-op. If stored authorization fails, return the relationship-not-authorized `403` and do not expose the profile fence or contract-mismatch result. Earlier operation-neutral security planning failures and route/content/profile-usage errors that occur before target authorization keep their established precedence.

### Questions 3

1. For PUT with empty normalized EdOrg claims, should Slice 4 follow the stored-value single-record model used by GET-by-id/DELETE, where operation-neutral planning runs first, a missing target remains 404, and an existing target returns relationship-not-authorized/403 before reference resolution, profile merge, `If-Match`, guarded no-op, or DML; or should PUT use the POST no-claims pre-target short-circuit?
2. When stored-value and proposed-value authorization both evaluate the configured relationship OR group, may an update pass when stored values authorize through one EdOrg-only relationship strategy and finalized proposed values authorize through a different EdOrg-only relationship strategy, or must the same configured strategy authorize both states?
3. Should Slice 4 reuse the Slice 2/3 versioned compact `AUTH1` failure-set payload and parser for both stored-value and proposed-value update checks, preserving the full failed OR-strategy/subject set with value-source metadata, or is an update-specific or coarser payload acceptable until Slice 6?
4. Should Slice 4 add focused writable-profile test coverage proving proposed authorization uses the finalized merged rowset, including profile-hidden stored EdOrg values and proposed element-required metadata, and should that coverage be unit-only or include provider integration?
5. Should unauthorized PUT and POST-as-update side-effect assertions include no `dms.DocumentChangeEvent` or change-query journal rows and no `dms.ReferentialIdentity` updates in addition to unchanged `dms.Document` stamps and resource/child rows?

### Answers 3

1. PUT should follow the stored-value single-record model used by GET-by-id and DELETE. Run operation-neutral planning first; if the route id does not resolve to a target, preserve the normal 404. If the target exists and the relationship plan requires EdOrg claims but the normalized claim list is empty, return relationship-not-authorized/403 with no-claims metadata before request-dependent reference resolution, profile merge, `If-Match`, guarded no-op, or DML. The POST pre-target no-claims short-circuit remains POST-specific because POST target detection can expose natural-key and reference existence.
2. The update may pass when stored values authorize through one configured EdOrg-only relationship strategy and finalized proposed values authorize through a different configured EdOrg-only relationship strategy. Stored-value authorization and proposed-value authorization each evaluate the full configured relationship OR group independently, preserving strategy order and metadata. Do not require the same OR strategy to authorize both states.
3. Reuse the Slice 2/3 versioned compact `AUTH1` failure-set payload and parser for both stored-value and proposed-value update checks. Each failed relationship OR group should carry the emitted check index plus plan-relative strategy/subject ordinals and compact failure-kind codes; backend mapping must preserve the full failed strategy/subject set in configured order and add stored/proposed value-source metadata on the external `RelationshipAuthorizationFailure`. Do not introduce an update-specific or coarser transitional payload.
4. Yes. Add focused writable-profile coverage at both levels: unit tests for orchestration and rowset binding, and narrow PostgreSQL and SQL Server backend integration coverage. The unit tests should prove proposed authorization reads from the finalized merged rowset, including hidden stored EdOrg values preserved by the active profile, and maps missing/null finalized proposed values to proposed element-required metadata. Provider integration should cover a writable-profile PUT and POST-as-update path where the hidden stored EdOrg value participates in authorization and the unauthorized case leaves persisted rows unchanged. No broad E2E profile matrix is required for this slice.
5. Yes. Unauthorized PUT and POST-as-update assertions should include no `dms.DocumentChangeEvent` or other change-query journal rows for the attempted update, no `dms.ReferentialIdentity` insert/update/replacement for the attempted identity, unchanged `dms.Document` content and identity stamps, and unchanged resource root/child/extension rows. Apply this to stored-value denials, proposed-value denials, and unauthorized no-op attempts. Do not assert that global sequences or identity generators did not advance; assert only persisted rows and derived artifacts that would indicate the write progressed.
