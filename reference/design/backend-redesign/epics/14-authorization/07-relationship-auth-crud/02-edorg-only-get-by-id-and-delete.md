---
jira: DMS-1056
jira_url: https://edfi.atlassian.net/browse/DMS-1056
---

# Slice 2: EdOrg-only GET-by-id And DELETE

## Purpose

Use the operation-neutral core from Slice 1 to implement the first vertical relationship CRUD authorization path: EdOrg-only stored-value checks for GET-by-id and DELETE.

These operations are grouped because they authorize only the already-stored root resource values and do not need proposed request-body authorization.

## In Scope

- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for GET-by-id.
- `RelationshipsWithEdOrgsOnly` and `RelationshipsWithEdOrgsOnlyInverted` for DELETE.
- Stored-value `EXISTS` checks using concrete root-table EdOrg subjects from Slice 1.
- GET-by-id authorization and reconstitution under the same observed read boundary. A single statement/command is acceptable but not required.
- DELETE authorization, `If-Match`, and delete execution under a shared locked transaction/session boundary. A single provider-specific command is acceptable but not required.
- PostgreSQL and SQL Server SQL generation/execution coverage.
- Minimal relationship authorization failure mapping compatible with Slice 6 hardening.
- Extend the GET-by-id and DELETE backend-to-handler result contracts with the explicit temporary not-implemented and security-configuration failure variants needed by this slice.

## Explicitly Out Of Scope

- POST-create authorization.
- PUT and POST-as-update authorization.
- People-involved relationship authorization.
- Final mixed-strategy composition with NamespaceBased, OwnershipBased, custom view-based, or People relationship strategies. Slice 2 keeps the temporary explicit not-implemented/fail-closed behavior for those known staged strategies.
- Exact final ProblemDetails wording, hint aggregation, and EdOrg claim formatting hardening.
- New auth database objects or DDL.

## Runtime Behavior

### GET-by-id

- Resolve the target document according to the existing GET-by-id flow.
- If the document does not exist, preserve existing not-found behavior.
- If the existing relational GET-by-id flow has a target-resolution `If-None-Match` / 304 short-circuit, preserve it; this slice must not add an authorization-only roundtrip after a request has already short-circuited. If that short-circuit is not present when Slice 2 starts, conditional GET remains out of scope.
- If the effective authorization set includes any known-but-not-enabled non-EdOrg strategy kind from Slice 1, fail explicitly through the temporary not-implemented result surface rather than partially authorizing with only the EdOrg subset.
- If Slice 1 classification/planning returns a security-configuration failure, surface it through an explicit configuration-failure result and stop before running authorization SQL or reconstitution.
- Before reconstitution, execute the relationship authorization check against stored root-table values under the same observed read boundary used for hydration.
- The read boundary may be implemented as a single statement/CTE, a single provider batch, an explicit read-only transaction/isolation choice, or a `ContentVersion` guard/retry.
- If a `ContentVersion` guard detects that the target changed between authorization and hydration, retry from target resolution/authorization or fail deterministically; do not hydrate a representation authorized against stale root EdOrg values.
- If authorization fails, return 403 and do not execute reconstitution.
- If authorization succeeds, continue with the existing reconstitution behavior.

### DELETE

- Resolve the target document and existing delete preconditions according to the current delete path.
- If the effective authorization set includes any known-but-not-enabled non-EdOrg strategy kind from Slice 1, fail explicitly through the temporary not-implemented result surface rather than partially authorizing with only the EdOrg subset.
- If Slice 1 classification/planning returns a security-configuration failure, surface it through an explicit configuration-failure result and stop before delete SQL/precondition execution.
- After target existence is established, lock or otherwise guard the target `dms.Document` row inside the write transaction/session.
- Before deleting, execute the relationship authorization check against stored root-table values observed under that same lock/guard.
- If authorization fails, return 403 before `If-Match` comparison and do not execute the delete statement, even when an existing-target `If-Match` check would also fail.
- If authorization succeeds, preserve the existing `If-Match` behavior and execute the existing delete statement while the same lock/guarded target context remains valid.

## Acceptance Criteria

- GET-by-id for an EdOrg-only relationship resource returns the document only when the caller has access through at least one configured EdOrg-only relationship strategy.
- GET-by-id with `RelationshipsWithEdOrgsOnlyInverted` uses inverted Source/Target hierarchy filtering.
- GET-by-id with multiple relationship strategies ORs the strategies and keeps each strategy's configured index metadata.
- GET-by-id with multiple concrete root-table EdOrg subjects inside one strategy ANDs the subjects.
- When the existing GET-by-id path already supports an `If-None-Match` / not-modified short-circuit, Slice 2 preserves it and does not add a separate authorization-only roundtrip for requests that stop there. Slice 2 does not implement conditional GET if it is not already present.
- GET-by-id stored-value authorization and hydration use the same observed read boundary; Slice 2 does not require one database command if a transaction/isolation choice or `ContentVersion` guard/retry proves the same-state behavior.
- GET-by-id does not authorize against one committed set of root EdOrg values and hydrate a different committed representation for the same response.
- Unauthorized GET-by-id returns 403 without running reconstitution queries.
- DELETE for an EdOrg-only relationship resource deletes the document only when the caller has access through at least one configured EdOrg-only relationship strategy.
- DELETE uses a shared locked transaction/session boundary for stored-value authorization, `If-Match`, and delete execution; Slice 2 does not require one provider-specific authorization/`If-Match`/delete command.
- If the effective single-record authorization set also contains any known-but-not-enabled non-EdOrg strategy kind from Slice 1, GET-by-id and DELETE fail explicitly as not implemented until the owning strategy story lands; Slice 2 does not partially authorize with only the EdOrg subset.
- GET-by-id and DELETE backend-to-handler result contracts explicitly model the temporary 501 not-implemented staging surface for known-but-not-enabled mixed strategies instead of routing that case through exceptions or generic unknown failures.
- Unauthorized DELETE returns 403 without deleting `dms.Document` or resource rows.
- For existing targets where stored-value relationship authorization and `If-Match` would both fail, DELETE returns 403 rather than 412.
- Empty EdOrg claims for a single-record stored-value check return 403 rather than the GET-many empty-page behavior.
- Missing/null stored EdOrg values required for authorization produce the relationship invalid-data failure metadata consumed by Slice 6.
- When a failed relationship OR group contains mixed failure kinds, the backend result preserves all failed strategy/subject metadata in configured order and carries enough failure-kind detail for Slice 6 to apply the shared ProblemDetails precedence rule, including proposed-value failures emitted by later slices: existing stored-value invalid data, proposed-value element required, then no relationship established.
- Security-configuration failures from Slice 1 surface as configuration failures, not as 403 authorization denials.
- GET-by-id and DELETE backend-to-handler result contracts explicitly model security-configuration failures from Slice 1 so handlers can return the canonical 500 security-configuration ProblemDetails rather than generic unknown/system-error responses.
- PostgreSQL uses `dms.throw_error('AUTH1', ...)` or the established PostgreSQL AUTH1 mechanism for aborting unauthorized batches.
- SQL Server uses the established `CAST('AUTH1 - ...' AS INT)` batch-abort pattern.
- SQL Server parameter binding uses scalar parameters below 2,000 unique EdOrg IDs and `dms.BigIntTable` at 2,000 or more.

## Tests Required

### Unit tests

- GET-by-id SQL/check composition for normal and inverted EdOrg-only strategies.
- DELETE SQL/check composition for normal and inverted EdOrg-only strategies.
- OR composition across two relationship strategies.
- AND composition across multiple root-table EdOrg subjects.
- Supported EdOrg strategy plus known-but-not-enabled non-EdOrg strategy produces the explicit not-implemented staging result instead of partial authorization.
- GET-by-id and DELETE result/handler mapping for the explicit not-implemented and security-configuration result variants.
- If conditional GET support already exists in the relational GET-by-id path, GET-by-id preserves the existing `If-None-Match` / not-modified short-circuit behavior.
- GET-by-id command composition or guard behavior proves authorization and hydration use the same observed read boundary.
- DELETE command/session composition proves stored-value authorization runs under the same lock/guarded target context used for `If-Match` and delete execution.
- DELETE authorization failure takes precedence over existing-target `If-Match` mismatch.
- Empty EdOrg claim list produces single-record unauthorized behavior.
- AUTH1 failure-set payload parsing maps the emitted check index and plan-relative strategy/subject ordinals back to the configured strategy metadata.
- Relationship failure payload mapping preserves all failed OR-strategy entries and failure kinds so Slice 6 can choose the top-level ProblemDetails case without re-querying.

### Backend integration tests

- PostgreSQL and SQL Server authorized GET-by-id returns reconstituted content.
- PostgreSQL and SQL Server unauthorized GET-by-id returns 403 and does not run reconstitution.
- PostgreSQL and SQL Server GET-by-id coverage proves authorization and hydration cannot observe different committed root EdOrg values for one response through the selected read-boundary implementation.
- PostgreSQL and SQL Server authorized DELETE removes the document.
- PostgreSQL and SQL Server unauthorized DELETE leaves `dms.Document` and resource rows intact.
- PostgreSQL and SQL Server DELETE coverage proves authorization, `If-Match`, and delete execution share the selected locked transaction/session boundary.
- PostgreSQL and SQL Server supported-EdOrg plus known-but-not-enabled mixed strategies fail explicitly as not implemented for GET-by-id and DELETE.
- Security-configuration failures from Slice 1 surface as canonical 500 security-configuration responses for GET-by-id and DELETE, not as generic unknown/system-error responses.
- PostgreSQL and SQL Server DELETE returns 403 rather than 412 when stored-value relationship authorization and existing-target `If-Match` both fail.
- Inverted strategy tests prove Source/Target filtering is swapped.

### E2E tests

- Focused DMS E2E coverage with real claim-set/token wiring for one GET-by-id scenario and one DELETE scenario, including an unauthorized case.

## Reviewer Focus

Reviewers should focus on whether the operation-neutral core can be consumed without reintroducing GET-many-specific assumptions, whether GET-by-id authorization and hydration share the same observed read state, and whether unauthorized requests abort before expensive or mutating work.

## Clarifying Questions and Answers

### Questions 1

1. For GET-by-id and DELETE in this slice, if the effective CRUD authorization set includes a supported EdOrg relationship strategy plus any known-but-not-enabled strategy kind from Slice 1 (People relationship, NamespaceBased, OwnershipBased, or custom view-based), should the endpoint fail fast rather than partially authorizing with only the EdOrg subset, or should it compose with any other CRUD strategy execution that happens to exist by merge time?
2. For an existing target where both conditional-header logic and the new stored-value relationship authorization would fail, what response precedence should Slice 2 enforce: should GET-by-id `If-None-Match` / DELETE `If-Match` preserve the current precondition flow, or should the new 403 authorization result win?

### Answers 1

1. Fail fast. Slice 2 remains an EdOrg-only vertical slice. If the effective single-record authorization set includes any known-but-not-enabled strategy kind from Slice 1 (People relationship, NamespaceBased, OwnershipBased, or a convention-matching custom view-based strategy), do not partially authorize with only the EdOrg subset and do not opportunistically compose with whatever other CRUD execution might exist by merge time. Return the temporary explicit not-implemented/fail-closed staging surface for those known strategies, matching the DMS-1055 staging model. `NoFurtherAuthorizationRequired` remains a no-op, and true security-configuration failures still win as configuration failures.
2. Split the precedence by operation.
   GET-by-id should preserve the `auth.md` roundtrip model: if the roundtrip-1 `If-None-Match` / not-modified check short-circuits, keep that existing behavior rather than adding a separate authorization-only roundtrip just to convert it into 403.
   DELETE should return 403 when stored-value relationship authorization and existing-target `If-Match` would both fail. DMS-1005 and the namespace deferred-guard notes establish that authorization failures for existing targets must not be overridden by `412` precondition failures. After authorization passes, preserve the normal `If-Match` behavior.

### Questions 2

1. Slice 2 now requires GET-by-id and DELETE to surface both temporary 501 not-implemented results for known-but-not-enabled mixed strategies and 500 security-configuration failures from Slice 1, but the current single-record result contracts do not model those surfaces consistently (`GetResult` has 501 but no security-configuration variant; `DeleteResult` has neither). Should this slice extend the GET/DELETE backend-to-handler result contracts now with explicit 501/500 variants, or should those failures surface through exceptions/generic unknown-failure mapping until a later cleanup story?

### Answers 2

1. Extend the GET-by-id and DELETE backend-to-handler result contracts now. Slice 1 explicitly deferred CRUD result-shape changes to Slice 2+, and this slice is the first vertical owner of these GET-by-id/DELETE failure surfaces. Do not route known temporary 501 staging outcomes or Slice 1 security-configuration failures through exceptions or `UnknownFailure`; those are intentional, user-visible authorization outcomes, not unexpected faults. `GetResult` should add an explicit security-configuration variant, and `DeleteResult` should add both temporary not-implemented and security-configuration variants, with handler mapping to 501 and the canonical DMS-1099-compatible 500 security-configuration ProblemDetails.

### Questions 3

1. If the relational GET-by-id path does not already have an `If-None-Match` / 304 target-resolution short-circuit when Slice 2 starts, should Slice 2 add that behavior before wiring relationship authorization, or is conditional GET a prerequisite/out of scope and this slice should only preserve whatever behavior already exists?
2. For relationship authorization denials, should Slice 2 extend `GetFailureNotAuthorized` / `DeleteFailureNotAuthorized` or add a parallel result payload to carry structured relationship failure metadata such as configured strategy index, value source, failure kind, securable names, and hints for Slice 6, or keep the current string-error surface until Slice 6 replaces it?
3. For stored root EdOrg values that are null/uninitialized, should the Slice 2 SQL/runtime path distinguish that case from an ordinary no-relationship denial immediately, and if so should it use separate AUTH1 indexes/failure kinds per subject/strategy or a post-failure metadata lookup?
4. If an OR relationship group has mixed failures, for example one EdOrg-only strategy has a null stored subject while another strategy simply has no matching relationship, what should Slice 2 return before Slice 6 hardening: all failing strategy metadata in deterministic order, only the first emitted check failure, or a coarse 403 with enough information for Slice 6 to re-query/aggregate later?
5. If the relational schema or API validation prevents creating null root EdOrg values through normal requests, should Slice 2 cover stored-null invalid-data behavior only with unit tests, or should backend integration tests seed the invalid stored-data state directly with SQL fixtures?

### Answers 3

1. Conditional GET is out of scope for Slice 2. If the relational GET-by-id path already has a target-resolution `If-None-Match` / 304 short-circuit, preserve it and do not add an authorization-only roundtrip for requests that stop there. If that short-circuit does not exist when Slice 2 starts, do not implement it as part of this story; wire relationship authorization into the existing GET-by-id flow and leave conditional GET to its owning read/concurrency work.
2. Add structured relationship failure metadata now. Extend the existing not-authorized result surface, or add an explicit relationship-not-authorized payload under it, so GET-by-id and DELETE denials can carry Slice 1 metadata instead of only a string. The payload should include stored-value source, emitted AUTH1 index, configured strategy index/order, strategy name/kind, failure kind, contributing securable element metadata/readable names/paths, root table/column bindings, auth object/hint metadata, and normalized EdOrg claim context. Handlers can still render a minimal transitional 403 before Slice 6, but the backend result must not discard metadata that Slice 6 needs.
3. Yes. Slice 2 should distinguish stored null/uninitialized root EdOrg values from ordinary no-relationship denials because `auth.md` defines the existing-data invalid-data case and this story already promises metadata for Slice 6. Detect this in the stored-value authorization SQL/check plan and produce typed relationship invalid-data failure entries for the affected strategy subjects. Do not rely on a post-failure database lookup. Also do not emit separate early-aborting checks that would break relationship OR semantics; evaluate the OR group, and only if no strategy authorizes, return an AUTH1 failure whose metadata identifies each failed strategy/subject and failure kind.
4. When the relationship OR group fails as a whole, return all failed relationship-strategy metadata in deterministic configured-strategy order. Before Slice 6, the HTTP 403 body can remain minimal/transitional, but the result payload should contain the full failure set, including mixed invalid-data and no-relationship failures, so Slice 6 can format without re-querying or guessing. If any OR strategy succeeds, the operation is authorized and failures from other OR strategies should not surface.
5. Add backend integration coverage that seeds the invalid stored-data state directly with SQL fixtures, in addition to focused unit tests. The normal API path may prevent null root EdOrg values today, but `auth.md` treats authorization-layer validation as a defensive check for future nullable securable elements and corrupted or legacy data. Integration tests should prove PostgreSQL and SQL Server classify stored nulls as relationship invalid-data metadata and still avoid reconstitution/deletion on authorization failure.

### Questions 4

1. The story now requires GET-by-id and DELETE not-authorized results to carry structured relationship failure metadata across the backend-to-handler boundary, but the current rich relationship metadata lives in backend planning contracts while `GetResult` and `DeleteResult` live in `Core.External.Backend`. Should Slice 2 promote a relationship-authorization failure DTO into `Core.External` and put it directly on the GET/DELETE result variants, or should it introduce another cross-boundary contract shape?
2. Because AUTH1 aborts currently communicate only an emitted check index, what exact mechanism should Slice 2 use to carry runtime-dependent failure details such as stored-null versus no-relationship and the full failed OR-strategy set without a post-failure lookup: encode a deterministic failure-set key in the AUTH1 message, generate separate abort branches per failure kind/strategy, return diagnostic rows before aborting, or something else?
3. For DELETE with `If-Match`, should Slice 2 compose stored-value authorization, ETag validation, and delete into one provider-specific batch/command, including any required row lock or guarded-delete predicate so authorization and mutation use the same observed stored values, or is it acceptable to run authorization as an earlier command in the existing write transaction before the current ETag checker/delete sequence?

### Answers 4

1. Add a small relationship authorization failure DTO in `Core.External.Backend` and carry it directly on the GET-by-id and DELETE not-authorized result variants, or on explicit relationship-not-authorized variants under those result contracts. Do not expose `Backend.Plans` types through `Core.External`; adapt the rich planning metadata into the external DTO at the backend boundary. The DTO should be a stable transport contract containing only the fields the handler/Slice 6 formatter needs: value source, emitted AUTH1 index, configured strategy index/order, strategy kind/name, failure kind, contributing securable element/readable-name/path metadata, root table/column bindings, auth object/hint metadata, and normalized EdOrg claim context.
2. Use one AUTH1 abort for the failed relationship OR group and encode a deterministic compact failure-set payload in the AUTH1 message alongside the emitted check index. The SQL should evaluate every strategy/subject in the OR group, determine whether any strategy authorized, and only when none did throw `AUTH1` with enough runtime facts to map each failed strategy/subject to `stored-null` versus `no-relationship` in configured order. The backend then maps that payload back to the precomputed plan metadata; it must not run a post-failure lookup. Do not emit separate early-aborting branches per strategy/failure kind, because that loses OR semantics, and do not rely on diagnostic result rows before an exception because provider behavior is awkward and unnecessary.
3. Do not run stored-value authorization as a standalone unlocked command before the current ETag checker/delete sequence. Slice 2 should make DELETE a composed locked authorization/delete path: after target existence is known, lock or otherwise guard the target `dms.Document` row inside the write transaction, run stored-value relationship authorization against the root rows observed under that lock, return 403 on AUTH1 before checking `If-Match`, then compare `If-Match` and execute the delete while the lock is still held. This may be implemented as one provider-specific batch/command, or as tightly sequenced commands in the same write session if the lock and observed target context are shared, but the behavior must preserve 403-over-412 precedence and prevent authorization and deletion from observing different stored values.

### Questions 5

1. For GET-by-id, should the stored-value authorization check and reconstitution observe the same database snapshot or locked target state so authorization cannot pass on one set of root EdOrg values while hydration returns a concurrently changed representation, or is the existing read-path consistency model sufficient as long as authorization precedes hydration in the same roundtrip?
2. Should Slice 2 define and test a stable provider-independent schema/parser for the compact AUTH1 failure-set payload, including SQL Server cast-message parsing/truncation behavior, or may the payload format remain an internal implementation detail as long as it deterministically produces the required `Core.External.Backend` relationship failure DTO?

### Answers 5

1. GET-by-id should use the same observed read state for stored-value authorization and hydration, but it should not introduce blocking write-style locks as the normal read strategy. Same roundtrip is not enough if the provider batch can observe different committed versions across statements. Compose the stored-value relationship check with the hydration/keyset batch under one provider-consistent read boundary: a single statement/CTE shape, an explicit read-only transaction/isolation choice, or a `ContentVersion` guard/retry are all acceptable implementation techniques. A concurrent write may cause GET-by-id to read either the before or after state, but it must not authorize against the before state and hydrate the after state. The task breakdown should add this read-consistency requirement for GET-by-id; tests can focus on command composition/guard behavior rather than a broad concurrency stress suite.
2. Define and test a stable provider-independent internal schema/parser for the compact AUTH1 failure-set payload in Slice 2. It is not a public API or ProblemDetails contract, but it is a cross-provider SQL-to-backend contract and should not remain free-form message text. Use a versioned, compact, bounded grammar that carries the emitted check index plus plan-relative strategy/subject ordinals and short failure-kind codes, with no user-readable names, JSON paths, EdOrg claim lists, or other large values. PostgreSQL and SQL Server may have different exception wrappers, but after extracting the AUTH1 text they should use the same parser. Unit tests should cover PostgreSQL `throw_error`, SQL Server `CAST('AUTH1 - ...' AS INT)` parsing, malformed/unknown versions, and conservative SQL Server message-length/truncation behavior. The backend then maps the parsed ordinals to the precomputed plan metadata and produces the `Core.External.Backend` relationship failure DTO without a post-failure database lookup.

### Questions 6

1. The story still says GET-by-id authorization checks are batched into the same roundtrip as reconstitution, but Answer 5 says same roundtrip is not enough and allows a single statement/CTE shape, an explicit read-only transaction/isolation choice, or a `ContentVersion` guard/retry. For Slice 2 tasking and tests, is a single database command still required, or is a multi-command read transaction/guard acceptable if it proves authorization and hydration use the same observed state and does not add authorization work after a 304 short-circuit?
2. The story still says DELETE authorization checks are batched into the same roundtrip as delete execution, while Answer 4 allows either one provider-specific batch/command or tightly sequenced commands in the same write session under a shared lock. Should the acceptance criteria be relaxed to a locked transaction/session boundary, or must Slice 2 deliver one provider-specific authorization/If-Match/delete command?
3. Should Slice 2 apply relationship authorization only to external `RelationalGetRequestReadMode.ExternalResponse` GET-by-id requests, leaving `StoredDocument` internal read-modify-write fetches authorization-bypassed, or should it introduce an explicit internal authorization-bypass contract for stored-document reads?
4. For backend integration coverage of stored-null EdOrg invalid-data metadata, should Slice 2 add a nullable-root-EdOrg synthetic fixture, temporarily relax or bypass DDL constraints in test setup, or treat this as unit-test-only when the selected relational fixture has non-nullable root EdOrg columns?

### Answers 6

1. A single database command is not required for Slice 2. The tasking should replace the older "same roundtrip" wording with "same observed read boundary." A single statement/CTE or single provider batch is still acceptable and likely simplest where it fits existing hydration, but a multi-command read transaction, provider snapshot/read-isolation choice, or `ContentVersion` guard/retry is also acceptable if authorization and hydration cannot observe different committed root EdOrg values for the same GET-by-id response. If a `ContentVersion` guard detects that the target changed between authorization and hydration, retry from target resolution/authorization or fail deterministically; do not hydrate a representation authorized against stale values. Preserve the 304 short-circuit: if target resolution plus `If-None-Match` stops the request, do not run a later authorization-only command.
2. Relax the Slice 2 DELETE acceptance criteria to a locked transaction/session boundary. One provider-specific authorization/`If-Match`/delete command is preferred when it is natural, but it is not required. The required contract is: after existence is established, lock or otherwise guard the target `dms.Document` row in the write transaction, authorize stored root values observed under that lock, return 403 before `If-Match` comparison when authorization fails, then evaluate `If-Match` and execute the delete while the same lock/guarded target context is still valid. Tests should assert ordering, 403-over-412 precedence, and unchanged rows on authorization failure, not a specific one-command topology.
3. Apply relationship authorization only to public external GET-by-id materialization: `RelationalGetRequestReadMode.ExternalResponse`. `StoredDocument` reads are internal read-modify-write/current-state fetches and must remain authorization-bypassed because the owning write/update/delete flow performs its own operation-specific authorization before mutation or response. Do not leave that as an accidental side effect of the read mode alone; Slice 2 tasks should make the bypass explicit in the backend-local contract or guard rails, for example with an internal authorization-bypass purpose/reason tied to `StoredDocument`, and tests should prove public GET handlers cannot request the bypass.
4. Add a nullable-root-EdOrg synthetic backend integration fixture for the stored-null invalid-data case. Do not relax or bypass production DDL constraints for a normal relational fixture, and do not downgrade this to unit-test-only. If the standard selected resource has non-nullable root EdOrg identity columns, use a test-only mapping/security fixture whose root EdOrg authorization subject is nullable, seed the null state directly with SQL, and verify PostgreSQL and SQL Server map the failed stored-value authorization to relationship invalid-data metadata without reconstitution or deletion.

### Questions 7

1. When a failed relationship OR group contains mixed failure kinds, for example one strategy has an existing stored EdOrg value that is null while another strategy simply has no matching relationship, which single ProblemDetails `type` and `detail` should Slice 6 choose?

### Answers 7

1. Use a deterministic failure-kind precedence rule for the failed relationship OR group:
   1. Existing stored-value invalid data / element uninitialized.
   2. Proposed-value element required.
   3. No relationship established / no matching authorization relationship.

   Slice 2 GET-by-id and DELETE can emit stored-value invalid-data and no-relationship failures; the proposed-value precedence slot is defined here for Slice 3, Slice 4, and Slice 6 so they do not invent a parallel rule. Slice 2 must still carry the full failed strategy/subject set in configured order across the backend-to-handler boundary. Slice 6 chooses the top-level ProblemDetails `type`, `detail`, and primary error text from the highest-precedence failure kind present. If multiple failures share that selected precedence, aggregate their readable securable names, strategy identity, and hints using the deterministic ordering rules. Lower-precedence entries must not hide or downgrade the selected top-level type. If any OR strategy succeeds, the operation is authorized and failures from other OR strategies should not surface.
