# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdLetBinding()]
<#
    .SYNOPSIS
        Automation script for running build operations from the command line.

    .DESCRIPTION
        Provides automation of the following tasks:

        * Clean: runs `dotnet clean`
        * Build: runs `dotnet build` with several implicit steps
          (clean, restore, inject version information).
        * UnitTest: executes NUnit tests in projects named `*.UnitTests`, which
          do not connect to a database.
        * E2ETest: executes NUnit tests in EdFi.DataManagementService.Tests.E2E, which
          runs the API in an isolated Docker environment and executes API Calls.
        * InstanceE2ETest: executes instance management E2E tests in
          EdFi.InstanceManagement.Tests.E2E, which require special setup with route
          qualifiers and multiple databases.
        * IntegrationTest: executes NUnit test in projects named `*.IntegrationTests`,
          which connect to a database.
        * BuildAndPublish: build and publish with `dotnet publish`
        * Package: builds NuGet packages. The DMS API application, SchemaTools, and DocumentCacheAdmin packages are published by the release workflows; the custom-validation abstractions package is built and verified only, and is deliberately not published yet. Use -PackageTarget to build only one package.
        * Push: uploads a NuGet package to the NuGet feed.
        * DockerBuild: builds a Docker image from source code
        * DockerRun: runs the Docker image that was built from source code
        * Run: starts the application
        * StartEnvironment: starts the Docker environment for DMS
    .EXAMPLE
        .\build-dms.ps1 build -Configuration Release -Version "2.0" -BuildCounter 45

        Overrides the default build configuration (Debug) to build in release
        mode with assembly version 2.0.45.

    .EXAMPLE
        .\build-dms.ps1 unittest

        Output: test results displayed in the console and saved to XML files.

    .EXAMPLE
        .\build-dms.ps1 InstanceE2ETest -Configuration Release

        Starts Docker environment with route qualifiers, configures test databases,
        and runs instance management E2E tests.

    .EXAMPLE
        .\build-dms.ps1 push -NuGetApiKey $env:nuget_key -PackageFile .\EdFi.Api.8.0.0.nupkg
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Build entry script intentionally writes operator progress and status output to the console.')]
param(
    # Command to execute, defaults to "Build".
    [string]
    [ValidateSet("Clean", "Restore", "Build", "BuildAndPublish", "UnitTest", "E2ETest", "InstanceE2ETest", "IntegrationTest", "Coverage", "Package", "Push", "DockerBuild", "DockerRun", "Run", "StartEnvironment")]
    $Command = "Build",

    # Assembly and package version number for the Data Management Service. The
    # current package number is configured in the build automation tool and
    # passed to this script.
    [string]
    $DMSVersion = "8.0.0",

    # .NET project build configuration, defaults to "Debug". Options are: Debug, Release.
    [string]
    [ValidateSet("Debug", "Release")]
    $Configuration = "Debug",

    # Selects which NuGet package(s) the Package command builds.
    [string]
    [ValidateSet("All", "Api", "SchemaTools", "CustomValidation", "DocumentCacheAdmin")]
    $PackageTarget = "All",

    # When set, `dotnet restore` runs with `--locked-mode`, failing the build if a committed
    # packages.lock.json is out of sync. The release/publish build and the relational scheduled
    # build pass this so published packages come from the committed lock graph; the PR
    # `verify-lock-files` gate enforces lock consistency separately. Ordinary build/test jobs and
    # local builds leave it off (see docs/NUGET-LOCK-FILES.md).
    [switch]
    $LockedMode,

    [bool]
    $DryRun = $false,

    # Ed-Fi's official NuGet package feed for package download and distribution.
    [string]
    $EdFiNuGetFeed = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",

    # API key for accessing the feed above. Only required with with the Push
    # command.
    [string]
    $NuGetApiKey,

    # Full path of a package file to push to the NuGet feed. Optional, only
    # applies with the Push command. If not set, then the script looks for a
    # NuGet package corresponding to the provided $DMSVersion and $BuildCounter.
    [string]
    $PackageFile,

    # Only required with local builds and testing.
    [switch]
    $IsLocalBuild,

    # Only required with E2E testing.
    [switch]
    $UsePublishedImage,

    # Only required with E2E testing.
    [switch]
    $SkipDockerBuild,

    # Opts into the seed phase after the stack starts. For StartEnvironment, forwarded to the
    # bootstrap wrapper so it uses the documented API-based seed path. E2ETest rejects this switch
    # because its database is reset and provisioned by provision-e2e-database.ps1 before tests run.
    [switch]
    $LoadSeedData,

    # Database engine backing the stack. Used by StartEnvironment (forwarded to the bootstrap
    # wrapper) and by E2ETest (forwarded through the E2E orchestration to the engine-aware
    # start/configure/provision leaf scripts). When omitted it is normalized to postgresql, which is
    # behavior-compatible with the prior PostgreSQL-only flow.
    [string]
    [ValidateSet("postgresql", "mssql")]
    $DatabaseEngine,

    # Redirects the CMS (Configuration Service) database to a dedicated edfi_configurationservice
    # database instead of sharing the DMS datastore database. Used by StartEnvironment only,
    # forwarded unchanged to the bootstrap wrapper and on to the start script. Supported on both
    # database engines.
    [switch]
    $SeparateConfigDatabase,

    # Identity provider type
    [string]
    [ValidateSet("keycloak", "self-contained")]
    $IdentityProvider="self-contained",

    # Environment file for docker-compose operations
    [string]
    $EnvironmentFile="./.env.e2e",

    # Optional test filter for dotnet test operations
    [string]
    $TestFilter,

    # Optional Ed-Fi Data Standard version. Forwarded to the start scripts, which compose the
    # matching .env.ds<NN> overlay onto -EnvironmentFile. Omit for the default (DS 5.2).
    [string]
    [ValidateSet("5.2", "6.1")]
    $DataStandardVersion
)

# Captured here (script scope) rather than at the point of use: $PSBoundParameters inside the
# Invoke-Main script block below reflects that block's own bindings, not this script's, so the
# ContainsKey check has to run in this scope while the top-level $PSBoundParameters is populated.
$dataStandardVersionSupplied = $PSBoundParameters.ContainsKey('DataStandardVersion')
# Whether -EnvironmentFile was supplied at the top level. InstanceE2ETest defaults to the route-context
# env file when omitted, so it must not inherit the standard suite's ./.env.e2e default.
$environmentFileSupplied = $PSBoundParameters.ContainsKey('EnvironmentFile')

$solutionRoot = "$PSScriptRoot/src/dms"
$defaultSolution = "$solutionRoot/EdFi.DataManagementService.sln"
$applicationRoot = "$solutionRoot/frontend"
$clisRoot = "$solutionRoot/clis"
$coreRoot = "$solutionRoot/core"
$projectName = "EdFi.DataManagementService.Frontend.AspNetCore"
$schemaDownloaderProjectName = "EdFi.DataManagementService.ApiSchemaDownloader"
$schemaToolsProjectName = "EdFi.DataManagementService.SchemaTools"
$documentCacheAdminProjectName = "EdFi.DataManagementService.DocumentCacheAdmin"
$packageName = "EdFi.Api"
$schemaToolsPackageName = "EdFi.Api.SchemaTools"
$customValidationPackageName = "EdFi.Api.CustomValidation"
$customValidationProjectName = "EdFi.DataManagementService.CustomValidation"
$documentCacheAdminPackageName = "EdFi.Api.DocumentCacheAdmin"
$testResults = "$PSScriptRoot/TestResults"
#Coverage
$thresholdCoverage = 58
$coverageOutputFile = "coverage.cobertura.xml"
$targetDir = "coveragereport"

$maintainers = "Ed-Fi Alliance, LLC and contributors"

Import-Module -Name "$PSScriptRoot/eng/build-helpers.psm1" -Force
Import-Module -Name "$PSScriptRoot/eng/docker-compose/effective-schema-hash.psm1" -Force
Import-Module -Name "$PSScriptRoot/package-helpers.psm1" -Force
# The E2E setup wrappers' schema-settings guard and container-environment reader. This script calls
# both directly rather than through helpers of its own: its gated call sites pass the guard's -Enabled
# parameter, so there is one implementation, one name, and nothing defined here for a setup wrapper's
# own call to bind instead of the module's export.
#
# Without -Force, the same rule this module applies to its own nested imports: -Force removes a module
# before re-importing it and removal is session-wide, so an already-loaded instance is reused instead.
# This import can therefore only ADD command resolution, never take it away from a setup wrapper this
# script invokes in-process.
Import-Module -Name "$PSScriptRoot/eng/docker-compose/dms-schema-environment.psm1"

function DotNetClean {
    Invoke-Execute { dotnet clean $defaultSolution -c $Configuration --nologo -v minimal }
}

function Restore {
    Invoke-Execute {
        $restoreArgs = @()
        if ($LockedMode) { $restoreArgs += "--locked-mode" }
        dotnet restore $defaultSolution --verbosity:normal @restoreArgs
    }
}

function SetDMSAssemblyInfo {
    Invoke-Execute {
        $assembly_version = $DMSVersion

        Invoke-RegenerateFile "$solutionRoot/Directory.Build.props" @"
<Project>
    <!-- This file is generated by the build script. -->
    <PropertyGroup>
        <TreatWarningsAsErrors>True</TreatWarningsAsErrors>
        <ErrorLog>results.sarif,version=2.1</ErrorLog>
        <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
        <Product>Ed-Fi API</Product>
        <Authors>$maintainers</Authors>
        <Company>$maintainers</Company>
        <Copyright>Copyright © ${(Get-Date).year)} Ed-Fi Alliance</Copyright>
        <VersionPrefix>$assembly_version</VersionPrefix>
        <VersionSuffix></VersionSuffix>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="Microsoft.CodeAnalysis.CSharp.CodeStyle">
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
        <PackageReference Include="SonarAnalyzer.CSharp">
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
    </ItemGroup>
</Project>
"@
    }
}

function Compile {
    Invoke-Execute {

        dotnet build $defaultSolution -c $Configuration --nologo --no-restore
    }
}

function PublishApi {
    Invoke-Execute {
        $project = "$applicationRoot/$projectName/"
        $outputPath = "$project/publish"
        # --no-restore: reuse the restore from Invoke-Build (which honors -LockedMode) instead of
        # letting publish run a second, unlocked restore that would bypass the lock graph.
        dotnet publish $project -c $Configuration -o $outputPath --nologo --no-restore
    }
}

function PublishCliApiDownloader {
    Invoke-Execute {
        $schemaDownloaderProject = "$clisRoot/$schemaDownloaderProjectName/"
        $outputPath = "$schemaDownloaderProject/publish"
        dotnet publish $schemaDownloaderProject -c $Configuration -o $outputPath --nologo --no-restore
    }
}

function SetAuthenticationServiceURL {
    param (
        # E2E test directory
        [string]
        $E2EDirectory
    )
    $appSettingsPath = Join-Path -Path $E2EDirectory -ChildPath "appsettings.json"
    $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    if ($IdentityProvider -eq  "self-contained") {
        $json.AuthenticationService ="http://ed-fi-api-config:8081/connect/token"
    }
    else {
        $json.AuthenticationService = "http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token"
    }
    $json | ConvertTo-Json -Depth 32 | Set-Content $appSettingsPath
}

function Resolve-E2EEnvironmentFilePath {
    param(
        [string]
        $Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        if (-not (Test-Path $Path)) {
            throw "Environment file not found: $Path"
        }

        return [System.IO.Path]::GetFullPath($Path)
    }

    $candidatePaths = @(
        $Path,
        (Join-Path (Get-Location) $Path),
        (Join-Path $PSScriptRoot $Path),
        (Join-Path (Join-Path $PSScriptRoot "eng/docker-compose") $Path)
    )

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path $candidatePath) {
            return [System.IO.Path]::GetFullPath([string](Resolve-Path $candidatePath))
        }
    }

    throw "Environment file not found: $Path"
}

function Get-E2ETestResultSuffix {
    param(
        [string]
        $TestFilter
    )

    $normalizedTestFilter = ConvertTo-NormalizedTestFilter -TestFilter $TestFilter

    if ([string]::IsNullOrWhiteSpace($TestFilter)) {
        return "e2e"
    }

    if ($normalizedTestFilter -match '(?i)\b(?:TestCategory|Category)\s*=\s*e2e-ci-shard-(\d+)\b') {
        return "e2e-shard-$($Matches[1])"
    }

    return "filtered"
}

function ConvertTo-NormalizedTestFilter {
    param(
        [string]
        $TestFilter
    )

    if ([string]::IsNullOrWhiteSpace($TestFilter)) {
        return $TestFilter
    }

    $normalizedTestFilter = $TestFilter
    $normalizedTestFilter = $normalizedTestFilter -replace 'TestCategory\s*!=\s*@', 'TestCategory!='
    $normalizedTestFilter = $normalizedTestFilter -replace 'TestCategory\s*=\s*@', 'TestCategory='
    $normalizedTestFilter = $normalizedTestFilter -replace 'TestCategory\s*!~\s*@', 'TestCategory!~'
    $normalizedTestFilter = $normalizedTestFilter -replace 'TestCategory\s*~\s*@', 'TestCategory~'
    $normalizedTestFilter = $normalizedTestFilter -replace 'Category\s*!=\s*@', 'Category!='
    $normalizedTestFilter = $normalizedTestFilter -replace 'Category\s*=\s*@', 'Category='
    $normalizedTestFilter = $normalizedTestFilter -replace 'Category\s*!~\s*@', 'Category!~'
    $normalizedTestFilter = $normalizedTestFilter -replace 'Category\s*~\s*@', 'Category~'

    return $normalizedTestFilter
}

function Get-E2ETestEnvironmentContext {
    param(
        [string]
        $EnvironmentFile,

        [string]
        $TestFilter,

        # Database engine backing the E2E stack. "postgresql" (default) or "mssql". An empty value
        # is normalized to postgresql so an omitted top-level -DatabaseEngine is behavior-compatible.
        [string]
        $DatabaseEngine = "postgresql"
    )

    $resolvedDatabaseEngine =
        if ([string]::IsNullOrWhiteSpace($DatabaseEngine)) { "postgresql" } else { $DatabaseEngine }

    $environmentFilePath = Resolve-E2EEnvironmentFilePath -Path $EnvironmentFile

    Import-Module -Name "$PSScriptRoot/eng/docker-compose/env-utility.psm1" -Force
    Import-Module -Name "$PSScriptRoot/eng/Dms-Management.psm1" -Force
    # Shared Compose-equivalent resolver so the E2E target database honours an ambient
    # E2E_DATABASE_NAME override exactly like provision-e2e-database.ps1's destructive reset does.
    Import-Module -Name "$PSScriptRoot/eng/docker-compose/database-safety.psm1" -Force

    # Single resolution point for the standard suite: compose the data-standard overlay (.env.ds<NN>)
    # first, then the database-engine overlay (.env.mssql), so every downstream consumer - relational
    # provisioning, configure, DMS startup, the test process, and teardown - reads the one resolved
    # file. The order (data standard then engine) matches start-local-dms.ps1 and must not be
    # reversed. With no -DataStandardVersion the data-standard step returns the file unchanged (DS 5.2
    # default), and for postgresql the engine step is a no-op.
    $environmentFilePath = Resolve-DataStandardEnvironmentFile `
        -DataStandardVersion $DataStandardVersion `
        -BaseEnvironmentFile $environmentFilePath `
        -DockerComposeRoot "$PSScriptRoot/eng/docker-compose"
    $environmentFilePath = Resolve-DatabaseEngineEnvironmentFile `
        -DatabaseEngine $resolvedDatabaseEngine `
        -BaseEnvironmentFile $environmentFilePath `
        -DockerComposeRoot "$PSScriptRoot/eng/docker-compose"

    $environmentValues = ReadValuesFromEnvFile $environmentFilePath
    # Resolve the E2E database name with Docker Compose precedence (an ambient process/shell value
    # wins over the env file), the same rule provision-e2e-database.ps1 applies before its
    # destructive reset - so the CMS data store, the test process, and the reset/provision phase
    # all target one database.
    $e2eDatabaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $environmentValues -Name "E2E_DATABASE_NAME"

    if ([string]::IsNullOrWhiteSpace($e2eDatabaseName)) {
        throw "E2E_DATABASE_NAME must be set in '$environmentFilePath' or the process environment so the DMS E2E database can be reset and provisioned before tests run."
    }

    # Build the two opaque connection strings once from the resolved environment: host-side
    # admin/reset access and the Docker-network Configuration Service registration string. Both carry
    # the same custom credentials/ports/database from the resolved env. They contain secrets and are
    # never written to host output; they flow to the test process via Invoke-WithE2ETestProcessContext.
    $connectionStrings = New-E2EDataStoreConnectionStrings `
        -DatabaseEngine $resolvedDatabaseEngine `
        -EnvironmentValues $environmentValues `
        -DatabaseName $e2eDatabaseName

    return [pscustomobject]@{
        EnvironmentFile = $environmentFilePath
        ShouldProvisionE2EDatabase = $true
        DataStoreDatabaseName = $e2eDatabaseName
        DatabaseEngine = $resolvedDatabaseEngine
        DataStoreAdminConnectionString = $connectionStrings.AdminConnectionString
        DataStoreConnectionString = $connectionStrings.RegistrationConnectionString
        TestResultSuffix = Get-E2ETestResultSuffix -TestFilter $TestFilter
    }
}

function Invoke-WithE2ETestProcessContext {
    param(
        [pscustomobject]
        $E2ETestSettings,

        [scriptblock]
        $Action
    )

    # Capture existence independently from value for every variable this context mutates. PowerShell
    # retains empty and whitespace-valued environment variables (Test-Path Env:<name> is true), so a
    # value-only check would convert an existing empty/whitespace variable into an unset one on
    # restore. Existence + verbatim value preserves the unset-versus-valued distinction exactly.
    $previousDataStoreDatabaseNameExists = Test-Path Env:AppSettings__DataStoreDatabaseName
    $previousDataStoreDatabaseName = $env:AppSettings__DataStoreDatabaseName
    $previousDatabaseEngineExists = Test-Path Env:AppSettings__DatabaseEngine
    $previousDatabaseEngine = $env:AppSettings__DatabaseEngine
    $previousDataStoreAdminConnectionStringExists = Test-Path Env:AppSettings__DataStoreAdminConnectionString
    $previousDataStoreAdminConnectionString = $env:AppSettings__DataStoreAdminConnectionString
    $previousDataStoreConnectionStringExists = Test-Path Env:AppSettings__DataStoreConnectionString
    $previousDataStoreConnectionString = $env:AppSettings__DataStoreConnectionString
    $previousNodeOptionsExists = Test-Path Env:NODE_OPTIONS
    $previousNodeOptions = $env:NODE_OPTIONS

    try {
        if ([string]::IsNullOrWhiteSpace($E2ETestSettings.DataStoreDatabaseName)) {
            throw "AppSettings__DataStoreDatabaseName must be set for the DMS E2E test process."
        }

        $env:AppSettings__DataStoreDatabaseName = $E2ETestSettings.DataStoreDatabaseName
        # Engine and the two opaque connection strings for the C# harness (host-side admin/reset
        # access and the Docker-network registration string). The values contain secrets and are set
        # into the environment only; they are never written to host output.
        $env:AppSettings__DatabaseEngine = $E2ETestSettings.DatabaseEngine
        $env:AppSettings__DataStoreAdminConnectionString = $E2ETestSettings.DataStoreAdminConnectionString
        $env:AppSettings__DataStoreConnectionString = $E2ETestSettings.DataStoreConnectionString
        Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
        & $Action
    }
    finally {
        # Restore each variable to its exact prior state: re-create with the verbatim prior value
        # (including empty/whitespace) when it existed, otherwise remove it.
        if ($previousDataStoreDatabaseNameExists) {
            $env:AppSettings__DataStoreDatabaseName = $previousDataStoreDatabaseName
        }
        else {
            Remove-Item Env:AppSettings__DataStoreDatabaseName -ErrorAction SilentlyContinue
        }

        if ($previousDatabaseEngineExists) {
            $env:AppSettings__DatabaseEngine = $previousDatabaseEngine
        }
        else {
            Remove-Item Env:AppSettings__DatabaseEngine -ErrorAction SilentlyContinue
        }

        if ($previousDataStoreAdminConnectionStringExists) {
            $env:AppSettings__DataStoreAdminConnectionString = $previousDataStoreAdminConnectionString
        }
        else {
            Remove-Item Env:AppSettings__DataStoreAdminConnectionString -ErrorAction SilentlyContinue
        }

        if ($previousDataStoreConnectionStringExists) {
            $env:AppSettings__DataStoreConnectionString = $previousDataStoreConnectionString
        }
        else {
            Remove-Item Env:AppSettings__DataStoreConnectionString -ErrorAction SilentlyContinue
        }

        if ($previousNodeOptionsExists) {
            $env:NODE_OPTIONS = $previousNodeOptions
        }
        else {
            Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
        }
    }
}

function Stop-DockerEnvironment {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal build orchestration helper; build-dms.ps1 does not expose -WhatIf end to end, so partial ShouldProcess support would create misleading no-op behavior.')]
    param(
        [string]
        $EnvironmentFilePath,

        [string]
        $IdentityProvider,

        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine = "postgresql",

        [switch]
        $RemoveBootstrap,

        [switch]
        $UseEnvironmentFileSchemaSettings
    )

    Invoke-Execute {
        try {
            Push-Location "$PSScriptRoot/eng/docker-compose"
            # Both compose projects bind-mount the same .bootstrap workspace, and each primitive removes it
            # after its own successful down. Only the last down may remove it: otherwise this local down
            # deletes the workspace the dms-published project's DMS services are still bind-mounting, and a
            # failing published down leaves a stopped-but-not-torn-down stack with no workspace to retry
            # against. The published down below still performs the removal on the success path, because an
            # absent project's down exits 0.
            Invoke-WithDmsEnvironmentFileSchemaAuthority -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                ./start-local-dms.ps1 -EnvironmentFile $EnvironmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -DatabaseEngine $DatabaseEngine -d -v -RemoveBootstrap:$false
            }
            Invoke-WithDmsEnvironmentFileSchemaAuthority -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                ./start-published-dms.ps1 -EnvironmentFile $EnvironmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -DatabaseEngine $DatabaseEngine -d -v -RemoveBootstrap:$RemoveBootstrap
            }
        }
        finally {
            Pop-Location
        }
    }
}

function RunTests {
    param (
        # File search filter
        [string]
        $Filter,

        # Optional dotnet test filter
        [string]
        $TestFilter,

        # Optional suffix for trx output name
        [string]
        $ResultNameSuffix
    )

    # Unit tests are collected in one run so coverage can be measured across the whole set. Every
    # other filter runs assembly by assembly.
    if ($Filter.Equals("*.Tests.Unit")) {
        RunUnitTestsWithCoverage
        return
    }

    # @() because the pipeline hands back a bare FileInfo when exactly one assembly matches, and the
    # loop below has to be able to treat the result as a collection either way.
    $testAssemblies = @(
        Get-RequiredTestAssembly -SolutionRoot $solutionRoot -Filter $Filter -Configuration $Configuration |
            Sort-Object -Property { $_.Name.Length }
    )
    $normalizedTestFilter = ConvertTo-NormalizedTestFilter -TestFilter $TestFilter

    Write-Output "Tests Assemblies List"
    Write-Output $testAssemblies
    Write-Output "End Tests Assemblies List"

    if (-not [string]::IsNullOrWhiteSpace($normalizedTestFilter) -and $normalizedTestFilter -ne $TestFilter) {
        Write-Output "Normalized test filter for VSTest: '$TestFilter' -> '$normalizedTestFilter'"
    }

    if (-not (Test-Path $testResults)) {
        New-Item -ItemType Directory -Path $testResults -Force | Out-Null
    }

    $testAssemblies | ForEach-Object {
        Write-Output "Executing: dotnet test $($_)"

        $target = $_.FullName

        $fileNameNoExt = $_.Name.subString(0, $_.Name.length - 4)
        $trxFileName =
            if ([string]::IsNullOrWhiteSpace($ResultNameSuffix)) {
                "$fileNameNoExt.trx"
            }
            else {
                "$fileNameNoExt.$ResultNameSuffix.trx"
            }

        $trxFilePath = Join-Path $testResults $trxFileName

        # Set Query Handler for E2E tests
        if ($Filter -like "*E2E*") {
            $dirPath = Split-Path -parent $($_)
            SetAuthenticationServiceURL($dirPath)
        }

        $dotNetTestArguments = @(
            $target,
            "--no-build",
            "--no-restore",
            "-v",
            "normal",
            "--logger",
            "trx;LogFileName=$trxFilePath",
            "--logger",
            "console",
            "--nologo"
        )

        if (-not [string]::IsNullOrWhiteSpace($normalizedTestFilter)) {
            $dotNetTestArguments += @("--filter", $normalizedTestFilter)
        }

        Invoke-Execute {
            dotnet test @dotNetTestArguments
        }
    }
}

function RunUnitTestsWithCoverage {
    # One `dotnet test` over a generated solution filter covering every *.Tests.Unit project.
    #
    # What this replaces: each assembly was wrapped in coverlet.console, which rewrote every DLL in
    # that project's output directory on disk, ran the tests, then restored the directory - once per
    # assembly, accumulating into a coverage.json that grew each pass. The threshold was applied only
    # to whichever assembly happened to sort last by name length, and a failure in any assembly
    # aborted the run before the later ones executed. The collector instruments in-process instead,
    # the whole set runs even when one project fails, and the threshold is applied once to the merged
    # total.
    $unitTestProjects = @(
        Get-RequiredUnitTestProject -SolutionRoot $solutionRoot -Filter "*.Tests.Unit" |
            Sort-Object -Property Name
    )

    Write-Output "Unit Test Projects List"
    Write-Output $unitTestProjects
    Write-Output "End Unit Test Projects List"

    if (-not (Test-Path $testResults)) {
        New-Item -ItemType Directory -Path $testResults -Force | Out-Null
    }

    $collectorOutput = Join-Path $testResults "unit-coverage"
    $mergedOutput = Join-Path $testResults "unit-coverage-merged"

    foreach ($staleDirectory in @($collectorOutput, $mergedOutput)) {
        if (Test-Path $staleDirectory) {
            # A report left by an earlier run would otherwise be merged into this one's total.
            Remove-Item -LiteralPath $staleDirectory -Recurse -Force
        }
    }

    if (Test-Path $coverageOutputFile) {
        # Cleared up front so a run that fails before the merge cannot leave the previous run's
        # report behind, where the workflow's hashFiles check and Coverage would read it as this
        # run's result.
        Remove-Item -LiteralPath $coverageOutputFile -Force
    }

    $solutionFilterPath = Join-Path $testResults "dms-unit-tests.slnf"
    $solutionFilterContent = ConvertTo-SolutionFilterContent `
        -SolutionPath ([System.IO.Path]::GetRelativePath($testResults, $defaultSolution)) `
        -ProjectPath @(
            $unitTestProjects | ForEach-Object {
                [System.IO.Path]::GetRelativePath($solutionRoot, $_.FullName)
            }
        )

    # The filter is generated rather than tracked so it cannot drift from the projects on disk.
    [System.IO.File]::WriteAllText(
        $solutionFilterPath,
        $solutionFilterContent,
        [System.Text.UTF8Encoding]::new($false)
    )

    Invoke-Execute {
        dotnet test $solutionFilterPath `
            --configuration $Configuration `
            --no-build `
            --no-restore `
            --blame `
            --collect:"XPlat Code Coverage" `
            --settings "$PSScriptRoot/eng/ci/coverlet.runsettings" `
            --results-directory $collectorOutput `
            --logger "trx" `
            --logger "console" `
            --nologo
    }

    # Each switch and its value must be ONE argument. A `--` earlier in a native command's arguments
    # puts PowerShell's parser into a mode where `-name:"value"` is emitted as two arguments,
    # `-name:` and the value, and ReportGenerator then reports "No report files specified" for an
    # invocation that looks correct. Quoting the whole `-name:value` token is what keeps it together.
    Invoke-Execute {
        dotnet tool run reportgenerator -- `
            "-reports:$collectorOutput/**/coverage.cobertura.xml" `
            "-targetdir:$mergedOutput" `
            "-reporttypes:Cobertura"
    }

    # $coverageOutputFile is relative, so this lands beside the caller exactly where the previous
    # driver left it - which is what the workflow's hashFiles check and `build-dms.ps1 Coverage`
    # both look for.
    Copy-Item -LiteralPath (Join-Path $mergedOutput "Cobertura.xml") -Destination $coverageOutputFile -Force

    $measured = Assert-CoverageThreshold -Path $coverageOutputFile -Threshold $thresholdCoverage

    Write-Output "Coverage: line $($measured.LinePercentage)%, branch $($measured.BranchPercentage)% (threshold $($measured.Threshold)%)"
}

function UnitTests {
    Invoke-Execute { RunTests -Filter "*.Tests.Unit" }
}

function IntegrationTests {
    Invoke-Execute { RunTests -Filter "*.Tests.Integration" }
}

function RunE2E {
    param(
        [string]
        $TestFilter,

        [pscustomobject]
        $E2ETestSettings
    )

    # Run only the standard E2E tests, excluding instance management tests
    # Instance management tests require special setup (route qualifiers, additional databases)
    # and should be run separately using the instance management test scripts
    Invoke-WithE2ETestProcessContext -E2ETestSettings $E2ETestSettings -Action {
        Invoke-Execute {
            RunTests `
                -Filter "EdFi.DataManagementService.Tests.E2E" `
                -TestFilter $TestFilter `
                -ResultNameSuffix $E2ETestSettings.TestResultSuffix
        }
    }
}

function Invoke-E2EDatabaseProvisioning {
    param(
        [pscustomobject]
        $E2ETestSettings
    )

    try {
        Push-Location "$PSScriptRoot/eng/docker-compose"
        $provisionOutput = @()
        ./provision-e2e-database.ps1 `
            -EnvironmentFile $E2ETestSettings.EnvironmentFile `
            -DatabaseEngine $E2ETestSettings.DatabaseEngine `
            -Configuration $Configuration 6>&1 |
            Tee-Object -Variable provisionOutput |
            ForEach-Object { Write-Host ([string]$_) }

        $provisionedEffectiveSchemaHash = Get-EffectiveSchemaHashFromOutput -Output $provisionOutput

        if ([string]::IsNullOrWhiteSpace($provisionedEffectiveSchemaHash)) {
            throw "E2E database provisioning completed without reporting an effective schema hash."
        }

        return $provisionedEffectiveSchemaHash
    }
    finally {
        Pop-Location
    }
}

function Write-DmsSchemaContainerEnvironment {
    param(
        [hashtable]
        $EnvironmentValues
    )

    Write-Output "DMS container schema environment:"
    foreach ($key in @(
            "AppSettings__Datastore",
            "AppSettings__UseApiSchemaPath",
            "AppSettings__ApiSchemaPath",
            "SCHEMA_PACKAGES"
        )) {
        if ($EnvironmentValues.ContainsKey($key)) {
            Write-Output "  $key = $($EnvironmentValues[$key])"
        }
        else {
            Write-Output "  $key = <not set>"
        }
    }
}

function Get-DmsRuntimeEffectiveSchemaHash {
    param(
        [string]
        $ContainerName,

        [datetime]
        $LogsSinceUtc = [datetime]::MinValue
    )

    $dockerLogArguments = @("logs")

    if ($LogsSinceUtc -ne [datetime]::MinValue) {
        $dockerLogArguments += @("--since", $LogsSinceUtc.ToUniversalTime().ToString("o"))
    }

    $dockerLogArguments += $ContainerName
    $dmsLogs = @(& docker @dockerLogArguments 2>&1)

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read Docker logs for container '$ContainerName'."
    }

    return Get-EffectiveSchemaHashFromOutput -Output $dmsLogs
}

function Assert-DmsRuntimeSchemaMatchesProvisionedDatabase {
    param(
        [string]
        $ProvisionedEffectiveSchemaHash,

        [string]
        $ContainerName,

        [datetime]
        $LogsSinceUtc = [datetime]::MinValue
    )

    Write-Output "Validating DMS runtime effective schema before E2E tests..."

    # The single container-environment reader, exported by the module imported at the top of this
    # script. This function used to carry its own copy under a different name; the two parsed the
    # same 'docker inspect --format {{json .Config.Env}}' output the same way and both failed closed,
    # so the copy only gave the next fix somewhere to land unnoticed.
    $environmentValues = Get-DmsContainerEnvironment -ContainerName $ContainerName
    Write-DmsSchemaContainerEnvironment -EnvironmentValues $environmentValues

    $dmsRuntimeEffectiveSchemaHash = Get-DmsRuntimeEffectiveSchemaHash `
        -ContainerName $ContainerName `
        -LogsSinceUtc $LogsSinceUtc

    Write-Output "Provisioned E2E effective schema hash: $ProvisionedEffectiveSchemaHash"
    Write-Output "DMS runtime effective schema hash: $dmsRuntimeEffectiveSchemaHash"

    if ([string]::IsNullOrWhiteSpace($dmsRuntimeEffectiveSchemaHash)) {
        docker logs --tail 120 $ContainerName 2>&1
        throw "DMS container '$ContainerName' did not report an effective schema hash before E2E tests."
    }

    if ($dmsRuntimeEffectiveSchemaHash -ne $ProvisionedEffectiveSchemaHash) {
        docker logs --tail 120 $ContainerName 2>&1
        throw "E2E setup mismatch: database was provisioned with effective schema hash '$ProvisionedEffectiveSchemaHash' but DMS runtime expects '$dmsRuntimeEffectiveSchemaHash'."
    }
}

function Start-DockerEnvironment {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal build orchestration helper; build-dms.ps1 does not expose -WhatIf end to end, so partial ShouldProcess support would create misleading no-op behavior.')]
    param (
        [switch]
        $UsePublishedImage,

        [switch]
        $SkipDockerBuild,

        [string]
        $IdentityProvider="self-contained",

        [string]
        $ResolvedEnvironmentFile,

        [string]
        $DataStoreDatabaseName = "",

        # Database engine backing the stack. "postgresql" (default) or "mssql". Forwarded to the
        # engine-aware start/configure leaf scripts and to teardown/failure cleanup.
        [string]
        $DatabaseEngine = "postgresql",

        [switch]
        $UseEnvironmentFileSchemaSettings,

        # Start only infrastructure + Configuration Service (via start-local-dms.ps1 -InfraOnly) and
        # defer the DMS container start to after E2E database provisioning. Used for the SQL Server
        # local-image E2E path, where the generated relational DDL must exist before DMS starts.
        [switch]
        $DeferDmsStart
    )

    $resolvedDatabaseEngine =
        if ([string]::IsNullOrWhiteSpace($DatabaseEngine)) { "postgresql" } else { $DatabaseEngine }

    $environmentFilePath =
        if ([string]::IsNullOrWhiteSpace($ResolvedEnvironmentFile)) {
            # Standalone entry points that bypass Get-E2ETestEnvironmentContext compose the overlays
            # here too, in the same order (data standard then engine); otherwise the configure step
            # below would read the raw base env file while DMS started on the selected data standard /
            # engine. The engine-aware leaf scripts re-compose idempotently.
            Import-Module -Name "$PSScriptRoot/eng/docker-compose/env-utility.psm1" -Force
            $baseEnvironmentFilePath = Resolve-E2EEnvironmentFilePath -Path $EnvironmentFile
            $dataStandardResolvedPath = Resolve-DataStandardEnvironmentFile `
                -DataStandardVersion $DataStandardVersion `
                -BaseEnvironmentFile $baseEnvironmentFilePath `
                -DockerComposeRoot "$PSScriptRoot/eng/docker-compose"
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine $resolvedDatabaseEngine `
                -BaseEnvironmentFile $dataStandardResolvedPath `
                -DockerComposeRoot "$PSScriptRoot/eng/docker-compose"
        }
        else {
            # Already composed by Get-E2ETestEnvironmentContext; use as-is (no double-composition).
            $ResolvedEnvironmentFile
        }

    if (-not $SkipDockerBuild -and -not $UsePublishedImage) {
        Invoke-Step { DockerBuild }
    }

    Stop-DockerEnvironment `
        -EnvironmentFilePath $environmentFilePath `
        -IdentityProvider $IdentityProvider `
        -DatabaseEngine $resolvedDatabaseEngine `
        -RemoveBootstrap `
        -UseEnvironmentFileSchemaSettings:$UseEnvironmentFileSchemaSettings

    Invoke-Execute {
        try {
            Push-Location "$PSScriptRoot/eng/docker-compose"
            # Choose the startup script by image mode. start-local-dms.ps1 and start-published-dms.ps1
            # share the -InfraOnly/-DmsOnly phase contract, so the deferred sequence is identical.
            $startupScriptPath = if ($UsePublishedImage) { "./start-published-dms.ps1" } else { "./start-local-dms.ps1" }

            if ($DeferDmsStart) {
                # SQL Server (either image mode) requires the generated DDL before DMS starts: bring up
                # only infrastructure + Configuration Service now, then create the data store. DMS is
                # started after provisioning by Initialize-E2EDatabase -StartDmsAfterProvisioning
                # (mirrors setup-local-dms.ps1's InfraOnly -> configure -> provision -> DmsOnly sequence).
                Invoke-WithDmsEnvironmentFileSchemaAuthority -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                    & $startupScriptPath -InfraOnly -EnvironmentFile $environmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -DatabaseEngine $resolvedDatabaseEngine -AddExtensionSecurityMetadata
                }
                # Neither start-published-dms.ps1 -InfraOnly nor start-local-dms.ps1 -InfraOnly creates a
                # data store; create it explicitly for both image modes so provisioning and DMS startup
                # find the instance in CMS.
                ./configure-local-data-store.ps1 -EnvironmentFile $environmentFilePath -DataStoreDatabaseName $DataStoreDatabaseName -DatabaseEngine $resolvedDatabaseEngine
            }
            elseif ($UsePublishedImage) {
                # Published image, PostgreSQL: full start. start-published-dms.ps1 creates the data store
                # internally from -DataStoreDatabaseName.
                Invoke-WithDmsEnvironmentFileSchemaAuthority -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                    & $startupScriptPath -EnvironmentFile $environmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -DatabaseEngine $resolvedDatabaseEngine -AddExtensionSecurityMetadata -DataStoreDatabaseName $DataStoreDatabaseName
                }
            }
            else {
                # Local image, PostgreSQL: start-local-dms.ps1 is infrastructure-lifecycle-only as of
                # DMS-1153 and no longer accepts -LoadSeedData.
                #
                # This flow is intentionally outside the bootstrap-manifest contract: the
                # -RemoveBootstrap teardown above guarantees no manifest is staged, so the claims-ready
                # gate is skipped. The DMS container restarts until the configure step below lands the
                # data store (restart: unless-stopped).
                Invoke-WithDmsEnvironmentFileSchemaAuthority -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                    & $startupScriptPath -EnvironmentFile $environmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -DatabaseEngine $resolvedDatabaseEngine -AddExtensionSecurityMetadata
                }

                # start-local-dms.ps1 no longer creates a default data store (DMS-1153 de-scope);
                # create it explicitly so DMS startup finds an instance in CMS.
                ./configure-local-data-store.ps1 -EnvironmentFile $environmentFilePath -DataStoreDatabaseName $DataStoreDatabaseName -DatabaseEngine $resolvedDatabaseEngine
            }
        }
        finally {
            Pop-Location
        }
    }
}

function Start-BootstrapDockerEnvironment {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal build orchestration helper; build-dms.ps1 does not expose -WhatIf end to end, so partial ShouldProcess support would create misleading no-op behavior.')]
    param (
        [switch]
        $UsePublishedImage,

        [switch]
        $SkipDockerBuild,

        [switch]
        $LoadSeedData,

        # Forwarded to the bootstrap wrapper only when supplied, so the wrapper's own default
        # ("postgresql") governs when this is omitted. Validated against
        # ValidateSet("postgresql", "mssql") at the top-level parameter; left as a plain string
        # here so forwarding an unset (null) value from the caller does not trip validation.
        [string]
        $DatabaseEngine,

        [string]
        $IdentityProvider="self-contained",

        # Forwarded to the bootstrap wrapper unchanged; see the top-level parameter for semantics.
        [switch]
        $SeparateConfigDatabase,

        # Forwarded to the bootstrap wrapper only when the caller explicitly supplied it (see
        # $dataStandardVersionSupplied), so the wrapper's own default-composition behavior governs
        # when it is absent.
        [string]
        $DataStandardVersion,

        [switch]
        $DataStandardVersionSupplied
    )

    $environmentFilePath = Resolve-E2EEnvironmentFilePath -Path $EnvironmentFile
    $effectiveDatabaseEngine =
        if ([string]::IsNullOrWhiteSpace($DatabaseEngine)) {
            "postgresql"
        }
        else {
            $DatabaseEngine
        }

    if (-not $SkipDockerBuild -and -not $UsePublishedImage) {
        Invoke-Step { DockerBuild }
    }

    Stop-DockerEnvironment `
        -EnvironmentFilePath $environmentFilePath `
        -IdentityProvider $IdentityProvider `
        -DatabaseEngine $effectiveDatabaseEngine

    Invoke-Execute {
        try {
            Push-Location "$PSScriptRoot/eng/docker-compose"

            $bootstrapArgs = @{
                EnvironmentFile = $environmentFilePath
                EnableConfig = $true
                IdentityProvider = $IdentityProvider
                AddExtensionSecurityMetadata = $true
            }

            if ($LoadSeedData) {
                $bootstrapArgs.LoadSeedData = $true
            }

            if ($DatabaseEngine) {
                $bootstrapArgs.DatabaseEngine = $DatabaseEngine
            }

            if ($SeparateConfigDatabase) {
                $bootstrapArgs.SeparateConfigDatabase = $true
            }

            if ($DataStandardVersionSupplied) {
                $bootstrapArgs.DataStandardVersion = $DataStandardVersion
            }

            Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
                if ($UsePublishedImage) {
                    ./bootstrap-published-dms.ps1 @bootstrapArgs
                }
                else {
                    ./bootstrap-local-dms.ps1 @bootstrapArgs
                }
            }
        }
        finally {
            Pop-Location
        }
    }
}

function Initialize-E2EDatabase {
    param(
        [pscustomobject]
        $E2ETestSettings,

        [switch]
        $UsePublishedImage,

        [string]
        $IdentityProvider = "self-contained",

        # When set, DMS was not started with the stack (Start-DockerEnvironment -DeferDmsStart). Start it
        # now via start-local-dms.ps1 -DmsOnly, after the relational schema is provisioned, instead of
        # restarting an already-running container. Used for the SQL Server local-image E2E path, which
        # requires the generated DDL before DMS starts (mirrors setup-local-dms.ps1's DmsOnly phase).
        [switch]
        $StartDmsAfterProvisioning
    )

    $dmsContainerName =
        if ($UsePublishedImage) {
            "dms-published-dms-1"
        }
        else {
            "ed-fi-api"
        }

    $provisionedEffectiveSchemaHash = Invoke-E2EDatabaseProvisioning -E2ETestSettings $E2ETestSettings
    $dmsStartedAtUtc = [DateTime]::UtcNow.AddSeconds(-2)
    if ($StartDmsAfterProvisioning) {
        # DMS start was deferred so the generated SQL Server DDL exists first; start it now that the
        # relational schema is provisioned, using the image-appropriate startup script (both
        # start-local-dms.ps1 and start-published-dms.ps1 share the -DmsOnly phase). Reuses the same
        # environment file, engine, identity provider, and schema-settings gate as the -InfraOnly phase
        # in Start-DockerEnvironment.
        $startupScriptPath = if ($UsePublishedImage) { "./start-published-dms.ps1" } else { "./start-local-dms.ps1" }
        Invoke-Execute {
            try {
                Push-Location "$PSScriptRoot/eng/docker-compose"
                Invoke-WithDmsEnvironmentFileSchemaAuthority -Enabled:$E2ETestSettings.ShouldProvisionE2EDatabase -Action {
                    & $startupScriptPath -DmsOnly -EnvironmentFile $E2ETestSettings.EnvironmentFile -EnableConfig -IdentityProvider $IdentityProvider -DatabaseEngine $E2ETestSettings.DatabaseEngine -AddExtensionSecurityMetadata
                }
            }
            finally {
                Pop-Location
            }
        }
    }
    else {
        Restart-DmsContainer `
            -ContainerName $dmsContainerName `
            -Reason "discard cached datastore connection pools after E2E database reprovisioning"
    }
    Assert-DmsRuntimeSchemaMatchesProvisionedDatabase `
        -ProvisionedEffectiveSchemaHash $provisionedEffectiveSchemaHash `
        -ContainerName $dmsContainerName `
        -LogsSinceUtc $dmsStartedAtUtc
}

function E2ETests {
    param(
        [switch]
        $UsePublishedImage,

        [switch]
        $SkipDockerBuild,

        [switch]
        $LoadSeedData,

        [string]
        $IdentityProvider="self-contained",

        [string]
        $TestFilter,

        # Database engine backing the E2E stack. "postgresql" (default) or "mssql". Resolved once in
        # Get-E2ETestEnvironmentContext (empty is normalized to postgresql) and reused from the
        # returned context for every downstream step.
        [string]
        $DatabaseEngine = "postgresql"
    )

    if ($LoadSeedData) {
        throw "E2ETest -LoadSeedData is not supported after legacy backend removal. E2ETest resets and provisions E2E_DATABASE_NAME with provision-e2e-database.ps1 before tests run; use StartEnvironment -LoadSeedData or add a relational/API seed path instead."
    }

    $e2eTestSettings = Get-E2ETestEnvironmentContext -EnvironmentFile $EnvironmentFile -TestFilter $TestFilter -DatabaseEngine $DatabaseEngine

    # Resolve the startup phase plan once (single decision point, unit-tested in
    # E2EEngineForwarding.Tests.ps1). SQL Server requires the generated relational DDL to exist before
    # DMS starts, so for MSSQL - in either image mode - start infrastructure + Configuration Service
    # only, configure the data store, provision the schema, then start DMS: the InfraOnly -> configure
    # -> provision -> DmsOnly sequence proven by setup-local-dms.ps1. PostgreSQL keeps its proven
    # full-stack start followed by a post-provisioning restart.
    Import-Module -Name "$PSScriptRoot/eng/Dms-Management.psm1" -Force
    $startupPlan = Get-E2EStartupPhasePlan -DatabaseEngine $e2eTestSettings.DatabaseEngine -UsePublishedImage:$UsePublishedImage
    $deferDmsStart = $startupPlan.DeferDmsStart

    Invoke-Step {
        Start-DockerEnvironment `
            -UsePublishedImage:$UsePublishedImage `
            -SkipDockerBuild:$SkipDockerBuild `
            -IdentityProvider $IdentityProvider `
            -ResolvedEnvironmentFile $e2eTestSettings.EnvironmentFile `
            -DataStoreDatabaseName $e2eTestSettings.DataStoreDatabaseName `
            -DatabaseEngine $e2eTestSettings.DatabaseEngine `
            -UseEnvironmentFileSchemaSettings:$e2eTestSettings.ShouldProvisionE2EDatabase `
            -DeferDmsStart:$deferDmsStart
    }

    Invoke-Step {
        Initialize-E2EDatabase `
            -E2ETestSettings $e2eTestSettings `
            -UsePublishedImage:$UsePublishedImage `
            -IdentityProvider $IdentityProvider `
            -StartDmsAfterProvisioning:$deferDmsStart
    }

    Invoke-Step { RunE2E -TestFilter $TestFilter -E2ETestSettings $e2eTestSettings }
}

function Wait-ForConfigServiceAndClientRegistration {
    Write-Host "Waiting for config service and OpenIddict clients to be fully initialized..." -ForegroundColor Cyan
    $maxAttempts = 60
    $attempt = 0
    $ready = $false

    while (-not $ready -and $attempt -lt $maxAttempts) {
        $attempt++
        Write-Host "Checking if CMSAuthMetadataReadOnlyAccess client is registered (attempt $attempt/$maxAttempts)..." -ForegroundColor Yellow

        try {
            # Try to get a token using the CMSAuthMetadataReadOnlyAccess client
            $tokenEndpoint = "http://localhost:8081/connect/token"
            $body = @{
                client_id = "CMSAuthMetadataReadOnlyAccess"
                client_secret = "ValidClientSecret1234567890!Abcd"
                grant_type = "client_credentials"
                scope = "edfi_admin_api/authMetadata_readonly_access"
            }

            $response = Invoke-RestMethod -Uri $tokenEndpoint -Method Post -Body $body -ContentType "application/x-www-form-urlencoded" -ErrorAction Stop

            if ($response.access_token) {
                $ready = $true
                Write-Host "Config service is ready and clients are registered!" -ForegroundColor Green
            }
        }
        catch {
            Write-Host "Config service or clients not ready yet. Error: $($_.Exception.Message)" -ForegroundColor Yellow
        }

        if (-not $ready) {
            Start-Sleep -Seconds 2
        }
    }

    if (-not $ready) {
        throw "Config service did not become ready with registered clients within the timeout period"
    }
}

function Restart-DmsContainer {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal build orchestration helper; build-dms.ps1 does not expose -WhatIf end to end, so partial ShouldProcess support would create misleading no-op behavior.')]
    param(
        [string]
        $ContainerName = "ed-fi-api",

        [string]
        $Reason = "refresh runtime state"
    )

    Write-Host "Restarting DMS container to $Reason..." -ForegroundColor Cyan

    docker restart $ContainerName

    Write-Host "Waiting for DMS to be ready..." -ForegroundColor Yellow
    Start-Sleep -Seconds 10

    # Wait for DMS health check
    $maxAttempts = 30
    $attempt = 0
    $ready = $false

    while (-not $ready -and $attempt -lt $maxAttempts) {
        $attempt++
        Write-Host "Checking DMS health (attempt $attempt/$maxAttempts)..." -ForegroundColor Yellow

        try {
            $response = Invoke-WebRequest -Uri "http://localhost:8080/health" -Method Get -TimeoutSec 5 -ErrorAction Stop
            if ($response.StatusCode -eq 200) {
                $ready = $true
                Write-Host "DMS is ready!" -ForegroundColor Green
            }
        }
        catch {
            Write-Host "DMS not ready yet" -ForegroundColor Yellow
        }

        if (-not $ready) {
            Start-Sleep -Seconds 2
        }
    }

    if (-not $ready) {
        throw "DMS container '$ContainerName' did not become ready within the timeout period"
    }
}

function Wait-ForPostgreSQL {
    Write-Host "Waiting for PostgreSQL to be ready..." -ForegroundColor Cyan
    $maxAttempts = 30
    $attempt = 0
    $ready = $false

    while (-not $ready -and $attempt -lt $maxAttempts) {
        $attempt++
        Write-Host "Checking PostgreSQL readiness (attempt $attempt/$maxAttempts)..." -ForegroundColor Yellow

        try {
            $null = docker exec dms-postgresql pg_isready -U postgres 2>&1
            if ($LASTEXITCODE -eq 0) {
                $ready = $true
                Write-Host "PostgreSQL is ready!" -ForegroundColor Green
            }
        }
        catch {
            Write-Host "PostgreSQL not ready yet: $_" -ForegroundColor Yellow
        }

        if (-not $ready) {
            Start-Sleep -Seconds 2
        }
    }

    if (-not $ready) {
        throw "PostgreSQL did not become ready within the timeout period"
    }
}

function Resolve-InstanceE2EBaseEnvironmentFile {
    <#
    .SYNOPSIS
    Resolves the Instance Management E2E base environment file, defaulting to the tracked
    route-context env file at the docker-compose root when no explicit file is supplied.

    .DESCRIPTION
    When -EnvironmentFile is empty (the InstanceE2ETest default), the tracked
    <docker-compose>/.env.routeContext.e2e is used so the documented repo-root entry point
    (./build-dms.ps1 InstanceE2ETest) works regardless of the caller's current working directory.
    An explicitly supplied path keeps the caller-relative (relative) or verbatim (absolute) contract
    of Resolve-LocalSettingsEnvironmentFile, whose repository-wide semantics are unchanged. A missing
    file fails fast.
    #>
    param(
        [string]
        $EnvironmentFile,

        [Parameter(Mandatory)]
        [string]
        $DockerComposeRoot
    )

    if ([string]::IsNullOrWhiteSpace($EnvironmentFile)) {
        $defaultRouteContextEnvironmentFile = Join-Path $DockerComposeRoot ".env.routeContext.e2e"
        if (-not (Test-Path -LiteralPath $defaultRouteContextEnvironmentFile -PathType Leaf)) {
            throw "Instance Management E2E default environment file not found: $defaultRouteContextEnvironmentFile"
        }
        return $defaultRouteContextEnvironmentFile
    }

    return Resolve-LocalSettingsEnvironmentFile -Path $EnvironmentFile -DockerComposeRoot $DockerComposeRoot
}

function Get-InstanceE2ETestEnvironmentContext {
    param(
        [string]
        $EnvironmentFile,

        # Database engine backing the Instance stack. "postgresql" (default) or "mssql". An empty
        # value is normalized to postgresql so an omitted top-level -DatabaseEngine is behavior-compatible.
        [string]
        $DatabaseEngine = "postgresql"
    )

    $resolvedDatabaseEngine =
        if ([string]::IsNullOrWhiteSpace($DatabaseEngine)) { "postgresql" } else { $DatabaseEngine }

    $dockerComposeRoot = "$PSScriptRoot/eng/docker-compose"
    Import-Module -Name "$dockerComposeRoot/env-utility.psm1" -Force
    Import-Module -Name "$dockerComposeRoot/database-safety.psm1" -Force
    Import-Module -Name "$PSScriptRoot/eng/Dms-Management.psm1" -Force

    # Single resolution point for the Instance suite: compose the data-standard overlay first, then the
    # database-engine overlay, so setup, provisioning, fixture registration, teardown, and the test
    # process all read the same resolved file. PostgreSQL and an omitted engine are behavior-compatible.
    $baseEnvironmentFile = Resolve-InstanceE2EBaseEnvironmentFile -EnvironmentFile $EnvironmentFile -DockerComposeRoot $dockerComposeRoot
    $resolvedEnvironmentFile = Resolve-DataStandardEnvironmentFile `
        -DataStandardVersion $DataStandardVersion `
        -BaseEnvironmentFile $baseEnvironmentFile `
        -DockerComposeRoot $dockerComposeRoot
    $resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile `
        -DatabaseEngine $resolvedDatabaseEngine `
        -BaseEnvironmentFile $resolvedEnvironmentFile `
        -DockerComposeRoot $dockerComposeRoot
    $environmentValues = ReadValuesFromEnvFile $resolvedEnvironmentFile

    # Read the three route-context database names from the resolved environment; require three
    # non-empty distinct names and never fall back to a fixed name.
    $databaseNames = @(
        (Get-EnvValue -EnvValues $environmentValues -Name "INSTANCE_E2E_DATABASE_1_NAME"),
        (Get-EnvValue -EnvValues $environmentValues -Name "INSTANCE_E2E_DATABASE_2_NAME"),
        (Get-EnvValue -EnvValues $environmentValues -Name "INSTANCE_E2E_DATABASE_3_NAME")
    )

    for ($databaseIndex = 0; $databaseIndex -lt $databaseNames.Count; $databaseIndex++) {
        if ([string]::IsNullOrWhiteSpace($databaseNames[$databaseIndex])) {
            throw "INSTANCE_E2E_DATABASE_$($databaseIndex + 1)_NAME must be set in '$resolvedEnvironmentFile' so the Instance Management E2E route-context databases can be provisioned and registered."
        }
    }

    if (@($databaseNames | Sort-Object -Unique).Count -ne $databaseNames.Count) {
        throw "The three INSTANCE_E2E_DATABASE_*_NAME values must be distinct; got: $($databaseNames -join ', ')."
    }

    # Validate every route-context database name up front - safe characters, not a reserved system
    # database, and dedicated (never the primary or CMS database by name or by the database embedded
    # in the admin/CMS connection strings) - BEFORE any registration connection string is built and
    # BEFORE the setup script provisions anything, so an unsafe or shared name fails fast and can never
    # reach a DROP/CREATE. provision-e2e-database.ps1 re-checks each name when it provisions (defense in
    # depth); this is the earliest gate in the Instance flow.
    foreach ($databaseName in $databaseNames) {
        Assert-E2EDatabaseIsDedicated `
            -EnvironmentValues $environmentValues `
            -EnvironmentFilePath $resolvedEnvironmentFile `
            -E2EDatabaseName $databaseName
    }

    # Docker-network registration connection string per database (dms-postgresql:5432 or
    # dms-mssql,1433), built once from the resolved credentials via the shared connection-string helper.
    # These carry secrets and are never written to host output.
    $registrationConnectionStrings = @(
        foreach ($databaseName in $databaseNames) {
            (New-E2EDataStoreConnectionStrings `
                    -DatabaseEngine $resolvedDatabaseEngine `
                    -EnvironmentValues $environmentValues `
                    -DatabaseName $databaseName).RegistrationConnectionString
        }
    )

    return [pscustomobject]@{
        EnvironmentFile               = $baseEnvironmentFile
        ResolvedEnvironmentFile       = $resolvedEnvironmentFile
        DatabaseEngine                = $resolvedDatabaseEngine
        DatabaseNames                 = $databaseNames
        RegistrationConnectionStrings = $registrationConnectionStrings
        EnvironmentValues             = $environmentValues
    }
}

function Register-InstanceE2EFixture {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal build orchestration helper; build-dms.ps1 does not expose -WhatIf end to end.')]
    param(
        [pscustomobject]
        $InstanceE2ESettings
    )

    Import-Module -Name "$PSScriptRoot/eng/docker-compose/env-utility.psm1" -Force
    Import-Module -Name "$PSScriptRoot/eng/Dms-Management.psm1" -Force

    $environmentValues = $InstanceE2ESettings.EnvironmentValues
    $cmsUrl = Resolve-CmsBaseUrl -EnvValues $environmentValues
    $bootstrapAdmin = Resolve-BootstrapAdminClient -EnvValues $environmentValues

    # Register the admin client (idempotent) and obtain a full-access token, mirroring
    # configure-local-data-store.ps1. Client id/secret and the token are never written to host output.
    Add-CmsClient `
        -CmsUrl $cmsUrl `
        -ClientId $bootstrapAdmin.ClientId `
        -ClientSecret $bootstrapAdmin.ClientSecret `
        -DisplayName "Instance E2E Fixture Administrator"
    $accessToken = Get-CmsToken `
        -CmsUrl $cmsUrl `
        -ClientId $bootstrapAdmin.ClientId `
        -ClientSecret $bootstrapAdmin.ClientSecret

    # Resolved with Compose precedence for consistency with the registration connection strings;
    # Add-DataStore ignores this credential whenever an explicit -ConnectionString is supplied, so
    # this only matters if a future caller registers without one.
    $postgresUser = Get-ComposeResolvedEnvValue -EnvironmentValues $environmentValues -Name "POSTGRES_USER" -DefaultValue "postgres"
    $postgresPassword = Get-ComposeResolvedEnvValue -EnvironmentValues $environmentValues -Name "POSTGRES_PASSWORD" -DefaultValue "abcdefgh1!"
    $postgresCredential = ConvertTo-PostgresCredential -UserName $postgresUser -Secret $postgresPassword

    # Canonical fixture routes: three data stores under two tenants (255901/2024 and 255901/2025 under
    # Tenant_255901; 255902/2024 under Tenant_255902). Index maps to the resolved database/registration
    # string in $InstanceE2ESettings.
    $routeDefinitions = @(
        [pscustomobject]@{ TenantName = "Tenant_255901"; DistrictId = "255901"; SchoolYear = "2024"; Index = 0 },
        [pscustomobject]@{ TenantName = "Tenant_255901"; DistrictId = "255901"; SchoolYear = "2025"; Index = 1 },
        [pscustomobject]@{ TenantName = "Tenant_255902"; DistrictId = "255902"; SchoolYear = "2024"; Index = 2 }
    )
    $tenantOrder = @("Tenant_255901", "Tenant_255902")

    $tenants = @(
        foreach ($tenantName in $tenantOrder) {
            Add-Tenant -CmsUrl $cmsUrl -AccessToken $accessToken -TenantName $tenantName | Out-Null

            # Vendor Company is globally unique in CMS (UX_Vendor_Company), so each canonical fixture
            # tenant must register a distinct, deterministic company name.
            $vendorId = Add-Vendor `
                -CmsUrl $cmsUrl `
                -AccessToken $accessToken `
                -Tenant $tenantName `
                -Company "Instance E2E Fixture Vendor $tenantName" `
                -NamespacePrefixes "uri://ed-fi.org"

            $tenantRoutes = @($routeDefinitions | Where-Object { $_.TenantName -eq $tenantName })

            # One structured route record per data store, capturing the CMS-assigned data-store id and
            # both route-context ids (districtId, schoolYear) alongside the resolved database name/index
            # this route is bound to. These records back the engine-neutral, non-secret route manifest.
            $routeRecords = @(
                foreach ($route in $tenantRoutes) {
                    $dataStoreId = Add-DataStore `
                        -CmsUrl $cmsUrl `
                        -AccessToken $accessToken `
                        -Tenant $tenantName `
                        -DataStoreType "District" `
                        -Name "Instance E2E $($route.DistrictId)/$($route.SchoolYear)" `
                        -PostgresCredential $postgresCredential `
                        -ConnectionString $InstanceE2ESettings.RegistrationConnectionStrings[$route.Index]

                    $districtContextId = Add-DataStoreContext -CmsUrl $cmsUrl -AccessToken $accessToken -Tenant $tenantName -DataStoreId $dataStoreId -ContextKey "districtId" -ContextValue $route.DistrictId
                    $schoolYearContextId = Add-DataStoreContext -CmsUrl $cmsUrl -AccessToken $accessToken -Tenant $tenantName -DataStoreId $dataStoreId -ContextKey "schoolYear" -ContextValue $route.SchoolYear

                    [pscustomobject]@{
                        TenantName          = $tenantName
                        DistrictId          = $route.DistrictId
                        SchoolYear          = $route.SchoolYear
                        DatabaseOrdinal     = $route.Index + 1
                        DatabaseName        = [string]$InstanceE2ESettings.DatabaseNames[$route.Index]
                        DataStoreId         = [long]$dataStoreId
                        DistrictContextId   = $districtContextId
                        SchoolYearContextId = $schoolYearContextId
                    }
                }
            )

            $dataStoreIds = @($routeRecords | ForEach-Object { $_.DataStoreId })
            $educationOrganizationIds = @($tenantRoutes | ForEach-Object { [long]$_.DistrictId } | Sort-Object -Unique)

            $application = Add-Application `
                -CmsUrl $cmsUrl `
                -AccessToken $accessToken `
                -Tenant $tenantName `
                -ApplicationName "Instance E2E $tenantName" `
                -ClaimSetName "E2E-NoFurtherAuthRequiredClaimSet" `
                -VendorId $vendorId `
                -EducationOrganizationIds $educationOrganizationIds `
                -DataStoreIds $dataStoreIds

            [pscustomobject]@{
                TenantName    = $tenantName
                VendorId      = $vendorId
                DataStoreIds  = $dataStoreIds
                Routes        = $routeRecords
                ApplicationId = $application.Id
                ClientKey     = $application.Key
                ClientSecret  = $application.Secret
            }
        }
    )

    return [pscustomobject]@{
        Tenants        = $tenants
        DataStoreIds   = @($tenants | ForEach-Object { $_.DataStoreIds } | ForEach-Object { $_ })
        ApplicationIds = @($tenants | ForEach-Object { $_.ApplicationId })
        Routes         = @($tenants | ForEach-Object { $_.Routes } | ForEach-Object { $_ })
    }
}

function Invoke-WithInstanceE2ETestProcessContext {
    param(
        [pscustomobject]
        $InstanceE2ESettings,

        [pscustomobject]
        $Fixture,

        [scriptblock]
        $Action
    )

    # Engine-neutral, non-secret route manifest: the exact tenant -> district/schoolYear -> database ->
    # data-store/route-context mapping the fixture created, as compact JSON. Contains no keys, secrets,
    # passwords, tokens, or connection strings, so it is safe for the test process to read and log.
    $routeManifestJson = ConvertTo-Json -Compress -Depth 5 -InputObject @(
        foreach ($route in $Fixture.Routes) {
            [ordered]@{
                tenant              = [string]$route.TenantName
                districtId          = [string]$route.DistrictId
                schoolYear          = [string]$route.SchoolYear
                databaseOrdinal     = [int]$route.DatabaseOrdinal
                databaseName        = [string]$route.DatabaseName
                dataStoreId         = [long]$route.DataStoreId
                districtContextId   = [long]$route.DistrictContextId
                schoolYearContextId = [long]$route.SchoolYearContextId
            }
        }
    )

    # Opaque environment variables the Instance suite's test process consumes. Names are stable and
    # engine-neutral. The connection-string values are the exact engine-correct Docker-network strings the
    # fixture registered (index-aligned to the database ordinals), so the C# consumers never re-derive
    # credentials, ports, host names, or connection-string syntax. Both the credential and connection-string
    # values carry secrets: they are set into the environment only and are never written to host output.
    $processVariables = [ordered]@{
        "INSTANCE_E2E_DATABASE_ENGINE"                 = [string]$InstanceE2ESettings.DatabaseEngine
        "INSTANCE_E2E_DATABASE_1_NAME"                 = [string]$InstanceE2ESettings.DatabaseNames[0]
        "INSTANCE_E2E_DATABASE_2_NAME"                 = [string]$InstanceE2ESettings.DatabaseNames[1]
        "INSTANCE_E2E_DATABASE_3_NAME"                 = [string]$InstanceE2ESettings.DatabaseNames[2]
        "INSTANCE_E2E_DATABASE_1_CONNECTION_STRING"    = [string]$InstanceE2ESettings.RegistrationConnectionStrings[0]
        "INSTANCE_E2E_DATABASE_2_CONNECTION_STRING"    = [string]$InstanceE2ESettings.RegistrationConnectionStrings[1]
        "INSTANCE_E2E_DATABASE_3_CONNECTION_STRING"    = [string]$InstanceE2ESettings.RegistrationConnectionStrings[2]
        "INSTANCE_E2E_ROUTE_MANIFEST"                  = [string]$routeManifestJson
        "INSTANCE_E2E_FIXTURE_TENANT_1_NAME"           = [string]$Fixture.Tenants[0].TenantName
        "INSTANCE_E2E_FIXTURE_TENANT_1_VENDOR_ID"      = [string]$Fixture.Tenants[0].VendorId
        "INSTANCE_E2E_FIXTURE_TENANT_1_APPLICATION_ID" = [string]$Fixture.Tenants[0].ApplicationId
        "INSTANCE_E2E_FIXTURE_TENANT_1_CLIENT_KEY"     = [string]$Fixture.Tenants[0].ClientKey
        "INSTANCE_E2E_FIXTURE_TENANT_1_CLIENT_SECRET"  = [string]$Fixture.Tenants[0].ClientSecret
        "INSTANCE_E2E_FIXTURE_TENANT_2_NAME"           = [string]$Fixture.Tenants[1].TenantName
        "INSTANCE_E2E_FIXTURE_TENANT_2_VENDOR_ID"      = [string]$Fixture.Tenants[1].VendorId
        "INSTANCE_E2E_FIXTURE_TENANT_2_APPLICATION_ID" = [string]$Fixture.Tenants[1].ApplicationId
        "INSTANCE_E2E_FIXTURE_TENANT_2_CLIENT_KEY"     = [string]$Fixture.Tenants[1].ClientKey
        "INSTANCE_E2E_FIXTURE_TENANT_2_CLIENT_SECRET"  = [string]$Fixture.Tenants[1].ClientSecret
        "INSTANCE_E2E_FIXTURE_DATASTORE_IDS"           = ($Fixture.DataStoreIds -join ",")
    }

    # Capture existence independently from value (PowerShell retains empty/whitespace variables) so the
    # unset-versus-valued distinction is preserved on restore.
    $previousState = @{}
    foreach ($name in $processVariables.Keys) {
        $previousState[$name] = [pscustomobject]@{
            Existed = (Test-Path -LiteralPath "Env:$name")
            Value   = [System.Environment]::GetEnvironmentVariable($name)
        }
    }
    $previousNodeOptionsExists = Test-Path Env:NODE_OPTIONS
    $previousNodeOptions = $env:NODE_OPTIONS

    try {
        foreach ($name in $processVariables.Keys) {
            Set-Item -LiteralPath "Env:$name" -Value ([string]$processVariables[$name])
        }
        Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
        & $Action
    }
    finally {
        foreach ($name in $processVariables.Keys) {
            if ($previousState[$name].Existed) {
                Set-Item -LiteralPath "Env:$name" -Value $previousState[$name].Value
            }
            else {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
        }
        if ($previousNodeOptionsExists) {
            $env:NODE_OPTIONS = $previousNodeOptions
        }
        else {
            Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
        }
    }
}

function RunInstanceE2E {
    param (
        [string]
        $TestFilter
    )

    # Run only the instance management E2E tests
    $testProject = "$solutionRoot/tests/EdFi.InstanceManagement.Tests.E2E/EdFi.InstanceManagement.Tests.E2E.csproj"
    $normalizedTestFilter = ConvertTo-NormalizedTestFilter -TestFilter $TestFilter
    $resultNameSuffix =
        if ($normalizedTestFilter -match '(?i)\b(?:TestCategory|Category)\s*=\s*instance-management-ci-shard-(\d+)\b') {
            ".instance-shard-$($Matches[1])"
        }
        else {
            ""
        }
    $trxFile = "$testResults/EdFi.InstanceManagement.Tests.E2E$resultNameSuffix.trx"

    if (-not [string]::IsNullOrWhiteSpace($normalizedTestFilter) -and $normalizedTestFilter -ne $TestFilter) {
        Write-Output "Normalized test filter for VSTest: '$TestFilter' -> '$normalizedTestFilter'"
    }

    $dotNetTestArguments = @(
        $testProject,
        "--configuration",
        $Configuration,
        "--logger",
        "trx;LogFileName=$trxFile",
        "--logger",
        "console",
        "--verbosity",
        "normal",
        "--nologo"
    )

    if (-not [string]::IsNullOrWhiteSpace($normalizedTestFilter)) {
        $dotNetTestArguments += @("--filter", $normalizedTestFilter)
    }

    Invoke-Execute {
        dotnet test @dotNetTestArguments
    }
}

function InstanceE2ETests {
    param (
        [switch]
        $SkipDockerBuild,

        [string]
        $TestFilter,

        # Optional Ed-Fi Data Standard version forwarded to the instance setup script so the
        # route-context stack AND its database provisioning run on the requested version (e.g. 6.1).
        # Empty (the default) leaves the DS 5.2 behavior unchanged.
        [string]
        $DataStandardVersion,

        # Database engine backing the Instance stack. "postgresql" (default) or "mssql". Resolved and
        # normalized once in Get-InstanceE2ETestEnvironmentContext.
        [string]
        $DatabaseEngine = "postgresql",

        # Environment file. Empty (the default) resolves to the tracked route-context env file at the
        # docker-compose root regardless of the current working directory; an explicit relative path is
        # caller-relative and an explicit absolute path is used verbatim.
        [string]
        $EnvironmentFile = ""
    )

    # Instance management tests require route qualifiers and three explicitly provisioned route-context databases.
    Write-Host "Setting up instance management E2E tests..." -ForegroundColor Cyan

    # Resolve the environment/engine and the three route-context databases once. The setup script,
    # fixture registration, teardown, and the test process all consume this context.
    $instanceSettings = Get-InstanceE2ETestEnvironmentContext -EnvironmentFile $EnvironmentFile -DatabaseEngine $DatabaseEngine

    # Tear down any prior dms-local stack for the SAME engine and the SAME final resolved environment
    # BEFORE setup, so pre-clean composes the identical engine set that setup/registration consume and
    # setup never runs against a stale stack (a previous engine's containers/volumes or a leftover run).
    # Uses the shared, project-scoped teardown primitive (start-local-dms.ps1 -d -v), which removes only
    # the dms-local compose project plus the two known local images. When reusing built images
    # (-SkipDockerBuild) the images are kept; otherwise they are removed ahead of the rebuild the setup
    # performs. The base env file is retained only for the standalone teardown guidance.
    $dockerComposeRoot = "$PSScriptRoot/eng/docker-compose"
    Import-Module -Name "$dockerComposeRoot/e2e-teardown.psm1" -Force
    Invoke-Step {
        $null = Invoke-E2EEngineAwareTeardown `
            -DatabaseEngine $instanceSettings.DatabaseEngine `
            -EnvironmentFile $instanceSettings.ResolvedEnvironmentFile `
            -ComposeRoot $dockerComposeRoot `
            -SkipLocalImageRemoval:$SkipDockerBuild
    }

    $instanceSetupScript = "$solutionRoot/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"

    if (Test-Path $instanceSetupScript) {
        Write-Host "Starting Docker environment and route-context provisioning ($($instanceSettings.DatabaseEngine))..." -ForegroundColor Cyan
        # The setup script owns the deterministic order: InfraOnly/Config Service -> provision all three
        # route-context databases (generated engine-correct DDL) -> engine-dispatched schema verification
        # -> DmsOnly -> DMS health. The environment was already composed once here; pass both the base
        # file (for teardown guidance) and the resolved file (-ResolvedEnvironmentFile, used verbatim)
        # so the setup performs no second overlay composition.
        $setupParameters = @{
            DataStandardVersion     = $DataStandardVersion
            DatabaseEngine          = $instanceSettings.DatabaseEngine
            EnvironmentFile         = $instanceSettings.EnvironmentFile
            ResolvedEnvironmentFile = $instanceSettings.ResolvedEnvironmentFile
        }
        if ($SkipDockerBuild) {
            $setupParameters.SkipDockerBuild = $true
        }
        Invoke-Execute {
            & $instanceSetupScript @setupParameters
        }
    }
    else {
        throw "Instance Management setup script not found at: $instanceSetupScript"
    }

    # Wait for the Configuration Service and its registered clients (DMS is already healthy after the
    # setup's DmsOnly phase).
    Invoke-Step { Wait-ForConfigServiceAndClientRegistration }

    # Suite-owned fixture: register the three engine-correct data stores, route contexts, tenants,
    # vendors, and applications in CMS.
    $instanceFixture = Register-InstanceE2EFixture -InstanceE2ESettings $instanceSettings

    # Restart DMS exactly once AFTER registration so it picks up the registered route contexts, then
    # wait for DMS health (Restart-DmsContainer polls /health). There is no pre-registration or
    # per-store restart.
    Invoke-Step { Restart-DmsContainer -Reason "refresh route-context registrations after suite-owned Instance E2E fixture registration" }

    Write-Host "`nInstance E2E setup complete!" -ForegroundColor Green

    # Run the routed tests inside the Instance test-process context so they consume the fixture.
    Invoke-Step {
        Invoke-WithInstanceE2ETestProcessContext -InstanceE2ESettings $instanceSettings -Fixture $instanceFixture -Action {
            RunInstanceE2E -TestFilter $TestFilter
        }
    }

    Write-Host "`nTests complete!" -ForegroundColor Green
}

function RunNuGetPack {
    param (
        [string]
        $ProjectPath,

        [string]
        $PackageVersion,

        [string]
        $nuspecPath
    )

    $copyrightYear = ${(Get-Date).year)}
    # NU5100 is the warning about DLLs outside of a "lib" folder. We're
    # deliberately using that pattern, therefore we bypass the
    # warning.
    Invoke-Execute {
        dotnet pack $ProjectPath `
            --no-build `
            --no-restore `
            --output $PSScriptRoot `
            -p:NuspecFile=$nuspecPath `
            -p:NuspecProperties="version=$PackageVersion;year=$copyrightYear" `
            /p:NoWarn=NU5100
    }
}

function BuildApiPackage {
    $mainPath = "$applicationRoot/$projectName"
    $projectPath = "$mainPath/$projectName.csproj"
    $nugetSpecPath = "$mainPath/publish/$projectName.nuspec"
    $expectedPackagePath = "$PSScriptRoot/$packageName.$DMSVersion.nupkg"

    if (Test-Path $expectedPackagePath) {
        Remove-Item -LiteralPath $expectedPackagePath -ErrorAction Stop
    }

    RunNuGetPack -ProjectPath $projectPath -PackageVersion $DMSVersion $nugetSpecPath

    if (-not (Test-Path $expectedPackagePath)) {
        throw "Expected API package was not created: $expectedPackagePath"
    }
}

function BuildSchemaToolsPackage {
    $projectPath = "$clisRoot/$schemaToolsProjectName/$schemaToolsProjectName.csproj"
    $expectedPackagePath = "$PSScriptRoot/$schemaToolsPackageName.$DMSVersion.nupkg"

    Write-Info "Building $schemaToolsPackageName package"

    Invoke-Execute {
        if (Test-Path $expectedPackagePath) {
            Remove-Item -LiteralPath $expectedPackagePath -ErrorAction Stop
        }

        dotnet pack $projectPath `
            -c $Configuration `
            --no-build `
            --no-restore `
            --output $PSScriptRoot `
            -p:PackageVersion=$DMSVersion

        if (-not (Test-Path $expectedPackagePath)) {
            throw "Expected SchemaTools package was not created: $expectedPackagePath"
        }
    }
}

function BuildCustomValidationPackage {
    $projectPath = "$coreRoot/$customValidationProjectName/$customValidationProjectName.csproj"
    $expectedPackagePath = "$PSScriptRoot/$customValidationPackageName.$DMSVersion.nupkg"

    Write-Info "Building $customValidationPackageName package"

    Invoke-Execute {
        if (Test-Path $expectedPackagePath) {
            Remove-Item -LiteralPath $expectedPackagePath -ErrorAction Stop
        }

        dotnet pack $projectPath `
            -c $Configuration `
            --no-build `
            --no-restore `
            --output $PSScriptRoot `
            -p:PackageVersion=$DMSVersion

        if (-not (Test-Path $expectedPackagePath)) {
            throw "Expected custom-validation package was not created: $expectedPackagePath"
        }
    }
}

function BuildDocumentCacheAdminPackage {
    $projectPath = "$clisRoot/$documentCacheAdminProjectName/$documentCacheAdminProjectName.csproj"
    $expectedPackagePath = "$PSScriptRoot/$documentCacheAdminPackageName.$DMSVersion.nupkg"

    Write-Info "Building $documentCacheAdminPackageName package"

    Invoke-Execute {
        if (Test-Path $expectedPackagePath) {
            Remove-Item -LiteralPath $expectedPackagePath -ErrorAction Stop
        }

        $restoreArgs = @()
        if ($LockedMode) { $restoreArgs += "--locked-mode" }

        dotnet restore $projectPath --verbosity:normal @restoreArgs

        dotnet pack $projectPath `
            -c $Configuration `
            --no-restore `
            --output $PSScriptRoot `
            -p:PackageVersion=$DMSVersion

        if (-not (Test-Path $expectedPackagePath)) {
            throw "Expected DocumentCacheAdmin package was not created: $expectedPackagePath"
        }
    }
}

function BuildPackage {
    switch ($PackageTarget) {
        "All" {
            BuildApiPackage
            BuildSchemaToolsPackage
            BuildCustomValidationPackage
            BuildDocumentCacheAdminPackage
        }
        "Api" {
            BuildApiPackage
        }
        "SchemaTools" {
            BuildSchemaToolsPackage
        }
        "CustomValidation" {
            BuildCustomValidationPackage
        }
        "DocumentCacheAdmin" {
            BuildDocumentCacheAdminPackage
        }
        default {
            throw "PackageTarget '$PackageTarget' is not recognized"
        }
    }
}

function Invoke-Build {
    Invoke-Step { DotNetClean }
    Invoke-Step { Restore }
    Invoke-Step { Compile }
}

function Invoke-SetAssemblyInfo {
    Write-Output "Setting Assembly Information"

    Invoke-Step { SetDMSAssemblyInfo }
}

function Invoke-Publish {
    Write-Output "Building Version ($DMSVersion)"

    Invoke-Step { PublishApi }
    Invoke-Step { PublishCliApiDownloader }
}

function Invoke-Clean {
    Invoke-Step { DotNetClean }
}

function Invoke-TestExecution {
    param (
        [ValidateSet("E2ETests", "UnitTests", "IntegrationTests",
            ErrorMessage = "Please specify a valid Test Type name from the list.",
            IgnoreCase = $true)]
        # File search filter
        [string]
        $Filter,

        [switch]
        $UsePublishedImage,

        [switch]
        $SkipDockerBuild,

        [switch]
        $LoadSeedData,

        [string]
        $IdentityProvider="self-contained",

        [string]
        $TestFilter,

        [string]
        $DatabaseEngine = "postgresql"
    )
    switch ($Filter) {
        E2ETests { Invoke-Step { E2ETests -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider -TestFilter $TestFilter -DatabaseEngine $DatabaseEngine } }
        UnitTests { Invoke-Step { UnitTests } }
        IntegrationTests { Invoke-Step { IntegrationTests } }
        Default { "Unknown Test Type" }
    }
}

function Invoke-Coverage {
    # Whole-token quoting for the same reason as the unit-test merge above, and Invoke-Execute so a
    # ReportGenerator failure fails the command. Without the exit-code check this reported success
    # while writing no report at all, which is indistinguishable from a report nobody reads.
    Invoke-Execute {
        dotnet tool run reportgenerator -- `
            "-reports:$coverageOutputFile" `
            "-targetdir:$targetDir" `
            "-reporttypes:Html"
    }
}

function Invoke-BuildPackage {
    Invoke-Step { BuildPackage }
}

function PushPackage {
    Invoke-Execute {
        if (-not $NuGetApiKey) {
            throw "Cannot push a NuGet package without providing an API key in the `NuGetApiKey` argument."
        }

        if (-not $EdFiNuGetFeed) {
            throw "Cannot push a NuGet package without providing a feed in the `EdFiNuGetFeed` argument."
        }

        if (-not $PackageFile) {
            throw "PackageFile is required for Push because DMS produces multiple packages. Pass -PackageFile '<path-to-.nupkg>'."
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

function Invoke-PushPackage {
    Invoke-Step { PushPackage }
}

$dockerTagBase = "local"
$dockerTagDMS = "$($dockerTagBase)/ed-fi-api"

function DockerBuild {
    $versionArgs = @()
    if (-not [string]::IsNullOrEmpty($DMSVersion))
    {
        # AssemblyVersion/FileVersion must be strictly numeric, so derive a numeric
        # assembly version from the (possibly prerelease) package version.
        $assemblyVersion = Convert-ToAssemblyVersion $DMSVersion
        $versionArgs += "--build-arg"
        $versionArgs += "VERSION=$DMSVersion"
        $versionArgs += "--build-arg"
        $versionArgs += "ASSEMBLY_VERSION=$assemblyVersion"
    }

    Push-Location src/dms/
    &docker buildx build -t $dockerTagDMS -f Dockerfile . --build-context parentdir=../ @versionArgs
    Pop-Location
}

function DockerRun {
    &docker run --rm -p 8080:8080 --env-file ./src/dms/.env -d $dockerTagDMS
}

function Run {
    Push-Location src/dms
    try {
        dotnet run --no-build --no-restore --project ./frontend/EdFi.DataManagementService.Frontend.AspNetCore
    }
    finally {
        Pop-Location
    }
}

function Invoke-Restore {
    Invoke-Step { Restore }
}

Invoke-Main {
    if ($IsLocalBuild) {
        $nugetExePath = Install-NugetCli
        Set-Alias nuget $nugetExePath -Scope Global -Verbose
    }
    switch ($Command) {
        Clean { Invoke-Clean }
        Restore { Invoke-Restore }
        Build { Invoke-Build }
        BuildAndPublish {
            Invoke-SetAssemblyInfo
            Invoke-Build
            Invoke-Publish
        }
        UnitTest { Invoke-TestExecution UnitTests }
        E2ETest { Invoke-TestExecution E2ETests -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider -TestFilter $TestFilter -DatabaseEngine $DatabaseEngine }
        InstanceE2ETest {
            $instanceE2EArguments = @{
                SkipDockerBuild     = [bool]$SkipDockerBuild
                TestFilter          = $TestFilter
                DataStandardVersion = $DataStandardVersion
                DatabaseEngine      = $DatabaseEngine
            }
            # Only forward -EnvironmentFile when it was explicitly supplied; otherwise InstanceE2ETests
            # defaults to the route-context env file rather than the standard suite's ./.env.e2e default.
            if ($environmentFileSupplied) {
                $instanceE2EArguments.EnvironmentFile = $EnvironmentFile
            }
            Invoke-Step { InstanceE2ETests @instanceE2EArguments }
        }
        IntegrationTest { Invoke-TestExecution IntegrationTests }
        Coverage { Invoke-Coverage }
        Package { Invoke-BuildPackage }
        Push { Invoke-PushPackage }
        DockerBuild { Invoke-Step { DockerBuild } }
        DockerRun { Invoke-Step { DockerRun } }
        Run { Invoke-Step { Run } }
        StartEnvironment { Invoke-Step { Start-BootstrapDockerEnvironment -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -DatabaseEngine $DatabaseEngine -SeparateConfigDatabase:$SeparateConfigDatabase -IdentityProvider $IdentityProvider -DataStandardVersion $DataStandardVersion -DataStandardVersionSupplied:$dataStandardVersionSupplied } }
        default { throw "Command '$Command' is not recognized" }
    }
}
