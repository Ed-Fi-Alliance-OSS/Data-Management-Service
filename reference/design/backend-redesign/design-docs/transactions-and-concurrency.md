# Backend Redesign: Transactions, Concurrency, and Cascades

## Status

Draft.

This document is the transactions/concurrency deep dive for `overview.md`, focusing on:
- reference validation,
- write-time identity propagation and derived maintenance (`dms.ReferentialIdentity`, abstract identity tables),
- update-tracking stamping (`dms.Document`), and
- operational caching patterns.

- Overview: [overview.md](overview.md)
- Update tracking: [update-tracking.md](update-tracking.md)
- Data model: [data-model.md](data-model.md)
- Authentication & authorization: [auth.md](auth.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Key unification (canonical columns + generated aliases): [key-unification.md](key-unification.md)
- Extensions: [extensions.md](extensions.md)
- DDL Generation: [ddl-generation.md](ddl-generation.md)
- AOT compilation (optional mapping pack distribution): [aot-compilation.md](aot-compilation.md)
- Strengths and risks: [strengths-risks.md](strengths-risks.md)

## Table of Contents

- [Reference Validation](#reference-validation)
- [How `ApiSchema.json` Drives This Design](#how-apischemajson-drives-this-design)
- [Write Path (POST Upsert / PUT by id)](#write-path-post-upsert--put-by-id)
- [Read Path (GET by id / GET query)](#read-path-get-by-id--get-query)
- [Caching (Low-Complexity Options)](#caching-low-complexity-options)
- [Optional Database Projection: `dms.DocumentCache`](#optional-database-projection-dmsdocumentcache)
- [Delete Path (DELETE by id)](#delete-path-delete-by-id)
- [Schema Validation (EffectiveSchema)](#schema-validation-effectiveschema)
- [Operational Considerations](#operational-considerations)

---

## Reference Validation

Reference validation is provided by **two layers**:

### 1) Write-time validation (application-level)

During POST/PUT processing, the backend:
- Normally resolves each extracted reference (`DocumentReference` / `DescriptorReference`) to a target `DocumentId`
  using an ApiSchema-derived natural-key resolver.
- Fails the request if a referenced identity does not exist, except for an existing PUT-by-`DocumentUuid` binding instance
  that exactly satisfies a compiled `SameStatementReferenceResolutionPlan`. That update-only plan may reuse the locked
  stored target id only when the retained cascade will give the same row the submitted future identity in the initiating
  statement; it is not a general lookup-miss fallback.

This is required because relational tables store **stable `DocumentId` foreign keys**, and we cannot write those without resolving them.

#### ApiSchema-derived natural-key resolver

The **natural-key resolver** is the backend service that converts a `(Project, Resource, DocumentIdentity)` triple into a persisted `DocumentId` **without per-resource code**.

This redesign keeps `ReferentialId` as the canonical “natural identity key” that the resolver uses for `ReferentialId → DocumentId` lookups; see [overview.md#Why keep ReferentialId](overview.md#why-keep-referentialid).

It is used for:
- resolving extracted `DocumentReference` / `DescriptorReference` instances during writes,
- POST upsert existence detection (always via `dms.ReferentialIdentity`), and
- query-time resolution when a filter targets a referenced identity but does not have a direct column mapping.

**Inputs**
- `QualifiedResourceName` of the target (from `DocumentReference.ResourceInfo` / `DescriptorReference.ResourceInfo`)
- `DocumentIdentity` (ordered `IdentityJsonPath → string value` pairs)
- the request-local `ReferentialId` (already computed by Core for writes; optional for query-time and computable deterministically using the same UUIDv5 algorithm as Core)
- optional “location” info for error reporting (e.g., concrete JSON path instance from Core extraction)

**Outputs**
- `DocumentId` when found
- “not found” when no matching row exists (write-time: validation failure; query-time: empty-match behavior)

##### Resolution algorithm (bulk, request-scoped)

1. Dedupe referential ids across all extracted references.
2. Resolve in bulk via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`).
3. For descriptor references, validate “is a descriptor” (and optional descriptor type) via `dms.Descriptor`.
4. For an eligible unresolved PUT instance only, select one exact binding/origin/mutation-case executor plan after stored
   authorization/current-state load; prove locked row correlation and the complete future public/anchor/id vector, then
   create an instance-scoped override.
5. After the initiating update, bypass caches and verify the submitted referential id, stable target id, and demanded
   anchors before commit. Any mismatch rolls back.

##### Caching

The resolver uses layered caching:
- **Per-request memoization** (always): avoids duplicate work within one request.
- Optional L1/L2 caches (after-commit population only):
  - `ReferentialId → DocumentId`

When identity updates occur, any cross-request cache of `ReferentialId → DocumentId` must be updated/evicted for affected keys after commit (or disabled / short-TTL; see [Caching](#caching-low-complexity-options)).
Never seed a cache from a certified future-identity override; v1 waits for ordinary post-commit lookup behavior.

### 2) Database enforcement (FKs + propagation)

Relational tables store references as foreign keys so the database enforces referential integrity.

Under key unification, a single logical identity value can appear at multiple JsonPaths within one document. The derived
relational model distinguishes:
- **Path/binding columns**: retain per-site/per-path column names and `SourceJsonPath`; may be stored or generated
  aliases.
- **Canonical/storage columns**: the single stored/writable source of truth for unified values; used for cascades,
  composite FKs, and propagation.
- **Intrinsic identity-lineage inventory/storage**: each target records a stable lineage id and stored `DocumentId` for
  every reference-backed identity lineage, independently of any incoming reference.
- **Site anchor demand**: an incoming reference starts with no anchors and gains a local anchor mapping only when a
  receiver-side full-FK validity/correlation obligation requires that target lineage. Demanded anchors participate in the
  site's propagation key/FK but never in API reconstitution.

#### Document references (`..._DocumentId` + identity-part columns)

For each document reference site, the referencing table includes:
- the stable `..._DocumentId` (stored/writable), and
- the referenced resource’s identity natural-key fields as local per-site identity-part columns
  (`{RefBaseName}_{IdentityPart}`), and
- only the storage-only identity-lineage anchor columns in that site's demanded `AnchorSetId`.

Under key unification, `{RefBaseName}_{IdentityPart}` columns are treated as **path/binding columns**. They may be
stored (baseline redesign) or generated/persisted aliases of canonical storage columns (unified redesign), preserving
the invariant that absent optional reference sites imply `NULL` at per-site binding columns.

DDL generator requirements (derived from ApiSchema):
- Enforce “all-or-none” nullability for the reference group via a CHECK constraint over:
  - `{RefBaseName}_DocumentId`, and
  - the per-site identity-part binding columns (aliases when unified), and
  - every dedicated demanded local lineage-anchor column in the site's selected `AnchorSetId`.
  - Rationale: a composite FK does not enforce anything if *any* referencing column is `NULL`.
- Inventory/store each target's intrinsic reference-backed identity lineages, then initialize every incoming site's
  demanded anchor set empty.
- Add an anchor demand only when receiver-side full-FK validity or row correlation requires it. Reuse an existing local
  `..._DocumentId` only when complete identity equivalence and presence are proved; otherwise add an internal stored
  local anchor. Propagate demand only through downstream identity/constraint consumers until the least fixed point.
- Group equal demanded subsets under a stable `AnchorSetId`. Omit a target-intrinsic anchor from a site only when no
  receiver validity/correlation obligation needs it; this omission proof still universally covers every valid
  identity-mutation subset and simultaneous combination.
- Example: DS 5.2 `CourseOffering -> Session` demands Session's intrinsic School anchor because its local
  `SchoolId_Unified` is also read by `CourseOffering -> School`. An unrelated Session referrer whose receiver has no such
  FK/correlation obligation remains on the empty-anchor variant.
- Map each logical reference through canonical public-identity storage and anchor storage, then canonicalize and
  deduplicate the result into a full-composite physical FK candidate. Its propagation vector contains public identity
  components, the site's demanded anchors, and target `DocumentId`.
- For PostgreSQL, directly assign `ON UPDATE CASCADE` when the target can change transitively and `NO ACTION` otherwise.
  DMS does not prune, classify, certify, or fail PostgreSQL because of cascade topology.
- For SQL Server, derive statement-scoped `ValueFlowAnalysis` facts and globally select `NativeCascade` /
  `NoPropagation` modes so the final assignment satisfies error 1785 and every value-flow obligation. A
  `NoPropagation` candidate is valid only when its changed-target route and receiver-carrier route prove same-row,
  same-vector coverage through the relevant constraint-check boundary. A carrier route may be the zero-hop initiating
  write. Cycles are breakable when this proof exists. Certificates cover complete mutation cases; primitive proofs may
  be reused only with typed `SubsetCompositionProof`, or derivation fails as `UnprovedSubsetComposition`.
- SQL Server infeasibility and deterministic work-limit exhaustion throw `RelationalModelDerivationException` with
  distinct structured errors. No partial model, DDL, success manifest, or pack is emitted.
- Every physical FK remains full composite. There is no `DocumentId`-only or identity-value propagation-trigger fallback.

When a referenced document's identity changes, the database propagates updated public identity values and site-demanded
lineage anchors through the finalized physical FK actions into direct referrers' **canonical/storage columns**. Any
per-site binding aliases recompute automatically while preserving optional-reference presence semantics.

#### Descriptor references (`..._DescriptorId`)

Descriptor references are stored as `..._DescriptorId` FKs to `dms.Descriptor` for existence enforcement and URI reconstitution. Descriptor identity/URI is immutable and does not participate in propagation; descriptor metadata fields are mutable and affect only the descriptor resource's own representation.

#### How polymorphic (abstract) references work end-to-end

Polymorphic references are stored as a single `BIGINT` `DocumentId` FK value, but the logical target is an *abstract* resource (e.g., `EducationOrganization`) with one abstract identity shape.

The pieces fit together like this:

1. **API payload uses abstract identity fields** (not a `DocumentId`):
   - e.g., an `educationOrganizationReference` carries `educationOrganizationId`.
2. **Write-time resolution uses `dms.ReferentialIdentity`**:
   - DMS computes the target `ReferentialId` for the abstract resource type + identity values and resolves `ReferentialId → DocumentId` in bulk via `dms.ReferentialIdentity`.
   - This works because each concrete subtype maintains superclass/abstract **alias** referential-id rows, so abstract references can resolve without per-subtype SQL.
3. **Persist `..._DocumentId`, abstract identity columns, and demanded lineage anchors**:
   - The abstract target intrinsically stores all reference-backed lineage values. The referencing row stores the
     resolved `DocumentId`, abstract public identity values, and only the site-demanded anchor values derived from the
     resolved concrete member.
4. **Database enforces membership + propagation via `{AbstractResource}Identity`**
   - The composite FK targets `{schema}.{AbstractResource}Identity`. Abstract derivation emits one shared,
     table-qualified concrete-member mapping inventory used by anchor closure and trigger derivation. PostgreSQL uses its
     fixed action without DMS classification; SQL Server analysis includes the statement boundary between a concrete-row
     write and the abstract-identity maintenance trigger (see [mssql-cascading.md](mssql-cascading.md)). This ensures:
     - the reference is guaranteed to target a valid member of the hierarchy, and
     - the stored abstract identity columns are kept correct automatically.
5. **Read-time reference identity projection is local**
   - Reconstitution emits reference objects from the local per-site binding columns (stored or alias; no join required).
   - A `{schema}.{AbstractResource}_View` union view can still be emitted for diagnostics/integrations, but it is not required for API correctness.

### Delete conflicts

Deletes rely on the FK graph:
- If a document is referenced, `DELETE` fails with an FK violation; DMS maps that to a `409` conflict.
- Because FK names are deterministic, DMS can map the violated constraint back to a referencing resource/table to produce a conflict response.

Optional diagnostics:
- For “who references me?” tooling, generate deterministic inbound-reference queries from the compiled relational model (no runtime-maintained edge table).

---

## How `ApiSchema.json` Drives This Design

Deep dive on derived mapping and the minimal `ApiSchema.json` additions: [flattening-reconstitution.md](flattening-reconstitution.md) (sections 2–4).

### Existing ApiSchema inputs (already present)

- `jsonSchemaForInsert`: authoritative shape, types, formats, maxLength, required (fully dereferenced/expanded;
  no `$ref`, `oneOf`/`anyOf`/`allOf`, `enum`)
- `identityJsonPaths`: natural key extraction and uniqueness
- `documentPathsMapping`: identifies references vs scalars vs descriptor paths, plus reference identity mapping; for
  supported reference-backed persisted multi-item collection scopes, `referenceJsonPaths` also supplies the
  reference-derived semantic-identity member order when exactly one scope-local binding qualifies
- `decimalPropertyValidationInfos`: precision/scale for `decimal`
- `arrayUniquenessConstraints`: authoritative schema metadata for non-reference collection semantic identity and the
  first schema source considered for collection semantic-identity compilation / relational API-semantic unique
  constraints. For a persisted multi-item collection scope, the compiled identity is the non-empty ordered member set
  compiled from either scope-resolved `arrayUniquenessConstraints` or, for a supported reference-backed scope whose
  AUC-derived identity is still empty, exactly one scope-local `DocumentReferenceBinding` in
  `documentPathsMapping.referenceJsonPaths` order. Reference-derived members bind to the reference `..._DocumentId`
  FK column rather than to propagated identity columns. DMS does not fall back to ordinals, parent-only locators, or
  hidden/internal row ids, and supported models that cannot supply a non-empty compiled identity from either source
  must fail validation/compilation.
- `abstractResources`: abstract identity metadata for polymorphic reference targets (drives `{AbstractResource}Identity` tables and optional union views)
- `isSubclass` + superclass metadata: drives insertion of superclass/abstract alias referential-id rows in `dms.ReferentialIdentity`
- `queryFieldMapping`: defines queryable fields and their JSON paths/types; may map to:
  - root scalar columns, or
  - reference-identity binding columns for reference-object identity fields (enabling no-subquery predicates)

### Optional ApiSchema additions

This redesign proposes a small, optional `resourceSchema.relational` block to support stable physical naming without enumerating full “flattening metadata”. See [flattening-reconstitution.md](flattening-reconstitution.md) (section 3) for the proposed shape and semantics.

---

## Write Path (POST Upsert / PUT by id)

Deep dive on flattening execution and write-planning: [flattening-reconstitution.md](flattening-reconstitution.md) (section 5).

### Common steps

1. Core validates JSON and extracts:
   - `DocumentIdentity` + `ReferentialId`
   - Document references (with `ReferentialId`s)
   - Descriptor references (with `ReferentialId`s, normalized URI)
2. Backend resolves target context without DML. For an existing target, observe the minimal stored authorization inputs
   and complete stored-value authorization before loading full current/profile/correlation state or validating request
   references. A denial takes precedence over submitted-reference errors.
3. After the stored gate, backend loads current rows/concurrency state and resolves request references in bulk:
   - Use an ApiSchema-derived resolver to turn references into `DocumentId`s via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`), including:
     - self-contained identities
     - reference-bearing identities (kept current via cascades + per-resource triggers)
     - polymorphic/abstract identities via superclass/abstract alias rows in `dms.ReferentialIdentity`
   - Descriptor refs additionally require a `dms.Descriptor` existence/type check (for “is a descriptor” enforcement)
   - Normal lookup always wins. An unresolved instance may use only an exact PUT certified same-statement plan with a
     stored target id, complete future vector, and locked retained-route correlation; all other misses fail closed.
4. Backend materializes the final profile-aware merged rowset (including hidden preserved stored values and completed
   ordinary/certified surrogate ids) and authorizes proposed/request values against that exact state.
5. Backend writes within a single transaction:
   - For update flows that already loaded the current document state, backend SHOULD compare the request-derived
     post-merge rowset to the current persisted rowset before issuing DML. If they are equal, treat the request as a successful
     no-op and skip data-modifying statements (see “No-op update detection” below).
   - Insert/update `dms.Document` (allocate `DocumentId`; persist `DocumentUuid` and `ResourceKeyId`).
   - Write resource root + child + extension tables (merge strategy for collections).
   - For each document reference site:
     - persist the stable `..._DocumentId`, and
     - populate canonical/storage identity-part columns deterministically (key-unified when required).
     - populate every anchor in the site's demanded `AnchorSetId` from the resolved target lineage.
     - per-site identity-part binding columns (`{RefBaseName}_{IdentityPart}`) may be generated aliases and are not
       written directly.
   - If key unification introduces synthetic presence flags for optional non-reference paths, writers MUST set those
     flags deterministically (`NULL` when absent; true/1 when present). There is no “keep previous” behavior based on
     missing inputs.
   - `dms.Descriptor` upsert if the resource is a descriptor.
6. Database enforces propagation and maintains derived artifacts (in-transaction):
   - Composite FK propagation uses finalized dialect actions over public identity components plus site-demanded anchors.
     PostgreSQL uses its fixed assignment without DMS classification. SQL Server's globally selected modes satisfy
     value-flow obligations and error 1785; full-composite `NO ACTION` is certified only from final-assignment coverage
     (see [mssql-cascading.md](mssql-cascading.md)). Both paths update canonical/storage columns (binding aliases
     recompute).
   - Generated triggers maintain `dms.ReferentialIdentity` (row-local recompute on identity-projection value-diff
     changes). `DbTriggerInfo.IdentityProjectionColumns` are null-safe compare inputs, not `UPDATE(column)` gates.
   - The `*_Stamp` triggers stamp `dms.Document.ContentVersion` / `ContentLastModifiedAt` and `IdentityVersion` / `IdentityLastModifiedAt`, mirror `ContentVersion` / `ContentLastModifiedAt` onto the resource root (or `dms.Descriptor`) via `MirrorStampTargetTable`, and append tombstone / key-change rows to the corresponding `tracked_changes_*` table when applicable (see [update-tracking.md](update-tracking.md) for stamping rules and [change-queries.md](change-queries.md) for the mirror and tracked-change tables).
7. For every certified future-identity override, execute its cache-bypassing post-write verification query before commit
   and compare the resolved target id plus demanded anchors to the retained predicted full vector.

### Authorization (CRUD checks)

Authorization is enforced for all writes and MUST be applied before executing any data-modifying statements that would materialize unauthorized state. The baseline redesign follows the ODS strategy model (relationship-based, namespace-based, ownership-based, custom view-based) but adapts it to `DocumentId`-centric storage; see [auth.md](auth.md).

Integration points:
- Authentication occurs before the write path begins and produces a token-derived authorization context (EdOrgIds, namespace prefixes, ownership tokens, and any claim-set-derived strategy configuration).
- Existing-target writes use two ordered gates. Minimal target observation and **stored-value** authorization happen first;
  full current-state/profile loading, submitted reference resolution (including certified correlation), and their errors
  are not observable until that gate succeeds. **Proposed-value** authorization then uses the completed resolved/merged
  request state and runs before inserts/updates/deletes.
- Create operations have no stored gate; they normally resolve request references, authorize proposed values, and only
  then insert `dms.Document` or resource rows.
- On create, `dms.Document.CreatedByOwnershipTokenId` is stamped from the authenticated client context (not from the request body) and is used by the ownership-based authorization strategy.

### Identity propagation and derived maintenance (DB-driven)

This redesign keeps relationships keyed by stable `..._DocumentId` and stores referenced identity natural-key fields at
every site. Targets intrinsically store their reference-backed lineage inventory. Each incoming site starts with an empty
anchor demand set and carries a lineage `DocumentId` only when receiver-side full-FK validity/correlation needs it.
Demand flows only through downstream identity/constraint consumers to the least fixed point; omission is allowed only
after proving no receiver obligation needs that anchor across every valid identity-mutation subset and combination.
Logical sites become deduplicated full-composite physical FK candidates only after storage mapping, key unification, and
anchor closure. PostgreSQL directly assigns fixed actions. SQL Server alone derives statement-scoped value-flow facts and
globally selects `NativeCascade` / `NoPropagation` modes satisfying both the obligations and error 1785. Cycles may be
broken when every pruned edge has a proved receiver carrier. There is no `DocumentId`-only or identity-value
propagation-trigger fallback (see [mssql-cascading.md](mssql-cascading.md)).

Key effects:
- **Indirect representation changes are materialized as row updates**: when a referenced identity changes, the database
  propagates updated public values and each site's demanded anchors into direct referrers' canonical/storage columns;
  per-site/per-path binding aliases recompute and preserve presence semantics.
- **Transitive identity effects converge without application traversal**: cascades propagate through chains of references, and row-local triggers recompute derived referential ids where needed.

Engine considerations:
- PostgreSQL has no SQL Server 1785 DDL restriction. DMS never prunes, classifies, or fails its cascade topology; it emits
  the fixed full-composite action assignment.
- SQL Server rejects a table reached by multiple cascade paths or a retained cascade cycle (error 1785), so it globally
  searches for modes that satisfy the action-graph restriction and all value-flow obligations. Cycles are deliberately
  breakable: a cycle edge may use `NoPropagation` when exact changed-target and receiver-carrier proofs cover every
  mutation case. No independent-parent or shared-column shortcut is valid.
- Every physical FK keeps the full composite key. There is no `DocumentId`-only shape and no identity-value propagation
  trigger fallback. See [mssql-cascading.md](mssql-cascading.md).

### Insert vs update detection

- **Upsert (POST)**: detect an existing row by resolving the request’s `ReferentialId` via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`).
  - The resource root table’s natural-key unique constraint remains a recommended relational guardrail and is still useful for race detection (unique violation → 409) if two writers attempt to create the same natural key concurrently.
- **Update by id (PUT)**: detect by `DocumentUuid`:
  - Find `DocumentId` from `dms.Document` by `DocumentUuid`.

### No-op update detection

For `PUT`, and for `POST` when upsert resolves to an existing document, the write path SHOULD support a
whole-document no-op fast path:

- Compare the request-derived relational rowset to the current persisted rowset using the stored/writable columns that
  the normal write executor would bind, after applying the same collection merge rules the write path would execute.
- Reuse the update flow’s existing “load current document” roundtrip for this comparison. Do not add a dedicated
  preflight query by default; the optimization is meant to reduce write amplification, not trade one write for an
  extra read roundtrip.
- If the rowsets are equal, the request succeeds without issuing DML for the resource tables, `dms.Document`, or other
  derived artifacts. No update-tracking stamps or `tracked_changes_*` rows are produced.
- If any row differs, execute the normal merge write path unchanged.

This is intentionally distinct from “API-surface partial updates”. The merge executor still implements full-document
PUT semantics; it just preserves stable child-row identities and hidden profile-scoped data while computing the new
stored state.

### Identity updates (`AllowIdentityUpdates`)

If identity changes on update:
- Treat `dms.ReferentialIdentity` as a derived index and recompute it **transactionally** (via triggers) for the changed document and any documents whose identity projection changes due to cascaded identity-component updates.
- Incoming relationships to the changed document remain keyed by its stable target `DocumentId`; those columns are not
  rewritten. When the changed identity itself contains a retargeted outgoing reference, that outgoing
  `..._DocumentId`, its public values, and the target row's intrinsic lineage storage change together.
- Support changes to every public identity component, reference-backed component retargeting, and simultaneous component
  changes. Site-demanded lineage anchors cascade through downstream identity/constraint consumers in the same
  transaction; an empty-demand site remains valid for the same universally quantified mutation cases.
- When a still-present reference names a target identity that will exist only after the same initiating cascade, PUT uses
  the compiled certified plan to retain the same stable target `DocumentId`, bind predicted anchors, and verify the future
  referential id/full vector before commit. POST continues to locate existing subjects only by the request's current
  referential id and never uses this update-only fallback.

Operational guidance:
- Identity updates can fan out broadly (cascaded updates + trigger work). Keep them rare; consider operational guardrails (rate limiting, maintenance window guidance, deadlock retry).

### Cascade scenarios (tables-per-resource)

Tables-per-resource storage removes the need for **relational** cascade rewrites when upstream natural keys change,
because relationships are stored as stable `DocumentId` FKs. Identity propagation still exists for
**canonical/storage identity-part and site-demanded lineage-anchor columns** (through finalized dialect physical FK actions) and for
**derived artifacts** (referential ids and stamps), and is handled in the database:

- **Identity/URI change on a document itself** (e.g., `StudentUniqueId` update)
  - Propagation updates canonical/storage identity columns in all direct referrers (identity-component and non-identity references).
  - Referrers’ `dms.Document.ContentVersion` stamps update because their full resource-state representation changes (the embedded reference identity changed).
  - For identity-component referrers, triggers also update `dms.Document.IdentityVersion` and `dms.ReferentialIdentity` for the referrer (and this may cascade further).

- **Outgoing reference changes on a document** (`..._DocumentId` value changes)
  - Relational writes update the FK columns (`..._DocumentId`) and the canonical/storage identity-part columns (plus any
    demanded lineage anchors and synthetic presence flags required by key unification). Per-site binding aliases are not
    written directly.
  - Composite FK enforcement guarantees the public identity values and every site-demanded anchor match the referenced
    target. Omitted target-intrinsic anchors have no receiver validity/correlation obligation at that site.

- **Representation update tracking (`_etag/_lastModifiedDate`, `ChangeVersion`)**
  - `_lastModifiedDate` and `ChangeVersion` are served from stored stamps on `dms.Document`; `_etag` is computed from the deterministic canonical JSON form of the full resource-state document those stamps track, before readable profile projection and excluding response decorations such as `link`.
  - Because identity propagation is materialized as row updates, the same per-table stamping triggers cover indirect changes (no read-time dependency derivation).

### Concurrency (optimistic `If-Match`)

With stored representation stamps:
- GET returns `_etag` as the deterministic `SHA-256` hash of the current canonical full resource-state JSON representation, before readable profile projection and excluding response decorations such as `link`.
- PUT/DELETE `If-Match` validation is row-local:
  - compare the request `_etag` to the current deterministic hash for that `DocumentId`;
  - if mismatched, return `412 Precondition Failed`.
- A no-op decision made before the write batch is only provisional. Before short-circuiting, the backend MUST verify
  that the `ContentVersion` observed during comparison is still current for that `DocumentId`.
  - If the observed `ContentVersion` is still current, the backend may commit a successful no-op without DML.
  - If the observed `ContentVersion` is no longer current and the request supplied `If-Match`, return `412 Precondition Failed`.
  - If the observed `ContentVersion` is no longer current and no `If-Match` precondition was supplied, abandon the
    no-op fast path and retry / re-evaluate against current state rather than returning success based on stale data.

Because FK cascades update referrers’ rows and triggers bump their representation stamps, indirect changes correctly cause `If-Match` failures on subsequently stale clients.

Certified same-statement resolution acquires its origin/current-row/changed-target locks only after stored authorization,
in the plan's canonical table/key order, and holds them through the initiating update plus post-write verification.
PostgreSQL uses `FOR UPDATE`; SQL Server uses `UPDLOCK, HOLDLOCK`. The stored target id and observed `ContentVersion` must
still match at execution; otherwise the request follows normal stale-precondition/retry behavior rather than reusing an
unlocked id.

Collection-write note:
- For profile-constrained writes, backend loads/reconstitutes the current stored document and invokes the Core-owned projector with the same mapping-set-scoped compiled-scope catalog used for address derivation to derive `ProfileAppliedWriteContext`, including `VisibleStoredBody`, `StoredScopeStates`, and `VisibleStoredCollectionRows`.
- Backend determines the visible persisted rows for merge/delete from `StoredScopeStates` / `VisibleStoredCollectionRows` in that Core-projected stored-state contract, using the compiled semantic-identity values already supplied there. Backend MUST NOT evaluate writable-profile member filters or collection-item value predicates itself, and MUST NOT infer hidden-vs-visible-absent from `VisibleStoredBody` alone.
- Core MUST reject any writable profile definition that excludes a field required to compute the compiled semantic identity of a persisted multi-item collection scope.
- Every persisted multi-item collection scope MUST compile a non-empty semantic identity from either scope-resolved
  `arrayUniquenessConstraints` or, for a supported reference-backed scope whose AUC-derived identity is still empty,
  exactly one scope-local `DocumentReferenceBinding` in `documentPathsMapping.referenceJsonPaths` order. Collection
  semantic-identity UNIQUE constraints are derived from that compiled identity, and any supported model that still
  cannot produce it must fail before runtime merge execution.
- Correctness for accepted profile writes still relies on the same full-resource `If-Match` / `ContentVersion` guard described above; profile projection does not create a separate ETag surface, and no new API surface is required.

### Deadlock + retry policy

Deadlocks are possible under contention, especially for identity updates with large cascades. The correct response is to roll back and retry the **entire** write transaction.

Recommended: bounded retry (e.g., 3 attempts) with jittered backoff. Treat these as retryable:
- PostgreSQL: `40P01` (deadlock detected)
- SQL Server: `1205` (deadlock victim), and optionally `1222` (lock request timeout, if configured)

### SQL Server isolation defaults (recommended)

To reduce reader/writer blocking and deadlocks under concurrent write load, strongly recommend enabling MVCC reads:

- `READ_COMMITTED_SNAPSHOT ON` (recommended)
- optionally `ALLOW_SNAPSHOT_ISOLATION ON` (if a snapshot isolation level is ever used explicitly)

---

## Read Path (GET by id / GET query)

Deep dive on reconstitution execution and read-planning: [flattening-reconstitution.md](flattening-reconstitution.md) (section 6).

### GET by id

1. Resolve `DocumentUuid` → `DocumentId` via `dms.Document`.
2. Authorize the request against stored values (namespace/ownership/relationship/custom-view strategies as configured); see [auth.md](auth.md).
3. Reconstitute JSON from relational tables and return it.

The returned JSON representation must preserve:
- Array order (via `Ordinal`)
- Required vs optional properties
- The API surface properties (`id`, `_etag`, `_lastModifiedDate`)

### Query

Filter directly on **resource root table columns** and do not require subqueries for reference-identity fields that have
local per-site binding columns (stored or alias).

Authorization integration:
- Authorization MUST be applied at the SQL layer during page selection so only authorized `DocumentId`s enter the page keyset (avoid reconstituting unauthorized rows).
- The authorization filter shape depends on the configured strategies and the token-derived authorization context; see [auth.md](auth.md) for the query patterns and batching guidance.

Contract/clarification:
- `queryFieldMapping` is constrained in ApiSchema to **root-table** paths (no JSON paths that cross an array boundary like `[*]`). This constraint is enforced by **MetaEd**, so query compilation does not need child-table `EXISTS (...)` / join predicate support.
- Backend model compilation should still fail fast if any `queryFieldMapping` path cannot be mapped to a root-table column.

Ordering/paging contract:
- Collection GET results are ordered by the **resource root table’s** `DocumentId` (ascending).
- Pagination applies to that ordering (`offset` skips N rows in `DocumentId` order; `limit` bounds the page size).

Query compilation patterns:
- **Scalar query fields**: `queryFieldMapping` JSON path → derived root-table column → `r.Column = @value`
- **Descriptor query fields**: normalize URI, compute descriptor `ReferentialId` → resolve `DescriptorId` via `dms.ReferentialIdentity` → `r.DescriptorIdColumn = @descriptorId`
- **Document reference identity query fields**: compile to predicates on local per-site identity binding columns (stored
  or alias), e.g.:
  - `r.Student_StudentUniqueId = @StudentUniqueId`
  - `r.School_SchoolId = @SchoolId`

Indexing:
- Ensure a supporting index exists for every foreign key (including composite parent/child FKs and composite reference
  FKs anchored on canonical/storage identity-part columns). See [ddl-generation.md](ddl-generation.md) (“FK index policy”).

---

## Caching (Low-Complexity Options)

### Recommended cache targets

1. **Derived relational mapping (from `ApiSchema`)**
   - Cache the derived mapping per `(EffectiveSchemaHash, ProjectName, ResourceName)`.
   - Invalidation: effective schema change + restart (natural).

2. **Authentication/authorization context** (request-scoped + cross-request)
   - Cache validated bearer token → authorization context:
     - caller EdOrgIds, namespace prefixes, ownership tokens, and any resolved claim-set-derived strategy configuration.
   - Invalidation:
     - token expiry (upper bound for cache TTL),
     - security configuration changes (best-effort evict on change; otherwise rely on short TTL),
     - avoid caching across tenants/instances (include `DataStoreId` in the cache key).

3. **`dms.ReferentialIdentity` lookups**
   - Cache `ReferentialId → DocumentId` for identity/reference resolution (all identities, including reference-bearing and abstract/superclass aliases).
   - Invalidation:
     - on insert: add cache entry after commit
     - on delete: remove relevant entries (or rely on short TTL)
     - on identity/URI change: identity updates can cascade; if you cannot enumerate impacted referential ids reliably, prefer short TTL or disable this cache for correctness.

4. **Descriptor expansion lookups** (optional)
   - Cache `DescriptorId → Uri` (and optionally `Discriminator`) to reconstitute descriptor strings without repeated joins.

5. **`DocumentUuid → DocumentId`**
   - Cache GET/PUT/DELETE resolution.
   - Invalidation: add on insert, remove on delete (or rely on TTL).

### Cache keying strategy

- Always include `(DataStoreId)` and `EffectiveSchemaHash` in cache keys.
- For Redis, prefix keys with `dms:{DataStoreId}:{EffectiveSchemaHash}:...`.

### Local-only (per-node) option

Use an in-process `MemoryCache`:
- Lowest complexity; no network hop.
- Good for: derived mapping, `ReferentialId → DocumentId`, descriptor expansion.

### Redis (distributed) option

Add Redis as an optional L2 cache:
- cache-aside reads
- write-through updates after successful commit

Invalidation approaches:
- **TTL-only** (simplest): acceptable for non-critical caches; for `ReferentialId → DocumentId` a long TTL can cause incorrect resolution after identity updates.
- **Best-effort delete on writes**: on identity updates/deletes, delete known affected Redis keys after commit.
- Optional later: pub/sub “invalidate key” messages.

---

## Optional Database Projection: `dms.DocumentCache`

`dms.DocumentCache` is an optional **materialized JSON projection** of GET/query results, intended for:
- accelerating GET/query response assembly (skip reconstitution),
- CDC streaming (e.g., Debezium → Kafka), and
- downstream indexing (e.g., OpenSearch).

Correctness must not depend on this table:
- rows may be missing/stale and rebuilt asynchronously,
- authorization must not use it as a source of truth.

### Freshness contract (recommended)

When serving from `dms.DocumentCache`, treat a row as usable only if it is **fresh**:
- compare the cached representation stamp (for example, cached `ContentVersion` plus the cached materialized `_etag`) to the current `dms.Document` stamp,
- if mismatched (or missing), fall back to relational reconstitution and/or enqueue a rebuild.

### Rebuild/invalidation triggers (eventual consistency)

Because indirect representation changes are materialized as local updates to referrers (via PostgreSQL FK cascades and SQL Server native `ON UPDATE CASCADE` on eligible edges), referrer `ContentVersion` is bumped by the same `*_Stamp` trigger that handles direct writes. `dms.Document.ContentVersion` therefore captures direct content changes and indirect reference-identity changes on referrers, without reverse dependency expansion at the projector layer.

A minimal projector approach:

1. Consume `dms.Document` in `ContentVersion` order.
2. Rebuild `dms.DocumentCache` for `(DocumentId, ContentVersion)` rows not yet applied.
3. Keep `dms.DocumentCache` rows tagged with the applied representation stamp (for example, the applied `ContentVersion` plus the derived materialized `_etag`) to enforce the freshness contract above.

---

## Delete Path (DELETE by id)

1. Resolve `DocumentUuid` → `DocumentId`.
2. Delete the concrete resource row, or the `dms.Descriptor` row for descriptor resources. This fires the resource or descriptor `_Stamp` trigger while `dms.Document` is still present, so the tombstone trigger can read `DocumentUuid` and the freshly bumped `ContentVersion`.
3. Delete the corresponding `dms.Document` row. The remaining `ON DELETE CASCADE` paths to `dms.DocumentCache` and `dms.ReferentialIdentity` finalize lifecycle cleanup.
4. Rely on FK constraints from referencing resource tables to prevent deleting referenced records.

Steps 2 and 3 execute in this order within the same transaction. The reverse order (deleting `dms.Document` first and relying on `ON DELETE CASCADE` to remove the resource row) would silently lose `/deletes` tombstones because the resource row’s `AFTER DELETE` stamping trigger would fire after `dms.Document` was already gone, causing its `INNER JOIN dms.Document` to match no rows. See `change-queries.md` §"Cascade-ordering requirement for deletes" and DMS-1180 (`epics/10-update-tracking-change-queries/17-delete-by-id-tombstone-ordering.md`) for the rationale.

Error reporting:
- SQL Server and PostgreSQL will report FK constraint violations. DMS should map the violated constraint name back to the referencing resource (deterministic FK naming) to produce a conflict response comparable to today’s `DeleteFailureReference`.
- Interim behavior: DMS-1010 ships a placeholder name (`"(referenced document)"` / `"(referenced descriptor)"`) in `DeleteFailureReference`; constraint-name-to-resource mapping is implemented by DMS-1011.

---

## Schema Validation (EffectiveSchema)

This redesign treats schema changes as an **operational concern outside DMS**. DMS does not define any in-place schema evolution behavior; instead it validates compatibility **per database** on **first use** of that database connection string:

- Schema provisioning is performed by a separate DDL generation utility that builds the same derived relational model as runtime and emits/provisions dialect-specific DDL (see [ddl-generation.md](ddl-generation.md)).
- Each provisioned database records its schema fingerprint in `dms.EffectiveSchema` + `dms.SchemaComponent`.
- `dms.EffectiveSchema` is a singleton current-state row; DMS reads `EffectiveSchemaHash` (and seed fingerprint columns) from that row.
- When a request is routed to a `DataStore`/connection string, DMS reads that database’s recorded fingerprint **once** (cached per connection string), and uses `EffectiveSchemaHash` to select the matching compiled mapping set.
- If no mapping set is available for that `EffectiveSchemaHash`, DMS rejects requests for that database (other databases can still be served).

This keeps schema mismatch a **fail-fast** condition while avoiding “one mis-provisioned instance prevents the server from starting” in multi-instance deployments.

---

## Operational Considerations

### Random UUID index behavior (`dms.ReferentialIdentity.ReferentialId`)

`ReferentialId` is a UUID (deterministic UUIDv5) and is effectively randomly distributed for index insertion. The primary concern is **write amplification** (page splits, fragmentation/bloat), not point-lookup speed.

**SQL Server guidance**
- Use a sequential clustered key (recommended: cluster on `(DocumentId, ResourceKeyId)`), and keep the UUID key as a **NONCLUSTERED** PK/unique index.
- Consider a lower `FILLFACTOR` on the UUID index (e.g., 80–90) to reduce page splits; monitor fragmentation and rebuild/reorganize as needed.

**PostgreSQL guidance**
- B-tree point lookups on UUID are fine; manage bloat under high write rates with:
  - index/table `fillfactor` (e.g., 80–90) if insert churn is high
  - healthy autovacuum settings and monitoring
  - periodic `REINDEX` when bloat warrants it
- If sustained ingest is extreme, consider hash partitioning `dms.ReferentialIdentity` by `ReferentialId` (e.g., 8–32 partitions) to reduce contention and make maintenance cheaper.
