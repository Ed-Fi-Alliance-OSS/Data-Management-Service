# Update Tracking: Representation `_etag/_lastModifiedDate` + Change Queries `ChangeVersion`

## Status

Draft. This document is the normative design for:
- serving `_etag` / `_lastModifiedDate` as representation-sensitive metadata, and
- enabling future Ed-Fi Change Query APIs (`ChangeVersion`).

## Motivation

The backend redesign needs representation-sensitive metadata:
- `_etag` and `_lastModifiedDate` MUST change when the returned resource representation changes, including when referenced identity values change (descriptors are treated as immutable in this redesign).
- Ed-Fi Change Query APIs depend on a global monotonic `ChangeVersion`.

This redesign accomplishes indirect-update semantics without a reverse-edge table by:
- persisting referenced identity values as local columns alongside every `..._DocumentId` reference, and
- using `ON UPDATE CASCADE` (or trigger-based propagation where required) only when the referenced target allows identity updates (`allowIdentityUpdates=true`); otherwise `ON UPDATE NO ACTION`.

Those referrer updates naturally trigger the same stamping rules as “direct” writes.

## Requirements and non-goals

### Requirements

1. **Correctness**: `_etag` and `_lastModifiedDate` MUST change when the served representation changes.
2. **Change Queries alignment**: `ChangeVersion` MUST be a global, monotonically increasing `bigint`.
3. **Cross-engine**: must work on PostgreSQL and SQL Server.
4. **Optimistic concurrency**: `If-Match` must be representation-sensitive.
5. **ODS watermark-only compatibility**: `ChangeVersion` MUST be unique per representation change so clients can safely persist only the max `ChangeVersion` as their watermark.

### Non-goals

- **As-of snapshots**: Change Queries return current representations whose `ChangeVersion` falls in the requested window (matching ODS behavior), not historical representations.
- **Lossless event sourcing**: this design does not attempt to return every intermediate update for a document within a window.

## Core concepts

### Representation stamps (served metadata)

Each persisted document maintains a **representation stamp** on `dms.Document`:
- `ContentVersion` (`bigint`, globally monotonic)
- `ContentLastModifiedAt` (UTC timestamp)

These are the source of truth for:
- API `_etag`
- API `_lastModifiedDate`
- per-item `ChangeVersion` (for Change Queries)

### Identity stamps (supporting internal semantics)

Each persisted document also maintains an **identity projection stamp**:
- `IdentityVersion`
- `IdentityLastModifiedAt`

These are updated only when the document’s own identity/URI projection changes (including via cascaded updates to identity-component propagated identity columns). Identity stamps are not required to serve `_etag/_lastModifiedDate/ChangeVersion`, but are useful for diagnostics and future features.

### Global sequence

All stamps are allocated from a single global sequence:
- PostgreSQL: `nextval('dms.ChangeVersionSequence')`
- SQL Server: `NEXT VALUE FOR dms.ChangeVersionSequence`

See [data-model.md](data-model.md) for the sequence DDL.

## Stamping rules (normative)

### What counts as a representation change?

A document’s served representation changes when any of the following occur:
- the document’s own persisted scalar/collection content changes (root/child/extension tables), or
- any referenced document’s identity values embedded in the representation change, which is realized as an FK cascade update to the document’s stored propagated identity columns.

### What counts as an identity projection change?

A document’s identity/URI projection changes when any identity component changes, including:
- scalar identity columns on the root table, and
- propagated identity columns for identity-component references (because those values participate in the document’s identity projection).

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
- FK-cascade updates to propagated identity columns MUST cause stamping (the database update itself triggers the same table triggers as a direct UPDATE).
- For watermark-only compatibility, allocating stamps must be **per-document**, not “one stamp for the whole statement”:
  - when a trigger stamps N `dms.Document` rows, it MUST allocate N distinct `ChangeVersionSequence` values (one per affected `DocumentId`).
  - SQL Server: do **not** assign `NEXT VALUE FOR dms.ChangeVersionSequence` to a variable and reuse it; use `NEXT VALUE FOR dms.ChangeVersionSequence` directly in the set-based `UPDATE` that stamps `dms.Document` (and dedupe `DocumentId`s so each document is updated once per trigger execution).

## Serving API metadata (normative)

For a document `P`:
- `_lastModifiedDate(P) = dms.Document.ContentLastModifiedAt`
- `ChangeVersion(P) = dms.Document.ContentVersion`
- `_etag(P)` is derived from the same representation stamp:
  - recommended: encode `ContentVersion` as an opaque string (e.g., `W/"{ContentVersion}"` or base64 of the 8-byte value).

This design does not compute any metadata from dependency scans at read time.

## Journaling for Change Queries

Change Queries need an efficient way to find “documents of resource R whose current `ChangeVersion` is in `[min,max]`” without scanning all documents.

This redesign uses one journal:
- `dms.DocumentChangeEvent` — append-only, representation changes only

See [data-model.md](data-model.md) for the table DDL and recommended indexes.

### Journal emission (normative)

Whenever `dms.Document.ContentVersion` changes (including insert), emit one journal row:
- `ChangeVersion = dms.Document.ContentVersion`
- `DocumentId`
- `ResourceKeyId`
- `CreatedAt = now (UTC)`

Recommended implementation: triggers on `dms.Document` that insert into `dms.DocumentChangeEvent` on `INSERT` and on `UPDATE OF ContentVersion`.

### Candidate selection (“journal + verify”)

Because `dms.DocumentChangeEvent` is append-only, there may be multiple rows for one `DocumentId` across time windows. Change Queries must return only documents whose **current** `ChangeVersion` is in the requested window.

The selection algorithm is:

1. Read candidate rows from `dms.DocumentChangeEvent` for the resource type and `[min,max]` window, ordered by `(ChangeVersion, DocumentId)` with cursor paging.
2. Verify candidates by joining to `dms.Document` and keeping only rows where:
   - `dms.Document.ContentVersion = dms.DocumentChangeEvent.ChangeVersion`
3. Fetch/reconstitute and return the **current** representations for the resulting `DocumentId`s (the stamps already satisfy the window).

In practice, step (1) may produce stale candidates (documents that changed again later); step (2) filters them out. If a page is underfilled after verification, the server continues reading more candidates until the page is full or the window is exhausted.

### Example candidate query (PostgreSQL sketch)

```sql
WITH candidates AS (
  SELECT e.ChangeVersion, e.DocumentId
  FROM dms.DocumentChangeEvent e
  WHERE e.ResourceKeyId = @ResourceKeyId
    AND e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
    AND (
      e.ChangeVersion > @AfterChangeVersion OR
      (e.ChangeVersion = @AfterChangeVersion AND e.DocumentId > @AfterDocumentId)
    )
  ORDER BY e.ChangeVersion, e.DocumentId
  LIMIT @CandidateLimit
)
SELECT c.ChangeVersion, c.DocumentId
FROM candidates c
JOIN dms.Document d
  ON d.DocumentId = c.DocumentId
 AND d.ContentVersion = c.ChangeVersion
ORDER BY c.ChangeVersion, c.DocumentId;
```

### Example candidate query (SQL Server sketch)

```sql
WITH candidates AS (
  SELECT TOP (@CandidateLimit) e.ChangeVersion, e.DocumentId
  FROM dms.DocumentChangeEvent e
  WHERE e.ResourceKeyId = @ResourceKeyId
    AND e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
    AND (
      e.ChangeVersion > @AfterChangeVersion OR
      (e.ChangeVersion = @AfterChangeVersion AND e.DocumentId > @AfterDocumentId)
    )
  ORDER BY e.ChangeVersion, e.DocumentId
)
SELECT c.ChangeVersion, c.DocumentId
FROM candidates c
JOIN dms.Document d
  ON d.DocumentId = c.DocumentId
 AND d.ContentVersion = c.ChangeVersion
ORDER BY c.ChangeVersion, c.DocumentId;
```

## Optimistic concurrency (`If-Match`)

With stored representation stamps:
- GET returns `_etag` derived from `dms.Document.ContentVersion`.
- PUT/DELETE validates `If-Match` by comparing the client’s `_etag` to the current stored stamp for that `DocumentId`.
- No dependency locking is required for correctness because indirect impacts are realized as local updates that bump the same representation stamp.

## Retention and `oldestChangeVersion`

Journals require retention planning once Change Queries are used in production.

Guidance:
- Retention policy should be defined per instance (time-based and/or size-based).
- Expose `oldestChangeVersion` as the minimum retained `ChangeVersion` across the tracking tables that the Change Query API depends on (for v1: `dms.DocumentChangeEvent`).
- When a client requests a window older than `oldestChangeVersion`, return a clear error instructing the client to resync.
