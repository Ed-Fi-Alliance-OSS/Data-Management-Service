# Natural-Key Reference Resolution: Removing UUIDv5 `ReferentialId`

> **Status: PROPOSED — for team review. Nothing described here has landed on `main`; the design is
> written in future tense throughout.** Once approved, implementation tickets will be created under
> [`../epics/`](../epics/). Once implemented, this document will supersede the
> earlier `ReferentialId` retention rationale (see
> ["Response to the earlier analysis"](#response-to-the-earlier-analysis) for why the balance has
> changed), and the passages of [overview.md](overview.md), [data-model.md](data-model.md),
> [ddl-generation.md](ddl-generation.md), and [transactions-and-concurrency.md](transactions-and-concurrency.md) that describe
> `dms.ReferentialIdentity` as current.
>
> **`dms.Document` will not be affected by this proposal in any way.** It will remain inserted,
> row-locked, the authoritative source of `DocumentId`/`DocumentUuid`/version metadata, the
> GET/PUT/DELETE lookup target, and the base of the DocumentCache enqueue triggers. Every UUIDv5
> referential-id artifact is in scope for removal: the `dms.ReferentialIdentity` table, generated
> maintenance, database helpers, Core model members/calculators/factories, backend lookup/write
> contracts, and tests or fixtures that seed or assert them.

## Summary

`dms.ReferentialIdentity` maps a deterministic UUIDv5 hash of each document's identity
(`ReferentialId`) to its `DocumentId`. It exists so that reference resolution, POST upsert detection,
and descriptor filter resolution can each be answered by one uniform, narrow index lookup.

This design will remove the table, stop computing UUIDv5 `ReferentialId` values anywhere in DMS,
and answer each of those questions with a batched natural-key lookup against the schema's existing
natural-key indexes. The only new lookup index is the
descriptor lower-URI index; abstract resolution adds a `smallint ResourceKeyId` payload column to
existing abstract identity rows but does not add another index:

| Lookup | Will be replaced by |
|---|---|
| Concrete document references | Scalar probe of the target's `UX_<Target>_RefKey` |
| Abstract (polymorphic) references | Probe of `UX_<Abstract>Identity_RefKey`, projecting the concrete `ResourceKeyId` |
| Descriptor references and filters | Probe of `UX_Descriptor_UriLowered_ResourceKeyId` (new lower-expression/computed-column index) |
| POST upsert detection | The document's own `UX_<R>_NK` |

On SQL Server, those lookups will not inherit identity equality from the database default collation.
The DDL generator will explicitly apply DMS's default case-insensitive collation,
`SQL_Latin1_General_CP1_CI_AS`, to every string column that stores or copies an identity value. This
includes canonical natural-key columns, flattened RefKey copies, abstract-identity columns, and
string members of collection identity constraints. A case-sensitive database default will remain
supported and unchanged; it simply will not govern DMS identity columns. Runtime identity comparers
will be selected from this same schema contract (`OrdinalIgnoreCase` for the SQL Server contract and
`Ordinal` for PostgreSQL), not from a general assumption about the database engine.

With the reads gone, the maintenance surface will go with them: every generated
`TR_<R>_ReferentialIdentity` trigger, the `dms.uuidv5()` function on both engines, the PostgreSQL
`pgcrypto` extension (uuidv5 is its only consumer in the DMS database), the SQL Server
`dms.UniqueIdentifierTable` table-valued parameter type, and the Core/backend C# surfaces that
produce, carry, accept, return, compare, or test UUIDv5 referential ids.

The migration will be **code-only and re-provision-only**; no in-place upgrade scripts will be
provided. This is a prerelease schema-shape change within the unreleased `v2` mapping line, so it
does not bump `RelationalMappingVersion` by itself. Databases provisioned from an earlier prerelease
shape are not guaranteed to be mechanically rejected by the fingerprint check; environments must
re-provision after picking up these changes. Once a mapping version has been released, later
incompatible mapping changes must bump `RelationalMappingVersion` so stale released databases fail
fast with the designed 503.

The resolver design was validated end to end by a complete working prototype: differential tests
proved old-vs-new resolution equivalence over seeded databases on both engines, benchmarks compared
the two resolvers under bulk, deep-identity, and small-batch workloads (results below), and the full
PostgreSQL + SQL Server integration suites and E2E ran green on the prototype. The explicit SQL
Server identity-column collation is a design-review correction after that prototype. Its required
case-sensitive-database coverage is therefore an implementation gate described under
["Test strategy"](#test-strategy), not claimed as completed prototype evidence.

## Why remove it

Four reasons, in decreasing order of weight:

**1. The schema already paid for the replacement.** When the baseline rationale and follow-up
analysis were written, removing the hash meant *building* natural-key resolution machinery: denormalized
identity columns, composite indexes, abstract identity tables. Since then, all of that machinery was
built anyway, for reasons unrelated to reference resolution:

- **`UX_<R>_RefKey`** — the fully-flattened scalar identity plus `DocumentId` — exists on every
  referenced resource as the composite-FK target that keeps reference binding columns consistent
  under cascades ([key-unification.md](key-unification.md)). Because identity flattening is recursive,
  a reference-bearing identity (e.g., a Section, whose identity contains a CourseOffering reference,
  whose identity contains Course and Session references) collapses to **one flat list of scalars** —
  resolvable in a single index seek, no multi-pass dependency layering.
- **`UX_<R>_NK`** — the natural-key unique constraint with reference-sourced parts as
  `..._DocumentId` columns — exists as the identity-uniqueness enforcement and the source of
  create-race unique violations (the 409/retry path).
- **`<Abstract>Identity` tables** with their own `RefKey` indexes exist to enforce cross-subclass
  identity uniqueness and to serve as polymorphic FK targets. This design will add the concrete
  member `ResourceKeyId` to those rows so abstract reference resolution can return the same
  compatibility token the runtime uses today. Resolution is still one probe of one table — not a
  union over subtype tables.

The descriptor-specific probe target missing was a case-insensitive descriptor lookup, which this
design will add as a lower-storage unique index on the existing `dms.Descriptor` table: a PostgreSQL
expression index, and a SQL Server non-persisted computed-column index. Descriptor URI identity will
be ASCII-only so the lowercasing contract is deterministic across C#, PostgreSQL, and SQL Server.

**2. The hash is derived state, and derived state has carrying costs.** Every document insert and
identity update fires a generated trigger that recomputes UUIDv5 hashes and writes one or two
`dms.ReferentialIdentity` rows (subclasses also write a superclass-alias row). That is write
amplification on the hottest path in the system. Because the table *can* drift from the row state it
is derived from, the resolver carries corruption-verification CTEs (a canary comparing request
identity against re-projected root state) — complexity that exists only to detect the failure mode
the derived state itself introduces. Natural-key lookups will read the rows directly; the disease
class and its canary will both disappear.

**3. The hash disagrees with the database about equality.** `ReferentialId` is computed over the
identity's exact bytes — ordinal, case-sensitive. Today's generated SQL Server identity columns
inherit their database's collation; under the standard case-insensitive default, their unique
constraints therefore disagree with the hash. A case-variant re-POST hashes to a *different*
`ReferentialId`, misses the lookup, attempts an insert, and fails on the CI unique index — surfacing
as a 409 for a request ODS would have accepted. A database provisioned with a case-sensitive default
behaves differently, which demonstrates why "SQL Server is case-insensitive" is not a sufficient
contract. This is the same "application equality disagrees with collation equality" disease class
that an Ed-Fi ODS advisory (April 2022) documented for NHibernate-era casing bugs. The natural-key
design will move matching into the database and explicitly collate every SQL Server identity string
column, so matching and storage will share the DMS-declared equality definition — making the
deliberate casing contract below independent of the containing database's default collation.

**4. The resolver was validated before being proposed.** Unlike the original analysis, which
correctly flagged performance risk as unknown, this design comes with measurements: the natural-key
resolver is *faster* than the hash resolver in most measured cases on both engines (see
["Performance validation"](#performance-validation)). The later explicit-collation correction does
not change the index or query shapes measured under the same standard SQL Server CI collation; its
case-sensitive-database correctness is covered by the new implementation gates.

## Response to the earlier analysis

The earlier removal analysis recommended against removal. Its concerns were correct for the model
era in which it was written; each is addressed structurally today:

| Concern (2025 analysis) | Status today |
|---|---|
| "Reference-bearing identities force multi-pass resolution or denormalization" | Denormalization happened — for FK-cascade reasons, not resolution ([key-unification.md](key-unification.md)). `RefKey` flattening collapses transitive identity chains to one seek per reference. There is no multi-pass, no dependency layering, no cycle handling. |
| "Polymorphic targets get significantly harder; abstract identity tables reintroduce central indexes with drift risk" | Abstract identity tables already exist and are already trigger-maintained — for cross-subclass uniqueness enforcement. Resolution will reuse them and add only a concrete-member `ResourceKeyId` column to the existing rows; no new tables, indexes, trigger families, or extra row writes are introduced for polymorphism. |
| "Batching needs TVPs/temp tables; parameter limits make giant OR-predicates non-viable" | Batching will be one prepared statement per target group, all groups in **one command, one round trip**: PostgreSQL will use typed `unnest` parallel arrays (SQL text independent of batch size); SQL Server will shred one JSON parameter per group via `OPENJSON … WITH` under `OPTION (FORCE ORDER)`. No TVPs, no temp tables. A guard will enforce SQL Server's parameter budget (`MssqlCommandLimits.MaxUserParametersPerCommand`). |
| "≥1 lookup per referenced resource type; round trips grow" | One multi-statement command will be one round trip regardless of group count; inside the composite write pipeline, a union-projection form will embed the entire lookup as a *single statement* so existing round-trip counts will not regress (enforced by command-stream pinning tests). |
| "Many query shapes → plan variability, cross-engine divergence" | The shapes will be generated from compiled metadata (uniform per group); the prototype differential-tested them old-vs-new over seeded databases on both engines and validated performance (see below). The SQL Server input-shape risk was real — two candidate shapes failed measurably in the prototype — and the surviving shape will be pinned by tests. |
| "Error reporting needs per-key-row location mapping" | Every batched key row will carry an explicit ordinal; results will map back by `Entries[ordinal-1]`, never by row position. Unmatched ordinals will flow into the existing reference-validation failure response unchanged. |
| "The system will tend to reinvent an equivalent central index" | Correct — and the equivalent already exists as the `RefKey`/`NK`/abstract-identity indexes the schema maintains for FK and uniqueness reasons. Removal will delete the *redundant* copy, not the mechanism. |

## What `dms.ReferentialIdentity` does today

The table: `dms.ReferentialIdentity (ReferentialId uuid PRIMARY KEY, DocumentId, ResourceKeyId)` with
a unique index on `(DocumentId, ResourceKeyId)` and FKs to `dms.Document` (ON DELETE CASCADE) and
`dms.ResourceKey`. On SQL Server the PK is nonclustered with a clustered unique index on
`(DocumentId, ResourceKeyId)`.

Writers:

- Generated `TR_<R>_ReferentialIdentity` triggers on every resource root table (subclasses write a
  second superclass-alias row so abstract references resolve).
- Hand-written SQL in the descriptor write handler (insert CTE/batch plus `ON CONFLICT`/`MERGE`
  upsert).

Runtime readers (each will become an implementation ticket):

| Consumer | Today | After this change |
|---|---|---|
| Reference resolution | `ReferenceResolver` → per-engine lookup builders joining `dms.ReferentialIdentity` | `NaturalKeyReferenceResolver` probing `RefKey`/abstract-identity/descriptor indexes |
| Corruption-canary verification | CTEs comparing request identity vs re-projected root state | Deleted — nothing derived left to verify |
| POST upsert detection | The composite write path's capture predicate (a `ReferentialId` subselect) plus a standalone fallback lookup | Natural-key capture predicate + `UX_<R>_NK` fallback probe |
| Descriptor upsert detection | `ReferentialId` probe in the descriptor write handler | Lowered-URI + `ResourceKeyId` probe |
| Descriptor-valued query filters | Query preprocessor lowercases + hashes the URI | Same preprocessor; the resolver will probe the descriptor lower-URI index instead |
| 409 duplicate-identity messages | Rebuilds NK column lists from `ReferentialIdentityMaintenance` trigger metadata | Re-sourced from compiled natural-key probe metadata (severed *before* the triggers drop) |

Verified non-consumers (these will be untouched by this design): row locking (`dms.Document` by `DocumentId`),
DELETE (captures by `DocumentUuid`; the only interaction was the ON DELETE CASCADE), GET-by-id,
`?id=` queries, link injection, ownership authorization, stamping and tracked-change triggers, change
queries, and the entire DocumentCache path.

## The replacement design

### The natural-key reference resolver

A new `NaturalKeyReferenceResolver` will own the resolver role. The DI extension names can stay, but
the request/result contracts will stop carrying referential ids. Its input will be each reference's
fully-flattened `DocumentIdentity`, which is always present at the Core/backend boundary.

No UUIDv5 value will survive as a request-scoped memo key. DMS will replace the hash-as-identity
memo with a request-local structural key over `(requested resource, ordered DocumentIdentity
elements)`. The implementation must not rely on `DocumentIdentity` record equality, because it wraps
arrays; it must use a structural `IEqualityComparer` that combines hash codes only for dictionary
bucketing and always performs full memberwise equality for the identity verdict. Collisions are
therefore harmless. The comparer must reproduce the current request-local semantics exactly:
ordinal equality over regular identity values, and the ASCII-lowercased URI as the descriptor key
member. On SQL Server, case-variant spellings of one reference may remain separate memo entries and
produce redundant probe rows; the database will still resolve both to the same `DocumentId`, and the
structural comparer will never mis-merge two distinct identities.

The Core cleanup is part of this design, not a follow-up. `ReferentialId`,
`ReferentialIdFactory`, `ReferentialIdCalculator`, `No.ReferentialId`, and every
`ReferentialId` member on `DocumentReference`, `DescriptorReference`, `SuperclassIdentity`, and
`DocumentInfo` will be removed. Extractors and middlewares that currently share the UUIDv5 key will
move together to the structural natural-key comparer so Core does not compute a UUIDv5 value for
documents, references, descriptors, duplicate-item validation, or write-target setup.

Per request, the resolver will:

1. Dedupe extracted references (memo keyed by the structural natural-key identity).
2. Convert identity strings to typed values once, using the same scalar-literal parser the write
   flattener uses. This sameness is itself a correctness property: the values probed are the values
   that would be written, so resolution and storage cannot disagree about a conversion.
3. Group references by target resource; emit **one command** with one statement and one result set
   per group. One round trip will resolve everything.

Per-group statement shapes (column names illustrative — real names come from the compiled model):

**Concrete targets** — equality probe of the target's `RefKey` index, one seek per reference,
`DocumentId` read from the same index. PostgreSQL will bind typed parallel arrays; the SQL text will
be independent of how many references are in the batch:

```sql
SELECT keys.ordinal, target."DocumentId"
FROM unnest(@ordinals, @schoolIds, @schoolYears, @sessionNames)
     WITH ORDINALITY AS keys(ordinal, "SchoolId", "SchoolYear", "SessionName")
JOIN edfi."Session" target
  ON target."SchoolReference_SchoolId" = keys."SchoolId"
 AND target."SchoolYearTypeReference_SchoolYear" = keys."SchoolYear"
 AND target."SessionName" = keys."SessionName"
```

SQL Server will shred one JSON parameter per group; the OPENJSON input will always be the leftmost
input in the join tree, and every statement will end with `OPTION (FORCE ORDER)` (the rationale is
measured — see
["Performance validation"](#performance-validation)):

```sql
SELECT keys.ordinal, target.[DocumentId]
FROM OPENJSON(@sessionKeys) WITH (
    ordinal     int           '$.o',
    SchoolId    int           '$.k1',
    SchoolYear  smallint      '$.k2',
    SessionName nvarchar(60)  '$.k3'
) AS keys
INNER JOIN edfi.[Session] AS target
    ON  target.[SchoolReference_SchoolId] = keys.SchoolId
    AND target.[SchoolYearTypeReference_SchoolYear] = keys.SchoolYear
    AND target.[SessionName] = keys.SessionName COLLATE SQL_Latin1_General_CP1_CI_AS
OPTION (FORCE ORDER);
```

**Descriptor-valued identity parts** (a descriptor URI inside a target's identity) will resolve with
an inline `dms.Descriptor` join on the lowered URI key plus a compile-time `ResourceKeyId` literal
for the descriptor type.

**Descriptor targets** — a probe of `UX_Descriptor_UriLowered_ResourceKeyId` projecting
`(Ordinal, DocumentId, ResourceKeyId)`. `ResourceKeyId` will read `dms.Descriptor`'s own NOT NULL
column (DMS-1251) and will be the authoritative descriptor-type key for lookup and uniqueness.
`Discriminator` remains stored for diagnostics/read compatibility, but descriptor resolution will
not depend on it.

**Abstract targets** — a probe of `UX_<Abstract>Identity_RefKey` projecting
`(Ordinal, DocumentId, ResourceKeyId)`. `ResourceKeyId` is the concrete member resource key stored on
the abstract identity row and populated by the abstract-identity trigger from the same compile-time
member metadata that supplies the diagnostic `Discriminator`. The resolver will not parse or map the
abstract `Discriminator`; `IncompatibleTargetType` will continue to compare the resolved concrete
`ResourceKeyId` with the target's allowed concrete resource keys. The abstract `RefKey` and `NK`
index key shapes will remain unchanged; `ResourceKeyId` is payload only, not part of abstract
identity equality. One table, one seek, no per-subtype SQL.

Results will map by explicit ordinal (`Entries[ordinal-1]`, never row position); unmatched ordinals
will flow into the unchanged reference-validation failure response, so error shapes and JSON-location
attribution will be identical to today. On SQL Server a group-count guard will enforce the shared
parameter budget (`MssqlCommandLimits.MaxUserParametersPerCommand`, 2098) before building the command.

The probe metadata (`NaturalKeyProbeTargets`, `OwnNaturalKeyProbesByResource`,
`DescriptorProbeTarget` on the compiled `MappingSet`) will be compiled from the relational model
itself — never from trigger metadata, never by parsing constraint names (dialect identifier
shortening hash-truncates names), and never by converting abstract discriminator strings to resource
keys at runtime. It will be storage-resolved, so key-unified identity parts will bind their canonical
stored columns and abstract probes will bind the stored concrete `ResourceKeyId`. The probe metadata
itself will not be serialized into manifests, so it will cause zero golden-manifest churn.

### POST upsert detection

The composite write path (DMS-1332) captures and locks the POST/PUT target as statement 1 of the
first-phase composite command. Today that capture matches via a `ReferentialId` subselect. It will
be replaced by a natural-key predicate over the aliased `dms.Document` row `d`:

```sql
d."DocumentId" = (
    SELECT root."DocumentId"
    FROM edfi."Section" root
    WHERE root."SectionIdentifier" = @p1
      AND root."CourseOffering_DocumentId" = (
          SELECT t."DocumentId"
          FROM edfi."CourseOffering" t
          WHERE t."CourseReference_CourseCode" = @p2
            AND t."CourseReference_EducationOrganizationId" = @p3
            AND t."SchoolReference_SchoolId" = @p4
            AND t."SessionReference_SchoolYear" = @p5
            AND t."SessionReference_SessionName" = @p6)
)
AND d."ResourceKeyId" = @rk
```

Key properties:

- The capture statement runs **before** the reference-lookup statement in the same command, so
  reference-sourced natural-key parts cannot bind resolved `DocumentId`s. They will resolve inline:
  each reference-sourced part will be a subselect seeking the target root's `RefKey` (0-or-1 rows by
  RefKey uniqueness); descriptor-valued parts will use a `dms.Descriptor` lowered-URI +
  `ResourceKeyId` subselect. All parts will bind from the payload's flattened `DocumentIdentity` —
  scalars directly.
- **Miss semantics will be correct by construction:** a missing referenced document will make its
  subselect yield NULL, the predicate false, and nothing captured; the write will proceed down the
  create path, and the later reference-lookup statement will report the missing reference through
  the existing 409 flow. Denial precedence (stored authorization beats reference failure) will be
  unchanged because it is statement order.
- **Cost** will be one extra seek per reference-sourced natural-key part at capture time — bounded by
  natural-key *width*, not identity depth (RefKey flattening collapses transitive chains). If this
  shape underperforms in practice, the contingency ladder below applies; the accepted last resort —
  beyond the ladder — is reverting the composite write-path batching (DMS-1332) itself, which
  restores an ordering where resolved reference ids are available to a flat capture probe.
- All parameters will be renameable scalars, as the composite statement rewriter requires.

The ordered-segments fallback path (where references *have* already been resolved) will use a flat
probe of the document's own `UX_<R>_NK`, binding resolved `..._DocumentId` values and payload
scalars, with metadata projected through the `dms.Document` join:

```sql
SELECT root."DocumentId", d."DocumentUuid", d."ContentVersion"
FROM edfi."Section" root
INNER JOIN dms."Document" d ON d."DocumentId" = root."DocumentId"
WHERE root."CourseOffering_DocumentId" = @p0
  AND root."SectionIdentifier" = @p1
```

A missing resolved reference will short-circuit to the create path without probing; more than one
row will be an invariant violation and throw.

**Create races will be unchanged:** two concurrent POSTs of the same new identity will still race to
the `UX_<R>_NK` unique constraint, and the loser will be classified into the existing 409/retry flow.
The 409 `duplicateIdentityValues` message machinery will re-source its column lists from the compiled
probe metadata instead of trigger metadata — a strict prerequisite, to be landed before the triggers
drop.

**Contingency ladder** (pre-agreed; climb only on measurement):

1. *Baseline:* the inline-subselect capture predicate above — zero schema change, uniform for all
   resources.
2. *If its benchmark case lags:* capture via the resource's **own `UX_<R>_RefKey`** using flattened
   payload scalars — a flat single seek, no subselects, still zero schema change. Cost: a second
   capture shape, because never-referenced resources have no RefKey and keep shape 1.
3. *Only if both measurably fail:* re-shape `UX_<R>_NK` onto flattened scalars. Uniqueness-preserving
   but rejected as a default — it makes the per-resource index pair wide+wide, doubles cascade churn
   (today's NK is cascade-stable via `..._DocumentId` columns), and re-litigates the key-unification
   design. If ever pursued, it is a separately-specced initiative.

**Rejected outright:** dropping `UX_<R>_NK` in favor of RefKey. RefKey's trailing `DocumentId` key
column makes its uniqueness vacuous (it exists to satisfy composite-FK exact-match rules), so NK is
the *sole* natural-key uniqueness enforcement and the source of create-race 409s. Removing it would
admit silent duplicate identities.

### Embedding in the composite write path

The composite write pipeline co-batches an embeddable reference lookup when the resolver can express
it as a single statement with a single result set (`TryBuildSessionLookupCommand`). Per-target-group
statements are multi-statement, so the natural-key builders will also expose a **union-projection
form**: `UNION ALL` across target groups projecting the superset columns
`(GroupOrdinal, Ordinal, DocumentId, ResourceKeyId)` plus an optional diagnostic
`AbstractDiscriminator` for abstract groups. The resolver's compatibility path will use
`ResourceKeyId`, not the discriminator string. On SQL Server every branch will keep its OPENJSON input
leftmost under one statement-level `OPTION (FORCE ORDER)`.

Returning null from this seam (falling back to ordered segments) would be *correct but would regress
round-trip counts* for reference-bearing writes on that engine — acceptable only as a recorded,
deliberate decision. The command-stream pinning tests (which assert round-trip counts per operation)
will be the CI-enforced proof that this holds: **POST create must stay at 2 commands** (it did in
the prototype).

### Descriptors

One schema addition will make descriptors resolvable case-insensitively on both engines without
duplicating the lowered URI in the base table. Descriptor URI identity will be **ASCII-only**:
descriptor writes, descriptor references, and descriptor-valued query filters will reject non-ASCII
URI values before normalization. Within that supported input space, C# `ToLowerInvariant()`,
PostgreSQL `lower(...)`, and SQL Server `LOWER(...)` produce the same lowered value.

| Object | Definition |
|---|---|
| PostgreSQL `UX_Descriptor_UriLowered_ResourceKeyId` | Unique expression index: `CREATE UNIQUE INDEX "UX_Descriptor_UriLowered_ResourceKeyId" ON dms."Descriptor" (lower("Uri"), "ResourceKeyId");` |
| SQL Server `dms.Descriptor.UriLowered` | Non-persisted computed column: `[UriLowered] AS LOWER([Uri])` |
| SQL Server `UX_Descriptor_UriLowered_ResourceKeyId` | Unique index on `[UriLowered], [ResourceKeyId]` |

The lowercased value is stored only in the index key (and only as computed index state on SQL
Server), not as a persisted duplicate in the descriptor row. The legacy
`UX_Descriptor_Uri_Discriminator` will be dropped when the new index is in place.

`ResourceKeyId`, not `Discriminator`, is the descriptor-type authority. This matches the existing
descriptor architecture: `ResourceKeyId` is required and already drives type identity; `Discriminator`
is retained for diagnostics and read compatibility but will not participate in descriptor lookup or
uniqueness.

The same identity contract applies when Change Queries determines whether a deleted row was
recreated. Descriptor `/deletes` anti-joins, and the descriptor-valued identity joins used by
resource `/deletes`, will probe the live descriptor table by the lowered tombstoned
`<namespace>#<codeValue>` URI plus the descriptor resource's compile-time `ResourceKeyId`. A shared
descriptor tombstone's `Discriminator` may be used only to route historical rows to the requested
descriptor endpoint; it will not be used as live descriptor identity or converted into a resource
key. Consequently, a descriptor recreated with only ASCII casing differences will suppress the old
tombstone on both engines.

The descriptor write handler will simplify from three tables to two: the `ReferentialId`
CTE/`INSERT`/`ON CONFLICT`/`MERGE` statements will be deleted, and upsert detection will become a
lowered-URI + `ResourceKeyId` probe. PostgreSQL will probe the expression index:

```sql
SELECT descriptor."DocumentId", d."DocumentUuid", d."ContentVersion"
FROM dms."Descriptor" descriptor
INNER JOIN dms."Document" d ON d."DocumentId" = descriptor."DocumentId"
WHERE lower(descriptor."Uri") = @uriLowered
  AND descriptor."ResourceKeyId" = @resourceKeyId
```

SQL Server will probe the computed-column index:

```sql
SELECT descriptor.[DocumentId], d.[DocumentUuid], d.[ContentVersion]
FROM [dms].[Descriptor] descriptor
INNER JOIN [dms].[Document] d ON d.[DocumentId] = descriptor.[DocumentId]
WHERE descriptor.[UriLowered] = @uriLowered
  AND descriptor.[ResourceKeyId] = @resourceKeyId
```

The `dms.Document` insert, `SCOPE_IDENTITY()` retrieval, row lock, uuid lookups, and delete builder
will all keep their current shape.

### Query-time descriptor filters

The query preprocessor will need no structural change: it already consumes `IReferenceResolver`, and
its existing `ToLowerInvariant()` call will feed the descriptor lower-URI probe instead of a hash
after ASCII validation. GET-by-id, `?id=`, link injection, ownership authorization, change queries,
and descriptor paging will be untouched.

## Casing and identity semantics

Moving identity matching into the database forces the casing question into the open. PostgreSQL's
DMS schema compares strings case-sensitively. SQL Server does not supply one dialect-wide answer:
string equality follows the participating column or expression collation, and DMS provisioning
supports and preserves a case-sensitive database default. The hash era *hid* this distinction behind
an ordinal hash that disagreed with the standard SQL Server case-insensitive deployment (the internal
inconsistency described above). This design states and enforces the SQL Server column contract
explicitly. The target model is **ODS behavior minus its bugs**, verified against ODS v7.3.2 code and
a sweep of the official documentation.

What the official guidance says: no document imposes a MUST on casing anywhere. The API Guidelines
"Data Strictness" section carries a non-mandatory SHOULD that values be treated case-insensitively
("not mandatory because it may be impractical in some data stores" — and indeed PostgreSQL is
case-sensitive in every implementation ever shipped). The official error docs prescribe *rejecting*
natural-key strings with leading/trailing spaces rather than normalizing them. The Discovery API 2.0
draft anticipates case sensitivity as a declared per-implementation setting. And the ODS test suite
contains `DescriptorCaseInsensitiveValidation.feature`, which asserts a descriptor echoes its
first-created canonical form — the only official test artifact on casing.

### SQL Server column-level identity collation

The SQL Server DDL generator will define one DMS default case-insensitive collation:
`SQL_Latin1_General_CP1_CI_AS`. It will emit that collation explicitly on every generated string
column that stores or copies an identity value, rather than allowing the column to inherit the
database default. For example:

```sql
[StudentUniqueId] nvarchar(32) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
```

The rule covers canonical natural-key string columns on resource roots, every flattened RefKey copy,
string identity columns in abstract-identity tables, and local string identity members used by child
or extension collection uniqueness. Both sides of any string-bearing identity FK therefore have the
same explicit collation. Descriptor identity keeps its lowered-ASCII lookup contract described above;
its SQL Server source and computed identity columns will also be emitted under the DMS default CI
collation. Columns with a purpose-specific stronger contract, such as the existing
`Latin1_General_100_BIN2` lifecycle token, retain that explicit collation.

This is a column contract, not a database provisioning constraint. SchemaTools will continue to
preserve an operator-selected case-sensitive database collation; generated DMS identity columns will
still compare case-insensitively because their `COLLATE` clauses take precedence. Natural-key unique
constraints and lookup predicates use those columns directly, so they necessarily evaluate under the
same equality semantics.

Ordinary scalar parameters are coercible to the target column's collation under SQL Server's
collation-precedence rules. A string projected as a column by `OPENJSON ... WITH`, however, can carry
the containing database's default collation. Every generated natural-key probe will therefore apply
the same DMS CI collation explicitly to each textual OPENJSON key operand, as shown in the SQL Server
probe above. This prevents a collation conflict on a case-sensitive database and keeps both operands
under the declared identity contract; it is not runtime discovery of the database default.

The backend's runtime identity-equality provider will derive its comparer from this declared schema
contract. The SQL Server contract selects `OrdinalIgnoreCase`; the PostgreSQL contract selects
`Ordinal`. Runtime code must not infer a comparer from the product name alone, inspect the database
default, or maintain an independent dialect switch disconnected from DDL generation. The fixed SQL
Server collation and its comparer selection are one backend contract and will be pinned together by
tests. `OrdinalIgnoreCase` remains an in-process approximation of the SQL collation rather than a
general-purpose collation emulator; where the database has produced a resolved id, that database
verdict remains authoritative, and the documented fail-closed residue below still applies.

### The contract

One principle covers every surface: **the DMS-declared store equality decides identity; when it says
"same document," stored casing wins, and a casing-only write is a true no-op — never an error, never
a cascade, never a version bump.**

The rows below state the target behavior once this design ships:

| Surface | SQL Server | PostgreSQL |
|---|---|---|
| Regular natural-key matching (reference resolution and upsert detection) | Case-insensitive under the explicitly emitted `SQL_Latin1_General_CP1_CI_AS` identity-column collation, independent of the database default. Matches ODS. | Case-sensitive. Matches ODS. |
| Case-variant natural-key POST of an existing document | **200** — silent update; stored casing preserved; if the payload is otherwise identical, a true no-op (no `ContentVersion` bump, no change event). Matches ODS. | Creates a second document (a case variant is a different value). Matches ODS. |
| Case-variant (casing-only) key change via PUT | Not a key change: stored key casing is **immutable** on SQL Server, exactly as it is structurally in ODS. Real key changes on cascade-enabled resources behave as today. | A real key change (allowed on cascade-enabled resources, as today). |
| Descriptor matching + uniqueness | Case-insensitive via lowered ASCII URI + `ResourceKeyId`. | Same — uniform across engines, a first (ODS *intended* CI descriptors but its PostgreSQL implementation stored CS and could accumulate case-variant duplicates). |
| Descriptor POST-as-update casing | Stored-wins: the update preserves stored `Namespace`/`CodeValue`/`Uri` casing; a casing-only re-POST is a true no-op. A case-only descriptor PUT is a 200 update/no-op, not an error. Matches `DescriptorCaseInsensitiveValidation.feature`. | Same. |
| Core-side equality constraints and duplicate-item validation | Ordinal (stricter than the DMS identity collation; fails closed with 400). The gap this leaves for collections is closed below. | Ordinal (exact). |

### How the write path will preserve stored casing (SQL Server)

How ODS preserves stored key casing: NHibernate's per-property dirty checking
uses a case-insensitive comparer on SQL Server, so a CI-equal key property is simply never assigned,
and the UPDATE omits it. DMS's write path is a full-row replacement with no ORM dirty checking, so
preservation must be explicit. Three pieces will be added, all in the shared write executor:

1. **Schema-contract-derived identity comparer.** The identity-stability guard (which today compares
   the merged root row's identity values against the current row ordinally, and rejects changes to an
   immutable identity) will obtain its comparer from the backend identity-equality contract used by
   DDL generation. That contract supplies `OrdinalIgnoreCase` for SQL Server because the identity
   columns are explicitly `SQL_Latin1_General_CP1_CI_AS`, and `Ordinal` for PostgreSQL. It is not an
   assumption that every SQL Server database is case-insensitive. All-columns-CI-equal will mean
   "identity unchanged — proceed as an update."
2. **Stored-identity rebind.** Before authorization-on-proposed-values and no-op detection, every
   CI-equal-but-byte-different identity value in the merged root row will be replaced with the
   persisted row's value. This is the load-bearing piece, for three verified reasons: GET
   reconstitutes the response from the relational tables (there is no stored JSON body), so whatever
   is written is what is served; the root identity columns are the `ON UPDATE CASCADE` FK targets of
   every referrer's RefKey constraint, so writing a recased value would rewrite scalar copies in
   every referrer; and the stamping trigger's identity diff is deliberately **binary** (string
   columns compared as `varbinary(max)`; see ["Trigger value-diffs stay byte-level on SQL
   Server"](#trigger-value-diffs-stay-byte-level-on-sql-server)), so an unrebated recase would bump
   `IdentityVersion` and record a key change in the tracked-change tables. After the rebind, none of
   that machinery will see a change — no suppression logic will be needed anywhere downstream.
3. **No-op detection will come free.** After the rebind, a casing-only re-POST with an otherwise
   identical payload will be row-for-row equal to current state and will land on the existing
   guarded no-op path: 200, no `ContentVersion` bump, no change event.

PUT will apply the same comparer and rebind **per column** (mirroring ODS's per-property behavior,
including mixed updates: a genuinely changed column will take the normal key-change/cascade path
while a merely recased column will keep its stored casing).

Descriptors will get the equivalent treatment in the descriptor write handler: POST-as-update will
bind the persisted identity fields (the target is matched *by* identity through the CI index, so
request and stored identity can differ only in casing), the no-op comparer will treat identity
fields case-insensitively and descriptive fields ordinally, and the PUT identity guard will compare
the URI case-insensitively with the same rebind. Descriptors have no cascade or key-change
machinery, so this path carries no side-effect risk.

**Fail-closed residue (documented):** `OrdinalIgnoreCase` approximates but does not equal the fixed
DMS SQL Server collation (linguistic equalities such as `ß`/`ss` or culture-specific case foldings
that the collation may fold but invariant case mapping does not). Where the two diverge, the guard
will fail closed — a 400 on POST, or treated-as-a-real-key-change on PUT — never silent corruption.
On POST the comparer will additionally be backstopped by the database itself: the update target can
only exist because the CI probe matched under the explicit identity-column collation.

For reference: ODS behaves the same way in this residue, because it uses the same approximation
(`DatabaseEngineSpecificStringEqualityComparerProvider` is `OrdinalIgnoreCase` on SQL Server). Its
CI probe finds the row, its key-equality check says "different," and it either throws
`KeyChangeNotSupportedException` (400) or performs a real key change with cascades. Fail-closed is
inherited behavior, not a new deviation.

### Trigger value-diffs stay byte-level on SQL Server

The generated triggers gate their work on null-safe old-vs-new value comparisons, and on SQL Server
string columns are compared as `CAST(… AS varbinary(max))` — byte-level, deliberately stronger than
the collation. This will not relax when `dms.ReferentialIdentity` drops. The only trigger family
whose binary diff existed *for* the hash is `TR_<R>_ReferentialIdentity` (the hash is computed over
exact bytes, so its maintenance triggers had to see byte changes), and that family will be deleted
wholesale, diff included. Every surviving consumer of the binary diff serves a contract this design
keeps: **DMS serves stored bytes** (GET reconstitutes from the tables), so ETags and change versions
must move exactly when stored bytes move. A plain collation-governed `<>` is blind to case-only
identity changes under the explicit DMS CI collation and to trailing-space-only changes under SQL
Server's string-padding rules; other string columns could also vary with the database default.
PostgreSQL needs no cast because `IS DISTINCT FROM` under its case-sensitive deterministic collation
is already byte-accurate — the cast is what gives both engines the same trigger semantics.

| Trigger family (all will be kept) | What the binary diff gates | Why it must stay byte-level |
|---|---|---|
| Document stamping — content stamp (resource roots, child scopes, and `dms.Descriptor`) | `ContentVersion` / `ContentLastModifiedAt` bumps | Non-identity fields stay request-wins under this contract: a case-only or trailing-space-only edit changes the served bytes, so the ETag must change and change queries must resurface the document. A collation diff would leave the ETag stale while the body changed. |
| Document stamping — identity stamp + key-change workset | `IdentityVersion` bump + the key-change tracked-change row | The fail-closed comparer residue: a byte-different-but-collation-equal key change (e.g. `Straße` → `Strasse`) is deliberately allowed through as a real key change, and its cascade rewrites referrer bytes; only a byte-level diff records any of it. |
| Abstract identity maintenance | Whether concrete identity changes propagate into the `<Abstract>Identity` tables | These tables will become the *only* resolution path for abstract references, and PostgreSQL matches them case-sensitively — byte drift between a concrete root and its abstract copy would become user-visible. |
| Identity propagation (pruned-cascade replacement triggers) | Whether identity changes propagate where SQL Server's cascade restrictions forced FK pruning | PostgreSQL propagates byte changes through native cascades; a collation diff here would make SQL Server skip what PostgreSQL propagates — cross-engine drift in referrer copies. |

(Non-string columns are never cast — the byte comparison exists only where collation equality and
byte equality can disagree.)

Two further reasons the diffs cannot be delegated to the write path's comparer-and-rebind
discipline: the rebind lives in one application code path, while triggers fire for *every* writer
(ETL, operational data fixes, future code paths) — the trigger is the engine-level backstop that
keeps "stored bytes changed ⇒ versions moved" true unconditionally. And once this design ships,
casing-only identity writes stop reaching the database on SQL Server (the rebind removes them at
the source), so the binary identity diff costs nothing at runtime — it simply stops firing.

Nor is byte-level trigger diffing a deviation from ODS; it is the relocation of ODS's own byte-level
change detection. ODS's change-version triggers are diff-free (any UPDATE bumps) because
NHibernate's per-property dirty checking filters value-identical writes upstream — comparing non-key
properties ordinally (byte-level) and key properties with the engine-specific CI comparer. DMS's
full-row replacement issues writes that carry unchanged values, so the "did anything actually
change?" question moves into the trigger, and the binary cast is the SQL Server translation of the
ordinal non-key comparison. In the comparer residue ODS's own bookkeeping goes blind (its key-change
and cascade-touch triggers compare under the collation, so a `Straße` → `Strasse` key change records
no key change and touches no referrer stamps even though every copy's bytes changed); the byte-level
diffs make DMS strictly more accurate there while keeping the same API-surface behavior.

### DMS Behavior changes

Relative to current DMS behavior (the hash era), on SQL Server:

- Databases whose default collation is case-sensitive will keep that database setting, but generated
  DMS identity string columns will become explicitly case-insensitive after re-provisioning. Their
  natural-key lookup and uniqueness behavior will therefore match the standard SQL Server deployment.
- Case-variant natural-key POST of an existing document will shift **409 → 200** (silent update; ODS
  parity).
- Casing-only PUT on a cascade-enabled resource: today a real key change (cascade through every
  referrer plus change-version ripples); it will become a no-op for the casing (stored key casing
  immutable, as in ODS).
- Descriptor POST-as-update: today it rewrites stored descriptor casing to the request's; it will
  preserve stored casing (first-created canonical form, per the official feature test). A case-only
  descriptor PUT is a 400 today; it will become a 200 update/no-op. These two descriptor deltas will
  apply on both engines.
- Non-ASCII descriptor URI writes, descriptor references, and descriptor-valued query filters will
  become 400 validation failures. Descriptor URI identity is intentionally ASCII-only so
  case-insensitive descriptor matching is deterministic across engines without storing a second
  normalized URI value in the base table.

PostgreSQL regular-resource behavior will remain unchanged on every pin.

### Collection duplicate detection

Collation-governed matching will open one gap that Core's request validation cannot close. Core's
request-local duplicate detection will remain engine-agnostic and ordinal: reference items compare
with the structural natural-key comparer, and scalar identity members compare through ordinal
dictionaries. Two collection items differing only in string casing can therefore pass Core
validation — and on SQL Server they will then resolve to the *same* target `DocumentId` under the
explicit DMS identity collation and collide in the collection's sibling unique constraint, which the
constraint resolver does not classify: an unmapped 5xx for what is really a client input error.

The fix will run after reference resolution and before DML, comparing each collection item's
flattened identity tuple per scope, in two tiers governed by one principle: *never invent an
equality definition where the database has issued a verdict; approximate its verdict only where it
hasn't spoken, and say so.*

1. **Reference and descriptor members (exact tier):** will be compared by resolved
   `DocumentId`/`DescriptorId` — the engine's own equality verdict, already in hand, zero
   approximation error. Deliberately not a string comparison: a C# string comparer would only
   approximate the collation and would reintroduce the same defect in rarer forms.
2. **Local string-scalar identity members (approximation tier):** no database verdict exists before
   the write, so they will compare with the same schema-contract-derived comparer —
   `OrdinalIgnoreCase` for the SQL Server identity-column contract, `Ordinal` on PostgreSQL (where it
   is exact). This will live backend-side so Core stays engine-agnostic (the same placement ODS uses
   for its engine-specific string comparer). Documented residual: the SQL Server comparer will not
   reproduce the fixed collation's padding rules or linguistic equalities; those exotic cases will
   fall through to the sibling unique constraint as an integrity backstop (and natural-key strings
   with leading/trailing spaces are already rejected with 400).

Duplicates will produce the same path-attributed 400 duplicate-item error Core produces today. The
sibling unique constraint's runtime meaning will stay "race/integrity backstop," never routine input
validation.

**Generic conflict fallback (ODS parity):** unique-constraint violations that the write path's
constraint resolver does not specifically recognize will map to a **409 Conflict** — the same
catch-all translation ODS applies to unique/PK violations — instead of surfacing as an unmapped 5xx.
The well-shaped, path-attributed 400s still come from the two detection tiers above; the fallback
only dresses the backstop's rare firings (linguistic equalities such as `ß`/`ss` in local string
scalars, which the collation folds but `OrdinalIgnoreCase` does not, plus any future unique
constraint without a specific mapping). Specific classifications keep their existing semantics —
in particular, the natural-key create-race classification and its retry behavior are untouched; the
fallback replaces only the unmapped-failure terminal.

A latent variant of this defect exists today in `main`: case-variant duplicate
*descriptor* array items already normalize to the same legacy descriptor identity and collide in the
sibling unique on both engines.

## Consistency and integrity

An audit of every invariant `dms.ReferentialIdentity` participates in, and its coverage after
removal:

| Invariant | Enforced after removal by |
|---|---|
| Concrete identity uniqueness | `UX_<R>_NK` (always was the primary enforcement) |
| Cross-subclass abstract identity uniqueness | `UX_<Abstract>Identity_NK` on the abstract identity tables (this is the alias row's real job, and the tables already enforce it) |
| Abstract reference compatibility | The concrete `ResourceKeyId` stored on the matched abstract identity row, compared with the target's allowed concrete resource-key set |
| Descriptor identity uniqueness | `UX_Descriptor_UriLowered_ResourceKeyId` (CI over ASCII URI, both engines) |
| Create-race detection (409/retry) | `UX_<R>_NK` unique violations, classified exactly as today |
| Reference targets exist and stay consistent | Composite FKs onto `RefKey` targets, unchanged |

Deliberately lost will be: the corruption canary (its entire disease class — derived state drifting
from row state — will be abolished with the derived state), and a redundant second uniqueness net
(the RI PK). Mitigations for the redundancy loss: the probe compiler will carry an empty-identity
guard (a resource whose compiled identity has zero parts will fail compilation loudly), a
compile-time parity guard will prove the compiled probes reproduce the legacy trigger derivation
resource-by-resource for as long as both exist, and golden DDL fixtures will continue to pin the
schema.

Cascades will need no new application-side revalidation: unified identity values are stored once
(aliases are generated columns; FKs bind canonical columns), collection sibling uniques bind
`..._DocumentId` columns (cascade-stable), and parent NK uniqueness gates any key change before it
can cascade.

## Schema and contract changes

### To be dropped

- `dms.ReferentialIdentity`, `FK_ReferentialIdentity_Document`, `FK_ReferentialIdentity_ResourceKey`.
- All `TR_<R>_ReferentialIdentity` triggers; the `ReferentialIdentityMaintenance` trigger kind and
  `SuperclassAliasInfo` contract types; `IdentityElementMapping` will shrink from arity 4 to 2
  (`ScalarType`/`IsDescriptorReference` exist only for hash emission); the manifest emitter's RI
  trigger-kind serialization.
- `dms.uuidv5()` on both engines — `ISqlDialect.CreateUuidv5Function` will be removed (breaking for
  Managed-API implementers).
- The PostgreSQL `pgcrypto` extension (uuidv5's `digest()` call is its only DMS-database consumer).
- `dms.UniqueIdentifierTable` TVP type (sole consumer: the SQL Server bulk RI lookup strategy).
  `dms.BigIntTable` will stay — it serves authorization.
- Core's UUIDv5 referential-id surface: `ReferentialId`, `ReferentialIdFactory`,
  `ReferentialIdCalculator`, `No.ReferentialId`, the `Be.Vlaanderen.Basisregisters.Generators.Guid`
  dependency if it has no remaining consumers, and all extractor/middleware code that computes or
  compares referential ids.
- All Core/backend contract members that carry referential ids, including
  `DocumentReference.ReferentialId`, `DescriptorReference.ReferentialId`,
  `SuperclassIdentity.ReferentialId`, `DocumentInfo.ReferentialId`,
  `DocumentReferenceFailure.ReferentialId`, `DescriptorReferenceFailure.ReferentialId`,
  `DescriptorWriteRequest.ReferentialId`, `RelationalWriteTargetRequest.Post.ReferentialId`,
  `ReferenceLookupRequest.ReferentialIds`, `ReferenceLookupRequestEntry.ReferentialId`,
  `ReferenceLookupResult.ReferentialId`, `ReferenceLookupSnapshot.ReferentialId`, and
  `ResolvedReferenceSet.LookupsByReferentialId`.
- Backend RI lookup C# code: `ReferenceResolver`'s referential-id memoization path,
  `ReferenceLookupResultReader`, PostgreSQL RI lookup command builders, SQL Server small-list/bulk
  RI lookup strategies, RI adapters/factories, corruption-canary verification, and the unit or
  integration tests dedicated to those code paths.
- `UX_Descriptor_Uri_Discriminator` (replaced by the `ResourceKeyId`-authoritative CI unique index).

### To be added

- A SQL Server identity-equality contract pairing the emitted
  `SQL_Latin1_General_CP1_CI_AS` column collation with the runtime `OrdinalIgnoreCase` comparer;
  PostgreSQL's corresponding contract pairs its existing case-sensitive storage with `Ordinal`.
- Descriptor URI ASCII validation.
- PostgreSQL `UX_Descriptor_UriLowered_ResourceKeyId` expression index, plus SQL Server
  non-persisted `dms.Descriptor.UriLowered` computed column and
  `UX_Descriptor_UriLowered_ResourceKeyId` index (definitions above).
- Compiled natural-key probe metadata on the mapping set (not serialized; zero manifest churn).
- `NaturalKeyReferenceResolver` + per-engine natural-key lookup command builders.

### To be changed

- The SQL Server DDL generator will emit `COLLATE SQL_Latin1_General_CP1_CI_AS` on every string
  column that stores or copies an identity value. The database default collation is neither changed
  nor treated as the identity contract. Purpose-specific explicit collations remain authoritative.
- **Published-contract trims:**
  - `DocumentReference`, `DescriptorReference`, `SuperclassIdentity`, and `DocumentInfo` will retain
    their non-hash identity, path, and reference payloads; no Core external model record will expose
    a UUIDv5 referential id.
  - `DocumentReferenceFailure` and `DescriptorReferenceFailure` will report the path, target
    resource, and natural identity; no failure record will expose a UUIDv5 referential id.
  - `DescriptorWriteRequest` and the POST write-target request will bind target existence by natural
    identity/probe metadata; no write request will carry a UUIDv5 referential id.
  - `ReferenceLookupRequest`, `ReferenceLookupRequestEntry`, `ReferenceLookupResult`,
    `ReferenceLookupSnapshot`, and `ResolvedReferenceSet` will be keyed by structural lookup
    ordinals / natural-key identities, not referential ids. `ReferenceLookupResult` will also lose
    `VerificationIdentityKey` (canary-only) and `ReferentialIdentityResourceKeyId`; `ResourceKeyId`
    remains the resolved concrete target key, including abstract matches.
  - `Add{Postgresql,Mssql}ReferenceResolver()` DI extensions will compose the natural-key resolver —
    a behavioral change for hosts that resolve references through the old registration.
- Abstract identity tables and their union views will add a concrete `ResourceKeyId smallint NOT
  NULL` payload column. Existing abstract-identity maintenance triggers will populate it from
  compile-time concrete-member metadata. The abstract identity `Discriminator` column remains for
  diagnostics/readability only; resolver compatibility will not parse it. Consumers that enumerate
  abstract identity scalar columns must continue to exclude both payload columns (`ResourceKeyId` and
  `Discriminator`) from identity-equality logic.
- Ops: the seed-clone script's TRUNCATE list will lose `dms."ReferentialIdentity"`; template
  management will drop its pgcrypto preamble.

### To remain unchanged

`dms.Document` in every respect (columns including `CreatedByOwnershipTokenId`, identity
`DocumentId`, the DocumentCache enqueue triggers, all FKs into it); `dms.Descriptor` except for the
descriptor unique-index swap, SQL Server's non-persisted `UriLowered` computed column, and the
explicit SQL Server identity collation (including `ResourceKeyId NOT NULL` and `Discriminator`
storage/read compatibility); the logical shapes of `UX_<R>_RefKey` / `UX_<R>_NK`; the abstract
identity table family, its uniqueness/FK constraints, and its trigger topology except for concrete
`ResourceKeyId` column population and explicit SQL Server identity collation; the DocumentCache table
family; tracked-change tables and triggers; `auth.*`; `dms.ResourceKey` / `dms.EffectiveSchema` /
`dms.SchemaComponent`; the read/reconstitution pipeline; `RelationalMappingVersion` remains `v2` for
this unreleased aggregate mapping shape.

## Migration and rollout: proposed tickets

One implementation branch, trunk-green per ticket. The list below is the proposed ticket breakdown —
one story per entry, sized like the existing stories under [`../epics/`](../epics/), except the drop
ticket, which deliberately bundles three deletion stages as ordered commits — to be created there
once this document is approved. Ordering is dependency order; the cutover tickets depend on the
foundation tickets (T1–T3).

**Foundations — the schema object can stay trigger-maintained as an unreferenced shadow until the
final schema drop, but the natural-key cutover is the point where production Core/backend C# stops
computing, carrying, or comparing UUIDv5 referential ids. T4–T8 are the coordinated C# cutover lane;
after T8, no production contract may still carry a `ReferentialId` member.**

- **T1 — Pin the SQL Server identity collation and runtime equality contract.** Emit
  `COLLATE SQL_Latin1_General_CP1_CI_AS` on every generated SQL Server string column that stores or
  copies an identity value, including root natural keys, RefKey copies, abstract identities,
  descriptor identity, and local collection identity members. Preserve purpose-specific explicit
  collations. Introduce the backend identity-equality contract consumed by both DDL/runtime
  composition, selecting `OrdinalIgnoreCase` for this SQL Server contract and `Ordinal` for
  PostgreSQL. AC: golden DDL proves full identity-column coverage with no inherited-collation gaps;
  provisioning against `Latin1_General_100_CS_AS_SC_UTF8` preserves that database default while
  `sys.columns` reports the pinned CI collation for representative canonical, copied, abstract, and
  descriptor identity columns; comparer-provider tests pin each schema contract; PostgreSQL DDL and
  comparer behavior are unchanged.
- **T2 — Add abstract `ResourceKeyId`, compile natural-key probe metadata, and re-source the 409
  duplicate-identity messages.** Add `ResourceKeyId smallint NOT NULL` to each abstract identity
  table and union view, then populate the table column from the existing abstract-identity
  maintenance triggers using compile-time concrete-member metadata. Compile per-resource probe
  metadata (reference targets, own-key probes, the descriptor probe) from the relational model —
  never from trigger metadata, constraint names, or discriminator string parsing — with an
  empty-identity compile guard and an every-resource parity guard against the live trigger derivation.
  Re-source the 409 `duplicateIdentityValues` machinery from the compiled probes, severing its
  trigger-metadata dependency before the triggers drop. AC: golden DDL/manifest diffs show only the
  abstract `ResourceKeyId` column/view/trigger-value change for this part; abstract identity-column
  consumers exclude `ResourceKeyId` and `Discriminator` from identity equality; parity guard green for
  every resource; 409 responses unchanged.
- **T3 — Add descriptor ASCII validation + `UX_Descriptor_UriLowered_ResourceKeyId`.** Reject
  non-ASCII descriptor URI values on descriptor writes, descriptor references, and
  descriptor-valued query filters. Emit the final lower-storage index shape on both engines:
  PostgreSQL gets the unique expression index on `lower("Uri"), "ResourceKeyId"` with no new
  column; SQL Server gets the non-persisted `UriLowered AS LOWER([Uri])` computed column and a
  unique index on `UriLowered, ResourceKeyId`. The legacy Discriminator-authoritative index stays
  through the transition. AC: golden DDL diff shows exactly the new index shape (and SQL Server
  computed column); ASCII validation unit/integration pins green.
- **T4 — The natural-key resolver replaces the hash resolver arm.** The dialect command builders
  (PostgreSQL `unnest` and SQL Server OPENJSON +
  `FORCE ORDER` group statements, the union-projection single-statement form, the parameter-budget
  guard) plus `NaturalKeyReferenceResolver` implementing the resolver role (structural memo, shared
  typed-value conversion, ordinal result mapping) and the composite embeddability seams. This ticket
  **replaces** the hash resolution arm rather than coexisting with it:
  `Add{Postgresql,Mssql}ReferenceResolver()` composes the new resolver directly, the old resolver
  (per-engine lookup builders/strategies, result reader, corruption canary) and its test suites are
  deleted, its composite-seam consumers re-point to the new factory, and the resolver-contract trims
  land here in final shape. Reference failures lose their referential-id payloads, and lookup
  requests/results/snapshots are keyed by structural natural-key identities and ordinals.
  `DocumentReference` and `DescriptorReference` stop carrying referential ids as part of this
  resolver cutover; the document-level POST/descriptor write consumers are removed in T5/T8 below.
  AC: SQL-shape pins (batch-size-independent text on PG, leftmost OPENJSON input, explicit DMS
  identity collation on every textual OPENJSON key operand, and one statement-level `FORCE ORDER` on
  MSSQL, budget-guard throw, abstract probes projecting concrete `ResourceKeyId` with no
  discriminator-to-key map); resolver unit suites green; the existing
  reference-resolution-dependent integration estate green on both engines, now exercising the new
  resolver. Correctness on this branch is carried by the behavior pins, the integration estate, and
  E2E (see ["Test strategy"](#test-strategy)). If production-shaped workloads later disagree on
  performance, the capture-predicate contingency ladder applies, with reverting the composite
  write-path batching (DMS-1332) accepted as the last resort.
- **T5 — Upsert-detection cutover in the composite write path.** Replace the capture predicate's
  hash subselect with the natural-key predicate (inline RefKey/lowered-descriptor subselects) and
  the standalone fallback with the `UX_<R>_NK` probe. The target resolver binds from
  `DocumentInfo.DocumentIdentity` and compiled own-key probe metadata, never from a UUIDv5
  referential id. `RelationalWriteTargetRequest.Post.ReferentialId` and the RI target-lookup
  builders are deleted here. AC: command-stream pins — round-trip counts unchanged (POST create
  stays at 2 commands), RI command classification zero; write suites green.
- **T6 — Collection duplicate detection + generic conflict fallback.** The two-tier post-resolution
  duplicate detection (resolved ids exact for reference/descriptor members; schema-contract-derived
  comparer for local string scalars) and the ODS-parity 409 fallback for unclassified unique
  violations; includes the case-variant duplicate-descriptor E2E scenario. AC: the per-engine
  duplicate-detection pin matrix + the E2E scenario green.
- **T7 — ODS-parity casing: CI identity guard + stored-identity rebind (SQL Server).** The
  schema-contract-derived identity comparer, the merged-row stored-identity rebind ahead of
  authorization and no-op detection, and the per-column PUT semantics. AC: the case-variant POST/PUT
  pin matrix (200, stored casing served, guarded no-op, no referrer rewrite / key-change row /
  `IdentityVersion` bump on SQL Server; PostgreSQL unchanged on every pin).
- **T8 — Descriptor write handler cutover.** Lowered-URI + `ResourceKeyId` upsert detection and
  stored-wins casing (persisted-identity binding, the split no-op comparer, the case-insensitive
  PUT identity guard). The descriptor handler no longer accepts `DescriptorWriteRequest.ReferentialId`
  and no longer writes `dms.ReferentialIdentity`. With the last document-level consumer gone,
  delete `DocumentInfo.ReferentialId`, `SuperclassIdentity.ReferentialId`, `ReferentialId`,
  `ReferentialIdFactory`, `ReferentialIdCalculator`, `No.ReferentialId`, Core extraction-time
  referential-id calculation, and the UUIDv5 package dependency if it has no remaining consumers.
  AC: descriptor write/stamping suites green; stored-wins pins per engine; `rg` over production
  Core/backend C# finds no `ReferentialId`, `ReferentialIds`, `ReferentialIdFactory`, or
  `ReferentialIdCalculator`.
- **T9 — Descriptor query cutovers.** Flip the production query preprocessor for descriptor filters
  and update Change Query recreated-row detection. Descriptor `/deletes` and descriptor-valued
  identity joins in resource `/deletes` probe the live descriptor table by lowered URI plus the
  descriptor resource's compile-time `ResourceKeyId`; shared-tombstone `Discriminator` remains a
  routing predicate only. Remove the now-unused live
  `IX_Descriptor_Discriminator_ContentVersion` index. AC: URI filter matches, case-variant URI
  matches, nonexistent/wrong-type URIs return empty pages with unchanged reasons; SQL snapshots use
  no live-descriptor `Discriminator` predicate; case-only descriptor recreation suppresses the old
  tombstone on both engines, including when resolving a descriptor-valued identity part for a
  resource `/deletes` anti-join; the same URI under a different `ResourceKeyId` does not suppress it.
- **T10 — Remove `dms.ReferentialIdentity` and everything that maintained it.** One ticket, three
  internally ordered stages, each landing as its own trunk-green commit:
  1. Delete any RI write-path remnants left only as dead code or tests: RI upsert-probe SQL,
     service members, and every test fixture that seeds RI rows directly. There should be no
     production Core/backend C# `ReferentialId` members by this point; finding one is a stop-the-line
     failure, not a reason to defer it again. The fixture sweep is a structural proof: a test failing
     for a missing RI row has found a surviving reader (stop and investigate, never reseed).
  2. Drop the `TR_<R>_ReferentialIdentity` triggers, scope-guarded — the DocumentCache enqueue,
     stamping, and abstract-identity trigger families are kept — with the shared cross-engine
     parity-contract tests updated once for both engines.
  3. Drop the table, `uuidv5`, pgcrypto, and the TVP, and collapse descriptor uniqueness onto the
     new `ResourceKeyId`-authoritative case-insensitive index.

  AC per stage; the final golden diff shows exactly the predicted removals and **no version-string
  or hash churn** because this remains within the unreleased `v2` aggregate mapping shape.

**Release compatibility note:** this branch assumes the upcoming release continues to publish mapping
version `v2` as the aggregate prerelease shape. Before `v2` is published, no `v2 → v3` bump is
required for this change. After `v2` is published, any later incompatible relational mapping change
must bump `RelationalMappingVersion` and re-bless the schema-hash pin so stale released databases
fail fast with the designed 503.

Rollback before the drop ticket will be a commit revert: `dms.ReferentialIdentity` stays
trigger-maintained until T10, so reverting the resolver swap (or any cutover ticket) resumes against
current data. After the drop ticket, rollback will be re-provisioning with the previous build —
consistent with the re-provision-only migration stance.

## Performance validation

Measured on a full prototype of this design (PostgreSQL 18 and SQL Server 2022, local native
instances), as median ratios of natural-key resolution vs the hash baseline across three workload
cases — bulk (4096 references), deep identities (512), small batch (64):

| Engine / input shape | bulk-4096 | deep-512 | small-64 |
|---|---|---|---|
| PostgreSQL, typed `unnest` arrays | **0.61×** | **0.86×** | 1.11× |
| SQL Server, OPENJSON + `FORCE ORDER` | **0.62×** | **0.53×** | 1.26× |

Values below 1.0× are faster than the hash resolver. The two small-batch regressions are modest and
bounded; the bulk and deep-identity cases — where resolution cost actually matters — are markedly
faster (deep identities benefit most: RefKey flattening replaces the hash path's per-level work with
one wide seek). Additional performance runs on the prototype beyond this matrix confirmed the
results. If production-shaped workloads later disagree, the capture-predicate
contingency ladder applies, with reverting the composite write-path batching (DMS-1332) accepted as
the last resort.

Two SQL Server input shapes were evaluated and **rejected on measurement**, which is why the
surviving shape is pinned by tests rather than left to convention:

- **Typed `VALUES` row constructors:** 1.7–3.8× the baseline (~17 µs per parameter, superlinear in
  batch size) — parameter binding dominates.
- **OPENJSON without `OPTION (FORCE ORDER)`:** up to **8.9×** the baseline — OPENJSON's fixed 50-row
  cardinality estimate can put the payload on the inner side of a nested loop. `FORCE ORDER` with the
  OPENJSON input leftmost in every join tree makes the plan shape deterministic; both properties are
  asserted by SQL-shape unit tests.

Round-trip counts are a separate, CI-enforced property: the command-stream pinning tests assert that
per-operation command counts do not regress (POST create must stay at 2 commands); those pins will
be the proof that the union-projection embedded lookup holds on both engines, as it did in the
prototype.

The first SQL Server 2025 exposure of the OPENJSON + FORCE ORDER shape will happen in CI's Docker
E2E lane; a performance re-measure on 2025 will be a post-merge observation item, not a rollout gate.

## Test strategy

- **Golden DDL fixtures** are the schema review surface; they will be regenerated per
  schema-affecting step and proven to be a fixed point after every regeneration. SQL Server fixtures
  will additionally prove that every string identity role carries
  `COLLATE SQL_Latin1_General_CP1_CI_AS`, including canonical, RefKey-copy, abstract, descriptor, and
  local collection identity columns, while purpose-specific binary columns retain their collation.
- **SQL Server collation-contract integration tests** will provision against the supported
  `Latin1_General_100_CS_AS_SC_UTF8` database default, assert that SchemaTools preserves that default,
  query `sys.columns` to prove representative identity columns use
  `SQL_Latin1_General_CP1_CI_AS`, and run the case-variant natural-key lookup/uniqueness behavior pins
  with the same expectations as the standard default-collation fixture. Runtime unit tests will pin
  `OrdinalIgnoreCase` to this declared SQL Server schema contract and `Ordinal` to PostgreSQL's.
- **Descriptor ASCII validation pins**: descriptor writes, descriptor references, and
  descriptor-valued query filters containing non-ASCII URI values return a path-attributed 400
  before any relational lookup.
- **Probe-compilation unit tests**, including an every-resource parity guard that will prove the
  compiled probes reproduce the legacy trigger derivation for as long as both exist, and abstract
  target pins proving the probe projects concrete `ResourceKeyId` without a discriminator-to-key map.
- **Dialect SQL unit tests**: statement shape independent of batch size (PostgreSQL), OPENJSON +
  FORCE ORDER + leftmost-input pins, explicit DMS identity collation on every textual OPENJSON key
  operand, and the parameter-budget guard (SQL Server), plus the union-projection single-statement
  form.
- **No old-vs-new gates.** The prototype's differential equivalence proof and benchmark matrix stand
  as the transition evidence; neither suite is ported to the implementation branch. Correctness is
  carried by the behavior pins below, the existing integration estate (running against the new
  resolver from T4 onward), and E2E; performance remedies are pre-agreed (the contingency ladder,
  then the accepted DMS-1332-revert last resort).
- **Command-stream pins**: round-trip counts must not regress; RI command classification will go to
  zero at cutover.
- **Behavior pins**:
  - Case-variant natural-key POST/PUT suites asserting the ODS-parity contract on SQL Server (200,
    stored casing served back, true no-op on identical payload, no referrer rewrite / key-change
    row / `IdentityVersion` bump; casing-only PUT is not a key change; a mixed PUT cascades only the
    genuinely changed column) and the PostgreSQL second-document behavior.
  - Descriptor stored-wins pins per engine (POST preserves stored casing and no-ops on casing-only
    re-POST; case-only descriptor PUT returns 200 with stored identity intact).
  - Collection duplicate-detection pins (SQL Server: case-variant duplicate reference items → 400
    duplicate-item, never an unmapped failure; case-variant duplicate string-scalar identity items →
    400; PostgreSQL: the same payloads → 409 unresolved reference and success-with-both-items
    respectively, since case variants are distinct values there; case-variant duplicate
    **descriptor** items → 400 duplicate-item on **both** engines — descriptor matching is
    case-insensitive everywhere, so both items resolve to the same `DescriptorId`; this pin doubles
    as the regression test for the pre-existing latent defect noted under
    ["Collection duplicate detection"](#collection-duplicate-detection); linguistic-equality
    (`ß`/`ss`-class) duplicate string-scalar items → **409 Conflict** on SQL Server via the generic
    fallback, never an unmapped 5xx, and success-with-both-items on PostgreSQL — engine-divergent
    expectations, so this pair stays at the integration level rather than E2E).
  - Abstract-reference pins: valid concrete members resolve through the abstract target with their
    concrete `ResourceKeyId`, and compatibility classification still returns `IncompatibleTargetType`
    for a resolved concrete key outside the target metadata's allowed set.
  - Change Query descriptor-identity pins on both engines: a case-only descriptor recreation
    suppresses its old tombstone; descriptor-valued resource identity joins resolve the recreated
    descriptor; the same URI under a different descriptor `ResourceKeyId` does not suppress the
    tombstone; generated live-descriptor probes use lowered URI plus compile-time `ResourceKeyId`
    and never `Discriminator`.
- **E2E** will gate the merge in CI on both engines. HTTP-visible contract points are pinned at the
  E2E level where possible; in particular, the case-variant duplicate descriptor-item scenario will
  be a dedicated E2E test (`EdFi.DataManagementService.Tests.E2E`): POST a resource whose collection
  carries two descriptor URIs differing only in casing → 400 duplicate-item, never a 5xx, on both
  engines.

## Risks and accepted trade-offs

1. **SQL Server OPENJSON plan quality** — no statistics on the shredded input. Mitigated by
   `FORCE ORDER` plus the leftmost-input rule (measured, pinned), with a re-observation on SQL
   Server 2025 after merge.
2. **Capture-predicate cost** — one extra seek per reference-sourced natural-key part at capture
   time, bounded by key width; remedies are pre-agreed as the contingency ladder (own-RefKey capture
   probe first; NK re-shaping only as a separate initiative; dropping NK never), with reverting the
   composite write-path batching (DMS-1332) accepted as the last resort. No live benchmark gate —
   prototype evidence stands (see "Performance validation").
3. **Write-path churn on recent code** — the composite write pipeline (DMS-1332) is recent, and the
   cutover will land on top of it. Mitigated by the seam-level replacement design (a predicate and a
   lookup swap, no structural change) and by keeping the DMS-1332 pinning suites green throughout.
4. **The DMS identity collation overrides a case-sensitive SQL Server database default** — accepted
   and asserted deliberately. SchemaTools preserves the database default, but generated identity
   columns explicitly use `SQL_Latin1_General_CP1_CI_AS`; this removes deployment-dependent identity
   behavior and moves DMS toward ODS behavior.
5. **Descriptor case-variant duplicates** will be rejected by a table-level CI unique index over
   lowered ASCII URI + `ResourceKeyId` (they are same-document by hash semantics today) — accepted;
   identical effective semantics, newly enforced by the engine.
6. **Lost corruption canary** — accepted by construction: the derived state it guarded will no
   longer exist.
7. **Casing comparer approximation** — the runtime provider derives `OrdinalIgnoreCase` from the
   fixed SQL Server identity contract, but it still does not emulate every
   `SQL_Latin1_General_CP1_CI_AS` equality; divergences will fail closed (documented above), never
   silently redefine database identity.
8. **ASCII-only descriptor URIs** — accepted to keep descriptor matching deterministic while
   minimizing storage. Non-ASCII descriptor URI values will be rejected explicitly rather than
   normalized differently by different engines.

## Out of scope

- Any change to `dms.Document` (columns, locking, DELETE shape, readers, or its DocumentCache
  triggers).
- Changing or constraining the SQL Server database default collation. The column-level identity
  contract is specifically what allows the supported case-sensitive database default to remain.
- In-place upgrade scripts (the migration is re-provision-only; prerelease databases provisioned
  from an earlier shape must be re-provisioned).
- DocumentCache/CDC work (live, RI-free, orthogonal).
- ApiSchema contract or resource JSON shapes, except for the new ASCII-only descriptor URI value
  contract described above.
- Rewriting the design-doc corpus beyond the targeted supersession banners and the batching-doc
  correction named under Migration.
