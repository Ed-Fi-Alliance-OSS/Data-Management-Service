# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Regression cover for one property of Assert-PluginsPackage.ps1: it must never delete anything the
# caller owns.
#
# An earlier revision of that script inherited the sibling custom-validation verifier's guard, which
# treats "this directory contains a *.nuspec" as proof that a previous run of its own produced the
# directory, and then recursively deletes it. That is not proof of ownership. A caller pointing the
# script at a package source folder, or at any directory that happens to hold a nuspec, loses every
# other file in it. The behaviour is silent, and no assertion in the verifier itself would notice.
#
# The fix removed the destructive branch entirely, so this file pins the absence of that branch
# rather than a message. Every fixture below is purpose-created under the temp directory; nothing
# here points at the repository or at any directory a person owns.

# Everything is resolved in BeforeAll, and the admission cases below report themselves inconclusive
# at run time rather than carrying a -Skip: condition. Pester evaluates -Skip: during discovery,
# where nothing BeforeAll sets yet exists, so a skip condition here would read $null and skip on
# every run - which is exactly the silent, always-green outcome these cases exist to avoid.
BeforeAll {
    $script:verifier = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../Assert-PluginsPackage.ps1")
    )
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))

    Import-Module (Join-Path $script:repositoryRoot "package-helpers.psm1") -Force
    $script:contractVersion = Get-PluginsContractVersion

    # The real packed artifact, when the lane has already produced it. The admission cases use it so
    # that "permits verification" means the whole verifier ran, not merely that extraction was
    # allowed to start. The preservation cases deliberately do not depend on it: they are the actual
    # regression, and they must run on a fresh checkout that has packed nothing.
    $script:packedPackage = Join-Path $script:repositoryRoot "EdFi.Api.Plugins.$($script:contractVersion).nupkg"
    $script:packedPackageAvailable = Test-Path -LiteralPath $script:packedPackage

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1496-verifier-tests-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    # Stands in for the package under test in the preservation cases. The verifier checks that the
    # package file exists before it touches the extraction directory, so the guard is reached with
    # any file here; the cases below assert that nothing is removed, which happens before the
    # archive is ever opened.
    $script:placeholderPackage = Join-Path $script:fixtureRoot "placeholder.nupkg"
    Set-Content -LiteralPath $script:placeholderPackage -Value "not a real package" -NoNewline

    # SupportsShouldProcess because the name carries a state-changing verb and PSScriptAnalyzer's
    # PSUseShouldProcessForStateChangingFunctions requires it. The name is worth keeping: this
    # genuinely creates a directory, and a Get- prefix would say otherwise.
    function New-FixtureDirectory {
        [CmdletBinding(SupportsShouldProcess)]
        param([Parameter(Mandatory)][string] $Name)

        $path = Join-Path $script:fixtureRoot "$Name-$([guid]::NewGuid().ToString('N'))"

        if ($PSCmdlet.ShouldProcess($path, "Create fixture directory")) {
            New-Item -ItemType Directory -Path $path -Force | Out-Null
        }

        return $path
    }

    function Invoke-Verifier {
        param(
            [Parameter(Mandatory)][string] $PackageFile,
            [Parameter(Mandatory)][string] $ExtractTo
        )

        try {
            & $script:verifier -PackageFile $PackageFile -ExtractTo $ExtractTo `
                -PackageId "EdFi.Api.Plugins" -ExpectedPackageVersion $script:contractVersion |
                Out-Null
            return [pscustomobject]@{ Threw = $false; Message = "" }
        }
        catch {
            return [pscustomobject]@{ Threw = $true; Message = $_.Exception.Message }
        }
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "Assert-PluginsPackage extraction directory handling" {

    Context "A directory the caller owns is never emptied" {

        It "refuses a non-empty directory that contains an unrelated nuspec, and preserves both files" {
            # The exact shape that defeated the inherited guard: an unrelated nuspec made the
            # directory look like a previous extraction, and the sentinel beside it was deleted.
            $target = New-FixtureDirectory -Name "unrelated-nuspec"
            $strayNuspec = Join-Path $target "Unrelated.nuspec"
            $sentinel = Join-Path $target "keep-this.txt"
            Set-Content -LiteralPath $strayNuspec -Value "<package />" -NoNewline
            Set-Content -LiteralPath $sentinel -Value "irreplaceable" -NoNewline

            $result = Invoke-Verifier -PackageFile $script:placeholderPackage -ExtractTo $target

            $result.Threw | Should -BeTrue
            $result.Message | Should -BeLike "*never deletes caller content*"

            Test-Path -LiteralPath $sentinel |
                Should -BeTrue -Because "the verifier must never delete caller content"
            Test-Path -LiteralPath $strayNuspec |
                Should -BeTrue -Because "a nuspec the caller owns is caller content too"
            Get-Content -LiteralPath $sentinel -Raw |
                Should -Be "irreplaceable" -Because "the file must be untouched, not merely present"
        }

        It "refuses a non-empty directory with no nuspec in it, and preserves its contents" {
            $target = New-FixtureDirectory -Name "no-nuspec"
            $sentinel = Join-Path $target "keep-this-too.txt"
            Set-Content -LiteralPath $sentinel -Value "also irreplaceable" -NoNewline

            $result = Invoke-Verifier -PackageFile $script:placeholderPackage -ExtractTo $target

            $result.Threw | Should -BeTrue
            $result.Message | Should -BeLike "*never deletes caller content*"

            Get-Content -LiteralPath $sentinel -Raw | Should -Be "also irreplaceable"
        }

        It "refuses a directory whose only content is a subdirectory, and preserves it" {
            # Get-ChildItem without -Force misses hidden entries and a naive file-only check misses
            # directories, so a caller directory holding only a folder must still be refused.
            $target = New-FixtureDirectory -Name "subdirectory-only"
            $nested = Join-Path $target "nested"
            New-Item -ItemType Directory -Path $nested -Force | Out-Null
            $sentinel = Join-Path $nested "deep.txt"
            Set-Content -LiteralPath $sentinel -Value "nested content" -NoNewline

            $result = Invoke-Verifier -PackageFile $script:placeholderPackage -ExtractTo $target

            $result.Threw | Should -BeTrue
            $result.Message | Should -BeLike "*never deletes caller content*"

            Get-Content -LiteralPath $sentinel -Raw | Should -Be "nested content"
        }
    }

    Context "A dedicated directory is still accepted" {

        It "verifies a valid package into a directory that does not exist yet" {
            if (-not $script:packedPackageAvailable) {
                Set-ItResult -Inconclusive -Because "EdFi.Api.Plugins.$($script:contractVersion).nupkg is absent; run ./build-dms.ps1 Package -PackageTarget Plugins to exercise the admission path"
            }

            $target = Join-Path (New-FixtureDirectory -Name "nonexistent-parent") "created-by-the-verifier"
            Test-Path -LiteralPath $target | Should -BeFalse

            $result = Invoke-Verifier -PackageFile $script:packedPackage -ExtractTo $target

            $result.Threw |
                Should -BeFalse -Because "a fresh unique directory is the documented calling pattern: $($result.Message)"
            Test-Path -LiteralPath (Join-Path $target "EdFi.Api.Plugins.nuspec") | Should -BeTrue
        }

        It "verifies a valid package into an existing empty directory" {
            if (-not $script:packedPackageAvailable) {
                Set-ItResult -Inconclusive -Because "EdFi.Api.Plugins.$($script:contractVersion).nupkg is absent; run ./build-dms.ps1 Package -PackageTarget Plugins to exercise the admission path"
            }

            $target = New-FixtureDirectory -Name "existing-empty"

            $result = Invoke-Verifier -PackageFile $script:packedPackage -ExtractTo $target

            $result.Threw |
                Should -BeFalse -Because "an empty directory carries nothing that could be lost: $($result.Message)"
            Test-Path -LiteralPath (Join-Path $target "EdFi.Api.Plugins.nuspec") | Should -BeTrue
        }

    }
}
