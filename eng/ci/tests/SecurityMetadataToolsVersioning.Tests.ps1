# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Pester fixtures supply variables to the actual build functions loaded from the script AST.')]
param()

Describe "Security Metadata Tools shared version calculation (DMS-1617)" {
    BeforeAll {
        $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        Import-Module (Join-Path $script:repoRoot "package-helpers.psm1")
        $script:projectPath = Join-Path $TestDrive "tool.csproj"

        function Set-TestPatch {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only the disposable Pester TestDrive fixture.')]
            param([string] $Patch)

            Set-Content -LiteralPath $script:projectPath -Value @"
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <SecurityMetadataToolsPatchVersion>$Patch</SecurityMetadataToolsPatchVersion>
    </PropertyGroup>
</Project>
"@
        }
    }

    It "calculates <Expected> from app <AppVersion>, patch <Patch>, stable <Stable>" -ForEach @(
        @{ AppVersion = "8.1.0"; Patch = "0"; Stable = $false; Expected = "8.1.0" }
        @{ AppVersion = "8.1.0"; Patch = "0"; Stable = $true; Expected = "8.1.0" }
        @{ AppVersion = "8.1.0"; Patch = "1"; Stable = $true; Expected = "8.1.1" }
        @{ AppVersion = "8.1.9"; Patch = "2"; Stable = $false; Expected = "8.1.2" }
        @{ AppVersion = "8.1.9"; Patch = "2"; Stable = $true; Expected = "8.1.2" }
        @{ AppVersion = "8.1.0-alpha.0.42"; Patch = "0"; Stable = $false; Expected = "8.1.0-alpha.0.42" }
        @{ AppVersion = "8.1.0-alpha.0.42"; Patch = "1"; Stable = $true; Expected = "8.1.1" }
        @{ AppVersion = "8.1.9-beta.2"; Patch = "2"; Stable = $false; Expected = "8.1.2-beta.2" }
        @{ AppVersion = "8.1.9-beta.2"; Patch = "2"; Stable = $true; Expected = "8.1.2" }
        @{ AppVersion = "8.1.9-RC-test.3+sha.123"; Patch = "2"; Stable = $false; Expected = "8.1.2-RC-test.3+sha.123" }
        @{ AppVersion = "8.1.9-RC-test.3+sha.123"; Patch = "2"; Stable = $true; Expected = "8.1.2" }
        @{ AppVersion = "8.1.9+sha.123"; Patch = "2"; Stable = $false; Expected = "8.1.2+sha.123" }
        @{ AppVersion = "8.1.9+sha.123"; Patch = "2"; Stable = $true; Expected = "8.1.2" }
        @{ AppVersion = "8.2.0-alpha.0.1"; Patch = "0"; Stable = $true; Expected = "8.2.0" }
        @{ AppVersion = "9.0.0-beta.1"; Patch = "0"; Stable = $false; Expected = "9.0.0-beta.1" }
        @{ AppVersion = "9.0.0"; Patch = "2"; Stable = $true; Expected = "9.0.2" }
        @{ AppVersion = "8.1.9"; Patch = "2147483647"; Stable = $true; Expected = "8.1.2147483647" }
    ) {
        Set-TestPatch $Patch

        Get-SecurityMetadataToolsVersion -AppVersion $AppVersion -ProjectPath $script:projectPath -StableRelease:$Stable |
            Should -BeExactly $Expected
    }

    It "reads the repository project by default, independently of the working directory" {
        $repositoryProject = Join-Path $script:repoRoot "src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools/EdFi.DmsConfigurationService.SecurityMetadataTools.csproj"
        $committedPatch = ([xml] (Get-Content -LiteralPath $repositoryProject -Raw)).Project.PropertyGroup.SecurityMetadataToolsPatchVersion
        Push-Location $TestDrive
        try {
            Get-SecurityMetadataToolsVersion -AppVersion "8.1.9-alpha.42" | Should -BeExactly "8.1.$committedPatch-alpha.42"
        }
        finally {
            Pop-Location
        }
    }

    It "keeps the selected checkout's patch unchanged across repeated stable and prerelease calculations" {
        Set-TestPatch "2"
        $original = [IO.File]::ReadAllBytes($script:projectPath)
        $dryRun = Get-SecurityMetadataToolsVersion -AppVersion "8.1.9-alpha.42" -ProjectPath $script:projectPath -StableRelease
        $prerelease = Get-SecurityMetadataToolsVersion -AppVersion "8.1.9-alpha.42" -ProjectPath $script:projectPath
        $publication = Get-SecurityMetadataToolsVersion -AppVersion "8.1.9-alpha.42" -ProjectPath $script:projectPath -StableRelease

        $dryRun | Should -BeExactly "8.1.2"
        $publication | Should -BeExactly $dryRun
        $prerelease | Should -BeExactly "8.1.2-alpha.42"
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($script:projectPath)) |
            Should -BeExactly ([Convert]::ToBase64String($original))
    }

    It "rejects invalid patch '<Patch>' without falling back to the app patch" -ForEach @(
        @{ Patch = "-1" }
        @{ Patch = "+1" }
        @{ Patch = "01" }
        @{ Patch = "1.2" }
        @{ Patch = "1-alpha" }
        @{ Patch = "2147483648" }
        @{ Patch = "999999999999999999999999999999" }
        @{ Patch = "one" }
        @{ Patch = '$(VersionPatch)' }
        @{ Patch = [string] [char] 0x0661 }
    ) {
        Set-TestPatch $Patch

        { Get-SecurityMetadataToolsVersion -AppVersion "8.1.9" -ProjectPath $script:projectPath } |
            Should -Throw "*SecurityMetadataToolsPatchVersion must be a nonnegative integer*"
    }

    It "rejects <Case> patch declarations" -ForEach @(
        @{ Case = "missing"; Xml = '<Project><PropertyGroup /></Project>'; ExpectedError = '*declares no SecurityMetadataToolsPatchVersion*' }
        @{ Case = "empty"; Xml = '<Project><PropertyGroup><SecurityMetadataToolsPatchVersion /></PropertyGroup></Project>'; ExpectedError = '*declares no SecurityMetadataToolsPatchVersion*' }
        @{ Case = "blank"; Xml = '<Project><PropertyGroup><SecurityMetadataToolsPatchVersion> </SecurityMetadataToolsPatchVersion></PropertyGroup></Project>'; ExpectedError = '*declares no SecurityMetadataToolsPatchVersion*' }
        @{ Case = "duplicate"; Xml = '<Project><PropertyGroup><SecurityMetadataToolsPatchVersion>1</SecurityMetadataToolsPatchVersion></PropertyGroup><PropertyGroup><SecurityMetadataToolsPatchVersion>2</SecurityMetadataToolsPatchVersion></PropertyGroup></Project>'; ExpectedError = '*declares SecurityMetadataToolsPatchVersion more than one time*' }
        @{ Case = "conditional property"; Xml = '<Project><PropertyGroup><SecurityMetadataToolsPatchVersion Condition="true">1</SecurityMetadataToolsPatchVersion></PropertyGroup></Project>'; ExpectedError = '*one unconditional SecurityMetadataToolsPatchVersion*' }
        @{ Case = "conditional group"; Xml = '<Project><PropertyGroup Condition="true"><SecurityMetadataToolsPatchVersion>1</SecurityMetadataToolsPatchVersion></PropertyGroup></Project>'; ExpectedError = '*one unconditional SecurityMetadataToolsPatchVersion*' }
        @{ Case = "target-scoped"; Xml = '<Project><Target Name="Patch"><PropertyGroup><SecurityMetadataToolsPatchVersion>1</SecurityMetadataToolsPatchVersion></PropertyGroup></Target></Project>'; ExpectedError = '*one unconditional SecurityMetadataToolsPatchVersion*' }
    ) {
        Set-Content -LiteralPath $script:projectPath -Value $Xml

        { Get-SecurityMetadataToolsVersion -AppVersion "8.1.9" -ProjectPath $script:projectPath } |
            Should -Throw $ExpectedError
    }

    It "identifies a missing project file" {
        { Get-SecurityMetadataToolsVersion -AppVersion "8.1.9" -ProjectPath (Join-Path $TestDrive "missing.csproj") } |
            Should -Throw "*Security Metadata Tools*missing.csproj does not exist*"
    }

    It "rejects malformed project XML" {
        Set-Content -LiteralPath $script:projectPath -Value '<Project>'

        { Get-SecurityMetadataToolsVersion -AppVersion "8.1.9" -ProjectPath $script:projectPath } |
            Should -Throw
    }

    It "rejects invalid app release source '<AppVersion>'" -ForEach @(
        @{ AppVersion = "8.1" }
        @{ AppVersion = "8.1.0.42" }
        @{ AppVersion = "v8.1.0" }
        @{ AppVersion = "8.01.0" }
        @{ AppVersion = "8.1.0-alpha.01" }
        @{ AppVersion = "not-a-version" }
    ) {
        Set-TestPatch "2"

        { Get-SecurityMetadataToolsVersion -AppVersion $AppVersion -ProjectPath $script:projectPath } |
            Should -Throw "*AppVersion*must be a semantic version from Get-VersionNumber*"
    }
}

Describe "Security Metadata Tools build and package routing (DMS-1617)" {
    BeforeAll {
        $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        Import-Module (Join-Path $script:repoRoot "package-helpers.psm1")
        Import-Module (Join-Path $script:repoRoot "eng/build-helpers.psm1")
        $parseErrors = $null
        $script:buildAst = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:repoRoot "build-config.ps1"), [ref] $null, [ref] $parseErrors)
        if ($parseErrors.Count) { throw $parseErrors[0] }
        # Execute the real orchestration functions, without Invoke-Main's process exit.
        $functions = $script:buildAst.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst]
        }, $false)
        $script:buildFunctions = $functions.Extent.Text -join "`n"

        function dotnet {
            throw "Unmocked dotnet: $args"
        }
    }

    BeforeEach {
        $packageRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $packageRoot | Out-Null
        $harness = Join-Path $packageRoot "build-functions.ps1"
        Set-Content -LiteralPath $harness -Value $script:buildFunctions
        . $harness
        $securityMetadataToolsProject = Join-Path $packageRoot "tool.csproj"
        Set-Content -LiteralPath $securityMetadataToolsProject -Value '<Project><PropertyGroup><SecurityMetadataToolsPatchVersion>2</SecurityMetadataToolsPatchVersion></PropertyGroup></Project>'
        $securityMetadataToolsPackageName = "EdFi.Api.SecurityMetadataTools"
        $packageName = "EdFi.Api.ConfigurationService"
        $DmsCSVersion = "8.1.9-alpha.0.42"
        $DmsCSAssemblyVersion = "8.1.9.42"
        $Configuration = "Release"
        $LockedMode = $true
        $PackageTarget = "SecurityMetadataTools"
        $applicationRoot = Join-Path $packageRoot "frontend"
        $projectName = "EdFi.DmsConfigurationService.Frontend.AspNetCore"
        $script:expectedPackage = Join-Path $packageRoot "EdFi.Api.SecurityMetadataTools.8.1.2-alpha.0.42.nupkg"
        $script:calls = [Collections.Generic.List[object]]::new()
        $script:createPackage = $true
        $script:failCommand = ""
        Mock dotnet {
            $DotnetArgs = @($args)
            $script:calls.Add(@($DotnetArgs))
            $global:LASTEXITCODE = 0
            if ($DotnetArgs[0] -eq $script:failCommand) {
                $global:LASTEXITCODE = 17
            }
            elseif ($DotnetArgs[0] -eq "pack" -and $script:createPackage) {
                Set-Content -LiteralPath $script:expectedPackage -Value "fresh"
            }
        }
    }

    It "restores, rebuilds at the calculated tool version, and packs only the tool" {
        BuildPackage

        $script:calls.Count | Should -Be 3
        $script:calls[0] | Should -Be @("restore", $securityMetadataToolsProject, "--locked-mode")
        $script:calls[1] | Should -Be @("build", $securityMetadataToolsProject, "-c", "Release", "--no-restore", "--nologo",
            "-p:Version=8.1.2-alpha.0.42", "-p:InformationalVersion=8.1.2-alpha.0.42", "-p:VersionPrefix=8.1.2-alpha.0.42")
        $script:calls[2] | Should -Be @("pack", $securityMetadataToolsProject, "-c", "Release", "--no-build", "--no-restore",
            "--output", $packageRoot, "-p:PackageVersion=8.1.2-alpha.0.42")
        Get-Content -LiteralPath $script:expectedPackage | Should -BeExactly "fresh"
    }

    It "honors the ordinary unlocked restore policy when LockedMode is absent" {
        $LockedMode = $false
        BuildPackage
        $script:calls[0] | Should -Be @("restore", $securityMetadataToolsProject)
    }

    It "rejects a missing package even when a stale exact version and another app/tool package exist" {
        Set-Content -LiteralPath $script:expectedPackage -Value "stale"
        Set-Content -LiteralPath (Join-Path $packageRoot "EdFi.Api.SecurityMetadataTools.8.1.1.nupkg") -Value "other tool"
        Set-Content -LiteralPath (Join-Path $packageRoot "EdFi.Api.ConfigurationService.8.1.9-alpha.0.42.nupkg") -Value "app"
        $script:createPackage = $false

        { BuildPackage } | Should -Throw "*Expected SecurityMetadataTools package was not created*"
        Test-Path -LiteralPath $script:expectedPackage | Should -BeFalse
        @(Get-ChildItem -LiteralPath $packageRoot -Filter "*.nupkg").Count | Should -Be 2
    }

    It "stops on a failed <Failed> without accepting stale bytes" -ForEach @(
        @{ Failed = "restore"; Count = 1 }
        @{ Failed = "build"; Count = 2 }
        @{ Failed = "pack"; Count = 3 }
    ) {
        Set-Content -LiteralPath $script:expectedPackage -Value "stale"
        $script:failCommand = $Failed
        { BuildPackage } | Should -Throw "*Error executing command*"
        $script:calls.Count | Should -Be $Count
        Test-Path -LiteralPath $script:expectedPackage | Should -BeFalse
    }

    It "uses NuGet's metadata-free filename while preserving informational metadata" {
        $DmsCSVersion = "8.1.9-alpha.0.42+source.123"
        BuildPackage
        $script:calls[1] | Should -Contain "-p:InformationalVersion=8.1.2-alpha.0.42+source.123"
        $script:calls[2] | Should -Contain "-p:PackageVersion=8.1.2-alpha.0.42+source.123"
        Test-Path -LiteralPath $script:expectedPackage | Should -BeTrue
    }

    It "keeps the existing app package target and its nuspec path" {
        $PackageTarget = "ConfigurationService"
        Mock RunNuGetPack {}
        BuildPackage
        Should -Invoke RunNuGetPack -Exactly 1 -ParameterFilter {
            $ProjectPath -eq "$applicationRoot/$projectName/$projectName.csproj" -and
            $PackageVersion -eq $DmsCSVersion -and
            $nuspecPath -eq "$applicationRoot/$projectName/publish/$projectName.nuspec"
        }
        $script:calls.Count | Should -Be 0
        $targetParameter = $script:buildAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq "PackageTarget" }
        $targetParameter.DefaultValue.SafeGetValue() | Should -BeExactly "ConfigurationService"
    }

    It "builds the calculated tool version after the app solution build" {
        $script:steps = [Collections.Generic.List[string]]::new()
        Mock DotNetClean { $script:steps.Add("clean") }
        Mock Restore { $script:steps.Add("restore") }
        Mock Compile { $script:steps.Add("app") }
        Mock CompileSecurityMetadataTools { $script:steps.Add("tool") }
        Invoke-Build
        $script:steps.ToArray() | Should -Be @("clean", "restore", "app", "tool")
    }

    It "selects only the <Target> artifact for an implicit push" -ForEach @(
        @{ Target = "ConfigurationService"; Filename = "EdFi.Api.ConfigurationService.8.1.9-alpha.0.42.nupkg" }
        @{ Target = "SecurityMetadataTools"; Filename = "EdFi.Api.SecurityMetadataTools.8.1.2-alpha.0.42.nupkg" }
    ) {
        $PackageTarget = $Target
        $PackageFile = ""
        $NuGetApiKey = "test-placeholder"
        $EdFiNuGetFeed = "https://example.invalid/feed"
        $DryRun = $false
        PushPackage
        $script:calls.Count | Should -Be 1
        $script:calls[0] | Should -Be @("nuget", "push", "$packageRoot/$Filename", "--api-key", "test-placeholder", "--source", $EdFiNuGetFeed)
    }

    It "preserves explicit PackageFile selection" {
        $PackageFile = Join-Path $packageRoot "explicit.nupkg"
        $NuGetApiKey = "test-placeholder"
        $EdFiNuGetFeed = "https://example.invalid/feed"
        $DryRun = $false
        PushPackage
        $script:calls[0][2] | Should -BeExactly $PackageFile
    }

    It "uses a numeric assembly version for <Operation> when only app semver is supplied" -ForEach @(
        @{ Operation = "Compile" }
        @{ Operation = "PublishApi" }
    ) {
        $DmsCSAssemblyVersion = ""
        $DmsCSVersion = "8.1.9-alpha.0.42+source.123"
        $defaultSolution = Join-Path $packageRoot "config.sln"
        & $Operation
        $script:calls.Count | Should -Be 1
        $script:calls[0] | Should -Contain "/p:AssemblyVersion=8.1.9.42"
        $script:calls[0] | Should -Contain "/p:FileVersion=8.1.9.42"
        $script:calls[0] | Should -Contain "/p:InformationalVersion=8.1.9-alpha.0.42+source.123"
        $script:calls[0] | Should -Contain "--no-restore"
    }

    It "preserves an explicitly supplied assembly version for <Operation>" -ForEach @(
        @{ Operation = "Compile" }
        @{ Operation = "PublishApi" }
    ) {
        $DmsCSAssemblyVersion = "8.1.9.123"
        $defaultSolution = Join-Path $packageRoot "config.sln"
        & $Operation
        $script:calls[0] | Should -Contain "/p:AssemblyVersion=8.1.9.123"
        $script:calls[0] | Should -Contain "/p:FileVersion=8.1.9.123"
    }

    It "exposes no independent tool version override" {
        $script:buildAst.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Not -Contain "SecurityMetadataToolsVersion"
        $script:buildAst.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Not -Contain "ToolVersion"
    }
}

Describe "Security Metadata Tools release publication and promotion (DMS-1617)" {
    BeforeAll {
        $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        Import-Module (Join-Path $script:repoRoot "package-helpers.psm1")
        $script:feed = 'https://feed.invalid/_packaging/EdFi/nuget/v3/index.json'
        $script:packageName = 'EdFi.Api.SecurityMetadataTools'
        $script:password = [Security.SecureString]::new()
        $script:password.AppendChar('x')

        function New-ReleasePackage {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only a disposable package in Pester TestDrive.')]
            param([string] $Version, [string] $Id = 'EdFi.Api.SecurityMetadataTools')
            $directory = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid()))
            Set-Content (Join-Path $directory "$script:packageName.nuspec") "<package><metadata><id>$Id</id><version>$Version</version></metadata></package>"
            $path = "$directory.nupkg"
            [IO.Compression.ZipFile]::CreateFromDirectory($directory, $path)
            return $path
        }

        function Invoke-TestPublication {
            Publish-SecurityMetadataToolsPackage -Version $script:version -PackageFile $script:packageFile `
                -Sha256 $script:hash -ServiceIndexUrl $script:feed -ApiKey 'test-only'
        }

        function Invoke-TestPromotion {
            Invoke-ContractPromotion -PackagesURL 'https://packages.invalid' -Username 'test' `
                -Password $script:password `
                -ServiceIndexUrl $script:feed -ApiKey 'test-only' -PackageName $script:packageName -Version $script:version
        }
    }

    BeforeEach {
        $script:version = '8.1.2'
        $script:packageFile = New-ReleasePackage $script:version
        $script:hash = (Get-FileHash $script:packageFile).Hash
        $script:published = [Collections.Generic.List[string]]::new()
        $script:published.Add('8.1.1')
        $script:released = @()
        $script:pushExit = 0
        $script:pushes = [Collections.Generic.List[object]]::new()
        Mock -ModuleName package-helpers dotnet {
            $script:pushes.Add(@($args))
            $global:LASTEXITCODE = $script:pushExit
            if ($script:pushExit -eq 0) { $script:published.Add($script:version) }
        }
        Mock -ModuleName package-helpers Invoke-RestMethod {
            if ($Uri -like '*_packaging/*') {
                $view = if ($Uri -like '*@Release/*') { 'Release' } else { 'Local' }
                return @{ resources = @(@{ '@id' = "https://feed.invalid/$view/flat2/"; '@type' = 'PackageBaseAddress/3.0.0' }) }
            }
            if ($Uri -like '*/Release/flat2/*') { return @{ versions = $script:released } }
            if ($Uri -like '*/Local/flat2/*') { return @{ versions = @($script:published) } }
            throw "Unexpected feed request: $Uri"
        }
        Mock -ModuleName package-helpers Invoke-WebRequest { return @{ StatusCode = 200 } }
    }

    It 'refuses unchanged patch 1 after independent 8.1.1, then publishes and promotes committed patch 2 for app 8.1.9' {
        $project = Join-Path $TestDrive 'release-source.csproj'
        Set-Content $project '<Project><PropertyGroup><SecurityMetadataToolsPatchVersion>1</SecurityMetadataToolsPatchVersion></PropertyGroup></Project>'
        $script:version = Get-SecurityMetadataToolsVersion -AppVersion '8.1.9' -ProjectPath $project
        $script:packageFile = New-ReleasePackage $script:version
        $script:hash = (Get-FileHash $script:packageFile).Hash
        { Invoke-TestPublication } | Should -Throw '*already published*Commit a new SecurityMetadataToolsPatchVersion*'
        $script:pushes.Count | Should -Be 0
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly

        Set-Content $project '<Project><PropertyGroup><SecurityMetadataToolsPatchVersion>2</SecurityMetadataToolsPatchVersion></PropertyGroup></Project>'
        $script:version = Get-SecurityMetadataToolsVersion -AppVersion '8.1.9' -ProjectPath $project
        $script:version | Should -BeExactly '8.1.2'
        $script:packageFile = New-ReleasePackage $script:version
        $script:hash = (Get-FileHash $script:packageFile).Hash
        Invoke-TestPublication
        Invoke-TestPromotion | Should -BeExactly 'EdFi.Api.SecurityMetadataTools 8.1.2 promoted to the Release view.'
        $script:pushes.Count | Should -Be 1
        $script:pushes[0] | Should -Be @('nuget', 'push', $script:packageFile, '--api-key', 'test-only', '--source', $script:feed)
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
            $payload = $Body | ConvertFrom-Json
            $payload.packages[0].id -ceq 'EdFi.Api.SecurityMetadataTools' -and
            $payload.packages[0].version -ceq '8.1.2' -and $payload.data.viewId -ceq 'release'
        }
    }

    It 'rejects an equivalent already-published version including metadata differences' {
        $script:published.Add('8.1.2+original')
        { Invoke-TestPublication } | Should -Throw '*already published*'
        $script:pushes.Count | Should -Be 0
    }

    It 'publishes when a reachable service index reports package-index 404' {
        Mock -ModuleName package-helpers Invoke-RestMethod {
            if ($Uri -like '*_packaging/*') {
                return @{ resources = @(@{ '@id' = 'https://feed.invalid/flat2/'; '@type' = 'PackageBaseAddress/3.0.0' }) }
            }
            $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::NotFound)
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('404', $response)
        }
        Invoke-TestPublication
        $script:pushes.Count | Should -Be 1
    }

    It 'does not push or promote when the feed <Failure>' -ForEach @(
        @{ Failure = 'is unavailable' }, @{ Failure = 'is malformed' }
    ) {
        if ($Failure -eq 'is unavailable') {
            Mock -ModuleName package-helpers Invoke-RestMethod { throw 'feed unavailable' }
        }
        else {
            Mock -ModuleName package-helpers Invoke-RestMethod { return @{ error = 'not a service index' } }
        }
        { Invoke-TestPublication; Invoke-TestPromotion } | Should -Throw
        $script:pushes.Count | Should -Be 0
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
    }

    It 'does not promote after a failed push (including a duplicate race)' {
        $script:pushExit = 1
        { Invoke-TestPublication; Invoke-TestPromotion } | Should -Throw '*push failed*'
        $script:pushes.Count | Should -Be 1
        $script:pushes[0] | Should -Not -Contain '--skip-duplicate'
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
    }

    It 'refuses altered bytes before consulting the feed' {
        Add-Content $script:packageFile 'changed'
        { Invoke-TestPublication } | Should -Throw '*hash differs*'
        Should -Invoke -ModuleName package-helpers Invoke-RestMethod -Times 0 -Exactly
        $script:pushes.Count | Should -Be 0
    }

    It 'refuses a different <Mismatch> in a correctly hashed artifact' -ForEach @(
        @{ Mismatch = 'version'; Id = 'EdFi.Api.SecurityMetadataTools'; Version = '8.1.9' }
        @{ Mismatch = 'package'; Id = 'EdFi.Api.ConfigurationService'; Version = '8.1.2' }
    ) {
        $script:packageFile = New-ReleasePackage -Version $Version -Id $Id
        $script:hash = (Get-FileHash $script:packageFile).Hash
        { Invoke-TestPublication } | Should -Throw '*package identity does not match*'
        Should -Invoke -ModuleName package-helpers Invoke-RestMethod -Times 0 -Exactly
        $script:pushes.Count | Should -Be 0
    }

    It 'treats promotion of an already-built package as promotion, without another publication check or push' {
        $script:published.Add($script:version)
        Invoke-TestPromotion | Should -Match 'promoted'
        $script:pushes.Count | Should -Be 0
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 1 -Exactly
    }

    It 'does not mutate a version already in the release view' {
        $script:released = @($script:version)
        Invoke-TestPromotion | Should -Match 'already in the Release view'
        $script:pushes.Count | Should -Be 0
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
    }

    It 'refuses to promote a version absent from the feed' {
        { Invoke-TestPromotion } | Should -Throw '*not on the feed cannot be promoted*'
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
    }
}

Describe 'Security Metadata Tools normal release workflow wiring and source handoff' {
    BeforeAll {
        $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        Import-Module (Join-Path $script:repoRoot 'package-helpers.psm1')
        $script:workflow = Get-Content (Join-Path $script:repoRoot '.github/workflows/on-prerelease.yml') -Raw
        $script:release = Get-Content (Join-Path $script:repoRoot '.github/workflows/on-release.yml') -Raw
        function Get-ReleaseJob([string] $Name) {
            $match = [regex]::Match($script:workflow, "(?ms)^  $([regex]::Escape($Name)):\r?\n.*?(?=^  [\w-]+:|\z)")
            if (-not $match.Success) { throw "Missing job: $Name" }
            $match.Value
        }
    }

    It 'keeps the actual release API payload on the triggering commit after main advances' {
        $source = New-Item -ItemType Directory (Join-Path $TestDrive 'source')
        Push-Location $source
        try {
            git init -b main | Out-Null
            $project = Join-Path $source 'tool.csproj'
            Set-Content $project '<Project><PropertyGroup><SecurityMetadataToolsPatchVersion>1</SecurityMetadataToolsPatchVersion></PropertyGroup></Project>'
            git add tool.csproj
            git -c user.name=Test -c user.email=test@example.invalid -c commit.gpgSign=false commit -m 'Release source' | Out-Null
            $triggerVersion = Get-SecurityMetadataToolsVersion -AppVersion '8.1.9' -ProjectPath $project
            $triggerVersion | Should -BeExactly '8.1.1'
            $triggerSha = git rev-parse HEAD
            Set-Content $project '<Project><PropertyGroup><SecurityMetadataToolsPatchVersion>2</SecurityMetadataToolsPatchVersion></PropertyGroup></Project>'
            git add tool.csproj
            git -c user.name=Test -c user.email=test@example.invalid -c commit.gpgSign=false commit -m 'Later main' | Out-Null
            Get-SecurityMetadataToolsVersion -AppVersion '8.1.9' -ProjectPath $project | Should -BeExactly '8.1.2'
            (git rev-parse main) | Should -Not -Be $triggerSha

            $workflow = Get-Content (Join-Path $script:repoRoot '.github/workflows/on-config-merge-or-tag.yml') -Raw
            $step = [regex]::Match($workflow, '(?ms)^      - name: Create CS API Pre-Release\r?\n        run: \|\r?\n(?<body>.*)\z').Groups['body'].Value
            $step | Should -Not -BeNullOrEmpty
            $body = ($step -replace '(?m)^          ', '').Replace('${{ steps.versions.outputs.dms-semver }}', 'cs-v8.1.9').
                Replace('${{ github.sha }}', $triggerSha).Replace('${{ env.TOKEN }}', 'test-only').
                Replace('${{ env.API_URL }}', 'https://api.invalid/repos/test/repo')
            $script:capturedRelease = $null
            Mock Invoke-RestMethod {
                $script:capturedRelease = $Body | ConvertFrom-Json
                return @{ id = 1 }
            }
            & ([scriptblock]::Create($body)) | Out-Null
            # Model GitHub's tag creation using the actual API payload, then read that source's patch.
            git -c tag.gpgSign=false tag --no-sign $script:capturedRelease.tag_name $script:capturedRelease.target_commitish
            $LASTEXITCODE | Should -Be 0
            git show "$($script:capturedRelease.tag_name):tool.csproj" | Set-Content (Join-Path $TestDrive 'tagged-tool.csproj')
            Get-SecurityMetadataToolsVersion -AppVersion '8.1.9' -ProjectPath (Join-Path $TestDrive 'tagged-tool.csproj') |
                Should -BeExactly $triggerVersion
            Should -Invoke Invoke-RestMethod -Times 1 -Exactly -ParameterFilter {
                $payload = $Body | ConvertFrom-Json
                $Method -eq 'POST' -and $Uri -eq 'https://api.invalid/repos/test/repo/releases' -and
                $payload.target_commitish -ceq $triggerSha -and $payload.tag_name -ceq 'cs-pre-8.1.9' -and
                $payload.name -ceq 'cs-v8.1.9' -and $payload.prerelease -eq $true
            }
        }
        finally { Pop-Location }
    }

    It 'gates packing on the Secrets check and the cs- release event, excluding contract dispatch' {
        $pack = Get-ReleaseJob 'pack-security-metadata-tools'
        $pack | Should -Match '(?m)^      - check-secrets-contract$'
        $pack | Should -Match "if: startsWith\(github.event.release.tag_name, 'cs-'\)"
        $pack | Should -Not -Match 'workflow_dispatch|always\('
        $pack | Should -Match 'fetch-depth: 0'
        $pack | Should -Match 'Get-SecurityMetadataToolsVersion -AppVersion "\$\{\{ steps.versions.outputs.dms-v \}\}"'
        $pack | Should -Not -Match '-StableRelease'
        $pack | Should -Match 'Test-SecurityMetadataToolsPackage.ps1'
    }

    It 'gates publication on successful packing and Secrets publication and checks the original version and hash' {
        $publish = Get-ReleaseJob 'publish-package-security-metadata-tools'
        $publish | Should -Match '(?m)^      - publish-package-secrets$'
        $publish | Should -Match '(?m)^      - pack-security-metadata-tools$'
        $publish | Should -Not -Match 'always\(|skip-duplicate|continue-on-error'
        $publish | Should -Match 'Publish-SecurityMetadataToolsPackage'
        $publish | Should -Match '-Version "\$\{\{ needs.pack-security-metadata-tools.outputs.tool-version \}\}"'
        $publish | Should -Match '-Sha256 "\$\{\{ needs.pack-security-metadata-tools.outputs.sha256 \}\}"'
    }

    It 'uses distinct artifacts and the same version and subject for SBOM, provenance and release attachments' {
        $pack = Get-ReleaseJob 'pack-security-metadata-tools'
        $pack | Should -Match 'tool-version: \$\{\{ steps.tool-version.outputs.version \}\}'
        $pack | Should -Match 'hash-code: \$\{\{ steps.hash-code.outputs.hash-code \}\}'
        $pack | Should -Match 'SECURITY_METADATA_TOOLS_PACKAGE_NAME.*steps.tool-version.outputs.version.*nupkg'
        $sbom = Get-ReleaseJob 'sbom-create-security-metadata-tools'
        $sbom | Should -Match 'needs.pack-security-metadata-tools.outputs.tool-version'
        $sbom | Should -Match './eng/ci/New-SecurityMetadataToolsSbom.ps1'
        $sbom | Should -Match '-Sha256 "\$\{\{ needs.pack-security-metadata-tools.outputs.sha256 \}\}"'
        $sbom | Should -Match 'SECURITY_METADATA_TOOLS_PACKAGE_NAME.*-NuGet'
        $sbom | Should -Match 'SECURITY_METADATA_TOOLS_PACKAGE_NAME.*-SBOM'
        $provenance = Get-ReleaseJob 'provenance-create-security-metadata-tools'
        $provenance | Should -Match 'base64-subjects: \$\{\{ needs.pack-security-metadata-tools.outputs.hash-code \}\}'
        $provenance | Should -Match 'provenance-name: edfiApiSecurityMetadataTools.intoto.jsonl'
        $attach = Get-ReleaseJob 'attach-security-metadata-tools-artifacts-to-release'
        foreach ($dependency in @('classify-release', 'pack-security-metadata-tools', 'publish-package-security-metadata-tools',
            'sbom-create-security-metadata-tools', 'provenance-create-security-metadata-tools')) {
            $attach | Should -Match "(?m)^      - $dependency`$"
        }
        $attach | Should -Match 'tagged-release == ''true'''
        $attach | Should -Match 'SECURITY_METADATA_TOOLS_PACKAGE_NAME.*-SBOM.zip'
        $attach | Should -Match 'edfiApiSecurityMetadataTools.intoto.jsonl'
    }

    It 'promotes from the release source checkout with full history and the calculated exact tool version' {
        $promotionJob = [regex]::Match($script:release, '(?ms)^  promote-azure-artifact:.*?(?=^  [\w-]+:|\z)').Value
        $promotionJob | Should -Match 'ref: \$\{\{ github.ref \}\}'
        $promotionJob | Should -Match 'fetch-depth: 0'
        $step = [regex]::Match($promotionJob, '(?ms)^      - name: Promote EdFi.Api.SecurityMetadataTools Package.*?(?=^      - name:|\z)').Value
        $step | Should -Match "github.ref_type == 'tag'"
        $step | Should -Match 'Get-SecurityMetadataToolsVersion -AppVersion "\$\{\{ steps.tool-release-source.outputs.dms-v \}\}" -StableRelease'
        $step | Should -Match 'Invoke-ContractPromotion'
        $step | Should -Match '-PackageName "EdFi.Api.SecurityMetadataTools"'
        $step | Should -Match '-Version \$version'
        $step | Should -Not -Match 'Publish-SecurityMetadataToolsPackage'
    }
}

Describe 'SecurityMetadataTools independent release orchestration' {
    BeforeAll {
        $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        Import-Module (Join-Path $script:repoRoot 'eng/ci/SecurityMetadataToolsRelease.psm1')
        Import-Module (Join-Path $script:repoRoot 'package-helpers.psm1')
        $script:feed = 'https://feed.invalid/_packaging/EdFi/nuget/v3/index.json'
        $script:password = [Security.SecureString]::new()
        $script:password.AppendChar('x')
        $script:secretsVersion = Get-SecretsContractVersion
        $script:toolVersion = Get-SecurityMetadataToolsVersion -AppVersion '8.1.9-beta.42' -StableRelease
        $stage = New-Item -ItemType Directory (Join-Path $TestDrive 'tool')
        Set-Content (Join-Path $stage 'EdFi.Api.SecurityMetadataTools.nuspec') "<package><metadata><id>EdFi.Api.SecurityMetadataTools</id><version>$script:toolVersion</version></metadata></package>"
        $script:toolPackage = Join-Path $TestDrive 'tool.nupkg'
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $script:toolPackage)
        $script:toolHash = (Get-FileHash $script:toolPackage).Hash

        # Real compiled contract fixtures exercise the existing verifier, including its
        # metadata, documentation and dependency comparison, without any live feed calls.
        $library = New-Item -ItemType Directory (Join-Path $TestDrive 'secrets/lib/net10.0')
        Add-Type -TypeDefinition 'public class IndependentReleaseContract { public void Apply() {} }' `
            -OutputAssembly (Join-Path $library 'EdFi.DmsConfigurationService.Secrets.dll')
        Set-Content (Join-Path $library 'EdFi.DmsConfigurationService.Secrets.xml') '<doc><members><member name="M:IndependentReleaseContract.Apply"><summary>Original contract.</summary></member></members></doc>'
        Set-Content (Join-Path $TestDrive 'secrets/EdFi.Api.Secrets.nuspec') "<package><metadata><id>EdFi.Api.Secrets</id><version>$script:secretsVersion</version></metadata></package>"
        $script:secretsPackage = Join-Path $TestDrive 'secrets.nupkg'
        [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $TestDrive 'secrets'), $script:secretsPackage)
        Set-Content (Join-Path $library 'EdFi.DmsConfigurationService.Secrets.xml') '<doc><members><member name="M:IndependentReleaseContract.Apply"><summary>Incompatible contract.</summary></member></members></doc>'
        $script:changedSecretsPackage = Join-Path $TestDrive 'changed-secrets.nupkg'
        [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $TestDrive 'secrets'), $script:changedSecretsPackage)

        function Invoke-IndependentPublication {
            Publish-SecurityMetadataToolsRelease -Version $script:toolVersion -PackageFile $script:toolPackage `
                -Sha256 $script:toolHash -ServiceIndexUrl $script:feed -ApiKey 'test-only' -FeedAccess $script:feedAccess
        }
        function Invoke-IndependentPromotion {
            Invoke-ContractPromotion -Version $script:toolVersion -PackageName 'EdFi.Api.SecurityMetadataTools' `
                -PackagesURL 'https://packages.invalid' -Username 'test' -Password $script:password `
                -ServiceIndexUrl $script:feed -ApiKey 'test-only'
        }
    }
    BeforeEach {
        $script:published = [Collections.Generic.List[string]]::new()
        $script:secretsPublished = $true
        $script:secretsFeedError = $false
        $script:publishedSecretsPackage = $script:secretsPackage
        $script:pushExit = 0
        $script:packExit = 0
        $script:promotionFails = $false
        $script:feedAccess = @{
            ResolvePackageBaseAddress = {
                if ($script:secretsFeedError) { throw 'Secrets feed unavailable' }
                'https://feed.invalid/flat2'
            }
            GetPublishedVersions = { @{ Found = $script:secretsPublished; Versions = @($script:secretsVersion) } }
            SavePublishedPackage = {
                param($BaseAddress, $NormalizedId, $NormalizedVersion, $Destination, $ApiKey)
                $BaseAddress | Should -BeExactly 'https://feed.invalid/flat2'
                $NormalizedId | Should -BeExactly 'edfi.api.secrets'
                $NormalizedVersion | Should -BeExactly $script:secretsVersion
                $ApiKey | Should -BeExactly 'test-only'
                Copy-Item $script:publishedSecretsPackage $Destination
            }
        }
        Mock -ModuleName SecurityMetadataToolsRelease pwsh { $global:LASTEXITCODE = 0 }
        Mock -ModuleName SecurityMetadataToolsRelease Get-FileHash { @{ Hash = $script:toolHash } }
        Mock -ModuleName SecurityMetadataToolsRelease dotnet {
            $args[0] | Should -BeExactly 'pack'
            $output = $args[[Array]::IndexOf($args, '--output') + 1]
            New-Item -ItemType Directory $output -Force | Out-Null
            Copy-Item $script:secretsPackage (Join-Path $output "EdFi.Api.Secrets.$script:secretsVersion.nupkg")
            $global:LASTEXITCODE = $script:packExit
        }
        Mock -ModuleName package-helpers dotnet {
            $args[0..1] | Should -Be @('nuget', 'push')
            $args[2] | Should -BeExactly $script:toolPackage
            $global:LASTEXITCODE = $script:pushExit
            if ($script:pushExit -eq 0) { $script:published.Add($script:toolVersion) }
        }
        Mock -ModuleName package-helpers Invoke-RestMethod {
            if ($Uri -like '*_packaging/*') {
                $view = if ($Uri -like '*@Release/*') { 'Release' } else { 'Local' }
                return @{ resources = @(@{ '@id' = "https://feed.invalid/$view/flat2/"; '@type' = 'PackageBaseAddress/3.0.0' }) }
            }
            if ($Uri -like '*/Release/flat2/*') { return @{ versions = @() } }
            if ($Uri -like '*/Local/flat2/*') { return @{ versions = @($script:published) } }
            throw "Unexpected feed request: $Uri"
        }
        Mock -ModuleName package-helpers Invoke-WebRequest {
            if ($script:promotionFails) { throw 'Promotion unavailable' }
            return @{ StatusCode = 200 }
        }
    }

    It 'builds identical stable versions for dry-run and publication from an app prerelease with no build-phase mutations' {
        $dryRun = Invoke-SecurityMetadataToolsReleaseBuild -AppVersion '8.1.9-beta.42+source.123'
        $publication = Invoke-SecurityMetadataToolsReleaseBuild -AppVersion '8.1.9-beta.42+source.123'
        $dryRun.Version | Should -BeExactly $script:toolVersion
        $publication.Version | Should -BeExactly $dryRun.Version
        $publication.Sha256 | Should -BeExactly $dryRun.Sha256
        Should -Invoke -ModuleName SecurityMetadataToolsRelease pwsh -Times 2 -Exactly -ParameterFilter {
            $args[-2] -ceq '-AppVersion' -and $args[-1] -ceq $script:toolVersion
        }
        Should -Invoke -ModuleName package-helpers dotnet -Times 0 -Exactly
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
        Should -Invoke -ModuleName SecurityMetadataToolsRelease dotnet -Times 0 -Exactly
    }

    It 'uses the shared MinVer source on an untagged implementation branch in stable mode' {
        # Control only MinVer/tool installation; execute the real source reader and
        # release build orchestration, without requiring a cs- application release tag.
        $hadMinVer = [bool] (Get-Command minver -ErrorAction SilentlyContinue)
        if (-not $hadMinVer) { function global:minver { throw 'MinVer must be mocked in this test.' } }
        Mock -ModuleName package-helpers minver { '8.1.9-beta.0.42' }
        Mock -ModuleName package-helpers dotnet { $global:LASTEXITCODE = 0 }
        $originalOutput = $env:GITHUB_OUTPUT
        $env:GITHUB_OUTPUT = Join-Path $TestDrive 'branch-versions.txt'
        $branch = New-Item -ItemType Directory (Join-Path $TestDrive 'implementation-branch')
        Push-Location $branch
        try {
            git init -q -b tool-patch
            git -c user.name=Test -c user.email=test@example.invalid -c commit.gpgsign=false commit -qm initial --allow-empty
            git branch --show-current | Should -BeExactly 'tool-patch'
            @(git tag).Count | Should -Be 0
            Get-VersionNumber -projectPrefix cs | Out-Null
            $source = ((Get-Content $env:GITHUB_OUTPUT | Where-Object { $_ -like 'dms-v=*' }) -split '=', 2)[1]
            (Invoke-SecurityMetadataToolsReleaseBuild -AppVersion $source).Version | Should -BeExactly $script:toolVersion
            Should -Invoke -ModuleName package-helpers minver -Times 1 -Exactly -ParameterFilter { $args -join ' ' -ceq '-t v' }
            Should -Invoke -ModuleName package-helpers dotnet -Times 0 -Exactly -ParameterFilter { $args[0] -ceq 'nuget' }
            Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
        }
        finally {
            Pop-Location
            $env:GITHUB_OUTPUT = $originalOutput
            if (-not $hadMinVer) { Remove-Item Function:/minver }
        }
    }

    It 'fails build when installed-package validation fails' {
        Mock -ModuleName SecurityMetadataToolsRelease pwsh { $global:LASTEXITCODE = 1 }
        { Invoke-SecurityMetadataToolsReleaseBuild -AppVersion '8.1.9-beta.42' } | Should -Throw '*validation failed*'
        Should -Invoke -ModuleName SecurityMetadataToolsRelease Get-FileHash -Times 0 -Exactly
    }

    It 'accepts a matching published Secrets contract, then pushes and promotes exactly the retained tool version' {
        Invoke-IndependentPublication
        Invoke-IndependentPromotion | Should -Match 'promoted'
        Should -Invoke -ModuleName package-helpers dotnet -Times 1 -Exactly
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
            $payload = $Body | ConvertFrom-Json
            $payload.packages[0].id -ceq 'EdFi.Api.SecurityMetadataTools' -and
            $payload.packages[0].version -ceq $script:toolVersion -and $payload.data.viewId -ceq 'release'
        }
    }

    It 'checks the published dependency in a dry run without publishing either package or promoting' {
        Assert-PublishedSecretsContract -ServiceIndexUrl $script:feed -ApiKey 'test-only' -FeedAccess $script:feedAccess
        Should -Invoke -ModuleName package-helpers dotnet -Times 0 -Exactly
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
    }

    It 'blocks tool publication/promotion for <Failure> Secrets state' -ForEach @(
        @{ Failure = 'missing'; Error = '*must already be published*' }
        @{ Failure = 'incompatible'; Error = '*XML documentation differ*' }
        @{ Failure = 'unreadable'; Error = '*Secrets feed unavailable*' }
        @{ Failure = 'unbuildable'; Error = '*packing failed*' }
    ) {
        switch ($Failure) {
            missing { $script:secretsPublished = $false }
            incompatible { $script:publishedSecretsPackage = $script:changedSecretsPackage }
            unreadable { $script:secretsFeedError = $true }
            unbuildable { $script:packExit = 1 }
        }
        { Invoke-IndependentPublication; Invoke-IndependentPromotion } | Should -Throw $Error
        Should -Invoke -ModuleName package-helpers dotnet -Times 0 -Exactly
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
    }

    It 'rejects a new attempt at the same exact version without promotion' {
        $script:published.Add($script:toolVersion)
        { Invoke-IndependentPublication; Invoke-IndependentPromotion } | Should -Throw '*already published*Commit a new*'
        Should -Invoke -ModuleName package-helpers dotnet -Times 0 -Exactly
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
    }

    It 'never promotes when push fails' {
        $script:pushExit = 1
        { Invoke-IndependentPublication; Invoke-IndependentPromotion } | Should -Throw '*push failed*'
        Should -Invoke -ModuleName package-helpers dotnet -Times 1 -Exactly
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 0 -Exactly
    }

    It 'retries only promotion after successful push without rebuilding, rechecking publication or changing the patch' {
        $build = Invoke-SecurityMetadataToolsReleaseBuild -AppVersion '8.1.9-beta.42'
        Invoke-IndependentPublication
        $script:promotionFails = $true
        { Invoke-IndependentPromotion } | Should -Throw '*Promotion unavailable*'
        $script:promotionFails = $false
        Invoke-IndependentPromotion | Should -Match 'promoted'
        $build.Version | Should -BeExactly $script:toolVersion
        Get-SecurityMetadataToolsVersion -AppVersion '8.1.9-beta.42' -StableRelease | Should -BeExactly $build.Version
        Should -Invoke -ModuleName SecurityMetadataToolsRelease pwsh -Times 1 -Exactly
        Should -Invoke -ModuleName SecurityMetadataToolsRelease dotnet -Times 1 -Exactly
        Should -Invoke -ModuleName package-helpers dotnet -Times 1 -Exactly
        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 2 -Exactly -ParameterFilter {
            ($Body | ConvertFrom-Json).packages[0].version -ceq $build.Version
        }
        # One Local lookup for duplicate refusal; one per promotion for existence.
        Should -Invoke -ModuleName package-helpers Invoke-RestMethod -Times 3 -Exactly -ParameterFilter { $Uri -like '*/Local/flat2/*' }
        { Invoke-IndependentPublication } | Should -Throw '*already published*'
        Should -Invoke -ModuleName package-helpers dotnet -Times 1 -Exactly
    }
}

Describe 'SecurityMetadataTools independent workflow wiring' {
    BeforeAll {
        $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $script:dispatch = Get-Content (Join-Path $script:repoRoot '.github/workflows/Pkg EdFi.Api.SecurityMetadataTools.yml') -Raw
    }
    It 'is dispatch-only with a publish switch, pinned selected-ref checkout and full version-source history' {
        $events = [regex]::Match($script:dispatch, '(?ms)^on:\n(.*?)^permissions:').Value
        $events | Should -Match 'workflow_dispatch:'
        $events | Should -Match 'publish_package:'
        $events | Should -Not -Match 'release:|push:|version:|workflow_call:'
        $build = [regex]::Match($script:dispatch, '(?ms)^  build:.*?(?=^  [\w-]+:)').Value
        $build | Should -Match 'ref: \$\{\{ github.sha \}\}'
        $build | Should -Match 'fetch-depth: 0'
        $build | Should -Match 'Get-VersionNumber -projectPrefix "cs"'
        $build | Should -Match 'Invoke-SecurityMetadataToolsReleaseBuild -AppVersion \$env:APP_VERSION'
        $build | Should -Not -Match 'publish_package|release.tag_name'
    }
    It 'always attests the exact artifact and gates publication on both attestations' {
        $script:dispatch | Should -Match 'needs: \[build, check-secrets-contract, sbom-create-security-metadata-tools, provenance-create-security-metadata-tools\]'
        $script:dispatch | Should -Match 'base64-subjects: \$\{\{ needs.build.outputs.hash-code \}\}'
        $script:dispatch | Should -Match '-Sha256 "\$\{\{ needs.build.outputs.sha256 \}\}"'
        $check = [regex]::Match($script:dispatch, '(?ms)^  check-secrets-contract:.*?(?=^  publish:)').Value
        $check | Should -Match 'Assert-PublishedSecretsContract'
        $check | Should -Not -Match 'if:|publish_package'
        $script:dispatch | Should -Match './eng/ci/New-SecurityMetadataToolsSbom.ps1'
        $script:dispatch | Should -Match 'provenance-name: edfiApiSecurityMetadataTools.intoto.jsonl'
    }
    It 'guards both mutations and retries promotion with only the original build version' {
        $publish = [regex]::Match($script:dispatch, '(?ms)^  publish:.*?(?=^  promote:)').Value
        $promote = [regex]::Match($script:dispatch, '(?ms)^  promote:.*').Value
        $publish | Should -Match 'if: \$\{\{ inputs.publish_package \}\}'
        $publish | Should -Match 'Publish-SecurityMetadataToolsRelease'
        $promote | Should -Match 'needs: \[build, publish\]'
        $promote | Should -Match 'if: \$\{\{ inputs.publish_package \}\}'
        $promote | Should -Match 'TOOL_VERSION: \$\{\{ needs.build.outputs.tool-version \}\}'
        $promote | Should -Match 'Invoke-ContractPromotion'
        $promote | Should -Match '-Version \$env:TOOL_VERSION'
        $promote | Should -Not -Match 'Publish-SecurityMetadataTools|Get-SecurityMetadataToolsVersion|dotnet pack|ReleaseBuild'
        $script:dispatch | Should -Not -Match 'on-prerelease.yml|on-release.yml|nuget push.*Secrets'
    }
}

Describe 'SecurityMetadataTools shared SBOM generation' {
    BeforeAll {
        $script:sbomScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../New-SecurityMetadataToolsSbom.ps1'))
        function dotnet { throw "Unmocked dotnet: $args" }
        function sbom-tool {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseApprovedVerbs', '', Justification = 'Matches the external executable name for mocking.')]
            param()
            throw "Unmocked sbom-tool: $args"
        }
    }

    BeforeEach {
        $workspace = Join-Path $TestDrive ([guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $workspace | Out-Null
        Push-Location $workspace
        $script:packageFile = Join-Path $workspace 'EdFi.Api.SecurityMetadataTools.8.1.0-alpha.42.nupkg'
        Set-Content -LiteralPath $script:packageFile -Value 'downloaded package'
        $script:sha256 = (Get-FileHash -LiteralPath $script:packageFile).Hash.ToLowerInvariant()
        Mock dotnet { $global:LASTEXITCODE = 0 }
        Mock sbom-tool {
            New-Item -ItemType Directory -Path '_manifest/spdx_2.2' -Force | Out-Null
            Set-Content '_manifest/spdx_2.2/manifest.spdx.json' -Value 'generated manifest'
            $global:LASTEXITCODE = 0
        }
    }

    AfterEach { Pop-Location }

    It 'generates and copies the manifest for the verified package and supplied version' {
        & $script:sbomScript -PackageFile $script:packageFile -Version '8.1.0-alpha.42' -Sha256 $script:sha256

        (Get-Content 'buildfilelist.txt') | Should -BeExactly $script:packageFile
        Should -Invoke sbom-tool -Times 1 -Exactly -ParameterFilter {
            ($args -join '|') -ceq 'generate|-b|./|-bl|./buildfilelist.txt|-bc|./|-pn|EdFi.Api.SecurityMetadataTools|-pv|8.1.0-alpha.42|-nsb|https://ed-fi.org|-m|./|-ps|Ed-Fi Alliance'
        }
        (Get-Content 'manifest.spdx.json') | Should -BeExactly 'generated manifest'
        Should -Invoke dotnet -Times 1 -Exactly -ParameterFilter {
            ($args -join ' ') -ceq 'tool install --global Microsoft.Sbom.DotNetTool'
        }
    }

    It 'rejects a <Failure> package before installing or generating anything' -ForEach @(
        @{ Failure = 'missing'; Message = '*package was not found*' }
        @{ Failure = 'changed'; Message = '*differs from the build artifact*' }
    ) {
        if ($Failure -eq 'missing') { Remove-Item -LiteralPath $script:packageFile }
        else { Set-Content -LiteralPath $script:packageFile -Value 'changed bytes' }

        { & $script:sbomScript -PackageFile $script:packageFile -Version '8.1.0' -Sha256 $script:sha256 } |
            Should -Throw $Message
        Should -Invoke dotnet -Times 0 -Exactly
        Should -Invoke sbom-tool -Times 0 -Exactly
    }

    It 'stops when the tool installation fails' {
        Mock dotnet { $global:LASTEXITCODE = 1 }

        { & $script:sbomScript -PackageFile $script:packageFile -Version '8.1.0' -Sha256 $script:sha256 } |
            Should -Throw '*SBOM tool installation failed*'
        Should -Invoke sbom-tool -Times 0 -Exactly
    }

    It 'does not copy a manifest when generation fails' {
        Mock sbom-tool { $global:LASTEXITCODE = 1 }
        Mock Copy-Item { throw 'Must not copy a failed generation' }

        { & $script:sbomScript -PackageFile $script:packageFile -Version '8.1.0' -Sha256 $script:sha256 } |
            Should -Throw '*SBOM generation failed*'
        Should -Invoke Copy-Item -Times 0 -Exactly
    }
}
