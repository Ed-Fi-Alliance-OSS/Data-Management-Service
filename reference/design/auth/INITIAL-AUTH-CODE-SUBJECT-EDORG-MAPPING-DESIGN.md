• Below is a more detailed design for how we’d maintain SubjectEdOrg (subject→EdOrg mapping) for relationship resources on both Upsert and UpdateById.

  I’ll focus on:

  - Which resources are treated as relationship resources.
  - How we detect affected subjects on insert/update.
  - How we recompute their SubjectEdOrg memberships.
  - Where this plugs into UpsertDocument and UpdateDocumentById.

  I’ll assume the generic tables we discussed:

  - SubjectEdOrg(SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId, PK=all)
  - EducationOrganization + EducationOrganizationRelationship
  - Document and (eventually) DocumentIndex

  ———

  ## 1) Relationship resources and pathways

  We treat the following as relationship resources (they define subject↔EdOrg membership):

  - StudentSchoolAssociation → Student’s EdOrg membership via school
      - Pathway: Pathway_StudentSchool
  - StudentEducationOrganizationResponsibilityAssociation → Student’s EdOrg membership via generic EdOrg
      - Pathway: Pathway_StudentResponsibility
  - StudentContactAssociation → Contact’s EdOrg membership via linked students’ school memberships
      - Pathway: Pathway_ContactStudentSchool
  - StaffEducationOrganizationEmploymentAssociation / StaffEducationOrganizationAssignmentAssociation
    → Staff’s EdOrg membership via EdOrg
      - Pathway: Pathway_StaffEdOrg

  We’ll have a simple mapping in the backend, for example:
```c#
  enum SubjectType { Student, Contact, Staff, EdOrg }

  enum Pathway
  {
      StudentSchool = 1,
      StudentResponsibility = 2,
      ContactStudentSchool = 3,
      StaffEdOrg = 4
  }
```
  And a mapping from ResourceInfo.ResourceName to (SubjectType, Pathway, “subject key property”):

  - StudentSchoolAssociation → (Student, StudentSchool, studentUniqueId)
  - StudentEducationOrganizationResponsibilityAssociation → (Student, StudentResponsibility, studentUniqueId)
  - StudentContactAssociation → (Contact, ContactStudentSchool, contactUniqueId)
  - StaffEducationOrganization*Association → (Staff, StaffEdOrg, staffUniqueId)

  We can either hard-code this mapping or derive it from ResourceSchema.AuthorizationPathways and DocumentSecurityElements, but the logic is the same.

  ———

  ## 2) General recompute pattern

  Instead of trying to incrementally adjust SubjectEdOrg per relationship row, we recompute the full membership for a subject+pathway whenever we touch that subject via that pathway. This avoids the need for per-row reference counts and keeps logic simple and
  deterministic.

  Generic algorithm:

  RecomputeSubjectEdOrgFor(subjectType, subjectKey, pathway):

    1. Find all relationship documents for this (subjectType, subjectKey, pathway).
    2. From those docs, extract all base EdOrgIds referenced by that pathway.
    3. For each base EdOrgId, compute ancestor EdOrgIds using EducationOrganizationRelationship.
    4. Build the union of all ancestor EdOrgIds.
    5. Delete existing SubjectEdOrg rows for (subjectType, subjectKey, pathway).
    6. Insert new SubjectEdOrg rows for each EdOrgId in the union.

  This is always executed inside the same transaction as the doc insert/update/delete, so the membership is consistent with the new state of the relationship docs.

  How we do step (1) depends on the resource and subject type.

  ———

  ## 3) Per-pathway recomputation details

  ### 3.1 StudentSchool (StudentSchoolAssociation → Student/StudentSchool pathway)

  Subject: Student (SubjectType = Student, SubjectIdentifier = studentUniqueId)
  Pathway: Pathway_StudentSchool
  Base EdOrg field: schoolId on StudentSchoolAssociation

  RecomputeStudentSchoolMembership(studentUniqueId)

  1. Fetch all StudentSchoolAssociation docs for this student
      - Either via DocumentIndex (preferred with the new design):
```sql
        SELECT (d.EdfiDoc->'schoolReference'->>'schoolId')::bigint AS SchoolId
        FROM dms.Document d
        WHERE d.ProjectName = 'Ed-Fi'
          AND d.ResourceName = 'StudentSchoolAssociation'
          AND d.SecurityElements->'Student'->0->>'Value' = $studentUniqueId;
```
        Or, once DocumentIndex(QueryFields) is implemented, via:

```sql
        SELECT di.DocumentPartitionKey, di.DocumentId
        FROM dms.DocumentIndex di
        WHERE di.ProjectName = 'Ed-Fi'
          AND di.ResourceName = 'StudentSchoolAssociation'
          AND di.QueryFields @> $jsonbFilter  -- {"studentUniqueId":"S-1234"}
        JOIN dms.Document d
          ON d.DocumentPartitionKey = di.DocumentPartitionKey
         AND d.Id = di.DocumentId;
```
  2. For each SchoolId, compute ancestors

     For each distinct schoolId:
```sql
     SELECT EducationOrganizationId
     FROM dms.GetEducationOrganizationAncestors(schoolId);
```
     Accumulate into a HashSet<long> of EdOrgIds.
  3. Rewrite SubjectEdOrg for this student/pathway
```sql
     DELETE FROM dms.SubjectEdOrg
     WHERE SubjectType = @Student
       AND SubjectIdentifier = @studentUniqueId
       AND Pathway = @StudentSchool;

     INSERT INTO dms.SubjectEdOrg (SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId)
     VALUES (@Student, @studentUniqueId, @StudentSchool, @eachEdOrgId);
```
  ### 3.2 StudentResponsibility (StudentEdOrgResponsibilityAssociation)

  Same pattern, but:

  - Base EdOrg field: educationOrganizationId on StudentEducationOrganizationResponsibilityAssociation.
  - Pathway: Pathway_StudentResponsibility.
  - Subject: same Student / studentUniqueId.

  RecomputeStudentResponsibilityMembership(studentUniqueId)

  - Query all StudentEducationOrganizationResponsibilityAssociation docs for the student.
  - Extract educationOrganizationReference.educationOrganizationId from each.
  - Expand ancestors as above, union, and rewrite SubjectEdOrg for (Student, studentUniqueId, StudentResponsibility).

  ### 3.3 StaffEdOrg (StaffEmployment/Assignment)

  Subject: Staff (SubjectType = Staff, SubjectIdentifier = staffUniqueId)
  Pathway: Pathway_StaffEdOrg
  Base EdOrg field: educationOrganizationId on StaffEducationOrganization...Association.

  RecomputeStaffEdOrgMembership(staffUniqueId)

  - Query all staff EdOrg association docs for this staff member.
  - Extract educationOrganizationReference.educationOrganizationId.
  - Expand ancestors and rewrite SubjectEdOrg for (Staff, staffUniqueId, StaffEdOrg).

  ### 3.4 ContactStudentSchool (StudentContactAssociation → Contact via Students)

  This one is slightly different because contact→EdOrg membership is derived via students.

  Subject: Contact (SubjectType = Contact, SubjectIdentifier = contactUniqueId)
  Pathway: Pathway_ContactStudentSchool

  RecomputeContactStudentSchoolMembership(contactUniqueId)

  1. Find all students for this contact

     Query StudentContactAssociation docs for contactUniqueId:
```sql
     SELECT d.SecurityElements->'Student'->0->>'Value' AS StudentUniqueId
     FROM dms.Document d
     WHERE d.ProjectName = 'Ed-Fi'
       AND d.ResourceName = 'StudentContactAssociation'
       AND d.SecurityElements->'Contact'->0->>'Value' = $contactUniqueId;
```
     Collect distinct studentUniqueIds.
  2. Get students’ EdOrg memberships

     For each studentUniqueId:
      - Prefer using SubjectEdOrg (already maintained by StudentSchool pathway):
```sql
        SELECT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = @Student
          AND SubjectIdentifier = @studentUniqueId
          AND Pathway = @StudentSchool;
```
      - Union all EdOrgIds across all students into one set for this contact.
  3. Rewrite SubjectEdOrg for the contact
```sql
     DELETE FROM dms.SubjectEdOrg
     WHERE SubjectType = @Contact
       AND SubjectIdentifier = @contactUniqueId
       AND Pathway = @ContactStudentSchool;

     INSERT INTO dms.SubjectEdOrg (SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId)
     VALUES (@Contact, @contactUniqueId, @ContactStudentSchool, @eachEdOrgId);
```
  This relies on StudentSchool memberships being up-to-date (so for StudentContactAssociation we want StudentSchool recomputation to happen first when both change).

  ———

  ## 4) When and how we call recompute on Upsert and UpdateById

  ### 4.1 UpsertDocument (insert path)

  In UpsertDocument.Upsert, after we decide we’re doing an INSERT:

  1. Insert dms.Document + Alias.
  2. Maintain EdOrg node / relationships for EdOrg resources.
  3. Maintain DocumentSubject (doc→subject).
  4. Maintain SubjectEdOrg for relationship resources:
      - Determine if this resource is a relationship resource via ResourceInfo.ResourceName (or ResourceAuthorizationPathways).
      - Extract the relevant subject key from upsertRequest.DocumentSecurityElements:
          - For StudentSchoolAssociation: securityElements.Student[0].Value → studentUniqueId.
          - For StudentEducationOrganizationResponsibilityAssociation: same.
          - For StudentContactAssociation: securityElements.Contact[0].Value.
          - For StaffEducationOrganization*: securityElements.Staff[0].Value.
      - Call the appropriate recompute function:

```C#
        if (resourceName == "StudentSchoolAssociation")
            RecomputeStudentSchoolMembership(studentUniqueId);
        else if (resourceName == "StudentEducationOrganizationResponsibilityAssociation")
            RecomputeStudentResponsibilityMembership(studentUniqueId);
        else if (resourceName == "StudentContactAssociation")
            RecomputeContactStudentSchoolMembership(contactUniqueId);
        else if (resourceName is StaffEmployment/Assignment)
            RecomputeStaffEdOrgMembership(staffUniqueId);
```
      - All inside the same transaction.

  Because we recompute based on all relationship docs for that subject, the new doc (just inserted) will be included in the query.

  ### 4.2 UpsertDocument (update path) and UpdateDocumentById

  For updates, we need to account for old and new subject keys, in case identity moved (e.g., studentUniqueId or staffUniqueId changed). Both UpsertDocument and UpdateDocumentById already fetch the existing document before update:

  - UpsertDocument.Upsert:
      - documentFromDb = await _sqlAction.FindDocumentByReferentialId(...)
  - UpdateDocumentById.UpdateById:
      - Same.

  We can then:

  1. Compute old subject keys from documentFromDb.SecurityElements:

     var oldSecurityElements = documentFromDb.SecurityElements.ToDocumentSecurityElements();
     string? oldStudentUniqueId = oldSecurityElements.Student.FirstOrDefault()?.Value;
     string? oldContactUniqueId = oldSecurityElements.Contact.FirstOrDefault()?.Value;
     string? oldStaffUniqueId = oldSecurityElements.Staff.FirstOrDefault()?.Value;
  2. Compute new subject keys from updateRequest.DocumentSecurityElements or upsertRequest.DocumentSecurityElements.
  3. After we update the dms.Document row (and EdOrg node/relationships, and DocumentSubject), we call recompute for any subject that might have changed for these relationship resources.

     Example for StudentSchoolAssociation update:

```C#
     if (resourceName == "StudentSchoolAssociation")
     {
         var oldStudent = oldSecurityElements.Student.FirstOrDefault()?.Value;
         var newStudent = newSecurityElements.Student.FirstOrDefault()?.Value;

         // Recompute for old and new if they differ or just new if same
         if (!string.IsNullOrEmpty(oldStudent))
             RecomputeStudentSchoolMembership(oldStudent);

         if (!string.IsNullOrEmpty(newStudent) && newStudent != oldStudent)
             RecomputeStudentSchoolMembership(newStudent);
     }
```
     Similarly for:
      - StudentEducationOrganizationResponsibilityAssociation → RecomputeStudentResponsibilityMembership.
      - StaffEducationOrganization* → RecomputeStaffEdOrgMembership.
      - StudentContactAssociation → RecomputeContactStudentSchoolMembership for old and new contact.
  4. Delete semantics (for completeness, though you asked about upsert/update):

     In DeleteDocumentById, after we delete the relationship doc, we:
      - Retrieve the old document’s security elements before delete.
      - Call the appropriate recompute for that subject/pathway (now the query over relationship docs will see one fewer row, possibly reducing subject’s EdOrg set).

  ———

  ## 5) Interaction with pipeline types

  - ProvideAuthorizationPathwayMiddleware already builds AuthorizationPathway records per resource using DocumentSecurityElements.
      - We can either:
          - Use these to confirm what type of relationship resource we’re dealing with (instead of raw ResourceName), or
          - Drive everything off ResourceName and ResourceSchema.AuthorizationPathways mapping.
  - ResourceAuthorizationPathways are already flowed into UpsertRequest/UpdateRequest/DeleteRequest, so we can inject ISubjectMembershipWriter into UpsertDocument / UpdateDocumentById and pass updateRequest.ResourceAuthorizationPathways if we prefer a metadata-driven
    approach.

  The recomputation logic itself remains as above; the only question is whether we look at resourceName or at AuthorizationPathway types to decide which recompute to call.

  ———

  This gives you a clear, deterministic pattern for maintaining SubjectEdOrg for relationship resources on both upsert and update paths:

  - Always recompute full memberships for any affected subject+pathway by scanning the narrow set of relationship docs for that subject.
  - Use the redesigned EdOrg hierarchy to expand to ancestor EdOrgs.
  - Do it inside the same transaction as the document change so reads see a consistent view.
