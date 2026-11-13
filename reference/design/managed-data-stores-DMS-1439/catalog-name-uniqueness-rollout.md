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
3. Apply migrations. Even when 0039 is journaled, 0040 independently validates its
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
