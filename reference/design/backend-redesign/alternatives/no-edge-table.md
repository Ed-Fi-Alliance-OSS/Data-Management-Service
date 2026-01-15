# Backend Redesign Alternative: Touch Cascades Without `dms.ReferenceEdge` (Projection-Based Referrer Discovery)

## Status

Draft (alternative design exploration).

## Context

The baseline backend redesign uses a persisted reverse index (`dms.ReferenceEdge`) for reverse lookups and dependency enumeration (see `reference/design/backend-redesign/data-model.md` and `reference/design/backend-redesign/update-tracking.md`).

This document explores an alternative way to determine “touch edges” (referrers) **without an edge table**, motivated by:

- **Out-of-band DML tolerance**: if writes occur outside the DMS write path (manual SQL, ETL, admin scripts), a persisted edge table can drift unless every write also updates it.
- **Simplicity of correctness** for referrer discovery: the relational FK graph is the source-of-truth, so derive referrers directly from it.

This alternative is specifically about *referrer discovery for touch cascades* (“who references this changed document?”). It does not require nor depend on `dms.ReferenceEdge`.

## Goals

1. Determine touch targets from the authoritative FK graph (root + child tables) without a persisted adjacency list.
2. Support ODS-like “indirect update” semantics for stored representation metadata: when a referenced document’s identity projection changes, referrers’ representation metadata is updated in-transaction.
3. Provide a viable path for environments that may do out-of-band DML (within limits; see “Out-of-band DML considerations”).
4. Remain implementable on PostgreSQL and SQL Server with generated, deterministic DDL.

## Non-goals

- Replace the baseline redesign’s read-time derived `_etag/_lastModifiedDate/ChangeVersion` approach (`reference/design/backend-redesign/update-tracking.md`).
- Define how identity correctness (`dms.ReferentialIdentity`) is maintained; this document only covers touch targeting.

---

## Design A: Global Projection (`dms.AllDocumentReferences`)

### Section 1/3 — Generated global reverse-reference projection

Generate a single database object that projects all document reference FK sites as `(ParentDocumentId, ChildDocumentId)` rows.

#### Shape

`dms.AllDocumentReferences` yields:

- `ParentDocumentId bigint` — the aggregate root’s `DocumentId`
- `ChildDocumentId bigint` — the referenced document’s `DocumentId` (from a `..._DocumentId` FK site)
- `IsIdentityComponent bit/boolean` — constant per FK site, derived from ApiSchema (“this reference site contributes to the parent identity”)

Descriptor FK sites (`..._DescriptorId`) are excluded.

#### Generation rule

For every derived table in the relational model (resource root tables and child/collection tables):

1. Determine the root-document id column for that table’s scope:
   - root tables: `DocumentId`
   - child tables: the root `..._DocumentId` key part column (per `reference/design/backend-redesign/flattening-reconstitution.md`)
2. For every **document-reference** FK column in the table (`..._DocumentId`):
   - emit a `SELECT` that projects:
     - the root-document id column as `ParentDocumentId`
     - the FK column as `ChildDocumentId`
     - a constant `IsIdentityComponent`
   - filter out null FK values (`WHERE <FkCol> IS NOT NULL`)
3. `UNION ALL` all branches into one object.

#### Example (illustrative)

```sql
CREATE VIEW dms.AllDocumentReferences AS
    -- Root table FK sites
    SELECT
        r.DocumentId AS ParentDocumentId,
        r.Student_DocumentId AS ChildDocumentId,
        CAST(1 AS bit) AS IsIdentityComponent
    FROM edfi.StudentSchoolAssociation r
    WHERE r.Student_DocumentId IS NOT NULL
UNION ALL
    -- Child table FK sites (ParentDocumentId is the root DocumentId key part)
    SELECT
        c.School_DocumentId AS ParentDocumentId,
        c.Calendar_DocumentId AS ChildDocumentId,
        CAST(0 AS bit) AS IsIdentityComponent
    FROM edfi.SchoolCalendar c
    WHERE c.Calendar_DocumentId IS NOT NULL;
```

#### Engine notes

- **SQL Server**: do not rely on an indexed view here; indexed views have restrictive rules and do not combine well with a massive multi-table `UNION ALL`. Plan to index the underlying FK columns instead (see “Indexing expectations”).
- **PostgreSQL**: a plain view is fine; a materialized view would effectively reintroduce an edge table (and its drift/refresh concerns).

---

## Touch Cascade Trigger Using the Projection

### Section 2/3 — Touch targets from the projection (no edge table)

Touch cascades trigger when a document’s **identity projection** changes. The simplest signal is `dms.Document.IdentityVersion` changing.

On that event:

1. Compute `changed_children` = document ids whose `IdentityVersion` changed in the statement.
2. Compute `touch_targets` by joining `changed_children` to `dms.AllDocumentReferences` on `ChildDocumentId`.
3. Apply the **no-double-touch** rule (optional but common when identity-bearing referrers are already locally updated by another mechanism):
   - Touch only parents that reference the changed children *only via non-identity sites*.
   - Operationally: `GROUP BY ParentDocumentId HAVING MAX(IsIdentityComponent) = 0`.
4. Update stored representation metadata on the touched parents (e.g., bump `ContentVersion` / `_lastModifiedDate`), allocating **one unique version per touched row** if strict ODS ChangeVersion compatibility is required.

#### “No double touch” (optional)

The `IsIdentityComponent` column exists to support:

- Excluding identity-component referrers from touch because they are already locally updated by another mechanism (e.g., identity-only natural-key propagation that rewrites identity columns on the parent and causes local stamping).

If your overall design does *not* have a separate mechanism that locally updates identity-component referrers, you should drop the exclusion and touch **all** referrers (i.e., ignore `IsIdentityComponent`).

#### Touch target query (conceptual)

```sql
touch_targets =
  SELECT r.ParentDocumentId
  FROM dms.AllDocumentReferences r
  JOIN changed_children c ON c.DocumentId = r.ChildDocumentId
  GROUP BY r.ParentDocumentId
  HAVING MAX(CAST(r.IsIdentityComponent AS int)) = 0;
```

Note: “touch” must not cascade recursively. Touch updates representation stamps (`ContentVersion`/timestamp), not `IdentityVersion`, so the trigger only fires for identity-version changes.

---

## Indexing Expectations

### Section 3/3 — Index policy to make projection queries viable

Because reverse lookups are now computed by scanning many FK sites, the underlying tables must support “find parents by child id” efficiently.

**Minimum requirement**
- Every document-reference FK column `X_DocumentId` needs an index that supports `WHERE X_DocumentId IN (@changed_children)` or a join on that column.

Baseline alignment:
- The baseline DDL generator’s “supporting index for every FK” policy (`reference/design/backend-redesign/ddl-generation.md`) is close to this, but you should verify it covers all `..._DocumentId` columns with the correct leading key order.

**Recommended (covering)**
- For each FK site, index the child-id column and include the parent-id projection column:
  - SQL Server: `IX_{Table}_{ChildFkCol} INCLUDE ({RootDocumentIdCol})`
  - PostgreSQL: an index on `{ChildFkCol}` is usually sufficient; consider including the parent-id column with `INCLUDE` if you expect heavy touch traffic and want fewer heap fetches.

**Optional (filtered/partial)**
- Because most FK columns are sparse, consider:
  - PostgreSQL partial indexes: `WHERE <FkCol> IS NOT NULL`
  - SQL Server filtered indexes: `WHERE <FkCol> IS NOT NULL`
  where operationally justified.

**Trigger-side materialization**
- For large `changed_children` sets (identity cascades), consider materializing and deduping the set into a temp table with a primary key for efficient joins:
  - SQL Server: `#changed_children(DocumentId PRIMARY KEY)`
  - PostgreSQL: a `WITH changed_children AS (...)` CTE is usually sufficient; avoid heavy temp table usage inside triggers unless necessary.

---

## Out-of-band DML considerations

This design improves **referrer discovery correctness** under out-of-band DML because:
- `dms.AllDocumentReferences` is derived from the source-of-truth FK columns, so it cannot drift.

However, out-of-band DML can still violate “stored representation metadata is accurate” unless you enforce one of the following:

1. **DB-level stamping triggers on resource tables are required**
   - Any out-of-band DML must still execute triggers that bump representation stamps for the modified parent documents (local changes).
2. **Or treat out-of-band DML as unsupported**
   - If operators can disable triggers, bulk-load with minimal logging/constraints, or manipulate rows without stamping, stored metadata correctness cannot be guaranteed in any write-time-touch design.
3. **Or provide audit/repair jobs**
   - Periodically recompute/repair representation stamps (and/or re-run touch) based on detected drift.

In short: this approach makes the **graph** accurate under out-of-band DML; it does not automatically make **stored metadata** correct unless the stamping/touch mechanisms also execute.

---

## Pros

- **No edge drift**: referrer discovery always reflects the actual FK graph, including out-of-band DML.
- **No edge maintenance cost on normal writes**: you avoid the “recompute edges per write” tax entirely.
- **Fewer correctness cliffs**: no “every write must call recompute” orchestration contract.
- **Simpler recovery for referrer discovery**: no edge rebuild tooling required just to answer “who references X?”.

## Cons

- **Optimizer/plan size risk**: a very large `UNION ALL` projection can be expensive to compile/plan and can behave unpredictably across engines and schema sizes.
- **Execution cost scales with schema breadth**: identity changes may pay “touch lookup across many FK sites”, even if the changed child has few referrers.
- **Index sprawl pressure**: making touch lookups fast often implies (nearly) one good index per FK site, sometimes with INCLUDE columns.
- **Does not remove fan-out risk**: touching 10k/100k+ parents is still operationally dangerous; this only changes how you find them.

## Variants worth considering

1. **Two projections**
   - `dms.AllDocumentReferences` (all sites)
   - `dms.IdentityComponentReferences` (identity sites only)
   - Can simplify “no double touch” and reduce grouping work.

2. **Per-target projections**
   - Replace the single global view with many smaller “referrers of target T” objects.
   - Covered in detail below.

3. **Procedural referrer discovery (SQL Server)**
   - Instead of a view, generate stored procedures per target or per resource that join only the relevant FK sites to a temp table/TVP of changed children.
   - Often easier to tune than a monolithic global view, but adds more generated objects and orchestration complexity.

---

## Design B: Per-Target Projections (Modular Reverse Lookup)

### Motivation

Per-target projections aim to keep each reverse-lookup object small and predictable:

- smaller view definitions
- fewer UNION branches per query
- more stable plans

This is primarily a mitigation for the “big global view” risks.

### What gets generated

For each *target bucket* `T`, generate a projection:

- `dms.Referrers_{T}` returning:
  - `ParentDocumentId`
  - `ChildDocumentId`
  - `IsIdentityComponent`

`T` is typically one of:

1. A concrete resource root table target (e.g., all FK sites that reference `edfi.School(DocumentId)`).
2. A polymorphic bucket for FK sites that reference `dms.Document(DocumentId)` (abstract targets).

Each `dms.Referrers_{T}` is a `UNION ALL` across only the FK columns that reference that target.

### Polymorphic/abstract reference targets

Any FK sites that reference `dms.Document(DocumentId)` can point to documents of many resource types.

Therefore, a per-target touch implementation must:
- always include the polymorphic bucket `dms.Referrers_DmsDocument` (or equivalent) when computing touch targets, because any changed child might be referenced via an abstract target site.

If the derived model produces **no** FK sites to `dms.Document`, this bucket can be omitted.

---

## Touch trigger dispatch options for per-target projections

The trigger still starts from `changed_children` (documents whose `IdentityVersion` changed) and must discover all referrers.

The key decision is how the trigger chooses which `dms.Referrers_{T}` objects to query.

### Option B1: Static trigger (no dynamic SQL)

#### What it looks like

Generate trigger logic that references the per-target projections without dynamic SQL. Two common patterns:

1. **Single static query** that unions all per-target referrer projections (still potentially large, but each projection is separately compiled/maintained as its own view).
2. **Static branching**: for each target `T`, conditionally execute the corresponding query block if needed (e.g., when there exists at least one changed child that could be referenced via `T`).

A typical implementation uses:
- a temp table of `changed_children` (deduped),
- a temp table for accumulated candidate parents with an aggregated “has identity edge” marker,
- then one final `UPDATE dms.Document` for the touch targets.

#### Pros

- **Operationally simpler than dynamic SQL**: fewer permissions/quoting pitfalls; easier debugging.
- **Better plan reuse**: each per-target view/query can have stable cached plans.
- **Safer in restricted environments**: avoids reliance on dynamic execution within triggers.

#### Cons

- **Compilation still scales with schema size**: if the trigger body references many targets, you can still end up with large trigger code or large static unions.
- **Harder to keep minimal**: without dynamic dispatch, you often pay per-target overhead even when only a few child types change.

#### Example (Data Standard 5.2 authoritative ApiSchema)

Using `ds-5.2-api-schema-authoritative.json`:

- `projectSchema.resourceSchemas` contains **349** resources.
- A static dispatcher typically generates ~**349** `IF EXISTS` blocks so it can handle *any* `ResourceKeyId` that appears in `changed_children`.

Illustrative sample as a **SQL Server stored procedure** (10 non-overlapping targets shown; a real generated procedure would contain one block per resource `ResourceKeyId` in the effective schema):

```sql
CREATE OR ALTER PROCEDURE dms.ApplyTouchCascade_Static
    @MaxTouchParents int
AS
BEGIN
    SET NOCOUNT ON;

    --   The dms.Document trigger materializes changed children for this statement as:
    --   #changed_children(DocumentId bigint NOT NULL PRIMARY KEY, ResourceKeyId smallint NOT NULL)

    DROP TABLE IF EXISTS #referrers;
    CREATE TABLE #referrers (
        ParentDocumentId bigint NOT NULL,
        ChildDocumentId bigint NOT NULL,
        IsIdentityComponent bit NOT NULL
    );

    -- Static dispatch (sample only; generated code repeats this pattern for every ResourceKeyId).
    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_ClassPeriod)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_ClassPeriod r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_ClassPeriod AND c.DocumentId = r.ChildDocumentId;

    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_Section)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_Section r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_Section AND c.DocumentId = r.ChildDocumentId;

    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_BellSchedule)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_BellSchedule r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_BellSchedule AND c.DocumentId = r.ChildDocumentId;

    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_CourseOffering)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_CourseOffering r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_CourseOffering AND c.DocumentId = r.ChildDocumentId;

    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_Assessment)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_Assessment r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_Assessment AND c.DocumentId = r.ChildDocumentId;

    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_GradebookEntry)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_GradebookEntry r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_GradebookEntry AND c.DocumentId = r.ChildDocumentId;

    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_Student)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_Student r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_Student AND c.DocumentId = r.ChildDocumentId;

    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_School)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_School r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_School AND c.DocumentId = r.ChildDocumentId;

    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_Program)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_Program r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_Program AND c.DocumentId = r.ChildDocumentId;

    IF EXISTS (SELECT 1 FROM #changed_children WHERE ResourceKeyId = @RK_Location)
        INSERT INTO #referrers (ParentDocumentId, ChildDocumentId, IsIdentityComponent)
        SELECT r.ParentDocumentId, r.ChildDocumentId, r.IsIdentityComponent
        FROM dms.Referrers_RK_Location r
        JOIN #changed_children c ON c.ResourceKeyId = @RK_Location AND c.DocumentId = r.ChildDocumentId;

    -- Remaining steps to update dms.Document not shown
END;
```

### Option B2: Dynamic SQL trigger (union only the needed targets)

#### What it looks like

At runtime:

1. Determine the set of targets needed for this statement:
   - always include the polymorphic bucket (if present),
   - include only concrete targets that are relevant to the changed children (based on their resource type/table mapping).
2. Build a dynamic SQL statement that unions only those `dms.Referrers_{T}` objects and joins them to `changed_children`.
3. Execute the statement within the trigger transaction to get `touch_targets`, then perform the touch update.

#### Pros

- **Work scales with what changed**: when only a few child resource types change, the trigger queries only a few projections.
- **Reduced planning/compile overhead** compared to “always union everything”.
- **More predictable hotspots**: you can focus tuning on the handful of targets that dominate real workloads.

#### Cons

- **Complexity + operational risk**: dynamic SQL in triggers is harder to test, troubleshoot, and secure.
- **Plan cache fragmentation**: query text varies by which targets appear, reducing plan reuse.
- **Permissions/quoting pitfalls**:
  - must carefully quote generated object names,
  - must ensure the trigger execution context can execute the dynamic statement across all referenced schemas.

#### Guardrails (recommended if using dynamic)

- The set of permissible targets must come from generator-controlled metadata (not user input).
- Enforce bounds (e.g., max number of targets to union in one statement) as a secondary guardrail to avoid pathological “compile a hundred-view union” scenarios.

#### Example (Data Standard 5.2 authoritative ApiSchema)

Using `ds-5.2-api-schema-authoritative.json`:

If an identity update to **`Section`** stamps `dms.Document.IdentityVersion` for the entire impacted set in a **single statement**, `changed_children` could include these six resource types (one `ResourceKeyId` per type):

- `Section`
- `SectionAttendanceTakenEvent`
- `StaffSectionAssociation`
- `StudentSectionAssociation`
- `StudentSectionAttendanceEvent`
- `SurveySectionAssociation`

Under B2, the dispatcher builds SQL that unions only those per-target projections (plus the polymorphic bucket if present), rather than carrying 349 static branches. Conceptual SQL shape:

```sql
-- Always include polymorphic bucket if there are FK sites to dms.Document
SELECT ... FROM dms.Referrers_DmsDocument r JOIN #changed_children c ON c.DocumentId = r.ChildDocumentId
UNION ALL
SELECT ... FROM dms.Referrers_Section r                      JOIN #changed_children c ON c.ResourceKeyId=@RK_Section                       AND c.DocumentId=r.ChildDocumentId
UNION ALL
SELECT ... FROM dms.Referrers_SectionAttendanceTakenEvent r   JOIN #changed_children c ON c.ResourceKeyId=@RK_SectionAttendanceTakenEvent   AND c.DocumentId=r.ChildDocumentId
UNION ALL
SELECT ... FROM dms.Referrers_StaffSectionAssociation r       JOIN #changed_children c ON c.ResourceKeyId=@RK_StaffSectionAssociation       AND c.DocumentId=r.ChildDocumentId
UNION ALL
SELECT ... FROM dms.Referrers_StudentSectionAssociation r     JOIN #changed_children c ON c.ResourceKeyId=@RK_StudentSectionAssociation     AND c.DocumentId=r.ChildDocumentId
UNION ALL
SELECT ... FROM dms.Referrers_StudentSectionAttendanceEvent r JOIN #changed_children c ON c.ResourceKeyId=@RK_StudentSectionAttendanceEvent AND c.DocumentId=r.ChildDocumentId
UNION ALL
SELECT ... FROM dms.Referrers_SurveySectionAssociation r      JOIN #changed_children c ON c.ResourceKeyId=@RK_SurveySectionAssociation      AND c.DocumentId=r.ChildDocumentId;
```

---

## Recommended solution (B2)

For a “no edge table” design that still scales beyond small schemas and tolerates out-of-band DML for referrer discovery, the recommended approach is:

1. **Per-target projections** (Design B), not one global `dms.AllDocumentReferences` view.
2. A dedicated **polymorphic bucket** projection for abstract/polymorphic FK sites (those targeting `dms.Document(DocumentId)`), which is always included in touch targeting.
3. **Dynamic dispatch in a stored routine** (Option B2), invoked by the touch trigger, so each identity-change statement unions only the projections needed for the changed child resource types.

### Why this is recommended

- **Avoids edge drift**: referrer discovery reflects the authoritative FK graph, so out-of-band DML that changes `..._DocumentId` values is automatically “seen”.
- **Avoids the global-union risks**: you do not pay the planning/compile cost of a single massive multi-table `UNION ALL` view on every touch.
- **Work scales with what changed**: typical identity changes touch a small number of resource types; dispatch unions only the relevant projections plus the (usually small) polymorphic bucket.
- **Keeps polymorphism bounded**: because polymorphic FK sites are “not common” (per current assumptions), the polymorphic bucket remains small, and always including it is acceptable.

### Concrete shape (generator contract)

1. **Per-target referrer projections**
   - Emit one projection per concrete target bucket, recommended to key by `ResourceKeyId` (stable, small, aligns with `dms.Document.ResourceKeyId`):
     - name: `dms.Referrers_RK_{ResourceKeyId}`
     - rows: `(ParentDocumentId, ChildDocumentId, IsIdentityComponent)`
     - definition: `UNION ALL` over only the FK sites that reference that concrete target table’s `DocumentId`.
2. **Polymorphic bucket**
   - Emit `dms.Referrers_DmsDocument` containing only FK sites whose constraint target is `dms.Document(DocumentId)`.
3. **Target inventory (optional but recommended)**
   - Emit a small generator-owned lookup (table or view) that enumerates the available projections and their semantics (e.g., `ResourceKeyId → ViewName`), so the dispatch routine never concatenates untrusted identifiers.

### Trigger + dispatch (recommended runtime pattern)

- Keep the trigger body minimal:
  - compute/materialize `changed_children(DocumentId, ResourceKeyId)` for rows whose `IdentityVersion` changed
  - call a generator-owned routine to apply the touch (`dms.ApplyTouchCascade(changed_children, MaxTouchParents, ...)`)
- Implement the “union only needed targets” logic in the routine:
  - always include `dms.Referrers_DmsDocument` (if present)
  - include only `dms.Referrers_RK_{id}` where `id` appears in `changed_children.ResourceKeyId`
  - apply no-double-touch by grouping referrers by `ParentDocumentId` and excluding any parent with an identity-component edge
  - allocate per-row stamps and update `dms.Document` for the final `touch_targets`

Notes:
- On SQL Server, this routine is naturally a stored procedure called from the `dms.Document` trigger (avoids a very large trigger body and centralizes dynamic SQL).
- On PostgreSQL, the trigger function can call a helper procedure/function that performs the dynamic union and update; use `format('%I.%I', ...)`-style quoting and generator-controlled identifiers.

### Important limitation (still applies)

This recommendation addresses “out-of-band DML tolerance” for **referrer discovery**. It does not, by itself, guarantee stored representation metadata stays correct if out-of-band DML modifies resource content/references without executing the stamping/touch mechanisms. For that, the environment must still:
- enforce stamping triggers on resource tables (or prohibit disabling them), and/or
- provide audit/repair jobs for representation metadata.
