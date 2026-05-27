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
6. Return a security-configuration failure only when a selected, operation-applicable People subject requires a People auth view that was not emitted because `HasAllPeopleAuthViewAssociations` is false. Do not produce executable specs that reference conventional auth view names that are absent from the deployed model. Mixed EdOrg-and-People strategies that select only applicable EdOrg subjects for the resource/operation must continue with those EdOrg subjects. The failure metadata should name the resource, strategy, selected person kind/auth view, and missing required association resources.
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
2. Yes. Slice 5 should expose a strategy-level operation-ineligible/no-executable-subjects result when all subjects in that configured strategy are operation-ineligible for POST create-new. DMS-1158 must not treat a selected People relationship strategy as an authorized no-op merely because no subject is executable. For ODS parity, authorization succeeds without a People relationship check only when the operation's effective security metadata does not select that relationship strategy, such as a `NoFurtherAuthorizationRequired` create. If the strategy is selected and operation-applicable but has no executable subjects after eligibility filtering, DMS-1158 should treat it as the existing no-applicable-subject security-configuration failure while retaining the ineligibility reasons for diagnostics. Stored/existing-resource operations still use the normal self-subject checks.
3. Yes. EducationOrganization subjects selected by `RelationshipsWithEdOrgsAndPeople` and `RelationshipsWithEdOrgsAndPeopleInverted` must preserve the same `AllowDirectClaimMatch` metadata as EdOrg-only subjects. Downstream SQL compilers must emit direct claim match plus hierarchy lookup for both stored and proposed checks; inverted strategies reverse only the hierarchy Source/Target comparison, not the direct-match predicate.
4. Fail only when at least one selected, operation-applicable person subject requires a missing People auth view. If `RelationshipsWithEdOrgsAndPeople` selects only applicable EdOrg subjects for the resource/operation, continue with those EdOrg subjects even when `HasAllPeopleAuthViewAssociations` is false. If any selected Student, Contact, Staff, or responsibility Student subject would need an auth view that was not emitted, return the security-configuration failure described in Answer 1.6 with the selected person kind, auth view, resource, strategy, and missing association resources.

### Questions 4

1. For POST create-new and proposed-value update checks on resources that themselves create or mutate the primary association backing a selected People auth view, such as `StudentSchoolAssociation`, `StudentContactAssociation`, `StaffEducationOrganizationAssignmentAssociation`, `StaffEducationOrganizationEmploymentAssociation`, or `StudentEducationOrganizationResponsibilityAssociation`, should the same-row People subject be marked operation-ineligible, evaluated only against pre-existing relationships, or replaced with a non-circular subject shape?
2. Are person securable elements whose resolved path starts from a child collection table eligible People relationship subjects for GET-many and CRUD? If they are eligible, should authorization require every child-row person value to authorize, at least one child-row value to authorize, or a distinct subject/check per child row, and how should zero child rows be classified?

### Answers 4

1. Do not introduce a generic same-row People bootstrap subject. DMS must preserve ODS behavior by relying on the effective ODS security metadata and the normal relationship auth views. In ODS metadata, people-domain create uses `NoFurtherAuthorizationRequired`, primary-relationship create uses `RelationshipsWithEdOrgsOnly` for the primary association resources in that domain, `StudentContactAssociation` create/update/read/delete is explicitly overridden to `RelationshipsWithStudentsOnly`, and `StudentEducationOrganizationResponsibilityAssociation` create follows its `relationshipBasedData` metadata rather than primaryRelationships metadata. Therefore POST create-new for association resources must not self-authorize a People relationship by treating the proposed row as though it already exists in the selected People auth view. If an operation's effective configured strategies select a People subject, evaluate it with the normal selected auth view against relationships already visible to that view; if the proposed same row is not yet visible, it does not bootstrap itself into authorization. Stored GET-by-id, DELETE, stored-before-update, and applicable proposed-value update checks continue to use the normal auth view semantics.
2. No. Person securable elements whose resolved path starts from a child collection table are not eligible People relationship subjects for ODS-parity GET-many or CRUD in Slice 5. ODS relationship authorization is scoped to aggregate root authorization context values and root-row query filters; it does not create row-scoped People subjects for child collections, require every child row to authorize, or fail a root row because a child collection is empty. DMS should keep array-nested People paths out of the executable subject set, preserving skipped-path metadata for diagnostics. If, after ODS subject-scope filtering and operation eligibility, a configured strategy has no applicable authorization subjects, use the existing no-applicable-subject security-configuration failure; do not invent `NOT EXISTS` unauthorized-child-row predicates or zero-child runtime failures.

### Questions 5

1. After Slice 5 lands but before DMS-1095 and DMS-1158 consume it, should existing GET-many and CRUD adapters continue returning their current known-but-not-enabled staging surfaces for People relationship strategies, or should Slice 5 wire any adapter path far enough to expose the new People specs without executing them?
2. Should a Student, Contact, or Staff self person subject be represented in the shared path model as a zero-hop root `DocumentId` binding or as a single synthetic self-hop, and should that representation participate in predicate dedupe keys and contributor metadata exactly like direct person references?
3. For GET-many consumers of the shared People specs, should rows with null or broken stored transitive person paths simply be filtered out as nonmatching rows with no runtime invalid-data result, while CRUD maps the same runtime facts to stored-value invalid-data metadata?
4. For child-collection People paths excluded by ODS subject-scope filtering, should no-applicable-subject security-configuration failures include those paths as skipped/ineligible contributor metadata with an explicit reason, or should they be omitted from the failure payload after filtering?
5. When a selected People auth view was not emitted because required association resources are missing, should the security-configuration failure name only the missing dependencies for that selected auth view, or all missing resources from the global five-association guard that suppresses every People auth view?
6. Should the People subject spec carry both the selected auth view/table name and the expected person `DocumentId` output column name, such as `Student_DocumentId`, `Contact_DocumentId`, or `Staff_DocumentId`, or should downstream SQL compilers derive the output column from person kind and auth view selection?

### Answers 5

1. Existing GET-many and CRUD adapters should keep returning their current known-but-not-enabled staging surfaces for People relationship strategies until DMS-1095 and DMS-1158 consume the core. Slice 5 should expose reusable core contracts and unit-test them directly, but it should not partially wire endpoint adapters or runtime SQL paths that expose People specs without executing them.
2. Represent a Student, Contact, or Staff self person subject as a zero-hop root `DocumentId` binding. The subject's terminal person value is the aggregate root `DocumentId`, with no synthetic self-hop. It participates in predicate dedupe keys and contributor metadata like direct person references, using the zero-hop binding, person kind, selected auth view, output column, value anchor, and original self securable element path/readable name.
3. Yes. GET-many should treat null or broken stored transitive person paths as nonmatching rows, so those rows are filtered from page/count results with no per-row invalid-data result. CRUD stored-value checks for a specific resource should map the same unresolved stored path facts to stored-value invalid-data metadata for the original person securable element.
4. Include excluded child-collection People paths as skipped/ineligible contributor metadata with an explicit reason, such as `ChildCollectionPersonPathOutsideSubjectScope`. They must not become executable subjects, but no-applicable-subject security-configuration failures should retain enough metadata to show which configured person paths were filtered out and why.
5. Name the selected person kind/auth view and list all missing resources from the global five-association guard that caused People auth views not to be emitted. Because the current DDL guard suppresses every People auth view unless all five association resources are present, listing only the selected view's direct dependencies can hide the actual reason the selected view is absent.
6. Carry both the selected auth view/table name and the expected person `DocumentId` output column name on the People subject spec. Downstream SQL compilers should consume these values from the spec rather than re-deriving them from person kind, and the same metadata should participate in predicate keys, diagnostics, and auth-view failure hints.
