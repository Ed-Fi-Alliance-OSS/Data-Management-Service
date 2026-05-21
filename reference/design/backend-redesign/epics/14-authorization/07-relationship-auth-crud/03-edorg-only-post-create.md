---
jira: DMS-1162
jira_url: https://edfi.atlassian.net/browse/DMS-1162
---

# Slice 3: EdOrg-only POST-create

## Purpose

Authorize EdOrg-only relationship strategies for POST requests that resolve to a new resource create.

This slice proves proposed-value authorization before `dms.Document` insertion while leaving existing-resource POST-as-update behavior for Slice 4.

## In Scope

- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for POST create-new.
- Proposed-value authorization from request-body/root-row values after reference and descriptor resolution needed by the write path.
- No-insert behavior when authorization fails.
- PostgreSQL and SQL Server SQL generation/execution coverage.
- Minimal relationship authorization failure mapping compatible with Slice 6 hardening.

## Explicitly Out Of Scope

- POST requests that resolve to an existing resource.
- Descriptor resource POST authorization. Descriptors use `NamespaceBased` authorization and are owned by DMS-1057, not this EdOrg relationship slice. DMS-1162 accepts the current relational descriptor POST behavior until DMS-1057 implements true NamespaceBased descriptor write authorization.
- PUT authorization.
- People-involved relationship authorization.
- Exact final ProblemDetails wording, hint aggregation, and EdOrg claim formatting hardening.
- Optimizations that skip authorization based on direct token EdOrg matches.

## Runtime Behavior

- Run operation-neutral authorization planning once route/resource/action and token context are known.
- If the normalized EdOrg claim list is empty after operation-neutral planning succeeds, return relationship-not-authorized/403 before request-dependent reference/descriptor resolution or target identity detection.
- Run normal POST reference and descriptor resolution required to determine the proposed root-row values and target identity.
- Determine whether POST resolves to create-new or existing-document using the established write path.
- For create-new:
  - authorize proposed EdOrg values before inserting into `dms.Document`,
  - abort the batch on authorization failure, and
  - insert no document or resource rows on failure.
- For existing-document:
  - if proposed-value relationship authorization fails, return the same relationship-not-authorized/403 used by create-new before exposing the staged existing-resource response,
  - if proposed-value relationship authorization succeeds, leave the existing explicit not-implemented/fail-closed behavior until Slice 4 lands, and
  - do not execute stored-value authorization, update authorization, or mutation behavior until Slice 4 lands.

## Acceptance Criteria

- POST create-new for an EdOrg-only relationship resource succeeds only when the caller has access through at least one configured EdOrg-only relationship strategy.
- POST create-new with `RelationshipsWithEdOrgsOnlyInverted` uses inverted Source/Target hierarchy filtering against proposed EdOrg values.
- Multiple relationship strategies OR together and preserve configured strategy index metadata.
- Multiple proposed EdOrg subjects inside one strategy AND together.
- Authorization checks execute before `dms.Document` insert.
- Unauthorized POST create-new returns 403 and inserts no `dms.Document` row, no resource root row, and no child rows.
- Proposed EdOrg values are parameterized; token EdOrg IDs are never inlined into SQL.
- Missing/null proposed EdOrg values required for authorization produce the relationship element-required failure metadata consumed by Slice 6.
- Reference-resolution failure remains a reference-resolution error and is not converted into an authorization denial when the request is not already denied by the empty-claims short-circuit.
- Authorization failure after successful reference resolution still prevents all inserts.
- Security-configuration failures from Slice 1 surface as configuration failures, not as 403 authorization denials.
- Enabling backend-planned authorization for relational POST does not change descriptor POST authorization semantics in DMS-1162. Descriptor POST remains outside the EdOrg relationship POST-create preflight and keeps the current relational descriptor write behavior; DMS-1057 owns changing descriptor POST to true NamespaceBased authorization.
- PostgreSQL and SQL Server both abort the create batch with the established AUTH1 mechanism.
- Empty normalized EdOrg claims return relationship-not-authorized/403 before reference/descriptor resolution or target identity detection, so callers with no EdOrg authorization context cannot probe references or natural-key existence.
- POST requests that resolve to an existing document during Slice 3 return the staged existing-resource POST-as-update result only after proposed-value relationship authorization would have succeeded; failed proposed authorization returns 403 so unauthorized callers cannot distinguish existing-resource POST from create-new POST by status code.
- For POST create-new with `If-Match`, failed proposed-value relationship authorization returns 403 before the
  create-new `If-Match` 412 response can be exposed.
- For POST create-new with `If-Match` where proposed-value relationship authorization succeeds, return 412 and insert
  no rows.

## Tests Required

### Unit tests

- Proposed-value check spec generation for normal and inverted EdOrg-only strategies.
- POST create SQL/check placement before `dms.Document` insert.
- Missing proposed securable element failure metadata.
- Multiple strategies OR and multiple subjects AND.
- Reference-resolution failure and authorization failure remain distinct result paths when the empty-claims short-circuit does not apply.
- Empty EdOrg claims short-circuit before reference/descriptor resolution and target identity detection.
- Existing-resource POST-as-update staging does not expose the staged 501 surface when proposed-value authorization fails.
- POST create-new `If-Match` precedence: unauthorized proposed values return relationship-not-authorized/403 rather
  than 412; authorized proposed values with `If-Match` return the upsert ETag mismatch/412.

### Backend integration tests

- PostgreSQL and SQL Server authorized POST create inserts document and resource rows.
- PostgreSQL and SQL Server unauthorized POST create inserts no rows.
- PostgreSQL and SQL Server reference-resolution failure does not execute authorization as a misleading substitute for reference validation.
- Inverted strategy tests prove proposed-value Source/Target filtering is swapped.

### E2E tests

- Focused DMS E2E coverage with real claim-set/token wiring for authorized and unauthorized POST create-new.

## Reviewer Focus

Reviewers should focus on the create boundary: the authorization check must happen after enough request processing to know the proposed values, but before any persistent insert can survive a failure.

## Clarifying Questions and Answers

### Questions 1

1. If the effective POST-create authorization set includes a supported EdOrg relationship strategy plus any known-but-not-enabled strategy kind from Slice 1, such as People relationship, NamespaceBased, OwnershipBased, or custom view-based, should Slice 3 fail fast through the same temporary not-implemented/fail-closed surface used by Slice 2 rather than partially authorizing with only the EdOrg subset?
2. Should Slice 3 extend the POST backend-to-handler result contract now with explicit temporary not-implemented, security-configuration, and relationship-not-authorized variants, or should any of those continue through existing generic/unknown failure mapping until a later slice?
3. Should POST-create relationship denials use the same versioned compact AUTH1 failure-set payload/parser introduced by Slice 2, carrying plan-relative strategy/subject ordinals and runtime proposed-value failure kinds, or is a coarser transitional 403 acceptable before Slice 6 hardening?
4. For a failed proposed-value relationship OR group, should Slice 3 return the full failed strategy/subject metadata in configured order, including mixed proposed-missing and no-relationship failures, so Slice 6 can apply the shared failure-kind precedence without re-querying?
5. What precedence should Slice 3 enforce when request/reference processing and authorization planning both fail, for example an unresolved reference plus a Slice 1 security-configuration error: should normal POST validation/reference-resolution failures win, or should security-configuration failures be detected and returned before request-dependent processing?
6. Should empty EdOrg claims for POST create-new short-circuit to a 403 relationship failure using the explicit no-claims metadata shape from Slice 1, or should the SQL authorization path still be composed and allowed to fail through AUTH1?
7. When a POST request resolves to an existing document during Slice 3, should the existing fail-closed/not-implemented behavior be modeled as the same explicit POST result variant used for mixed known-but-not-enabled strategies, or should existing-resource POST continue to use its current separate behavior until Slice 4?
8. For proposed EdOrg values that are normally required by API validation or identity resolution, should Slice 3 add backend integration fixtures that bypass normal validation to prove element-required metadata, or is focused unit coverage sufficient until Slice 6 response hardening?

### Answers 1

1. Fail fast. Slice 3 remains an EdOrg-only vertical slice. If the effective POST-create authorization set includes any known-but-not-enabled strategy kind from Slice 1, such as People relationship, NamespaceBased, OwnershipBased, or a convention-matching custom view-based strategy, do not partially authorize with only the EdOrg subset. Return the same temporary explicit not-implemented/fail-closed staging surface used by Slice 2. `NoFurtherAuthorizationRequired` remains a no-op, and true security-configuration failures still win as configuration failures.
2. Extend the POST backend-to-handler result contract now. Add explicit variants for temporary not-implemented, security-configuration failure, and relationship-not-authorized. The relationship-not-authorized variant should carry the external relationship authorization failure DTO with `proposed` value-source metadata. Handlers can still render transitional responses before Slice 6, but these intentional authorization outcomes must not flow through exceptions, generic unknown failures, or string-only error surfaces.
3. Use the same versioned compact `AUTH1` failure-set payload and parser introduced by Slice 2. POST-create should encode the emitted check index plus plan-relative strategy/subject ordinals and compact runtime proposed-value failure-kind codes. The payload should not include user-readable names, JSON paths, EdOrg claim lists, or large values; the backend maps parsed ordinals back to precomputed plan metadata. A coarse transitional 403 is not acceptable because Slice 6 must format failures without re-querying or guessing.
4. Yes. When the proposed-value relationship OR group fails as a whole, return the full failed strategy/subject set in deterministic configured-strategy order, including mixed proposed-missing and no-relationship failures. If any OR strategy succeeds, the create is authorized and failures from other relationship strategies should not surface. Slice 6 will apply the shared failure-kind precedence: existing stored-value invalid data, proposed-value element required, then no relationship established.
5. Detect operation-neutral security planning and configuration failures before request-dependent reference/descriptor resolution and create/update target work. If Slice 1 planning returns a security-configuration failure, return the canonical security-configuration 500 even if request-dependent processing would also have failed later. Once authorization planning succeeds, normal POST validation and reference-resolution failures keep their own result paths and must not be converted into relationship authorization denials.
6. Short-circuit to a 403 relationship failure using the explicit no-claims metadata shape from Slice 1 before request-dependent reference/descriptor resolution and target identity detection. Do not compose SQL authorization or rely on `AUTH1` when the normalized EdOrg claim list is empty. The operation should return relationship-not-authorized, insert nothing, and let Slice 6 render the final EdOrg-claims-as-`none` wording. This avoids letting callers with no EdOrg authorization context probe referenced-resource existence or natural-key target existence.
7. Model existing-resource POST during Slice 3 as the same explicit temporary not-implemented/fail-closed POST result variant used for known-but-not-enabled mixed strategies, with reason metadata distinguishing existing-resource POST-as-update from strategy-not-enabled, but expose that staged 501 surface only after proposed-value relationship authorization would have succeeded. If proposed-value relationship authorization fails, return relationship-not-authorized/403 before the existing-resource staged result. This prevents unauthorized callers from distinguishing existing-resource POST-as-update from create-new POST by status code. Slice 4 replaces the existing-resource branch with stored-then-proposed authorization.
8. Focused unit coverage is sufficient for Slice 3. Do not add backend integration fixtures that bypass normal API validation or reference resolution solely to force missing identity/reference values into the proposed authorization path. Unit tests should construct proposed-value check inputs or root-row buffers with missing/null EdOrg values and assert the relationship element-required metadata. Backend integration should stay focused on authorized create, unauthorized create with no inserts, and reference-resolution failures remaining distinct from authorization failures.

### Questions 2

1. Should temporary not-implemented/fail-closed outcomes for known-but-not-enabled strategies be returned during operation-neutral planning before request-dependent validation, reference/descriptor resolution, and target identity work, or do normal POST validation/reference failures take precedence once there is no true security-configuration failure?
2. For empty normalized EdOrg claims, should Slice 3 still run normal POST request processing through reference/descriptor resolution and create-vs-existing target detection before returning relationship-not-authorized, or should the no-claims 403 short-circuit occur before those request-dependent steps?
3. Must the proposed-value authorization check and `dms.Document` insert be composed in one provider command/transaction batch where `AUTH1` aborts before the insert statement, or is it acceptable to execute proposed authorization as a separate command after target identity resolution and before the existing create persistence path?
4. Should proposed-value authorization read from the final root-row values that will be inserted after profile/write-shape filtering and generated/default value handling, or from the pre-persistence row buffer immediately after reference/descriptor resolution?

### Answers 2

1. Return temporary not-implemented/fail-closed outcomes during operation-neutral planning, before request-dependent POST validation, reference/descriptor resolution, and target identity work. After route/resource/action and token context are known, run Slice 1 classification/planning first. True security-configuration failures still win over known-but-not-enabled staging metadata and return the canonical 500; otherwise any known-but-not-enabled strategy returns the explicit staged 501 instead of allowing body/reference failures to mask unsupported authorization composition.
2. Short-circuit empty EdOrg claims before request-dependent reference/descriptor resolution and create-vs-existing target detection. After operation-neutral planning succeeds and the effective authorization plan requires EdOrg relationship claims, an empty normalized EdOrg claim list returns relationship-not-authorized with the no-claims metadata, composes no authorization SQL, inserts nothing, and does not rely on `AUTH1`. Normal reference-resolution failures keep their reference error path only when the request is not already denied by this no-claims authorization short-circuit.
3. Compose the proposed-value authorization check and `dms.Document` insert in the same provider create command/batch inside the request-scoped write transaction, with the `AUTH1` relationship check before the insert statement. A separate successful authorization command followed by the existing create persistence path is not sufficient for Slice 3 because the slice must prove provider abort behavior and the no-`dms.Document` side-effect boundary. Earlier reference/descriptor resolution and target identity detection can remain separate request steps.
4. Authorize against the same finalized root `RowBuffer` values that the create executor will bind for the root insert. That means after selecting the effective write body, including `ProfileAppliedWriteRequest.WritableRequestBody` when a writable profile applies, after reference/descriptor resolution, key-unification/precomputed binding population, write-shape filtering, and any application-computed default values that are present in the row buffer. Do not authorize from raw JSON or an earlier buffer that can differ from persisted values. Do not wait for database-generated defaults to supply authorization subjects; if an authorization-required proposed value has no bound root-row value, emit proposed element-required failure metadata.

### Questions 3

1. If POST create-new has a present `If-Match` header and the proposed EdOrg relationship check would fail, should Slice 3 preserve the DMS-1005 create-new `If-Match` precedence and return 412 before proposed-value authorization, or should the relationship 403 win?
2. If create-new reaches authorization with both empty normalized EdOrg claims and a missing/null required proposed EdOrg subject, should Slice 3 return the explicit no-claims relationship failure, or should proposed element-required take precedence over no-claims/no-relationship metadata?
3. Should Slice 3 add focused coverage for writable-profile POST create to prove authorization uses the profile-applied finalized root `RowBuffer`, or is no-profile unit/integration coverage sufficient as long as the extractor is row-buffer based?
4. Should the unauthorized POST-create no-side-effect assertions include `dms.ReferentialIdentity`, update-tracking/journal rows, and ownership-token stamping artifacts in addition to `dms.Document`, root rows, and child rows?

### Answers 3

1. Let proposed-value relationship authorization win for unauthorized create-new requests. After operation-neutral authorization planning succeeds and the request is valid enough to resolve the POST target identity as create-new, extract and evaluate the proposed EdOrg authorization values before applying the create-new `If-Match` 412 rule. If proposed-value relationship authorization fails, return relationship-not-authorized/403, insert nothing, and do not expose a 412 response. If proposed-value relationship authorization succeeds and `If-Match` is present, return the explicit upsert ETag mismatch/412 result, insert nothing, and do not execute the create persistence path. This preserves DMS-1005 semantics for callers authorized to create the proposed resource while avoiding a natural-key existence oracle for unauthorized callers. Earlier operation-neutral security-configuration and known-but-not-enabled planning failures still keep their existing Slice 3 precedence.
2. This state should not be reachable in normal Slice 3 orchestration when the normalized EdOrg claim list is empty, because the no-claims relationship failure short-circuits before request-dependent processing. Once create-new reaches proposed-value authorization with a finalized root `RowBuffer` and a non-empty EdOrg claim context, extract required proposed EdOrg subject values from the row buffer first; if any authorization-required proposed value is missing/null, return relationship-not-authorized with proposed element-required failure metadata and no SQL authorization command. This preserves proposed element-required precedence for authorization attempts that can legitimately reach proposed-value extraction without allowing no-claim callers to probe references or target identity.
3. Add focused writable-profile coverage for Slice 3. Add a POST-create authorization orchestration unit test that supplies a `ProfileAppliedWriteRequest.WritableRequestBody` whose finalized root `RowBuffer` differs from the raw request body and asserts that proposed authorization parameters and element-required metadata come from the finalized row buffer. Keep provider integration coverage focused on no-profile PostgreSQL and SQL Server authorization/abort behavior; this profile test is to protect the body-selection and row-buffer boundary, not to create a new E2E matrix.
4. Yes. Expand unauthorized POST-create no-side-effect assertions to include all create-side artifacts that would exist only if the create progressed: no `dms.ReferentialIdentity` rows for the attempted identity, no `dms.DocumentChangeEvent`/update-tracking journal rows for the attempted document/resource, and no `dms.Document` row carrying a `CreatedByOwnershipTokenId`. Because the ownership token is stamped on `dms.Document`, the concrete assertion is that no document row exists with that ownership stamp. Do not assert that global sequences or identity generators did not advance; assert only persisted rows/artifacts.
