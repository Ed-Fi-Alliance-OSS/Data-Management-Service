# Ed-Fi Data Management Service - Schema Generator CLI

A command-line interface tool for generating database Data Definition Language (DDL) scripts from Ed-Fi API schema files. This tool supports multiple database platforms and provides flexible configuration options for schema generation.

## Purpose

The Schema Generator CLI is designed to:

- **Generate Database DDL Scripts**: Convert Ed-Fi API schema JSON files into platform-specific database schema scripts
- **Support Multiple Database Platforms**: Generate scripts for PostgreSQL and SQL Server databases
- **Handle Ed-Fi Extensions**: Optionally include extension tables and schema elements
- **Provide Flexible Schema Options**: Support both unified schema and separate schema approaches
- **Enable Automated DevOps Workflows**: Integrate database schema generation into CI/CD pipelines

## Features

### Database Platform Support

- **PostgreSQL**: Generates optimized PostgreSQL DDL with proper data types and constraints
- **SQL Server**: Creates SQL Server-compatible DDL with appropriate MSSQL data types
- **Multi-Platform**: Generate scripts for all supported platforms simultaneously

### Schema Generation Options

- **Extension Support**: Include/exclude Ed-Fi extension tables and elements
- **Union Views**: Control generation of union views for polymorphic references
- **Table Naming**: Choose between prefixed table names (default) or separate database schemas
- **Precision Handling**: Accurate decimal precision and scale from MetaEd schema definitions

### Configuration Flexibility

- **Command-Line Arguments**: Direct parameter specification for automation
- **JSON Configuration**: Persistent configuration via `appsettings.json`
- **Environment Variables**: Override settings using environment variables
- **Logging**: Configurable logging levels with file and console output

## Prerequisites

- **.NET 8.0**: The CLI is built on .NET 8.0 runtime
- **Ed-Fi API Schema**: Valid Ed-Fi API schema JSON file (typically generated from MetaEd or Ed-Fi ODS/API)
- **File System Access**: Read access to schema file and write access to output directory

## Installation

The CLI is typically built as part of the Ed-Fi Data Management Service solution:

```powershell
# Build the entire solution
dotnet build src\dms\EdFi.DataManagementService.sln

# Or build just the CLI project
dotnet build src\dms\clis\EdFi.DataManagementService.SchemaGenerator.Cli
```

The executable will be available at:

```
src\dms\clis\EdFi.DataManagementService.SchemaGenerator.Cli\bin\Debug\net8.0\EdFi.DataManagementService.SchemaGenerator.Cli.exe
```

## Usage

### Basic Usage

```powershell
# Generate DDL for all databases (default)
EdFi.DataManagementService.SchemaGenerator.Cli --input schema.json --output ./ddl

# Generate only PostgreSQL DDL
EdFi.DataManagementService.SchemaGenerator.Cli -i schema.json -o ./ddl -p postgresql

# Generate only SQL Server DDL
EdFi.DataManagementService.SchemaGenerator.Cli -i schema.json -o ./ddl -p mssql
```

### Advanced Usage

```powershell
# Generate PostgreSQL DDL with extensions and separate schemas
EdFi.DataManagementService.SchemaGenerator.Cli \
  --input schema.json \
  --output ./ddl \
  --provider postgresql \
  --extensions \
  --use-schemas

# Generate SQL Server DDL with extensions and prefixed table names
EdFi.DataManagementService.SchemaGenerator.Cli \
  -i schema.json \
  -o ./ddl \
  -p mssql \
  -e \
  --prefixed-tables

# Skip union views for better performance
EdFi.DataManagementService.SchemaGenerator.Cli \
  --input schema.json \
  --output ./ddl \
  --skip-union-views
```

## Command-Line Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--input` | `-i` | **(Required)** Path to the input API schema JSON file | - |
| `--output` | `-o` | **(Required)** Directory where DDL scripts will be generated | - |
| `--provider` | `-p` | Database provider: `pgsql`/`postgresql`, `mssql`, or `all` | `all` |
| `--extensions` | `-e` | Include extension tables in the generated DDL | `false` |
| `--skip-union-views` | `-s` | Skip generation of union views for polymorphic references | `false` |
| `--use-schemas` | - | Generate separate database schemas (edfi, tpdm, etc.) | `false` |
| `--use-prefixed-names` | - | Use prefixed table names in dms schema | `true` |
| `--help` | `-h` | Display help information | - |

### Provider Aliases

- **PostgreSQL**: Both `pgsql` and `postgresql` are accepted
- **SQL Server**: Use `mssql`
- **All Platforms**: Use `all` (default)

## Configuration File

You can configure default settings in `appsettings.json`:

```json
{
  "SchemaGenerator": {
    "InputFilePath": "path/to/schema.json",
    "OutputDirectory": "path/to/output",
    "DatabaseProvider": "all",
    "IncludeExtensions": false,
    "SkipUnionViews": false,
    "UsePrefixedTableNames": true
  },
  "Logging": {
    "LogFilePath": "logs/SchemaGenerator.log",
    "MinimumLevel": "Information"
  }
}
```

### Configuration Precedence

1. **Command-line arguments** (highest priority)
2. **Environment variables** (format: `SchemaGenerator__InputFilePath`)
3. **appsettings.json** (lowest priority)

## Environment Variables

Override configuration using environment variables:

```powershell
# Windows PowerShell
$env:SchemaGenerator__InputFilePath = "C:\path\to\schema.json"
$env:SchemaGenerator__OutputDirectory = "C:\path\to\output"
$env:SchemaGenerator__DatabaseProvider = "postgresql"

# Linux/macOS
export SchemaGenerator__InputFilePath="/path/to/schema.json"
export SchemaGenerator__OutputDirectory="/path/to/output"
export SchemaGenerator__DatabaseProvider="postgresql"
```

## Output Files

The CLI generates the following output files:

### PostgreSQL

- **File**: `EdFi-DMS-Database-Schema-PostgreSQL.sql`
- **Content**: Complete PostgreSQL DDL with tables, indexes, constraints, and views
- **Features**: Optimized data types, proper constraints, decimal precision support

### SQL Server

- **File**: `EdFi-DMS-Database-Schema-SQLServer.sql`
- **Content**: Complete SQL Server DDL with tables, indexes, constraints, and views
- **Features**: MSSQL-specific data types, proper constraints, decimal precision support

### Log Files

- **File**: `logs/SchemaGenerator.log` (configurable)
- **Content**: Detailed execution logs with timestamps
- **Levels**: Debug, Information, Warning, Error

## Schema Features

### Table Generation

- **Core Tables**: Ed-Fi core domain entity tables
- **Extension Tables**: Optional extension entity tables
- **Descriptors**: Enumeration/lookup tables
- **Reference Tables**: Foreign key reference validation

### Data Types

- **String Types**: Proper length specifications from schema
- **Decimal Types**: Precision and scale from MetaEd definitions
- **Boolean Types**: Platform-appropriate boolean representations
- **Date/Time Types**: Standard date, time, and datetime types

### Constraints

- **Primary Keys**: Single and composite primary keys
- **Foreign Keys**: Referential integrity constraints
- **Unique Constraints**: Alternate key enforcement
- **Check Constraints**: Domain validation rules

### Views

- **Union Views**: Polymorphic reference resolution (optional)
- **Descriptor Views**: Lookup table convenience views

## Integration Examples

### CI/CD Pipeline (GitHub Actions)

```yaml
- name: Generate Database Schema
  run: |
    dotnet run --project src/dms/clis/EdFi.DataManagementService.SchemaGenerator.Cli \
      --input ${{ github.workspace }}/schemas/EdFi.3.3.1-b.ApiSchema.json \
      --output ${{ github.workspace }}/database/ddl \
      --provider all \
      --extensions
```

### PowerShell Script

```powershell
# Generate-Schema.ps1
param(
    [Parameter(Mandatory=$true)]
    [string]$SchemaFile,
    
    [Parameter(Mandatory=$true)]
    [string]$OutputDir,
    
    [string]$Provider = "all"
)

$cliPath = "src\dms\clis\EdFi.DataManagementService.SchemaGenerator.Cli\bin\Debug\net8.0\EdFi.DataManagementService.SchemaGenerator.Cli.exe"

& $cliPath --input $SchemaFile --output $OutputDir --provider $Provider --extensions

if ($LASTEXITCODE -eq 0) {
    Write-Host "Schema generation completed successfully" -ForegroundColor Green
} else {
    Write-Error "Schema generation failed with exit code $LASTEXITCODE"
}
```

## Troubleshooting

### Common Issues

1. **File Not Found**

   ```
   Error: InputFilePath and OutputDirectory are required.
   ```

   - **Solution**: Ensure both `--input` and `--output` parameters are provided
   - **Check**: Verify the schema file exists and is accessible

2. **JSON Deserialization Error**

   ```
   Failed to deserialize ApiSchema.
   ```

   - **Solution**: Validate the input JSON file is a valid Ed-Fi API schema
   - **Check**: Ensure the JSON file is not corrupted or truncated

3. **Output Directory Issues**
   - **Solution**: Ensure the output directory exists or can be created
   - **Check**: Verify write permissions to the output directory

4. **Provider Not Recognized**
   - **Solution**: Use valid provider names: `pgsql`, `postgresql`, `mssql`, or `all`
   - **Check**: Review the help output for supported options

### Logging and Diagnostics

Enable detailed logging for troubleshooting:

```json
{
  "Logging": {
    "MinimumLevel": "Debug",
    "LogFilePath": "logs/SchemaGenerator-debug.log"
  }
}
```

Or set via environment variable:

```powershell
$env:Logging__MinimumLevel = "Debug"
```

## Performance Considerations

- **Large Schemas**: Consider using `--skip-union-views` for schemas with many polymorphic references
- **Extensions**: Only include extensions (`--extensions`) when needed
- **Output Location**: Use local directories for better I/O performance
- **Logging**: Use `Warning` or `Error` level logging in production environments

## Development and Testing

### Building from Source

```powershell
# Clone the repository
git clone https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service.git

# Navigate to the CLI project
cd Data-Management-Service/src/dms/clis/EdFi.DataManagementService.SchemaGenerator.Cli

# Build the project
dotnet build

# Run tests
dotnet test ../EdFi.DataManagementService.SchemaGenerator.Tests.Unit
```

### Testing the CLI

```powershell
# Run with help to verify installation
dotnet run -- --help

# Test with a sample schema
dotnet run -- --input sample-schema.json --output ./test-output --provider postgresql
```

## Support and Documentation

- **Project Repository**: [Ed-Fi Data Management Service](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service)
- **Ed-Fi Alliance**: [https://www.ed-fi.org/](https://www.ed-fi.org/)
- **Technical Documentation**: See `/docs` folder in the project root
- **Issue Tracking**: GitHub Issues in the project repository

## License

Licensed under the Apache License, Version 2.0. See the [LICENSE](../../../../LICENSE) and [NOTICES](../../../../NOTICES.md) files in the project root for more information.
