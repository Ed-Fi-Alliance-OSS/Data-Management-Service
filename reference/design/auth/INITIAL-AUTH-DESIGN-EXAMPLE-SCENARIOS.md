› Create some examples of these combined pathways/strategies and sketch the exact DMS plans to see where we might need extra indexes or minor tweaks.

## Example scenarios

• Here are some concrete combined-pathway examples, with how they’d look in the new DMS auth model and where we might want extra indexing or tweaks.

  I’ll assume:

  - DocumentSubject(ProjectName, ResourceName, DocumentPartitionKey, DocumentId, SubjectType, SubjectIdentifier) with PK on all those columns.
  - SubjectEdOrg(SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId) with PK (SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId).
  - DocumentIndex as in your indexing design (GIN on QueryFields, B-tree on (ProjectName, ResourceName, CreatedAt, DocumentPartitionKey, DocumentId)).
  - authorized_edorg_ids is a bigint[] computed once in app code.

  SubjectType / Pathway are small enums; I’ll use symbolic names and assume they’re constants in SQL (or mapped to ints).

  ———

  1) StudentSchool OR StudentResponsibility (classic student-based union)

  ODS semantics

  - Resource is authorized if the student on the doc is in either:
      - StudentSchoolAssociation pathway, or
      - StudentEducationOrganizationResponsibility pathway,
  - And any of those EdOrgs intersects caller’s EdOrg set.

  DMS SQL sketch

  WITH page AS (
      SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
      FROM dms.DocumentIndex di
      WHERE di.ProjectName = $1
        AND di.ResourceName = $2
        AND di.QueryFields @> $3::jsonb
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
              AND s.SubjectType          = SubjectType_Student
              AND se.Pathway             IN (Pathway_StudentSchool, Pathway_StudentResponsibility)
              AND se.EducationOrganizationId = ANY($4::bigint[])
        )
      ORDER BY di.CreatedAt
      OFFSET $5 LIMIT $6
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id = p.DocumentId
  ORDER BY p.CreatedAt;

  Index implications

  - DocumentSubject:
      - PK already starts with (ProjectName, ResourceName, DocumentPartitionKey, DocumentId), which matches the join predicates; good for point lookup.
  - SubjectEdOrg:
      - PK (SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId) is fine:
          - Filter on SubjectType, SubjectIdentifier, Pathway, and EducationOrganizationId = ANY(...).
      - If we see many different Pathway values, we might add:
          - IX_SubjectEdOrg_SubjectPathway(SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId) – but that’s essentially the PK already.

  No extra indexes needed beyond PKs for this union case.

  ———

  2) Student-based auth AND Namespace-based auth

  ODS semantics

  - Resource is authorized if:
      - Student-based relationship holds (as above).
      - AND the resource’s namespace matches one of the caller’s namespace prefixes.

  DMS expression

  - Namespace constraint is best handled via QueryFields (e.g., {"namespace": "uri://ed-fi.org/..."}  with prefix logic in app code).
  - Student auth is handled via DocumentSubject + SubjectEdOrg as before.

  DMS SQL sketch

  WITH page AS (
      SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
      FROM dms.DocumentIndex di
      WHERE di.ProjectName = $1
        AND di.ResourceName = $2
        -- query filters including namespace, built by app from claim prefixes:
        AND di.QueryFields @> $3::jsonb    -- e.g. {"namespace":"uri://ed-fi.org/sis%"} modeled as exact or via |= patterns
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
              AND s.SubjectType          = SubjectType_Student
              AND se.Pathway             IN (Pathway_StudentSchool, Pathway_StudentResponsibility)
              AND se.EducationOrganizationId = ANY($4::bigint[])
        )
      ORDER BY di.CreatedAt
      OFFSET $5 LIMIT $6
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id = p.DocumentId
  ORDER BY p.CreatedAt;

  Index implications

  - Same auth indexes as example 1.
  - Namespace filtering relies on:
      - GIN (QueryFields jsonb_path_ops) as defined in the DocumentIndex design.
  - If namespace queries are common and heavy, we might:
      - Ensure the projection stores namespace as a single scalar key (not array).
      - Optionally add a simple B-tree expression index on ((QueryFields->>'namespace')) if prefix matching is done as LIKE 'prefix%' instead of @>.

  No change needed on auth tables; tuning may be focused on QueryFields if namespace filters dominate.

  ———

  3) Student OR Staff pathways (either relationship can authorize)

  Scenario

  - Resource is authorized if either:
      - It is about a Student in caller’s EdOrg set, OR
      - It is about a Staff member in caller’s EdOrg set.
  - This approximates a composite strategy (StudentRelationships OR StaffRelationships).

  DMS SQL sketch

  We express this as a disjunction of two EXISTS conditions:

  WITH page AS (
      SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
      FROM dms.DocumentIndex di
      WHERE di.ProjectName = $1
        AND di.ResourceName = $2
        AND di.QueryFields @> $3::jsonb
        AND (
            EXISTS (
                -- Student side
                SELECT 1
                FROM dms.DocumentSubject s
                JOIN dms.SubjectEdOrg se
                  ON se.SubjectType       = s.SubjectType
                 AND se.SubjectIdentifier = s.SubjectIdentifier
                WHERE s.ProjectName          = di.ProjectName
                  AND s.ResourceName         = di.ResourceName
                  AND s.DocumentPartitionKey = di.DocumentPartitionKey
                  AND s.DocumentId           = di.DocumentId
                  AND s.SubjectType          = SubjectType_Student
                  AND se.Pathway             IN (Pathway_StudentSchool, Pathway_StudentResponsibility)
                  AND se.EducationOrganizationId = ANY($4::bigint[])
            )
            OR
            EXISTS (
                -- Staff side
                SELECT 1
                FROM dms.DocumentSubject s
                JOIN dms.SubjectEdOrg se
                  ON se.SubjectType       = s.SubjectType
                 AND se.SubjectIdentifier = s.SubjectIdentifier
                WHERE s.ProjectName          = di.ProjectName
                  AND s.ResourceName         = di.ResourceName
                  AND s.DocumentPartitionKey = di.DocumentPartitionKey
                  AND s.DocumentId           = di.DocumentId
                  AND s.SubjectType          = SubjectType_Staff
                  AND se.Pathway             = Pathway_StaffEdOrg
                  AND se.EducationOrganizationId = ANY($4::bigint[])
            )
        )
      ORDER BY di.CreatedAt
      OFFSET $5 LIMIT $6
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id = p.DocumentId
  ORDER BY p.CreatedAt;

  Index implications

  - Each EXISTS uses the same join pattern and PKs as before.
  - The planner may evaluate either side of the OR depending on row estimates; in practice, both are cheap per-row because:
      - DocumentSubject lookup is by full doc key (prefix of PK).
      - Then SubjectEdOrg is by (SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId) (the PK).
  - If we see that one side of the OR dominates (e.g., most docs are Student-only), we can:
      - Optionally refactor in app code: only add the Staff EXISTS when the resource is actually Staff-securable.
      - Or rely on the planner; this is roughly analogous to ODS having multiple strategies with OR semantics.

  No new index requirements; the OR is a composition of two well-indexed patterns.

  ———

  4) Student AND Staff (both relationships must hold)

  Scenario

  - Resource is authorized only if:
      - It is about Student S in caller’s EdOrg set.
      - AND it is about Staff T in caller’s EdOrg set.
  - This corresponds to an AND composition of two relationship strategies.

  DMS SQL sketch

  Two separate EXISTS, both required:

  WITH page AS (
      SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
      FROM dms.DocumentIndex di
      WHERE di.ProjectName = $1
        AND di.ResourceName = $2
        AND di.QueryFields @> $3::jsonb
        AND EXISTS (
            -- Student side
            SELECT 1
            FROM dms.DocumentSubject s
            JOIN dms.SubjectEdOrg se
              ON se.SubjectType       = s.SubjectType
             AND se.SubjectIdentifier = s.SubjectIdentifier
            WHERE s.ProjectName          = di.ProjectName
              AND s.ResourceName         = di.ResourceName
              AND s.DocumentPartitionKey = di.DocumentPartitionKey
              AND s.DocumentId           = di.DocumentId
              AND s.SubjectType          = SubjectType_Student
              AND se.Pathway             IN (Pathway_StudentSchool, Pathway_StudentResponsibility)
              AND se.EducationOrganizationId = ANY($4::bigint[])
        )
        AND EXISTS (
            -- Staff side
            SELECT 1
            FROM dms.DocumentSubject s
            JOIN dms.SubjectEdOrg se
              ON se.SubjectType       = s.SubjectType
             AND se.SubjectIdentifier = s.SubjectIdentifier
            WHERE s.ProjectName          = di.ProjectName
              AND s.ResourceName         = di.ResourceName
              AND s.DocumentPartitionKey = di.DocumentPartitionKey
              AND s.DocumentId           = di.DocumentId
              AND s.SubjectType          = SubjectType_Staff
              AND se.Pathway             = Pathway_StaffEdOrg
              AND se.EducationOrganizationId = ANY($4::bigint[])
        )
      ORDER BY di.CreatedAt
      OFFSET $5 LIMIT $6
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id = p.DocumentId
  ORDER BY p.CreatedAt;

  Index implications

  - Same as the OR case; we’re just requiring both patterns.
  - Because both EXISTS predicates use the doc key to reach DocumentSubject, there’s no need for composite indexes combining Student and Staff; separate lookups are cheap.

  Again, existing PKs / simple indexes are sufficient.

  ———

  5) EdOrg-only pathway (no subject)

  Scenario

  - Resources directly reference an EdOrg (e.g., School, LocalEducationAgency, or a resource with a direct educationOrganizationId security element).
  - ODS uses EducationOrganization-based strategies (no student/staff).

  DMS modeling

  We can treat the document itself as a subject with SubjectType = EdOrg, SubjectIdentifier = EducationOrganizationId::text:

  - DocumentSubject row for:
     - (SubjectType = EdOrg, SubjectIdentifier = <doc’s EdOrgId>).
  - SubjectEdOrg membership for that “subject” can be either:
      - A single row per ancestor EdOrgId (including self), or
     - We can treat SubjectIdentifier as an EdOrg and bypass SubjectEdOrg and just join to EducationOrganizationHierarchyTermsLookup (if we keep it).

  Simplest DMS SQL (using SubjectEdOrg)

  WITH page AS (
      SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
      FROM dms.DocumentIndex di
      WHERE di.ProjectName = $1
        AND di.ResourceName = $2
        AND di.QueryFields @> $3::jsonb
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
              AND s.SubjectType          = SubjectType_EdOrg
              AND se.Pathway             = Pathway_EdOrgDirect
              AND se.EducationOrganizationId = ANY($4::bigint[])
        )
      ORDER BY di.CreatedAt
      OFFSET $5 LIMIT $6
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id = p.DocumentId
  ORDER BY p.CreatedAt;

  Index implications

  - Same patterns as other cases; no special indexes beyond PKs.
  - If EdOrg-only resources are extremely high-volume and common, we could consider:
      - A dedicated index or view optimized for (ProjectName, ResourceName, EdOrgId) to avoid even the Subject join; but it’s probably unnecessary initially.

  ———

  Where we might need tweaks

  From these examples, the auth access patterns are consistent:

  - Always:
      - Start from DocumentIndex row.
      - Lookup DocumentSubject by full doc key (prefix of its PK).
      - For each subject row, lookup SubjectEdOrg by (SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId) (its PK).
  - Combined strategies are just combinations of these patterns using EXISTS with AND / OR.

  Potential tweaks to keep in mind:

  - If we see a lot of queries where the subject type is known and doc key is not selective enough, we might add:
      - IX_DocumentSubject_Doc_SubjectType(ProjectName, ResourceName, DocumentPartitionKey, DocumentId, SubjectType) – but PK already covers that with SubjectType as the next column.
  - If some strategies never care about Pathway (e.g., any student EdOrg is fine), we can:
      - Relax AND se.Pathway IN (...) to just filter by SubjectType.
      - Or pre-aggregate memberships per SubjectType into a “combined” pathway to reduce rows.
  - For very complex OR combinations, we can consider:
      - Prejoining DocumentSubject + SubjectEdOrg into a view auth.DocumentEdOrg to make plans simpler and reuse indexes, but the underlying access pattern stays the same.

  If you’d like, next we can pick one concrete resource and mock the exact DocumentSubject / SubjectEdOrg rows it would have, then walk through the real EXPLAIN/EXPLAIN ANALYZE behavior once some of this is prototyped.

 ## Mocks 

 ### StudentAssessment using StudentSchool

• StudentAssessment is a good concrete example to mock: it’s common, student-based, and typically secured via student→school/LEA relationships.

  Here’s how I’d sketch it in the new model:

  Scenario

  - Resource: studentAssessments
  - Security:
      - Student-based: caller must be authorized for at least one EdOrg in the student’s EdOrg set (via StudentSchool + EdOrg hierarchy).
      - Namespace-based: namespace must match caller’s namespace prefixes.
  - Caller:
      - Authorized EdOrgs: [255901, 255902, 2559xx LEA ancestor]
      - Namespace prefix: uri://ed-fi.org/sis/tenantA

  Mock data

  - One StudentAssessment document, DocumentId=100, PartitionKey=3:
      - studentUniqueId = 'S-1234'
      - reportedSchoolId = 255901
      - namespace = 'uri://ed-fi.org/sis/tenantA/assessments'
  - Student–school relationships:
      - StudentSchoolAssociation for student S-1234 at schoolId=255901.
      - EducationOrganizationHierarchy says 255901 (School) rolls up to 2559 (LEA).
  - SubjectEdOrg rows (computed when StudentSchoolAssociation was upserted):

    (SubjectType=Student, SubjectIdentifier='S-1234', Pathway=StudentSchool, EducationOrganizationId=255901)
    (SubjectType=Student, SubjectIdentifier='S-1234', Pathway=StudentSchool, EducationOrganizationId=2559)
  - DocumentSubject row for this StudentAssessment (computed when the doc was upserted):

    (ProjectName='Ed-Fi', ResourceName='StudentAssessment',
     DocumentPartitionKey=3, DocumentId=100,
     SubjectType=Student, SubjectIdentifier='S-1234')
  - DocumentIndex row (simplified):

    {
      "ProjectName": "Ed-Fi",
      "ResourceName": "StudentAssessment",
      "DocumentPartitionKey": 3,
      "DocumentId": 100,
      "CreatedAt": "2024-10-01T12:00:00Z",
      "QueryFields": {
        "studentUniqueId": "S-1234",
        "reportedSchoolId": 255901,
        "namespace": "uri://ed-fi.org/sis/tenantA/assessments"
      }
    }

  Authorized GET /studentAssessments

  App builds:

  - projectName = 'Ed-Fi'
  - resourceName = 'StudentAssessment'
  - Query filters → queryFieldsFilter JSONB, e.g. { "studentUniqueId": "S-1234" }
  - authorized_edorg_ids = ARRAY[255901,2559]
  - Namespace handled by including "namespace": "uri://ed-fi.org/sis/tenantA/assessments" (or a prefix-equivalent) in queryFieldsFilter.

  SQL:

  WITH page AS (
      SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
      FROM dms.DocumentIndex di
      WHERE di.ProjectName = 'Ed-Fi'
        AND di.ResourceName = 'StudentAssessment'
        AND di.QueryFields @> $queryFieldsFilter::jsonb
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
              AND s.SubjectType          = SubjectType_Student
              AND se.Pathway             = Pathway_StudentSchool
              AND se.EducationOrganizationId = ANY ($authorized_edorg_ids::bigint[])
        )
      ORDER BY di.CreatedAt
      OFFSET 0
      LIMIT 100
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id                   = p.DocumentId
  ORDER BY p.CreatedAt;

  Indexes involved

  - DocumentIndex:
      - GIN on (QueryFields) → filters on studentUniqueId + namespace.
      - B-tree on (ProjectName, ResourceName, CreatedAt, DocumentPartitionKey, DocumentId) → ordering + partition pruning.
  - DocumentSubject:
      - PK starting with (ProjectName, ResourceName, DocumentPartitionKey, DocumentId) → point lookup per candidate doc.
  - SubjectEdOrg:
      - PK (SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId) → point/IN lookup for (Student, 'S-1234', StudentSchool, any authorized_edorg_ids).

  This is the simplest, realistic case where StudentSchool is the only pathway. If you agree with this as a baseline, next step would be to extend the same doc with a Staff relationship (e.g., teacher who proctored the assessment) and show the AND/OR compositions on
  this same resource.


 ### StudentAssessment using StudentSchool AND/OR Staff

• Let’s extend the StudentAssessment example with staff and show both OR and AND compositions.

  ———

  Extended mock data: StudentAssessment with Staff

  Same StudentAssessment document as before:

  - DocumentId = 100, DocumentPartitionKey = 3
  - studentUniqueId = 'S-1234'
  - reportedSchoolId = 255901
  - namespace = 'uri://ed-fi.org/sis/tenantA/assessments'
  - New: staffUniqueId = 'T-7890' (e.g., teacher/proctor)

  Caller:

  - authorized_edorg_ids = [255901, 2559] (school + LEA)

  EdOrg relationships:

  - Student S-1234:
      - StudentSchoolAssociation at school 255901 → EdOrg set {255901, 2559} via hierarchy.
  - Staff T-7890:
      - StaffEducationOrganizationEmployment/Assignment at school 255901 → EdOrg set {255901, 2559}.

  SubjectEdOrg rows:

  -- Student via StudentSchool pathway
  (Student, 'S-1234', Pathway_StudentSchool, 255901)
  (Student, 'S-1234', Pathway_StudentSchool, 2559)

  -- Staff via StaffEdOrg pathway
  (Staff, 'T-7890', Pathway_StaffEdOrg, 255901)
  (Staff, 'T-7890', Pathway_StaffEdOrg, 2559)

  DocumentSubject rows for this StudentAssessment:

  (ProjectName='Ed-Fi', ResourceName='StudentAssessment',
   DocumentPartitionKey=3, DocumentId=100,
   SubjectType=Student, SubjectIdentifier='S-1234')

  (ProjectName='Ed-Fi', ResourceName='StudentAssessment',
   DocumentPartitionKey=3, DocumentId=100,
   SubjectType=Staff,   SubjectIdentifier='T-7890')

  DocumentIndex row (QueryFields unchanged except maybe we add staff if needed for query):

  {
    "ProjectName": "Ed-Fi",
    "ResourceName": "StudentAssessment",
    "DocumentPartitionKey": 3,
    "DocumentId": 100,
    "CreatedAt": "2024-10-01T12:00:00Z",
    "QueryFields": {
      "studentUniqueId": "S-1234",
      "reportedSchoolId": 255901,
      "namespace": "uri://ed-fi.org/sis/tenantA/assessments"
    }
  }

  ———

  1) Student OR Staff can authorize

  Semantics: like ODS (StudentRelationships OR StaffRelationships).

  WITH page AS (
      SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
      FROM dms.DocumentIndex di
      WHERE di.ProjectName = 'Ed-Fi'
        AND di.ResourceName = 'StudentAssessment'
        AND di.QueryFields @> $queryFieldsFilter::jsonb  -- includes namespace, other filters
        AND (
            -- Student branch
            EXISTS (
                SELECT 1
                FROM dms.DocumentSubject s
                JOIN dms.SubjectEdOrg se
                  ON se.SubjectType       = s.SubjectType
                 AND se.SubjectIdentifier = s.SubjectIdentifier
                WHERE s.ProjectName          = di.ProjectName
                  AND s.ResourceName         = di.ResourceName
                  AND s.DocumentPartitionKey = di.DocumentPartitionKey
                  AND s.DocumentId           = di.DocumentId
                  AND s.SubjectType          = SubjectType_Student
                  AND se.Pathway             = Pathway_StudentSchool
                  AND se.EducationOrganizationId = ANY($authorized_edorg_ids::bigint[])
            )
            OR
            -- Staff branch
            EXISTS (
                SELECT 1
                FROM dms.DocumentSubject s
                JOIN dms.SubjectEdOrg se
                  ON se.SubjectType       = s.SubjectType
                 AND se.SubjectIdentifier = s.SubjectIdentifier
                WHERE s.ProjectName          = di.ProjectName
                  AND s.ResourceName         = di.ResourceName
                  AND s.DocumentPartitionKey = di.DocumentPartitionKey
                  AND s.DocumentId           = di.DocumentId
                  AND s.SubjectType          = SubjectType_Staff
                  AND se.Pathway             = Pathway_StaffEdOrg
                  AND se.EducationOrganizationId = ANY($authorized_edorg_ids::bigint[])
            )
        )
      ORDER BY di.CreatedAt
      OFFSET 0 LIMIT 100
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id                   = p.DocumentId
  ORDER BY p.CreatedAt;

  Behavior for our mock doc

  - Student branch:
      - Finds DocumentSubject row (Student,'S-1234').
      - Joins to SubjectEdOrg rows with Pathway_StudentSchool and EdOrg in [255901,2559] → success.
  - Staff branch:
      - Similarly succeeds via (Staff,'T-7890').
  - Because we have OR, either path is enough; in ODS terms, both strategies would report “authorized” for this doc.

  Index usage

  - DocumentIndex:
      - GIN on QueryFields for filters.
      - B-tree on (ProjectName, ResourceName, CreatedAt, DocumentPartitionKey, DocumentId) for ordering.
  - DocumentSubject:
      - Lookup by (ProjectName, ResourceName, DocumentPartitionKey, DocumentId, SubjectType) using PK; very selective.
  - SubjectEdOrg:
      - Lookup by (SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId) using PK; also selective.

  No additional indexes required; we just use the same pattern twice with OR.

  ———

  2) Student AND Staff must both authorize

  Semantics: both the student and the staff on the doc must be in caller’s EdOrg set (an AND over two pathways).

  WITH page AS (
      SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
      FROM dms.DocumentIndex di
      WHERE di.ProjectName = 'Ed-Fi'
        AND di.ResourceName = 'StudentAssessment'
        AND di.QueryFields @> $queryFieldsFilter::jsonb
        AND EXISTS (
            -- Student branch
            SELECT 1
            FROM dms.DocumentSubject s
            JOIN dms.SubjectEdOrg se
              ON se.SubjectType       = s.SubjectType
             AND se.SubjectIdentifier = s.SubjectIdentifier
            WHERE s.ProjectName          = di.ProjectName
              AND s.ResourceName         = di.ResourceName
              AND s.DocumentPartitionKey = di.DocumentPartitionKey
              AND s.DocumentId           = di.DocumentId
              AND s.SubjectType          = SubjectType_Student
              AND se.Pathway             = Pathway_StudentSchool
              AND se.EducationOrganizationId = ANY($authorized_edorg_ids::bigint[])
        )
        AND EXISTS (
            -- Staff branch
            SELECT 1
            FROM dms.DocumentSubject s
            JOIN dms.SubjectEdOrg se
              ON se.SubjectType       = s.SubjectType
             AND se.SubjectIdentifier = s.SubjectIdentifier
            WHERE s.ProjectName          = di.ProjectName
              AND s.ResourceName         = di.ResourceName
              AND s.DocumentPartitionKey = di.DocumentPartitionKey
              AND s.DocumentId           = di.DocumentId
              AND s.SubjectType          = SubjectType_Staff
              AND se.Pathway             = Pathway_StaffEdOrg
              AND se.EducationOrganizationId = ANY($authorized_edorg_ids::bigint[])
        )
      ORDER BY di.CreatedAt
      OFFSET 0 LIMIT 100
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id                   = p.DocumentId
  ORDER BY p.CreatedAt;

  Behavior

  - For our doc, both EXISTS succeed, so it’s authorized.
  - If the staff member were in a different EdOrg (not in authorized_edorg_ids), the Staff EXISTS would fail and the doc would be filtered out, even if the student branch succeeds.

  This matches an ODS “AND” composition of two relationship-based strategies.

  Index usage

  Same as the OR case; still using only:

  - GIN index on DocumentIndex.QueryFields.
  - PKs on DocumentSubject and SubjectEdOrg.

  Again, no new indexes needed—both branches are narrow, point-like lookups.

  ———

  If you’d like to go one step deeper, we can next sketch how the authorization strategy layer in C# would decide whether to generate the OR vs AND composition for StudentAssessment, and how that mapping might be driven by metadata similar to the ODS security DB.
