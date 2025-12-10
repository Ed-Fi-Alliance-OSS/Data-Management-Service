# Authorization Design (Subject/EdOrg Model)

## 1. Purpose and Goals

This document replaces the current JSONB/trigger‑driven model with a relational, strategy‑friendly design
that:

- Eliminates JSONB authorization arrays and authorization triggers on
  `dms.Document`.
- Removes pathway‑specific authorization tables
  (`…Authorization`, `…SecurableDocument`, etc.).
- Preserves ODS authorization semantics:
  - EducationOrganization‑based authorization (including hierarchy).
  - Student‑, Staff‑, and Contact‑based relationship strategies.
  - Namespace‑based and ownership‑style strategies.
  - Strategy composition (AND/OR across pathways).
- Integrates with the planned `dms.DocumentIndex` query/indexing design so that
  `/data` queries filter and page over authorized subsets without scanning
  `dms.Document`.
- Keeps all authorization enforcement in application code while using narrow relational tables for performance.

---

## 2. High‑Level Model

The new model is built around a small set of generic tables and existing ODS‑
style strategy logic:

1. **`dms.DocumentIndex`**  
   Narrow, hash‑partitioned index table for efficient filtering and paging, from the read performance redesign:
   - Key fields: `(ProjectName, ResourceName, CreatedAt, DocumentPartitionKey, DocumentId)`.
   - `QueryFields JSONB` holds a compact, canonical projection of queryable
     resource fields (from `ResourceSchema.QueryFields`).
   - GIN index on `QueryFields` and B‑tree on `(ProjectName, ResourceName,
     CreatedAt, DocumentPartitionKey, DocumentId)`.

2. **`dms.DocumentSubject`**  
   Generic mapping from documents to “subjects” they are about (students,
   contacts, staff, EdOrgs, etc.). This replaces JSONB auth arrays and
   pathway‑specific authorization tables.

3. **`dms.SubjectEdOrg`**  
   Generic mapping from subjects to EducationOrganizations, including pathway
   information and ancestor expansion through the EdOrg hierarchy.

4. **EducationOrganization hierarchy**  
   Clean adjacency model (`EducationOrganization` + relationships) with a
   recursive function to compute ancestor EdOrgs. The legacy
   `dms.EducationOrganizationHierarchyTermsLookup` table is removed.

Authorization is expressed as an **existence check**:

> A document is readable if there exists a subject on that document that is
> associated (via `SubjectEdOrg`) with at least one of the caller’s authorized
> EducationOrganizationIds (plus any namespace or ownership constraints).

Write‑time logic in C# maintains `DocumentSubject` and `SubjectEdOrg`. There are
no authorization triggers on `dms.Document`.

---

## 3. Data Model

### 3.1 Document and DocumentIndex

The `dms.Document` table remains the store for resource payloads
(`EdfiDoc`). In this design:

- All **authorization JSONB arrays are removed** from `dms.Document`:
  - `StudentSchoolAuthorizationEdOrgIds`
  - `StudentEdOrgResponsibilityAuthorizationIds`
  - `ContactStudentSchoolAuthorizationEdOrgIds`
  - `StaffEducationOrganizationAuthorizationEdOrgIds`
- The `SecurityElements` JSONB column is removed from `dms.Document`. The
  `DocumentSecurityElements` structure still exists **in memory only**, derived
  from `EdfiDoc` or the request body when needed (see section 9).
- Authorization‑relevant relationships are now represented exclusively via
  `DocumentSubject` and `SubjectEdOrg`.

The planned `dms.DocumentIndex` design is adopted as the basis for all
authorization‑aware `/data` queries:

```sql
CREATE TABLE IF NOT EXISTS dms.DocumentIndex (
    DocumentPartitionKey smallint NOT NULL,
    DocumentId           bigint   NOT NULL,
    ProjectName          varchar(256) NOT NULL,
    ResourceName         varchar(256) NOT NULL,
    CreatedAt            timestamp without time zone NOT NULL,
    QueryFields          jsonb    NOT NULL,
    PRIMARY KEY (DocumentPartitionKey, DocumentId, ProjectName, ResourceName)
) PARTITION BY HASH (ProjectName, ResourceName);

ALTER TABLE dms.DocumentIndex
    ADD CONSTRAINT DocumentIndex_document_fk
        FOREIGN KEY (DocumentPartitionKey, DocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id)
        ON DELETE CASCADE;
```

Key points:

- Hash partitions by `(ProjectName, ResourceName)` so each `/data` query
  touches exactly one partition.
- Per‑partition B‑tree on `(ProjectName, ResourceName, CreatedAt,
  DocumentPartitionKey, DocumentId)` for ordered paging.
- Per‑partition GIN on `QueryFields` (using `jsonb_path_ops`) for query filtering.
- Writes go through `dms.InsertNewDocument(...)`, which inserts into both
  `dms.Document` and `dms.DocumentIndex` with precomputed `QueryFields`. Updates
  use a similar stored procedure.

### 3.2 DocumentSubject

`dms.DocumentSubject` represents the subjects a document is “about”. Each row
encodes a single subject instance for a resource instance.

Conceptual schema:

```sql
CREATE TABLE dms.DocumentSubject (
    ProjectName          varchar(256) NOT NULL,
    ResourceName         varchar(256) NOT NULL,
    DocumentPartitionKey smallint     NOT NULL,
    DocumentId           bigint       NOT NULL,

    SubjectType          smallint     NOT NULL, -- e.g. 1 = Student, 2 = Contact, 3 = Staff, 4 = EdOrg, ...
    SubjectIdentifier    text         NOT NULL, -- StudentUniqueId, ContactUniqueId, StaffUniqueId, EdOrgId::text, etc.

    PRIMARY KEY (
        ProjectName,
        ResourceName,
        DocumentPartitionKey,
        DocumentId,
        SubjectType,
        SubjectIdentifier
    )
);

CREATE INDEX IX_DocumentSubject_Subject
    ON dms.DocumentSubject (SubjectType, SubjectIdentifier);
```

Notes:

- `SubjectType` is a `smallint` with a locked mapping in `dms.SubjectType` (see section 4.3).
- `SubjectIdentifier` is the canonical identifier for the subject in that type:
  - Student → `StudentUniqueId` value.
  - Contact → `ContactUniqueId` value.
  - Staff → `StaffUniqueId` value.
  - EdOrg → `EducationOrganizationId` value.
  - Other subject dimensions as well.
- A document may have multiple subjects (e.g., a resource with both student and
  staff securables).

### 3.3 SubjectEdOrg

`dms.SubjectEdOrg` represents subject EdOrg membership, including which
authorization pathway produced that membership.

Conceptual schema:

```sql
CREATE TABLE dms.SubjectEdOrg (
    SubjectType           smallint NOT NULL, -- Student, Contact, Staff, EdOrg, etc.
    SubjectIdentifier     text     NOT NULL, -- unique identifier per subject type
    EducationOrganizationId bigint NOT NULL,

    Pathway               smallint NOT NULL, -- StudentSchool, StudentResponsibility, ContactStudentSchool, StaffEdOrg, etc.

    PRIMARY KEY (
        SubjectType,
        SubjectIdentifier,
        Pathway,
        EducationOrganizationId
    )
);

-- Primary key can be used for:
-- - Fast authorization membership checks in the read path (the `EXISTS` join from
--   `DocumentSubject` → `SubjectEdOrg`, typically constrained by SubjectType,
--   SubjectIdentifier, optional Pathway, and `EducationOrganizationId = ANY(...)`).
-- - Efficient recomputation writes (idempotent delete/insert) scoped to a single
--   `(SubjectType, SubjectIdentifier, Pathway)` set.

CREATE INDEX IX_SubjectEdOrg_EdOrg
    ON dms.SubjectEdOrg (EducationOrganizationId);
```

Notes:

- `SubjectEdOrg` is **not** scoped per document. It describes the global
  membership of a subject across the EdOrg hierarchy.
- Pathways allow separate tracking of different relationship types for the same
  subject:
  - StudentSchool vs StudentResponsibility vs other student relationships.
  - ContactStudentSchool for contacts via students’ school associations.
  - StaffEdOrg for staff employment/assignment.
  - Future pathways (e.g., ProgramParticipation) can be added by introducing new
    enum values.
- The combination `(SubjectType, SubjectIdentifier, Pathway)` can always be recomputed
  from the corresponding relationship documents, see synchronization design is section 6.
- `Pathway` is a `smallint` with a locked mapping in `dms.AuthorizationPathway` (section 4.3).

### 3.4 EducationOrganization Hierarchy

The hierarchy is represented with a clean adjacency model (rather than a
denormalized terms‑lookup table):

- `dms.EducationOrganization` stores the set of known EducationOrganizations (by `EducationOrganizationId`).
- `dms.EducationOrganizationRelationship` stores parent‑child relationships between EdOrgs.

Conceptual DDL:

```sql
-- One row per EdOrg identifier present in the system.
-- Maintained by the service based on EducationOrganization documents (School, LocalEducationAgency, etc.).
CREATE TABLE IF NOT EXISTS dms.EducationOrganization (
    EducationOrganizationId bigint NOT NULL,
    DocumentPartitionKey    smallint NOT NULL,
    DocumentId              bigint   NOT NULL,
    PRIMARY KEY (EducationOrganizationId),
    CONSTRAINT FK_EducationOrganization_Document
        FOREIGN KEY (DocumentPartitionKey, DocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id)
        ON DELETE CASCADE,
    UNIQUE (DocumentPartitionKey, DocumentId)
);

-- Parent-child edges. Multiple parents are allowed
CREATE TABLE IF NOT EXISTS dms.EducationOrganizationRelationship (
    EducationOrganizationId        bigint NOT NULL, -- child
    ParentEducationOrganizationId  bigint NOT NULL, -- parent
    PRIMARY KEY (EducationOrganizationId, ParentEducationOrganizationId),
    CONSTRAINT FK_EdOrgRelationship_Child
        FOREIGN KEY (EducationOrganizationId)
        REFERENCES dms.EducationOrganization (EducationOrganizationId)
        ON DELETE CASCADE,
    CONSTRAINT FK_EdOrgRelationship_Parent
        FOREIGN KEY (ParentEducationOrganizationId)
        REFERENCES dms.EducationOrganization (EducationOrganizationId)
        ON DELETE CASCADE
);

-- Supports descendant lookups; ancestor lookups are driven by the PK on child id.
CREATE INDEX IF NOT EXISTS IX_EducationOrganizationRelationship_Parent
    ON dms.EducationOrganizationRelationship (ParentEducationOrganizationId);
```

A database function computes ancestors:

```sql
CREATE OR REPLACE FUNCTION dms.GetEducationOrganizationAncestors(
    p_educationOrganizationId bigint
)
RETURNS TABLE (EducationOrganizationId bigint)
AS $$
BEGIN
    RETURN QUERY
    WITH RECURSIVE OrganizationHierarchy AS (
        -- Base case: start with the given organization (only if it exists)
        SELECT eo.EducationOrganizationId
        FROM dms.EducationOrganization eo
        WHERE eo.EducationOrganizationId = p_educationOrganizationId

        UNION

        -- Recursive case: get all ancestors via parent edges
        SELECT eor.ParentEducationOrganizationId
        FROM dms.EducationOrganizationRelationship eor
        JOIN OrganizationHierarchy child
          ON child.EducationOrganizationId = eor.EducationOrganizationId
    )
    SELECT EducationOrganizationId
    FROM OrganizationHierarchy
    ORDER BY EducationOrganizationId;
END;
$$ LANGUAGE plpgsql;
```

Key changes:

- `dms.EducationOrganizationHierarchy` (legacy) is replaced by `dms.EducationOrganization` + `dms.EducationOrganizationRelationship`.
- `dms.EducationOrganizationHierarchyTermsLookup` (legacy) and its triggers are removed.
- Ancestor expansion is performed via recursive query when computing
  `SubjectEdOrg` memberships (write path), not at query time on `dms.Document`.

---

## 4. Subject and Pathway Model

### 4.1 SubjectType

`SubjectType` identifies the “dimension” of the subject participating in
authorization:

- `Student` – keyed by `StudentUniqueId`.
- `Contact` – keyed by `ContactUniqueId`.
- `Staff` – keyed by `StaffUniqueId`.
- `EdOrg` – keyed by `EducationOrganizationId::text`.
- (Optional future) Ownership subject, program subject, etc.

This enum is used consistently in:

- `DocumentSubject.SubjectType`.
- `SubjectEdOrg.SubjectType`.
- Application‑level mapping between Ed‑Fi resources and subject dimensions.

### 4.2 Pathway

`Pathway` identifies which authorization pathway produced a subject’s
membership in an EdOrg. Initial pathways:

- `StudentSchool` – derived from `StudentSchoolAssociation` documents.
- `StudentResponsibility` – derived from
  `StudentEducationOrganizationResponsibilityAssociation` documents.
- `ContactStudentSchool` – derived from `StudentContactAssociation` +
  student school memberships.
- `StaffEdOrg` – derived from staff EdOrg employment/assignment associations.
- `EdOrgDirect` – direct EdOrg authorization (e.g., resources directly secured
  on EdOrg).

Strategies can refer to specific pathways (e.g., “relationships with students
only through responsibility”) or to combinations (e.g., “students via school OR
responsibility”).

### 4.3 Locking Down `SubjectType` and `Pathway` Identifiers

This design stores `SubjectType` and `Pathway` as `smallint` for compact keys
and fast joins. However, using ad-hoc numeric enums is fragile: if the numeric
values drift between deployments (or between C# code and database seed data),
authorization can silently misbehave.

To make the mapping explicit introduce lookup
tables with immutable IDs and stable string `Code` values:

```sql
CREATE TABLE IF NOT EXISTS dms.SubjectType (
    SubjectTypeId smallint PRIMARY KEY,
    Code          text     NOT NULL UNIQUE,
    Description   text     NULL
);

CREATE TABLE IF NOT EXISTS dms.AuthorizationPathway (
    PathwayId     smallint PRIMARY KEY,
    SubjectTypeId smallint NOT NULL REFERENCES dms.SubjectType(SubjectTypeId),
    Code          text     NOT NULL,
    Description   text     NULL,
    UNIQUE (SubjectTypeId, Code),
    UNIQUE (SubjectTypeId, PathwayId)
);
```

Enforce integrity:

```sql
ALTER TABLE dms.DocumentSubject
    ADD CONSTRAINT FK_DocumentSubject_SubjectType
        FOREIGN KEY (SubjectType) REFERENCES dms.SubjectType(SubjectTypeId);

ALTER TABLE dms.SubjectEdOrg
    ADD CONSTRAINT FK_SubjectEdOrg_SubjectType
        FOREIGN KEY (SubjectType) REFERENCES dms.SubjectType(SubjectTypeId);

ALTER TABLE dms.SubjectEdOrg
    ADD CONSTRAINT FK_SubjectEdOrg_Pathway
        FOREIGN KEY (Pathway) REFERENCES dms.AuthorizationPathway(PathwayId);

-- Optional but recommended: ensure Pathway is valid for the SubjectType
ALTER TABLE dms.SubjectEdOrg
    ADD CONSTRAINT FK_SubjectEdOrg_SubjectTypePathway
        FOREIGN KEY (SubjectType, Pathway)
        REFERENCES dms.AuthorizationPathway (SubjectTypeId, PathwayId);
```

Seed with fixed IDs (IDs are a contract, only ever add new rows):

```sql
INSERT INTO dms.SubjectType (SubjectTypeId, Code) VALUES
  (1, 'Student'),
  (2, 'Contact'),
  (3, 'Staff'),
  (4, 'EdOrg')
ON CONFLICT DO NOTHING;

INSERT INTO dms.AuthorizationPathway (PathwayId, SubjectTypeId, Code) VALUES
  (10, 1, 'StudentSchool'),
  (11, 1, 'StudentResponsibility'),
  (20, 2, 'ContactStudentSchool'),
  (30, 3, 'StaffEdOrg'),
  (40, 4, 'EdOrgDirect')
ON CONFLICT DO NOTHING;
```

Application behavior:

- At startup, the service should validate that required `(Code → Id)`
  mappings exist and match expected IDs, since specific code depends on them.
- In logs/diagnostics/configuration, refer to `Code` values; use numeric IDs
  only for storage and joins.

### 4.4 Mapping Ed‑Fi Resources to Subjects and Pathways

At a high level:

- **Securable resources** (documents authorized by subject identity):
  - Student‑securable → `DocumentSubject` rows with `(SubjectType = Student,
    SubjectIdentifier = StudentUniqueId)`.
  - Contact‑securable → `(SubjectType = Contact, SubjectIdentifier = ContactUniqueId)`.
  - Staff‑securable → `(SubjectType = Staff, SubjectIdentifier = StaffUniqueId)`.
  - EdOrg‑securable → `(SubjectType = EdOrg, SubjectIdentifier = EdOrgId::text)`.

- **Relationship resources** (documents that define subject→EdOrg memberships):
  - `StudentSchoolAssociation` → `SubjectEdOrg` rows:
    `(SubjectType = Student, SubjectIdentifier = StudentUniqueId, Pathway = StudentSchool, EducationOrganizationId = ancestorEdOrg)`.
  - `StudentEducationOrganizationResponsibilityAssociation` → pathway
    `StudentResponsibility`.
  - `StudentContactAssociation` plus student school memberships → pathway
    `ContactStudentSchool`.
  - Staff EdOrg employment/assignment associations → pathway `StaffEdOrg`.

Resource configuration (via MetaEd and `AuthorizationSecurableInfo`) determines
which subject dimensions apply to each resource.

---

## 5. Application Architecture and Code Changes

### 5.1 Components That Stay Conceptually

The overall ODS‑style authorization pipeline remains:

- **Claimset → Strategy resolution**
  - `ResourceActionAuthorizationMiddleware`:
    - Maps HTTP method → action (Create/Read/Update/Delete).
    - Looks up the client’s ClaimSet and associates ResourceClaims.
    - Extracts the list of authorization strategy names per resource+action.
    - Populates `ResourceActionAuthStrategies` on the request.

- **Strategy filters from token claims**
  - `ProvideAuthorizationFiltersMiddleware`:
    - Resolves each strategy name to an `IAuthorizationFiltersProvider`:
      - Relationship strategies (students, staff, contacts, EdOrgs).
      - `NamespaceBased`.
      - `NoFurtherAuthorizationRequired`.
    - Produces `AuthorizationStrategyEvaluator[]`, each with:
      - Strategy name.
      - `AuthorizationFilter[]` (e.g., EdOrg filters, namespace filters).
      - Operator (AND/OR) for write‑time composition.
    - Stores `AuthorizationStrategyEvaluators` on the request.

- **Write‑time decision engine**
  - `ResourceAuthorizationHandler` remains the central authorizer for writes:
    - Accepts `DocumentSecurityElements`, `AuthorizationStrategyEvaluator[]`,
      `AuthorizationSecurableInfo[]`.
    - Delegates to strategy validators (relationship, namespace, ownership).
    - Uses `IAuthorizationRepository` to compute subject→EdOrg memberships and
      compares with client’s EdOrg filters.

### 5.2 IAuthorizationRepository → New DB Model

`PostgresqlAuthorizationRepository` is reimplemented to use `SubjectEdOrg` and
the new EdOrg hierarchy:

- Existing methods are preserved:
  - `GetAncestorEducationOrganizationIds(long[] edOrgIds)` – uses
    `GetEducationOrganizationAncestors`.
  - `GetEducationOrganizationsForStudent(studentUniqueId)` – `SubjectType =
    Student`, `Pathway IN (StudentSchool, StudentResponsibility)`.
  - `GetEducationOrganizationsForStudentResponsibility(studentUniqueId)` –
    same with `Pathway = StudentResponsibility`.
  - `GetEducationOrganizationsForContact(contactUniqueId)` – `SubjectType =
    Contact`, `Pathway = ContactStudentSchool`.
  - `GetEducationOrganizationsForStaff(staffUniqueId)` – `SubjectType = Staff`,
    `Pathway = StaffEdOrg`.

Strategy helper methods (`RelationshipsBasedAuthorizationHelper.*`) keep their
signatures; only their repository queries change.

### 5.3 Removal of JSONB Auth Arrays and Specialized Tables

The following are **removed** from the design:

- JSONB columns on `dms.Document`:
  - `StudentSchoolAuthorizationEdOrgIds`,
    `StudentEdOrgResponsibilityAuthorizationIds`,
    `ContactStudentSchoolAuthorizationEdOrgIds`,
    `StaffEducationOrganizationAuthorizationEdOrgIds`.
- Specialized tables and triggers:
  - `StudentSchoolAssociationAuthorization`,
    `StudentEducationOrganizationResponsibilityAuthorization`,
    `ContactStudentSchoolAuthorization`, `StaffEducationOrganizationAuthorization`.
  - `StudentSecurableDocument`, `ContactSecurableDocument`, `StaffSecurableDocument`.
  - Triggers on `dms.Document` and legacy EdOrg hierarchy/auth tables that maintain those tables and JSONB arrays.
- Legacy EdOrg hierarchy structures:
  - `dms.EducationOrganizationHierarchy` (replaced by `dms.EducationOrganization` + `dms.EducationOrganizationRelationship`).
  - `dms.EducationOrganizationHierarchyTermsLookup` and its triggers.

All authorization data is instead represented in `DocumentSubject` and
`SubjectEdOrg`.

### 5.4 New Write Helper: SubjectMembershipWriter

A new service encapsulates write‑side maintenance:

- **Responsibilities**
  - Maintain `DocumentSubject` for securable resources:
    - Insert/update/delete subject rows based on `DocumentSecurityElements`.
  - Maintain `SubjectEdOrg` for relationship resources:
    - Recompute subject memberships per pathway when relationship rows change.
  - Coordinate with EdOrg hierarchy for ancestor expansion.

- **Integration points**
  - `UpsertDocument` (insert path):
    - After inserting into `dms.Document` and `dms.DocumentIndex`, call:
      - `SubjectMembershipWriter.MaintainDocumentSubjects(...)` for the new
        document.
      - `SubjectMembershipWriter.MaintainSubjectEdOrgForRelationship(...)` for
        relationship resources.
  - `UpsertDocument` (update path) and `UpdateDocumentById`:
    - After updating `dms.Document` and references, call the same helper with
      old vs new security data.
  - `DeleteDocumentById`:
    - Delete `dms.Document`.
    - Delete related `DocumentSubject` rows (cascade or explicit).
    - For relationship resources, recompute memberships for affected subjects
      via `SubjectEdOrg` (see §6).

All membership updates are done within the same transaction as the document
write.

### 5.5 AddAuthorizationFilters Redesign (Query Path)

The existing `AddAuthorizationFilters` is reworked to target `DocumentIndex` plus
`DocumentSubject` + `SubjectEdOrg`:

1. **Derive authorized EdOrg IDs in C#**  
   Using `AuthorizationStrategyEvaluator[]`, collect all EdOrg filters produced
   by relationship strategies:

   ```csharp
   private static long[] GetAuthorizedEdOrgIds(IQueryRequest queryRequest)
   {
       var edOrgIds = queryRequest.AuthorizationStrategyEvaluators
           .SelectMany(e => e.Filters)
           .OfType<AuthorizationFilter.EducationOrganization>()
           .Select(f => long.Parse(f.Value))
           .Distinct()
           .ToArray();

       return edOrgIds;
   }
   ```

   If relationship strategies are present but this set is empty, the same error
   condition as today applies (authorization failure).

2. **Namespace and other non‑relationship filters**  
   Namespace filters from `AuthorizationStrategyEvaluators` are used to build
   additional `QueryFields @> ...` predicates on `DocumentIndex`, not to filter
   `dms.Document` directly.

3. **SubjectType/Pathway selection**  
   Using `AuthorizationSecurableInfo` and strategy names, determine which
   `(SubjectType, Pathway)` combinations are relevant for the query (e.g.,
   Students via StudentSchool and/or StudentResponsibility, Contacts via
   ContactStudentSchool, Staff via StaffEdOrg).

4. **EXISTS predicate over DocumentSubject + SubjectEdOrg**  
   Inject a single authorization predicate into the `WHERE` over `dms.DocumentIndex`
   (aliased `di`):

   ```sql
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
         -- Optional subject/pathway filters:
         -- AND s.SubjectType IN (...)
         -- AND se.Pathway   IN (...)
         AND se.EducationOrganizationId = ANY ($N::bigint[])
   )
   ```

The rest of the query continues to use `DocumentIndex.QueryFields` and the
paging index as described in §7.

---

## 6. Write‑Side Synchronization and Membership Maintenance

This section summarizes the synchronization model for `DocumentSubject` and
`SubjectEdOrg`. The concrete logic is encapsulated in the
`SubjectMembershipWriter`, but we describe it here at the level of database
effects.

### 6.1 Principles

- **Recomputation over incremental updates**  
  For each subject/pathway (e.g., a given Student via StudentSchool), we
  recompute memberships from scratch by reading all relevant relationship
  documents for that subject.
  - Avoids subtle bugs from incremental “add/remove one EdOrg”.
  - Keeps logic the same across create/update/delete and cascades.

- **Subject‑centric operations**  
  Helpers are of the form:
  - `RecomputeStudentSchoolMembership(studentUniqueId)`
  - `RecomputeStudentResponsibilityMembership(studentUniqueId)`
  - `RecomputeContactStudentSchoolMembership(contactUniqueId)`
  - `RecomputeStaffEdOrgMembership(staffUniqueId)`

  Each helper:
  - Reads the relevant relationship documents for the subject.
  - Computes ancestor EdOrgIds via the hierarchy.
  - Rebuilds the `SubjectEdOrg` rows for `(SubjectType, SubjectIdentifier, Pathway)`.

- **No authorization triggers**  
  All recomputation happens in C# within the service layer using normal SQL,
  not database triggers.

### 6.2 StudentSchoolAssociation

Pathway: `StudentSchool`.

**Create**

1. Insert `StudentSchoolAssociation` document into `dms.Document`.
2. Extract `StudentUniqueId` and `schoolId` from the resource body (or by
   running the same extractor used to build in‑memory `DocumentSecurityElements`
   over `EdfiDoc`).
3. Call `RecomputeStudentSchoolMembership(studentUniqueId)`:
   - Find all `StudentSchoolAssociation` documents for this student.
   - For each distinct `schoolId`, compute ancestor EdOrgIds via
     `GetEducationOrganizationAncestors`.
   - Union all ancestor EdOrgIds across schools.
   - Delete existing `SubjectEdOrg` rows for:
     `(SubjectType = Student, SubjectIdentifier = studentUniqueId, Pathway = StudentSchool)`.
   - Insert one `SubjectEdOrg` row per ancestor EdOrgId.

**Update**

1. Detect changes to `StudentUniqueId` or `schoolId`.
   - If neither changes, skip recomputation.
   - If `StudentUniqueId` changes:
     - `RecomputeStudentSchoolMembership(oldStudentUniqueId)`.
     - `RecomputeStudentSchoolMembership(newStudentUniqueId)`.
   - If only `schoolId` changes:
     - `RecomputeStudentSchoolMembership(studentUniqueId)`.
2. Update the `StudentSchoolAssociation` document in `dms.Document`.

**Delete**

1. Retrieve `StudentUniqueId` from the deleted association.
2. Delete the `StudentSchoolAssociation` document from `dms.Document`.
3. Call `RecomputeStudentSchoolMembership(studentUniqueId)`.

### 6.3 StudentEducationOrganizationResponsibilityAssociation

Pathway: `StudentResponsibility`.

**Create**

1. Insert the responsibility association document into `dms.Document`.
2. Extract `StudentUniqueId` and `educationOrganizationId`.
3. Call `RecomputeStudentResponsibilityMembership(studentUniqueId)`:
   - Find all responsibility associations for this student.
   - For each distinct `educationOrganizationId`, compute ancestor EdOrgIds.
   - Union all ancestors.
   - Delete existing `SubjectEdOrg` rows for:
     `(SubjectType = Student, SubjectIdentifier = studentUniqueId, Pathway = StudentResponsibility)`.
   - Insert one `SubjectEdOrg` row per ancestor EdOrgId.

**Update**

1. Detect changes to `StudentUniqueId` or `educationOrganizationId`.
   - If neither changes, skip.
   - If `StudentUniqueId` changes:
     - `RecomputeStudentResponsibilityMembership(oldStudentUniqueId)`.
     - `RecomputeStudentResponsibilityMembership(newStudentUniqueId)`.
   - If only `educationOrganizationId` changes:
     - `RecomputeStudentResponsibilityMembership(studentUniqueId)`.
2. Update the responsibility association document in `dms.Document`.

**Delete**

1. Retrieve `StudentUniqueId`.
2. Delete the responsibility association document.
3. Call `RecomputeStudentResponsibilityMembership(studentUniqueId)`.

### 6.4 Student‑securable Documents

Student‑securable resources are authorized via student‑based strategies (e.g.,
they have a `StudentUniqueId` securable key).

**Create**

1. Insert the document into `dms.Document`.
2. Extract `StudentUniqueId` from the request body / in‑memory
   `DocumentSecurityElements`.
3. Insert a `DocumentSubject` row:

   ```text
   (ProjectName, ResourceName, DocumentPartitionKey, DocumentId,
    SubjectType = Student, SubjectIdentifier = studentUniqueId)
   ```

4. No `SubjectEdOrg` changes are needed; memberships are defined solely by
   relationship resources.

**Update**

1. Detect changes to `StudentUniqueId`.
   - If unchanged, skip `DocumentSubject` maintenance.
   - If changed:
     - Delete existing `DocumentSubject` rows for this document where
       `SubjectType = Student`.
     - Insert a new row with the updated `StudentUniqueId`.
2. Update the document in `dms.Document`.

**Delete**

1. Delete the document from `dms.Document`.
2. Delete corresponding `DocumentSubject` rows via cascade or explicit delete.
3. `SubjectEdOrg` is unchanged (student’s memberships may still be used for
   other documents).

### 6.5 Contact‑based Pathway: StudentContactAssociation + StudentSchool

Pathway: `ContactStudentSchool`.

Contacts derive their EdOrg memberships via students’ school memberships.

**StudentSchoolAssociation – additional behavior**

On `StudentSchoolAssociation` create/update/delete:

1. Run the StudentSchool recompute logic above.
2. For each affected `StudentUniqueId`, find related contacts via
   `StudentContactAssociation` documents.
3. For each `contactUniqueId`, call
   `RecomputeContactStudentSchoolMembership(contactUniqueId)`:
   - Find all `StudentContactAssociation` documents for the contact.
   - For each referenced `StudentUniqueId`, read student memberships from
     `SubjectEdOrg` where `(SubjectType = Student, Pathway = StudentSchool)`.
   - Union all EdOrgIds across referenced students.
   - Rebuild `SubjectEdOrg` rows for:
     `(SubjectType = Contact, SubjectIdentifier = contactUniqueId, Pathway = ContactStudentSchool)`.

**StudentContactAssociation**

**Create**

1. Insert `StudentContactAssociation` into `dms.Document`.
2. Extract `StudentUniqueId` and `ContactUniqueId`.
3. Call `RecomputeContactStudentSchoolMembership(contactUniqueId)` as described
   above.

**Update**

If identity properties are allowed to change:

1. If `ContactUniqueId` changes:
   - Recompute old contact’s memberships.
   - Recompute new contact’s memberships.
2. Update the association document.

If identity is immutable, no recomputation is needed on update.

**Delete**

1. Retrieve `ContactUniqueId` and `StudentUniqueId`.
2. Delete the association document.
3. Call `RecomputeContactStudentSchoolMembership(contactUniqueId)`.

### 6.6 Contact‑securable Documents

**Create**

1. Insert the document into `dms.Document`.
2. Extract `ContactUniqueId` from the request body / in‑memory
   `DocumentSecurityElements`.
3. Insert `DocumentSubject` row:

   ```text
   (ProjectName, ResourceName, DocumentPartitionKey, DocumentId,
    SubjectType = Contact, SubjectIdentifier = contactUniqueId)
   ```

**Update**

1. Detect changes to `ContactUniqueId`; if changed, rebuild `DocumentSubject`
   rows analogously to the student case.
2. Update the document.

**Delete**

1. Delete the document.
2. Delete `DocumentSubject` rows for `(SubjectType = Contact)`.
3. `SubjectEdOrg` is unchanged (contact memberships remain relevant to other
   documents).

### 6.7 Staff‑based Pathway

Staff memberships (pathway `StaffEdOrg`) are maintained analogously:

- Relationship resources:
  - `StaffEducationOrganizationEmploymentAssociation`.
  - `StaffEducationOrganizationAssignmentAssociation`.
- Helper `RecomputeStaffEdOrgMembership(staffUniqueId)`:
  - Reads all relevant employment/assignment documents.
  - Extracts associated EdOrgIds.
  - Expands ancestors.
  - Rebuilds `SubjectEdOrg` rows for:
    `(SubjectType = Staff, SubjectIdentifier = staffUniqueId, Pathway = StaffEdOrg)`.

Staff‑securable documents insert `DocumentSubject` rows with
`(SubjectType = Staff, SubjectIdentifier = staffUniqueId)` in the same pattern as
student/contact‑securable documents.

### 6.8 EdOrg Hierarchy Changes

EducationOrganization hierarchy changes affect all subject memberships that rely
on those EdOrgs. We need to think about immediate recomputation versus deferring.

- **Immediate recomputation**  
  On hierarchy change, determine affected EdOrgIds and:
  - Enumerate subjects whose memberships include those EdOrgs.
  - Recompute memberships for those subjects and relevant pathways.

- **Deferred reconciliation (maybe?)**  
  Since hierarchy changes are expected to be rare after initial load, the
  initial implementation may defer to a background job that periodically rederives `SubjectEdOrg` from
    the current snapshot of relationship documents and hierarchy.

---

## 7. Read Path and Query Patterns

### 7.1 Authorized Paging over DocumentIndex

All `/data` queries use the `DocumentIndex`‑based paging pattern. Authorization
is enforced inside the same CTE that performs `ORDER BY / OFFSET / LIMIT`, so
the page is computed over already authorized rows.

Representative pattern:

```sql
WITH page AS (
    SELECT
        di.DocumentPartitionKey,
        di.DocumentId,
        di.ProjectName,
        di.ResourceName,
        di.CreatedAt
    FROM dms.DocumentIndex di
    WHERE di.ProjectName = $1
      AND di.ResourceName = $2
      AND di.QueryFields @> $3::jsonb          -- query filters
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
            AND se.EducationOrganizationId = ANY($4::bigint[])
            -- Optional: subject/type pathway predicates
      )
    ORDER BY di.CreatedAt
    OFFSET $5
    LIMIT  $6
)
SELECT d.EdfiDoc
FROM page p
JOIN dms.Document d
  ON d.DocumentPartitionKey = p.DocumentPartitionKey
 AND d.Id                   = p.DocumentId
ORDER BY p.CreatedAt;
```

Notes:

- Planner prunes to the single `DocumentIndex` hash partition for the
  `(ProjectName, ResourceName)` pair.
- GIN on `QueryFields` filters by query parameters before paging.
- `DocumentSubject` + `SubjectEdOrg` joins are backed by narrow B‑tree indexes,
  enabling fast existence checks.
- `totalCount` requests uses the same `WHERE` (including the `EXISTS`)
  against `DocumentIndex`

### 7.2 Namespace‑Based and Other Non‑Relationship Auth

Namespace‑based authorization is fully expressed via `QueryFields`:

- The `QueryFields` projection includes canonical `namespace` values for
  resources where namespace security applies.
- Namespace filters from `AuthorizationStrategyEvaluators` are translated into
  the same JSON object used for other query filters and applied as:

```sql
AND di.QueryFields @> $namespaceFilterJson::jsonb
```

Other non‑relationship strategies (e.g., ownership) can be represented
either as:

- Additional subject types/pathways in `SubjectEdOrg`, or
- Additional `QueryFields` with matching JSON `@>` predicates.

### 7.3 Get by Id

`GET /data/.../{id}` operations:

1. Fetch the document via `dms.Document` (partition + UUID) to obtain `EdfiDoc`.
2. Recompute `DocumentSecurityElements` in memory by running the same extractor
   used in the write pipeline over `EdfiDoc` and the resource schema.
3. Invoke `ResourceAuthorizationHandler` for the `ExistingData` phase with the
   appropriate strategies and the in‑memory `DocumentSecurityElements`.
3. Validators use `IAuthorizationRepository` (which now reads `SubjectEdOrg`)
   to ensure at least one relevant subject membership intersects the caller’s
   EdOrg filters.

For Get‑by‑Id, we do not need the `DocumentIndex` table; the per‑resource
authorization is instance‑level and uses the same strategy pipeline as writes.

---

## 8. Strategy Semantics and Complex Strategies

The new data model preserves ODS‑style authorization semantics. Strategies
compose over the same conceptual dimensions; only their backing storage changes.

### 8.1 Relationship Strategies

Examples:

- `RelationshipsWithStudentsOnly`
- `RelationshipsWithStudentsOnlyThroughResponsibility`
- `RelationshipsWithEdOrgsAndPeople`
- `RelationshipsWithEdOrgsOnly`

Each strategy:

- Defines which `SubjectType` and `Pathway` combinations are relevant:
  - Students via StudentSchool and/or StudentResponsibility.
  - Contacts via ContactStudentSchool.
  - Staff via StaffEdOrg.
  - Direct EdOrg authorization via `(SubjectType = EdOrg, Pathway = EdOrgDirect)`.
- Decides whether multiple pathways are combined with AND or OR at the
  strategy level.

Write‑time behavior:

- Strategies use `IAuthorizationRepository` to:
  - Expand claimset EdOrg filters to ancestor EdOrgs.
  - Compare with subject memberships in `SubjectEdOrg`.

Read‑time behavior:

- `AddAuthorizationFilters` uses the same strategy/evaluator metadata to:
  - Derive the authorized EdOrg set.
  - Decide which subset of `SubjectType`/`Pathway` combinations to consider in
    the `EXISTS` predicate.

### 8.2 Strategy Composition (AND/OR)

`AuthorizationStrategyEvaluator.Operator` continues to drive strategy
composition:

- AND strategies must all succeed:
  - For writes, failure in any AND strategy leads to authorization failure.
  - For reads, authorized EdOrg filters must satisfy all ANDed constraints.
- OR strategies provide alternative pathways:
  - At least one OR branch must succeed.
  - On read, this typically means the authorized EdOrg set and subject/pathway
    filters are the union of OR branches.

Complex strategies such as “StudentSchool OR StudentResponsibility” or “EdOrgs
AND Students” are expressed in strategy code; the `DocumentSubject` +
`SubjectEdOrg` model supports them by providing:

- Distinct pathways in `SubjectEdOrg`.
- Generic subject mapping in `DocumentSubject`.

---

## 9. SecurityElements and QueryFields

### 9.1 SecurityElements

The `SecurityElements` JSONB column is removed from `dms.Document`. Instead:

- `DocumentSecurityElements` remains as an in‑memory structure representing
  security‑relevant fields (StudentUniqueId, ContactUniqueId, StaffUniqueId,
  EducationOrganizationId, Namespace, etc.).
- For writes, `DocumentSecurityElements` is extracted from the request body in
  the pipeline (as today).
- For reads (Get‑by‑Id, update, delete), `DocumentSecurityElements` is
  reconstructed from `EdfiDoc` using the same extractor used in the write
  pipeline.

This in‑memory structure is used to:

- Drive `DocumentSubject` maintenance (identify subject keys when documents are
  created or updated).
- Provide per‑document context to `ResourceAuthorizationHandler` for instance‑
  level authorization (writes and Get‑by‑Id).

All **database‑level** authorization logic that previously relied on
`SecurityElements` now uses:

- `DocumentIndex.QueryFields` for namespace and query filters.
- `DocumentSubject` + `SubjectEdOrg` + EdOrg tables for relationship‑based
  authorization.

### 9.2 QueryFields

`QueryFields` on `DocumentIndex` is the basis for:

- Application query filtering.
- Namespace‑based authorization filtering.

---

## 12. Open Questions and Future Enhancements

Areas for future refinement include:

- **Hierarchy change handling**
  - Exact strategy for reconciling `SubjectEdOrg` after EdOrg hierarchy changes
    (on‑change vs periodic job).

- **Ownership and other advanced strategies**
  - Determine whether ownership‑style and additional advanced strategies are
    better modelled as subjects/pathways or as `QueryFields`.
