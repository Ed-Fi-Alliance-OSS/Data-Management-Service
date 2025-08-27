# CmsHierarchy

The CmsHierarchy project provides tools for parsing XML files and transforming
JSON files to manage claim hierarchies. This project includes the following
features:

* Parsing XML files to extract claim hierarchies
* Transforming existing claims by applying modifications specified in JSON
  files
* Outputting the results to a file or as JSON content

## Features

### ParseXml

> [!NOTE]
> Here is the SQL script for generating the security metadata XML file
> [Security Metadata XML File Script](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/blob/main/SecurityMetadata/Export/MsSql/Security-Metadata-to-XML.sql)

The ParseXml command parses an XML file to extract claim hierarchies and outputs
the result as JSON.

### Transform

The Transform command transforms existing claims by applying modifications
specified in one or more JSON files and outputs the result as JSON.

**Note:** Some of this behavior can now be performed at startup by CMS itself.

## Usage

### Command Line Arguments

The program accepts the following command line arguments:

* --command: The command to execute (ParseXml or Transform)
* --input: The input file path (XML for ParseXml, JSON for Transform). For
  Transform, multiple JSON files can be specified, separated by semicolons (`;`)
   > [!NOTE]
   > For now, we are placing the ClaimSet JSON files and the
   > AuthorizationHierarchy file into the ClaimSetFiles folder. If anyone wants
   > to transform and inject claims, they need to place the new ClaimSet JSON
   > file into this folder ( make sure to set `Copy to output directory`) and
   > add the file name to the semicolon-separated list
   > while running the command.
* --output: The output file path for the resulting JSON
* --outputFormat: The output format (ToFile or Json)
* --skipAuths: Not implemented auth strategies, separated by semicolon (`;`)

## Example Usage

```powershell
# To parse an XML file and write the result to a file:
dotnet run --no-launch-profile --command ParseXml --input input.xml --output output.json --outputFormat ToFile

# To parse an XML file and print the result as JSON:
dotnet run --no-launch-profile --command ParseXml --input input.xml --outputFormat Json

# To transform claims using one or more JSON files and write the result to a file:
dotnet run --no-launch-profile --command Transform --input input1.json;input2.json --output output.json --outputFormat ToFile

# To transform claims using one or more JSON files and print the result as JSON:
dotnet run --no-launch-profile --command Transform --input input1.json;input2.json --outputFormat Json

# To transform claims using one or more JSON files, remove NotImplementedAuth auth strategies and print the result as JSON:
dotnet run --no-launch-profile --command Transform --input input1.json;input2.json --outputFormat Json --skipAuths NotImplementedAuth

```

## Steps for Adding the ClaimSet as Claims to Hierarchy JSON

* Create or generate the required `ClaimSet` content with existing format and
  save it as JSON file and place it under `ClaimSetFiles` folder
* Ensure you have the latest `AuthorizationHierarchy.json` file
* Execute the application to process the ClaimSet and generate the updated
  authorization hierarchy JSON
* Use the command line arguments to specify the input and output files
* Once you have the updated authorization hierarchy JSON, then update the VALUE
  in `dmscs.claimshierarchy` table [Claims Hierarchy Script](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0011_Insert_ClaimsHierarchy.sql)
* Add new ClaimSet details to VALUES [Insert
  ClaimSets](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0010_Insert_Claimset.sql)

## Steps for Adding Extension-Specific Security Metadata to Hierarchy JSON

### Define Your Extension Resource Claims

  Create or update your extension’s resource claims in a JSON file (e.g., SampleExtensionResourceClaims.json).

### Transform Claims into Authorization Hierarchy

  Follow the [Steps for Adding the ClaimSet as Claims to Hierarchy
  JSON](#steps-for-adding-the-claimset-as-claims-to-hierarchy-json) to transform
  the files in to authorization hierarchy.
  
> [!NOTE]
> By default, all E2E*.json files (such as E2E-ExtensionsClaimSet.json) must be
  included along with your extension claims file (e.g.,
  SampleExtensionResourceClaims.json) as input when running the application.
  These files are required to support the execution of end-to-end (E2E) tests.
