# PostgreSQL major upgrades

DMS requires PostgreSQL 18 or later with a UTF-8 database encoding (see
[Database versions](OPERATIONS.md#database-versions)). This page covers what a
PostgreSQL major upgrade, for example from 18 to 19, needs beyond the usual
PostgreSQL procedure: rebuilding every DMS index that folds case under the builtin
`pg_c_utf8` collation, after checking that the new folding rules do not make two
stored descriptors collide.

## Why a major upgrade affects descriptor identity

Descriptor identity is case-insensitive. On PostgreSQL the descriptor identity index
folds each descriptor URI, rebuilt from its stored namespace and code value, with
`lower(("Namespace" || '#' || "CodeValue") COLLATE "pg_c_utf8")`:

```sql
CREATE UNIQUE INDEX "UX_Descriptor_UriLowered_ResourceKeyId"
    ON dms."Descriptor" (lower(("Namespace" || '#' || "CodeValue") COLLATE "pg_c_utf8"), "ResourceKeyId");
```

`pg_c_utf8` uses Unicode simple case mapping and compares by code point. PostgreSQL
documents its behavior as "stable within a PostgreSQL major version"
([Collation Support](https://www.postgresql.org/docs/18/collation.html)); a new major
can ship newer Unicode case-mapping data. An index keeps the folded values it was
built with:

- `pg_upgrade` copies index files unchanged, so after a binary upgrade the index
  can hold keys that the new version would fold differently. Lookups can then miss
  descriptors that exist, and uniqueness is no longer enforced against the new
  folding.
- A dump restored into the new major rebuilds the index under the new folding. If
  two stored descriptors now fold to the same value, that index fails to build.

Either way, two descriptors that were distinct under the old major can become one
identity under the new one. The index cannot be rebuilt until that is resolved.

### Which indexes this covers

The PostgreSQL 18 and UTF-8 floor (DMS-1447) does not itself create a `pg_c_utf8`
index. `UX_Descriptor_UriLowered_ResourceKeyId` is added by DMS-1448, and the
stories after it move descriptor lookups onto it; all of them ship in the same
release as the floor. Step 1 lists the `pg_c_utf8` indexes a database actually
has, so run it on every upgrade: it is how this playbook finds the indexes to
rebuild. If it finds none, a major upgrade needs only the usual PostgreSQL
procedure.

## Playbook

Run steps 1 to 6 in every DMS database on the server: each single-tenant
database, and each tenant's or data store's database in a multi-tenant
deployment. Connect as the database owner or a superuser.

### 1. Find the indexes to rebuild

On the current major, before upgrading:

```sql
SELECT n.nspname AS schema_name, t.relname AS table_name, c.relname AS index_name,
       pg_catalog.pg_get_indexdef(i.indexrelid) AS index_definition
FROM pg_catalog.pg_index i
JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid
JOIN pg_catalog.pg_class t ON t.oid = i.indrelid
JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
WHERE pg_catalog.pg_get_indexdef(i.indexrelid) LIKE '%pg_c_utf8%'
ORDER BY 1, 2, 3;
```

Save the output, including the full `index_definition`: step 4 may need it to
re-create an index. With the descriptor index in place, the expected output is:

```text
 schema_name | table_name |               index_name               |                                                                                            index_definition
-------------+------------+----------------------------------------+--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
 dms         | Descriptor | UX_Descriptor_UriLowered_ResourceKeyId | CREATE UNIQUE INDEX "UX_Descriptor_UriLowered_ResourceKeyId" ON dms."Descriptor" USING btree (lower((((("Namespace")::text || '#'::text) || ("CodeValue")::text) COLLATE pg_c_utf8)), "ResourceKeyId")
(1 row)
```

If it returns `(0 rows)`, skip steps 3 to 5 for this database. If it lists any other
index, rebuild it in step 4 as well, and adapt the step 3 query to that index's
expression and columns.

### 2. Stop writers and back up

Stop every DMS instance that writes to the database. Writes made while the old
index is still in place, or while collisions are unresolved, would be checked
against the wrong folding. Take a backup you can restore before continuing.

Then upgrade the server with `pg_upgrade`, or restore a dump into the new major.
After `pg_upgrade` the index exists but still holds the old folding. After a
restore, the index exists only if it built cleanly under the new folding; if
`pg_restore` reports an error creating `UX_Descriptor_UriLowered_ResourceKeyId`,
the data is restored without that index, and step 3 shows the descriptors that
caused the error.

### 3. Look for new collisions under the new major

Run this on the new major, either on the upgraded server or on a restored copy,
before rebuilding anything. It turns off index scans for the transaction so every
value is folded again from the table rows; the old index's stored keys are not
trusted:

```sql
BEGIN;
SET LOCAL enable_indexscan = off;
SET LOCAL enable_indexonlyscan = off;
SET LOCAL enable_bitmapscan = off;

SELECT lower((d."Namespace" || '#' || d."CodeValue") COLLATE "pg_c_utf8") AS uri_lowered,
       d."ResourceKeyId",
       count(*) AS row_count,
       array_agg(d."DocumentId" ORDER BY d."DocumentId") AS document_ids,
       array_agg(d."Namespace" || '#' || d."CodeValue" ORDER BY d."DocumentId") AS uris
FROM dms."Descriptor" d
GROUP BY 1, 2
HAVING count(*) > 1
ORDER BY 2, 1;

ROLLBACK;
```

Expected output:

```text
 uri_lowered | ResourceKeyId | row_count | document_ids | uris
-------------+---------------+-----------+--------------+------
(0 rows)
```

To confirm the query is folding from the table, run `EXPLAIN (COSTS OFF)` on the
`SELECT` inside the same transaction. The plan reads `Seq Scan on "Descriptor" d`,
with no index scan.

If it returns rows, do not rebuild the index and keep DMS stopped. Each row is a
group of descriptors of one resource type (`ResourceKeyId`) that were different
identities under the old major and are one identity under the new one; `uris`
shows how each was stored. Neither DMS nor this playbook resolves them
automatically, and descriptors must not be deleted or rewritten just to let the
index build. Decide with the data owners which descriptor in each group stays,
move whatever references the others, and remove or change them through a reviewed
data change. Then run this step again until it returns `(0 rows)`. If a decision
cannot be made, restore the backup onto the old major and stay there.

### 4. Rebuild the indexes

With DMS still stopped, rebuild or re-create each index found in step 1. First
check whether the index exists in this database:

```sql
SELECT to_regclass('dms."UX_Descriptor_UriLowered_ResourceKeyId"') AS existing_index;
```

When the index exists, the output is:

```text
                existing_index
----------------------------------------------
 dms."UX_Descriptor_UriLowered_ResourceKeyId"
(1 row)
```

When it is missing, `existing_index` is empty:

```text
 existing_index
----------------

(1 row)
```

If it exists, which is always the case after `pg_upgrade`, rebuild it:

```sql
REINDEX INDEX dms."UX_Descriptor_UriLowered_ResourceKeyId";
```

If `existing_index` is empty, because the restore could not build it, run the
`index_definition` saved in step 1 exactly as recorded, so that any index options
are kept. For the descriptor index that is:

```sql
CREATE UNIQUE INDEX "UX_Descriptor_UriLowered_ResourceKeyId" ON dms."Descriptor" USING btree (lower((((("Namespace")::text || '#'::text) || ("CodeValue")::text) COLLATE pg_c_utf8)), "ResourceKeyId");
```

`REINDEX` takes an `ACCESS EXCLUSIVE` lock on the index while it runs, and
because the planner locks every index of a table, almost every query on
`dms."Descriptor"` waits until it finishes, reads included. `CREATE INDEX` locks
out writes to the table but not reads, since the index does not exist yet
([REINDEX](https://www.postgresql.org/docs/18/sql-reindex.html)). Run either one
inside the maintenance window, with DMS stopped.

If either fails with a unique violation (`23505`, `could not create unique index
"UX_Descriptor_UriLowered_ResourceKeyId"` with a `Key (...) is duplicated`
detail), step 3 missed a collision: an existing index is left unchanged, and a
missing one is still missing. Go back to step 3.

### 5. Validate

Every check must show the expected output before DMS is started again.

The index is valid, ready and unique:

```sql
SELECT indisvalid, indisready, indisunique
FROM pg_catalog.pg_index
WHERE indexrelid = 'dms."UX_Descriptor_UriLowered_ResourceKeyId"'::regclass;
```

```text
 indisvalid | indisready | indisunique
------------+------------+-------------
 t          | t          | t
(1 row)
```

No invalid index is left behind:

```sql
SELECT indexrelid::regclass AS invalid_index
FROM pg_catalog.pg_index
WHERE NOT indisvalid;
```

```text
 invalid_index
---------------
(0 rows)
```

The definition is unchanged: run step 1 again. It returns the same rows as before
the upgrade, with `COLLATE pg_c_utf8` still in each definition.

There are no collisions: run step 3 again. It returns `(0 rows)`.

A descriptor is found case-insensitively through the index. Use a URI and
`ResourceKeyId` from your data, given in a different case than it is stored; the
example below looks up `uri://ed-fi.org/GradeLevelDescriptor#Ninth grade` (namespace
`uri://ed-fi.org/GradeLevelDescriptor`, code value `Ninth grade`) stored with
`ResourceKeyId` 1. The lookup returns exactly one row, the stored descriptor:

```sql
SELECT "DocumentId", "Namespace", "CodeValue"
FROM dms."Descriptor"
WHERE lower(("Namespace" || '#' || "CodeValue") COLLATE "pg_c_utf8") =
      lower('URI://ED-FI.ORG/GRADELEVELDESCRIPTOR#NINTH GRADE' COLLATE "pg_c_utf8")
  AND "ResourceKeyId" = 1;
```

To confirm the rebuilt index serves that lookup, explain it with sequential scans
discouraged for the transaction; on a small table the planner may otherwise
correctly prefer a sequential scan:

```sql
BEGIN;
SET LOCAL enable_seqscan = off;
EXPLAIN (COSTS OFF)
SELECT "DocumentId", "Namespace", "CodeValue"
FROM dms."Descriptor"
WHERE lower(("Namespace" || '#' || "CodeValue") COLLATE "pg_c_utf8") =
      lower('URI://ED-FI.ORG/GRADELEVELDESCRIPTOR#NINTH GRADE' COLLATE "pg_c_utf8")
  AND "ResourceKeyId" = 1;
ROLLBACK;
```

The plan names `UX_Descriptor_UriLowered_ResourceKeyId`, as an `Index Scan` or a
`Bitmap Index Scan`, with the lowered value under `COLLATE pg_c_utf8` and the
`ResourceKeyId` in its index condition. For example:

```text
 Index Scan using "UX_Descriptor_UriLowered_ResourceKeyId" on "Descriptor"
   Index Cond: ((lower((((("Namespace")::text || '#'::text) || ("CodeValue")::text))::text) = 'uri://ed-fi.org/gradeleveldescriptor#ninth grade'::text COLLATE pg_c_utf8) AND ("ResourceKeyId" = 1))
```

Optionally, `amcheck` can confirm that every table row has a matching index entry
under the new folding. It requires the `amcheck` extension, which a superuser
creates once per database:

```sql
CREATE EXTENSION IF NOT EXISTS amcheck;
SELECT bt_index_check('dms."UX_Descriptor_UriLowered_ResourceKeyId"'::regclass, heapallindexed => true);
```

It returns one empty row when the index is consistent and raises an error when it
is not.

### 6. Start DMS

Start DMS only after step 5 passes in every DMS database on the server.

## Case-folding differences between PostgreSQL and SQL Server

PostgreSQL and SQL Server do not fold or compare descriptor URIs the same way. Each
engine's own verdict defines descriptor identity on that engine, and DMS does not
try to make them agree:

- PostgreSQL uses `pg_c_utf8`: Unicode simple case mapping and code-point
  comparison. DMS does not normalize Unicode, so canonically equivalent spellings
  stay distinct.
- SQL Server uses the `SQL_Latin1_General_CP1_CI_AS` identity collation: `LOWER`
  with that collation's casing table, then case-insensitive linguistic comparison.
  As a legacy version-80 collation it gives no comparison weight to code points it
  does not know, so those are ignored when comparing.

The examples below were checked on PostgreSQL 18.6 and SQL Server 2025 (RTM-CU9).
They describe what to expect on each engine; they are not a promise of matching
behavior, and they do not cover every character. Live fixtures that pin these
verdicts on both engines are part of DMS-1455, after the descriptor index and
lookups exist.

| Values compared | PostgreSQL (`pg_c_utf8`) | SQL Server (`SQL_Latin1_General_CP1_CI_AS`) |
|---|---|---|
| `GradeLevelDescriptor#Ninth grade` and the same text in upper case | Same identity | Same identity |
| `É` and `é` | Same identity | Same identity |
| `Straße` and `Strasse` | Different: simple case mapping leaves `ß` as `ß` | Same identity: linguistic comparison equates `ß` and `ss` |
| Precomposed `é` (U+00E9) and `e` followed by a combining acute accent (U+0301) | Different: compared by code point | Same identity: linguistic comparison equates them |
| `A` and `A😀` | Different | Same identity: the emoji has no comparison weight |
| `Ǹ` (U+01F8) and `ǹ` (U+01F9) | Same identity: `lower` maps `Ǹ` to `ǹ` | Same identity, but because neither carries a comparison weight: `LOWER` leaves `Ǹ` unchanged, and `AǸ` also matches `A` |

A PostgreSQL major upgrade can change the PostgreSQL column for characters
whose case mapping changed in Unicode; step 3 is what finds stored descriptors
affected by such a change.
