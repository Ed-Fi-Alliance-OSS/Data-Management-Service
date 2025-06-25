# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdLetBinding()]
<#
    .SYNOPSIS
        Automation script for running sdk build operations from the command line.

    .DESCRIPTION
        Provides automation of the following tasks:

        * BuildAndGenerateSdk: runs `dotnet clean`
        * Package: builds package for the Data Management Service SDK
        * Push: uploads a NuGet package to the NuGet feed
    .EXAMPLE
        .\build-sdk.ps1 BuildAndGenerateSdk

        Generates the SDK using openapi codegen cli.

    .EXAMPLE
        .\build-dms.ps1 Package

        Output: a nuget package of the sdk dll.

    .EXAMPLE
        .\build-dms.ps1 push -NuGetApiKey $env:nuget_key
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive')]
param(
    # Command to execute, defaults to "BuildAndGenerateSdk".
    [string]
    [ValidateSet("BuildAndGenerateSdk", "Package", "Push")]
    $Command = "BuildAndGenerateSdk",

    # Assembly and package version number for the Data Management Service SDK. The
    # current package number is configured in the build automation tool and
    # passed to this script.
    [string]
    $SdkVersion = "0.1",

    # Ed-Fi's official NuGet package feed for package download and distribution.
    [string]
    $EdFiNuGetFeed = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",

    # API key for accessing the feed above. Only required with with the Push
    # command.
    [string]
    $NuGetApiKey,

    # Full path of a package file to push to the NuGet feed. Optional, only
    # applies with the Push command. If not set, then the script looks for a
    # NuGet package corresponding to the provided $DmsVersion and $BuildCounter.
    [string]
    $PackageFile,

    [bool]
    $DryRun = $false,

    # Base DSM url
    [string]
    $DmsUrl = "http://localhost:8080",

    # Output Folder
    [string]
    $OutputFolder = "./eng/sdkGen/csharp",

    # Package name for the NuGet package
    [string]
    [ValidateSet("EdFi.OdsApi.Sdk", "EdFi.OdsApi.TestSdk")]
    $PackageName = "EdFi.OdsApi.Sdk",

    [string]
    $StandardVersion = "5.2.0"
)

Import-Module -Name "$PSScriptRoot/eng/build-helpers.psm1" -Force

$PackageName = "EdFi.OdsApi.Sdk"
$solutionRoot = "$PSScriptRoot/$OutputFolder"
$projectPath = "$solutionRoot/src/$PackageName/$PackageName.csproj"
$nuspecPath = "$PSScriptRoot/eng/sdkGen/$PackageName.nuspec"

function DownloadCodeGen {
    if (-not (Test-Path -Path openApi-codegen-cli.jar)) {
        Invoke-WebRequest -OutFile openApi-codegen-cli.jar https://repo1.maven.org/maven2/org/openapitools/openapi-generator-cli/7.9.0/openapi-generator-cli-7.9.0.jar
    }
}

function GenerateSdk {
    param (
        [string]
        $ApiPackage,

        [string]
        $ModelPackage,

        [string]
        $Endpoint
    )

    # Download and parse OpenAPI spec
    $spec = Invoke-WebRequest -Uri $Endpoint | ConvertFrom-Json

    # Find all operationIds that contain an underscore
    $operationIds = $spec.paths.PSObject.Properties.Value | ForEach-Object {
        $_.PSObject.Properties.Value | Where-Object { $_.operationId -and $_.operationId -like "*_*" } | ForEach-Object { $_.operationId }
    }

    # Normalize operationId to camelCase without underscores (for the left side of mapping)
    function Normalize-OperationId {
        param($opId)
        $parts = $opId -split '_'
        $camel = $parts[0] + ($parts[1..($parts.Count-1)] | ForEach-Object { $_.Substring(0,1).ToUpper() + $_.Substring(1) } | ForEach-Object { $_ }) -join ''
        return $camel
    }

    # Capitalize the first character of the string
    function Capitalize-FirstChar {
        param($s)
        if ($s.Length -eq 0) { return $s }
        return $s.Substring(0,1).ToUpper() + $s.Substring(1)
    }

    # Build mappings string: left = normalized, right = original with first char uppercased
    $mappings = ($operationIds | Sort-Object -Unique | ForEach-Object { "$(Normalize-OperationId $_)=$(Capitalize-FirstChar $_)" }) -join ","
    # Example --operation-id-name-mappings deleteHomographContactsById=Delete_HomographContactsById

    & java -Xmx5g -jar openApi-codegen-cli.jar generate `
    -g csharp `
    -i $Endpoint `
    --api-package $ApiPackage `
    --model-package $ModelPackage `
    -o $OutputFolder `
    --operation-id-name-mappings $mappings `
    --additional-properties "packageName=$PackageName,targetFramework=net8.0,netCoreProjectFile=true" `
    --global-property modelTests=false `
    --global-property apiTests=false `
    --global-property apiDocs=false `
    --global-property modelDocs=false `
    --skip-validate-spec

}

function BuildSdk {
    Invoke-Execute { dotnet build $projectPath -c Release }
}

function BuildPackage {
    Invoke-Step { RunNuGetPack }
}

function RunNuGetPack {

    $copyrightYear = (Get-Date).year

    # This worksaround an issue using -p:NuspecProperties (https://github.com/dotnet/sdk/issues/15482)
    # where only the first property is parsed correctly
    [xml] $xml = Get-Content $nuspecPath
    $xml.package.metadata.id = "$PackageName.Standard.$StandardVersion"
    $xml.package.metadata.copyright = "Copyright @ $copyrightYear Ed-Fi Alliance, LLC and Contributors"
    $xml.Save($nuspecPath)

    # NU5100 is the warning about DLLs outside of a "lib" folder. We're
    # deliberately using that pattern, therefore we bypass the
    # warning.
    &dotnet pack $projectPath `
        --no-build `
        --no-restore `
        --output $PSScriptRoot `
        -p:NuspecFile=$nuspecPath `
        -p:NoDefaultExcludes=true `
        -p:NuspecProperties="version=$SdkVersion;configuration=Release;year=$copyrightYear" `
        /p:NoWarn=NU5100
}

function PushPackage {
    Invoke-Execute {
        if (-not $NuGetApiKey) {
            throw "Cannot push a NuGet package without providing an API key in the `NuGetApiKey` argument."
        }

        if (-not $EdFiNuGetFeed) {
            throw "Cannot push a NuGet package without providing a feed in the `EdFiNuGetFeed` argument."
        }

        if ($DryRun) {
            Write-Info "Dry run enabled, not pushing package."
        }
        else {
            Write-Info ("Pushing $PackageFile to $EdFiNuGetFeed")

            dotnet nuget push $PackageFile --api-key $NuGetApiKey --source $EdFiNuGetFeed
        }
    }
}

function Invoke-BuildAndGenerateSdk {
    Invoke-Step { DownloadCodeGen }

    if ($PackageName -eq "EdFi.OdsApi.TestSdk") {
        Invoke-Step { GenerateSdk -ApiPackage "Apis.All" -ModelPackage "Models.All" -Endpoint "$DmsUrl/metadata/specifications/resources-spec.json" }
        Invoke-Step { GenerateSdk -ApiPackage "Apis.All" -ModelPackage "Models.All" -Endpoint "$DmsUrl/metadata/specifications/descriptors-spec.json" }
    } elseif ($PackageName -eq "EdFi.OdsApi.Sdk") {
        Invoke-Step { GenerateSdk -ApiPackage "Apis.Ed_Fi" -ModelPackage "Models.Ed_Fi" -Endpoint "$DmsUrl/metadata/specifications/resources-spec.json" }
        Invoke-Step { GenerateSdk -ApiPackage "Apis.Ed_Fi" -ModelPackage "Models.Ed_Fi" -Endpoint "$DmsUrl/metadata/specifications/descriptors-spec.json" }
    } else {
        throw "Unknown PackageName value: $PackageName"
    }

    Invoke-Step { BuildSdk }
}

function Invoke-PushPackage {
    Invoke-Step { PushPackage }
}

function Invoke-BuildPackage {
    Invoke-Step { BuildPackage }
}

Invoke-Main {
    switch ($Command) {
        BuildAndGenerateSdk { Invoke-BuildAndGenerateSdk }
        Package { Invoke-BuildPackage }
        Push { Invoke-PushPackage }
        default { throw "Command '$Command' is not recognized" }
    }
}
