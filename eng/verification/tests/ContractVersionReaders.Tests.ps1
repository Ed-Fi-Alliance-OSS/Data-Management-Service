# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The two contract version readers in package-helpers.psm1.
#
# Each contract declares its version in exactly one file, and every lane that needs that version
# calls one of these rather than repeating a literal. What is pinned here is the behaviour that
# makes the single declaration hold: the value comes from the file the compiler reads, a file that
# declares no version is an error rather than an empty string, and a second declaration cannot
# stringify into a version no package will ever carry.
#
# The repository's own two declarations are read as well, because a reader that agreed with a
# fixture and disagreed with the tree would leave every lane packing something else.

BeforeAll {
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))

    Import-Module (Join-Path $script:repositoryRoot "package-helpers.psm1") -Force

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-version-readers-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    function New-FixtureFile {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param(
            [Parameter(Mandatory)][string] $Name,
            [Parameter(Mandatory)][string] $Content
        )

        $path = Join-Path $script:fixtureRoot $Name

        if ($PSCmdlet.ShouldProcess($path, "Write fixture")) {
            Set-Content -LiteralPath $path -Value $Content -NoNewline
        }

        return $path
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "Get-CustomValidationContractVersion" {
    It "returns the version the repository's own contract csproj declares" {
        $declared = Get-CustomValidationContractVersion

        $csprojPath = Join-Path $script:repositoryRoot `
            "src/dms/core/EdFi.DataManagementService.CustomValidation/EdFi.DataManagementService.CustomValidation.csproj"
        $expected = ([xml] (Get-Content -LiteralPath $csprojPath -Raw)).SelectSingleNode(
            "//PropertyGroup/Version"
        ).InnerText.Trim()

        $declared | Should -BeExactly $expected
    }

    It "reads the version out of a supplied csproj" {
        $path = New-FixtureFile -Name "declared.csproj" -Content @"
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <Version>2.3.4</Version>
    </PropertyGroup>
</Project>
"@

        Get-CustomValidationContractVersion -ProjectPath $path | Should -BeExactly "2.3.4"
    }

    It "trims surrounding whitespace rather than returning it as part of the version" {
        $path = New-FixtureFile -Name "padded.csproj" -Content @"
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <Version>
            1.0.0
        </Version>
    </PropertyGroup>
</Project>
"@

        Get-CustomValidationContractVersion -ProjectPath $path | Should -BeExactly "1.0.0"
    }

    It "throws when the project file does not exist" {
        $missing = Join-Path $script:fixtureRoot "absent.csproj"

        { Get-CustomValidationContractVersion -ProjectPath $missing } |
            Should -Throw -ExpectedMessage "*does not exist*"
    }

    It "throws when the project declares no Version" {
        $path = New-FixtureFile -Name "undeclared.csproj" -Content @"
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
    </PropertyGroup>
</Project>
"@

        { Get-CustomValidationContractVersion -ProjectPath $path } |
            Should -Throw -ExpectedMessage "*declares no Version*"
    }

    It "throws when the project declares an empty Version" {
        $path = New-FixtureFile -Name "empty.csproj" -Content @"
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <Version>   </Version>
    </PropertyGroup>
</Project>
"@

        { Get-CustomValidationContractVersion -ProjectPath $path } |
            Should -Throw -ExpectedMessage "*declares no Version*"
    }

    # A second PropertyGroup is the shape that makes property access return an array and stringify
    # into something like "1.0.0 2.0.0", which no package would ever carry.
    It "throws rather than stringifying two declarations into one version" {
        $path = New-FixtureFile -Name "duplicated.csproj" -Content @"
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <Version>1.0.0</Version>
    </PropertyGroup>
    <PropertyGroup>
        <Version>2.0.0</Version>
    </PropertyGroup>
</Project>
"@

        { Get-CustomValidationContractVersion -ProjectPath $path } |
            Should -Throw -ExpectedMessage "*more than one*"
    }
}

Describe "Get-PluginsContractVersion" {
    It "returns the version the repository's own contract props declares" {
        $declared = Get-PluginsContractVersion

        $propsPath = Join-Path $script:repositoryRoot "src/plugins/Directory.Build.props"
        $expected = ([xml] (Get-Content -LiteralPath $propsPath -Raw)).SelectSingleNode(
            "//PropertyGroup/VersionPrefix"
        ).InnerText.Trim()

        $declared | Should -BeExactly $expected
    }

    It "reads the version out of a supplied props file" {
        $path = New-FixtureFile -Name "declared.props" -Content @"
<Project>
    <PropertyGroup>
        <VersionPrefix>4.5.6</VersionPrefix>
    </PropertyGroup>
</Project>
"@

        Get-PluginsContractVersion -PropsPath $path | Should -BeExactly "4.5.6"
    }

    It "throws when the props file does not exist" {
        $missing = Join-Path $script:fixtureRoot "absent.props"

        { Get-PluginsContractVersion -PropsPath $missing } |
            Should -Throw -ExpectedMessage "*does not exist*"
    }

    It "throws when the props file declares no VersionPrefix" {
        $path = New-FixtureFile -Name "undeclared.props" -Content @"
<Project>
    <PropertyGroup>
        <Product>Ed-Fi API</Product>
    </PropertyGroup>
</Project>
"@

        { Get-PluginsContractVersion -PropsPath $path } |
            Should -Throw -ExpectedMessage "*declares no VersionPrefix*"
    }

    It "throws rather than stringifying two declarations into one version" {
        $path = New-FixtureFile -Name "duplicated.props" -Content @"
<Project>
    <PropertyGroup>
        <VersionPrefix>1.0.0</VersionPrefix>
    </PropertyGroup>
    <PropertyGroup>
        <VersionPrefix>2.0.0</VersionPrefix>
    </PropertyGroup>
</Project>
"@

        { Get-PluginsContractVersion -PropsPath $path } |
            Should -Throw -ExpectedMessage "*more than one*"
    }
}

Describe "The two contracts version independently" {
    # The whole point of the second reader. If these ever collapsed onto one source, a contract's
    # version would move when the other contract's surface moved, and the loader's skew preflight
    # compares exactly these values.
    It "reads each contract's version from its own declaration file" {
        $pluginsPropsPath = Join-Path $script:repositoryRoot "src/plugins/Directory.Build.props"
        $customValidationProjectPath = Join-Path $script:repositoryRoot `
            "src/dms/core/EdFi.DataManagementService.CustomValidation/EdFi.DataManagementService.CustomValidation.csproj"

        Get-PluginsContractVersion -PropsPath $pluginsPropsPath | Should -Not -BeNullOrEmpty
        Get-CustomValidationContractVersion -ProjectPath $customValidationProjectPath |
            Should -Not -BeNullOrEmpty
    }

    It "declares a custom-validation version that is not the DMS assembly version" {
        $dmsPropsPath = Join-Path $script:repositoryRoot "src/dms/Directory.Build.props"
        $dmsDeclared = ([xml] (Get-Content -LiteralPath $dmsPropsPath -Raw)).SelectSingleNode(
            "//PropertyGroup/AssemblyVersion"
        )

        # The committed props carries AssemblyVersion; the build-stamped shape carries VersionPrefix
        # instead. Either way the contract's own version must not be that value.
        if ($null -ne $dmsDeclared) {
            Get-CustomValidationContractVersion | Should -Not -BeExactly $dmsDeclared.InnerText.Trim()
        }
    }
}
