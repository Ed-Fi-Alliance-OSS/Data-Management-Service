# Claims Management E2E Tests

## Overview

This test suite validates the claims management functionality of the DMS Configuration Service, specifically focusing on hybrid mode operation where base claims are loaded from embedded resources and extended with filesystem fragments.

## Prerequisites

### Environment Configuration
The test environment must be configured with the following settings (from `.env.e2e`):

```bash
DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true
DMS_CONFIG_CLAIMS_SOURCE=Hybrid
DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims
```

### Docker Setup
The tests require the full DMS stack to be running:

```bash
cd src/dms/tests/EdFi.DataManagementService.Tests.E2E
./setup-local-dms.ps1
```

### Container Environment Variables
Ensure the following environment variables are set in the Docker configuration:

```yaml
environment:
  - DMS_CONFIG_CLAIMS_SOURCE=Hybrid
  - DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims
  - DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true
```

### Expected Fragment Files
The following fragment files must be present in the mounted volume (`/app/additional-claims`):
- `001-namespace-claimset.json` (E2E-NameSpaceBasedClaimSet)
- `002-nofurtherauth-claimset.json` (E2E-NoFurtherAuthRequiredClaimSet)
- `003-edorgsonly-claimset.json` (E2E-RelationshipsWithEdOrgsOnlyClaimSet)
- `004-sample-extension-claimset.json` (SampleExtensionResourceClaims)
- `005-homograph-extension-claimset.json` (HomographExtensionResourceClaims)

These files are sourced from `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets/` and mounted into the container.

## Test Scenarios

### Scenario 1: Initial Hybrid Mode Verification
Verifies that on startup, the system correctly:
- Loads base claims from embedded resources
- Discovers and applies all fragment files
- Produces a composition matching the authoritative structure

### Scenario 2: Upload Claims
Tests the upload endpoint:
- Captures initial reload ID
- Uploads new claims via POST
- Verifies uploaded claims replace the composed claims

### Scenario 3: Reload Claims
Tests the reload functionality:
- Starts with uploaded claims
- Performs reload via POST
- Verifies original fragment composition is restored

### Scenario 4: Validation Tests
Ensures composed claims are valid:
- No empty arrays (must be null or have items)
- Proper JSON structure

### Scenario 5: Error Handling
Tests invalid claims upload:
- Sends malformed claims
- Verifies 400 response with validation errors

### Scenario 6: Security
Verifies endpoints require dynamic claims loading to be enabled

## Troubleshooting

### Test Failures

1. **"Authoritative composition not loaded"**
   - Ensure `TestData/Claims/authoritative-composition.json` exists
   - Verify the file is being copied to output directory

2. **"Expected header 'X-Reload-Id' was not found"**
   - Check that dynamic claims loading is enabled
   - Verify the Configuration Service is running with correct environment variables

3. **"Claim set 'E2E-NameSpaceBasedClaimSet' was not found"**
   - Verify fragment files are in `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets/`
   - Check Docker volume mounting in `local-config.yml` and `local-dms.yml`
   - Ensure fragment files follow naming pattern: `*-claims.json`

4. **404 responses from management endpoints**
   - Dynamic claims loading is not enabled
   - Check `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true`

5. **Claims not loading in hybrid mode**
   - Verify `DMS_CONFIG_CLAIMS_SOURCE=Hybrid`
   - Check `DMS_CONFIG_CLAIMS_DIRECTORY` points to correct path
   - Ensure fragment files are properly mounted

### Running Individual Tests

To run a specific scenario:
```bash
dotnet test --filter "FullyQualifiedName~ClaimsManagement" --filter "Name~scenario_name"
```

### Debugging

1. Check Docker logs:
   ```bash
   docker logs dms-local-config-1
   ```

2. Verify claims are loaded:
   ```bash
   curl -H "Authorization: Bearer $TOKEN" http://localhost:8081/management/current-claims
   ```

3. Check fragment discovery:
   - Look for log entries like "Discovered X fragment files"
   - Verify fragment files have correct naming pattern

### Migration Notes

If working with older test configurations, update the following environment variables:
- `DMS_CONFIG_USE_CLAIMS_PATH=true` + `DMS_CONFIG_USE_EMBEDDED_BASE_CLAIMS=true` → `DMS_CONFIG_CLAIMS_SOURCE=Hybrid`
- `DMS_CONFIG_CLAIMS_PATH` → `DMS_CONFIG_CLAIMS_DIRECTORY`
- `DMS_CONFIG_DANGEROUSLY_ENABLE_DYNAMIC_CLAIMS_LOADING` → `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING`

