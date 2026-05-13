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
    [ValidateSet("EdFi.DmsApi.Sdk", "EdFi.DmsApi.TestSdk")]
    $PackageName = "EdFi.DmsApi.Sdk",

    [string]
    $StandardVersion = "5.2.0"
)

Import-Module -Name "$PSScriptRoot/eng/build-helpers.psm1" -Force

$openApiGeneratorVersion = "7.19.0"
$openApiGeneratorJar = "openApi-codegen-cli-$openApiGeneratorVersion.jar"

$solutionRoot = "$PSScriptRoot/$OutputFolder"
$projectPath = "$solutionRoot/src/$PackageName/$PackageName.csproj"
$nuspecPath = "$PSScriptRoot/eng/sdkGen/$PackageName.nuspec"

function DownloadCodeGen {
    if (-not (Test-Path -Path $openApiGeneratorJar)) {
        Invoke-WebRequest -OutFile $openApiGeneratorJar https://repo1.maven.org/maven2/org/openapitools/openapi-generator-cli/$openApiGeneratorVersion/openapi-generator-cli-$openApiGeneratorVersion.jar
    }
}

function Rename-DescriptorOperationId {
    param([string]$old)
    if ($old -match 'ById$') {
        return ($old -replace 'ById$', 'DescriptorById')
    } elseif ($old -match 's$') {
        return ($old -replace 's$', 'Descriptors')
    } else {
        return "${old}Descriptor"
    }
}

function Merge-DmsSpecs {
    # Merges resources-spec and descriptors-spec into a single OpenAPI document so the generator
    # runs once and emits a complete HostConfiguration.cs / IApi.cs. The two-pass flow used to
    # let the descriptors pass clobber the resources-pass HostConfiguration, leaving resource
    # APIs unregistered in DI and the smoke test tool throwing NREs on every resource call.
    param(
        [string]$ResourcesEndpoint,
        [string]$DescriptorsEndpoint
    )

    $resources = (Invoke-WebRequest -Uri $ResourcesEndpoint).Content | ConvertFrom-Json -Depth 100 -AsHashtable
    $descriptors = (Invoke-WebRequest -Uri $DescriptorsEndpoint).Content | ConvertFrom-Json -Depth 100 -AsHashtable

    # Rename descriptor operationIds that collide with resource ones (e.g. getGradingPeriods
    # appears in both specs; without renaming we'd get duplicate C# method names in Apis.All).
    $resourceIds = @{}
    foreach ($pathOps in $resources.paths.Values) {
        foreach ($op in $pathOps.Values) {
            if ($op -is [System.Collections.IDictionary] -and $op.ContainsKey('operationId')) {
                $resourceIds[$op.operationId] = $true
            }
        }
    }
    foreach ($pathOps in $descriptors.paths.Values) {
        foreach ($op in $pathOps.Values) {
            if ($op -is [System.Collections.IDictionary] -and $op.ContainsKey('operationId') -and $resourceIds.ContainsKey($op.operationId)) {
                $op.operationId = Rename-DescriptorOperationId $op.operationId
            }
        }
    }

    # Union descriptor paths/schemas/tags into resources. Parameters/responses/securitySchemes
    # are identical across both specs (verified by inspection) so the resources copy is kept.
    foreach ($pathName in $descriptors.paths.Keys) {
        if (-not $resources.paths.ContainsKey($pathName)) {
            $resources.paths[$pathName] = $descriptors.paths[$pathName]
        }
    }
    foreach ($schemaName in $descriptors.components.schemas.Keys) {
        if (-not $resources.components.schemas.ContainsKey($schemaName)) {
            $resources.components.schemas[$schemaName] = $descriptors.components.schemas[$schemaName]
        }
    }
    $existingTagNames = @{}
    foreach ($t in $resources.tags) { $existingTagNames[$t.name] = $true }
    foreach ($t in $descriptors.tags) {
        if (-not $existingTagNames.ContainsKey($t.name)) {
            $resources.tags += $t
        }
    }

    # Drop required arrays on Homograph* schemas so the generichost-library generator omits its
    # throw-on-missing-required validation. The smoke test tool's data factory cannot populate
    # required props on these extension models, and the server-side spec still enforces them.
    foreach ($schemaName in @($resources.components.schemas.Keys)) {
        if ($schemaName.StartsWith('Homograph')) {
            $schema = $resources.components.schemas[$schemaName]
            if ($schema -is [System.Collections.IDictionary] -and $schema.ContainsKey('required')) {
                $schema.Remove('required')
            }
        }
    }

    return $resources
}

function GenerateSdk {
    param (
        [string]$ApiPackage,
        [string]$ModelPackage,
        [System.Collections.IDictionary]$Spec
    )

    # Rewrite tags on non-ed-fi paths whose tag also appears on /ed-fi/* paths. Without this,
    # /ed-fi/contacts and /homograph/contacts share tag 'contacts' and land on the same
    # ContactsApi, which the smoke test tool's "one Post per Api class" categorizer can't
    # disambiguate. The generator emits a distinct *Api class per tag, so prefixing the tag
    # with the namespace splits the colliding endpoints into separate classes.
    $coreTags = @{}
    foreach ($pathName in @($Spec.paths.Keys)) {
        if ($pathName.StartsWith('/ed-fi/')) {
            foreach ($verb in @($Spec.paths[$pathName].Keys)) {
                $op = $Spec.paths[$pathName][$verb]
                if ($op -is [System.Collections.IDictionary] -and $op.ContainsKey('tags') -and $op['tags']) {
                    foreach ($t in $op['tags']) { $coreTags[$t] = $true }
                }
            }
        }
    }
    foreach ($pathName in @($Spec.paths.Keys)) {
        if ($pathName -match '^/(?<ns>[^/]+)/' -and $matches.ns -ne 'ed-fi') {
            $nsSafe = ($matches.ns -replace '[^A-Za-z0-9]', '')
            foreach ($verb in @($Spec.paths[$pathName].Keys)) {
                $op = $Spec.paths[$pathName][$verb]
                if ($op -is [System.Collections.IDictionary] -and $op.ContainsKey('tags') -and $op['tags']) {
                    $op['tags'] = @($op['tags'] | ForEach-Object { if ($coreTags.ContainsKey($_)) { "${nsSafe}_$_" } else { $_ } })
                }
            }
        }
    }

    # Build --operation-id-name-mappings for underscore-bearing operationIds so the generator
    # preserves the namespace separator in C# method names (post_HomographContact stays
    # Post_HomographContact rather than collapsing to PostHomographContact).
    $operationIds = @()
    foreach ($pathOps in $Spec.paths.Values) {
        foreach ($op in $pathOps.Values) {
            if ($op -is [System.Collections.IDictionary] -and $op.ContainsKey('operationId') -and $op.operationId -like "*_*") {
                $operationIds += $op.operationId
            }
        }
    }

    function Normalize-OperationId {
        param($opId)
        $parts = $opId -split '_'
        $camel = $parts[0] + (($parts[1..($parts.Count-1)] | ForEach-Object { $_.Substring(0,1).ToUpper() + $_.Substring(1) }) -join '')
        return $camel
    }
    function Capitalize-FirstChar {
        param($s)
        if ($s.Length -eq 0) { return $s }
        return $s.Substring(0,1).ToUpper() + $s.Substring(1)
    }
    $mappings = ($operationIds | Sort-Object -Unique | ForEach-Object { "$(Normalize-OperationId $_)=$(Capitalize-FirstChar $_)" }) -join ","

    $specTempPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "openapi-spec-$([System.Guid]::NewGuid()).json")
    $Spec | ConvertTo-Json -Depth 100 -Compress | Set-Content -Path $specTempPath -Encoding UTF8NoBOM

    try {
        & java -Xmx5g -jar $openApiGeneratorJar generate `
        -g csharp `
        -i $specTempPath `
        --api-package $ApiPackage `
        --model-package $ModelPackage `
        -o $OutputFolder `
        --operation-id-name-mappings $mappings `
        --additional-properties "packageName=$PackageName,targetFramework=net10.0,netCoreProjectFile=true" `
        --global-property modelTests=false `
        --global-property apiTests=false `
        --global-property apiDocs=false `
        --global-property modelDocs=false `
        --skip-validate-spec
    }
    finally {
        Remove-Item $specTempPath -ErrorAction SilentlyContinue
    }
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

    $mergedSpec = Merge-DmsSpecs `
        -ResourcesEndpoint "$DmsUrl/metadata/specifications/resources-spec.json" `
        -DescriptorsEndpoint "$DmsUrl/metadata/specifications/descriptors-spec.json"

    $packagePair = switch ($PackageName) {
        "EdFi.DmsApi.TestSdk" { @{ Api = "Apis.All"; Model = "Models.All" } }
        "EdFi.DmsApi.Sdk"     { @{ Api = "Apis.Ed_Fi"; Model = "Models.Ed_Fi" } }
        default               { throw "Unknown PackageName value: $PackageName" }
    }

    Invoke-Step { GenerateSdk -ApiPackage $packagePair.Api -ModelPackage $packagePair.Model -Spec $mergedSpec }

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
