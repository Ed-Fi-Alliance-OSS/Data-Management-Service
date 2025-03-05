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

        * BuildCore: runs `dotnet clean`
        * Package: builds package for the Data Management Service SDK
        * Push: uploads a NuGet package to the NuGet feed
    .EXAMPLE
        .\build-sdk.ps1 BuildCore

        Generates the SDK using openapi codegen cli.

    .EXAMPLE
        .\build-dms.ps1 Package

        Output: a nuget package of the sdk dll.

    .EXAMPLE
        .\build-dms.ps1 push -NuGetApiKey $env:nuget_key
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive')]
param(
    # Command to execute, defaults to "BuildCore".
    [string]
    [ValidateSet("BuildCore", "Package", "Push")]
    $Command = "BuildCore",

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
    $OutputFolder = "./eng/sdkGen/csharp"
)

Import-Module -Name "$PSScriptRoot/eng/build-helpers.psm1" -Force

$packageName = "EdFi.Api.Sdk"
$solutionRoot = "$PSScriptRoot/$OutputFolder"
$projectPath = "$solutionRoot/src/$packageName/$packageName.csproj"
$nuspecPath = "$PSScriptRoot/eng/sdkGen/$packageName.nuspec"

function DownloadCodeGen {
    if (-not (Test-Path -Path openApi-codegen-cli.jar)) {
        Invoke-WebRequest -OutFile openApi-codegen-cli.jar https://repo1.maven.org/maven2/org/openapitools/openapi-generator-cli/7.9.0/openapi-generator-cli-7.9.0.jar
    }
}

function GenerateSdk {
    param (
        [string]
        $Namespace,

        [string]
        $Endpoint
    )

    &java -jar openApi-codegen-cli.jar generate -g csharp -i $Endpoint `
    --api-package Api.$Namespace --model-package Models.$Namespace -o $OutputFolder `
    --additional-properties "packageName=$packageName,targetFramework=net8.0,netCoreProjectFile=true" `
    --global-property modelTests=false --global-property apiTests=false --global-property apiDocs=false --global-property modelDocs=false --skip-validate-spec
}

function BuildSdk {
    Invoke-Execute { dotnet build $projectPath -c Release }
}

function BuildPackage {
    Invoke-Step { RunNuGetPack }
}

function RunNuGetPack {

    $copyrightYear = ${(Get-Date).year)}
    # NU5100 is the warning about DLLs outside of a "lib" folder. We're
    # deliberately using that pattern, therefore we bypass the
    # warning.
    &dotnet pack $projectPath `
        --no-build `
        --no-restore `
        --output $PSScriptRoot `
        -p:NuspecFile=$nuspecPath `
        -p:NoDefaultExcludes=true `
        -p:NuspecProperties="version=$SdkVersion;configuration=Release;;year=$copyrightYear" `
        --configuration Release `
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

function Invoke-BuildCore {
    Invoke-Step { DownloadCodeGen }

    Invoke-Step { GenerateSdk -Namespace "Ed_Fi" -Endpoint "$DmsUrl/metadata/specifications/resources-spec.json" }

    Invoke-Step { GenerateSdk -Namespace "Ed_Fi" -Endpoint "$DmsUrl/metadata/specifications/descriptors-spec.json" }

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
        BuildCore { Invoke-BuildCore }
        Package { Invoke-BuildPackage }
        Push { Invoke-PushPackage }
        default { throw "Command '$Command' is not recognized" }
    }
}
