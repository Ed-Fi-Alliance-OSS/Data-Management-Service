# Derived Token + ChangeVersion Design

## Status

Draft. This document extends `reference/design/backend-redesign/DERIVED-TOKEN.md` by adding a DMS `ChangeVersion` design that aligns with Ed-Fi ODS/API Change Queries semantics while keeping the limited locking model (no `CacheTargets` fan-out, no SERIALIZABLE edge scans).

## Goals

1. Preserve the derived-token design’s guarantee: `_etag` / `_lastModifiedDate` MUST change when the returned representation changes.
2. Add an ODS-style, monotonically increasing `ChangeVersion` to support future Change Query endpoints.
3. Derive `ChangeVersion` from the derived-token design’s existing stored tokens (`ContentVersion`/`IdentityVersion`) so we don’t introduce a second, unrelated versioning scheme.
4. Keep locking limited (same as `DERIVED-TOKEN.md`): no broad referrer scans under SERIALIZABLE for representation metadata maintenance.

## Non-goals (explicit trade-offs)

- Making `ChangeVersion` *fully representation-sensitive* (i.e., change whenever derived `_etag` changes due solely to upstream identity/descriptor changes on **non-identity** edges) is not a goal. Doing so requires write-time fan-out or phantom-safe referrer scans, which this design intentionally avoids.
- Eliminating strict identity correctness work (`dms.ReferentialIdentity` and `IdentityClosure` recompute/locking) is out of scope; this design assumes those requirements remain.

## Background: what ChangeVersion is in ODS/API

In Ed-Fi ODS/API:

- `ChangeVersion` is a **global**, monotonically increasing `bigint` sourced from a single database sequence.
- Each resource row stores its latest `ChangeVersion`, and change queries apply inclusive filtering:
  - `ChangeVersion >= minChangeVersion` (if provided)
  - `ChangeVersion <= maxChangeVersion` (if provided)
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

So in this design: `ChangeVersion(P) = max(P.ContentVersion, P.IdentityVersion)`.

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

Add a queryable `ChangeVersion` representation (either computed or materialized):

- Computed definition: `ChangeVersion = max(ContentVersion, IdentityVersion)`
- PostgreSQL index option: expression index on `GREATEST(ContentVersion, IdentityVersion)`
- SQL Server option: persisted computed column `ChangeVersion AS (CASE WHEN ContentVersion > IdentityVersion THEN ContentVersion ELSE IdentityVersion END) PERSISTED` + index

This yields an ODS-style `ChangeVersion` without introducing a third independent counter.

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
P.ChangeVersion = max(P.ContentVersion, P.IdentityVersion)
```

### What causes ChangeVersion to advance?

`ChangeVersion` advances when **this document’s persisted tokens advance**, i.e., when the system updates:

- `ContentVersion` due to an actual persisted content change for `P`, or
- `IdentityVersion` due to an actual identity projection change for `P` (including identity-closure recompute where `P` is in the closure and its identity projection changes).

### What does NOT advance ChangeVersion?

`ChangeVersion` does **not** advance merely because `P`’s derived `_etag` changes due to upstream identity/URI changes along **non-identity** reference edges (i.e., “representation dependencies” that are not part of identity closure).

This is the key trade-off that keeps locking limited and avoids write-time fan-out.

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

This makes `P.ChangeVersion` equal to the single stamp `v`, matching the “latest change version on the row” model from ODS.

For inserts (POST creating a new document), treat this as `contentChanged=true` and `identityChanged=true` and initialize both `ContentVersion` and `IdentityVersion` from the allocated stamp.

### Rule 3: identity-closure recompute bumps `IdentityVersion` with stamps

During strict identity recompute (closure locking and recomputation required for `dms.ReferentialIdentity`):

- For each impacted document `X` in the closure:
  - recompute identity projection values,
  - if the identity projection changed:
    - allocate `v = nextval(dms.ChangeVersionSequence)`,
    - set `X.IdentityVersion = v`, `X.IdentityLastModifiedAt = now()`,
    - update `dms.ReferentialIdentity` for `X`.

This produces a monotonic, queryable `ChangeVersion` for identity-driven changes *that are already being updated transactionally for identity correctness*.

## Dependency depth: is 1 level enough?

For derived `_etag/_lastModifiedDate`, a **single level of outbound representation dependencies** is sufficient, because:

- For non-identity edges: `_etag` directly incorporates the referenced document’s `IdentityVersion` (read-time derivation; no cascades).
- For identity-component edges: strict identity closure recompute already updates `IdentityVersion` for documents whose identity projection changes, which “rolls up” transitive identity changes into the dependent document’s own `IdentityVersion`.

So: no precomputed “fan-out dependency sources” are needed for derived tokens beyond what is already required for identity closure correctness.

## Future Change Query API mapping (DMS)

This design enables an ODS-like change query API without reintroducing representation-version cascades.

### 1) `availableChangeVersions`

Return:

- `newestChangeVersion`: the last allocated value of `dms.ChangeVersionSequence`.
  - If the sequence has never been used, DMS can return `0` (matching the “0 if no version is available” language) or return the sequence start value; pick one and document it.
- `oldestChangeVersion`: optional (either `0`, or derived from retention policy / oldest retained tracked event).

### 2) Resource change queries (GET with `minChangeVersion`/`maxChangeVersion`)

For a resource query that returns a set of `DocumentId`s:

- Join to `dms.Document` and filter by `ChangeVersion` (computed as `max(ContentVersion, IdentityVersion)`).
- Include `changeVersion` in the response payload when change query parameters are supplied.

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
- `G.ChangeVersion` does **not** change, because `G.ContentVersion`/`G.IdentityVersion` did not advance.

This is intentional: DMS avoids write-time fan-out for representation dependency changes and keeps `ChangeVersion` aligned with ODS’s “row changed” semantics.

## Summary of differences vs `DERIVED-TOKEN.md`

- Adds a global `dms.ChangeVersionSequence`.
- Interprets `ContentVersion` and `IdentityVersion` as stamps allocated from that global sequence.
- Defines `ChangeVersion` as `max(ContentVersion, IdentityVersion)` for ODS-style change queries.
- Introduces tracked delete/key-change event tables (future-facing) using the same global sequence.
