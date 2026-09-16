# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The identity rules and canonicalizations the publish decision rests on.
#
# Two failure shapes are pinned throughout. A canonicalization that loses a distinction lets a
# changed contract republish under an unchanged version. A parser that repairs malformed input into
# a valid-looking value does the same thing while appearing to validate.

BeforeAll {
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))

    Import-Module (Join-Path $PSScriptRoot "../ContractPackageComparison.psm1") -Force

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-comparison-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    function New-XmlDocumentation {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param(
            [Parameter(Mandatory)][AllowEmptyString()][string] $Members
        )

        $path = Join-Path $script:fixtureRoot "doc-$([guid]::NewGuid().ToString('N')).xml"

        if (-not $PSCmdlet.ShouldProcess($path, "Write XML documentation")) {
            return $null
        }

        [System.IO.File]::WriteAllText($path, @"
<?xml version="1.0"?>
<doc>
    <assembly><name>EdFi.Api.TestContract</name></assembly>
    <members>
$Members
    </members>
</doc>
"@)

        return $path
    }

    function New-Nuspec {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param(
            [Parameter(Mandatory)][AllowEmptyString()][string] $Dependencies
        )

        $path = Join-Path $script:fixtureRoot "spec-$([guid]::NewGuid().ToString('N')).nuspec"

        if (-not $PSCmdlet.ShouldProcess($path, "Write nuspec")) {
            return $null
        }

        [System.IO.File]::WriteAllText($path, @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
    <metadata>
        <id>EdFi.Api.TestContract</id>
        <version>1.0.0</version>
$Dependencies
    </metadata>
</package>
"@)

        return $path
    }

    function Test-DocumentationChanged {
        [CmdletBinding()]
        [OutputType([bool])]
        param(
            [Parameter(Mandatory)][string] $Left,
            [Parameter(Mandatory)][string] $Right
        )

        $a = [string[]] @(ConvertTo-CanonicalXmlDocumentation -Path (New-XmlDocumentation -Members $Left))
        $b = [string[]] @(ConvertTo-CanonicalXmlDocumentation -Path (New-XmlDocumentation -Members $Right))

        return 0 -ne @(Compare-Object -ReferenceObject $a -DifferenceObject $b -CaseSensitive -SyncWindow 0).Count
    }

    function Test-DependencyChanged {
        [CmdletBinding()]
        [OutputType([bool])]
        param(
            [Parameter(Mandatory)][AllowEmptyString()][string] $Left,
            [Parameter(Mandatory)][AllowEmptyString()][string] $Right
        )

        $a = [string[]] @(ConvertTo-CanonicalDependencySet -NuspecPath (New-Nuspec -Dependencies $Left))
        $b = [string[]] @(ConvertTo-CanonicalDependencySet -NuspecPath (New-Nuspec -Dependencies $Right))

        return 0 -ne @(Compare-Object -ReferenceObject $a -DifferenceObject $b -CaseSensitive -SyncWindow 0).Count
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "ConvertTo-NormalizedPackageVersion uses NuGet's own parser" {
    It "normalizes a four-part version to the three-part form NuGet addresses it by" {
        ConvertTo-NormalizedPackageVersion -Version "1.0.0.0" | Should -BeExactly "1.0.0"
    }

    It "pads a two-part version" {
        ConvertTo-NormalizedPackageVersion -Version "1.0" | Should -BeExactly "1.0.0"
    }

    It "keeps a non-zero fourth segment, which is a different version" {
        ConvertTo-NormalizedPackageVersion -Version "1.0.0.4" | Should -BeExactly "1.0.0.4"
    }

    It "drops build metadata, which is not part of identity" {
        ConvertTo-NormalizedPackageVersion -Version "1.0.0+abc123" | Should -BeExactly "1.0.0"
    }

    It "folds a prerelease label to one comparison key regardless of case" {
        ConvertTo-NormalizedPackageVersion -Version "1.0.0-Beta" |
            Should -BeExactly (ConvertTo-NormalizedPackageVersion -Version "1.0.0-BETA")
    }

    It "keeps a prerelease distinct from its release" {
        ConvertTo-NormalizedPackageVersion -Version "1.0.0-beta" |
            Should -Not -BeExactly (ConvertTo-NormalizedPackageVersion -Version "1.0.0")
    }

    # A hand-rolled parser accepted this and returned 1.0.0, quietly repairing metadata into a value
    # that compares equal to a real version.
    # Valid shorthand NuGet accepts. The hand-rolled parser this replaced rejected it, which would
    # have failed a lane over a spelling NuGet resolves without complaint.
    It "accepts a single-segment version and normalizes it" {
        ConvertTo-NormalizedPackageVersion -Version "1" | Should -BeExactly "1.0.0"
    }

    It "rejects a dangling prerelease separator rather than repairing it" {
        { ConvertTo-NormalizedPackageVersion -Version "1.0.0-" } |
            Should -Throw -ExpectedMessage "*is not a package version NuGet accepts*"
    }

    It "rejects text that is not a version" {
        { ConvertTo-NormalizedPackageVersion -Version "bogus" } | Should -Throw
    }

    It "rejects an empty version" {
        { ConvertTo-NormalizedPackageVersion -Version "" } | Should -Throw
    }
}

Describe "ConvertTo-NormalizedVersionRange uses NuGet's own parser" {
    It "expands a bare version to the minimum-inclusive range NuGet resolves it as" {
        ConvertTo-NormalizedVersionRange -Range "1.0.0" |
            Should -BeExactly (ConvertTo-NormalizedVersionRange -Range "[1.0.0, )")
    }

    It "keeps an exact pin distinct from a minimum" {
        ConvertTo-NormalizedVersionRange -Range "[1.0.0]" |
            Should -Not -BeExactly (ConvertTo-NormalizedVersionRange -Range "1.0.0")
    }

    It "keeps a widened range distinct from a pin" {
        ConvertTo-NormalizedVersionRange -Range "[1.0.0]" |
            Should -Not -BeExactly (ConvertTo-NormalizedVersionRange -Range "[1.0.0, 2.0.0)")
    }

    It "treats an omitted range as the all-inclusive range" {
        ConvertTo-NormalizedVersionRange -Range "" | Should -BeExactly "(, )"
    }

    # A hand-rolled parser read this single-value exclusive range as an exact pin, turning invalid
    # metadata into a range that compares equal to a valid one.
    It "rejects a single-value exclusive range rather than reading it as a pin" {
        { ConvertTo-NormalizedVersionRange -Range "(1.0.0)" } |
            Should -Throw -ExpectedMessage "*is not a version range NuGet accepts*"
    }

    It "rejects an unclosed range" {
        { ConvertTo-NormalizedVersionRange -Range "[1.0.0" } | Should -Throw
    }
}

Describe "ConvertTo-NormalizedTargetFramework" {
    It "reduces two spellings of one framework to one" {
        ConvertTo-NormalizedTargetFramework -Framework ".NETCoreApp,Version=v10.0" |
            Should -BeExactly (ConvertTo-NormalizedTargetFramework -Framework "net10.0")
    }

    It "keeps two different frameworks apart" {
        ConvertTo-NormalizedTargetFramework -Framework "net10.0" |
            Should -Not -BeExactly (ConvertTo-NormalizedTargetFramework -Framework "net9.0")
    }

    It "reads an absent framework as the any-framework group" {
        ConvertTo-NormalizedTargetFramework -Framework "" | Should -BeExactly "(any)"
    }

    It "rejects a framework NuGet does not recognize" {
        { ConvertTo-NormalizedTargetFramework -Framework "not-a-framework" } | Should -Throw
    }
}

Describe "ConvertTo-CanonicalXmlDocumentation keeps distinctions a reader can see" {
    # Escaped text and real markup render identically under an XML-shaped serialization, and they
    # are different contracts: one shows an implementer angle brackets, the other shows bold.
    It "separates escaped text from the markup it looks like" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y"><summary>&lt;b&gt;must&lt;/b&gt;</summary></member>' `
            -Right '<member name="M:X.Y"><summary><b>must</b></summary></member>' |
            Should -BeTrue
    }

    It "separates content that imitates the serialization's own delimiters" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y"><summary>T{a}</summary></member>' `
            -Right '<member name="M:X.Y"><summary>a</summary></member>' |
            Should -BeTrue
    }

    # The default XmlDocument discards whitespace-only text nodes, under which both of these become
    # an empty <code> element.
    It "keeps two indentations of one code sample apart" {
        Test-DocumentationChanged `
            -Left "<member name=`"M:X.Y`"><example><code> </code></example></member>" `
            -Right "<member name=`"M:X.Y`"><example><code>  </code></example></member>" |
            Should -BeTrue
    }

    It "keeps the layout of a multi-line code sample" {
        Test-DocumentationChanged `
            -Left "<member name=`"M:X.Y`"><example><code>var x = 1;`n    var y = 2;</code></example></member>" `
            -Right "<member name=`"M:X.Y`"><example><code>var x = 1;`n        var y = 2;</code></example></member>" |
            Should -BeTrue
    }

    # The preserve request is inherited, so it governs content below the element that carries it.
    It "honours xml:space declared on the member itself" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y" xml:space="preserve"><summary>a  b</summary></member>' `
            -Right '<member name="M:X.Y" xml:space="preserve"><summary>a b</summary></member>' |
            Should -BeTrue
    }

    # A nearer xml:space="default" has to be able to reset a preserving ancestor. Inheriting the
    # ancestor's true with -or made the reset unreachable, so whitespace in a default context
    # demanded a version bump.
    It "honours an xml:space reset inside a preserving member" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y" xml:space="preserve"><summary xml:space="default">one two</summary></member>' `
            -Right '<member name="M:X.Y" xml:space="preserve"><summary xml:space="default">one  two</summary></member>' |
            Should -BeFalse
    }

    It "keeps preserving outside the reset element" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y" xml:space="preserve">a b<summary xml:space="default">one two</summary></member>' `
            -Right '<member name="M:X.Y" xml:space="preserve">a  b<summary xml:space="default">one two</summary></member>' |
            Should -BeTrue
    }

    # A sample is what an implementer copies, so nothing inside it is cosmetic. A nested element
    # asking for default treatment does not make the sample's own layout negotiable.
    It "preserves code content even when nested markup asks for default treatment" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y"><example><code><b xml:space="default">one two</b></code></example></member>' `
            -Right '<member name="M:X.Y"><example><code><b xml:space="default">one  two</b></code></example></member>' |
            Should -BeTrue
    }

    # The same rule with the review's own fixture shape, kept alongside the <b> case so the exact
    # probe that found this is pinned by name rather than only by equivalence.
    It "preserves code content wrapped in a span asking for default treatment" {
        Test-DocumentationChanged `
            -Left '<member name="T:C"><code><span xml:space="default">one two</span></code></member>' `
            -Right '<member name="T:C"><code><span xml:space="default">one  two</span></code></member>' |
            Should -BeTrue
    }

    It "still normalizes a default reset that is not inside a code sample" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y" xml:space="preserve"><summary><b xml:space="default">one two</b></summary></member>' `
            -Right '<member name="M:X.Y" xml:space="preserve"><summary><b xml:space="default">one  two</b></summary></member>' |
            Should -BeFalse
    }

    # A code sample is preserved on its own terms, so a reset above it does not reach inside.
    It "preserves a code sample declared under a reset" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y"><summary xml:space="default"><code>a b</code></summary></member>' `
            -Right '<member name="M:X.Y"><summary xml:space="default"><code>a  b</code></summary></member>' |
            Should -BeTrue
    }

    It "normalizes the same whitespace where preservation was not asked for" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y"><summary>a  b</summary></member>' `
            -Right '<member name="M:X.Y"><summary>a b</summary></member>' |
            Should -BeFalse
    }

    It "normalizes a re-wrapped comment" {
        Test-DocumentationChanged `
            -Left "<member name=`"M:X.Y`"><summary>a b</summary></member>" `
            -Right "<member name=`"M:X.Y`"><summary>a`n        b</summary></member>" |
            Should -BeFalse
    }

    It "sees a word changed only in case" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y"><summary>must not be null</summary></member>' `
            -Right '<member name="M:X.Y"><summary>must not be NULL</summary></member>' |
            Should -BeTrue
    }

    It "sees an attribute value change" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y"><summary><see cref="T:A" /></summary></member>' `
            -Right '<member name="M:X.Y"><summary><see cref="T:B" /></summary></member>' |
            Should -BeTrue
    }

    It "ignores attribute order" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.Y"><summary><see cref="T:A" langword="null" /></summary></member>' `
            -Right '<member name="M:X.Y"><summary><see langword="null" cref="T:A" /></summary></member>' |
            Should -BeFalse
    }

    It "ignores the order two members are declared in" {
        Test-DocumentationChanged `
            -Left '<member name="M:X.A"><summary>a</summary></member><member name="M:X.B"><summary>b</summary></member>' `
            -Right '<member name="M:X.B"><summary>b</summary></member><member name="M:X.A"><summary>a</summary></member>' |
            Should -BeFalse
    }

    It "rejects a file with no members rather than returning an empty list" {
        $path = New-XmlDocumentation -Members ""

        { ConvertTo-CanonicalXmlDocumentation -Path $path } |
            Should -Throw -ExpectedMessage "*declares no members*"
    }

    It "rejects two members sharing one name" {
        $path = New-XmlDocumentation -Members @'
        <member name="M:X.Y"><summary>first</summary></member>
        <member name="M:X.Y"><summary>second</summary></member>
'@

        { ConvertTo-CanonicalXmlDocumentation -Path $path } |
            Should -Throw -ExpectedMessage "*more than once*"
    }

    It "rejects a member with no name" {
        $path = New-XmlDocumentation -Members '<member><summary>anonymous</summary></member>'

        { ConvertTo-CanonicalXmlDocumentation -Path $path } |
            Should -Throw -ExpectedMessage "*no name*"
    }

    It "rejects a file that is not well-formed" {
        $path = Join-Path $script:fixtureRoot "broken-$([guid]::NewGuid().ToString('N')).xml"
        [System.IO.File]::WriteAllText($path, "<doc><members>")

        { ConvertTo-CanonicalXmlDocumentation -Path $path } |
            Should -Throw -ExpectedMessage "*not well-formed*"
    }
}

Describe "ConvertTo-OrdinalOrder and ConvertTo-CanonicalAssetSet" {
    # Sort-Object orders by the current culture even with -CaseSensitive, so a canonical list ordered
    # through it can come out differently on two machines and a paired comparison then reports a
    # collation difference as a contract change.
    It "orders ordinally rather than by culture" {
        $ordered = ConvertTo-OrdinalOrder -Value @("b", "A", "a", "B")

        ($ordered -join ",") | Should -BeExactly "A,B,a,b"
    }

    It "returns an empty array rather than null for an empty input" {
        $ordered = ConvertTo-OrdinalOrder -Value @()

        $ordered.Count | Should -Be 0
        $null -eq $ordered | Should -BeFalse
    }

    It "reads a reordered asset set as the same set" {
        ConvertTo-CanonicalAssetSet -Assets "compile,runtime" |
            Should -BeExactly (ConvertTo-CanonicalAssetSet -Assets "runtime, compile")
    }

    It "reads two spellings of one asset group as one" {
        ConvertTo-CanonicalAssetSet -Assets "Compile" |
            Should -BeExactly (ConvertTo-CanonicalAssetSet -Assets "compile")
    }

    It "reads a repeated asset token as one" {
        ConvertTo-CanonicalAssetSet -Assets "compile,compile" |
            Should -BeExactly (ConvertTo-CanonicalAssetSet -Assets "compile")
    }

    It "keeps a genuinely different asset set apart" {
        ConvertTo-CanonicalAssetSet -Assets "compile" |
            Should -Not -BeExactly (ConvertTo-CanonicalAssetSet -Assets "compile,runtime")
    }

    It "reads an empty attribute as no assets" {
        ConvertTo-CanonicalAssetSet -Assets "" | Should -BeExactly ""
    }
}

Describe "ConvertTo-CanonicalDependencySet" {
    It "reads the grouped and bare layouts of one dependency as one set" {
        Test-DependencyChanged `
            -Left '<dependencies><dependency id="A" version="1.0.0" /></dependencies>' `
            -Right '<dependencies><group><dependency id="A" version="1.0.0" /></group></dependencies>' |
            Should -BeFalse
    }

    # NuGet selects the single best-matching group, so an empty group for a specific framework gives
    # a consumer on that framework no dependencies, where removing the group entirely would let a
    # fallback group's dependencies apply instead.
    It "sees an empty group added beside a populated one" {
        Test-DependencyChanged `
            -Left '<dependencies><group targetFramework="net9.0"><dependency id="A" version="1.0.0" /></group></dependencies>' `
            -Right '<dependencies><group targetFramework="net9.0"><dependency id="A" version="1.0.0" /></group><group targetFramework="net10.0" /></dependencies>' |
            Should -BeTrue
    }

    It "sees an empty group removed" {
        Test-DependencyChanged `
            -Left '<dependencies><group targetFramework="net10.0" /></dependencies>' `
            -Right '<dependencies></dependencies>' |
            Should -BeTrue
    }

    It "reads two spellings of one target framework as one group" {
        Test-DependencyChanged `
            -Left '<dependencies><group targetFramework="net10.0"><dependency id="A" version="1.0.0" /></group></dependencies>' `
            -Right '<dependencies><group targetFramework=".NETCoreApp,Version=v10.0"><dependency id="A" version="1.0.0" /></group></dependencies>' |
            Should -BeFalse
    }

    It "sees a dependency move between frameworks" {
        Test-DependencyChanged `
            -Left '<dependencies><group targetFramework="net10.0"><dependency id="A" version="1.0.0" /></group></dependencies>' `
            -Right '<dependencies><group targetFramework="net9.0"><dependency id="A" version="1.0.0" /></group></dependencies>' |
            Should -BeTrue
    }

    It "sees include and exclude assets change" {
        Test-DependencyChanged `
            -Left '<dependencies><dependency id="A" version="1.0.0" /></dependencies>' `
            -Right '<dependencies><dependency id="A" version="1.0.0" exclude="compile" /></dependencies>' |
            Should -BeTrue
    }

    It "reads two spellings of one package id as one dependency" {
        Test-DependencyChanged `
            -Left '<dependencies><dependency id="Example.Package" version="1.0.0" /></dependencies>' `
            -Right '<dependencies><dependency id="example.package" version="1.0.0" /></dependencies>' |
            Should -BeFalse
    }

    It "reads a reordered exclude attribute as the same dependency" {
        Test-DependencyChanged `
            -Left '<dependencies><dependency id="A" version="1.0.0" exclude="compile,runtime" /></dependencies>' `
            -Right '<dependencies><dependency id="A" version="1.0.0" exclude="runtime, compile" /></dependencies>' |
            Should -BeFalse
    }

    It "rejects a nuspec carrying more than one dependencies element" {
        $path = New-Nuspec -Dependencies '<dependencies><dependency id="A" version="1.0.0" /></dependencies><dependencies><dependency id="B" version="1.0.0" /></dependencies>'

        { ConvertTo-CanonicalDependencySet -NuspecPath $path } |
            Should -Throw -ExpectedMessage "*exactly one is allowed*"
    }

    It "rejects one framework declaring one package id twice" {
        $path = New-Nuspec -Dependencies '<dependencies><group targetFramework="net10.0"><dependency id="A" version="1.0.0" /><dependency id="A" version="2.0.0" /></group></dependencies>'

        { ConvertTo-CanonicalDependencySet -NuspecPath $path } |
            Should -Throw -ExpectedMessage "*more than once*"
    }

    It "allows one package id in two different frameworks" {
        $path = New-Nuspec -Dependencies '<dependencies><group targetFramework="net10.0"><dependency id="A" version="1.0.0" /></group><group targetFramework="net9.0"><dependency id="A" version="1.0.0" /></group></dependencies>'

        (ConvertTo-CanonicalDependencySet -NuspecPath $path).Count | Should -Be 2
    }

    It "rejects a dependency with no id" {
        $path = New-Nuspec -Dependencies '<dependencies><dependency version="1.0.0" /></dependencies>'

        { ConvertTo-CanonicalDependencySet -NuspecPath $path } |
            Should -Throw -ExpectedMessage "*no id*"
    }

    It "rejects a dependency whose range is malformed" {
        $path = New-Nuspec -Dependencies '<dependencies><dependency id="A" version="(1.0.0)" /></dependencies>'

        { ConvertTo-CanonicalDependencySet -NuspecPath $path } | Should -Throw
    }

    # Assigned directly rather than re-wrapped in @(), which is how the publish check consumes it.
    # These functions return their array through the unary comma so that an empty result survives as
    # an empty array instead of collapsing to $null; wrapping that again in @() nests it.
    It "returns an empty set for a nuspec declaring no dependencies" {
        $path = New-Nuspec -Dependencies ""
        $entries = ConvertTo-CanonicalDependencySet -NuspecPath $path

        $entries.Count | Should -Be 0
        $null -eq $entries | Should -BeFalse -Because "an empty array, not null, is what stops two broken packages comparing equal"
        $entries -is [string[]] | Should -BeTrue
    }
}
