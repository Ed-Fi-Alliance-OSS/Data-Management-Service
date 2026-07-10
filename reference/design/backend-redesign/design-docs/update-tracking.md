# Update Tracking: Representation `_etag/_lastModifiedDate` + Change Queries `ChangeVersion`

## Status

Draft. This document is the normative design for:

- serving `_etag` / `_lastModifiedDate` as resource-state-sensitive metadata, and
- enabling Ed-Fi Change Query APIs by stamping the global monotonic `ChangeVersion` that those APIs filter on (the API endpoints themselves are defined in [change-queries.md](change-queries.md)).

## Motivation

The backend redesign needs resource-state-sensitive metadata:

- `_etag` and `_lastModifiedDate` MUST change when the full resource-state representation changes,
  including when referenced identity values change. Readable profile filtering can change the
  response shape, but it does not change the `_etag` surface. Server-generated response decorations
  such as reference `link` objects are not resource state and do not participate in `_etag`
  derivation.
  Descriptor identity/URI is immutable, while descriptor metadata fields are mutable and affect
  only the descriptor resource's own representation.
- Ed-Fi Change Query APIs depend on a global monotonic `ChangeVersion`.

This redesign accomplishes indirect-update semantics without a reverse-edge table by persisting complete referenced
public/lineage vectors alongside each stable target `..._DocumentId` and propagating them through native FK actions.
PostgreSQL assigns fixed actions mechanically; SQL Server globally selects native cascades and exact-carrier covered
`NO ACTION` diamond edges. Provider-independent validation rejects identity cycles. There is no identity-value propagation
trigger; see [mssql-cascading.md](mssql-cascading.md).

Those referrer updates naturally trigger the same stamping rules as “direct” writes.

## Requirements and non-goals

### Requirements

1. **Correctness**: `_etag` and `_lastModifiedDate` MUST change when the full resource-state
   representation changes. Readable profile filtering and response-only decorations such as
   reference `link` objects MUST NOT change `_etag`.
2. **Change Queries alignment**: `ChangeVersion` MUST be a global, monotonically increasing `bigint`.
3. **Cross-engine**: must work on PostgreSQL and SQL Server.
4. **Optimistic concurrency**: `If-Match` must be resource-state-sensitive.
5. **ODS watermark-only compatibility**: `ChangeVersion` MUST be unique per representation change so clients can safely persist only the max `ChangeVersion` as their watermark.

### Non-goals

- **As-of snapshots**: Change Queries return current representations whose `ChangeVersion` falls in the requested window (matching ODS behavior), not historical representations.
- **Lossless event sourcing**: this design does not attempt to return every intermediate update for a document within a window.

## Core concepts

### Representation stamps (served metadata)

Each persisted document maintains a **representation stamp** on `dms.Document`:

- `ContentVersion` (`bigint`, globally monotonic)
- `ContentLastModifiedAt` (UTC timestamp)

Note: `dms.Document` also carries non-stamp metadata used by other subsystems (e.g., `CreatedByOwnershipTokenId` for ownership-based authorization; see `auth.md`). Stamping rules defined in this document are unchanged by those additional columns.

These are the source of truth for:

- API `_lastModifiedDate`
- per-item `ChangeVersion` (for Change Queries)

API `_etag` is derived from the canonical resource-state JSON representation described below.

### Identity stamps (supporting internal semantics)

Each persisted document also maintains an **identity projection stamp**:

- `IdentityVersion`
- `IdentityLastModifiedAt`

These are updated only when the document’s own identity/URI projection changes (including via cascaded updates to identity-component reference identity storage columns; see [key-unification.md](key-unification.md)). Identity stamps are not required to serve `_etag/_lastModifiedDate/ChangeVersion`, but are useful for diagnostics and future features.

### Global sequence

All stamps are allocated from a single global sequence:

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

- `_lastModifiedDate(P) = dms.Document.ContentLastModifiedAt`
- `ChangeVersion(P) = dms.Document.ContentVersion`
- `_etag(P)` is a deterministic hash of the canonical JSON form of the full resource-state
  document, before readable-profile projection. It is a resource-state validator, not a
  response-shape validator:
  - remove server-generated fields `id`, `link`, `_etag`, and `_lastModifiedDate`, recursively
    canonicalize object properties using ordinal string ordering while preserving array order,
    serialize the canonical form as minified UTF-8, compute `SHA-256` over those bytes, and encode
    the hash as base64.
  - readable-profile responses preserve the same full-resource `_etag`; profile filtering changes
    which fields are returned, but not the concurrency validator.
  - `DataManagement:ResourceLinks:Enabled` does not affect `_etag`; `link` subtrees are excluded
    from hashing whether they are present in the response or stripped by the flag.

This design does not compute metadata from dependency scans at read time. Representation changes
are still tracked by stored `ContentVersion`/`ContentLastModifiedAt`, while `_etag` is computed
from the canonical full resource-state document rather than exact transport serializer bytes. When
the response-shape difference is readable-profile filtering or link inclusion/exclusion,
implementations reuse the same `_etag`.

Interaction with `dms.DocumentCache` (when enabled): the cache stores the caller-agnostic pre-profile
document and its full-resource `_etag`/`_lastModifiedDate`. Profile-scoped reads apply readable-profile
projection after cache retrieval and preserve the cached/full-resource `_etag`. The
`DataManagement:ResourceLinks:Enabled` strip pass does not change `_etag`, because `link` is excluded
from canonicalization in both flag states.

## Change Query candidate selection (cross-reference)

Change Query candidate selection is defined in [change-queries.md](change-queries.md). In summary:

- The per-resource `ContentVersion` / `ContentLastModifiedAt` mirror on each `StorageKind = RelationalTables` root and on `dms.Descriptor` is what resource and descriptor `?minChangeVersion=X&maxChangeVersion=Y` reads filter on.
- Per-resource `tracked_changes_<schema>.<resource>` tables and the shared `tracked_changes_edfi.Descriptor` back the `/deletes` and `/keyChanges` endpoints; they are populated by the same `*_Stamp` triggers that stamp `dms.Document` (extended with `DocumentStamping.ChangeTracking`).
- `/availableChangeVersions` is served by `GetMaxChangeVersion` (`"dms"."GetMaxChangeVersion"()` in PostgreSQL, `[dms].[GetMaxChangeVersion]` in SQL Server).

`update-tracking.md` owns the stamping contract on `dms.Document` and how `_etag` / `_lastModifiedDate` are derived. It does not own the SQL or storage shape of candidate selection.

## Optimistic concurrency (`If-Match`)

With stored representation stamps:

- GET returns `_etag` as the deterministic `SHA-256` hash of the current canonical full
  resource-state JSON representation, with readable profile filtering and server-generated response
  decorations such as `link` excluded from ETag-surface selection.
- PUT/DELETE validates `If-Match` by comparing the client’s `_etag` to the current deterministic hash for that `DocumentId`.
- No dependency locking is required for correctness because indirect impacts are realized as local updates that bump the same representation stamp.

## Retention and `oldestChangeVersion`

`oldestChangeVersion` is a floor: a client requesting any window `[min, max]` with `min < oldestChangeVersion` is asking for change history that may no longer be complete and should resync.

**v1 behavior:** `oldestChangeVersion = 0`, matching ODS. DMS does not automate retention pruning of `tracked_changes_*` tables in v1; see [change-queries.md](change-queries.md) §"Operational considerations: tracked-change table volume" for the manual truncation guidance DMS inherits from ODS, and the trade-off (loss of visibility into deletes and key-changes that predate the truncation point).

The resource-table `ContentVersion` mirror is excluded from any retention concept by design. The mirror always reflects current state; there is no retention shape on it, and no `/deletes` / `/keyChanges` semantic it could fail to honor.
