# The Problem With Removing `ReferentialId`s (Write-Path Impacts)

## Status

Draft (analysis note).

## Context

The baseline backend redesign keeps a deterministic UUIDv5 `ReferentialId` and persists it in a single identity index:

- `dms.ReferentialIdentity(ReferentialId → DocumentId)` (see `reference/design/backend-redesign/data-model.md`), and
- a metadata-driven write path that resolves *all* references in a request by bulk looking up `ReferentialId`s (see `reference/design/backend-redesign/transactions-and-concurrency.md`).

The question explored here is: what happens if we make a radical change and remove `ReferentialId`s entirely?

This note focuses only on the **write path** impacts: what additional work is required, how it would work, and the expected performance differences versus keeping `ReferentialId`.

Related baseline rationale: `reference/design/backend-redesign/overview.md` (“Why keep ReferentialId”).

## What `ReferentialId` buys the baseline design

The baseline “bulk resolve references” story works because `ReferentialId` is:

- **Uniform** across all resource types (and descriptors), so resolution is one query shape.
- **Deterministic** (UUIDv5), so Core can compute it from request identity values and the backend can compute it for query-time filters if needed.
- **Compact**: reference resolution can use a single `IN (...)`-style set lookup with narrow keys.
- **Polymorphism-friendly** via alias rows (superclass/abstract `ResourceKeyId` mapping to the same `DocumentId`), enabling abstract reference resolution without per-subtype SQL.

Removing `ReferentialId` deletes this uniform identity index and forces the backend to resolve references by **natural keys** against **per-resource tables**.

## If we remove `ReferentialId`, what must replace it?

You still need to turn “reference identity values in the request” into `DocumentId` foreign keys before you can write relational rows.

Without `dms.ReferentialIdentity`, the backend needs a new **natural-key resolver** that:

1. Understands, per target resource type, which relational columns represent its natural identity (from ApiSchema-derived metadata).
2. Batches lookups (to avoid N+1).
3. Handles reference-bearing identities (identities that include other references).
4. Handles abstract/polymorphic targets.
5. Produces correct error reporting tied back to request JSON locations.

This is doable, but it moves a large amount of complexity and DB work onto the write path.

## Write-path changes required (additional work and how)

### 1) Per-resource natural-key resolution replaces one bulk lookup

**Baseline**
- Dedupe all extracted references in the request by `ReferentialId`.
- One bulk lookup: `SELECT ReferentialId, DocumentId FROM dms.ReferentialIdentity WHERE ReferentialId IN (...)`.

**Without `ReferentialId`**
- Group extracted references by `QualifiedResourceName` (resource type).
- For each distinct referenced resource type `R`, execute a batched lookup against `{schema}.{R}` using its natural key columns.

This immediately increases query count from “~1” to “# referenced resource types”, and that’s before dealing with reference-bearing identities or abstract targets.

### 2) Batching mechanics (PostgreSQL and SQL Server)

To keep round-trips “almost constant”, you need set-based staging per resource type.

**PostgreSQL options**
- `VALUES (...)` table + `JOIN`:
  - `WITH keys(k1,k2,...) AS (VALUES (...),(...)) SELECT ... FROM keys JOIN edfi.Student s ON (...)`
- `UNNEST` over arrays of key parts (careful with alignment and null handling).
- Temporary table (`CREATE TEMP TABLE ... ON COMMIT DROP`) populated via `COPY` or multi-row `INSERT`, then joined.

**SQL Server options**
- Table-valued parameter (TVP) with a user-defined table type for the key shape of `R`.
- `#temp` table populated via `SqlBulkCopy` or batched inserts, then joined.

Why you must do this:
- SQL Server has practical parameter-count limits; trying to build giant `WHERE (k1=@p1 AND k2=@p2) OR ...` predicates is not viable.
- Both engines need stable, index-driven join plans; staging tables/TVPs are the standard approach.

### 3) Reference-bearing identities force multi-pass resolution (or denormalization)

This is the largest behavioral change.

Many Ed-Fi resources have identities that include references (example pattern: an association identity includes `StudentReference + SchoolReference + BeginDate`).

When resolving a reference to such a resource from API identity values, you have two general approaches:

#### Approach A: Multi-pass resolution (dependency layering)

1. Build a dependency graph of identity references between resource types from ApiSchema.
2. Resolve “leaf” identities first (self-contained identities like `SchoolId`, `StudentUniqueId`).
3. Use resolved `..._DocumentId` values to resolve the next layer of resources whose identities include those references.
4. Iterate until all required referenced documents are resolved.

This becomes a request-scoped closure computation (but on the request’s *reference set*, not on persisted reverse edges).

Complications:
- You need to detect and handle cycles (rare but possible in modeled data).
- You must guarantee deterministic ordering and handle “not found” reporting at the right JSON location.
- Even with batching, the number of DB passes can be > 1 if the request includes references across multiple dependency layers.

#### Approach B: Denormalize referenced identity values (ODS-style propagation)

Instead of multi-pass, store the identity-bearing referenced natural key parts as columns alongside `..._DocumentId` FKs, and keep them consistent via cascades/triggers.

This is effectively the “identity-only natural-key propagation” pattern.

It can make *some* lookups “row-local” and remove recursion, but it is a major DDL and trigger/cascade commitment:
- extra columns
- extra unique constraints
- composite FKs
- engine-specific cascade constraints (especially on SQL Server)

Even in this denormalized model, you still need per-resource resolution by natural keys unless you add yet another central index.

### 4) Polymorphic (abstract) reference targets get significantly harder

The baseline uses `dms.ReferentialIdentity` alias rows to resolve abstract identities without per-subtype SQL and without requiring clients to specify a concrete subtype.

Without `ReferentialId`, you lose the “single identity index with polymorphic aliases” mechanism. To preserve Ed-Fi abstract reference behavior, the write path now has to:

- Resolve abstract natural keys to a concrete `DocumentId` across potentially many subtype tables (including extensions).
- Validate abstract membership (“this resolved `DocumentId` is a valid member of abstract type A”), since many relational FKs will still be `..._DocumentId → dms.Document(DocumentId)` for existence-only enforcement.
- Standardize identity renames and key surfaces across subtypes for resolution (e.g., if the abstract identity surface differs from a subtype’s natural key naming).
- Carry and cache a heavier key: `(AbstractType, key parts...) → DocumentId`, rather than a single UUID.

Resolution typically becomes one of:

- **Union view resolution**:
  - Query `{Abstract}_View` (or a generated union view) by abstract natural key columns to get `DocumentId`.
  - Cost: the DB must consider multiple subtype branches; worst-case this behaves like “try N subtype indexes” per lookup row (even when index-driven), and the view definition grows with extensions.
  - Risks: optimizer sensitivity, plan instability, and cross-engine divergence (what’s acceptable on PostgreSQL may regress on SQL Server).

- **Abstract identity table**:
  - Maintain a `{schema}.{Abstract}Identity` table keyed by abstract natural key → `DocumentId`, updated by triggers from each concrete subtype.
  - This reintroduces a central index per abstract type (functionally similar to what `dms.ReferentialIdentity` provided for polymorphism), but with:
    - more objects (one table per abstract type),
    - more write amplification (subtype writes must also upsert the identity table),
    - and a drift risk that requires audit/rebuild tooling.

Either way, removing `ReferentialId` makes polymorphic references a first-order write-path concern (more branching, more DDL, more performance variability) instead of a uniform “compute UUID and look up once” mechanism.

### 5) Upsert (POST) existence detection becomes per-resource natural-key lookup

Baseline POST upsert detection:
- compute request `ReferentialId`, do one lookup in `dms.ReferentialIdentity` to find `DocumentId` if it exists.

Without `ReferentialId`:
- the backend must query the target resource table by its natural key columns to find `DocumentId`.
- for reference-bearing identities, this inherits the same multi-pass or denormalization requirements as reference resolution.

You still need a relational unique constraint on natural keys to enforce correctness and detect races, but the read-before-write existence check becomes more expensive and more engine-sensitive.

### 6) Error reporting still needs “location” metadata, plus more mapping logic

The baseline already expects Core to provide concrete JSON locations for references inside collections (`reference/design/backend-redesign/flattening-reconstitution.md`).

Without `ReferentialId`, you still need those locations, but now you also need:
- per-resource “which JSON identity fields map to which columns” for building staged key rows,
- per-key-row “not found” mapping back to a concrete request location.

This is doable, but it increases complexity in the request-scoped reference-resolution layer.

## Expected performance impact vs keeping `ReferentialId`

### 1) More queries / more DB passes

With `ReferentialId`:
- 1 bulk lookup covers all referenced resource types, plus descriptor checks.

Without `ReferentialId`:
- ≥ 1 lookup per referenced resource type, plus:
  - additional passes if reference-bearing identities require multi-pass resolution, and/or
  - additional branches for abstract targets.

Even with good batching, the minimum number of round-trips grows with “how many different things are referenced” and with the depth of identity dependencies.

### 2) Wider joins and heavier indexing

`ReferentialId` lookups use a narrow UUID index.

Natural-key lookups require composite indexes over multiple columns (often including strings), which:
- are larger in bytes,
- are more sensitive to collation/normalization mismatches,
- increase buffer/cache pressure,
- and can be slower for both seeks and joins, especially under heavy write load.

### 3) Higher plan variability and cross-engine divergence

One query shape (`dms.ReferentialIdentity`) is easier to tune and validate across PostgreSQL and SQL Server.

Per-resource natural-key resolution produces many query shapes:
- different key widths and data types per resource,
- different selectivity per resource,
- different join strategies chosen by the optimizer.

This increases the chance of “works on Postgres, regresses on SQL Server” and makes performance testing more expensive.

### 4) Increased CPU and memory in the API layer

Building per-resource staged key sets and mapping results back to request locations costs:
- more allocations (composite keys),
- more grouping/sorting work per request,
- more complex retry/partial failure handling.

### 5) Net assessment

For reference-heavy payloads and for schemas with many resource types (especially with extensions), removing `ReferentialId` is expected to:

- increase write latency (more DB work and more passes),
- increase DB CPU and IO (composite joins and larger indexes),
- increase risk of N+1-like regressions if batching is imperfect,
- and increase cross-engine divergence risk.

The baseline design keeps `ReferentialId` largely to avoid these exact costs (see `reference/design/backend-redesign/overview.md` “Why keep ReferentialId”).

## If we still want to remove `ReferentialId`: practical mitigation options

If the real goal is “don’t store UUID referential ids”, but we still want a single uniform identity index, the system will tend to reinvent an equivalent:

1. **Central “identity hash” index**
   - Store a deterministic hash of `(ResourceType + ordered identity paths + values)` → `DocumentId`.
   - This is functionally the same as `ReferentialId`, just not UUID-shaped.

2. **Per-abstract/per-resource identity tables**
   - Create `{Resource}Identity` tables keyed by natural key → `DocumentId`.
   - This reduces join width on main tables but increases object count and complexity; abstract targets still require special handling.

3. **Commit to denormalization + cascades**
   - ODS-style identity propagation can reduce some recursion but increases DDL/triggers/cascade-path constraints.

Each of these options has non-trivial complexity and operational cost; they are not “free simplifications” compared to keeping `ReferentialId`.

## Recommendation

Unless there is a strong constraint that forbids storing or computing `ReferentialId`s, removing them is a net negative for the write path:

- it replaces one uniform bulk lookup with a complex, per-resource, often multi-pass natural-key resolver,
- and it increases both implementation complexity and performance risk across engines.

If the project decides to pursue “no `ReferentialId`”, treat it as a major redesign with explicit investment in:
- dependency-ordered batching (TVPs/temp tables),
- abstract-target resolution structures,
- heavy cross-engine performance validation,
- and clear operational guardrails for worst-case fan-in scenarios.
