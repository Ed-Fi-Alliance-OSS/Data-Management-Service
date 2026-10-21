# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

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
