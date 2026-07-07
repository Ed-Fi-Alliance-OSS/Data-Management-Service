# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

param()

BeforeAll {
    function New-TestApiSchemaPackage {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pester helper writes only inside $TestDrive-managed temporary workspaces; no public -WhatIf surface.')]
        param(
            [string]$PackageCacheRoot,
            [string]$PackageId,
            [AllowNull()]$DiscoverySpecPath = $null,
            [AllowNull()]$XsdDirectory = $null,
            [switch]$CreateDiscoverySpec,
            [switch]$CreateXsdDirectory
        )

        $packageRoot = Join-Path $PackageCacheRoot "contentFiles/any/any/ApiSchema"
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

        "{}" | Set-Content -LiteralPath (Join-Path $packageRoot "ApiSchema.json") -Encoding utf8

        if ($CreateDiscoverySpec) {
            "{}" | Set-Content -LiteralPath (Join-Path $packageRoot "discovery-spec.json") -Encoding utf8
        }

        if ($CreateXsdDirectory) {
            $xsdRoot = Join-Path $packageRoot "xsd"
            New-Item -ItemType Directory -Path $xsdRoot -Force | Out-Null
            "<schema/>" | Set-Content -LiteralPath (Join-Path $xsdRoot "Ed-Fi-Core.xsd") -Encoding utf8
        }

        [ordered]@{
            version             = 1
            packageId           = $PackageId
            projectName         = "Ed-Fi"
            projectEndpointName = "ed-fi"
            isExtensionProject  = $false
            schemaPath          = "ApiSchema.json"
            discoverySpecPath   = $DiscoverySpecPath
            xsdDirectory        = $XsdDirectory
        } | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $packageRoot "package-manifest.json") -Encoding utf8
    }

    function New-TestProject {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pester helper writes only inside $TestDrive-managed temporary workspaces; no public -WhatIf surface.')]
        param(
            [string]$WorkspaceRoot,
            [string]$TargetsPath
        )

        $projectRoot = Join-Path $WorkspaceRoot "project"
        New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null

        $escapedTargetsPath = [System.Security.SecurityElement]::Escape($TargetsPath)
        $projectPath = Join-Path $projectRoot "PackagingHarness.csproj"

        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultItems>false</EnableDefaultItems>
  </PropertyGroup>
  <Import Project="$escapedTargetsPath" />
</Project>
"@ | Set-Content -LiteralPath $projectPath -Encoding utf8

        return $projectPath
    }

    function Invoke-TestPublish {
        param(
            [string]$ProjectPath,
            [string]$PackageCacheRoot,
            [string]$WorkspaceRoot
        )

        $publishDirectory = Join-Path $WorkspaceRoot "publish"
        $intermediateDirectory = Join-Path $WorkspaceRoot "obj"
        $output = & dotnet publish $ProjectPath `
            --nologo `
            -v:minimal `
            -o $publishDirectory `
            "/p:PkgEdFi_DataStandard52_ApiSchema=$PackageCacheRoot" `
            "/p:BaseIntermediateOutputPath=$intermediateDirectory/" `
            "/p:IntermediateOutputPath=$intermediateDirectory/" 2>&1

        return [pscustomobject]@{
            ExitCode         = $LASTEXITCODE
            Output           = $output
            PublishDirectory = $publishDirectory
        }
    }
}

Describe "DMS-1154 Dockerfile ApiSchema packaging contract" {
    BeforeAll {
        $script:dockerfilePath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../src/dms/Dockerfile")
        )
        $script:dockerfileContent = Get-Content -LiteralPath $script:dockerfilePath -Raw
    }

    It "copies Directory.Build.targets into the build stage before restore" {
        # The COPY line may carry sibling build files (e.g. Directory.Build.props); the contract
        # is only that Directory.Build.targets lands in the build stage ahead of restore.
        $targetsCopyMatch = [regex]::Match(
            $script:dockerfileContent,
            '(?m)^COPY\s[^\r\n]*Directory\.Build\.targets[^\r\n]*$'
        )
        $restoreIndex = $script:dockerfileContent.IndexOf("RUN dotnet restore")

        $targetsCopyMatch.Success | Should -BeTrue
        $restoreIndex | Should -BeGreaterThan $targetsCopyMatch.Index
    }

    It "copies published ApiSchema content recursively into the final image" {
        $script:dockerfileContent |
            Should -Match 'COPY --from=build /app/Frontend/ApiSchema/ ./ApiSchema/'
        $script:dockerfileContent |
            Should -Not -Match 'COPY --from=build /app/Frontend/ApiSchema/\*\.json ./ApiSchema/'
    }
}

Describe "DMS-1154 bundled ApiSchema manifest materialization" {
    BeforeAll {
        $script:directoryBuildTargetsPath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../src/dms/Directory.Build.targets")
        )
        $script:packageId = "EdFi.DataStandard52.ApiSchema"
    }

    It "publishes explicit null for package manifest optional content fields with no package assets" {
        $workspaceRoot = Join-Path $TestDrive "optional-null"
        $packageCacheRoot = Join-Path $workspaceRoot "packages/$script:packageId"
        New-TestApiSchemaPackage `
            -PackageCacheRoot $packageCacheRoot `
            -PackageId $script:packageId `
            -DiscoverySpecPath $null `
            -XsdDirectory $null
        $projectPath = New-TestProject `
            -WorkspaceRoot $workspaceRoot `
            -TargetsPath $script:directoryBuildTargetsPath

        $result = Invoke-TestPublish `
            -ProjectPath $projectPath `
            -PackageCacheRoot $packageCacheRoot `
            -WorkspaceRoot $workspaceRoot

        $result.ExitCode | Should -Be 0 -Because ($result.Output -join [Environment]::NewLine)
        $manifestPath = Join-Path $result.PublishDirectory "ApiSchema/bootstrap-api-schema-manifest.json"
        $manifestRaw = Get-Content -LiteralPath $manifestPath -Raw
        $manifest = $manifestRaw | ConvertFrom-Json

        $manifestRaw | Should -Match '"discoverySpecPath": null'
        $manifestRaw | Should -Match '"xsdDirectory": null'
        @($manifest.projects).Count | Should -Be 1
        $manifest.projects[0].schemaPath | Should -Be "Packages/$script:packageId/ApiSchema.json"
        $manifest.projects[0].discoverySpecPath | Should -BeNullOrEmpty
        $manifest.projects[0].xsdDirectory | Should -BeNullOrEmpty
        Test-Path -LiteralPath (Join-Path $result.PublishDirectory "ApiSchema/Packages/$script:packageId/ApiSchema.json") |
            Should -BeTrue
        Test-Path -LiteralPath (Join-Path $result.PublishDirectory "ApiSchema/Packages/$script:packageId/xsd") |
            Should -BeFalse
    }

    It "publishes manifest-declared optional content paths when package assets exist" {
        $workspaceRoot = Join-Path $TestDrive "optional-present"
        $packageCacheRoot = Join-Path $workspaceRoot "packages/$script:packageId"
        New-TestApiSchemaPackage `
            -PackageCacheRoot $packageCacheRoot `
            -PackageId $script:packageId `
            -DiscoverySpecPath "discovery-spec.json" `
            -XsdDirectory "xsd" `
            -CreateDiscoverySpec `
            -CreateXsdDirectory
        $projectPath = New-TestProject `
            -WorkspaceRoot $workspaceRoot `
            -TargetsPath $script:directoryBuildTargetsPath

        $result = Invoke-TestPublish `
            -ProjectPath $projectPath `
            -PackageCacheRoot $packageCacheRoot `
            -WorkspaceRoot $workspaceRoot

        $result.ExitCode | Should -Be 0 -Because ($result.Output -join [Environment]::NewLine)
        $manifest = Get-Content `
            -LiteralPath (Join-Path $result.PublishDirectory "ApiSchema/bootstrap-api-schema-manifest.json") `
            -Raw |
            ConvertFrom-Json

        $manifest.projects[0].discoverySpecPath |
            Should -Be "Packages/$script:packageId/discovery-spec.json"
        $manifest.projects[0].xsdDirectory | Should -Be "Packages/$script:packageId/xsd"
        Test-Path -LiteralPath (Join-Path $result.PublishDirectory "ApiSchema/Packages/$script:packageId/discovery-spec.json") |
            Should -BeTrue
        Test-Path -LiteralPath (Join-Path $result.PublishDirectory "ApiSchema/Packages/$script:packageId/xsd/Ed-Fi-Core.xsd") |
            Should -BeTrue
    }

    It "fails publish when package manifest declares missing <FieldName>" -TestCases @(
        @{ FieldName = "discoverySpecPath"; DiscoverySpecPath = "discovery-spec.json"; XsdDirectory = $null },
        @{ FieldName = "xsdDirectory"; DiscoverySpecPath = $null; XsdDirectory = "xsd" }
    ) {
        param(
            [string]$FieldName,
            [AllowNull()][string]$DiscoverySpecPath,
            [AllowNull()][string]$XsdDirectory
        )

        $workspaceRoot = Join-Path $TestDrive "missing-$FieldName"
        $packageCacheRoot = Join-Path $workspaceRoot "packages/$script:packageId"
        New-TestApiSchemaPackage `
            -PackageCacheRoot $packageCacheRoot `
            -PackageId $script:packageId `
            -DiscoverySpecPath $DiscoverySpecPath `
            -XsdDirectory $XsdDirectory
        $projectPath = New-TestProject `
            -WorkspaceRoot $workspaceRoot `
            -TargetsPath $script:directoryBuildTargetsPath

        $result = Invoke-TestPublish `
            -ProjectPath $projectPath `
            -PackageCacheRoot $packageCacheRoot `
            -WorkspaceRoot $workspaceRoot

        $result.ExitCode | Should -Not -Be 0
        ($result.Output -join [Environment]::NewLine) | Should -Match $FieldName
        Test-Path -LiteralPath (Join-Path $result.PublishDirectory "ApiSchema/bootstrap-api-schema-manifest.json") |
            Should -BeFalse
    }
}
