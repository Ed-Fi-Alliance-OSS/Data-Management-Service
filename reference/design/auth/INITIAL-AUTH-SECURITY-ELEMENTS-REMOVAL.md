• Think of SecurityElements as having two roles today:

  1. In-memory: a structured view of the doc’s security attributes (DocumentSecurityElements) used by authorization logic.
  2. In-DB: a JSONB column used by SQL for filtering and EdOrg/namespace checks.

  If we drop the JSONB column, we only need to replace the DB usage. The in-memory role stays; we just don’t persist it.

  Here’s how DocumentSubject + SubjectEdOrg + EdOrg substitute for the DB-side SecurityElements uses:

  ———

  1) Query-time authorization (Get by query)

  Today (simplified)

  SqlAction.AddAuthorizationFilters builds WHERE clauses against dms.Document using:

  - SecurityElements->'Namespace'...
  - SecurityElements->'EducationOrganization'...
  - JSONB auth arrays (StudentSchoolAuthorizationEdOrgIds, etc.)

  So the DB uses SecurityElements and auth JSONB arrays to answer: “is this doc in one of the caller’s EdOrgs/namespaces?”

  New model

  We replace all of that with:

  - Namespace: stored in DocumentIndex.QueryFields and filtered via QueryFields @> ... (no SecurityElements needed).
  - EdOrg-based auth: expressed via relational joins:

```sql
    WITH page AS (
        SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
        FROM dms.DocumentIndex di
        WHERE di.ProjectName = $project
          AND di.ResourceName = $resource
          AND di.QueryFields @> $queryFieldsFilter::jsonb        -- includes namespace, other filters
          AND EXISTS (
              SELECT 1
              FROM dms.DocumentSubject s
              JOIN dms.SubjectEdOrg se
                ON se.SubjectType = s.SubjectType
               AND se.SubjectKey  = s.SubjectKey
              WHERE s.ProjectName          = di.ProjectName
                AND s.ResourceName         = di.ResourceName
                AND s.DocumentPartitionKey = di.DocumentPartitionKey
                AND s.DocumentId           = di.DocumentId
                -- SubjectType/Pathway constraints based on strategies
                AND se.EducationOrganizationId = ANY($authorized_edorg_ids::bigint[])
          )
        ORDER BY di.CreatedAt
        OFFSET $offset LIMIT $limit
    )
    SELECT d.EdfiDoc
    FROM page p
    JOIN dms.Document d
      ON d.DocumentPartitionKey = p.DocumentPartitionKey
     AND d.Id                   = p.DocumentId;
```

  - DocumentSubject plays the role “which subjects is this doc about?” (student, staff, contact, EdOrg).
  - SubjectEdOrg plays the role “which EdOrgs is this subject in?” (pre-expanded via the EdOrg hierarchy).
  - The combination replaces all SecurityElements-based SQL authorization filters.

  So for queries, SecurityElements isn’t needed at all; we use DocumentIndex + DocumentSubject + SubjectEdOrg (+ EdOrg tables).

  ———

  2) Per-document authorization (GET by id, PUT, DELETE, POST)

  These use ResourceAuthorizationHandler + IAuthorizationValidators today:

  - For example, GetDocumentById:

```c#
    DocumentSummary doc = ... // includes EdfiDoc + SecurityElements
    var securityElements = doc.SecurityElements.ToDocumentSecurityElements();
    var authResult = await ResourceAuthorizationHandler.Authorize(
        securityElements,
        OperationType.Get,
        traceId
    );
```

  - ResourceAuthorizationHandler then calls validators like RelationshipsWithStudentsOnlyValidator, which in turn call IAuthorizationRepository:
      - GetEducationOrganizationsForStudent(studentUniqueId)
      - GetEducationOrganizationsForStaff(staffUniqueId), etc.

  New model

  We do not replace this with DocumentSubject/SubjectEdOrg directly; instead:

  - We stop persisting SecurityElements in the DB.
  - At read/update time, when we need DocumentSecurityElements, we recompute it from EdfiDoc in memory using the same extractor used in ExtractDocumentSecurityElementsMiddleware.

  Concretely:

  - Change SqlAction.FindDocumentEdfiDocByDocumentUuid / GetDocumentById to only return EdfiDoc (no SecurityElements JSON).
  - In GetDocumentById:

```c#
    DocumentSummary doc = ... // EdfiDoc only
    var securityElements = ExtractDocumentSecurityElements(doc.EdfiDoc, resourceSchema);
    var authResult = await ResourceAuthorizationHandler.Authorize(
        securityElements,
        OperationType.Get,
        traceId
    );
```

  - For UpdateDocumentById and UpsertDocument, we already have DocumentSecurityElements from the request body (extracted in the pipeline), so we don’t need DB SecurityElements at all.

  Then, IAuthorizationRepository is wired to SubjectEdOrg instead of the old JSONB auth tables:

  - GetEducationOrganizationsForStudent(studentUniqueId) becomes:

```sql
    SELECT EducationOrganizationId
    FROM dms.SubjectEdOrg
    WHERE SubjectType = @Student
      AND SubjectKey = @studentUniqueId
      AND Pathway IN (StudentSchool, StudentResponsibility);
```

  - GetEducationOrganizationsForStaff, GetEducationOrganizationsForContact, etc. do the same with appropriate SubjectType/Pathway.

  So for per-document authorization:

  - DocumentSecurityElements is in-memory only (derived from EdfiDoc or request body).
  - SubjectEdOrg + EdOrg tables answer “what EdOrgs does this subject belong to?”.
  - DocumentSubject isn’t needed for single-document auth (we already know the subjects from DocumentSecurityElements).

  ———

  3) EdOrg hierarchy

  The EdOrg hierarchy table(s) no longer combine with SecurityElements in queries:

  - Instead, they are used only when building SubjectEdOrg:
      - On relationship-resource upserts/updates/deletes, we:
          - Read subject key + base EdOrgId from DocumentSecurityElements or AuthorizationPathway.
          - Use EducationOrganizationRelationship / GetEducationOrganizationAncestors() to expand ancestors.
          - Rewrite that subject’s rows in SubjectEdOrg.

  At read time, SubjectEdOrg already contains ancestor-expanded memberships, so reads never touch the hierarchy directly.

  ———

  Putting it together

  - SecurityElements JSONB column is removed from dms.Document.
  - All DB-level authorization logic that used SecurityElements or JSONB auth arrays now uses:
      - DocumentIndex.QueryFields for namespace and query filters.
      - DocumentSubject (doc→subject) + SubjectEdOrg (subject→EdOrg) + EdOrg tables for relationship-based auth.
  - All per-document authorization that used SecurityElements from DB now:
      - Reconstructs DocumentSecurityElements from EdfiDoc in memory.
      - Uses IAuthorizationRepository backed by SubjectEdOrg to check subject memberships.

  So DocumentSubject + SubjectEdOrg + EdOrg replace all SQL uses of SecurityElements, and the in-memory DocumentSecurityElements fills the small remaining role of “what subjects are on this one document?” without ever being persisted.
