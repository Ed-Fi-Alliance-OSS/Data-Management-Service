Compact descriptor catalog verification accepts a reviewed implementation baseline through
`ExpectedModelManifestPath`. Select that baseline explicitly; `EffectiveSchemaHash` and mapping
version `v3` do not identify the physical descriptor schema. The verifier reads observed metadata
from the inspected database, and expected metadata only from the manifest and the compact schema
contract in these assertion assets. It never adapts expectations to an older source or restore.

Generate the manifest with `DerivedModelSetManifestEmitter`, including every concrete resource in
`resource_details`. `EdFi.DataManagementService.Backend.Ddl.Tests.Unit/FixtureRunner` supplies that
complete selection. The focused integration baseline is:

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
database-default collation and non-persistence. Its separate-session regression also exercises
fresh and reused runtime pools without inheriting provisioner settings.

For DMS-1401's combined template gate, pass `-ExpectedModelManifestPath $baseline` to
`verify-template-restore.ps1`; it asserts the source before restoration and the restored catalog
afterward. DMS-1401 supplies its reviewed combined baseline and timestamp gate. These compact
assertions deliberately do not require presence or absence of document timestamps, so they remain
reusable after that ownership change. DMS-1404 fresh-schema tests separately require both existing
document stamp columns. Template build/restore matrices and package publication remain their
assigned work; this task verifies fresh databases and the reusable catalog gate.

The integration cases execute test-only mutations under `tests/fixtures`, including a legacy core
descriptor table with bigint DocumentId primary key, physically stored URI and discriminator.
Every mutation preserves the current schema fingerprint and is rolled back. Old schemas fail the
same assertions even with a matching hash. Existing databases need deliberate reprovisioning
from changed DDL or compatible rebuilt templates; retaining `v3` does not confer compatibility.
