# Using Smoke test tool

Please refer [Smoke Test
Tool](https://edfi.atlassian.net/wiki/spaces/ODSAPIS3V72/pages/23299359/Smoke+Test+Utility)
for more details.

## Generating SDK for DMS metadata specification

Use [SdkGen
Console](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/main/Utilities/SdkGen/EdFi.SdkGen.Console)
application to generate sdk for DMS metadata specifications.

### Required code changes

For running the SdkGen tool against DMS, the following code changes are needed
in \Ed-Fi-ODS\Utilities\SdkGen\EdFi.SdkGen.Console\OpenApiCodeGenCliRunner.cs (
while defining the Core Edfi Namespace list)

 ``` none
 var coreEdfiNamespaceList = new[] { @".*/metadata/specifications/resources-spec.json", @".*/metadata/specifications/descriptors-spec.json" };
 ```

Once the C# files are created from the SdkGen process, open the generated
project from
`Ed-Fi-ODS\Utilities\SdkGen\EdFi.SdkGen.Console\bin\Debug\net8.0\csharp\src\EdFi.OdsApi.Sdk`.
Then, move the files from Apis.Descriptors and Apis.Resources to Apis.All
folder, and move the files from Models.Descriptors and Models.Resources to the
Models.All folder. The Smoke test tool will be specifically looking for the
Apis.All and Models.All namespaces. Once the files are rearranged, the
`EdFi.OdsApi.Sdk.dll` can be built and used.

## Automatic Credential Creation

The SmokeTest module includes a `Get-SmokeTestCredential` function that
automatically creates the necessary vendor and application credentials for
smoke testing. This function:

1. Creates system administrator credentials in the Configuration Service
2. Obtains an authentication token
3. Creates a vendor with the required namespace prefixes
4. Creates an application with the appropriate claimset and education
   organization IDs
5. Returns the key and secret for use in smoke tests

### Usage

```powershell
Import-Module ./modules/SmokeTest.psm1 -Force
$credentials = Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081"

# Use the credentials in smoke tests
./Invoke-NonDestructiveApiTests.ps1 -BaseUrl "http://localhost:8080" -Key $credentials.Key -Secret $credentials.Secret
```

### Parameters

- `ConfigServiceUrl` (Required): The URL of the Configuration Service
- `SysAdminId` (Optional): System administrator ID (default: "smoke-test-admin")
- `SysAdminSecret` (Optional): System administrator secret. The default is a fixed
  bootstrap literal defined in `modules/SmokeTest.psm1`, not a generated credential.
- `VendorName` (Optional): Vendor name (default: "Smoke Test Vendor")
- `ApplicationName` (Optional): Application name (default: "Smoke Test Application")
- `ClaimSetName` (Optional): Claim set name (default: "EdFiSandbox")
- `EducationOrganizationIds` (Optional): Array of education organization IDs (default:
  5, 6, 7, 255901, 19255901, 100000, 200000, 300000). The first three keep the TPDM
  sample education organizations reachable, since `educatorPreparationProgram` defaults
  to a `RelationshipsWithEdOrgsOnly` claim.
- `DataStoreIds` (Optional): Array of data store IDs to associate with the application
  (default: empty, in which case the first available data store is used)
- `Tenant` (Optional): Tenant name, for multi-tenant Configuration Service deployments
  (default: empty, meaning single tenant)

The function returns the key and secret to the caller and does not write them to the
console. Keep them in a variable and pass them onward rather than echoing them.

### Example

The `Usage` section above is a complete example. For a working invocation inside a
larger script, see `eng/docker-compose/start-published-dms.ps1`, which calls
`Get-SmokeTestCredential` when run with `-AddSmokeTestCredentials`.
