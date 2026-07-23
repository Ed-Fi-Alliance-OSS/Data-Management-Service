# api-schema-tools CLI

Command-line tool for Ed-Fi DMS schema hashing and DDL generation. Generates
deterministic SQL artifacts and manifests from `ApiSchema.json` inputs without
requiring database connectivity.

> For the end-to-end provisioning, schema-fingerprint validation, and testing
> workflow, see the [Relational Backend Developer Guide](../../../../docs/RELATIONAL-BACKEND.md).

## Installation

There are two ways to use `api-schema-tools`:

1. Install the published .NET tool package from Azure Artifacts.
2. Download the source code and compile the tool locally with `dotnet build`.

### Install from Azure Artifacts

Use this option when you want the published tool without cloning or building the
DMS repository.

```powershell
$feed = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json"
$version = "<published-version>"
dotnet tool install --global EdFi.Api.SchemaTools --source $feed --version $version
```

Use a published package version from the feed. To install the latest stable
package, omit `--version $version`.

The installed command is `api-schema-tools`.

### Build from source

Use this option when you have downloaded or cloned the DMS repository and want
to compile the tool locally.

```bash
dotnet build src/dms/clis/EdFi.DataManagementService.SchemaTools
```

The executable is output as `api-schema-tools` (or `api-schema-tools.exe` on Windows).

## Commands

### `hash` — Compute effective schema hash

Loads one or more `ApiSchema.json` files, normalizes them, and prints the
effective schema hash (SHA-256, lowercase hex).

```bash
api-schema-tools hash <coreSchemaPath> [extensionSchemaPath...]
```

**Arguments:**

| Argument | Required | Description |
|---|---|---|
| `coreSchemaPath` | Yes | Path to the core `ApiSchema.json` file |
| `extensionSchemaPath` | No | Path(s) to extension `ApiSchema.json` file(s) |

**Examples:**

```bash
# Core schema only
api-schema-tools hash core/ApiSchema.json

# Core + one extension
api-schema-tools hash core/ApiSchema.json extensions/tpdm/ApiSchema.json

# Core + multiple extensions
api-schema-tools hash core/ApiSchema.json extensions/tpdm/ApiSchema.json extensions/sample/ApiSchema.json
```

### `connection` — Parse a connection string with the exact runtime provider

Both verbs read the connection string from **stdin** (never a command argument, so the password does not
appear in the process arguments) and parse it with the exact runtime provider (Npgsql for PostgreSQL,
Microsoft.Data.SqlClient for SQL Server), so alias canonicalization, last-wins duplicate synonyms, and
rejection of unsupported keywords match what the services do at runtime.

```bash
# validate: print { valid, database, error }
echo "Host=localhost;Database=edfi_dms;Username=postgres;Password=secret" | api-schema-tools connection validate --engine postgresql

# inspect: print the non-secret canonical { valid, database, host, port, username, error }
echo "Server=dms-mssql,1433;Database=edfi_dms;User Id=sa;Password=secret;TrustServerCertificate=true" | api-schema-tools connection inspect --engine mssql
```

**Options (both verbs):**

| Option | Required | Description |
|---|---|---|
| `--engine` | Yes | Target database engine: `postgresql` or `mssql` (case-insensitive; an unsupported value is a usage error, exit `2`). |

**`validate`** prints a JSON `{ valid, database, error }` result — the stable contract the docker-compose
start scripts consume for host-side pre-flight validation. It never emits any other field.

**`inspect`** prints a JSON `{ valid, database, host, port, username, error }` of the **non-secret** canonical
coordinates. It never emits the password. `port` is `null` for SQL Server, which encodes the port inside the
data source (`host,port`); split it host-side when a separate port is needed.

Both verbs exit `0` with a `{ valid: false, ... }` result for a connection string the provider rejects, and
exit `2` (a usage error) for an unsupported `--engine` value.

### `ddl emit` — Generate DDL SQL and manifests

Generates dialect-specific DDL scripts and manifest files to an output directory.
Does not require database connectivity.

```bash
api-schema-tools ddl emit --schema <paths...> --output <directory> [--dialect <dialect>] [--ddl-manifest]
```

**Options:**

| Option | Short | Required | Default | Description |
|---|---|---|---|---|
| `--schema` | `-s` | Yes | — | `ApiSchema.json` path(s). First is core, rest are extensions. |
| `--output` | `-o` | Yes | — | Output directory for generated files |
| `--dialect` | `-d` | No | `both` | SQL dialect: `pgsql`, `mssql`, or `both` |
| `--ddl-manifest` | — | No | `false` | Also emit `ddl.manifest.json` (dialect-independent normalized-SQL hash + statement count) for diagnostics |

**Examples:**

```bash
# Generate PostgreSQL DDL only
api-schema-tools ddl emit --schema core/ApiSchema.json --output ./ddl-output --dialect pgsql

# Generate both dialects
api-schema-tools ddl emit -s core/ApiSchema.json -o ./ddl-output -d both

# Core + extension, SQL Server only
api-schema-tools ddl emit -s core/ApiSchema.json -s extensions/tpdm/ApiSchema.json -o ./ddl-output -d mssql
```

**Output files:**

| File | Condition | Description |
|---|---|---|
| `pgsql.sql` | `--dialect pgsql` or `both` | PostgreSQL DDL script |
| `mssql.sql` | `--dialect mssql` or `both` | SQL Server DDL script |
| `relational-model.{dialect}.manifest.json` | Per selected dialect | Derived relational model inventory (tables, columns, constraints, indexes, views, triggers) |
| `effective-schema.manifest.json` | Always | Schema fingerprint, components, and resource key seed summary |
| `ddl.manifest.json` | Only with `--ddl-manifest` | Dialect-independent summary: normalized-SQL SHA-256 and statement count per dialect, for diagnostics |

All output files use Unix line endings (`\n`) for deterministic, byte-for-byte
stable output across platforms.

### `ddl provision` — Generate DDL and execute against a database

Generates dialect-specific DDL and executes it against a target database in a
single transaction. Provisions one database at a time (`--dialect both` is not
accepted).

```bash
api-schema-tools ddl provision --schema <paths...> --connection-string <connstr> --dialect <dialect> [--create-database] [--timeout <seconds>]
```

**Options:**

| Option | Short | Required | Default | Description |
|---|---|---|---|---|
| `--schema` | `-s` | Yes | — | `ApiSchema.json` path(s). First is core, rest are extensions. |
| `--connection-string` | `-c` | Yes | — | ADO.NET connection string for the target database. |
| `--dialect` | `-d` | Yes | — | SQL dialect: `pgsql` or `mssql` (not `both`). |
| `--create-database` | — | No | `false` | Create the target database if it does not exist before provisioning. |
| `--timeout` | `-t` | No | `300` | Command timeout in seconds for DDL execution. |
| `--override-host` | — | No | — | Host to substitute for the connection string's endpoint. Must be paired with `--override-port`. |
| `--override-port` | — | No | — | Port (`1`-`65535`) to substitute for the connection string's endpoint. Must be paired with `--override-host`. |

**Endpoint override.** `--override-host` / `--override-port` optionally substitute the connection string's
endpoint — for host-side tooling that must reach a Docker-published port rather than the container-internal
one. They are atomic: supply **both or neither**; the host must be non-blank and the port in `1`-`65535`
(otherwise a controlled usage error, non-zero exit). When supplied, the exact provider rewrites **only** the
endpoint — every other option (credentials, SSL, pooling, a password containing `;` or `=`, …) is preserved —
and the rewritten connection is used for every operation below (database-name lookup, optional create, MVCC,
seed/schema preflight, and transactional DDL). A connection string the provider rejects fails with a
controlled usage error, never a stack trace, and never echoes the connection string or password.

**Examples:**

```bash
# Provision a PostgreSQL database
api-schema-tools ddl provision --schema core/ApiSchema.json --connection-string "Host=localhost;Port=5432;Database=edfi_dms;Username=postgres;Password=secret" --dialect pgsql --create-database

# Provision an existing SQL Server database (add --create-database to create it, as in the pgsql examples)
api-schema-tools ddl provision -s core/ApiSchema.json -c "Server=localhost;Initial Catalog=edfi_dms;User Id=sa;Password=secret;TrustServerCertificate=true" -d mssql

# Core + extension, PostgreSQL
api-schema-tools ddl provision -s core/ApiSchema.json -s extensions/tpdm/ApiSchema.json -c "Host=localhost;Database=edfi_dms;Username=postgres;Password=secret" -d pgsql --create-database
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

SQL Server integration tests **run by default** — the project's committed `appsettings.json`
already supplies an `MssqlAdmin` connection string pointing at `localhost`. The skip guard
only checks that `MssqlAdmin` is set (it does not probe connectivity), so the tests fail on
connection errors if no SQL Server is reachable there. To point them at your own SQL Server
locally:

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

Because the committed `appsettings.json` always sets `MssqlAdmin`, the SQL Server tests run
and fail on any server issue — same behavior as the PostgreSQL tests. They report as skipped
only if `MssqlAdmin` is removed from the committed config.

## Breaking changes

Prior to DMS-950, the CLI accepted positional arguments for schema hashing:

```bash
# Old (no longer works)
api-schema-tools core/ApiSchema.json

# New equivalent
api-schema-tools hash core/ApiSchema.json
```

The `hash` subcommand is now required.
