# Manifest parity fixture (DMS-1438, Step 2.2)

Produced once by the delivered DMS-1271 PowerShell module `eng/DatabaseTemplates/Template-RestoreCore.psm1`,
so the C# canonical inventory, hash, and restore manifest reader are proven against real tooling output
rather than against documents the C# tests wrote themselves.

- `inventory.json`: the raw input inventory, deliberately unsorted, with mixed-case object types, an empty
  schema, every escaping class (quote, backslash, a tab, a raw DEL, `é`, an emoji surrogate pair), an
  ordinal-order trap (`Zebra` before `apple`), and unsorted principals. It was built as nested ordered
  hashtables and written with `ConvertTo-Json -Depth 10`.
- `inventory.canonical.json`: `ConvertTo-CanonicalInventoryJson -Inventory <inventory.json content>`.
- `inventory.sha256`: `Get-CanonicalInventoryHash -Inventory <inventory.json content>`.
- `restore-manifest.postgresql.json` and `restore-manifest.mssql.json`: `New-TemplateRestoreManifest` over the
  same inventory (package `EdFi.Api.Minimal.Template.{PostgreSql|MsSql}.5.2.0` version `1.0.123`, projects
  `edfi` and `tpdm`, resource key count 42, seed hash bytes 1..32, mapping version `v3`; PostgreSQL engine
  version `18.1` with `jsonb`, SQL Server `17.0.900.7` at compatibility level 170 with `nvarchar(max)`),
  written with `ConvertTo-Json -Depth 10`. The producer self-validates with `Assert-RestoreManifestShape`
  and embeds the inventory in canonical order, so the SQL Server file carries `databaseCompatibilityLevel`
  as its last member.

All files were written as UTF-8 without a byte order mark and with LF line endings; the two text values are
compared after trimming the trailing newline. Regenerating any file means regenerating all five together.

`CanonicalInventoryHashTests.Given_the_PowerShell_produced_inventory_fixture` and
`RestoreManifestReaderTests.Given_the_PowerShell_produced_manifest_fixtures` consume these files.
