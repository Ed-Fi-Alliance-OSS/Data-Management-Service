# Key Unification Design Summary

## What Problem Does This Solve?

The DMS backend redesign stores reference identity parts as independent physical columns per reference site (e.g., `StudentSchoolAssociation_StudentUniqueId` and `StudentEducationOrganizationAssociation_StudentUniqueId`). When two reference sites carry the same logical identity value, ApiSchema `equalityConstraints` declare they must be equal — but the database has two separate writable columns that can drift apart via direct SQL writes, bugs, or partial updates.

A naive fix like `CHECK (colA = colB)` fails under PostgreSQL `ON UPDATE CASCADE` because cascades update the two columns in separate statements, triggering a "mid-cascade" check failure.

## Core Idea

Replace duplicated writable columns with a **single canonical stored column** plus **generated/computed alias columns** that project the canonical value under the original column names. Aliases are **presence-gated** so that absent optional reference sites or paths continue to read as `NULL`.

## Key Concepts

### Canonical Column

- A single writable physical column per unification class (a transitive closure of equality-constrained identity parts on the same table row).
- Storage-only: `SourceJsonPath = null`. Not visible to API endpoint resolution — reachable only through alias metadata.
- Participates in composite FKs and cascades.

### Alias Column

- A read-only generated/computed persisted column that retains the original per-site column name and `SourceJsonPath`.
- Queries, reconstitution, and API-semantic UNIQUE constraints continue to bind to alias columns.
- Never written directly; never referenced by FKs.

### Presence Gating

Aliases evaluate to `NULL` when their binding site is absent, preventing values from "leaking" across paths:

| Member type | Presence column | Alias evaluates to |
|---|---|---|
| Reference-site identity part | `{RefBaseName}_DocumentId` | `NULL` when DocumentId is `NULL`, else canonical |
| Optional non-reference path | Synthetic `{PathColumnName}_Present` (boolean, `NULL` vs `TRUE`) | `NULL` when presence flag is `NULL`, else canonical |
| Required non-reference path | *(none — ungated)* | Always canonical |

### Identity-Lineage Anchor

- Every target intrinsically inventories/stores a `DocumentId` for each reference-backed identity lineage.
- Every incoming reference site's anchor demand starts empty.
- Receiver-side full-FK validity/correlation adds only the anchors that site needs; demand propagates only through
  downstream identity/constraint consumers to a least fixed point.
- A demanded local anchor reuses an existing `..._DocumentId` only when complete identity equivalence and co-presence are
  proved; otherwise it gets dedicated storage.
- Omission is permitted only with proof that no receiver obligation needs the intrinsic anchor across every mutation
  subset/simultaneous combination.

## Derived Model Metadata

### Column-Level: `ColumnStorage`

Every `DbColumnModel` carries a `Storage` discriminator:

- **`Stored`** — normal writable column (default for all columns before `KeyUnificationPass`).
- **`UnifiedAlias(CanonicalColumn, PresenceColumn?)`** — read-only alias of a canonical column, optionally presence-gated.

### Table-Level: `KeyUnificationClass`

Each table with unification carries an ordered inventory:

```
KeyUnificationClass(
    CanonicalColumn,
    MemberPathColumns[]   // ordered by SourceJsonPath.Canonical
)
```

This tells writers how to populate the canonical column from an API document when the canonical has no `SourceJsonPath`.

## Consumer Behavior Rules

### Binding (Queries + Reconstitution)

- Bind by `SourceJsonPath → ColumnName` as before.
- Read from alias columns to preserve presence semantics.

### Storage (Writes + FKs + Cascades)

- **Never** write `UnifiedAlias` columns.
- **Always** write the canonical column, even though it has no `SourceJsonPath`.
- Composite reference FKs target canonical storage columns for unified identity parts.
- Full propagation vectors append only the site's demanded lineage anchors and stable target `DocumentId`.
- Equal demand sets use deterministic propagation-key/`RefKey` variants keyed by `AnchorSetId`; intrinsic target anchors are
  not blanket-copied, and each reference still has exactly one full FK.
- Descriptor FKs target canonical storage columns (de-duplicated per `(table, StorageColumn)`).
- All-or-none constraints cover the site `..._DocumentId`, per-site aliases, and every dedicated demanded local anchor
  (preserving "absent reference site → every selected vector item NULL").

### API-Semantic vs FK-Supporting Constraints

| Constraint type | Column set |
|---|---|
| Root natural-key UNIQUE, collection-element UNIQUE | Binding/alias columns |
| FK-supporting referenced-key UNIQUE | Canonical public identity storage + site-demanded lineage anchors + target `DocumentId` |
| Composite reference FK (local + target) | Same full propagation vector |
| All-or-none CHECK | Site `..._DocumentId` + alias columns + every dedicated demanded local anchor |
| Presence-flag hardening CHECK | Synthetic `..._Present` columns (`NULL OR TRUE`) |

## Write-Time Behavior

### Canonical Value Coalescing (Per Row)

1. Evaluate each member path against the request row's JSON scope → zero-or-one candidate values per member.
2. JSON `null` = absent. Resolve descriptors to `DocumentId`.
3. **Conflict detection**: if 2+ members are present with different non-null values → fail closed.
4. **Proposed canonical value** = first present member in `MemberPathColumns` order. No members present → `NULL`.
5. Baseline unprofiled writes use that value directly. Profiled matched writes first overlay Core's member/scope states:
   hidden preserves stored canonical value/presence, visible-absent clears only when no hidden owner remains, and
   visible-present applies the proposed value.
6. Run conflict/guardrail checks on the effective merged value, then write `CanonicalColumn`.

### Write-Time Guardrails (Fail-Fast)

- If a gated member's presence column is non-null (site/path is present), the canonical value must be non-null.
- If the canonical column is `NOT NULL`, the computed value must be non-null.
- These checks run before issuing SQL, producing actionable errors instead of DB constraint violations.

### Plan Compiler + Flattener

- Exclude `UnifiedAlias` columns from INSERT/UPDATE column lists.
- Canonical and synthetic `..._Present` columns use `WriteValueSource.Precomputed`.
- A `KeyUnificationWritePlan` per class describes how to compute the canonical and presence values.
- Two-phase row materialization: (1) populate non-precomputed bindings, (2) evaluate unification plans.

## Canonical Column Naming

Deterministic naming derived from API JsonPath semantics (not from override-mutated physical column names):

1. For each member, derive a base-name token by stripping the binding-site prefix (reference object path or table scope) from the member's `SourceJsonPath` and PascalCasing the remaining property segments.
2. If all members agree on the base-name, use it. Otherwise, use the first member's token.
3. Column name template:
   - Scalar: `{Base}{Disambiguator}_Unified`
   - DescriptorFk: `{Base}{Disambiguator}_Unified_DescriptorId`
4. `Disambiguator` = `_U{Hash8}` only when the initial name collides with an existing column. `Hash8` is the first 8 hex chars of a SHA-256 over sorted member `SourceJsonPath` values.

## Dialect DDL

### PostgreSQL

```sql
"StudentUniqueId_Unified" varchar(32) NOT NULL,  -- canonical stored

"StudentSchoolAssociation_StudentUniqueId" varchar(32)
  GENERATED ALWAYS AS (
    CASE WHEN "StudentSchoolAssociation_DocumentId" IS NULL THEN NULL
         ELSE "StudentUniqueId_Unified"
    END
  ) STORED
```

### SQL Server

```sql
StudentUniqueId_Unified varchar(32) NOT NULL,  -- canonical stored

StudentSchoolAssociation_StudentUniqueId AS (
  CASE WHEN StudentSchoolAssociation_DocumentId IS NULL THEN NULL
       ELSE StudentUniqueId_Unified
  END
) PERSISTED
```

## Cascade Safety

- A shared canonical receiver column removes duplicate storage; it does **not** prove update safety. Independent mutable
  parents, or a mutable parent plus an immutable FK, can read the same receiver column and invalidate one another.
- Least-fixed-point demand starts every incoming site empty and adds a target-intrinsic lineage `DocumentId` only when
  receiver validity/correlation requires it. DS 5.2 `CourseOffering -> Session` demands the School anchor because
  `SchoolId_Unified` is also read by `CourseOffering -> School`; an unrelated Session referrer does not.
- Demand propagates only through downstream identity/constraint consumers. Omission still proves every reference
  retargeting and simultaneous component-change case.
- Logical references are storage-mapped and physically de-duplicated before actions are assigned. Physical identity is
  `(semantic FK kind, local table, ordered local columns, target table, ordered target columns, ON DELETE)` and excludes
  update action/mode, logical path, and generated name; incompatible contributor metadata is an error, not a duplicate
  FK.
- **PostgreSQL** directly uses fixed eligible-`CASCADE`/immutable-`NO ACTION` actions. DMS does not prune, classify,
  certify, or fail PostgreSQL cascade topology.
- **SQL Server** alone derives `ValueFlowAnalysis` obligations and globally selects `NativeCascade`, `NoPropagation`, or
  `ImmutableNoAction` to satisfy them and error 1785. Cycles are breakable action-choice constraints, not hard failures.
- Every success-only `NoPropagation` decision is keyed by `PhysicalForeignKeyId`; its certificates identify the selected
  `AnchorSetId` and complete `MutationCaseId` and contain typed
  changed-target and receiver-carrier routes, complete selected-vector equality, separate origin/receiver row
  correlation, presence, and constraint timing. A carrier may be zero-hop `OriginWrite`.
- Certificates cover complete mutation cases. Primitive proofs may be reused only with typed
  `SubsetCompositionProof`; missing composition is `UnprovedSubsetComposition`.
- An undischarged SQL Server obligation throws `RelationalModelDerivationException`, not a partial success artifact.
  Every FK stays full-composite; no identity-value propagation trigger exists.

## Triggers Under Unification

- Triggers must **not** use "column updated" detection for alias columns (they are never written directly).
- Identity-change detection uses **value diffs** between old/new row images, where unified members use the presence-gated canonical expression.
- This applies to document stamping, referential identity maintenance, and abstract identity maintenance triggers.

## Deriving Unification Classes from ApiSchema

### Scope

- **Row-local only**: both endpoints must bind to columns on the same physical table row.
- **Cross-table** constraints (root↔child, child↔child, base↔extension) are ignored (remain Core-only).
- **Cross-row** constraints (e.g., all elements in a collection share a value) are ignored.

### Endpoint Resolution

- Uses `DbColumnModel.SourceJsonPath` as the sole authoritative mapping.
- Zero matches → fail fast. Multiple matches to different `(table, column)` → fail fast.

### Classification

| Condition | Classification |
|---|---|
| Both endpoints same table, different columns | **Applied** (contributes to unification class) |
| Both endpoints same `(table, column)` | **Redundant** (no-op) |
| Endpoints on different tables | **Ignored** (Core-only, reason: `cross_table`) |
| Endpoint unresolved | **Fail fast** (error) |
| Unsupported endpoint kind | **Fail fast** (error) |

### Type Compatibility

All members of a class must share the exact same physical signature:
- Scalar: same `ScalarKind`, `MaxLength`, `(Precision, Scale)`.
- DescriptorFk: same `TargetResource`. Stored as `BIGINT`.
- Scalar and DescriptorFk must not be mixed.

### Canonical Nullability

`CanonicalColumn.IsNullable = ALL members are nullable`. If any member is required, the canonical is `NOT NULL`.

## Pass Ordering

`KeyUnificationPass` runs before physical reference-FK derivation:

1. BaseTraversalAndDescriptorBindingPass
2. DescriptorResourceMappingPass
3. ExtensionTableDerivationPass
4. ReferenceBindingPass
5. **KeyUnificationPass** ← new
6. AbstractIdentityTableAndUnionViewDerivationPass + shared `AbstractIdentityMemberMapping` inventory
7. ValidateUnifiedAliasMetadataPass
8. RootIdentityConstraintPass
9. TransitiveIdentityMutabilityPass
10. IdentityLineageAnchorClosurePass
    - intrinsic target lineage inventory + empty initial site demands
    - receiver validity/correlation demand to a least fixed point
    - equal demand sets become `AnchorSetId` propagation-key/`RefKey` variants
11. ReferenceConstraintPass internal phases:
    - storage-map public values + site-demanded anchors, then physical de-duplication
    - PostgreSQL fixed assignment, or SQL Server-only value-flow + global 1785 selection
    - final FK emission
12. Remaining constraint, naming, inventory, shortening, and canonical-ordering passes

**After** ReferenceBindingPass (so propagated identity-part columns exist) and **before** constraint derivation passes (so they see the unified model).

## Manifest + Mapping Pack

- Success manifests include per-column `storage`, per-table `key_unification_classes`, equality-constraint diagnostics,
  descriptor FK de-duplication, provider-neutral typed anchor-omission proofs for both dialects, and success-only SQL
  Server decisions with ordered `CoverageCertificates` for `NoPropagation` modes.
- Successful compilation returns `DerivedRelationalModelArtifact(Model, Diagnostics, ExecutorRequirements)`. SQL Server classification failures
  throw `RelationalModelDerivationException`; a partial model is never serialized as a success manifest.
- Mapping packs carry runtime-required unification metadata plus each expanded local/target vector, stable
  `PhysicalForeignKeyId`, selected `AnchorSetId`, and both final referential actions. They omit derivation-only SQL Server
  modes/certificates/composition proofs; consumers do not rerun demand closure.
- This is the pre-production v1 baseline; no `RelationalMappingVersion` bump, migration, compatibility discriminator, or
  physical-model hash is required.
- All metadata is represented structurally — no SQL text, no naming-convention inference.

MetaEd (METAED-1667) and DMS consume the same versioned conformance corpus with separate `metaEd`, `dmsPostgresql`, and
`dmsSqlServer` outcomes. PostgreSQL outcomes never contain classifier rejection; SQL Server owns decisions and
certificates. Cross-table/root-to-child equality propagation remains separate future work and cannot serve as FK
coverage evidence.

## Worked Example: Synthetic Presence Flags

Two optional scalar endpoints `$.fiscalYear` and `$.localFiscalYear` are equality-constrained. Their base-name tokens disagree ("FiscalYear" vs "LocalFiscalYear"), so the class base-name is chosen as the first member’s token. Because `FiscalYear_Unified` does not collide on the table, no `_U{Hash8}` disambiguator is required:

| Column | Role | Storage |
|---|---|---|
| `FiscalYear_Unified` | Canonical (nullable int) | Stored |
| `FiscalYear_Present` | Presence flag (nullable boolean) | Stored |
| `LocalFiscalYear_Present` | Presence flag (nullable boolean) | Stored |
| `FiscalYear` | Alias → canonical, gated by `FiscalYear_Present` | UnifiedAlias |
| `LocalFiscalYear` | Alias → canonical, gated by `LocalFiscalYear_Present` | UnifiedAlias |

Write scenarios:
- Both absent → canonical `NULL`, both flags `NULL`.
- Only `$.fiscalYear = 2025` → canonical `2025`, `FiscalYear_Present = TRUE`, `LocalFiscalYear_Present = NULL`.
- Only `$.localFiscalYear = 2025` → canonical `2025`, `FiscalYear_Present = NULL`, `LocalFiscalYear_Present = TRUE`.
- Both present, same value → canonical from first member, both flags `TRUE`.
- Both present, conflicting → fail closed.

Read behavior for the "only `$.fiscalYear = 2025`" scenario above:
- `FiscalYear` returns `2025` — its presence flag is `TRUE`, so the alias evaluates to the canonical value.
- `LocalFiscalYear` returns `NULL` — its presence flag is `NULL`, so the alias is masked even though the canonical column holds `2025`. No cross-path leakage.
