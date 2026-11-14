# PostgreSQL inventory parity fixture (DMS-1438, Step 3.1)

`dms-shaped-database.sql` builds the hand-built DMS-shaped database the Step 3.1 integration tests read:
the `dms` singleton tables with the generated DDL's column shapes, one resource project (`edfi`) with its
`tracked_changes_edfi` companion, a view, an enum, a SECURITY DEFINER enqueue function owned by
`edfi_dms_enqueue_owner` with EXECUTE revoked from PUBLIC, and exactly the grants the core DDL emitter makes
to that role. The cluster role must exist before the script runs; the test helper and the generation steps
below both ensure it with the delivered DO-block semantics.

The two companion files were produced once from that database by the delivered DMS-1271 PowerShell module
`eng/DatabaseTemplates/Template-RestoreCore.psm1`, so the C# inventory queries, canonical inventory, and hash
are proven against the producer's own tooling rather than against values the C# tests computed:

- `dms-shaped-database.inventory.canonical.json`: `ConvertTo-CanonicalInventoryJson` over
  `ConvertFrom-InventoryQueryRow -SchemaRow <rows> -ObjectRow <rows> -PrincipalRow @()`, where the schema rows
  came from `Get-InventorySchemaQuerySql -DatabaseEngine postgresql -Purpose InventoryEnumeration` and the
  object rows from `Get-InventoryObjectQuerySql -DatabaseEngine postgresql`, both run with
  `psql -U postgres -d <database> -tA -c <query>` against a PostgreSQL 18.6 database created with a plain
  `CREATE DATABASE` and replayed from `dms-shaped-database.sql` with `psql -v ON_ERROR_STOP=1 -f`.
- `dms-shaped-database.inventory.sha256`: `Get-CanonicalInventoryHash` over the same inventory.

Both files are UTF-8 without a byte order mark with a single trailing LF; the tests compare after trimming it.
Changing the SQL means regenerating both files together. The same run also confirmed that
`Get-InventoryDatabaseObjectQuerySql` returns no row for this database and that
`Get-PostgresqlTemplateOwnershipQuerySql` returns exactly the enqueue-owned function and the
`pg_database_owner`-owned `public` schema, which is what the ownership tests assert.

`PostgresqlDmsTargetEngineFactsTests` consumes these files.
