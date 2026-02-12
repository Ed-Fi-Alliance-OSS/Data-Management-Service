# Backend Redesign: Key Unification (Canonical Columns + Generated Aliases; Presence-Gated When Optional)

## Status

Draft. First cut of the Option 3 design described in `key-unification-issue.md`.

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
- Ensure canonical storage columns and any synthetic presence flag columns appear before aliases in the physical column
  order (dependency).

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
shortening. The baseline rule for first cut:

- Use the identity-part base name suffix that already appears in `{RefBaseName}_{IdentityPart}` (or the scalar leaf
  name when unifying scalar paths).
- If that name collides with an existing column (including any member path column that must be preserved), apply a
  deterministic collision suffix.

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

1. If `{PathColumnName}` is required (`IsNullable=false`):
   - Do not create a `{PathColumnName}_Present` column.
   - Define alias column `{PathColumnName}` as an ungated alias of `{Canonical}`.
2. If `{PathColumnName}` is optional (`IsNullable=true`):
   1. Create a stored presence flag column `{PathColumnName}_Present` with:
      - type: `bit` (SQL Server) / `boolean` (PostgreSQL)
      - nullability: nullable (`NULL` means absent; `1`/`TRUE` means present)
      - `SourceJsonPath = null` (storage-only; not an API binding column)
      - semantics:
        - `NULL` → `{PathColumnName}` was absent for this row
        - non-null (`1`/`TRUE`) → `{PathColumnName}` was present for this row
      - record `{PathColumnName}_Present` as `ColumnStorage.UnifiedAlias.PresenceColumn` for `{PathColumnName}`
   2. Define alias column `{PathColumnName}` as:
      - `NULL` when `{PathColumnName}_Present` is `NULL`
      - `{Canonical}` otherwise
   3. Flattening MUST write `{PathColumnName}_Present` for each row:
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

## Pending Questions

- The canonical-column naming rule when multiple per-site identity-part suffixes differ due to name overrides or when
  the leaf property name is ambiguous.
- Whether a canonical column shared across multiple composite FKs with `ON UPDATE CASCADE` can introduce transient FK
  violations during multi-edge cascades, and what the mitigation strategy is for PostgreSQL and SQL Server.
- Why the legacy Ed-Fi ODS schema sometimes does not physically unify columns that are related by ApiSchema
  `equalityConstraints` (e.g., DS 5.2 `Grade` uses both `SchoolYear` and `GradingPeriodSchoolYear` - is this a bug in
  ApiSchema generation?), and whether DMS should unify those cases anyway (and if so, how to select canonical vs alias columns).
- Whether the identifier-shortening pass can change canonical/alias naming decisions and how to keep the result stable
  across dialects.
