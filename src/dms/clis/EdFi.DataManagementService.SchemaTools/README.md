# dms-schema CLI

Command-line tool for Ed-Fi DMS schema hashing and DDL generation. Generates
deterministic SQL artifacts and manifests from `ApiSchema.json` inputs without
requiring database connectivity.

## Installation

Build from source:

```bash
dotnet build src/dms/clis/EdFi.DataManagementService.SchemaTools
```

The executable is output as `dms-schema` (or `dms-schema.exe` on Windows).

## Commands

### `hash` — Compute effective schema hash

Loads one or more `ApiSchema.json` files, normalizes them, and prints the
effective schema hash (SHA-256, lowercase hex).

```bash
dms-schema hash <coreSchemaPath> [extensionSchemaPath...]
```

**Arguments:**

| Argument | Required | Description |
|---|---|---|
| `coreSchemaPath` | Yes | Path to the core `ApiSchema.json` file |
| `extensionSchemaPath` | No | Path(s) to extension `ApiSchema.json` file(s) |

**Examples:**

```bash
# Core schema only
dms-schema hash core/ApiSchema.json

# Core + one extension
dms-schema hash core/ApiSchema.json extensions/tpdm/ApiSchema.json

# Core + multiple extensions
dms-schema hash core/ApiSchema.json extensions/tpdm/ApiSchema.json extensions/sample/ApiSchema.json
```

### `ddl emit` — Generate DDL SQL and manifests

Generates dialect-specific DDL scripts and manifest files to an output directory.
Does not require database connectivity.

```bash
dms-schema ddl emit --schema <paths...> --output <directory> [--dialect <dialect>]
```

**Options:**

| Option | Short | Required | Default | Description |
|---|---|---|---|---|
| `--schema` | `-s` | Yes | — | `ApiSchema.json` path(s). First is core, rest are extensions. |
| `--output` | `-o` | Yes | — | Output directory for generated files |
| `--dialect` | `-d` | No | `both` | SQL dialect: `pgsql`, `mssql`, or `both` |

**Examples:**

```bash
# Generate PostgreSQL DDL only
dms-schema ddl emit --schema core/ApiSchema.json --output ./ddl-output --dialect pgsql

# Generate both dialects
dms-schema ddl emit -s core/ApiSchema.json -o ./ddl-output -d both

# Core + extension, SQL Server only
dms-schema ddl emit -s core/ApiSchema.json -s extensions/tpdm/ApiSchema.json -o ./ddl-output -d mssql
```

**Output files:**

| File | Condition | Description |
|---|---|---|
| `pgsql.sql` | `--dialect pgsql` or `both` | PostgreSQL DDL script |
| `mssql.sql` | `--dialect mssql` or `both` | SQL Server DDL script |
| `relational-model.{dialect}.manifest.json` | Per selected dialect | Derived relational model inventory (tables, columns, constraints, indexes, views, triggers) |
| `effective-schema.manifest.json` | Always | Schema fingerprint, components, and resource key seed summary |

All output files use Unix line endings (`\n`) for deterministic, byte-for-byte
stable output across platforms.

### `ddl provision` — Generate DDL and execute against a database

Generates dialect-specific DDL and executes it against a target database in a
single transaction. Provisions one database at a time (`--dialect both` is not
accepted).

```bash
dms-schema ddl provision --schema <paths...> --connection-string <connstr> --dialect <dialect> [--create-database]
```

**Options:**

| Option | Short | Required | Default | Description |
|---|---|---|---|---|
| `--schema` | `-s` | Yes | — | `ApiSchema.json` path(s). First is core, rest are extensions. |
| `--connection-string` | `-c` | Yes | — | ADO.NET connection string for the target database. |
| `--dialect` | `-d` | Yes | — | SQL dialect: `pgsql` or `mssql` (not `both`). |
| `--create-database` | — | No | `false` | Create the target database if it does not exist before provisioning. |

**Examples:**

```bash
# Provision a PostgreSQL database
dms-schema ddl provision --schema core/ApiSchema.json --connection-string "Host=localhost;Port=5432;Database=edfi_dms;Username=postgres;Password=secret" --dialect pgsql --create-database

# Provision a SQL Server database (database must already exist)
dms-schema ddl provision -s core/ApiSchema.json -c "Server=localhost;Initial Catalog=edfi_dms;User Id=sa;Password=secret;TrustServerCertificate=true" -d mssql

# Core + extension, PostgreSQL
dms-schema ddl provision -s core/ApiSchema.json -s extensions/tpdm/ApiSchema.json -c "Host=localhost;Database=edfi_dms;Username=postgres;Password=secret" -d pgsql --create-database
```

**Behavior:**

1. Loads and normalizes schema files, builds the effective schema set
2. Generates DDL (core tables, relational model, seed DML) for the specified dialect
3. Optionally creates the database if `--create-database` is set
4. Executes all DDL in a single transaction against the target database
5. For SQL Server: configures Read Committed Snapshot Isolation (RCSI) on newly
   created databases; warns if RCSI is disabled on existing databases

## Determinism guarantee

For a fixed set of `(ApiSchema.json inputs, dialect, relational mapping version)`,
`ddl emit` produces **byte-for-byte identical** output files across runs.
This enables reliable golden-file testing and change detection.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Error (invalid arguments, missing files, schema validation failure, etc.) |

Errors are written to stderr with descriptive messages.

## Integration tests

### PostgreSQL

PostgreSQL integration tests run automatically and require a PostgreSQL server.
The connection string is configured in `appsettings.json` in the
`SchemaTools.Tests.Integration` project (port 5432 for CI, overridden to 5435
locally via `appsettings.Test.json`). If PostgreSQL is unreachable, the tests
fail — this is intentional so CI detects infrastructure problems.

### SQL Server

SQL Server integration tests are **opt-in**. They are skipped unless a
`MssqlAdmin` connection string is configured. To enable them locally:

1. Create `appsettings.Test.json` in the `SchemaTools.Tests.Integration` project
   directory (this file is gitignored):

   ```json
   {
       "ConnectionStrings": {
           "PostgresAdmin": "Host=localhost;Port=5435;Database=postgres;Username=postgres;Password=abcdefgh1!;",
           "MssqlAdmin": "Server=localhost;Database=master;User Id=sa;Password=YourPassword;TrustServerCertificate=true;"
       }
   }
   ```

2. Ensure SQL Server is running (e.g., via Docker):

   ```bash
   docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourPassword" -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest
   ```

3. Run the integration tests:

   ```bash
   dotnet test src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration
   ```

When `MssqlAdmin` is not configured, the SQL Server tests report as skipped
(not failed). When it is configured, they run and fail on any server issue —
same behavior as the PostgreSQL tests.

## Breaking changes

Prior to DMS-950, the CLI accepted positional arguments for schema hashing:

```bash
# Old (no longer works)
dms-schema core/ApiSchema.json

# New equivalent
dms-schema hash core/ApiSchema.json
```

The `hash` subcommand is now required.
