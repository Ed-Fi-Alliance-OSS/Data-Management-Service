# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# What Assert-SecretsPackage.ps1 says about a packed EdFi.Api.Secrets, pinned by making it fail.
#
# The contract is packed here rather than looked for on disk. The lane that runs this directory packs
# only the DMS contracts, so a case that reported itself inconclusive without a secrets nupkg would be
# inconclusive on every run, which is the always-green outcome these cases exist to avoid. The
# contract has no dependencies, so the pack is a few seconds of compile.
#
# Every negative case repacks that genuine artifact with one thing changed, so every other check in
# the verifier still runs against real content and a failure can only be the property the case
# mutated. The unmodified repack in the control cases is what makes that attribution hold.

BeforeAll {
    $script:verifier = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Assert-SecretsPackage.ps1"))
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
    $script:packageId = "EdFi.Api.Secrets"
    $script:assemblyName = "EdFi.DmsConfigurationService.Secrets"
    $script:contractDirectory = Join-Path $script:repositoryRoot "src/config/contracts/$($script:assemblyName)"
    $script:guidePath = Join-Path $script:contractDirectory "README.md"

    Import-Module (Join-Path $script:repositoryRoot "package-helpers.psm1") -Force
    $script:packageVersion = Get-SecretsContractVersion

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1555-secrets-package-tests-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    $packOutput = Join-Path $script:fixtureRoot "pack"
    dotnet pack (Join-Path $script:contractDirectory "$($script:assemblyName).csproj") -c Release --nologo -o $packOutput | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack of $($script:packageId) failed."
    }
    $script:packedPackage = Join-Path $packOutput "$($script:packageId).$($script:packageVersion).nupkg"
    if (-not (Test-Path -LiteralPath $script:packedPackage)) {
        throw "Expected $($script:packedPackage) after packing."
    }

    # SupportsShouldProcess because the name carries a state-changing verb and PSScriptAnalyzer's
    # PSUseShouldProcessForStateChangingFunctions requires it.
    function New-RepackedPackage {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)][string] $Name,
            # Receives the extracted package directory and changes one thing in it.
            [Parameter(Mandatory)][scriptblock] $Mutate
        )

        $stage = Join-Path $script:fixtureRoot "$Name-stage-$([guid]::NewGuid().ToString('N'))"
        $packagePath = Join-Path $script:fixtureRoot "$Name-$([guid]::NewGuid().ToString('N')).nupkg"

        if (-not $PSCmdlet.ShouldProcess($packagePath, "Repack the secrets contract package")) {
            return $null
        }

        Expand-Archive -LiteralPath $script:packedPackage -DestinationPath $stage
        & $Mutate $stage

        # -LiteralPath, and per entry. A nupkg carries [Content_Types].xml at its root, and square
        # brackets are a character class to Compress-Archive's wildcard -Path.
        $entries = @(Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { $_.FullName })
        Compress-Archive -LiteralPath $entries -DestinationPath $packagePath

        return $packagePath
    }

    function Invoke-Verifier {
        param(
            [Parameter(Mandatory)][string] $PackageFile,
            [string] $GuidePath = $script:guidePath,
            [string] $ExpectedPackageVersion = $script:packageVersion
        )

        $extractTo = Join-Path $script:fixtureRoot "extract-$([guid]::NewGuid().ToString('N'))"

        try {
            $output = & $script:verifier -PackageFile $PackageFile -ExtractTo $extractTo `
                -PackageId $script:packageId -ExpectedPackageVersion $ExpectedPackageVersion `
                -GuidePath $GuidePath
            return [pscustomobject]@{ Threw = $false; Message = ($output -join "`n") }
        }
        catch {
            return [pscustomobject]@{ Threw = $true; Message = $_.Exception.Message }
        }
    }

    function Edit-Nuspec {
        param([Parameter(Mandatory)][string] $Stage, [Parameter(Mandatory)][scriptblock] $Transform)

        $path = Join-Path $Stage "$($script:packageId).nuspec"
        [System.IO.File]::WriteAllText($path, (& $Transform ([System.IO.File]::ReadAllText($path))))
    }
}

AfterAll {
    # Only ever a directory this file created under the temporary root.
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedFixtureRoot = [System.IO.Path]::GetFullPath($script:fixtureRoot)
    if ($resolvedFixtureRoot.StartsWith($temporaryRoot, [StringComparison]::Ordinal) -and
        $resolvedFixtureRoot -ne $temporaryRoot -and
        (Test-Path -LiteralPath $resolvedFixtureRoot)) {
        Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
    }
}

Describe "Assert-SecretsPackage control cases" {
    It "admits the package the build produced" {
        $result = Invoke-Verifier -PackageFile $script:packedPackage

        $result.Threw | Should -BeFalse -Because "the packed artifact must satisfy its own verifier: $($result.Message)"
        $result.Message | Should -Match "3 exported types"
    }

    It "admits an unmodified repack, so every refusal below is attributable to its own mutation" {
        $result = Invoke-Verifier -PackageFile (New-RepackedPackage -Name "unmodified" -Mutate { param($stage) })

        $result.Threw | Should -BeFalse -Because "repacking alone must not change the verifier's answer: $($result.Message)"
    }

    It "refuses a missing package file before it touches anything" {
        $result = Invoke-Verifier -PackageFile (Join-Path $script:fixtureRoot "absent.nupkg")

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*was not found*"
    }

    It "refuses to empty an extraction directory it did not create, and leaves it untouched" {
        $occupied = Join-Path $script:fixtureRoot "occupied-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $occupied | Out-Null
        $sentinel = Join-Path $occupied "keep.txt"
        Set-Content -LiteralPath $sentinel -Value "not a package extraction"

        {
            & $script:verifier -PackageFile $script:packedPackage -ExtractTo $occupied `
                -PackageId $script:packageId -ExpectedPackageVersion $script:packageVersion
        } | Should -Throw "*Refusing to empty*"

        Test-Path -LiteralPath $sentinel | Should -BeTrue
    }
}

Describe "Assert-SecretsPackage refusals" {
    It "refuses a package version other than the contract's own" {
        $result = Invoke-Verifier -PackageFile $script:packedPackage -ExpectedPackageVersion "9.9.9"

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*expected 9.9.9, found $($script:packageVersion)*"
    }

    It "refuses a public type the contract does not declare" {
        $package = New-RepackedPackage -Name "extra-type" -Mutate {
            param($stage)
            $xmlPath = Join-Path $stage "lib/net10.0/$($script:assemblyName).xml"
            $xml = [System.IO.File]::ReadAllText($xmlPath)
            [System.IO.File]::WriteAllText(
                $xmlPath,
                $xml.Replace("<members>", "<members><member name=`"T:$($script:assemblyName).AccidentallyPublic`"><summary>x</summary></member>")
            )
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*Unexpected: $($script:assemblyName).AccidentallyPublic*"
    }

    It "refuses a package that does not carry the XML documentation" {
        $package = New-RepackedPackage -Name "no-xml" -Mutate {
            param($stage)
            Remove-Item -LiteralPath (Join-Path $stage "lib/net10.0/$($script:assemblyName).xml")
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*XML documentation*"
    }

    It "refuses a dependency" {
        $package = New-RepackedPackage -Name "dependency" -Mutate {
            param($stage)
            Edit-Nuspec -Stage $stage -Transform {
                param($text)
                $text -replace '<group targetFramework="net10.0" />', '<group targetFramework="net10.0"><dependency id="Some.Library" version="1.0.0" /></group>'
            }
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*no dependencies, found: Some.Library*"
    }

    It "refuses a package without a description" {
        $package = New-RepackedPackage -Name "no-description" -Mutate {
            param($stage)
            Edit-Nuspec -Stage $stage -Transform { param($text) $text -replace '<description>[^<]*</description>', '' }
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*Missing package metadata: description*"
    }

    It "refuses a license expression other than Apache-2.0" {
        $package = New-RepackedPackage -Name "license" -Mutate {
            param($stage)
            Edit-Nuspec -Stage $stage -Transform { param($text) $text.Replace(">Apache-2.0</license>", ">MIT</license>") }
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*unexpected license expression: MIT*"
    }

    It "refuses a package without its <Element> metadata" -ForEach @(
        @{ Element = "title" }
        @{ Element = "projectUrl" }
    ) {
        $package = New-RepackedPackage -Name "no-$Element" -Mutate {
            param($stage)
            Edit-Nuspec -Stage $stage -Transform { param($text) $text -replace "<$Element>[^<]*</$Element>", "" }
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*Missing package metadata: $Element*"
    }

    # The nuspec is moved to a version the assembly was not built at, and the verifier is told to
    # expect it, so the package version check passes and only the AssemblyVersion check can refuse.
    It "refuses an assembly whose AssemblyVersion is not the package version" {
        $package = New-RepackedPackage -Name "assembly-version" -Mutate {
            param($stage)
            Edit-Nuspec -Stage $stage -Transform {
                param($text)
                $text.Replace("<version>$($script:packageVersion)</version>", "<version>9.9.9</version>")
            }
        }

        $result = Invoke-Verifier -PackageFile $package -ExpectedPackageVersion "9.9.9"

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*Unexpected AssemblyVersion*expected 9.9.9.0*"
    }

    It "refuses a readme that is not the committed guide, which is what a stale package looks like" {
        $package = New-RepackedPackage -Name "stale-readme" -Mutate {
            param($stage)
            Add-Content -LiteralPath (Join-Path $stage "README.md") -Value "`nA line the committed guide does not have."
        }

        $result = Invoke-Verifier -PackageFile $package

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*is not the committed implementer guide*"
    }

    It "refuses a guide that has lost a worked example, even when the guide itself lost it" {
        $claim = "eng/verification/SecretsPluginExamples/ParameterStoreSecretResolver.cs#resolver"
        $mutatedGuide = ([System.IO.File]::ReadAllText($script:guidePath)).Replace("<!-- embed: $claim -->", "")
        $mutatedGuidePath = Join-Path $script:fixtureRoot "guide-without-resolver.md"
        [System.IO.File]::WriteAllText($mutatedGuidePath, $mutatedGuide)

        $package = New-RepackedPackage -Name "lost-example" -Mutate {
            param($stage)
            [System.IO.File]::WriteAllText((Join-Path $stage "README.md"), $mutatedGuide)
        }

        # -GuidePath at the mutated text, so the whole-file comparison passes and only the claim
        # check can refuse it.
        $result = Invoke-Verifier -PackageFile $package -GuidePath $mutatedGuidePath

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*$claim*"
    }
}
