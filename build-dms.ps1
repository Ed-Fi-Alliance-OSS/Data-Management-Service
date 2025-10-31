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
param(
    # Command to execute, defaults to "Build".
    [string]
    [ValidateSet("Clean", "Restore", "Build", "BuildAndPublish", "UnitTest", "E2ETest", "InstanceE2ETest", "IntegrationTest", "Coverage", "Package", "Push", "DockerBuild", "DockerRun", "Run", "StartEnvironment")]
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
    # Run only the standard E2E tests, excluding instance management tests
    # Instance management tests require special setup (route qualifiers, additional databases)
    # and should be run separately using the instance management test scripts
    Invoke-Execute { RunTests -Filter "EdFi.DataManagementService.Tests.E2E" }
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
        $IdentityProvider="self-contained"
    )

    if (-not $SkipDockerBuild -and -not $UsePublishedImage) {
        Invoke-Step { DockerBuild }
    }

    # Clean up all the containers and volumes
    Invoke-Execute {
        try {
            Push-Location eng/docker-compose/
            ./start-local-dms.ps1 -EnvironmentFile $EnvironmentFile -EnableConfig -d -v
            ./start-published-dms.ps1 -EnvironmentFile $EnvironmentFile -EnableConfig -d -v
        }
        finally {
            Pop-Location
        }
    }
        Invoke-Execute {
            try {
                Push-Location eng/docker-compose/
                if ($UsePublishedImage) {
                    if ($LoadSeedData) {
                    ./start-published-dms.ps1 -EnvironmentFile $EnvironmentFile -EnableConfig -AddExtensionSecurityMetadata -LoadSeedData -IdentityProvider $IdentityProvider
                    }
                    else {
                    ./start-published-dms.ps1 -EnvironmentFile $EnvironmentFile -EnableConfig -AddExtensionSecurityMetadata -IdentityProvider $IdentityProvider
                    }
                }
                else {
                    if ($LoadSeedData) {
                    ./start-local-dms.ps1 -EnvironmentFile $EnvironmentFile -EnableConfig -AddExtensionSecurityMetadata -LoadSeedData -IdentityProvider $IdentityProvider
                    }
                    else {
                    ./start-local-dms.ps1 -EnvironmentFile $EnvironmentFile -EnableConfig -AddExtensionSecurityMetadata -IdentityProvider $IdentityProvider
                    }
                }
            }
            finally {
                Pop-Location
            }
        }
}

function E2ETests {
    Invoke-Step { Start-DockerEnvironment -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider }
    Invoke-Step { RunE2E }
}

function Wait-ForConfigServiceAndClients {
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
                client_secret = "s3creT@09"
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
    Write-Host "Restarting DMS container to ensure it can authenticate properly..." -ForegroundColor Cyan

    # Determine the container name based on docker compose project
    $containerName = "dms-local-dms-1"

    docker restart $containerName

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

function Setup-InstanceManagementDatabases {
    Write-Host "Creating test databases for multi-instance routing..." -ForegroundColor Cyan

    # Create the three test databases
    docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2024;" 2>&1
    Write-Host "Created database: edfi_datamanagementservice_d255901_sy2024" -ForegroundColor Green

    docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2025;" 2>&1
    Write-Host "Created database: edfi_datamanagementservice_d255901_sy2025" -ForegroundColor Green

    docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255902_sy2024;" 2>&1
    Write-Host "Created database: edfi_datamanagementservice_d255902_sy2024" -ForegroundColor Green

    Write-Host "Exporting schema from main database..." -ForegroundColor Cyan

    # Export schema from main database to a temporary location
    $tempSchemaFile = [System.IO.Path]::GetTempFileName()
    docker exec dms-postgresql pg_dump -U postgres -d edfi_datamanagementservice --schema-only > $tempSchemaFile
    Write-Host "Schema exported successfully" -ForegroundColor Green

    Write-Host "Applying schema to test databases..." -ForegroundColor Cyan

    # Apply schema to each test database
    Get-Content $tempSchemaFile | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024
    Write-Host "Schema applied to: edfi_datamanagementservice_d255901_sy2024" -ForegroundColor Green

    Get-Content $tempSchemaFile | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025
    Write-Host "Schema applied to: edfi_datamanagementservice_d255901_sy2025" -ForegroundColor Green

    Get-Content $tempSchemaFile | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024
    Write-Host "Schema applied to: edfi_datamanagementservice_d255902_sy2024" -ForegroundColor Green

    # Clean up temp file
    Remove-Item $tempSchemaFile -ErrorAction SilentlyContinue

    # Verify schema was applied (should show tables in dms schema)
    Write-Host "`nVerifying schema deployment..." -ForegroundColor Cyan
    docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT COUNT(*) as table_count FROM information_schema.tables WHERE table_schema = 'dms';"
}

function RunInstanceE2E {
    # Run only the instance management E2E tests
    $testProject = "$solutionRoot/tests/EdFi.InstanceManagement.Tests.E2E/EdFi.InstanceManagement.Tests.E2E.csproj"
    $trxFile = "$testResults/EdFi.InstanceManagement.Tests.E2E.trx"

    Invoke-Execute {
        dotnet test $testProject `
            --configuration $Configuration `
            --logger "trx;LogFileName=$trxFile" `
            --logger "console" `
            --verbosity normal `
            --nologo
    }
}

function InstanceE2ETests {
    # Instance management tests require the DMS environment to be started with route qualifiers
    Write-Host "Setting up instance management E2E tests..." -ForegroundColor Cyan

    # Start the Docker environment with route qualifiers using the instance management setup script
    $instanceSetupScript = "$solutionRoot/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1"

    if (Test-Path $instanceSetupScript) {
        Write-Host "Starting Docker environment with route qualifiers..." -ForegroundColor Cyan
        Invoke-Execute {
            & $instanceSetupScript
        }
    }
    else {
        throw "Instance Management setup script not found at: $instanceSetupScript"
    }

    # Wait for config service to have all clients registered
    Invoke-Step { Wait-ForConfigServiceAndClients }

    # Restart DMS so it can authenticate with the registered clients
    Invoke-Step { Restart-DmsContainer }

    # Wait for PostgreSQL to be ready
    Invoke-Step { Wait-ForPostgreSQL }

    # Create and configure test databases
    Invoke-Step { Setup-InstanceManagementDatabases }

    # Run the instance management E2E tests
    Invoke-Step { RunInstanceE2E }
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
        $UsePublishedImage,

        [switch]
        $SkipDockerBuild,

        [switch]
        $LoadSeedData
    )
    switch ($Filter) {
        E2ETests { Invoke-Step { E2ETests -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider } }
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
        E2ETest { Invoke-TestExecution E2ETests -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider }
        InstanceE2ETest { Invoke-Step { InstanceE2ETests } }
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
