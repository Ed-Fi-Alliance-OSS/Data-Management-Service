# Key Unification Issue in Propagated Identity Columns

## Abstract

ApiSchema supports “key unification” via `resourceSchema.equalityConstraints`: the same logical natural-key field can appear in multiple reference objects on the same resource. In the current backend redesign, each reference site is mapped as self-contained by persisting per-site propagated identity columns (`{RefBaseName}_{IdentityPart}`) alongside `..._DocumentId`. This paper explains why key unification becomes a database-integrity problem (especially when identity updates and `ON UPDATE CASCADE` are enabled), and proposes three solutions. It recommends a “canonical physical column + computed/persisted aliases” approach as the best balance of integrity, performance, and compatibility with the redesign.

## Background (what exists today)

- The redesign persists, per reference site:
  - `..._DocumentId`, and
  - `{RefBaseName}_{IdentityPart}` propagated identity columns.
- The DDL generator emits an “all-or-none” CHECK constraint per reference group to prevent null-bypassing of composite foreign keys.
- ApiSchema `equalityConstraints` are currently enforced at request time (see `src/dms/core/EdFi.DataManagementService.Core/Validation/EqualityConstraintValidator.cs`).

This is usually sufficient for API-originated writes, but it does not *fail closed* at the database layer, and it does not address subtle interactions with cascades.

## The problem

### 1) Duplicate stored identity values are inevitable with per-site propagation

If two reference sites carry the same identity part (e.g., `studentUniqueId` appears in multiple reference objects), the derived relational model will contain two physical columns (e.g., `StudentEducationOrganizationAssociation_StudentUniqueId` and `StudentSchoolAssociation_StudentUniqueId`). Without DB-level enforcement, these copies can diverge (through direct SQL writes, bulk operations, bugs, or incomplete propagation).

The legacy Ed-Fi ODS solves this by physically unifying the overlapping key-part into a single stored column shared by multiple FKs (e.g., a single `StudentUSI` column used by multiple FKs on `StudentAssessmentRegistration`).

The ODS also demonstrates an important edge case: optional references that share a unified key-part (e.g., a single `SchoolYear` column participating in multiple reference groups). This is exactly where “all-or-none” nullability becomes tricky if key-parts are physically shared.

### 2) Naive DB enforcement can break (PostgreSQL + cascades)

A tempting “DB guardrail” is a row-local equality CHECK (e.g., `CHECK (colA = colB)`). In PostgreSQL, that pattern is incompatible with `ON UPDATE CASCADE` when the two columns can be updated by different FK cascades:

```sql
CREATE TABLE parent(id int PRIMARY KEY);
CREATE TABLE child(
  a int REFERENCES parent(id) ON UPDATE CASCADE,
  b int REFERENCES parent(id) ON UPDATE CASCADE,
  CONSTRAINT ck_ab CHECK (a = b)
);

INSERT INTO parent VALUES (1), (2);
INSERT INTO child VALUES (1, 1);
UPDATE parent SET id = 3 WHERE id = 1; -- fails: (3,1) mid-cascade
```

PostgreSQL executes cascades as separate UPDATE statements (one per FK). CHECK constraints are enforced immediately after each statement, so the row can temporarily violate the equality check after the first cascade, aborting the whole operation before the second cascade runs.

### 3) SQL Server has different constraints (no deferral + cascade-path limits)

SQL Server does not offer a PostgreSQL-style “defer validation to end-of-transaction” mechanism for constraints/triggers. It also rejects schemas with “multiple cascade paths” (error 1785), which means some cascade graphs we would need cannot be expressed declaratively and require trigger/procedural fallback. Any key-unification enforcement must keep the table consistent after each statement.

## Three solution options

### Option 1: Trigger-based enforcement (dialect-specific)

**PostgreSQL approach: deferred, final-state validation triggers**

- Generate per-table *DEFERRABLE constraint triggers* for row-local equalityConstraints (only when both equality paths bind to columns on the same table).
- Validate against the *final row state* by re-reading the row by primary key at trigger-fire time (do not trust `NEW` if the row may be updated multiple times within a statement/transaction).
- Leave the per-reference-site physical columns intact; triggers are the DB-level guardrail.

**SQL Server approach: set-based propagation triggers (and/or equality checks)**

- Because deferral isn’t available, either:
  - implement deterministic, set-based propagation triggers to update all unified columns in one statement (so equality never transiently fails), and/or
  - enforce equality immediately via CHECK or `AFTER` trigger logic (only safe if updates are guaranteed to keep the row consistent per statement).
- This aligns with the existing need for trigger fallback where `ON UPDATE CASCADE` graphs are illegal (multiple cascade paths).

**Pros**
- Preserves the current relational shape (`{RefBaseName}_{IdentityPart}` per site).
- Can be applied selectively (only same-table equalityConstraints).
- Can provide targeted error messages at the DB boundary.

**Cons**
- Trigger overhead on write paths (especially in PostgreSQL if implemented row-by-row with extra lookups).
- Two implementations to maintain (PostgreSQL vs SQL Server).
- Only covers equalityConstraints that bind to columns on the same table; cross-table equality still needs application enforcement or heavier DB logic.

### Option 2: ODS-style physical key-part unification

Collapse equality-constrained identity parts into a single physical column used by multiple composite FKs (keeping distinct `..._DocumentId` columns per reference site).

**Pros**
- Strong integrity: only one stored value exists, so drift is impossible.
- Best DB-level performance: fewer columns written/updated and no trigger validation cost.
- Simple mental model for reads (one value per unified part).

**Cons**
- Conflicts with the redesign’s “reference sites are self-contained” mapping: two JSON paths now map to one column, complicating reconstitution/diagnostics.
- Interacts poorly with optional references + “all-or-none” checks: a shared key-part can appear populated “because of the other reference,” making naive all-or-none logic incorrect unless special-cased.
- Increases coupling at write time: updates to one reference site’s identity part implicitly affect the other site’s constraints (sometimes desirable, sometimes surprising).
- Does not eliminate SQL Server cascade-path problems; it can still force trigger-based propagation in parts of the FK graph.

### Option 3 (recommended): Canonical physical columns + computed/persisted aliases

Store one canonical, writable column for each equality-class identity part, and keep the per-reference-site columns as *read-only aliases* of the canonical value.

**Shape**

- Canonical storage column (example): `StudentUniqueId` (writable).
- Per-reference alias columns (examples):
  - SQL Server: computed, persisted
    - `StudentSchoolAssociation_StudentUniqueId AS (CASE WHEN StudentSchoolAssociation_DocumentId IS NULL THEN NULL ELSE StudentUniqueId END) PERSISTED`
  - PostgreSQL: generated, stored
    - `StudentSchoolAssociation_StudentUniqueId GENERATED ALWAYS AS (CASE WHEN StudentSchoolAssociation_DocumentId IS NULL THEN NULL ELSE StudentUniqueId END) STORED`

**Presence-gating (to handle optional reference semantics)**

With per-site propagation, a NULL propagated identity part means “this reference site is absent”, and query compilation can safely treat a predicate like `StudentSchoolAssociation_StudentUniqueId = 123` as implying the reference is present.

If overlapping identity parts are collapsed into a single physical column, that invariant breaks: the canonical `StudentUniqueId` can be non-NULL *because another reference site is present*, even when `StudentSchoolAssociation_DocumentId` is NULL. Without “masking”, an absent reference site would appear to have identity values, and predicates on the per-site identity part could produce false matches unless every query is also gated by `StudentSchoolAssociation_DocumentId IS NOT NULL`.

The computed/generated per-site alias columns above are therefore intentionally **presence-gated**: they evaluate to NULL when the reference site’s `..._DocumentId` is NULL, preserving the original “absent ⇒ NULL identity parts” semantics while still storing only one canonical value.

**Keys and cascades**

- Composite FKs must be defined over the canonical physical columns (not the computed/generated aliases), plus the per-site `..._DocumentId`.
- A cascade updates the canonical value once; alias columns reflect the new value automatically with no transient “A≠B” window.

**Pros**
- Single source of truth: DB-level drift between duplicated identity parts becomes impossible.
- Compatible with PostgreSQL cascade semantics (no equality CHECK needed across two writable columns).
- Preserves per-reference column names for mapping/diagnostics while keeping storage canonical.
- Can preserve optional-reference presence semantics by gating aliases with `..._DocumentId` as shown.

**Cons**
- Requires dialect-specific generated/computed column syntax and careful DDL generation.
- Requires changing FK column lists to use the canonical physical columns (aliases cannot participate in `ON UPDATE CASCADE` semantics).
- Adds complexity in inserts/updates: callers write only canonical columns; alias columns are read-only.

## Recommendation

Adopt **Option 3 (canonical physical columns + computed/persisted aliases)** as the default DB-level strategy for key unification **when both equality paths bind to the same table**. It provides a DB-enforced single source of truth while preserving the redesign’s per-reference-site “shape” for mapping and diagnostics, and it avoids PostgreSQL’s cascade-vs-CHECK incompatibility.

Use **Option 1 (triggers)** as a targeted fallback where canonicalization cannot preserve optional-reference semantics cleanly, and retain request-level validation for better error messages and for equalityConstraints that span multiple tables.
