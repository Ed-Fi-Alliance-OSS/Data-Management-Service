# Initial Authorization Design – Stakeholder Summary

## Overview

The new authorization design makes security checks fast and predictable by using simple relational tables instead of large JSON blobs or triggers.

At a high level:

- `DocumentIndex` is a narrow, partitioned index table for fast filtering and paging.
- `DocumentSubject` records who each document is about (students, staff, contacts, education organizations).
- `SubjectEdOrg` records which education organizations each subject “belongs to”, already expanded through the EdOrg hierarchy.

At query time, we join these pieces: we find matching documents in `DocumentIndex`, then require that at least one subject on each document has an EducationOrganization in the caller’s authorized set.

All of this happens in application code and SQL; there is no reliance on PostgreSQL Row-Level Security in this design.

---

## What is a “Subject”?

A subject is the person or organization that the data is about, in a way that matters for authorization.

Examples:

- Student – identified by `StudentUniqueId` (e.g., `"S-1234"`).
- Staff member – identified by `StaffUniqueId` (e.g., `"T-7890"`).
- Contact (parent/guardian) – identified by `ContactUniqueId`.
- Education Organization – school, district, ESC, identified by `EducationOrganizationId`.

A single document can have multiple subjects. For example:

- A `StudentAssessment` might have both a student and a staff member.
- A contact note might have both a student and a contact.

---

## Key Tables and Their Roles

### 1. `DocumentIndex`

- Holds a compact projection of each document:
  - `ProjectName`, `ResourceName`
  - `DocumentPartitionKey`, `DocumentId`
  - `CreatedAt`
  - `QueryFields` (JSONB) – only the fields needed for query filters (e.g., student ID, namespace, date).
- Hash-partitioned by `(ProjectName, ResourceName)` for efficient paging.
- Indexed for:
  - GIN on `QueryFields` for query filters.
  - B-tree on `(ProjectName, ResourceName, CreatedAt, DocumentPartitionKey, DocumentId)` for ordering.

### 2. `DocumentSubject`

Answers: “Who is this document about?”

- For each document, stores one row per subject:
  - Subject type (Student, Staff, Contact, EdOrg, etc.).
  - Subject key (e.g., `StudentUniqueId`, `StaffUniqueId`, `ContactUniqueId`, `EducationOrganizationId`).
- Example rows for one `StudentAssessment`:

  - `(Student, "S-1234")`
  - `(Staff, "T-7890")`

### 3. `SubjectEdOrg`

Answers: “Which EducationOrganizations does this subject belong to?”

- For each subject and authorization pathway (StudentSchool, StudentResponsibility, ContactStudentSchool, StaffEdOrg), stores one row per EducationOrganizationId.
- Includes ancestor-expanded EdOrgs via the EdOrg hierarchy:
  - e.g., if a student is at School 255901 and that school belongs to LEA 2559, we store both 255901 and 2559.
- Example rows:

  - `(Student, "S-1234", StudentSchool, 255901)`
  - `(Student, "S-1234", StudentSchool, 2559)`
  - `(Staff, "T-7890", StaffEdOrg, 255901)`
  - `(Staff, "T-7890", StaffEdOrg, 2559)`

### 4. EducationOrganization and Hierarchy

- A clean EdOrg hierarchy is stored in:
  - `EducationOrganization` – one row per EdOrg (with link to its document).
  - `EducationOrganizationRelationship` – parent/child edges, allowing multiple parents and children.
- Used only to expand base EdOrgIds into ancestor sets when maintaining `SubjectEdOrg`.

---

## How Authorization Works at Read Time

When a client calls a GET with filters and pagination:

1. **Filter & page on `DocumentIndex`**  
   The system:
   - Filters on `ProjectName`, `ResourceName`.
   - Applies query filters via `QueryFields @> ...` (e.g., student ID, namespace, dates).
   - Orders by `CreatedAt` and applies `OFFSET/LIMIT`.

2. **Enforce authorization with `DocumentSubject` + `SubjectEdOrg`**  
   For each candidate row in `DocumentIndex`, we require:
   - There exists a subject in `DocumentSubject` for that document.
   - That subject has at least one row in `SubjectEdOrg` where `EducationOrganizationId` is in the caller’s authorized EdOrg set.

3. **Fetch full document from `Document`**  
   After we have an authorized page of `(DocumentPartitionKey, DocumentId)` from `DocumentIndex`, we join to `dms.Document` and return the full `EdfiDoc` payloads.

Effectively, a document is readable if **any of its subjects has a relationship to an EdOrg that the caller is authorized for**, and that is enforced before pagination.

---

## How the Tables Stay in Sync with `Document`

All updates to `DocumentSubject` and `SubjectEdOrg` happen inside the same database transaction as the `Document` write, so they are always consistent.

### Normal POST/PUT (securable resources)

When a securable document (e.g., Student-secure, Staff-secure, Contact-secure, EdOrg-secure) is created or updated:

1. The pipeline extracts `DocumentSecurityElements` from the request body (student IDs, staff IDs, etc.).
2. The backend writes or updates the `Document` row.
3. The backend rewrites the document’s `DocumentSubject` rows to reflect the subjects in the payload:
   - Delete existing `DocumentSubject` rows for that document.
   - Insert new rows based on current security elements.

If subjects change (e.g., a StudentUniqueId changes on the document), the stored subjects are updated accordingly.

### Relationship resources (subject–EdOrg relationships)

For a relationship resource like `StudentSchoolAssociation`, `StudentContactAssociation`, or staff EdOrg associations:

1. The pipeline identifies the subject key (e.g., student, contact, staff) and the base EdOrg from the payload.
2. After writing the relationship document, the backend:
   - Finds all relationship documents for that subject and pathway.
   - Extracts all base EdOrgIds from those docs.
   - Uses the EdOrg hierarchy to compute all ancestor EdOrgIds.
   - Rewrites that subject’s `SubjectEdOrg` rows for the pathway:
    - Deletes existing rows for `(SubjectType, SubjectIdentifier, Pathway)`.
     - Inserts new rows for each EdOrgId in the union.

This “recompute on change” strategy keeps the logic simple and ensures `SubjectEdOrg` always reflects the current state of relationships.

### Deletes

On deletion of a document:

- `DocumentSubject` rows for that document are removed (via cascade or explicit delete).

On deletion of a relationship document:

- The backend recomputes the affected subject’s `SubjectEdOrg` memberships (using the same recomputation steps), so the EdOrg list reflects the new state.

Because all of this work is done inside a single transaction per request, any read either sees the old state (before the change) or the new state (after the change), never a mix.

---

## Key Benefits for Stakeholders

- **Performance and scalability**  
  - Authorization checks use small, indexed tables instead of large JSON blobs, making queries predictable and performant at scale.

- **Clarity and flexibility**  
  - The model (Document → Subject → EdOrg) is easy to reason about and matches the way education data is naturally secured (by who the data is about and which organizations they belong to).

- **Consistency**  
  - Authorization metadata is updated in lockstep with document changes, ensuring consistent behavior without complex triggers.

- **Extensibility**  
  - New subject types or pathways (e.g., additional relationship patterns) can be added by extending the `SubjectType`/`Pathway` enums and the write-side maintenance logic, without redesigning the core model.
