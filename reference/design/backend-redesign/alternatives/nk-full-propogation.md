# Backend Redesign Alternative: Full Natural-Key Propagation (No `dms.ReferenceEdge`)

## Status

Draft (alternative design exploration).

## Executive summary

This document is a modified version of `reference/design/backend-redesign/alternatives/nk-plus-touch-cleaned.md`. It keeps the same core idea of **database-driven natural-key propagation** to make referential-id maintenance row-local, but extends it to **all direct document references**, not just identity-component reference sites.

The key change is:

- For every `{...}Reference` object that is flattened to a `..._DocumentId` FK column, also materialize the referenced resource’s **identity natural-key fields** into columns on the referencing table and enforce a **composite FK with `ON UPDATE CASCADE`**.

This yields two primary benefits:

1. **Eliminate touch cascades and `dms.ReferenceEdge`**
   - When a referenced resource’s identity changes, the database cascades the updated identity values into all direct referrers (identity-component and non-identity).
   - Those cascaded updates are real row updates on the referrers, so normal “representation stamping” triggers can bump `_etag`/`_lastModifiedDate`/`ChangeVersion` without any separate reverse-lookup mechanism.

2. **Avoid subqueries for reference-identity query parameters**
   - As described in `reference/design/backend-redesign/flattening-reconstitution.md` (Query predicates for reference identity fields, lines 513–538), the baseline query compiler must translate reference-identity parameters into `IN (subquery)`/`EXISTS` patterns because reference objects are stored as a single FK.
   - With propagated identity columns for all reference sites, `ApiSchema.queryFieldMapping` entries that point into reference-object identity fields can compile to simple predicates against local columns (no subquery).

This trade is attractive only if:
- identity updates are operationally rare, and
- the system accepts potentially large synchronous fan-out during those events (now performed by FK cascades instead of touch procedures).

## How this changes the baseline redesign (explicit deltas)

This alternative intentionally diverges from these baseline design points:

1. **Update tracking is no longer read-time derived**
   - Baseline: `reference/design/backend-redesign/update-tracking.md` derives `_etag/_lastModifiedDate/ChangeVersion` at read time from local tokens plus dependency tokens to avoid write-time fan-out.
   - Alternative: store representation metadata on `dms.Document` and keep it correct by write-time updates (here: via FK cascade updates + normal stamping triggers).

2. **Reference objects are not purely `..._DocumentId`**
   - Baseline: `reference/design/backend-redesign/flattening-reconstitution.md` suppresses scalar-column derivation for reference-object identity descendants (“a reference object is represented by one FK, not duplicated natural-key columns”).
   - Alternative: for **every document reference site** (root + child tables), also materialize the referenced resource’s identity values into scalar columns and keep them synchronized via `ON UPDATE CASCADE`.

3. **Identity closure recompute is no longer application-managed**
   - Baseline: `reference/design/backend-redesign/transactions-and-concurrency.md` maintains `dms.ReferentialIdentity` for reference-bearing identities via application-managed closure traversal and explicit locking (`dms.IdentityLock`).
   - Alternative: use database cascades (`ON UPDATE CASCADE`, or triggers where required) + per-resource triggers to maintain `dms.ReferentialIdentity` row-locally (no closure traversal in application code).

4. **No `dms.ReferenceEdge`, no touch-cascade targeting**
   - Baseline: uses `dms.ReferenceEdge` for reverse lookups and (in some designs) indirect-update handling.
   - `nk-plus-touch-cleaned`: keeps `dms.ReferenceEdge` to target non-identity referrers for a touch cascade.
   - Alternative (this doc): remove both the touch cascade and `dms.ReferenceEdge`; indirect-update impacts are materialized as regular FK-cascade updates to the referrer’s stored reference identity columns.

## Goals

1. Preserve baseline’s relational-first storage and use of stable `DocumentId` FKs for referential integrity and query performance.
2. Make identity updates and their dependent effects fully correct at commit time (no stale window).
3. Provide ODS-like “indirect update” semantics for representation metadata:
   - if a referenced identity changes, referrers’ `_etag/_lastModifiedDate/ChangeVersion` must change.
4. Improve query compilation for reference-identity parameters by avoiding referenced-table subqueries where possible.
5. Remain feasible on PostgreSQL and SQL Server with the same logical algorithms (engine-specific SQL where necessary).

## Non-goals

- Define the entire read path and query semantics (use baseline documents for flattening/reconstitution and query execution).
- Remove `ReferentialId`/`dms.ReferentialIdentity` (see `reference/design/backend-redesign/alternatives/the-problem-with-removing-referentialids.md` for write-path costs).

## Core concepts

### 1) Two dependency types

For a parent document that references a child document:

1. **Identity component reference**
   - Child’s projected identity values participate in Parent’s identity (`identityJsonPaths`).
   - If those projected values change, Parent’s `ReferentialId` must change.

2. **Non-identity representation dependency**
   - Child’s projected identity values appear in Parent’s API representation (as a `{...}Reference` object), but do not participate in Parent’s identity.
   - If they change, Parent’s representation metadata must change, but Parent’s `ReferentialId` does not.

### 2) One propagation mechanism, two effects

This design uses the same propagation mechanism for both dependency types:

- **Full natural-key propagation**: for every document reference FK, store the referenced identity values locally and enforce a composite FK with `ON UPDATE CASCADE`.

Then, per-resource triggers determine the downstream effect:

- If the updated columns include the resource’s identity projection columns, recompute the resource’s `ReferentialId` (`dms.ReferentialIdentity`) and stamp both identity + representation metadata.
- If the updated columns are only non-identity reference identity columns (or other representation-affecting columns), stamp representation metadata only.

There is no separate “touch cascade” and no reverse-index needed to find referrers: the database updates the referrers directly.

## Data model changes

### A) Reference-site identity columns + composite cascading FKs (all document references)

For each **document reference site** (derived from ApiSchema), regardless of whether it contributes to the parent identity:

1. Keep the stable FK:
   - `<RefBaseName>_DocumentId bigint NULL|NOT NULL` (as in the baseline redesign).
2. Materialize the referenced identity value columns that appear in the reference object:
   - one column per referenced identity field (e.g., `StudentUniqueId`, `SchoolId`, etc.), prefixed by reference base name.
3. Add a unique constraint on the referenced target table to support a composite FK:
   - `UNIQUE (DocumentId, <TargetIdentityCols...>)`
4. Add a composite FK on the referencing table:
   - `FOREIGN KEY (<RefBaseName>_DocumentId, <RefBaseName>_<TargetIdentityCols...>)`
     `REFERENCES <TargetTable>(DocumentId, <TargetIdentityCols...>) ON UPDATE CASCADE`

Result:
- when the referenced identity value changes, the DB rewrites the corresponding identity value columns on dependent rows (root or child tables), even when the reference is non-identity.
- those rewrites are normal updates on the dependent rows, enabling normal version-stamping triggers to update `_etag/_lastModifiedDate/ChangeVersion`.

#### Nullability and “all-or-none” safety

Composite FKs are typically considered satisfied if any referencing column is `NULL`. To avoid “half-present” reference shapes (e.g., `..._DocumentId` present but some identity columns null), add a generated check constraint per reference site:

- If `<RefBaseName>_DocumentId IS NULL` then all `<RefBaseName>_<IdentityPart>` are `NULL`.
- If `<RefBaseName>_DocumentId IS NOT NULL` then all `<RefBaseName>_<IdentityPart>` are `NOT NULL`.

This makes reference rows structurally consistent and prevents write-path bugs from silently bypassing FK enforcement.

#### Write-path compatibility (flattening + minimal round-trips)

Including propagated identity columns remains compatible with the baseline “flatten then insert” write path as long as the incoming API payload continues to carry reference identity fields (normal Ed-Fi `{...}Reference` objects).

- The flattener can set both:
  - `<RefBaseName>_DocumentId` (from the existing bulk `ReferentialId → DocumentId` resolution), and
  - `<RefBaseName>_<IdentityPart>` columns (directly from the request JSON at the reference-object paths),
  without additional database reads.

#### Column naming convention (avoid collisions)

Extend the baseline naming rules in `reference/design/backend-redesign/data-model.md` (Naming Rules → Column names) for the new propagated identity columns:

- For each propagated identity value sourced from a reference object, use:
  - `{ReferenceBaseName}_{IdentityFieldBaseName}` (PascalCase components; underscore separator to match existing `{ReferenceBaseName}_DocumentId` convention).
  - Example: `Student_DocumentId` + `Student_StudentUniqueId`; `School_DocumentId` + `School_SchoolId`.
- Use `resourceSchema.relational.nameOverrides` on full JSON paths (e.g., `$.studentReference.studentUniqueId`) to resolve remaining collisions, and apply the baseline truncation+hash rule when dialect identifier limits are exceeded.

#### Query compilation benefit (reference identity fields without subqueries)

The baseline query compilation behavior for reference identity fields is described in `reference/design/backend-redesign/flattening-reconstitution.md` (lines 513–538): because the referencing table has only `..._DocumentId`, a query on `$.studentReference.studentUniqueId` must compile to a subquery over the referenced table to produce matching `DocumentId`s.

With propagated identity columns for **all** reference sites, `ApiSchema.queryFieldMapping` entries that point into reference objects can compile to local predicates, e.g.:

- `WHERE r.Student_StudentUniqueId = @StudentUniqueId`

instead of:

- `WHERE r.Student_DocumentId IN (SELECT s.DocumentId FROM edfi.Student s WHERE s.StudentUniqueId = @StudentUniqueId)`

This also naturally supports **partial referenced identities**: if the client supplies only some identity parts, the query becomes a predicate on the supplied propagated columns.

### B) Abstract/polymorphic reference targets require “abstract identity” tables

`ON UPDATE CASCADE` requires a concrete target table with the required identity columns.

For abstract targets (e.g., `EducationOrganizationReference`), introduce an **identity table per abstract resource**:

- One row per concrete member document
- Columns: `DocumentId` + abstract identity fields + discriminator (optional but useful)
- Maintained by triggers on each concrete member root table

Referencing tables then use composite FKs to the abstract identity table with `ON UPDATE CASCADE`.

This approach is the same as in `nk-plus-touch-cleaned.md`, but applies to **all** abstract reference sites (not only those used in identities).

#### Example: abstract identity table + maintenance triggers (`EducationOrganization`)

**PostgreSQL (sketch)**

```sql
CREATE TABLE edfi.EducationOrganizationIdentity (
    DocumentId bigint NOT NULL PRIMARY KEY
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    EducationOrganizationId integer NOT NULL,
    Discriminator varchar(256) NOT NULL,
    CONSTRAINT UX_EdOrgIdentity_DocumentId_EdOrgId UNIQUE (DocumentId, EducationOrganizationId),
    CONSTRAINT UX_EdOrgIdentity_EdOrgId UNIQUE (EducationOrganizationId)
);

CREATE OR REPLACE FUNCTION edfi.trg_school_edorg_identity_upsert()
RETURNS trigger AS
$$
BEGIN
  INSERT INTO edfi.EducationOrganizationIdentity (DocumentId, EducationOrganizationId, Discriminator)
  VALUES (NEW.DocumentId, NEW.SchoolId, 'School')
  ON CONFLICT (DocumentId) DO UPDATE
    SET EducationOrganizationId = EXCLUDED.EducationOrganizationId,
        Discriminator = EXCLUDED.Discriminator;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER TR_School_EdOrgIdentity_Upsert
AFTER INSERT OR UPDATE OF SchoolId ON edfi.School
FOR EACH ROW
EXECUTE FUNCTION edfi.trg_school_edorg_identity_upsert();
```

**SQL Server (sketch)**

```sql
CREATE TABLE edfi.EducationOrganizationIdentity (
    DocumentId bigint NOT NULL,
    EducationOrganizationId int NOT NULL,
    Discriminator nvarchar(256) NOT NULL,
    CONSTRAINT PK_EdOrgIdentity PRIMARY KEY CLUSTERED (DocumentId),
    CONSTRAINT FK_EdOrgIdentity_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT UX_EdOrgIdentity_DocumentId_EdOrgId UNIQUE (DocumentId, EducationOrganizationId),
    CONSTRAINT UX_EdOrgIdentity_EdOrgId UNIQUE (EducationOrganizationId)
);
GO

CREATE OR ALTER TRIGGER edfi.TR_School_EdOrgIdentity_Upsert
ON edfi.School
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Update existing rows
    UPDATE t
    SET
        t.EducationOrganizationId = i.SchoolId,
        t.Discriminator = N'School'
    FROM edfi.EducationOrganizationIdentity t
    JOIN inserted i ON i.DocumentId = t.DocumentId;

    -- Insert missing rows
    INSERT INTO edfi.EducationOrganizationIdentity (DocumentId, EducationOrganizationId, Discriminator)
    SELECT i.DocumentId, i.SchoolId, N'School'
    FROM inserted i
    WHERE NOT EXISTS (
        SELECT 1 FROM edfi.EducationOrganizationIdentity t WHERE t.DocumentId = i.DocumentId
    );
END;
GO
```

### C) Stored representation metadata on `dms.Document` (served by the API)

This alternative serves representation metadata directly from persisted columns and updates them in-transaction:

- `_lastModifiedDate` is read from a stored timestamp.
- `ChangeVersion` is read from a stored monotonic stamp.
- `_etag` is derived from that stamp (or stored alongside it).

To minimize schema width and align with the existing token columns, repurpose the baseline token meanings:

- `dms.Document.ContentVersion` / `ContentLastModifiedAt` become **representation** version/timestamp:
  - bump on local content changes (root/child/extension tables)
  - bump on local identity projection changes
  - bump on cascaded updates to stored reference identity columns (this design’s replacement for touch)
- `dms.Document.IdentityVersion` / `IdentityLastModifiedAt` remain **identity projection** change signals:
  - bump only when the document’s own identity projection changes (directly or via identity-component propagation)

Because indirect impacts are materialized as local row updates (via FK cascades), Change Query journaling can remain based on representation stamps (`ContentVersion`) the same way as in `nk-plus-touch-cleaned.md`.

#### Example: `dms.DocumentChangeEvent` journal triggers (on `dms.Document`)

**PostgreSQL (sketch)**

```sql
CREATE OR REPLACE FUNCTION dms.trg_document_change_event_ins()
RETURNS trigger AS
$$
BEGIN
  INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId)
  SELECT ContentVersion, DocumentId, ResourceKeyId
  FROM inserted;

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION dms.trg_document_change_event_upd()
RETURNS trigger AS
$$
BEGIN
  INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId)
  SELECT i.ContentVersion, i.DocumentId, i.ResourceKeyId
  FROM inserted i
  JOIN deleted d ON d.DocumentId = i.DocumentId
  WHERE i.ContentVersion <> d.ContentVersion;

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS TR_DocumentChangeEvent_Insert ON dms.Document;
CREATE TRIGGER TR_DocumentChangeEvent_Insert
AFTER INSERT ON dms.Document
REFERENCING NEW TABLE AS inserted
FOR EACH STATEMENT
EXECUTE FUNCTION dms.trg_document_change_event_ins();

DROP TRIGGER IF EXISTS TR_DocumentChangeEvent_Update ON dms.Document;
CREATE TRIGGER TR_DocumentChangeEvent_Update
AFTER UPDATE ON dms.Document
REFERENCING NEW TABLE AS inserted OLD TABLE AS deleted
FOR EACH STATEMENT
EXECUTE FUNCTION dms.trg_document_change_event_upd();
```

**SQL Server (sketch)**

```sql
CREATE OR ALTER TRIGGER dms.TR_DocumentChangeEvent
ON dms.Document
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId)
    SELECT i.ContentVersion, i.DocumentId, i.ResourceKeyId
    FROM inserted i
    LEFT JOIN deleted d ON d.DocumentId = i.DocumentId
    WHERE d.DocumentId IS NULL
       OR i.ContentVersion <> d.ContentVersion;
END;
```

### D) Baseline tables that can be removed

This alternative changes the baseline cascade and update-tracking mechanisms such that some baseline design core tables are no longer required:

- `dms.IdentityLock` (baseline: `reference/design/backend-redesign/transactions-and-concurrency.md`)
  - Baseline purpose: phantom-safe identity-closure locking for application-managed `dms.ReferentialIdentity` recompute and dependency-stable read-time algorithms.
  - Alternative: identity correctness is maintained by DB cascades + row-local triggers; optimistic concurrency uses stored representation metadata. There is no application-managed identity-closure traversal to lock.

- `dms.ReferenceEdge` (baseline: `reference/design/backend-redesign/data-model.md`)
  - Baseline purpose: reverse lookups for dependency enumeration and (in some designs) indirect-update handling.
  - Alternative: indirect-update effects are realized as FK-cascade updates on the referrers’ stored reference identity columns, so there is no need for a reverse index just to “touch” referrers.

- `dms.IdentityChangeEvent` (baseline: `reference/design/backend-redesign/update-tracking.md` / `reference/design/backend-redesign/data-model.md`)
  - Baseline purpose: expand indirectly impacted parents during Change Query selection (child identity changes + reverse lookups).
  - Alternative: indirect impacts become local representation-stamp updates on the impacted documents, so Change Query selection does not need a separate “identity changed” journal.

`dms.DocumentChangeEvent` is not strictly required, but is still recommended:
- If you keep it, Change Query window scans remain narrow and index-friendly.
- If you drop it, Change Query selection must instead query/index `dms.Document` directly on `(ResourceKeyId, ContentVersion)` (or the chosen representation stamp), which can add hot, high-churn indexing pressure to `dms.Document`.

## Identity maintenance: DB triggers (row-local) + natural-key propagation

### Referential id recompute (row-local triggers)

Per resource root table `{schema}.{R}`:

- `AFTER INSERT`: compute and insert the primary referential id row in `dms.ReferentialIdentity`
- `AFTER UPDATE` (identity columns changed): recompute and replace the row(s)
- `AFTER DELETE`: delete the row(s) (or rely on `ON DELETE CASCADE` via `DocumentId`)

The trigger must match Core’s referential-id computation exactly:

- `ResourceInfoString = ProjectName + ResourceName`
- `DocumentIdentityString = join("#", "$" + IdentityJsonPath + "=" + IdentityValue)` (ordered by `identityJsonPaths`)
- `ReferentialId = UUIDv5(namespace, ResourceInfoString + DocumentIdentityString)`

Subclass/superclass alias rows (polymorphic identity behavior) are maintained the same way (up to two rows per document).

### Representation stamping (including cascaded reference identity updates)

To replace the “touch cascade” behavior from `nk-plus-touch-cleaned.md`, this design relies on the fact that FK cascades produce normal updates on the referencing tables.

Therefore, stamping triggers must treat the propagated reference identity columns as representation-affecting:

- Any update to propagated reference identity columns on a row within a resource’s scope must bump the resource’s `dms.Document.ContentVersion/ContentLastModifiedAt`.
- Only updates to the resource’s own identity projection must bump `dms.Document.IdentityVersion/IdentityLastModifiedAt` and recompute `dms.ReferentialIdentity`.

SQL Server note (multi-row statements):
- Because cascades can update many rows in one statement, stamping must produce **unique per-row** `ContentVersion` values (ODS compatibility for watermark-only clients).
- Options:
  - call `NEXT VALUE FOR dms.ChangeVersionSequence` per row (e.g., via `CROSS APPLY`), or
  - allocate a range via `sys.sp_sequence_get_range` and assign deterministically by `ROW_NUMBER()` (as in the touch-cascade sketch in `nk-plus-touch-cleaned.md`).

## Concurrency and operational hardening

This alternative makes indirect-update fan-out synchronous and write-amplifying. Compared to `nk-plus-touch-cleaned.md`, it removes the extra work of:
- reverse-index (`dms.ReferenceEdge`) maintenance, and
- explicit touch targeting and stamping.

However, it does not remove the fundamental operational risk: a single identity update can still affect many dependent rows, now via FK cascades.

### Locking model (why no special lock table)

This alternative does **not** use a baseline-style “special” lock protocol (e.g., `dms.IdentityLock`) because it removes the baseline’s main reason for it: application-managed impacted-set discovery that must be phantom-safe.

Instead:

- Correctness is enforced by the database via composite foreign keys and `ON UPDATE CASCADE` (or generated trigger-based propagation where required by engine limitations).
- Derived artifacts are updated in the same transaction via normal trigger statements:
  - `dms.ReferentialIdentity` upserts take locks on the affected index entries/rows.
  - `dms.Document` version stamps take row locks on the stamped document row(s).

This does **not** eliminate deadlocks or contention risk. It means this design relies on the database to provide correctness under concurrency, while the system still needs operational hardening (ordering, timeouts, retries, and guardrails) to handle worst-case fan-out.

### Database isolation defaults

- SQL Server: strongly recommend MVCC reads (`READ_COMMITTED_SNAPSHOT ON`, optionally `ALLOW_SNAPSHOT_ISOLATION ON`) to reduce deadlocks and blocking caused by readers during concurrent writes.
- PostgreSQL: default MVCC is sufficient.

### Lock ordering rules (deadlock minimization)

- Any operation updating multiple `dms.Document` rows should lock/update in ascending `DocumentId` order.
- Where trigger-based propagation is used (instead of declarative cascades), apply updates in ascending key order to minimize deadlock cycles.

### Guardrail: cap synchronous cascade fan-out (optional)

Unlike an explicit touch procedure, declarative FK cascades do not naturally expose “how many rows will be updated” before doing the work.

If fan-out must be bounded, enforce identity-changing writes through an explicit stored procedure that:

1. materializes the set of would-be updated dependent rows by querying indexed FK columns (or a generated projection view), and
2. fails closed if the count exceeds a configured threshold, before applying the identity update.

This is optional but recommended for operational safety in environments where identity changes are possible.

### Retry + backpressure (application contract)

The API layer should implement bounded retries with jittered exponential backoff for:
- SQL Server deadlock victim (`1205`)
- PostgreSQL deadlock (`40P01`)
- (optional) serialization failures if any path uses stronger isolation

### Optional serialization of identity-changing writes

To reduce risk of “two large cascades overlap and deadlock each other”, optionally serialize only identity-changing writes using:

- PostgreSQL: `pg_advisory_xact_lock(...)`
- SQL Server: `sp_getapplock` (exclusive, transaction-owned)

## Required operational tooling

1. **Migration/backfill**: procedures/jobs to populate the new propagated reference identity columns for existing data, then enable composite FKs and constraints.
2. **Audit/verify**: integrity checks that sample reference sites and validate:
   - the propagated identity columns match the referenced identity for the pointed `..._DocumentId` (should be guaranteed once composite FKs are enabled),
   - representation stamps change appropriately for cascaded updates in representative scenarios.
3. **Telemetry**: metrics for cascade fan-out size, cascade duration, and deadlock retries.

## Correctness invariants (must-hold)

1. For every document reference site, the tuple `(…_DocumentId, …_<IdentityParts...>)` matches a real referenced identity row (enforced by the composite FK).
2. `dms.ReferentialIdentity` is never stale after commit (row-local triggers + natural-key propagation must fully converge in the same transaction).
3. Every representation change (local or indirect via cascaded reference identity updates) bumps stored representation metadata (`ContentVersion`/timestamp), with per-row unique stamps for multi-row cascades.

## Open questions

1. **SQL Server feasibility**: do cascade path/cycle restrictions require generated trigger-based propagation for some reference sites instead of `ON UPDATE CASCADE`?
2. **Schema width/indexing**: does materializing reference identity columns for *all* reference sites create unacceptable schema bloat or index pressure for some resources?
3. **Selective propagation**: should the generator allow opting out of propagation for reference sites that are never queried and where indirect-update semantics are not required (or are acceptable to approximate)?
4. **UUIDv5 implementation**: same question as `nk-plus-touch-cleaned.md`—engine dependency vs custom SQL implementation and cross-engine equivalence testing.
