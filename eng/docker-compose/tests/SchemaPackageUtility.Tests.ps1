# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

param()

Describe "schema-package-utility" {
    BeforeAll {
        $script:modulePath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../schema-package-utility.psm1")
        )

        function script:New-TestDirectory {
            $path = Join-Path ([System.IO.Path]::GetTempPath()) "api-schema-tools-package-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            return $path
        }
    }

    It "resolves schema files from package manifests and ignores downloaded package copies" {
        $workspace = New-TestDirectory
        $previousPath = $env:PATH

        try {
            $repoRoot = Join-Path $workspace "repo"
            $schemaDirectory = Join-Path $workspace "schema"
            $stubDirectory = Join-Path $workspace "bin"
            New-Item -ItemType Directory -Path $repoRoot, $schemaDirectory, $stubDirectory -Force | Out-Null

            $environmentFile = Join-Path $workspace ".env"
            @'
SCHEMA_PACKAGES='[
  {
    "version": "1.2.3",
    "feedUrl": "https://example.test/nuget/v3/index.json",
    "name": "Example.ApiSchema"
  }
]'
'@ | Set-Content -LiteralPath $environmentFile -Encoding utf8

            $fakeDotnetPath = Join-Path $stubDirectory "dotnet"
            @'
#!/usr/bin/env bash
set -euo pipefail

package_name=""
output_directory=""

while [ "$#" -gt 0 ]; do
    case "$1" in
        -p)
            package_name="$2"
            shift 2
            ;;
        -d)
            output_directory="$2"
            shift 2
            ;;
        *)
            shift
            ;;
    esac
done

if [ -z "$package_name" ] || [ -z "$output_directory" ]; then
    echo "Expected -p and -d arguments." >&2
    exit 1
fi

downloaded_package_schema_directory="$output_directory/DownloadedPackages/$package_name/contentFiles/any/any/ApiSchema"
generated_package_directory="$output_directory/Packages/$package_name"

mkdir -p "$downloaded_package_schema_directory" "$generated_package_directory"

printf "{}" > "$downloaded_package_schema_directory/ApiSchema.json"
printf "{}" > "$generated_package_directory/ApiSchema.json"

cat > "$generated_package_directory/package-manifest.json" <<MANIFEST
{
  "version": "1",
  "projectName": "$package_name",
  "projectEndpointName": "example",
  "isExtensionProject": false,
  "schemaPath": "ApiSchema.json",
  "discoverySpecPath": null,
  "xsdDirectory": null
}
MANIFEST

echo "fake package download complete"
exit 0
'@ | Set-Content -LiteralPath $fakeDotnetPath -Encoding utf8

            if ($IsLinux -or $IsMacOS) {
                chmod +x $fakeDotnetPath
            }
            else {
                @'
@echo off
setlocal

set "package_name="
set "output_directory="

:parse
if "%~1"=="" goto done
if "%~1"=="-p" (
    set "package_name=%~2"
    shift
    shift
    goto parse
)
if "%~1"=="-d" (
    set "output_directory=%~2"
    shift
    shift
    goto parse
)
shift
goto parse

:done
if "%package_name%"=="" (
    echo Expected -p and -d arguments. 1>&2
    exit /b 1
)
if "%output_directory%"=="" (
    echo Expected -p and -d arguments. 1>&2
    exit /b 1
)

set "downloaded_package_schema_directory=%output_directory%\DownloadedPackages\%package_name%\contentFiles\any\any\ApiSchema"
set "generated_package_directory=%output_directory%\Packages\%package_name%"

mkdir "%downloaded_package_schema_directory%"
mkdir "%generated_package_directory%"

> "%downloaded_package_schema_directory%\ApiSchema.json" echo {}
> "%generated_package_directory%\ApiSchema.json" echo {}

(
echo {
echo   "version": "1",
echo   "projectName": "%package_name%",
echo   "projectEndpointName": "example",
echo   "isExtensionProject": false,
echo   "schemaPath": "ApiSchema.json",
echo   "discoverySpecPath": null,
echo   "xsdDirectory": null
echo }
) > "%generated_package_directory%\package-manifest.json"

echo fake package download complete
exit /b 0
'@ | Set-Content -LiteralPath (Join-Path $stubDirectory "dotnet.cmd") -Encoding ascii
            }

            $env:PATH = "$stubDirectory$([System.IO.Path]::PathSeparator)$previousPath"

            Import-Module $script:modulePath -Force

            $schemaFiles = @(Resolve-SchemaFilesFromEnvironmentFile `
                    -EnvironmentFilePath $environmentFile `
                    -Configuration Release `
                    -RepoRoot $repoRoot `
                    -SchemaDirectory $schemaDirectory `
                    -DownloaderDotnetRunArgs @("--no-launch-profile"))

            $expectedSchemaFile = [System.IO.Path]::GetFullPath(
                (Join-Path $schemaDirectory "Packages/Example.ApiSchema/ApiSchema.json")
            )
            $downloadedSchemaFile = Join-Path $schemaDirectory "DownloadedPackages/Example.ApiSchema/contentFiles/any/any/ApiSchema/ApiSchema.json"

            $schemaFiles | Should -HaveCount 1
            $schemaFiles[0] | Should -Be $expectedSchemaFile
            Test-Path -LiteralPath $downloadedSchemaFile -PathType Leaf |
                Should -BeTrue -Because "the duplicate downloaded package copy should not be selected"
        }
        finally {
            $env:PATH = $previousPath
            Remove-Module schema-package-utility -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
