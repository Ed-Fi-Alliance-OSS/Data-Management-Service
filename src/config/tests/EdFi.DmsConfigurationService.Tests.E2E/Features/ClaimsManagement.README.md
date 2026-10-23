# Claims Management E2E Tests

## Overview

This test suite validates the claims management functionality of the Configuration Service, specifically focusing on hybrid mode operation where base claims are loaded from embedded resources and extended with filesystem fragments.

## Prerequisites

### Environment Configuration
The test environment must be configured with the following settings (from `.env.e2e`):

```bash
CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true
CONFIGURATION_SERVICE_CLAIMS_SOURCE=Hybrid
CONFIGURATION_SERVICE_CLAIMS_DIRECTORY=/app/additional-claims
```

### Docker Setup
The tests use the isolated Configuration Service `cs-local` stack:

```bash
cd src/config/tests/EdFi.DmsConfigurationService.Tests.E2E
pwsh ./setup-local-cms.ps1
```

### Container Environment Variables
Ensure the following environment variables are set in the Docker configuration:

```yaml
environment:
  - CONFIGURATION_SERVICE_CLAIMS_SOURCE=Hybrid
  - CONFIGURATION_SERVICE_CLAIMS_DIRECTORY=/app/additional-claims
  - CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true
```

### Expected Fragment Files
The following fragment files must be present in the mounted volume (`/app/additional-claims`):
- `001-namespace-claims.json` (E2E-NameSpaceBasedClaimSet)
- `002-nofurtherauth-claims.json` (E2E-NoFurtherAuthRequiredClaimSet)
- `003-edorgsonly-claims.json` (E2E-RelationshipsWithEdOrgsOnlyClaimSet)
- `003a-edorgsonly-inverted-claims.json` (E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet)
- `003b-edorgsonly-or-inverted-claims.json` (E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet)
- `003c-edorgsonly-mixed-claims.json` (E2E-RelationshipsWithEdOrgsOnlyMixedStrategyClaimSet)
- `004-sample-extension-claims.json` (top-level name `SampleExtensionClaims`; label only, parent-only fragment, grants attach to `EdFiSandbox`)
- `005-homograph-extension-claims.json` (top-level name `HomographExtensionClaims`; label only, parent-only fragment, grants attach to `EdFiSandbox`)

The six E2E fragments (`001` through `003c`) come from the test-owned `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/Fragments/` directory, and `004`/`005` come from `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaims/`. The E2E setup stages both into `eng/docker-compose/.e2e-claims/` and mounts that directory into the container: `setup-local-cms.ps1` and `build-config.ps1 E2ETest` pass `-AddE2EClaimSets` to `start-local-config.ps1`, and the DMS E2E setup does the same through `-AddExtensionSecurityMetadata`. The E2E claim sets are not declared in the embedded `Claims.json`; the Configuration Service registers each one from its fragment's top-level `name`.

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

### Scenario 6: Round-Trip Upload
Tests that a downloaded claims document can be uploaded unmodified:
- Captures the `GET /management/current-claims` response body verbatim, with its reload ID
- Posts those exact bytes to `POST /management/upload-claims`
- Verifies HTTP 200 with a new reload ID, and that the claim sets and hierarchy are unchanged afterward

### Scenario 7: SecurityMetadataTools Upload

Runs the built `security-metadata-tools` executable against the shared
[representative ODS export](../../../clis/EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit/Fixtures/OdsSecurityMetadata/README.md).
A project reference builds the tool, and a build target stages its complete runtime
output in a separate `SecurityMetadataTools` directory so the test host's shared-framework
dependencies cannot mask missing CLI dependencies. The XML is linked from its original fixture location. The scenario uploads the generated file bytes through
`PostVerbatimBodyAsync`, without a wrapper, placeholder substitution, or policy edits.

The provider must return the complete converted document, including `isSystemReserved: false`
for every imported set. Separate database reads compare the full hierarchy after deserializing
both sides through CMS's `Claim` model and serializing with the persistence defaults. This
accounts for absent defaults becoming null and absent collections becoming empty arrays;
claim order, nesting, actions, strategy names and overrides still compare exactly. The
persisted catalog must contain the captured reserved entries plus imported names that are
not already reserved. Existing reserved IDs/flags must survive, and at least one previously
absent imported name must be inserted with `isSystemReserved: false`.

Before changing state, the scenario requires no applications, captures both database tables,
and saves their baseline. It reloads the standard provider, removes nonreserved catalog
entries, and temporarily removes the first imported name to guarantee a real insert: the
standard seed already reserves every name in this export. The remaining reserved rows form
the upload baseline and must keep their IDs/flags. The removed row is test preparation only;
the full original catalog, including that row's reserved flag and ID, is restored afterward. A tagged `AfterScenario` hook reloads the standard provider
and restores the original catalog IDs, flags, tenant/audit fields and hierarchy, even after
an assertion fails. Database restoration runs in `finally`, including if provider cleanup fails;
cleanup errors fail the scenario. Run against an isolated test deployment, never a shared CMS.

From the repository root, build fresh binaries and run the named scenario:

```bash
pwsh ./src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/teardown-local-cms.ps1
pwsh ./build-config.ps1 Build -Configuration Release
env -u NODE_OPTIONS pwsh ./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -E2ETestFilter 'FullyQualifiedName~SecurityMetadataTools'
```

Run the full feature against that stack with `-SkipDockerBuild` and
`-E2ETestFilter 'FullyQualifiedName~ClaimsManagement'`. The existing PostgreSQL CI E2E
run discovers the scenario without workflow changes. The database helper also uses the
existing SQL Server connection conventions when that backend is explicitly selected.
Inspect the new TRX result for `_07SecurityMetadataToolsConvertedODSExportUploadsUnchangedAndPersists`:
a skipped scenario or zero matching tests is not verification. TRX attachments retain the
unmodified generated file, provider response, baseline/isolated/persisted catalogs, persisted
hierarchy, and restored provider/database state under `TestResults/SecurityMetadataTools-*`.

## Authorization Coverage

This feature's `Background` obtains a **full-access** Configuration Service token (`Given valid credentials` / `And token received`) and sends it on every request, so scenarios 01–07 exercise only the **authenticated happy path** (including the 400 validation case in scenario 05). The feature does **not** contain a negative-authorization scenario.

The negative authorization behavior of the `/management/*` claims endpoints is covered by the focused in-process unit tests in `ClaimsManagementModuleTests` (project `EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit`), not by this E2E feature:

- **401** — a request without a bearer token, with the dangerous flag both enabled and disabled.
- **403** — a valid token that is not authorized: a read-only scope on the write endpoints, an unsupported scope on the read endpoint, or a principal lacking the configuration-service role.
- **404** — an authorized request while `DangerouslyEnableUnrestrictedClaimsLoading` is disabled.

## Troubleshooting

### Test Failures

1. **"Authoritative composition not loaded"**
   - Ensure `TestData/Claims/authoritative-composition.json` exists
   - Verify the file is being copied to output directory

2. **"Expected header 'X-Reload-Id' was not found"**
   - Check that dynamic claims loading is enabled
   - Verify the Configuration Service is running with correct environment variables

3. **"Claim set 'E2E-NameSpaceBasedClaimSet' was not found"**
   - Verify the E2E fragment files are in `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/Fragments/`
   - Verify the stack was started through the E2E setup, so `eng/docker-compose/.e2e-claims/` holds all eight fragments and is the mounted claims directory; a default stack mounts only `Deploy/AdditionalClaims` and has no E2E claim sets
   - Check Docker volume mounting in `local-config.yml` and `local-dms.yml`
   - Ensure fragment files follow naming pattern: `*-claims.json`

4. **404 responses from management endpoints**
   - Dynamic claims loading is not enabled
   - Check `CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true`

5. **Claims not loading in hybrid mode**
   - Verify `CONFIGURATION_SERVICE_CLAIMS_SOURCE=Hybrid`
   - Check `CONFIGURATION_SERVICE_CLAIMS_DIRECTORY` points to correct path
   - Ensure fragment files are properly mounted

### Running Individual Tests

To run a specific scenario:
```bash
env -u NODE_OPTIONS pwsh ./build-config.ps1 E2ETest -Configuration Release -SkipDockerBuild -E2ETestFilter 'FullyQualifiedName~ClaimsManagement'
```

### Debugging

1. Check Docker logs:
   ```bash
   docker logs ed-fi-api-config-service
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
- `DMS_CONFIG_USE_CLAIMS_PATH=true` + `DMS_CONFIG_USE_EMBEDDED_BASE_CLAIMS=true` → `CONFIGURATION_SERVICE_CLAIMS_SOURCE=Hybrid`
- `DMS_CONFIG_CLAIMS_PATH` → `CONFIGURATION_SERVICE_CLAIMS_DIRECTORY`
- `DMS_CONFIG_DANGEROUSLY_ENABLE_DYNAMIC_CLAIMS_LOADING` → `CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING`
