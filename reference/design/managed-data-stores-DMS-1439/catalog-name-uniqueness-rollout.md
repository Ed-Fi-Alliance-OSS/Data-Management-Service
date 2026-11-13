# Catalog name uniqueness rollout (DMS-1439 step 1.3c)

Migration 0040 replaces only `UX_DataStore_TenantId_Name`, retaining its name for
ordinary POST/PUT duplicate-result mapping, with uniqueness on
`(TenantId, NameComparisonKey)`. NULL tenants compare equal. The display Name,
IDs, tenant associations, connections, dependent records and ApiClient constraints
are preserved. No managed lifecycle schema or runtime is introduced by this step.

Names trim the fixed boundary whitespace set recorded in
[contract provenance](contract-provenance.md#q5--strict-parity-and-approved-compatibility-impact),
then fold ASCII A–Z only. Thus Store/store and edge-padded variants conflict;
café/cafe, non-ASCII case variants and composed/decomposed Unicode remain distinct.
This intentionally changes ordinary duplicate eligibility even when management
is disabled. Disabling management does not restore legacy uniqueness or reads.

The coordinated read changes use the same key for name equality, exact untrimmed
type equality, and unsigned UTF-8 lexical name/type ordering with Id tie-breaks.
Ordinary default Id-ascending, no implicit limit, offset-only paging, authorization
and tenant partitioning remain unchanged. Explicit text-sort page positions may
change; external clients have not been certified. IDs remain reliable identifiers.

## Maintenance deployment

1. Back up the catalog and plan a maintenance window. Drain **all** CMS replicas,
   catalog writers and old binaries, including direct SQL writers and setup jobs.
   Deploy the corrected ordinary read paths and bootstrap name selection together
   with the migration. Old CMS read binaries must not serve the new schema.
2. Confirm PostgreSQL UTF8 encoding and the migration principal's database TEMP
   privilege, or SQL Server 2019+ with the required UTF-8 query collation. PostgreSQL
   uses a temporary empty Name column to obtain the server-parsed expected generated
   expression. SQL Server uses the indexed-computed-column SET options in the
   migration; direct SQL writers must also use the required options (ANSI_NULLS,
   ANSI_PADDING, ANSI_WARNINGS, ARITHABORT, CONCAT_NULL_YIELDS_NULL and
   QUOTED_IDENTIFIER ON; NUMERIC_ROUNDABORT OFF).
3. Inventory **all** unique indexes and constraints using the queries below. Reconcile
   residual display-name or expression uniqueness explicitly before resuming traffic.
   Apply migrations. Even when 0039 is journaled, 0040 independently validates its
   complete generated-key definition, including the SQL Server helper body,
   signature and module settings. Equivalent custom definitions may be
   conservatively refused. Missing or incompatible objects are not repaired.
4. Verify the same-named constraint now covers TenantId and NameComparisonKey,
   including PostgreSQL NULLS NOT DISTINCT, and that ApiClient constraints remain.
   Smoke-test distinct café/cafe POSTs and separate collection/by-ID reads, a
   Store/store duplicate validation 400, rename and subsequent reads, metadata and
   dependents, tenant selection and paged ordering with the new binaries.
5. Resume traffic only after migration and smoke tests succeed. Keep management,
   provisioning and background workers at their intended deployment settings;
   this catalog migration does not enable managed operations.

## Refusal, remediation and recovery

0040 locks the catalog throughout definition validation, collision preflight and
constraint replacement, inside an explicit transaction. SQL Server acquires a
table lock using a real COUNT_BIG scan, including empty tables; PostgreSQL takes
ACCESS EXCLUSIVE. Any failure rolls back the replacement and leaves 0040
unjournaled. A committed 0040 with a missing journal entry can be retried only when
the complete key and approved new constraint definitions still match.

Existing canonical collisions stop before dropping legacy uniqueness. Diagnostics
contain a count and at most 20 ordered row IDs, with a remaining count; they contain
no names or connection values. Review those IDs under authorized access and choose
explicit remediation: rename specific records with PUT /v3/dataStores/{id}, or
delete only records whose removal and dependent effects the operator has approved.
Use the current version in a controlled maintenance session if API remediation is
needed, drain it again, then retry. The migration never chooses a survivor,
deduplicates, rewrites display names or deletes data automatically.

For an incompatible definition, inspect the deployed schema against the approved
0039 definitions and plan an explicit repair or restore; do not delete journal
entries or substitute a custom normalizer merely to bypass validation.

Rollback to a legacy binary/schema requires a preflight for **legacy** name
collisions (newly admitted café/cafe may conflict on SQL Server), explicit operator
decisions and a coordinated downgrade, or a planned catalog restore. Simply
reinstalling old binaries or turning management off is not a rollback procedure.


## Residual uniqueness inventory (ordinary and managed catalogs)

Replacing the approved ordinary constraint does not remove custom constraints or
indexes. An additional unique index on `Name`, `(TenantId, Name)`, or a custom
expression can still reject names admitted by the canonical policy. A global
`Name` index can also prevent reuse across tenants. In SQL Server a residual
case/accent-insensitive display-name index can reject café/cafe; PostgreSQL
expression indexes and nondeterministic/custom collations can impose a different
policy. The same issue applies to `DataStoreManage` after migration 0041.

Run these inventories before deployment and again after 0041, including empty
tables. Inspect every unique key, its collation, computed/expression definition,
and filter. Constraint-backed indexes appear alongside standalone indexes. Do not
limit the inventory to approved names or to keys that literally contain `Name`.
These are read-only queries; neither migration automatically drops custom objects.
Operators must assess why each residual object exists, check its dependent uses,
and explicitly approve reconciliation in the drained maintenance window. An incompatible residual object blocks parity qualification and enablement
until the operator reconciles it; documenting its extra restriction does not
satisfy the approved canonical acceptance policy.

PostgreSQL:

```sql
SELECT t.relname AS "TableName", ix.relname AS "IndexName",
       pg_get_indexdef(ix.oid) AS "Definition"
FROM pg_index i
JOIN pg_class t ON t.oid = i.indrelid
JOIN pg_namespace s ON s.oid = t.relnamespace
JOIN pg_class ix ON ix.oid = i.indexrelid
WHERE s.nspname = 'dmscs'
  AND t.relname IN ('DataStore', 'DataStoreManage')
  AND i.indisunique
ORDER BY t.relname, ix.relname;
```

Also inspect the PostgreSQL columns: an index on a generated key does not inline
its generation expression or the column's default collation in `pg_get_indexdef`.

```sql
SELECT t.relname AS "TableName", a.attname AS "ColumnName",
       c.collname AS "Collation", pg_get_expr(d.adbin, d.adrelid) AS "Expression"
FROM pg_class t
JOIN pg_namespace s ON s.oid = t.relnamespace
JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped
LEFT JOIN pg_collation c ON c.oid = a.attcollation
LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
WHERE s.nspname = 'dmscs' AND t.relname IN ('DataStore', 'DataStoreManage')
ORDER BY t.relname, a.attnum;
```

SQL Server (the definition includes ordered key columns, collations, computed
expressions, and the complete filtered-index predicate):

```sql
SELECT t.name AS TableName, i.name AS IndexName,
       STRING_AGG(CONCAT(c.name, ':', COALESCE(c.collation_name, ''),
                        COALESCE(' AS ' + cc.definition, '')), ',')
           WITHIN GROUP (ORDER BY ic.key_ordinal)
       + COALESCE(' WHERE ' + i.filter_definition, '') AS Definition
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.indexes i ON i.object_id = t.object_id
JOIN sys.index_columns ic ON ic.object_id = i.object_id
    AND ic.index_id = i.index_id AND ic.key_ordinal > 0
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id
    AND cc.column_id = c.column_id
WHERE s.name = 'dmscs'
  AND t.name IN ('DataStore', 'DataStoreManage')
  AND i.is_unique = 1
GROUP BY t.name, i.name, i.filter_definition
ORDER BY t.name, i.name;
```

## Managed aggregate schema (0041)

`DataStoreManage` stores the internal lifecycle identity, current and historical
ordinary links, durable create/delete job IDs, UTC timestamps, bounded failure
codes, the complete versioned endpoint descriptor, and all twelve trusted-artifact
pin fields. The pin is either wholly absent or wholly present. Failure metadata stores a
bounded category and exactly one bounded code, without messages; the typed model
validates registered provisioning codes and their category mapping. Database shape
checks do not duplicate the evolving provisioning taxonomy. Pin immutability
and lifecycle-dependent invariants remain the responsibility of the lifecycle
model and the forthcoming repository. This migration adds no handlers or managed
records. Job IDs deliberately have no foreign key to retained jobs: job retention
must not erase aggregate evidence or make completed jobs undeletable. Tenant and
current ordinary links restrict deletion; historical links remain after ordinary
catalog removal. Tenant scoping remains partition correctness, not authorization.

Every status other than `Deleted` reserves `(TenantId, NameComparisonKey)`, with
NULL tenants equal. Direct-storage Unicode test vectors establish common-key
parity; they do not expand the managed API's ASCII name grammar. The generated key uses the same exact fixed-whitespace trim
and ASCII fold as ordinary `DataStore`; display-name collation is not an input.
Every non-NULL current ordinary link is unique. Only `Deleted` releases the name
and target reservation; a retained failure or error continues to reserve them.

The endpoint descriptor is the complete, fixed-order JSON array returned by
`ManagedDataStoreEndpoint.CanonicalEncoding`: version, provider, transport, host,
port, instance, and maintenance database. It is stored without an arbitrary
length cap (`text`/`nvarchar(max)`). This includes Unicode Unix socket locators
and named instances. These internal locators are not artifact/staging paths or
credentials and must not appear in public responses or logs. The forthcoming
repository must write that canonical encoding; alternate hand-written JSON
serializations are not a supported admission path. Database checks validate JSON
shape, version and provider basics; they do not reproduce the .NET serializer.

An active target is reserved across **all tenants** by a unique index on a
database-generated SHA-256 of the complete canonical descriptor and the
ASCII-folded target database name. The fixed-size digest makes the index possible
without truncating endpoint fields or inventing host/instance length limits.
Digest bytes need not match between CMS datastore providers: they are local
reservation keys. A digest collision conservatively refuses a second reservation;
it can never authorize operations on a different endpoint. The digest is **not**
endpoint identity proof. Before any physical operation, guarded admission under the acquired target lock
must compare every descriptor field against the exact operation-bound administrative
snapshot, alongside the lifecycle/job fencing checks. Before publication, also
validate the exact runtime snapshot used for encryption. A separately resolved
pre-call comparison alone is insufficient (specification sections 5.6–5.7). The canonical name key itself is never hashed.
