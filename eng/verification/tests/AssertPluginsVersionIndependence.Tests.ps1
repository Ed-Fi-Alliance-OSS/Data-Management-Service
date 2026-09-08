# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Regression cover for Assert-PluginsVersionIndependence.ps1.
#
# Two properties are pinned here, and the second is the one an earlier revision got wrong.
#
# The check must not be vacuous: it has to fail when no stamping run happened, because otherwise its
# real assertion - that the plugin contract's props was untouched - passes trivially.
#
# And it must not claim ownership of a file it did not write. That revision decided "this is the
# build's output" from the presence of the generated-file marker comment. A developer can edit an
# already-stamped file and the marker survives, so the check accepted the edit, reported success,
# and restored the baseline over it. Both edit shapes that defeat a marker test are covered below:
# changing an existing property, and adding new content.
#
# Every fixture is a purpose-created repository under the temp directory, passed via -RepositoryRoot
# with a copy of the real build script. Nothing here writes into the working tree.

BeforeAll {
    $script:verifier = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../Assert-PluginsVersionIndependence.ps1")
    )
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
    $script:realBuildScript = Join-Path $script:repositoryRoot "build-dms.ps1"

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1496-versioncheck-tests-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    # A copy of the real build script travels with each fixture, so the expected stamped content the
    # verifier derives is the production template rather than a restatement of it.
    function New-FixtureRepository {
        [CmdletBinding(SupportsShouldProcess)]
        param([Parameter(Mandatory)][string] $Name)

        $root = Join-Path $script:fixtureRoot "$Name-$([guid]::NewGuid().ToString('N'))"

        if ($PSCmdlet.ShouldProcess($root, "Create fixture repository")) {
            New-Item -ItemType Directory -Path (Join-Path $root "src/plugins") -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $root "src/dms") -Force | Out-Null

            Copy-Item -LiteralPath (Join-Path $script:repositoryRoot "src/plugins/Directory.Build.props") `
                -Destination (Join-Path $root "src/plugins/Directory.Build.props")
            Copy-Item -LiteralPath (Join-Path $script:repositoryRoot "src/dms/Directory.Build.props") `
                -Destination (Join-Path $root "src/dms/Directory.Build.props")
            Copy-Item -LiteralPath $script:realBuildScript -Destination (Join-Path $root "build-dms.ps1")
        }

        return $root
    }

    # What a real stamping run leaves behind, produced the same way the verifier derives it, so the
    # fixtures exercise the production template rather than a hand-written approximation of it.
    function Get-StampedPropsContent {
        param([Parameter(Mandatory)][string] $Version)

        $scriptText = Get-Content -LiteralPath $script:realBuildScript -Raw
        $templateMatch = [regex]::Match(
            $scriptText,
            '(?s)Invoke-RegenerateFile\s+"\$solutionRoot/Directory\.Build\.props"\s+@"\r?\n(?<template>.*?)\r?\n"@'
        )
        $templateMatch.Success | Should -BeTrue -Because "the fixtures depend on the same template anchor the verifier uses"

        $maintainersMatch = [regex]::Match($scriptText, '\$maintainers\s*=\s*"(?<value>[^"]*)"')

        # Set-Variable rather than plain assignment: these are read only by the expander at runtime,
        # so as assignments they read to static analysis as dead stores.
        Set-Variable -Name "assembly_version" -Value $Version
        Set-Variable -Name "maintainers" -Value $maintainersMatch.Groups['value'].Value

        return $ExecutionContext.InvokeCommand.ExpandString($templateMatch.Groups['template'].Value)
    }

    function Set-StampedDmsPropsContent {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)][string] $Root,
            [Parameter(Mandatory)][string] $Version
        )

        $path = Join-Path $Root "src/dms/Directory.Build.props"

        if ($PSCmdlet.ShouldProcess($path, "Write stamped props")) {
            Set-Content -LiteralPath $path -Value (Get-StampedPropsContent -Version $Version) -NoNewline
        }
    }

    function Invoke-Check {
        param(
            [Parameter(Mandatory)][string] $Root,
            [Parameter(Mandatory)][string] $BaselineDirectory,
            [string] $ExpectedDMSVersion
        )

        $warnings = @()
        $threw = $false
        $message = ""

        try {
            $arguments = @{
                BaselineDirectory = $BaselineDirectory
                RepositoryRoot    = $Root
                BuildScriptPath   = (Join-Path $Root "build-dms.ps1")
                WarningVariable   = "+warnings"
                WarningAction     = "SilentlyContinue"
            }
            if (-not [string]::IsNullOrWhiteSpace($ExpectedDMSVersion)) {
                $arguments.ExpectedDMSVersion = $ExpectedDMSVersion
            }

            & $script:verifier @arguments | Out-Null
        }
        catch {
            $threw = $true
            $message = $_.Exception.Message
        }

        return [pscustomobject]@{
            Threw    = $threw
            Message  = $message
            Warnings = ($warnings | ForEach-Object { "$_" }) -join " | "
        }
    }

    function New-Baseline {
        [CmdletBinding(SupportsShouldProcess)]
        param([Parameter(Mandatory)][string] $Root)

        $baseline = Join-Path $Root ".baseline"

        if ($PSCmdlet.ShouldProcess($baseline, "Capture props baseline")) {
            & $script:verifier -BaselineDirectory $baseline -CaptureBaseline -RepositoryRoot $Root `
                -BuildScriptPath (Join-Path $Root "build-dms.ps1") | Out-Null
        }

        return $baseline
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "Assert-PluginsVersionIndependence" {

    Context "A file the check did not write is never overwritten" {

        It "rejects an edit to an already-stamped file that changed an existing property, and preserves it" {
            # The shape that defeated the marker test: still stamped, still carrying the generated
            # comment and the expected VersionPrefix, but with one property altered by a person.
            $root = New-FixtureRepository -Name "edited-existing-property"
            $baseline = New-Baseline -Root $root
            $props = Join-Path $root "src/dms/Directory.Build.props"

            Set-StampedDmsPropsContent -Root $root -Version "9.9.9"
            $edited = (Get-Content -LiteralPath $props -Raw) -replace
                "<Product>Ed-Fi API</Product>", "<Product>User edit made after version stamping</Product>"
            Set-Content -LiteralPath $props -Value $edited -NoNewline

            $result = Invoke-Check -Root $root -BaselineDirectory $baseline -ExpectedDMSVersion "9.9.9"

            $result.Threw | Should -BeTrue
            $result.Message | Should -BeLike "*does not match what SetDMSAssemblyInfo writes*"

            Get-Content -LiteralPath $props -Raw |
                Should -Be $edited -Because "the check must not overwrite an edit it did not make"
            $result.Warnings | Should -BeLike "*has no basis for claiming it*"
        }

        It "rejects an addition to an already-stamped file, and preserves it" {
            $root = New-FixtureRepository -Name "added-content"
            $baseline = New-Baseline -Root $root
            $props = Join-Path $root "src/dms/Directory.Build.props"

            Set-StampedDmsPropsContent -Root $root -Version "9.9.9"
            $edited = (Get-Content -LiteralPath $props -Raw) -replace
                "</PropertyGroup>", "    <NoWarn>NU1701</NoWarn>`n    </PropertyGroup>"
            Set-Content -LiteralPath $props -Value $edited -NoNewline

            $result = Invoke-Check -Root $root -BaselineDirectory $baseline -ExpectedDMSVersion "9.9.9"

            $result.Threw | Should -BeTrue
            Get-Content -LiteralPath $props -Raw | Should -Be $edited
        }

        It "rejects content that carries the generated marker but is not the generated output" {
            # A marker and one matching property were once enough. They must not be.
            $root = New-FixtureRepository -Name "marker-only"
            $baseline = New-Baseline -Root $root
            $props = Join-Path $root "src/dms/Directory.Build.props"

            $fake = @"
<Project>
    <!-- This file is generated by the build script. -->
    <PropertyGroup>
        <VersionPrefix>9.9.9</VersionPrefix>
    </PropertyGroup>
</Project>
"@
            Set-Content -LiteralPath $props -Value $fake -NoNewline

            $result = Invoke-Check -Root $root -BaselineDirectory $baseline -ExpectedDMSVersion "9.9.9"

            $result.Threw |
                Should -BeTrue -Because "a marker plus one property is not evidence that the build wrote this"
            Get-Content -LiteralPath $props -Raw | Should -Be $fake
        }

        It "rejects a file stamped to a version other than the one under test, and preserves it" {
            $root = New-FixtureRepository -Name "wrong-version"
            $baseline = New-Baseline -Root $root
            $props = Join-Path $root "src/dms/Directory.Build.props"

            Set-StampedDmsPropsContent -Root $root -Version "1.2.3"
            $stampedToOther = Get-Content -LiteralPath $props -Raw

            $result = Invoke-Check -Root $root -BaselineDirectory $baseline -ExpectedDMSVersion "9.9.9"

            $result.Threw | Should -BeTrue
            Get-Content -LiteralPath $props -Raw |
                Should -Be $stampedToOther -Because "the baseline is retained for explicit recovery rather than assumed"
        }
    }

    Context "The check cannot pass vacuously" {

        It "fails when no stamping run happened between capture and assert" {
            $root = New-FixtureRepository -Name "no-stamping"
            $baseline = New-Baseline -Root $root

            $result = Invoke-Check -Root $root -BaselineDirectory $baseline -ExpectedDMSVersion "9.9.9"

            $result.Threw | Should -BeTrue
            $result.Message | Should -BeLike "*proves nothing about version stamping*"
        }

        It "requires the expected version, without which the positive control cannot fire" {
            $root = New-FixtureRepository -Name "missing-version"
            $baseline = New-Baseline -Root $root

            $result = Invoke-Check -Root $root -BaselineDirectory $baseline

            $result.Threw | Should -BeTrue
            $result.Message | Should -BeLike "*-ExpectedDMSVersion is required*"
        }

        It "refuses a baseline directory that is not empty" {
            $root = New-FixtureRepository -Name "stale-baseline"
            $baseline = Join-Path $root ".stale"
            New-Item -ItemType Directory -Path $baseline -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $baseline "stale.txt") -Value "x" -NoNewline

            {
                & $script:verifier -BaselineDirectory $baseline -CaptureBaseline -RepositoryRoot $root `
                    -BuildScriptPath (Join-Path $root "build-dms.ps1")
            } | Should -Throw -ExpectedMessage "*it is not empty*"
        }

        It "fails loudly when the build script's template anchor no longer matches" {
            # The expected content is read out of the build script. If that template is moved or
            # rewritten, this check must stop rather than compare against nothing.
            $root = New-FixtureRepository -Name "template-moved"
            $baseline = New-Baseline -Root $root
            Set-StampedDmsPropsContent -Root $root -Version "9.9.9"
            Set-Content -LiteralPath (Join-Path $root "build-dms.ps1") -Value "# the template moved" -NoNewline

            $result = Invoke-Check -Root $root -BaselineDirectory $baseline -ExpectedDMSVersion "9.9.9"

            $result.Threw | Should -BeTrue
            $result.Message | Should -BeLike "*Could not locate the SetDMSAssemblyInfo props template*"
        }
    }

    Context "A genuine stamping run is accepted and cleaned up" {

        It "accepts the stamped output and restores the baseline" {
            $root = New-FixtureRepository -Name "happy-path"
            $props = Join-Path $root "src/dms/Directory.Build.props"
            $original = Get-Content -LiteralPath $props -Raw
            $baseline = New-Baseline -Root $root

            Set-StampedDmsPropsContent -Root $root -Version "9.9.9"

            $result = Invoke-Check -Root $root -BaselineDirectory $baseline -ExpectedDMSVersion "9.9.9"

            $result.Threw | Should -BeFalse -Because $result.Message
            Get-Content -LiteralPath $props -Raw | Should -Be $original
        }

        It "reports a stamped plugins props and still restores the DMS baseline" {
            # The defect the whole check exists to catch, with the DMS side genuinely the build's
            # output: the failure must be about the plugin contract, and cleanup must still happen.
            $root = New-FixtureRepository -Name "plugins-stamped"
            $dmsProps = Join-Path $root "src/dms/Directory.Build.props"
            $pluginsProps = Join-Path $root "src/plugins/Directory.Build.props"
            $originalDms = Get-Content -LiteralPath $dmsProps -Raw
            $baseline = New-Baseline -Root $root

            Set-StampedDmsPropsContent -Root $root -Version "9.9.9"
            (Get-Content -LiteralPath $pluginsProps -Raw) -replace
                "<VersionPrefix>1.0.0</VersionPrefix>", "<VersionPrefix>9.9.9</VersionPrefix>" |
                Set-Content -LiteralPath $pluginsProps -NoNewline

            $result = Invoke-Check -Root $root -BaselineDirectory $baseline -ExpectedDMSVersion "9.9.9"

            $result.Threw | Should -BeTrue
            $result.Message | Should -BeLike "*must stay outside SetDMSAssemblyInfo's reach*"
            Get-Content -LiteralPath $dmsProps -Raw |
                Should -Be $originalDms -Because "a failed assertion must not leave the tree stamped"
        }
    }
}
