---
jira: DMS-1229
jira_url: https://edfi.atlassian.net/browse/DMS-1229
related_jira:
  - DMS-1222
labels:
  - relational-backend
  - stretch-goal
---

# Bug Design: ODS-Compatible Profile Write Handling

## Context

`DMS-1222` relational profile-write E2E conversion exposed a mismatch between the current DMS relational profile write pipeline, legacy ODS behavior, and several profile E2E scenarios. The affected scenarios were quarantined with `@ignore` until DMS implements the intended product behavior.

The current DMS relational path treats submitted data outside the writable profile as a uniform writable-profile validation failure. In practice, `WritableRequestShaper` removes hidden data from `WritableRequestBody`, records `ForbiddenSubmittedData` failures for submitted hidden members or scopes, and `ProfileWritePipeline` short-circuits before the relational backend write handler runs. The API layer commonly maps these failures to `400 data-policy-enforced`, even when the failure is not a creatability/data-policy violation.

Legacy ODS behavior is hybrid:

- Hidden scalar, nested object, and reference members submitted under a writable profile are generally accepted and ignored during write mapping.
- Identifying members and references are implicitly preserved even when not listed by an IncludeOnly profile and cannot effectively be excluded by an ExcludeOnly profile.
- Creatability remains separate. A create that needs required data hidden by the profile is rejected as data-policy-enforced.
- Submitted collection items that fail a profile value filter are rejected as data validation failures, not silently pruned.
- Existing hidden stored values are preserved on PUT.

This bug design records the intended DMS behavior for `DMS-1229`. Where this document conflicts with the older relational profile design docs that describe all forbidden submitted data as validation failures, this document is the ticket-specific compatibility update.

## Related Design

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/01a-c3-request-visibility-and-writable-shaping.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/01a-c8-typed-profile-error-classification.md`
- `reference/design/backend-redesign/epics/07-relational-write-path/05b-profile-error-classification.md`

## Scope

The scope is DMS profile write behavior for relational backend requests using writable profiles. The change is primarily in DMS Core profile request shaping and failure mapping, with backend changes only if the current Core/backend profile write contract is insufficient to preserve hidden stored data or identity references.

In scope:

- Ordinary hidden submitted profile members are ignored during writable request shaping.
- Hidden submitted members remain absent from `WritableRequestBody`.
- Existing hidden stored data is preserved during PUT.
- Required identity/reference members are implicitly writable for identity preservation when needed by the resource identity.
- Submitted collection items that fail profile value filters remain request failures.
- Collection value-filter failures are surfaced as `400 data-validation-failed`.
- Creatability failures remain `400 data-policy-enforced`.
- Quarantined relational profile E2E scenarios are updated and unignored where they now match the intended behavior.

Out of scope:

- Readable profile projection behavior.
- Profile OpenAPI/discovery contract changes, except where tests need wording updates.
- Extension profile name normalization covered by `DMS-1233`.
- Changing server-generated field handling for `id`, `link`, `_etag`, or `_lastModifiedDate`.
- Replacing the relational profile write merge architecture.

## Behavior Matrix

| Case | Intended Behavior | Response |
| --- | --- | --- |
| Hidden scalar member submitted, such as `webSite` under an IncludeOnly school write profile | Accept request. Remove the member from `WritableRequestBody`. Do not emit `ForbiddenSubmittedData`. | Success if remaining visible request is valid and creatable |
| Hidden nested/object member submitted, such as `addresses[*].nameOfCounty` when excluded | Accept request. Remove hidden member from the shaped body. On PUT, preserve existing stored value. | Success if remaining visible request is valid |
| Hidden non-collection scope submitted | Treat as hidden profile input. Do not write the submitted scope. Preserve existing stored scope on PUT. | Success unless creatability or another validation rule fails |
| Hidden collection submitted | Treat as hidden profile input. Do not write submitted hidden collection rows. Preserve existing stored rows on PUT. | Success unless creatability or another validation rule fails |
| Hidden extension scope submitted, such as `_ext.sample` when the active write profile hides that extension | Treat as hidden profile input. Do not write the submitted extension scope. Preserve existing stored extension data on PUT. | Success unless creatability, invalid profile usage, or another validation rule fails |
| Hidden submitted member is required for create | Ignore the submitted hidden value. Evaluate creatability from the visible write surface. | `400 data-policy-enforced` if the new resource/scope/item is not creatable |
| PUT excludes a required stored field | Allow update when the existing stored resource already has the hidden required value. Preserve the hidden stored value. | Success |
| IncludeOnly profile omits resource identity references needed by the resource key | Preserve/implicitly include the identity reference data needed to compute and persist identity. | Success if the request is otherwise valid |
| ExcludeOnly profile tries to exclude identity members | Identity members remain effectively included, matching ODS behavior. | Success or profile-definition rejection depending on existing validation rules; do not silently lose identity |
| Submitted collection item fails a profile value filter | Reject the request. Do not silently prune the item and continue. | `400 data-validation-failed` |
| Stored collection item fails profile value filter during PUT | Treat the stored item as hidden for the active profile and preserve it. | Success if visible request is valid |
| Duplicate visible collection items after shaping | Reject as writable-profile validation/data validation. | `400 data-validation-failed` |
| Server-generated field submitted | Preserve existing DMS rejection behavior. Server-generated fields are outside the profile namespace. | Existing data-validation/bad-request behavior |

## Core Request Shaping

`WritableRequestShaper` remains responsible for producing the profile-shaped `WritableRequestBody`, request scope states, visible collection item addresses, and immediate validation failures.

For `DMS-1229`, hidden submitted members and hidden submitted scopes must be treated as shape-only events:

1. Omit hidden members/scopes from `WritableRequestBody`.
2. Emit the same visibility metadata needed by backend merge code.
3. Do not add `ForbiddenSubmittedData` failures for ordinary hidden submitted scalar, object, collection, or reference data.

Extension scopes under `_ext` follow the same rule when the extension scope is known and the active profile hides it. A submitted hidden extension scope should be omitted from `WritableRequestBody`, should still emit the visibility metadata needed for merge/preservation, and should not produce `ForbiddenSubmittedData`. This does not change invalid extension names, unsupported extension profile usage, server-generated field handling, or extension collection value-filter failures.

Collection value filters are different. If a submitted collection item does not pass the active profile's collection item filter, the shaper should still emit an immediate validation failure and exclude that item from the shaped body. The pipeline must not allow the request to succeed after silently dropping invalid submitted collection items.

The failure type may continue using the shared writable-profile validation failure contract internally, but response mapping must distinguish collection value-filter violations from creatability/data-policy failures so API clients see the ODS-compatible `data-validation-failed` problem type.

## Identity And Reference Preservation

Legacy ODS implicitly includes identifying members and references in writable profiles. DMS must do the same for resource identity and reference members needed to compute the resource identity, even when an IncludeOnly write profile does not explicitly list the top-level reference object.

The known DMS-1229 example is Calendar:

- `schoolReference.schoolId`
- `schoolYearTypeReference.schoolYear`

An IncludeOnly Calendar write profile may list `calendarCode`, `calendarTypeDescriptor`, and `gradeLevels` while omitting `schoolReference` and `schoolYearTypeReference`. The write pipeline must still preserve the identity references needed for create and PUT merge.

Implementation should prefer a shared identity-preservation primitive derived from schema identity paths or existing identity extraction helpers rather than hard-coding resource names. The rule should apply to both IncludeOnly and ExcludeOnly modes:

- IncludeOnly: identity top-level members/references are effectively included.
- ExcludeOnly: identity top-level members/references cannot be effectively excluded from write processing.

This identity preservation does not grant write access to unrelated hidden members of the same object unless they are part of the identity surface or otherwise visible by the profile.

## Creatability

Creatability stays separate from request-side hidden-member shaping.

If a POST or PUT-as-create requires a new root resource, non-collection scope, collection item, extension scope, or extension collection item whose required data is hidden by the profile, the request is non-creatable. The submitted hidden value must not be used to make the create succeed. This preserves the ODS distinction between:

- hidden input being ignored for mapping, and
- profile capability determining whether a new visible instance can be created.

Existing PUTs may still succeed when the hidden required data already exists in stored state. The backend merge must preserve hidden stored data instead of clearing or overwriting it from the shaped request.

## API Error Mapping

DMS should expose these client-visible problem types:

- Creatability failures: `urn:ed-fi:api:data-policy-enforced`.
- Submitted collection value-filter failures: `urn:ed-fi:api:bad-request:data-validation-failed`.
- Duplicate visible collection item collisions after shaping: `urn:ed-fi:api:bad-request:data-validation-failed`.
- Invalid profile usage or invalid profile definitions: preserve existing profile-specific behavior unless directly affected by this ticket.
- Core/backend contract mismatches and binding-accounting failures: preserve existing server-error handling.

The current generic profile failure mapping in `ProfileWritePipelineMiddleware` is too coarse for DMS-1229 because it maps non-server profile failures to `ForDataPolicyEnforced`. That mapping should be refined so value-filter validation failures are not reported as required-field policy failures.

## Validation And Extraction Boundary (Schema-Valid-Only Compatibility)

DMS-1229's "accept and ignore hidden submitted data" rule is scoped to **schema-valid** submitted data. Profile shaping (`ProfileWritePipelineMiddleware` / `WritableRequestShaper`) runs *after* document/decimal validation and *after* relational reference extraction in the `ApiService` upsert/update pipelines, and those earlier stages operate on the raw submitted body and are profile-unaware. The compatibility boundary is therefore:

- **Schema-valid hidden scalar, object, and reference data is accepted and ignored.** The shaper strips it from `WritableRequestBody` with no failure, and the backend shaped-reference filter drops any reference/descriptor no longer present in the shaped body.
- **Schema-invalid hidden data can still fail pre-profile validation.** A hidden member with the wrong type (or an invalid decimal) is rejected by `ValidateDocumentMiddleware` / `ValidateDecimalMiddleware` before shaping can strip it.
- **Malformed hidden references can still fail extraction.** A reference object missing identifying values is rejected by relational reference extraction (`ExtractDocumentInfoMiddleware` / `ReferenceExtractor` in `RelationalWriteValidation` mode) before the backend shaped-reference filter runs.
- **Complete hidden references are ignored by the backend shaped-reference filter.** A fully-formed reference the profile hides extracts cleanly and is then dropped from the resolved set by `ProfileWriteReferenceFilter`, so it is neither written nor rejected as unresolved.

This matches the legacy ODS surface for the well-formed submitted-data shapes the Behavior Matrix covers. Literal ODS bind-time tolerance of *invalid* hidden values is intentionally **out of scope** for DMS-1229; achieving it would require making document validation and reference extraction profile-aware, which this ticket does not do. If that broader compatibility is later required, it is a separate behavior change, not a documentation/test update.

## Persistence And Backend Boundary

The relational backend should continue to consume Core-supplied profile write contracts:

- `WritableRequestBody`
- `RequestScopeStates`
- `VisibleRequestCollectionItems`
- `ProfileAppliedWriteContext`
- stored-side scope and collection visibility metadata
- hidden member paths

Backend should not re-evaluate profile member filters or profile value filters. Backend changes are in scope only if existing Core contract data does not let the profile-aware merge preserve hidden stored values or identity references after the DMS-1229 Core shaping changes.

The no-profile write path must remain unchanged.

## E2E Coverage

The quarantined relational profile E2E scenarios should be updated and unignored according to the behavior matrix.

Hidden member ignore/preserve scenarios expected to succeed:

- `ProfileWriteFiltering.feature`
  - Scenario 01: IncludeOnly write profile strips excluded fields.
  - Scenario 02: IncludeOnly write profile preserves identity and allowed fields.
  - Scenario 03: ExcludeOnly write profile strips excluded fields.
- `ProfileCreatabilityValidation.feature`
  - Scenario 04: PUT with profile excluding required field succeeds.
- `ProfileEmbeddedObjectFiltering.feature`
  - Scenario 03: Write profile excluding nested member does not persist excluded nested update.
- `ProfilePutMerge.feature`
  - Scenario 01: PUT with collection non-key property exclusion preserves excluded property from existing document.
- `ProfileNestedIdentityPreservation.feature`
  - Scenario 01: POST Calendar with IncludeOnly write profile preserves nested identity references.
  - Scenario 02: PUT Calendar with IncludeOnly write profile preserves nested identity references during merge.

Collection value-filter scenarios expected to fail with data validation:

- `ProfileWriteFiltering.feature`
  - Scenario 05: POST with collection item filter rejects submitted non-matching items.
  - Scenario 06: POST with collection item filter rejects a submitted non-matching item.
- `ProfileCreatabilityValidation.feature`
  - Scenario 06: POST with CollectionRule on required collection rejects non-matching submitted items.

Relational E2E runs must use the relational backend setup path. Prefer the repo-root `build-dms.ps1 E2ETest` path with `.env.e2e.relational` and a focused `relational-backend` category filter.

## Unit And Integration Coverage

Core unit tests should cover:

- Hidden scalar submitted under IncludeOnly is stripped with no validation failure.
- Hidden scalar submitted under ExcludeOnly is stripped with no validation failure.
- Hidden non-collection scope submitted is stripped with no validation failure.
- Hidden collection submitted is stripped with no validation failure.
- Hidden extension scope submitted is stripped with no validation failure.
- Hidden submitted null values follow the same ignore/strip behavior.
- Hidden required member submitted on POST is ignored and then fails creatability if the visible profile cannot create the resource.
- Existing PUT with hidden required stored value succeeds and preserves stored value.
- Collection value-filter failure still produces an immediate validation failure.
- Collection value-filter failure maps to data-validation response shape.
- Duplicate visible collection items after shaping still fail as data validation.
- Identity top-level reference members are effectively included from resource identity paths.

The Validation And Extraction Boundary above is locked by existing coverage; each boundary clause maps to a test that fails if the boundary regresses:

- Schema-valid hidden data accepted and ignored: `WritableRequestShaperTests` (hidden scalar, nested object, non-collection scope, collection, extension scope, null-valued, and hidden non-identity reference cases stripped with no failure; identity references preserved).
- Schema-invalid hidden data still fails pre-profile validation: `ValidateDocumentMiddlewareTests.Given_A_Request_With_Wrong_Type_Property_Value`.
- Malformed hidden references still fail extraction: `ReferenceExtractorTests` (partial/invalid nested reference) and `ExtractDocumentInfoMiddlewareTests` (malformed root/nested reference identity member returns a validation response).
- Complete hidden references ignored by the backend shaped-reference filter: `ProfileWriteReferenceFilterTests` (`RetainPresent` drops references/descriptors absent from the shaped body and keeps identity references present).

Backend or profile-merge tests should cover preservation of hidden stored values if existing tests do not already prove:

- hidden root columns are preserved on matched PUT,
- hidden collection item non-key columns are preserved on matched PUT,
- hidden stored collection rows failing a profile value filter are preserved on PUT,
- identity/reference values needed for resource identity survive request shaping and merge.

## Risks And Guardrails

- Do not make all out-of-profile data valid. Collection value-filter violations remain request validation failures.
- Do not use hidden submitted values to satisfy create-time required members. Hidden submitted values are ignored before creatability is evaluated.
- Do not weaken server-generated field rejection.
- Do not move profile semantic evaluation into the backend.
- Do not treat this bug as permission to implement profile partial updates. PUT remains full-document semantics over the visible profile surface, with hidden stored data preserved.
- Keep response type changes narrow. Data-policy remains correct for creatability; data-validation is correct for submitted collection filter violations and duplicate visible collection identities.

## Open Questions

1. Should hidden submitted root identity values under ExcludeOnly profiles be silently preserved even if existing profile definition validation currently rejects ExcludeOnly identity exclusions? The safest default is to keep existing profile-definition validation and apply the implicit identity inclusion rule at runtime for valid profiles.
2. Should the internal `ForbiddenSubmittedDataWritableProfileValidationFailure` type be reused for collection value-filter failures, or should a more specific typed failure be introduced? The safest default is a specific diagnostic or discriminator for value-filter failures so response mapping is explicit.
3. Are collection value-filter failures currently distinguishable from hidden-member failures after shaping? If not, implementation should add the minimum typed distinction needed for stable response mapping.

## Acceptance Criteria

- Hidden submitted scalar, object, collection, reference, and extension-scope members are stripped from writable requests without causing `ForbiddenSubmittedData` failures.
- Submitted collection items failing profile value filters still fail the request.
- Collection value-filter failures return `400` with `urn:ed-fi:api:bad-request:data-validation-failed`.
- Creatability failures still return `400` with `urn:ed-fi:api:data-policy-enforced`.
- PUT preserves existing hidden stored values for matched resources, scopes, and collection items.
- Calendar IncludeOnly write profile scenarios preserve `schoolReference` and `schoolYearTypeReference` identity references.
- DMS-1229 quarantined relational E2E scenarios are unignored or revised to match the ODS-compatible behavior.
- Focused Core unit tests cover hidden-member ignore semantics, value-filter rejection semantics, response mapping, creatability, and identity preservation.

## Question Log

Record DMS-1229 architecture, product, implementation, and review questions here as they come up, with the answer that was given. Keep entries chronological.

### 2026-06-24

**Q:** How should a collection value-filter violation be represented so the middleware can map it to data-validation-failed (vs. data-policy-enforced for other category-3 failures)?

**A:** Represent collection value-filter violations as their own typed category-3 leaf, not as `ForbiddenSubmittedDataWritableProfileValidationFailure`. For example, add a `CollectionValueFilterViolationWritableProfileValidationFailure : WritableProfileValidationFailure` emitted by `WritableRequestShaper` when a submitted collection item fails `PassesCollectionItemFilter`.

The type should carry enough structured data for diagnostics and tests: profile context, collection `JsonScope`, `ScopeKind.Collection`, request JSON paths for failing items, the filter property name, filter mode, and filter values when available. It should also include standard diagnostics such as scope and request paths. Middleware should map by concrete failure type or an explicit discriminator, not by parsing messages or checking for empty `ForbiddenCanonicalMemberPaths`.

This keeps the model clear:

- hidden submitted members should no longer emit category-3 failures under DMS-1229;
- submitted collection items that fail value filters emit the new value-filter failure and map to `urn:ed-fi:api:bad-request:data-validation-failed`;
- duplicate visible collection item collisions should also map to `data-validation-failed`;
- creatability violations remain category 4 and map to `data-policy-enforced`;
- any remaining generic category-3 profile-policy failures can continue to use the existing policy/error mapping if they are not request data validation failures.

**Q:** When a write profile hides a reference object that carries resource identity (e.g. Calendar `schoolReference` holding `schoolId`), what should be implicitly preserved?

**A:** Preserve the minimum resource-identity surface, not the entire reference object. The shaping rule should derive resource identity paths from schema metadata and effectively include the top-level reference container plus the identity leaf paths needed to identify the resource.

For Calendar, if the writable IncludeOnly profile omits `schoolReference` and `schoolYearTypeReference`, the shaped write body should still retain:

- `schoolReference.schoolId`
- `schoolYearTypeReference.schoolYear`

If a reference object has multiple identity leaves, retain all of the identity leaves and the ancestor object structure needed to carry them. Do not implicitly retain unrelated reference members, `link`, metadata, or non-identity fields unless the active profile also makes those members visible.

This implicit preservation should be used for request shaping, reference resolution, and resource identity/key validation. On PUT, it should preserve the existing resource identity and allow normal identity/route-key checks to reject attempts to change identity; it must not silently rewrite the resource identity from hidden profile input.

**Q:** A profile-hidden EXTENSION scope that the client submits currently also emits `ForbiddenSubmittedData`. Should this be suppressed too?

**A:** Yes. A known extension scope hidden by the active write profile should follow the same DMS-1229 rule as hidden base-resource scopes: omit it from `WritableRequestBody`, keep the request/stored visibility metadata needed by merge processing, and do not emit `ForbiddenSubmittedData` merely because the client submitted hidden extension data.

This applies to root extension scopes such as `$._ext.sample` and extension scopes below objects or collection items. On PUT, existing stored hidden extension data should be preserved. On POST or PUT-as-create, submitted hidden extension data must not be used to create or satisfy hidden required extension members; creatability should still decide whether a new extension scope or extension collection item is allowed from the visible profile surface.

Do not broaden the suppression to unrelated extension failures. Invalid or unrecognized extension names, unsupported extension profile usage, server-generated fields, visible extension collection value-filter violations, duplicate visible collection identities after shaping, and creatability failures should keep their existing or DMS-1229-specific failure behavior. `DMS-1233` extension-name normalization remains out of scope for this ticket.
