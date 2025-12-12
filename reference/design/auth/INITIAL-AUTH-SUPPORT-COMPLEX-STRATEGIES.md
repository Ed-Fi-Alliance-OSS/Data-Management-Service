• The “most complicated” ODS authorization relationships are all in the relationships strategies family; they layer different subject types, path modifiers, and “including deletes” variations. At a high level:

  - Students via responsibility (your example)
      - RelationshipsWithStudentsOnlyThroughResponsibilityAuthorizationStrategy
          - Uses student identifiers only, but routes through StudentEducationOrganizationResponsibilityAssociation instead of StudentSchoolAssociation.
          - Implemented by adding an authorization path modifier "ThroughResponsibility" to student subject endpoints, so the engine hits the corresponding auth.*ThroughResponsibility view.
      - RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletesAuthorizationStrategy
          - Same pathway, but includes soft‑deleted responsibility associations.
          - Uses a shorter path modifier ("ThroughDeletedResponsibility") to hit a view that unions current + deleted relationships.
  - Students via multiple paths and deletes
      - RelationshipsWithStudentsOnlyAuthorizationStrategy
          - Student identifiers only, via “normal” associations (e.g., StudentSchool).
      - RelationshipsWithStudentsOnlyIncludingDeletesAuthorizationStrategy
          - Same student pathways, but extends to deleted student relationship rows.
          - Uses "IncludingDeletes" path modifier for student endpoints so the view joins against a superset of relationships.
  - EdOrgs + people (multi-endpoint strategies)
      - RelationshipsWithEdOrgsAndPeopleAuthorizationStrategy
          - Treats both EdOrg IDs and person IDs (Student/Staff/Parent/Contact) as subject endpoints; any of them can satisfy auth.
          - The filters are an OR of multiple subject endpoints → multiple auth.* views (EdOrg-only, person-based) are eligible to authorize the row.
      - RelationshipsWithEdOrgsAndPeopleIncludingDeletesAuthorizationStrategy
          - Same, but includes deleted relationships; has its own filter-definitions factory and views.
      - RelationshipsWithEdOrgsAndPeopleInvertedAuthorizationStrategy
          - Mixes normal and “inverted” paths:
              - Person identifiers use normal direction.
              - Non-person EdOrg endpoints get an "Inverted" path modifier, so the engine uses “inverse” views where the EdOrg is on the non-standard side of the relationship (e.g., child vs parent).
          - This is used where the natural relationship direction in the model doesn’t line up with how the claim is expressed.
  - EdOrg-only and people-only specializations
      - RelationshipsWithEdOrgsOnlyAuthorizationStrategy / RelationshipsWithEdOrgsOnlyInvertedAuthorizationStrategy
          - Only EdOrg ID properties participate; the inverted variant uses the "Inverted" modifier to flip join direction.
      - RelationshipsWithPeopleOnlyAuthorizationStrategy
          - Only person identifiers (e.g., StudentUSI, StaffUSI, ParentUSI) participate, ignoring EdOrg IDs on the resource.

  All of these are then composed with other strategies (e.g., NamespaceBased, OwnershipBased) via the AND/OR semantics in AuthorizationStrategyFiltering.Operator. So the most complex relationships you need to support functionally in DMS are:

  - Student→EdOrg via both school and responsibility pathways, with/without deletes.
  - Contact→EdOrg via Student relationships (in ODS this is handled within the auth views).
  - Staff→EdOrg via employment/assignment.
  - Mixed EdOrg + person subject endpoints, including inverted relationship directions and including-deletes variants.

  Our new SubjectEdOrg model needs to be able to represent those same combinations (different pathways per subject, including historical/deleted flags if you want “including deletes” semantics) even though we won’t necessarily mirror ODS’s
  view-per-pathway structure.


› How do we support these complex combinations with our DMS design? Do we need changes?


• Explored
  └ List AuthorizationValidation
    Read RelationshipsWithStudentsOnlyThroughResponsibilityValidator.cs
    Read RelationshipsWithEdOrgsAndPeopleValidator.cs

─ Worked for 38s ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

• We can support those complex ODS strategies with the current DMS design, with small extensions in how we use SubjectEdOrg and how we map strategies → subject/pathway sets, but without changing the core table structures.

  I’ll walk through the main “complex” patterns and what they need.

  ———

  Strategy 1: RelationshipsWithStudentsOnly

  - ODS intent: authorize based on a student’s EdOrg relationships (primarily StudentSchoolAssociation; sometimes you may want it to also cover responsibility).
  - Current DMS write‑side:
      - We already maintain SubjectEdOrg for students via the StudentSchool pathway:
          - (SubjectType=Student, SubjectIdentifier=StudentUniqueId, Pathway=StudentSchool, EducationOrganizationId=...).
      - RelationshipsWithStudentsOnlyValidator calls GetEducationOrganizationsForStudent(studentUniqueId).
  - DMS mapping:
      - Implement GetEducationOrganizationsForStudent in PostgresqlAuthorizationRepository as:

        SELECT DISTINCT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = Student
          AND SubjectIdentifier = $1
          AND Pathway = Pathway_StudentSchool;
      - For reads, query auth uses Student subjects + StudentSchool pathway:
          - In the EXISTS auth clause over DocumentSubject + SubjectEdOrg, filter:

            AND s.SubjectType = Student
            AND se.Pathway = Pathway_StudentSchool

  No schema change needed; just define Pathway_StudentSchool and use it consistently.

  ———

  Strategy 2: RelationshipsWithStudentsOnlyThroughResponsibility

  - ODS intent: similar to above, but only via StudentEducationOrganizationResponsibilityAssociation (not StudentSchool).
  - Current DMS write‑side:
      - We will maintain SubjectEdOrg for students via the responsibility pathway:
          - (Student, StudentUniqueId, Pathway=StudentResponsibility, EdOrgId...).
      - RelationshipsWithStudentsOnlyThroughResponsibilityValidator already calls ValidateStudentResponsibilityAuthorization, which uses GetEducationOrganizationsForStudentResponsibility.
  - DMS mapping:
      - Implement GetEducationOrganizationsForStudentResponsibility as:

        SELECT DISTINCT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = Student
          AND SubjectIdentifier = $1
          AND Pathway = Pathway_StudentResponsibility;
      - Read‑side auth: same EXISTS join, but:

        AND s.SubjectType = Student
        AND se.Pathway = Pathway_StudentResponsibility

  Again, no structural change; we just add a Pathway_StudentResponsibility code and wire SubjectEdOrg + IAuthorizationRepository accordingly.

  ———

  Strategy 3: RelationshipsWithEdOrgsAndPeople

  - ODS intent: authorize when any of these subject types is related to the caller’s EdOrgs:
      - EdOrg IDs on the resource.
      - Student identifiers.
      - Staff identifiers.
      - Contact/Parent identifiers (depending on model).
  - Current DMS write‑side:
      - We can already model all of these:
          - EdOrg-secured resources: DocumentSubject row (SubjectType=EdOrg, SubjectIdentifier=EdOrgId).
          - Student-secured: (Student, StudentUniqueId); SubjectEdOrg for StudentSchool/Responsibility.
          - Staff-secured: (Staff, StaffUniqueId); SubjectEdOrg for StaffEdOrg.
          - Contact-secured: (Contact, ContactUniqueId); SubjectEdOrg for ContactStudentSchool.
  - DMS mapping:
      - RelationshipsWithEdOrgsAndPeopleValidator already calls:
          - ValidateEdOrgAuthorization → uses GetAncestorEducationOrganizationIds + EdOrg claims.
          - ValidateStudentAuthorization, ValidateStaffAuthorization, ValidateContactAuthorization.
      - We re-implement these helpers in terms of SubjectEdOrg and EdOrg hierarchy as in earlier messages.
      - For query‑time auth, we treat this strategy as:
          - SubjectTypes allowed: {EdOrg, Student, Staff, Contact}.
          - Pathways:
              - EdOrg: Pathway_EdOrgDirect.
              - Student: Pathway_StudentSchool and/or Pathway_StudentResponsibility.
              - Staff: Pathway_StaffEdOrg.
              - Contact: Pathway_ContactStudentSchool.
      - In SQL, that’s just:

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
              AND (
                    (s.SubjectType = EdOrg   AND se.Pathway = Pathway_EdOrgDirect)
                 OR (s.SubjectType = Student AND se.Pathway IN (Pathway_StudentSchool, Pathway_StudentResponsibility))
                 OR (s.SubjectType = Staff   AND se.Pathway = Pathway_StaffEdOrg)
                 OR (s.SubjectType = Contact AND se.Pathway = Pathway_ContactStudentSchool)
              )
              AND se.EducationOrganizationId = ANY($authorized_edorg_ids)
        )

  This is exactly the OR composition ODS does via multiple subject endpoints and views, just expressed against our generic tables.

  ———

  Strategy 4: RelationshipsWithEdOrgsOnly / PeopleOnly / Inverted

  - EdOrgsOnly: only EdOrg endpoints participate; in DMS we just:
      - Store (EdOrg, EdOrgId) in DocumentSubject.
      - Populate SubjectEdOrg for EdOrgs if needed (e.g., including ancestors).
      - For reads, restrict the auth EXISTS to SubjectType = EdOrg (and the appropriate Pathway_EdOrgDirect).
  - PeopleOnly: only person identifiers (Student/Staff/Parent/Contact).
      - We simply ignore SubjectType = EdOrg when building the auth clause for this strategy.
  - Inverted:
      - In ODS, *InvertedAuthorizationStrategy uses different auth views where EdOrg “on the other side” of the relationship is used.
      - In our model, we can treat inversion as purely a membership computation concern when we build SubjectEdOrg:
          - For EdOrg subjects, decide whether SubjectEdOrg rows represent parents, children, or both.
          - Pathway enum can have Pathway_EdOrgInverted if we need to distinguish.
      - The tables do not need additional structure; we just decide what EdOrgIds go into SubjectEdOrg for a given subject/pathway.

  ———

  Strategy 5: “IncludingDeletes” variants

  This is the one area where semantics depend on how DMS handles deletes:

  - ODS “IncludingDeletes” variants (e.g., RelationshipsWithStudentsOnlyIncludingDeletes, RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes) are about:
      - Being able to authorize based on historical relationships (soft-deleted rows), and
      - Avoiding information leaks about existence via certain operations.
  - In DMS today:
      - Relationship resources (e.g., StudentSchoolAssociation) are hard-deleted from dms.Document.
      - We don’t currently store tombstones for soft deletes.

  To fully replicate “including deletes” semantics, we’d need an additional concept:

  - Either:
      - Soft-delete support for relationship docs, with an IsDeleted or EndDate flag, and include them in SubjectEdOrg for these specific strategies.
      - Or a separate SubjectEdOrgHistory/SubjectEdOrgIncludingDeletes path where we preserve historical memberships.

  However, this is orthogonal to the table layout:

  - We can support it by:
      - Extending Pathway with flags (e.g., Pathway_StudentSchoolIncludingDeletes, Pathway_StudentResponsibilityIncludingDeletes), and
      - Adjusting recomputation of SubjectEdOrg to pull from both live and soft-deleted relationship docs for those pathways.
  - If DMS is satisfied with pure “current state authorization” (no historical/deleted relationships), we can skip including-deletes semantics and just map the non-including versions to our current pathways.

  So:

  - No schema changes are required to add including-deletes support; we’d only:
      - Add one or two extra pathway codes and
      - Introduce how we persist/delete relationship docs (soft vs hard) to populate those pathways.

  ———

  ### Do we need design changes?

  Structurally, no:

  - DocumentSubject(SubjectType, SubjectIdentifier, ...) + SubjectEdOrg(SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId) can express:
      - Multiple pathways per subject (StudentSchool vs Responsibility vs ContactStudent vs StaffEdOrg vs EdOrgDirect).
      - Multiple subject types per document (EdOrg + Student + Staff + Contact).
      - AND/OR compositions over those subjects and pathways in the SQL EXISTS clause.

  What we do need is:

  1. A richer enum/constant set for Pathway, to distinguish:
      - StudentSchool
      - StudentResponsibility
      - ContactStudentSchool
      - StaffEdOrg
      - EdOrgDirect
      - Optional: including-deletes variants or inverted variants if we choose to support them.
  2. A mapping layer (in C#) from strategy name → (SubjectTypes, Pathways):
      - RelationshipsWithStudentsOnly → Student + StudentSchool.
      - RelationshipsWithStudentsOnlyThroughResponsibility → Student + StudentResponsibility.
      - RelationshipsWithEdOrgsAndPeople → {EdOrg+EdOrgDirect} OR {Student+StudentSchool/Responsibility} OR {Staff+StaffEdOrg} OR {Contact+ContactStudentSchool}.
      - RelationshipsWithEdOrgsOnly → EdOrg+EdOrgDirect, etc.
  3. Membership computation code that knows, for each relationship resource:
      - Which (SubjectType, Pathway) it feeds.
      - How to use EducationOrganizationRelationship to expand to ancestors (and whether to invert direction).

  Once those are in place, the current DMS design is flexible enough to express all the complex ODS relationship strategies, including “through responsibility” and mixed EdOrg+people strategies, without further structural DB changes.

 # Detailed examples

 • We can support those strategies without changing the core schema; we just need to be explicit about:

  - Which SubjectTypes + Pathways each strategy uses.
  - How SubjectEdOrg is populated (sync design).
  - How the query-time EXISTS clause is shaped (example-scenarios design).

  Below are three concrete strategies with end‑to‑end examples.

  ———

  ## 1) RelationshipsWithEdOrgsAndPeople

  ODS intent

  - Authorize when the resource is related to the caller’s EdOrgs by any of:
      - EdOrg directly on the resource.
      - A Student on the resource.
      - A Staff on the resource.
      - A Contact/Parent on the resource.

  ### 1.1 Write path & synchronization

  This plugs directly into INITIAL-AUTH-SYNCHRONIZATION-DESIGN.md:

  - Student membership
      - SubjectEdOrg(SubjectType=Student, SubjectIdentifier=StudentUniqueId, Pathway=StudentSchool) and Pathway=StudentResponsibility are maintained exactly as described under:
          - “Authorization Algorithm for Student-securable document”
          - “RecomputeStudentSchoolMembership”
          - “RecomputeStudentResponsibilityMembership”
  - Contact membership (via students)
      - SubjectEdOrg(SubjectType=Contact, SubjectIdentifier=ContactUniqueId, Pathway=ContactStudentSchool) is maintained via:
          - RecomputeContactStudentSchoolMembership(contactUniqueId) (StudentContactAssociation section).
  - Staff membership
      - We add a parallel Staff section in the sync design:
          - On create/update/delete of StaffEducationOrganization*Association:
              - Extract staffUniqueId and educationOrganizationId.
              - Expand ancestors via GetEducationOrganizationAncestors.
              - Rewrite SubjectEdOrg(Staff, staffUniqueId, Pathway_StaffEdOrg, EdOrgId) with the union of ancestors.
  - EdOrg membership for EdOrg subjects
      - For EdOrg-centric resources (like EdOrg documents themselves or EdOrg-securable docs), we treat the EdOrg as a subject:
          - DocumentSubject: (SubjectType=EdOrg, SubjectIdentifier=EducationOrganizationId::text).
      - For SubjectEdOrg we define:
          - Pathway_EdOrgDirect:
              - SubjectIdentifier = EdOrgId.
              - EdOrgIds = self + ancestors (so an SEA subject is related to its LEAs and schools, depending on how you want to interpret).
          - Maintained when:
              - EdOrg documents are created or updated (using the redesigned EdOrg hierarchy).

  So by the time authorization runs, for a given document we can have DocumentSubject rows pointing to:

  - (EdOrg, EdOrgId)
  - (Student, StudentUniqueId)
  - (Staff, StaffUniqueId)
  - (Contact, ContactUniqueId)

  …and SubjectEdOrg rows describing each subject’s ancestor-expanded EdOrgs.

  ### 1.2 Write-time authorization (Create/Update/Delete)

  INITIAL-AUTH-SYNCHRONIZATION-DESIGN.md already describes the write-side pattern:

  - RelationshipsWithEdOrgsAndPeopleValidator:
      - Calls:
          - ValidateEdOrgAuthorization → uses GetAncestorEducationOrganizationIds (EdOrg-only path).
          - ValidateStudentAuthorization → uses GetEducationOrganizationsForStudent (StudentSchool/Responsibility).
          - ValidateStaffAuthorization → uses GetEducationOrganizationsForStaff.
          - ValidateContactAuthorization → uses GetEducationOrganizationsForContact.
      - All of those repository methods are now implemented against SubjectEdOrg, not JSONB:

        -- Student
        SELECT DISTINCT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = Student
          AND SubjectIdentifier = @studentUniqueId
          AND Pathway IN (Pathway_StudentSchool, Pathway_StudentResponsibility);

        -- Staff
        SELECT DISTINCT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = Staff
          AND SubjectIdentifier = @staffUniqueId
          AND Pathway = Pathway_StaffEdOrg;

        -- Contact
        SELECT DISTINCT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = Contact
          AND SubjectIdentifier = @contactUniqueId
          AND Pathway = Pathway_ContactStudentSchool;

        -- EdOrg subject (if we expose "resource EdOrg" as a subject)
        SELECT DISTINCT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = EdOrg
          AND SubjectIdentifier = @educationOrganizationId::text
          AND Pathway = Pathway_EdOrgDirect;
  - RelationshipsBasedAuthorizationHelper then compares these EdOrg sets with the caller’s EdOrg filters (from AuthorizationFilter.EducationOrganization).

  So for a write, RelationshipsWithEdOrgsAndPeople is already supported by the synchronization design: we just point the repo at SubjectEdOrg.

  ### 1.3 Read path (GET by query)

  In INITIAL-AUTH-DESIGN-EXAMPLE-SCENARIOS.md we already sketched a Student+Staff OR example. For RelationshipsWithEdOrgsAndPeople, we generalize that to include EdOrg + Contact in the same pattern.

  Let’s say we have:

  - authorized_edorg_ids derived from the client’s token claims.
  - A resource StudentAssessment that might have:
      - studentUniqueId
      - staffUniqueId
      - reportedSchoolId (EdOrg)
      - contactUniqueId (if applicable)

  The query follows the same shape as in the example doc, but with four branches:

  WITH page AS (
      SELECT di.DocumentPartitionKey, di.DocumentId, di.ProjectName, di.ResourceName, di.CreatedAt
      FROM dms.DocumentIndex di
      WHERE di.ProjectName = $1
        AND di.ResourceName = $2
        AND di.QueryFields @> $3::jsonb              -- query filters + namespace, etc.
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
              AND (
                   (s.SubjectType = @EdOrg   AND se.Pathway = @Pathway_EdOrgDirect)
                OR (s.SubjectType = @Student AND se.Pathway IN (@Pathway_StudentSchool, @Pathway_StudentResponsibility))
                OR (s.SubjectType = @Staff   AND se.Pathway = @Pathway_StaffEdOrg)
                OR (s.SubjectType = @Contact AND se.Pathway = @Pathway_ContactStudentSchool)
              )
              AND se.EducationOrganizationId = ANY($4::bigint[])   -- authorized_edorg_ids
        )
      ORDER BY di.CreatedAt
      OFFSET $5 LIMIT $6
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id                   = p.DocumentId
  ORDER BY p.CreatedAt;

  This is exactly the composition pattern in INITIAL-AUTH-DESIGN-EXAMPLE-SCENARIOS.md, extended with EdOrg and Contact branches.

  ———

  ## 2) RelationshipsWithEdOrgsOnly

  ODS intent

  - Only EdOrg IDs on the resource matter; person identifiers are ignored.
  - A document is authorized if its EdOrg-valued security elements are related to the caller’s EdOrgs.

  ### 2.1 Write path & synchronization

  We add an explicit EdOrg subject flow to INITIAL-AUTH-SYNCHRONIZATION-DESIGN.md:

  - For EdOrg-securable documents (i.e., resources with an EdOrg security element and a RelationshipsWithEdOrgsOnly strategy):
      - On create/update:
          1. Insert/update the core document.
          2. Extract the EdOrg security elements (e.g., educationOrganizationId, schoolId, localEducationAgencyId) from DocumentSecurityElements.
          3. Maintain DocumentSubject:
              - Delete any existing (SubjectType=EdOrg, SubjectIdentifier=*) rows for this doc.
              - Insert (EdOrg, EdOrgId) for each EdOrg security element we care about.
  - For EdOrg membership in SubjectEdOrg:
      - When EdOrg hierarchy changes (or when an EdOrg document is upserted):
          - For a node E:
              - Compute its ancestor set via EducationOrganizationRelationship.
              - Rewrite SubjectEdOrg(SubjectType=EdOrg, SubjectIdentifier=E.Id::text, Pathway=EdOrgDirect, EducationOrganizationId=ancestors).

  This is fully consistent with the “synchronization via recompute” pattern used for Student and Contact in the sync design.

  ### 2.2 Write-time authorization

  - RelationshipsWithEdOrgsOnlyValidator uses ValidateEdOrgAuthorization:
      - That helper:
          - Reads EdOrg security elements from DocumentSecurityElements (e.g., schoolId, educationOrganizationId).
          - Uses GetAncestorEducationOrganizationIds and the caller’s EdOrg filters.
      - With the new model, GetAncestorEducationOrganizationIds reads the hierarchy tables; we don’t need SubjectEdOrg for this validator.

  So no additional changes: we’re just using the existing validators with the new hierarchy design.

  ### 2.3 Read path (GET by query)

  For read queries, we can either:

  - Use GetAncestorEducationOrganizationIds per request and filter via QueryFields, or
  - Use DocumentSubject + SubjectEdOrg for EdOrg subjects, symmetric with the relationships strategies.

  The second approach is more in line with the example-scenarios design.

  Example SQL for RelationshipsWithEdOrgsOnly

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
              AND s.SubjectType          = @EdOrg
              AND se.Pathway             = @Pathway_EdOrgDirect
              AND se.EducationOrganizationId = ANY ($4::bigint[])
        )
      ORDER BY di.CreatedAt
      OFFSET $5 LIMIT $6
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id                   = p.DocumentId
  ORDER BY p.CreatedAt;

  This fits exactly the pattern in INITIAL-AUTH-DESIGN-EXAMPLE-SCENARIOS.md (one EXISTS with a single subject type and pathway).

  ———

  ## 3) Inverted example (e.g., RelationshipsWithEdOrgsAndPeopleInverted)

  ODS intent

  - Similar to RelationshipsWithEdOrgsAndPeople, but for non-person EdOrg endpoints, the relationship direction is “inverted”:
      - Person identifiers use normal relationships.
      - EdOrg identifiers use an inverted path (e.g., matching children rather than parents, or vice versa).

  In ODS, this is encoded via an AuthorizationPathModifier “Inverted” and separate auth.*Inverted views.

  ### 3.1 How we represent inversion in DMS

  We treat inversion as a different Pathway when we populate SubjectEdOrg:

  - For EdOrg subjects we define:

    Pathway_EdOrgDirectAncestors    // normal direction: subject EdOrg has ancestors in SubjectEdOrg
    Pathway_EdOrgDirectDescendants  // inverted direction: subject EdOrg has descendants in SubjectEdOrg
  - Synchronization extension (in addition to the EdOrg section above):
      - To maintain Pathway_EdOrgDirectAncestors:
          - For each EdOrg E, compute its ancestors and store them as SubjectEdOrg(EdOrg, E, Pathway_EdOrgDirectAncestors, ancestorId).
      - To maintain Pathway_EdOrgDirectDescendants (inverted):
          - For each EdOrg E, compute its descendants using the same EducationOrganizationRelationship table, but traversing down instead of up:

            WITH RECURSIVE descendants AS (
                SELECT EducationOrganizationId, ParentEducationOrganizationId
                FROM dms.EducationOrganizationRelationship
                WHERE ParentEducationOrganizationId = @E

                UNION ALL

                SELECT child.EducationOrganizationId, child.ParentEducationOrganizationId
                FROM dms.EducationOrganizationRelationship child
                JOIN descendants d
                  ON child.ParentEducationOrganizationId = d.EducationOrganizationId
            )
            SELECT DISTINCT EducationOrganizationId FROM descendants;
          - Store those in SubjectEdOrg(EdOrg, E, Pathway_EdOrgDirectDescendants, descendantId).

  This is consistent with INITIAL-AUTH-SYNCHRONIZATION-DESIGN.md: we recompute memberships over the full set of relationships, just in a different direction.

  ### 3.2 Write-time authorization

  - The DMS RelationshipsWithEdOrgsAndPeopleInvertedValidator (if we add one) can:
      - For person subjects: behave as RelationshipsWithEdOrgsAndPeople (Student/Staff/Contact use normal pathways).
      - For EdOrg subjects: use Pathway_EdOrgDirectDescendants instead of ancestors.
          - If we need that on write, we can:
              - Either call IAuthorizationRepository.GetAncestorEducationOrganizationIds against the inverted direction (a new method or flag).
              - Or rely on the SubjectEdOrg EdOrg pathways described above.

  This only affects how we query SubjectEdOrg for EdOrg subjects, not the table structure.

  ### 3.3 Read path (GET by query)

  In query SQL, inversion just changes which pathways we allow for EdOrg subjects.

  Example: “inverted” version of RelationshipsWithEdOrgsAndPeople where:

  - Students/Staff/Contacts use normal pathways (StudentSchool, StudentResponsibility, StaffEdOrg, ContactStudentSchool).
  - EdOrg subjects use Pathway_EdOrgDirectDescendants.

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
              AND (
                   (s.SubjectType = @EdOrg   AND se.Pathway = @Pathway_EdOrgDirectDescendants)
                OR (s.SubjectType = @Student AND se.Pathway IN (@Pathway_StudentSchool, @Pathway_StudentResponsibility))
                OR (s.SubjectType = @Staff   AND se.Pathway = @Pathway_StaffEdOrg)
                OR (s.SubjectType = @Contact AND se.Pathway = @Pathway_ContactStudentSchool)
              )
              AND se.EducationOrganizationId = ANY ($4::bigint[])
        )
      ORDER BY di.CreatedAt
      OFFSET $5 LIMIT $6
  )
  SELECT d.EdfiDoc
  FROM page p
  JOIN dms.Document d
    ON d.DocumentPartitionKey = p.DocumentPartitionKey
   AND d.Id                   = p.DocumentId
  ORDER BY p.CreatedAt;

  This fits directly into the pattern already shown in INITIAL-AUTH-DESIGN-EXAMPLE-SCENARIOS.md:

  - One EXISTS over DocumentIndex → DocumentSubject → SubjectEdOrg.
  - Composition of subject types and pathways using OR.
  - Same indexes on DocumentSubject and SubjectEdOrg.

  ———

  Bottom line

  - RelationshipsWithEdOrgsAndPeople: supported by combining Student/Staff/Contact and EdOrg branches in the EXISTS clause, backed by the Student/Contact/Staff synchronization rules already in INITIAL-AUTH-SYNCHRONIZATION-DESIGN.md.
  - RelationshipsWithEdOrgsOnly: a specialization that uses only EdOrg subjects and the EdOrg pathways in SubjectEdOrg.
  - Inverted strategies: require no structural changes; we add inverted pathways for EdOrg subjects and use them in both membership recomputation and the EXISTS clause.

  All of these fit cleanly within the DocumentSubject + SubjectEdOrg + DocumentIndex design already captured in INITIAL-AUTH-SYNCHRONIZATION-DESIGN.md and INITIAL-AUTH-DESIGN-EXAMPLE-SCENARIOS.md.
