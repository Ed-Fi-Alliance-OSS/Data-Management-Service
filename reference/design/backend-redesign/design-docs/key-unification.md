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
  - canonical storage columns must have `SourceJsonPath = null` so a single API JsonPath never resolves to both an alias
    and a canonical column.

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
- DescriptorFk vs DescriptorFk (both are stored as `BIGINT` / `bigint` document ids)
- Scalar and DescriptorFk are not compatible

The derived model build must fail fast if incompatible types are unified.

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
- Flattening must populate canonical columns from the JSON payload, using a deterministic coalescing strategy across all
  equality-constrained JSON paths in the unification class.
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

- Whether descriptor identity parts stored as `..._DescriptorId` participate in key unification.
- Whether scalar (non-reference) columns that are equality-constrained should always keep per-path alias columns, or
  whether some duplicates can be dropped without affecting query/reconstitution behavior.
- The canonical-column naming rule when multiple per-site identity-part suffixes differ due to name overrides or when
  the leaf property name is ambiguous.
- The canonical-column nullability rule when a unification class contains a mix of required and optional endpoints.
- The write-time coalescing rule for canonical columns when some equality-constrained JSON paths are absent and others
  are present, and what error behavior is required when conflicting values are supplied.
- Whether backend write paths should re-validate equality constraints at flatten time as a fail-closed guardrail, or
  rely exclusively on Core validation.
- Whether a canonical column shared across multiple composite FKs with `ON UPDATE CASCADE` can introduce transient FK
  violations during multi-edge cascades, and what the mitigation strategy is for PostgreSQL and SQL Server.
- Whether the derived relational model needs explicit metadata to distinguish canonical vs alias columns for:
  - write plan compilation
  - read plan compilation
  - constraint derivation
  - DDL generation
- Whether the identifier-shortening pass can change canonical/alias naming decisions and how to keep the result stable
  across dialects.
