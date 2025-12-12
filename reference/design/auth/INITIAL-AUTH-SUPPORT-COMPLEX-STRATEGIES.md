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
          - (SubjectType=Student, SubjectKey=StudentUniqueId, Pathway=StudentSchool, EducationOrganizationId=...).
      - RelationshipsWithStudentsOnlyValidator calls GetEducationOrganizationsForStudent(studentUniqueId).
  - DMS mapping:
      - Implement GetEducationOrganizationsForStudent in PostgresqlAuthorizationRepository as:

        SELECT DISTINCT EducationOrganizationId
        FROM dms.SubjectEdOrg
        WHERE SubjectType = Student
          AND SubjectKey = $1
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
          AND SubjectKey = $1
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
          - EdOrg-secured resources: DocumentSubject row (SubjectType=EdOrg, SubjectKey=EdOrgId).
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
              ON se.SubjectType = s.SubjectType
             AND se.SubjectKey  = s.SubjectKey
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

  - DocumentSubject(SubjectType, SubjectKey, ...) + SubjectEdOrg(SubjectType, SubjectKey, Pathway, EducationOrganizationId) can express:
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

 