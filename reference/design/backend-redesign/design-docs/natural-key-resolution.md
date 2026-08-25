# Natural-Key Reference Resolution: Removing UUIDv5 `ReferentialId`

## Summary

`dms.ReferentialIdentity` maps a deterministic UUIDv5 hash of each document's identity
(`ReferentialId`) to its `DocumentId`. It exists so that reference resolution, POST upsert detection,
and descriptor filter resolution can each be answered by one uniform, narrow index lookup.

This design will remove the table, stop computing UUIDv5 `ReferentialId` values anywhere in DMS,
and answer each of those questions with a batched natural-key lookup against the schema's existing
natural-key indexes. The only new lookup index will be the descriptor lower-URI index; the design
will also add a narrow `UX_Document_DocumentId_ResourceKeyId` parent key for descriptor/abstract
document-resource invariants. Abstract resolution will add a `smallint ResourceKeyId` payload column
and composite FK to existing abstract identity rows but will not add another abstract lookup index:

| Lookup | Will be replaced by |
|---|---|
| Concrete document references | Scalar probe of the target's `UX_<Target>_RefKey` |
| Abstract (polymorphic) references | Probe of `UX_<Abstract>Identity_RefKey`, projecting the concrete `ResourceKeyId` |
| Descriptor references and filters | Probe of `UX_Descriptor_UriLowered_ResourceKeyId` (new lower-expression/computed-column index) |
| POST upsert detection | The document's own `UX_<R>_NK` |

For abstract targets, the replacement depends on the data-model contract explicitly pinning both
`UX_<Abstract>Identity_NK` and `UX_<Abstract>Identity_RefKey`. The former will take over the
cross-subclass identity uniqueness that the legacy alias/hash path made redundant; the latter will be
the stable FK/probe target with `DocumentId` last. `ResourceKeyId` and `Discriminator` will be
projected payload only and must be excluded from both abstract identity key shapes. Because the
projected `ResourceKeyId` will become the authoritative compatibility key for abstract references,
every abstract identity table must pair it with `DocumentId` in a composite FK to
`dms.Document(DocumentId, ResourceKeyId)` so it cannot drift from the owning document's concrete
resource type. `dms.Document.ResourceKeyId` will remain the FK-constrained path to the seeded
effective-schema mapping.

On SQL Server, those lookups will not inherit identity equality from the database default collation.
The DDL generator will explicitly apply DMS's default case-insensitive collation,
`SQL_Latin1_General_CP1_CI_AS`, to every string column that stores or copies an identity value. This
includes canonical natural-key columns, flattened RefKey copies, abstract-identity columns, and
tracked-change old/new identity copies, and string members of collection identity constraints. A
case-sensitive database default will remain supported and unchanged; it simply will not govern DMS
identity columns. Runtime identity comparers will be selected from this same schema contract
(`OrdinalIgnoreCase` for the SQL Server contract and `Ordinal` for PostgreSQL), not from a general
assumption about the database engine.

With the reads gone, the maintenance surface will go with them: every generated
`TR_<R>_ReferentialIdentity` trigger, the `dms.uuidv5()` function on both engines, the PostgreSQL
DMS relational DDL dependency on `pgcrypto` (uuidv5's `digest()` call is its only DMS relational
consumer), the SQL Server `dms.UniqueIdentifierTable` table-valued parameter type, and the
Core/backend C# surfaces that produce, carry, accept, return, compare, or test UUIDv5 referential
ids. This is scoped to DMS relational provisioning. PostgreSQL `pgcrypto` remains owned by
CMS/OpenIddict when encrypted signing keys require it, including self-contained local deployments
where the Configuration Service objects share the same physical database as DMS, as described in
[bootstrap command boundaries](bootstrap/command-boundaries.md).

The migration will be **code-only and re-provision-only**; no in-place upgrade scripts will be
provided. Release review confirmed that mapping version `v2` has not been released as a supported
database shape. DMS-1408 consumes `RelationalMappingVersion=v3` for the current physical schema
shape. Databases provisioned from an earlier prerelease shape must re-provision after picking up
these changes; the mapping-version mismatch rejects the stale shape. Future natural-key physical
storage changes must not reuse `v3`; each later incompatible physical schema change requires its own
`RelationalMappingVersion` bump and schema-hash re-bless so stale databases fail fast with the
designed 503.

The rollout is filed as epic DMS-1402 with fourteen stories, DMS-1443 through DMS-1456; this
document refers to them by their stable local aliases T1–T14, defined in
[`epics/21-storage-reduction/EPIC.md`](../epics/21-storage-reduction/EPIC.md#stories) (T5 =
DMS-1447 PostgreSQL floor, T6 = DMS-1448 descriptor index/FK foundations, T9 = DMS-1451 resolver
cutover, T13 = DMS-1455 Change Query cutover, T14 = DMS-1456 removal).

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
identity columns, composite indexes, abstract identity tables. Since then, the relational model has
standardized on that machinery for FK enforcement, identity lookup, and Change Queries:

- **`UX_<R>_RefKey`** — the fully-flattened scalar identity plus `DocumentId` — exists on every
  concrete resource that some other resource in the effective schema references
  (`ReferenceConstraintPass.EnsureTargetUnique`). It is the composite-FK target that keeps reference
  binding columns consistent under cascades ([key-unification.md](key-unification.md)) and the
  identity-first probe shape used by natural-key resolution and by `/deletes` recreated-row probes
  where present. Emission will stay conditional: the resolver can only ever be asked to probe a
  resource that the schema references, and that is exactly the set carrying a RefKey. Never-referenced
  resources (in Data Standard 5.2 these include the highest-volume leaf tables such as attendance
  events and gradebook entries) will not pay for a wide identity index they cannot be probed through;
  their `/deletes` handling is discussed under
  ["Consistency and integrity"](#consistency-and-integrity). Because identity flattening
  is recursive, a reference-bearing identity (e.g., a Section, whose identity contains a
  CourseOffering reference, whose identity contains Course and Session references) collapses to
  **one flat list of scalars** — resolvable in a single index seek, no multi-pass dependency
  layering. Under key unification the RefKey binds the canonical storage column (for example
  `SchoolId_Unified`), not a generated alias, so probe metadata must bind storage columns.
- **`UX_<R>_NK`** — the natural-key unique constraint with reference-sourced parts as
  `..._DocumentId` columns — exists as the identity-uniqueness enforcement and the source of
  create-race unique violations (the 409/retry path).
- **`<Abstract>Identity` tables** with their own `RefKey` indexes exist to enforce cross-subclass
  identity uniqueness and to serve as polymorphic FK targets. This design will add the concrete
  member `ResourceKeyId` plus a composite `(DocumentId, ResourceKeyId)` FK to `dms.Document` so
  abstract reference resolution can return the same compatibility token the runtime uses today without
  allowing the identity row to disagree with its owning document. Resolution will still be one probe
  of one table — not a union over subtype tables.

The descriptor-specific probe target missing was a case-insensitive descriptor lookup, which this
design will add as a lower-storage unique index on the existing `dms.Descriptor` table: a PostgreSQL
expression index, and a SQL Server non-persisted computed-column index. Case folding will be owned
entirely by the database engines — PostgreSQL's builtin `pg_c_utf8` collation (requiring
PostgreSQL 17+) and SQL Server's `SQL_Latin1_General_CP1_CI_AS` identity collation — so C# will
never lowercase descriptor values. Descriptor URI values must be well-formed (no NUL, no unpaired
surrogates) but will otherwise be accepted without a character-repertoire restriction, including
non-ASCII characters. SQL Server's accepted version-80 collation limitation will make identity
comparison lossy for some of that input space (see accepted trade-off #8).

**2. The hash is derived state, and derived state has carrying costs.** Every document insert and
identity update fires a generated trigger that recomputes UUIDv5 hashes and writes one or two
`dms.ReferentialIdentity` rows (subclasses also write a superclass-alias row). That is write
amplification on the hottest path in the system. Because the table *can* drift from the row state it
is derived from, the resolver carries corruption-verification CTEs (a canary comparing request
identity against re-projected root state) — complexity that exists only to detect the failure mode
the `dms.ReferentialIdentity` state itself introduces. Natural-key lookups for concrete resources,
descriptors, and POST upsert detection will read authoritative rows/indexes directly; the
`dms.ReferentialIdentity` drift class and its resolver canary will disappear. Abstract reference
resolution is the exception: it will continue to rely on trigger-maintained `<Abstract>Identity`
rows, so this design must keep explicit abstract-identity parity/corruption coverage rather than
treating derived state as globally abolished.

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
| "Polymorphic targets get significantly harder; abstract identity tables reintroduce central indexes with drift risk" | Abstract identity tables already exist and are already trigger-maintained — for cross-subclass uniqueness enforcement. Resolution will reuse them and add only a concrete-member `ResourceKeyId` column plus a composite `(DocumentId, ResourceKeyId)` FK back to `dms.Document`; no new lookup tables, natural-key/probe indexes, trigger families, or extra row writes will be introduced for polymorphism. |
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
| Corruption-canary verification | CTEs comparing request identity vs re-projected root state | RI canary deleted; abstract identity drift covered by dedicated parity/corruption pins |
| POST upsert detection | The write path's capture predicate (a `ReferentialId` subselect), issued as statement 1 of the composite command or as the first command of the ordered-segments fallback; `RelationalWriteTargetLookupResolver`'s RI lookup is registered but has no production resource-POST consumer | Natural-key capture predicate on both paths; the unused RI lookup builders are deleted |
| Descriptor upsert detection | `ReferentialId` probe in the descriptor write handler | Lowered-URI + `ResourceKeyId` probe |
| Descriptor-valued query filters | Query preprocessor lowercases + hashes the URI | Backend relational query preprocessing identifies descriptor-id targets from compiled query metadata, rejects NUL-bearing URI values as 400 validation failures (unpaired surrogates cannot reach a query string; see "Descriptors"), then probes the descriptor lower-URI index with the raw validated value (the probe folds it in SQL) |
| 409 duplicate-identity messages | Rebuilds NK column lists from `ReferentialIdentityMaintenance` trigger metadata | Re-sourced from compiled natural-key probe metadata (severed *before* the triggers drop) |

Verified non-consumers (these will be untouched by this design): row locking (`dms.Document` by `DocumentId`),
DELETE (captures by `DocumentUuid`; the only interaction was the ON DELETE CASCADE), GET-by-id,
`?id=` queries, link injection, ownership authorization, stamping and tracked-change triggers, Change
Query routing/response contracts/authorization/`/keyChanges`, and the entire DocumentCache path.
Change Query `/deletes` recreated-row detection is a consumer of the natural-key and descriptor
identity contracts below, so it will be updated by this design.

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
therefore harmless. The comparer will use ordinal equality over every identity value, including the
raw (unfolded) URI as the descriptor key member — C# will own no case folding, so the comparer will
never approximate an engine verdict. Raw spellings that an engine considers the same identity (regular
case variants on SQL Server; descriptor case variants on both engines; and accepted SQL Server
linguistic or unweighted-code-point aliases) may remain separate memo entries and produce redundant
probe rows. The database will still resolve them to the same `DocumentId`, and the structural
comparer will never mis-merge two distinct raw identities.

This comparer requirement is an enforceable contract, not a comment on a caller-owned dictionary.
The structural key itself is new: the resolver will introduce an internal `ReferenceLookupKey`
record struct over `(target resource, DocumentIdentity)` (nothing like it exists today; the current
resolver memoizes by the `ReferentialId` Guid). The resolver result will expose a dedicated
document-reference map/factory contract (`IResolvedDocumentReferenceMap` /
`IResolvedDocumentReferenceMapFactory`, sketched in
[flattening-reconstitution.md](flattening-reconstitution.md)) instead of a raw
`IReadOnlyDictionary<ReferenceLookupKey, long>`. A plain dictionary could silently use
`ReferenceLookupKey`'s default record-struct equality, which would inherit array reference equality
from `DocumentIdentity` and miss semantically identical identities backed by different arrays. The
factory will be the only construction path for the resolved document-reference map and will install
the structural comparer; consumers will look up by `(target resource, DocumentIdentity)` through that
map rather than by direct dictionary indexing. DMS-1450 introduces the key, map, and factory behind
the internal seam; DMS-1451 re-points `ResolvedReferenceSet` and its consumers to the map.

The Core cleanup is part of this design, not a follow-up. `ReferentialId`,
`ReferentialIdFactory`, `ReferentialIdCalculator`, `No.ReferentialId`, and every
`ReferentialId` member on `DocumentReference`, `DescriptorReference`, `SuperclassIdentity`, and
`DocumentInfo` will be removed. Extractors and middlewares that currently share the UUIDv5 key will
move together to the structural natural-key comparer so Core will no longer compute a UUIDv5 value
for documents, references, descriptors, duplicate-item validation, or write-target setup.

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
     AS keys(ordinal, "SchoolId", "SchoolYear", "SessionName")
JOIN edfi."Session" target
  ON target."SchoolReference_SchoolId" = keys."SchoolId"
 AND target."SchoolYearTypeReference_SchoolYear" = keys."SchoolYear"
 AND target."SessionName" = keys."SessionName"
```

PostgreSQL examples must not use `WITH ORDINALITY` for these probe inputs. The explicit
`@ordinals` array is the only attribution source; generated row-position ordinality would
reintroduce a second ordinal model and could incorrectly encourage implementers to map errors by
input row position rather than by `Entries[ordinal-1]`.

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
`FK_Descriptor_DocumentResourceKey` will pair `(DocumentId, ResourceKeyId)` back to
`dms.Document(DocumentId, ResourceKeyId)` so the descriptor-type key cannot drift from the owning
document. That FK will become the sole owner of the descriptor/document `ResourceKeyId` invariant:
the equality guard at the top of today's `TF_/TR_Descriptor_Stamp_Document` triggers (an `EXISTS`
re-check on every descriptor INSERT/UPDATE that raises "diverges from the owning dms.Document row",
justified in the emitter by "no FK ties the two together") will be retired in the same step, so
the invariant has one declarative, every-writer enforcement point and the descriptor write path
drops a per-row subquery. A mismatch is a DMS defect, never client input, so surfacing it as an FK
violation instead of the bespoke message loses nothing at the API. `Discriminator` remains stored for diagnostics/read compatibility, but descriptor
resolution will not depend on it.

**Abstract targets** — a probe of `UX_<Abstract>Identity_RefKey` projecting
`(Ordinal, DocumentId, ResourceKeyId)`. `ResourceKeyId` will be the concrete member resource key
stored on the abstract identity row and populated by the abstract-identity trigger from the same
compile-time member metadata that supplies the diagnostic `Discriminator`. The resolver will not parse or map the
abstract `Discriminator`; `IncompatibleTargetType` will continue to compare the resolved concrete
`ResourceKeyId` with the target's allowed concrete resource keys. The abstract `RefKey` and `NK`
index key shapes will remain unchanged; `ResourceKeyId` will be payload only, not part of abstract
identity equality. The abstract identity table must FK-constrain `(DocumentId, ResourceKeyId)` to
`dms.Document(DocumentId, ResourceKeyId)` because the resolver will treat the projected value as
authoritative for compatibility, not diagnostic metadata, and the projected key must match the owning
document's concrete resource type. The compiled abstract-identity trigger contract must carry a typed
concrete-member `ResourceKeyId` literal alongside the diagnostic discriminator literal; dialect
emitters must not recover the key by parsing `Discriminator` or re-deriving it from the source table.
One table, one seek, no per-subtype SQL.
The abstract identity table will be the required write-time resolution surface. Any
`{AbstractResource}_View` union view is diagnostic/integration-only and must not be required for
reference resolution, target-type compatibility, or API correctness.

Results will map by explicit ordinal (`Entries[ordinal-1]`, never row position); unmatched ordinals
will flow into the unchanged reference-validation failure response, so error shapes and JSON-location
attribution will be identical to today. On SQL Server a group-count guard will enforce the shared
parameter budget (`MssqlCommandLimits.MaxUserParametersPerCommand`, 2098) before building the command.

The probe metadata (`NaturalKeyProbeTargets`, `OwnNaturalKeyProbesByResource`,
`DescriptorProbeTarget` on the compiled `MappingSet`) will be compiled from the relational model
itself — never from trigger metadata, never by parsing constraint names (dialect identifier
shortening hash-truncates names), and never by converting abstract discriminator strings to resource
keys at runtime. It will be storage-resolved, so key-unified identity parts will bind their canonical
stored columns and abstract probes will bind the stored concrete `ResourceKeyId`. Each probe key entry
will also carry its command-binding metadata: scalar keys carry `RelationalScalarType`; descriptor-valued
identity parts carry the descriptor resource whose compile-time `ResourceKeyId` drives the inline
descriptor join; own-natural-key document-reference parts carry the
`ResourceWritePlan.Model.DocumentReferenceBindings` index for the reference site that supplies the
stored `..._DocumentId` key value. That document-reference binding will be valid only for
`OwnNaturalKeyProbe.KeyColumns`; normal target probes will still consume a fully flattened
`DocumentIdentity` and therefore need only scalar/descriptor binding metadata.

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
  each reference-sourced part will be represented in `OwnNaturalKeyProbe.KeyColumns` as a
  `DocumentReference` binding to the owning resource's `DocumentReferenceBinding`. That binding will
  supply the reference-object path, target resource, target identity order, and local
  `..._DocumentId` key column being compared. The probe compiler must obtain this binding from the
  relational model; command builders must not recover the reference site by parsing the key column
  name or by re-reading `ApiSchema.json`.
- `UX_<R>_NK`, `OwnNaturalKeyProbe`, POST capture, create-race classification, and duplicate-identity
  diagnostics must all share the same root natural-key column contract. Scalar identity parts bind to
  scalar path/binding columns, descriptor identity parts bind to resolved `..._DescriptorId` columns,
  and document-reference-sourced identity parts bind to the resolved reference `..._DocumentId`
  column only. The propagated reference identity-part binding columns still exist for FK/cascade
  consistency, query binding, and reconstitution, but they are not extra `UX_<R>_NK` members. Adding
  them would split the DDL uniqueness contract from the resolver/upsert contract, which always reasons
  about reference identity through the resolved target `DocumentId`.
- A document-reference capture binding will resolve the referenced `DocumentId` by target kind:
  - **Concrete target:** emit a scalar subselect over the concrete target root table's flattened
    `RefKey` columns and return its `DocumentId`.
  - **Abstract target:** emit the same scalar subselect shape against the compiled
    `{AbstractResource}Identity` table, ordered by the abstract identity fields, and return its
    `DocumentId`. There is no abstract root table to probe. `ResourceKeyId` will remain payload for
    the later compatibility check; the capture predicate will only need the referenced `DocumentId`.
  - **Descriptor-valued identity part inside the referenced target identity:** use the
    `dms.Descriptor` lowered-URI + descriptor `ResourceKeyId` subselect (the same probe shape as
    descriptor targets) to produce the descriptor `DocumentId` key value before comparing the
    target key column.
  The 0-or-1 cardinality guarantee does **not** come from `UX_<Target>_RefKey` alone: because
  `DocumentId` trails that key, its uniqueness is vacuous while `DocumentId` is the value being
  discovered. For concrete targets, cardinality is instead the consequence of the target's
  `UX_<Target>_NK` identity constraint plus the composite-FK/cascade invariants that keep flattened
  `RefKey` copies in parity with the natural key. For abstract targets, cardinality is the
  consequence of `UX_<Abstract>Identity_NK` plus the same abstract-identity maintenance invariants.
  Generated capture probes must therefore treat multiple matches as invariant drift and fail loudly;
  they must never use `LIMIT 1`, `TOP 1`, or equivalent row picking to mask duplicate candidates.
  All parts will bind from the payload's flattened `DocumentIdentity` — scalar identity parts
  directly, descriptor parts through the descriptor probe, and document-reference parts through the
  compiled reference-site capture binding.
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
- All parameters will be renameable, as the composite statement rewriter requires (it renames
  parameters; PostgreSQL array parameters are embeddable today, only SQL Server TVPs are not).

The ordered-segments fallback path (taken when the resolver cannot embed a single-statement lookup)
will use **the same inline-subselect capture predicate**, issued as its own first command. Today that
path captures and locks the target *before* it resolves references
(`RelationalWriteFirstPhase.ResolveInOrderedSegmentsAsync`), so no resolved `..._DocumentId` values
exist at capture time on either path; the fallback will not be resequenced by this design, and no
second, "flat" capture shape will be introduced. There is no separate production POST target-lookup
consumer to replace: `RelationalWriteTargetLookupResolver` is registered but not consumed by the
write executor for resource POST (its RI lookup support is used only by the descriptor write
handler, which DMS-1454 cuts over), so its RI lookup builders will be deleted rather than re-pointed.
The flat own-`UX_<R>_NK` probe below therefore describes the shape that will be used wherever
resolved `..._DocumentId` values *are* in hand — post-resolution invariant checks and tests, and
the contingency ladder's measurement harness — with metadata projected through the `dms.Document`
join:

```sql
SELECT root."DocumentId", d."DocumentUuid", d."ContentVersion"
FROM edfi."Section" root
INNER JOIN dms."Document" d ON d."DocumentId" = root."DocumentId"
WHERE root."CourseOffering_DocumentId" = @p0
  AND root."SectionIdentifier" = @p1
```

Wherever it is used, a missing resolved reference will short-circuit to "no target" without probing,
and more than one row will be an invariant violation and throw.

**Create races will be unchanged:** two concurrent POSTs of the same new identity will still race to
the `UX_<R>_NK` unique constraint, and the loser will be classified into the existing 409/retry flow.
The 409 `duplicateIdentityValues` message machinery will re-source its column lists from the compiled
probe metadata instead of trigger metadata — a strict prerequisite, to be landed before the triggers
drop.

**Contingency ladder** (pre-agreed; climb only on measurement):

1. *Baseline:* the inline-subselect capture predicate above — zero schema change, uniform for all
   resources.
2. *If its benchmark case lags:* capture via the resource's **own `UX_<R>_RefKey`** using flattened
   payload scalars — a flat single seek, no subselects, zero schema change for every resource that
   already carries a RefKey (i.e., every referenced resource). For a never-referenced resource this
   rung requires emitting a RefKey for that resource, which is a measured, per-resource schema
   decision, not a blanket rule. Cost: a second capture shape and a wider identity-copy predicate,
   so keep it behind measurement.
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
duplicating the lowered URI in the base table. **Case folding will be engine-owned**: C# will never
lowercase a descriptor value. Descriptor URI values will accept all well-formed Unicode scalar values
— non-ASCII included — subject to one well-formedness boundary with two distinct mechanisms:

- **NUL** (U+0000, unstorable in PostgreSQL `text`/`varchar`) *can* reach a C# string — a JSON
  `\u0000` escape materializes as `'\0'`, and a query-string `%00` percent-decodes to it — so
  descriptor writes, descriptor references, and descriptor-valued query filters will reject it with
  a path-attributed 400 through a shared validate-and-assert helper before any descriptor lookup or
  write command.
- **Malformed UTF-16** (an unpaired surrogate from a JSON `\uD800`-class escape, which PostgreSQL
  would reject as invalid UTF-8 at write time) can *never* reach a C# string: `JsonNode.Parse` is
  lazy and succeeds, and the first `GetValue<string>()`/`ToString()` on that node throws
  `InvalidOperationException` ("Cannot read incomplete UTF-16 JSON text as string with missing low
  surrogate"). Today that surfaces wherever the first middleware reads the value, as an unmapped
  5xx, for any string property of any resource. The design therefore rejects it at the body-parse
  boundary: `ParseBodyMiddleware` will materialize string leaves after `JsonNode.Parse` and translate
  that exception into the existing malformed-body 400 (JSON-path attributed). This is body-wide by
  necessity — it cannot be descriptor-specific because the failure occurs at first read — and it is
  a Core parse behavior change, not a descriptor validator. Query strings cannot carry an unpaired
  surrogate at all (`\u` is not an escape there; invalid percent-encoded UTF-8 such as `%ED%A0%80`
  is left as literal text and simply fails to match; raw invalid bytes decode to U+FFFD), so
  query-side validation is NUL-only. Raw invalid UTF-8 body bytes are likewise replaced with U+FFFD
  by the framework decoder before parsing and are accepted as that (well-formed) code point.

The PostgreSQL collation will be part of the descriptor identity contract, and it will pin the
engine's folding rules. Every PostgreSQL descriptor identity index, lookup predicate, and Change Query
recreated-row probe must lower values under the builtin **`pg_c_utf8`** collation, for example
`lower("Uri" COLLATE "pg_c_utf8")`. The implementation must not emit an unqualified `lower("Uri")`
or `lower(<namespace-codeValue expression>)` and rely on the database default. `pg_c_utf8`
requires **PostgreSQL 17+** (the pinned minimum version for this design) and a UTF-8 database
encoding; its folding tables ship inside PostgreSQL and change only at a PostgreSQL major
upgrade, so the upgrade playbook must include a `REINDEX` of the descriptor expression index —
and account for the case where a Unicode revision makes two stored descriptors newly collide,
which blocks the `REINDEX` until the data is resolved manually. On SQL Server, folding will follow
the `LOWER(...)` computed column and the explicitly emitted `SQL_Latin1_General_CP1_CI_AS`
identity collation. Every parameter-side descriptor fold must select that same casing table before
`LOWER` executes, for example `LOWER(@uri COLLATE SQL_Latin1_General_CP1_CI_AS)`; an unqualified
`LOWER(@uri)` would fold under the database default before collation precedence is applied to the
comparison. This rule covers write/upsert, reference-resolution, query-filter, descriptor-valued
identity, and Change Query recreated-row probes. The two engines' non-ASCII verdicts differ — an
accepted trade-off (see "Risks and accepted trade-offs").

The SQL Server difference is deliberately broader than case folding. The chosen
`SQL_Latin1_General_CP1_CI_AS` identity collation is a legacy version-80 `SQL_*` collation. It can
store well-formed supplementary characters in `nvarchar`, but it does not assign meaningful
comparison weights or case mappings to the full Unicode repertoire. Unsupported code points are
therefore ignorable in comparison: on SQL Server 2025, for example, `A😀` compares equal to `A`, and
`A😀` compares equal to `A😁`; the older casing table also leaves some mapped characters unchanged
(`LOWER(N'Ǹ' COLLATE SQL_Latin1_General_CP1_CI_AS) = N'Ǹ'`). Its linguistic comparison may
additionally equate canonically equivalent spellings such as precomposed `é` and `e` + combining
acute even though DMS performs no Unicode
normalization. These are not validation failures: they are accepted descriptor-identity aliases.
The unique index, resolver/upsert probes, stored-wins behavior, and Change Query recreated-row probes
must all treat each such pair as the same SQL Server descriptor identity. The project accepts this
limitation to preserve the fixed ODS-aligned SQL Server identity collation; live fixtures will pin
the known examples so an engine change is visible.

On SQL Server the `LOWER` computed column will be retained deliberately, even though the CI
collation alone would make a plain `(Uri, ResourceKeyId)` unique index case-insensitive. `LOWER` applies
the collation's *casing table* while the index applies its *comparison weights*; these are
distinct tables that disagree for some characters (Windows lowercasing folds dotted `İ` to `i`,
for example, while `Latin1_General` comparison treats `İ` as a distinct letter). Keeping `LOWER`
will therefore fold slightly more than raw CI comparison would — closer to PostgreSQL's Unicode
simple fold — and preserve the cross-engine index shape at zero storage cost (the computed
column will be non-persisted; the folded value will exist only in index keys).

**This is an implementation change, not only a storage or documentation constraint.** The current
write extraction and query preprocessing implementations lowercase arbitrary Unicode descriptor
values in C#. They must change as follows:

- For a descriptor resource POST/PUT, Core's descriptor identity extraction will derive the URI from
  the canonicalized `$.namespace` + `#` + `$.codeValue` and validate the two client-supplied
  components as well-formed without NUL. It will not lowercase. A failure will be attributed to each
  offending source path (`$.namespace` and/or `$.codeValue`) and will stop the write before
  descriptor target lookup or a descriptor write command.
- For a descriptor reference in a resource body, Core's descriptor extraction will validate the raw
  URI at its concrete request JSON path before constructing the descriptor identity. A failure will
  stop the write before the reference resolver is invoked.
- For a query field compiled to a descriptor-id target, the relational backend will perform the
  well-formedness validation during query preprocessing. Core query validation will remain
  responsible for generic query-field recognition, scalar type validation, and query-element
  construction, but it does not own `RelationalQueryFieldTarget.DescriptorIdColumn`; that target is
  backend compiled metadata. `RelationalQueryRequestPreprocessor` will therefore use the selected
  `RelationalQueryCapability` to identify descriptor-id targets, validate the query value before it
  creates a descriptor reference or calls the resolver, and surface a failure with the existing
  path-attributed 400 query-validation response shape. The failure must not be represented as
  `RelationalQueryPreprocessingOutcome.EmptyPage`, because malformed input is not a lookup miss.
  The validated value will be passed to the probe raw; the probe will fold it in SQL.

The NUL validation boundary will run after ordinary request parsing, schema validation, coercion,
profile shaping, and the existing trimming rules (unpaired-surrogate escapes never get that far;
they are answered at body parse as described above). NUL will be rejected because PostgreSQL
`text`/`varchar` cannot store it — validating up front turns it into a path-attributed 400 instead
of an engine error. Downstream write flattening, key unification, descriptor upsert detection, and query
lookup must consume only values that have passed this validation, and must never lowercase them:
the shared helper will be validate-and-assert ("validated well-formed without NUL"), and any
`ToLowerInvariant()` call on a descriptor value will be a defect — C# case folding would diverge from
the engine verdicts that define descriptor identity. Two further deliberate consequences: **DMS
performs no Unicode normalization**; under PostgreSQL's code-point comparison, canonically
equivalent spellings such as precomposed `é` vs `e` + combining accent will remain distinct
descriptors, while SQL Server's accepted linguistic collation may treat them as one identity without
rewriting either stored value. Descriptor-valued query parameters are UTF-8 percent-decoded by the framework
before validation. The corresponding write and query algorithms are updated in
[flattening-reconstitution.md](flattening-reconstitution.md), [key-unification.md](key-unification.md),
and [transactions-and-concurrency.md](transactions-and-concurrency.md).

| Object | Definition |
|---|---|
| PostgreSQL `UX_Descriptor_UriLowered_ResourceKeyId` | Unique expression index: `CREATE UNIQUE INDEX "UX_Descriptor_UriLowered_ResourceKeyId" ON dms."Descriptor" (lower("Uri" COLLATE "pg_c_utf8"), "ResourceKeyId");` |
| SQL Server `dms.Descriptor.UriLowered` | Non-persisted computed column: `[UriLowered] AS LOWER([Uri])` |
| SQL Server `UX_Descriptor_UriLowered_ResourceKeyId` | Unique index on `[UriLowered], [ResourceKeyId]` |

The lowercased value will be stored only in the index key (and only as computed index state on SQL
Server), not as a persisted duplicate in the descriptor row. The legacy
`UX_Descriptor_Uri_Discriminator` will coexist with the new index from DMS-1448 (T6) until the
final removal story DMS-1456 (T14) drops it — after descriptor writes (DMS-1454) and Change Queries
(DMS-1455) have moved to the lowered-URI + `ResourceKeyId` contract. The CI index is additive, so
keeping the legacy unique through the transition costs nothing.

`ResourceKeyId`, not `Discriminator`, will be the descriptor-type authority. This matches the
existing descriptor architecture: `ResourceKeyId` is required and already drives type identity;
`Discriminator` will be retained for diagnostics and read compatibility but will not participate in
descriptor lookup or uniqueness.

The same identity contract will apply when Change Queries determines whether a deleted row was
recreated. Descriptor `/deletes` anti-joins, and the descriptor-valued identity joins used by
resource `/deletes`, will probe the live descriptor table by the lowered tombstoned
`<namespace>#<codeValue>` URI plus the descriptor resource's compile-time `ResourceKeyId`. A shared
descriptor tombstone's `Discriminator` may be used only to route historical rows to the requested
descriptor endpoint; it will not be used as live descriptor identity or converted into a resource
key. Consequently, any descriptor recreation that the engine considers the same identity will
suppress the old tombstone: this includes casing differences on both engines and the accepted linguistic or
unweighted-code-point aliases on SQL Server.

The descriptor write handler will simplify from three tables to two: the `ReferentialId`
CTE/`INSERT`/`ON CONFLICT`/`MERGE` statements will be deleted, and upsert detection will become a
lowered-URI + `ResourceKeyId` probe. PostgreSQL will probe the expression index:

```sql
SELECT descriptor."DocumentId", d."DocumentUuid", d."ContentVersion"
FROM dms."Descriptor" descriptor
INNER JOIN dms."Document" d ON d."DocumentId" = descriptor."DocumentId"
WHERE lower(descriptor."Uri" COLLATE "pg_c_utf8") = lower(@uri COLLATE "pg_c_utf8")
  AND descriptor."ResourceKeyId" = @resourceKeyId
```

SQL Server will probe the computed-column index:

```sql
SELECT descriptor.[DocumentId], d.[DocumentUuid], d.[ContentVersion]
FROM [dms].[Descriptor] descriptor
INNER JOIN [dms].[Document] d ON d.[DocumentId] = descriptor.[DocumentId]
WHERE descriptor.[UriLowered] = LOWER(@uri COLLATE SQL_Latin1_General_CP1_CI_AS)
  AND descriptor.[ResourceKeyId] = @resourceKeyId
```

The `dms.Document` insert, `SCOPE_IDENTITY()` retrieval, row lock, uuid lookups, and delete builder
will all keep their current shape.

### Query-time descriptor filters

The resolver-facing query preprocessor still consumes `IReferenceResolver`, and its validated raw
value will feed the descriptor lower-URI probe instead of a hash; the probe will fold the parameter
in SQL under the explicitly selected descriptor identity collation. The validation boundary will move
into that preprocessor because descriptor-id query targets are
backend compiled relational metadata, not Core validation metadata. Core will continue to parse and
validate generic query fields/types, then pass query elements downstream.
`RelationalQueryRequestPreprocessor` will inspect the selected `RelationalQueryCapability`, identify
fields whose compiled target is `RelationalQueryFieldTarget.DescriptorIdColumn`, reject NUL-bearing
values with the existing path-attributed 400 response (the only malformed input a query string can
deliver; unpaired surrogates cannot arrive through percent-decoding), and only
then create a descriptor reference or invoke `IReferenceResolver`. This requires a
validation-failure preprocessing path (or equivalent typed exception translated by the
repository/frontend) rather than reusing `RelationalQueryPreprocessingOutcome.EmptyPage`: a valid
descriptor URI that does not resolve will still return an empty page, but malformed URI input will
be a client validation error. The preprocessor will delete its `ToLowerInvariant()` call entirely: it
will validate with the shared well-formedness helper and pass the value through unfolded.
GET-by-id, `?id=`, link injection, ownership authorization, and descriptor paging will not get
result-contract changes. Change Query route/response/authorization contracts remain
unchanged, but `/deletes` recreated-row detection will follow the lowered-URI + `ResourceKeyId`
descriptor identity contract described above.

### Query-time string filters (`?field=value`)

Relational GET-many string equality filters on SQL Server currently override the column collation
with `COLLATE Latin1_General_100_BIN2` (`MssqlPlanDialect`, a DMS-993 decision taken to mirror
PostgreSQL's byte-exact `=` before DMS declared any identity collation). PostgreSQL emits a plain
`"Column" = @p`. Once identity columns carry the explicit DMS CI collation, that override would
leave SQL Server internally inconsistent — `StudentUniqueId` matched case-insensitively for
upsert and uniqueness but case-sensitively for `?studentUniqueId=` — and it is non-sargable
because the collation cast sits on the column side of the predicate.

This design will therefore remove the SQL Server override: string `=` filters will render as
`t.[Column] = @p` on both engines and follow the **column's** collation. On SQL Server, identity
string columns will compare under the explicit `SQL_Latin1_General_CP1_CI_AS` contract
(deployment-independent, index-seekable), and non-identity string columns will compare under the
database default collation, exactly as ODS does. PostgreSQL filters will remain case-sensitive. The
per-engine split matches the identity-matching split accepted in
["The contract"](#the-contract) and the API Guidelines' non-mandatory SHOULD on case-insensitive
value matching. Descriptor-valued filters will be unaffected: they will resolve through the
descriptor probe, never through a string comparison on the resource table. This supersedes the
"ordinal/case-sensitive string semantics" default recorded for DMS-993 in
[`epics/08-relational-read-path/04-query-execution.md`](../epics/08-relational-read-path/04-query-execution.md)
and the matching descriptor-endpoint answer in
[`05-descriptor-endpoints.md`](../epics/08-relational-read-path/05-descriptor-endpoints.md); the
two SQL Server pins that assert case-sensitive filtering will flip, and the E2E mixed-case-value
query scenarios that were left ignored under DMS-993 will be resolved with per-engine expectations.

## Casing and identity semantics

Moving identity matching into the database forces the casing question into the open. PostgreSQL's
DMS schema compares strings case-sensitively. SQL Server does not supply one dialect-wide answer:
string equality follows the participating column or expression collation, and DMS provisioning
supports and preserves a case-sensitive database default. The hash era *hid* this distinction behind
an ordinal hash that disagreed with the standard SQL Server case-insensitive deployment (the internal
inconsistency described above). This design states the SQL Server column contract explicitly and
will enforce it. The target model is **ODS behavior minus its bugs**, verified against ODS v7.3.2 code and
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
string identity columns in abstract-identity tables, tracked-change old/new string copies of identity
values, and local string identity members used by child or extension collection uniqueness. Both
sides of any string-bearing identity FK will therefore have the same explicit collation. Descriptor
identity will keep its lowered-URI lookup contract described above; its SQL Server source and
computed identity columns will also be emitted under the DMS default CI collation. Columns with a
purpose-specific stronger contract, such as the existing `Latin1_General_100_BIN2` lifecycle token,
will retain that explicit collation.

Tracked-change identity copies are part of this contract because Change Queries compare historical
`Old*`/`New*` values back to live identity and descriptor columns. `/deletes` uses those comparisons
to suppress tombstones for rows recreated under a new `DocumentId`, and `/keyChanges` exposes the
same stored identity values. On a supported case-sensitive SQL Server database default, allowing
tracked-change identity copies to inherit the database collation could either produce collation
conflicts or make recreated-row detection depend on the database default instead of DMS identity
semantics. Therefore every SQL Server `TrackedChangeColumnInfo` string column whose origin includes
identity, including descriptor `OldNamespace`/`OldCodeValue` and `NewNamespace`/`NewCodeValue`, will
be emitted with `COLLATE SQL_Latin1_General_CP1_CI_AS`. Routing-only columns such as the shared
descriptor `Discriminator`, and non-identity tracked scalar payloads, are outside this identity
collation rule unless another explicit contract applies.

This is a column contract, not a database provisioning constraint. SchemaTools will continue to
preserve an operator-selected case-sensitive database collation; generated DMS identity columns will
still compare case-insensitively because their `COLLATE` clauses take precedence. Natural-key unique
constraints and lookup predicates will use those columns directly, so they will necessarily evaluate
under the same equality semantics.

Ordinary scalar parameters compared directly with identity columns are coercible to the target
column's collation under SQL Server's collation-precedence rules. That coercion happens too late for
an expression such as `LOWER(@uri)`: `LOWER` has already used the database default's casing table.
Every generated descriptor probe must therefore collate a scalar input *inside* the fold:
`LOWER(<parameter> COLLATE SQL_Latin1_General_CP1_CI_AS)`. A string projected as a column by
`OPENJSON ... WITH` can likewise carry the containing database's default collation, so every
generated natural-key probe will apply the same DMS CI collation explicitly to each textual
`OPENJSON` key operand, as shown in the SQL Server probe above. These rules prevent collation
conflicts and casing-table drift on a database with a different default and keep all operands under
the declared identity contract; they do not discover the database default at runtime.

The backend's runtime identity-equality provider will derive its comparer from this declared schema
contract. The SQL Server contract will select `OrdinalIgnoreCase`; the PostgreSQL contract will
select `Ordinal`. Runtime code must not infer a comparer from the product name alone, inspect the database
default, or maintain an independent dialect switch disconnected from DDL generation. The fixed SQL
Server collation and its comparer selection are one backend contract and will be pinned together by
tests. `OrdinalIgnoreCase` will remain an in-process approximation of the SQL collation rather than
a general-purpose collation emulator; where the database has produced a resolved id, that database
verdict will remain authoritative, and the documented fail-closed residue below will still apply.

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
| Descriptor matching + uniqueness | Case-insensitive via the `LOWER(...)` computed column under the CI identity collation, + `ResourceKeyId`; also includes the accepted version-80 linguistic and unweighted-code-point aliases described above. | Case-insensitive via `lower(... COLLATE "pg_c_utf8")` + `ResourceKeyId` — the first time descriptors are CI on PostgreSQL (ODS *intended* CI descriptors but its PostgreSQL implementation stored CS and could accumulate case-variant duplicates). Uniform with SQL Server for ASCII input; non-ASCII folding verdicts are per-engine (accepted trade-off #8). |
| Descriptor POST-as-update casing | Stored-wins: the update preserves stored `Namespace`/`CodeValue`/`Uri` casing; a casing-only re-POST is a true no-op. A case-only descriptor PUT is a 200 update/no-op, not an error. Matches `DescriptorCaseInsensitiveValidation.feature`. | Same. |
| Core-side equality constraints and duplicate-item validation | Ordinal (stricter than the DMS identity collation; fails closed with 400). The gap this leaves for collections is closed below. | Ordinal (exact). |
| Collection item matching against stored rows (local string semantic-key members) | Case-insensitive under the schema comparer; a comparer-equal item keeps its stored row, `CollectionItemId`, hidden profile columns, and stored casing (casing-only re-PUT is a no-op). Matches ODS child-entity key equality. | Case-sensitive (unchanged): a case variant is a different item (delete + insert). Matches ODS. |
| GET-many string equality filters (`?field=value`) | Follow the column collation (no query-side `COLLATE` override): case-insensitive under the DMS identity collation for identity string columns, database-default collation for other string columns. Matches ODS. | Case-sensitive (unchanged). Matches ODS. |

### How the write path will preserve stored casing (SQL Server)

How ODS preserves stored key casing: NHibernate's per-property dirty checking
uses a case-insensitive comparer on SQL Server, so a CI-equal key property is simply never assigned,
and the UPDATE omits it. DMS's write path is a full-row replacement with no ORM dirty checking, so
preservation must be explicit. Three pieces will be added, all in the shared write executor:

1. **Schema-contract-derived identity comparer.** The identity-stability guard (which today compares
   the merged root row's identity values against the current row ordinally, and rejects changes to an
   immutable identity) will obtain its comparer from the backend identity-equality contract used by
   DDL generation. That contract will supply `OrdinalIgnoreCase` for SQL Server because the identity
   columns will be explicitly `SQL_Latin1_General_CP1_CI_AS`, and `Ordinal` for PostgreSQL. It is not an
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
   `ContentVersion` and record a key change in the tracked-change tables. After the rebind, none of
   that machinery will see a change — no suppression logic will be needed anywhere downstream.
3. **No-op detection will come free.** After the rebind, a casing-only re-POST with an otherwise
   identical payload will be row-for-row equal to current state and will land on the existing
   guarded no-op path: 200, no `ContentVersion` bump, no change event.

PUT will apply the same comparer and rebind **per column** (mirroring ODS's per-property behavior,
including mixed updates: a genuinely changed column will take the normal key-change/cascade path
while a merely recased column will keep its stored casing).

**Collection semantic keys get the same treatment.** The collection merge matches request items to
stored rows by `(ParentScope, SemanticKey)` ([flattening-reconstitution.md](flattening-reconstitution.md#55-update-behavior-for-collections-merge-strategy)).
Today that match uses `ObjectValueArrayComparer` — ordinal for strings — so on SQL Server a request
item whose local string semantic-key member differs from the stored row only in casing (for
example `electronicMailAddress`) would not match: the stored row would be deleted as omitted and the
item re-inserted under a new `CollectionItemId`, recreating nested descendants, firing stamps, and —
under a profile-scoped write — discarding the stored row's hidden columns. That is incoherent with
the identity contract (T1 collates those members CI; the duplicate-detection tier treats the same
two strings as equal within one request) and with ODS, whose child-entity key equality uses the
engine CI comparer and keeps stored casing. Therefore:

- reference and descriptor semantic-key members will continue to match by resolved
  `DocumentId`/`DescriptorId`;
- local string semantic-key members will match with the schema-contract-derived comparer
  (`OrdinalIgnoreCase` under the SQL Server identity collation, `Ordinal` on PostgreSQL);
- on a match that is comparer-equal but byte-different, the member will be rebound to the stored
  row's value before no-op comparison and DML, exactly as root identity will be; the row will keep
  its `CollectionItemId`, hidden profile columns will be preserved, and a casing-only re-PUT of the
  item will be a no-op;
- a byte-different value the comparer does *not* consider equal will remain a semantic-key change
  and keep today's `delete old row + insert new row` semantics.

The comparer residue will be the same as for root identity (see "Comparer residue" below): where
the comparer is stricter than the collation (`ß`/`ss`), the merge will treat the item as a different
row and fall back to today's delete + insert, which the delete-before-insert order keeps free of
unique violations; in the (currently hypothetical) looser direction, the item will be matched and
rebound to the stored value. Neither direction is corruption; both are documented. On PostgreSQL
nothing will change.

Descriptors will get the equivalent treatment in the descriptor write handler: POST-as-update will
bind the persisted identity fields whenever the target is matched *by* identity through the CI
index. Request and stored identity may differ in casing or, on SQL Server, by an accepted linguistic
or unweighted-code-point alias; the database match will be authoritative in every case. After the
rebind, the no-op comparer will treat the persisted identity fields as equal and descriptive fields
ordinally; the PUT identity guard will follow the same engine descriptor-identity verdict and rebind.
Descriptors have no cascade or key-change machinery, so this path will carry no side-effect risk.

Because [`data-model.md`](data-model.md#2-dmsdescriptor-unified) also owns descriptor update
semantics, it must express descriptor immutability in these equality-contract terms. The invariant is
that PUT cannot persist a move to a different descriptor identity; it is not a byte-for-byte request
matching rule. Without that distinction, the data-model rule reads as rejecting the case-only
descriptor PUT that this design requires to return 200/no-op with stored casing intact.

**Comparer residue (documented, two directions):** `OrdinalIgnoreCase` approximates but does not
equal the fixed DMS SQL Server collation, and the two can disagree in either direction:

- *Comparer stricter than the collation* — linguistic equalities such as `ß`/`ss`, or foldings the
  collation applies but invariant simple case mapping does not. Here the guard will fail closed: the
  value will be treated as a real key change on PUT (cascade on cascade-enabled resources, otherwise
  the immutable-identity 400), never as a silent recase. On POST the database will backstop the
  comparer: the update target can only exist because the CI probe matched under the identity-column
  collation.
- *Comparer looser than the collation* — a pair `OrdinalIgnoreCase` equates but the collation
  distinguishes. No such pair is currently known: .NET's `OrdinalIgnoreCase` uses simple case
  folding and, measured on .NET 10, does *not* equate `ſ`/`s`, dotless `ı`/`i`, or Kelvin `K`
  (U+212A)/`k`; and code points the version-80 collation has no data for are unweighted, which makes
  them compare *equal* on the SQL side as well (`Ǹ`/`ǹ` is equal under both). If the
  engine-divergence fixtures ever surface such a pair, the behavior is defined here rather than left
  implicit: the guard will judge the value "unchanged" and the request value will be rebound to the
  stored value (PUT keeps the stored key; collection matching binds to the stored row; tier-2
  duplicate detection over-rejects), which is non-destructive but silent — the same behavior ODS has
  with the same comparer.

For reference: ODS behaves identically in both directions, because it uses the same
approximation and no database-side re-check.
`DatabaseEngineSpecificStringEqualityComparerProvider` selects `OrdinalIgnoreCase` on SQL Server
and `Ordinal` on PostgreSQL; the generated `EntityMapper` detects a primary-key change with that
comparer for string key members (descriptor usages always `OrdinalIgnoreCase`) and, when it reports
"equal," does not copy the request's key values onto the persisted entity — stored casing wins
silently; when it reports "different," it either throws `KeyChangeNotSupportedException` or performs
a real key change with cascades. Generated entity `Equals`/`GetHashCode` use the same comparer for
string key members, and `SynchronizeCollectionTo` matches child items by `Equals` without
re-syncing child key values. Both residue directions are therefore inherited behavior, not a new
deviation.

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

The surviving trigger inventory deliberately excludes SQL Server identity-value fan-out. That old
`MssqlIdentityPropagationTrigger` fallback is retired by
[`sql-server-pruning.md`](sql-server-pruning.md): retained native cascades propagate eligible identity
updates, and pruned full-composite `NO ACTION` edges have no trigger fallback. Reintroducing an
identity-propagation trigger row here would contradict that pruning contract.

| Surviving trigger family | What the binary diff gates | Why it must stay byte-level |
|---|---|---|
| Document stamping — content stamp (resource roots, child scopes, and `dms.Descriptor`) | `ContentVersion` / `ContentLastModifiedAt` bumps | Non-identity fields stay request-wins under this contract: a case-only or trailing-space-only edit changes the served bytes, so the ETag must change and change queries must resurface the document. A collation diff would leave the ETag stale while the body changed. |
| Document stamping — key-change workset | Whether the key-change tracked-change row is emitted | The fail-closed comparer residue: a byte-different-but-collation-equal key change (e.g. `Straße` → `Strasse`) is deliberately allowed through as a real key change, and its cascade rewrites referrer bytes; only a byte-level diff records any of it. |
| Abstract identity maintenance | Whether concrete identity changes propagate into the `<Abstract>Identity` tables | These tables will become the *only* resolution path for abstract references, and PostgreSQL matches them case-sensitively — byte drift between a concrete root and its abstract copy would become user-visible. |

(Non-string columns are never cast — the byte comparison exists only where collation equality and
byte equality can disagree.)

Two further reasons the diffs cannot be delegated to the write path's comparer-and-rebind
discipline: the rebind lives in one application code path, while triggers fire for *every* writer
(ETL, operational data fixes, future code paths) — the trigger is the engine-level backstop that
keeps "stored bytes changed ⇒ versions moved" true unconditionally. And once this design ships,
casing-only identity writes will stop reaching the database on SQL Server (the rebind will remove
them at the source), so the binary identity diff will cost nothing at runtime — it will simply stop
firing.

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
- NUL in descriptor URI writes, descriptor references, and descriptor-valued query filters will
  become a path-attributed 400 validation failure; a JSON `\uD800`-class unpaired-surrogate escape
  anywhere in a request body (any resource, any string property) will become a malformed-body 400
  at parse instead of today's unmapped 5xx. Other non-ASCII descriptor
  URI values will become accepted inputs, with case folding owned by each engine — `pg_c_utf8` on
  PostgreSQL (raising the minimum supported PostgreSQL version to 17) and the CI identity collation
  on SQL Server. The engines' non-ASCII identity verdicts differ; SQL Server's accepted version-80
  behavior includes linguistic and unweighted-code-point aliases (accepted trade-off #8).
- GET-many string equality filters (`?field=value`): today forced case-sensitive by the
  `COLLATE Latin1_General_100_BIN2` query-side override (DMS-993); they will follow the column
  collation — case-insensitive for identity string columns under the DMS identity collation,
  database-default collation for other string columns (ODS parity), and index-seekable. See
  ["Query-time string filters"](#query-time-string-filters-fieldvalue).

PostgreSQL regular-resource behavior will remain unchanged on every pin.

### Collection duplicate detection

Duplicate collection items are detected in two places today, and this design will keep both while
closing one gap:

- **Core (request-local, ordinal):** `ArrayUniquenessValidationMiddleware` compares raw item values
  through ordinal string keys, and `ReferenceArrayUniquenessValidationMiddleware` compares reference
  items (by referential id today; by the structural natural-key comparer after this design). Core
  will stay engine-agnostic and profile-shaped.
- **Backend (storage-resolved):** `RelationalWriteFlattener` already dedupes the siblings of every
  persisted collection scope, per parent scope, on their *materialized* semantic identity — resolved
  `DescriptorId` / `DocumentId` literals plus local scalar values — using `ObjectValueArrayComparer`
  (ordinal for strings), and rejects a collision with a path-attributed
  `RelationalWriteRequestValidationException` (400) before any DML. This is why two case-variant
  descriptor URIs in one collection are a 400 on both engines today: they resolve to one
  `DescriptorId` and the flattener catches them. Reference and descriptor members are therefore
  already compared by the engine's own equality verdict, with zero approximation error, and this
  design will not add a second check for them.

The gap is the **local string-scalar** semantic-key member. Its equality is decided by the column
collation, which the flattener's ordinal comparer does not see. On SQL Server this is a latent
defect on `main` already: collection sibling uniques such as
`UX_StaffElectronicMail_… UNIQUE (Staff_DocumentId, ElectronicMailAddress, ElectronicMailTypeDescriptor_DescriptorId)`
include an uncollated `nvarchar` member that inherits the database default (CI in the standard
deployment), so two items differing only in the casing of `electronicMailAddress` pass Core and the
flattener, both insert, the CI unique fires, and the constraint resolver classifies a non-root
unique violation as `Unresolved` → an unmapped 5xx for what is a client input error. The explicit
DMS identity collation on local collection identity members will make that verdict
deployment-independent, and this design will close the gap where the flattener already stands,
governed by one principle: *never invent an equality definition where the database has issued a verdict;
approximate its verdict only where it hasn't spoken, and say so.*

1. **Reference and descriptor members (exact tier, existing):** compared by resolved
   `DocumentId`/`DescriptorId` — the engine's own verdict, already in hand. Deliberately not a
   string comparison: a C# string comparer would only approximate the collation and would
   reintroduce the same defect in rarer forms.
2. **Local string-scalar identity members (approximation tier, new):** no database verdict exists
   before the write, so the flattener's semantic-identity comparer will compare these members with
   the schema-contract-derived comparer — `OrdinalIgnoreCase` for the SQL Server identity-column
   contract, `Ordinal` on PostgreSQL (where it is exact) — instead of plain `object.Equals`. This
   will stay backend-side so Core stays engine-agnostic (the same placement ODS uses for its
   engine-specific string comparer). Documented residual: the SQL Server comparer will not reproduce
   the fixed collation's padding rules or linguistic equalities; those exotic cases will fall through
   to the sibling unique constraint as an integrity backstop (and natural-key strings with
   leading/trailing spaces are already rejected with 400).

Duplicates caught by either tier will produce the same path-attributed 400 duplicate-item response
shape Core produces today; the flattener's existing exception is already path-attributed and its
message will be aligned with Core's duplicate-item wording so clients see one shape regardless of
which tier fired. The sibling unique constraint's runtime meaning will stay "race/integrity
backstop," never routine input validation.

**Generic conflict fallback (ODS parity):** unique-constraint violations that the write path's
constraint resolver does not specifically recognize will map to a **409 Conflict** — the same
catch-all translation ODS applies to unique/PK violations — instead of surfacing as an unmapped 5xx.
The well-shaped, path-attributed 400s will still come from the two detection tiers above; the
fallback will only dress the backstop's rare firings (linguistic equalities such as `ß`/`ss` in local
string scalars, which the collation folds but `OrdinalIgnoreCase` does not, plus any future unique
constraint without a specific mapping). Specific classifications will keep their existing semantics
— in particular, the natural-key create-race classification and its retry behavior will be
untouched; the fallback will replace only the unmapped-failure terminal.

## Consistency and integrity

An audit of every invariant `dms.ReferentialIdentity` participates in, and its coverage after
removal:

| Invariant | Enforced after removal by |
|---|---|
| Concrete identity uniqueness | `UX_<R>_NK` (always was the primary enforcement) |
| Cross-subclass abstract identity uniqueness | `UX_<Abstract>Identity_NK` on the abstract identity tables (this is the alias row's real job, and the tables already enforce it) |
| Abstract reference compatibility | The concrete `ResourceKeyId` stored on the matched abstract identity row, FK-constrained with `DocumentId` to the owning `dms.Document` row, then compared with the target's allowed concrete resource-key set |
| Descriptor identity uniqueness | `UX_Descriptor_UriLowered_ResourceKeyId` (CI over the engine-lowered URI, both engines) |
| Create-race detection (409/retry) | `UX_<R>_NK` unique violations, classified exactly as today |
| Reference targets exist and stay consistent | Composite FKs onto `RefKey` targets, unchanged |
| Reference-resolution cardinality | `UX_<R>_NK` plus FK/cascade parity between natural-key columns and flattened `RefKey` copies; `UX_<R>_RefKey` is the access/FK shape, not scalar identity uniqueness while `DocumentId` is unbound |

The abstract rows are the critical transfer point for the removed central index. `UX_<Abstract>Identity_NK`
must enforce cross-subclass equality over only the abstract identity fields, while
`UX_<Abstract>Identity_RefKey` must expose those same fields plus trailing `DocumentId` for abstract
FKs and probes. Golden DDL must pin both constraints' column order and prove that `ResourceKeyId`
and `Discriminator` remain payload only. Golden DDL must also pin the composite
`(DocumentId, ResourceKeyId)` FK to `dms.Document(DocumentId, ResourceKeyId)`, because the
compatibility key will be read from the abstract identity row rather than from the removed central
index. Without that FK, `dms.ReferentialIdentity` would be removed before its abstract-resource
invariants had an explicit relational owner tied to the document's real concrete resource type.

Deliberately lost will be: the `dms.ReferentialIdentity` corruption canary (the RI hash row drifting
from the root row it summarizes), and a redundant second uniqueness net (the RI PK). Mitigations for
the redundancy loss: the probe compiler will carry an empty-identity guard (a resource whose
compiled identity has zero parts will fail compilation loudly), a compile-time parity guard will
prove the compiled probes reproduce the legacy trigger derivation resource-by-resource for as long
as both exist, generated natural-key probes will fail on duplicate candidates instead of silently
choosing one, and golden DDL fixtures will continue to pin the schema.

This is not a claim that every derived-state risk disappears. `<Abstract>Identity` tables will
remain trigger-maintained derived state, and after the cutover they will serve the only resolution
path for abstract references. Their integrity coverage is therefore mandatory: trigger and integration tests
must prove table rows and diagnostic union views stay in parity with concrete root rows across
insert, delete, identity rename, and concrete `ResourceKeyId` population.

Cascades will need no new application-side revalidation: unified identity values are stored once
(aliases are generated columns; FKs bind canonical columns), collection sibling uniques bind
`..._DocumentId` columns (cascade-stable), and parent NK uniqueness gates any key change before it
can cascade.

### `/deletes` recreated-row detection on never-referenced resources

Change Query `/deletes` suppresses a tombstone when a live row with the same identity exists. The
live join binds the resource root by its flattened scalar identity — the RefKey column set — so on
referenced resources it seeks `UX_<R>_RefKey` (see
[change-queries.md](change-queries.md#_refkey-index-ordering-for-deletes)). Never-referenced
resources have no RefKey and will keep the plan they have on `main` today: a partial seek on
whatever scalar parts lead `UX_<R>_NK` plus a residual filter, evaluated once per tombstone row per
page. This design will not change that behavior and will not add an index for it. RefKey emission
will be kept conditional deliberately: the never-referenced set is dominated by high-volume leaf tables,
where a wide identity index would cost storage and insert-time maintenance in an epic whose goal
is storage reduction.

If measurement shows `/deletes` pages on those tables are too slow, the agreed remedy is not a
blanket index but re-shaping the anti-join onto the resource's own natural key, using the same
compiled `OwnNaturalKeyProbe` and `DocumentReferenceBinding` metadata as the POST capture
predicate: each reference-sourced identity part is resolved by a scalar subselect over the
*referenced target's* `RefKey` (the target is referenced, so its RefKey exists) bound from the
tombstone's `Old*` scalar copies; descriptor-valued parts use the lowered-URI + compile-time
`ResourceKeyId` descriptor subselect already used by Change Queries; and the outer join then seeks
the unconditional `UX_<R>_NK`. Cost is one seek per reference-sourced identity part plus one NK
seek per tombstone row — bounded by identity width — and the shape resolves a recreated referenced
target by natural key, so suppression remains correct when both the row and its referenced target
were deleted and recreated. A missing target yields NULL and shows the tombstone (correct: nothing
live can reference an absent target); more than one target match is invariant drift and fails the
page loudly rather than picking a row, consistent with the capture-predicate policy. This remedy is
a follow-on to be filed only on evidence and is not part of the T1–T14 rollout.

## Schema and contract changes

### To be dropped

- `dms.ReferentialIdentity`; dropping the table removes its owned constraints and indexes. Its entry
  in the DMS-managed CDC table inventory (`CdcDmsManagedTableInventory`, which drives the PostgreSQL
  publication and SQL Server capture instances) goes with it — a public CDC contract change: the RI
  change stream disappears for downstream consumers.
- All `TR_<R>_ReferentialIdentity` triggers; the `ReferentialIdentityMaintenance` trigger kind and
  `SuperclassAliasInfo` contract types; `IdentityElementMapping` will shrink from arity 4 to 2
  (`ScalarType`/`IsDescriptorReference` exist only for hash emission); the manifest emitter's RI
  trigger-kind serialization.
- `dms.uuidv5()` on both engines — `ISqlDialect.CreateUuidv5Function` will be removed (breaking for
  Managed-API implementers).
- DMS relational DDL's PostgreSQL `CREATE EXTENSION pgcrypto` preamble and every DMS dependency on
  `digest()`. This is not a global database-drop contract: CMS/OpenIddict PostgreSQL deployment still
  owns `pgcrypto` when encrypted signing keys are stored in the Configuration Service database, which
  may be the same physical database in self-contained shared-database local setups.
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
- Every abstract-identity foreign key whose name has the exact
  `FK_<AbstractResource>Identity_Document` shape and whose only column is `DocumentId`, replaced by
  `FK_<AbstractResource>Identity_DocumentResourceKey` on `(DocumentId, ResourceKeyId)`.
- `FK_Descriptor_Document` and `FK_Descriptor_ResourceKey`, replaced by the single
  `FK_Descriptor_DocumentResourceKey` foreign key on `(DocumentId, ResourceKeyId)`.
- The `ResourceKeyId` equality guard (and its `RAISE EXCEPTION`/`THROW`) at the top of
  `TF_/TR_Descriptor_Stamp_Document`, superseded by that composite FK; the triggers keep their
  no-op guard and stamp/mirror logic.
- The live-descriptor `IX_Descriptor_Discriminator_ContentVersion` index after Change Query
  recreated-row detection moves to `ResourceKeyId`-qualified natural identity probes.
- `UX_Descriptor_Uri_Discriminator` (replaced by the `ResourceKeyId`-authoritative CI unique index).

### To be added

- A SQL Server identity-equality contract pairing the emitted
  `SQL_Latin1_General_CP1_CI_AS` column collation with the runtime `OrdinalIgnoreCase` comparer;
  PostgreSQL's corresponding contract pairs its existing case-sensitive storage with `Ordinal`.
- Descriptor URI NUL validation (shared validate-and-assert helper at descriptor extraction and
  descriptor-valued query preprocessing), plus body-parse rejection of unpaired-surrogate JSON
  escapes in `ParseBodyMiddleware` (body-wide malformed-body 400; the exception to translate is
  `InvalidOperationException` from string materialization, not only `JsonException`).
- A PostgreSQL 17 + UTF-8-encoding floor (both required by the builtin `pg_c_utf8` collation),
  guarded by SchemaTools before any DDL runs, including the pinned CI/compose/Dockerfile
  PostgreSQL 16 bumps and a template-package rebuild on 17.
- `UX_Document_DocumentId_ResourceKeyId`, used only as the parent key for descriptor and abstract
  identity document/resource invariants.
- PostgreSQL `UX_Descriptor_UriLowered_ResourceKeyId` expression index with the lowered URI pinned to
  `COLLATE "pg_c_utf8"`, plus SQL Server
  non-persisted `dms.Descriptor.UriLowered` computed column and
  `UX_Descriptor_UriLowered_ResourceKeyId` index (definitions above).
- Compiled natural-key probe metadata on the mapping set, omitted from DDL manifests (the probe
  metadata itself causes no manifest churn; the DDL changes in this design — abstract
  `ResourceKeyId` and composite FKs, the descriptor index/FK swap, SQL Server identity collation,
  and the RI removal — do change manifests and goldens).
- `NaturalKeyReferenceResolver` + per-engine natural-key lookup command builders.
- A new internal `ReferenceLookupKey` record struct plus a resolved document-reference map/factory
  contract (`IResolvedDocumentReferenceMap` / `IResolvedDocumentReferenceMapFactory`) that owns the
  structural `(target resource, DocumentIdentity)` comparer. The map may use a dictionary internally,
  but no public write-pipeline contract will require callers to provide or index an
  `IReadOnlyDictionary<ReferenceLookupKey, long>` with default equality.

### To be changed

- The SQL Server DDL generator will emit `COLLATE SQL_Latin1_General_CP1_CI_AS` on every string
  column that stores or copies an identity value, including tracked-change old/new identity copies.
  The database default collation will be neither changed nor treated as the identity contract.
  Purpose-specific explicit collations will remain authoritative.
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
    ordinals / natural-key identities, not referential ids. `ResolvedReferenceSet` will expose the
    dedicated resolved document-reference map (`IResolvedDocumentReferenceMap`) rather than a raw
    dictionary keyed by the new `ReferenceLookupKey`. `ReferenceLookupResult` will also lose
    `VerificationIdentityKey` (canary-only) and `ReferentialIdentityResourceKeyId`; `ResourceKeyId`
    will remain the resolved concrete target key, including abstract matches.
  - `Add{Postgresql,Mssql}ReferenceResolver()` DI extensions will compose the natural-key resolver —
    a behavioral change for hosts that resolve references through the old registration.
- Abstract identity tables and their union views will add a concrete `ResourceKeyId smallint NOT
  NULL` payload column, with table columns FK-constrained as `(DocumentId, ResourceKeyId)` to
  `dms.Document(DocumentId, ResourceKeyId)`.
  Existing abstract-identity maintenance triggers will populate it from a typed compile-time
  concrete-member key literal carried by `AbstractIdentityMaintenance`, not from discriminator
  parsing or DDL-emitter inference. The abstract identity `Discriminator` column
  will remain for diagnostics/readability only; resolver compatibility will not parse it. Consumers that
  enumerate abstract identity scalar columns must continue to exclude both payload columns
  (`ResourceKeyId` and `Discriminator`) from identity-equality logic.
- Ops: the seed-clone script's TRUNCATE list will lose `dms."ReferentialIdentity"`; DMS template
  management (`eng/DatabaseTemplates/Template-Management.psm1`) will drop its DMS relational
  `pgcrypto` template-backup preamble only. It must not issue
  `DROP EXTENSION pgcrypto`; shared databases may still need the extension for CMS/OpenIddict key
  encryption.

### Retained contracts

`dms.Document` except for the narrow `UX_Document_DocumentId_ResourceKeyId` parent key used only by
descriptor and abstract identity document/resource invariants (columns including
`CreatedByOwnershipTokenId`, identity `DocumentId`, and the DocumentCache enqueue triggers remain
unchanged); `dms.Descriptor` except for the descriptor unique-index swap, SQL Server's non-persisted
`UriLowered` computed column, the composite `(DocumentId, ResourceKeyId)` FK back to `dms.Document`,
and the explicit SQL Server identity collation. The retained descriptor table contract includes
`ResourceKeyId NOT NULL`, `Discriminator` storage/read compatibility, and the mirrored
`ContentVersion` / `ContentLastModifiedAt` columns required by descriptor change-version page
selection on `IX_Descriptor_ResourceKeyId_ContentVersion_DocumentId`; the natural-key change must not
move that live descriptor path back to a `dms.Document` join. The logical shapes of
`UX_<R>_RefKey` / `UX_<R>_NK`; the abstract identity table family, its uniqueness constraints, and
its trigger topology except for concrete `ResourceKeyId` column population, the composite
document/resource FK, and explicit SQL Server identity collation; the DocumentCache table family;
tracked-change tables and triggers; `auth.*`;
`dms.ResourceKey` / `dms.EffectiveSchema` / `dms.SchemaComponent`; the read/reconstitution pipeline;
`RelationalMappingVersion` is `v3` because these physical mapping changes must reject earlier
prerelease aggregate database shapes.

## Release compatibility and rollback

Release review confirmed that `v2` has not been published as a supported database shape. This
design therefore moves to the unreleased, re-provision-only `v3` aggregate. Current schema-hash
expectations will be re-blessed as the physical changes land. Environments using an earlier
prerelease `v2` shape must re-provision; the mapping-version mismatch rejects that stale shape.

Rollback is a commit revert while `dms.ReferentialIdentity` remains fully maintained. Once
descriptor writes stop maintaining RI rows, rollback to the RI resolver requires re-provisioning
with the previous build or an explicitly designed backfill; the continued presence of an RI table
is not evidence that its rows are current.

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
  tracked-change old/new identity columns, and local collection identity columns, while
  purpose-specific binary columns retain their collation.
- **Abstract identity DDL pins** will prove every abstract identity table emits
  `UX_<Abstract>Identity_NK` over exactly the abstract identity fields in
  `abstractResources[A].identityJsonPaths` order, plus `UX_<Abstract>Identity_RefKey` over those same
  fields with trailing `DocumentId`, plus `FK_<Abstract>Identity_DocumentResourceKey` to
  `dms.Document(DocumentId, ResourceKeyId)`. The fixtures must prove `ResourceKeyId` and
  `Discriminator` are payload columns only, are absent from both abstract identity key definitions,
  and cannot drift from the owning document's `ResourceKeyId`.
- **SQL Server collation-contract integration tests** will provision against the supported
  `Latin1_General_100_CS_AS_SC_UTF8` database default, assert that SchemaTools preserves that default,
  query `sys.columns` to prove representative identity columns use
  `SQL_Latin1_General_CP1_CI_AS`, and run the case-variant natural-key lookup/uniqueness behavior pins
  with the same expectations as the standard default-collation fixture. That coverage must include a
  descriptor and a regular resource delete/recreate case where only identity casing changes, proving
  `/deletes` suppresses the tombstone without collation-conflict errors under the case-sensitive
  database default. Runtime unit tests will pin `OrdinalIgnoreCase` to this declared SQL Server
  schema contract and `Ordinal` to PostgreSQL's.
- **Well-formedness validation pins** will prove that descriptor writes, descriptor references, and
  descriptor-valued query filters containing NUL (`\u0000` in a body, `%00` in a query string)
  return a path-attributed 400 before any relational lookup; that a JSON `\uD800`-class escape in
  any body string property returns a malformed-body 400 at parse (never a 5xx) on every resource;
  that a percent-encoded lone surrogate in a descriptor query filter (`%ED%A0%80`) is treated as
  literal text and yields an empty page, not an error; and, as a companion, that descriptor-valued
  query parameters are UTF-8 percent-decoded before validation and folding.
- **PostgreSQL descriptor-lowering pins**: golden DDL and generated SQL fixtures will prove every
  descriptor lowered-URI index/probe expression uses `COLLATE "pg_c_utf8"` (never the database
  default), including under a non-default database collation; C# fixtures will prove no descriptor
  code path lowercases (the well-formedness helper asserts, never folds).
- **SQL Server descriptor-lowering pins**: generated SQL fixtures will inventory write/upsert,
  reference resolution, descriptor-valued identity, query-filter, and Change Query recreated-row
  probes and prove every descriptor input is collated *inside* `LOWER` with
  `SQL_Latin1_General_CP1_CI_AS`; no emitted `LOWER(<parameter>)` may rely on the database default. A
  focused live fixture will use a `Turkish_100_CS_AS` database default, where unqualified
  `LOWER(N'I')` produces dotless `ı`, and prove that those probe surfaces still resolve an existing
  `I`-bearing descriptor through its pinned computed-column index rather than missing and attempting
  a duplicate insert. The fixture will also exercise Change Query recreated-row suppression under
  that default. Both this live fixture and the engine-divergence matrix below are owned by
  DMS-1455 (T13), which lands after every descriptor probe surface (index, write/upsert, resolver,
  query filter, Change Queries) exists; they are not part of the PostgreSQL-floor story.
- **Engine-divergence golden fixtures**: live-database fixtures will document the accepted per-engine
  non-ASCII verdicts — at minimum `ß`/`ss`, width-variant values, dotted `İ`/`i`, precomposed `é` vs
  `e` + combining acute, missing version-80 casing data (`Ǹ`/`ǹ`), unweighted supplementary
  characters (`A`/`A😀` and `A😀`/`A😁`), and the comparer-boundary candidates `Ǹ`/`ǹ`, `ſ`/`s`,
  dotless `ı`/`i`, and Kelvin `K` (U+212A)/`k`, each recorded with both the collation verdict and
  the `OrdinalIgnoreCase` verdict so both residue directions are pinned explicitly and a
  comparer-looser pair, if one ever appears, is visible. Verdicts will be captured empirically per
  engine (on SQL Server, `LOWER`'s casing table and the CI collation's comparison weights are
  distinct tables and can disagree) so a folding change after a PostgreSQL major upgrade or a
  collation surprise surfaces as a fixture diff rather than a production discovery. Companion
  fixtures will prove uniqueness, reference/upsert resolution, stored-wins rebinding, and Change Query
  recreated-row detection all follow the same per-engine verdicts.
- **Probe-compilation unit tests**, including an every-resource parity guard that will prove the
  compiled probes reproduce the legacy trigger derivation for as long as both exist, and abstract
  target pins proving the probe projects concrete `ResourceKeyId` without a discriminator-to-key map.
- **Abstract identity parity/corruption pins** proving trigger-maintained `<Abstract>Identity` rows
  and diagnostic union views stay in parity with concrete root rows across insert, committed root
  delete via `dms.Document` cascade, identity rename, SQL Server identity collation behavior,
  PostgreSQL byte-sensitive behavior, and concrete `ResourceKeyId` population from compile-time
  member metadata.
- **Dialect SQL unit tests**: statement shape independent of batch size (PostgreSQL), OPENJSON +
  FORCE ORDER + leftmost-input pins, explicit DMS identity collation on every textual OPENJSON key
  operand, and the parameter-budget guard (SQL Server), plus the union-projection single-statement
  form.
- **No old-vs-new gates.** The prototype's differential equivalence proof and benchmark matrix stand
  as the transition evidence; neither suite will be ported to the implementation branch. Correctness
  will be carried by the behavior pins below, the existing integration estate (running against the new
  resolver from T9 onward), and E2E; performance remedies are pre-agreed (the contingency ladder,
  then the accepted DMS-1332-revert last resort).
- **Command-stream pins**: round-trip counts must not regress; RI command classification will go to
  zero at cutover.
- **Behavior pins**:
  - Case-variant natural-key POST/PUT suites asserting the ODS-parity contract on SQL Server (200,
    stored casing served back, true no-op on identical payload, no referrer rewrite / key-change
    row / `ContentVersion` bump; casing-only PUT is not a key change; a mixed PUT cascades only the
    genuinely changed column) and the PostgreSQL second-document behavior.
  - Descriptor stored-wins pins per engine (POST preserves stored casing and no-ops on casing-only
    re-POST; case-only descriptor PUT returns 200 with stored identity intact).
  - Collection semantic-key stored-wins pins: on SQL Server a PUT whose collection item differs from
    the stored row only in the casing of a local string semantic-key member keeps the same
    `CollectionItemId`, serves the stored casing, is a guarded no-op when otherwise identical, and
    preserves hidden columns under a profile-scoped write; on PostgreSQL the same PUT replaces the
    row (delete + insert) as today.
  - Query-filter collation pins: generated SQL Server GET-many SQL contains no query-side
    `COLLATE` on string equality predicates; a case-variant `?field=` on an identity string column
    matches on SQL Server and does not match on PostgreSQL; a plan assertion proves the identity
    column filter seeks its index on SQL Server. The DMS-993 pins asserting case-sensitive SQL
    Server filtering (`It_enforces_case_sensitive_string_filtering_for_sql_server` and the
    descriptor read-filter equivalent) will be inverted, and the ignored E2E mixed-case-value query
    scenarios will be enabled with per-engine expectations.
  - Collection duplicate-detection pins (SQL Server: case-variant duplicate reference items → 400
    duplicate-item, never an unmapped failure; case-variant duplicate string-scalar identity items →
    400; PostgreSQL: the same payloads → 409 unresolved reference and success-with-both-items
    respectively, since case variants are distinct values there; case-variant duplicate
    **descriptor** items → 400 duplicate-item on **both** engines — descriptor matching is
    case-insensitive everywhere, so both items resolve to the same `DescriptorId`; this is
    existing flattener behavior and the pin is a regression guard, not a fix; the SQL Server
    case-variant duplicate **local string-scalar** pin (e.g. two `electronicMails` differing only in
    the casing of `electronicMailAddress`) is the regression test for the pre-existing unmapped-5xx
    defect noted under ["Collection duplicate detection"](#collection-duplicate-detection);
    linguistic-equality
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
    and never `Discriminator`; PostgreSQL probes lower both the live URI and any tombstoned
    namespace/codeValue expression under `COLLATE "pg_c_utf8"`.
- **E2E** will gate the merge in CI. The PostgreSQL lane runs the full DS 5.2 suite; the SQL Server
  lane runs only the bounded `@MssqlRepresentative` cross-section (23 scenarios across 18 features
  today, by design). Therefore every E2E scenario this design adds that must gate SQL Server carries
  `@MssqlRepresentative`, and engine-divergent outcomes are pinned at the integration level or as
  engine-tagged scenario pairs (`@PostgresqlOnly` / `@MssqlOnly` categories that the respective lanes
  include or exclude — a mechanism DMS-1443 introduces; today features carry no engine tag and steps
  have no engine conditional). The representative set grows by roughly half a dozen scenarios, an
  accepted lane-time cost. HTTP-visible contract points will be pinned at the E2E level where
  possible; in particular, the case-variant duplicate descriptor-item scenario will be a dedicated
  E2E test (`EdFi.DataManagementService.Tests.E2E`): POST a resource whose collection carries two
  descriptor URIs differing only in casing → 400 duplicate-item, never a 5xx, on both engines
  (tagged `@MssqlRepresentative`). The ODS-derived `DescriptorCaseInsensitiveValidation.feature`
  scenarios, cited above as the only official casing artifact, run only on PostgreSQL today and are
  tagged `@MssqlRepresentative` by DMS-1454 so descriptor stored-wins is exercised on the engine
  where casing matters.

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
   and asserted deliberately. SchemaTools will preserve the database default, but generated identity
   columns, including tracked-change old/new identity copies, will explicitly use
   `SQL_Latin1_General_CP1_CI_AS`; this will remove deployment-dependent identity behavior and move
   DMS toward ODS behavior.
5. **Descriptor case-variant duplicates** will be rejected by a table-level CI unique index over
   the engine-lowered URI + `ResourceKeyId` (they are same-document by hash semantics today) — accepted;
   identical effective semantics for ASCII input, newly enforced by the engine.
6. **Lost `dms.ReferentialIdentity` corruption canary** — accepted by construction for RI: the hash
   rows it guarded will no longer exist. This does not remove every derived-state risk; abstract
   identity tables will remain trigger-maintained and will be covered by the explicit
   parity/corruption pins above.
7. **Casing comparer approximation** — the runtime provider will derive `OrdinalIgnoreCase` from the
   fixed SQL Server identity contract, but it will still not emulate every
   `SQL_Latin1_General_CP1_CI_AS` equality. Where the comparer is stricter the guard will fail closed;
   where it is looser (no instance currently known) a collation-distinct value would be treated as
   unchanged and the stored value kept (non-destructive but silent). Both directions are documented
   above, pinned by the engine-divergence fixtures, and identical to ODS; neither silently
   redefines database identity.
8. **Engine-owned Unicode descriptor folding** — well-formed non-ASCII descriptor URI values will be
   accepted, with case folding owned by each engine (`pg_c_utf8` on PostgreSQL, the CI identity collation on
   SQL Server) rather than by an application-side folding function (see the decision record
   below). Accepted residuals: the engines' non-ASCII verdicts differ (for example `ß` = `ss` and
   width-insensitive matches on SQL Server only; code points unknown to the version-80 SQL Server
   collation carry no collation weight and are ignored in comparison). The latter is intentionally
   lossy identity behavior: distinct raw URIs such as `A`, `A😀`, and `A😁` can be one SQL Server
   descriptor identity. DMS performs no Unicode normalization, but SQL Server's linguistic
   comparison may likewise treat canonically equivalent spellings as one identity; PostgreSQL's
   code-point comparison keeps them distinct. Exact per-character verdicts are pinned by the
   engine-divergence fixtures rather than generalized beyond the tested repertoire. PostgreSQL 17
   will become the minimum supported version; and a PostgreSQL major upgrade can change folding,
   requiring the documented `REINDEX` playbook (a newly-created collision blocks the `REINDEX`
   until the data is resolved).

## Decision record: non-ASCII descriptor URIs

The business requires descriptor `namespace` and `codeValue` values to accept non-ASCII
characters, which lifted this design's original ASCII-only-without-NUL descriptor URI contract.
Two designs were evaluated (August 2026):

- **Engine-side folding (chosen)** — case folding owned by the database engines: PostgreSQL's
  builtin `pg_c_utf8` collation and SQL Server's CI identity collation; C# never lowercases; no
  schema additions. The team accepted the PostgreSQL 17 minimum-version floor this requires, the
  per-engine non-ASCII verdicts (including SQL Server's lossy version-80 identity aliases), and the
  PostgreSQL major-upgrade `REINDEX` playbook — all recorded in accepted trade-off #8.
- **Application-side folding (rejected)** — one C# folding function (`ToLowerInvariant()`) with a
  persisted `UriLowered` column, byte-collated indexes (`COLLATE "C"` /
  `Latin1_General_100_BIN2`), and a folded copy on descriptor tombstones. It offered byte-exact
  cross-engine uniformity, exact in-process comparer agreement, and no PostgreSQL floor, at the
  cost of an application-derived column family with parity pins and recompute migrations whenever
  the .NET runtime's Unicode data changes. A PostgreSQL nondeterministic ICU collation variant was
  also considered and rejected: it would reimport ambient ICU version drift, the exact hazard the
  deterministic collation contract exists to eliminate.

The chosen design is normative throughout this document ("Descriptors", "Query-time descriptor
filters", "Casing and identity semantics", tickets T5–T6, the test strategy, and accepted trade-off
#8). The dependent documents ([flattening-reconstitution.md](flattening-reconstitution.md),
[key-unification.md](key-unification.md), and
[transactions-and-concurrency.md](transactions-and-concurrency.md)) consume the descriptor
validation boundary and lowered-value contracts and are aligned with this decision.

## Out of scope

- Any change to `dms.Document` other than the narrow `UX_Document_DocumentId_ResourceKeyId`
  parent key required by the descriptor/abstract document-resource FKs; its columns, locking,
  DELETE shape, readers, and `DocumentCache` triggers remain unchanged.
- Changing or constraining the SQL Server database default collation. The column-level identity
  contract is specifically what allows the supported case-sensitive database default to remain.
- In-place upgrade scripts (the migration is re-provision-only; prerelease databases provisioned
  from an earlier shape must be re-provisioned).
- DocumentCache/CDC work (live, RI-free, orthogonal).
- ApiSchema contract or resource JSON shapes, except for the new descriptor URI NUL rejection and
  the body-wide parse-time rejection of unpaired-surrogate JSON escapes described above.
- Mapping packs / AOT mode ([`mpack-format-v1.md`](mpack-format-v1.md),
  [`aot-compilation.md`](aot-compilation.md), epic 05). This design does not update them; the
  alignment attempted in `50c37f77` / `d2d3997d` was reverted deliberately, and mapping packs are
  unimplemented (`IMappingPackStore` is a placeholder; `PackFormatVersion=1` is an unreleased draft,
  so no format bump is implied). The boundary is stated so E05 cannot start from stale docs: when
  pack work resumes, the pack payload must carry — or the loader must recompile from the derived
  model — `NaturalKeyProbeTargets`, `OwnNaturalKeyProbesByResource`, and `DescriptorProbeTarget`
  with their `NaturalKeyProbeKeyBinding` metadata, `DbColumnModel.UsesSqlServerIdentityCollation`,
  and the descriptor `pg_c_utf8` expression-index shape, and the compile-time probe validation
  (target probe requires RefKey inventory; empty-identity guard) must run on pack load.
- Rewriting the design-doc corpus beyond the targeted supersession banners (including the E21 note
  added to [`08-write-roundtrip-batching.md`](../epics/07-relational-write-path/08-write-roundtrip-batching.md)).
