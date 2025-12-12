
› Go into more detail on what SubjectMembershipWriter looks like


• Here’s a concrete design for SubjectMembershipWriter: what it does, how it’s called, and how it interacts with the new tables.

  ———

  Role

  SubjectMembershipWriter is a backend helper that runs inside the same transaction as dms.Document changes and maintains:

  - dms.DocumentSubject – which subjects a document is about.
  - dms.SubjectEdOrg – which EdOrgs each subject belongs to, per pathway.

  It has no authorization logic; it just keeps the auth tables consistent with the domain data.

  ———

  Dependencies

  Put it in the PostgreSQL backend (e.g., EdFi.DataManagementService.Backend.Postgresql/SubjectMembershipWriter.cs) and inject:

  - ISqlAction (or a narrower interface) with methods like:
      - InsertDocumentSubject(...), DeleteDocumentSubjects(...)
      - InsertSubjectEdOrg(...), DeleteSubjectEdOrgForSubjectPathway(...)
      - SelectRelationshipBaseEdOrgsForStudentSchool(...) (optional helper if you choose to recompute memberships from relationship docs, see below).
  - ILogger<SubjectMembershipWriter>

  All methods accept NpgsqlConnection + NpgsqlTransaction so they participate in the same transaction as document upserts/updates/deletes.

  ———

  Public surface

  Two main categories of operations:

  1. Document → Subject mapping (for securable resources)
  2. Subject → EdOrg mapping (for relationship resources)

  I’d expose them via an interface:

```C#
  public interface ISubjectMembershipWriter
  {
      Task MaintainDocumentSubjectsAsync(
          ResourceInfo resourceInfo,
          DocumentSecurityElements securityElements,
          long documentId,
          short documentPartitionKey,
          NpgsqlConnection connection,
          NpgsqlTransaction transaction
      );

      Task MaintainSubjectEdOrgForUpsertAsync(
          IReadOnlyList<AuthorizationPathway> resourceAuthorizationPathways,
          JsonNode edfiDoc,
          NpgsqlConnection connection,
          NpgsqlTransaction transaction
      );

      Task MaintainSubjectEdOrgForDeleteAsync(
          IReadOnlyList<AuthorizationPathway> resourceAuthorizationPathways,
          JsonNode edfiDoc,
          NpgsqlConnection connection,
          NpgsqlTransaction transaction
      );
  }
```

  You’d call:

  - MaintainDocumentSubjectsAsync from UpsertDocument.AsInsert and UpdateDocumentById.UpdateById after inserting/updating dms.Document.
  - MaintainSubjectEdOrgForUpsertAsync and MaintainSubjectEdOrgForDeleteAsync from upsert/update/delete paths for relationship resources (resources whose ResourceSchema.AuthorizationPathways is non-empty and describes StudentSchool, StudentResponsibility,
    ContactStudent, StaffEdOrg, etc.).

  ———

  ### 1) MaintainDocumentSubjectsAsync

  Maps securable documents to their subjects in dms.DocumentSubject.

  Inputs

  - ResourceInfo – tells you:
      - ProjectName, ResourceName (for DocumentSubject key).
      - AuthorizationSecurableInfo[] (which security elements represent subjects for this resource).
  - DocumentSecurityElements – already extracted in pipeline.
  - documentId, documentPartitionKey.
  - connection / transaction.

  Behavior

  1. Delete existing mappings for this document:

     DELETE FROM dms.DocumentSubject
     WHERE ProjectName = @ProjectName
       AND ResourceName = @ResourceName
       AND DocumentPartitionKey = @DocumentPartitionKey
       AND DocumentId = @DocumentId;
  2. Determine subjects based on AuthorizationSecurableInfo and DocumentSecurityElements:

     Example logic (conceptual):

```C#
     var subjects = new List<(SubjectType subjectType, string subjectKey)>();

     var securables = resourceInfo.AuthorizationSecurableInfo.AsEnumerable();

     if (HasSecurable(securables, SecurityElementNameConstants.StudentUniqueId) &&
         securityElements.Student?.Length > 0)
     {
         foreach (var student in securityElements.Student)
             subjects.Add((SubjectType.Student, student.Value));
     }

     if (HasSecurable(securables, SecurityElementNameConstants.ContactUniqueId) &&
         securityElements.Contact?.Length > 0)
     {
         foreach (var contact in securityElements.Contact)
             subjects.Add((SubjectType.Contact, contact.Value));
     }

     if (HasSecurable(securables, SecurityElementNameConstants.StaffUniqueId) &&
         securityElements.Staff?.Length > 0)
     {
         foreach (var staff in securityElements.Staff)
             subjects.Add((SubjectType.Staff, staff.Value));
     }

     if (HasSecurable(securables, SecurityElementNameConstants.EducationOrganization) &&
         securityElements.EducationOrganization?.Length > 0)
     {
         foreach (var edOrg in securityElements.EducationOrganization)
             subjects.Add((SubjectType.EducationOrganization, edOrg.Id.ToString()));
     }
```

      - You can choose to:
          - Only take FirstOrDefault() (current behavior).
          - Or insert one DocumentSubject row per element (better for multi-student resources).
  3. Insert new mappings:

     For each (subjectType, subjectKey):

```sql
     INSERT INTO dms.DocumentSubject (
         ProjectName,
         ResourceName,
         DocumentPartitionKey,
         DocumentId,
         SubjectType,
         SubjectKey
     )
     VALUES (@ProjectName, @ResourceName, @DocumentPartitionKey, @DocumentId, @SubjectType, @SubjectKey);
```
     Use a batch insert via ISqlAction for efficiency.

  ———

  ### 2) MaintainSubjectEdOrgForUpsertAsync

  Maintains subject→EdOrg memberships in dms.SubjectEdOrg for relationship resources (StudentSchoolAssociation, StudentEdOrgResponsibility, StudentContactAssociation, StaffEdOrg associations, etc.).

  Inputs

  - ResourceAuthorizationPathways – from ProvideAuthorizationPathwayMiddleware, tells you which pathway(s) apply and carries keys extracted from the document (StudentUniqueId, ContactUniqueId, StaffUniqueId, SchoolId, EducationOrganizationId).
  - edfiDoc – full document payload as JsonNode (only needed if you want to re-read values; the pathway records usually suffice).
  - connection / transaction.

  High-level behavior

  For each pathway in resourceAuthorizationPathways:

  1. Extract the subject key and the base EdOrgId(s).
  2. Compute the full set of EdOrg ancestors via GetEducationOrganizationAncestors.
  3. Rewrite that subject’s memberships for that pathway in SubjectEdOrg:
      - DELETE existing rows for (SubjectType, SubjectKey, Pathway).
      - INSERT one row per ancestor EducationOrganizationId.

  This is the simplest, safe approach: we treat the current resource instance as authoritative for that subject/pathway; if you need to consider multiple relationship rows per subject/pathway, you can extend this to aggregate across all relationship documents (see
  note below).

  Per-pathway logic

  Pseudo-code:

```C#:
  public async Task MaintainSubjectEdOrgForUpsertAsync(
      IReadOnlyList<AuthorizationPathway> pathways,
      JsonNode edfiDoc,
      NpgsqlConnection connection,
      NpgsqlTransaction transaction)
  {
      foreach (var pathway in pathways)
      {
          switch (pathway)
          {
              case AuthorizationPathway.StudentSchoolAssociation ssa:
                  await MaintainStudentSchoolMembershipAsync(ssa, connection, transaction);
                  break;

              case AuthorizationPathway.StudentEducationOrganizationResponsibilityAssociation seora:
                  await MaintainStudentResponsibilityMembershipAsync(seora, connection, transaction);
                  break;

              case AuthorizationPathway.StudentContactAssociation sca:
                  await MaintainContactMembershipAsync(sca, connection, transaction);
                  break;

              case AuthorizationPathway.StaffEducationOrganizationAssociation sea:
                  await MaintainStaffMembershipAsync(sea, connection, transaction);
                  break;
          }
      }
  }
```
  Each Maintain*MembershipAsync does roughly:

  - Validate the keys (e.g., StudentUniqueId present).
  - Use GetEducationOrganizationAncestors to compute edOrgIds from SchoolId / EducationOrganizationId.
  - Delete and insert:

```C#
  await DeleteSubjectEdOrgAsync(
      SubjectType.Student,
      studentUniqueId,
      Pathway.StudentSchool,
      connection,
      transaction);

  foreach (var edOrgId in edOrgIds)
  {
      await InsertSubjectEdOrgAsync(
          SubjectType.Student,
          studentUniqueId,
          Pathway.StudentSchool,
          edOrgId,
          connection,
          transaction);
  }
```

  Note on multiple relationship documents per subject/pathway

  The simplest implementation above assumes the current document completely determines that subject’s membership for that pathway. If you need to support multiple relationship docs per subject/pathway (e.g., multiple StudentSchoolAssociations per student), you have
  two options:

  - Recompute from all relationship docs for that subject on each upsert/delete:
      - Query dms.Document for all relevant relationship resources for this subject.
      - Extract base EdOrgIds from their payloads.
      - Union and expand to ancestors.
      - Write the resulting set into SubjectEdOrg as above.
  - Or extend SubjectEdOrg schema to include a RelationshipDocumentId/PartitionKey, so each relationship doc contributes its own rows and you SELECT DISTINCT EducationOrganizationId when reading.
      - In that case, MaintainSubjectEdOrgForUpsertAsync only inserts rows for this doc; MaintainSubjectEdOrgForDeleteAsync deletes rows for this doc.
      - IAuthorizationRepository would read:

        SELECT DISTINCT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = @Student
          AND SubjectKey = @studentUniqueId
          AND Pathway = @StudentSchool;

  The initial design assumes “rewrite per subject+pathway” (simpler schema), but the second option is a straightforward refinement if you want to avoid recomputation across docs.

  ———

  ### 3) MaintainSubjectEdOrgForDeleteAsync

  Handles relationship deletes.

  Inputs

  - ResourceAuthorizationPathways – built from the deleted document’s security elements before the delete.
  - edfiDoc – original payload if you need it.
  - connection / transaction.

  Behavior

  Depends on which membership strategy you choose:

  - If you recompute from all relationship docs:
      - On delete:
          - Use AuthorizationPathway to get subject key & base EdOrgId.
          - Query remaining relationship docs for that subject/pathway.
          - Compute new membership set.
          - Rewrite SubjectEdOrg for (SubjectType, SubjectKey, Pathway).
  - If you include relationship doc id in SubjectEdOrg:
      - On delete:
          - Just delete rows for this specific doc’s (SubjectType, SubjectKey, Pathway, RelationshipDocId, RelationshipDocPartitionKey).
          - No need to recompute.

  Implementation shape matches the upsert path; it just picks the correct delete pattern.

  ———

  ### 4) Where SubjectMembershipWriter is called

  - UpsertDocument.AsInsert / AsUpdate:
      - After InsertDocumentAndAlias / UpdateDocumentEdfiDoc and EdOrg node/relationship maintenance:
          - await subjectMembershipWriter.MaintainDocumentSubjectsAsync(...).
      - If the resource’s AuthorizationPathways indicate it is a relationship resource:
          - await subjectMembershipWriter.MaintainSubjectEdOrgForUpsertAsync(...).
  - UpdateDocumentById.UpdateById:
      - Same pattern: after document update and EdOrg hierarchy updates, call MaintainDocumentSubjectsAsync and MaintainSubjectEdOrgForUpsertAsync when applicable.
  - DeleteDocumentById.DeleteById:
      - Before/after deleting the Document row (depending on FK design):
          - If this is a relationship resource, call MaintainSubjectEdOrgForDeleteAsync(...).
      - DocumentSubject rows are removed via FK cascade or explicit DELETE.

  ———

  This gives you a clear, single point (SubjectMembershipWriter) responsible for keeping DocumentSubject and SubjectEdOrg in sync with the documents and relationship resources, while keeping all logic explicit and transaction-scoped.
› Go into more detail on what SubjectMembershipWriter looks like


• Here’s a concrete design for SubjectMembershipWriter: what it does, how it’s called, and how it interacts with the new tables.

  ———

  Role

  SubjectMembershipWriter is a backend helper that runs inside the same transaction as dms.Document changes and maintains:

  - dms.DocumentSubject – which subjects a document is about.
  - dms.SubjectEdOrg – which EdOrgs each subject belongs to, per pathway.

  It has no authorization logic; it just keeps the auth tables consistent with the domain data.

  ———

  Dependencies

  Put it in the PostgreSQL backend (e.g., EdFi.DataManagementService.Backend.Postgresql/SubjectMembershipWriter.cs) and inject:

  - ISqlAction (or a narrower interface) with methods like:
      - InsertDocumentSubject(...), DeleteDocumentSubjects(...)
      - InsertSubjectEdOrg(...), DeleteSubjectEdOrgForSubjectPathway(...)
      - SelectRelationshipBaseEdOrgsForStudentSchool(...) (optional helper if you choose to recompute memberships from relationship docs, see below).
  - ILogger<SubjectMembershipWriter>

  All methods accept NpgsqlConnection + NpgsqlTransaction so they participate in the same transaction as document upserts/updates/deletes.

  ———

  Public surface

  Two main categories of operations:

  1. Document → Subject mapping (for securable resources)
  2. Subject → EdOrg mapping (for relationship resources)

  I’d expose them via an interface:

```C#
  public interface ISubjectMembershipWriter
  {
      Task MaintainDocumentSubjectsAsync(
          ResourceInfo resourceInfo,
          DocumentSecurityElements securityElements,
          long documentId,
          short documentPartitionKey,
          NpgsqlConnection connection,
          NpgsqlTransaction transaction
      );

      Task MaintainSubjectEdOrgForUpsertAsync(
          IReadOnlyList<AuthorizationPathway> resourceAuthorizationPathways,
          JsonNode edfiDoc,
          NpgsqlConnection connection,
          NpgsqlTransaction transaction
      );

      Task MaintainSubjectEdOrgForDeleteAsync(
          IReadOnlyList<AuthorizationPathway> resourceAuthorizationPathways,
          JsonNode edfiDoc,
          NpgsqlConnection connection,
          NpgsqlTransaction transaction
      );
  }
```

  You’d call:

  - MaintainDocumentSubjectsAsync from UpsertDocument.AsInsert and UpdateDocumentById.UpdateById after inserting/updating dms.Document.
  - MaintainSubjectEdOrgForUpsertAsync and MaintainSubjectEdOrgForDeleteAsync from upsert/update/delete paths for relationship resources (resources whose ResourceSchema.AuthorizationPathways is non-empty and describes StudentSchool, StudentResponsibility,
    ContactStudent, StaffEdOrg, etc.).

  ———

  ### 1) MaintainDocumentSubjectsAsync

  Maps securable documents to their subjects in dms.DocumentSubject.

  Inputs

  - ResourceInfo – tells you:
      - ProjectName, ResourceName (for DocumentSubject key).
      - AuthorizationSecurableInfo[] (which security elements represent subjects for this resource).
  - DocumentSecurityElements – already extracted in pipeline.
  - documentId, documentPartitionKey.
  - connection / transaction.

  Behavior

  1. Delete existing mappings for this document:

     DELETE FROM dms.DocumentSubject
     WHERE ProjectName = @ProjectName
       AND ResourceName = @ResourceName
       AND DocumentPartitionKey = @DocumentPartitionKey
       AND DocumentId = @DocumentId;
  2. Determine subjects based on AuthorizationSecurableInfo and DocumentSecurityElements:

     Example logic (conceptual):

```C#
     var subjects = new List<(SubjectType subjectType, string subjectKey)>();

     var securables = resourceInfo.AuthorizationSecurableInfo.AsEnumerable();

     if (HasSecurable(securables, SecurityElementNameConstants.StudentUniqueId) &&
         securityElements.Student?.Length > 0)
     {
         foreach (var student in securityElements.Student)
             subjects.Add((SubjectType.Student, student.Value));
     }

     if (HasSecurable(securables, SecurityElementNameConstants.ContactUniqueId) &&
         securityElements.Contact?.Length > 0)
     {
         foreach (var contact in securityElements.Contact)
             subjects.Add((SubjectType.Contact, contact.Value));
     }

     if (HasSecurable(securables, SecurityElementNameConstants.StaffUniqueId) &&
         securityElements.Staff?.Length > 0)
     {
         foreach (var staff in securityElements.Staff)
             subjects.Add((SubjectType.Staff, staff.Value));
     }

     if (HasSecurable(securables, SecurityElementNameConstants.EducationOrganization) &&
         securityElements.EducationOrganization?.Length > 0)
     {
         foreach (var edOrg in securityElements.EducationOrganization)
             subjects.Add((SubjectType.EducationOrganization, edOrg.Id.ToString()));
     }
```
      - You can choose to:
          - Only take FirstOrDefault() (current behavior).
          - Or insert one DocumentSubject row per element (better for multi-student resources).
  3. Insert new mappings:

     For each (subjectType, subjectKey):
```sql
     INSERT INTO dms.DocumentSubject (
         ProjectName,
         ResourceName,
         DocumentPartitionKey,
         DocumentId,
         SubjectType,
         SubjectKey
     )
     VALUES (@ProjectName, @ResourceName, @DocumentPartitionKey, @DocumentId, @SubjectType, @SubjectKey);

     Use a batch insert via ISqlAction for efficiency.
```
  ———

  ### 2) MaintainSubjectEdOrgForUpsertAsync

  Maintains subject→EdOrg memberships in dms.SubjectEdOrg for relationship resources (StudentSchoolAssociation, StudentEdOrgResponsibility, StudentContactAssociation, StaffEdOrg associations, etc.).

  Inputs

  - ResourceAuthorizationPathways – from ProvideAuthorizationPathwayMiddleware, tells you which pathway(s) apply and carries keys extracted from the document (StudentUniqueId, ContactUniqueId, StaffUniqueId, SchoolId, EducationOrganizationId).
  - edfiDoc – full document payload as JsonNode (only needed if you want to re-read values; the pathway records usually suffice).
  - connection / transaction.

  High-level behavior

  For each pathway in resourceAuthorizationPathways:

  1. Extract the subject key and the base EdOrgId(s).
  2. Compute the full set of EdOrg ancestors via GetEducationOrganizationAncestors.
  3. Rewrite that subject’s memberships for that pathway in SubjectEdOrg:
      - DELETE existing rows for (SubjectType, SubjectKey, Pathway).
      - INSERT one row per ancestor EducationOrganizationId.

  This is the simplest, safe approach: we treat the current resource instance as authoritative for that subject/pathway; if you need to consider multiple relationship rows per subject/pathway, you can extend this to aggregate across all relationship documents (see
  note below).

  Per-pathway logic

  Pseudo-code:

```C#
  public async Task MaintainSubjectEdOrgForUpsertAsync(
      IReadOnlyList<AuthorizationPathway> pathways,
      JsonNode edfiDoc,
      NpgsqlConnection connection,
      NpgsqlTransaction transaction)
  {
      foreach (var pathway in pathways)
      {
          switch (pathway)
          {
              case AuthorizationPathway.StudentSchoolAssociation ssa:
                  await MaintainStudentSchoolMembershipAsync(ssa, connection, transaction);
                  break;

              case AuthorizationPathway.StudentEducationOrganizationResponsibilityAssociation seora:
                  await MaintainStudentResponsibilityMembershipAsync(seora, connection, transaction);
                  break;

              case AuthorizationPathway.StudentContactAssociation sca:
                  await MaintainContactMembershipAsync(sca, connection, transaction);
                  break;

              case AuthorizationPathway.StaffEducationOrganizationAssociation sea:
                  await MaintainStaffMembershipAsync(sea, connection, transaction);
                  break;
          }
      }
  }
```
  Each Maintain*MembershipAsync does roughly:

  - Validate the keys (e.g., StudentUniqueId present).
  - Use GetEducationOrganizationAncestors to compute edOrgIds from SchoolId / EducationOrganizationId.
  - Delete and insert:

```C#
  await DeleteSubjectEdOrgAsync(
      SubjectType.Student,
      studentUniqueId,
      Pathway.StudentSchool,
      connection,
      transaction);

  foreach (var edOrgId in edOrgIds)
  {
      await InsertSubjectEdOrgAsync(
          SubjectType.Student,
          studentUniqueId,
          Pathway.StudentSchool,
          edOrgId,
          connection,
          transaction);
  }
```

  Note on multiple relationship documents per subject/pathway

  The simplest implementation above assumes the current document completely determines that subject’s membership for that pathway. If you need to support multiple relationship docs per subject/pathway (e.g., multiple StudentSchoolAssociations per student), you have
  two options:

  - Recompute from all relationship docs for that subject on each upsert/delete:
      - Query dms.Document for all relevant relationship resources for this subject.
      - Extract base EdOrgIds from their payloads.
      - Union and expand to ancestors.
      - Write the resulting set into SubjectEdOrg as above.
  - Or extend SubjectEdOrg schema to include a RelationshipDocumentId/PartitionKey, so each relationship doc contributes its own rows and you SELECT DISTINCT EducationOrganizationId when reading.
      - In that case, MaintainSubjectEdOrgForUpsertAsync only inserts rows for this doc; MaintainSubjectEdOrgForDeleteAsync deletes rows for this doc.
      - IAuthorizationRepository would read:

        SELECT DISTINCT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = @Student
          AND SubjectKey = @studentUniqueId
          AND Pathway = @StudentSchool;

  The initial design assumes “rewrite per subject+pathway” (simpler schema), but the second option is a straightforward refinement if you want to avoid recomputation across docs.

  ———

 ### 3) MaintainSubjectEdOrgForDeleteAsync

  Handles relationship deletes.

  Inputs

  - ResourceAuthorizationPathways – built from the deleted document’s security elements before the delete.
  - edfiDoc – original payload if you need it.
  - connection / transaction.

  Behavior

  Depends on which membership strategy you choose:

  - If you recompute from all relationship docs:
      - On delete:
          - Use AuthorizationPathway to get subject key & base EdOrgId.
          - Query remaining relationship docs for that subject/pathway.
          - Compute new membership set.
          - Rewrite SubjectEdOrg for (SubjectType, SubjectKey, Pathway).
  - If you include relationship doc id in SubjectEdOrg:
      - On delete:
          - Just delete rows for this specific doc’s (SubjectType, SubjectKey, Pathway, RelationshipDocId, RelationshipDocPartitionKey).
          - No need to recompute.

  Implementation shape matches the upsert path; it just picks the correct delete pattern.

  ———

  4) Where SubjectMembershipWriter is called

  - UpsertDocument.AsInsert / AsUpdate:
      - After InsertDocumentAndAlias / UpdateDocumentEdfiDoc and EdOrg node/relationship maintenance:
          - await subjectMembershipWriter.MaintainDocumentSubjectsAsync(...).
      - If the resource’s AuthorizationPathways indicate it is a relationship resource:
          - await subjectMembershipWriter.MaintainSubjectEdOrgForUpsertAsync(...).
  - UpdateDocumentById.UpdateById:
      - Same pattern: after document update and EdOrg hierarchy updates, call MaintainDocumentSubjectsAsync and MaintainSubjectEdOrgForUpsertAsync when applicable.
  - DeleteDocumentById.DeleteById:
      - Before/after deleting the Document row (depending on FK design):
          - If this is a relationship resource, call MaintainSubjectEdOrgForDeleteAsync(...).
      - DocumentSubject rows are removed via FK cascade or explicit DELETE.

  ———

  This gives you a clear, single point (SubjectMembershipWriter) responsible for keeping DocumentSubject and SubjectEdOrg in sync with the documents and relationship resources, while keeping all logic explicit and transaction-scoped.
