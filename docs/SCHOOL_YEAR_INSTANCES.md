# School Year Instance Setup

This guide explains how to set up multiple data stores with school year routing.

## Choose a Workflow

There are two ways to end up with multiple school year instances:

1. **Scripted convenience (this guide)**: use `-SchoolYearRange` on `configure-local-data-store.ps1` (after starting the stack with `start-local-dms.ps1`) to automatically create multiple `dataStores` and their `schoolYear` route contexts in the Configuration Service.
2. **Manual/field-like (Configuration API)**: create instances and route contexts yourself (for example, using the REST client `.http` workflow described in the multi-tenancy guide).

These approaches use the same underlying Configuration Service APIs and should result in the same DMS routing behavior and Swagger UI dropdowns.

> [!IMPORTANT]
> As of DMS-1153, `-SchoolYearRange` and `-NoDataStore` live on
> `configure-local-data-store.ps1`, not `start-local-dms.ps1` (which is
> infrastructure-only).
> `-SchoolYearRange` is currently a convenience helper and is not idempotent.
> If you also create instances manually (or re-run the script), you can end up with duplicate instances/route contexts (similar names, different IDs).
> Prefer **one** workflow per environment: either scripted *or* manual.
> `-SchoolYearRange` and `-NoDataStore` are mutually exclusive.
> If `DMS_CONFIG_MULTI_TENANCY=true`, then `-SchoolYearRange` requires
> `CONFIG_SERVICE_TENANT` in the environment file so the script can send the
> required `Tenant` header.

## Overview

The `configure-local-data-store.ps1` phase command supports automatic creation of multiple data stores, each configured with a specific school year route context. This is useful for multi-tenant scenarios where you need to separate data by school year. (`start-local-dms.ps1` is infrastructure-only as of DMS-1153 and no longer creates instances.)

## Quick Start

### Create School Year Instances

To create data stores for a range of school years, start the stack and then run the configure phase with the `-SchoolYearRange` parameter:

```powershell
./start-local-dms.ps1 -EnableConfig -EnvironmentFile ./.env -EnableSwaggerUI
./configure-local-data-store.ps1 -EnvironmentFile ./.env -SchoolYearRange "2022-2026"
```

This will:

1. Start all required services (PostgreSQL, DMS, Configuration Service)
2. Create 5 data stores: one for each year from 2022 to 2026
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

1. **data store**: A database instance with:
   - **Instance Type**: "SchoolYear"
   - **Instance Name**: "School Year {YEAR}"
   - **Connection String**: Configured from your environment file

2. **Route Context**: A routing rule with:
   - **Context Key**: "schoolYear"
   - **Context Value**: The year (e.g., "2024")

## Swagger UI Integration

When you start the stack with `-EnableSwaggerUI` and create instances with `-SchoolYearRange`, the Swagger UI will automatically:

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
# Start DMS, then create school year instances for 2022-2026
./start-local-dms.ps1 `
    -EnableConfig `
    -EnvironmentFile ./.env `
    -EnableSwaggerUI `
    -r

./configure-local-data-store.ps1 `
    -EnvironmentFile ./.env `
    -SchoolYearRange "2022-2026"
```

> [!NOTE]
> If you are using .env file don't forget to uncomment ROUTE_QUALIFIER_SEGMENTS
> and set the value equals to schoolYear

```
ROUTE_QUALIFIER_SEGMENTS=schoolYear
```

These commands:

- Rebuild images (`-r`)
- Enable Configuration Service (`-EnableConfig`)
- Enable Swagger UI (`-EnableSwaggerUI`)
- Create instances for years 2022-2026 (configure phase)

## Single Instance (Default Behavior)

If you don't specify `-SchoolYearRange`, the configure phase will create a single default instance:

```powershell
./start-local-dms.ps1 -EnableConfig -EnvironmentFile ./.env -EnableSwaggerUI
./configure-local-data-store.ps1 -EnvironmentFile ./.env
```

This creates:

- **1 data store**: "Local Development Instance"
- **No route contexts**: Accessible at `http://localhost:8080/data`

## Manual Route Context Creation

If you need more complex routing or want to add route contexts manually, you can still use the Configuration Service API. See `test-schoolyear-route.http` in `src/dms/tests/RestClient/` for examples.

If you are using the manual approach, skip `-SchoolYearRange` on the configure phase (running `configure-local-data-store.ps1 -NoDataStore` against an existing single data store may be appropriate) to avoid creating duplicate configuration data.

## Troubleshooting

### Changes not reflected immediately

After creating instances with route contexts, the DMS may take a moment to refresh its cache. If you don't see the new routes immediately:

1. Wait 20-30 seconds for the cache to refresh
2. Restart the DMS container if needed:

   ```powershell
   docker restart ed-fi-api
   ```

### Invalid year range format

Ensure your range uses the correct format: `"YYYY-YYYY"` with a hyphen separator.

✅ Correct: `-SchoolYearRange "2022-2026"`
❌ Incorrect: `-SchoolYearRange "2022 2026"` or `-SchoolYearRange "22-26"`

## See Also

- `Dms-Management.psm1` - PowerShell module with functions for managing data stores
- `test-schoolyear-route.http` - REST Client examples for manual route creation
- `edfi-route-contexts-from-spec.js` - Swagger UI plugin for tenant and route
   qualifier selection (choosing which school year or tenant route context is used
   when calling the DMS APIs)
