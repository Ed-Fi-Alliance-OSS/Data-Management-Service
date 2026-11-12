# SQL Server inventory parity fixture (DMS-1438, Step 4.1)

`dms-shaped-database.sql` builds the hand-built DMS-shaped database the Step 4.1 integration tests read: the
`dms` singleton tables with the generated DDL's column shapes, one resource project (`edfi`) with its
`tracked_changes_edfi` companion, and one object of each inventory category the producer's query reports there
(sequence, table, view, scalar function, trigger, table type, named and system-named constraints, standalone and
unique indexes). Like the generated DDL, it emits no `GRANT`, `DENY`, user, role, or membership, so the database
keeps exactly the built-in security state of a new database. Batches are separated by lines holding only `GO`.

The two companion files were produced once from that database by the delivered DMS-1271 PowerShell module
`eng/DatabaseTemplates/Template-RestoreCore.psm1`, so the C# inventory queries, canonical inventory, and hash are
proven against the producer's own tooling rather than against values the C# tests computed:

- `dms-shaped-database.inventory.canonical.json`: `ConvertTo-CanonicalInventoryJson` over
  `ConvertFrom-InventoryQueryRow -SchemaRow <rows> -ObjectRow <rows> -PrincipalRow <rows>`, where the rows came
  from `Get-InventorySchemaQuerySql -DatabaseEngine mssql -Purpose InventoryEnumeration`,
  `Get-InventoryObjectQuerySql -DatabaseEngine mssql`, and `Get-InventoryPrincipalQuerySql -DatabaseEngine mssql`,
  each run as the delivered consumer runs them (`sqlcmd -C -b -h -1 -W -Q <query>` through `docker exec`) against
  a SQL Server 2025 (17.0.4065.4) database created with a plain `CREATE DATABASE` and built from
  `dms-shaped-database.sql` with `sqlcmd -b -i`.
- `dms-shaped-database.inventory.sha256`: `Get-CanonicalInventoryHash` over the same inventory.

Both files are UTF-8 without a byte order mark with a single trailing LF; the tests compare after trimming it.
Changing the SQL means regenerating both files together. The same run also confirmed that
`Get-InventoryDatabaseObjectQuerySql` and the principal query return no row for this database and that
`Assert-MssqlArtifactSecurityState` accepts its `Get-MssqlSecurityStateQuerySql` rows (236 on that build: the
three database-scope defaults, `SELECT` to `public` on system objects, and `dbo` in `db_owner`).

`MssqlDmsTargetEngineFactsTests` consumes these files.
