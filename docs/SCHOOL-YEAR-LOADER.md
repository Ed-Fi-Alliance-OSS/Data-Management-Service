# School Year Loader

# Load custom range of school years with auto-calculated current year
.\eng\Load-SchoolYears.ps1 -StartYear 2020 -EndYear 2030

# Load custom range with explicit current school year
.\eng\Load-SchoolYears.ps1 -StartYear 2020 -EndYear 2030 -CurrentSchoolYear 2024

The School Year Loader is a PowerShell-based utility that loads school year data into the Ed-Fi Data Management Service (DMS). This replaces the previous C# application with a more maintainable PowerShell solution that is automatically tested through the database template management system.

## Components

### 1. SchoolYear-Loader.psm1 Module
**Location**: `eng/SchoolYear-Loader.psm1`

A PowerShell module that provides the core `Invoke-SchoolYearLoader` function for loading school year data via the DMS API.

### 2. Load-SchoolYears.ps1 Script
**Location**: `eng/Load-SchoolYears.ps1`

A standalone PowerShell script that provides a command-line interface for loading school years. This script handles authentication and provides user-friendly feedback.

## Usage

### Using the Standalone Script (Recommended)

The simplest way to load school years is using the standalone script:

```powershell
# Load default school years (1991-2037)
.\eng\Load-SchoolYears.ps1

# Load custom range of school years
.\eng\Load-SchoolYears.ps1 -StartYear 2020 -EndYear 2037 -CurrentSchoolYear 2024

# Use custom service URLs
.\eng\Load-SchoolYears.ps1 -DmsUrl "https://api.example.com" -CmsUrl "https://config.example.com"
```

### Using the PowerShell Module Directly

For advanced scenarios or integration into other scripts:

```powershell
# Import required modules
Import-Module ./eng/Dms-Management.psm1 -Force
Import-Module ./eng/SchoolYear-Loader.psm1 -Force

# Set up authentication (example)
Add-CmsClient -CmsUrl "http://localhost:8081"
$cmsToken = Get-CmsToken -CmsUrl "http://localhost:8081"
$keySecret = Get-KeySecret -CmsUrl "http://localhost:8081" -CmsToken $cmsToken -ClaimSetName 'BootstrapDescriptorsandEdOrgs'
$dmsToken = Get-DmsToken -DmsUrl "http://localhost:8080" -Key $keySecret.Key -Secret $keySecret.Secret

# Load school years
Invoke-SchoolYearLoader -StartYear 2020 -EndYear 2037 -CurrentSchoolYear 2024 -DmsUrl "http://localhost:8080" -DmsToken $dmsToken
```

## Configuration Parameters

### Script Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `StartYear` | int | 1991 | The first school year to load |
| `EndYear` | int | 2037 | The last school year to load |
| `CurrentSchoolYear` | int | 0 | The school year to mark as current. If 0, automatically calculated based on current date: if after June, uses next year; otherwise uses current year. |
| `DmsUrl` | string | "http://localhost:8080" | Data Management Service URL |
| `CmsUrl` | string | "http://localhost:8081" | Configuration Management Service URL |
| `ClaimSetName` | string | 'BootstrapDescriptorsandEdOrgs' | Claim set for authentication |

### Function Parameters

The `Invoke-SchoolYearLoader` function accepts:

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `StartYear` | int | No | 1991 | The first school year to load |
| `EndYear` | int | No | 2037 | The last school year to load |
| `CurrentSchoolYear` | int | No | 0 (auto-calculate) | The school year to mark as current. If 0, automatically calculated based on current date: if after June, uses next year; otherwise uses current year |
| `DmsUrl` | string | Yes | - | Data Management Service URL |
| `DmsToken` | string | Yes | - | DMS authentication token |

## Prerequisites

1. **PowerShell 7 or later** - Required for cross-platform compatibility
2. **Network Access** - Both DMS and CMS services must be accessible
3. **Running Services**:
   - Configuration Management Service (CMS) for authentication
   - Data Management Service (DMS) for data loading
4. **Authentication** - Valid claim set configured in CMS

## How It Works

1. **Authentication Setup**: The script connects to the CMS to obtain authentication credentials
2. **Token Generation**: Uses the CMS to generate a DMS access token
3. **Current School Year Calculation**: If `CurrentSchoolYear` is not provided (or set to 0), it automatically calculates based on current date:
   - If current month is after June (July-December): uses next calendar year
   - If current month is June or before (January-June): uses current calendar year
   - This follows the typical academic year calendar where school years span across two calendar years
4. **Data Loading**: Iterates through the specified year range, creating school year records
5. **API Calls**: Each school year is posted as a separate API call to `/data/ed-fi/schoolYearTypes`

### Data Structure

Each school year creates a record with:
- `schoolYear`: The year (e.g., 2024)
- `currentSchoolYear`: Boolean indicating if this is the current year
- `schoolYearDescription`: Formatted description (e.g., "2023-2024")

## Integration with Template Management

The school year loader is automatically used by the database template management system (`eng/DatabaseTemplates/Template-Management.psm1`). This ensures that the functionality is tested whenever database templates are built.

The template management system imports the `SchoolYear-Loader.psm1` module and calls `Invoke-SchoolYearLoader` as part of its workflow, providing automatic testing coverage.

## Error Handling

The school year loader includes comprehensive error handling:

- **Parameter Validation**: Ensures all years are positive integers and ranges are valid
- **Service Connectivity**: Checks that both CMS and DMS services are accessible
- **Authentication**: Validates that tokens can be obtained and are valid
- **API Errors**: Reports failures for individual school year loads
- **Detailed Logging**: Provides clear feedback on progress and any issues

## Examples

### Basic Usage
```powershell
# Load school years for a typical implementation
.\eng\Load-SchoolYears.ps1 -StartYear 2015 -EndYear 2035
```

### Production Environment
```powershell
# Load school years for production with remote services
.\eng\Load-SchoolYears.ps1 `
  -StartYear 2020 `
  -EndYear 2030 `
  -CurrentSchoolYear 2024 `
  -DmsUrl "https://dms.school-district.edu" `
  -CmsUrl "https://cms.school-district.edu"
```

### Development/Testing
```powershell
# Load minimal set for development with auto-calculated current year
.\eng\Load-SchoolYears.ps1 -StartYear 2023 -EndYear 2026

# Load minimal set with explicit current year
.\eng\Load-SchoolYears.ps1 -StartYear 2023 -EndYear 2026 -CurrentSchoolYear 2024
```

## Migration from C# Application

The previous C# SchoolYearLoader application has been removed and replaced with this PowerShell solution. The key benefits of this migration include:

- **Simplified Deployment**: No need to compile or distribute executables
- **Better Integration**: Seamlessly integrates with existing PowerShell tooling
- **Automatic Testing**: Tested through the template management system
- **Easier Maintenance**: PowerShell is more accessible for configuration and updates
- **Cross-Platform**: Works on Windows, macOS, and Linux with PowerShell 7+

The functionality remains the same - loading school year data into the DMS - but with improved maintainability and integration.
