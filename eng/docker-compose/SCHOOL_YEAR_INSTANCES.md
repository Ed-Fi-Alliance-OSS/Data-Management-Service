# School Year Instance Setup

This guide explains how to set up multiple DMS instances with school year routing.

## Overview

The `start-local-dms.ps1` script now supports automatic creation of multiple DMS instances, each configured with a specific school year route context. This is useful for multi-tenant scenarios where you need to separate data by school year.

## Quick Start

### Create School Year Instances

To create DMS instances for a range of school years, use the `-SchoolYearRange` parameter:

```powershell
./start-local-dms.ps1 -EnableConfig -EnvironmentFile ./.env -EnableSwaggerUI -LoadSeedData -SchoolYearRange "2022-2026"
```

This will:
1. Start all required services (PostgreSQL, DMS, Configuration Service)
2. Create 5 DMS instances: one for each year from 2022 to 2026
3. Configure route contexts for each instance with `schoolYear` as the key
4. Make the instances available at URLs like:
   - `http://localhost:8080/2022/data`
   - `http://localhost:8080/2023/data`
   - `http://localhost:8080/2024/data`
   - `http://localhost:8080/2025/data`
   - `http://localhost:8080/2026/data`

### Format

The `SchoolYearRange` parameter uses the format: `StartYear-EndYear`

Examples:
- `"2022-2026"` - Creates instances for 2022, 2023, 2024, 2025, 2026
- `"2024-2025"` - Creates instances for 2024, 2025
- `"2025-2025"` - Creates a single instance for 2025

## What Gets Created

For each school year in the range, the script creates:

1. **DMS Instance**: A database instance with:
   - **Instance Type**: "SchoolYear"
   - **Instance Name**: "School Year {YEAR}"
   - **Connection String**: Configured from your environment file

2. **Route Context**: A routing rule with:
   - **Context Key**: "schoolYear"
   - **Context Value**: The year (e.g., "2024")

## Swagger UI Integration

When you use the `-EnableSwaggerUI` flag along with `-SchoolYearRange`, the Swagger UI will automatically:

1. Detect all available school years from the OpenAPI specification
2. Display a school year selector dropdown
3. **Set the current school year as the default** (calculated based on the current date):
   - If current month > June: uses next calendar year
   - Otherwise: uses current calendar year
4. Update the API base URL when you change the selected year

### Example

If you're accessing Swagger UI in August 2025:
- Default selected year: **2026** (because August > June)
- Available years: All years from your range

If you're accessing Swagger UI in March 2025:
- Default selected year: **2025** (because March ≤ June)
- Available years: All years from your range

## Complete Example

```powershell
# Start DMS with school year instances for 2022-2026
./start-local-dms.ps1 `
    -EnableConfig `
    -EnvironmentFile ./.env `
    -EnableSwaggerUI `
    -LoadSeedData `
    -SchoolYearRange "2022-2026" `
    -r
```

This command:
- Rebuilds images (`-r`)
- Enables Configuration Service (`-EnableConfig`)
- Loads seed data (`-LoadSeedData`)
- Enables Swagger UI (`-EnableSwaggerUI`)
- Creates instances for years 2022-2026

## Single Instance (Default Behavior)

If you don't specify `-SchoolYearRange`, the script will create a single default instance:

```powershell
./start-local-dms.ps1 -EnableConfig -EnvironmentFile ./.env -EnableSwaggerUI -LoadSeedData
```

This creates:
- **1 DMS Instance**: "Local Development Instance"
- **No route contexts**: Accessible at `http://localhost:8080/data`

## Manual Route Context Creation

If you need more complex routing or want to add route contexts manually, you can still use the Configuration Service API. See `test-schoolyear-route.http` in `src/dms/tests/RestClient/` for examples.

## Troubleshooting

### Changes not reflected immediately

After creating instances with route contexts, the DMS may take a moment to refresh its cache. If you don't see the new routes immediately:

1. Wait 20-30 seconds for the cache to refresh
2. Restart the DMS container if needed:
   ```powershell
   docker restart dms-local-edfi-dm-1
   ```

### Invalid year range format

Ensure your range uses the correct format: `"YYYY-YYYY"` with a hyphen separator.

✅ Correct: `-SchoolYearRange "2022-2026"`
❌ Incorrect: `-SchoolYearRange "2022 2026"` or `-SchoolYearRange "22-26"`

## See Also

- `Dms-Management.psm1` - PowerShell module with functions for managing DMS instances
- `test-schoolyear-route.http` - REST Client examples for manual route creation
- `edfi-school-year-from-spec.js` - Swagger UI plugin for school year selection
