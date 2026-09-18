# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The publish decision: publish when absent, skip when unchanged, fail when changed.
#
# Every case builds real .nupkg files from compiled assemblies and hands the script a fake feed, so
# the policy is exercised end to end with no network and no credential. What is pinned is the
# decision, not the wording of any one message, except where a message is itself the deliverable:
# a failure has to name the id, the version, which of the three comparisons differed, and what to
# do about it, or whoever hits it in a release lane cannot act on it.

BeforeAll {
    $script:checker = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../Invoke-ContractPublishCheck.ps1")
    )

    $script:packageId = "EdFi.Api.TestContract"
    $script:assemblyName = "EdFi.Api.TestContract"
    $script:targetFramework = "net10.0"

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-publish-check-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    $script:fixtureOrdinal = 0

    function New-ContractPackage {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param(
            [string] $Version = "1.0.0",

            # The public surface, as a C# class body.
            [string] $Body = "        public void Apply(int value) { }",

            # The XML documentation for that member, as the inner XML of its <member> element.
            [string] $Documentation = "<summary>Applies a value.</summary>",

            # The member name the documentation is attached to.
            [string] $MemberName = "M:EdFi.Api.TestContract.Contract.Apply(System.Int32)",

            # The <dependencies> element, or an empty string for none.
            [string] $Dependencies = "",

            [string] $Readme = "# Test contract",

            # Written verbatim as the nuspec's declared id, so a mismatch case can state one.
            [string] $DeclaredId = "",

            # Written verbatim as the nuspec's declared version, for the same reason.
            [string] $DeclaredVersion = "",

            # Omits the assembly, the XML file, or both.
            [switch] $WithoutAssembly,
            [switch] $WithoutDocumentation
        )

        $stage = Join-Path $script:fixtureRoot "stage-$([guid]::NewGuid().ToString('N'))"
        $packagePath = Join-Path $script:fixtureRoot "$($script:packageId).$Version-$([guid]::NewGuid().ToString('N')).nupkg"

        if (-not $PSCmdlet.ShouldProcess($packagePath, "Build contract package")) {
            return $null
        }

        $libraryFolder = Join-Path $stage "lib/$($script:targetFramework)"
        New-Item -ItemType Directory -Path $libraryFolder -Force | Out-Null

        if (-not $WithoutAssembly) {
            $script:fixtureOrdinal++

            Add-Type -OutputAssembly (Join-Path $libraryFolder "$($script:assemblyName).dll") -OutputType Library -TypeDefinition @"
#nullable enable
#pragma warning disable CS0067, CS0169, CS0649, CS0414

using System;
using System.Collections.Generic;

namespace EdFi.Api.TestContract
{
    public class Contract
    {
$Body
    }
}
"@
        }

        if (-not $WithoutDocumentation) {
            [System.IO.File]::WriteAllText(
                (Join-Path $libraryFolder "$($script:assemblyName).xml"),
                @"
<?xml version="1.0"?>
<doc>
    <assembly><name>$($script:assemblyName)</name></assembly>
    <members>
        <member name="$MemberName">$Documentation</member>
    </members>
</doc>
"@
            )
        }

        [System.IO.File]::WriteAllText((Join-Path $stage "README.md"), $Readme)

        $nuspecId = if ([string]::IsNullOrEmpty($DeclaredId)) { $script:packageId } else { $DeclaredId }
        $nuspecVersion = if ([string]::IsNullOrEmpty($DeclaredVersion)) { $Version } else { $DeclaredVersion }

        [System.IO.File]::WriteAllText(
            (Join-Path $stage "$($script:packageId).nuspec"),
            @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
    <metadata>
        <id>$nuspecId</id>
        <version>$nuspecVersion</version>
        <authors>Ed-Fi Alliance, LLC and contributors</authors>
        <description>A contract used by the publish-check fixtures.</description>
        <readme>README.md</readme>
$Dependencies
    </metadata>
</package>
"@
        )

        $entries = @(Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { $_.FullName })
        Compress-Archive -LiteralPath $entries -DestinationPath $packagePath

        return $packagePath
    }

    # A feed that answers from a table rather than over the network. Every default is the healthy
    # case, and each case overrides only the behaviour it is about.
    #
    # Every parameter below is read inside one of the three closures, and every closure parameter is
    # part of the seam's calling contract whether or not a particular fake consults it. The analyzer
    # does not follow either, so both are suppressed here with that reason rather than papered over
    # by deleting parameters the real implementations are called with.
    function Get-FakeFeed {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside GetNewClosure bodies, and the closure parameters are the injected seam signature.')]
        [CmdletBinding()]
        [OutputType([hashtable])]
        param(
            # The versions the package index reports, or $null for an id that was never published.
            [string[]] $Versions,

            [string] $ServiceIndexError = "",
            [string] $VersionIndexError = "",
            [string] $DownloadError = "",

            # The package the feed serves as the published one.
            [string] $PublishedPackage = "",

            # Reports a success without writing the file, which is the shape of a silently truncated
            # transfer.
            [switch] $DownloadNothing,

            # The first N version-index reads report the id absent, and only then does the listing
            # above appear: a healthy feed that has not yet indexed a fresh push.
            [int] $AbsentPolls = 0
        )

        $state = @{ VersionIndexReads = 0 }

        return @{
            State                     = $state
            ResolvePackageBaseAddress = {
                param([string] $IndexUrl, [string] $ApiKey)

                if ($ServiceIndexError.Length -gt 0) {
                    throw $ServiceIndexError
                }

                return "https://feed.invalid/flat2"
            }.GetNewClosure()

            GetPublishedVersions      = {
                param([string] $BaseAddress, [string] $NormalizedId, [string] $ApiKey)

                if ($VersionIndexError.Length -gt 0) {
                    throw $VersionIndexError
                }

                $state.VersionIndexReads++

                if ($null -eq $Versions -or $state.VersionIndexReads -le $AbsentPolls) {
                    return @{ Found = $false; Versions = @() }
                }

                return @{ Found = $true; Versions = $Versions }
            }.GetNewClosure()

            SavePublishedPackage      = {
                param(
                    [string] $BaseAddress,
                    [string] $NormalizedId,
                    [string] $NormalizedVersion,
                    [string] $Destination,
                    [string] $ApiKey
                )

                if ($DownloadError.Length -gt 0) {
                    throw $DownloadError
                }

                if ($DownloadNothing) {
                    return
                }

                Copy-Item -LiteralPath $PublishedPackage -Destination $Destination
            }.GetNewClosure()
        }
    }

    # A seam that ignores its arguments and returns a fixed value. The suppression lives here rather
    # than on each case because a seam's parameters are its calling contract: the real
    # implementations are invoked with all of them, so a fake that consults none still has to accept
    # them.
    function Get-ConstantSeam {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'A fake seam accepts the real seam signature whether or not it reads every argument.')]
        [CmdletBinding()]
        [OutputType([scriptblock])]
        param(
            [Parameter(Mandatory)][AllowNull()][AllowEmptyString()]
            $Value
        )

        return { param($One, $Two, $Three, $Four, $Five) return $Value }.GetNewClosure()
    }

    function Invoke-Check {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string] $PackageFile,
            [Parameter(Mandatory)][hashtable] $Feed,
            [string] $Version = "1.0.0",
            [string] $PackageId = "",
            [switch] $ConfirmPublished,
            [scriptblock] $Delay,
            [int] $SettleTimeoutSeconds = 60,
            [int] $SettleIntervalSeconds = 10
        )

        $id = if ([string]::IsNullOrEmpty($PackageId)) { $script:packageId } else { $PackageId }

        $arguments = @{
            PackageFile               = $PackageFile
            PackageId                 = $id
            PackageVersion            = $Version
            WorkingDirectory          = (Join-Path $script:fixtureRoot "work-$([guid]::NewGuid().ToString('N'))")
            ResolvePackageBaseAddress = $Feed.ResolvePackageBaseAddress
            GetPublishedVersions      = $Feed.GetPublishedVersions
            SavePublishedPackage      = $Feed.SavePublishedPackage
            SettleTimeoutSeconds      = $SettleTimeoutSeconds
            SettleIntervalSeconds     = $SettleIntervalSeconds
        }

        if ($ConfirmPublished) {
            $arguments.ConfirmPublished = $true
        }

        if ($null -ne $Delay) {
            $arguments.Delay = $Delay
        }

        return & $script:checker @arguments
    }

    # Records every wait the script asks for instead of sleeping, so a settle window runs in no time
    # and the case can assert how many polls happened.
    function Get-DelayRecorder {
        [CmdletBinding()]
        [OutputType([hashtable])]
        param()

        $waits = [System.Collections.Generic.List[int]]::new()

        return @{
            Waits  = $waits
            Script = {
                param([int] $Seconds)

                $waits.Add($Seconds)
            }.GetNewClosure()
        }
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "Invoke-ContractPublishCheck publishes when the version is absent" {
    It "pushes when the id has never been published" {
        $packed = New-ContractPackage
        $result = Invoke-Check -PackageFile $packed -Feed (Get-FakeFeed)

        $result.ShouldPush | Should -BeTrue
        $result.Reason | Should -BeExactly "absent"
    }

    It "pushes when the id exists but this version does not" {
        $packed = New-ContractPackage -Version "1.1.0"
        $result = Invoke-Check -PackageFile $packed -Version "1.1.0" -Feed (Get-FakeFeed -Versions @("1.0.0"))

        $result.ShouldPush | Should -BeTrue
    }

    It "does not download anything when the version is absent" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -DownloadError "the download seam must not be reached when the version is absent"

        { Invoke-Check -PackageFile $packed -Feed $feed } | Should -Not -Throw
    }
}

Describe "Invoke-ContractPublishCheck skips when the published version is unchanged" {
    It "skips cleanly when all three comparisons match" {
        $published = New-ContractPackage
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        $result = Invoke-Check -PackageFile $packed -Feed $feed

        $result.ShouldPush | Should -BeFalse
        $result.Reason | Should -BeExactly "unchanged"
    }

    # The readme is prose an implementer reads rather than something they compile or resolve
    # against, so a documentation fix must not force a version bump.
    It "skips when only the readme differs" {
        $published = New-ContractPackage -Readme "# Test contract"
        $packed = New-ContractPackage -Readme "# Test contract`n`nNow with an introduction."
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        (Invoke-Check -PackageFile $packed -Feed $feed).ShouldPush | Should -BeFalse
    }

    It "skips when a documentation comment is only re-wrapped" {
        $published = New-ContractPackage -Documentation "<summary>Applies a value to the resource.</summary>"
        $packed = New-ContractPackage -Documentation "<summary>Applies a value`n        to the resource.</summary>"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        (Invoke-Check -PackageFile $packed -Feed $feed).ShouldPush | Should -BeFalse
    }

    It "skips when only the indentation around block documentation elements differs" {
        $published = New-ContractPackage -Documentation "<summary>Applies <c>value</c>.</summary><remarks>Never throws.</remarks>"
        $packed = New-ContractPackage -Documentation "`n            <summary>`n            Applies <c>value</c>.`n            </summary>`n            <remarks>Never throws.</remarks>`n        "
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        (Invoke-Check -PackageFile $packed -Feed $feed).ShouldPush | Should -BeFalse
    }

    # Both spellings declare one dependency set, and calling them different would fail a publish
    # over a packaging detail rather than a contract change.
    It "skips when the same dependency is declared grouped and ungrouped" {
        $published = New-ContractPackage -Dependencies @"
        <dependencies>
            <dependency id="Microsoft.Extensions.Configuration.Abstractions" version="10.0.0" />
        </dependencies>
"@
        $packed = New-ContractPackage -Dependencies @"
        <dependencies>
            <group>
                <dependency id="Microsoft.Extensions.Configuration.Abstractions" version="10.0.0" />
            </group>
        </dependencies>
"@
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        (Invoke-Check -PackageFile $packed -Feed $feed).ShouldPush | Should -BeFalse
    }

    It "treats a four-part published version as the version it declares" {
        $published = New-ContractPackage -Version "1.0.0" -DeclaredVersion "1.0.0.0"
        $packed = New-ContractPackage -Version "1.0.0"
        $feed = Get-FakeFeed -Versions @("1.0.0.0") -PublishedPackage $published

        (Invoke-Check -PackageFile $packed -Feed $feed).ShouldPush | Should -BeFalse
    }
}

Describe "Invoke-ContractPublishCheck fails when the published version changed" {
    It "fails on a changed public surface, naming the id, the version and the bump" {
        $published = New-ContractPackage -Body "        public void Apply(int value) { }"
        $packed = New-ContractPackage -Body "        public void Apply(long value) { }"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*public surface*"

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*EdFi.Api.TestContract 1.0.0*"

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*Bump the contract's declared version*"
    }

    It "fails on a changed documentation comment" {
        $published = New-ContractPackage -Documentation "<summary>Applies a value.</summary>"
        $packed = New-ContractPackage -Documentation "<summary>Applies a value, or throws.</summary>"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*XML documentation*"
    }

    It "fails on a documentation change that is only a change of case" {
        $published = New-ContractPackage -Documentation "<summary>The value must not be null.</summary>"
        $packed = New-ContractPackage -Documentation "<summary>The value must not be NULL.</summary>"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*XML documentation*"
    }

    # The same text under a different element kind is different documentation: a rule stated for
    # a parameter is not the same rule stated for a type parameter.
    It "fails when documented text moves from param to typeparam under one name" {
        $published = New-ContractPackage -Documentation '<summary>Applies.</summary><param name="value">Must not be null.</param>'
        $packed = New-ContractPackage -Documentation '<summary>Applies.</summary><typeparam name="value">Must not be null.</typeparam>'
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*XML documentation*"
    }

    It "fails when the space between two inline elements is removed" {
        $published = New-ContractPackage -Documentation "<summary>Pass <c>a</c> <c>b</c>.</summary>"
        $packed = New-ContractPackage -Documentation "<summary>Pass <c>a</c><c>b</c>.</summary>"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*XML documentation*"
    }

    # An implementer copies the sample, so its layout is part of what ships.
    It "fails when whitespace inside a code sample changes" {
        $published = New-ContractPackage -Documentation "<example><code>var x = 1;`n    var y = 2;</code></example>"
        $packed = New-ContractPackage -Documentation "<example><code>var x = 1;`n        var y = 2;</code></example>"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*XML documentation*"
    }

    It "fails when a dependency's version range is widened" {
        $published = New-ContractPackage -Dependencies @"
        <dependencies>
            <group targetFramework="net10.0">
                <dependency id="Microsoft.Extensions.Configuration.Abstractions" version="[10.0.0]" />
            </group>
        </dependencies>
"@
        $packed = New-ContractPackage -Dependencies @"
        <dependencies>
            <group targetFramework="net10.0">
                <dependency id="Microsoft.Extensions.Configuration.Abstractions" version="[10.0.0, 11.0.0)" />
            </group>
        </dependencies>
"@
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*declared dependencies*"
    }

    It "fails when a dependency is added" {
        $published = New-ContractPackage
        $packed = New-ContractPackage -Dependencies @"
        <dependencies>
            <group targetFramework="net10.0">
                <dependency id="Newtonsoft.Json" version="13.0.0" />
            </group>
        </dependencies>
"@
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*declared dependencies*"
    }

    # include and exclude decide what a consumer actually inherits, not merely which version
    # resolves, so they are part of the entry.
    It "fails when a dependency's exclude assets change" {
        $published = New-ContractPackage -Dependencies @"
        <dependencies>
            <group targetFramework="net10.0">
                <dependency id="Microsoft.Extensions.Configuration.Abstractions" version="10.0.0" />
            </group>
        </dependencies>
"@
        $packed = New-ContractPackage -Dependencies @"
        <dependencies>
            <group targetFramework="net10.0">
                <dependency id="Microsoft.Extensions.Configuration.Abstractions" version="10.0.0" exclude="compile" />
            </group>
        </dependencies>
"@
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*declared dependencies*"
    }

    It "fails when the same dependency moves to a different target framework" {
        $published = New-ContractPackage -Dependencies @"
        <dependencies>
            <group targetFramework="net10.0">
                <dependency id="Microsoft.Extensions.Configuration.Abstractions" version="10.0.0" />
            </group>
        </dependencies>
"@
        $packed = New-ContractPackage -Dependencies @"
        <dependencies>
            <group targetFramework="net9.0">
                <dependency id="Microsoft.Extensions.Configuration.Abstractions" version="10.0.0" />
            </group>
        </dependencies>
"@
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*declared dependencies*"
    }
}

Describe "Invoke-ContractPublishCheck fails closed on a feed it cannot trust" {
    # The rule that matters most here: a feed that cannot be read is not an empty feed, and the next
    # thing that happens after "absent" is a push that burns the id.
    It "fails rather than publishing when the service index cannot be read" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -ServiceIndexError "the feed answered 401"

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*401*"
    }

    It "fails rather than publishing when the package index errors" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -VersionIndexError "the feed answered 503 for the package index"

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*503*"
    }

    # The listing and the fetch are the same feed a moment apart, so a disappearance is a fault.
    It "fails when the download errors after the version was listed" {
        $published = New-ContractPackage
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published -DownloadError "the feed answered 404 for the package"
        $packed = New-ContractPackage

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*404*"
    }

    It "fails when the download reports success but writes nothing" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -Versions @("1.0.0") -DownloadNothing

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*was not written*"
    }
}

Describe "Invoke-ContractPublishCheck validates what the feed actually returned" {
    # Not only that a seam threw. A feed can answer successfully with something that is not an
    # answer, and each of these would otherwise compose a request that fails in a way
    # indistinguishable from an absent package.
    It "refuses a package base address that is not absolute" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed
        $feed.ResolvePackageBaseAddress = Get-ConstantSeam -Value "flat2/"

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*not an absolute http or https address*"
    }

    It "refuses a package base address with a scheme the feed cannot serve" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed
        $feed.ResolvePackageBaseAddress = Get-ConstantSeam -Value "file:///C:/feed"

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*not an absolute http or https address*"
    }

    It "refuses an empty package base address" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed
        $feed.ResolvePackageBaseAddress = Get-ConstantSeam -Value ""

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*not an absolute http or https address*"
    }

    It "refuses a lookup result that reports presence with no versions" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed
        $feed.GetPublishedVersions = Get-ConstantSeam -Value @{ Found = $true; Versions = $null }

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*malformed response*"
    }

    It "refuses a lookup result that is not an answer at all" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed
        $feed.GetPublishedVersions = Get-ConstantSeam -Value $null

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*not an absent package*"
    }

    # Filtering an unusable entry away empties the list, an empty list reads as "this version is not
    # published", and that reads as a push.
    It "refuses a blank version entry rather than discarding it" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -Versions @(" ")

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*malformed version index*"
    }

    It "refuses a blank entry beside a valid one" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -Versions @("1.0.0", "")

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*malformed version index*"
    }

    It "refuses a lookup result whose Found is not a boolean" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed
        $feed.GetPublishedVersions = Get-ConstantSeam -Value @{ Found = "yes"; Versions = @() }

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*not a boolean*"
    }

    It "refuses a listed version that is not a version NuGet accepts" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -Versions @("1.0.0-")

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*is not a package version NuGet accepts*"
    }
}

Describe "Invoke-ContractPublishCheck validates both packages before comparing them" {
    It "fails when the packed package carries no assembly" {
        $published = New-ContractPackage
        $packed = New-ContractPackage -WithoutAssembly
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*0 assembly/assemblies*"
    }

    It "fails when the published package carries no XML documentation" {
        $published = New-ContractPackage -WithoutDocumentation
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*no XML documentation*"
    }

    # Two empty or broken packages must not read as one unchanged contract.
    It "fails when both packages carry no assembly" {
        $published = New-ContractPackage -WithoutAssembly
        $packed = New-ContractPackage -WithoutAssembly
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } | Should -Throw
    }

    It "fails when the packed package declares a different id" {
        $published = New-ContractPackage
        $packed = New-ContractPackage -DeclaredId "EdFi.Api.SomethingElse"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*declares id*"
    }

    It "fails when the packed package declares a different version" {
        $published = New-ContractPackage
        $packed = New-ContractPackage -DeclaredVersion "2.0.0"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*declares version*"
    }

    It "fails when the packed package does not exist" {
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage (New-ContractPackage)

        { Invoke-Check -PackageFile (Join-Path $script:fixtureRoot "absent.nupkg") -Feed $feed } |
            Should -Throw -ExpectedMessage "*was not found*"
    }

    It "refuses a working directory that already holds content" {
        $packed = New-ContractPackage
        $occupied = Join-Path $script:fixtureRoot "occupied-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $occupied -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $occupied "keep.txt") -Value "caller content" -NoNewline

        $feed = Get-FakeFeed

        {
            & $script:checker `
                -PackageFile $packed `
                -PackageId $script:packageId `
                -PackageVersion "1.0.0" `
                -WorkingDirectory $occupied `
                -ResolvePackageBaseAddress $feed.ResolvePackageBaseAddress `
                -GetPublishedVersions $feed.GetPublishedVersions `
                -SavePublishedPackage $feed.SavePublishedPackage
        } | Should -Throw -ExpectedMessage "*never deletes caller content*"

        Test-Path -LiteralPath (Join-Path $occupied "keep.txt") | Should -BeTrue
    }
}

Describe "Invoke-ContractPublishCheck compares ordinally" {
    # Compare-Object -CaseSensitive and -ceq both compare by culture, under which U+00AD (a soft
    # hyphen) is ignorable and "abc" equals "ab<U+00AD>c". A rule reworded with one would have read
    # as unchanged and republished silently.
    It "fails on a documentation difference that culture comparison ignores" {
        $published = New-ContractPackage -Documentation "<summary>The value must not be null.</summary>"
        $packed = New-ContractPackage -Documentation "<summary>The value must not be nu$([char]0x00AD)ll.</summary>"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed } |
            Should -Throw -ExpectedMessage "*XML documentation*"
    }

    It "fails on the same difference under a culture with different casing rules" {
        $published = New-ContractPackage -Documentation "<summary>The value must not be null.</summary>"
        $packed = New-ContractPackage -Documentation "<summary>The value must not be nu$([char]0x00AD)ll.</summary>"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        $previous = [System.Globalization.CultureInfo]::CurrentCulture

        try {
            [System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo("tr-TR")

            { Invoke-Check -PackageFile $packed -Feed $feed } |
                Should -Throw -ExpectedMessage "*XML documentation*"
        }
        finally {
            [System.Globalization.CultureInfo]::CurrentCulture = $previous
        }
    }

    # A positional walk with no set difference reported every line after an insertion as changed.
    It "reports one inserted surface line as one difference" {
        $published = New-ContractPackage -Body "        public void Apply(int value) { }`n        public void Zed() { }"
        $packed = New-ContractPackage -Body "        public void Apply(int value) { }`n        public void Middle() { }`n        public void Zed() { }"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        $message = ""

        try {
            Invoke-Check -PackageFile $packed -Feed $feed | Out-Null
        }
        catch {
            $message = $_.Exception.Message
        }

        $message | Should -BeLike "*public surface*"
        ([regex]::Matches($message, "only in the packed package")).Count | Should -Be 1
        ([regex]::Matches($message, "only in the published package")).Count | Should -Be 0
        $message | Should -BeLike "*Middle*"
    }
}

Describe "Invoke-ContractPublishCheck writes exactly one decision object" {
    It "writes one typed object on the absent path" {
        $packed = New-ContractPackage
        $objects = @(Invoke-Check -PackageFile $packed -Feed (Get-FakeFeed) 6>$null)

        $objects.Count | Should -Be 1
        $objects[0].PSObject.TypeNames[0] | Should -BeExactly "EdFi.ContractPublishDecision"
        $objects[0].Mode | Should -BeExactly "decide"
        $objects[0].ShouldPush | Should -BeOfType [bool]
    }

    It "writes one typed object on the unchanged path" {
        $published = New-ContractPackage
        $packed = New-ContractPackage
        $objects = @(Invoke-Check -PackageFile $packed -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published) 6>$null)

        $objects.Count | Should -Be 1
        $objects[0].Reason | Should -BeExactly "unchanged"
    }

    # A decision to push is not a publication. The push can still fail, so a caller that attached
    # evidence on the strength of ShouldPush would attest bytes that never reached the feed.
    It "never reports attach evidence from a decision to push" {
        $packed = New-ContractPackage
        $result = Invoke-Check -PackageFile $packed -Feed (Get-FakeFeed)

        $result.ShouldPush | Should -BeTrue
        $result.AttachEvidence | Should -BeFalse
        $result.PublishedBytesIdentical | Should -BeFalse
    }
}

Describe "Invoke-ContractPublishCheck confirms a publication" {
    It "confirms and attaches when the feed serves the packed file's exact bytes" {
        $published = New-ContractPackage
        $packed = Join-Path $script:fixtureRoot "identical-$([guid]::NewGuid().ToString('N')).nupkg"
        Copy-Item -LiteralPath $published -Destination $packed
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        $result = Invoke-Check -PackageFile $packed -Feed $feed -ConfirmPublished

        $result.Mode | Should -BeExactly "confirm"
        $result.Reason | Should -BeExactly "confirmed"
        $result.ShouldPush | Should -BeFalse
        $result.PublishedBytesIdentical | Should -BeTrue
        $result.AttachEvidence | Should -BeTrue
    }

    # Semantically the same contract, packed twice: two different archives. The feed's bytes are
    # not this run's, so this run's SBOM and provenance describe nothing anyone can restore.
    It "confirms without attaching when the feed serves an equal contract in different bytes" {
        $published = New-ContractPackage
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        $result = Invoke-Check -PackageFile $packed -Feed $feed -ConfirmPublished

        $result.Reason | Should -BeExactly "confirmed"
        $result.PublishedBytesIdentical | Should -BeFalse
        $result.AttachEvidence | Should -BeFalse
    }

    It "still refuses a differing contract in confirm mode" {
        $published = New-ContractPackage -Body "        public void Apply(int value) { }"
        $packed = New-ContractPackage -Body "        public void Apply(long value) { }"
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published

        { Invoke-Check -PackageFile $packed -Feed $feed -ConfirmPublished } |
            Should -Throw -ExpectedMessage "*public surface*"
    }

    It "waits for a healthy feed that has not yet listed the version" {
        $published = New-ContractPackage
        $packed = Join-Path $script:fixtureRoot "settled-$([guid]::NewGuid().ToString('N')).nupkg"
        Copy-Item -LiteralPath $published -Destination $packed
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $published -AbsentPolls 2
        $delay = Get-DelayRecorder

        $result = Invoke-Check -PackageFile $packed -Feed $feed -ConfirmPublished -Delay $delay.Script -SettleTimeoutSeconds 60 -SettleIntervalSeconds 10

        $result.AttachEvidence | Should -BeTrue
        ($delay.Waits -join ",") | Should -BeExactly "10,10"
        $feed.State.VersionIndexReads | Should -Be 3
    }

    It "fails when the version is still not listed at the end of the window" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -AbsentPolls 100
        $delay = Get-DelayRecorder

        { Invoke-Check -PackageFile $packed -Feed $feed -ConfirmPublished -Delay $delay.Script -SettleTimeoutSeconds 60 -SettleIntervalSeconds 10 } |
            Should -Throw -ExpectedMessage "*not listed on the feed after 60 seconds*"

        $delay.Waits.Count | Should -Be 6
    }

    It "rounds a window that is not a multiple of the interval up to one more poll" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -AbsentPolls 100
        $delay = Get-DelayRecorder

        { Invoke-Check -PackageFile $packed -Feed $feed -ConfirmPublished -Delay $delay.Script -SettleTimeoutSeconds 25 -SettleIntervalSeconds 10 } |
            Should -Throw

        $delay.Waits.Count | Should -Be 3
    }

    # Only a healthy feed that does not list the version is worth waiting for. An error is a feed that
    # cannot be trusted, and waiting would only turn it into a confirmed publication by patience.
    It "does not retry a package index that errors" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -VersionIndexError "the feed answered 401 for the package index"
        $delay = Get-DelayRecorder

        { Invoke-Check -PackageFile $packed -Feed $feed -ConfirmPublished -Delay $delay.Script } |
            Should -Throw -ExpectedMessage "*401*"

        $delay.Waits.Count | Should -Be 0
    }

    It "does not retry a malformed listing" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed
        $feed.GetPublishedVersions = Get-ConstantSeam -Value @{ Found = $true; Versions = $null }
        $delay = Get-DelayRecorder

        { Invoke-Check -PackageFile $packed -Feed $feed -ConfirmPublished -Delay $delay.Script } |
            Should -Throw -ExpectedMessage "*malformed response*"

        $delay.Waits.Count | Should -Be 0
    }

    It "does not retry an unreadable service index" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -ServiceIndexError "the feed answered 403"
        $delay = Get-DelayRecorder

        { Invoke-Check -PackageFile $packed -Feed $feed -ConfirmPublished -Delay $delay.Script } |
            Should -Throw -ExpectedMessage "*403*"

        $delay.Waits.Count | Should -Be 0
    }

    It "does not wait at all in decide mode" {
        $packed = New-ContractPackage
        $feed = Get-FakeFeed -AbsentPolls 100
        $delay = Get-DelayRecorder

        $result = Invoke-Check -PackageFile $packed -Feed $feed -Delay $delay.Script

        $result.ShouldPush | Should -BeTrue
        $delay.Waits.Count | Should -Be 0
    }
}
