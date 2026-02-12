# Backend Redesign: Key Unification (Canonical Columns + Presence-Gated Aliases)

## Status

Draft. First cut of the Option 3 design described in `key-unification-issue.md`.

## Summary

ApiSchema supports “key unification” via `resourceSchema.equalityConstraints`: the same logical identity value can
appear at multiple JSON paths in the same resource document (often inside multiple reference objects). The backend
redesign currently stores reference identity parts per reference site as independent physical columns
(`{RefBaseName}_{IdentityPart}`), relying on Core request-time validation to keep equality-constrained copies aligned.

This design changes the relational model so equality-constrained identity parts have a single physical source of truth:

- A **canonical physical column** stores the unified value (writable, participates in composite FKs).
- The existing **per-site identity columns** remain in the table shape, but become **generated/computed, persisted**
  **presence-gated aliases** of the canonical column (read-only; `NULL` when the reference site is absent).
- The derived relational model includes **explicit metadata** that links alias columns ↔ canonical storage columns and
  inventories per-table unification classes. This allows DDL generation, constraint derivation, and runtime planning to
  answer both “bind to API JsonPath” and “store/FK/cascade” questions deterministically.

This prevents DB-level drift, preserves existing column naming for query compilation and reconstitution, and avoids
PostgreSQL “mid-cascade” issues that occur when enforcing equality across two independently cascaded writable columns.

## Goals

- **Single source of truth in the database** for equality-constrained identity values stored on the same table row.
- **Preserve the current “per-reference-site” column shape** to avoid widespread query/reconstitution changes.
- **Preserve optional-reference presence semantics**:
  - per-site identity part column is `NULL` when that reference site is absent.
- **Remain compatible with identity propagation** via composite FKs and `ON UPDATE CASCADE` where enabled.
- **Support both PostgreSQL and SQL Server** with deterministic DDL generation.

## Non-Goals

- Enforcing all `equalityConstraints` at the database layer.
  - This design applies only when both sides of an equality constraint resolve to columns stored on the **same table
    row scope**.
- Defining a general cross-table / cross-row equality enforcement mechanism.
- Defining schema migrations. The implementation is in progress and there are no production deployments.

## Terminology

- **Reference site**: a document reference object stored as `{RefBaseName}_DocumentId` plus identity parts
  (e.g., `studentSchoolAssociationReference` on a resource).
- **Reference group**: `{RefBaseName}_DocumentId` plus the identity-part columns for that reference site.
- **Identity part**: a single scalar component of a target resource’s identity (including descriptor identity parts,
  which are stored as `..._DescriptorId`).
- **Equality constraint**: a pair of JSON paths (`sourceJsonPath`, `targetJsonPath`) whose values must be equal in a
  single resource document (`resourceSchema.equalityConstraints`).
- **Unification class**: the transitive closure of equality constraints over a set of (table, value) bindings on the
  same table row scope.
- **Canonical column**: the single stored/writable physical column for a unification class.
- **Alias column**: a generated/computed, persisted column that projects the canonical value under an existing column
  name, optionally gated by reference-site presence.
- **Path column**: the column that binds to an API JsonPath (`DbColumnModel.SourceJsonPath != null`), used by endpoint
  resolution, query compilation, and reconstitution. Under key unification, a path column may be a stored column or a
  generated alias.
- **Presence gating**: making a per-site alias evaluate to `NULL` when `{RefBaseName}_DocumentId` is `NULL`.

## Background (current redesign behavior)

For each document reference site, the relational mapping stores:

- `{RefBaseName}_DocumentId` (stable key)
- `{RefBaseName}_{IdentityPart}` propagated identity columns (one per identity part)

Composite FKs target `(DocumentId, <IdentityParts...>)` on the referenced table, using `ON UPDATE CASCADE` only when the
referenced target has `allowIdentityUpdates=true`.

Core validates `equalityConstraints` on API writes (see `EdFi.DataManagementService.Core/Validation`), but the database
does not prevent drift between duplicated identity parts created by per-site propagation.

## Problem Statement

When two reference sites store the same logical identity value, the current “per-site propagation” approach results in
multiple writable physical columns containing duplicated data. Without DB-level enforcement, those copies can diverge
(direct SQL writes, bugs, bulk operations, or partial updates). Enforcing equality with a simple `CHECK (colA = colB)`
is unsafe under PostgreSQL `ON UPDATE CASCADE` when different cascades update the two columns in separate statements.

## Design Overview

### 1) Canonical physical column

For each unification class on a given table, store **exactly one** canonical physical column:

- Writable (normal column)
- Typed consistently with member columns
- Used in:
  - composite FKs for reference propagation
  - any other constraints that must be stable under cascades

### 2) Presence-gated alias columns

Each previously independent per-site identity column name remains present in the physical table definition, but becomes
a computed/generated column of the canonical value.

For identity parts that belong to a reference group, aliases are presence-gated:

- When `{RefBaseName}_DocumentId IS NULL` → alias evaluates to `NULL`
- Else → alias evaluates to the canonical value

This preserves the invariant used throughout the redesign:

> An absent optional reference site implies `NULL` for that site’s identity part columns.

### 3) Constraints (FKs + all-or-none)

- Composite reference FKs are defined over:
  - `{RefBaseName}_DocumentId`, and
  - the **canonical physical columns** for the identity parts required by the target identity shape.
- “All-or-none” CHECK constraints are defined over:
  - `{RefBaseName}_DocumentId`, and
  - the **per-site alias columns** (presence-gated) for that reference group.

This preserves optional-reference semantics while keeping FK propagation on writable columns.

## Derived Relational Model Metadata (Explicit Canonical/Alias + Unification Classes)

The derived relational model must answer two different questions for the same “logical field”:

1. **Binding question (queries + reconstitution)**: “What column name do I bind to for this API JsonPath?”
   - Answer: the **path column** (the column that retains `DbColumnModel.SourceJsonPath` and the existing per-site
     column naming).
2. **Storage question (writes + FKs + propagation)**: “What column is physically stored/writable and used in composite
   FKs and cascades?”
   - Answer: the **canonical storage column** (single source of truth for the unification class).

Endpoint resolution for equality constraints is path-based (it uses `SourceJsonPath`), but FK derivation and writes must
use canonical storage columns. Inferring canonical-vs-alias behavior implicitly (by naming conventions or ad-hoc rules)
is brittle: both the DDL generator and the runtime plan compiler need to answer the storage question deterministically
for columns that remain present in the table shape specifically to preserve query and reconstitution behavior.

This design therefore introduces explicit model metadata that represents canonical/alias relationships structurally and
without SQL text.

### 1) Column-level storage metadata (alias ↔ canonical)

Extend `DbColumnModel` to distinguish stored vs generated alias columns, and for aliases, record the canonical storage
column and optional presence gate.

Conceptually:

```csharp
public abstract record ColumnStorage
{
    /// <summary>
    /// A normal writable stored column.
    /// </summary>
    public sealed record Stored : ColumnStorage;

    /// <summary>
    /// A read-only generated alias of a canonical stored column.
    /// </summary>
    /// <param name="CanonicalColumn">The stored/writable canonical column name.</param>
    /// <param name="PresenceFkColumn">
    /// Optional presence gate. When set, the alias evaluates to NULL when the presence FK is NULL.
    /// </param>
    public sealed record UnifiedAlias(DbColumnName CanonicalColumn, DbColumnName? PresenceFkColumn) : ColumnStorage;
}

public sealed record DbColumnModel(
    DbColumnName ColumnName,
    ColumnKind Kind,
    RelationalScalarType? ScalarType,
    bool IsNullable,
    JsonPathExpression? SourceJsonPath,
    QualifiedResourceName? TargetResource,
    ColumnStorage Storage
);
```

Rules:

- **Stored column**:
  - normal writable column
  - used for composite FKs and propagation cascades
  - may be an “API-bound” column (has `SourceJsonPath`) or storage-only (no `SourceJsonPath`) depending on how the
    canonical was selected (see “SourceJsonPath rules” below)
- **UnifiedAlias column**:
  - read-only generated/computed alias of `CanonicalColumn`
  - retains `SourceJsonPath` so endpoint resolution, query compilation, and reconstitution bind by API-path semantics
  - when `PresenceFkColumn` is set, the alias is presence-gated:
    - `NULL` when `PresenceFkColumn IS NULL`
    - else `CanonicalColumn`
  - must have a type compatible with `CanonicalColumn` (fail fast otherwise)

This metadata provides a single dialect-neutral “walk” from an API-bound path column to its physical storage column:

- `Stored` → the column itself
- `UnifiedAlias` → `CanonicalColumn`

No string parsing is required (e.g., no need to infer reference groups by trimming `_DocumentId`).

Defaults:

- When no key unification applies to a table, all columns are `Stored` and `KeyUnificationClasses` is empty.
- When key unification applies, only the columns that are members of a unification class become `UnifiedAlias`; all
  other columns remain `Stored`.

### SourceJsonPath rules under unification

`DbColumnModel.SourceJsonPath` remains the authoritative “API JsonPath → binding column” mapping:

- Every API JsonPath must map to **exactly one** column in the derived model.
- A canonical stored column may retain a non-null `SourceJsonPath` only when it is itself the binding column for that
  JsonPath (i.e., no competing alias column with the same `SourceJsonPath` may exist).
- Canonical stored columns introduced solely for storage (common when unifying across multiple reference paths) must
  have `SourceJsonPath = null` and be reachable only through alias metadata.

### 2) Table-level “unification class” metadata (write coalescing inventory)

Column-level alias metadata answers “what do I FK against / what do I write”, but writes also need to answer “how do I
populate the canonical column from an API document” when the canonical has no `SourceJsonPath`.

Add a per-table inventory of unification classes:

```csharp
public sealed record KeyUnificationClass(
    DbColumnName CanonicalColumn,
    IReadOnlyList<DbColumnName> MemberPathColumns
    // Optional: deterministic preferred-source policy hook
);

public sealed record DbTableModel(
    DbTableName Table,
    JsonPathExpression JsonScope,
    TableKey Key,
    IReadOnlyList<DbColumnModel> Columns,
    IReadOnlyList<TableConstraint> Constraints,
    IReadOnlyList<KeyUnificationClass> KeyUnificationClasses
);
```

Semantics:

- `CanonicalColumn` is the stored/writable canonical column for the class.
- `MemberPathColumns` is an **ordered** list of the columns that:
  - participate in the unification class as equality-constrained endpoints, and
  - have `SourceJsonPath` (i.e., they represent API-visible “binding sites” in the document).
- Members will usually be `UnifiedAlias` columns, but can include a stored scalar (e.g., `FiscalYear`) when an existing
  base scalar column is selected as canonical and remains the binding column for its JsonPath.
- Ordering must be deterministic for a fixed effective schema (baseline rule: ordinal sort by
  `Member.SourceJsonPath.Canonical`; a future extension can add an explicit preferred-source override).

### Consumer behavior (how metadata is used)

**Queries + reconstitution**

- Bind by `SourceJsonPath → ColumnName` as today.
- Read/select the existing per-site identity part column names (aliases) to preserve query compilation and
  reconstitution behavior.

**Constraint derivation (composite reference FKs)**

- Derive the composite FK shape using the API identity-path ordering as today.
- For each identity-part column in the composite FK, use `DbColumnModel.Storage` to map:
  - local FK columns → canonical storage columns
  - referenced target columns → canonical storage columns
- Keep all-or-none constraints over the per-site alias columns (presence-gated), preserving the original meaning.

**Writes (flattening / write-plan compilation)**

- Never write `UnifiedAlias` columns (they are read-only).
- Always write `KeyUnificationClass.CanonicalColumn` (the single stored source of truth), even when the canonical has
  `SourceJsonPath = null`.
- Writes MUST be deterministic and MUST NOT consult existing row values (“keep the previous value”) to fill missing
  unified values.

#### Canonical value coalescing (normative)

For each table row being materialized, and for each `KeyUnificationClass` on that table:

1. Evaluate each member path column in `MemberPathColumns` against the request document at the current row’s JSON
   scope, producing **zero-or-one** candidate values per member:
   - A member is **present** when its `SourceJsonPath` selects a scalar value for this row.
   - A member is **absent** when the path selects no value.
   - A selected JSON `null` value MUST be treated as **absent** (Core prunes null-valued properties; backend
     coalescing must match that behavior).
2. Materialize each present candidate value in canonical storage form:
   - For `Scalar` members: convert the JSON scalar to the member’s storage scalar type using the same conversion rules
     used for normal scalar columns (coercions are Core-owned; backend should treat incoming values as already
     canonicalized).
   - For `DescriptorFk` members: resolve the JSON descriptor URI string to the descriptor `DocumentId` (BIGINT) using:
     - normalization consistent with descriptor resolution (baseline: `ToLowerInvariant()`), and
     - the member’s `DbColumnModel.TargetResource` as part of the lookup key.
     - recommended key shape: `DescriptorKey(NormalizedUri, DescriptorResource)` as defined in
       `flattening-reconstitution.md` (`ResolvedReferenceSet.DescriptorIdByKey`).
     If a descriptor URI is present but cannot be resolved to a `DocumentId`, the write MUST fail closed (descriptor
     reference validation failure).
3. Apply conflict detection:
   - If **two or more** members are present with **non-null** values and the values are not equal after conversion,
     the write MUST fail closed (data validation error). This is a defense-in-depth re-check of `equalityConstraints`
     (Core already validates at the document level).
     - For `DescriptorFk` members, equality comparison is `DocumentId` equality (resolved identifier equality), not raw
       descriptor URI string equality.
   - Equality comparison MUST be stable and based on the converted storage representation (e.g., ordinal string
     equality for strings).
4. Choose a canonical value deterministically:
   - The canonical value is the value of the **first present** member in `MemberPathColumns` order.
   - If **no** members are present, the canonical value is `NULL`.
5. Write the canonical value to `CanonicalColumn`.

Notes:
- This coalescing rule applies identically to `POST` (upsert) and `PUT` (update-by-id) to preserve replace semantics
  and idempotency.
- “First present member” uses the deterministic ordering already required by `KeyUnificationClass.MemberPathColumns`.
  The baseline ordering rule remains `SourceJsonPath.Canonical` ordinal sort unless an explicit preferred-source policy
  is added later.

#### Required write-time guardrails (fail-fast)

In addition to conflict detection, flattening MUST apply the following required guardrails per row:

1. **Presence-gated non-null requirement**:
   - For a `KeyUnificationClass`, for each `MemberPathColumn` whose `DbColumnModel.Storage` is
     `UnifiedAlias(CanonicalColumn, PresenceFkColumn)` with `PresenceFkColumn != null`:
     - If the current row’s `PresenceFkColumn` value is non-null (i.e., that reference site is present), then the
       canonical value computed for the class MUST be non-null.
   - If this requirement is violated, the write MUST fail closed (data validation error) rather than relying on a
     later database `CK_*_AllNone` violation. This produces actionable failures and prevents emitting doomed SQL.
2. **Canonical nullability**:
   - If `CanonicalColumn.IsNullable = false`, the canonical value computed for the class MUST be non-null; otherwise
     the write MUST fail closed.

**DDL generation**

- Emit `Stored` columns as normal columns.
- Emit `UnifiedAlias` columns as generated/computed, persisted/stored columns using the recorded canonical/presence-gate
  metadata and dialect-specific syntax.
- Ensure the canonical stored column appears before aliases in the physical column order (dependency).

## Deriving Unification Classes from ApiSchema

### Scope of DB-Level Unification

`equalityConstraints` are defined at the **document** level: a JSONPath may match multiple values (especially with
`[*]`), and Core validation requires that all matched values across the document are equal.

Option 3 does **not** attempt to enforce full document-level equality in the database. Instead, it uses
`equalityConstraints` as a signal that the relational model has duplicated **stored identity values** that should be
physically unified so they cannot drift.

As a result, DB-level unification is strictly **row-local** (within a single physical table row), and only addresses
the “duplicated storage” problem.

Out of scope for Option 3 (Core-only unless a trigger-based design is added):

- **Cross-table** equality: root ↔ child scopes, child ↔ child scopes, base ↔ extension scopes.
- **Cross-row** equality: constraints that imply “all elements in a collection share the same value”, even when both
  endpoints bind to the same table (because the table represents many rows for the document).

### Applicability (in-scope constraints)

This pass applies when both sides of an equality constraint resolve to value bindings on the **same physical table**.

### Endpoint resolution (authoritative mapping)

Equality constraint endpoints are resolved to a physical column using `DbColumnModel.SourceJsonPath` as the single
authoritative “API JsonPath → stored column” mapping.

Key rules:

- Only `DbColumnModel` entries with a non-null `SourceJsonPath` participate in endpoint resolution.
- `DocumentReferenceBinding.IdentityBindings` and `DescriptorEdgeSource` are **not** used as additional endpoint binding
  sources; they are derived metadata over the same `DbColumnModel` set.
- Under key unification:
  - per-path/per-site alias columns retain the original `SourceJsonPath` so queries and reconstitution continue to bind
    by API-path semantics, and
  - canonical storage columns must not introduce ambiguity in JsonPath endpoint resolution:
    - canonical columns introduced solely for storage have `SourceJsonPath = null`, and
    - when an existing base scalar column is selected as canonical, it may retain `SourceJsonPath` as the binding
      column for that path (no competing alias for the same `SourceJsonPath` may exist).

Resolution algorithm (per endpoint path):

1. Find all `DbColumnModel` columns across the derived resource model whose `SourceJsonPath.Canonical` matches the
   endpoint JsonPath string exactly.
2. If there are **zero** matches, the endpoint is not enforceable via Option 3 and remains Core-only.
3. If there is **exactly one** match, that `(table, column)` is the resolved binding for this endpoint.
4. If there is **more than one** match:
   - If all matches refer to the same physical column name on the same table, treat as a duplicate inventory and
     de-duplicate.
   - Otherwise, fail fast: the derived model has become ambiguous for a single API JsonPath endpoint, and any automatic
     “pick one” behavior risks unifying the wrong columns silently.

If either endpoint fails to resolve to exactly one binding, or the two endpoints resolve to different tables, the
constraint is not enforced by this design (it remains Core-only).

### Class construction

Within a given table:

1. Treat each resolved endpoint binding as a node.
2. Each equality constraint adds an undirected edge between its two endpoint nodes.
3. Connected components define unification classes.

### Type compatibility

All nodes within a unification class must have compatible physical types:

- Scalar vs scalar
- DescriptorFk vs DescriptorFk (both are stored as `BIGINT` / `bigint` descriptor document ids)
  - DescriptorFk columns are compatible only when they target the **same** descriptor resource type
    (`DbColumnModel.TargetResource`).
- Scalar and DescriptorFk are not compatible

The derived model build must fail fast if incompatible types are unified.

### Descriptor identity parts (`..._DescriptorId`) under unification

Descriptor values are API-facing URI strings but are stored as foreign keys to `dms.Descriptor` (`..._DescriptorId`,
`BIGINT` / `bigint`).

Implications when descriptor identity parts participate in key unification:

- Unification for descriptor endpoints is over the **resolved descriptor `DocumentId`**, not the raw URI string.
  - Canonical descriptor columns store the single source of truth `DocumentId`.
  - Per-site/path columns remain present as `UnifiedAlias` aliases (presence-gated where applicable), preserving
    query/reconstitution bindings by `SourceJsonPath`.
- Coalescing and conflict detection operate on resolved `DocumentId`s, so the key-unification write planner depends on
  descriptor resolution results (the same resolved descriptor-id map used to populate normal descriptor FK columns).
- Descriptor FK unification MUST be resource-type safe: a unification class must not mix descriptor FK members that
  target different descriptor resource types.
- Descriptor resources are treated as immutable in this redesign, so descriptor FK columns generally do not
  participate in `ON UPDATE CASCADE` identity propagation. Key unification still matters to prevent drift between
  duplicated writable `..._DescriptorId` columns on the same row.

## Canonical Column Selection and Naming

### Selection rules (deterministic)

For each unification class on a table, select exactly one canonical column:

1. Prefer an existing **non-reference-site** column (i.e., a column whose name is not `{RefBaseName}_...`) when one of
   the unified endpoints is already represented as a “base” scalar column on the table.
2. Otherwise, create a new canonical column and convert all member per-site columns into aliases.

### Naming rules (deterministic)

The canonical column name must be deterministic for a fixed effective schema and must remain stable under identifier
shortening. The baseline rule for first cut:

- If a “base” scalar column is selected as canonical, its name is unchanged.
- Otherwise:
  - Use the identity-part base name suffix that already appears in `{RefBaseName}_{IdentityPart}`.
  - If that name collides with an existing column, apply a deterministic collision suffix.

The alias columns keep the existing per-site names (e.g., `StudentSchoolAssociation_StudentUniqueId`) so query
compilation and reconstitution can continue to bind by those names.

## Presence-Gated Alias Semantics

For a reference site `{RefBaseName}` and unified identity part canonical column `{Canonical}`:

- Alias column `{RefBaseName}_{IdentityPart}` is defined as:
  - `NULL` when `{RefBaseName}_DocumentId` is `NULL`
  - `{Canonical}` otherwise

This allows:

- per-site predicates to continue using `{RefBaseName}_{IdentityPart}` without additionally checking
  `{RefBaseName}_DocumentId IS NOT NULL`
- “all-or-none” nullability constraints to remain expressible at the per-reference-group level

## Dialect DDL

### SQL Server

Alias columns are computed, persisted:

```sql
StudentSchoolAssociation_StudentUniqueId AS (
  CASE
    WHEN StudentSchoolAssociation_DocumentId IS NULL THEN NULL
    ELSE StudentUniqueId
  END
) PERSISTED
```

### PostgreSQL

Alias columns are generated, stored:

```sql
"StudentSchoolAssociation_StudentUniqueId" <type>
  GENERATED ALWAYS AS (
    CASE
      WHEN "StudentSchoolAssociation_DocumentId" IS NULL THEN NULL
      ELSE "StudentUniqueId"
    END
  ) STORED
```

Notes:

- Aliases are read-only in both dialects.
- Indexes can be created on persisted/stored generated columns if required by query patterns.

## Constraint Changes

### Composite reference foreign keys

For a reference site, the composite FK uses canonical columns for unified identity parts:

- Local FK columns:
  - `{RefBaseName}_DocumentId`
  - `<CanonicalIdentityParts...>` (in the referenced target’s identity path order)
- Target columns:
  - `DocumentId`
  - `<TargetIdentityColumns...>`

### All-or-none nullability constraints

All-or-none constraints use the per-site alias columns, preserving the original meaning:

- If `{RefBaseName}_DocumentId` is `NULL` then all per-site identity aliases are `NULL`
- If `{RefBaseName}_DocumentId` is not `NULL` then all per-site identity aliases are not `NULL`

## Runtime Implications

### Writes (flattening)

- Only canonical columns are writable for unified identity parts.
- Flattening must populate canonical columns from the JSON payload using the table’s `KeyUnificationClass` inventory per
  the “Canonical value coalescing (normative)” and “Required write-time guardrails (fail-fast)” rules above.
- Per-site alias columns are not written.

### Reads (reconstitution)

- Reconstitution continues to read from per-site identity columns.
- Presence gating ensures absent optional reference sites do not “inherit” canonical values from other sites.

### Queries

- Query compilation can continue to bind per-site predicates to per-site identity column names.
- Presence gating preserves the existing semantics where filtering on a per-site identity part implies reference
  presence.

## Interactions with Existing Design Docs

This design refines the baseline language in:

- `data-model.md` (reference identity columns)
- `ddl-generation.md` (“Reference constraints” and the “Key unification note”)
- `transactions-and-concurrency.md` (reference propagation semantics)

Those documents currently describe per-site identity part columns as independent physical columns. Under key
unification, some of those columns become generated/computed aliases.

## Pending Questions

- Whether scalar (non-reference) columns that are equality-constrained should always keep per-path alias columns, or
  whether some duplicates can be dropped without affecting query/reconstitution behavior.
- The canonical-column naming rule when multiple per-site identity-part suffixes differ due to name overrides or when
  the leaf property name is ambiguous.
- The canonical-column nullability rule when a unification class contains a mix of required and optional endpoints.
- Whether a canonical column shared across multiple composite FKs with `ON UPDATE CASCADE` can introduce transient FK
  violations during multi-edge cascades, and what the mitigation strategy is for PostgreSQL and SQL Server.
- Why the legacy Ed-Fi ODS schema sometimes does not physically unify columns that are related by ApiSchema
  `equalityConstraints` (e.g., DS 5.2 `Grade` uses both `SchoolYear` and `GradingPeriodSchoolYear` - is this a bug in
  ApiSchema generation?), and whether DMS should unify those cases anyway (and if so, how to select canonical vs alias columns).
- Whether the identifier-shortening pass can change canonical/alias naming decisions and how to keep the result stable
  across dialects.
