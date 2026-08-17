# Natural-Key Reference Resolution: Removing UUIDv5 `ReferentialId`

## Summary

`dms.ReferentialIdentity` maps a deterministic UUIDv5 hash of each document's identity
(`ReferentialId`) to its `DocumentId`. It exists so that reference resolution, POST upsert detection,
and descriptor filter resolution can each be answered by one uniform, narrow index lookup.

This design will remove the table, stop computing UUIDv5 `ReferentialId` values anywhere in DMS,
and answer each of those questions with a batched natural-key lookup against the schema's existing
natural-key indexes. The only new lookup index is the descriptor lower-URI index; the design also
adds a narrow `UX_Document_DocumentId_ResourceKeyId` parent key for descriptor/abstract
document-resource invariants. Abstract resolution adds a `smallint ResourceKeyId` payload column and
composite FK to existing abstract identity rows but does not add another abstract lookup index:

| Lookup | Will be replaced by |
|---|---|
| Concrete document references | Scalar probe of the target's `UX_<Target>_RefKey` |
| Abstract (polymorphic) references | Probe of `UX_<Abstract>Identity_RefKey`, projecting the concrete `ResourceKeyId` |
| Descriptor references and filters | Probe of `UX_Descriptor_UriLowered_ResourceKeyId` (new lower-expression/computed-column index) |
| POST upsert detection | The document's own `UX_<R>_NK` |

For abstract targets, the replacement depends on the data-model contract explicitly pinning both
`UX_<Abstract>Identity_NK` and `UX_<Abstract>Identity_RefKey`. The former takes over the
cross-subclass identity uniqueness that the legacy alias/hash path made redundant; the latter is the
stable FK/probe target with `DocumentId` last. `ResourceKeyId` and `Discriminator` are projected
payload only and must be excluded from both abstract identity key shapes. Because the projected
`ResourceKeyId` becomes the authoritative compatibility key for abstract references, every abstract
identity table must pair it with `DocumentId` in a composite FK to
`dms.Document(DocumentId, ResourceKeyId)` so it cannot drift from the owning document's concrete
resource type. `dms.Document.ResourceKeyId` remains the FK-constrained path to the seeded
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
identity columns, composite indexes, abstract identity tables. Since then, the relational model has
standardized on that machinery for FK enforcement, identity lookup, Change Queries, and AOT metadata:

- **`UX_<R>_RefKey`** — the fully-flattened scalar identity plus `DocumentId` — exists on every
  concrete resource stored in relational tables, whether or not the resource is currently referenced
  by another resource. It is the composite-FK target that keeps reference binding columns consistent
  under cascades ([key-unification.md](key-unification.md)) and the uniform identity-first probe
  shape used by natural-key resolution, `/deletes` recreated-row probes, and AOT mapping-pack
  metadata. Conditional emission for never-referenced resources would fork query planning and pack
  validation around a storage fact unrelated to the resource's identity. Because identity flattening
  is recursive, a reference-bearing identity (e.g., a Section, whose identity contains a
  CourseOffering reference, whose identity contains Course and Session references) collapses to
  **one flat list of scalars** — resolvable in a single index seek, no multi-pass dependency
  layering.
- **`UX_<R>_NK`** — the natural-key unique constraint with reference-sourced parts as
  `..._DocumentId` columns — exists as the identity-uniqueness enforcement and the source of
  create-race unique violations (the 409/retry path).
- **`<Abstract>Identity` tables** with their own `RefKey` indexes exist to enforce cross-subclass
  identity uniqueness and to serve as polymorphic FK targets. This design will add the concrete
  member `ResourceKeyId` plus a composite `(DocumentId, ResourceKeyId)` FK to `dms.Document` so
  abstract reference resolution can return the same compatibility token the runtime uses today without
  allowing the identity row to disagree with its owning document. Resolution is still one probe of one
  table — not a union over subtype tables.

The descriptor-specific probe target missing was a case-insensitive descriptor lookup, which this
design will add as a lower-storage unique index on the existing `dms.Descriptor` table: a PostgreSQL
expression index, and a SQL Server non-persisted computed-column index. Descriptor URI identity will
be ASCII-only, excluding NUL, so the lowercasing and storage contract is deterministic across C#,
PostgreSQL, and SQL Server.

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
| "Polymorphic targets get significantly harder; abstract identity tables reintroduce central indexes with drift risk" | Abstract identity tables already exist and are already trigger-maintained — for cross-subclass uniqueness enforcement. Resolution will reuse them and add only a concrete-member `ResourceKeyId` column plus a composite `(DocumentId, ResourceKeyId)` FK back to `dms.Document`; no new lookup tables, natural-key/probe indexes, trigger families, or extra row writes are introduced for polymorphism. |
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
| POST upsert detection | The composite write path's capture predicate (a `ReferentialId` subselect) plus a standalone fallback lookup | Natural-key capture predicate + `UX_<R>_NK` fallback probe |
| Descriptor upsert detection | `ReferentialId` probe in the descriptor write handler | Lowered-URI + `ResourceKeyId` probe |
| Descriptor-valued query filters | Query preprocessor lowercases + hashes the URI | Backend relational query preprocessing identifies descriptor-id targets from compiled query metadata, rejects non-ASCII or NUL URI values as 400 validation failures, then lowercases the validated value and probes the descriptor lower-URI index |
| 409 duplicate-identity messages | Rebuilds NK column lists from `ReferentialIdentityMaintenance` trigger metadata | Re-sourced from compiled natural-key probe metadata (severed *before* the triggers drop) |

Verified non-consumers (these will be untouched by this design): row locking (`dms.Document` by `DocumentId`),
DELETE (captures by `DocumentUuid`; the only interaction was the ON DELETE CASCADE), GET-by-id,
`?id=` queries, link injection, ownership authorization, stamping and tracked-change triggers, Change
Query routing/response contracts/authorization/`/keyChanges`, and the entire DocumentCache path.
Change Query `/deletes` recreated-row detection is a consumer of the natural-key and descriptor
identity contracts below, so it is updated by this design.

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

This comparer requirement is an enforceable contract, not a comment on a caller-owned dictionary.
The resolver result must expose a dedicated document-reference map/factory contract instead of a raw
`IReadOnlyDictionary<ReferenceLookupKey, long>`. A plain dictionary can silently use
`ReferenceLookupKey`'s default record-struct equality, which inherits array reference equality from
`DocumentIdentity` and can miss semantically identical identities backed by different arrays. The
only construction path for the resolved document-reference map must install the structural comparer,
and consumers must look up by `(target resource, DocumentIdentity)` through that map rather than by
direct dictionary indexing.

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
document. `Discriminator` remains stored for diagnostics/read compatibility, but descriptor
resolution will not depend on it.

**Abstract targets** — a probe of `UX_<Abstract>Identity_RefKey` projecting
`(Ordinal, DocumentId, ResourceKeyId)`. `ResourceKeyId` is the concrete member resource key stored on
the abstract identity row and populated by the abstract-identity trigger from the same compile-time
member metadata that supplies the diagnostic `Discriminator`. The resolver will not parse or map the
abstract `Discriminator`; `IncompatibleTargetType` will continue to compare the resolved concrete
`ResourceKeyId` with the target's allowed concrete resource keys. The abstract `RefKey` and `NK`
index key shapes will remain unchanged; `ResourceKeyId` is payload only, not part of abstract
identity equality. The abstract identity table must FK-constrain `(DocumentId, ResourceKeyId)` to
`dms.Document(DocumentId, ResourceKeyId)` because the resolver treats the projected value as
authoritative for compatibility, not diagnostic metadata, and the projected key must match the owning
document's concrete resource type. The compiled abstract-identity trigger contract must carry a typed
concrete-member `ResourceKeyId` literal alongside the diagnostic discriminator literal; dialect
emitters must not recover the key by parsing `Discriminator` or re-deriving it from the source table.
One table, one seek, no per-subtype SQL.
The abstract identity table is the required write-time resolution surface. Any
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
stored `..._DocumentId` key value. That document-reference binding is valid only for
`OwnNaturalKeyProbe.KeyColumns`; normal target probes still consume a fully flattened
`DocumentIdentity` and therefore need only scalar/descriptor binding metadata. Pack producers will
serialize that storage-resolved, typed metadata into `.mpack` payloads so AOT consumers can
reconstruct the same `MappingSet` without running the probe compiler or re-deriving abstract identity
key types from `ApiSchema.json`. Because shared-descriptor resources intentionally omit per-resource
natural-key probes while relational-table resources require them, `.mpack` payloads must also
serialize each concrete resource's `ResourceStorageKind`. Pack consumers validate probe presence
against that field and must not infer storage kind from `ApiSchema.json`, descriptor naming
conventions, or probe absence; otherwise a malformed relational resource pack could be accepted as
though it were a descriptor resource. The metadata will not be serialized into DDL manifests, so it
will cause zero golden-manifest churn. Non-normative AOT schema sketches must preserve those
conditional presence rules rather than implying every `ResourcePack` carries every plan and probe
record; abstract packs do not carry relational plans, and shared-descriptor packs intentionally use
only the payload-level descriptor probe.

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
  each reference-sourced part is represented in `OwnNaturalKeyProbe.KeyColumns` as a
  `DocumentReference` binding to the owning resource's `DocumentReferenceBinding`. That binding
  supplies the reference-object path, target resource, target identity order, and local
  `..._DocumentId` key column being compared. AOT consumers must receive this binding from the
  `.mpack` payload; they must not recover the reference site by parsing the key column name or by
  re-reading `ApiSchema.json`.
- `UX_<R>_NK`, `OwnNaturalKeyProbe`, POST capture, create-race classification, and duplicate-identity
  diagnostics must all share the same root natural-key column contract. Scalar identity parts bind to
  scalar path/binding columns, descriptor identity parts bind to resolved `..._DescriptorId` columns,
  and document-reference-sourced identity parts bind to the resolved reference `..._DocumentId`
  column only. The propagated reference identity-part binding columns still exist for FK/cascade
  consistency, query binding, and reconstitution, but they are not extra `UX_<R>_NK` members. Adding
  them would split the DDL uniqueness contract from the resolver/upsert contract, which always reasons
  about reference identity through the resolved target `DocumentId`.
- A document-reference capture binding resolves the referenced `DocumentId` by target kind:
  - **Concrete target:** emit a scalar subselect over the concrete target root table's flattened
    `RefKey` columns and return its `DocumentId`.
  - **Abstract target:** emit the same scalar subselect shape against the compiled
    `{AbstractResource}Identity` table, ordered by the abstract identity fields, and return its
    `DocumentId`. There is no abstract root table to probe. `ResourceKeyId` remains payload for the
    later compatibility check; the capture predicate only needs the referenced `DocumentId`.
  - **Descriptor-valued identity part inside the referenced target identity:** use the existing
    `dms.Descriptor` lowered-URI + descriptor `ResourceKeyId` subselect to produce the descriptor
    `DocumentId` key value before comparing the target key column.
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
   payload scalars — a flat single seek, no subselects, still zero schema change and still available
   for every concrete relational resource. Cost: a second capture shape and a wider identity-copy
   predicate, so keep it behind measurement.
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
duplicating the lowered URI in the base table. Descriptor URI identity will be **ASCII-only,
excluding NUL**: descriptor writes, descriptor references, and descriptor-valued query filters will
reject non-ASCII or NUL URI values before normalization. Within that supported input space, C#
`ToLowerInvariant()`, PostgreSQL `lower(... COLLATE "C")`, and SQL Server `LOWER(...)` produce the
same lowered value.

The PostgreSQL collation is part of the descriptor identity contract. ASCII validation removes
Unicode case-folding ambiguity, but it does not by itself make PostgreSQL `lower(...)` independent of
the database's locale/collation. Every PostgreSQL descriptor identity index, lookup predicate, and
Change Query recreated-row probe must therefore lower values under the deterministic `"C"` collation,
for example `lower("Uri" COLLATE "C")`. The implementation must not emit an unqualified
`lower("Uri")` or `lower(<namespace-codeValue expression>)` and rely on the database default.

**This is an implementation change, not only a storage or documentation constraint.** The current
write extraction and query preprocessing implementations lowercase arbitrary Unicode descriptor
values directly. They must change as follows:

- For a descriptor resource POST/PUT, Core's descriptor identity extraction derives the URI from the
  canonicalized `$.namespace` + `#` + `$.codeValue`, validates the two client-supplied components as
  ASCII without NUL, and only then lowercases the derived URI. A failure is attributed to each
  offending source path (`$.namespace` and/or `$.codeValue`) and stops the write before descriptor
  target lookup or a descriptor write command.
- For a descriptor reference in a resource body, Core's descriptor extraction validates the raw URI
  at its concrete request JSON path before constructing the normalized descriptor identity. A
  failure stops the write before the reference resolver is invoked.
- For a query field compiled to a descriptor-id target, the relational backend performs the
  descriptor ASCII validation during query preprocessing. Core query validation remains responsible
  for generic query-field recognition, scalar type validation, and query-element construction, but it
  does not own `RelationalQueryFieldTarget.DescriptorIdColumn`; that target is backend compiled
  metadata. `RelationalQueryRequestPreprocessor` therefore uses the selected
  `RelationalQueryCapability` to identify descriptor-id targets, validates the query value before it
  creates a descriptor reference or calls the resolver, and surfaces a failure with the existing
  path-attributed 400 query-validation response shape. The failure must not be represented as
  `RelationalQueryPreprocessingOutcome.EmptyPage`, because malformed input is not a lookup miss.
  Only after validation may the preprocessor lowercase the value with the shared
  validated-ASCII-without-NUL helper.

Here, "before normalization" means before descriptor-specific case normalization. Ordinary request
parsing, schema validation, coercion, profile shaping, and the existing trimming rules may already
have run. ASCII means every character in the resulting client-supplied value is in U+0001 through
U+007F; U+0000 NUL is invalid even though it is inside the formal ASCII range because PostgreSQL
`text`/`varchar` cannot store NUL. Downstream write flattening, key unification, descriptor upsert
detection, and query lookup must consume only values that have passed this validation; their
lowercase helpers must preserve or assert that invariant rather than calling `ToLowerInvariant()` on
unchecked input. Because NUL is formally ASCII, dependent write/query contracts and helper wording
must not shorten this boundary to "reject non-ASCII" or "validated ASCII" in a way that can be read
as allowing U+0000; they must state "non-ASCII or NUL" and "validated ASCII without NUL" wherever the
validation boundary or lowered descriptor key is described. The corresponding write and query
algorithms are updated in
[flattening-reconstitution.md](flattening-reconstitution.md), [key-unification.md](key-unification.md),
and [transactions-and-concurrency.md](transactions-and-concurrency.md).

| Object | Definition |
|---|---|
| PostgreSQL `UX_Descriptor_UriLowered_ResourceKeyId` | Unique expression index: `CREATE UNIQUE INDEX "UX_Descriptor_UriLowered_ResourceKeyId" ON dms."Descriptor" (lower("Uri" COLLATE "C"), "ResourceKeyId");` |
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
WHERE lower(descriptor."Uri" COLLATE "C") = @uriLowered
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

The resolver-facing query preprocessor still consumes `IReferenceResolver`, and its lowercase value
will feed the descriptor lower-URI probe instead of a hash. The validation boundary moves into that
preprocessor because descriptor-id query targets are backend compiled relational metadata, not Core
validation metadata. Core continues to parse and validate generic query fields/types, then passes
query elements downstream. `RelationalQueryRequestPreprocessor` inspects the selected
`RelationalQueryCapability`, identifies fields whose compiled target is
`RelationalQueryFieldTarget.DescriptorIdColumn`, rejects non-ASCII or NUL values with the existing
path-attributed 400 response, and only then creates a descriptor reference or invokes
`IReferenceResolver`. This requires a validation-failure preprocessing path (or equivalent typed
exception translated by the repository/frontend) rather than reusing
`RelationalQueryPreprocessingOutcome.EmptyPage`: a valid descriptor URI that does not resolve still
returns an empty page, but malformed URI input is a client validation error. The preprocessor
replaces its unchecked `ToLowerInvariant()` call with the shared validated-ASCII-without-NUL
lowercase helper.
GET-by-id, `?id=`, link injection, ownership authorization, and descriptor paging will not get
result-contract changes. Change Query route/response/authorization contracts remain
unchanged, but `/deletes` recreated-row detection follows the lowered-URI + `ResourceKeyId`
descriptor identity contract described above.

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
string identity columns in abstract-identity tables, tracked-change old/new string copies of identity
values, and local string identity members used by child or extension collection uniqueness. Both
sides of any string-bearing identity FK therefore have the same explicit collation. Descriptor
identity keeps its lowered-ASCII lookup contract described above; its SQL Server source and computed
identity columns will also be emitted under the DMS default CI collation. Columns with a
purpose-specific stronger contract, such as the existing `Latin1_General_100_BIN2` lifecycle token,
retain that explicit collation.

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
| Descriptor matching + uniqueness | Case-insensitive via lowered ASCII-without-NUL URI + `ResourceKeyId`. | Same — uniform across engines, a first (ODS *intended* CI descriptors but its PostgreSQL implementation stored CS and could accumulate case-variant duplicates). |
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

Because [`data-model.md`](data-model.md#2-dmsdescriptor-unified) also owns descriptor update
semantics, it must express descriptor immutability in these equality-contract terms. The invariant is
that PUT cannot persist a move to a different descriptor identity; it is not a byte-for-byte request
matching rule. Without that distinction, the data-model rule reads as rejecting the case-only
descriptor PUT that this design requires to return 200/no-op with stored casing intact.

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

The surviving trigger inventory deliberately excludes SQL Server identity-value fan-out. That old
`MssqlIdentityPropagationTrigger` fallback is retired by
[`sql-server-pruning.md`](sql-server-pruning.md): retained native cascades propagate eligible identity
updates, and pruned full-composite `NO ACTION` edges have no trigger fallback. Reintroducing an
identity-propagation trigger row here would contradict that pruning contract.

| Surviving trigger family | What the binary diff gates | Why it must stay byte-level |
|---|---|---|
| Document stamping — content stamp (resource roots, child scopes, and `dms.Descriptor`) | `ContentVersion` / `ContentLastModifiedAt` bumps | Non-identity fields stay request-wins under this contract: a case-only or trailing-space-only edit changes the served bytes, so the ETag must change and change queries must resurface the document. A collation diff would leave the ETag stale while the body changed. |
| Document stamping — identity stamp + key-change workset | `IdentityVersion` bump + the key-change tracked-change row | The fail-closed comparer residue: a byte-different-but-collation-equal key change (e.g. `Straße` → `Strasse`) is deliberately allowed through as a real key change, and its cascade rewrites referrer bytes; only a byte-level diff records any of it. |
| Abstract identity maintenance | Whether concrete identity changes propagate into the `<Abstract>Identity` tables | These tables will become the *only* resolution path for abstract references, and PostgreSQL matches them case-sensitively — byte drift between a concrete root and its abstract copy would become user-visible. |

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
- Non-ASCII or NUL descriptor URI writes, descriptor references, and descriptor-valued query filters
  will become 400 validation failures. Descriptor URI identity is intentionally ASCII-only without
  NUL so case-insensitive descriptor matching is deterministic across engines without storing a
  second normalized URI value in the base table.

PostgreSQL regular-resource behavior will remain unchanged on every pin.

### Collection duplicate detection

Collation-governed matching will open one gap that Core's request validation cannot close. Core's
request-local duplicate detection will remain engine-agnostic and ordinal: reference items compare
with the structural natural-key comparer, and scalar identity members compare through ordinal
dictionaries. Two collection items differing only in string casing can therefore pass Core
validation — and on SQL Server they will then resolve to the *same* target `DocumentId` under the
explicit DMS identity collation and collide in the collection's sibling unique constraint, which the
constraint resolver does not classify: an unmapped 5xx for what is really a client input error.

That gap is architectural, not just a constraint-mapping defect. Any write-path contract that treats
Core's request-local duplicate check as the final duplicate boundary is incomplete after this design.
Core will still own the pre-resolution ordinal/profile-shaped check, but backend must own the second,
storage-resolved check because only backend has the resolved `DocumentId`/`DescriptorId` values and
the selected schema equality contract for local string identity columns.

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
| Abstract reference compatibility | The concrete `ResourceKeyId` stored on the matched abstract identity row, FK-constrained with `DocumentId` to the owning `dms.Document` row, then compared with the target's allowed concrete resource-key set |
| Descriptor identity uniqueness | `UX_Descriptor_UriLowered_ResourceKeyId` (CI over ASCII-without-NUL URI, both engines) |
| Create-race detection (409/retry) | `UX_<R>_NK` unique violations, classified exactly as today |
| Reference targets exist and stay consistent | Composite FKs onto `RefKey` targets, unchanged |
| Reference-resolution cardinality | `UX_<R>_NK` plus FK/cascade parity between natural-key columns and flattened `RefKey` copies; `UX_<R>_RefKey` is the access/FK shape, not scalar identity uniqueness while `DocumentId` is unbound |

The abstract rows are the critical transfer point for the removed central index. `UX_<Abstract>Identity_NK`
must enforce cross-subclass equality over only the abstract identity fields, while
`UX_<Abstract>Identity_RefKey` must expose those same fields plus trailing `DocumentId` for abstract
FKs and probes. Golden DDL must pin both constraints' column order and prove that `ResourceKeyId`
and `Discriminator` remain payload only. Golden DDL must also pin the composite
`(DocumentId, ResourceKeyId)` FK to `dms.Document(DocumentId, ResourceKeyId)`, because the
compatibility key is now read from the abstract identity row rather than from the removed central
index. Without that FK, `dms.ReferentialIdentity` would be removed before its abstract-resource
invariants had an explicit relational owner tied to the document's real concrete resource type.

Deliberately lost will be: the `dms.ReferentialIdentity` corruption canary (the RI hash row drifting
from the root row it summarizes), and a redundant second uniqueness net (the RI PK). Mitigations for
the redundancy loss: the probe compiler will carry an empty-identity guard (a resource whose
compiled identity has zero parts will fail compilation loudly), a compile-time parity guard will
prove the compiled probes reproduce the legacy trigger derivation resource-by-resource for as long
as both exist, generated natural-key probes will fail on duplicate candidates instead of silently
choosing one, and golden DDL fixtures will continue to pin the schema.

This is not a claim that every derived-state risk disappears. `<Abstract>Identity` tables remain
trigger-maintained derived state, and after the cutover they serve the only resolution path for
abstract references. Their integrity coverage is therefore mandatory: trigger and integration tests
must prove table rows and diagnostic union views stay in parity with concrete root rows across
insert, delete, identity rename, and concrete `ResourceKeyId` population.

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
- `UX_Descriptor_Uri_Discriminator` (replaced by the `ResourceKeyId`-authoritative CI unique index).

### To be added

- A SQL Server identity-equality contract pairing the emitted
  `SQL_Latin1_General_CP1_CI_AS` column collation with the runtime `OrdinalIgnoreCase` comparer;
  PostgreSQL's corresponding contract pairs its existing case-sensitive storage with `Ordinal`.
- Descriptor URI ASCII validation.
- `UX_Document_DocumentId_ResourceKeyId`, used only as the parent key for descriptor and abstract
  identity document/resource invariants.
- PostgreSQL `UX_Descriptor_UriLowered_ResourceKeyId` expression index with the lowered URI pinned to
  `COLLATE "C"`, plus SQL Server
  non-persisted `dms.Descriptor.UriLowered` computed column and
  `UX_Descriptor_UriLowered_ResourceKeyId` index (definitions above).
- Compiled natural-key probe metadata and concrete `ResourceStorageKind` on the mapping set,
  serialized into mapping packs for AOT reconstruction but omitted from DDL manifests (zero manifest
  churn).
- `NaturalKeyReferenceResolver` + per-engine natural-key lookup command builders.
- A resolved document-reference map/factory contract that owns the structural
  `(target resource, DocumentIdentity)` comparer. The map may use a dictionary internally, but no
  public write-pipeline contract may require callers to provide or index an
  `IReadOnlyDictionary<ReferenceLookupKey, long>` with default equality.

### To be changed

- The SQL Server DDL generator will emit `COLLATE SQL_Latin1_General_CP1_CI_AS` on every string
  column that stores or copies an identity value, including tracked-change old/new identity copies.
  The database default collation is neither changed nor treated as the identity contract.
  Purpose-specific explicit collations remain authoritative.
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
    ordinals / natural-key identities, not referential ids. `ResolvedReferenceSet` exposes the
    dedicated resolved document-reference map rather than a raw dictionary keyed by
    `ReferenceLookupKey`. `ReferenceLookupResult` will also lose `VerificationIdentityKey`
    (canary-only) and `ReferentialIdentityResourceKeyId`; `ResourceKeyId` remains the resolved
    concrete target key, including abstract matches.
  - `Add{Postgresql,Mssql}ReferenceResolver()` DI extensions will compose the natural-key resolver —
    a behavioral change for hosts that resolve references through the old registration.
- Abstract identity tables and their union views will add a concrete `ResourceKeyId smallint NOT
  NULL` payload column, with table columns FK-constrained as `(DocumentId, ResourceKeyId)` to
  `dms.Document(DocumentId, ResourceKeyId)`.
  Existing abstract-identity maintenance triggers will populate it from a typed compile-time
  concrete-member key literal carried by `AbstractIdentityMaintenance`, not from discriminator
  parsing or DDL-emitter inference. The abstract identity `Discriminator` column
  remains for diagnostics/readability only; resolver compatibility will not parse it. Consumers that
  enumerate abstract identity scalar columns must continue to exclude both payload columns
  (`ResourceKeyId` and `Discriminator`) from identity-equality logic.
- Ops: the seed-clone script's TRUNCATE list will lose `dms."ReferentialIdentity"`; DMS template
  management will drop its DMS relational `pgcrypto` preamble only. It must not issue
  `DROP EXTENSION pgcrypto`; shared databases may still need the extension for CMS/OpenIddict key
  encryption.

### To remain unchanged

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
`RelationalMappingVersion` remains `v2` for this unreleased aggregate mapping shape.

## Migration and rollout: proposed tickets

One implementation branch, trunk-green per ticket. The list below is the proposed ticket breakdown —
one story per entry, sized like the existing stories under [`../epics/`](../epics/) — to be created
there once this document is approved. Ordering is dependency order; the resolver and write cutovers
depend on the foundation tickets (T1–T5).

**Foundations — the schema object can stay trigger-maintained as an unreferenced shadow until the
final schema drop, but the natural-key cutover is the point where production Core/backend C# stops
computing, carrying, or comparing UUIDv5 referential ids. T6–T9 are the coordinated C# cutover lane:
T6 removes resolver-facing referential ids, T7 removes the document POST target referential id, and
T9 removes the descriptor-write and `DocumentInfo` referential-id consumers. After T9, no production
Core/backend C# contract may still carry a `ReferentialId` member.**

- **T1 — Pin the SQL Server identity collation and runtime equality contract.** Emit
  `COLLATE SQL_Latin1_General_CP1_CI_AS` on every generated SQL Server string column that stores or
  copies an identity value, including root natural keys, RefKey copies, abstract identities,
  descriptor identity, tracked-change old/new identity copies, and local collection identity members.
  Preserve purpose-specific explicit collations. Introduce the backend identity-equality contract
  consumed by both DDL/runtime composition, selecting `OrdinalIgnoreCase` for this SQL Server
  contract and `Ordinal` for PostgreSQL. AC: golden DDL proves full identity-column coverage with no
  inherited-collation gaps; provisioning against `Latin1_General_100_CS_AS_SC_UTF8` preserves that
  database default while `sys.columns` reports the pinned CI collation for representative canonical,
  copied, abstract, descriptor, and tracked-change identity columns; comparer-provider tests pin each
  schema contract; PostgreSQL DDL and comparer behavior are unchanged.
- **T2 — Add document/resource invariant key plus abstract `ResourceKeyId` DDL, trigger population,
  and parity pins.** Add the `UX_Document_DocumentId_ResourceKeyId` parent key to `dms.Document`, add
  `ResourceKeyId smallint NOT NULL` to each abstract identity table and union view, FK-constrain table
  columns as `(DocumentId, ResourceKeyId)` to `dms.Document(DocumentId, ResourceKeyId)`, then populate
  the table column from the existing abstract-identity maintenance triggers using a new typed
  `AbstractIdentityMaintenance.ResourceKeyIdValue` literal paired with the existing diagnostic
  `DiscriminatorValue`. `ResourceKeyId` is payload metadata for target disambiguation, never an
  abstract identity member. AC: golden DDL/manifest diffs show the new document candidate key, the
  abstract `ResourceKeyId` column/view/trigger-value change, and
  `FK_<Abstract>Identity_DocumentResourceKey`; abstract identity-column consumers exclude
  `ResourceKeyId` and `Discriminator` from identity equality; parity and corruption pins prove insert,
  delete, identity rename, SQL Server/PostgreSQL casing behavior, and document/resource-key drift
  attempts preserve or enforce concrete `ResourceKeyId` population from compile-time metadata.
- **T3 — Compile and serialize natural-key probe metadata.** Compile per-resource probe metadata
  (reference targets, own-key probes, the descriptor probe) from the relational model — never from
  trigger metadata, constraint names, or discriminator string parsing — with an empty-identity compile
  guard and an every-resource parity guard against the live trigger derivation. Serialize the
  storage-resolved, typed probe records into PackFormatVersion 1 mapping packs: target and own-key
  records live with their resource packs, and the shared descriptor probe lives at payload scope.
  Each serialized key entry includes the physical column plus scalar type or descriptor-resource
  binding metadata, so abstract probes are AOT-self-contained even though abstract resources do not
  serialize a `RelationalResourceModel`. AOT decode reconstructs the mapping-set dictionaries from
  those authoritative records and does not rerun the probe compiler. AC: parity guard green for every
  resource; mapping-pack round trips reproduce all three mapping-set probe contracts with typed key
  binding metadata and semantic key-column order intact; AOT decode builds the same runtime probe
  dictionaries without ApiSchema re-derivation.
- **T4 — Re-source 409 duplicate-identity diagnostics from compiled probes.** Move
  `duplicateIdentityValues` classification off `ReferentialIdentityMaintenance` trigger metadata and
  onto the compiled own-key probe metadata, severing the trigger-metadata dependency before the
  triggers drop. The failure mapper must not parse trigger metadata, constraint names, discriminator
  strings, or generated SQL to determine natural-key members. AC: duplicate-identity responses are
  unchanged on both engines; the mapper remains green when `ReferentialIdentityMaintenance` trigger
  metadata is withheld from a test mapping set; descriptor-valued identity diagnostics preserve
  semantic key-column order.
- **T5 — Add descriptor ASCII validation, `UX_Descriptor_UriLowered_ResourceKeyId`, and descriptor
  document/resource FK.** Reject
  non-ASCII or NUL descriptor URI values on descriptor writes, descriptor references, and
  descriptor-valued query filters. This changes the current implementations: Core descriptor
  identity/reference extraction validates before constructing lowercased identities, relational
  query preprocessing identifies backend-compiled descriptor-id query targets and rejects malformed
  query values before descriptor-reference creation or resolver lookup, and downstream
  normalization sites replace unchecked `ToLowerInvariant()` calls with the shared
  validated-ASCII-without-NUL helper. Emit the final lower-storage index shape on both engines:
  PostgreSQL gets the unique expression index on `lower("Uri" COLLATE "C"), "ResourceKeyId"` with no
  new column; SQL Server gets the non-persisted `UriLowered AS LOWER([Uri])` computed column and a
  unique index on `UriLowered, ResourceKeyId`. Replace the descriptor's independent document/resource
  constraints with `FK_Descriptor_DocumentResourceKey` on `(DocumentId, ResourceKeyId)` to
  `dms.Document(DocumentId, ResourceKeyId)` so the lookup authority cannot drift from the owning
  document. The legacy Discriminator-authoritative index stays through the transition. AC: golden DDL
  diff shows exactly the new index shape (and SQL Server computed column) plus the descriptor
  document/resource FK; PostgreSQL descriptor lookup SQL proves every lowered descriptor URI
  expression uses `COLLATE "C"` rather than the database default; write failures identify the concrete
  descriptor-reference path or the offending descriptor `namespace`/`codeValue` field; query failures
  use the existing query-validation 400; validation pins prove no descriptor resolver call occurs for
  non-ASCII or NUL input and corruption pins reject descriptor/document `ResourceKeyId` drift.
- **T6 — Natural-key resolver contract cutover.** Implement the dialect command builders
  (PostgreSQL `unnest` and SQL Server OPENJSON + `FORCE ORDER` group statements, the
  union-projection single-statement form, the parameter-budget guard), plus
  `NaturalKeyReferenceResolver` implementing the resolver role (structural memo, shared typed-value
  conversion, ordinal result mapping) and the composite embeddability seams. This ticket **replaces**
  the hash resolution arm rather than coexisting with it: `Add{Postgresql,Mssql}ReferenceResolver()`
  composes the new resolver directly, the old resolver (per-engine lookup builders/strategies,
  result reader, corruption canary) and its test suites are deleted, and composite-seam consumers
  re-point to the new factory.

  The resolver-facing contracts land here in final shape. `DocumentReference`,
  `DescriptorReference`, `SuperclassIdentity`, `DocumentReferenceFailure`,
  `DescriptorReferenceFailure`, lookup requests, lookup results, resolved-reference maps, replay
  snapshots, and query preprocessing stop carrying or constructing referential ids. The production
  descriptor query preprocessor is cut over in this ticket: it identifies descriptor-id query targets
  from compiled metadata, validates malformed values before resolver calls, resolves valid descriptor
  filter values through the natural-key descriptor probe, and preserves the existing empty-page
  behavior for valid-but-missing or wrong-type descriptor URIs. Core reference-array duplicate
  validation also moves off `DocumentReference.ReferentialId` here, using the schema-contract-derived
  structural comparer needed after the model member disappears.

  AC: SQL-shape pins (batch-size-independent text on PG, leftmost OPENJSON input, explicit DMS
  identity collation on every textual OPENJSON key operand, one statement-level `FORCE ORDER` on
  MSSQL, budget-guard throw, abstract probes projecting concrete `ResourceKeyId` with no
  discriminator-to-key map); resolver unit suites green, including a resolved document-reference map
  pin proving separate `DocumentIdentity` arrays with identical ordered elements resolve to the same
  `DocumentId`; descriptor query-filter URI, case-variant URI, malformed URI, nonexistent URI, and
  wrong-type URI pins green; reference-array duplicate pins green without referential ids; existing
  reference-resolution-dependent integration estate green on both engines, now exercising the new
  resolver. Correctness on this branch is carried by the behavior pins, the integration estate, and
  E2E (see ["Test strategy"](#test-strategy)). If production-shaped workloads later disagree on
  performance, the capture-predicate contingency ladder applies, with reverting the composite
  write-path batching (DMS-1332) accepted as the last resort.
- **T7 — POST upsert-detection cutover + SQL Server stored-identity rebind.** Replace the composite
  write path's capture predicate hash subselect with the natural-key predicate (inline
  RefKey/lowered-descriptor subselects) and the standalone fallback with the `UX_<R>_NK` probe. The
  target resolver binds from `DocumentInfo.DocumentIdentity` and compiled own-key probe metadata,
  never from a UUIDv5 referential id. As part of this target-resolution boundary, perform the
  SQL Server merged-row stored-identity rebind ahead of authorization and no-op detection so
  post-as-update uses stored casing for the existing row. Delete
  `RelationalWriteTargetRequest.Post.ReferentialId` and the RI target-lookup builders here.
  AC: command-stream pins show round-trip counts unchanged (POST create stays at 2 commands) and RI
  command classification zero for resource POST target lookup; case-variant POST pins prove 200,
  stored casing served, guarded no-op, no referrer rewrite, no key-change row, and no
  `IdentityVersion` bump on SQL Server; PostgreSQL behavior unchanged; write suites green; the
  write-flow sketches in `transactions-and-concurrency.md` and `flattening-reconstitution.md` show
  stored-identity rebind before proposed-value authorization, no-op detection, and writer DML.
- **T8 — Post-resolution collection duplicate detection + generic conflict fallback.** Add the
  two-tier duplicate detection that requires resolved ids for reference/descriptor collection
  members and the schema-contract-derived comparer for local string scalars. Add the ODS-parity 409
  fallback for unclassified unique violations. This ticket assumes resolver-facing Core contracts are
  already referential-id-free from T6 and only owns duplicate detection that depends on resolved
  relational ids or database exceptions. AC: the per-engine duplicate-detection pin matrix is green;
  the case-variant duplicate-descriptor E2E scenario is green; unmapped unique violations return the
  generic ODS-parity 409 rather than a 5xx; the write-flow sketches in
  `transactions-and-concurrency.md` and `flattening-reconstitution.md` show the storage-resolved
  duplicate validator after reference/descriptor resolution and before storage binding, no-op
  detection, or collection-table DML.
- **T9 — Descriptor write handler cutover + final Core UUIDv5 cleanup.** Replace descriptor upsert
  detection with lowered-URI + `ResourceKeyId` probes and implement stored-wins casing for descriptor
  writes (persisted-identity binding, the split no-op comparer, the case-insensitive PUT identity
  guard). The descriptor handler no longer accepts `DescriptorWriteRequest.ReferentialId` and no
  longer writes `dms.ReferentialIdentity`. With the last document-level consumer gone, delete
  `DocumentInfo.ReferentialId`, `ReferentialId`, `ReferentialIdFactory`,
  `ReferentialIdCalculator`, `No.ReferentialId`, Core extraction-time referential-id calculation, and
  the UUIDv5 package dependency if it has no remaining consumers. AC: descriptor write/stamping suites
  green; stored-wins pins per engine; targeted `rg` over Core models, Core extraction/middleware,
  resolver/query contracts, and write-request contracts finds no `ReferentialId`, `ReferentialIds`,
  `ReferentialIdFactory`, or `ReferentialIdCalculator`; any remaining `ReferentialId` text is
  confined to RI DDL/trigger maintenance and the T11–T13 schema-removal lane.
- **T10 — Change Query descriptor identity cutover.** Update Change Query recreated-row detection.
  Descriptor `/deletes` and descriptor-valued identity joins in resource `/deletes` probe the live
  descriptor table by lowered URI plus the descriptor resource's compile-time `ResourceKeyId`;
  shared-tombstone `Discriminator` remains a routing predicate only. Remove the now-unused live
  `IX_Descriptor_Discriminator_ContentVersion` index. AC: SQL snapshots use no live-descriptor
  `Discriminator` predicate; case-only descriptor recreation suppresses the old tombstone on both
  engines, including when resolving a descriptor-valued identity part for a resource `/deletes`
  anti-join; the same URI under a different `ResourceKeyId` does not suppress it; descriptor
  route/response/authorization contracts stay unchanged.
- **T11 — ReferentialIdentity reader/dead-code and fixture sweep.** Delete any RI write-path remnants
  left only as dead code or tests: RI upsert-probe SQL, service members, and every test fixture that
  seeds RI rows directly. There should be no production Core/backend C# `ReferentialId` members by
  this point; finding one is a stop-the-line failure, not a reason to defer it again. The fixture
  sweep is a structural proof: a test failing for a missing RI row has found a surviving reader (stop
  and investigate, never reseed). AC: production Core/backend C# contains no RI reader or
  referential-id contract; tests do not seed `dms.ReferentialIdentity` rows except in schema-drop
  fixtures that explicitly exercise this ticket lane.
- **T12 — Drop `TR_<R>_ReferentialIdentity` triggers.** Drop the
  `TR_<R>_ReferentialIdentity` trigger family, scope-guarded so the DocumentCache enqueue, stamping,
  and abstract-identity trigger families are kept. Update the shared cross-engine parity-contract
  tests once for both engines. AC: golden DDL removes only the RI trigger family; retained trigger
  families and abstract identity parity pins remain green on both engines.
- **T13 — Drop `dms.ReferentialIdentity`, uuidv5 DDL, and remaining RI infrastructure.** Drop the
  table, `uuidv5`, DMS-generated `CREATE EXTENSION pgcrypto` / `digest()` usage, and the TVP, and
  collapse descriptor uniqueness onto the new `ResourceKeyId`-authoritative case-insensitive index.
  Do not drop the `pgcrypto` extension from an existing database; it may be owned by CMS/OpenIddict
  in shared-database deployments. AC: the final golden diff shows exactly the predicted removals and
  **no version-string or hash churn** because this remains within the unreleased `v2` aggregate
  mapping shape. PostgreSQL DMS-generated DDL contains no `dms.uuidv5()`, `digest(`, or DMS-owned
  `CREATE EXTENSION pgcrypto`; the gate must not assert the extension is absent from a running
  database because CMS/OpenIddict may still create it.

**Release compatibility note:** this branch assumes the upcoming release continues to publish mapping
version `v2` as the aggregate prerelease shape. Before `v2` is published, no `v2 → v3` bump is
required for this change. After `v2` is published, any later incompatible relational mapping change
must bump `RelationalMappingVersion` and re-bless the schema-hash pin so stale released databases
fail fast with the designed 503.

Rollback before the final schema-drop ticket will be a commit revert: `dms.ReferentialIdentity`
stays trigger-maintained until T13, so reverting the resolver swap (or any cutover ticket) resumes
against current data. After the final schema-drop ticket, rollback will be re-provisioning with the
previous build —
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
  tracked-change old/new identity columns, and local collection identity columns, while
  purpose-specific binary columns retain their collation.
- **Abstract identity DDL pins** prove every abstract identity table emits
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
- **Descriptor ASCII validation pins**: descriptor writes, descriptor references, and
  descriptor-valued query filters containing non-ASCII or NUL URI values return a path-attributed
  400 before any relational lookup.
- **PostgreSQL descriptor-lowering pins**: golden DDL and generated SQL fixtures prove every
  descriptor lowered-URI index/probe expression uses `COLLATE "C"`; a locale-sensitive fixture covers
  ASCII `I`/`i` descriptor identity under a non-default database collation so PostgreSQL cannot drift
  from C# `ToLowerInvariant()` or Change Query recreated-row detection.
- **Probe-compilation unit tests**, including an every-resource parity guard that will prove the
  compiled probes reproduce the legacy trigger derivation for as long as both exist, and abstract
  target pins proving the probe projects concrete `ResourceKeyId` without a discriminator-to-key map.
- **Abstract identity parity/corruption pins** proving trigger-maintained `<Abstract>Identity` rows
  and diagnostic union views stay in parity with concrete root rows across insert, committed root
  delete via `dms.Document` cascade, identity rename, SQL Server identity collation behavior,
  PostgreSQL byte-sensitive behavior, and concrete `ResourceKeyId` population from compile-time
  member metadata.
- **Mapping-pack round-trip tests** prove target probes, own-key probes, serialized concrete
  `ResourceStorageKind`, and the shared descriptor probe survive PackFormatVersion 1 encode/decode
  with storage-resolved columns, typed key binding metadata, and semantic key-column order unchanged;
  malformed presence, resource-kind, column, binding, and dialect combinations fail fast. Fixtures
  must prove no `ApiSchema` re-derivation is needed to build typed probe commands or distinguish
  relational-table resources from shared-descriptor resources.
- **Dialect SQL unit tests**: statement shape independent of batch size (PostgreSQL), OPENJSON +
  FORCE ORDER + leftmost-input pins, explicit DMS identity collation on every textual OPENJSON key
  operand, and the parameter-budget guard (SQL Server), plus the union-projection single-statement
  form.
- **No old-vs-new gates.** The prototype's differential equivalence proof and benchmark matrix stand
  as the transition evidence; neither suite is ported to the implementation branch. Correctness is
  carried by the behavior pins below, the existing integration estate (running against the new
  resolver from T6 onward), and E2E; performance remedies are pre-agreed (the contingency ladder,
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
    and never `Discriminator`; PostgreSQL probes lower both the live URI and any tombstoned
    namespace/codeValue expression under `COLLATE "C"`.
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
   columns, including tracked-change old/new identity copies, explicitly use
   `SQL_Latin1_General_CP1_CI_AS`; this removes deployment-dependent identity behavior and moves DMS
   toward ODS behavior.
5. **Descriptor case-variant duplicates** will be rejected by a table-level CI unique index over
   lowered ASCII-without-NUL URI + `ResourceKeyId` (they are same-document by hash semantics today) — accepted;
   identical effective semantics, newly enforced by the engine.
6. **Lost `dms.ReferentialIdentity` corruption canary** — accepted by construction for RI: the hash
   rows it guarded will no longer exist. This does not remove every derived-state risk; abstract
   identity tables remain trigger-maintained and are covered by the explicit parity/corruption pins
   above.
7. **Casing comparer approximation** — the runtime provider derives `OrdinalIgnoreCase` from the
   fixed SQL Server identity contract, but it still does not emulate every
   `SQL_Latin1_General_CP1_CI_AS` equality; divergences will fail closed (documented above), never
   silently redefine database identity.
8. **ASCII-only descriptor URIs** — accepted to keep descriptor matching deterministic while
   minimizing storage. Non-ASCII and NUL descriptor URI values will be rejected explicitly rather
   than normalized differently by different engines or allowed to fail later against PostgreSQL
   `text`/`varchar` storage.

## Open proposal: non-ASCII descriptor URIs (decision pending)

The business requires descriptor `namespace` and `codeValue` values to accept non-ASCII characters.
That conflicts with the ASCII-only-without-NUL descriptor URI contract this document establishes
(see "Descriptors", behavioral delta list, and accepted trade-off #8). This section presents two
candidate designs for lifting the restriction. **No decision has been made.** Until one is, every
other section of this document — and the dependent write/query contracts in
[flattening-reconstitution.md](flattening-reconstitution.md), [key-unification.md](key-unification.md),
and [transactions-and-concurrency.md](transactions-and-concurrency.md) — intentionally still
describes the ASCII-only contract. After the decision, this section will be folded into the
affected sections and the dependent documents will be updated.

Both options keep descriptor matching case-insensitive. They differ in **who owns the case-folding
rules**: the database engines (Option A) or the application (Option B).

### Shared under both options

- **Validation narrows but does not disappear.** The ASCII check is replaced by: reject **NUL**
  (PostgreSQL `text`/`varchar` cannot store it) and reject **malformed UTF-16** (unpaired
  surrogates from JSON `\uXXXX` escapes, which PostgreSQL rejects as invalid UTF-8 at write time).
  Same validation sites, same path-attributed 400s: descriptor writes attribute `$.namespace` /
  `$.codeValue`, descriptor references attribute the concrete request path, and
  `RelationalQueryRequestPreprocessor` keeps the query-validation 400 (never `EmptyPage`). The
  shared "validated ASCII without NUL" helper becomes "validated well-formed without NUL".
- **No Unicode normalization.** Canonically-equivalent spellings (precomposed `é` vs
  `e` + combining accent) are **distinct descriptors**. This is deliberate consistency with regular
  string identity columns, which already store and compare unnormalized values on both engines.
  Accepted residual: visually identical descriptor pairs can coexist; neither engine's folding nor
  either option below merges them.
- **HTTP boundary pin.** Descriptor-valued query parameters are UTF-8 percent-decoded by the
  framework before validation; a pin proves the decoded value (not the raw encoded form) is what
  gets validated and folded.
- **Collection duplicate detection is unaffected.** Descriptor members already compare by resolved
  `DescriptorId` (the exact tier), which is casing- and encoding-agnostic.
- **Descriptor POST-as-update casing rules are unchanged** (stored-wins, casing-only re-POST is a
  true no-op).
- **Migration backfill is trivial**: all existing descriptor data is ASCII, so both options
  reproduce today's stored identity exactly at cutover.
- **Considered and rejected: PostgreSQL nondeterministic ICU collations** (a CI collation on the
  `Uri` column, no lowering anywhere). It would reimport ambient ICU version drift — folding rules
  changing under an OS patch, the exact hazard the deterministic-collation contract exists to
  eliminate.

### Option A — engine-side folding (`pg_c_utf8` + SQL Server CI collation)

Folding moves entirely into SQL; C# never lowercases. Core extraction and the query preprocessor
pass the raw validated value; every probe folds its parameter with the same expression as the
index.

- **PostgreSQL**: replace `COLLATE "C"` with the builtin **`pg_c_utf8`** collation (PostgreSQL 17+)
  in every descriptor lowering expression: index
  `lower("Uri" COLLATE "pg_c_utf8"), "ResourceKeyId"`, probe predicates
  `lower($n COLLATE "pg_c_utf8")`, and the Change Query recreated-row probes. The builtin provider
  folds full Unicode (simple case mapping) with no libc/ICU dependency; rules change **only at
  PostgreSQL major upgrades**, never via OS updates.
- **SQL Server**: the designed shape is unchanged (`UriLowered AS LOWER([Uri])` computed column,
  CI unique index). For non-ASCII, identity verdicts are whatever `SQL_Latin1_General_CP1_CI_AS`
  says — including width-insensitivity and linguistic equalities (`ß`/`ss`) that cannot be fully
  specified or emulated in C#.
- **In-process equality goes ordinal and fails closed.** The structural memo's descriptor key
  member becomes the raw (unfolded) URI: case-variant spellings of one descriptor reference remain
  separate memo entries and produce redundant probe rows that the database resolves to the same
  `DocumentId` — the identical accepted posture SQL Server regular keys already have. Descriptor
  equality constraints compare ordinally and fail closed with 400 (stricter than today's lowered
  comparison).
- **Tracked changes: no schema impact.** `/deletes` recreated-row probes fold tombstoned values in
  SQL under the same collation.
- **Versioning cost**: a PostgreSQL major upgrade can change folding for characters present in
  data, so the descriptor expression index requires `REINDEX` after `pg_upgrade` — and a Unicode
  revision that makes two stored descriptors newly collide blocks the `REINDEX` until the data is
  manually resolved. Requires a documented upgrade playbook.
- **Engines diverge** on which non-ASCII variants match (simple Unicode fold vs legacy CI
  collation). The "uniform across engines" descriptor claim in the casing table narrows to ASCII
  input.

### Option B — application-side folding, byte-comparing storage

DMS owns one canonicalization function; the database only ever compares bytes.

- **One folding authority in C#**: validate (as above), then lowercase with `ToLowerInvariant()`.
  Acknowledged: invariant casing follows the Unicode data of the runtime environment (ICU on
  modern .NET, so host- and .NET-version-dependent), meaning folding behavior can change when the
  runtime or host is upgraded. Because the folded value is persisted, such a change surfaces as
  stored `UriLowered` values disagreeing with newly computed ones — remediated by an `UPDATE`
  recompute of the folded columns, never index corruption. If this drift risk proves
  unacceptable, the function can later be hardened by vendoring a pinned Unicode
  `CaseFolding.txt` mapping into DMS, with no change to schema or probe shapes.
- **Schema**: `dms.Descriptor.UriLowered` becomes a **persisted, application-written column**
  (originals keep stored casing — referencing documents reconstitute descriptor strings from
  `dms.Descriptor`, so the source columns remain the display authority). Unique index on
  `("UriLowered", "ResourceKeyId")` over plain columns: PostgreSQL `COLLATE "C"`, SQL Server
  `Latin1_General_100_BIN2`. The PostgreSQL expression index and the SQL Server computed column are
  not created; **no `lower()` remains in generated descriptor SQL**, retiring the `COLLATE "C"`
  descriptor-lowering pins.
- **Tracked changes: one new column.** Descriptor tombstone identity copies gain a folded-URI
  column stamped at write time, so `/deletes` recreated-row probes stay a byte-compare. This is
  the schema cost the option pays.
- **In-process equality is exact.** The memo comparer's descriptor key member and descriptor
  equality constraints use the same fold function; agreement with the engine verdict is byte
  equality — exact, not approximate, closing the fail-closed gaps for descriptors.
- **Cross-engine uniformity survives non-ASCII**: both engines compare identical C#-produced
  bytes, so descriptor matching and uniqueness behave identically on PostgreSQL and SQL Server for
  the full input space.
- **Derived-state cost, named honestly**: `UriLowered` and the tombstone copies are
  application-derived state stored in the database — the same species this design removes
  elsewhere. Scope is mild (one column family, one write path, no triggers, no cross-resource
  coupling), and it takes the standard treatment: a parity pin proving
  `UriLowered = fold(Uri)` on every descriptor and tombstone row, plus a corruption pin. The
  parity pin doubles as drift detection: after a runtime upgrade that changed invariant casing,
  it is what fails first, pointing at the recompute remediation.

### Decision drivers

| Dimension | A: engine-side (`pg_c_utf8`) | B: application-side fold |
|---|---|---|
| Folding authority | Two (each engine's own rules) | One (C# `ToLowerInvariant()`) |
| Rules change when | PostgreSQL major upgrade (ambient for the deployment) | .NET runtime / host ICU upgrade (hardenable later via a vendored fold table) |
| Remediation on rule change | `REINDEX`; newly-colliding data can block it | `UPDATE` recompute of folded columns |
| PostgreSQL floor | 17+ | none |
| Schema additions | none | `UriLowered` + tombstone folded copies (derived state + parity pins) |
| SQL Server non-ASCII semantics | CI-collation verdicts (width-insensitive, linguistic; not fully speccable) | byte equality of the folded value (exact) |
| Cross-engine descriptor uniformity | ASCII input only | full input space |
| In-process (memo/equality-constraint) agreement | approximate, ordinal fail-closed | exact (same function, byte equality) |
| Generated SQL | collation-pinned `lower()` on every descriptor expression | no folding in SQL |

### After the decision

Within this document: rework "Descriptors", "Query-time descriptor filters", the casing tables and
behavioral deltas, ticket T5, the descriptor validation/lowering pins, accepted trade-off #8, and
the ASCII-only bullet under "Out of scope". Then update
[flattening-reconstitution.md](flattening-reconstitution.md), [key-unification.md](key-unification.md),
and [transactions-and-concurrency.md](transactions-and-concurrency.md), which consume the
descriptor validation boundary and lowered-value contracts.

## Out of scope

- Any change to `dms.Document` (columns, locking, DELETE shape, readers, or its DocumentCache
  triggers).
- Changing or constraining the SQL Server database default collation. The column-level identity
  contract is specifically what allows the supported case-sensitive database default to remain.
- In-place upgrade scripts (the migration is re-provision-only; prerelease databases provisioned
  from an earlier shape must be re-provisioned).
- DocumentCache/CDC work (live, RI-free, orthogonal).
- ApiSchema contract or resource JSON shapes, except for the new ASCII-only descriptor URI value
  contract described above, including NUL rejection.
- Rewriting the design-doc corpus beyond the targeted supersession banners and the batching-doc
  correction named under Migration.
