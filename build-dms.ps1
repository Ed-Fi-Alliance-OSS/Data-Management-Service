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
        * Package: builds pre-release and release NuGet packages for the Dms API application.
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
        .\build-dms.ps1 push -NuGetApiKey $env:nuget_key
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

    # Opts into the seed phase after the stack starts. For the published-image path, forwarded to
    # start-published-dms.ps1. For the local-image path, the direct-SQL database-template seed path
    # (setup-database-template.psm1 LoadSeedData) runs after stack startup: start-local-dms.ps1 is
    # infrastructure-lifecycle-only as of DMS-1153, and the API-based load-dms-seed-data.ps1 path
    # requires a staged bootstrap manifest that this flow's -RemoveBootstrap teardown removes.
    [switch]
    $LoadSeedData,

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

    # Optional Ed-Fi Data Standard version (e.g. "5.2", "6.1"). Forwarded to the start scripts, which
    # compose the matching .env.ds<NN> overlay onto -EnvironmentFile. Omit for the default (DS 5.2).
    [string]
    $DataStandardVersion
)

$solutionRoot = "$PSScriptRoot/src/dms"
$defaultSolution = "$solutionRoot/EdFi.DataManagementService.sln"
$applicationRoot = "$solutionRoot/frontend"
$clisRoot = "$solutionRoot/clis"
$projectName = "EdFi.DataManagementService.Frontend.AspNetCore"
$schemaDownloaderProjectName = "EdFi.DataManagementService.ApiSchemaDownloader"
$packageName = "EdFi.Api"
$testResults = "$PSScriptRoot/TestResults"
#Coverage
$thresholdCoverage = 58
$coverageOutputFile = "coverage.cobertura.xml"
$targetDir = "coveragereport"

$maintainers = "Ed-Fi Alliance, LLC and contributors"

Import-Module -Name "$PSScriptRoot/eng/build-helpers.psm1" -Force
Import-Module -Name "$PSScriptRoot/package-helpers.psm1" -Force

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

function ConvertTo-Boolean {
    param(
        [string]
        $Value,

        [bool]
        $DefaultValue = $false
    )

    $parsedValue = $false

    if ([bool]::TryParse($Value, [ref]$parsedValue)) {
        return $parsedValue
    }

    return $DefaultValue
}

function Get-E2ETestResultSuffix {
    param(
        [bool]
        $UseRelationalBackend,

        [string]
        $TestFilter
    )

    $normalizedTestFilter = ConvertTo-NormalizedTestFilter -TestFilter $TestFilter

    if ($UseRelationalBackend) {
        if ($normalizedTestFilter -match '(?i)\b(?:TestCategory|Category)\s*=\s*relational-ci-shard-(\d+)\b') {
            return "relational-shard-$($Matches[1])"
        }

        return "relational"
    }

    if ([string]::IsNullOrWhiteSpace($TestFilter)) {
        return $null
    }

    if ($TestFilter -match "@relational-backend" -and $TestFilter -match "!=") {
        return "legacy"
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

function Test-FilterIncludesRelationalCategory {
    param(
        [string]
        $NormalizedTestFilter
    )

    if ([string]::IsNullOrWhiteSpace($NormalizedTestFilter)) {
        return $false
    }

    return $NormalizedTestFilter -match '(?i)\b(?:TestCategory|Category)\s*=\s*relational-backend\b'
}

function Test-FilterExcludesRelationalCategory {
    param(
        [string]
        $NormalizedTestFilter
    )

    if ([string]::IsNullOrWhiteSpace($NormalizedTestFilter)) {
        return $false
    }

    return $NormalizedTestFilter -match '(?i)\b(?:TestCategory|Category)\s*!=\s*relational-backend\b'
}

function Test-FilterUsesOrOperator {
    param(
        [string]
        $NormalizedTestFilter
    )

    if ([string]::IsNullOrWhiteSpace($NormalizedTestFilter)) {
        return $false
    }

    return $NormalizedTestFilter -match '\|'
}

function Assert-E2ETestLaneMatchesFilter {
    param(
        [string]
        $EnvironmentFile,

        [bool]
        $UseRelationalBackend,

        [string]
        $TestFilter
    )

    $normalizedTestFilter = ConvertTo-NormalizedTestFilter -TestFilter $TestFilter
    $includesRelationalCategory = Test-FilterIncludesRelationalCategory -NormalizedTestFilter $normalizedTestFilter
    $excludesRelationalCategory = Test-FilterExcludesRelationalCategory -NormalizedTestFilter $normalizedTestFilter
    $usesOrOperator = Test-FilterUsesOrOperator -NormalizedTestFilter $normalizedTestFilter

    if ([string]::IsNullOrWhiteSpace($normalizedTestFilter)) {
        if ($UseRelationalBackend) {
            throw "Relational E2E environment '$EnvironmentFile' requires a test filter that selects the relational lane with 'Category=@relational-backend'."
        }

        throw "Legacy E2E environment '$EnvironmentFile' requires a test filter that excludes the relational lane with 'Category!=@relational-backend'."
    }

    if ($usesOrOperator) {
        throw "E2E lane test filter '$TestFilter' cannot use '|'. Use '&' to further narrow the lane while preserving relational and legacy isolation."
    }

    if ($UseRelationalBackend) {
        if (-not $includesRelationalCategory) {
            throw "Relational E2E environment '$EnvironmentFile' requires a test filter that selects the relational lane with 'Category=@relational-backend'."
        }

        if ($excludesRelationalCategory) {
            throw "Relational E2E environment '$EnvironmentFile' cannot be combined with a test filter that excludes '@relational-backend'."
        }

        return
    }

    if ($includesRelationalCategory) {
        throw "Legacy E2E environment '$EnvironmentFile' cannot be combined with a test filter that selects '@relational-backend'. Use './.env.e2e.relational' for relational lane runs."
    }

    if (-not $excludesRelationalCategory) {
        throw "Legacy E2E environment '$EnvironmentFile' requires a test filter that excludes the relational lane with 'Category!=@relational-backend'."
    }
}

function Get-E2ETestEnvironmentContext {
    param(
        [string]
        $EnvironmentFile,

        [string]
        $TestFilter
    )

    $environmentFilePath = Resolve-E2EEnvironmentFilePath -Path $EnvironmentFile

    Import-Module -Name "$PSScriptRoot/eng/docker-compose/env-utility.psm1" -Force

    # Compose the requested data-standard overlay (.env.ds<NN>) onto the base env file once, here, so
    # every downstream consumer - relational provisioning, seed loading, configure, and DMS startup -
    # reads the same data-standard values. Without this single composition point, startup composed the
    # overlay (via start-local-dms.ps1 -DataStandardVersion) while provisioning/seed/configure read the
    # raw base file, mixing e.g. DS 6.1 runtime schema with a DS 5.2 template/seed. With no
    # -DataStandardVersion this returns the base file unchanged (DS 5.2 default).
    $environmentFilePath = Resolve-DataStandardEnvironmentFile `
        -DataStandardVersion $DataStandardVersion `
        -BaseEnvironmentFile $environmentFilePath `
        -DockerComposeRoot "$PSScriptRoot/eng/docker-compose"

    $environmentValues = ReadValuesFromEnvFile $environmentFilePath
    $useRelationalBackend = ConvertTo-Boolean -Value ([string]$environmentValues["USE_RELATIONAL_BACKEND"])

    Assert-E2ETestLaneMatchesFilter `
        -EnvironmentFile $environmentFilePath `
        -UseRelationalBackend:$useRelationalBackend `
        -TestFilter $TestFilter

    $dataStoreDatabaseName =
        if ($useRelationalBackend) {
            $relationalDatabaseName = [string]$environmentValues["E2E_DATABASE_NAME"]

            if ([string]::IsNullOrWhiteSpace($relationalDatabaseName)) {
                throw "Relational E2E environment '$environmentFilePath' requires E2E_DATABASE_NAME to be set."
            }

            $relationalDatabaseName
        }
        else {
            "edfi_datamanagementservice"
        }

    return [pscustomobject]@{
        EnvironmentFile = $environmentFilePath
        UseRelationalBackend = $useRelationalBackend
        DataStoreDatabaseName = $dataStoreDatabaseName
        TestResultSuffix = Get-E2ETestResultSuffix -UseRelationalBackend:$useRelationalBackend -TestFilter $TestFilter
    }
}

function Invoke-WithE2ETestProcessContext {
    param(
        [pscustomobject]
        $E2ETestSettings,

        [scriptblock]
        $Action
    )

    $previousUseRelationalBackend = $env:AppSettings__UseRelationalBackend
    $previousDataStoreDatabaseName = $env:AppSettings__DataStoreDatabaseName
    $previousNodeOptions = $env:NODE_OPTIONS

    try {
        if ($E2ETestSettings.UseRelationalBackend) {
            $env:AppSettings__UseRelationalBackend = $E2ETestSettings.UseRelationalBackend.ToString().ToLowerInvariant()
            $env:AppSettings__DataStoreDatabaseName = $E2ETestSettings.DataStoreDatabaseName
        }
        else {
            Remove-Item Env:AppSettings__UseRelationalBackend -ErrorAction SilentlyContinue
            Remove-Item Env:AppSettings__DataStoreDatabaseName -ErrorAction SilentlyContinue
        }

        Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
        & $Action
    }
    finally {
        if ([string]::IsNullOrWhiteSpace($previousUseRelationalBackend)) {
            Remove-Item Env:AppSettings__UseRelationalBackend -ErrorAction SilentlyContinue
        }
        else {
            $env:AppSettings__UseRelationalBackend = $previousUseRelationalBackend
        }

        if ([string]::IsNullOrWhiteSpace($previousDataStoreDatabaseName)) {
            Remove-Item Env:AppSettings__DataStoreDatabaseName -ErrorAction SilentlyContinue
        }
        else {
            $env:AppSettings__DataStoreDatabaseName = $previousDataStoreDatabaseName
        }

        if ([string]::IsNullOrWhiteSpace($previousNodeOptions)) {
            Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
        }
        else {
            $env:NODE_OPTIONS = $previousNodeOptions
        }
    }
}

function Invoke-WithEnvironmentFileSchemaSettings {
    param(
        [switch]
        $Enabled,

        [scriptblock]
        $Action
    )

    if (-not $Enabled) {
        & $Action
        return
    }

    $schemaEnvironmentVariableNames = @(
        "USE_API_SCHEMA_PATH",
        "API_SCHEMA_PATH",
        "SCHEMA_PACKAGES"
    )
    $previousValues = @{}

    foreach ($name in $schemaEnvironmentVariableNames) {
        $previousValues[$name] = [System.Environment]::GetEnvironmentVariable($name)
    }

    try {
        foreach ($name in $schemaEnvironmentVariableNames) {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }

        & $Action
    }
    finally {
        foreach ($name in $schemaEnvironmentVariableNames) {
            if ($null -eq $previousValues[$name]) {
                Remove-Item "Env:$name" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($name, $previousValues[$name])
            }
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

    $testAssemblyPath = "$solutionRoot/*/$Filter/bin/$Configuration/"
    $testAssemblies = Get-ChildItem -Path $testAssemblyPath -Filter "$Filter.dll" -Recurse |
    Sort-Object -Property { $_.Name.Length }
    $normalizedTestFilter = ConvertTo-NormalizedTestFilter -TestFilter $TestFilter

    if ($testAssemblies.Length -eq 0) {
        Write-Output "no test assemblies found in $testAssemblyPath"
    }

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

        if ($Filter.Equals("*.Tests.Unit")) {
            # For unit tests, we need to collect coverage but not check thresholds yet
            $isLastTest = $_ -eq $testAssemblies[-1]

            if ($isLastTest) {
                # Last test: generate final reports and check thresholds
                Invoke-Execute {
                    dotnet tool run coverlet -- $($_) `
                        --target dotnet --targetargs "test $target --logger:console --logger:trx --nologo --blame"`
                        --exclude "[EdFi.DataManagementService.Tests.E2E]*" `
                        --threshold $thresholdCoverage `
                        --threshold-type line `
                        --threshold-type branch `
                        --threshold-stat total `
                        --format json `
                        --format cobertura `
                        --merge-with "coverage.json"
                }
            }
            else {
                # Not the last test: just collect coverage without threshold check
                Invoke-Execute {
                    dotnet tool run coverlet -- $($_) `
                        --target dotnet --targetargs "test $target --logger:console --logger:trx --nologo --blame"`
                        --exclude "[EdFi.DataManagementService.Tests.E2E]*" `
                        --format json `
                        --merge-with "coverage.json"
                }
            }
        }
        else {
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

function Get-EffectiveSchemaHashFromOutput {
    param(
        [object[]]
        $Output
    )

    $effectiveSchemaHash = $null

    foreach ($line in $Output) {
        $lineText = [string]$line

        if ($lineText -match '(?i)Effective schema hash:\s*([a-f0-9]{64})') {
            $effectiveSchemaHash = $Matches[1].ToLowerInvariant()
        }
    }

    return $effectiveSchemaHash
}

function Invoke-RelationalE2EDatabaseProvisioning {
    param(
        [pscustomobject]
        $E2ETestSettings
    )

    try {
        Push-Location "$PSScriptRoot/eng/docker-compose"
        $provisionOutput = @()
        ./provision-e2e-database.ps1 `
            -EnvironmentFile $E2ETestSettings.EnvironmentFile `
            -Configuration $Configuration 6>&1 |
            Tee-Object -Variable provisionOutput |
            ForEach-Object { Write-Host ([string]$_) }

        $provisionedEffectiveSchemaHash = Get-EffectiveSchemaHashFromOutput -Output $provisionOutput

        if ([string]::IsNullOrWhiteSpace($provisionedEffectiveSchemaHash)) {
            throw "Relational E2E provisioning completed without reporting an effective schema hash."
        }

        return $provisionedEffectiveSchemaHash
    }
    finally {
        Pop-Location
    }
}

function Get-DockerContainerEnvironmentMap {
    param(
        [string]
        $ContainerName
    )

    $environmentJson = docker inspect $ContainerName --format '{{json .Config.Env}}'

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect Docker container '$ContainerName'."
    }

    $environmentEntries = @($environmentJson | ConvertFrom-Json)
    $environmentValues = @{}

    foreach ($entry in $environmentEntries) {
        $entryText = [string]$entry
        $separatorIndex = $entryText.IndexOf("=")

        if ($separatorIndex -lt 0) {
            continue
        }

        $key = $entryText.Substring(0, $separatorIndex)
        $value = $entryText.Substring($separatorIndex + 1)
        $environmentValues[$key] = $value
    }

    return $environmentValues
}

function Write-DmsSchemaContainerEnvironment {
    param(
        [hashtable]
        $EnvironmentValues
    )

    Write-Output "DMS container schema environment:"
    foreach ($key in @(
            "AppSettings__UseRelationalBackend",
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

    Write-Output "Validating DMS runtime effective schema before relational E2E tests..."

    $environmentValues = Get-DockerContainerEnvironmentMap -ContainerName $ContainerName
    Write-DmsSchemaContainerEnvironment -EnvironmentValues $environmentValues

    $dmsRuntimeEffectiveSchemaHash = Get-DmsRuntimeEffectiveSchemaHash `
        -ContainerName $ContainerName `
        -LogsSinceUtc $LogsSinceUtc

    Write-Output "Provisioned relational effective schema hash: $ProvisionedEffectiveSchemaHash"
    Write-Output "DMS runtime effective schema hash: $dmsRuntimeEffectiveSchemaHash"

    if ([string]::IsNullOrWhiteSpace($dmsRuntimeEffectiveSchemaHash)) {
        docker logs --tail 120 $ContainerName 2>&1
        throw "DMS container '$ContainerName' did not report an effective schema hash before relational E2E tests."
    }

    if ($dmsRuntimeEffectiveSchemaHash -ne $ProvisionedEffectiveSchemaHash) {
        docker logs --tail 120 $ContainerName 2>&1
        throw "Relational E2E setup mismatch: database was provisioned with effective schema hash '$ProvisionedEffectiveSchemaHash' but DMS runtime expects '$dmsRuntimeEffectiveSchemaHash'."
    }
}

function Start-DockerEnvironment {
    param (
        [switch]
        $UsePublishedImage,

        [switch]
        $SkipDockerBuild,

        [switch]
        $LoadSeedData,

        [string]
        $IdentityProvider="self-contained",

        [string]
        $ResolvedEnvironmentFile,

        [switch]
        $UseEnvironmentFileSchemaSettings
    )

    $environmentFilePath =
        if ([string]::IsNullOrWhiteSpace($ResolvedEnvironmentFile)) {
            # Standalone entry points (e.g. StartEnvironment) bypass Get-E2ETestEnvironmentContext, so
            # compose the data-standard overlay here too; otherwise the seed/configure steps below would
            # read the raw base env file while DMS started on the selected data standard version.
            Import-Module -Name "$PSScriptRoot/eng/docker-compose/env-utility.psm1" -Force
            $baseEnvironmentFilePath = Resolve-E2EEnvironmentFilePath -Path $EnvironmentFile
            Resolve-DataStandardEnvironmentFile `
                -DataStandardVersion $DataStandardVersion `
                -BaseEnvironmentFile $baseEnvironmentFilePath `
                -DockerComposeRoot "$PSScriptRoot/eng/docker-compose"
        }
        else {
            # Already composed by Get-E2ETestEnvironmentContext; use as-is (no double-composition).
            $ResolvedEnvironmentFile
        }

    if (-not $SkipDockerBuild -and -not $UsePublishedImage) {
        Invoke-Step { DockerBuild }
    }

    # Clean up all the containers and volumes
    Invoke-Execute {
        try {
            Push-Location "$PSScriptRoot/eng/docker-compose"
            Invoke-WithEnvironmentFileSchemaSettings -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                ./start-local-dms.ps1 -EnvironmentFile $environmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -d -v -RemoveBootstrap
            }
            Invoke-WithEnvironmentFileSchemaSettings -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                ./start-published-dms.ps1 -EnvironmentFile $environmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -d -v -RemoveBootstrap
            }
        }
        finally {
            Pop-Location
        }
    }

    Invoke-Execute {
        try {
            Push-Location "$PSScriptRoot/eng/docker-compose"
            if ($UsePublishedImage) {
                if ($LoadSeedData) {
                    Invoke-WithEnvironmentFileSchemaSettings -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                        ./start-published-dms.ps1 -EnvironmentFile $environmentFilePath -EnableConfig -LoadSeedData -IdentityProvider $IdentityProvider -AddExtensionSecurityMetadata
                    }
                }
                else {
                    Invoke-WithEnvironmentFileSchemaSettings -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                        ./start-published-dms.ps1 -EnvironmentFile $environmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -AddExtensionSecurityMetadata
                    }
                }
            }
            else {
                # Local-image path: start-local-dms.ps1 is infrastructure-lifecycle-only as of
                # DMS-1153 and no longer accepts -LoadSeedData.
                #
                # This flow is intentionally outside the bootstrap-manifest contract: the
                # -RemoveBootstrap teardown above guarantees no manifest is staged, so the
                # claims-ready gate is skipped. The DMS container restarts until the configure
                # step below lands the data store (restart: unless-stopped).
                if ($LoadSeedData) {
                    # The database template owns dms schema creation for this legacy seed path.
                    Invoke-WithEnvironmentFileSchemaSettings -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                        ./start-local-dms.ps1 -EnvironmentFile $environmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -AddExtensionSecurityMetadata
                    }

                    # Direct-SQL database-template seed path, relocated verbatim from the seed
                    # block de-scoped out of start-local-dms.ps1; its removal is gated on the
                    # bootstrap-design.md Section 6.4 verification gate closing. The
                    # API-based load-dms-seed-data.ps1 path is not usable here: it requires a
                    # staged bootstrap manifest, and the -RemoveBootstrap teardown above
                    # guarantees none is present.
                    Import-Module ./setup-database-template.psm1 -Force
                    Write-Output "Loading initial data from the database template..."
                    LoadSeedData -EnvironmentFile $environmentFilePath

                    if ($LASTEXITCODE -ne 0) {
                        throw "Failed to load initial data, with exit code $LASTEXITCODE."
                    }
                }
                else {
                    Invoke-WithEnvironmentFileSchemaSettings -Enabled:$UseEnvironmentFileSchemaSettings -Action {
                        ./start-local-dms.ps1 -EnvironmentFile $environmentFilePath -EnableConfig -IdentityProvider $IdentityProvider -AddExtensionSecurityMetadata
                    }
                }

                # start-local-dms.ps1 no longer creates a default data store (DMS-1153 de-scope);
                # create it explicitly so DMS startup finds an instance in CMS.
                ./configure-local-data-store.ps1 -EnvironmentFile $environmentFilePath
            }
        }
        finally {
            Pop-Location
        }
    }
}

function Initialize-RelationalE2EDatabase {
    param(
        [pscustomobject]
        $E2ETestSettings,

        [switch]
        $UsePublishedImage
    )

    if (-not $E2ETestSettings.UseRelationalBackend) {
        return
    }

    $dmsContainerName =
        if ($UsePublishedImage) {
            "dms-published-dms-1"
        }
        else {
            "ed-fi-api"
        }

    $provisionedEffectiveSchemaHash = Invoke-RelationalE2EDatabaseProvisioning -E2ETestSettings $E2ETestSettings
    $dmsRestartStartedAtUtc = [DateTime]::UtcNow.AddSeconds(-2)
    Restart-DmsContainer `
        -ContainerName $dmsContainerName `
        -Reason "discard cached PostgreSQL pools after relational reprovisioning"
    Assert-DmsRuntimeSchemaMatchesProvisionedDatabase `
        -ProvisionedEffectiveSchemaHash $provisionedEffectiveSchemaHash `
        -ContainerName $dmsContainerName `
        -LogsSinceUtc $dmsRestartStartedAtUtc
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
        $TestFilter
    )

    $e2eTestSettings = Get-E2ETestEnvironmentContext -EnvironmentFile $EnvironmentFile -TestFilter $TestFilter

    Invoke-Step {
        Start-DockerEnvironment `
            -UsePublishedImage:$UsePublishedImage `
            -SkipDockerBuild:$SkipDockerBuild `
            -LoadSeedData:$LoadSeedData `
            -IdentityProvider $IdentityProvider `
            -ResolvedEnvironmentFile $e2eTestSettings.EnvironmentFile `
            -UseEnvironmentFileSchemaSettings:$e2eTestSettings.UseRelationalBackend
    }

    if ($e2eTestSettings.UseRelationalBackend) {
        Invoke-Step { Initialize-RelationalE2EDatabase -E2ETestSettings $e2eTestSettings -UsePublishedImage:$UsePublishedImage }
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
        Write-Host "DMS did not become ready, but continuing anyway..." -ForegroundColor Yellow
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
            $result = docker exec dms-postgresql pg_isready -U postgres 2>&1
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

# Setup-InstanceManagementDatabases function removed
# This is now handled by start-local-dms.ps1 -AddDataStore
# which creates databases, runs migrations, and sets up Kafka connectors

# Setup-InstanceKafkaConnectors function removed
# This is now handled by start-local-dms.ps1 -AddDataStore
# which creates Kafka connectors via setup-data-store-kafka-connectors.ps1

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
        $DataStandardVersion
    )

    # Instance management tests require the DMS environment to be started with route qualifiers
    Write-Host "Setting up instance management E2E tests..." -ForegroundColor Cyan

    # Start the Docker environment with route qualifiers using the instance management setup script
    $instanceSetupScript = "$solutionRoot/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"

    if (Test-Path $instanceSetupScript) {
        Write-Host "Starting Docker environment with route qualifiers..." -ForegroundColor Cyan
        Invoke-Execute {
            if ($SkipDockerBuild) {
                & $instanceSetupScript -SkipDockerBuild -DataStandardVersion $DataStandardVersion
            }
            else {
                & $instanceSetupScript -DataStandardVersion $DataStandardVersion
            }
        }
    }
    else {
        throw "Instance Management setup script not found at: $instanceSetupScript"
    }

    # Wait for config service to have all clients registered
    Invoke-Step { Wait-ForConfigServiceAndClientRegistration }

    # Restart DMS so it can authenticate with the registered clients
    Invoke-Step { Restart-DmsContainer }

    Write-Host "`nInstance E2E setup complete!" -ForegroundColor Green
    Write-Host "Infrastructure was created by setup-local-dms.ps1:" -ForegroundColor Cyan
    Write-Host "  - 3 PostgreSQL databases with DMS schema" -ForegroundColor Gray

    # Run the instance management E2E tests
    Invoke-Step { RunInstanceE2E -TestFilter $TestFilter }

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
    dotnet pack $ProjectPath `
        --no-build `
        --no-restore `
        --output $PSScriptRoot `
        -p:NuspecFile=$nuspecPath `
        -p:NuspecProperties="version=$PackageVersion;year=$copyrightYear" `
        /p:NoWarn=NU5100
}

function BuildPackage {
    $mainPath = "$applicationRoot/$projectName"
    $projectPath = "$mainPath/$projectName.csproj"
    $nugetSpecPath = "$mainPath/publish/$projectName.nuspec"

    RunNuGetPack -ProjectPath $projectPath -PackageVersion $DMSVersion $nugetSpecPath
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
        $TestFilter
    )
    switch ($Filter) {
        E2ETests { Invoke-Step { E2ETests -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider -TestFilter $TestFilter } }
        UnitTests { Invoke-Step { UnitTests } }
        IntegrationTests { Invoke-Step { IntegrationTests } }
        Default { "Unknown Test Type" }
    }
}

function Invoke-Coverage {
    dotnet tool run reportgenerator -- -reports:"$coverageOutputFile" -targetdir:"$targetDir" -reporttypes:Html
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
            $PackageFile = "$PSScriptRoot/$packageName.$DMSVersion.nupkg"
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
        E2ETest { Invoke-TestExecution E2ETests -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider -TestFilter $TestFilter }
        InstanceE2ETest { Invoke-Step { InstanceE2ETests -SkipDockerBuild:$SkipDockerBuild -TestFilter $TestFilter -DataStandardVersion $DataStandardVersion } }
        IntegrationTest { Invoke-TestExecution IntegrationTests }
        Coverage { Invoke-Coverage }
        Package { Invoke-BuildPackage }
        Push { Invoke-PushPackage }
        DockerBuild { Invoke-Step { DockerBuild } }
        DockerRun { Invoke-Step { DockerRun } }
        Run { Invoke-Step { Run } }
        StartEnvironment { Invoke-Step { Start-DockerEnvironment -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider } }
        default { throw "Command '$Command' is not recognized" }
    }
}
