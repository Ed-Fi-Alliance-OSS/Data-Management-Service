# Initial Authorization Synchronization Design

This document describes how authorization is enforced and how the new relational
authorization tables (`DocumentSubject`, `SubjectEdOrg`, and the redesigned
EducationOrganization hierarchy) are synchronized with core Ed-Fi relationship
resources and securable documents.

It mirrors the structure of the existing Student- and Contact-EdOrg
authorization design documents, but is updated for the new model:

- No JSONB authorization arrays on `dms.Document`.
- No pathway-specific authorization tables or triggers.
- Authorization decisions rely on:
  - `SubjectEdOrg` for subject→EdOrg membership.
  - `DocumentSubject` for document→subject mappings.
  - `DocumentIndex` for document filtering and paging.

---

## Authorization Algorithm for Create/Update/Delete/Get-by-ID of a Student-securable Document

There are two phases to security actions for Student-securable documents:

- **Authorization itself** (does the caller have a valid relationship to the student, given their EdOrg claims?).
- **Synchronization** (keeping `SubjectEdOrg` and `DocumentSubject` correct as relationship and securable documents change).

This section covers the authorization algorithm. See the synchronization section
below for how relational tables are maintained.

### Create/Update (Student-securable document)

1. **DMS Core determines that the resource is Student-securable**  
   - `ProvideAuthorizationSecurableInfoMiddleware` reads the resource schema’s
     `AuthorizationSecurableInfo` and sets the fact that the document is
     Student-securable (i.e., it has a `StudentUniqueId` security element).
2. **Core extracts the StudentUniqueId from the request body**  
   - `ExtractDocumentSecurityElementsMiddleware` builds `DocumentSecurityElements`.
   - `RelationshipsWithStudentsOnlyValidator` (and/or other student-based validators)
     inspects `DocumentSecurityElements.Student[0].Value`.
3. **Core resolves the caller’s authorized EdOrgIds from token claims**  
   - `ProvideAuthorizationFiltersMiddleware` uses `IAuthorizationFiltersProvider`
     for student-based strategies to produce `AuthorizationFilter.EducationOrganization`
     values (one per EdOrgId in the token).
4. **Core queries subject→EdOrg membership for the student**  
   - `RelationshipsBasedAuthorizationHelper.ValidateStudentAuthorization` calls
     `IAuthorizationRepository.GetEducationOrganizationsForStudent(studentUniqueId)`.
   - In the new design, this method reads from `SubjectEdOrg`:
    - `SELECT DISTINCT EducationOrganizationId FROM dms.SubjectEdOrg WHERE SubjectType = Student AND SubjectIdentifier = @studentUniqueId AND Pathway IN (StudentSchool, StudentResponsibility);`
5. **Core compares subject EdOrgIds with caller’s EdOrg filters**  
   - The helper intersects the EdOrgIds from `SubjectEdOrg` with the
     `AuthorizationFilter.EducationOrganization` values.
   - If there is at least one match, authorization passes for this strategy; if
     not, it returns a `NotAuthorized` result (with hints if appropriate).
6. **ResourceAuthorizationHandler composes strategies**  
   - If any “AND” strategy fails, the upsert/update is denied.
   - If all “AND” strategies succeed and at least one “OR” strategy succeeds (or
     there are no OR strategies), the upsert/update is authorized.

### Get-by-ID (Student-securable document)

1. **Core calls the backend to retrieve the document**  
   - `GetByIdHandler` uses `PostgresqlDocumentStoreRepository.GetDocumentById` to
     fetch the `EdfiDoc` and metadata for the requested document.
2. **Core reconstructs DocumentSecurityElements from the EdfiDoc**  
   - The same extraction logic used at write time is applied to the returned
     `EdfiDoc` to produce `DocumentSecurityElements`, including `StudentUniqueId`.
3. **Core runs ResourceAuthorizationHandler**  
   - The same validators (`RelationshipsWithStudentsOnlyValidator`,
     etc.) and repository calls as in Create/Update are used, but now against the
     persisted `EdfiDoc`.
4. **If authorization passes, the document is returned to the client; otherwise a 403 is returned.**

> Note: For high-volume queries (GET by query), authorization is enforced inside
> the SQL against `DocumentIndex` + `DocumentSubject` + `SubjectEdOrg`, not via
> per-document fetch-and-check. This is documented in the main auth design.

### Delete (Student-securable document)

1. **Core resolves which document is being deleted and retrieves its EdfiDoc**  
   - `DeleteByIdHandler` uses the backend to retrieve or validate the existence
     of the target document, then reconstructs `DocumentSecurityElements`.
2. **Core runs ResourceAuthorizationHandler for OperationType.Delete**  
   - The same student-based authorization logic used for Update is applied.
3. **If authorization passes, the backend DELETE is executed; otherwise a 403 is returned.**

---

## Synchronization between StudentSchoolAssociation document, StudentEducationOrganizationResponsibilityAssociation document, SubjectEdOrg, DocumentSubject, and Student-securable document (Document table)

This is the synchronization phase. It ensures the relational auth tables are
kept consistent with the Ed-Fi relationship documents and securable documents.
All actions occur inside the same transaction as the document change.

### StudentSchoolAssociation (Document table)

*Create*

1. Insert `StudentSchoolAssociation` document into `dms.Document`.
2. Extract `StudentUniqueId` and `schoolId` from the document.
3. Call `RecomputeStudentSchoolMembership(studentUniqueId)`:
   1. Find all `StudentSchoolAssociation` documents for this student.
   2. For each distinct `schoolId`, compute ancestor EdOrgIds via the
      `EducationOrganization`/`EducationOrganizationRelationship` tables.
   3. Union all ancestor EdOrgIds.
   4. Delete existing `SubjectEdOrg` rows for
      `(SubjectType = Student, SubjectIdentifier = studentUniqueId, Pathway = StudentSchool)`.
   5. Insert one `SubjectEdOrg` row per ancestor EdOrgId for this student/pathway.

*Update (including cascade)*

1. Detect changes to either `StudentUniqueId` or `schoolId` on the association
   (both values are present in `DocumentSecurityElements` and/or the existing
   document).
   1. If neither has changed, skip membership recomputation.
   2. If `StudentUniqueId` changes:
      1. Call `RecomputeStudentSchoolMembership(oldStudentUniqueId)` to remove
         any now-orphaned memberships.
      2. Call `RecomputeStudentSchoolMembership(newStudentUniqueId)` to add the
         new memberships.
   3. If only `schoolId` changes, call `RecomputeStudentSchoolMembership(studentUniqueId)`.
2. Update the `StudentSchoolAssociation` document in `dms.Document`.

*Delete*

1. Retrieve the `StudentUniqueId` from the existing association document.
2. Delete the `StudentSchoolAssociation` document from `dms.Document`.
3. Call `RecomputeStudentSchoolMembership(studentUniqueId)` to remove any
   memberships that relied on this association.

### StudentEducationOrganizationResponsibilityAssociation (Document table)

*Create*

1. Insert `StudentEducationOrganizationResponsibilityAssociation` document into `dms.Document`.
2. Extract `StudentUniqueId` and `educationOrganizationId`.
3. Call `RecomputeStudentResponsibilityMembership(studentUniqueId)`:
   1. Find all `StudentEducationOrganizationResponsibilityAssociation` documents
      for this student.
   2. For each distinct `educationOrganizationId`, compute ancestor EdOrgIds via
      the hierarchy tables.
   3. Union all ancestor EdOrgIds.
   4. Delete existing `SubjectEdOrg` rows for
      `(SubjectType = Student, SubjectIdentifier = studentUniqueId, Pathway = StudentResponsibility)`.
   5. Insert one `SubjectEdOrg` row per ancestor EdOrgId for this student/pathway.

*Update (including cascade)*

1. Detect changes to either `StudentUniqueId` or `educationOrganizationId`.
   1. If neither has changed, skip.
   2. If `StudentUniqueId` changes:
      1. `RecomputeStudentResponsibilityMembership(oldStudentUniqueId)`.
      2. `RecomputeStudentResponsibilityMembership(newStudentUniqueId)`.
   3. If only `educationOrganizationId` changes:
      1. `RecomputeStudentResponsibilityMembership(studentUniqueId)`.
2. Update the `StudentEducationOrganizationResponsibilityAssociation`
   document in `dms.Document`.

*Delete*

1. Retrieve the `StudentUniqueId` from the existing responsibility association.
2. Delete the `StudentEducationOrganizationResponsibilityAssociation` document
   from `dms.Document`.
3. Call `RecomputeStudentResponsibilityMembership(studentUniqueId)`.

### Student-securable Document (Document table)

These are resources authorized using student-based strategies (e.g., resources
with a `StudentUniqueId` securable key).

*Create*

1. Insert the Student-securable document into `dms.Document`.
2. Extract `StudentUniqueId` from `DocumentSecurityElements`.
3. Insert a `DocumentSubject` row:
   - `(ProjectName, ResourceName, DocumentPartitionKey, DocumentId, SubjectType = Student, SubjectIdentifier = studentUniqueId)`.
4. (No EdOrg arrays are stored on `dms.Document`; EdOrg membership remains in `SubjectEdOrg`.)

*Update (including cascade)*

1. Detect changes to `StudentUniqueId`:
   1. If none, skip `DocumentSubject` maintenance.
   2. If changed:
      1. Delete existing `DocumentSubject` row(s) for this document with `SubjectType = Student`.
      2. Insert new `DocumentSubject` row with the updated `StudentUniqueId`.
2. Update the Student-securable document in `dms.Document`.
3. No changes are required to `SubjectEdOrg` because student→EdOrg membership is
   driven by relationship resources, not by Student-securable documents themselves.

*Delete*

1. Delete the Student-securable document from `dms.Document`.
2. Delete corresponding `DocumentSubject` row(s) via FK cascade or explicit delete.
3. No changes are required to `SubjectEdOrg` (relationships remain valid for
   other documents).

### EducationOrganizationHierarchy

- Changes in EducationOrganization documents (or in their relationships) affect
  ancestor expansion for `SubjectEdOrg`.
- For the initial implementation:
  - EdOrg hierarchy changes are expected to be rare after initial load.
  - We can:
    - Either recompute `SubjectEdOrg` memberships for affected subjects immediately, or
    - Defer to a background reconciliation process.

---

## Authorization Algorithm for Create/Update/Delete/Get-by-ID of a Contact-securable Document

As with Student-securable documents, backend interfaces need to know which
authorization pathways apply to a document. For Contact-securable documents,
authorization is based on Contacts’ EdOrg memberships built via students (the
ContactStudentSchool pathway).

### Create/Update (Contact-securable document)

1. **Core determines that the resource is Contact-securable**  
   - `ProvideAuthorizationSecurableInfoMiddleware` identifies resources that are
     secured by `ContactUniqueId`.
2. **Core extracts the ContactUniqueId from the request body**  
   - `ExtractDocumentSecurityElementsMiddleware` builds
     `DocumentSecurityElements.Contact[0].Value`.
3. **Core builds caller EdOrg filters**  
   - `ProvideAuthorizationFiltersMiddleware` uses contact-related authorization
     strategies to create `AuthorizationFilter.EducationOrganization` values
     from the caller’s EdOrg claims.
4. **Core queries subject→EdOrg membership for the contact**  
   - `RelationshipsBasedAuthorizationHelper.ValidateContactAuthorization` calls
     `IAuthorizationRepository.GetEducationOrganizationsForContact(contactUniqueId)`.
   - In the new design this reads from `SubjectEdOrg`:

    ```sql
    SELECT DISTINCT EducationOrganizationId
    FROM dms.SubjectEdOrg
    WHERE SubjectType = Contact
       AND SubjectIdentifier = @contactUniqueId
       AND Pathway     = ContactStudentSchool;
     ```

5. **Core compares contact EdOrgIds with caller’s EdOrg filters**  
   - If any EdOrgId in `SubjectEdOrg` intersects with the caller’s EdOrg filters,
     authorization passes for this strategy; otherwise a `NotAuthorized` result
     is returned.
6. **ResourceAuthorizationHandler composes strategies**  
   - Combined with any other strategies (Student-based, namespace, etc.) using
     the same AND/OR semantics as for student-secured resources.

### Get-by-ID (Contact-securable document)

1. Backend returns the Contact-securable document (`EdfiDoc`).
2. Core reconstructs `DocumentSecurityElements` and extracts `ContactUniqueId`.
3. Core runs `ResourceAuthorizationHandler` with the same contact-based logic as
   for Create/Update.

### Delete (Contact-securable document)

1. Core retrieves the target Contact-securable document (or at least its
   `ContactUniqueId`).
2. Core runs `ResourceAuthorizationHandler` for `OperationType.Delete`.
3. If authorized, the delete is executed; otherwise the request is rejected.

---

## Synchronization between StudentSchoolAssociation document, StudentContactAssociation document, SubjectEdOrg, DocumentSubject, and Contact-securable document (Document table)

This is the synchronization phase for contact-based authorization. The
ContactStudentSchool pathway builds contact→EdOrg membership using students’
school memberships.

### StudentSchoolAssociation – additional behavior for Contact support

*Create*

1. Insert `StudentSchoolAssociation` document into `dms.Document`.
2. Run `RecomputeStudentSchoolMembership(studentUniqueId)` as described above
   (Student synchronization).
3. For each Contact related to this student (via `StudentContactAssociation`):
   1. Find ContactUniqueIds for this student by querying `StudentContactAssociation` documents.
   2. For each `contactUniqueId`, call `RecomputeContactStudentSchoolMembership(contactUniqueId)` (see below).

*Update*

1. If `StudentUniqueId` or `schoolId` changes:
   1. Run the StudentSchool recompute logic as above.
   2. For the old and/or new `StudentUniqueId`:
      1. Find all Contacts for the student.
      2. Recompute each contact’s ContactStudentSchool membership
         via `RecomputeContactStudentSchoolMembership(contactUniqueId)`.
2. Update the `StudentSchoolAssociation` document in `dms.Document`.

*Delete*

1. Retrieve `StudentUniqueId` from the deleted `StudentSchoolAssociation`.
2. Delete the `StudentSchoolAssociation` document from `dms.Document`.
3. Run `RecomputeStudentSchoolMembership(studentUniqueId)`.
4. Find Contacts for this student using `StudentContactAssociation` documents,
   and for each `contactUniqueId` run `RecomputeContactStudentSchoolMembership(contactUniqueId)`.

### StudentContactAssociation (Document table)

*Create*

1. Insert `StudentContactAssociation` document into `dms.Document`.
2. Extract `StudentUniqueId` and `ContactUniqueId`.
3. Call `RecomputeContactStudentSchoolMembership(contactUniqueId)`:
   1. Find all `StudentContactAssociation` documents for this contact, and
      gather the referenced `StudentUniqueId`s.
   2. For each `StudentUniqueId`, read their StudentSchool memberships from `SubjectEdOrg`:

      ```sql
      SELECT EducationOrganizationId
      FROM dms.SubjectEdOrg
      WHERE SubjectType = Student
        AND SubjectIdentifier  = @studentUniqueId
        AND Pathway     = StudentSchool;
      ```

   3. Union all EdOrgIds across all students associated with this contact.
   4. Delete `SubjectEdOrg` rows for `(SubjectType = Contact, SubjectIdentifier = contactUniqueId, Pathway = ContactStudentSchool)`.
   5. Insert new `SubjectEdOrg` rows for each EdOrgId in the union.

*Update*

1. Identity for `StudentContactAssociation` is treated as immutable for this
   pathway; if the design allows identity change, handle similarly to create:
   1. Recompute the old contact’s membership if the contact changed.
   2. Recompute the new contact’s membership if applicable.
2. Update the `StudentContactAssociation` document in `dms.Document`.

*Delete*

1. Retrieve `ContactUniqueId` and `StudentUniqueId` from the existing association.
2. Delete the `StudentContactAssociation` document from `dms.Document`.
3. Call `RecomputeContactStudentSchoolMembership(contactUniqueId)` to rebuild
   this contact’s EdOrg memberships based on remaining associations.

### Contact-securable Document (Document table)

These are resources authorized using contact-based strategies (e.g., secured by
`ContactUniqueId`).

*Create*

1. Insert the Contact-securable document into `dms.Document`.
2. Extract `ContactUniqueId` from `DocumentSecurityElements`.
3. Insert `DocumentSubject` row:
   - `(ProjectName, ResourceName, DocumentPartitionKey, DocumentId, SubjectType = Contact, SubjectIdentifier = contactUniqueId)`.

*Update (including cascade)*

1. Detect changes to `ContactUniqueId`:
   1. If none, skip `DocumentSubject` maintenance.
   2. If changed:
      1. Delete existing `DocumentSubject` row(s) for this document with `SubjectType = Contact`.
      2. Insert a new row with the updated `ContactUniqueId`.
2. Update the Contact-securable document in `dms.Document`.

*Delete*

1. Delete the Contact-securable document from `dms.Document`.
2. Delete the related `DocumentSubject` row(s) via FK cascade or explicit delete.
3. `SubjectEdOrg` is unaffected by doc deletion (contact→EdOrg membership remains
   for other documents).

---

## Notes and Possible Future Improvements

- For both student and contact pathways, recomputation always works over the
  full set of relevant relationship documents, which simplifies correctness and
  avoids complex incremental logic.
- If performance testing indicates recomputation is too expensive in some edge
  cases, we can:
  - Introduce incremental adjustments for simple changes (e.g., add/remove one EdOrg).
  - Defer some recomputations to a background job, accepting short-lived staleness.
- EdOrg hierarchy changes can either:
  - Trigger recomputation for affected subjects, or
  - Be handled by a scheduled reconciliation process, depending on operational needs.
