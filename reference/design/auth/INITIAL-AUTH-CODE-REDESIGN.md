• Here’s how I’d rewire the C# side to align with the new DB design (DocumentIndex + DocumentSubject + SubjectEdOrg + redesigned EdOrg hierarchy), while reusing as much of the existing pipeline shape as makes sense.

  I’ll break this into “what stays”, “what changes”, and “what’s new”.

  ———

  1) What stays conceptually

  - Claimset → strategy resolution
      - ResourceActionAuthorizationMiddleware still:
          - Maps HTTP method → action (Create/Read/Update/Delete).
          - Looks up the client’s ClaimSet, finds matching ResourceClaims, and extracts the list of authorization strategies.
          - Populates requestInfo.ResourceActionAuthStrategies.
  - Strategy filters from token claims
      - ProvideAuthorizationFiltersMiddleware still:
          - Resolves each strategy name to an IAuthorizationFiltersProvider (e.g., RelationshipsWithStudentsOnly, RelationshipsWithEdOrgsAndPeople, NamespaceBased, NoFurtherAuthorizationRequired).
          - Produces AuthorizationStrategyEvaluator[] with:
              - AuthorizationStrategyName
              - AuthorizationFilter[] (e.g., EducationOrganization filters representing the caller’s EdOrgIds, Namespace filters, etc.)
              - Operator (AND/OR) for write-time composition.
          - Stores them on requestInfo.AuthorizationStrategyEvaluators.
  - Write-time decision engine
      - ResourceAuthorizationHandler remains the central “authorizer” for writes:
          - Accepts DocumentSecurityElements, AuthorizationStrategyEvaluator[], AuthorizationSecurableInfo[] and delegates to IAuthorizationValidator implementations (Relationships*, NamespaceBased, etc.).
          - Uses IAuthorizationRepository to compute subject→EdOrg memberships and compares with client’s EdOrg filters (AuthorizationFilter[]).
      - This gives you ODS-like strategy composition for POST/PUT/DELETE without changing core semantics.

  ———

  2) What changes: move to SubjectEdOrg + DocumentSubject + DocumentIndex

  2.1 IAuthorizationRepository → new DB model

  Update PostgresqlAuthorizationRepository to use SubjectEdOrg (and new EdOrg tables), instead of the old JSONB-based auth tables:

  - Current methods:
      - GetAncestorEducationOrganizationIds(long[] educationOrganizationIds)
      - GetEducationOrganizationsForStudent(string studentUniqueId)
      - GetEducationOrganizationsForStudentResponsibility(string studentUniqueId)
      - GetEducationOrganizationsForContact(string contactUniqueId)
      - GetEducationOrganizationsForStaff(string staffUniqueId)
  - New implementations (examples):
      - GetEducationOrganizationsForStudent:
          - SELECT DISTINCT EducationOrganizationId FROM dms.SubjectEdOrg WHERE SubjectType = Student AND SubjectIdentifier = $1 AND Pathway IN (StudentSchool, StudentResponsibility);
      - GetEducationOrganizationsForStudentResponsibility:
          - Same but Pathway = StudentResponsibility.
      - GetEducationOrganizationsForContact:
          - SubjectType = Contact, Pathway = ContactStudentSchool.
      - GetEducationOrganizationsForStaff:
          - SubjectType = Staff, Pathway = StaffEdOrg.
      - GetAncestorEducationOrganizationIds:
          - Calls a new GetEducationOrganizationAncestors() function over EducationOrganization/EducationOrganizationRelationship.

  All RelationshipsBasedAuthorizationHelper.*Validate*Authorization methods keep their signatures and logic; only the repository queries change.

  2.2 Drop JSONB auth arrays & specialized auth tables

  - Remove usage of:
      - StudentSchoolAuthorizationEdOrgIds, StudentEdOrgResponsibilityAuthorizationIds, ContactStudentSchoolAuthorizationEdOrgIds, StaffEducationOrganizationAuthorizationEdOrgIds columns from dms.Document and all related code.
      - StudentSchoolAssociationAuthorization, StudentEducationOrganizationResponsibilityAuthorization, ContactStudentSchoolAuthorization, StaffEducationOrganizationAuthorization, StudentSecurableDocument, ContactSecurableDocument, StaffSecurableDocument, plus the
        triggers that maintain them.
      - EducationOrganizationHierarchyTermsLookup and its trigger; keep only the clean adjacency model (EducationOrganization + EducationOrganizationRelationship).

  2.3 Upsert/Update backend: new write helpers

  Replace DocumentAuthorizationHelper and all JSONB auth writes with a helper that maintains DocumentSubject and SubjectEdOrg.

  - New service (backend): e.g., SubjectMembershipWriter
      - Methods (conceptual):
          - MaintainDocumentSubjects(IUpsertRequest or IUpdateRequest, long documentId, short partitionKey, NpgsqlConnection, NpgsqlTransaction)
          - MaintainSubjectEdOrgForRelationship(IUpsertRequest/IUpdateRequest/IDeleteRequest, long documentId, short partitionKey, NpgsqlConnection, NpgsqlTransaction)
  - UpsertDocument changes:
      - Before: after authorization and before commit:
          - Compute JSONB auth arrays via DocumentAuthorizationHelper.GetAuthorizationEducationOrganizationIds.
          - Insert/update dms.Document with those arrays.
          - Insert into StudentSecurableDocument/ContactSecurableDocument/StaffSecurableDocument.
          - Insert/update EducationOrganizationHierarchy row.
      - After (new design):
          1. Authorization: unchanged – still uses ResourceAuthorizationHandler + new IAuthorizationRepository.
          2. Insert/Update dms.Document:
              - InsertDocumentAndAlias and UpdateDocumentEdfiDoc stop accepting auth JSONB parameters; they only write EdfiDoc, SecurityElements, etc.
          3. Maintain EdOrg hierarchy:
              - Replace InsertEducationOrganizationHierarchy/UpdateEducationOrganizationHierarchy calls with:
                  - InsertEducationOrganization (node for EducationOrganizationId).
                  - InsertEducationOrganizationRelationship for each parent EdOrg found in EducationOrganizationHierarchyInfo.
          4. Maintain document→subject mapping:
              - Call SubjectMembershipWriter.MaintainDocumentSubjects(...):
                  - Inspect ResourceInfo.AuthorizationSecurableInfo + DocumentSecurityElements:
                      - If Student-securable: insert (SubjectType=Student, SubjectIdentifier=StudentUniqueId).
                      - If Staff-securable: (Staff, StaffUniqueId).
                      - If Contact-securable: (Contact, ContactUniqueId).
                      - If EdOrg-securable: (EdOrg, EducationOrganizationId).
                  - For updates: delete existing DocumentSubject for the doc key, then insert new rows.
          5. Maintain subject→EdOrg mapping for relationship resources:
              - Use updateRequest.ResourceAuthorizationPathways (built by ProvideAuthorizationPathwayMiddleware) to know we’re handling:
                  - StudentSchoolAssociation, StudentEducationOrganizationResponsibilityAssociation, StudentContactAssociation, StaffEducationOrganizationAssociation, etc.
              - For each pathway:
                  - Read the subject identifier(s) and base EdOrgId from AuthorizationPathway records.
                  - Use GetEducationOrganizationAncestors to expand the EdOrgId.
                  - Rewrite the subject’s rows in SubjectEdOrg for the given Pathway.
          6. Drop calls to DocumentAuthorizationHelper.InsertSecurableDocument / UpdateSecurableDocument.
  - UpdateDocumentById mirrors UpsertDocument:
      - Use ResourceAuthorizationHandler (unchanged).
      - After updating dms.Document:
          - Maintain EdOrg node/relationships for EdOrg resources.
          - Maintain DocumentSubject for securable resources.
          - Maintain SubjectEdOrg for relationship resources.

  2.4 Delete backend

  - DeleteDocumentById should additionally:
      - Delete DocumentSubject rows for the doc (via cascade if you FK DocumentSubject to Document, or via explicit delete).
      - For relationship documents, remove/adjust SubjectEdOrg rows for the subject/pathway they contributed.
          - E.g., for a StudentSchoolAssociation delete, recompute that student’s EdOrg memberships for StudentSchool and rewrite the subject’s rows.

  ———

  3) What changes: query path + DocumentIndex

  3.1 Query pipeline (core)

  - CreateQueryPipeline() in ApiService stays structurally similar:
      - Still calls:
          - ValidateQueryMiddleware
          - ResourceActionAuthorizationMiddleware
          - ProvideAuthorizationSecurableInfoMiddleware
          - ProvideAuthorizationFiltersMiddleware
          - QueryRequestHandler
  - QueryRequestHandler continues to create a QueryRequest with:
      - ResourceInfo
      - QueryElements
      - AuthorizationSecurableInfo (which subject types are securable)
      - AuthorizationStrategyEvaluators (filters from claimset/strategies)
      - PaginationParameters
      - TraceId

  No major changes here; the meaning of the strategy evaluators is unchanged.

  3.2 QueryDocument / SqlAction: use DocumentIndex + DocumentSubject + SubjectEdOrg

  - Replace SqlAction.GetAllDocumentsByResourceNameAsync and GetTotalDocumentsForResourceName with new versions that:
      - Target dms.DocumentIndex instead of dms.Document directly.
      - Use the query design from document-query-indexing-design.md:
          - GIN on QueryFields for application-level filters.
          - B-tree ordering on (ProjectName, ResourceName, CreatedAt, DocumentPartitionKey, DocumentId).
      - Apply authorization as EXISTS joins to DocumentSubject + SubjectEdOrg before ORDER BY / OFFSET / LIMIT.
  - Auth inputs to query:
      - Use AuthorizationStrategyEvaluators to derive:
          - authorized_edorg_ids from AuthorizationFilter.EducationOrganization values.
          - Namespace prefixes from AuthorizationFilter.Namespace values (for NamespaceBased strategies).
      - Namespace predicates are added into the QueryFields @> $filter_json JSON; EdOrg auth is enforced via the EXISTS join.
  - New SQL shape (conceptual):

```sql
    WITH page AS (
        SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
        FROM dms.DocumentIndex di
        WHERE di.ProjectName = $project
          AND di.ResourceName = $resource
          AND di.QueryFields @> $queryFieldsFilter::jsonb   -- includes namespace etc.
          AND EXISTS (
              SELECT 1
              FROM dms.DocumentSubject s
              JOIN dms.SubjectEdOrg se
                ON se.SubjectType       = s.SubjectType
               AND se.SubjectIdentifier = s.SubjectIdentifier
              WHERE s.ProjectName          = di.ProjectName
                AND s.ResourceName         = di.ResourceName
                AND s.DocumentPartitionKey = di.DocumentPartitionKey
                AND s.DocumentId           = di.DocumentId
                -- SubjectType/Pathway conditions derived from AuthorizationSecurableInfo + strategies
                AND se.EducationOrganizationId = ANY($authorized_edorg_ids::bigint[])
          )
        ORDER BY di.CreatedAt
        OFFSET $offset LIMIT $limit
    )
    SELECT d.EdfiDoc
    FROM page p
    JOIN dms.Document d
      ON d.DocumentPartitionKey = p.DocumentPartitionKey
     AND d.Id                   = p.DocumentId
    ORDER BY p.CreatedAt;
```
  - AddAuthorizationFilters in SqlAction is replaced by logic that:
      - Computes authorized_edorg_ids from AuthorizationStrategyEvaluators.
      - Generates the EXISTS block above, with subject type/pathway conditions based on AuthorizationSecurableInfo and the strategies in play (e.g., Student-only vs Student+Staff, OR vs AND compositions).

  ———

  4) How ODS-style composition maps into this

  - Write-time composition (ResourceAuthorizationHandler + AuthorizationValidators):
      - Already matches ODS semantics:
          - Multiple strategies (Relationships*, NamespaceBased) with AND/OR, driven by AuthorizationStrategyEvaluator.Operator.
          - Validators delegate to IAuthorizationRepository to check subject→EdOrg memberships for the subjects present in DocumentSecurityElements.
      - After we point IAuthorizationRepository at SubjectEdOrg, write-time behavior stays intact.
  - Read-time composition:
      - For query GETs, the effective set of authorized_edorg_ids is the union of EdOrg filters across relationship-based strategies; AND/OR composition occurs in which subject types and pathways we check for each doc (which we can express as a combination of EXISTS
        clauses over DocumentSubject/SubjectEdOrg).
      - Namespace composition (AND with relationships) is enforced via QueryFields filter + EXISTS.

  ———

  5) Summary of the redesign

  - Core (DMS):
      - Keep the existing JWT → ClaimSet → Strategies → Filters pipeline.
      - Keep ResourceAuthorizationHandler and IAuthorizationValidators, but rewire IAuthorizationRepository to SubjectEdOrg.
      - Continue using ProvideAuthorizationSecurableInfoMiddleware and ProvideAuthorizationPathwayMiddleware to pass subject & pathway context into the backend.
  - Backend (PostgreSQL):
      - Remove JSONB auth arrays, GINs on dms.Document, and all specialized auth tables and triggers.
      - Introduce DocumentSubject and SubjectEdOrg, and redesigned EdOrg tables.
      - Add a backend helper (SubjectMembershipWriter) used by UpsertDocument/UpdateDocumentById/DeleteDocumentById to maintain those tables based on:
          - DocumentSecurityElements + AuthorizationSecurableInfo for docs.
          - ResourceAuthorizationPathways for relationship resources.
      - Update query operations to:
          - Filter and page via DocumentIndex.
          - Enforce authorization via DocumentSubject + SubjectEdOrg joins, using EdOrg lists derived from AuthorizationStrategyEvaluators.

  This keeps the C# surface area familiar (middleware and handlers look largely the same) while cleanly switching the underlying data model to the new relational auth structure.
