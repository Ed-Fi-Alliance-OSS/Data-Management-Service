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
        * E2ETest: executes NUnit tests in projects named `*.E2ETests`, which
          runs the API in an isolated Docker environment and executes API Calls .
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
        .\build-dms.ps1 push -NuGetApiKey $env:nuget_key
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive')]
param(
    # Command to execute, defaults to "Build".
    [string]
    [ValidateSet("Clean", "Restore", "Build", "BuildAndPublish", "UnitTest", "E2ETest", "IntegrationTest", "Coverage", "Package", "Push", "DockerBuild", "DockerRun", "Run", "StartEnvironment")]
    $Command = "Build",

    # Assembly and package version number for the Data Management Service. The
    # current package number is configured in the build automation tool and
    # passed to this script.
    [string]
    $DMSVersion = "0.1",

    # .NET project build configuration, defaults to "Debug". Options are: Debug, Release.
    [string]
    [ValidateSet("Debug", "Release")]
    $Configuration = "Debug",

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
    $EnableOpenSearch,

    # Only required with E2E testing.
    [switch]
    $EnableElasticSearch,

    # Only required with E2E testing.
    [switch]
    $UsePublishedImage,

    # Only required with E2E testing.
    [switch]
    $SkipDockerBuild,

    # Load seed data when starting DMS environment.
    [switch]
    $LoadSeedData,

    # Identity provider type
    [string]
    [ValidateSet("keycloak", "self-contained")]
    $IdentityProvider="self-contained",

    # Environment file for docker-compose operations
    [string]
    $EnvironmentFile="./.env.e2e"
)

$solutionRoot = "$PSScriptRoot/src/dms"
$defaultSolution = "$solutionRoot/EdFi.DataManagementService.sln"
$applicationRoot = "$solutionRoot/frontend"
$backendRoot = "$solutionRoot/backend"
$clisRoot = "$solutionRoot/clis"
$projectName = "EdFi.DataManagementService.Frontend.AspNetCore"
$installerProjectName = "EdFi.DataManagementService.Backend.Installer"
$schemaDownloaderProjectName = "EdFi.DataManagementService.ApiSchemaDownloader"
$packageName = "EdFi.DataManagementService"
$testResults = "$PSScriptRoot/TestResults"
#Coverage
$thresholdCoverage = 58
$coverageOutputFile = "coverage.cobertura.xml"
$targetDir = "coveragereport"

$maintainers = "Ed-Fi Alliance, LLC and contributors"

Import-Module -Name "$PSScriptRoot/eng/build-helpers.psm1" -Force

function DotNetClean {
    Invoke-Execute { dotnet clean $defaultSolution -c $Configuration --nologo -v minimal }
}

function Restore {
    Invoke-Execute { dotnet restore $defaultSolution --verbosity:normal }
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
        <Product>Ed-Fi Data Management Service</Product>
        <Authors>$maintainers</Authors>
        <Company>$maintainers</Company>
        <Copyright>Copyright © ${(Get-Date).year)} Ed-Fi Alliance</Copyright>
        <VersionPrefix>$assembly_version</VersionPrefix>
        <VersionSuffix></VersionSuffix>
    </PropertyGroup>
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
        dotnet publish $project -c $Configuration -o $outputPath --nologo
    }
}

function PublishBackendInstaller {
    Invoke-Execute {
        $installerProject = "$backendRoot/$installerProjectName/"
        $outputPath = "$installerProject/publish"
        dotnet publish $installerProject -c $Configuration -o $outputPath --nologo
    }
}

function PublishCliApiDownloader {
    Invoke-Execute {
        $schemaDownloaderProject = "$clisRoot/$schemaDownloaderProjectName/"
        $outputPath = "$schemaDownloaderProject/publish"
        dotnet publish $schemaDownloaderProject -c $Configuration -o $outputPath --nologo
    }
}

function SetQueryHandler {
    param (
        # E2E test directory
        [string]
        $E2EDirectory
    )

    $appSettingsPath = Join-Path -Path $E2EDirectory -ChildPath "appsettings.json"
    $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    if ($EnableOpenSearch -or $EnableElasticSearch) {
        $json.QueryHandler = "opensearch"
    }
    else {
        $json.QueryHandler = "postgresql"
    }
    $json | ConvertTo-Json -Depth 32 | Set-Content $appSettingsPath
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
        $json.AuthenticationService ="http://dms-config-service:8081/connect/token"
    }
    else {
        $json.AuthenticationService = "http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token"
    }
    $json | ConvertTo-Json -Depth 32 | Set-Content $appSettingsPath
}

function RunTests {
    param (
        # File search filter
        [string]
        $Filter
    )

    $testAssemblyPath = "$solutionRoot/*/$Filter/bin/$Configuration/"
    $testAssemblies = Get-ChildItem -Path $testAssemblyPath -Filter "$Filter.dll" -Recurse |
    Sort-Object -Property { $_.Name.Length }

    if ($testAssemblies.Length -eq 0) {
        Write-Output "no test assemblies found in $testAssemblyPath"
    }

    Write-Output "Tests Assemblies List"
    Write-Output $testAssemblies
    Write-Output "End Tests Assemblies List"

    $testAssemblies | ForEach-Object {
        Write-Output "Executing: dotnet test $($_)"

        $target = $_.FullName

        if ($Filter.Equals("*.Tests.Unit")) {
            # For unit tests, we need to collect coverage but not check thresholds yet
            $isLastTest = $_ -eq $testAssemblies[-1]

            if ($isLastTest) {
                # Last test: generate final reports and check thresholds
                Invoke-Execute {
                    coverlet $($_) `
                        --target dotnet --targetargs "test $target --logger:console --logger:trx --nologo --blame"`
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
                    coverlet $($_) `
                        --target dotnet --targetargs "test $target --logger:console --logger:trx --nologo --blame"`
                        --format json `
                        --merge-with "coverage.json"
                }
            }
        }
        else {
            $fileNameNoExt = $_.Name.subString(0, $_.Name.length - 4)
            $trx = "$testResults/$fileNameNoExt"

            # Set Query Handler for E2E tests
            if ($Filter -like "*E2E*") {
                $dirPath = Split-Path -parent $($_)
                SetQueryHandler($dirPath)
                SetAuthenticationServiceURL($dirPath)
            }

            Invoke-Execute {
                dotnet test $target `
                    --no-build `
                    --no-restore `
                     -v normal `
                    --logger "trx;LogFileName=$trx.trx" `
                    --logger "console" `
                    --nologo
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
    Invoke-Execute { RunTests -Filter "*.Tests.E2E" }
}

function Start-DockerEnvironment {
    param (
        [switch]
        $EnableOpenSearch,

        [switch]
        $EnableElasticSearch,

        [switch]
        $UsePublishedImage,

        [switch]
        $SkipDockerBuild,

        [switch]
        $LoadSeedData,

        [string]
        $IdentityProvider="self-contained"
    )

    if (-not $SkipDockerBuild -and -not $UsePublishedImage) {
        Invoke-Step { DockerBuild }
    }

    # Clean up all the containers and volumes
    Invoke-Execute {
        try {
            Push-Location eng/docker-compose/
            ./start-local-dms.ps1 -EnvironmentFile $EnvironmentFile -SearchEngine "OpenSearch" -EnableConfig -d -v
            ./start-local-dms.ps1 -EnvironmentFile $EnvironmentFile -SearchEngine "ElasticSearch" -EnableConfig -d -v
            ./start-published-dms.ps1 -EnvironmentFile $EnvironmentFile -SearchEngine "OpenSearch" -EnableConfig -d -v
            ./start-published-dms.ps1 -EnvironmentFile $EnvironmentFile -SearchEngine "ElasticSearch" -EnableConfig -d -v
        }
        finally {
            Pop-Location
        }
    }

    if ($EnableOpenSearch -or $EnableElasticSearch) {

        $searchEngine = "OpenSearch"
        if ($EnableElasticSearch) {
            $searchEngine = "ElasticSearch"
        }

        Invoke-Execute {
            try {
                Push-Location eng/docker-compose/
                if ($UsePublishedImage) {
                    if ($LoadSeedData) {
                        ./start-published-dms.ps1 -EnvironmentFile $EnvironmentFile -SearchEngine $searchEngine -EnableConfig -AddExtensionSecurityMetadata -LoadSeedData -IdentityProvider $IdentityProvider
                    }
                    else {
                        ./start-published-dms.ps1 -EnvironmentFile $EnvironmentFile -SearchEngine $searchEngine -EnableConfig -AddExtensionSecurityMetadata -IdentityProvider $IdentityProvider
                    }
                }
                else {
                    if ($LoadSeedData) {
                        ./start-local-dms.ps1 -EnvironmentFile $EnvironmentFile -SearchEngine $searchEngine -EnableConfig -AddExtensionSecurityMetadata -LoadSeedData -IdentityProvider $IdentityProvider
                    }
                    else {
                        ./start-local-dms.ps1 -EnvironmentFile $EnvironmentFile -SearchEngine $searchEngine -EnableConfig -AddExtensionSecurityMetadata -IdentityProvider $IdentityProvider
                    }
                }
            }
            finally {
                Pop-Location
            }
        }
    }
    else {
        Invoke-Step { DockerRun }
    }
}

function E2ETests {
    Invoke-Step { Start-DockerEnvironment -EnableOpenSearch:$EnableOpenSearch -EnableElasticSearch:$EnableElasticSearch -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider}
    Invoke-Step { RunE2E }
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
    Invoke-Step { PublishBackendInstaller }
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
        $EnableOpenSearch,

        [switch]
        $EnableElasticSearch,

        [switch]
        $UsePublishedImage,

        [switch]
        $SkipDockerBuild,

        [switch]
        $LoadSeedData
    )
    switch ($Filter) {
        E2ETests { Invoke-Step { E2ETests -EnableOpenSearch:$EnableOpenSearch -EnableElasticSearch:$EnableElasticSearch -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider} }
        UnitTests { Invoke-Step { UnitTests } }
        IntegrationTests { Invoke-Step { IntegrationTests } }
        Default { "Unknown Test Type" }
    }
}

function Invoke-Coverage {
    reportgenerator -reports:"$coverageOutputFile" -targetdir:"$targetDir" -reporttypes:Html
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
$dockerTagDMS = "$($dockerTagBase)/data-management-service"

function DockerBuild {
    Push-Location src/dms/
    &docker buildx build -t $dockerTagDMS -f Dockerfile . --build-context parentdir=../
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
        E2ETest { Invoke-TestExecution E2ETests -EnableOpenSearch:$EnableOpenSearch -EnableElasticSearch:$EnableElasticSearch -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider}
        IntegrationTest { Invoke-TestExecution IntegrationTests }
        Coverage { Invoke-Coverage }
        Package { Invoke-BuildPackage }
        Push { Invoke-PushPackage }
        DockerBuild { Invoke-Step { DockerBuild } }
        DockerRun { Invoke-Step { DockerRun } }
        Run { Invoke-Step { Run } }
        StartEnvironment { Invoke-Step { Start-DockerEnvironment -EnableOpenSearch:$EnableOpenSearch -EnableElasticSearch:$EnableElasticSearch -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider} }
        default { throw "Command '$Command' is not recognized" }
    }
}
