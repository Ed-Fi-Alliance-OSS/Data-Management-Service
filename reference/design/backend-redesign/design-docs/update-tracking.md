# Update Tracking: Representation `_etag/_lastModifiedDate` + Change Queries `ChangeVersion`

## Status

Draft. This document is the normative design for:

- serving `_etag` / `_lastModifiedDate` as resource-state-sensitive metadata, and
- enabling Ed-Fi Change Query APIs by stamping the global monotonic `ChangeVersion` that those APIs filter on (the API endpoints themselves are defined in [change-queries.md](change-queries.md)).

## Motivation

The backend redesign needs resource-state-sensitive metadata:

- `_lastModifiedDate` and `ChangeVersion` MUST change when the full resource-state representation
  changes, including when referenced identity values change.
- `_etag` MUST change whenever the served byte-representation changes. It is derived from
  `ContentVersion` (which tracks resource-state change) **and** a `variantKey` that distinguishes
  the byte-affecting representation selectors (response format/media type, active readable profile,
  `link` mode, and response content coding). Unlike `_lastModifiedDate`/`ChangeVersion`, `_etag` is therefore
  representation-sensitive: two representations of the same stored state that differ in served bytes
  (e.g. different readable profiles, links on vs. off, or identity vs. gzip coding) MUST carry different `_etag` values, as
  required for RFC 9110 §8.8.1 strong validators.
  Descriptor identity/URI is immutable, while descriptor metadata fields are mutable and affect
  only the descriptor resource's own representation.
- Ed-Fi Change Query APIs depend on a global monotonic `ChangeVersion`.

This redesign accomplishes indirect-update semantics without a reverse-edge table by:

- persisting referenced identity values as local columns alongside every `..._DocumentId` reference, and
- using provider-specific full-composite FK actions: PostgreSQL uses `ON UPDATE CASCADE` for abstract or transitively
  mutable concrete targets (otherwise `ON UPDATE NO ACTION`); SQL Server retains native cascades and applies safe
  full-composite `ON UPDATE NO ACTION` convergence cuts under [sql-server-pruning.md](sql-server-pruning.md).

Those referrer updates naturally trigger the same stamping rules as “direct” writes.

## Requirements and non-goals

### Requirements

1. **Correctness**: `_lastModifiedDate` and `ChangeVersion` MUST change when the full
   resource-state representation changes. `_etag` MUST change whenever the served byte-representation
   changes — i.e. on resource-state change **and** on any change to the representation selectors
   (format, readable profile, `link` mode, content coding).
2. **RFC 9110 validator semantics**: `_etag` is served as a strong validator (RFC 9110 §8.8.1),
   unquoted in the `_etag` body field, quoted as an entity-tag in the `ETag` header (RFC 9110
   §8.8.3), and never `W/`-prefixed. `If-Match` uses strong comparison and `If-None-Match` uses weak
   comparison (RFC 9110 §8.8.3.2). Write-side comparisons use the tag's **state-significant
   projection** — `ContentVersion` and `schemaEpoch`; the `format`, `profileCode`, `linkFlag`, and
   `contentCoding` components are excluded (see "ETag preconditions"). Weak validators cannot satisfy `If-Match`
   and are not emitted.
3. **Change Queries alignment**: `ChangeVersion` MUST be a global, monotonically increasing `bigint`.
4. **Cross-engine**: must work on PostgreSQL and SQL Server.
5. **Optimistic concurrency**: `If-Match` must be resource-state-sensitive.
6. **ODS watermark-only compatibility**: `ChangeVersion` MUST be unique per representation change so clients can safely persist only the max `ChangeVersion` as their watermark.

### Non-goals

- **As-of snapshots**: Change Queries return current representations whose `ChangeVersion` falls in the requested window (matching ODS behavior), not historical representations.
- **Lossless event sourcing**: this design does not attempt to return every intermediate update for a document within a window.

## Core concepts

### Representation tracking metadata

Each persisted document maintains representation-tracking metadata on `dms.Document`:

- `ContentVersion` (`bigint`, globally monotonic), the representation stamp
- `ContentLastModifiedAt` (UTC timestamp), whose provider precision is retained in storage
  and whose whole-second UTC formatting supplies `_lastModifiedDate`

Note: `dms.Document` also carries metadata used by other subsystems (e.g., `CreatedByOwnershipTokenId` for ownership-based authorization; see `auth.md`). Update-tracking rules defined in this document are unchanged by those additional columns.

These are the source of truth for:

- API `_lastModifiedDate`
- per-item `ChangeVersion` (for Change Queries)

API `_etag` is derived from `dms.Document.ContentVersion` and the response `variantKey`, as
specified in "Serving API metadata (normative)" below. It is **not** a hash of the resource-state
JSON.

### Identity stamps (supporting internal semantics)

Each persisted document also maintains an **identity projection stamp**:

- `IdentityVersion`
- `IdentityLastModifiedAt`

These are updated only when the document’s own identity/URI projection changes (including via cascaded updates to identity-component reference identity storage columns; see [key-unification.md](key-unification.md)). Identity stamps are not required to serve `_etag/_lastModifiedDate/ChangeVersion`, but are useful for diagnostics and future features.

### Global sequence

All version stamps are allocated from a single global sequence:

- PostgreSQL: `nextval('dms.ChangeVersionSequence')`
- SQL Server: `NEXT VALUE FOR dms.ChangeVersionSequence`

See [data-model.md](data-model.md) for the sequence DDL.

## Stamping rules (normative)

### What counts as a representation change?

A document's full resource-state representation changes when any of the following occur:

- the document’s own persisted scalar/collection content changes (root/child/extension tables), or
- any referenced document’s identity values embedded in the representation change, which is realized as an FK cascade update to the document’s stored reference identity storage columns (canonical under key unification; see [key-unification.md](key-unification.md)).

A successful update request that results in **no persisted row changes** is **not** a representation change. In that
case, `ContentVersion` and `ContentLastModifiedAt` MUST remain unchanged.

### What counts as an identity projection change?

A document’s identity/URI projection changes when any identity component changes, including:

- scalar identity columns on the root table, and
- reference identity values stored alongside identity-component references (because those values participate in the document’s identity projection; canonical under key unification; see [key-unification.md](key-unification.md)).

### Stamp updates

When a document’s **representation changes**, in the same transaction:

- set `dms.Document.ContentVersion = next ChangeVersionSequence value`
- set `dms.Document.ContentLastModifiedAt = now (UTC)`

When a document’s **identity projection changes**, in the same transaction:

- set `dms.Document.IdentityVersion = next ChangeVersionSequence value`
- set `dms.Document.IdentityLastModifiedAt = now (UTC)`
- and also treat it as a representation change (update `ContentVersion`/`ContentLastModifiedAt`)

### Initialization on insert (normative)

Inserting a new document is a representation change (and also an identity projection change). Therefore, on `INSERT` of a new `dms.Document` row, the inserted row MUST have:

- `ContentVersion` set to a newly allocated `dms.ChangeVersionSequence` value (unique per inserted document),
- `IdentityVersion` set to a newly allocated `dms.ChangeVersionSequence` value (unique per inserted document),
- `ContentLastModifiedAt` and `IdentityLastModifiedAt` set to the insert time (UTC).

Implementation options:

- **Column defaults** on `dms.Document` that use the sequence (recommended for cross-engine multi-row inserts), plus update-time triggers for later changes.
- Triggers that stamp inserted rows (ensure they allocate one sequence value per inserted `DocumentId` and do not assign a single statement-level value).
- Explicit values provided by the write path.

A constant default (e.g., `DEFAULT 1`) is not compatible with the uniqueness/monotonicity requirements for `ChangeVersion` and MUST NOT be treated as correct initialization.

Notes:

- Multiple updates to the same `DocumentId` in one transaction may allocate multiple sequence values; the only required property is that the final committed stamps are monotonic and correct.
- FK-cascade updates to reference identity storage columns MUST cause stamping (the database update itself triggers the same table triggers as a direct UPDATE; under key unification, per-site binding aliases recompute automatically).
- A successful no-op update path (request accepted, but no stored/writable row values changed) MUST NOT allocate new content or identity stamps.
- For watermark-only compatibility, allocating stamps must be **per-document**, not “one stamp for the whole statement”:
  - when a trigger stamps N `dms.Document` rows, it MUST allocate N distinct `ChangeVersionSequence` values (one per affected `DocumentId`).
  - SQL Server: do **not** assign `NEXT VALUE FOR dms.ChangeVersionSequence` to a variable and reuse it; use `NEXT VALUE FOR dms.ChangeVersionSequence` directly in the set-based `UPDATE` that stamps `dms.Document` (and dedupe `DocumentId`s so each document is updated once per trigger execution).

## Serving API metadata (normative)

For a document `P`:

- `_lastModifiedDate(P) = formatUtcWholeSeconds(dms.Document.ContentLastModifiedAt)` using
  the existing DMS `yyyy-MM-ddTHH:mm:ssZ` representation; fractional seconds are discarded
  without rounding
- `ChangeVersion(P) = dms.Document.ContentVersion`
- `_etag(P)` is composed, not hashed:

  ```
  _etag-value = ContentVersion(P) "-" variantKey
  ETag header = DQUOTE _etag-value DQUOTE      ; quotes are HTTP framing only
  ```

  - `ContentVersion(P) = dms.Document.ContentVersion`, serialized as an **opaque string** (never
    interpreted numerically by server or client).
  - `variantKey` is the deterministic representation token defined in "`variantKey` encoding"
    below. It makes `_etag` distinct per served byte-representation, satisfying RFC 9110 §8.8.1
    strong-validator semantics.
  - `_etag` MUST be computed with **no document hashing and no representation readback**: it is a
    string composition of the stored `ContentVersion` counter and precomputed representation tokens.
    Obtaining the counter itself may cost a single lightweight scalar `ContentVersion` read on the
    write path — read after every table mutation, in the persistence layer, because child-table stamp
    triggers can bump the root document's `ContentVersion` after its own INSERT (see the ContentVersion
    ADR's 2026-07-08 final-`ContentVersion`-read amendment). That scalar read is deliberately *not* the
    hydrate-materialize-hash readback of the document that this design eliminates.

This design does not compute `_etag` from the document body or from dependency scans at read time.
Representation-state change is tracked by stored `ContentVersion`; `ContentLastModifiedAt` supplies
the representation's `_lastModifiedDate` payload metadata. `_etag` additionally reflects the
representation selectors via `variantKey`.

`dms.DocumentCache` stores the `ContentVersion` needed by this document's `_etag`
composition rules, but `DocumentJson` does not contain a reusable `_etag`. The row also
stores a separate opaque `StreamEtag`, produced through the same served-ETag composer for
the fixed CDC representation. API reads ignore `StreamEtag` and compose their
request-specific validator from `ContentVersion` and the active request `variantKey`.
The v1 stream has no projection generation. `StreamEtag` is opaque and its exact bytes are
not independently frozen. Every compatible materialization or ETag correction that changes
public bytes uses the explicitly offline representation-restamp utility to advance canonical
`ContentVersion`; ordinary projection then publishes a higher-version record in the existing
topic. Equal-version records are byte-identical duplicates and do not replace consumer
state. A change to the public key, required field names or types, delete semantics, or
document contract uses a new versioned topic after complete reprojection. See the
topic/message ADR's
[compatibility rule](cdc/0002-kafka-topic-and-message-contract.md#v1-compatibility-and-corrective-republishes).
The cache projection and freshness behavior is defined in
[Relational CDC and Document Projection](../../cdc-streaming.md#freshness-and-reconciliation).

### `variantKey` encoding (normative)

`variantKey` is a dot-delimited, fixed-order, lowercase ASCII token of five always-present
components. All characters are drawn from `[a-z0-9_]` plus the `.` separator — all valid `etagc`
characters (RFC 9110 §8.8.3); it contains no `"` or `\`.

```
variantKey = schemaEpoch "." format "." profileCode "." linkFlag "." contentCoding
```

1. **`schemaEpoch`** — the first 8 lowercase hex characters of the in-force `EffectiveSchemaHash`.
   Captures every rendering input that is not the document state itself (resource field set/ordering
   and all profile *definitions*). A schema or profile-definition change rotates this segment,
   correctly invalidating prior `_etag` values whose bytes are no longer reproducible.
2. **`format`** — a stable code for the response media type from a fixed server-side registry
   (`j` = `application/json` today; reserve e.g. `x` for XML). MUST NOT be derived from the raw
   media-type string at runtime.
3. **`profileCode`** — `_` when no readable profile applies; otherwise the first 8 lowercase hex
   characters of `SHA-256(UTF-8(profileName))` — a hash of the readable profile's *name*, never the
   document. This hashes only the tiny, static profile-name descriptor, so it upholds the
   no-representation-hash rule; it is deterministic and stable across processes and engines, needs no
   profile catalog to enumerate, and a profile-*definition* change still rotates `schemaEpoch`. (See
   the ContentVersion ADR's 2026-07-08 `profileCode`-hash amendment; the earlier "compile-time index"
   form was never implemented because a `MappingSet` exposes no enumerable profile catalog.)
4. **`linkFlag`** — `l` when `DataManagement:ResourceLinks:Enabled` is true, `n` when false.
5. **`contentCoding`** — a stable code for the selected HTTP response content coding: `i` =
   identity, `b` = Brotli (`br`), and `g` = gzip. Selection comes from the registered ASP.NET Core
   response-compression provider; any additional provider requires a registered stable code.

Examples: identity `_etag` body value `5-a1b2c3d4.j._.l.i`, header
`"5-a1b2c3d4.j._.l.i"`; gzip `_etag` body value `5-a1b2c3d4.j._.l.g`, header
`"5-a1b2c3d4.j._.l.g"`.

The server MUST recompute the full `_etag` deterministically from request context (negotiated
format, profile in effect, `link` mode, and content coding) plus the loaded schema at read response
and write response, with **no document hashing and no representation readback**. Write responses
use the identity-coding variant because they carry no encoded resource representation. The
only database access the tag requires is obtaining the stored `ContentVersion` counter it composes
over — already loaded with the row on the read path, a single lightweight scalar read in the
persistence layer on the write path (see "Serving API metadata"), and the locked-row read for a
write precondition — never a hydrate-materialize-hash readback of the document. Conditional-read
`If-None-Match` compares the full served tag; write-side `If-Match` and `If-None-Match` compare only
the **state-significant projection** (`ContentVersion` and `schemaEpoch`; `format`, `profileCode`,
`linkFlag`, and `contentCoding` excluded) — see "ETag preconditions".

## Change Query candidate selection (cross-reference)

Change Query candidate selection is defined in [change-queries.md](change-queries.md). In summary:

- The per-resource `ContentVersion` / `ContentLastModifiedAt` mirror on each `StorageKind = RelationalTables` root and on `dms.Descriptor` is what resource and descriptor `?minChangeVersion=X&maxChangeVersion=Y` reads filter on.
- Per-resource `tracked_changes_<schema>.<resource>` tables and the shared `tracked_changes_edfi.Descriptor` back the `/deletes` and `/keyChanges` endpoints; they are populated by the same `*_Stamp` triggers that stamp `dms.Document` (extended with `DocumentStamping.ChangeTracking`).
- `/availableChangeVersions` is served by `GetMaxChangeVersion` (`"dms"."GetMaxChangeVersion"()` in PostgreSQL, `[dms].[GetMaxChangeVersion]` in SQL Server).

`update-tracking.md` owns the stamping contract on `dms.Document` and how `_etag` / `_lastModifiedDate` are derived. It does not own the SQL or storage shape of candidate selection.

## ETag preconditions (`If-Match` and `If-None-Match`)

With stored representation stamps:

Comparison basis summary:

| Surface | Comparison function | `ContentVersion` | `schemaEpoch` | `format` | `profileCode` | `linkFlag` | `contentCoding` | Failure / match result |
|---|---|---|---|---|---|---|---|---|
| Served `_etag` / `ETag` emission | N/A; compose full strong validator | Included | Included | Included | Included | Included | Included | Distinct tag per served byte-representation |
| Conditional GET `If-None-Match` | RFC weak comparison against full served tag | Significant | Significant | Significant | Significant | Significant | Significant | Any match returns `304` |
| Write `If-Match` | RFC strong comparison over state-significant projection | Significant | Significant | Ignored | Ignored | Ignored | Ignored | Mismatch returns `412` |
| Write `If-None-Match` | RFC weak comparison over state-significant projection | Significant | Significant | Ignored | Ignored | Ignored | Ignored | Any match returns `412` |
| Bare `If-Match: *` | Existence precondition | Not compared | Not compared | Not compared | Not compared | Not compared | Not compared | Missing current representation returns `412` |
| Bare `If-None-Match: *` | Non-existence precondition | Not compared | Not compared | Not compared | Not compared | Not compared | Not compared | Existing current representation returns `412` |

- GET returns `_etag` as `"{ContentVersion}-{variantKey}"` for the representation actually served
  (see "Serving API metadata"). It is a strong validator under RFC 9110 §8.8.1.
- Conditional GET evaluates `If-None-Match` only after authorization and other normal request checks
  would permit a successful response. It uses RFC 9110 §8.8.3.2 weak comparison against the **full
  served tag**, including `format`, `profileCode`, `linkFlag`, and `contentCoding`; any matching list
  member returns `304 Not Modified` with the current `ETag`, as specified by RFC 9110 §13.1.2.
  When response compression is enabled, ETag-bearing `200` and `304` responses include
  `Vary: Accept-Encoding`.
- PUT/DELETE validates `If-Match` using strong comparison over the tag's **state-significant
  projection**. The backend reads the current `ContentVersion` and composes the expected tag from
  the request's `variantKey`, then compares it to the client's `If-Match` while **ignoring the
  `format`, `profileCode`, `linkFlag`, and `contentCoding` components** — these encode only how the
  representation is rendered, filtered, or transferred, never resource state. The compared components
  are `ContentVersion` and `schemaEpoch`. `profileCode` is **not** significant (amended 2026-07-04):
  a profiled or cross-profile `If-Match` matches whenever `ContentVersion` and `schemaEpoch` agree,
  matching legacy ODS/API behavior — a readable profile filters the response body but does not alter
  the resource-state concurrency validator. A mismatch on any compared component returns `412`. (The
  served `ETag` still carries the full `variantKey`; only the write-time comparison is projected, so
  conditional-GET / `If-None-Match` caching stays byte-correct.) A client presents the `_etag` it
  obtained for the representation it is acting on.
- POST/PUT write-side `If-None-Match` uses RFC 9110 §8.8.3.2 weak comparison over the same
  state-significant projection. Any matching supplied tag returns `412 Precondition Failed`; a
  non-match proceeds through the normal write path. This deliberately differs from conditional GET,
  where all representation components remain significant.
- A bare, unquoted `If-Match: *` is not an opaque tag but an RFC 9110 §13.1.1 wildcard existence
  precondition (amended 2026-07-05): it is satisfied whenever a current representation of the target
  exists (any `ContentVersion`, no projection comparison) and returns `412` when none exists. For
  PUT and DELETE this is the one case where a missing target returns `412` instead of `404`; a POST
  upsert that resolves to an insert (no current representation) likewise returns `412`. Only the
  bare, unquoted `*` is the wildcard — a quoted `"*"` is treated as an ordinary opaque tag.
- A bare, unquoted `If-None-Match: *` is the inverse RFC 9110 §13.1.2 existence precondition: it
  returns `412` for a POST/PUT target that exists and permits a create when the target is absent. A
  quoted `"*"` is an ordinary opaque tag.
- On input the server accepts an unquoted `If-Match` value as equivalent to the same value quoted
  (amended 2026-07-05, for legacy ODS/API compatibility). Emitted `ETag` headers remain quoted and
  `W/` weak tags remain rejected by `If-Match`; `If-None-Match` accepts them for weak comparison.
- When both headers are present, `If-Match` takes precedence and `If-None-Match` is ignored, following
  RFC 9110 §13.2.2.
- No dependency locking is required for correctness because indirect impacts are realized as local updates that bump the same representation stamp.

## Retention and `oldestChangeVersion`

`oldestChangeVersion` is a floor: a client requesting any window `[min, max]` with `min < oldestChangeVersion` is asking for change history that may no longer be complete and should resync.

**v1 behavior:** `oldestChangeVersion = 0`, matching ODS. DMS does not automate retention pruning of `tracked_changes_*` tables in v1; see [change-queries.md](change-queries.md) §"Operational considerations: tracked-change table volume" for the manual truncation guidance DMS inherits from ODS, and the trade-off (loss of visibility into deletes and key-changes that predate the truncation point).

The resource-table `ContentVersion` mirror is excluded from any retention concept by design. The mirror always reflects current state; there is no retention shape on it, and no `/deletes` / `/keyChanges` semantic it could fail to honor.
