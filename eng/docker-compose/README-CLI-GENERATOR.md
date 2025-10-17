# Ed-Fi Data Management Service - CLI Generator Docker Workflow

This directory contains the Docker Compose configuration and scripts for running the Ed-Fi Data Management Service Schema Generator CLI in a containerized environment. The CLI generates database DDL (Data Definition Language) scripts from Ed-Fi API schema files.

## Quick Start

```powershell
# 1. Setup the container (one-time)
.\setup-cli-generator.ps1

# 2. Generate schema from local file
.\run-cli-generator.ps1 -InputFile "C:\path\to\schema.json" -OutputFolder "C:\path\to\output"

# 3. Generate schema from URL
.\run-cli-generator.ps1 -SchemaUrl "https://api.example.com/schema.json" -OutputFolder "C:\path\to\output"
```

## Files Overview

| File | Purpose |
|------|---------|
| `setup-cli-generator.ps1` | Builds the CLI generator Docker container |
| `run-cli-generator.ps1` | Executes the CLI with proper volume mounts for host file access |
| `cli-generator.yml` | Docker Compose configuration for the CLI container |
| `start-cli-generator.ps1` | Legacy script (deprecated - use setup + run instead) |

## Workflow Scripts

### 1. Container Setup Script

**File**: `setup-cli-generator.ps1`

**Purpose**: Builds the CLI generator Docker container from source code.

```powershell
# Build the container
.\setup-cli-generator.ps1

# Force rebuild (useful after code changes)
.\setup-cli-generator.ps1 -Rebuild

# Stop and remove containers
.\setup-cli-generator.ps1 -Down
```

**Parameters**:

- `-Rebuild`: Force rebuild of the container image
- `-Down`: Stop and remove containers instead of building

### 2. CLI Execution Script

**File**: `run-cli-generator.ps1`

**Purpose**: Runs the CLI generator with proper volume mounts to access host files and write output to host directories.

```powershell
# Generate from local schema file
.\run-cli-generator.ps1 -InputFile "C:\Temp\schema.json" -OutputFolder "C:\Temp\output"

# Generate from remote URL
.\run-cli-generator.ps1 -SchemaUrl "https://api.edfi.org/schema.json" -OutputFolder "C:\Temp\output"

# Generate PostgreSQL only
.\run-cli-generator.ps1 -InputFile "C:\Temp\schema.json" -OutputFolder "C:\Temp\output" -CliArguments @("-p", "postgresql")

# Show help
.\run-cli-generator.ps1
```

**Parameters**:

- `-InputFile`: Path to local Ed-Fi API schema JSON file
- `-OutputFolder`: Directory where DDL scripts will be generated
- `-SchemaUrl`: URL to fetch the Ed-Fi API schema JSON file
- `-CliArguments`: Additional CLI arguments (optional)

**Important Notes**:

- You must specify either `-InputFile` OR `-SchemaUrl`, but not both
- The `-OutputFolder` parameter is required for actual generation
- Input files and output folders are automatically mounted as Docker volumes

## Docker Volume Mapping

The script automatically handles volume mounting between the host and container:

| Host Path | Container Path | Purpose |
|-----------|----------------|---------|
| Your `-InputFile` | `/app/input/input-file.txt` | Schema input file |
| Your `-OutputFolder` | `/app/output` | Generated DDL output directory |

## CLI Options

The CLI supports the following options (use with `-CliArguments`):

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--input` | `-i` | Path to input API schema JSON file | Auto-mounted |
| `--output` | `-o` | Directory for generated DDL scripts | Auto-mounted |
| `--provider` | `-p` | Database provider: `pgsql`, `mssql`, or `all` | `all` |
| `--url` | `-u` | URL to fetch API schema JSON file | - |
| `--extensions` | `-e` | Include extension tables | `false` |
| `--skip-union-views` | `-s` | Skip union views generation | `false` |
| `--use-schemas` | | Generate separate database schemas | `false` |
| `--use-prefixed-names` | | Use prefixed table names (default) | `true` |
| `--help` | `-h` | Display help information | - |

## Usage Examples

### Basic Usage

```powershell
# Generate all database types from local file
.\run-cli-generator.ps1 -InputFile "C:\Schemas\EdFi.3.3.1-b.ApiSchema.json" -OutputFolder "C:\Output\DDL"

# Generate PostgreSQL only from URL
.\run-cli-generator.ps1 `
  -SchemaUrl "https://raw.githubusercontent.com/Ed-Fi-Alliance-OSS/MetaEd-js/main/test/schema.json" `
  -OutputFolder "C:\Output\PostgreSQL" `
  -CliArguments @("-p", "postgresql")
```

### Advanced Usage

```powershell
# Generate with extensions and separate schemas
.\run-cli-generator.ps1 `
  -InputFile "C:\Schemas\schema.json" `
  -OutputFolder "C:\Output\WithExtensions" `
  -CliArguments @("--extensions", "--use-schemas", "--provider", "postgresql")

# Generate without union views for better performance
.\run-cli-generator.ps1 `
  -InputFile "C:\Schemas\large-schema.json" `
  -OutputFolder "C:\Output\NoUnionViews" `
  -CliArguments @("--skip-union-views")

# Generate SQL Server DDL with prefixed table names
.\run-cli-generator.ps1 `
  -InputFile "C:\Schemas\schema.json" `
  -OutputFolder "C:\Output\SQLServer" `
  -CliArguments @("-p", "mssql", "--use-prefixed-names")
```

### URL-Based Generation

```powershell
# Fetch schema from Ed-Fi GitHub repository
.\run-cli-generator.ps1 `
  -SchemaUrl "https://raw.githubusercontent.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/main/Application/EdFi.Ods.Standard/Artifacts/ApiSchema/EdFi.3.3.1-b.ApiSchema.json" `
  -OutputFolder "C:\Output\FromGitHub"

# Fetch from internal API endpoint
.\run-cli-generator.ps1 `
  -SchemaUrl "https://internal-api.school-district.org/metadata/schema" `
  -OutputFolder "C:\Output\InternalAPI" `
  -CliArguments @("--extensions", "--provider", "all")
```

## CI/CD Integration

### GitHub Actions Example

```yaml
name: Generate Database Schema

on:
  push:
    paths:
      - 'schemas/**'

jobs:
  generate-schema:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Setup CLI Container
        run: .\eng\docker-compose\setup-cli-generator.ps1
        
      - name: Generate PostgreSQL Schema
        run: |
          .\eng\docker-compose\run-cli-generator.ps1 `
            -InputFile "${{ github.workspace }}\schemas\EdFi.ApiSchema.json" `
            -OutputFolder "${{ github.workspace }}\database\postgresql" `
            -CliArguments @("-p", "postgresql", "--extensions")
            
      - name: Generate SQL Server Schema
        run: |
          .\eng\docker-compose\run-cli-generator.ps1 `
            -InputFile "${{ github.workspace }}\schemas\EdFi.ApiSchema.json" `
            -OutputFolder "${{ github.workspace }}\database\sqlserver" `
            -CliArguments @("-p", "mssql", "--extensions")
            
      - name: Upload Artifacts
        uses: actions/upload-artifact@v3
        with:
          name: database-schemas
          path: database/
```

### Azure DevOps Pipeline

```yaml
trigger:
  paths:
    include:
      - schemas/*

pool:
  vmImage: 'windows-latest'

steps:
- powershell: |
    .\eng\docker-compose\setup-cli-generator.ps1
  displayName: 'Setup CLI Container'

- powershell: |
    .\eng\docker-compose\run-cli-generator.ps1 `
      -InputFile "$(Build.SourcesDirectory)\schemas\EdFi.ApiSchema.json" `
      -OutputFolder "$(Build.ArtifactStagingDirectory)\database" `
      -CliArguments @("--extensions", "--provider", "all")
  displayName: 'Generate Database Schemas'

- task: PublishBuildArtifacts@1
  inputs:
    PathtoPublish: '$(Build.ArtifactStagingDirectory)\database'
    ArtifactName: 'database-schemas'
```

## Troubleshooting

### Common Issues

1. **Container Not Found**

   ```
   Unable to find image 'docker-compose-cli-generator:latest'
   ```

   **Solution**: Run `.\setup-cli-generator.ps1` to build the container first.

2. **File Access Denied**

   ```
   Error: The specified input file does not exist
   ```

   **Solution**: Ensure the input file path is absolute and the file exists on the host system.

3. **Output Directory Issues**
   **Solution**: The script automatically creates output directories. Ensure you have write permissions to the specified location.

4. **Volume Mount Errors**

   ```
   Error response from daemon: invalid mount config
   ```

   **Solution**: Use absolute paths for both `-InputFile` and `-OutputFolder` parameters.

### Debug Mode

For troubleshooting, you can run the CLI directly with Docker to see detailed output:

```powershell
# Manual Docker run with debug output
docker run --rm `
  -v "C:\path\to\schema.json:/app/input/input-file.txt" `
  -v "C:\path\to\output:/app/output" `
  docker-compose-cli-generator `
  --input /app/input/input-file.txt `
  --output /app/output `
  --provider all `
  --extensions
```

### Log Files

The CLI generates log files inside the container. To access them:

```powershell
# Run container with log volume mount
docker run --rm `
  -v "C:\path\to\schema.json:/app/input/input-file.txt" `
  -v "C:\path\to\output:/app/output" `
  -v "C:\path\to\logs:/app/logs" `
  docker-compose-cli-generator `
  --input /app/input/input-file.txt `
  --output /app/output
```

## Prerequisites

- **Docker Desktop**: Must be installed and running
- **PowerShell 5.1+**: For running the automation scripts
- **File System Access**: Read access to schema files, write access to output directories

## Performance Considerations

- **Large Schemas**: Use `--skip-union-views` for faster generation with large schemas
- **Extensions**: Only include `--extensions` when needed, as it increases generation time
- **Local Files**: Local files are faster than URL downloads for repeated generation
- **Output Location**: Use local drives for better I/O performance

## Container Management

### Container Lifecycle

```powershell
# Build container
.\setup-cli-generator.ps1

# Rebuild after code changes
.\setup-cli-generator.ps1 -Rebuild

# Remove containers and images
.\setup-cli-generator.ps1 -Down
docker rmi docker-compose-cli-generator
```

### Image Information

The CLI generator container:

- **Base Image**: `mcr.microsoft.com/dotnet/runtime:8.0`
- **Size**: ~86MB
- **Built From**: Latest source code in `src/dms/clis/`
- **Working Directory**: `/app`
- **Entrypoint**: `dotnet EdFi.DataManagementService.SchemaGenerator.Cli.dll`

## Development

For developers working on the CLI generator:

### Building from Source

```powershell
# Build the solution
dotnet build src\dms\EdFi.DataManagementService.sln

# Run tests
dotnet test src\dms\clis\EdFi.DataManagementService.SchemaGenerator.Tests.Unit

# Rebuild container with changes
.\setup-cli-generator.ps1 -Rebuild
```

### Testing Changes

```powershell
# Test with sample schema
.\run-cli-generator.ps1 `
  -InputFile "test-data\sample-schema.json" `
  -OutputFolder "test-output" `
  -CliArguments @("--provider", "postgresql")
```

## Support

- **Documentation**: See the main [CLI README](../../src/dms/clis/EdFi.DataManagementService.SchemaGenerator.Cli/README.md) for detailed CLI options
- **Issues**: Report issues in the [GitHub repository](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/issues)
- **Ed-Fi Community**: Visit [Ed-Fi.org](https://www.ed-fi.org/) for community support

## License

Licensed under the Apache License, Version 2.0. See the [LICENSE](../../LICENSE) and [NOTICES](../../NOTICES.md) files in the project root for more information.
