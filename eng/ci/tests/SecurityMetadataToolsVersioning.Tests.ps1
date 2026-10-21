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
