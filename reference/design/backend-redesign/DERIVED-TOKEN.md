# Derived Token Design for Representation-Sensitive `_etag` / `_lastModifiedDate`

## Status

Draft. This document proposes an alternative to the “stored representation-version bump” design in `reference/design/backend-redesign/transactions-and-concurrency.md` for generating `_etag` and `_lastModifiedDate` under identity/descriptor cascades.

## Motivation

The backend redesign draft treats `_etag` and `_lastModifiedDate` as **representation metadata** that must change when the returned JSON representation changes (including when referenced identities or descriptor URIs change). The draft achieves this by:

1. computing an impacted set (`CacheTargets = IdentityClosure + 1-hop referrers`), and
2. **updating `dms.Document(Etag, LastModifiedAt)`** for every `DocumentId` in `CacheTargets`,
3. requiring phantom-safe impacted-set computation, which drives **SERIALIZABLE semantics** / key-range locks on the `dms.ReferenceEdge` scan.

This document describes a different approach (“derived tokens”) that:

- preserves the semantic requirement: `_etag/_lastModifiedDate` change whenever the representation changes,
- eliminates cross-document “representation bump” cascades, and therefore removes the need for SERIALIZABLE edge scans,
- keeps `dms.ReferentialIdentity` strict and transactional (identity correctness remains the hard requirement),
- shifts work from “fan-out writes” to “read-time derivation” using stable per-document tokens.

Compatibility note: recent Ed-Fi ODS/API versions also bump ETag/LastModifiedDate (and `ChangeVersion` for Change Queries) on indirect representation changes (e.g., referenced identity/descriptor URI changes). Derived tokens target the same externally-visible semantics without write-time fan-out; see `reference/design/backend-redesign/DERIVED-TOKEN-CHANGE-VERSION.md` for the `ChangeVersion` mapping.

## Requirements and non-goals

### Requirements

1. **Correctness**: `_etag` and `_lastModifiedDate` MUST change when the representation changes.
2. **Best-effort minimization**: `_etag/_lastModifiedDate` MAY change even if the representation does not change, but the system should make a best effort to avoid unnecessary changes.
3. **Cross-engine**: must work on PostgreSQL and SQL Server.
4. **Optimistic concurrency**: `If-Match` is representation-sensitive (the current design explicitly intends `If-Match` failures when upstream identity/URI changes alter the representation).

### Non-goals

- Eliminating strict identity correctness (`dms.ReferentialIdentity` transactional recompute) is out of scope; this document assumes that portion of the draft remains.
- Making `dms.DocumentCache` “free” is not guaranteed; derived tokens change the cache freshness mechanics.

## High-level idea

Instead of *mutating* dependent documents’ stored `Etag/LastModifiedAt` whenever something they reference changes, each document stores small, stable **local tokens**, and the API `_etag/_lastModifiedDate` are **derived at read time** from:

1. the document’s own local content token, plus
2. the identity/URI tokens of the documents whose identities/URIs are embedded in the document’s representation.

This is a Merkle-ish approach: a document’s representation token is a hash over its own content token and the identity tokens of its immediate outbound dependencies.

When a referenced document’s identity changes, **only that referenced document’s identity token changes** (plus any strict identity-closure recompute already required). The referencing document’s `_etag/_lastModifiedDate` then change automatically on the next read, because they are derived from the referenced document’s new token.

There is no need to compute `CacheTargets`, no need to scan `dms.ReferenceEdge` under SERIALIZABLE, and no need to update `dms.Document` rows for dependent documents just to bump `_etag/_lastModifiedDate`.

## Definitions

### Representation dependency

For a document `P` (parent), the representation returned by the API embeds:

- **Document references**: identity fields from the referenced resource instance (e.g., `studentReference.studentUniqueId`).
- **Descriptor references**: the descriptor URI string (e.g., `gradeLevelDescriptor`).

Therefore, `P`’s representation depends on:

- the **identity projection** of each referenced resource document, and
- the **URI identity** of each referenced descriptor document.

This document calls those outbound targets “representation dependencies”.

### Local tokens (per document)

Each persisted document maintains two small tokens:

1. **Content token**: changes when *this document’s own persisted relational content changes* (root/child rows, ordinals, FK targets, scalar fields).
2. **Identity token**: changes when *the identity projection of this document changes* (the values used to construct its `{resource}Reference` object or, for descriptors, the descriptor `uri`).

Additionally, each token has a corresponding “last modified” timestamp:

- `ContentLastModifiedAt`: time of last change to the document’s own persisted content.
- `IdentityLastModifiedAt`: time of last change to the document’s identity projection / descriptor URI.

These are not “read-time” timestamps; they are persisted metadata updated on writes / identity recomputes.

## Proposed data model adjustments

### `dms.Document` (conceptual)

This design replaces the “stored representation etag” concept with two stored tokens:

- `ContentVersion` (or `ContentHash`)
- `IdentityVersion` (or `IdentityHash`)
- `ContentLastModifiedAt`
- `IdentityLastModifiedAt`

Example (PostgreSQL-ish sketch; not final DDL):

```sql
ALTER TABLE dms.Document
  ADD COLUMN ContentVersion bigint NOT NULL DEFAULT 1,
  ADD COLUMN IdentityVersion bigint NOT NULL DEFAULT 1,
  ADD COLUMN ContentLastModifiedAt timestamp with time zone NOT NULL DEFAULT now(),
  ADD COLUMN IdentityLastModifiedAt timestamp with time zone NOT NULL DEFAULT now();
```

Notes:

- `ContentVersion` is bumped when the persisted relational content for that `DocumentId` changes.
- `IdentityVersion` is bumped when the identity projection changes (for the document itself, and for identity-closure dependents during strict identity recompute).
- Either “version” can be replaced with a hash if preferred (`bytea`/`varbinary(32)`), but versions are often easier to index/inspect.
- Best-effort minimization is achieved by *only bumping* when an actual change is detected (see “Best-effort minimization” below).

## Derived API metadata

### Derived `_etag`

For document `P`, define:

- `P.ContentVersion`, `P.IdentityVersion`
- For each representation dependency `D` of `P`:
  - `D.IdentityVersion` (or identity hash)

Then:

```
P._etag = Base64(SHA-256( EncodeV1(
  P.ContentVersion,
  P.IdentityVersion,
  Sorted( for each dependency D: (D.DocumentId, D.IdentityVersion) )
)))
```

Key properties:

- If `P`’s own content changes ⇒ `ContentVersion` changes ⇒ `_etag` changes.
- If any dependency identity changes ⇒ that dependency’s `IdentityVersion` changes ⇒ `_etag` changes.
- No cross-document writes are needed to make a referencing document’s `_etag` change.

### Derived `_lastModifiedDate`

Define representation last modified as the maximum of:

- `P.ContentLastModifiedAt` (local persisted content changes),
- `P.IdentityLastModifiedAt` (local identity projection changes),
- and each dependency’s `IdentityLastModifiedAt` (because only identity/URI changes of dependencies can affect `P`’s representation).

```
P._lastModifiedDate = max(
  P.ContentLastModifiedAt,
  P.IdentityLastModifiedAt,
  max(for each dependency D: D.IdentityLastModifiedAt)
)
```

This preserves the semantic requirement (“changes when representation changes”) without mutating dependent documents.

## How to find “representation dependencies”

Representation dependencies can be derived from the same ApiSchema-driven relational mapping used for reconstitution:

- Every FK column that represents a document reference contributes a dependency on the target document’s identity token.
- Every FK column that represents a descriptor reference contributes a dependency on the target descriptor document’s identity token (covering URI).

Two practical sources:

1. **From the reconstitution read** (cache miss):
   - reconstitution already gathers referenced `DocumentId`s to project identity fields and descriptor URIs;
   - add a parallel projection for `(DocumentId, IdentityVersion, IdentityLastModifiedAt)`.

2. **From a dependency projection query** (cache hit / “metadata only”):
   - compile a plan that `UNION ALL`s all FK columns for the resource (root + child tables), filtered by `ParentDocumentId`,
   - returns distinct target `DocumentId`s (and optionally counts if needed).

This avoids relying on `dms.ReferenceEdge` for correctness; `dms.ReferenceEdge` can remain for diagnostics and operational tooling, but is not required to derive `_etag/_lastModifiedDate`.

## Write path changes

### Normal writes (POST/PUT, no identity cascade)

Within a write transaction for document `P`:

1. Persist the relational rows for `P` (root + children).
2. Detect whether persisted content changed (best-effort; see below).
3. If changed:
   - bump `P.ContentVersion` and set `P.ContentLastModifiedAt = now()`.
4. Detect whether `P`’s identity projection changed (either because scalar identity fields changed, identity-component FK changed, or identity-component descriptor URI changed).
5. If identity projection changed:
   - bump `P.IdentityVersion` and set `P.IdentityLastModifiedAt = now()`,
   - update `dms.ReferentialIdentity` for `P` (primary + superclass alias).

There is no `CacheTargets` computation and no updates to other documents just to maintain `_etag/_lastModifiedDate`.

### Identity updates (strict closure recompute)

The redesign draft already requires transactional closure locking + recompute for `dms.ReferentialIdentity` when identities change. Under derived tokens:

- the strict closure work remains (identity correctness),
- but the “representation version bump for closure + 1-hop referrers” is removed,
- and identity recompute additionally updates `IdentityVersion/IdentityLastModifiedAt` for impacted documents when their identity projection changes.

Concretely, during the closure recompute transaction:

1. Compute + lock `IdentityClosure` (as in the draft).
2. For each impacted document `X` in the closure:
   - recompute the identity projection values used in reference objects and referential-id computation,
   - if those values differ from the previous values:
     - bump `X.IdentityVersion`,
     - set `X.IdentityLastModifiedAt = now()`,
     - update `dms.ReferentialIdentity` rows for `X`.

This produces a single “identity-token change” at the documents whose identities actually changed; dependents’ `_etag/_lastModifiedDate` shift automatically on reads.

## Read path changes

### GET by id (cache miss: reconstitute)

1. Reconstitute the JSON body from relational tables.
2. While doing identity projection / descriptor URI expansion, also collect:
   - the set of referenced target `DocumentId`s,
   - the set of referenced descriptor `DocumentId`s.
3. In a single batched query, load dependency tokens:
   - `SELECT DocumentId, IdentityVersion, IdentityLastModifiedAt FROM dms.Document WHERE DocumentId = ANY (@deps)`
4. Compute:
   - `_etag` using `P.ContentVersion`, `P.IdentityVersion`, and dependency `(DocumentId, IdentityVersion)` pairs.
   - `_lastModifiedDate` as the max of the relevant timestamps.
5. Inject `_etag` and `_lastModifiedDate` into the response.

### GET by id (cache hit: projection is present)

If `dms.DocumentCache` stores a materialized JSON representation (including reference identities and descriptor URIs), the cached JSON can become stale when any dependency identity/URI changes.

With derived tokens, “freshness check = `cache.Etag == dms.Document.Etag`” no longer applies. Two options:

1. **Verify-by-dependencies (correct, extra queries)**:
   - compute current derived `_etag` for `P` (using a dependency projection query + dependency token read),
   - compare to `dms.DocumentCache.Etag` stored at materialization time,
   - return cache only if it matches.

2. **Keep `dms.ReferenceEdge` for fast dependency lookup (optional)**:
   - maintain `dms.ReferenceEdge` as an exact outbound-dependency index,
   - on cache read, use it to fetch dependency ids cheaply (no FK-column union query),
   - compute derived `_etag` and compare.

Both preserve correctness. The trade-off is that derived tokens make cache hits more expensive than a single-row compare, unless you keep an accurate outbound dependency index.

### Query paging

Query responses return a page of `DocumentId`s. Derived tokens require dependency token reads, so avoid N+1 by batching:

1. Materialize the page’s documents (or use cache).
2. Extract all dependency `DocumentId`s across the page (dedupe).
3. Read dependency identity tokens in one query.
4. Compute `_etag/_lastModifiedDate` per document using the in-memory lookup.

This matches the existing reconstitution approach that already batches identity projections by referenced resource type.

## Optimistic concurrency (`If-Match`)

The current draft uses a stored `dms.Document.Etag` and a conditional update (`WHERE Etag = @expected`) to make representation-sensitive concurrency cheap and atomic.

With derived tokens there is no single stored “representation etag” row to compare against, so the concurrency check must compare `If-Match` to a **derived** token computed from current state.

### Recommended: lock outbound dependencies during the check (strict semantics)

To preserve the intended behavior (“If-Match fails when upstream identity/URI changes alter the representation”), the derived token used for the `If-Match` comparison must be stable for the duration of the write transaction.

Recommended algorithm for update/delete of `P` when `If-Match` is present:

1. Determine outbound dependency `DocumentId`s for `P` (dependency projection query or `dms.ReferenceEdge`).
2. Acquire **shared identity locks** on `dms.IdentityLock(DocumentId)` for:
   - every outbound dependency `D` (so `D.IdentityVersion` cannot change during the transaction), and
   - optionally `P` itself (consistent ordering), using ascending `DocumentId` to reduce deadlocks.
3. Compute current derived `_etag` for `P` by reading:
   - `P.ContentVersion`, `P.IdentityVersion`,
   - each dependency’s `IdentityVersion`.
4. Compare to `If-Match`. If mismatch, fail with the appropriate status.
5. Proceed with the write using the draft’s existing lock ordering for identity correctness.

This replaces “write-write conflicts from etag cascades” with “read locks on the precise set of dependencies” and avoids SERIALIZABLE edge scans.

### Simpler (weaker) alternative: no dependency locks

If the system is willing to accept a narrow race where a dependency identity changes between the `If-Match` check and commit, the shared locks can be omitted.

This reduces locking but weakens the intended “representation-sensitive If-Match” guarantee under concurrency. If this is considered acceptable, it should be explicitly documented and tested.

## Best-effort minimization strategies

Derived tokens allow correctness without cross-document bumps, but minimization still matters for client cache churn and sync workflows.

Recommended best-effort measures:

### 1) Avoid bumping `ContentVersion` on no-op writes

Detect whether the persisted relational content for `P` actually changes:

- For scalars: compare incoming values to persisted values (or rely on “rows affected” patterns).
- For collections: use diff-based upsert instead of “delete-all then insert-all” so no-op updates produce zero writes.

If no persisted values change, do not bump `ContentVersion/ContentLastModifiedAt`.

### 2) Avoid bumping `IdentityVersion` on idempotent recompute

During identity closure recompute:

- compute the new identity projection (the values used in reference objects),
- compare to the previous projection (or compare referential-id + identity projection hash),
- only bump `IdentityVersion/IdentityLastModifiedAt` if values actually differ.

### 3) Keep the derived `_etag` stable for equivalent dependency sets

Canonicalize the dependency input:

- dedupe dependencies (a dependency’s identity token is the same regardless of how many times it is referenced),
- sort deterministically by `(DocumentId)` (and include `(ProjectName, ResourceName)` only if needed to avoid ambiguity).

This ensures `_etag` does not change due to arbitrary ordering.

## Worked examples

### Example 1: upstream identity change changes dependent `_etag` without cascades

Assume:

- Student `S` has `DocumentId=100`, `IdentityVersion=10`, `IdentityLastModifiedAt=T1`.
- GraduationPlan `G` has `DocumentId=400`, `ContentVersion=5`, `IdentityVersion=2`, `ContentLastModifiedAt=T0`, `IdentityLastModifiedAt=T0i`.
- `G` references `S`, and that reference is **not** an identity component edge (`IsIdentityComponent=false`), so `G` is not in `S`’s `IdentityClosure`.

Derived tokens:

```
G._etag = Hash(G.ContentVersion=5, G.IdentityVersion=2, deps=[(100, 10)])
G._lastModifiedDate = max(G.ContentLastModifiedAt=T0, G.IdentityLastModifiedAt=T0i, S.IdentityLastModifiedAt=T1)
```

Now `S` has an identity update (e.g., `studentUniqueId` changes), and strict identity recompute bumps:

- `S.IdentityVersion = 11`
- `S.IdentityLastModifiedAt = T2`

No updates occur to `G`.

Next GET of `G`:

```
G._etag = Hash(G.ContentVersion=5, G.IdentityVersion=2, deps=[(100, 11)]) // changed
G._lastModifiedDate = max(G.ContentLastModifiedAt=T0, G.IdentityLastModifiedAt=T0i, S.IdentityLastModifiedAt=T2) // changed
```

This meets the “representation metadata changes” requirement without computing `CacheTargets` or updating `G` in the write transaction for `S`.

### Example 2: upstream non-identity change does not change dependent `_etag`

If `S` changes a non-identity field (e.g., `lastSurname`):

- `S.ContentVersion` changes
- `S.IdentityVersion` does not change

Since `G` depends only on `S.IdentityVersion`, `G._etag` does not change (correct: the reference object embedded in `G` did not change).

## Differences from the current redesign draft

### What is removed / simplified

- **No `CacheTargets`** for representation metadata.
- **No cross-document `UPDATE dms.Document SET Etag=Etag+1, LastModifiedAt=...`** driven by identity/descriptor changes.
- **No SERIALIZABLE / key-range locks** for phantom-safe “1-hop referrers of IdentityClosure” scans.

### What remains (unchanged hard requirements)

- `dms.ReferentialIdentity` is still a strict derived index and still requires:
  - identity closure computation/locking (Algorithms 1–2 in the draft),
  - transactional recompute on identity changes.
- Identity-component edge correctness still matters for identity closure.

### What moves from write-time to read-time

- Representation metadata becomes a function of:
  - local versions, and
  - dependency identity tokens read at request time.

This increases read-time work modestly (extra columns / batched lookups), but removes the potentially large fan-out write work and the concurrency complexity around phantom-safe referrer scans.

### Cache implications

- The current draft’s cache freshness check (`DocumentCache.Etag == Document.Etag`) is no longer available.
- Correctness-preserving cache usage requires verifying derived tokens, which needs either:
  - a dependency projection query, or
  - maintaining an exact outbound dependency index (e.g., `dms.ReferenceEdge`) to avoid scanning FK columns.

## Operational and performance notes

- **Write throughput**: derived tokens remove the highest-fanout operation (bumping many dependent docs), which should reduce lock contention and deadlocks under identity/descriptor churn.
- **Read cost**: derived tokens add “dependency token fetch + hash” per returned document. For deep documents and query paging, batching is required (similar to the existing identity projection batching).
- **Hot dependency documents**: under strict `If-Match` locking, updates to a document that references a “hub” (e.g., many documents reference one `School`) may acquire shared locks on that hub during updates, increasing contention. This is typically less severe than transitive closure locking, but it should be benchmarked.

## Open questions / decisions needed

1. **Token type**: prefer `bigint` versions vs `sha256` hashes for `Content` and `Identity` tokens?
2. **Strict `If-Match` semantics**: do we require shared-locking outbound dependencies for updates/deletes, or accept a narrow race?
3. **Dependency source**: rely on FK-column union projection queries, or keep `dms.ReferenceEdge` as a correctness-grade outbound dependency index to speed cache hits and `If-Match` checks?
