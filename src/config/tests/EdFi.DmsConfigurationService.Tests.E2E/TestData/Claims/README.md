# Claims Test Data

This directory contains reference data for E2E claims management tests.

## Files

- [authoritative-composition.json](authoritative-composition.json) - Reference hierarchy
  snapshot loaded by the ClaimsManagement hybrid composition scenarios. It is a
  hierarchy array, not a complete upload document. The current hybrid inputs are the
  [Data Standard 5.2 base claims](../../../../backend/EdFi.DmsConfigurationService.Claims/Claims/Standards/ds52/Claims.json),
  the test-owned fragments below, and the shipped
  [Sample and Homograph extension fragments](../../../../backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaims/).

- `Fragments/` - The test-owned claims fragments `001` through `003c`, which define the six `E2E-*` claim
  sets. They are not shipped with the Configuration Service; the E2E setup stages them with the default
  `004`/`005` extension fragments into `eng/docker-compose/.e2e-claims/`, which is mounted as the claims
  directory.
  - `e2e-readiness-checks.json` - One canonical readiness probe per E2E claim set (claim set, full leaf
    resource-claim URI, action). Bootstrap staging with `-IncludeE2EClaimSets` records these for the
    claims-ready gate. The file is not named `*-claims.json`, so it is never loaded as a fragment.

- [ODS security metadata acceptance fixtures](../../../../clis/EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit/Fixtures/OdsSecurityMetadata/README.md) -
  The shared `representative.xml` is an actual export from the documented seeded
  ODS security database. The CLI unit tests compare conversion with the independently
  prepared `expected-claims.json` complete document. The E2E project links the same
  XML into `TestData/Claims/OdsSecurityMetadata/representative.xml` in its build output
  and stages the built CLI under `SecurityMetadataTools/`.

The base policies and JSON schema are embedded once in
`EdFi.DmsConfigurationService.Claims`, shared by CMS and the offline tool.
The [isolated policy scenarios](SecurityMetadataTools/README.md) reuse reviewed
Hybrid fragments for merge/resolve parity, unchanged Filesystem/upload deployment,
and an independent editable baseline for installed-tool drift verification.

## Usage

The test suite uses this data to verify:

1. Initial hybrid mode composition matches the expected structure
2. Claims are properly restored after upload/reload cycles
3. The SecurityMetadataTools scenario converts the shared export, uploads the generated
   bytes unchanged, and compares both the current provider document and independently
   queried database hierarchy/catalog. Cleanup restores the original database state
   and standard provider claims, including after an assertion failure.

For manual conversion, use the installed
[Ed-Fi API Security Metadata Tools](../../../../clis/EdFi.DmsConfigurationService.SecurityMetadataTools/README.md)
command with .NET 10:

```sh
security-metadata-tools convert-ods-xml --input export.xml --output Claims.json
```

See [installation and verified export scope](../../../../../../docs/DATA-STANDARD-VERSIONS.md#converting-ods-security-metadata).
The linked conversion guidance documents project-qualified legacy resource URIs
and rejection of remaining whitespace in normalized names, including nonbreaking
spaces. Preserve these rules when updating the shared fixture or upload scenario.
The output is a complete document with top-level `claimSets` and `claimsHierarchy`.
Send that file unchanged to `POST /management/upload-claims`; no wrapper, catalog
edits, or strategy substitutions are needed. The local E2E stack enables
`CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true`, and the
scenario uses its existing administrative authentication. See the
[Claims Loading Deployment Guide](../../../../../../docs/CLAIMS-LOADING-GUIDE.md)
for manual loading prerequisites. Imported catalog entries are editable
(`isSystemReserved: false`); an existing reserved set with the same name keeps its
stored reserved status. Successful conversion and upload do not prove runtime
support for every imported authorization strategy.

