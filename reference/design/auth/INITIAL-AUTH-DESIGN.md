# Initial Authorization Design (Post-JSONB)

## Purpose and Goals

This document outlines an initial redesign of the DMS authorization model focused on:

- Eliminating JSONB-based authorization arrays and their GIN indexes on `dms.Document`.
- Removing specialized authorization tables and trigger-heavy synchronization logic.
- Providing an authorization model that:
  - Supports all existing ODS authorization semantics (EdOrg-, student-, staff-, contact-based, and namespace-based), but does not mimic ODS schema.
  - Integrates cleanly with the planned `dms.DocumentIndex` query/indexing design.
  - Keeps database roundtrips low and avoids multi-index bitmap scans on `dms.Document`.
- Keeping all authorization enforcement in application code (no PostgreSQL RLS in this phase).

This is a starting point for design; details may evolve with implementation and performance testing.

---

## High-Level Model

The new authorization model is built around three main pieces:

1. `dms.DocumentIndex` – a narrow, hash-partitioned index table that supports efficient filtering and paging using `QueryFields` (JSONB) and created-at ordering (already defined in the document-query indexing design).
2. `dms.DocumentSubject` – a generic relational mapping from documents to the “subjects” they are about (students, contacts, staff, education organizations, etc.).
3. `dms.SubjectEdOrg` – a generic relational mapping from subjects to education organizations that they are associated with, including ancestor expansion through the EdOrg hierarchy.

Authorization is expressed as a relational existence check:

> A document is readable if there exists a subject on that document that is associated with at least one of the caller’s authorized EducationOrganizationIds through `SubjectEdOrg` (plus any additional constraints like namespace).

All write-side maintenance of `DocumentSubject` and `SubjectEdOrg` is performed explicitly in the C# pipeline. There are no authorization-related triggers on `dms.Document`.

---

## Core Tables

### 1. `dms.DocumentSubject`

Represents which “subjects” a particular document is about. This replaces JSONB auth fields on `dms.Document` and pathway-specific authorization tables.

**Conceptual schema**

```sql
CREATE TABLE dms.DocumentSubject (
    ProjectName          varchar(256) NOT NULL,
    ResourceName         varchar(256) NOT NULL,
    DocumentPartitionKey smallint     NOT NULL,
    DocumentId           bigint       NOT NULL,

    SubjectType          smallint     NOT NULL, -- e.g. 1 = Student, 2 = Contact, 3 = Staff, 4 = EdOrg, ...
    SubjectIdentifier    text         NOT NULL, -- StudentUniqueId, ContactUniqueId, StaffUniqueId, or EdOrgId::text, etc.

    PRIMARY KEY (
        ProjectName,
        ResourceName,
        DocumentPartitionKey,
        DocumentId,
        SubjectType,
        SubjectIdentifier
    )
);
```

**Indexes**

- Primary key as above.
- Supporting index for write-side and introspection:

  ```sql
  CREATE INDEX IX_DocumentSubject_Subject
      ON dms.DocumentSubject (SubjectType, SubjectIdentifier);
  ```

**Population**

- C# upsert/update pipeline is responsible for maintaining `DocumentSubject`:
  - Use `DocumentSecurityElements` and resource metadata to determine if a resource is:
    - Student-securable → `(SubjectType = Student, SubjectIdentifier = StudentUniqueId)`.
    - Contact-securable → `(SubjectType = Contact, SubjectIdentifier = ContactUniqueId)`.
    - Staff-securable → `(SubjectType = Staff, SubjectIdentifier = StaffUniqueId)`.
    - EdOrg-securable → `(SubjectType = EdOrg, SubjectIdentifier = EducationOrganizationId::text)`.
    - Additional subject types can be added as needed.
  - For a given document:
    - On insert: insert one or more `DocumentSubject` rows.
    - On update: delete existing subject rows for that document and insert new rows based on the updated security elements.
    - On delete: cascading delete via FK on `Document` (if desired) or explicit delete in C#.

This table is deliberately generic so that we do not need a per-pathway authorization table.

### 2. `dms.SubjectEdOrg`

Represents the education organization memberships for a subject. This is the generic replacement for all pathway-specific `…Authorization` tables.

**Conceptual schema**

```sql
CREATE TABLE dms.SubjectEdOrg (
    SubjectType          smallint NOT NULL, -- Student, Contact, Staff, EdOrg, etc.
    SubjectIdentifier    text     NOT NULL, -- unique identifier per subject-type
    EducationOrganizationId bigint NOT NULL,

    Pathway              smallint NOT NULL, -- optional: e.g. 1 = StudentSchool, 2 = StudentResponsibility, 3 = ContactStudentSchool, 4 = StaffEdOrg, ...

    PRIMARY KEY (SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId)
);
```

**Indexes**

The primary key provides an index suitable for:

- Looking up all EdOrgs for a subject:

  ```sql
  WHERE SubjectType = $1 AND SubjectIdentifier = $2
  ```

- Evaluating auth for a set of EdOrgIds:

  ```sql
  WHERE SubjectType = $1 AND SubjectIdentifier = $2 AND EducationOrganizationId = ANY ($edorg_ids)
  ```

If needed, we can add a simple supporting index:

```sql
CREATE INDEX IX_SubjectEdOrg_Subject
    ON dms.SubjectEdOrg (SubjectType, SubjectIdentifier);
```

**Population**

Population is driven by upserts/deletes of *relationship* resources and the EdOrg hierarchy, via C#:

- For `StudentSchoolAssociation`:
  - Extract `StudentUniqueId` and `schoolId`.
  - Use `EducationOrganizationHierarchy` (and `GetEducationOrganizationAncestors`) to compute all ancestor EdOrgIds.
  - For `(SubjectType = Student, SubjectIdentifier = StudentUniqueId, Pathway = StudentSchool)`:
    - Delete existing rows in `SubjectEdOrg` for that subject/pathway.
    - Insert one row per ancestor EdOrgId.

- For `StudentEducationOrganizationResponsibilityAssociation`, `StudentContactAssociation`, and Staff EdOrg associations:
  - Similar pattern: compute `(SubjectType, SubjectIdentifier, Pathway)` and their ancestor-expanded EdOrgIds, rewrite the subject’s rows for that pathway.

- For EdOrg hierarchy changes:
  - Either:
    - Recompute affected subjects’ memberships in C# (batch job or background worker), or
    - Provide a small helper routine to recalculate memberships for subjects associated with a changed EdOrg.

Important: there are **no triggers** here; the C# pipeline takes explicit responsibility for maintaining `SubjectEdOrg`.

---

## Read Path: Authorized Paging over `DocumentIndex`

The read path combines:

- Query filtering and paging on `dms.DocumentIndex` (GIN on `QueryFields`, B-tree on `(ProjectName, ResourceName, CreatedAt, DocumentPartitionKey, DocumentId)`).
- Authorization filtering via `DocumentSubject` and `SubjectEdOrg`.

We assume that, for the current request, the caller’s effective `EducationOrganizationIds` have already been determined in application code (e.g., from claimset + hierarchy expansion) as `authorized_edorg_ids[]`.

**Authorized query with offset/limit**

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
      AND di.QueryFields @> $3::jsonb               -- normal query filters
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
            AND se.EducationOrganizationId = ANY ($4::bigint[])
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

Key points:

- Authorization is applied **inside** the same CTE that performs `ORDER BY / OFFSET / LIMIT`. Paging happens over *already authorized* rows.
- The planner can:
  - Use the GIN on `QueryFields` and the `(ProjectName, ResourceName, CreatedAt, DocumentPartitionKey, DocumentId)` index on `DocumentIndex` for filtering and ordering.
  - Use B-tree indexes on `DocumentSubject` and `SubjectEdOrg` to evaluate the `EXISTS` subquery.
- `totalCount` (if needed) uses the same `WHERE` (including the `EXISTS`) against `dms.DocumentIndex` without the `OFFSET/LIMIT`.

### Namespace-Based and Other Non-Relationship Auth

Namespace-based authorization and other non-relationship strategies can remain entirely within `DocumentIndex.QueryFields`:

- The `QueryFields` projection should include the canonical `namespace` (or prefixes) for resources where namespace-based security applies.
- Authorization logic then adds namespace predicates to the same `QueryFields @>` filter JSON, with no need for `DocumentSubject`/`SubjectEdOrg`.

Other strategies (e.g., ownership tokens) can either:

- Be represented as additional `SubjectType`/`SubjectIdentifier` values in `DocumentSubject` and `SubjectEdOrg`, or
- Be expressed as additional fields in `QueryFields` plus appropriate application-level predicates.

---

## Write Path Responsibilities (C#)

This design assumes all auth-related maintenance is handled in C# code, not triggers.

### Securable Document Upsert

For a securable document (e.g., student-, contact-, staff-, or EdOrg-securable):

1. Extract security elements using existing `DocumentSecurityElements`/MetaEd metadata.
2. Determine which subject(s) apply based on resource configuration.
3. Maintain `dms.DocumentSubject`:
   - On insert:
     - Insert one or more rows per subject: `(ProjectName, ResourceName, DocumentPartitionKey, DocumentId, SubjectType, SubjectIdentifier)`.
   - On update:
     - Delete existing `DocumentSubject` rows for that document key.
     - Insert updated rows based on the new security elements.
   - On delete:
     - Delete `DocumentSubject` rows for the document (either via FK cascade or explicit delete).

`dms.DocumentIndex` continues to be maintained via the existing `InsertNewDocument` / update procedures and the `QueryFields` projection design.

### Relationship Resource Upsert

For relationship resources that define memberships (e.g., `StudentSchoolAssociation`, `StudentEducationOrganizationResponsibilityAssociation`, `StudentContactAssociation`, staff EdOrg associations):

1. Extract the subject keys (`StudentUniqueId`, `ContactUniqueId`, `StaffUniqueId`, etc.) and the EdOrg identifiers from `EdfiDoc`.
2. Use `EducationOrganizationHierarchy` (and `GetEducationOrganizationAncestors`) to expand to all ancestor EdOrgIds.
3. Rewrite the subject’s rows in `dms.SubjectEdOrg` for the relevant pathway:
   - Delete existing `(SubjectType, SubjectIdentifier, Pathway, *)` rows.
   - Insert new rows `(SubjectType, SubjectIdentifier, Pathway, EducationOrganizationId)` for all ancestor EdOrgIds.

For EdOrg hierarchy changes, the pipeline must ensure `SubjectEdOrg` remains consistent (either via targeted recomputation or a periodic reconciliation job).

---

## Comparison to ODS Authorization

Although schema details differ, the effective behavior is analogous to ODS’s view-based authorization:

- ODS:
  - Uses `auth.*` views fed by relational relationship tables, and existence checks like:
    - “Does there exist a row in `auth.StudentSchool` for (StudentUSI, EdOrgId in caller’s set)?”
  - Strategies combine these views with AND/OR logic.

- DMS (proposed):
  - Uses two generic tables:
    - `DocumentSubject`: which subjects a document is about.
    - `SubjectEdOrg`: which EdOrgs each subject belongs to (per pathway).
  - Authorization is an existence check:
    - “Does there exist a (subject on this document) whose `SubjectEdOrg` row has `EducationOrganizationId` in the caller’s set?”
  - Strategy composition (StudentSchool vs StudentResponsibility vs Staff vs Contact, and AND/OR combinations) remains in application-level strategy code, but the underlying relational data easily supports the same semantics.

Performance characteristics for the authorization portion of reads should be in the same class as ODS:

- Narrow tables and B-tree indexes.
- Existence checks and joins against well-indexed subject/EdOrg relationships.
- No multi-index bitmap scans on `dms.Document`.

---

## Open Questions / Next Steps

This initial design intentionally keeps the model simple and generic. Open questions and follow-ups include:

- Exact encoding of `SubjectType` and `Pathway` (enums vs descriptor tables).
- Detailed mapping from Ed-Fi resources to `SubjectType`/`Pathway` values (e.g., which resources are “securable”, which relationship resources feed which pathways).
- How to structure C# helpers for:
  - Maintaining `DocumentSubject` and `SubjectEdOrg` consistently.
  - Computing ancestor EdOrg sets efficiently and caching them when appropriate.
- Migration strategy:
  - Dropping JSONB auth arrays and GINs on `dms.Document`.
  - Removing existing `…Authorization` tables and triggers.
  - Backfilling `DocumentSubject` and `SubjectEdOrg` from current data.
- Additional indexing based on observed query patterns and data volumes.

This document should be treated as the baseline for implementation and performance spikes, not the final word on the authorization schema.
