# DMS Reference Validation Redesign – Idea 1

> *Per-resource `*Core` tables + small join tables for arrays/non-key references, with `Alias` retained as a generic identity layer and FKs targeting `Alias.Id` (BIGINT) instead of `ReferentialId` or a global `Reference` table.*

## 1. Goals

This redesign is motivated by performance and maintainability concerns with the
current DMS reference validation approach, especially the churn on the global
`dms.Reference` table.

**Goals:**

- Reduce write churn and index pressure caused by externalizing **every**
  logical reference into `dms.Reference`.
- Keep reference validation **declarative** via foreign keys, not primarily via
  triggers or application logic.
- Retain a **generic identity abstraction** that can represent inheritance
  (e.g., `School` satisfying an `EducationOrganization` reference) without
  bespoke per-hierarchy base tables.
- Preserve the single `Document` table for JSON payloads and the existing
  partitioning approach.

## 2. High-Level Design

The new approach pivots reference validation away from a single global
`dms.Reference` table and onto:

1. Per-resource `*Core` tables, with one row per document:
   - Identity (natural key fields).
   - Key references (especially those that participate in natural keys of
     association resources).
   - FK relationships to other entities via alias IDs or base identities.
2. Small, type-specific join tables for:
   - Arrays of references.
   - Non-key references that we still want to validate via FKs.
3. `Alias` remains as a generic identity layer, but:
   - FKs target `Alias.Id` (BIGINT, sequential) instead of `ReferentialId`
     (UUID).
   - `ReferentialId` is used only for identity uniqueness and lookup.
4. The existing `Reference` table is:
   - Removed from the hot write path, or
   - Treated as a derived index for reverse lookups and cascade support, built
     from `*Core`/join tables (potentially asynchronously).

## 3. Schema Sketch

### 3.1 Document Table

Retain the existing partitioned `Document` table (simplified sketch):

- `dms.Document`
  - `Id BIGINT` – surrogate PK per partition.
  - `DocumentPartitionKey SMALLINT` – partition key.
  - `DocumentUuid UUID` – external resource ID.
  - `ResourceName VARCHAR`, `ResourceVersion VARCHAR`, `ProjectName VARCHAR`.
  - `EdfiDoc JSONB`.
  - `CreatedAt`, `LastModifiedAt`, `LastModifiedTraceId`.
  - PK: `(DocumentPartitionKey, Id)`.
  - Unique index on `(DocumentPartitionKey, DocumentUuid)`.

### 3.2 Alias Table (Generic Identity + Inheritance)

Retain `Alias` but emphasize `Id` as the FK target:

- `dms.Alias`
  - `Id BIGINT` – sequential surrogate key.
  - `ReferentialPartitionKey SMALLINT NOT NULL`.
  - `ReferentialId UUID NOT NULL`.
  - `DocumentId BIGINT NOT NULL`.
  - `DocumentPartitionKey SMALLINT NOT NULL`.
  - PK: `(ReferentialPartitionKey, Id)` or simply `(Id)` depending on physical
    design.
  - Unique index:
    - `UX_Alias_ReferentialId` on `(ReferentialPartitionKey, ReferentialId)` to
      enforce identity uniqueness and support alias lookup.
  - FK:
    - `(DocumentPartitionKey, DocumentId) → dms.Document(DocumentPartitionKey, Id)`.

**Inheritance:**

- A single document may have multiple alias rows:
  - One alias for its "own" identity (e.g., `School`).
  - One or more aliases for its base-type identities (e.g.,
    `EducationOrganization`), allowing superclass references to be satisfied by
    subclass documents.

### 3.3 Per-Resource `*Core` Tables

For each resource type `R`, add a `RCore` table:

- One row per document of type `R`.
- Columns:
  - `(DocumentPartitionKey, DocumentId)` – PK, FK → `Document`.
  - Natural key fields for resource `R`.
  - Key references represented as alias IDs (BIGINT) or identity key parts.
  - UNIQUE constraints for resource identity.
  - FKs for reference validation.

**Example: StudentCore**

- `StudentCore`
  - `DocumentPartitionKey SMALLINT NOT NULL`.
  - `DocumentId BIGINT NOT NULL`.
  - `StudentUniqueId VARCHAR(256) NOT NULL`.
  - `StudentAliasId BIGINT NOT NULL` – alias for this Student.
  - PK: `(DocumentPartitionKey, DocumentId)`.
  - UNIQUE: `(StudentUniqueId)`.
  - FK:
    - `(DocumentPartitionKey, DocumentId) → Document`.
    - `(StudentAliasId) → Alias(Id)` (or `(AliasPartitionKey, StudentAliasId)`
      if partitioned).

**Example: SectionCore (referencing EdOrg via alias)**

- `SectionCore`
  - `DocumentPartitionKey SMALLINT NOT NULL`.
  - `DocumentId BIGINT NOT NULL`.
  - `SectionIdentifier VARCHAR NOT NULL`.
  - `SchoolYear INT NOT NULL`.
  - `EducationOrganizationAliasId BIGINT NOT NULL` – alias for base EdOrg.
  - `SectionAliasId BIGINT NOT NULL` – alias for the Section itself, if
    desired.
  - PK: `(DocumentPartitionKey, DocumentId)`.
  - UNIQUE: `(SectionIdentifier, SchoolYear, EducationOrganizationAliasId)`.
  - FK:
    - `(DocumentPartitionKey, DocumentId) → Document`.
    - `(EducationOrganizationAliasId) → Alias(Id)` (base-type identity).
    - `(SectionAliasId) → Alias(Id)` (optional).

**Example: StudentSectionAssociationCore (high-volume association)**

- `StudentSectionAssociationCore`
  - `DocumentPartitionKey SMALLINT NOT NULL`.
  - `DocumentId BIGINT NOT NULL`.
  - `StudentAliasId BIGINT NOT NULL`.
  - `SectionAliasId BIGINT NOT NULL`.
  - PK: `(DocumentPartitionKey, DocumentId)`.
  - UNIQUE:
    - `(StudentAliasId, SectionAliasId)` or the equivalent natural key form.
  - FK:
    - `(DocumentPartitionKey, DocumentId) → Document`.
    - `(StudentAliasId) → Alias(Id)`.
    - `(SectionAliasId) → Alias(Id)`.

### 3.4 Small Join Tables for Arrays / Non-Key References

For references that:

- Are not part of the natural key, and/or
- Are arrays inside the JSON document,

introduce small, type-specific join tables, e.g.:

- `StudentContactRef`
  - `ParentDocumentPartitionKey SMALLINT NOT NULL`.
  - `ParentDocumentId BIGINT NOT NULL`.
  - `ContactAliasId BIGINT NOT NULL`.
  - PK: `(ParentDocumentPartitionKey, ParentDocumentId, ContactAliasId)` (or
    a surrogate key).
  - FK:
    - `(ParentDocumentPartitionKey, ParentDocumentId) → Document`.
    - `(ContactAliasId) → Alias(Id)`.

These tables:

- Externalize only the references that cannot be captured cleanly in `*Core`
  scalar columns.
- Are significantly smaller and more focused than the monolithic
  `dms.Reference` table.

### 3.5 Optional: Derived `Reference` Table

If needed for reverse navigation or cascade support, `dms.Reference` can be
retained as a **derived** or **materialized** index:

- Populated from `*Core` and join tables:
  - e.g., logically unioning `StudentSectionAssociationCore` and
    `StudentContactRef` into a global view of references.
- Maintained:
  - Synchronously via stored procedures; or
  - Asynchronously via staging tables and background workers.
- Not used for primary reference validation; FKs live on `*Core` and join
  tables.

## 4. Write Paths

### 4.1 Insert (POST)

For a document of resource type `R`:

1. **Compute identities & aliases**:
   - Compute `ReferentialId`(s) for the document (resource-specific and any
     relevant base-type identities).
   - Lookup or insert the corresponding `Alias` rows:
     - If a `ReferentialId` already exists, reuse its `Alias.Id`.
     - Otherwise, insert a new `Alias` row with the computed identity and the
       eventual `DocumentId` (or fill `DocumentId` after step 2 if necessary).
2. **Insert into `Document`**:
   - Insert into `dms.Document` to obtain `(DocumentPartitionKey, Id)`.
3. **Update/complete Alias rows** (if necessary):
   - Ensure alias rows have the correct `(DocumentPartitionKey, DocumentId)`.
4. **Insert into `RCore`**:
   - Using parsed JSON and alias IDs, insert into the resource’s `*Core` table:
     - Identity fields.
     - Key references as `Alias.Id` values.
   - FKs here enforce reference existence via `Alias`.
5. **Insert into join tables (arrays/non-key references)**:
   - Parse JSON arrays of references.
   - Insert rows into the relevant join tables with:
     - Parent `(DocumentPartitionKey, DocumentId)`.
     - Target `ContactAliasId`, etc.
   - FKs to `Alias(Id)` enforce existence of targets.

All steps occur within a single transaction.

### 4.2 Update (PUT/PATCH)

For an update to a document of resource type `R`:

1. **Find the document**:
   - Locate `(DocumentPartitionKey, DocumentId)` using `DocumentUuid` or other
     unique keys.
2. **Recompute identities & aliases if identity changes**:
   - If the natural key changes, recompute `ReferentialId`(s).
   - Upsert alias rows as needed:
     - Possible `UX_Alias_ReferentialId` violations indicate identity
       conflicts.
3. **Update `RCore`**:
   - If identity or key references changed, update the `*Core` row.
   - FKs ensure new references are valid.
4. **Refresh join tables** (if arrays/non-key references changed):
   - Delete existing rows for `(DocumentPartitionKey, DocumentId)` from relevant
     join tables.
   - Insert new rows based on updated JSON.
5. **Update JSON**:
   - Update the `EdfiDoc` in `Document`.

If identity does not change and key references do not change, steps 2–4 may be
skipped, resulting in a simple JSON patch and minimal relational churn.

### 4.3 Delete (DELETE)

For deleting a document:

1. **Delete from join tables**:
   - Delete rows referencing `(DocumentPartitionKey, DocumentId)` in join
     tables (or rely on ON DELETE CASCADE if appropriate).
2. **Delete from `RCore`**:
   - Attempt to delete the row from the resource’s `*Core` table.
   - FK violations indicate the existence of other documents that still depend
     on this document (via alias IDs), preventing the delete.
3. **Delete Alias rows**:
   - Once it is safe to remove the document, delete the alias rows pointing to
     `(DocumentPartitionKey, DocumentId)`.
   - FKs from other `*Core`/join tables to `Alias(Id)` will prevent deletion if
     this identity is still referenced.
4. **Delete from `Document`**:
   - Finally, delete the row from `dms.Document`.

## 5. Superclass/Subclass Handling via Alias

A key benefit of retaining `Alias` is that inheritance can be encoded as
multiple identities for the same document without bespoke base tables.

### Example: `EducationOrganization` / `School`

For a `School` document:

1. Compute:
   - A `School` `ReferentialId` (identity based on School natural key).
   - An `EducationOrganization` `ReferentialId` (base identity).
2. Insert two alias rows:
   - `Alias` row for the School identity.
   - `Alias` row for the EducationOrganization identity.
   - Both rows point to the same `(DocumentPartitionKey, DocumentId)`.
3. A `Section` or association resource that logically references an
   `EducationOrganization`:
   - Computes the base identity.
   - Looks up the corresponding alias.
   - Stores the alias’s `Id` in its `*Core` row.
   - The FK `(EducationOrganizationAliasId) → Alias(Id)` ensures that some
     document implementing that base identity exists (regardless of its subtype).

This approach generalizes to other hierarchies (e.g., descriptors, staff
hierarchies) without explicit inheritance base tables.

## 6. Performance Considerations

### 6.1 What This Design Fixes

- **Removes the global `Reference` table from the hot path**:
  - No longer a “one row per reference instance” model.
  - Most references are validated via:
    - One `*Core` row per document.
    - A small number of join table rows for array/non-key references.
- **Reduces write churn**:
  - Updates that do not change identity or key references only touch the
    `Document` table.
  - High-volume association/event resources have reference validation in their
    own `*Core` tables, not via a central `Reference` table.

### 6.2 Cost of Keeping Alias

Compared to dropping `Alias` and modeling inheritance with bespoke base
identity tables:

- **Alias costs**:
  - One additional table (`Alias`) with ~1–2 rows per document.
  - A unique non-clustered index on `(ReferentialPartitionKey, ReferentialId)`:
    - Random UUID keys, but:
      - Only per-identity (per document), not per reference instance.
      - Used primarily for identity uniqueness and occasional lookup.
  - FKs from `*Core`/join tables to `Alias.Id`:
    - Operate on sequential BIGINT keys, not UUIDs.
- **Benefits**:
  - A single, generic identity abstraction:
    - Works for all resource types.
    - Handles superclass/subclass semantics via multiple aliases per document.
  - Avoids many bespoke base identity tables and wide composite-key indexes.

Overall, the main performance problems from the current design are addressed by:

- Moving validation FKs onto `*Core` and join tables.
- Eliminating or shrinking the hot `Reference` table.

The incremental cost of keeping `Alias` as a generic identity layer (with FKs by
BIGINT `Id`) is relatively modest and may be lower than the cost of managing
many per-hierarchy base identity tables with composite keys.

## 7. Summary

This redesign:

- Retains the existing `Document` and `Alias` tables.
- Introduces per-resource `*Core` tables and small join tables for arrays and
  non-key references.
- Moves FK-based reference validation onto:
  - `*Core` tables (one row per document).
  - Join tables (limited to complex/non-key references).
- Uses `Alias.Id` (BIGINT) as the primary FK join target for references,
  keeping FKs and their supporting indexes narrow and sequential.
- Treats any global `Reference` table as derived, not as the main validation
  mechanism.

The result is a design that:

- Preserves the generic, identity-based philosophy of DMS.
- Substantially reduces write churn and contention compared to the current
  `Reference`-centric implementation.
- Keeps correctness anchored in declarative foreign keys rather than triggers
  or extensive application validation logic.

