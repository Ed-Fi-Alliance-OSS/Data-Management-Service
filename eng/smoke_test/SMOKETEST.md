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

> [!NOTE]
> The generated SDK for DMS metadata specifications (ODS API version 7.2
> with Data standard 5.1.0) is available
> [here](https://odsassets.blob.core.windows.net/public/project-tanager/sdk/5.1.0/EdFi.OdsApi.Sdk.zip).
