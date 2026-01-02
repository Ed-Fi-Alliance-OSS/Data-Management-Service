# Authorization Design for Relational Primary Store (Tables per Resource)

## Status

Draft.

This document is the authorization deep dive for `overview.md`:

- Overview: [overview.md](overview.md)
- Data model: [data-model.md](data-model.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Caching & operations: [caching-and-ops.md](caching-and-ops.md)
- Extensions: [extensions.md](extensions.md)

This document proposes an authorization storage/query design that fits the relational primary store
and can support an ODS-style view-based authorization approach (similar to `auth.*` views in Ed-Fi ODS).

## Table of Contents

- [1. Goals and Constraints](#1-goals-and-constraints)
- [2. Inputs From ApiSchema and the Backend Redesign](#2-inputs-from-apischema-and-the-backend-redesign)
- [3. Proposed Core Tables (Recommended Baseline)](#3-proposed-core-tables-recommended-baseline)
- [4. ODS-Style View-Based Authorization (Design Ideas)](#4-ods-style-view-based-authorization-design-ideas)
- [5. Strategy Semantics (Mapping to DMS Concepts)](#5-strategy-semantics-mapping-to-dms-concepts)
- [6. Write Path Integration (Maintaining `DocumentSubject` and EdOrg Hierarchy)](#6-write-path-integration-maintaining-documentsubject-and-edorg-hierarchy)
- [7. Read Path Integration](#7-read-path-integration)
- [8. Alternatives and Tradeoffs (Design Ideas)](#8-alternatives-and-tradeoffs-design-ideas)
- [9. Open Questions](#9-open-questions)

---

### Origins (Adapted vs New)

This design builds on the prior Subject/EdOrg authorization redesign (`reference/design/auth/auth-redesign-subject-edorg-model.md`) but changes the storage/query model to fit the relational primary store.

**Directly adapted**

- Core principles: remove JSONB authorization arrays and avoid authorization triggers; keep enforcement in application code.
- Generic “document → subject” modeling via `dms.DocumentSubject` and stable `SubjectType`/`AuthorizationPathway` lookup tables (to prevent enum drift).
- ODS-consistent strategy semantics (relationship strategies, namespace-based authorization, AND/OR composition).

**New or changed for the relational primary store**

- Subjects are keyed by `DocumentId` (`SubjectDocumentId`) rather than natural-key strings (`StudentUniqueId`, etc.) so identity updates don’t require rekeying authorization rows.
- ODS-style **view-based authorization** is a first-class option: `auth.*` views (backed by an EdOrg closure/tuple table) can be joined to `dms.DocumentSubject` for query-time authorization.
- Adds a dedicated `dms.EducationOrganization(EducationOrganizationId → DocumentId)` mapping to bridge token claim ids and relational `..._DocumentId` FKs.
- The `SubjectEdOrg` “materialized membership” approach is treated as an alternative (not the baseline) because view-based authorization can compute membership from canonical relationship tables.

---

## 1. Goals and Constraints

### Goals

1. **Preserve existing DMS/ODS authorization semantics**
   - Relationship strategies: Students, Staff, Contacts, EdOrgs.
   - Namespace-based authorization.
   - Strategy composition (AND/OR) consistent with ODS behavior.
2. **Relational-first, JSON-independent enforcement**
   - No JSONB authorization arrays on `dms.Document`.
   - No authorization triggers on resource data tables.
   - Authorization does not depend on optional `dms.DocumentCache`.
3. **Use stable surrogate keys**
   - Prefer `DocumentId` for “subject” identities (Student/Staff/Contact/EdOrg) to avoid cascades on natural-key changes.
4. **Efficient authorization-aware query paging**
   - Authorization filtering occurs before paging so paging is over authorized rows.
5. **Cross-engine parity**
   - Works on PostgreSQL and SQL Server, including “large claim set” handling.
6. **Support “view-based” authorization**
   - Ability to express authorization membership using database views (and optionally a tuple/closure table) similar to ODS’ `auth.*`.

### Non-goals (for this draft)

- Designing new claim set/strategy metadata; this assumes the existing DMS strategy pipeline and `ApiSchema`-provided securable metadata.
- Full support for custom/tenant-defined authorization strategies beyond the existing DMS set.

---

## 2. Inputs From ApiSchema and the Backend Redesign

DMS already has schema-derived authorization metadata:

- `resourceSchema.securableElements.*` (Student, Staff, Contact, EducationOrganization, Namespace)
- `resourceSchema.authorizationPathways` (e.g., StudentSchoolAssociation, StudentEducationOrganizationResponsibilityAssociation, etc.)

From the relational primary store redesign we additionally have:

- Stable `dms.Document(DocumentId, DocumentUuid, ProjectName, ResourceName, ...)`.
- Referential resolution via `dms.ReferentialIdentity(ReferentialId -> DocumentId)` including polymorphic superclass alias rows (e.g., `School` as `EducationOrganization`).
- Resource data stored in per-resource tables with FK columns storing `..._DocumentId` for references.
- Optional `dms.DocumentCache` which must not be used as an authorization source of truth.

---

## 3. Proposed Core Tables (Recommended Baseline)

### 3.1 `dms.DocumentSubject` (document → subject documents)

Normalize per-document “aboutness” into a narrow table that the query engine can join against regardless of where securable elements occur in JSON (root, nested collections, etc.).

Recommended conceptual schema:

```sql
CREATE TABLE dms.DocumentSubject (
    DocumentId        bigint   NOT NULL
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,

    SubjectType       smallint NOT NULL, -- Student=1, Contact=2, Staff=3, EducationOrganization=4

    SubjectDocumentId bigint   NOT NULL
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,

    PRIMARY KEY (DocumentId, SubjectType, SubjectDocumentId)
);

CREATE INDEX IX_DocumentSubject_Subject
    ON dms.DocumentSubject (SubjectType, SubjectDocumentId, DocumentId);
```

Notes:

- **Key choice**: `SubjectDocumentId` (not `StudentUniqueId`/`StaffUniqueId`) avoids churn when natural keys change.
- `SubjectType` is stable and backed by a lookup table (see 3.3).
- For “self” resources (e.g., Student), insert `(DocumentId, SubjectType=Student, SubjectDocumentId=DocumentId)` so Student reads are governed by the same relationship membership as other student-securable resources.
- This table is **maintained transactionally** by the write path (see section 6).

### 3.2 Subject/pathway lookups (stable IDs)

As in `reference/design/auth/auth-redesign-subject-edorg-model.md`, use lookup tables to prevent enum drift:

```sql
CREATE TABLE dms.SubjectType (
    SubjectTypeId smallint PRIMARY KEY,
    Code          varchar(64) NOT NULL UNIQUE
);

CREATE TABLE dms.AuthorizationPathway (
    PathwayId     smallint PRIMARY KEY,
    Code          varchar(128) NOT NULL UNIQUE
);
```

`DocumentSubject.SubjectType` should FK to `dms.SubjectType`.

### 3.3 EducationOrganization hierarchy storage

Relationship strategies require EdOrg hierarchy expansion (e.g., LEA claims authorize School-scoped data).

Two viable options:

**Option A (simple, no precomputed closure)**: adjacency + recursive query

- Maintain:
  - `dms.EducationOrganization` mapping EdOrg ids → EdOrg documents
  - `dms.EducationOrganizationRelationship` (child → parent) adjacency
- Use recursive CTEs/functions to compute ancestor/descendant sets as needed.

**Option B (ODS-like, query-friendly)**: precomputed EdOrg closure (“tuple table”)

- Maintain a closure table similar to ODS’ `auth.EducationOrganizationIdToEducationOrganizationId`:

```sql
CREATE TABLE auth.EducationOrganizationIdToEducationOrganizationId (
    SourceEducationOrganizationId bigint NOT NULL,
    TargetEducationOrganizationId bigint NOT NULL,
    CONSTRAINT PK_EdOrgToEdOrg PRIMARY KEY (SourceEducationOrganizationId, TargetEducationOrganizationId)
);

CREATE INDEX IX_EdOrgToEdOrg_Target
    ON auth.EducationOrganizationIdToEducationOrganizationId (TargetEducationOrganizationId)
    INCLUDE (SourceEducationOrganizationId);
```

- Semantics: `SourceEducationOrganizationId` can “reach” `TargetEducationOrganizationId` (descendant closure; include `(X, X)`).
- Keep it current via application code (preferred) or database triggers (ODS pattern, but not recommended for DMS).

This document assumes **Option B** when describing view-based authorization (section 4), but either option can support the same strategy semantics.

#### `dms.EducationOrganization` (id → DocumentId mapping)

Because the relational primary store persists EdOrg references as `..._DocumentId` FKs, authorization frequently needs a fast mapping between:

- token claims and API values (`EducationOrganizationId`), and
- internal FK values (`DocumentId` for concrete EdOrg documents).

Recommended conceptual schema:

```sql
CREATE TABLE dms.EducationOrganization (
    EducationOrganizationId bigint NOT NULL PRIMARY KEY,
    DocumentId              bigint NOT NULL
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT UX_EducationOrganization_DocumentId UNIQUE (DocumentId)
);
```

Population approach (metadata-driven):

- When writing any document that is a subclass of `EducationOrganization` (e.g., `School`), Core already derives a `SuperclassIdentity` with the canonical identity json path `educationOrganizationId` and its value.
- Use that identity value to maintain `dms.EducationOrganization(EducationOrganizationId, DocumentId)` alongside `dms.ReferentialIdentity` maintenance for the superclass alias.

#### `dms.EducationOrganizationRelationship` (adjacency; child → parent)

If using adjacency (Option A), maintain:

```sql
CREATE TABLE dms.EducationOrganizationRelationship (
    EducationOrganizationId       bigint NOT NULL,
    ParentEducationOrganizationId bigint NOT NULL,
    PRIMARY KEY (EducationOrganizationId, ParentEducationOrganizationId),
    FOREIGN KEY (EducationOrganizationId)
        REFERENCES dms.EducationOrganization (EducationOrganizationId) ON DELETE CASCADE,
    FOREIGN KEY (ParentEducationOrganizationId)
        REFERENCES dms.EducationOrganization (EducationOrganizationId) ON DELETE CASCADE
);

CREATE INDEX IX_EdOrgRelationship_Parent
    ON dms.EducationOrganizationRelationship (ParentEducationOrganizationId);
```

---

## 4. ODS-Style View-Based Authorization (Design Ideas)

ODS’ relationship authorization relies on `auth.*` views built atop an EdOrg closure table.
DMS can support the same model, but using `DocumentId` keys for subjects and relationships.

### 4.1 Core views to support the existing DMS relationship strategies

Create views in schema `auth` that expose a uniform “EdOrg claim → subject document” mapping:

- `auth.EducationOrganizationIdToStudentDocumentId`
- `auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility`
- `auth.EducationOrganizationIdToStaffDocumentId`
- `auth.EducationOrganizationIdToContactDocumentId`
- (optional) `auth.EducationOrganizationIdToEducationOrganizationDocumentId` (for EdOrg-direct authorization on FK `..._DocumentId`)

These views can be defined *either*:

1. **Directly from the canonical relationship tables** (most ODS-like; no extra “subject membership” tables), or
2. **On top of a materialized subject membership table** (see Alternative A in section 8).

Example (conceptual, DMS names will differ):

```sql
CREATE VIEW auth.EducationOrganizationIdToStudentDocumentId AS
SELECT edorgs.SourceEducationOrganizationId, ssa.Student_DocumentId AS StudentDocumentId
FROM auth.EducationOrganizationIdToEducationOrganizationId edorgs
JOIN dms.EducationOrganization eo
  ON eo.EducationOrganizationId = edorgs.TargetEducationOrganizationId
JOIN edfi.StudentSchoolAssociation ssa
  ON ssa.School_DocumentId = eo.DocumentId
GROUP BY edorgs.SourceEducationOrganizationId, ssa.Student_DocumentId;
```

Key idea:

- Views provide a stable, queryable interface for authorization joins.
- The query engine can join `dms.DocumentSubject` to these views without per-resource hand-written SQL.

### 4.2 Query-time authorization using views (pattern)

For a page query over a resource root table `R`:

- Apply normal query predicates over `R` (from `ApiSchema.queryFieldMapping`).
- Add one predicate per required “segment” (Student, Staff, Contact, EdOrg), ANDed within a strategy.
- Compose strategies using the existing `AuthorizationStrategyEvaluator.Operator` semantics.

Segment predicate shape (Student segment example):

```sql
AND EXISTS (
    SELECT 1
    FROM dms.DocumentSubject ds
    JOIN auth.EducationOrganizationIdToStudentDocumentId av
      ON av.StudentDocumentId = ds.SubjectDocumentId
    WHERE ds.DocumentId = R.DocumentId
      AND ds.SubjectType = 1 -- Student
      AND av.SourceEducationOrganizationId = ANY (@ClaimEdOrgIds)
)
```

This supports:

- Fast paging: authorization filtering is part of the same query that selects page keys.
- Consistency with single-item authorization: GET-by-id can use the same `EXISTS` pattern (see section 7).

### 4.3 Large claim sets (SQL Server)

ODS switches from `IN (...)` to TVPs at ~2,000 EdOrg ids.
DMS should do the same on SQL Server:

- If `ClaimEdOrgIds.Length < 2000`: use `IN ( ... )` with literal/parameter list.
- Else: pass EdOrg ids as a table-valued parameter and join.

PostgreSQL can use `= ANY(@bigint[])`.

---

## 5. Strategy Semantics (Mapping to DMS Concepts)

The existing DMS middleware produces:

- `AuthorizationStrategyEvaluator[]` (strategy name, filters, AND/OR operator)
- `AuthorizationSecurableInfo[]` (which securable dimensions apply to the resource)

Authorization filtering logic must preserve ODS semantics:

- **Within a strategy**: required securable dimensions are ANDed (e.g., Student AND EdOrg for `RelationshipsWithEdOrgsAndPeople` where both are securable).
- **Across strategies**: evaluators’ operators drive AND/OR composition.

Pathway selection is strategy-specific:

- `RelationshipsWithStudentsOnly` → Student via School (and optionally Responsibility, depending on existing DMS semantics).
- `RelationshipsWithStudentsOnlyThroughResponsibility` → Student via Responsibility only.
- `RelationshipsWithEdOrgsOnly` → EdOrg direct.
- `RelationshipsWithEdOrgsAndPeople` → EdOrg direct AND any person dimensions securable on the resource.

In view-based mode, “pathway selection” is implemented by choosing the appropriate view(s).

---

## 6. Write Path Integration (Maintaining `DocumentSubject` and EdOrg Hierarchy)

### 6.1 Maintaining `dms.DocumentSubject`

Maintain `dms.DocumentSubject` transactionally on POST/PUT/DELETE:

1. After reference resolution (natural key → `..._DocumentId` FK values are known), extract subject document ids for each securable dimension:
   - Student: referenced Student `DocumentId`, or self for Student resource.
   - Staff: referenced Staff `DocumentId`, or self for Staff resource.
   - Contact: referenced Contact `DocumentId`, or self for Contact resource.
   - EdOrg: referenced EducationOrganization `DocumentId` (polymorphic via FK to `dms.Document`).
2. Replace the document’s subject rows:
   - `DELETE FROM dms.DocumentSubject WHERE DocumentId = @docId`
   - bulk insert current rows.

This design keeps authorization independent of JSON and independent of optional `dms.DocumentCache`.

### 6.2 Maintaining EdOrg hierarchy

The system needs a current hierarchy to support:

- view-based authorization (closure table), and/or
- instance-level authorization checks (`IAuthorizationRepository.GetAncestorEducationOrganizationIds`).

Recommended approach:

1. Maintain a normalized EdOrg node table `dms.EducationOrganization` keyed by `EducationOrganizationId` with `DocumentId` back-pointer.
2. Maintain adjacency edges `dms.EducationOrganizationRelationship` based on EdOrg parent references (e.g., School → LEA).
3. Populate/refresh the closure table (`auth.EducationOrganizationIdToEducationOrganizationId`) from adjacency:
   - incrementally for small changes, and/or
   - via a periodic/full rebuild job after large ingests.

Open decision: whether the list of parent-reference sites is hard-coded (Ed-Fi standard) or derived from metadata.

### 6.3 Implementing `IAuthorizationRepository` (relational primary store)

DMS Core calls `IAuthorizationRepository` using **natural keys** (e.g., `StudentUniqueId`), but the relational primary store is keyed by `DocumentId`.

Recommended pattern:

1. **Resolve natural key → subject `DocumentId`** using `dms.ReferentialIdentity`:
   - compute the appropriate `ReferentialId` (same UUIDv5 algorithm as Core),
   - look up `DocumentId` in `dms.ReferentialIdentity`.
2. **Fetch reachable `EducationOrganizationId`s** using either:
   - the `auth.*` views (reverse lookup by subject doc id), and/or
   - joins over relationship tables + the EdOrg closure table.

Examples (conceptual):

- `GetEducationOrganizationsForStudent(studentUniqueId)`:
  - resolve student → `StudentDocumentId`
  - query:

    ```sql
    SELECT DISTINCT edorgs.SourceEducationOrganizationId
    FROM edfi.StudentSchoolAssociation ssa
    JOIN dms.EducationOrganization school
      ON school.DocumentId = ssa.School_DocumentId
    JOIN auth.EducationOrganizationIdToEducationOrganizationId edorgs
      ON edorgs.TargetEducationOrganizationId = school.EducationOrganizationId
    WHERE ssa.Student_DocumentId = @StudentDocumentId;
    ```

- `GetAncestorEducationOrganizationIds([edOrgIds...])`:
  - if using the closure table, ancestors are:

    ```sql
    SELECT DISTINCT SourceEducationOrganizationId
    FROM auth.EducationOrganizationIdToEducationOrganizationId
    WHERE TargetEducationOrganizationId = ANY(@edOrgIds);
    ```

---

## 7. Read Path Integration

### 7.1 GET by id (avoid reconstituting unauthorized data)

For `GET /data/.../{DocumentUuid}`:

1. Resolve `DocumentId` from `dms.Document` via `DocumentUuid`.
2. Run authorization checks using `dms.DocumentSubject` + auth views (or membership tables), returning 404/403 consistently with current DMS behavior.
3. Only if authorized, reconstitute from relational tables (or serve from `dms.DocumentCache` when enabled and fresh).

This keeps authorization enforcement:

- independent from `dms.DocumentCache`,
- consistent for GET and query,
- efficient (no JSON materialization for unauthorized requests).

### 7.2 GET by query (authorization-aware paging)

The authorization predicate must be applied inside the same query used to select page keys (DocumentIds), before `ORDER BY/OFFSET/LIMIT`.

Implementation detail depends on the query shape chosen for the relational primary store (see `reference/design/backend-redesign/caching-and-ops.md`), but the critical invariant is:

> The page is computed over already-authorized rows.

---

## 8. Alternatives and Tradeoffs (Design Ideas)

### Alternative A: Materialized subject membership (`dms.SubjectEdOrg`)

Adapt `reference/design/auth/auth-redesign-subject-edorg-model.md` to the relational primary store:

- Keep `dms.DocumentSubject` (but store `SubjectDocumentId`, not natural keys).
- Add `dms.SubjectEdOrg(SubjectType, SubjectDocumentId, PathwayId, EducationOrganizationId)` maintained transactionally or via background reconciliation.

Pros:

- Very fast query-time checks (`EXISTS` joins only).
- Cross-engine; no reliance on complex views.

Cons:

- Write-time maintenance and recomputation logic (relationship resources drive membership).
- Risk of stale membership if maintenance fails or is deferred.

### Alternative B: Pure view-based (no `dms.DocumentSubject`)

Skip `dms.DocumentSubject` and join resource tables directly to `auth.*` views using derived mapping (table/column determined from `ApiSchema`).

Pros:

- Fewer tables.

Cons:

- Harder for nested securable element paths (requires joins to child tables).
- More complexity in query compilation.

### Alternative C: Hybrid (recommended direction)

- `dms.DocumentSubject` (generic per-document subject extraction; transactional)
- EdOrg hierarchy (adjacency + optional closure table)
- View-based mapping for Student/Staff/Contact using the canonical relationship tables

This keeps write overhead low for high-volume relationship data (no subject membership recomputation) while still enabling ODS-style view-based authorization.

---

## 9. Open Questions

1. **EdOrg hierarchy derivation**: do we hard-code parent relationships (ODS pattern), or derive from `ApiSchema`/MetaEd metadata?
2. **Scope isolation**: if multiple projects share an EdOrg id space, do we scope the hierarchy/closure by `ProjectName`?
3. **Strategy-specific pathway semantics**: confirm which DMS strategies include which pathways so view selection is unambiguous.
4. **Delete semantics**: do we need “including deletes” auth views (ODS supports tracked deletes), or can DMS ignore for now?
