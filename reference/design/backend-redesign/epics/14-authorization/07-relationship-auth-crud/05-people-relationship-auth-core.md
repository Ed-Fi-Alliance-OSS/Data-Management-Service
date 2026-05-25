---
jira: DMS-1164
jira_url: https://edfi.atlassian.net/browse/DMS-1164
---

# Slice 5: Implement People Relationship Auth Core

## Description

Create the shared People-involved relationship authorization core used by both:

- `DMS-1095`: People-involved Relationship-based Authorization for GET-many
- `DMS-1158`: People Relationship CRUD

This story does not authorize any endpoint by itself. It provides reusable strategy classification, securable subject resolution, auth view selection, SQL predicate/check building inputs, and failure-hint metadata for People-involved relationship strategies.

## Strategies In Scope

- `RelationshipsWithEdOrgsAndPeople`
- `RelationshipsWithEdOrgsAndPeopleInverted`
- `RelationshipsWithPeopleOnly`
- `RelationshipsWithStudentsOnly`
- `RelationshipsWithStudentsOnlyThroughResponsibility`

## Acceptance Criteria

- The core classifies the five People-involved relationship strategies as known People relationship strategies instead of treating them as unsupported or unknown metadata.
- The core determines participating securable element kinds per strategy:
  - `RelationshipsWithEdOrgsAndPeople`: EducationOrganization, Student, Contact, Staff.
  - `RelationshipsWithEdOrgsAndPeopleInverted`: EducationOrganization, Student, Contact, Staff, with inverted EdOrg hierarchy filtering.
  - `RelationshipsWithPeopleOnly`: Student, Contact, Staff.
  - `RelationshipsWithStudentsOnly`: Student only.
  - `RelationshipsWithStudentsOnlyThroughResponsibility`: Student only, using the responsibility-based student auth view.
- Person securable elements resolve to DocumentId-based authorization subjects, not UniqueId/USI values.
- Direct person references resolve to the person DocumentId column on the subject resource table.
- Transitive person references resolve to an ordered join path through intermediate resource tables, using `ResolveSecurableElementColumnPath(subjectResourceFullName, securableElement)`.
- The core selects the correct auth view/table per person subject:
  - Student: `auth.EducationOrganizationIdToStudentDocumentId`
  - Contact: `auth.EducationOrganizationIdToContactDocumentId`
  - Staff: `auth.EducationOrganizationIdToStaffDocumentId`
  - Student through responsibility: `auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility`
- For strategies that include EducationOrganization subjects, the core reuses the EdOrg subject-resolution behavior established by DMS-1055/DMS-1056 rather than introducing child-table EdOrg predicates.
- Inverted strategy behavior is explicit: EdOrg hierarchy predicates swap Source/Target filtering; person auth view semantics remain the same unless a later design change says otherwise.
- The core exposes operation-neutral auth specs that downstream stories can consume for:
  - GET-many page/count filtering in `DMS-1095`.
  - GET-by-id, POST, PUT, and DELETE checks in `DMS-1158`.
- The core preserves relationship OR composition metadata so multiple relationship strategies can be combined by consuming stories without losing strategy identity or index ordering.
- The core provides failure-hint metadata for each auth view per `auth.md`:
  - StudentSchoolAssociation hint.
  - StudentContactAssociation hint.
  - Staff employment/assignment hint.
  - StudentEducationOrganizationResponsibilityAssociation hint.
- If a configured People relationship strategy produces no applicable authorization subjects, the core returns a security-configuration failure with resource, strategy, and securable element details.
- Unit tests cover strategy classification, subject-kind selection, auth view selection, inverted EdOrg behavior, transitive person path handling, responsibility-based student handling, and no-subject security failures.

## Out of Scope

- No GET-many filtering behavior.
- No GET-by-id, POST, PUT, or DELETE authorization execution.
- No endpoint ProblemDetails mapping.
- No database execution or roundtrip batching.
- No new auth views or DDL emission.

## Reviewer Focus

Reviewers should focus on whether People authorization subjects are represented as reusable DocumentId-based specs that GET-many and CRUD consumers can share without re-resolving strategy-specific behavior.

## Clarifying Questions and Answers

### Questions 1

1. When a resource declares multiple independent securable elements for the same person kind, such as two different Student references that are not merely key-unification aliases, should Slice 5 create one authorization subject per declared path and AND them, dedupe only identical physical paths, or select a single shortest path per person kind?
2. How should the core resolve a Student, Contact, or Staff resource's own person securable element, such as `Student.$.studentUniqueId`: as the root `DocumentId` subject, as no applicable People subject for create-new until the row exists, or through another explicit self-subject shape?
3. For `RelationshipsWithEdOrgsAndPeople` and `RelationshipsWithEdOrgsAndPeopleInverted`, if configured EdOrg securable elements resolve only to child-table/non-root paths but valid person subjects exist, should the strategy fail as a security-configuration error or continue with only the applicable person subjects?
4. What operation-neutral shape should a transitive person subject expose so both GET-many/stored checks and POST/PUT proposed-value checks can consume it: an ordered join chain anchored at the root table, separate stored/proposed anchors, or is proposed transitive person authorization intentionally deferred to DMS-1158?
5. Should People subject specs carry the original person securable element JSON path and readable name for each contributing path, matching the Slice 1 EdOrg contributor metadata, so downstream security-configuration failures and Slice 6 ProblemDetails can name fields like `StudentUniqueId` without reverse-mapping from physical columns?
6. In partial or synthetic mapping sets where the association resources required by `AuthObjectDefinitions.HasAllPeopleAuthViewAssociations` are absent and People auth views are not emitted, should Slice 5 return a security-configuration failure for People strategies, or still produce specs that reference the conventional auth view names?
7. For transitive person paths, if an intermediate stored FK/DocumentId is null or a proposed reference was not resolved into the needed DocumentId, should the eventual failure metadata classify the original person securable element as stored-value invalid data/proposed element required, no relationship established, or security configuration?

### Answers 1

1. Create one People authorization subject per independent declared securable element path and AND those subjects within the strategy. Dedupe only when canonical path resolution produces the same physical path/predicate, preserving all contributing JSON paths and readable names in metadata. Use shortest-path selection only inside `ResolveSecurableElementColumnPath` when one declared securable element has multiple key-unification/alias routes to the same person.
2. Resolve a Student, Contact, or Staff resource's own person securable element as an explicit self person subject whose terminal person `DocumentId` is the resource root `DocumentId`; never authorize it through UniqueId/USI. Stored GET-by-id, DELETE, PUT, and existing-resource POST-as-update checks bind the existing root `DocumentId`. For POST create-new of that same person resource, the self subject has no proposed person `DocumentId` until the row exists, so Slice 5 should mark it as not applicable to create-new proposed checks rather than dropping the stored/existing self-subject shape or treating it as security configuration.
3. Continue with the applicable person subjects. For mixed EdOrg-and-People strategies, child-table/non-root EdOrg paths are excluded by the established EdOrg subject scope, but they do not make the strategy invalid when at least one applicable person subject remains. Return a security-configuration failure only for truly unresolved configured elements or when the strategy has no applicable authorization subjects after strategy/operation eligibility is applied.
4. Expose one ordered person path model with explicit stored and proposed anchors. The shared subject spec should carry the ordered `ResolveSecurableElementColumnPath` hop chain, terminal person kind/auth view metadata, and anchor metadata for stored/GET-many root-table SQL plus proposed root-row binding of the first hop. Direct person references are the degenerate case where the terminal person `DocumentId` is already on the root/proposed row. Do not defer proposed transitive person authorization to DMS-1158; DMS-1158 should consume this shape.
5. Yes. People subject specs must carry contributor metadata for every original person securable element path: JSON path, readable/MetaEd name, person kind, and contribution order. Slice 6 and security-configuration failures should use that metadata directly instead of reverse-mapping from physical columns or auth view names.
6. Return a security-configuration failure for People strategies when `HasAllPeopleAuthViewAssociations` is false and the required People auth views were not emitted. Do not produce executable specs that reference conventional auth view names that are absent from the deployed model. The failure metadata should name the resource, strategy, selected person kind/auth view, and missing required association resources.
7. Classify missing stored/proposed path values against the original person securable element, not as security configuration. A null stored intermediate FK/DocumentId is stored-value invalid data / element uninitialized for the original person element. A proposed reference that does not yield the needed DocumentId in the finalized row/proposed anchor is proposed element required for the original person element. If normal reference resolution fails because the referenced resource does not exist, that remains a reference-resolution error before authorization rather than being converted to no-relationship.

### Questions 2

1. Should People relationship checks expose the same explicit no-EdOrg-claims metadata shape as the EdOrg core, including selected person auth view and hint metadata, so GET-many can short-circuit to an empty result while CRUD can return a 403 without composing auth-view SQL?
2. For POST create-new of `Student`, `Contact`, or `Staff` where the self person subject is marked not applicable because the row's `DocumentId` does not exist yet, should Slice 5 emit a distinct operation-eligibility reason so DMS-1158 can distinguish this from a true no-applicable-subject security-configuration failure?
3. Should People subject specs extend the existing `RelationshipAuthorizationSpec`/subject model and reuse the Slice 2/3/4 `AUTH1` failure-set ordinal mapping and external failure DTO shape, or should Slice 5 introduce a separate People-specific spec that GET-many and CRUD adapters merge later?
4. For stored transitive person paths, if a non-null intermediate `DocumentId` points to a missing intermediate row, or the intermediate row exists but its terminal person `DocumentId` is null, should the failure be reported as stored-value invalid data for the original person securable element, no relationship established, or a separate referential-corruption/configuration failure?
5. Should the core dedupe identical person auth predicates only within a single configured strategy while preserving duplicate configured strategies and all contributing person JSON paths/readable names in the same contributor metadata model used for EdOrg same-column dedupe?

### Answers 2

1. Yes. People relationship plans should expose the same explicit no-EdOrg-claims failure shape as the EdOrg core. The metadata must identify the strategy, person kind, selected person auth view, contributing securable elements, and auth-view hint. GET-many consumes that shape as an empty page/count result, while CRUD maps it to relationship-not-authorized/403 without composing auth-view SQL for an empty EdOrg claim list.
2. Yes. Emit a distinct operation-eligibility reason, such as `SelfPersonDocumentIdUnavailableForCreateNew`, on the self person subject. DMS-1158 should treat that as an operation-ineligible subject for POST create-new, not as a security-configuration failure and not as an unresolved securable element. Stored/existing-resource operations still use the same self-subject spec with the root `DocumentId`.
3. Extend the existing `RelationshipAuthorizationSpec` and relationship subject model with People subject variants. Do not introduce a separate People-specific plan that adapters merge later. People subjects should reuse the Slice 2/3/4 versioned compact `AUTH1` failure-set payload, plan-relative strategy/subject ordinals, and the external `RelationshipAuthorizationFailure` DTO, adding person-specific fields for DocumentId path, auth view, person kind, and hint metadata.
4. Report both cases as stored-value invalid data for the original person securable element. A broken stored transitive path or null terminal person `DocumentId` means the required existing authorization value cannot be derived from stored data; it is not a no-relationship denial and not a security-configuration failure once the mapping path resolved successfully. Preserve internal diagnostic detail for the broken hop, but expose Slice 6 failure metadata as existing-data element-uninitialized for the original person field.
5. Yes. Dedupe identical People auth predicates only within one configured strategy/check, using a predicate key that includes the resolved physical person path, value source/anchor, terminal person kind, and auth view. Preserve duplicate configured strategies as separate OR entries, and preserve all contributing person JSON paths, readable names, person kinds, and contribution order on the deduped subject metadata. Do not dedupe across different configured strategies, normal vs inverted strategy entries, or different auth views such as responsibility vs non-responsibility student views.

### Questions 3

1. For POST create-new proposed checks, when a self person subject is operation-ineligible but other subjects in the same relationship strategy are applicable, should the ineligible self subject be omitted from that operation's AND check, or should the whole strategy be marked operation-ineligible for create-new?
2. For POST create-new proposed checks, when all subjects in a configured People relationship strategy are operation-ineligible, should Slice 5 expose a strategy-level operation-ineligible/no-executable-subjects result for DMS-1158, and should DMS-1158 treat that as authorized/no-op, relationship-not-authorized, or another outcome?
3. Should Slice 5 explicitly preserve the EdOrg direct-claim-match flag for EducationOrganization subjects inside `RelationshipsWithEdOrgsAndPeople` and `RelationshipsWithEdOrgsAndPeopleInverted`, so downstream GET-many and CRUD SQL compilers can emit direct claim match plus hierarchy lookup for stored and proposed checks?
4. In a partial or synthetic mapping set where People auth views are not emitted, should `RelationshipsWithEdOrgsAndPeople` fail whenever `HasAllPeopleAuthViewAssociations` is false, or only when at least one selected person subject would require a missing People auth view and otherwise continue with applicable EdOrg subjects?

### Answers 3

1. Omit the operation-ineligible self person subject from the POST create-new AND check when at least one other subject in the same configured strategy is applicable. Preserve the omitted subject and `SelfPersonDocumentIdUnavailableForCreateNew` reason in operation-eligibility metadata, but do not make the whole strategy ineligible and do not emit an impossible self `DocumentId` predicate for create-new.
2. Yes. Slice 5 should expose a strategy-level operation-ineligible/no-executable-subjects result when all subjects in that configured strategy are operation-ineligible for POST create-new. DMS-1158 should treat that strategy as an authorized no-op for POST create-new, not as relationship-not-authorized and not as security configuration, while retaining the ineligibility reasons for diagnostics. Stored/existing-resource operations still use the normal self-subject checks.
3. Yes. EducationOrganization subjects selected by `RelationshipsWithEdOrgsAndPeople` and `RelationshipsWithEdOrgsAndPeopleInverted` must preserve the same `AllowDirectClaimMatch` metadata as EdOrg-only subjects. Downstream SQL compilers must emit direct claim match plus hierarchy lookup for both stored and proposed checks; inverted strategies reverse only the hierarchy Source/Target comparison, not the direct-match predicate.
4. Fail only when at least one selected, operation-applicable person subject requires a missing People auth view. If `RelationshipsWithEdOrgsAndPeople` selects only applicable EdOrg subjects for the resource/operation, continue with those EdOrg subjects even when `HasAllPeopleAuthViewAssociations` is false. If any selected Student, Contact, Staff, or responsibility Student subject would need an auth view that was not emitted, return the security-configuration failure described in Answer 1.6 with the selected person kind, auth view, resource, strategy, and missing association resources.

### Questions 4

1. For POST create-new and proposed-value update checks on resources that themselves create or mutate the primary association backing a selected People auth view, such as `StudentSchoolAssociation`, `StudentContactAssociation`, `StaffEducationOrganizationAssignmentAssociation`, `StaffEducationOrganizationEmploymentAssociation`, or `StudentEducationOrganizationResponsibilityAssociation`, should the same-row People subject be marked operation-ineligible, evaluated only against pre-existing relationships, or replaced with a non-circular subject shape?
2. Are person securable elements whose resolved path starts from a child collection table eligible People relationship subjects for GET-many and CRUD? If they are eligible, should authorization require every child-row person value to authorize, at least one child-row value to authorize, or a distinct subject/check per child row, and how should zero child rows be classified?

### Answers 4

1. Replace the same-row People subject with a non-circular primary-association bootstrap subject for POST create-new and proposed-value update checks. Do not mark it operation-ineligible, and do not require the proposed row to already exist in the selected People auth view. The bootstrap subject should evaluate the relationship that the proposed association row itself would contribute, using the same SourceEducationOrganizationId-to-person semantics as the selected People auth view and preserving the original person contributor metadata. `StudentSchoolAssociation`, `StaffEducationOrganizationAssignmentAssociation`, `StaffEducationOrganizationEmploymentAssociation`, and `StudentEducationOrganizationResponsibilityAssociation` bootstrap from the row's proposed EducationOrganization plus proposed person `DocumentId`. `StudentContactAssociation` bootstraps the proposed Contact through the row's proposed Student relationship, so the caller must already be authorized to the proposed Student via `EducationOrganizationIdToStudentDocumentId`; the Contact contributor metadata is still retained. Stored GET-by-id, DELETE, and stored-before-update checks continue to use the normal auth view because the existing row is already visible to that view.
2. Yes. Person securable elements whose resolved path starts from a child collection table are eligible People relationship subjects for GET-many and CRUD. Treat each stored or finalized proposed child row for that declared path as a row-scoped People subject, and require every child-row person value on that path to authorize; do not use "at least one child row" semantics. SQL may implement this as `NOT EXISTS` unauthorized child rows or equivalent row-scoped `EXISTS` checks, but failure metadata must still point back to the original collection securable element path and readable name. Zero child rows are runtime missing authorization values, not security configuration: GET-many should make that configured strategy fail for that root row unless another OR strategy succeeds; stored single-record checks should classify it as stored-value invalid data / element uninitialized for the original collection person element; proposed create/update checks should classify it as proposed element required.
