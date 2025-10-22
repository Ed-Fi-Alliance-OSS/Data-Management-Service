# How to Load Southridge Data into DMS

This document describes the steps to load the Southridge sample dataset into a locally-running DMS instance.

## Prerequisites

- DMS and CMS must be running locally (started with `start-dms-local.ps1` and `start-cms-local.ps1`)
- PostgreSQL must be running on localhost:5432
- The initialization script `initialize-local-dms-setup.ps1` must have been run

## Known Issues

### 7-Zip Extraction Problem

The `Invoke-LoadSouthridge.ps1` script attempts to extract the Southridge 7z archive using the `7Zip4Powershell` module, which fails on Linux with a kernel32.dll dependency error:

```
Unable to load shared library 'kernel32.dll' or one of its dependencies
```

**Solution**: The Southridge data archive is already extracted to `eng/.packages/southridge-xml-2023/`. Skip the extraction step and load the data directly.

## Step 1: Verify Data is Extracted

Check that the Southridge XML files are present:

```bash
ls eng/.packages/southridge-xml-2023/
```

You should see files like:
- `Descriptors-2023-2024 School Year.xml`
- `EducationOrganization-2023-2024 School Year.xml`
- `Student-*.xml`
- etc.

## Step 2: Verify API Application Credentials

The `initialize-local-dms-setup.ps1` script automatically creates a data load application with the `EdfiSandbox` ClaimSet. The credentials are saved in `dataload-creds.json` at the repository root.

**Verify credentials exist:**

```bash
cat dataload-creds.json
```

You should see a JSON file with `key`, `secret`, `claimSet`, `namespacePrefixes`, and `educationOrganizationIds`.

**If the credentials file doesn't exist**, create the application manually:

```pwsh
# From the repository root
Import-Module ./eng/Dms-Management.psm1 -Force

# Get admin token
$adminToken = Get-CmsToken -CmsUrl 'http://localhost:8081' -ClientId 'dms-local-admin' -ClientSecret 'LocalSetup1!'

# Create vendor
$vendorId = Add-Vendor -CmsUrl 'http://localhost:8081' -Company 'Data Load Vendor' -ContactName 'Data Loader' -ContactEmailAddress 'dataload@example.com' -NamespacePrefixes 'uri://ed-fi.org' -AccessToken $adminToken

# Create application with EdfiSandbox ClaimSet (provides full access)
$creds = Add-Application -CmsUrl 'http://localhost:8081' -VendorId $vendorId -ApplicationName 'Data Load Application' -ClaimSetName 'EdfiSandbox' -AccessToken $adminToken -EducationOrganizationIds @(255901, 19255901)

# Save credentials
@{
    key = $creds.Key
    secret = $creds.Secret
    claimSet = "EdfiSandbox"
    namespacePrefixes = "uri://ed-fi.org"
    educationOrganizationIds = @(255901, 19255901)
} | ConvertTo-Json -Depth 10 | Set-Content -Path ./dataload-creds.json
```

## Step 3: Load the Southridge Dataset

Run the bulk load script using the credentials from `dataload-creds.json`:

```pwsh
# From the repository root
cd eng/bulkLoad

# Load credentials
$creds = Get-Content ../../dataload-creds.json | ConvertFrom-Json

# Run bulk load
pwsh -NoProfile -c "
Import-Module ../Package-Management.psm1 -Force
Import-Module ./modules/Get-XSD.psm1 -Force
Import-Module ./modules/BulkLoad.psm1 -Force

\`$paths = Initialize-ToolsAndDirectories
\`$paths.SampleDataDirectory = Resolve-Path '../.packages/southridge-xml-2023'

Write-Host 'Loading Southridge dataset (including descriptors)...'
Write-Southridge -BaseUrl 'http://localhost:8080' -Key '$($creds.key)' -Secret '$($creds.secret)' -Paths \`$paths
"
```

### What This Does

1. Initializes the bulk load tools and downloads XSD schemas from DMS
2. Loads API metadata and dependencies from DMS
3. Reads all XML files from the Southridge directory
4. Posts resources to DMS in dependency order (descriptors first, then education organizations, then students, etc.)

The process may take several minutes depending on the dataset size.

## Troubleshooting

### 401 Unauthorized Errors

If you see authentication errors, verify:
1. CMS is running on http://localhost:8081
2. DMS is running on http://localhost:8080
3. The data load application was created successfully (check `dataload-creds.json` exists)
4. You're using the correct credentials from `dataload-creds.json`

**Test DMS authentication:**
```pwsh
Import-Module ./eng/Dms-Management.psm1 -Force
$creds = Get-Content ./dataload-creds.json | ConvertFrom-Json
$token = Get-DmsToken -DmsUrl 'http://localhost:8080' -Key $creds.key -Secret $creds.secret
Write-Host "Token obtained: $($token.Substring(0,50))..."
```

This should return a JWT token if authentication is successful.

### 403 Authorization Denied Errors

If you see "Authorization Denied" errors during data loading, this indicates the application's ClaimSet doesn't have permission to access the requested resources.

**Solution:** Ensure the application uses the `EdfiSandbox` ClaimSet (which has full access):

```pwsh
# Verify ClaimSet in credentials file
$creds = Get-Content ./dataload-creds.json | ConvertFrom-Json
Write-Host "ClaimSet: $($creds.claimSet)"  # Should be "EdfiSandbox"
```

If the ClaimSet is not `EdfiSandbox`, you'll need to recreate the application (see Step 2).

### 429 Too Many Requests Errors

If you see "Too Many Requests - 429" errors during bulk loading, this indicates DMS is rate limiting the requests. The default rate limit (5,000 requests per 10 seconds) is too low for bulk data loading.

**Solution:** Increase the rate limit in DMS configuration:

1. Edit `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.Development.json`
2. Add or modify the `RateLimit` section:

```json
"RateLimit": {
    "PermitLimit": 10000000,
    "Window": 10,
    "QueueLimit": 0
}
```

This configuration allows 10 million requests per 10 seconds, which is suitable for bulk loading operations.

3. Restart DMS to apply the new configuration:

```bash
# Stop DMS (Ctrl+C in the DMS terminal)
./start-dms-local.ps1
```

**Note:** The default configuration in `appsettings.json` has a `PermitLimit` of 5000, which is appropriate for normal API usage but insufficient for bulk data loading. The Development environment override eliminates rate limiting for local development and testing.

### Missing Bootstrap Directory Error

The `Write-Descriptors` function expects a `Bootstrap` subdirectory, which doesn't exist in the Southridge dataset. Use `Write-Southridge` instead, which loads all XML files from the root directory.

## Alternative: Using the Invoke-LoadSouthridge.ps1 Script

If the 7-zip extraction problem is fixed, you can use the convenience script:

```pwsh
# After fixing 7-zip issue
./eng/bulkLoad/Invoke-LoadSouthridge.ps1 -Key 'dataload' -Secret 'DataLoad123!' -FullDataSet
```

The `-FullDataSet` switch loads the complete Southridge dataset instead of just descriptors.
