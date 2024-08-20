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
        * SetUpKafka: Setup Kafka and OpenSearch
    .EXAMPLE
        .\build.ps1 build -Configuration Release -Version "2.0" -BuildCounter 45

        Overrides the default build configuration (Debug) to build in release
        mode with assembly version 2.0.45.

    .EXAMPLE
        .\build.ps1 unittest

        Output: test results displayed in the console and saved to XML files.

    .EXAMPLE
        .\build.ps1 push -NuGetApiKey $env:nuget_key
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive')]
param(
    # Command to execute, defaults to "Build".
    [string]
    [ValidateSet("Clean", "Build", "BuildAndPublish", "UnitTest", "E2ETest", "IntegrationTest", "Coverage", "Package", "Push", "DockerBuild", "DockerRun", "Run", "SetUpKafka")]
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
    $IsLocalBuild
)

$solutionRoot = "$PSScriptRoot/src"
$defaultSolution = "$solutionRoot/EdFi.DataManagementService.sln"
$applicationRoot = "$solutionRoot/frontend"
$backendRoot = "$solutionRoot/backend"
$projectName = "EdFi.DataManagementService.Frontend.AspNetCore"
$installerProjectName = "EdFi.DataManagementService.Backend.Installer"
$packageName = "EdFi.DataManagementService"
$testResults = "$PSScriptRoot/TestResults"
#Coverage
$thresholdCoverage = 58
$coverageOutputFile = "coverage.cobertura.xml"
$targetDir = "coveragereport"

$kafkaOpenSearchRoot = "./eng/deployment/postgresql-kafka-opensearch"

$maintainers = "Ed-Fi Alliance, LLC and contributors"

Import-Module -Name "$PSScriptRoot/eng/build-helpers.psm1" -Force

function DotNetClean {
    Invoke-Execute { dotnet clean $defaultSolution -c $Configuration --nologo -v minimal }
}

function Restore {
    Invoke-Execute { dotnet restore $defaultSolution }
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

function RunTests {
    param (
        # File search filter
        [string]
        $Filter
    )

    $testAssemblyPath = "$solutionRoot/*/$Filter/bin/$Configuration/"
    $testAssemblies = Get-ChildItem -Path $testAssemblyPath -Filter "$Filter.dll" -Recurse

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
            Invoke-Execute {
                #Execution with coverage
                # Threshold need to be defined
                coverlet $($_) `
                    --target dotnet --targetargs "test $target --logger:console --logger:trx --nologo"`
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
            $fileNameNoExt = $_.Name.subString(0, $_.Name.length - 4)
            $trx = "$testResults/$fileNameNoExt"

            Invoke-Execute {
                dotnet test $target `
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
    Invoke-Execute { RunTests -Filter "*.Test.Integration" }
}

function RunE2E {
    Invoke-Execute { RunTests -Filter "*.Tests.E2E" }
}

function E2ETests {
    Invoke-Step { DockerBuild }
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
    dotnet pack $ProjectPath --no-build --no-restore --output $PSScriptRoot -p:NuspecFile=$nuspecPath -p:NuspecProperties="version=$PackageVersion;year=$copyrightYear" /p:NoWarn=NU5100
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
        $Filter
    )
    switch ($Filter) {
        E2ETests { Invoke-Step { E2ETests } }
        UnitTests { Invoke-Step { UnitTests } }
        IntegrationTests { Invoke-Step { IntegrationTests }}
        Default { "Unknow Test Type" }
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
$dockerTagDMS = "$($dockerTagBase)/edfi-data-management-service"

function DockerBuild {
    Push-Location src/
    &docker build -t $dockerTagDMS -f Dockerfile .
    Pop-Location
}

function DockerRun {
    &docker run --rm -p 8080:8080 --env-file ./src/.env -d $dockerTagDMS
}

function Run {
    Push-Location src
    try {
        dotnet run --no-build --no-restore --project ./frontend/EdFi.DataManagementService.Frontend.AspNetCore
    }
    finally {
        Pop-Location
    }
}

function IsReady([string] $Url)
{
    $maxAttempts = 3
    $attempt = 0
    $waitTime = 5
    while ($attempt -lt $maxAttempts) {
        try {
            Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 5
            return $true;
        }
        catch {
            Start-Sleep -Seconds $waitTime
            $attempt++
        }
    }
    return $false;
}

function SetUpKafkaConfiguration
{
    $sourcePort="8083"
    $sinkPort="8084"

    $sourceBase = "http://localhost:$sourcePort/connectors"
    $sinkBase = "http://localhost:$sinkPort/connectors"
    $sourceUrl = "$sourceBase/postgresql-source"
    $sinkUrl = "$sinkBase/opensearch-sink"

    # Source connector
    if(IsReady($sourceBase))
    {
        try {
            $sourceResponse = Invoke-RestMethod -Uri $sourceUrl -Method Get
            if($sourceResponse)
            {
                Write-Output "Deleting existing source connector configuration."
                Invoke-RestMethod -Method Delete -uri $sourceUrl
            }
        }
        catch {}

        try {
            $sourceBody = Get-Content -Path "$kafkaOpenSearchRoot\postgresql_connector.json" -Raw
            Invoke-RestMethod -Method Post -uri $sourceBase -ContentType "application/json" -Body $sourceBody
        }
        catch {
            Write-Output $_.Exception.Message
        }
        Start-Sleep 2
        Invoke-RestMethod -Method Get -uri $sourceUrl
    }
    else {
        Write-Output "Service at $sourceBase not available."
    }

    # Sink connector
    if(IsReady($sinkBase))
    {
        try {
            $sinkResponse = Invoke-RestMethod -Uri $sinkUrl -Method Get
            if($sinkResponse)
            {
                Write-Output "Deleting existing sink connector configuration."
                Invoke-RestMethod -Method Delete -uri $sinkUrl
            }
        }
        catch {}

        try {
            $sinkBody = Get-Content -Path "$kafkaOpenSearchRoot\opensearch_connector.json" -Raw
            Invoke-RestMethod -Method Post -uri $sinkBase -ContentType "application/json" -Body $sinkBody
        }
        catch {
            Write-Output $_.Exception.Message
        }
        Start-Sleep 2
        Invoke-RestMethod -Method Get -uri $sinkUrl
    }
    else {
        Write-Output "Service at $sinkBase not available."
    }
}

function SetUpKafka
{
    &docker compose -f "$kafkaOpenSearchRoot\docker-compose.yml" up -d
    SetUpKafkaConfiguration
}

function Invoke-SetupKafka {
    Invoke-Step { SetUpKafka }
}

Invoke-Main {
    if ($IsLocalBuild) {
        $nugetExePath = Install-NugetCli
        Set-Alias nuget $nugetExePath -Scope Global -Verbose
    }
    switch ($Command) {
        Clean { Invoke-Clean }
        Build { Invoke-Build }
        BuildAndPublish {
            Invoke-SetAssemblyInfo
            Invoke-Build
            Invoke-Publish
        }
        UnitTest { Invoke-TestExecution UnitTests }
        E2ETest { Invoke-TestExecution E2ETests }
        SetUpKafka { Invoke-SetupKafka }
        IntegrationTest { Invoke-TestExecution IntegrationTests }
        Coverage { Invoke-Coverage }
        Package { Invoke-BuildPackage }
        Push { Invoke-PushPackage }
        DockerBuild { Invoke-Step { DockerBuild } }
        DockerRun { Invoke-Step { DockerRun } }
        Run { Invoke-Step { Run } }
        default { throw "Command '$Command' is not recognized" }
    }
}
