# Update Tracking: Representation `_etag/_lastModifiedDate` + Change Queries `ChangeVersion`

## Status

Draft. This document is the unified “update tracking” design that combines:

- derived representation metadata (`_etag` / `_lastModifiedDate`) and
- journal-driven Change Queries (`ChangeVersion`).

## Motivation

The backend redesign needs representation-sensitive metadata:

- `_etag` and `_lastModifiedDate` MUST change when the returned resource representation changes, including when referenced identity values change (descriptor rows are treated as immutable in this redesign).
- DMS is also expected to implement Ed-Fi “Change Query” APIs in the future, which depend on a global monotonic `ChangeVersion` (also representation-sensitive in current ODS behavior).

The earlier redesign draft in `reference/design/backend-redesign/transactions-and-concurrency.md` achieves representation sensitivity by:

1. computing an impacted set (`CacheTargets = IdentityClosure + 1-hop referrers`), and
2. performing write-time fan-out: `UPDATE dms.Document SET Etag=Etag+1, LastModifiedAt=... WHERE DocumentId IN CacheTargets`,
3. requiring phantom-safe impacted-set computation, driving SERIALIZABLE edge scans / key-range locks.

This design keeps strict identity correctness (the hard requirement) but avoids write-time fan-out for representation metadata by shifting representation tracking to:

1. **stable per-document local tokens** (`ContentVersion/IdentityVersion` and timestamps),
2. **read-time derivation** for `_etag/_lastModifiedDate` and per-item `ChangeVersion`,
3. **small append-only journals** to make change-query selection efficient without reintroducing fan-out.

Compatibility note: recent Ed-Fi ODS/API versions bump ETag/LastModifiedDate (and ChangeVersion for Change Queries) on indirect representation changes. This design targets the same externally visible semantics without write-time fan-out to all impacted aggregate roots.

## Requirements and non-goals

### Requirements

1. **Correctness**: `_etag` and `_lastModifiedDate` MUST change when the representation changes.
2. **Best-effort minimization**: `_etag/_lastModifiedDate` MAY change even if the representation does not change, but the system should make a best effort to avoid unnecessary changes.
3. **Change Queries alignment**: `ChangeVersion` MUST be a global, monotonically increasing `bigint` suitable for change-query windows and deterministic paging.
4. **Cross-engine**: must work on PostgreSQL and SQL Server.
5. **Optimistic concurrency**: `If-Match` should be representation-sensitive (identity/URI changes can cause `If-Match` failures).
6. **No SERIALIZABLE dependency scans**: representation tracking must not require SERIALIZABLE scans of `dms.ReferenceEdge` to be correct.

### Non-goals

- **As-of snapshots**: Change Queries are not “read the database as it was at maxChangeVersion”; they return current representations whose derived `ChangeVersion` falls in the requested window (matching ODS behavior).
- **Lossless event sourcing**: this design is not trying to return every intermediate update within a window.

## Core concepts

### Representation dependencies (1 hop)

For a document `P` (parent), the API representation embeds values from references:

- **Resource references** embed identity projection values of referenced resource documents (e.g., `studentReference.studentUniqueId`).
- **Descriptor references** embed the descriptor URI string. In this redesign, descriptors are treated as immutable reference data, so descriptor rows do not participate in representation-change cascades and are excluded from dependency tracking (`dms.ReferenceEdge`, derived tokens).

So `P`’s representation depends on the **identity projection** of the non-descriptor documents it references in its JSON representation.

Important: representation dependencies are **1 hop** (the referenced documents themselves). DMS does *not* need multi-hop “fan-out” for representation tracking because:

- transitive identity-component effects are captured by the referenced document’s own `IdentityVersion` via strict identity-closure recompute (see below), and
- the referencing document derives representation metadata from the referenced document’s `IdentityVersion`.

### Local tokens (per document)

Each persisted document maintains two local tokens plus timestamps:

1. **Content token** (local): changes when the document’s own persisted relational content changes.
2. **Identity token** (local): changes when the document’s identity/URI projection changes (values embedded in `{resource}Reference` objects or descriptor `uri`).

Persisted columns:

- `ContentVersion` and `ContentLastModifiedAt`
- `IdentityVersion` and `IdentityLastModifiedAt`

These columns are updated on write and during strict identity closure recompute.

### Global change stamps

To support Ed-Fi Change Queries, `ContentVersion` and `IdentityVersion` are treated as **globally comparable monotonic stamps**, allocated from a single sequence:

- PostgreSQL: `nextval('dms.ChangeVersionSequence')`
- SQL Server: `NEXT VALUE FOR dms.ChangeVersionSequence`

This is compatible with derived `_etag/_lastModifiedDate` and enables a derived `ChangeVersion`.

### Change journals (append-only)

Change Queries need to select “documents whose derived `ChangeVersion` is in `[min,max]`”.

Because `ChangeVersion` is derived (read-time), we add small journals that record *contributors* that are known at write time:

- `dms.DocumentChangeEvent`: local representation-affecting changes for a document (content and/or identity projection changed).
- `dms.IdentityChangeEvent`: identity/URI changes for a document (identity projection changed).

The read-side uses `dms.ReferenceEdge` to expand identity changes into impacted parent documents, then computes/filters derived `ChangeVersion` only for candidates.

## Data model changes (conceptual)

### 1) Global sequence: `dms.ChangeVersionSequence`

**PostgreSQL**

```sql
CREATE SEQUENCE dms.ChangeVersionSequence AS bigint START WITH 1 INCREMENT BY 1;
```

**SQL Server**

```sql
CREATE SEQUENCE dms.ChangeVersionSequence
    AS bigint
    START WITH 1
    INCREMENT BY 1;
```

### 2) `dms.Document` token columns

Add the derived-token columns to `dms.Document` (sketch; not final DDL):

**PostgreSQL-ish**

```sql
ALTER TABLE dms.Document
  ADD COLUMN ContentVersion bigint NOT NULL DEFAULT 1,
  ADD COLUMN IdentityVersion bigint NOT NULL DEFAULT 1,
  ADD COLUMN ContentLastModifiedAt timestamp with time zone NOT NULL DEFAULT now(),
  ADD COLUMN IdentityLastModifiedAt timestamp with time zone NOT NULL DEFAULT now();
```

Notes:

- `ContentVersion`/`IdentityVersion` are **global stamps** (not “per-row counters”).
- Best-effort minimization: only stamp when an actual change is detected.
- If Change Queries are not enabled, these could be hashes; but this unified design assumes Change Queries support, so they are numeric stamps.

### 3) `dms.ReferenceEdge` (required for change queries)

Change Queries need:

1. “who references changed dependency X?” (reverse lookup), and
2. “what dependencies does parent P reference?” (to compute `max(deps.IdentityVersion)`).

`dms.ReferenceEdge` provides both, via:

- a reverse index on `ChildDocumentId` (existing),
- and the primary key on `(ParentDocumentId, ChildDocumentId)` (supports scanning children per parent).

The `IsIdentityComponent` flag is used for strict identity closure recompute, but **is not used** for representation dependencies (representation depends on all references, not just identity components).

Coverage is correctness-critical: DMS must record **all** outgoing non-descriptor resource references (including nested collections) or change queries and identity closure recompute can become incorrect.

See `reference/design/backend-redesign/data-model.md` for the full `dms.ReferenceEdge` definition and its indexes.

### 3a) `dms.IdentityLock` (lock orchestration)

This design assumes the existing strict-identity locking approach from the redesign (a stable per-document row to lock), as described in `reference/design/backend-redesign/data-model.md` and `reference/design/backend-redesign/transactions-and-concurrency.md`.

Conceptual DDL:

```sql
CREATE TABLE dms.IdentityLock (
    DocumentId bigint NOT NULL PRIMARY KEY
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE
);
```

The derived-token `If-Match` algorithm uses shared locks on these rows to stabilize dependency identity tokens during the concurrency check.

### 4) `dms.DocumentChangeEvent` (local changes)

Records local representation-affecting changes of the document itself.

**PostgreSQL**

```sql
CREATE TABLE dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    ResourceKeyId smallint NOT NULL REFERENCES dms.ResourceKey (ResourceKeyId),
    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT PK_DocumentChangeEvent PRIMARY KEY (ChangeVersion, DocumentId)
);

CREATE INDEX IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion
    ON dms.DocumentChangeEvent (ResourceKeyId, ChangeVersion, DocumentId);
```

**SQL Server**

```sql
CREATE TABLE dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL,
    ResourceKeyId smallint NOT NULL,
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_DocumentChangeEvent_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_DocumentChangeEvent PRIMARY KEY CLUSTERED (ChangeVersion, DocumentId),
    CONSTRAINT FK_DocumentChangeEvent_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT FK_DocumentChangeEvent_ResourceKey FOREIGN KEY (ResourceKeyId)
        REFERENCES dms.ResourceKey (ResourceKeyId)
);

CREATE INDEX IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion
    ON dms.DocumentChangeEvent (ResourceKeyId, ChangeVersion, DocumentId);
```

### 5) `dms.IdentityChangeEvent` (identity changes)

Records identity projection changes. Used by change queries to find indirectly impacted parents via `dms.ReferenceEdge`.

**PostgreSQL**

```sql
CREATE TABLE dms.IdentityChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT PK_IdentityChangeEvent PRIMARY KEY (ChangeVersion, DocumentId)
);
```

**SQL Server**

```sql
CREATE TABLE dms.IdentityChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL,
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_IdentityChangeEvent_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_IdentityChangeEvent PRIMARY KEY CLUSTERED (ChangeVersion, DocumentId),
    CONSTRAINT FK_IdentityChangeEvent_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE
);
```

## Derived API metadata (read-time)

Representation dependencies: `SELECT ChildDocumentId FROM dms.ReferenceEdge WHERE ParentDocumentId=@P` (do not filter on `IsIdentityComponent`)

### Derived `_etag`

For document `P`, collect:

- `P.ContentVersion`, `P.IdentityVersion`
- For each representation dependency `D` of `P`: `(D.DocumentId, D.IdentityVersion)`

Then:

```text
P._etag = Base64(SHA-256( EncodeV1(
  P.ContentVersion,
  P.IdentityVersion,
  Sorted( for each dependency D: (D.DocumentId, D.IdentityVersion) )
)))
```

Properties:

- Local content change ⇒ `ContentVersion` changes ⇒ `_etag` changes.
- Local identity projection change ⇒ `IdentityVersion` changes ⇒ `_etag` changes.
- Upstream identity change ⇒ dependency `IdentityVersion` changes ⇒ `_etag` changes.
- No cross-document writes are required to make a referencing document’s `_etag` change.

#### Example: `_etag` input set (stable ordering)

Assume document `P` has:

- `P.ContentVersion = 42`
- `P.IdentityVersion = 40`

And `P` has two representation dependencies, observed in any order:

- `D1 = (DocumentId=2001, IdentityVersion=7)`
- `D2 = (DocumentId=3001, IdentityVersion=11)`

Before hashing, sort dependencies by `DocumentId`:

```text
Sorted deps = [(2001, 7), (3001, 11)]
```

Then the `_etag` input tuple is:

```text
P._etag = Base64(SHA-256(EncodeV1(
  42,
  40,
  [(2001, 7), (3001, 11)]
)))
```

### Derived `_lastModifiedDate`

Representation last modified is the maximum of:

- `P.ContentLastModifiedAt`
- `P.IdentityLastModifiedAt`
- and each dependency’s `IdentityLastModifiedAt`

```text
P._lastModifiedDate = max(
  P.ContentLastModifiedAt,
  P.IdentityLastModifiedAt,
  max(for each dependency D: D.IdentityLastModifiedAt)
)
```

#### Example: `_lastModifiedDate` (max of local + dependency identity timestamps)

Assume:

- `P.ContentLastModifiedAt = 2026-01-05T09:10:00Z`
- `P.IdentityLastModifiedAt = 2026-01-06T18:22:41Z`
- Dependency identity timestamps:
  - `D1.IdentityLastModifiedAt = 2026-01-04T00:00:00Z`
  - `D2.IdentityLastModifiedAt = 2026-01-07T03:00:00Z`

Then:

```text
P._lastModifiedDate = max(
  2026-01-05T09:10:00Z,
  2026-01-06T18:22:41Z,
  max(2026-01-04T00:00:00Z, 2026-01-07T03:00:00Z)
) = 2026-01-07T03:00:00Z
```

### Derived per-item `ChangeVersion`

Define:

```text
LocalChangeVersion(P) = max(P.ContentVersion, P.IdentityVersion)

ChangeVersion(P) = max(
  LocalChangeVersion(P),
  max(for each dependency D: D.IdentityVersion)
)
```

This is computed alongside `_etag/_lastModifiedDate` from the same dependency token reads.

#### Example: `ChangeVersion` (max of local + dependency identity versions)

Assume:

- `P.ContentVersion = 42`
- `P.IdentityVersion = 40`
- Dependency identity versions:
  - `D1.IdentityVersion = 7`
  - `D2.IdentityVersion = 11`

Then:

```text
LocalChangeVersion(P) = max(42, 40) = 42
ChangeVersion(P) = max(42, max(7, 11)) = 42
```

If later `D2.IdentityVersion` becomes `60`, then on the next read:

```text
ChangeVersion(P) = max(42, 60) = 60
```

## Write-side behavior

### Stamping rule (recommended)

Allocate **one** new stamp per document write operation, and apply it to the relevant token columns:

- If local content changed: set `ContentVersion = @stamp`, `ContentLastModifiedAt = now()`.
- If identity projection changed: set `IdentityVersion = @stamp`, `IdentityLastModifiedAt = now()`.
- If both changed, reuse the same `@stamp` for both.

Best-effort minimization: do not allocate a new stamp (and do not insert journal rows) for no-op writes where neither persisted content nor identity projection changes.

### Journal insertion rules

These are the normative rules for “what gets recorded”. Recommended implementation: enforce them with database triggers on `dms.Document` (see below). With triggers enabled, application code should treat the journal tables as derived artifacts and **not** write to them directly (to avoid double entries).

Within the same transaction that updates `dms.Document`:

- If `ContentVersion` or `IdentityVersion` changed:
  - insert one row into `dms.DocumentChangeEvent(ChangeVersion=@LocalChangeVersion, DocumentId=..., resource key...)`.
- If `IdentityVersion` changed (or on insert):
  - insert one row into `dms.IdentityChangeEvent(ChangeVersion=@IdentityVersion, DocumentId=...)`.

`@LocalChangeVersion` is `max(ContentVersion, IdentityVersion)` after the write (typically `@stamp` under the stamping rule).

### Normal writes (POST/PUT, no identity cascade)

Within a write transaction for document `P`:

1. Persist relational rows for `P` (root + children).
2. Maintain `dms.ReferenceEdge` for `P` by diffing outbound reference targets (low-churn: no-op updates write 0 rows).
3. Detect whether persisted content changed (best-effort; e.g., diff-based upsert).
4. Detect whether `P`’s identity projection changed.
5. Allocate stamp(s) per the stamping rule and update `dms.Document` token columns accordingly.
6. Maintain `dms.ReferentialIdentity` for `P` if its identity projection changed.
7. Rely on `dms.Document` triggers to emit journal rows (recommended); otherwise insert per the journal insertion rules.

There is no write-time “find referrers and bump their representation tokens”.

### Identity updates (strict closure recompute)

The redesign still requires strict identity correctness: when a document’s identity projection changes, DMS must transactionally recompute `dms.ReferentialIdentity` for the impacted identity closure.

Under derived tokens + journals:

- the strict closure work remains (identity correctness),
- but “representation metadata fan-out” is removed,
- and identity recompute also bumps `IdentityVersion/IdentityLastModifiedAt` for the documents whose identity projection actually changes.

Concretely, during the closure recompute transaction:

1. Compute + lock `IdentityClosure` (as in the draft).
2. For each impacted document `X` in the closure:
   - recompute the identity projection values used in reference objects and referential-id computation,
   - if values differ from previous values:
     - allocate a stamp `v` and set `X.IdentityVersion = v`, `X.IdentityLastModifiedAt = now()`,
     - update `dms.ReferentialIdentity` rows for `X`,
     - journal emission:
       - rely on `dms.Document` triggers (the `IdentityVersion` update emits both `dms.IdentityChangeEvent` and `dms.DocumentChangeEvent`).

This makes the documents whose identities actually changed “emit” an identity-change stamp; dependents observe the new `IdentityVersion` at read time.

### Required: database triggers (enforcing journal writes)

This redesign requires DB-enforced journaling. Triggers on `dms.Document` insert into the journal tables when token columns change. Application code should treat the journal tables as derived artifacts and **not** write to them directly.

The DDL generator must emit these triggers (and any supporting functions) as part of provisioning.
SQL below is illustrative; the generator output must follow the canonicalization and quoting rules in `ddl-generation.md`.

#### PostgreSQL (generated example; statement-level triggers)

```sql
-- INSERT trigger: journal rows for new documents
CREATE OR REPLACE FUNCTION dms.trg_document_change_events_ins()
RETURNS trigger AS
$$
BEGIN
  INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId)
  SELECT GREATEST(ContentVersion, IdentityVersion), DocumentId, ResourceKeyId
  FROM inserted;

  INSERT INTO dms.IdentityChangeEvent(ChangeVersion, DocumentId)
  SELECT IdentityVersion, DocumentId
  FROM inserted;

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- UPDATE trigger: journal rows only when token columns change
CREATE OR REPLACE FUNCTION dms.trg_document_change_events_upd()
RETURNS trigger AS
$$
BEGIN
  INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId)
  SELECT GREATEST(i.ContentVersion, i.IdentityVersion), i.DocumentId, i.ResourceKeyId
  FROM inserted i
  JOIN deleted d ON d.DocumentId = i.DocumentId
  WHERE i.ContentVersion <> d.ContentVersion
     OR i.IdentityVersion <> d.IdentityVersion;

  INSERT INTO dms.IdentityChangeEvent(ChangeVersion, DocumentId)
  SELECT i.IdentityVersion, i.DocumentId
  FROM inserted i
  JOIN deleted d ON d.DocumentId = i.DocumentId
  WHERE i.IdentityVersion <> d.IdentityVersion;

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS TR_Document_ChangeEvents_Insert ON dms.Document;
CREATE TRIGGER TR_Document_ChangeEvents_Insert
AFTER INSERT ON dms.Document
REFERENCING NEW TABLE AS inserted
FOR EACH STATEMENT
EXECUTE FUNCTION dms.trg_document_change_events_ins();

DROP TRIGGER IF EXISTS TR_Document_ChangeEvents_Update ON dms.Document;
CREATE TRIGGER TR_Document_ChangeEvents_Update
AFTER UPDATE ON dms.Document
REFERENCING NEW TABLE AS inserted OLD TABLE AS deleted
FOR EACH STATEMENT
EXECUTE FUNCTION dms.trg_document_change_events_upd();
```

#### SQL Server (generated example)

```sql
CREATE OR ALTER TRIGGER dms.TR_Document_ChangeEvents
ON dms.Document
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Local changes (content and/or identity): insert one row per affected document
    INSERT INTO dms.DocumentChangeEvent(ChangeVersion, DocumentId, ResourceKeyId)
    SELECT
        CASE WHEN i.ContentVersion > i.IdentityVersion THEN i.ContentVersion ELSE i.IdentityVersion END AS ChangeVersion,
        i.DocumentId,
        i.ResourceKeyId
    FROM inserted i
    LEFT JOIN deleted d ON d.DocumentId = i.DocumentId
    WHERE d.DocumentId IS NULL
       OR i.ContentVersion <> d.ContentVersion
       OR i.IdentityVersion <> d.IdentityVersion;

    -- Identity changes only (includes inserts)
    INSERT INTO dms.IdentityChangeEvent(ChangeVersion, DocumentId)
    SELECT
        i.IdentityVersion AS ChangeVersion,
        i.DocumentId
    FROM inserted i
    LEFT JOIN deleted d ON d.DocumentId = i.DocumentId
    WHERE d.DocumentId IS NULL
       OR i.IdentityVersion <> d.IdentityVersion;
END;
```

## Read-side behavior (GET/query)

### How to find “representation dependencies”

Representation dependencies are the set of referenced **non-descriptor** `DocumentId`s whose identity values are embedded into the returned JSON representation (via resource reference objects). Descriptor URIs are projected into the representation, but descriptor rows are treated as immutable in this redesign and are excluded from dependency tracking.

Practical sources:

1. **From the reconstitution read (cache miss)**:
   - reconstitution already projects reference identities (and descriptor URIs);
   - collect referenced `DocumentId`s for **document references** as you go.
2. **From a dependency projection query (cache hit / metadata-only)**:
   - compile a plan from ApiSchema that `UNION ALL`s all FK columns that represent **document references** (`..._DocumentId`) (root + children) for a given parent `DocumentId`,
   - return distinct dependency `DocumentId`s.
3. **From `dms.ReferenceEdge` (recommended when enabled)**:
   - treat `dms.ReferenceEdge(ParentDocumentId → ChildDocumentId)` as an outbound-dependency index,
   - `SELECT ChildDocumentId FROM dms.ReferenceEdge WHERE ParentDocumentId=@P`.

This design already needs `dms.ReferenceEdge` for change queries, so option (3) is typically the simplest for cache hits and `If-Match` checks.

### GET by id (cache miss: reconstitute)

1. Reconstitute JSON from relational tables.
2. While projecting reference identities, also collect dependency `DocumentId`s (document references only).
3. Batch load dependency tokens:
   - `SELECT DocumentId, IdentityVersion, IdentityLastModifiedAt FROM dms.Document WHERE DocumentId IN (...)`
4. Compute:
   - derived `_etag`
   - derived `_lastModifiedDate`
   - derived `ChangeVersion` (per-item)
5. Inject into the response.

### Query paging

For query responses returning many documents:

1. Materialize a page of documents (cache or reconstitute).
2. Extract and dedupe dependency `DocumentId`s across the page.
3. Batch load dependency tokens.
4. Compute derived tokens per document in memory.

## Optimistic concurrency (`If-Match`)

Derived `_etag` removes the single stored representation-etag row that the original draft used for cheap conditional updates.

To preserve representation-sensitive `If-Match` semantics, recommended algorithm for update/delete of `P` when `If-Match` is present:

1. Determine outbound dependency `DocumentId`s for `P` (dependency projection query or `dms.ReferenceEdge`).
2. Acquire shared identity locks on `dms.IdentityLock(DocumentId)` for dependencies (and optionally `P`), in ascending `DocumentId`.
3. Compute current derived `_etag` by reading `P.ContentVersion`, `P.IdentityVersion`, and dependencies’ `IdentityVersion`.
4. Compare to `If-Match`. If mismatch, fail.
5. Proceed with the write using the existing identity-correctness lock ordering.

This avoids SERIALIZABLE edge scans while keeping `If-Match` representation-sensitive.

### Simpler (weaker) alternative: no dependency locks

If DMS is willing to accept a narrow race where a dependency identity changes between the `If-Match` check and commit, the shared identity locks can be omitted.

This reduces locking but weakens the intended “representation-sensitive If-Match” guarantee under concurrency. If this is considered acceptable, it should be explicitly documented and tested.

## Change Queries (future API)

This section covers the selection problem for “what changed since X?”.

### `availableChangeVersions`

At minimum (like ODS), return the current sequence value as `newestChangeVersion`:

**PostgreSQL**

```sql
SELECT last_value AS NewestChangeVersion
FROM dms.ChangeVersionSequence;
```

**SQL Server**

```sql
SELECT CAST(current_value AS bigint) AS NewestChangeVersion
FROM sys.sequences
WHERE name = 'ChangeVersionSequence' AND SCHEMA_NAME(schema_id) = 'dms';
```

Optionally, compute `oldestChangeVersion` from journal retention (see below).

### Expected cost profile

The query work is proportional to “what changed in the window”, not to the total size of `dms.Document`:

- direct work: `O(#DocumentChangeEvent rows in window for R)`
- indirect expansion work: `O(#IdentityChangeEvent rows in window + #ReferenceEdge rows reachable from those changed dependencies)`
- verification work (often dominant): `O(#ReferenceEdge rows reachable from candidate parents)` to compute `max(child.IdentityVersion)` per candidate parent

If a dependency has very high fan-in (many parents reference it), the indirect work is inherently large. ODS pays this cost at write-time via fan-out; this design pays it at read-time.

### Indexing requirements (recommended)

The journal-driven query shape assumes:

- `dms.DocumentChangeEvent`:
  - `PK (ChangeVersion, DocumentId)`
  - `IX (ResourceKeyId, ChangeVersion, DocumentId)`
- `dms.IdentityChangeEvent`:
  - `PK (ChangeVersion, DocumentId)`
- `dms.ReferenceEdge`:
  - `PK (ParentDocumentId, ChildDocumentId)` (scan children by parent)
  - an index supporting `ChildDocumentId → ParentDocumentId` expansion (e.g., `IX_ReferenceEdge_ChildDocumentId` from `reference/design/backend-redesign/data-model.md`)

On SQL Server, keep `dms.Document` access by `DocumentId` cheap (ideally clustered/PK on `DocumentId`) because `dep_max` joins many child `DocumentId`s to read `IdentityVersion`.

### Resource change query algorithm (high level)

Given resource key `R = (ResourceKeyId)` and window `[min,max]`:

1. **Direct contributor scan**:
   - from `dms.DocumentChangeEvent` for `R` in `[min,max]`, collect `DocumentId`s.
2. **Indirect contributor scan**:
   - from `dms.IdentityChangeEvent` in `[min,max]`, collect changed dependency `DocumentId`s.
   - expand to parents via `dms.ReferenceEdge(ChildDocumentId → ParentDocumentId)` using **all edges**.
   - filter parents to resource `R`.
3. **Candidate union**:
   - union distinct of (direct + indirect parents).
4. **Compute derived `ChangeVersion` for candidates**:
   - `MaxDepIdentityVersion = max(child.IdentityVersion)` over all outbound dependencies (via `dms.ReferenceEdge`)
   - `ChangeVersion = max(ContentVersion, IdentityVersion, MaxDepIdentityVersion)`
5. **Filter + page**:
   - filter computed `ChangeVersion` to `[min,max]`
   - order by `(ChangeVersion, DocumentId)` for deterministic paging

Keyset paging is recommended (use `(ChangeVersion, DocumentId)` as the page cursor).

Client guidance (ODS-style):

- Start a sync session by calling `availableChangeVersions` and capturing `newestChangeVersion = max`.
- Page through results using a fixed window (e.g., `minChangeVersion = checkpoint+1`, `maxChangeVersion = max`) until exhausted.
- Only advance the client checkpoint to `max` after fully paging the window (prevents “moving max” gaps/duplicates).

### PostgreSQL: resource change query (journal-driven, keyset paging)

Parameters:

- `@ResourceKeyId` (`smallint`; bind/cast to avoid implicit casts)
- `@MinChangeVersion`, `@MaxChangeVersion`
- `@AfterChangeVersion`, `@AfterDocumentId` (cursor; use `0,0` for first page)
- `@Limit`

```sql
WITH direct AS (
    SELECT e.DocumentId
    FROM dms.DocumentChangeEvent e
    WHERE e.ResourceKeyId = @ResourceKeyId
      AND e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
),
deps_in_window AS (
    SELECT e.DocumentId
    FROM dms.IdentityChangeEvent e
    WHERE e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
),
indirect AS (
    SELECT DISTINCT re.ParentDocumentId AS DocumentId
    FROM dms.ReferenceEdge re
    JOIN deps_in_window w
      ON w.DocumentId = re.ChildDocumentId
),
candidates AS (
    SELECT DocumentId FROM direct
    UNION
    SELECT d.DocumentId
    FROM indirect i
    JOIN dms.Document d
      ON d.DocumentId = i.DocumentId
    WHERE d.ResourceKeyId = @ResourceKeyId
),
dep_max AS (
    SELECT re.ParentDocumentId AS DocumentId,
           MAX(child.IdentityVersion) AS MaxDepIdentityVersion
    FROM dms.ReferenceEdge re
    JOIN dms.Document child
      ON child.DocumentId = re.ChildDocumentId
    JOIN candidates c
      ON c.DocumentId = re.ParentDocumentId
    GROUP BY re.ParentDocumentId
),
computed AS (
    SELECT d.DocumentId,
           GREATEST(
             d.ContentVersion,
             d.IdentityVersion,
             COALESCE(dm.MaxDepIdentityVersion, 0)
           ) AS ChangeVersion
    FROM dms.Document d
    JOIN candidates c
      ON c.DocumentId = d.DocumentId
    LEFT JOIN dep_max dm
      ON dm.DocumentId = d.DocumentId
    WHERE d.ResourceKeyId = @ResourceKeyId
)
SELECT DocumentId, ChangeVersion
FROM computed
WHERE ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
  AND (
    ChangeVersion > @AfterChangeVersion
    OR (ChangeVersion = @AfterChangeVersion AND DocumentId > @AfterDocumentId)
  )
ORDER BY ChangeVersion, DocumentId
LIMIT @Limit;
```

### SQL Server: resource change query (journal-driven, keyset paging)

```sql
WITH direct AS (
    SELECT e.DocumentId
    FROM dms.DocumentChangeEvent e
    WHERE e.ResourceKeyId = @ResourceKeyId
      AND e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
),
deps_in_window AS (
    SELECT e.DocumentId
    FROM dms.IdentityChangeEvent e
    WHERE e.ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
),
indirect AS (
    SELECT DISTINCT re.ParentDocumentId AS DocumentId
    FROM dms.ReferenceEdge re
    JOIN deps_in_window w
      ON w.DocumentId = re.ChildDocumentId
),
candidates AS (
    SELECT DocumentId FROM direct
    UNION
    SELECT d.DocumentId
    FROM indirect i
    JOIN dms.Document d
      ON d.DocumentId = i.DocumentId
    WHERE d.ResourceKeyId = @ResourceKeyId
),
dep_max AS (
    SELECT re.ParentDocumentId AS DocumentId,
           MAX(child.IdentityVersion) AS MaxDepIdentityVersion
    FROM dms.ReferenceEdge re
    JOIN dms.Document child
      ON child.DocumentId = re.ChildDocumentId
    JOIN candidates c
      ON c.DocumentId = re.ParentDocumentId
    GROUP BY re.ParentDocumentId
),
computed AS (
    SELECT d.DocumentId,
           CASE
             WHEN dm.MaxDepIdentityVersion IS NULL
               THEN (CASE WHEN d.ContentVersion > d.IdentityVersion THEN d.ContentVersion ELSE d.IdentityVersion END)
             ELSE
               (CASE
                  WHEN dm.MaxDepIdentityVersion >= d.ContentVersion AND dm.MaxDepIdentityVersion >= d.IdentityVersion THEN dm.MaxDepIdentityVersion
                  WHEN d.ContentVersion >= d.IdentityVersion THEN d.ContentVersion
                  ELSE d.IdentityVersion
                END)
           END AS ChangeVersion
    FROM dms.Document d
    JOIN candidates c
      ON c.DocumentId = d.DocumentId
    LEFT JOIN dep_max dm
      ON dm.DocumentId = d.DocumentId
    WHERE d.ResourceKeyId = @ResourceKeyId
)
SELECT TOP (@Limit) DocumentId, ChangeVersion
FROM computed
WHERE ChangeVersion BETWEEN @MinChangeVersion AND @MaxChangeVersion
  AND (
    ChangeVersion > @AfterChangeVersion
    OR (ChangeVersion = @AfterChangeVersion AND DocumentId > @AfterDocumentId)
  )
ORDER BY ChangeVersion, DocumentId;
```

### Optional enhancements (reduce read-time cost)

The journal-driven query still needs to compute `max(child.IdentityVersion)` per candidate parent via `dms.ReferenceEdge` (`dep_max`). For large candidate sets, this group-by can be the dominant cost.

If this becomes too expensive, these options can simplify the read-time computation **without requiring SERIALIZABLE** (at the expense of some additional write-time work).

#### A) Monotonic `MaxDepIdentityVersion` column (bounded fan-out)

Add a column on `dms.Document`:

- `MaxDepIdentityVersion bigint NOT NULL DEFAULT 0`

Maintain it as a monotonic max:

- when `ReferenceEdge(Parent → Child)` is inserted/updated, set:
  - `Parent.MaxDepIdentityVersion = max(Parent.MaxDepIdentityVersion, Child.IdentityVersion)`
- when `Child.IdentityVersion` increases, set:
  - `Parent.MaxDepIdentityVersion = max(Parent.MaxDepIdentityVersion, Child.IdentityVersion)` for all parents referencing `Child`

This is a write-time fan-out across parents, but it is:

- one-column updates (no reconstitution/hashing),
- monotonic (no “decrease” maintenance), and
- does not require SERIALIZABLE edge scans if edge writers also “self-heal” by applying the max at edge creation time.

Read-time simplifies to:

```text
ChangeVersion(P) = max(P.ContentVersion, P.IdentityVersion, P.MaxDepIdentityVersion)
```

and the expensive `dep_max` group-by can be avoided for change queries.

#### B) Denormalize parent resource key onto `dms.ReferenceEdge`

If indirect expansion frequently joins `ReferenceEdge → Document` only to filter parents by `ResourceKeyId`, consider storing `ParentResourceKeyId` on `dms.ReferenceEdge` (maintained on parent writes).

This can remove a join from the candidate-building step when expanding indirect impacts.

### Retention and `oldestChangeVersion`

The journal tables will grow with write volume. Two approaches:

1. **No retention (initially)**: simplest.
2. **Retention window** (recommended once used):
   - periodically delete old journal rows (by version and/or time),
   - expose `oldestChangeVersion` as the minimum retained across relevant tracking tables, so clients never request older windows.

Partitioning by change version range (or time) is a natural fit for both PostgreSQL and SQL Server if needed.

## Best-effort minimization strategies

1. **Avoid bumping tokens on no-op writes**:
   - use diff-based upserts for collections instead of delete-all/insert-all.
2. **Avoid “double bumps”**:
   - reuse one stamp for content+identity changes in a single write.
3. **Avoid bumping `IdentityVersion` on idempotent recompute**:
   - in identity closure recompute, only stamp when the identity/URI projection values actually change.
4. **Stable dependency ordering for `_etag`**:
   - sort dependencies by `DocumentId` when hashing.
5. **Keep `dms.ReferenceEdge` low-churn**:
   - maintain by diff so no-op updates write 0 rows to the edge table.

## Worked examples

### Example 1: upstream identity change updates dependent metadata without cascades

Assume:

- Student `S` has `DocumentId=100`, `IdentityVersion=10`, `IdentityLastModifiedAt=T1`.
- GraduationPlan `G` has `DocumentId=400`, `ContentVersion=5`, `IdentityVersion=2`, `ContentLastModifiedAt=T0`, `IdentityLastModifiedAt=T0i`.
- `G` references `S` (edge `G → S` exists in `dms.ReferenceEdge`).

Derived metadata for `G` (before the identity change):

```text
G._etag = Hash(G.ContentVersion=5, G.IdentityVersion=2, deps=[(100, 10)])
G._lastModifiedDate = max(G.ContentLastModifiedAt=T0, G.IdentityLastModifiedAt=T0i, S.IdentityLastModifiedAt=T1)
G.ChangeVersion = max(5, 2, 10) = 10
```

Now `S` has an identity update and strict identity recompute stamps:

- `S.IdentityVersion = 11`
- `S.IdentityLastModifiedAt = T2`
- journal row: `IdentityChangeEvent(ChangeVersion=11, DocumentId=100)`

No updates occur to `G`.

Next GET of `G` (after `S.IdentityVersion` changes):

```text
G._etag = Hash(G.ContentVersion=5, G.IdentityVersion=2, deps=[(100, 11)]) // changed
G._lastModifiedDate = max(T0, T0i, T2) // changed
G.ChangeVersion = max(5, 2, 11) = 11 // changed
```

### Example 2: upstream non-identity change does not change dependent `_etag`

If `S` changes a non-identity field:

- `S.ContentVersion` changes
- `S.IdentityVersion` does not change

Since `G` depends only on `S.IdentityVersion`, `G._etag` and `G.ChangeVersion` do not change (correct: the embedded reference identity did not change).

## Differences vs the original redesign draft

### What is removed / simplified

- No write-time `CacheTargets` set computation for representation metadata.
- No cross-document representation-metadata fan-out updates (`UPDATE dms.Document SET Etag=Etag+1...`).
- No SERIALIZABLE/key-range locks to make “1-hop referrers of closure” scans phantom-safe for representation metadata.

### What remains (hard requirements)

- Strict `dms.ReferentialIdentity` maintenance still requires:
  - closure computation/locking, and
  - transactional recompute on identity changes.

### What moves from write-time to read-time

- Representation metadata and per-item `ChangeVersion` are computed from:
  - local tokens, and
  - dependency identity tokens read at request time.
- Change Query selection is “journal + verify” rather than “filter on stored ChangeVersion”.

## Operational and performance notes

- **Write throughput**: avoids high-fan-out representation-metadata cascades; the remaining write-time costs are bounded per updated document (token stamps, edge diff, and journal rows).
- **Read cost**: `_etag/_lastModifiedDate/ChangeVersion` require dependency token reads; batch aggressively for query pages to avoid N+1 patterns.
- **Locking hotspots**: strict `If-Match` checks can add shared locks on dependency `IdentityLock` rows; high fan-in “hub” dependencies may increase contention (use the weaker alternative or benchmark if this becomes problematic).
- **Change query hotspots**: if a dependency with very high fan-in has an identity change in the window, the candidate set can be large; optional enhancements like `MaxDepIdentityVersion` can reduce read-time aggregation cost.
- **Journals/retention**: plan for retention/partitioning once Change Queries are used in production; expose `oldestChangeVersion` accordingly.
