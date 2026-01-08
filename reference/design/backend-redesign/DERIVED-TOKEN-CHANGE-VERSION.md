# Derived Token + ChangeVersion Design

## Status

Draft. This document extends `reference/design/backend-redesign/DERIVED-TOKEN.md` by adding a DMS `ChangeVersion` design that aligns with Ed-Fi ODS/API Change Queries semantics while keeping the limited locking model (no `CacheTargets` fan-out, no SERIALIZABLE edge scans).

Note: recent ODS versions bump ETag/LastModifiedDate/**and ChangeVersion** for indirect representation changes (e.g., referenced identity/descriptor URI changes). This design accounts for that behavior while still avoiding write-time fan-out.

## Goals

1. Preserve the derived-token design’s guarantee: `_etag` / `_lastModifiedDate` MUST change when the returned representation changes.
2. Add an ODS-style, monotonically increasing `ChangeVersion` that advances for both direct and indirect representation changes.
3. Derive `ChangeVersion` from the derived-token design’s existing stored tokens (`ContentVersion`/`IdentityVersion`) and dependency identity stamps so we don’t introduce a second, unrelated versioning scheme.
4. Keep locking limited (same as `DERIVED-TOKEN.md`): no broad referrer scans under SERIALIZABLE for representation metadata maintenance.

## Non-goals (explicit trade-offs)

- Exactly matching ODS’s *implementation mechanism* (write-time fan-out bumps) is not a goal; DMS can achieve the same externally-visible semantics using derived tokens.
- Eliminating strict identity correctness work (`dms.ReferentialIdentity` and `IdentityClosure` recompute/locking) is out of scope; this design assumes those requirements remain.

## Background: what ChangeVersion is in ODS/API

In Ed-Fi ODS/API:

- `ChangeVersion` is a **global**, monotonically increasing `bigint` sourced from a single database sequence.
- Each resource row stores its latest `ChangeVersion`, and change queries apply inclusive filtering:
  - `ChangeVersion >= minChangeVersion` (if provided)
  - `ChangeVersion <= maxChangeVersion` (if provided)
- Recent versions treat `ChangeVersion` (and ETag/LastModifiedDate) as **representation-sensitive**: indirect changes that alter a resource’s representation can also advance the resource’s `ChangeVersion`.
- Deletes and key changes are returned from separate tracked-change tables, each event assigned its own `ChangeVersion` (also from the global sequence).

This model works because `ChangeVersion` is global and monotonic, and it is cheap to query by range using an index.

## Key design decision: make Content/Identity versions be “global stamps”

The derived-token design already requires two persisted “local tokens”:

- `ContentVersion` (changes when the document’s persisted content changes)
- `IdentityVersion` (changes when the document’s identity projection changes)

To make an ODS-compatible `ChangeVersion` derivable from these tokens, we redefine them as **global stamps** allocated from a single global sequence.

### Why not “just add them”?

If `ContentVersion` and `IdentityVersion` are per-document counters, then:

- `ContentVersion + IdentityVersion` is not global (not comparable across documents), and therefore cannot back ODS-style `minChangeVersion` / `maxChangeVersion` filtering.

If they are global stamps, then:

- Adding them produces a value that does not correspond to any real change event (and double-counts when both are set in the same transaction).
- The correct “latest change” value is the **maximum**, not the sum.

So in this design: `P.LocalChangeVersion = max(P.ContentVersion, P.IdentityVersion)`.

Because ODS semantics include indirect representation changes, DMS defines:

- `P.LocalChangeVersion = max(P.ContentVersion, P.IdentityVersion)` (direct changes to `P`)
- `P.ChangeVersion = max(P.LocalChangeVersion, max(deps.IdentityVersion))` (direct + indirect representation changes)

## Data model (conceptual)

### Global sequence

Introduce a single global sequence used for all change stamps:

- PostgreSQL: `CREATE SEQUENCE dms.ChangeVersionSequence START WITH 1;`
- SQL Server: `CREATE SEQUENCE dms.ChangeVersionSequence AS bigint START WITH 1 INCREMENT BY 1;`

This sequence is the source of all values assigned to:

- `dms.Document.ContentVersion`
- `dms.Document.IdentityVersion`
- tracked delete/key-change events (future change queries)

### `dms.Document`

Keep the derived-token columns from `DERIVED-TOKEN.md`, but interpret the `*Version` columns as global stamps:

- `ContentVersion bigint NOT NULL DEFAULT 0`
- `IdentityVersion bigint NOT NULL DEFAULT 0`
- `ContentLastModifiedAt datetime NOT NULL`
- `IdentityLastModifiedAt datetime NOT NULL`

Add a queryable `LocalChangeVersion` representation (either computed or materialized):

- Computed definition: `LocalChangeVersion = max(ContentVersion, IdentityVersion)`
- PostgreSQL index option: expression index on `GREATEST(ContentVersion, IdentityVersion)`
- SQL Server option: persisted computed column `LocalChangeVersion AS (CASE WHEN ContentVersion > IdentityVersion THEN ContentVersion ELSE IdentityVersion END) PERSISTED` + index

`LocalChangeVersion` is indexable and supports efficient “what did this document itself change?” queries.

The API-facing `ChangeVersion` (direct + indirect representation changes) is derived at read/query time (see below). This matches ODS semantics without requiring write-time fan-out bumps.

## Derived API metadata (unchanged from derived-token design)

All derived-token behavior remains as in `reference/design/backend-redesign/DERIVED-TOKEN.md`:

- `_etag` is a hash over:
  - `P.ContentVersion`, `P.IdentityVersion`, and
  - each outbound dependency’s `IdentityVersion` (resource identity projection or descriptor URI identity).
- `_lastModifiedDate` is:
  - `max(P.ContentLastModifiedAt, P.IdentityLastModifiedAt, max(deps.IdentityLastModifiedAt))`

## ChangeVersion semantics in DMS

### Definition

For any document `P`:

```
P.LocalChangeVersion = max(P.ContentVersion, P.IdentityVersion)
P.ChangeVersion = max(
  P.LocalChangeVersion,
  max(for each representation dependency D of P: D.IdentityVersion)
)
```

### What causes ChangeVersion to advance?

`ChangeVersion` advances when **either** this document’s tokens advance **or** one of its representation dependencies’ identity stamps advances:

- `ContentVersion` due to an actual persisted content change for `P`, or
- `IdentityVersion` due to an actual identity projection change for `P` (including identity-closure recompute where `P` is in the closure and its identity projection changes).
- Any representation dependency `D.IdentityVersion` change (referenced identity/descriptor URI changes that affect `P`’s representation).

### What does NOT advance ChangeVersion?

`ChangeVersion` does **not** advance for indirect changes that do not affect representation (e.g., upstream **content-only** changes where `D.IdentityVersion` does not change).

## Write-path rules (tokens + ChangeVersion)

This section augments `DERIVED-TOKEN.md` by specifying how bumps are sourced from the global sequence.

Notation: this document uses `nextval(dms.ChangeVersionSequence)` as shorthand for “allocate the next stamp from the global sequence” (PostgreSQL: `nextval('dms.ChangeVersionSequence')`; SQL Server: `NEXT VALUE FOR dms.ChangeVersionSequence`).

### Rule 1: allocate a stamp only when something actually changes

To minimize unnecessary churn:

- Do not allocate a sequence value until after you have determined there is a real content and/or identity projection change.
- Continue using the “best-effort minimization strategies” from `DERIVED-TOKEN.md` (no-op detection, identity projection comparison).

### Rule 2: one stamp per document update (recommended)

When a single write transaction updates a single document `P`, allocate at most one stamp for that document update:

1. Detect `contentChanged` and `identityChanged` for `P`.
2. If neither changed: do not bump versions.
3. If either changed:
   - `v = nextval(dms.ChangeVersionSequence)`
   - if `contentChanged`: set `P.ContentVersion = v`, `P.ContentLastModifiedAt = now()`
   - if `identityChanged`: set `P.IdentityVersion = v`, `P.IdentityLastModifiedAt = now()`, and update `dms.ReferentialIdentity` as required.

This makes `P.LocalChangeVersion` equal to the single stamp `v`. `P.ChangeVersion` will be at least `v`, and can be greater if any dependency has a higher `IdentityVersion`.

For inserts (POST creating a new document), treat this as `contentChanged=true` and `identityChanged=true` and initialize both `ContentVersion` and `IdentityVersion` from the allocated stamp.

### Rule 3: identity-closure recompute bumps `IdentityVersion` with stamps

During strict identity recompute (closure locking and recomputation required for `dms.ReferentialIdentity`):

- For each impacted document `X` in the closure:
  - recompute identity projection values,
  - if the identity projection changed:
    - allocate `v = nextval(dms.ChangeVersionSequence)`,
    - set `X.IdentityVersion = v`, `X.IdentityLastModifiedAt = now()`,
    - update `dms.ReferentialIdentity` for `X`.

This produces monotonic identity stamps that (a) keep strict identity correctness, and (b) automatically flow into dependent resources’ derived `_etag/_lastModifiedDate/ChangeVersion` on subsequent reads.

## Dependency depth: is 1 level enough?

For derived `_etag/_lastModifiedDate`, a **single level of outbound representation dependencies** is sufficient, because:

- For non-identity edges: `_etag` directly incorporates the referenced document’s `IdentityVersion` (read-time derivation; no cascades).
- For identity-component edges: strict identity closure recompute already updates `IdentityVersion` for documents whose identity projection changes, which “rolls up” transitive identity changes into the dependent document’s own `IdentityVersion`.

So: no precomputed “fan-out dependency sources” are needed for derived tokens beyond what is already required for identity closure correctness.

## Future Change Query API mapping (DMS)

This design enables an ODS-like change query API (including indirect changes) without reintroducing representation-version cascades.

### 1) `availableChangeVersions`

Return:

- `newestChangeVersion`: the last allocated value of `dms.ChangeVersionSequence`.
  - If the sequence has never been used, DMS can return `0` (matching the “0 if no version is available” language) or return the sequence start value; pick one and document it.
- `oldestChangeVersion`: optional (either `0`, or derived from retention policy / oldest retained tracked event).

### 2) Resource change queries (GET with `minChangeVersion`/`maxChangeVersion`)

For a resource change query, DMS must consider both:

- **direct** changes to the resource (`LocalChangeVersion`), and
- **indirect** representation changes caused by dependency identity/URI changes (`deps.IdentityVersion`).

One approach that stays consistent with derived tokens:

1. Build a candidate set of `DocumentId`s as the union of:
   - documents where `LocalChangeVersion` is within the requested window, and
   - documents that reference a dependency `D` whose `IdentityVersion` is within the requested window (requires an inbound lookup; `dms.ReferenceEdge` can serve as the index).
2. For each candidate document, compute derived `ChangeVersion` using the same dependency set used to compute derived `_etag`, then apply the `minChangeVersion/maxChangeVersion` bounds to the derived value.
3. Return the *current representation* plus `changeVersion`.

This matches ODS behavior: return the *current representation* of resources whose latest `ChangeVersion` is within the requested window.

### 3) Deletes (`/{resource}/deletes`)

To support deletes, DMS will need a tracked-delete event store because the `dms.Document` row is gone (or “soft deleted”).

Proposed minimal tracked-delete model:

- `dms.TrackedDelete`:
  - `DocumentUuid` (or stable resource identifier exposed by API)
  - `ResourceName` (and/or schema/project)
  - `DeletedAt`
  - `ChangeVersion` (allocated from `dms.ChangeVersionSequence`)
  - `KeyValues` (serialized identity projection needed by the deletes endpoint)

On delete:

- allocate `v = nextval(dms.ChangeVersionSequence)`
- insert tracked delete event with `ChangeVersion = v`

### 4) Key changes (`/{resource}/keyChanges`)

If DMS supports changing a resource’s identifying values (identity projection changes that affect the “key values” in references), change queries need key-change tracking.

Proposed minimal tracked-key-change model:

- `dms.TrackedKeyChange`:
  - `DocumentUuid` (stable ID)
  - `ResourceName`
  - `ChangeVersion` (allocated from `dms.ChangeVersionSequence`)
  - `OldKeyValues` (identity projection before)
  - `NewKeyValues` (identity projection after)

On identity projection change for the document itself:

- allocate `v` (same stamp as used for the `IdentityVersion` bump under Rule 2)
- update `IdentityVersion = v`
- insert tracked key change event with `ChangeVersion = v`

This keeps key change events in the same global version space and avoids needing additional locking.

Identity projection changes that occur during strict identity-closure recompute (Rule 3) should be treated the same way: if a document’s *exposed key values* change, record a key change event for that document using the same stamp that was assigned to its `IdentityVersion`.

## Worked example: derived `_etag` vs ChangeVersion

Using the same scenario as `DERIVED-TOKEN.md` Example 1:

- Student `S` (`DocumentId=100`) has an identity update, so `S.IdentityVersion` advances to a new stamp.
- GraduationPlan `G` (`DocumentId=400`) references `S` but the edge is non-identity (`IsIdentityComponent=false`).

Results:

- `G._etag` changes (derived from `S.IdentityVersion`), so caching/concurrency metadata remains representation-correct.
- `G.ChangeVersion` also changes (derived from `S.IdentityVersion`), matching ODS semantics for indirect representation changes without updating `G`.

## Summary of differences vs `DERIVED-TOKEN.md`

- Adds a global `dms.ChangeVersionSequence`.
- Interprets `ContentVersion` and `IdentityVersion` as stamps allocated from that global sequence.
- Defines `ChangeVersion` as `max(LocalChangeVersion, max(deps.IdentityVersion))` to align with ODS’s “indirect changes advance ChangeVersion” semantics.
- Introduces tracked delete/key-change event tables (future-facing) using the same global sequence.
