# Backend Redesign: Key Unification (Canonical Columns + Generated Aliases; Presence-Gated When Optional)

## Status

Draft.

## Summary

ApiSchema supports “key unification” via `resourceSchema.equalityConstraints`: the same logical identity value can
appear at multiple JSON paths in the same resource document (often inside multiple reference objects). The backend
redesign currently stores reference identity parts per reference site as independent physical columns
(`{RefBaseName}_{IdentityPart}`), relying on Core request-time validation to keep equality-constrained copies aligned.

This design changes the relational model so equality-constrained identity parts have a single physical source of truth:

- A **canonical physical column** stores the unified value (writable, participates in composite FKs).
- The existing **per-site / per-path columns** remain in the table shape, but become **generated/computed, persisted**
  **aliases** of the canonical column (read-only; `NULL` when the site/path is absent). When the site/path can be
  absent, aliases are **presence-gated**.
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
- **Preserve per-path presence semantics** for equality-constrained non-reference fields:
  - absent optional paths MUST reconstitute as absent (`NULL` at the binding column), and
  - predicates against a per-path column MUST continue to imply that the path was present.
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
  name, optionally gated by per-path presence.
- **Path column**: the column that binds to an API JsonPath (`DbColumnModel.SourceJsonPath != null`), used by endpoint
  resolution, query compilation, and reconstitution. Under key unification, a path column may be a stored column or a
  generated alias.
- **Presence column**: a physical column whose nullability indicates whether a binding site is present for this row.
  - For reference-site identity aliases: `{RefBaseName}_DocumentId`
  - For optional non-reference per-path aliases: a synthetic presence flag column (see below)
- **Presence flag column**: a stored nullable boolean/bit column used as a presence column for optional non-reference
  paths:
  - `NULL` → the path is absent for this row
  - `TRUE`/`1` → the path is present for this row
  - `FALSE`/`0` is not used (presence is `NULL` vs `TRUE`)
- **Presence gating**: making an alias evaluate to `NULL` when its `PresenceColumn` is `NULL` (when a presence column is
  defined).

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

For equality-constrained non-reference path columns (scalar values and descriptor FK values) that are optional,
aliases are presence-gated using a synthetic per-path presence flag column (stored, nullable `bool`/`bit`):

- When `{PathColumnName}_Present IS NULL` → alias evaluates to `NULL`
- Else → alias evaluates to the canonical value

This preserves the invariant used by query compilation and reconstitution:

> An absent optional JSON path implies `NULL` for that path’s binding column.

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
column and presence gate.

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
    /// <param name="PresenceColumn">
    /// Optional presence gate:
    /// - When <c>PresenceColumn</c> is non-null, the alias evaluates to NULL when <c>PresenceColumn IS NULL</c>.
    /// - When <c>PresenceColumn</c> is null, the alias is not presence-gated and always evaluates to
    ///   <c>CanonicalColumn</c>.
    ///
    /// PresenceColumn is one of:
    /// - reference-site presence: the reference group’s <c>..._DocumentId</c> column, or
    /// - scalar/path presence: a synthetic <c>..._Present</c> presence flag column (stored, nullable bool/bit),
    ///   used only for optional non-reference member endpoints.
    /// </param>
    public sealed record UnifiedAlias(DbColumnName CanonicalColumn, DbColumnName? PresenceColumn) : ColumnStorage;
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
  - may be an “API-bound” column (has `SourceJsonPath`) or storage-only (no `SourceJsonPath`)
  - canonical storage columns are always storage-only (`SourceJsonPath = null`)
- **UnifiedAlias column**:
  - read-only generated/computed alias of `CanonicalColumn`
  - retains `SourceJsonPath` so endpoint resolution, query compilation, and reconstitution bind by API-path semantics
  - presence gating:
    - when `PresenceColumn` is non-null:
      - `NULL` when `PresenceColumn IS NULL`
      - else `CanonicalColumn`
    - when `PresenceColumn` is null: always `CanonicalColumn` (ungated alias)
  - `PresenceColumn` is:
    - the reference group’s `..._DocumentId` column for reference-site aliases, and
    - a synthetic `..._Present` presence flag column only for optional non-reference path aliases.
  - must have a type compatible with `CanonicalColumn` (fail fast otherwise)

This metadata provides a single dialect-neutral “walk” from an API-bound path column to its physical storage column:

- `Stored` → the column itself
- `UnifiedAlias` → `CanonicalColumn`

No string parsing is required (e.g., no need to infer reference groups by trimming `_DocumentId`).

Defaults:

- When no key unification applies to a table, all columns are `Stored` and `KeyUnificationClasses` is empty.
- When key unification applies, only the columns that are members of a unification class become `UnifiedAlias`; all
  other columns remain `Stored`.
- For optional non-reference member path columns, key unification introduces additional stored `..._Present` presence
  flag columns used for presence gating.

### SourceJsonPath rules under unification

`DbColumnModel.SourceJsonPath` remains the authoritative “API JsonPath → binding column” mapping:

- Every API JsonPath must map to **exactly one** column in the derived model.
- For any unification class, all equality-constrained API JsonPaths MUST remain bound to distinct **per-path columns**
  in the table shape (`KeyUnificationClass.MemberPathColumns`); duplicates MUST NOT be dropped.
- `KeyUnificationClass.CanonicalColumn` MUST be storage-only and MUST have `SourceJsonPath = null`:
  - canonical columns MUST NOT participate in API JsonPath endpoint resolution, and
  - canonical columns MUST be reachable only through alias metadata (`ColumnStorage.UnifiedAlias`).

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
- Members are the API-bound “binding columns” for the class and are typically `UnifiedAlias` columns whose
  `ColumnStorage.UnifiedAlias.CanonicalColumn` points at `CanonicalColumn`.
- Ordering must be deterministic for a fixed effective schema (baseline rule: ordinal sort by
  `Member.SourceJsonPath.Canonical`; a future extension can add an explicit preferred-source override).

### Consumer behavior (how metadata is used)

**Queries + reconstitution**

- Bind by `SourceJsonPath → ColumnName` as today.
- Read/select the existing per-site/per-path binding column names (aliases) to preserve query compilation and
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
- When `UnifiedAlias.PresenceColumn` is non-null and is a synthetic `..._Present` presence flag column, writes MUST
  also populate that presence flag column (`NULL` when absent; non-null when present) for each materialized row.
- Writes MUST be deterministic and MUST NOT consult existing row values (“keep the previous value”) to fill missing
  unified values.

#### Binding vs storage semantics for existing derived metadata (normative)

Key unification introduces a structural split between:

- **Binding/path semantics** (queries + reconstitution + API-visible uniqueness/presence), and
- **Storage/writable semantics** (writes + FKs + cascades + triggers + maintenance).

This split MUST be applied consistently across all existing derived metadata that refers to columns by name.

##### Definitions (dialect-neutral)

For any `DbTableModel`:

- A **binding column** (or “path column”) is a column with a non-null `DbColumnModel.SourceJsonPath`.
  - Under key unification, a binding column MAY be stored (`ColumnStorage.Stored`) or an alias
    (`ColumnStorage.UnifiedAlias`).
- A **storage column** is a physical stored/writable column.
  - For any binding column, its storage column is:
    - `Stored` → the column itself
    - `UnifiedAlias(CanonicalColumn, ...)` → `CanonicalColumn`
- A **presence column** is the column named by `UnifiedAlias.PresenceColumn` (when non-null).
  - Presence columns are always stored (`Stored`) and storage-only (`SourceJsonPath = null`).
  - Presence columns are either:
    - reference-site presence: `{RefBaseName}_DocumentId`, or
    - scalar/path presence: synthetic `{PathColumnName}_Present` (nullable `bool`/`bit`).

Derived models MUST preserve the global “endpoint binding” invariant:

- Every API JsonPath endpoint MUST resolve to exactly one binding column via `SourceJsonPath`.
- Canonical columns MUST be storage-only (`SourceJsonPath = null`) and MUST NOT participate in endpoint resolution.

##### Required consumer mapping rules

Any consumer that needs a column for **DML/DDL that targets writable storage** MUST resolve the storage column using
`DbColumnModel.Storage`:

- Writes (flattening / parameter binding): write only storage columns.
- Foreign key derivation + emission: define FKs only over storage columns.
- Identity propagation (cascades and fallbacks): update storage columns only.
- FK-supporting index derivation: index the final FK column list after storage mapping and de-duplication.

Any consumer that needs a column for **API-path semantics** MUST continue to use binding columns:

- Query compilation binds predicates to binding columns to preserve “presence implies predicate participation”.
- Reconstitution reads from binding columns so optional sites/paths remain absent when absent.
- API-semantic UNIQUE constraints are derived from JsonPaths and use binding columns (including aliases).

Consumers MUST NOT attempt to infer binding vs storage behavior by column naming conventions.

##### `DocumentReferenceBinding` and `ReferenceIdentityBinding` (normative)

`DocumentReferenceBinding` binds a reference object path to:

- `FkColumn`: the reference-site `..._DocumentId` column.
  - `FkColumn` is always stored/writable.
  - `FkColumn` is the **presence column** for any unified per-site identity-part alias columns in that reference
    group.
- `IdentityBindings[*].Column`: the binding column for a specific `IdentityBindings[*].ReferenceJsonPath`.
  - Under key unification, this binding column MAY be a `UnifiedAlias` and therefore read-only.

Required interpretation rules:

1. **Endpoint binding / reconstitution**:
   - `IdentityBindings[*].Column` is the authoritative binding column for that identity endpoint’s `SourceJsonPath`.
2. **Composite reference FK derivation**:
   - When deriving the local/target identity-part column list for a composite reference FK, each identity-part column
     MUST be mapped to its storage column via `DbColumnModel.Storage` before emitting the FK.
3. **All-or-none constraints**:
   - All-or-none constraints MUST remain defined over:
     - `FkColumn`, and
     - the per-site binding columns (identity-part aliases),
     preserving the “reference site absent implies identity parts are null” meaning.
   - All-or-none constraints MUST NOT be rewritten to use canonical columns, because canonical columns may remain
     non-null due to another reference site and would change the constraint’s semantics.

##### `DescriptorEdgeSource` (normative)

`DescriptorEdgeSource` binds a descriptor value path to `FkColumn`:

- `DescriptorValuePath` remains the authoritative endpoint path for descriptor resolution.
- `FkColumn` is the binding column for that endpoint and MAY be `UnifiedAlias`.

Required interpretation rules:

1. **Descriptor resolution**:
   - Resolution remains path-driven (`DescriptorValuePath`) and resource-type safe (`DescriptorResource`).
2. **Writes**:
   - Write layers MUST NOT assume `DescriptorEdgeSource.FkColumn` is writable.
   - When populating a descriptor FK value, the write layer MUST map the binding column to its storage column via
     `DbColumnModel.Storage`. When the binding column is unified, the value is written to the class’s canonical column
     via the key-unification precompute step.
3. **Descriptor FK constraints**:
   - Descriptor FK constraints to `dms.Descriptor(DocumentId)` MUST be anchored on the storage column (see
     “Descriptor foreign keys (`dms.Descriptor`) (normative)” below).

#### Plan compiler and flattener contract (normative)

Key unification changes the write-time relationship between API-bound “binding columns” and physical writable
storage:

- API-bound columns for unified endpoints remain present in the table shape for query and reconstitution, but may be
  `UnifiedAlias` columns (read-only generated aliases of a canonical stored column).
- Canonical storage columns are writable but are storage-only (`SourceJsonPath = null`).
- Some unified endpoints (optional non-reference paths) introduce additional stored synthetic `..._Present` columns
  that must be written deterministically to preserve per-path presence semantics.

Therefore, the write-plan layer described in `flattening-reconstitution.md` MUST be extended so the plan compiler and
flattener can:

1. emit DDL/SQL that writes only stored/writable columns,
2. populate canonical storage columns whose `SourceJsonPath` is null, and
3. populate synthetic `..._Present` flags deterministically (without consulting existing row values).

##### Plan-layer shape changes

Extend the plan-layer types (conceptually, as defined in `flattening-reconstitution.md`) as follows:

```csharp
public abstract record WriteValueSource
{
    // Existing cases omitted.

    /// <summary>
    /// The column value is produced by a table-local precompute step (e.g., key-unification coalescing).
    /// </summary>
    public sealed record Precomputed() : WriteValueSource;
}

public sealed record TableWritePlan(
    DbTableModel TableModel,
    string InsertSql,
    string? UpdateSql,
    string? DeleteByParentSql,
    IReadOnlyList<WriteColumnBinding> ColumnBindings,
    IReadOnlyList<KeyUnificationWritePlan> KeyUnificationPlans
);
```

`KeyUnificationPlans` is empty when `DbTableModel.KeyUnificationClasses` is empty.

Add a per-table inventory describing how to compute canonical and presence-flag values during row materialization:

```csharp
public sealed record KeyUnificationWritePlan(
    DbColumnName CanonicalColumn,
    int CanonicalBindingIndex,
    IReadOnlyList<KeyUnificationMemberWritePlan> MembersInOrder
);

public sealed record KeyUnificationMemberWritePlan(
    DbColumnName MemberPathColumn,
    JsonPathExpression RelativePath,
    ColumnKind Kind,
    RelationalScalarType? ScalarType,
    QualifiedResourceName? DescriptorResource,
    DbColumnName? PresenceColumn,
    int? PresenceBindingIndex,
    bool PresenceIsSynthetic
);
```

Rules:

- `KeyUnificationMemberWritePlan.Kind` MUST be `Scalar` or `DescriptorFk`.
- `RelativePath` MUST be relative to the table’s `JsonScope` node and MUST NOT contain `[*]` segments (row-local,
  zero-or-one selection per row).
- `CanonicalBindingIndex` is an index into `TableWritePlan.ColumnBindings`:
  - this preserves the existing “parameter ordering is defined by `ColumnBindings`” invariant for compiled SQL.
- When `PresenceColumn` is non-null, `PresenceBindingIndex` is an index into `TableWritePlan.ColumnBindings`.
- `MembersInOrder` ordering MUST exactly match the table’s `KeyUnificationClass.MemberPathColumns` ordering.

##### Plan compiler rules (writes)

For a `DbTableModel` with one or more `KeyUnificationClasses`, plan compilation MUST follow these rules:

1. **Write only stored/writable columns**
   - Exclude any column whose `DbColumnModel.Storage` is `UnifiedAlias(...)` from:
     - `TableWritePlan.ColumnBindings`, and
     - the emitted `InsertSql` / `UpdateSql` column lists.
   - Include:
     - key columns,
     - all normal stored scalar/FK columns,
     - all canonical stored columns (`KeyUnificationClass.CanonicalColumn`),
     - all synthetic `..._Present` columns introduced for optional non-reference unified endpoints.
2. **Canonical and synthetic presence-flag columns are `Precomputed`**
   - Emit canonical columns and synthetic `..._Present` columns in `ColumnBindings` with
     `WriteValueSource.Precomputed`.
3. **Emit `KeyUnificationWritePlan` for every `KeyUnificationClass`**
   - `CanonicalBindingIndex` MUST point at the canonical column’s `ColumnBindings` position.
   - For each member endpoint in `KeyUnificationClass.MemberPathColumns`, emit a corresponding
     `KeyUnificationMemberWritePlan` entry:
     - `MemberPathColumn` is the API-bound binding-column name (typically a `UnifiedAlias` column).
     - `RelativePath` is derived by relativizing the member column’s `SourceJsonPath` against the table’s `JsonScope`.
     - `PresenceColumn` is taken from the member column’s `UnifiedAlias.PresenceColumn` (may be null).
     - When `PresenceColumn` is non-null, `PresenceBindingIndex` MUST point at `PresenceColumn`’s `ColumnBindings`
       position.
     - `PresenceIsSynthetic` is `true` only when `PresenceColumn` is the synthetic `..._Present` column for a
       non-reference member endpoint.
4. **Fail-fast invariants**
   - Any attempt to write a `UnifiedAlias` column MUST fail at plan compile time (do not emit doomed SQL).
   - Every `WriteValueSource.Precomputed` binding MUST be populated by exactly one `KeyUnificationWritePlan`
     (no orphan precomputed columns).

##### Flattener algorithm (per row)

When materializing a row for a table whose `TableWritePlan.KeyUnificationPlans` is non-empty, the flattener MUST use a
two-phase approach:

1. Allocate the row buffer in `ColumnBindings` order.
2. Populate all non-`Precomputed` bindings as today (document id, parent keys, ordinals, scalars, references,
   descriptors).
   - Unified endpoints’ alias columns are not written and therefore do not participate in row materialization.
3. For each `KeyUnificationWritePlan`:
   1. Evaluate member endpoints in `MembersInOrder` against the current row’s scope node using `RelativePath`,
      producing (present?, value) candidates per the “Canonical value coalescing (normative)” rule below.
   2. When `PresenceIsSynthetic` is `true`, write the synthetic presence flag deterministically:
      - absent → `NULL`
      - present → `TRUE`/`1`
      - `FALSE`/`0` MUST NOT be written (presence is “non-null means present”).
   3. Convert/resolve each present member value to its canonical storage representation and apply conflict detection.
   4. Choose the canonical value deterministically and write it to the canonical column at `CanonicalBindingIndex`.
   5. Apply “Required write-time guardrails (fail-fast)” using the presence-column parameter values that are now in
      the row buffer (including reference-site `..._DocumentId` values written in phase 1).
4. After all unification plans complete, every `WriteValueSource.Precomputed` binding MUST have a value assigned
   (possibly `NULL`); otherwise the write MUST fail closed.

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
     `UnifiedAlias(CanonicalColumn, PresenceColumn)` and `PresenceColumn` is non-null:
     - If the current row’s `PresenceColumn` value is non-null (i.e., that path’s presence gate indicates the member
       site is present), then the canonical value computed for the class MUST be non-null.
   - If this requirement is violated, the write MUST fail closed (data validation error) rather than relying on a
     later database `CK_*_AllNone` violation. This produces actionable failures and prevents emitting doomed SQL.
2. **Canonical nullability**:
   - If `CanonicalColumn.IsNullable = false`, the canonical value computed for the class MUST be non-null; otherwise
     the write MUST fail closed.

**DDL generation**

- Emit `Stored` columns as normal columns.
- Emit `UnifiedAlias` columns as generated/computed, persisted/stored columns using the recorded canonical/presence-gate
  metadata and dialect-specific syntax.
- Column ordering MUST be deterministic and MUST respect alias dependencies:
  - For every `UnifiedAlias` column, its `CanonicalColumn` and optional `PresenceColumn` MUST appear earlier in
    `DbTableModel.Columns`.
  - Recommended ordering per table:
    1. key columns in `TableKey` order
    2. key-unification support columns (canonical storage columns + synthetic presence flags), ordered by
       `ColumnName.Value` (ordinal)
    3. all remaining columns ordered per the baseline canonical rules, provided the dependency invariant above holds
       (this preserves existing column-grouping conventions outside the unification support set)

### 3) Model / manifest / mapping-pack surface (normative)

Key unification introduces new “binding vs storage” semantics that MUST be represented explicitly and consistently in:

- the in-memory derived model types,
- any emitted relational-model manifest JSON (diagnostics + golden tests), and
- any mapping-pack payloads (`.mpack`) used for AOT mode.

No producer/consumer is allowed to infer unification behavior by naming conventions or ad-hoc SQL text parsing.

#### In-memory derived model (required)

The derived relational model types defined in `flattening-reconstitution.md` (and implemented in the shared
`EdFi.DataManagementService.Backend.External` contracts) MUST carry the unification metadata explicitly:

- `DbColumnModel.Storage` identifies whether a column is:
  - `Stored` (writable physical column), or
  - `UnifiedAlias` (read-only generated alias of a canonical stored column, optionally presence-gated).
- `DbTableModel.KeyUnificationClasses` inventories per-table unification classes so writers can:
  - compute canonical values for storage-only canonical columns (`SourceJsonPath = null`), and
  - populate synthetic presence flags deterministically.

Determinism requirements:

- `DbTableModel.KeyUnificationClasses` MUST be in deterministic order for a fixed effective schema:
  - baseline: ordinal sort by `CanonicalColumn.Value`.
  - `MemberPathColumns` ordering is significant and MUST match the deterministic member-order rule defined above.
- `DbColumnModel.Storage` references (`CanonicalColumn`, `PresenceColumn`) MUST refer to final physical column names
  after any dialect identifier hashing/shortening; any renaming pass MUST update these references.

#### Manifest JSON surface (required)

Any relational-model manifest output used for diagnostics and/or golden tests MUST include the explicit unification
metadata so drift is observable without inspecting generated SQL.

At minimum, the manifest MUST include:

1. For every table column:
   - a `storage` object describing `Stored` vs `UnifiedAlias`, and
   - for aliases, the `canonical_column` and optional `presence_column` column names.
2. For every table:
   - a `key_unification_classes` array describing each class’s canonical column and ordered member-path columns.

Recommended manifest shape (illustrative):

```json
{
  "tables": [
    {
      "schema": "edfi",
      "name": "StudentAssessmentRegistration",
      "scope": "$",
      "key_unification_classes": [
        {
          "canonical_column": "StudentUniqueId_Unified",
          "member_path_columns": [
            "StudentEducationOrganizationAssociation_StudentUniqueId",
            "StudentSchoolAssociation_StudentUniqueId"
          ]
        }
      ],
      "columns": [
        {
          "name": "StudentUniqueId_Unified",
          "kind": "Scalar",
          "source_path": null,
          "storage": { "kind": "Stored" }
        },
        {
          "name": "StudentSchoolAssociation_StudentUniqueId",
          "kind": "Scalar",
          "source_path": "$.studentSchoolAssociationReference.studentUniqueId",
          "storage": {
            "kind": "UnifiedAlias",
            "canonical_column": "StudentUniqueId_Unified",
            "presence_column": "StudentSchoolAssociation_DocumentId"
          }
        }
      ]
    }
  ]
}
```

Rules:

- The manifest MUST represent unification metadata without embedding dialect SQL expressions (those are DDL-emitter
  concerns). Canonical and presence columns are represented strictly by physical column names.
- When `UnifiedAlias.PresenceColumn` is null (ungated alias), `presence_column` SHOULD be emitted as `null` for
  clarity.
- The ordering of `key_unification_classes` and `member_path_columns` MUST be deterministic and match the in-memory
  ordering rules above.

#### Equality-constraint diagnostics surface (required)

Key unification is derived from ApiSchema `resourceSchema.equalityConstraints`, but Option 3 intentionally applies only
to a subset of those constraints (row-local, unambiguous bindings, same-table). Any remaining constraints are enforced
by Core only.

To avoid silent “Core-only vs DB-unified” drift, any relational-model manifest output used for diagnostics and/or
golden tests MUST include a deterministic per-resource report that classifies every equality constraint as:

- **applied** (contributed to a same-table unification class), or
- **skipped** (left Core-only), with an explicit, machine-readable skip reason.

Recommended manifest shape (illustrative):

```json
{
  "resource": { "project_name": "Ed-Fi", "resource_name": "StudentAssessmentRegistration" },
  "key_unification_equality_constraints": {
    "applied": [
      {
        "endpoint_a_path": "$.studentSchoolAssociationReference.studentUniqueId",
        "endpoint_b_path": "$.studentEducationOrganizationAssociationReference.studentUniqueId",
        "table": { "schema": "edfi", "name": "StudentAssessmentRegistration" },
        "endpoint_a_column": "StudentSchoolAssociation_StudentUniqueId",
        "endpoint_b_column": "StudentEducationOrganizationAssociation_StudentUniqueId",
        "canonical_column": "StudentUniqueId_Unified"
      }
    ],
    "skipped": [
      {
        "endpoint_a_path": "$.schoolYear",
        "endpoint_b_path": "$.gradingPeriods[*].schoolYear",
        "reason": "cross_table",
        "endpoint_a_binding": {
          "table": { "schema": "edfi", "name": "StudentAssessmentRegistration" },
          "column": "SchoolYear"
        },
        "endpoint_b_binding": {
          "table": { "schema": "edfi", "name": "StudentAssessmentRegistration_GradingPeriods" },
          "column": "GradingPeriodSchoolYear"
        }
      },
      {
        "endpoint_a_path": "$.somePathNotStored",
        "endpoint_b_path": "$.someOtherPathNotStored",
        "reason": "unresolved_endpoint",
        "endpoint_a_binding": null,
        "endpoint_b_binding": null
      }
    ],
    "skipped_by_reason": {
      "cross_table": 1,
      "unresolved_endpoint": 1
    }
  }
}
```

Rules:

- The report is per-resource (same scope as ApiSchema `resourceSchema.equalityConstraints`).
- Endpoints MUST be emitted in a canonical undirected order:
  - `endpoint_a_path` is the ordinal-min of the two endpoint paths
  - `endpoint_b_path` is the ordinal-max
  This makes the report stable even if ApiSchema emits the same constraint in both directions.
- `endpoint_*_binding` describes the resolved **path column** binding (the column that retains `SourceJsonPath`,
  typically an alias under unification). It is either:
  - `{ table, column }` when resolvable via `DbColumnModel.SourceJsonPath`, or
  - `null` when the endpoint is not resolvable by the derived model and remains Core-only.
- `applied[]` entries MUST include:
  - the resolved owning table (same for both endpoints), and
  - the two endpoint column names, and
  - the canonical storage column name for the corresponding unification class.
- `skipped[]` entries MUST include:
  - `reason`, and
  - both endpoint bindings (`null` when unresolved).
- `skipped_by_reason` MUST match `skipped[]` exactly.
- Ordering MUST be deterministic:
  - `applied[]` and `skipped[]` sorted by `(endpoint_a_path, endpoint_b_path)` ordinal.
  - `skipped_by_reason` keys sorted ordinal (or emitted in a deterministic fixed order if the writer does not
    preserve key ordering).

Skip reasons (v1):

- `unresolved_endpoint`: one or both endpoints did not resolve to a derived column via `SourceJsonPath`.
- `unsupported_endpoint_kind`: one or both endpoints resolved, but to an unsupported `ColumnKind` for unification (e.g.
  `DocumentFk`, `Ordinal`, `ParentKeyPart`).
- `cross_table`: both endpoints resolved, but to different physical tables.

#### Mapping-pack payload surface (`.mpack`) (required)

Mapping packs are required to contain enough information for a consumer to:

- bind by API JsonPath (queries/reconstitution),
- write only stored columns (never `UnifiedAlias`),
- compute canonical values deterministically for storage-only canonical columns, and
- populate synthetic presence flags deterministically.

Therefore, the PackFormatVersion=1 payload schema defined in `mpack-format-v1.md` MUST be extended (wire-compatible
additions only) to carry the unification metadata explicitly.

##### Model payload additions (required)

1. `DbColumnModel` MUST include storage metadata:

- Add `ColumnStorage storage = 20;` where:
  - `Stored` means “writable physical column”, and
  - `UnifiedAlias` carries:
    - `canonical_column` (required), and
    - `presence_column` (optional; omitted for ungated aliases).

2. `DbTableModel` MUST include per-table unification classes:

- Add `repeated KeyUnificationClass key_unification_classes = 20;`
- `KeyUnificationClass` includes:
  - `canonical_column` (required), and
  - `member_path_columns` (ordered; required to preserve deterministic coalescing behavior).

Recommended proto shape (illustrative; exact message/field names are non-normative as long as the semantics match):

```proto
message ColumnStorage {
  oneof kind {
    StoredStorage stored = 1;
    UnifiedAliasStorage unified_alias = 2;
  }
}

message StoredStorage {}

message UnifiedAliasStorage {
  DbColumnName canonical_column = 1;
  DbColumnName presence_column = 2; // optional; omitted for ungated aliases
}

message KeyUnificationClass {
  DbColumnName canonical_column = 1;
  repeated DbColumnName member_path_columns = 2; // ordered
}
```

##### Plan payload additions (required)

When using the “Plan compiler and flattener contract (normative)” described above, the mapping-pack plan schema MUST
also carry the additional write-time unification planning constructs:

- Add a `WriteValueSource` kind for precomputed values (e.g., `WritePrecomputed precomputed = 7;`).
- Add `TableWritePlan.key_unification_plans` (repeated, recommended `= 30`) and the corresponding message shapes for:
  - `KeyUnificationWritePlan`, and
  - `KeyUnificationMemberWritePlan` (including the binding indices and presence semantics described above).

These are payload-level equivalents of the in-memory plan-layer types so AOT mode does not require runtime derivation
from `ApiSchema.json`.

##### Payload validation rules (required)

Pack producers and consumers MUST validate the following invariants at build/load time (fail fast on any violation):

- `UnifiedAlias.canonical_column` exists on the same table and has `storage.kind = Stored`.
- `UnifiedAlias.canonical_column` is storage-only (`source_json_path` absent/empty) and is not itself a `UnifiedAlias`.
- When `UnifiedAlias.presence_column` is present:
  - it exists on the same table and has `storage.kind = Stored`, and
  - it is nullable and storage-only (`source_json_path` absent/empty).
- `key_unification_classes[*].canonical_column` exists and is stored.
- `key_unification_classes[*].member_path_columns`:
  - are distinct,
  - exist on the same table,
  - each have a non-empty `source_json_path`, and
  - each are `UnifiedAlias` columns whose `canonical_column` matches the class’s canonical column.
- Plan-layer unification shapes are internally consistent:
  - all referenced binding indices are in range for `TableWritePlan.column_bindings`,
  - each precomputed binding index corresponds to a `WriteValueSource.Precomputed` binding, and
  - each precomputed binding is populated by exactly one unification write plan.

##### Versioning (required)

Adding proto fields is wire-compatible and does not require bumping `PackFormatVersion`. However, key unification is a
semantic change to mapping behavior and MUST be gated by `RelationalMappingVersion`:

- Producers MUST bump `RelationalMappingVersion` when key unification semantics are enabled in emitted artifacts.
- Consumers MUST reject mapping packs whose `relational_mapping_version` does not match the runtime’s expected value.

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
    - canonical columns are storage-only and have `SourceJsonPath = null`.

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

All nodes within a unification class MUST have compatible physical types.

More precisely: all members of a unification class MUST share the same physical **member signature** (fail fast
otherwise).

Compatibility rules:

- Scalar vs Scalar:
  - `DbColumnModel.Kind` MUST be `Scalar` for all members, and
  - `DbColumnModel.ScalarType` MUST be **exactly equal** across all members, including:
    - `ScalarKind`, and
    - `MaxLength` (for strings), and
    - `(Precision, Scale)` (for decimals).
- DescriptorFk vs DescriptorFk:
  - `DbColumnModel.Kind` MUST be `DescriptorFk` for all members,
  - physical storage MUST be `BIGINT` / `bigint` (as in the base redesign), and
  - `DbColumnModel.TargetResource` MUST be the **same** descriptor resource type across all members.
- Scalar and DescriptorFk MUST NOT be unified.

The derived model build MUST fail fast if incompatible types are unified.

### Canonical column type derivation (normative)

When a unification class is applied to a table, the builder MUST create exactly one storage-only canonical column for
that class and MUST derive its physical type deterministically from the class member signature.

Rules:

1. Compute the class signature from the first member path column.
2. Verify all other member path columns have the exact same signature per “Type compatibility” above (fail fast
   otherwise).
3. Create the canonical column with:
   - `DbColumnModel.Kind` = member `Kind` (`Scalar` or `DescriptorFk`)
   - `DbColumnModel.ScalarType` = member `ScalarType` (exact copy)
   - `DbColumnModel.TargetResource`:
     - `null` for `Scalar`, and
     - the member descriptor resource type for `DescriptorFk`

Notes:
- This design does **not** attempt to “widen” or “merge” type constraints (e.g., picking the larger of two string
  `MaxLength` values). Any mismatch is treated as a schema/design error and MUST fail fast to avoid silently changing
  DDL, query behavior, or parameter binding semantics.
- For descriptor-key unification, the canonical column stores the unified descriptor `DocumentId` (BIGINT), but retains
  the descriptor resource type in `TargetResource` so downstream consumers can remain resource-type safe.

### Canonical column nullability derivation (normative)

The canonical column nullability MUST be derived deterministically from the class member-path column nullability.

Rule:

- `CanonicalColumn.IsNullable = MemberPathColumns.All(m => m.IsNullable)`

Equivalently:
- If **any** member path column is required (`IsNullable=false`), the canonical column MUST be `NOT NULL`.
- The canonical column MAY be nullable only when **all** member path columns are nullable.

Rationale:
- A unification class represents a single logical value that may be absent when all binding sites are optional.
- If any binding site is required, the unified value is required for every row in that table scope, so the canonical
  storage column must be `NOT NULL` and write coalescing must always produce a non-null value.

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

1. Create a new canonical stored column for the unification class.
2. Convert all member path columns (the API-bound endpoints) into `UnifiedAlias` columns that point at the new
   canonical column, preserving:
   - existing column names, and
   - existing `SourceJsonPath` bindings.

The canonical column’s physical kind/type/nullability are derived per:
- “Canonical column type derivation (normative)”
- “Canonical column nullability derivation (normative)”

### Naming rules (deterministic)

The canonical column name must be deterministic for a fixed effective schema and must remain stable under identifier
shortening.

Canonical naming MUST NOT consult any `relational.nameOverrides`-modified physical column names.

- Rationale: key unification specifically addresses the case where per-site identity-part suffixes may differ due to
  overrides, while still representing a single logical value. Canonical naming must therefore be derived from
  **API JsonPath semantics**, not from potentially divergent overridden per-site column names.

#### Canonical base-name derivation from member `SourceJsonPath` (normative)

For each member path column in a `KeyUnificationClass`, derive a logical base-name token from the member’s
`DbColumnModel.SourceJsonPath` by:

1. Determining the member’s **binding-site prefix path**:
   - If the member `SourceJsonPath` is bound as a reference-identity value under a
     `DocumentReferenceBinding.ReferenceObjectPath`, then:
     - the binding-site prefix MUST be that `ReferenceObjectPath`, and
     - the member’s relative path is the reference-relative field path under the reference object.
     - Detection rule (required): locate the unique `DocumentReferenceBinding` whose `IdentityBindings[*].ReferenceJsonPath`
       equals the member’s `SourceJsonPath`. If none exists, the member is not a reference-identity binding.
   - Otherwise, the binding-site prefix MUST be the owning table’s `DbTableModel.JsonScope`.

2. Stripping the prefix segments from the member `SourceJsonPath` segments to produce a **relative segment list**.
   - The prefix MUST be a true prefix; otherwise fail fast (derived model bug).

3. Converting the relative segment list to a logical base-name token:
   - When the relative segment list contains one or more `Property` segments:
     - the base-name token is the concatenation of `ToPascalCase(property.Name)` for each `Property` segment, in order.
     - The relative segment list MUST NOT contain `AnyArrayElement` segments; if it does, fail fast:
       - row-local key unification cannot safely name or enforce “multi-value” endpoints that still contain wildcards
         after binding-site stripping.
   - When the relative segment list is empty (the `SourceJsonPath` equals the binding-site prefix):
     - This can occur for arrays-of-descriptor-strings where the value path is the array element itself (e.g.,
       `$.programDescriptors[*]`) and the table scope is that same wildcard path.
     - In this case, the base-name token MUST be derived from the last property segment in the prefix path:
       - take the final `Property` segment name before any trailing `AnyArrayElement`, and
       - apply `ToPascalCase` (and, when applicable, the same singularization rule used for descriptor-array column
         naming elsewhere in the redesign).
     - If there is no such property segment, fail fast.

Notes:
- This base-name derivation intentionally ignores any name overrides. It is derived solely from canonical JSONPath
  segments and therefore remains stable even when overrides make per-site suffixes differ.
- This derivation uses the full reference-relative or scope-relative property path (not only the leaf property name),
  which avoids ambiguity when leaf names are repeated under different inlined objects.

#### Canonical base-name selection across members (normative)

For a unification class, compute each member’s logical base-name token per the rules above.

- If all members have the **same** logical base-name token (ordinal comparison), use that token as the class base-name.
- Otherwise:
  - Choose the class base-name as the logical base-name token of the **first** member in `MemberPathColumns` order.
  - Mark the class as requiring a name disambiguator (see below).

Rationale:
- A disagreement is rare and indicates that the equality constraint relates two different-looking paths that are still
  logically unified (or that the effective schema is inconsistent). In either case, we must pick a deterministic name
  without silently “choosing” a semantic interpretation.

#### Canonical column naming template (normative)

Given:
- `Base` = class base-name token from the selection rule above, and
- `Hash8` = the first 8 hex characters of `sha256hex(utf8("key-unification-canonical-name:v1\n" + join(sorted(member SourceJsonPath.Canonical), "\n")))`
  (members sorted ordinal by `SourceJsonPath.Canonical`),

The canonical column name MUST be:

- For `Scalar` classes: `{Base}{Disambiguator}_Unified`
- For `DescriptorFk` classes: `{Base}{Disambiguator}_Unified_DescriptorId`

Where:
- `Disambiguator` is empty by default.
- `Disambiguator` MUST be `_U{Hash8}` when:
  - member base-name tokens disagree (per the selection rule above), or
  - the initial computed canonical column name collides with an existing column name on the table.

Collision handling (required):
- If `{Base}_Unified` (or `{Base}_Unified_DescriptorId`) collides and applying the `_U{Hash8}` disambiguator still
  collides (extremely unlikely but possible via overrides), append a deterministic numeric suffix before the unified
  suffix:
  - scalar: `{Base}_U{Hash8}_{n}_Unified`
  - descriptor: `{Base}_U{Hash8}_{n}_Unified_DescriptorId`
  - starting with `n = 2` and incrementing until a unique name is found.

The alias columns keep the existing per-site names (e.g., `StudentSchoolAssociation_StudentUniqueId`) so query
compilation and reconstitution can continue to bind by those names.

## Presence-Gated Alias Semantics

### Reference sites (DocumentId presence)

For a reference site `{RefBaseName}` and unified identity part canonical column `{Canonical}`:

- Alias column `{RefBaseName}_{IdentityPart}` is defined as:
  - `NULL` when `{RefBaseName}_DocumentId` is `NULL`
  - `{Canonical}` otherwise

This allows:

- per-site predicates to continue using `{RefBaseName}_{IdentityPart}` without additionally checking
  `{RefBaseName}_DocumentId IS NOT NULL`
- “all-or-none” nullability constraints to remain expressible at the per-reference-group level

### Non-reference paths (presence flags)

Equality constraints can also relate non-reference path columns (scalar values and `..._DescriptorId` columns). These
paths can be independently present/absent in the API payload, and Core’s equality validation only rejects
**conflicting values** (it does not require that all constrained paths are present).

To preserve query and reconstitution semantics under unification:

- Optional non-reference member path columns MUST be presence-gated with a synthetic presence flag column.
- Required non-reference member path columns MUST be ungated aliases (direct aliases of the canonical column).

For a member path column `{PathColumnName}` and canonical column `{Canonical}`:

#### Presence-flag column naming (normative)

When a member path column is optional (`IsNullable=true`), key unification introduces a synthetic presence flag column
used only for presence gating. Its name MUST be derived deterministically from the member path column name, with
deterministic collision handling.

Definitions:

- `MemberSourceJsonPath` = the member path column’s `DbColumnModel.SourceJsonPath`.
- The presence flag column name MUST NOT consult `relational.nameOverrides` directly (it is synthetic), but it is
  derived from the already-resolved member path column name (which may itself have applied overrides).

Rules:

1. Base name: `{PathColumnName}_Present`.
2. If the base name collides with any existing column name on the same table, apply a disambiguator:
   - `Hash8` = the first 8 hex characters of `sha256hex(utf8("key-unification-presence-name:v1\n" + MemberSourceJsonPath.Canonical))`.
   - Candidate name: `{PathColumnName}_U{Hash8}_Present`.
3. If the candidate name still collides (extremely unlikely but possible via overrides/shortening interactions),
   append a deterministic numeric suffix before `_Present`:
   - `{PathColumnName}_U{Hash8}_{n}_Present`, starting with `n = 2` and incrementing until a unique name is found.
4. Dialect identifier shortening may apply later; any renaming/shortening pass MUST update all
   `ColumnStorage.UnifiedAlias.PresenceColumn` references accordingly.

Let `{PresenceColumnName}` be the final selected name produced by the rules above.

1. If `{PathColumnName}` is required (`IsNullable=false`):
   - Do not create a `{PathColumnName}_Present` column.
   - Define alias column `{PathColumnName}` as an ungated alias of `{Canonical}`.
2. If `{PathColumnName}` is optional (`IsNullable=true`):
   1. Create a stored presence flag column `{PresenceColumnName}` with:
      - type: `bit` (SQL Server) / `boolean` (PostgreSQL)
      - nullability: nullable (`NULL` means absent; `1`/`TRUE` means present)
      - `SourceJsonPath = null` (storage-only; not an API binding column)
      - semantics:
        - `NULL` → `{PathColumnName}` was absent for this row
        - non-null (`1`/`TRUE`) → `{PathColumnName}` was present for this row
      - record `{PresenceColumnName}` as `ColumnStorage.UnifiedAlias.PresenceColumn` for `{PathColumnName}`
   2. Define alias column `{PathColumnName}` as:
      - `NULL` when `{PresenceColumnName}` is `NULL`
      - `{Canonical}` otherwise
   3. Flattening MUST write `{PresenceColumnName}` for each row:
      - present → `TRUE`/`1`
      - absent → `NULL`
      - flattening MUST NOT write `0`/`FALSE` (presence uses `NULL` vs `TRUE`)

This prevents canonical values supplied at one JsonPath from “leaking” into a different absent JsonPath during reads
or when evaluating predicates against the per-path binding columns.

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

Presence-flag-gated aliases (optional non-reference paths):

```sql
SchoolYear_Unified <type> NULL, -- canonical stored column

GradingPeriodSchoolYear_Present bit NULL, -- optional path presence gate

GradingPeriodSchoolYear AS (
  CASE
    WHEN GradingPeriodSchoolYear_Present IS NULL THEN NULL
    ELSE SchoolYear_Unified
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

Presence-flag-gated aliases (optional non-reference paths):

```sql
"SchoolYear_Unified" <type> NULL, -- canonical stored column

"GradingPeriodSchoolYear_Present" boolean NULL, -- optional path presence gate

"GradingPeriodSchoolYear" <type>
  GENERATED ALWAYS AS (
    CASE
      WHEN "GradingPeriodSchoolYear_Present" IS NULL THEN NULL
      ELSE "SchoolYear_Unified"
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

### Descriptor foreign keys (`dms.Descriptor`) (normative)

Descriptor endpoints are stored as `DescriptorFk` columns (`..._DescriptorId`) referencing
`dms.Descriptor(DocumentId)`.

Under key unification, descriptor binding columns may become `UnifiedAlias` columns and therefore be read-only. The
descriptor FK constraint MUST be anchored on the canonical stored column, not the binding alias.

Normative rules:

1. **Storage targeting**:
   - For each `DbColumnModel` where `Kind = DescriptorFk`, determine the storage FK column:
     - `Storage = Stored` → `StorageColumn = ColumnName`
     - `Storage = UnifiedAlias(CanonicalColumn, ...)` → `StorageColumn = CanonicalColumn`
2. **FK emission**:
   - Emit a FK constraint from `(StorageColumn)` to `dms.Descriptor(DocumentId)` with:
     - `ON DELETE NO ACTION`, and
     - `ON UPDATE NO ACTION`.
   - The DDL generator MUST NOT attempt to define FKs over `UnifiedAlias` columns.
3. **De-duplication**:
   - If multiple descriptor binding columns map to the same `StorageColumn` on the same table (e.g., because they are
     unified), emit exactly one descriptor FK constraint for that `(table, StorageColumn)` pair.
4. **Naming + supporting indexes**:
   - Descriptor FK constraint names MUST be derived from `(Table, StorageColumn)` (e.g.,
     `BuildDescriptorForeignKeyName(table, StorageColumn)`).
   - FK-supporting index derivation MUST use the final storage column list after canonical mapping and
     de-duplication (per the FK index policy in `ddl-generation.md`).

### Multi-edge cascades (canonical columns shared across composite FKs) (normative)

Key unification can cause a single canonical identity-part column to participate in **multiple composite reference FKs**
on the same referencing table.

Example (illustrative only):

- Table `A` has two independent reference sites to different targets `B` and `C`.
- `B` and `C` each carry the same logical identity part `StudentUniqueId`.
- ApiSchema emits `equalityConstraints` that relate the two identity JsonPaths in `A`, so key unification creates a
  single canonical column `StudentUniqueId_Unified` and converts the per-site columns into aliases.

Under this shape, `A` can end up with multiple composite FKs that share the same canonical identity-part column:

- `FK_A_B`: `(B_DocumentId, StudentUniqueId_Unified) → B(DocumentId, StudentUniqueId_Unified)`
- `FK_A_C`: `(C_DocumentId, StudentUniqueId_Unified) → C(DocumentId, StudentUniqueId_Unified)`

If `B` and `C` themselves depend on the same upstream identity and that upstream identity is updated with
`allowIdentityUpdates=true`, the cascade graph may contain **multiple update paths** that reach `A`.

This section defines the required cross-engine behavior and mitigation strategy.

#### PostgreSQL (supported; no special mitigation required)

PostgreSQL supports “cycles or multiple cascade paths” for FK cascades. Therefore, it is valid for DDL emission to use
declarative `ON UPDATE CASCADE` on all eligible edges (per the baseline design’s `allowIdentityUpdates` rule).

Key properties under unification:

- The historical PostgreSQL hazard was a `CHECK (colA = colB)` constraint across two **independently-cascaded writable**
  columns (a “mid-cascade” failure). Key unification removes that pattern by ensuring there is only one writable
  physical column for the unified value.
- When multiple composite FKs on a referencing row share the same canonical identity-part columns, cascaded updates are
  safe because the stored canonical key is updated atomically as a single value for that row (there is no second
  writable copy to drift).

Normative guidance:

- Do **not** introduce DB-level equality checks across two independently-cascaded stored columns as an alternative to
  unification. This design exists specifically to avoid that failure mode.
- Prefer the default FK behavior (`NOT DEFERRABLE`) and rely on normal FK cascade semantics; unification is intended to
  make the cascade behavior safe without requiring deferred constraints.
- DDL verification MUST include a fixture scenario where a single identity update fans out across multiple cascade
  paths that reach the same referrer table whose composite FKs share unified canonical columns, and MUST validate the
  identity update succeeds (no transient FK violations and no mid-cascade check failures).

#### SQL Server (DDL may be rejected; mitigation is trigger-based propagation fallback)

SQL Server may reject FK graphs that contain “cycles or multiple cascade paths”. This is a **DDL feasibility**
constraint, not an application policy decision. Under key unification, this can occur in the same places as in the
baseline design, and may become more common as unified canonical identity columns are shared across composite FKs.

Normative rules:

1. The DDL generator MUST attempt to use declarative `ON UPDATE CASCADE` for reference composite FKs only when:
   - the referenced target allows identity updates (`allowIdentityUpdates=true`), and
   - SQL Server accepts the resulting FK graph (no multiple-cascade-path/cycle rejection).
2. When SQL Server rejects `ON UPDATE CASCADE` for an otherwise-eligible edge due to cascade-path restrictions, the DDL
   generator MUST:
   - emit that FK with `ON UPDATE NO ACTION`, and
   - emit a deterministic, set-based trigger-based propagation fallback for that edge as
     `DbTriggerKind.IdentityPropagationFallback` (see `transactions-and-concurrency.md` and `07-index-and-trigger-inventory.md`).
3. Trigger-based propagation fallback MUST update the referencing table’s **canonical storage columns** for unified
   identity parts (never alias columns), because aliases are computed/read-only.

Required correctness properties for trigger-based propagation fallback:

- Set-based: handle multi-row updates of the referenced table and update all impacted referrers in one trigger
  execution.
- Old→new mapping: join `inserted`/`deleted` to map the referenced old composite key to the referenced new composite
  key, and update referrers that still match the old key.
- Idempotent under convergence: when multiple paths could update the same canonical value on a referrer, the trigger
  predicate SHOULD match on the old key values so rows already updated to the new canonical values are not updated
  redundantly.

### All-or-none nullability constraints

All-or-none constraints use the per-site alias columns, preserving the original meaning:

- If `{RefBaseName}_DocumentId` is `NULL` then all per-site identity aliases are `NULL`
- If `{RefBaseName}_DocumentId` is not `NULL` then all per-site identity aliases are not `NULL`

### UNIQUE constraints and indexes (binding vs storage)

Key unification introduces two different “column identities” for the same logical value:

- **Path/binding columns** (API semantics): the columns that retain `SourceJsonPath` and preserve the existing
  per-site/per-path names (often `ColumnStorage.UnifiedAlias`).
- **Canonical/storage columns** (DB semantics): the single stored/writable source-of-truth columns
  (`SourceJsonPath = null`).

As a result, UNIQUE constraints fall into two distinct categories with different correctness requirements.

#### 1) API-semantic UNIQUE constraints MUST use path/binding columns (aliases)

These are constraints whose purpose is to enforce API-visible uniqueness rules derived from ApiSchema:

- root natural-key UNIQUE (derived from `identityJsonPaths`), and
- collection element uniqueness UNIQUEs (derived from `arrayUniquenessConstraints`).

Normative rules:

1. API-semantic UNIQUE constraints MUST be derived from JsonPath endpoint bindings (`DbColumnModel.SourceJsonPath`) and
   MUST use the **path/binding column names** (even when those columns are unified aliases).
2. API-semantic UNIQUE constraints MUST NOT “collapse” or substitute member path columns with their canonical storage
   columns.

Rationale:

- Path/binding columns preserve the “presence semantics” used by the redesign:
  - optional paths remain `NULL` at the binding column when absent (via presence gating), and
  - predicates/constraints on a per-path column continue to imply that the path was present.
- If an API-semantic UNIQUE were defined over canonical columns, a value supplied at one binding site could
  inadvertently participate in uniqueness enforcement at a different *absent* binding site (because the canonical is
  shared), changing API semantics.

Notes:

- Existing rules about identity paths sourced from references remain unchanged:
  - when an identity/uniqueness path resolves to a reference identity value, the constraint binds to the reference
    `..._DocumentId` column (stable key) rather than propagated identity part columns.

#### 2) FK-supporting “referenced-key” UNIQUE constraints MUST use canonical/storage columns

Composite foreign keys require the referenced column set to be UNIQUE. In this redesign, many composite FKs target:

- `(DocumentId, <IdentityParts...>)` on a concrete root table, or
- `(DocumentId, <AbstractIdentityParts...>)` on an abstract identity table.

Under key unification, composite reference FKs are defined over **canonical storage columns** for unified identity
parts (see “Composite reference foreign keys” above). Therefore, any UNIQUE constraints whose sole purpose is “make
the composite FK legal” MUST be defined over the same canonical storage columns.

Normative rules:

1. When deriving a referenced-key UNIQUE for a composite FK target, the target column list MUST be:
   - `DocumentId`, plus
   - the target identity-part columns mapped to their **canonical storage columns**:
     - `Stored` → itself
     - `UnifiedAlias` → `UnifiedAlias.CanonicalColumn`
2. If two or more identity parts map to the same canonical column (because the identity schema contains duplicated
   endpoints that are equality-constrained), the derivation MUST de-duplicate the repeated canonical column name
   deterministically:
   - keep the **first** occurrence in identity-path order, and
   - drop subsequent duplicates.

Rationale:

- The composite FK must not reference read-only alias columns.
- The composite FK and its required referenced-key UNIQUE must agree on the same physical key shape to avoid
  mismatches and to keep identity propagation (`ON UPDATE CASCADE`) anchored on the canonical writable columns.

#### Index inventory (constraints imply indexes)

- UNIQUE constraints imply UNIQUE indexes:
  - API-semantic UNIQUEs are typically on binding columns (including persisted/stored aliases).
  - Referenced-key UNIQUEs are on canonical storage columns.
- FK-supporting indexes (per `ddl-generation.md` FK index policy) MUST be derived from the final FK column list after
  canonical mapping and de-duplication (if applicable).
- This redesign does not introduce any additional “query” indexes for unified aliases beyond those implied by
  constraints; query-index derivation remains out of scope.

## Runtime Implications

### Writes (flattening)

- Only canonical columns are writable for unified identity parts.
- Flattening must populate canonical columns from the JSON payload using the table’s `KeyUnificationClass` inventory per
  the “Canonical value coalescing (normative)” and “Required write-time guardrails (fail-fast)” rules above.
- Per-site alias columns are not written.

### Reads (reconstitution)

- Reconstitution continues to read from per-path binding columns (aliases) for unified values.
- Presence gating ensures absent optional reference sites and absent optional scalar paths do not “inherit” canonical
  values from other sites/paths.

### Queries

- Query compilation can continue to bind predicates to the per-path binding column names (aliases).
- Presence gating preserves the existing semantics where filtering on a per-site/per-path value implies that
  site/path was present.

### Triggers (stamping + identity maintenance) under unified aliases (normative)

Key unification introduces generated/computed **alias columns** (`ColumnStorage.UnifiedAlias`) whose values can change
when their canonical column changes, even though the alias columns themselves are **read-only**.

As a result, trigger implementations that care about “identity projection columns changed” MUST NOT rely on
“column updated” detection (because aliases are never written directly):

- PostgreSQL: do not use `UPDATE OF <aliasColumn>` to detect identity projection change.
- SQL Server: do not use `IF UPDATE(<aliasColumn>)` (or similar) to detect identity projection change.

Instead, triggers MUST treat “changed” as a **value-diff** between the pre-image and post-image of the row, where a
unified member’s value is the **presence-gated expression** derived from `ColumnStorage.UnifiedAlias`:

- `Stored` column value: `<Column>`
- `UnifiedAlias` column value:
  - when `<PresenceColumn>` is null: `<CanonicalColumn>`
  - otherwise: `CASE WHEN <PresenceColumn> IS NULL THEN NULL ELSE <CanonicalColumn> END`

This makes identity change detection robust to:

- FK-cascade updates (or SQL Server trigger-based propagation fallbacks) that update canonical storage columns, and
- presence changes that gate an alias between `NULL` and a canonical value.

#### `DbTriggerKind.DocumentStamping` (stamps `dms.Document`)

Implementation guidance (dialect-neutral semantics):

- Fire on `INSERT`, `UPDATE`, and `DELETE` for each schema-derived table (as described in `update-tracking.md`).
- Compute `affectedDocumentIds` from `inserted ∪ deleted` (dedupe).
- Always bump **Content** stamps for `affectedDocumentIds` (representation changed).
- Compute `identityChangedDocumentIds` by diffing the document’s identity projection values between `inserted` and
  `deleted` (null-safe comparison). For unified members, diff the **presence-gated canonical expression** above (not
  the alias column name).
- Bump **Identity** stamps only for `identityChangedDocumentIds`.

#### `DbTriggerKind.ReferentialIdentityMaintenance` + `DbTriggerKind.AbstractIdentityMaintenance`

These triggers must use the same value-diff gating:

- A trigger may execute on every `UPDATE`, but it MUST compute its workset as the rows whose identity projection
  values differ between `inserted` and `deleted` (null-safe).
- For unified identity members, the identity value is the presence-gated canonical expression above.

This guarantees that cascades / propagation-fallback updates to canonical columns correctly cause referential-id and
abstract-identity maintenance, even though alias columns are read-only.

Design note (applies to `07-index-and-trigger-inventory.md` and any DDL emission docs):

- Any phrase like “`UPDATE` when identity-projection columns change” MUST be interpreted as “identity projection
  values are distinct between old/new row images (null-safe), using the `UnifiedAlias` expression for unified
  members”, not “the column appears in the SET list”.

## Integration Point + Pass Ordering (DMS-1033) (normative)

Key unification MUST be applied during derived-model-set compilation (E01) as a set-level pass over the full effective
schema set. It MUST run late enough that all candidate endpoint columns exist (including propagated reference identity
columns), and early enough that all downstream consumers (constraint derivation, index/trigger inventory, plan
compilation, manifests, and DDL generation) see a unified model.

### Recommended placement in `RelationalModelSetPasses` order

Recommended set-level pass order (relative to the current default implementation in
`src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/Build/RelationalModelSetPasses.cs`):

1. `BaseTraversalAndDescriptorBindingPass`
2. `DescriptorResourceMappingPass`
3. `ExtensionTableDerivationPass`
4. `ReferenceBindingPass`
5. **`KeyUnificationPass` (new)** ← applies canonical columns + presence-gated aliases + `KeyUnificationClasses`
6. `AbstractIdentityTableAndUnionViewDerivationPass`
7. `RootIdentityConstraintPass`
8. `ReferenceConstraintPass`
9. `ArrayUniquenessConstraintPass`
10. `ApplyConstraintDialectHashingPass`
11. *(When implemented in E01)* `IndexAndTriggerInventoryPass` (DMS-945)
12. `ApplyDialectIdentifierShorteningPass`
13. `CanonicalizeOrderingPass`

Notes:

- `KeyUnificationPass` MUST run **after** `ReferenceBindingPass` so unified classes can include per-site propagated
  identity-part columns (`{RefBaseName}_{IdentityPart}`) whose `SourceJsonPath` binds equality-constraint endpoints.
- `KeyUnificationPass` MUST run **after** `ExtensionTableDerivationPass` so any equality-constraint endpoints that bind
  to extension tables can still be resolved deterministically (even when the resulting constraint is out-of-scope for
  DB-level unification due to cross-table scope).
- `KeyUnificationPass` MUST run **before** any constraint derivation pass that needs to target canonical storage
  columns (notably reference composite FKs), and before any pass that builds SourceJsonPath-based lookups that must see
  the post-unification table/column inventory.
- If E01 derives index/trigger inventories (DMS-945), that pass SHOULD run **after**
  `ApplyConstraintDialectHashingPass` so PK/UK-implied index names that mirror constraint names reflect the final
  hashed constraint identifiers.

### Required postconditions for downstream passes

After `KeyUnificationPass`:

- All canonical storage columns and any synthetic presence-flag columns MUST already be present in the owning table’s
  `DbTableModel.Columns`.
- Every unified member path column MUST remain present under its existing column name and retain its existing
  `SourceJsonPath` binding, but MUST be marked as a `UnifiedAlias` that points to the canonical storage column and
  presence gate.
- `DbTableModel.KeyUnificationClasses` MUST be populated for any table with one or more unification classes.

These postconditions allow subsequent passes to be purely consumer-oriented:

- Constraint derivation passes use `SourceJsonPath` for endpoint resolution and `ColumnStorage` for storage/FK
  targeting.
- Index/trigger inventory derivation (when present) can treat canonical columns as the physical “writable” identity
  columns while still emitting query/reconstitution-facing metadata in terms of member path columns.
- Dialect identifier shortening can rename columns and update all `UnifiedAlias` and `KeyUnificationClass` references
  in one place.

## Interactions with Existing Design Docs

This design refines the baseline language in:

- `data-model.md` (reference identity columns)
- `ddl-generation.md` (“Reference constraints” and the “Key unification note”)
- `transactions-and-concurrency.md` (reference propagation semantics)

Those documents currently describe per-site identity part columns as independent physical columns. Under key
unification, some of those columns become generated/computed aliases.

## Verification Checklist (required minimum coverage)

This checklist is a minimum required verification set for implementing Option 3 key unification safely across:

- derived-model compilation (E01),
- DDL generation and provisioning,
- write planning and flattening,
- read/reconstitution and query binding, and
- triggers / identity maintenance.

The intent is to prevent “it works for the happy path” implementations that silently regress determinism, presence
semantics, or cascade correctness.

### Derived-model build (E01)

- `SourceJsonPath` endpoint resolution remains unambiguous:
  - each API JsonPath binds to exactly one `DbColumnModel` (binding column), and
  - canonical columns (`SourceJsonPath = null`) never introduce endpoint ambiguity.
- Unification class construction is correct and deterministic:
  - edges are built only for endpoints that resolve to the same physical table, and
  - connected components produce stable classes with deterministic member ordering.
- Equality-constraint diagnostics surface is emitted and deterministic:
  - every `equalityConstraint` is classified as `applied` or `skipped`,
  - `skipped` includes a machine-readable reason (`unresolved_endpoint`, `unsupported_endpoint_kind`,
    `cross_table`), and
  - output ordering and undirected endpoint normalization are stable.
- Derived-model validation fails fast on:
  - incompatible member types (scalar kind/type metadata mismatch, descriptor target resource mismatch),
  - scalar-vs-descriptor mixing in one class, and
  - “multi-value” endpoints that still contain `[*]` after binding-site stripping (where prohibited by naming and
    row-local assumptions).
- Canonical column derivation is correct:
  - `Kind`, `ScalarType`, and `TargetResource` match the class signature exactly,
  - `SourceJsonPath = null`,
  - `Storage = Stored`.
- Canonical nullability derivation is correct:
  - canonical is `NOT NULL` when any member is required.
- Canonical naming and collision handling are deterministic and stable under overrides:
  - base-name derived from JsonPath semantics (not overrides),
  - `_U{Hash8}` disambiguator when required,
  - deterministic numeric fallback when collisions persist.
- Presence-gated alias behavior is correct:
  - reference-site aliases gated by `{RefBaseName}_DocumentId`,
  - optional non-reference aliases gated by synthetic `..._Present`,
  - required non-reference aliases are ungated.
- Synthetic `..._Present` columns:
  - deterministic naming, including collision handling and dialect shortening compatibility,
  - correct physical type and nullability (`NULL` vs `TRUE`/`1` only),
  - storage-only (`SourceJsonPath = null`, `Storage = Stored`).

### DDL emission (PostgreSQL + SQL Server)

- Physical column order respects dependencies:
  - canonical storage columns and any synthetic presence flag columns are emitted before dependent aliases.
- Generated/computed alias column syntax is correct per dialect and matches `UnifiedAlias(Canonical, Presence)` rules.
- No FK attempts to reference `UnifiedAlias` columns:
  - composite reference FKs use canonical storage columns for unified identity parts,
  - descriptor FKs use canonical storage columns for unified descriptor parts.
- Descriptor FK constraints:
  - if descriptor endpoints unify, the table emits exactly one FK anchored on the canonical storage column
    (de-duplicated by `(table, StorageColumn)`).
- All-or-none constraints remain on reference-group binding columns (aliases) plus `..._DocumentId`.
- API-semantic UNIQUE constraints are defined over binding/path columns (aliases allowed) and do not collapse to
  canonicals.
- FK-supporting referenced-key UNIQUE constraints are defined over canonical storage columns (after mapping + de-dup).
- FK-supporting index derivation uses the final FK column list after canonical mapping and de-duplication.
- SQL Server cascade feasibility:
  - `ON UPDATE CASCADE` is used only when accepted by SQL Server’s cascade-path rules,
  - rejected edges use `ON UPDATE NO ACTION` plus trigger-based propagation fallback that updates canonical storage
    columns (never aliases).

### Write planning + flattening

- Plan compiler excludes `UnifiedAlias` columns from:
  - `INSERT`/`UPDATE` column lists, and
  - parameter bindings.
- Canonical columns and synthetic `..._Present` columns are represented as precomputed bindings and are always
  populated deterministically for every materialized row.
- Coalescing behavior matches the normative rules:
  - JSON `null` treated as absent,
  - first-present member wins (deterministic ordering),
  - absent everywhere → canonical `NULL`.
- Conflict detection is enforced:
  - two or more present, non-null members disagree after conversion → fail closed,
  - descriptor members compare on resolved `DocumentId`, not raw URI string.
- Guardrails are enforced before issuing SQL:
  - when any gated member site/path is present, canonical must be non-null,
  - when canonical is `NOT NULL`, canonical must be non-null.
- Descriptor resolution failures fail closed:
  - descriptor URI present but unresolved → write fails.
- PUT/replace semantics remain deterministic:
  - missing unified values do not consult existing row values to “retain” canonical values.

### Read / reconstitution / query semantics

- Optional reference-site absence remains correct:
  - per-site alias columns read as `NULL` when `..._DocumentId` is `NULL`, even if canonical is non-null due to another
    site.
- Optional non-reference path absence remains correct:
  - per-path alias reads as `NULL` when `..._Present` is `NULL` (no cross-path leakage).
- Query binding preserves presence semantics:
  - predicates on binding columns continue to imply the path/site was present.

### Triggers (stamping + identity maintenance)

- Triggers do not rely on “updated column” checks for alias columns (aliases are read-only).
- Identity-change detection uses value diffs between old/new row images:
  - unified members use the presence-gated canonical expression, not the alias column name.
- Cascades and SQL Server trigger-based propagation fallbacks that update canonicals still trigger correct stamping and
  maintenance behavior (no missed recomputes).
- SQL Server trigger implementations are set-based and correct under multi-row statements.

### AOT mapping pack + manifests

- Relational-model manifests include:
  - per-column storage metadata (`Stored` vs `UnifiedAlias` with canonical + optional presence), and
  - per-table `KeyUnificationClass` inventory (canonical + ordered members),
  - per-resource applied/skipped equality-constraint diagnostics.
- Mapping-pack payload carries the same unification metadata and validates invariants at build/load time:
  - alias canonical/presence references exist and are stored,
  - canonical columns are storage-only,
  - member-path columns are distinct, resolvable, and reference the correct canonical.
- Plan payload carries precomputed bindings and key-unification write plans with consistent binding indices.
- `RelationalMappingVersion` gating is enforced (packs with mismatched version are rejected).

### Determinism / ordering / shortening

- Output ordering is stable for a fixed effective schema:
  - unification classes, member ordering, manifests, and DDL object emission are deterministic.
- Dialect identifier hashing/shortening updates all unification references:
  - `UnifiedAlias(CanonicalColumn, PresenceColumn)` pointers, and
  - `KeyUnificationClass` member lists.
- No dangling pre-shortening/pre-hashing column names remain in manifests, plans, constraints, indexes, or triggers.

## Pending Questions

- Why the legacy Ed-Fi ODS schema sometimes does not physically unify columns that are related by ApiSchema
  `equalityConstraints` (e.g., DS 5.2 `Grade` uses both `SchoolYear` and `GradingPeriodSchoolYear` - is this a bug in
  ApiSchema generation?), and whether DMS should unify those cases anyway (and if so, how to select canonical vs alias columns).
