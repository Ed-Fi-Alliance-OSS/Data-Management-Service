Compact descriptor catalog verification accepts a reviewed implementation baseline through
`ExpectedModelManifestPath`. Select that baseline explicitly; `EffectiveSchemaHash` and mapping
version `v3` do not identify the physical descriptor schema. The verifier reads observed metadata
from the inspected database, and expected metadata only from the manifest and the compact schema
contract in these assertion assets. It never adapts expectations to an older source or restore.

For template builds, use the existing SchemaTools `ddl emit` path (which calls
`DerivedModelSetManifestEmitter`), including every concrete resource in `resource_details`.
The Minimal/Populated workflows emit this manifest from the exact ordered ApiSchema inputs
used for fresh provisioning: DS `5.2.0` core + TPDM or DS `6.1.0` core. Review the matching
provider manifest before using it as the expected baseline. For fixture regeneration,
`EdFi.DataManagementService.Backend.Ddl.Tests.Unit/FixtureRunner` supplies that complete selection. The focused integration baseline is:

```
src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/compact-descriptor/expected/relational-model.pgsql.manifest.json
src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/compact-descriptor/expected/relational-model.mssql.manifest.json
```

Run the `FixtureRunner_With_CompactDescriptor` golden tests to regenerate/check those artifacts.
This synthetic schema includes extension, nested collection, unified alias, composite reference,
abstract identity and descriptor history shapes. For production schemas, use the matching
authoritative DS/core+extension manifest regenerated for that exact schema set; the synthetic
baseline cannot verify a production template. The manifest dialect, resource inventory and schema
hash must match the inspected database. Missing details, old wide references, old shared keys or
old descriptor history fail verification.

The single inventory reader is also the task-25 carry-forward input:

```powershell
Import-Module ./eng/DatabaseTemplates/Compact-Descriptor.psm1
$inventory = Read-CompactDescriptorInventory -Dialect pgsql -ExpectedModelManifestPath $baseline
$inventory.data_schemas # resource/history table schemas to copy; includes extension histories
$inventory.aliases # generated descriptor aliases checked in catalogs/source shapes; never writable
$inventory.columns # canonical Stored DescriptorFk only; schema/table/name/scalar type/nullability
$inventory.constraints # full composite membership, FK target columns and referential actions
$inventory.indexes # complete ordered descriptor-related key/index membership
```

It reads resource tables and abstract identity tables, de-duplicates physical tables, excludes
`UnifiedAlias` and union-view outputs from writable coverage, and retains compact abstract identity
projection columns even though those projections do not declare another direct descriptor FK.
Catalog assertions separately require every expected descriptor alias to exist as an `int4` stored
generated column on PostgreSQL or an `int` persisted computed column on SQL Server. Alias names
come from the baseline; renamed, missing, wide, ordinary or non-persisted aliases fail verification.

Verify a container database directly:

```powershell
./eng/DatabaseTemplates/Assert-CompactDescriptor.ps1 -DatabaseEngine postgresql `
    -ContainerName dms-postgresql -DatabaseName edfi_datamanagementservice `
    -ExpectedModelManifestPath $baseline
```

For SQL Server use `-DatabaseEngine mssql`, its container/database, and `MSSQL_SA_PASSWORD` or
`-MssqlPassword`. `-RenderSql` renders the same provider SQL for a direct provider connection.
Integration tests use this route, then execute it on ordinary connections separate from DDL
provisioning. SQL Server verification checks effective indexed-computation SET options, URI type,
the DMS identity collation (`SQL_Latin1_General_CP1_CI_AS`) on `Namespace`, `CodeValue` and the
computed `Uri`, independent of the database default, and non-persistence. Its separate-session
regression also exercises fresh and reused runtime pools without inheriting provisioner settings.

Both providers require `FK_Descriptor_DocumentResourceKey` on `(DocumentId, ResourceKeyId)` to
`dms.Document (DocumentId, ResourceKeyId)` with RESTRICT (NO ACTION on SQL Server), and the unique
lowered whole-URI index `UX_Descriptor_UriLowered_ResourceKeyId`. On PostgreSQL that index keys
`lower(("Namespace" || '#' || "CodeValue") COLLATE "pg_c_utf8")` and then `ResourceKeyId`, with
the `pg_c_utf8` collation pinned, and no `UriLowered` column may exist. On SQL Server it keys
`(UriLowered, ResourceKeyId)`, where `UriLowered` is a non-persisted `nvarchar(306)` computed as
`LOWER` over the stored components under the DMS identity collation.

For the combined template gate, `verify-template-restore.ps1` requires
`-ExpectedModelManifestPath $baseline`. It calls `Assert-TemplateSchema.ps1` on the source before
restoration and on the restored catalog before API registration or probes. The combined gate
requires `dms.Document` with stored non-null bigint `ContentVersion` and no `ContentLastModifiedAt`.
It selects every ordinary resource root from complete `resource_details` (`scope = "$"`), including
extension resource roots, and requires its stored non-null authoritative timestamp. It also checks
`dms.Descriptor`, whose timestamp is defined by core DDL rather than the shared descriptor model.
Missing tables and columns fail explicitly. The same invocation then runs all existing compact
assertions above through `Read-CompactDescriptorInventory`; package features never select the
expectations. The standalone compact assertions remain independent of timestamp ownership.

Retain the emitted manifest and hash, rendered catalog assertions, verification results and
package provenance together for each leg. The closure matrix is Minimal/Populated ×
PostgreSQL/SQL Server × DS `5.2.0` core + TPDM / DS `6.1.0` core (eight legs), built from one
reviewed combined commit. Use fresh source and restore databases, retain `RequirePopulatedData`
for Populated legs and run authenticated API probes only after both catalog gates pass.
`publish_package: false` build artifacts satisfy build/restore evidence; affected fixed consumer
pins must still resolve verified post-change prereleases with unchanged Data Standard package-id
suffixes before closure. Final stable publication, release promotion, cache replacement and
reprovisioning follow the [developer deployment instructions](../../docs/RELATIONAL-BACKEND.md#descriptor-catalog-checks-and-template-handoff).

Render or execute the combined assertions with `Assert-TemplateSchema.ps1`, using the same
parameters as `Assert-CompactDescriptor.ps1`. To run the real focused package restore regressions,
set `DMS_TEMPLATE_TEST_PG_CONTAINER` and `DMS_TEMPLATE_TEST_MSSQL_CONTAINER` to exclusively owned
disposable PostgreSQL 16 / SQL Server 2025 containers, and set
`DMS_TEMPLATE_TEST_MSSQL_PASSWORD` if different from the test default `EdFi_Dms1!`. Then run:

```powershell
Invoke-Pester -Path ./eng/DatabaseTemplates/tests -CI -Output Detailed
```

These tests create unique source/restore databases, package actual SQL dumps/backups, and exercise
the verifier's catalog and data checks. They intercept the first CMS call to verify ordering;
full authenticated serveability remains in the eight-leg template build/restore matrix. Correct
packages reach that API boundary; old sources and restored packages fail beforehand with the same
fingerprint. Without the container variables only those six live package cases are skipped.
Provider `CompactDescriptorSchemaTests` additionally roll back timestamp and compact-feature
catalog mutations against the same reviewed focused manifest.

The integration cases execute test-only mutations under `tests/fixtures`, including a legacy core
descriptor table with bigint DocumentId primary key, physically stored URI and discriminator, a
missing or cascading composite document FK, and a missing or stale lowered whole-URI index.
Every mutation preserves the current schema fingerprint and is rolled back. Old schemas fail the
same assertions even with a matching hash. Existing databases need deliberate reprovisioning
from changed DDL or compatible rebuilt templates; retaining `v3` does not confer compatibility.
