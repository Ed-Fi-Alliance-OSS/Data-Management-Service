# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Assert-IdentityPackage.ps1 asserts on the id, the version, the license, the readme, the assembly,
# its AssemblyVersion, the exported type surface, five load-bearing documentation landmarks, and the
# dependency set of a packed EdFi.Api.Identity nupkg. The admission case below runs the whole script
# against the real packed artifact, and each negative control repacks that same artifact with exactly
# one thing changed, so a failure is attributable to the one property the case mutated rather than to
# repacking itself.
#
# Every negative control asserts on the specific error message the mutated branch produces, not
# merely that the script threw. A wrong `-ExpectedPackageVersion`, an extra exported type, and a
# stray dependency would each also be caught by some other unrelated exception if the fixture were
# built wrong, and a test that only checked "did it throw" would still go green in that case.
#
# The extra-type and dependency controls are built by editing the shipped XML documentation file and
# the nuspec directly, inside a repacked copy of the real artifact, rather than by recompiling the
# contract project with a real added type or a real PackageReference. The verifier itself only ever
# reads those two files - it does not reflect over the assembly for its type list, and it does not
# restore the package to see its dependency closure - so editing them directly exercises the exact
# code path a real rebuild would reach, without paying for a second compile and without touching the
# contract's own source.
BeforeAll {
    $script:verifier = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../Assert-IdentityPackage.ps1")
    )
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
    $script:packageId = "EdFi.Api.Identity"
    $script:assemblyName = "EdFi.DataManagementService.Identity"
    $script:targetFramework = "net10.0"

    Import-Module (Join-Path $script:repositoryRoot "package-helpers.psm1") -Force
    $script:contractVersion = Get-PluginsContractVersion -PropsPath (
        Join-Path $script:repositoryRoot "src/dms/core/$($script:assemblyName)/$($script:assemblyName).csproj"
    )

    # The real packed artifact, when the lane has already produced it. Admission and every negative
    # control below use it, because "the verifier refuses X" is only meaningful when every property
    # of the fixture other than X is exactly what the real package ships.
    $script:packedPackage = Join-Path $script:repositoryRoot "$($script:packageId).$($script:contractVersion).nupkg"
    $script:packedPackageAvailable = Test-Path -LiteralPath $script:packedPackage

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1514-identity-package-tests-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    # SupportsShouldProcess because the name carries a state-changing verb and PSScriptAnalyzer's
    # PSUseShouldProcessForStateChangingFunctions requires it.
    function New-FixtureDirectory {
        [CmdletBinding(SupportsShouldProcess)]
        param([Parameter(Mandatory)][string] $Name)

        $path = Join-Path $script:fixtureRoot "$Name-$([guid]::NewGuid().ToString('N'))"

        if ($PSCmdlet.ShouldProcess($path, "Create fixture directory")) {
            New-Item -ItemType Directory -Path $path -Force | Out-Null
        }

        return $path
    }

    # Repacks the genuine artifact with the nuspec and/or the shipped XML documentation replaced.
    # Everything else in the repack - the assembly, its AssemblyVersion, the readme, the metadata
    # this function does not touch - stays exactly what the real build produced, which is what keeps
    # each negative control attributable to its own transform.
    function New-RepackedPackage {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)][string] $Name,
            [scriptblock] $TransformNuspec,
            [scriptblock] $TransformXmlDocumentation
        )

        $stage = Join-Path $script:fixtureRoot "$Name-stage-$([guid]::NewGuid().ToString('N'))"
        $packagePath = Join-Path $script:fixtureRoot "$Name-$([guid]::NewGuid().ToString('N')).nupkg"

        if (-not $PSCmdlet.ShouldProcess($packagePath, "Repack the identity contract package")) {
            return $null
        }

        Expand-Archive -LiteralPath $script:packedPackage -DestinationPath $stage

        if ($null -ne $TransformNuspec) {
            $nuspecPath = Join-Path $stage "$($script:packageId).nuspec"
            $original = [System.IO.File]::ReadAllText($nuspecPath)
            [System.IO.File]::WriteAllText($nuspecPath, (& $TransformNuspec $original))
        }

        if ($null -ne $TransformXmlDocumentation) {
            $xmlPath = Join-Path $stage "lib/$($script:targetFramework)/$($script:assemblyName).xml"
            $original = [System.IO.File]::ReadAllText($xmlPath)
            [System.IO.File]::WriteAllText($xmlPath, (& $TransformXmlDocumentation $original))
        }

        # -LiteralPath, and per entry. A nupkg carries [Content_Types].xml at its root, and square
        # brackets are a character class to Compress-Archive's wildcard -Path, so globbing the stage
        # directory would silently drop that entry.
        $entries = @(Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { $_.FullName })
        Compress-Archive -LiteralPath $entries -DestinationPath $packagePath

        return $packagePath
    }

    function Invoke-Verifier {
        param(
            [Parameter(Mandatory)][string] $PackageFile,
            [string] $ExpectedPackageVersion = $script:contractVersion
        )

        # A fresh unique extraction directory per invocation, never a directory holding anything a
        # caller owns.
        $extractTo = Join-Path (New-FixtureDirectory -Name "extract") "package"

        try {
            & $script:verifier -PackageFile $PackageFile -ExtractTo $extractTo `
                -PackageId $script:packageId -ExpectedPackageVersion $ExpectedPackageVersion |
                Out-Null
            return [pscustomobject]@{ Threw = $false; Message = "" }
        }
        catch {
            return [pscustomobject]@{ Threw = $true; Message = $_.Exception.Message }
        }
    }

    function Test-PackedPackageAvailable {
        if (-not $script:packedPackageAvailable) {
            Set-ItResult -Inconclusive -Because "$($script:packageId).$($script:contractVersion).nupkg is absent; run ./build-dms.ps1 Package -PackageTarget Identity to exercise these cases"
        }
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "Assert-IdentityPackage on the packed contract" {

    It "admits the package the build produced" {
        Test-PackedPackageAvailable

        $result = Invoke-Verifier -PackageFile $script:packedPackage

        $result.Threw |
            Should -BeFalse -Because "the packed artifact must satisfy its own verifier: $($result.Message)"
    }

    It "admits an unmodified repack, so every refusal below is attributable to its own mutation" {
        Test-PackedPackageAvailable

        # The control the negative cases depend on. Each of them repacks the genuine artifact with
        # one thing changed, which only isolates that change if repacking itself is harmless.
        $result = Invoke-Verifier -PackageFile (
            New-RepackedPackage -Name "unmodified" -TransformXmlDocumentation { param($xml) $xml }
        )

        $result.Threw |
            Should -BeFalse -Because "repacking alone must not change the verifier's answer: $($result.Message)"
    }
}

Describe "Assert-IdentityPackage negative controls" {

    It "refuses a wrong -ExpectedPackageVersion" {
        Test-PackedPackageAvailable

        $result = Invoke-Verifier -PackageFile $script:packedPackage -ExpectedPackageVersion "1.0.1"

        $result.Threw | Should -BeTrue
        $result.Message |
            Should -BeLike "*Unexpected package version: expected 1.0.1, found $($script:contractVersion)*"
        $result.Message |
            Should -BeLike "*identity contract is versioned on its own surface, not on the DMS release*"
    }

    It "refuses a package whose exported type surface carries one extra public type" {
        Test-PackedPackageAvailable

        # The verifier's type-surface check reads only the <member name="T:..."> entries in the
        # shipped XML file, so adding one there - without touching the compiled assembly at all -
        # reaches exactly the branch a real added public type would.
        $extraTypeName = "$($script:assemblyName).NegativeControlExtraType"
        $package = New-RepackedPackage -Name "extra-type" -TransformXmlDocumentation {
            param($xml)
            $xml -replace
            "</members>",
            @"
        <member name="T:$extraTypeName">
            <summary>A type that must never ship. Its presence here is the negative control itself.</summary>
        </member>
    </members>
"@
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*Published type surface changed*"
        $result.Message | Should -BeLike "*Unexpected: $extraTypeName*"
    }

    It "refuses a package that declares a dependency" {
        Test-PackedPackageAvailable

        # The verifier's dependency check reads only the nuspec's <dependencies> element, so adding a
        # <dependency> there - without restoring or compiling anything - reaches exactly the branch a
        # real added PackageReference would.
        $package = New-RepackedPackage -Name "with-dependency" -TransformNuspec {
            param($nuspec)
            $nuspec -replace
            '<group targetFramework="net10\.0" />',
            '<group targetFramework="net10.0"><dependency id="Newtonsoft.Json" version="13.0.3" /></group>'
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*Package must have no dependencies, found: Newtonsoft.Json*"
    }

    It "refuses a package whose shipped XML documentation has lost a load-bearing landmark" {
        Test-PackedPackageAvailable

        # One of the five phrases Assert-IdentityPackage.ps1 requires, replaced with prose that keeps
        # the surrounding sentence readable but drops the specific rule. The member this text lives
        # on is untouched, so this is purely the landmark check and not the type-surface check.
        #
        # Matched with \s+ between words rather than a literal string: the generated XML doc comment
        # wraps this phrase across two source lines, the same reason the verifier itself collapses
        # whitespace before comparing, and a literal match here would silently find nothing and
        # repack the phrase unchanged.
        $package = New-RepackedPackage -Name "missing-landmark" -TransformXmlDocumentation {
            param($xml)
            $xml -replace (
                "The\s+1024-character\s+ceiling\s+bounds\s+the\s+escaped\s+form\s+because\s+escaping" +
                "\s+only\s+ever\s+expands\s+a\s+token"
            ), "This rewrite no longer documents a ceiling on the escaped form"
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*no longer contains*load-bearing rule phrase*"
        $result.Message |
            Should -BeLike "*The 1024-character ceiling bounds the escaped form because escaping only ever expands a token*"
    }

    It "still refuses a missing package file before it touches anything" {
        $result = Invoke-Verifier -PackageFile (Join-Path $script:fixtureRoot "absent.nupkg")

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*was not found*"
    }
}
