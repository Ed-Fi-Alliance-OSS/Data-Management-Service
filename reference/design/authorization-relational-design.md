# Authorization: Relational, Read-Optimized Design

Audience: senior developers new to the current DMS authorization implementation. The prior JSON/trigger-based design is not in production; this proposal replaces it with a PostgreSQL-first, read-optimized model.

## What We Are Solving
- Fast row-level authorization fully in PostgreSQL (no OpenSearch dependency).
- Composable strategies (AND/OR across pathways: Direct EdOrg, Student, Staff, Contact, Namespace).
- Minimal write amplification; authorization facts live in small indexed tables.
- Fewest possible round-trips on reads; predictable plans using B-tree indexes.

### At a Glance (the shape of the solution)
- **Precomputed closure** of the EdOrg hierarchy to avoid recursive queries at request time.
- **Expanded client grants** (EdOrg, Namespace) stored once; loaded per request via array parameters or prepared CTEs (no extra round-trips).
- **Subject→EdOrg link tables** for Students, Staff, Contacts; populated from association documents with set-based refresh.
- **Document attribute tables** (Document→Namespace/EdOrg/Student/Staff/Contact) aligned to document partitions; cascade on delete.
- **Mandatory, partitioned materialized view** that explodes all document-to-EdOrg pathways, carries `ResourceName`/`CreatedAt`, and is indexed for ordered scans (avoids bitmap merges).
- **Strategy metadata** that describes which pathways are required for a resource/action.

Everything else—queries, checks, and examples—flow from these pieces.

## Example Dataset (used throughout)
- **EdOrg hierarchy**: SEA 1 → LEA 10 → School 100; SEA 1 → LEA 11 → School 110.
- **Clients**: Client A is authorized for EdOrg 10 (LEA) and 11 (LEA); namespaces `uri://lea` and `uri://vendor`.
- **Student S1**: StudentUniqueId = `stu-1`, enrolled in School 100.
- **Staff T1**: StaffUniqueId = `stf-1`, assigned to LEA 10 and School 110.
- **Contact C1**: ContactUniqueId = `ct-1`, is a contact for Student S1.
- **Documents**: `Document(Id=101, Partition=0, Resource=studentSchoolAssociations, StudentUniqueId=stu-1, SchoolId=100)`; `Document(Id=201, Resource=students, StudentUniqueId=stu-1)`; `Document(Id=301, Resource=staffEducationOrganizationAssignments, StaffUniqueId=stf-1, EdOrgId=110)`; `Document(Id=401, Resource=studentContacts, ContactUniqueId=ct-1, StudentUniqueId=stu-1)`.

## Core Concepts & Terms
- **Authorization Pathway**: The route from a client grant to a document (DirectEdOrg, StudentSchool, StudentResponsibility, StaffEdOrg, ContactStudentSchool, Namespace).
- **Strategy Segment**: An AND-group of pathway predicates required for a resource/action; segments are OR-ed together to express complex rules.
- **Authorization Facts**: Small tables holding the minimal indexed data needed to prove access (client grants, subject links, document attributes).
- **Closure**: The ancestor/descendant expansion of EdOrg relationships so filters never run recursive CTEs at request time.

## Data Model (with examples)

### 1) Education Organization Closure
Pre-expanded hierarchy for cheap descendant checks.

```sql
CREATE TABLE dms.EducationOrganizationClosure (
  AncestorEdOrgId bigint NOT NULL,
  DescendantEdOrgId bigint NOT NULL,
  Depth smallint NOT NULL, -- 0 = self
  PRIMARY KEY (AncestorEdOrgId, DescendantEdOrgId)
);
```

**Example rows**
| Ancestor | Descendant | Depth |
|----------|------------|-------|
| 1        | 1          | 0     |
| 1        | 10         | 1     |
| 1        | 11         | 1     |
| 1        | 100        | 2     |
| 1        | 110        | 2     |
| 10       | 10         | 0     |
| 10       | 100        | 1     |
| 11       | 11         | 0     |
| 11       | 110        | 1     |
| 100      | 100        | 0     |
| 110      | 110        | 0     |

### 2) Client Grants (expanded once, not per request)

```sql
CREATE TABLE dms.ClientAuthorizedEdOrg (
  ClientId uuid NOT NULL,
  EdOrgId bigint NOT NULL,
  PRIMARY KEY (ClientId, EdOrgId)
);

CREATE TABLE dms.ClientAuthorizedNamespace (
  ClientId uuid NOT NULL,
  NamespacePrefix text NOT NULL,
  PRIMARY KEY (ClientId, NamespacePrefix)
);
```

**Example rows (Client A)**
| ClientId | EdOrgId |
|----------|---------|
| A        | 10      |
| A        | 11      |

| ClientId | NamespacePrefix    |
|----------|--------------------|
| A        | uri://lea          |
| A        | uri://vendor       |

### 3) Subject→EdOrg Links
Populated from association documents; keyed by source so deletes are exact. These tables are hash-partitioned by `SourceDocumentPartitionKey` to align with `Document` partitions, and they FK back to the source `Document` with delete cascade.

```sql
CREATE TABLE dms.StudentEdOrgLink (
  StudentUniqueId varchar(32) NOT NULL,
  EdOrgId bigint NOT NULL,
  SourceResource text NOT NULL,
  SourceDocumentPartitionKey smallint NOT NULL,
  SourceDocumentId bigint NOT NULL,
  PRIMARY KEY (StudentUniqueId, EdOrgId, SourceResource, SourceDocumentPartitionKey, SourceDocumentId),
  FOREIGN KEY (SourceDocumentPartitionKey, SourceDocumentId)
    REFERENCES dms.Document(DocumentPartitionKey, Id) ON DELETE CASCADE
) PARTITION BY LIST(SourceDocumentPartitionKey);

CREATE TABLE dms.StaffEdOrgLink (
  StaffUniqueId varchar(32) NOT NULL,
  EdOrgId bigint NOT NULL,
  SourceResource text NOT NULL,
  SourceDocumentPartitionKey smallint NOT NULL,
  SourceDocumentId bigint NOT NULL,
  PRIMARY KEY (StaffUniqueId, EdOrgId, SourceResource, SourceDocumentPartitionKey, SourceDocumentId),
  FOREIGN KEY (SourceDocumentPartitionKey, SourceDocumentId)
    REFERENCES dms.Document(DocumentPartitionKey, Id) ON DELETE CASCADE
) PARTITION BY LIST(SourceDocumentPartitionKey);

CREATE TABLE dms.ContactStudentLink (
  ContactUniqueId varchar(32) NOT NULL,
  StudentUniqueId varchar(32) NOT NULL,
  SourceDocumentPartitionKey smallint NOT NULL,
  SourceDocumentId bigint NOT NULL,
  PRIMARY KEY (ContactUniqueId, StudentUniqueId, SourceDocumentPartitionKey, SourceDocumentId),
  FOREIGN KEY (SourceDocumentPartitionKey, SourceDocumentId)
    REFERENCES dms.Document(DocumentPartitionKey, Id) ON DELETE CASCADE
) PARTITION BY LIST(SourceDocumentPartitionKey);
```

#### Example rows

**StudentEdOrgLink** (from Document 101: StudentSchoolAssociation stu-1, school 100):

| Student | EdOrgId | SourceResource            | SrcDocPk | SrcDocId |
|---------|---------|---------------------------|----------|-------|
| stu-1   | 100     | studentSchoolAssociations | 0        | 101   |
| stu-1   | 10      | studentSchoolAssociations | 0        | 101   |
| stu-1   | 1       | studentSchoolAssociations | 0        | 101   |


**StaffEdOrgLink** (from Document 301: StaffAssignment stf-1 to EdOrg 110):

| Staff | EdOrgId | SourceResource                        | SrcDocPk | SrcDocId |
|-------|---------|---------------------------------------|----------|-------|
| stf-1 | 110     | staffEducationOrganizationAssignments | 0        | 301   |
| stf-1 | 11      | staffEducationOrganizationAssignments | 0        | 301   |
| stf-1 | 1       | staffEducationOrganizationAssignments | 0        | 301   |


**ContactStudentLink** (from Document 401: Contact ct-1 for stu-1):

| Contact | Student | SrcDocPk | SrcDocId |
|---------|---------|----------|----------|
| ct-1    | stu-1   | 0        | 401      |


### 4) Document Attribute Tables
Populate on POST/PUT of any securable document; cascade on delete with Document.

```sql
CREATE TABLE dms.DocumentNamespace (
  DocumentPartitionKey smallint NOT NULL,
  DocumentId bigint NOT NULL,
  Namespace text NOT NULL,
  PRIMARY KEY (DocumentPartitionKey, DocumentId, Namespace),
  FOREIGN KEY (DocumentPartitionKey, DocumentId) REFERENCES dms.Document(DocumentPartitionKey, Id) ON DELETE CASCADE
);

CREATE TABLE dms.DocumentEducationOrganization (
  DocumentPartitionKey smallint NOT NULL,
  DocumentId bigint NOT NULL,
  Role text NOT NULL,
  EdOrgId bigint NOT NULL,
  PRIMARY KEY (DocumentPartitionKey, DocumentId, Role, EdOrgId),
  FOREIGN KEY (DocumentPartitionKey, DocumentId) REFERENCES dms.Document(DocumentPartitionKey, Id) ON DELETE CASCADE
);

CREATE TABLE dms.DocumentStudent ( ... );
CREATE TABLE dms.DocumentStaff ( ... );
CREATE TABLE dms.DocumentContact ( ... );
```

**Example rows**
- For Document 101 (studentSchoolAssociations): `DocumentEducationOrganization`: (0,101,'School',100); `DocumentStudent`: (0,101,'stu-1').
- For Document 201 (students): `DocumentStudent`: (0,201,'stu-1'); optionally namespaces and edorg roles depending on resource shape.
- For Document 301 (staffAssignment): `DocumentEducationOrganization`: (0,301,'AssignmentEdOrg',110); `DocumentStaff`: (0,301,'stf-1').
- For Document 401 (studentContacts): `DocumentContact`: (0,401,'ct-1'); `DocumentStudent`: (0,401,'stu-1').

### 5) Document-to-EdOrg Authorization Materialized View (required)
Union of all pathways, persisted and indexed for ordered scans. Includes `ResourceName` and `CreatedAt` to satisfy collection paging without combining indexes.

```sql
CREATE MATERIALIZED VIEW dms.DocumentEdOrgAuthorization
PARTITION BY HASH(DocumentPartitionKey) AS
SELECT d.DocumentPartitionKey,
       d.Id AS DocumentId,
       d.ResourceName,
       d.CreatedAt,
       'DirectEdOrg' AS Pathway,
       c.DescendantEdOrgId AS EdOrgId
FROM dms.DocumentEducationOrganization de
JOIN dms.Document d ON d.DocumentPartitionKey = de.DocumentPartitionKey AND d.Id = de.DocumentId
JOIN dms.EducationOrganizationClosure c ON c.AncestorEdOrgId = de.EdOrgId
UNION ALL
SELECT d.DocumentPartitionKey,
       d.Id AS DocumentId,
       d.ResourceName,
       d.CreatedAt,
       'StudentSchool' AS Pathway,
       c.DescendantEdOrgId AS EdOrgId
FROM dms.DocumentStudent ds
JOIN dms.Document d ON d.DocumentPartitionKey = ds.DocumentPartitionKey AND d.Id = ds.DocumentId
JOIN dms.StudentEdOrgLink se ON se.StudentUniqueId = ds.StudentUniqueId
JOIN dms.EducationOrganizationClosure c ON c.AncestorEdOrgId = se.EdOrgId
UNION ALL
SELECT d.DocumentPartitionKey,
       d.Id AS DocumentId,
       d.ResourceName,
       d.CreatedAt,
       'StaffEdOrg' AS Pathway,
       c.DescendantEdOrgId AS EdOrgId
FROM dms.DocumentStaff dst
JOIN dms.Document d ON d.DocumentPartitionKey = dst.DocumentPartitionKey AND d.Id = dst.DocumentId
JOIN dms.StaffEdOrgLink st ON st.StaffUniqueId = dst.StaffUniqueId
JOIN dms.EducationOrganizationClosure c ON c.AncestorEdOrgId = st.EdOrgId
UNION ALL
SELECT d.DocumentPartitionKey,
       d.Id AS DocumentId,
       d.ResourceName,
       d.CreatedAt,
       'ContactStudentSchool' AS Pathway,
       c.DescendantEdOrgId AS EdOrgId
FROM dms.DocumentContact dc
JOIN dms.Document d ON d.DocumentPartitionKey = dc.DocumentPartitionKey AND d.Id = dc.DocumentId
JOIN dms.ContactStudentLink cs ON cs.ContactUniqueId = dc.ContactUniqueId
JOIN dms.StudentEdOrgLink se ON se.StudentUniqueId = cs.StudentUniqueId
JOIN dms.EducationOrganizationClosure c ON c.AncestorEdOrgId = se.EdOrgId
WITH NO DATA;

-- Refresh hooks tied to Document*/Link inserts/updates keep it current.
-- Covering index preserves ORDER BY CreatedAt without bitmap merges:
CREATE INDEX CONCURRENTLY IF NOT EXISTS IX_DocumentEdOrgAuth_Pathway_EdOrg_Resource_CreatedAt
  ON dms.DocumentEdOrgAuthorization (Pathway, EdOrgId, ResourceName, CreatedAt, DocumentPartitionKey, DocumentId);
ALTER MATERIALIZED VIEW dms.DocumentEdOrgAuthorization CLUSTER ON IX_DocumentEdOrgAuth_Pathway_EdOrg_Resource_CreatedAt;
```

**Example rows produced**
- For Document 101 (studentSchoolAssociation): Pathway `StudentSchool`, EdOrgId 1/10/100.
- For Document 201 (student): Pathway `StudentSchool`, EdOrgId 1/10/100 (via StudentEdOrgLink of stu-1).
- For Document 301 (staff assignment): Pathway `StaffEdOrg`, EdOrgId 1/11/110.
- For Document 401 (student contact): Pathway `ContactStudentSchool`, EdOrgId 1/10/100.

### 6) Strategy Metadata (how resources map to pathways)

```sql
CREATE TABLE dms.AuthorizationStrategy (
  StrategyName text PRIMARY KEY
);

CREATE TABLE dms.AuthorizationStrategySegment (
  StrategyName text NOT NULL REFERENCES dms.AuthorizationStrategy(StrategyName) ON DELETE CASCADE,
  SegmentId smallint NOT NULL, -- OR across segments
  Pathway text NOT NULL,       -- DirectEdOrg, StudentSchool, StaffEdOrg, ContactStudentSchool, Namespace
  IsNamespace boolean NOT NULL DEFAULT false,
  PRIMARY KEY (StrategyName, SegmentId, Pathway)
);
```

**Example strategies**
- `Students_Read`: one segment requiring `StudentSchool`. (Segment 1: Pathway=StudentSchool)
- `StudentContacts_Read`: one segment requiring `ContactStudentSchool`. (Segment 1: Pathway=ContactStudentSchool)
- `Descriptors_Write`: one segment requiring `Namespace`. (Segment 1: Pathway=Namespace, IsNamespace=true)
- `Assessments_Read`: two segments OR-ed: (1) DirectEdOrg; (2) Namespace AND DirectEdOrg (if resource carries both).

### Mixed AND/OR Strategy Examples
- **Example A: `(Namespace AND DirectEdOrg) OR StudentSchool`**  
  - Segment 1 (AND group): Pathway=Namespace (IsNamespace=true) AND Pathway=DirectEdOrg.  
  - Segment 2 (AND group): Pathway=StudentSchool.  
  - Interpretation: Either the client matches the document’s namespace and EdOrg directly, or they have access through the student’s school lineage.

- **Example B: `(StaffEdOrg AND Namespace) OR (StudentSchool AND ContactStudentSchool)`**  
  - Segment 1: Pathway=StaffEdOrg AND Namespace.  
  - Segment 2: Pathway=StudentSchool AND ContactStudentSchool.  
  - Interpretation: Either the staff member’s EdOrg plus namespace qualifies, or the combination of student-based and contact-based EdOrg paths qualifies.

**SQL pattern for Example A** (merge into WHERE with OR across segments):
```sql
-- Segment 1: Namespace AND DirectEdOrg
EXISTS (
  SELECT 1
  FROM dms.DocumentNamespace dn
  JOIN AuthorizedNamespaces an ON an.NamespacePrefix = dn.Namespace
  WHERE dn.DocumentPartitionKey = d.DocumentPartitionKey
    AND dn.DocumentId = d.Id
)
AND EXISTS (
  SELECT 1
  FROM dms.DocumentEdOrgAuthorization dea
  JOIN AuthorizedEdOrgs ae ON ae.EdOrgId = dea.EdOrgId
  WHERE dea.DocumentPartitionKey = d.DocumentPartitionKey
    AND dea.DocumentId = d.Id
    AND dea.Pathway = 'DirectEdOrg'
)

-- Segment 2: StudentSchool
OR EXISTS (
  SELECT 1
  FROM dms.DocumentEdOrgAuthorization dea
  JOIN AuthorizedEdOrgs ae ON ae.EdOrgId = dea.EdOrgId
  WHERE dea.DocumentPartitionKey = d.DocumentPartitionKey
    AND dea.DocumentId = d.Id
    AND dea.Pathway = 'StudentSchool'
)
```

**SQL pattern for Example B**:
```sql
-- Segment 1: StaffEdOrg AND Namespace
EXISTS (
  SELECT 1
  FROM dms.DocumentNamespace dn
  JOIN AuthorizedNamespaces an ON an.NamespacePrefix = dn.Namespace
  WHERE dn.DocumentPartitionKey = d.DocumentPartitionKey
    AND dn.DocumentId = d.Id
)
AND EXISTS (
  SELECT 1
  FROM dms.DocumentEdOrgAuthorization dea
  JOIN AuthorizedEdOrgs ae ON ae.EdOrgId = dea.EdOrgId
  WHERE dea.DocumentPartitionKey = d.DocumentPartitionKey
    AND dea.DocumentId = d.Id
    AND dea.Pathway = 'StaffEdOrg'
)

-- Segment 2: StudentSchool AND ContactStudentSchool
OR (
  EXISTS (
    SELECT 1
    FROM dms.DocumentEdOrgAuthorization dea
    JOIN AuthorizedEdOrgs ae ON ae.EdOrgId = dea.EdOrgId
    WHERE dea.DocumentPartitionKey = d.DocumentPartitionKey
      AND dea.DocumentId = d.Id
      AND dea.Pathway = 'StudentSchool'
  )
  AND EXISTS (
    SELECT 1
    FROM dms.DocumentEdOrgAuthorization dea
    JOIN AuthorizedEdOrgs ae ON ae.EdOrgId = dea.EdOrgId
    WHERE dea.DocumentPartitionKey = d.DocumentPartitionKey
      AND dea.DocumentId = d.Id
      AND dea.Pathway = 'ContactStudentSchool'
  )
)
```

**Example outcomes using the sample data**
- For a student document (Id 201) with namespace `uri://lea` and EdOrg path 1/10/100:
  - Example A authorizes via Segment 1 if Client A has namespace `uri://lea` and EdOrg 10; Segment 2 also authorizes through StudentSchool, so access is granted.
- For a contact document (Id 401) without a namespace but with StudentSchool path 1/10/100:
  - Example A authorizes via Segment 2 (StudentSchool).
  - Example B authorizes via Segment 2 only (needs both StudentSchool and ContactStudentSchool, which 401 satisfies); Segment 1 would fail if the doc has no namespace or staff linkage.

## Request-Time Flow (step-by-step with examples)

### Build authorized sets (per request, single statement, no extra round-trip)
Use array parameters (or prepared, reusable temp tables per pooled connection) to avoid creating/dropping temp tables every call.

```sql
WITH AuthorizedEdOrgs AS (
  SELECT unnest(:authorizedEdOrgIds::bigint[]) AS EdOrgId
),
AuthorizedNamespaces AS (
  SELECT unnest(:authorizedNamespacePrefixes::text[]) AS NamespacePrefix
)
-- subsequent predicates reference these CTEs; no standalone DDL/DML round-trip
```

### GET collection example: Client A reading Students
Strategy: `StudentSchool`. Single SQL; planner can index-scan `IX_DocumentEdOrgAuth_Pathway_EdOrg_Resource_CreatedAt` to satisfy both auth and ordering.

```sql
WITH AuthorizedEdOrgs AS (
  SELECT unnest(:authorizedEdOrgIds::bigint[]) AS EdOrgId
)
SELECT d.*
FROM dms.Document d
WHERE d.ResourceName = 'students'
  AND EXISTS (
    SELECT 1
    FROM dms.DocumentEdOrgAuthorization dea
    JOIN AuthorizedEdOrgs ae ON ae.EdOrgId = dea.EdOrgId
    WHERE dea.DocumentPartitionKey = d.DocumentPartitionKey
      AND dea.DocumentId = d.Id
      AND dea.Pathway = 'StudentSchool'
  )
ORDER BY d.CreatedAt, d.Id
LIMIT :limit OFFSET :offset;
```

**Result using the example data**
- Document 201 (student stu-1) is returned because it has EdOrgIds 1/10/100 and Client A has 10.

### GET by id / DELETE example: Contact document
Strategy: `ContactStudentSchool` (also hits the covering index).

```sql
SELECT 1
FROM dms.DocumentEdOrgAuthorization dea
JOIN (SELECT unnest(:authorizedEdOrgIds::bigint[]) AS EdOrgId) ae ON ae.EdOrgId = dea.EdOrgId
WHERE dea.DocumentPartitionKey = :pk
  AND dea.DocumentId = :id
  AND dea.Pathway = 'ContactStudentSchool'
LIMIT 1;
```

Document 401 passes because it expands to EdOrgIds 1/10/100 and Client A has 10.

### Namespace example: Descriptor write
Strategy: `Namespace`.

```sql
SELECT 1
FROM dms.DocumentNamespace dn
JOIN AuthorizedNamespaces an ON an.NamespacePrefix = dn.Namespace
WHERE dn.DocumentPartitionKey = :pk
  AND dn.DocumentId = :id
LIMIT 1;
```

## Write Path (how tables get populated, with examples)

### Association document insert (StudentSchoolAssociation -> StudentEdOrgLink)
1) Insert Document 101.
2) Compute descendant EdOrgs once:
```sql
INSERT INTO dms.StudentEdOrgLink (StudentUniqueId, EdOrgId, SourceResource, SourceDocumentPartitionKey, SourceDocumentId)
SELECT 'stu-1', c.DescendantEdOrgId, 'studentSchoolAssociations', 0, 101
FROM dms.EducationOrganizationClosure c
WHERE c.AncestorEdOrgId = 100;
```
Rows for EdOrg 100, 10, 1 are inserted.

### Securable document insert (Students -> DocumentStudent)
1) Insert Document 201.
2) Extract security attributes:
```sql
INSERT INTO dms.DocumentStudent VALUES (0, 201, 'stu-1');
```
No further denormalization needed; authorization view joins to StudentEdOrgLink at read time.

### Staff association insert (StaffEdOrgLink)
For Document 301 (staff assignment to EdOrg 110):
```sql
INSERT INTO dms.StaffEdOrgLink (StaffUniqueId, EdOrgId, SourceResource, SourceDocumentPartitionKey, SourceDocumentId)
SELECT 'stf-1', c.DescendantEdOrgId, 'staffEducationOrganizationAssignments', 0, 301
FROM dms.EducationOrganizationClosure c
WHERE c.AncestorEdOrgId = 110;
```

### Contact association insert (ContactStudentLink)
For Document 401 (contact ct-1 for stu-1):
```sql
INSERT INTO dms.ContactStudentLink VALUES ('ct-1', 'stu-1', 0, 401);
```
No EdOrg expansion here; expansion happens via StudentEdOrgLink at read time.

## Read-Path Optimization (fast reads, fewer round-trips)
- **Single SQL per API call**: Authorization check is fused with the data fetch via `EXISTS` and array-unnest CTEs; no extra temp-table DDL per request.
- **Mandatory materialization**: `DocumentEdOrgAuthorization` is always materialized (per partition) and refreshed incrementally from Document*/Link changes; the non-materialized UNION is not on the hot path.
- **Order-preserving covering index**: `IX_DocumentEdOrgAuth_Pathway_EdOrg_Resource_CreatedAt` (clustered) lets the planner satisfy auth + `ORDER BY CreatedAt` without bitmap merges or separate sorts during deep paging.
- **Optional doc-level cache**: If profiling shows the materialized view is still hot, add `DocumentAuthorizationCache(DocumentPartitionKey, DocumentId, Pathway, EdOrgId, ResourceName, CreatedAt)` maintained with the same triggers/refresh logic.
- **Prepared statements**: Parameterize the auth predicates and reuse them across requests to keep plans stable and predictable.

## Compatibility with the DMS 3-Table Pipeline (Document/Alias/Reference)
- **Document remains the canonical store**; Alias and Reference flows stay unchanged. New authorization tables (`Document*`, `*Link`, `ClientAuthorized*`) FK to `Document` and cascade on delete.
- **Ingestion updates**: On insert/upsert into `Document`, the write pipeline extracts security attributes (namespace, edorg ids, student/staff/contact identifiers) from the incoming JSON and populates `Document*` rows; association documents populate `*Link` tables with descendant expansion via `EducationOrganizationClosure`.
- **Materialized view maintenance**: Triggers (or a lightweight job) on `Document*` and `*Link` changes refresh the corresponding `DocumentEdOrgAuthorization` partition incrementally to keep reads single-index.
- **Migration plan**: Retire JSON auth columns (`StudentSchoolAuthorizationEdOrgIds`, `ContactStudentSchoolAuthorizationEdOrgIds`, `StaffEducationOrganizationAuthorizationEdOrgIds`, `StudentEdOrgResponsibilityAuthorizationIds`) and their GIN indexes after backfilling the new tables and materialized view. This removes competing indexes that would otherwise force bitmap scans.

## Performance Characteristics & Tuning
- All predicates are B-tree friendly; no JSON operators are used.
- Closure avoids recursive CTEs on the read path; descendant expansion happens once in writes.
- Write amplification is limited to small link tables; document rows stay narrow.
- For clients with very large EdOrg sets, array-unnest CTEs (or per-connection prepared temp tables) keep joins hash/merge friendly without bitmap merges.
- Partition alignment on `DocumentPartitionKey` keeps authorization tables co-located with the document partitions for cheaper nested loops.

## What Changed vs. the Old (non-production) Design
- Removed per-pathway JSON columns on `Document` and heavy trigger chains in favor of narrow relational tables.
- Replaced search-engine-shaped denormalization with relational link tables and a mandatory, order-preserving materialized view.
- Moved descendant expansion to closure tables and set-based link maintenance.
- Added strategy metadata to avoid hardcoded pathway combinations and aligned the read path to a single covering index scan.

## Recap: How to Think About This Model
1. **Facts are normalized and indexed**: Client grants, subject→EdOrg links, document attributes.
2. **Hierarchy is pre-expanded**: Closure gives cheap descendant checks.
3. **Authorization = EXISTS**: Join authorized sets to the materialized, order-preserving view in one SQL round-trip.
4. **Write once, read many**: Association writes expand and store only the minimal rows; reads stay fast without JSON scans or bitmap merges.
