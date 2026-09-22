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
    .EXAMPLE
        .\build-config.ps1 build -Configuration Release -Version "2.0" -BuildCounter 45

        Overrides the default build configuration (Debug) to build in release
        mode with assembly version 2.0.45.

    .EXAMPLE
        .\build-config.ps1 unittest

        Output: test results displayed in the console and saved to XML files.

    .EXAMPLE
        .\build-config.ps1 push -NuGetApiKey $env:nuget_key
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive')]
param(
    # Command to execute, defaults to "Build".
    [string]
    [ValidateSet("Clean", "Restore", "Build", "BuildAndPublish", "UnitTest", "E2ETest", "IntegrationTest", "Coverage", "Package", "Push", "DockerBuild", "DockerRun", "Run")]
    $Command = "Build",

    # Full semantic version for the DMS Configuration Service (e.g. "0.7.1-alpha.0.83").
    # When non-empty, forwarded to MSBuild as /p:Version and /p:InformationalVersion.
    # The current package number is configured in the build automation tool and
    # passed to this script.
    [string]
    $DmsCSVersion = "8.0.0",

    # Normalized four-part assembly version (Major.Minor.Patch.Height, e.g. "0.7.1.83").
    # When non-empty, forwarded to MSBuild as /p:AssemblyVersion and /p:FileVersion.
    # Derived from $DmsCSVersion by the CI pipeline for prerelease builds.
    [string]
    $DmsCSAssemblyVersion = "",

    # .NET project build configuration, defaults to "Debug". Options are: Debug, Release.
    [string]
    [ValidateSet("Debug", "Release")]
    $Configuration = "Debug",

    # When set, `dotnet restore` runs with `--locked-mode`, failing the build if a committed
    # packages.lock.json is out of sync. The release/publish build passes this so published
    # packages come from the committed lock graph; the PR `verify-lock-files` gate enforces lock
    # consistency separately. Ordinary build/test jobs and local builds leave it off (see
    # docs/NUGET-LOCK-FILES.md).
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
    # NuGet package corresponding to the provided $DmsCSVersion and $BuildCounter.
    [string]
    $PackageFile,

    # Only required with local builds and testing.
    [switch]
    $IsLocalBuild,

    # Only required with E2E testing.
    [switch]
    $SkipDockerBuild,

    # Identity provider type
    [string]
    [ValidateSet("keycloak", "self-contained")]
    $IdentityProvider="self-contained",

    # Environment file for the E2E docker-compose stack
    [string]
    $EnvironmentFile = "./.env.config.e2e",

    # Optional dotnet test --filter expression applied to E2E test runs
    [string]
    $E2ETestFilter = ""
)

$solutionRoot = "$PSScriptRoot/src/config"
$defaultSolution = "$solutionRoot/EdFi.DmsConfigurationService.sln"
$applicationRoot = "$solutionRoot/frontend"
$projectName = "EdFi.DmsConfigurationService.Frontend.AspNetCore"
$packageName = "EdFi.Api.ConfigurationService"
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
    Invoke-Execute {
        $restoreArgs = @()
        if ($LockedMode) { $restoreArgs += "--locked-mode" }
        dotnet restore $defaultSolution --verbosity:normal @restoreArgs
    }
}

function SetDMSAssemblyInfo {
    Invoke-Execute {
        $assembly_version = $DmsCSVersion

        Invoke-RegenerateFile "$solutionRoot/Directory.Build.props" @"
<Project>
    <!-- This file is generated by the build script. -->
    <PropertyGroup>
        <TreatWarningsAsErrors>True</TreatWarningsAsErrors>
        <ErrorLog>results.sarif,version=2.1</ErrorLog>
        <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
        <Product>Ed-Fi API Configuration Service</Product>
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
        $versionArgs = @()
        if (-not [string]::IsNullOrEmpty($DmsCSVersion))
        {
            $versionArgs += "/p:Version=$DmsCSVersion"
            $versionArgs += "/p:InformationalVersion=$DmsCSVersion"
            $versionArgs += "/p:VersionPrefix=$DmsCSVersion"
        }
        if (-not [string]::IsNullOrEmpty($DmsCSAssemblyVersion))
        {
            $versionArgs += "/p:AssemblyVersion=$DmsCSAssemblyVersion"
            $versionArgs += "/p:FileVersion=$DmsCSAssemblyVersion"
        }

        dotnet build $defaultSolution -c $Configuration --nologo --no-restore @versionArgs
    }
}

function PublishApi {
    Invoke-Execute {
        $project = "$applicationRoot/$projectName/"
        $outputPath = "$project/publish"
        $versionArgs = @()
        if (-not [string]::IsNullOrEmpty($DmsCSVersion))
        {
            $versionArgs += "/p:Version=$DmsCSVersion"
            $versionArgs += "/p:InformationalVersion=$DmsCSVersion"
            $versionArgs += "/p:VersionPrefix=$DmsCSVersion"
        }
        if (-not [string]::IsNullOrEmpty($DmsCSAssemblyVersion))
        {
            $versionArgs += "/p:AssemblyVersion=$DmsCSAssemblyVersion"
            $versionArgs += "/p:FileVersion=$DmsCSAssemblyVersion"
        }

        # --no-restore: reuse the restore from Invoke-Build (which honors -LockedMode) instead of
        # letting publish run a second, unlocked restore that would bypass the lock graph.
        dotnet publish $project -c $Configuration -o $outputPath --nologo --no-restore @versionArgs
    }
}


function RunTests {
    param (
        # File search filter
        [string]
        $Filter,

        [string]
        $IdentityProvider,

        # Optional dotnet test --filter expression
        [string]
        $TestFilter
    )

    $testAssemblies = @(Get-RequiredTestAssembly -SolutionRoot $solutionRoot -Filter $Filter -Configuration $Configuration)

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
                dotnet tool run coverlet -- $($_) `
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

            $filterArgs = @()
            if (-not [string]::IsNullOrEmpty($TestFilter)) {
                $filterArgs += "--filter"
                $filterArgs += "$TestFilter"
            }

            Invoke-Execute {
                dotnet test $target `
                    --no-build `
                    --no-restore `
                    --logger "trx;LogFileName=$trx.trx" `
                    --logger "console" `
                    --nologo `
                    @filterArgs
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
    Invoke-Execute { RunTests -Filter "*.Tests.E2E" -TestFilter $E2ETestFilter }
}

function E2ETests {
    param (
        [switch]
        $SkipDockerBuild,

        [string]
        $IdentityProvider
    )
    Invoke-Execute {
        try {
            Push-Location eng/docker-compose/
            if ($SkipDockerBuild) {
                ./start-local-config.ps1 -EnvironmentFile $EnvironmentFile -IdentityProvider $IdentityProvider
            }
            else {
                ./start-local-config.ps1 -EnvironmentFile $EnvironmentFile -r -IdentityProvider $IdentityProvider
            }

            Import-Module ./env-utility.psm1 -Force
            $envValues = ReadValuesFromEnvFile $EnvironmentFile
            if ($envValues["DMS_CONFIG_DATASTORE"]) {
                $env:DMS_CONFIG_DATASTORE = $envValues["DMS_CONFIG_DATASTORE"]
            }
            # The E2E harness skips @MultitenantOnly scenarios unless this is true.
            # Assign unconditionally so an env file without the key clears any stale
            # value instead of leaving the scenarios enabled against a single-tenant stack.
            $env:DMS_CONFIG_MULTI_TENANCY = $envValues["DMS_CONFIG_MULTI_TENANCY"]
            # The E2E test-data cleanup connects to the stack's database using these
            # values (falling back to the standard .env.config*.e2e defaults when unset).
            # Assign unconditionally so keys absent from the env file clear stale values
            # and cleanup targets the same database the compose stack published.
            $env:POSTGRES_PASSWORD = $envValues["POSTGRES_PASSWORD"]
            $env:POSTGRES_PORT = $envValues["POSTGRES_PORT"]
            $env:POSTGRES_DB_NAME = $envValues["POSTGRES_DB_NAME"]
            $env:MSSQL_SA_PASSWORD = $envValues["MSSQL_SA_PASSWORD"]
            $env:MSSQL_PORT = $envValues["MSSQL_PORT"]
        }
        finally {
            Pop-Location
        }
    }
    Invoke-Execute { PublishEffectiveRoleClaimSettings }
    Invoke-Step { RunE2E }
}

<#
.SYNOPSIS
    Publishes the role settings the running Configuration Service actually received, for the
    role-claim E2E scenario to compare a token's claim against.
.DESCRIPTION
    Read back from the container rather than from the environment file. Compose resolves a
    shell-provided value ahead of the file, so copying file values would make the scenario expect
    a role the container was never given; and a key the compose files never forward to the
    container would otherwise change the expectation without changing the provider. Only the four
    settings the scenario depends on are read, so no unrelated container value is exported.
#>
function PublishEffectiveRoleClaimSettings {
    $settingNames = @(
        "AppSettings__IdentityProvider",
        "IdentitySettings__RoleClaimType",
        "IdentitySettings__ClientRole",
        "Authentication__RoleClaimAttribute"
    )

    $containerEnvironment = docker inspect ed-fi-api-config-service `
        --format '{{range .Config.Env}}{{println .}}{{end}}'
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the Configuration Service container environment; the role-claim scenario would otherwise compare a token against values the running stack may not have."
    }

    $effective = @{}
    foreach ($line in $containerEnvironment) {
        $pair = $line -split "=", 2
        if ($pair.Count -eq 2 -and $settingNames -contains $pair[0]) {
            $effective[$pair[0]] = $pair[1]
        }
    }

    if (-not $effective.ContainsKey("AppSettings__IdentityProvider")) {
        throw "The Configuration Service container reports no AppSettings__IdentityProvider; cannot determine which provider the role-claim scenario is running against."
    }

    # Published under test-only names that neither Compose nor the application reads, so the
    # expectations never become configuration for a later run in the same shell. Assigned
    # unconditionally so a setting the container does not carry clears a stale expectation
    # instead of leaving the scenario comparing against an earlier lane.
    $env:CMS_E2E_EXPECTED_IDENTITY_PROVIDER = $effective["AppSettings__IdentityProvider"]
    $env:CMS_E2E_EXPECTED_ROLE_CLAIM_TYPE = $effective["IdentitySettings__RoleClaimType"]
    $env:CMS_E2E_EXPECTED_CLIENT_ROLE = $effective["IdentitySettings__ClientRole"]
    $env:CMS_E2E_EXPECTED_SELF_CONTAINED_ROLE_CLAIM_TYPE = $effective["Authentication__RoleClaimAttribute"]
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

    RunNuGetPack -ProjectPath $projectPath -PackageVersion $DmsCSVersion $nugetSpecPath
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
    Write-Output "Building Version ($DmsCSVersion)"

    Invoke-Step { PublishApi }
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
        $SkipDockerBuild,

        [string]
        $IdentityProvider
    )
    switch ($Filter) {
        E2ETests { Invoke-Step { E2ETests -SkipDockerBuild:$SkipDockerBuild -IdentityProvider $IdentityProvider } }
        UnitTests { Invoke-Step { UnitTests } }
        IntegrationTests { Invoke-Step { IntegrationTests } }
        Default { "Unknow Test Type" }
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
            $PackageFile = "$PSScriptRoot/$packageName.$DmsCSVersion.nupkg"
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
$dockerTagDMS = "$($dockerTagBase)/ed-fi-api-configuration-service"

function DockerBuild {
    $versionArgs = @()
    if (-not [string]::IsNullOrEmpty($DmsCSVersion))
    {
        $versionArgs += "--build-arg"
        $versionArgs += "VERSION=$DmsCSVersion"
    }
    if (-not [string]::IsNullOrEmpty($DmsCSAssemblyVersion))
    {
        $versionArgs += "--build-arg"
        $versionArgs += "ASSEMBLY_VERSION=$DmsCSAssemblyVersion"
    }

    Push-Location src/config/
    &docker buildx build -t $dockerTagDMS -f Dockerfile . --build-context parentdir=../ @versionArgs
    Pop-Location
}

function DockerRun {
    &docker run --rm -p 8080:8080 --env-file ./src/.env -d $dockerTagDMS
}

function Run {
    Push-Location src
    try {
        dotnet run --no-build --no-restore --project ./config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore
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
        E2ETest { Invoke-TestExecution E2ETests -SkipDockerBuild:$SkipDockerBuild -IdentityProvider $IdentityProvider }
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
