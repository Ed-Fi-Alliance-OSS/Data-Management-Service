---
jira: DMS-1095
jira_url: https://edfi.atlassian.net/browse/DMS-1095
---

# Story: Implement People-involved Relationship-based Authorization for GET-many

## Description

Extend the GET-many authorization subquery framework established in [DMS-1055](https://edfi.atlassian.net/browse/DMS-1055) by consuming the People relationship authorization core established in [DMS-1056](https://edfi.atlassian.net/browse/DMS-1056) Slice 5 (`07-relationship-auth-crud/05-people-relationship-auth-core.md`), per:

- `reference/design/backend-redesign/design-docs/auth.md`

DMS-1056 owns shared People strategy classification, Student/Contact/Staff DocumentId path resolution, auth-view selection, through-responsibility selection, hint metadata, and parameterization contracts. This story integrates that core into the GET-many page/count SQL path.

For the EducationOrganization portion of mixed EdOrg-and-People relationship strategies, DMS-1095 inherits DMS-1055's ODS-parity GET-many subject scope: only DMS concrete root-table EdOrg authorization subjects participate. Child-table EdOrg paths remain out of scope unless a later story explicitly introduces different DMS semantics.

## Dependencies

- Depends on [DMS-1055](https://edfi.atlassian.net/browse/DMS-1055) for the EdOrg-only GET-many framework.
- Depends on [DMS-1056](https://edfi.atlassian.net/browse/DMS-1056) Slice 5 for the shared People relationship authorization core.

## Acceptance Criteria

- The following relationship-based strategies are implemented for GET-many:
  - RelationshipsWithEdOrgsAndPeople — includes EducationOrganization, Student, Contact, and Staff securable elements.
  - RelationshipsWithEdOrgsAndPeopleInverted — same as above with inverted EdOrg filtering (bottom-to-top).
  - RelationshipsWithPeopleOnly — includes only Student, Contact, and Staff securable elements.
  - RelationshipsWithStudentsOnly — includes only Student securable elements.
  - RelationshipsWithStudentsOnlyThroughResponsibility — includes only Student securable elements, using the EducationOrganizationIdToStudentDocumentIdThroughResponsibility auth view.
- GET-many results are filtered based on the configured strategy; unauthorized resources are never returned.
- People-related securable elements (Student, Contact, Staff) use the DocumentId path-resolution and auth-view metadata supplied by DMS-1056 Slice 5.
- All shared framework behavior from DMS-1055 (OR semantics, IN subquery approach, pagination, caching, TVP threshold) applies to the strategies implemented here.
- This story replaces the temporary DMS-1055 GET-many 501 Not Implemented behavior for People relationship strategies. When mixed with EdOrg-only relationship strategies, People relationship strategies are added to the relationship OR group instead of causing the unsupported mixed-strategy failure.
- Works for both PostgreSQL and SQL Server.

NOTE: People-involved GET-by-id, POST, PUT, and DELETE endpoint execution remains out of this story and is expected to consume the same People core in follow-on People CRUD work.

## Clarifying Questions and Answers

### Questions 1

1. For DMS-1095 GET-many, should People subject resolution and security-configuration validation run before the empty EdOrg-claims short-circuit, or should any supported People relationship strategy with no token EdOrg IDs return an empty page and count even if subjects cannot resolve or required People auth views were not emitted?
2. What exact SQL shape should DMS-1095 require for transitive People subjects under the DMS-1055 `IN` subquery rule: root `DocumentId IN (SELECT r2.DocumentId FROM <root/path joins> WHERE terminal Person_DocumentId IN (SELECT ... FROM auth view ...))`, correlated `EXISTS`, or another canonical shape for page and count queries?
3. When DMS-1095 removes the temporary People-strategy 501 for GET-many, should unsupported non-relationship strategies such as `NamespaceBased`, `OwnershipBased`, and custom view-based strategies continue to produce the DMS-1055 501 staging response when combined with People relationship strategies, or should this story compose with any non-relationship strategies that have already landed on the branch?
4. What minimum acceptance test matrix is required for People GET-many: direct person/self subjects, transitive person paths, Contact and Staff views, `RelationshipsWithStudentsOnlyThroughResponsibility`, mixed EdOrg-and-People AND semantics, multiple relationship strategies ORed with EdOrg-only, empty EdOrg claims, missing auth views/no applicable subjects, and SQL Server TVP threshold?
5. Which existing E2E scenarios, if any, should DMS-1095 re-enable or retag for the relational backend because they specifically exercise People-involved GET-many behavior, and which People E2E scenarios should remain deferred to DMS-1158 CRUD?

### Answers 1

1. Run strategy classification plus DMS-1056 People subject planning and security-configuration validation before the empty EdOrg-claims data short-circuit. If the selected People plan has an unresolved operation-applicable subject, no applicable subjects after ODS subject-scope filtering, or an operation-applicable selected People subject that requires a People auth view that was not emitted, return the security-configuration failure even when the token has no EdOrg claims. Do not fail merely because People auth views are absent when the selected strategy has no executable People subjects and continues with applicable EdOrg subjects only. Once the GET-many plan is valid and supported, an empty normalized `ClaimEducationOrganizationIds` list short-circuits to a 200 empty page and `totalCount = 0` when requested, without composing or executing People auth-view SQL.

2. Use a root `DocumentId IN` subquery for each executable People subject in both page and count SQL. Do not use a correlated `EXISTS` shape. For a transitive Student subject, the canonical shape is:

   ```sql
   r.DocumentId IN (
       SELECT r2.DocumentId
       FROM edfi.CourseTranscript AS r2
       JOIN edfi.StudentAcademicRecord AS p1
           ON p1.DocumentId = r2.StudentAcademicRecord_DocumentId
       WHERE p1.Student_DocumentId IN (
           SELECT av.Student_DocumentId
           FROM auth.EducationOrganizationIdToStudentDocumentId AS av
           WHERE av.SourceEducationOrganizationId <token-list predicate>
       )
   )
   ```

   The token-list predicate uses the existing DMS-1055 parameter contract: PostgreSQL `= ANY(@ClaimEducationOrganizationIds)`, SQL Server expanded scalar parameters below 2,000 unique IDs, and `dms.BigIntTable` at 2,000+ unique IDs. Direct root person references and self person subjects use the same root `DocumentId IN` pattern with a zero-hop or direct terminal column. Use the auth-view table name and output column carried by the DMS-1056 spec, such as `Student_DocumentId`, `Contact_DocumentId`, or `Staff_DocumentId`. Combine subjects within one relationship strategy with `AND`; combine relationship strategies, including EdOrg-only and People-involved strategies, with `OR`.

3. DMS-1095 should use capability-based composition. People relationship strategies become supported GET-many relationship strategies and join the relationship OR group instead of causing the DMS-1055 People 501. Any non-relationship GET-many strategy that has already landed in the relational path must compose using final `auth.md` semantics as an `AND` filter with the relationship OR group. Known non-relationship strategies that have not yet landed, including `NamespaceBased`, `OwnershipBased`, and valid custom view-based strategy names, should keep the temporary 501 staging response. DMS-1095 must not partially apply relationship authorization while silently ignoring unsupported `AND` strategies. `NoFurtherAuthorizationRequired` remains a no-op, and unknown or invalid security metadata remains a security-configuration failure.

4. Minimum acceptance test matrix:

   - Planner/unit coverage for all five People relationship strategies, selected subject kinds, auth view/output-column selection, through-responsibility selection, inverted EdOrg metadata, direct person references, self person subjects, transitive person paths, child-collection People paths skipped as ineligible, missing selected People auth views, and no-applicable-subject security failures.
   - SQL generation coverage proving page and count use the same root `DocumentId IN` predicates for Student self resources, direct Student references, a transitive Student path such as `CourseTranscript -> StudentAcademicRecord -> Student`, Contact, Staff, and `RelationshipsWithStudentsOnlyThroughResponsibility`.
   - PostgreSQL and SQL Server integration coverage for authorized and unauthorized GET-many results for Student, Contact, Staff, and through-responsibility Student subjects. For GET-many, null or broken stored transitive paths filter out as nonmatches and do not produce per-row invalid-data failures.
   - Mixed `RelationshipsWithEdOrgsAndPeople` coverage showing EdOrg and People subjects are `AND`ed within one strategy, including a case where the EdOrg subject passes but the People subject fails and a case where the People subject passes but the EdOrg subject fails.
   - Multiple relationship strategy coverage showing EdOrg-only and People-involved strategies are `OR`ed and do not return duplicate root documents.
   - Pagination and `totalCount` coverage proving authorization filtering happens before paging/counting.
   - Empty EdOrg-claims coverage for a valid People relationship plan returning an empty page and `totalCount = 0`.
   - Parameterization coverage for PostgreSQL array binding, SQL Server 1,999 unique IDs as expanded scalar parameters, SQL Server 2,000 unique IDs as `dms.BigIntTable`, and dedupe/sort before threshold selection.
   - Focused relational E2E coverage with real token/claim-set wiring for Student GET-many, StudentSchoolAssociation GET-many, Staff GET-many, and empty EdOrg claims. Contact GET-many must have PostgreSQL and SQL Server backend integration coverage. Do not require Contact E2E for DMS-1095 unless a focused scenario can seed Contact and StudentContactAssociation setup without relying on People-involved GET-by-id, POST, PUT, or DELETE authorization; otherwise defer Contact E2E to DMS-1158. Add a new focused through-responsibility GET-many E2E scenario if no existing scenario can run without People CRUD setup.

5. Retag existing scenarios only when the scenario's behavior under test is GET-many and the scenario can run without relying on People-involved GET-by-id, POST, PUT, or DELETE execution. Add `@relational-backend` and the appropriate `@relational-ci-shard-*` tag to these existing GET-many scenarios:

   - `Features/Authorization/RelationshipsWithEdOrgsAndPeople.feature`: scenario 05, "Ensure client can only query authorized StudentSchoolAssociation"; scenario 14, "Ensure client can only query authorized Students"; scenario 46, "Ensure client with access to both schools can query multiple student school associations"; scenario 47, "Ensure client with access to one school can query one student school associations"; scenario 54, "Ensure client can query a Student associated to a School with a long ID".
   - `Features/Authorization/RelationshipsWithEdOrgsAndStaffs.feature`: scenario 04, "Ensure client can Search staffEducationOrganizationAssignmentAssociations"; scenario 11, "Ensure client cannot search staffEducationOrganizationAssignmentAssociations with client does not have access it to educationOrganizationId"; scenario 16, "Ensure client can GET staffEducationOrganizationEmploymentAssociations"; scenario 23, "Ensure client cannot search staffEducationOrganizationEmploymentAssociations with client does not have access it to educationOrganizationId".

   Do not retag existing People scenarios whose scenario body exercises People GET-by-id, POST, PUT, DELETE, or mutation-cascade behavior, even if they also contain GET-many assertions. Those remain deferred to DMS-1158, including the explicit DMS-1158 scenario list in `07b-people-relationship-auth-crud.md`, `RelationshipsWithEdOrgsAndPeople.feature` scenarios 33-35, 44-45, 48, and 51-53, `RelationshipsWithEdOrgsAndContacts.feature` scenarios 01-22 and 50-55, `RelationshipsWithStudentsOnlyThroughResponsibility.feature` scenarios 01-07, and People role-named scenarios whose setup or assertion requires People CRUD. If DMS-1095 needs Contact or through-responsibility E2E coverage before DMS-1158, add new focused GET-many scenarios with setup that does not depend on People CRUD rather than retagging the existing mixed CRUD scenarios.
