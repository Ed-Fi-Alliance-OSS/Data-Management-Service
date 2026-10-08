# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# What Assert-CustomValidationPackage.ps1 says about the packed guide, pinned by making it fail.
#
# The assertions under test are that the packed readme IS the committed implementer guide rather than
# merely a file of that name, that it carries all three sample blocks, and that it and
# ICustomResourceValidator's own XML documentation agree on how a validator is delivered. A check of
# that shape can fail in two directions, and both are pinned here: it must refuse a stale, gutted, or
# contradicted guide, and it must NOT refuse the accurate prose that compiling a validator into a DMS
# build remains possible.
#
# Every negative case is built by repacking the genuine artifact with one thing changed. That is what
# keeps each case about the assertion it names: every other check in the verifier still runs against
# real content, so a failure can only be the one property the case mutated.
#
# Where a case needs to reach an assertion that a later one would mask, it passes -GuidePath at the
# mutated readme so the whole-file equality check is satisfied and the case can isolate what it is
# about. That is what the parameter is for.

# Everything is resolved in BeforeAll, and the cases needing the real artifact report themselves
# inconclusive at run time rather than carrying a -Skip: condition. Pester evaluates -Skip: during
# discovery, where nothing BeforeAll sets yet exists, so a skip condition here would read $null and
# skip on every run, which is the silent always-green outcome these cases exist to avoid.
BeforeAll {
    $script:verifier = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../Assert-CustomValidationPackage.ps1")
    )
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
    $script:packageId = "EdFi.Api.CustomValidation"
    $script:assemblyName = "EdFi.DataManagementService.CustomValidation"
    $script:guidePath = Join-Path $script:repositoryRoot "src/dms/core/$($script:assemblyName)/CUSTOM-VALIDATION.md"

    # The verifier is given a whole nupkg, so these cases need a real one. Any version of it will
    # do: the assertions under test read the readme and the XML documentation, neither of which
    # depends on the version, so the newest matching package on disk is used rather than a version
    # this file would have to keep in step with the build.
    $script:packedPackage = @(
        Get-ChildItem -LiteralPath $script:repositoryRoot -Filter "$($script:packageId).*.nupkg" -File |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
    ) | ForEach-Object { $_.FullName }
    $script:packedPackageAvailable = -not [string]::IsNullOrEmpty($script:packedPackage)

    # The version that package declares, read from its own file name. The verifier requires an
    # expected version and these cases are not about it: they repack the genuine artifact, so
    # handing back its own version satisfies that assertion rather than masking the guide assertions
    # underneath it. The version assertions have their own Describe below, on fixtures built to
    # disagree.
    $script:packedPackageVersion = if ($script:packedPackageAvailable) {
        [System.IO.Path]::GetFileNameWithoutExtension($script:packedPackage).Substring($script:packageId.Length + 1)
    }
    else {
        "0.0.0"
    }

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1435-cv-package-tests-$([guid]::NewGuid().ToString('N'))"
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

    # Repacks the genuine artifact with the readme and/or the XML documentation replaced. Returns
    # both the package path and, for convenience, the readme text it was given, so a case can pass
    # that same text as -GuidePath.
    function New-RepackedPackage {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)][string] $Name,
            [string] $ReadmeContent,
            [scriptblock] $TransformXmlDocumentation
        )

        $stage = Join-Path $script:fixtureRoot "$Name-stage-$([guid]::NewGuid().ToString('N'))"
        $packagePath = Join-Path $script:fixtureRoot "$Name-$([guid]::NewGuid().ToString('N')).nupkg"

        if (-not $PSCmdlet.ShouldProcess($packagePath, "Repack the custom-validation package")) {
            return $null
        }

        Expand-Archive -LiteralPath $script:packedPackage -DestinationPath $stage

        if ($PSBoundParameters.ContainsKey("ReadmeContent")) {
            [System.IO.File]::WriteAllText((Join-Path $stage "CUSTOM-VALIDATION.md"), $ReadmeContent)
        }

        if ($null -ne $TransformXmlDocumentation) {
            $xmlPath = Join-Path $stage "lib/net10.0/$($script:assemblyName).xml"
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
            [string] $GuidePath = $script:guidePath
        )

        # A fresh unique extraction directory per invocation, never a directory holding anything a
        # caller owns.
        $extractTo = Join-Path (New-FixtureDirectory -Name "extract") "package"

        try {
            & $script:verifier -PackageFile $PackageFile -ExtractTo $extractTo `
                -PackageId $script:packageId -ExpectedPackageVersion $script:packedPackageVersion `
                -GuidePath $GuidePath | Out-Null
            return [pscustomobject]@{ Threw = $false; Message = "" }
        }
        catch {
            return [pscustomobject]@{ Threw = $true; Message = $_.Exception.Message }
        }
    }

    function Test-PackedPackageAvailable {
        if (-not $script:packedPackageAvailable) {
            Set-ItResult -Inconclusive -Because "no $($script:packageId).*.nupkg is present; run ./build-dms.ps1 Package -PackageTarget CustomValidation to exercise these cases"
        }
    }

    # The fixtures the version assertions run on. They are synthesized rather than repacked from a
    # real artifact, so every case runs on a fresh checkout that has packed nothing: a case that
    # reports itself inconclusive when the lane has not packed yet is a case that can be green for
    # the wrong reason.
    #
    # Both version assertions exist because a command-line global property outranks a csproj
    # property. A pack lane that reintroduced -p:PackageVersion=$DMSVersion, or a build that stamped
    # an AssemblyVersion from the release-stamped props, would produce a package that every other
    # assertion in that script accepts: the id, the readme, the assembly name, the exported types
    # and the empty dependency set would all be exactly right, and the version an implementer
    # resolves would be the wrong one.
    $script:targetFramework = "net10.0"

    $script:versionFixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-cv-package-tests-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:versionFixtureRoot -Force | Out-Null

    # The exact surface the verifier's own expected-type list names. The fixture's XML documentation
    # declares these so that the type-surface assertion passes and the version assertions are the
    # only thing a failing case can be about.
    $script:contractTypes = @(
        "EdFi.DataManagementService.CustomValidation.CustomValidationFailure",
        "EdFi.DataManagementService.CustomValidation.CustomValidationFailure.OnPath",
        "EdFi.DataManagementService.CustomValidation.CustomValidationFailure.OnResource",
        "EdFi.DataManagementService.CustomValidation.CustomValidationOperation",
        "EdFi.DataManagementService.CustomValidation.ICustomResourceValidator",
        "EdFi.DataManagementService.CustomValidation.ValidatedResource",
        "EdFi.DataManagementService.CustomValidation.ValidatedResourceInfo",
        "EdFi.DataManagementService.CustomValidation.ValidationScope"
    )

    # Each fixture assembly gets its own type name. Add-Type compiles into this session, and a second
    # compilation declaring a type name already defined here fails rather than producing a file.
    $script:fixtureOrdinal = 0

    function New-FixtureAssembly {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param(
            [Parameter(Mandatory)][string] $Path,
            [Parameter(Mandatory)][string] $AssemblyVersion
        )

        if (-not $PSCmdlet.ShouldProcess($Path, "Compile fixture assembly")) {
            return $null
        }

        $script:fixtureOrdinal++
        $typeName = "ContractFixture$($script:fixtureOrdinal)"

        Add-Type -OutputAssembly $Path -OutputType Library -TypeDefinition @"
[assembly: System.Reflection.AssemblyVersion("$AssemblyVersion")]
namespace EdFi.DataManagementService.CustomValidation.Fixtures
{
    public class $typeName
    {
    }
}
"@

        return $Path
    }

    function New-FixturePackage {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param(
            # The version the nuspec declares.
            [Parameter(Mandatory)][string] $PackageVersion,

            # The version the packed assembly carries, four-part.
            [Parameter(Mandatory)][string] $AssemblyVersion
        )

        $stage = Join-Path $script:versionFixtureRoot "stage-$([guid]::NewGuid().ToString('N'))"
        $packagePath = Join-Path $script:versionFixtureRoot "$($script:packageId).$PackageVersion-$([guid]::NewGuid().ToString('N')).nupkg"

        if (-not $PSCmdlet.ShouldProcess($packagePath, "Build fixture package")) {
            return $null
        }

        $libFolder = Join-Path $stage "lib/$($script:targetFramework)"
        New-Item -ItemType Directory -Path $libFolder -Force | Out-Null

        $null = New-FixtureAssembly `
            -Path (Join-Path $libFolder "$($script:assemblyName).dll") `
            -AssemblyVersion $AssemblyVersion

        # The verifier reads ICustomResourceValidator's own summary to check that it and the guide
        # agree on plugin delivery, so that one member carries the allowlist key. The rest only have
        # to exist, for the type-surface assertion.
        $members = ($script:contractTypes | ForEach-Object {
                $summary = if ($_ -eq "$($script:assemblyName).ICustomResourceValidator") {
                    "Fixture. An implementation is loaded from a published plugin directory named in <c>Plugins:Allowed</c>."
                }
                else {
                    "Fixture."
                }

                "        <member name=`"T:$_`"><summary>$summary</summary></member>"
            }) -join "`n"

        [System.IO.File]::WriteAllText(
            (Join-Path $libFolder "$($script:assemblyName).xml"),
            @"
<?xml version="1.0"?>
<doc>
    <assembly><name>$($script:assemblyName)</name></assembly>
    <members>
$members
    </members>
</doc>
"@
        )

        # The committed implementer guide verbatim. The verifier holds the packed readme to that
        # guide, so a stub here would fail on the comparison before a version assertion was reached,
        # and these cases would stop being about the version.
        Copy-Item -LiteralPath $script:guidePath -Destination (Join-Path $stage "CUSTOM-VALIDATION.md")

        [System.IO.File]::WriteAllText(
            (Join-Path $stage "$($script:packageId).nuspec"),
            @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
    <metadata>
        <id>$($script:packageId)</id>
        <version>$PackageVersion</version>
        <title>Ed-Fi API Custom Validation Abstractions</title>
        <authors>Ed-Fi Alliance, LLC and contributors</authors>
        <license type="expression">Apache-2.0</license>
        <readme>CUSTOM-VALIDATION.md</readme>
        <projectUrl>https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service</projectUrl>
        <description>Public abstractions for implementing custom resource validation.</description>
    </metadata>
</package>
"@
        )

        # -LiteralPath, and per entry, for the reason the sibling suite records: a nupkg root can
        # carry [Content_Types].xml, whose square brackets are a character class to the wildcard form.
        $entries = @(Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { $_.FullName })
        Compress-Archive -LiteralPath $entries -DestinationPath $packagePath

        return $packagePath
    }

    function Invoke-VersionVerifier {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string] $PackageFile,
            [Parameter(Mandatory)][string] $ExpectedPackageVersion
        )

        & $script:verifier `
            -PackageFile $PackageFile `
            -ExtractTo (Join-Path $script:versionFixtureRoot "extract-$([guid]::NewGuid().ToString('N'))") `
            -PackageId $script:packageId `
            -ExpectedPackageVersion $ExpectedPackageVersion
    }
}

AfterAll {
    # A recursive force-delete of a computed path, guarded before it runs rather than trusted.
    # Each fixture root is built from GetTempPath plus a GUID, so in the normal case this is
    # obviously safe; the guard is for the abnormal ones, where BeforeAll threw before assigning it,
    # where the variable was somehow reassigned, or where GetTempPath returned something unexpected.
    # Without it, an empty or short value would make this a recursive delete of a directory nobody
    # chose.
    #
    # Containment is checked lexically on full paths with a trailing separator on the parent, which
    # is what stops "C:\Temp\x" from being read as inside "C:\Temp2". Ordinal comparison, because
    # this is about the string the delete would receive. The fixture root must be strictly inside the
    # temp directory and never the temp directory itself.
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $temporaryRootWithSeparator = $temporaryRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar

    foreach ($fixtureRoot in @($script:fixtureRoot, $script:versionFixtureRoot)) {
        if ([string]::IsNullOrWhiteSpace($fixtureRoot)) {
            continue
        }

        $resolvedFixtureRoot = [System.IO.Path]::GetFullPath($fixtureRoot)

        $isContained = $resolvedFixtureRoot.StartsWith(
            $temporaryRootWithSeparator, [StringComparison]::OrdinalIgnoreCase
        ) -and $resolvedFixtureRoot.Length -gt $temporaryRootWithSeparator.Length

        if (-not $isContained) {
            throw "Refusing to remove $resolvedFixtureRoot : the test fixture root must be a directory strictly inside $temporaryRoot. Nothing was deleted."
        }

        if (Test-Path -LiteralPath $resolvedFixtureRoot) {
            Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
        }
    }
}

Describe "Assert-CustomValidationPackage on the packed implementer guide" {

    It "admits the package the build produced" {
        Test-PackedPackageAvailable

        # The whole verifier, not merely the new assertions: this is what makes every case below
        # attributable to the one thing it changed.
        $result = Invoke-Verifier -PackageFile $script:packedPackage

        $result.Threw |
            Should -BeFalse -Because "the packed artifact must satisfy its own verifier: $($result.Message)"
    }

    It "refuses a readme that is not the committed guide, which is what a stale package looks like" {
        Test-PackedPackageAvailable

        # A guide edited after the package was packed produces exactly this: a readme that is a
        # former version of the committed file. Dropping one line stands in for that, and it is the
        # case a heading survey or a keyword list would pass.
        $committed = ([System.IO.File]::ReadAllText($script:guidePath)).Replace("`r`n", "`n")
        $stale = ($committed -split "`n" | Select-Object -Skip 1) -join "`n"

        $result = Invoke-Verifier -PackageFile (New-RepackedPackage -Name "stale" -ReadmeContent $stale)

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*is not the committed implementer guide*"
        $result.Message |
            Should -BeLike "*must be repacked*" -Because "a stale package is the likely cause and the message should say so"
    }

    It "reports where the packed readme first differs from the guide" {
        Test-PackedPackageAvailable

        $committed = ([System.IO.File]::ReadAllText($script:guidePath)).Replace("`r`n", "`n")
        $mutated = $committed -replace "(?m)^# Ed-Fi API Custom Validation Abstractions$", "# Something Else Entirely"

        $result = Invoke-Verifier -PackageFile (New-RepackedPackage -Name "mutated" -ReadmeContent $mutated)

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*First difference at line 1*"
        $result.Message | Should -BeLike "*Something Else Entirely*"
    }

    It "refuses a placeholder readme in place of the guide" {
        Test-PackedPackageAvailable

        $result = Invoke-Verifier -PackageFile (
            New-RepackedPackage -Name "placeholder" -ReadmeContent "# Ed-Fi API Custom Validation Abstractions`n`nA full implementer guide is planned.`n"
        )

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*is not the committed implementer guide*"
    }

    It "refuses a guide that has lost one of the three sample blocks" {
        Test-PackedPackageAvailable

        # -GuidePath points at the mutated text on purpose. Without that the whole-file equality
        # check fires first and this case would pass for the wrong reason, proving nothing about the
        # sample-block assertion it exists for.
        $withoutOptions = (
            ([System.IO.File]::ReadAllText($script:guidePath)).Replace("`r`n", "`n") -replace
            "<!-- embed: eng/verification/CustomValidatorPluginConsumer/StudentIdentityOptions\.cs#options -->",
            "<!-- the options sample used to be here -->"
        )
        $guideFixture = Join-Path (New-FixtureDirectory -Name "guide-without-options") "CUSTOM-VALIDATION.md"
        [System.IO.File]::WriteAllText($guideFixture, $withoutOptions)

        $result = Invoke-Verifier `
            -PackageFile (New-RepackedPackage -Name "without-options" -ReadmeContent $withoutOptions) `
            -GuidePath $guideFixture

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*StudentIdentityOptions.cs#options*"
        $result.Message | Should -BeLike "*does not compile where it is read*"
    }
}

Describe "Assert-CustomValidationPackage on the delivery path the two documents state" {

    It "refuses XML documentation that still denies runtime loading" {
        Test-PackedPackageAvailable

        # The sentence the plugin work made false, restored in the contract's own documentation while
        # the guide still describes plugin delivery. That disagreement is the regression this
        # assertion exists for, and it is invisible to every other check in the verifier.
        $result = Invoke-Verifier -PackageFile (
            New-RepackedPackage -Name "obsolete-xml" -TransformXmlDocumentation {
                param($xml)
                $xml -replace
                "An implementation is delivered as a plugin",
                "An implementation is compiled into the host deployment and is not loaded from a dropped-in assembly at runtime. Formerly it was delivered as a plugin"
            }
        )

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*ICustomResourceValidator's XML documentation*"
        $result.Message | Should -BeLike "*dropped-in assembly at runtime*"
    }

    It "refuses a guide that still denies runtime loading" {
        Test-PackedPackageAvailable

        $reverted = (
            ([System.IO.File]::ReadAllText($script:guidePath)).Replace("`r`n", "`n") -replace
            "(?m)^Compiling a validator into a DMS build remains possible and is not the documented route\.$",
            "A validator is compiled into the host deployment and is not loaded from a dropped-in assembly at runtime."
        )
        $guideFixture = Join-Path (New-FixtureDirectory -Name "guide-reverted") "CUSTOM-VALIDATION.md"
        [System.IO.File]::WriteAllText($guideFixture, $reverted)

        $result = Invoke-Verifier `
            -PackageFile (New-RepackedPackage -Name "obsolete-guide" -ReadmeContent $reverted) `
            -GuidePath $guideFixture

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*the packed CUSTOM-VALIDATION.md*"
        $result.Message | Should -BeLike "*dropped-in assembly at runtime*"
    }

    It "refuses the obsolete denial even when it wraps across lines" {
        Test-PackedPackageAvailable

        # The case the first version of this check missed, and the reason the comparison collapses
        # whitespace. Neither document controls where its sentences break: an XML doc comment wraps
        # at the source line, behind a leading '///', and a Markdown paragraph wraps at the column
        # limit. A revert written by a human therefore arrives with newlines between the words, so a
        # check matching the raw text would have caught only a denial written as one unbroken line,
        # which is the one shape a real revert would not take.
        $result = Invoke-Verifier -PackageFile (
            New-RepackedPackage -Name "wrapped-denial" -TransformXmlDocumentation {
                param($xml)
                $xml -replace
                "An implementation is delivered as a plugin",
                "An implementation is not`n            loaded from a dropped-in`n            assembly at runtime. Formerly it was delivered as a plugin"
            }
        )

        $result.Threw |
            Should -BeTrue -Because "a wrapped denial is the same claim as an unwrapped one"
        $result.Message | Should -BeLike "*ICustomResourceValidator's XML documentation*"
        $result.Message | Should -BeLike "*not loaded from a dropped-in assembly at runtime*"
    }

    It "still admits the accurate affirmative that a validator IS loaded at runtime" {
        Test-PackedPackageAvailable

        # The other half of the precision, and the failure the first version of this check actually
        # had: matching the bare noun phrase "dropped-in assembly at runtime" refused this sentence,
        # which is the true statement of plugin delivery and the exact opposite of the claim being
        # guarded against. A check that refuses the truth it exists to protect is worse than no
        # check, because the next author satisfies it by deleting an accurate sentence.
        $affirmative = (
            ([System.IO.File]::ReadAllText($script:guidePath)).Replace("`r`n", "`n") -replace
            "(?m)^Compiling a validator into a DMS build remains possible and is not the documented route\.$",
            "A validator is loaded from a dropped-in assembly at runtime. Compiling one into a DMS build remains possible and is not the documented route."
        )
        $guideFixture = Join-Path (New-FixtureDirectory -Name "guide-affirmative") "CUSTOM-VALIDATION.md"
        [System.IO.File]::WriteAllText($guideFixture, $affirmative)

        $result = Invoke-Verifier `
            -PackageFile (New-RepackedPackage -Name "affirmative" -ReadmeContent $affirmative) `
            -GuidePath $guideFixture

        $result.Threw |
            Should -BeFalse -Because "the affirmative is the delivery path, not the obsolete denial: $($result.Message)"
    }

    It "refuses a guide that never names the allowlist key" {
        Test-PackedPackageAvailable

        $withoutKey = (
            ([System.IO.File]::ReadAllText($script:guidePath)).Replace("`r`n", "`n").Replace(
                "Plugins:Allowed", "some configuration setting"
            )
        )
        $guideFixture = Join-Path (New-FixtureDirectory -Name "guide-without-key") "CUSTOM-VALIDATION.md"
        [System.IO.File]::WriteAllText($guideFixture, $withoutKey)

        $result = Invoke-Verifier `
            -PackageFile (New-RepackedPackage -Name "without-key" -ReadmeContent $withoutKey) `
            -GuidePath $guideFixture

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*never mentions Plugins:Allowed*"
    }

    It "still admits the accurate prose that compiling a validator in remains possible" {
        Test-PackedPackageAvailable

        # The over-reach guard, and the reason the obsolete claim is matched on its distinctive tail
        # rather than on the word "compiled". Both documents legitimately say compiling a validator
        # into a DMS build is possible and is not the documented route; a check that refused that
        # would be refusing the truth, and it would push the next author into deleting an accurate
        # sentence to get a green lane.
        $withMoreCompiledInProse = (
            ([System.IO.File]::ReadAllText($script:guidePath)).Replace("`r`n", "`n") -replace
            "(?m)^Compiling a validator into a DMS build remains possible and is not the documented route\.$",
            "Compiling a validator into a DMS build remains possible, is compiled into the host deployment when you do, and is not the documented route."
        )
        $guideFixture = Join-Path (New-FixtureDirectory -Name "guide-compiled-in-prose") "CUSTOM-VALIDATION.md"
        [System.IO.File]::WriteAllText($guideFixture, $withMoreCompiledInProse)

        $result = Invoke-Verifier `
            -PackageFile (New-RepackedPackage -Name "compiled-in-prose" -ReadmeContent $withMoreCompiledInProse) `
            -GuidePath $guideFixture

        $result.Threw |
            Should -BeFalse -Because "accurate prose about compiled-in delivery is not the obsolete claim: $($result.Message)"
    }
}

Describe "Assert-CustomValidationPackage control cases" {

    It "admits an unmodified repack, so every refusal above is attributable to its own mutation" {
        Test-PackedPackageAvailable

        # The control the negative cases depend on. Each of them repacks the genuine artifact with
        # one thing changed, which only isolates that change if repacking is itself harmless. This
        # goes through the same expand-and-recompress path and changes nothing, so a failure here
        # would mean the cases above are refusing the repack rather than the mutation.
        $result = Invoke-Verifier -PackageFile (
            New-RepackedPackage -Name "unmodified" -TransformXmlDocumentation { param($xml) $xml }
        )

        $result.Threw |
            Should -BeFalse -Because "repacking alone must not change the verifier's answer: $($result.Message)"
    }

    It "still refuses a missing package file before it touches anything" {
        $result = Invoke-Verifier -PackageFile (Join-Path $script:fixtureRoot "absent.nupkg")

        $result.Threw | Should -BeTrue
        $result.Message | Should -BeLike "*was not found*"
    }
}

Describe "Assert-CustomValidationPackage version assertions" {
    It "verifies a package whose declared version and assembly version agree with the expectation" {
        $package = New-FixturePackage -PackageVersion "1.0.0" -AssemblyVersion "1.0.0.0"

        $output = Invoke-VersionVerifier -PackageFile $package -ExpectedPackageVersion "1.0.0"

        $output | Should -Match "version 1\.0\.0"
        $output | Should -Match "AssemblyVersion 1\.0\.0\.0"
    }

    # The regression: a lane that puts the release version back on the pack command line.
    It "fails when the package declares a version other than the contract's own" {
        $package = New-FixturePackage -PackageVersion "8.1.0-pre.0.42" -AssemblyVersion "1.0.0.0"

        { Invoke-VersionVerifier -PackageFile $package -ExpectedPackageVersion "1.0.0" } |
            Should -Throw -ExpectedMessage "*expected 1.0.0, found 8.1.0-pre.0.42*"
    }

    It "names the DMS release in the failure, so the cause is in the message" {
        $package = New-FixturePackage -PackageVersion "8.1.0" -AssemblyVersion "1.0.0.0"

        { Invoke-VersionVerifier -PackageFile $package -ExpectedPackageVersion "1.0.0" } |
            Should -Throw -ExpectedMessage "*not on the DMS release*"
    }

    # The other half: the package version can be right while the assembly inside carries the
    # release-stamped value, which is the value the loader's preflight actually compares.
    It "fails when the packed assembly carries a version other than the package version" {
        $package = New-FixturePackage -PackageVersion "1.0.0" -AssemblyVersion "8.1.0.0"

        { Invoke-VersionVerifier -PackageFile $package -ExpectedPackageVersion "1.0.0" } |
            Should -Throw -ExpectedMessage "*expected 1.0.0.0, found 8.1.0.0*"
    }

    It "compares the assembly version against the release portion of a prerelease package version" {
        $package = New-FixturePackage -PackageVersion "1.1.0-beta.3" -AssemblyVersion "1.1.0.0"

        $output = Invoke-VersionVerifier -PackageFile $package -ExpectedPackageVersion "1.1.0-beta.3"

        $output | Should -Match "AssemblyVersion 1\.1\.0\.0"
    }

    # Read off the parsed command rather than by calling it without the parameter. An omitted
    # mandatory parameter prompts, and a prompt in a lane with no console is a hang rather than a
    # failure; the contract being pinned here is that no caller can default this version silently.
    It "requires the expected version rather than defaulting to whatever the package declares" {
        $mandatory = @(
            (Get-Command $script:verifier).Parameters['ExpectedPackageVersion'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] } |
                ForEach-Object { $_.Mandatory }
        )

        $mandatory | Should -Contain $true
    }
}
