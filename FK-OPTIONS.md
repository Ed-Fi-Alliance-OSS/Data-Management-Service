# FK-Oriented Design Options for DMS Reference Validation

This document describes several alternative designs for reference validation in
the Data Management Service (DMS) that:

- Continue to use a relational database for document storage.
- Reduce churn on the global `Reference` table by avoiding "one row per logical
  reference instance" in the hot path.
- Lean more heavily on foreign keys (FKs) for correctness, instead of triggers
  or computed JSON columns.

It is based on the current design described in:

- `Project-Tanager/docs/DMS/PRIMARY-DATA-STORAGE/README.md`
- `Project-Tanager/docs/DMS/PRIMARY-DATA-STORAGE/ALTERNATIVES.md`
- The PostgreSQL backend scripts in  
  `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts`

## 0. Baseline: Current DMS Design

Current DMS reference validation is centered on three tables:

- `dms.Document`
- `dms.Alias`
- `dms.Reference`

### Key Ideas

- All resources (documents) are stored in a single, partitioned `Document`
  table with a sequential surrogate key `Id` and a `DocumentPartitionKey`.
- Each document has one or more **referential identities** represented as
  deterministic UUIDs (`ReferentialId`), computed from resource type and
  natural key using UUIDv5:
  - This identity can be recomputed from the document contents without hitting
    the database.
  - Subclass resources (e.g., `School`) get an additional alias in terms of
    their superclass identity (e.g., `EducationOrganization`).
- The `Alias` table maps each `ReferentialId` to the corresponding document:
  - `(ReferentialPartitionKey, ReferentialId)` is unique.
  - `DocumentId` and `DocumentPartitionKey` FK back to the document row.
- The `Reference` table stores **every individual reference** that any document
  makes to any other:
  - One row per logical reference instance.
  - `ParentDocumentId` and `ParentDocumentPartitionKey` FK back to the parent
    document.
  - `AliasId` and `ReferentialPartitionKey` FK to `Alias` to enforce existence.
  - The referenced document identity is denormalized into
    `(ReferencedDocumentPartitionKey, ReferencedDocumentId)` to support reverse
    lookups.
- `0015_Create_Reference_Validation_FKs.sql` adds the FK from
  `Reference(ReferentialPartitionKey, AliasId)` to `Alias`, which is the key
  enforcing reference existence.

### Problem: Churn on `Reference`

While this design is very generic and keeps the core schema small, it has a
major practical issue:

- **Every insert/update** of any document with references requires deletes and
  inserts or upserts in the shared `Reference` table.
- High-volume, high-fanout resources (e.g., associations and attendance events)
  can generate large numbers of `Reference` rows per document.
- Any update that changes the set of references implies:
  - Deleting the old rows for that document from `Reference`.
  - Inserting the new set.
- As a result, `Reference` is both:
  - Very large (on the order of 10× `Document` in expected row count).
  - Very hot (frequent writes, many indexes, validation FK to `Alias`).

The goal of the options below is to preserve relational reference validation via
FKs, but **shift that validation onto more stable tables** and reduce overall
churn on a global reference store.

---

## 1. Per-Resource Relational Core (Identity + References)

This design keeps a generic `Document` table for JSON payloads, but introduces a
generated **relational core** per resource type. Each `*Core` table:

- Has one row per document.
- Stores the resource's **natural key fields** (its identity).
- Stores only the **semantically important references** for that resource
  (especially those that appear in natural keys for association resources).
- Has a 1:1 FK back to `Document`.

### Structure

Core document table (similar to current `dms.Document`):

- `Document(DocumentPartitionKey, Id, ResourceName, ResourceVersion, EdfiDoc, ...)`
  - Partitioned by `DocumentPartitionKey`.
  - Primary key: `(DocumentPartitionKey, Id)`.

For `Student`:

- `StudentCore(
    DocumentPartitionKey,
    DocumentId PK/FK → Document(DocumentPartitionKey, Id),
    StudentUniqueId,
    -- other identity-related fields if required
    UNIQUE(StudentUniqueId)
  )`

For `Section`:

- `SectionCore(
    DocumentPartitionKey,
    DocumentId PK/FK → Document,
    SectionIdentifier,
    SchoolId,
    SchoolYear,
    SessionName,
    UNIQUE(SectionIdentifier, SchoolId, SchoolYear, SessionName),
    FK (SchoolId) → SchoolCore(SchoolId),
    FK (SchoolYear) → SchoolYearCore(SchoolYear),
    FK (SessionName, SchoolYear, SchoolId) → SessionCore(SessionName, SchoolYear, SchoolId)
  )`

For `StudentSectionAssociation` (a high-volume association resource):

- `StudentSectionAssociationCore(
    DocumentPartitionKey,
    DocumentId PK/FK → Document,
    StudentUniqueId,
    SectionIdentifier,
    SchoolId,
    SchoolYear,
    SessionName,
    UNIQUE(StudentUniqueId, SectionIdentifier, SchoolId, SchoolYear, SessionName),
    FK (StudentUniqueId) → StudentCore(StudentUniqueId),
    FK (SectionIdentifier, SchoolId, SchoolYear, SessionName)
        → SectionCore(SectionIdentifier, SchoolId, SchoolYear, SessionName)
  )`

### Write Path

On `POST`/`PUT` for a resource:

1. Parse the request JSON according to the API schema.
2. Construct the resource's identity and reference fields.
3. Upsert into the `*Core` table:
   - Identity fields (natural key).
   - Reference fields (e.g., `StudentUniqueId`, `SectionIdentifier`, `SchoolId`,
     `SchoolYear`, `SessionName`).
   - FKs on the `*Core` table enforce reference existence.
4. If step 3 succeeds, insert or update the JSON row in `Document`, joined by
   `(DocumentPartitionKey, DocumentId)`.

On `DELETE`:

1. Delete from the resource's `*Core` table by identity or Document key.
2. A FK violation indicates that other resources still reference it.
3. If the delete from `*Core` succeeds, delete the corresponding row from
   `Document`.

### Benefits

- **Fewer rows in the validation path**:
  - One `*Core` row per document instead of many `Reference` rows per individual
    reference.
  - Association and event resources already have one document per relationship;
    modeling their identity and key references in `*Core` simply reflects this,
    without increasing the document count.
- **Less churn**:
  - Updates that do not change identity or key references do **not** require
    changes in `*Core`; they only patch `Document.EdfiDoc`.
  - Only updates that change key references or identity need to touch `*Core`.
- **FK-based validation** is pushed to stable tables:
  - FKs between `*Core` tables enforce reference correctness.
  - No reliance on triggers or computed JSON columns.

### Tradeoffs

- Increases schema surface area:
  - You now have one `*Core` table per resource instead of three generic tables.
  - However, these tables can be generated from the API schema (similar to
    Query tables) and kept narrow (identity + key references only).
- Does not automatically capture "body references" that are not part of natural
  keys or key relationships. Handling those is covered in the next option.

---

## 2. Core FKs for Identity-Level References + Small Join Tables for Arrays

This option builds on the `*Core` approach from Option 1, but adds targeted,
type-specific join tables for:

- Arrays of references.
- Non-identity references that we still want strong FK-based validation for.

### Structure

Retain:

- `Document` table.
- Per-resource `*Core` tables with identity and key references.

Add per-reference join tables only when needed, e.g.:

- `StudentContactRef(
    ParentDocumentPartitionKey,
    ParentDocumentId FK → Document(DocumentPartitionKey, Id),
    ContactKey1, ContactKey2, ...,  -- natural key fields for Contact
    FK (ContactKey1, ContactKey2, ...) → ContactCore(...)
  )`

There would be one such join table for each "important" repeated or non-key
reference pattern. These tables are significantly smaller and less generic than
the current global `Reference` table.

### Write Path

On `POST`/`PUT`:

1. Upsert the resource's `*Core` row (identity + key references).
   - FKs enforce correctness for key references.
2. For non-key references you care about (e.g., arrays of references):
   - Parse the JSON arrays.
   - Insert rows into the relevant join table (`StudentContactRef`, etc.).
   - FKs to the corresponding `*Core` tables enforce existence.
3. Write or update the JSON in `Document`.

On `DELETE`:

1. Delete from the relevant join tables for that document, if needed.
2. Delete from the resource's `*Core` table:
   - FK violations indicate other entities still reference it.
3. Delete from `Document`.

### Benefits vs Current `Reference` Table

- You no longer externalize **every** reference for **every** document into a
  single monolithic `Reference` table.
- Most high-cardinality relationships (SSA, Section, AttendanceEvent, etc.) are
  validated by FKs in their own `*Core` tables:
  - The "reference" is inherent in their identity (natural key).
  - No extra row per reference instance is needed.
- Only the "hard" references (especially deep or repeated ones) get
  externalized into small, type-specific tables.
- Churn is distributed across multiple smaller tables and is bounded per
  resource type, rather than concentrated in a single global table.

### Tradeoffs

- More tables overall:
  - One per resource (`*Core`).
  - Some number of per-reference join tables for arrays or special cases.
- Design decisions around which references are "worth" externalizing:
  - Some non-key references may remain unvalidated, or validated only in
    application code, to avoid a combinatorial explosion of join tables.

---

## 3. Keeping Aliases but Moving FKs off the Hot `Reference` Table

If retaining the "hash each identity" concept is desirable, but `Reference`
churn is problematic, you can repurpose the current `Alias` approach as the
main source of referential truth and move FKs onto more stable tables.

### Structure

Keep:

- `Document` table.
- `Alias` table mapping `ReferentialId` → `(DocumentPartitionKey, DocumentId)`
  (including superclass aliases for inheritance semantics).

Add:

- Per-resource `*Core` tables, but referencing `Alias` instead of other
  `*Core` tables directly. For example:

  - `StudentSectionAssociationCore(
      DocumentPartitionKey,
      DocumentId PK/FK → Document,
      StudentReferentialId UUID,
      SectionReferentialId UUID,
      FK (StudentReferentialId) → Alias(ReferentialId),
      FK (SectionReferentialId) → Alias(ReferentialId)
    )`

Change the role of `Reference`:

- The global `Reference` table stops being the primary validation mechanism:
  - Either remove it from the hot write path entirely.
  - Or treat it as a **derived index** for reverse navigation and cascading
    updates, populated from a staging table or via batch jobs.
- Reference existence is enforced at `*Core` → `Alias` FKs.

### Write Path

On `POST`/`PUT`:

1. Compute `ReferentialId`(s) as today for the document and its references.
2. Ensure `Alias` rows exist for the referenced identities (or fail if there is
   no target document).
3. Insert/update the resource's `*Core` row with the relevant referential IDs:
   - FKs to `Alias(ReferentialId)` enforce reference existence.
4. Insert/update the `Document` row.
5. Optionally:
   - Populate `Reference` from `*Core` in batch for reverse lookup and cascade
     support, via e.g. `ReferenceStage` and a background process.

On `DELETE`:

1. Delete from `*Core` tables referencing the document's aliases, or rely on FK
   violations to prevent deletes where references exist.
2. Delete the document's `Alias` rows.
3. Delete the `Document` row.

### Benefits

- Retains:
  - Deterministic, DB-independent identity via UUIDv5-style `ReferentialId`.
  - Alias-based inheritance support (subclass vs superclass identity).
- Moves FK-based validation to:
  - Per-resource `*Core` tables referencing `Alias`.
  - These tables have one row per document and far less churn.
- The global `Reference` table:
  - Can be removed from the critical path, or
  - Reduced to a secondary, derived index used for reverse lookups and cascade
    operations, possibly populated asynchronously.

### Tradeoffs

- More schema surface area (per-resource `*Core` tables) compared to three
  generic tables.
- Still relies on hashed IDs:
  - Debuggability is somewhat reduced compared to natural-key or "semantic"
    identity columns.
  - However, index size and structure remain very predictable, similar to
    today's design.

---

## 4. Why Not Triggers or Computed JSON Columns?

Two other broad strategies were considered but rejected for this context:

1. **JSON + computed/generated columns + FKs**
   - Approach:
     - Use JSON store only (`Document`).
     - Add generated columns that extract identity/reference values from JSON.
       - PostgreSQL: `EdfiDoc->'schoolReference'->>'schoolId'`.
       - SQL Server: `JSON_VALUE(EdfiDoc, '$.schoolReference.schoolId')`.
     - Define FKs on those generated columns to identity tables.
   - Issues:
     - Requires many computed columns per resource per reference path.
     - Arrays and nested references still require side tables.
     - Cross-database portability is difficult; depends on JSON-expression and
       generated-column support in each RDBMS.

2. **Pure JSON + triggers/procs for validation**
   - Approach:
     - Single `Document` table with JSON.
     - Per-resource triggers/procs parse JSON and use expression indexes to
       validate references via imperative SQL queries.
   - Issues:
     - Correctness depends on trigger logic, which is harder to reason about
       and test than declarative FKs.
     - Toggling validation on/off is messier.
     - Reverse-reference queries are hard without auxiliary reference tables.

Given the desire to:

- "Lean more on FKs to support validation."
- Avoid "tons of computed columns" and heavy trigger reliance.

the FK-centric options above assume that **references must be visible as normal
relational columns somewhere**, not only as data buried in JSON or hashed
identities in a global `Reference` table.

---

## 5. Summary: Shifting Where FKs Live

All of these FK-oriented options rely on the same fundamental shift:

- Today:
  - Reference validation lives in a global `Reference` table, with one row per
    reference instance, and an FK to `Alias`.
  - This yields a very large, very hot table with high churn.
- Proposed:
  - Move FK-based validation onto **more stable per-document tables**:
    - Per-resource `*Core` tables with identity and key references.
    - Optional small join tables for repeated or special references.
    - Optionally, `*Core` → `Alias` instead of `*Core` → `*Core` for identity.
  - Treat any global reference index (if needed) as derived rather than primary.

This preserves:

- Relational document storage.
- FK-based reference validation.
- The ability to precompute identities (or hashes thereof) from document
  contents.

While it sacrifices:

- The extreme schema simplicity of "three generic tables for everything".

In exchange for:

- Less write-churn.
- Fewer rows in the hot validation path.
- FKs that live on tables whose row count scales with documents, not with
  individual reference count.

