# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# What Get-ContractPublicSurface.ps1 treats as a change to a published contract, and what it does
# not.
#
# The publish lane republishes a contract at a version already on the feed and has to decide between
# "identical, skip cleanly" and "different, fail and bump". Every case below is one of the ways an
# externally consumable contract can change while the type list the packed-package verifiers compare
# stays byte-identical: a member added, a parameter type changed, a return type changed, an
# accessibility widened, a default value changed, an interface added, a constraint added, a variance
# annotation flipped, an indexer renamed.
#
# Each case compiles two real assemblies from C# and compares their surfaces. A fixture that asserted
# on a hand-written expected string would pin this script's output format; these pin its behaviour,
# which is the thing the publish decision rests on.
#
# Comparison is ordinal and case-sensitive throughout, because PowerShell's default string equality
# is neither, and under the default a rename differing only in case reads as no change at all.

BeforeAll {
    $script:reader = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../Get-ContractPublicSurface.ps1")
    )
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-surface-tests-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    # Compiles one fixture assembly and returns its surface. Both variants of a case share a
    # namespace so that the only differences in their output are the ones the case introduces.
    function Get-FixtureSurface {
        [CmdletBinding()]
        [OutputType([string[]])]
        param(
            [Parameter(Mandatory)][string] $Namespace,
            [Parameter(Mandatory)][string] $Body
        )

        $path = Join-Path $script:fixtureRoot "$Namespace-$([guid]::NewGuid().ToString('N')).dll"

        # Add-Type compiles each fixture on its own, so the nullable context a csproj would supply is
        # declared here, and the warnings a fixture provokes by design are disabled: an event nothing
        # raises and a field nothing assigns are exactly what a surface fixture is made of, and
        # Add-Type reports them as errors.
        Add-Type -OutputAssembly $path -OutputType Library -TypeDefinition @"
#nullable enable
#pragma warning disable CS0067, CS0169, CS0649, CS0414

using System;
using System.Collections.Generic;

namespace $Namespace
{
$Body
}
"@

        return [string[]] @(& $script:reader -AssemblyPath $path)
    }

    # $null when the two surfaces are ordinally identical, position by position, otherwise the lines
    # each side holds that the other does not. Ordinal is load bearing: the surface is sorted
    # ordinally, and both PowerShell's -ceq and Compare-Object -CaseSensitive compare by culture,
    # under which a case-only rename or an ignorable character reads as no change.
    # An empty surface is a legitimate result: an assembly whose only public type became internal
    # exports nothing an implementer can bind to, and that is precisely a change worth failing a
    # publish over. So both sides admit null and empty.
    function Compare-Surface {
        [CmdletBinding()]
        [OutputType([object[]])]
        param(
            [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][string[]] $Left,
            [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][string[]] $Right
        )

        $leftLines = [string[]] @($Left)
        $rightLines = [string[]] @($Right)

        if ([System.Linq.Enumerable]::SequenceEqual($leftLines, $rightLines, [System.StringComparer]::Ordinal)) {
            return $null
        }

        $leftSet = [System.Collections.Generic.HashSet[string]]::new($leftLines, [System.StringComparer]::Ordinal)
        $rightSet = [System.Collections.Generic.HashSet[string]]::new($rightLines, [System.StringComparer]::Ordinal)

        $differences = @(
            @($leftLines | Where-Object { -not $rightSet.Contains($_) } | ForEach-Object { "<= $_" }) +
            @($rightLines | Where-Object { -not $leftSet.Contains($_) } | ForEach-Object { "=> $_" })
        )

        if ($differences.Count -eq 0) {
            $differences = @("the same lines in a different order or count")
        }

        return , $differences
    }

    # Compiles a fixture and then corrupts one byte of it, so that a malformed-metadata case is a
    # real assembly the reader walks into rather than a hand-written stub it rejects early.
    #
    # The byte is located by searching for the exact DecimalConstantAttribute argument blob of the
    # value the fixture declares, which is why that value is distinctive. Finding anything other than
    # exactly one occurrence fails the case rather than corrupting an arbitrary byte.
    function New-CorruptedDecimalFixture {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param(
            [Parameter(Mandatory)][string] $Namespace,

            # Offset within the 18-byte blob to overwrite, and the byte to write there.
            [Parameter(Mandatory)][int] $Offset,
            [Parameter(Mandatory)][byte] $Value
        )

        $path = Join-Path $script:fixtureRoot "$Namespace-corrupt-$([guid]::NewGuid().ToString('N')).dll"

        if (-not $PSCmdlet.ShouldProcess($path, "Corrupt fixture")) {
            return $null
        }

        # The attribute is applied explicitly rather than written as `= 1.23m`, so the fixture
        # controls all three 32-bit words. A compiler-chosen value produces mostly zero bytes, and a
        # mostly-zero pattern matches at more than one alignment inside the blob heap: the search
        # then finds a single occurrence that is not the blob meant, and corrupting "the scale byte"
        # lands on some neighbour's payload instead.
        Add-Type -OutputAssembly $path -OutputType Library -TypeDefinition @"
#nullable enable
namespace $Namespace
{
    public class Contract
    {
        public void Apply(
            [System.Runtime.InteropServices.Optional]
            [System.Runtime.CompilerServices.DecimalConstant(0, 0, 0x0A0B0C0Du, 0x0E0F1011u, 0x12131415u)]
            decimal amount) { }
    }
}
"@

        # The three words, little-endian, as they sit in the blob after prolog, scale and sign.
        $anchor = [byte[]] @(
            0x0D, 0x0C, 0x0B, 0x0A,
            0x11, 0x10, 0x0F, 0x0E,
            0x15, 0x14, 0x13, 0x12
        )

        $bytes = [System.IO.File]::ReadAllBytes($path)

        # Not $matches: that is a PowerShell automatic variable, and assigning to it is both a
        # PSScriptAnalyzer finding and a way to have the regex engine overwrite the search results.
        $blobMatches = @()

        for ($index = 0; $index -le $bytes.Length - $anchor.Length; $index++) {
            $found = $true

            # $position, not $offset. PowerShell variable names are case-insensitive, so an inner
            # loop counter named $offset silently overwrites the $Offset parameter and every
            # corruption lands at whatever index the search loop happened to finish on.
            for ($position = 0; $position -lt $anchor.Length; $position++) {
                if ($bytes[$index + $position] -ne $anchor[$position]) {
                    $found = $false
                    break
                }
            }

            if ($found) {
                $blobMatches += $index
            }
        }

        if ($blobMatches.Count -ne 1) {
            throw "Expected exactly one DecimalConstantAttribute payload in $path, found $($blobMatches.Count). Refusing to corrupt an arbitrary byte."
        }

        # $Offset is relative to the prolog, which sits four bytes before the anchored words: two
        # bytes of prolog, then scale and sign.
        $bytes[$blobMatches[0] - 4 + $Offset] = $Value
        [System.IO.File]::WriteAllBytes($path, $bytes)

        return $path
    }

    # One case: two bodies in one namespace, and whether their surfaces should differ.
    function Test-SurfaceChange {
        [CmdletBinding()]
        [OutputType([bool])]
        param(
            [Parameter(Mandatory)][string] $Namespace,
            [Parameter(Mandatory)][string] $Before,
            [Parameter(Mandatory)][string] $After
        )

        $left = Get-FixtureSurface -Namespace $Namespace -Body $Before
        $right = Get-FixtureSurface -Namespace $Namespace -Body $After

        return $null -ne (Compare-Surface -Left $left -Right $right)
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "Get-ContractPublicSurface stability" {
    It "produces the same surface for the same assembly read twice" {
        $body = @"
    public abstract class Contract
    {
        public abstract string Name { get; }
        public virtual void Configure(IDictionary<string, int> options) { }
    }
"@
        $left = Get-FixtureSurface -Namespace "StableOne" -Body $body
        $right = Get-FixtureSurface -Namespace "StableOne" -Body $body

        Compare-Surface -Left $left -Right $right | Should -BeNullOrEmpty
    }

    It "sorts its output ordinally, so the order cannot depend on the current culture" {
        $surface = Get-FixtureSurface -Namespace "Ordering" -Body @"
    public class Contract
    {
        public void Zeta() { }
        public void alpha() { }
        public void Alpha() { }
    }
"@
        $sorted = [string[]] ([System.Linq.Enumerable]::OrderBy(
                [string[]] $surface, [Func[string, string]] { param($line) $line },
                [System.StringComparer]::Ordinal))

        Compare-Surface -Left $surface -Right $sorted | Should -BeNullOrEmpty
    }

    It "reads the repository's own contract assemblies" {
        $plugins = Join-Path $script:repositoryRoot "src/plugins/EdFi.Api.Plugins/bin/Release/net10.0/EdFi.Api.Plugins.dll"
        $customValidation = Join-Path $script:repositoryRoot "src/dms/core/EdFi.DataManagementService.CustomValidation/bin/Release/net10.0/EdFi.DataManagementService.CustomValidation.dll"

        if (-not (Test-Path -LiteralPath $plugins) -or -not (Test-Path -LiteralPath $customValidation)) {
            Set-ItResult -Inconclusive -Because "the fixture needs the Release build of both contract assemblies"
        }

        # An interface has no base type, and a nil base-type handle reports its Kind as
        # TypeDefinition, so a reader that switches on Kind without checking for nil reads past the
        # end of the type table. ICustomResourceValidator is the first interface either contract
        # declares, which is why this case names a real assembly rather than a fixture.
        $customValidationSurface = & $script:reader -AssemblyPath $customValidation
        $customValidationSurface | Should -Contain "TYPE EdFi.DataManagementService.CustomValidation.ICustomResourceValidator accessibility=public kind=interface modifiers=abstract base=none interfaces=none generics=none"

        $pluginsSurface = & $script:reader -AssemblyPath $plugins
        @($pluginsSurface | Where-Object { $_ -clike "TYPE *" }) | Should -HaveCount 1
    }

    It "throws rather than returning an empty surface for a file that is not a managed assembly" {
        $notAnAssembly = Join-Path $script:fixtureRoot "not-an-assembly.dll"
        [System.IO.File]::WriteAllBytes($notAnAssembly, [byte[]] (1..64))

        { & $script:reader -AssemblyPath $notAnAssembly } | Should -Throw
    }

    It "throws for an assembly that does not exist" {
        { & $script:reader -AssemblyPath (Join-Path $script:fixtureRoot "absent.dll") } |
            Should -Throw -ExpectedMessage "*does not exist*"
    }
}

Describe "Get-ContractPublicSurface fails closed on malformed metadata" {
    # A sentinel string such as "decimal:malformed" would make two differently corrupt inputs
    # compare equal, which for a skip-when-unchanged publish gate means republishing over a package
    # whose contents were never actually compared.

    It "refuses an attribute whose prolog is not 0x0001" {
        $corrupt = New-CorruptedDecimalFixture -Namespace "CorruptProlog" -Offset 0 -Value 0x02

        { & $script:reader -AssemblyPath $corrupt } |
            Should -Throw -ExpectedMessage "*prolog 0x0002*"
    }

    It "names the member carrying the malformed attribute" {
        $corrupt = New-CorruptedDecimalFixture -Namespace "CorruptNamesMember" -Offset 0 -Value 0x02

        { & $script:reader -AssemblyPath $corrupt } |
            Should -Throw -ExpectedMessage "*parameter 'amount'*"
    }

    It "refuses an attribute declaring named arguments it does not support" {
        $corrupt = New-CorruptedDecimalFixture -Namespace "CorruptNamedCount" -Offset 16 -Value 0x01

        { & $script:reader -AssemblyPath $corrupt } |
            Should -Throw -ExpectedMessage "*named argument*"
    }

    It "refuses an out-of-range decimal scale rather than surfacing an argument exception" {
        # 29 is one past the maximum a decimal can carry, and the failure has to name the member
        # rather than arrive as an ArgumentOutOfRangeException from inside the decimal constructor.
        $corrupt = New-CorruptedDecimalFixture -Namespace "CorruptScale" -Offset 2 -Value 29

        { & $script:reader -AssemblyPath $corrupt } |
            Should -Throw -ExpectedMessage "*scale 29*"
    }

    # The property that matters: two differently corrupt inputs must not read as one unchanged
    # contract. Both throw, so no comparison between them can ever return "identical".
    It "never reports two differently corrupt assemblies as identical" {
        $first = New-CorruptedDecimalFixture -Namespace "CorruptPair" -Offset 0 -Value 0x02
        $second = New-CorruptedDecimalFixture -Namespace "CorruptPair" -Offset 0 -Value 0x03

        { & $script:reader -AssemblyPath $first } | Should -Throw
        { & $script:reader -AssemblyPath $second } | Should -Throw
    }
}

Describe "Get-ContractPublicSurface reads a signature carrying a custom modifier" {
    # SignatureDecoder introduces a modifier's own type with element type 0x00 rather than CLASS or
    # VALUETYPE, so a reader that admitted only those two threw BadImageFormatException on every
    # shape below and produced no surface at all. In the prerelease lane that is a whole-release
    # failure, not one contract's: pack-dms and pack-schema-tools need the check jobs.
    #
    # These are the three shapes the C# compiler emits one for. An `in` parameter is deliberately
    # among them and deliberately virtual: on a non-virtual method `in` emits no modifier, so the
    # `in` case in the byref test above never exercised this path.

    It "reads a ref readonly return" {
        $surface = Get-FixtureSurface -Namespace "RefReadonlyReturn" -Body @"
    public class Contract
    {
        private int _value;
        public ref readonly int Read() => ref _value;
    }
"@

        $surface | Should -Contain "METHOD RefReadonlyReturn.Contract.Read``0() : modreq(System.Runtime.InteropServices.InAttribute) System.Int32& nullable=[] accessibility=public modifiers=none generics=none"
    }

    It "reads a volatile field" {
        $surface = Get-FixtureSurface -Namespace "VolatileField" -Body "    public class Contract { public volatile int Flag; }"

        $surface | Should -Contain "FIELD VolatileField.Contract.Flag : modreq(System.Runtime.CompilerServices.IsVolatile) System.Int32 nullable=[] accessibility=public modifiers=none"
    }

    It "reads an in parameter on an interface member" {
        $surface = Get-FixtureSurface -Namespace "InOnInterface" -Body "    public interface IContract { void Apply(in int value); }"

        $surface | Should -Contain "METHOD InOnInterface.IContract.Apply``0(in modreq(System.Runtime.InteropServices.InAttribute) System.Int32& value nullable=[]) : System.Void nullable=[] accessibility=public modifiers=abstract,virtual generics=none"
    }

    It "sees a modifier added to a field" {
        Test-SurfaceChange -Namespace "VolatileAdded" `
            -Before "    public class Contract { public int Flag; }" `
            -After "    public class Contract { public volatile int Flag; }" |
            Should -BeTrue
    }

    It "keeps a modified type's nullability vector unchanged" {
        # GetModifiedType discards the modifier's shape and keeps the unmodified type's, so a
        # modifier must not shift the positions the vector describes.
        $modified = Get-FixtureSurface -Namespace "ModifierNullability" -Body @"
    public interface IContract { void Apply(in string? text, string other); }
"@
        $plain = Get-FixtureSurface -Namespace "ModifierNullability" -Body @"
    public interface IContract { void Apply(ref string? text, string other); }
"@

        ($modified | Where-Object { $_ -clike "METHOD*Apply*" }) |
            Should -BeExactly "METHOD ModifierNullability.IContract.Apply``0(in modreq(System.Runtime.InteropServices.InAttribute) System.String& text nullable=[2], System.String other nullable=[1]) : System.Void nullable=[] accessibility=public modifiers=abstract,virtual generics=none"
        ($plain | Where-Object { $_ -clike "METHOD*Apply*" }) |
            Should -BeExactly "METHOD ModifierNullability.IContract.Apply``0(System.String& text nullable=[2], System.String other nullable=[1]) : System.Void nullable=[] accessibility=public modifiers=abstract,virtual generics=none"
    }
}

Describe "Get-ContractPublicSurface sees a change that the XML type list does not" {
    # Every case here leaves the set of public type names identical, which is all the packed-package
    # verifiers compare. That is the whole reason this script exists.

    It "sees a public member added to an unchanged type" {
        Test-SurfaceChange -Namespace "MemberAdded" `
            -Before "    public class Contract { public void First() { } }" `
            -After "    public class Contract { public void First() { } public void Second() { } }" |
            Should -BeTrue
    }

    It "sees a parameter type changed" {
        Test-SurfaceChange -Namespace "ParameterType" `
            -Before "    public class Contract { public void Apply(int value) { } }" `
            -After "    public class Contract { public void Apply(long value) { } }" |
            Should -BeTrue
    }

    It "sees parameters reordered" {
        Test-SurfaceChange -Namespace "ParameterOrder" `
            -Before "    public class Contract { public void Apply(int first, string second) { } }" `
            -After "    public class Contract { public void Apply(string second, int first) { } }" |
            Should -BeTrue
    }

    It "sees a return type changed" {
        Test-SurfaceChange -Namespace "ReturnType" `
            -Before "    public class Contract { public int Read() => 0; }" `
            -After "    public class Contract { public long Read() => 0; }" |
            Should -BeTrue
    }

    It "sees a protected member removed" {
        Test-SurfaceChange -Namespace "ProtectedRemoved" `
            -Before "    public class Contract { protected void Hook() { } }" `
            -After "    public class Contract { }" |
            Should -BeTrue
    }

    It "sees a member's accessibility widened from protected to public" {
        Test-SurfaceChange -Namespace "AccessibilityWidened" `
            -Before "    public class Contract { protected void Hook() { } }" `
            -After "    public class Contract { public void Hook() { } }" |
            Should -BeTrue
    }

    It "sees protected changed to protected internal" {
        Test-SurfaceChange -Namespace "ProtectedInternal" `
            -Before "    public class Contract { protected void Hook() { } }" `
            -After "    public class Contract { protected internal void Hook() { } }" |
            Should -BeTrue
    }

    It "sees a public type made internal" {
        Test-SurfaceChange -Namespace "TypeHidden" `
            -Before "    public class Kept { } public class Removed { }" `
            -After "    public class Kept { } internal class Removed { }" |
            Should -BeTrue
    }

    It "sees a base type changed" {
        Test-SurfaceChange -Namespace "BaseChanged" `
            -Before "    public class Root { } public class Other { } public class Contract : Root { }" `
            -After "    public class Root { } public class Other { } public class Contract : Other { }" |
            Should -BeTrue
    }

    It "sees an interface added to a type" {
        Test-SurfaceChange -Namespace "InterfaceAdded" `
            -Before "    public interface IMarker { } public class Contract { }" `
            -After "    public interface IMarker { } public class Contract : IMarker { }" |
            Should -BeTrue
    }

    It "sees a generic constraint added" {
        Test-SurfaceChange -Namespace "ConstraintAdded" `
            -Before "    public class Contract<T> { }" `
            -After "    public class Contract<T> where T : class { }" |
            Should -BeTrue
    }

    It "sees a variance annotation changed" {
        Test-SurfaceChange -Namespace "VarianceChanged" `
            -Before "    public interface IContract<in T> { void Apply(T value); }" `
            -After "    public interface IContract<T> { void Apply(T value); }" |
            Should -BeTrue
    }

    It "sees a type parameter renamed" {
        Test-SurfaceChange -Namespace "TypeParameterRenamed" `
            -Before "    public class Contract<TValue> { public TValue Read() => default!; }" `
            -After "    public class Contract<TResult> { public TResult Read() => default!; }" |
            Should -BeTrue
    }

    It "sees ref, out and in on a parameter" {
        Test-SurfaceChange -Namespace "ByReference" `
            -Before "    public class Contract { public void Apply(int value) { } }" `
            -After "    public class Contract { public void Apply(ref int value) { } }" |
            Should -BeTrue

        Test-SurfaceChange -Namespace "OutParameter" `
            -Before "    public class Contract { public void Apply(ref int value) { } }" `
            -After "    public class Contract { public void Apply(out int value) { value = 0; } }" |
            Should -BeTrue

        # On a non-virtual method `in` carries no custom modifier at all; it is ParameterAttributes.In
        # on an ordinary byref, which is what separates this pair. The modifier cases are below.
        Test-SurfaceChange -Namespace "InParameter" `
            -Before "    public class Contract { public void Apply(ref int value) { } }" `
            -After "    public class Contract { public void Apply(in int value) { } }" |
            Should -BeTrue
    }

    It "sees in changed to ref readonly on a parameter" {
        # Both are ParameterAttributes.In on a byref and both compile to the same modifier state, so
        # only RequiresLocationAttribute separates them. A caller sees the difference: `ref readonly`
        # warns at a call site that passes an expression without `in` or `ref`, and `in` does not.
        Test-SurfaceChange -Namespace "RefReadonlyParameter" `
            -Before "    public class Contract { public void Apply(in int value) { } }" `
            -After "    public class Contract { public void Apply(ref readonly int value) { } }" |
            Should -BeTrue
    }

    It "sees a default value changed" {
        Test-SurfaceChange -Namespace "DefaultChanged" `
            -Before "    public class Contract { public void Apply(int value = 1) { } }" `
            -After "    public class Contract { public void Apply(int value = 2) { } }" |
            Should -BeTrue
    }

    It "sees a default value removed" {
        Test-SurfaceChange -Namespace "DefaultRemoved" `
            -Before "    public class Contract { public void Apply(int value = 1) { } }" `
            -After "    public class Contract { public void Apply(int value) { } }" |
            Should -BeTrue
    }

    It "sees a params modifier dropped" {
        Test-SurfaceChange -Namespace "ParamsDropped" `
            -Before "    public class Contract { public void Apply(params int[] values) { } }" `
            -After "    public class Contract { public void Apply(int[] values) { } }" |
            Should -BeTrue
    }

    It "sees a const value changed" {
        Test-SurfaceChange -Namespace "ConstChanged" `
            -Before "    public class Contract { public const int Limit = 10; }" `
            -After "    public class Contract { public const int Limit = 20; }" |
            Should -BeTrue
    }

    It "sees a field becoming readonly" {
        Test-SurfaceChange -Namespace "FieldReadonly" `
            -Before "    public class Contract { public int Value; }" `
            -After "    public class Contract { public readonly int Value; }" |
            Should -BeTrue
    }

    It "sees an enum's underlying type changed" {
        Test-SurfaceChange -Namespace "EnumUnderlying" `
            -Before "    public enum Mode { First, Second }" `
            -After "    public enum Mode : long { First, Second }" |
            Should -BeTrue
    }

    It "sees an enum member's value changed" {
        Test-SurfaceChange -Namespace "EnumValue" `
            -Before "    public enum Mode { First = 0, Second = 1 }" `
            -After "    public enum Mode { First = 0, Second = 2 }" |
            Should -BeTrue
    }

    # get_Item is the accessor name for every indexer, so accessors alone cannot tell these apart.
    It "sees an indexer renamed although its accessors are identical" {
        Test-SurfaceChange -Namespace "IndexerRenamed" `
            -Before @"
    public class Contract
    {
        [System.Runtime.CompilerServices.IndexerName("Item")]
        public int this[int index] => index;
    }
"@ `
            -After @"
    public class Contract
    {
        [System.Runtime.CompilerServices.IndexerName("Entry")]
        public int this[int index] => index;
    }
"@ | Should -BeTrue
    }

    It "sees an indexer's parameter type changed" {
        Test-SurfaceChange -Namespace "IndexerParameter" `
            -Before "    public class Contract { public int this[int index] => index; }" `
            -After "    public class Contract { public int this[string index] => 0; }" |
            Should -BeTrue
    }

    It "sees a setter added to a get-only property" {
        Test-SurfaceChange -Namespace "SetterAdded" `
            -Before "    public class Contract { public int Value { get; } }" `
            -After "    public class Contract { public int Value { get; set; } }" |
            Should -BeTrue
    }

    It "sees a set accessor become init" {
        Test-SurfaceChange -Namespace "InitAdded" `
            -Before "    public class Contract { public int Value { get; set; } }" `
            -After "    public class Contract { public int Value { get; init; } }" |
            Should -BeTrue
    }

    It "sees a property accessor's accessibility narrowed" {
        Test-SurfaceChange -Namespace "SetterNarrowed" `
            -Before "    public class Contract { public int Value { get; set; } }" `
            -After "    public class Contract { public int Value { get; protected set; } }" |
            Should -BeTrue
    }

    It "sees an abstract member become virtual" {
        Test-SurfaceChange -Namespace "AbstractToVirtual" `
            -Before "    public abstract class Contract { public abstract void Apply(); }" `
            -After "    public abstract class Contract { public virtual void Apply() { } }" |
            Should -BeTrue
    }

    It "sees an abstract property become virtual" {
        Test-SurfaceChange -Namespace "AbstractPropertyToVirtual" `
            -Before "    public abstract class Contract { public abstract int Value { get; } }" `
            -After "    public abstract class Contract { public virtual int Value => 0; }" |
            Should -BeTrue
    }

    It "sees a static member become instance" {
        Test-SurfaceChange -Namespace "StaticToInstance" `
            -Before "    public class Contract { public static void Apply() { } }" `
            -After "    public class Contract { public void Apply() { } }" |
            Should -BeTrue
    }

    It "sees a sealed modifier added to a type" {
        Test-SurfaceChange -Namespace "SealedAdded" `
            -Before "    public class Contract { }" `
            -After "    public sealed class Contract { }" |
            Should -BeTrue
    }

    It "sees an event added" {
        Test-SurfaceChange -Namespace "EventAdded" `
            -Before "    public class Contract { }" `
            -After "    public class Contract { public event EventHandler? Changed; }" |
            Should -BeTrue
    }

    It "sees an event's handler type changed" {
        Test-SurfaceChange -Namespace "EventType" `
            -Before "    public class Contract { public event EventHandler? Changed; }" `
            -After "    public class Contract { public event EventHandler<EventArgs>? Changed; }" |
            Should -BeTrue
    }

    # A decimal has no CLR literal encoding, so the compiler records both decimal defaults and
    # decimal constants in DecimalConstantAttribute rather than in the metadata Constant table. A
    # reader that consulted only that table reported both sides of a changed decimal as having no
    # value at all, and a consumer compiles that value into its own code.
    It "sees a decimal default value changed" {
        Test-SurfaceChange -Namespace "DecimalDefault" `
            -Before "    public class Contract { public void Apply(decimal amount = 1m) { } }" `
            -After "    public class Contract { public void Apply(decimal amount = 2m) { } }" |
            Should -BeTrue
    }

    It "sees a decimal constant's value changed" {
        Test-SurfaceChange -Namespace "DecimalConstant" `
            -Before "    public class Contract { public const decimal Amount = 1m; }" `
            -After "    public class Contract { public const decimal Amount = 2m; }" |
            Should -BeTrue
    }

    It "reports the decimal value rather than only that one exists" {
        $surface = Get-FixtureSurface -Namespace "DecimalRendered" -Body @"
    public class Contract
    {
        public const decimal Amount = 1.25m;
        public void Apply(decimal rate = 3.5m) { }
    }
"@

        ($surface -join "`n") | Should -BeLike "*value=decimal:1.25*"
        ($surface -join "`n") | Should -BeLike "*rate = decimal:3.5*"
    }

    # DateTimeConstantAttribute is the other attribute-encoded default the framework defines. The
    # parameter is marked optional through [Optional] rather than `= default`, because a C# default
    # clause plus the attribute is two distinct defaults and the compiler refuses it.
    It "sees a DateTime default value changed" {
        Test-SurfaceChange -Namespace "DateTimeDefault" `
            -Before @"
    public class Contract
    {
        public void Apply(
            [System.Runtime.InteropServices.Optional]
            [System.Runtime.CompilerServices.DateTimeConstant(100L)] DateTime at) { }
    }
"@ `
            -After @"
    public class Contract
    {
        public void Apply(
            [System.Runtime.InteropServices.Optional]
            [System.Runtime.CompilerServices.DateTimeConstant(200L)] DateTime at) { }
    }
"@ | Should -BeTrue
    }

    # notnull is not a CLR constraint flag; the compiler records it as NullableAttribute(1) on the
    # type parameter, so the constraint table and GenericParameterAttributes are both blind to it.
    It "sees a notnull constraint added to a type parameter" {
        Test-SurfaceChange -Namespace "NotNullConstraint" `
            -Before "    public class Contract<T> { }" `
            -After "    public class Contract<T> where T : notnull { }" |
            Should -BeTrue
    }

    It "sees a notnull constraint added to a generic method" {
        Test-SurfaceChange -Namespace "NotNullMethod" `
            -Before "    public class Contract { public void Apply<T>(T value) { } }" `
            -After "    public class Contract { public void Apply<T>(T value) where T : notnull { } }" |
            Should -BeTrue
    }

    # Which entity carries NullableContextAttribute is a compiler packing decision. Adding a private
    # member can move it from a method onto the declaring type, and a reader that consulted only the
    # method then reports an unchanged public constraint as changed, demanding a version bump for a
    # change no consumer can observe. That is as damaging as a missed change: it breaks the
    # skip-when-unchanged half of the publish policy.
    It "ignores a private member that moves the nullable context onto the declaring type" {
        Test-SurfaceChange -Namespace "MethodContextMoved" `
            -Before "    public class Contract { public void Apply<T>() where T : notnull { } }" `
            -After @"
    public class Contract
    {
        public void Apply<T>() where T : notnull { }
        private string Hidden(string input) => input;
    }
"@ | Should -BeFalse
    }

    It "ignores a private member that moves the nullable context on a nested type" {
        Test-SurfaceChange -Namespace "NestedContextMoved" `
            -Before @"
    public class Outer
    {
        public class Contract<T> where T : notnull { }
    }
"@ `
            -After @"
    public class Outer
    {
        public class Contract<T> where T : notnull { }
        private string Hidden(string input) => input;
    }
"@ | Should -BeFalse
    }

    It "still sees a constraint change on a nested type" {
        Test-SurfaceChange -Namespace "NestedConstraintChanged" `
            -Before "    public class Outer { public class Contract<T> where T : notnull { } }" `
            -After "    public class Outer { public class Contract<T> { } }" |
            Should -BeTrue
    }

    It "reports notnull by name" {
        $surface = Get-FixtureSurface -Namespace "NotNullRendered" `
            -Body "    public class Contract<T> where T : notnull { }"

        ($surface -join "`n") | Should -BeLike "*generics=T:notnull*"
    }

    # Apply(value: 1) stops compiling when the parameter is renamed, at an unchanged package version
    # and with a byte-identical signature, so the name is part of what a consumer binds to.
    It "sees a parameter renamed, which breaks every named argument at the call site" {
        Test-SurfaceChange -Namespace "ParameterRenamed" `
            -Before "    public class Contract { public void Apply(int value) { } }" `
            -After "    public class Contract { public void Apply(int input) { } }" |
            Should -BeTrue
    }

    It "sees a constructor's parameter renamed" {
        Test-SurfaceChange -Namespace "ConstructorParameterRenamed" `
            -Before "    public class Contract { public Contract(int value) { } }" `
            -After "    public class Contract { public Contract(int input) { } }" |
            Should -BeTrue
    }

    It "sees an indexer's parameter renamed" {
        Test-SurfaceChange -Namespace "IndexerParameterRenamed" `
            -Before "    public class Contract { public int this[int index] => index; }" `
            -After "    public class Contract { public int this[int position] => position; }" |
            Should -BeTrue
    }

    It "keeps parameters positional, so two names swapped is a change" {
        Test-SurfaceChange -Namespace "ParameterNamesSwapped" `
            -Before "    public class Contract { public void Apply(int first, int second) { } }" `
            -After "    public class Contract { public void Apply(int second, int first) { } }" |
            Should -BeTrue
    }

    It "sees a rename that differs only in case" {
        Test-SurfaceChange -Namespace "CaseOnlyRename" `
            -Before "    public class Contract { public void Apply() { } }" `
            -After "    public class Contract { public void APPLY() { } }" |
            Should -BeTrue
    }

    It "sees a nested public type made internal on its declaring type" {
        Test-SurfaceChange -Namespace "NestedHidden" `
            -Before "    public class Outer { public class Inner { } }" `
            -After "    internal class Outer { public class Inner { } }" |
            Should -BeTrue
    }
}

Describe "Get-ContractPublicSurface ignores what an implementer cannot bind to" {
    It "ignores an internal type" {
        Test-SurfaceChange -Namespace "InternalType" `
            -Before "    public class Contract { }" `
            -After "    public class Contract { } internal class Hidden { public void Apply() { } }" |
            Should -BeFalse
    }

    It "ignores a private and an internal member" {
        Test-SurfaceChange -Namespace "InternalMember" `
            -Before "    public class Contract { public void Apply() { } }" `
            -After @"
    public class Contract
    {
        public void Apply() { }
        internal void Hidden() { }
        private void Secret() { }
        private int _state;
        internal int Shared;
    }
"@ | Should -BeFalse
    }

    # private protected is inaccessible outside the declaring assembly, so nothing an implementer
    # compiles can bind to it. Including it would fail a publish over a change no consumer can see.
    It "ignores a private protected member" {
        Test-SurfaceChange -Namespace "PrivateProtected" `
            -Before "    public class Contract { public void Apply() { } }" `
            -After "    public class Contract { public void Apply() { } private protected void Hook() { } }" |
            Should -BeFalse
    }

    It "ignores a method body change that leaves every signature alone" {
        Test-SurfaceChange -Namespace "BodyChanged" `
            -Before "    public class Contract { public int Read() => 1; }" `
            -After "    public class Contract { public int Read() => 2; }" |
            Should -BeFalse
    }

    It "ignores an added attribute that encodes no signature, default or constraint" {
        # The boundary, stated rather than implied. What is compared is the signature, the declared
        # defaults and the generic constraints, including the attribute-encoded forms of the last
        # two. Attributes that encode none of those, such as [Obsolete], are outside the comparison.
        #
        # That is a deliberate limit rather than a claim of harmlessness: [Obsolete] raises a
        # diagnostic, and [Obsolete(error: true)], or a consumer building with warnings as errors,
        # can stop that consumer's build. Widening the gate to arbitrary attributes is a larger
        # policy decision than this comparison makes.
        Test-SurfaceChange -Namespace "AttributeAdded" `
            -Before '    public class Contract { public void Apply() { } }' `
            -After '    public class Contract { [Obsolete] public void Apply() { } }' |
            Should -BeFalse
    }
}

Describe "Get-ContractPublicSurface nullability" {
    # A consumer building with warnings as errors, which both scratch consumers in this repository
    # do, stops compiling when a parameter it passes null to becomes non-nullable or a value it
    # dereferences becomes nullable. Both directions are a changed contract at an unchanged version.
    It "sees a parameter changed from string to string?" {
        Test-SurfaceChange -Namespace "ParamNullable" `
            -Before '    public class Contract { public void Apply(string value) { } }' `
            -After '    public class Contract { public void Apply(string? value) { } }' |
            Should -BeTrue
    }

    It "sees a parameter changed from string? to string" {
        Test-SurfaceChange -Namespace "ParamNotNullable" `
            -Before '    public class Contract { public void Apply(string? value) { } }' `
            -After '    public class Contract { public void Apply(string value) { } }' |
            Should -BeTrue
    }

    It "sees a return type changed from string? to string" {
        Test-SurfaceChange -Namespace "ReturnNullable" `
            -Before '    public class Contract { public string? Read() => null; }' `
            -After '    public class Contract { public string Read() => ""; }' |
            Should -BeTrue
    }

    It "sees a property type changed from string to string?" {
        Test-SurfaceChange -Namespace "PropertyNullable" `
            -Before '    public class Contract { public string Name { get; set; } = ""; }' `
            -After '    public class Contract { public string? Name { get; set; } }' |
            Should -BeTrue
    }

    It "sees a field type changed from string to string?" {
        Test-SurfaceChange -Namespace "FieldNullable" `
            -Before '    public class Contract { public string Name = ""; }' `
            -After '    public class Contract { public string? Name; }' |
            Should -BeTrue
    }

    It "sees an event's delegate argument nullability changed" {
        Test-SurfaceChange -Namespace "EventNullable" `
            -Before '    public class Contract { public event Action<string?>? Changed; }' `
            -After '    public class Contract { public event Action<string>? Changed; }' |
            Should -BeTrue
    }

    It "sees an indexer parameter changed from string? to string" {
        Test-SurfaceChange -Namespace "IndexerNullable" `
            -Before '    public class Contract { public string this[string? key] => ""; }' `
            -After '    public class Contract { public string this[string key] => ""; }' |
            Should -BeTrue
    }

    # The compiler encodes these as a byte[] with one entry per position, so the outer type and
    # each argument are read as separate positions.
    It "sees a nested generic argument changed from string to string?" {
        Test-SurfaceChange -Namespace "NestedNullable" `
            -Before '    public class Contract { public List<string> Items() => new(); }' `
            -After '    public class Contract { public List<string?> Items() => new(); }' |
            Should -BeTrue
    }

    It "sees a reference argument of a generic value type changed" {
        Test-SurfaceChange -Namespace "MixedNullable" `
            -Before '    public class Contract { public KeyValuePair<string, int> Pair() => default; }' `
            -After '    public class Contract { public KeyValuePair<string?, int> Pair() => default; }' |
            Should -BeTrue
    }

    It "sees a nullable array of non-nullable elements as different from a non-nullable array of nullable elements" {
        Test-SurfaceChange -Namespace "ArrayNullable" `
            -Before '    public class Contract { public string?[] Names() => new string?[0]; }' `
            -After '    public class Contract { public string[]? Names() => null; }' |
            Should -BeTrue
    }

    It "sees T? changed to T on an unconstrained generic parameter" {
        Test-SurfaceChange -Namespace "GenericParameterNullable" `
            -Before '    public class Contract<T> { public T? Get() => default; }' `
            -After '    public class Contract<T> { public T Get() => default!; }' |
            Should -BeTrue
    }

    # Which entity carries NullableContext, and whether NullableAttribute is written out or elided,
    # are compiler packing decisions that private members can change. The same public signatures
    # must read the same however they were packed, or the skip-when-unchanged policy breaks.
    It "ignores private members that repack the nullable context around a nested generic signature" {
        Test-SurfaceChange -Namespace "NestedRepacked" `
            -Before '    public class Contract { public Dictionary<string, List<string?>> Map(string key) => new(); }' `
            -After @"
    public class Contract
    {
        public Dictionary<string, List<string?>> Map(string key) => new();
        private string? Hidden(string? a, string? b) => a;
        private string? Other(string? a, string? b, string? c) => a;
        private List<string?>? Third(string? a) => null;
    }
"@ | Should -BeFalse
    }

    It "ignores private members that repack the nullable context around a mixed value and reference signature" {
        Test-SurfaceChange -Namespace "MixedRepacked" `
            -Before '    public class Contract { public KeyValuePair<string, int> Pair(int count, string name) => default; }' `
            -After @"
    public class Contract
    {
        public KeyValuePair<string, int> Pair(int count, string name) => default;
        private string? Hidden(string? a, string? b) => a;
        private string? Other(string? a, string? b, string? c) => a;
    }
"@ | Should -BeFalse
    }

    It "ignores private members that repack the nullable context around a value-type-only signature" {
        Test-SurfaceChange -Namespace "ValueOnlyRepacked" `
            -Before '    public class Contract { public int Sum(int a, int b) => a + b; public int? Maybe(int? x) => x; }' `
            -After @"
    public class Contract
    {
        public int Sum(int a, int b) => a + b;
        public int? Maybe(int? x) => x;
        private string? Hidden(string? a, string? b) => a;
        private string? Other(string? a, string? b, string? c) => a;
    }
"@ | Should -BeFalse
    }

    It "reads explicit and elided annotations of one signature as the same" {
        # The single public method reads string -> string. Alone, its annotations equal the type's
        # context and are elided; beside members whose annotations outnumber it, the type's context
        # flips and the compiler writes the public method's annotations out explicitly.
        Test-SurfaceChange -Namespace "ExplicitVersusElided" `
            -Before '    public class Contract { public string Name(string s) => s; }' `
            -After @"
    public class Contract
    {
        public string Name(string s) => s;
        private string? A(string? o) => o;
        private string? B(string? o) => o;
        private string? C(string? o) => o;
    }
"@ | Should -BeFalse
    }

    # A generic value type keeps its fixed-zero slot even when nothing inside it is annotatable:
    # the compiler writes [2,0,2] for both of these, so a shape that dropped the slot would refuse
    # valid metadata as a length mismatch.
    It "keeps a nested all-value tuple argument's slot" {
        $surface = Get-FixtureSurface -Namespace "NestedTupleSlot" `
            -Body '    public class Contract { public Dictionary<(int, int), string?>? Map() => null; }'

        ($surface -join "`n").Contains('nullable=[2,0,2]', [System.StringComparison]::Ordinal) | Should -BeTrue
    }

    It "keeps a nested all-value generic struct argument's slot" {
        $surface = Get-FixtureSurface -Namespace "NestedStructSlot" `
            -Body '    public class Contract { public Dictionary<KeyValuePair<int, int>, string?>? Map() => null; }'

        ($surface -join "`n").Contains('nullable=[2,0,2]', [System.StringComparison]::Ordinal) | Should -BeTrue
    }

    It "ignores private members that repack the context around a bare all-value generic struct" {
        Test-SurfaceChange -Namespace "BareStructRepacked" `
            -Before '    public class Contract { public KeyValuePair<int, int> Pair() => default; }' `
            -After @"
    public class Contract
    {
        public KeyValuePair<int, int> Pair() => default;
        private string? Hidden(string? a, string? b) => a;
        private string? Other(string? a, string? b, string? c) => a;
    }
"@ | Should -BeFalse
    }

    # A struct-constrained generic parameter is a value type to the compiler and gets a fixed 0
    # where an unconstrained one is annotatable: List<T>? is [2,0] under `where T : struct` and
    # [2,1] without the constraint.
    It "reads a struct-constrained generic parameter as a fixed-zero position" {
        $surface = Get-FixtureSurface -Namespace "StructConstrainedSlot" `
            -Body '    public class Contract { public List<T>? Items<T>() where T : struct => null; }'

        ($surface -join "`n").Contains('nullable=[2,0]', [System.StringComparison]::Ordinal) | Should -BeTrue
    }

    It "reads an unconstrained generic parameter as an annotatable position" {
        $surface = Get-FixtureSurface -Namespace "UnconstrainedSlot" `
            -Body '    public class Contract { public List<T>? Items<T>() => null; }'

        ($surface -join "`n").Contains('nullable=[2,1]', [System.StringComparison]::Ordinal) | Should -BeTrue
    }

    It "ignores private members that repack the context around a struct-constrained identity signature" {
        Test-SurfaceChange -Namespace "StructConstrainedRepacked" `
            -Before '    public class Contract { public T Echo<T>(T value) where T : struct => value; }' `
            -After @"
    public class Contract
    {
        public T Echo<T>(T value) where T : struct => value;
        private string? Hidden(string? a, string? b) => a;
        private string? Other(string? a, string? b, string? c) => a;
    }
"@ | Should -BeFalse
    }

    # The property row's annotation is governed by the type's context, not the accessor's: this
    # getter chooses context 2 for its nullable index parameters and writes an explicit 1 on its
    # return row, while the property type stays a non-nullable string under the type's context 1.
    It "resolves an indexer's property type against the type context, not the getter's" {
        $surface = Get-FixtureSurface -Namespace "IndexerTypeContext" -Body @"
    public class Contract
    {
        public string Name(string a, string b, string c) => a;
        public string Other(string a, string b, string c) => a;
        public string this[string? a, string? b, string? c] => "";
    }
"@

        ($surface -join "`n").Contains(
            'PROPERTY IndexerTypeContext.Contract.Item[System.String a nullable=[2], System.String b nullable=[2], System.String c nullable=[2]] : System.String nullable=[1]',
            [System.StringComparison]::Ordinal
        ) | Should -BeTrue
    }

    It "sees that indexer's property type changed to string?" {
        Test-SurfaceChange -Namespace "IndexerTypeChanged" `
            -Before @"
    public class Contract
    {
        public string Name(string a, string b, string c) => a;
        public string Other(string a, string b, string c) => a;
        public string this[string? a, string? b, string? c] => "";
    }
"@ `
            -After @"
    public class Contract
    {
        public string Name(string a, string b, string c) => a;
        public string Other(string a, string b, string c) => a;
        public string? this[string? a, string? b, string? c] => null;
    }
"@ | Should -BeTrue
    }

    It "ignores private members that repack the context around an unchanged property" {
        Test-SurfaceChange -Namespace "PropertyRepacked" `
            -Before '    public class Contract { public string Name { get; set; } = ""; public string? Label { get; set; } }' `
            -After @"
    public class Contract
    {
        public string Name { get; set; } = "";
        public string? Label { get; set; }
        private string? Hidden(string? a, string? b) => a;
        private string? Other(string? a, string? b, string? c) => a;
        private string? Third(string? a, string? b, string? c) => a;
    }
"@ | Should -BeFalse
    }

    It "renders the vector with one entry per position" {
        $surface = Get-FixtureSurface -Namespace "VectorRendered" `
            -Body '    public class Contract { public Dictionary<string, List<string?>> Map(int count, string? name) => new(); }'

        # Contains rather than -like: brackets are wildcard classes to -like, and the backtick in
        # the arity suffix is its escape character.
        ($surface -join "`n").Contains(
            'METHOD VectorRendered.Contract.Map`0(System.Int32 count nullable=[], System.String name nullable=[2]) : System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<System.String>> nullable=[1,1,1,2] accessibility=public',
            [System.StringComparison]::Ordinal
        ) | Should -BeTrue
    }
}

Describe "Get-ContractPublicSurface nullable-flow attributes" {
    It "sees [AllowNull] added to a property, on the setter's value parameter" {
        Test-SurfaceChange -Namespace "AllowNullAdded" `
            -Before '    public class Contract { public string Name { get; set; } = ""; }' `
            -After '    public class Contract { [System.Diagnostics.CodeAnalysis.AllowNull] public string Name { get; set; } = ""; }' |
            Should -BeTrue
    }

    It "sees [NotNullWhen(true)] changed to [NotNullWhen(false)]" {
        Test-SurfaceChange -Namespace "NotNullWhenChanged" `
            -Before '    public class Contract { public bool Try([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) { value = ""; return true; } }' `
            -After '    public class Contract { public bool Try([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? value) { value = ""; return true; } }' |
            Should -BeTrue
    }

    # [NotNull] on the getter's return promises the consumer a value; [NotNull] on the setter's
    # value demands one of them. The same attribute on the other accessor is a different contract,
    # so the two rows are rendered under different labels rather than merged.
    It "distinguishes the same annotation on the getter's return from the setter's value" {
        Test-SurfaceChange -Namespace "AccessorRoles" `
            -Before '    public class Contract { public string? Name { [return: System.Diagnostics.CodeAnalysis.NotNull] get => ""; set { } } }' `
            -After '    public class Contract { public string? Name { get => ""; [param: System.Diagnostics.CodeAnalysis.NotNull] set { } } }' |
            Should -BeTrue
    }

    # An accessor nobody outside the assembly can call is not part of the surface, and neither is an
    # analysis promise attached to it: the property line already reads get=none for it.
    It "ignores an annotation added to a private getter" {
        Test-SurfaceChange -Namespace "PrivateGetterAnnotation" `
            -Before '    public class Contract { public string? Value { private get => null!; set { } } }' `
            -After '    public class Contract { public string? Value { [return: System.Diagnostics.CodeAnalysis.NotNull] private get => null!; set { } } }' |
            Should -BeFalse
    }

    It "ignores an annotation added to a private setter" {
        Test-SurfaceChange -Namespace "PrivateSetterAnnotation" `
            -Before '    public class Contract { public string? Value { get => null; private set { } } }' `
            -After '    public class Contract { public string? Value { get => null; [param: System.Diagnostics.CodeAnalysis.DisallowNull] private set { } } }' |
            Should -BeFalse
    }

    It "renders accessor annotations under their own labels" {
        $surface = Get-FixtureSurface -Namespace "AccessorLabels" `
            -Body '    public class Contract { public string? Name { [return: System.Diagnostics.CodeAnalysis.NotNull] get => ""; [param: System.Diagnostics.CodeAnalysis.AllowNull] set { } } }'

        ($surface -join "`n").Contains(
            'PROPERTY AccessorLabels.Contract.Name : System.String nullable=[2] get=public:none set=public:none setkind=set getattrs=[NotNull] setattrs=[AllowNull]',
            [System.StringComparison]::Ordinal
        ) | Should -BeTrue
    }

    It "sees [DoesNotReturn] added to a method" {
        Test-SurfaceChange -Namespace "DoesNotReturnAdded" `
            -Before '    public class Contract { public void Fail() { } }' `
            -After '    public class Contract { [System.Diagnostics.CodeAnalysis.DoesNotReturn] public void Fail() => throw new Exception(); }' |
            Should -BeTrue
    }

    It "sees [NotNullIfNotNull] renamed to another parameter" {
        Test-SurfaceChange -Namespace "NotNullIfNotNullChanged" `
            -Before '    public class Contract { [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull("first")] public string? Pick(string? first, string? second) => first; }' `
            -After '    public class Contract { [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull("second")] public string? Pick(string? first, string? second) => second; }' |
            Should -BeTrue
    }
}

Describe "Get-ContractPublicSurface hierarchy closure" {
    # An abstract private protected member cannot be satisfied outside the assembly, so it closes
    # the hierarchy to every external deriver. Adding one turns a derivable abstract type into a
    # closed one, and removing one opens it, at an unchanged version either way.
    It "sees an abstract private protected member added" {
        Test-SurfaceChange -Namespace "ClosureAdded" `
            -Before '    public abstract class Contract { public abstract void Apply(); }' `
            -After '    public abstract class Contract { public abstract void Apply(); private protected abstract void Close(); }' |
            Should -BeTrue
    }

    It "sees an abstract private protected member removed" {
        Test-SurfaceChange -Namespace "ClosureRemoved" `
            -Before '    public abstract class Contract { public abstract void Apply(); private protected abstract void Close(); }' `
            -After '    public abstract class Contract { public abstract void Apply(); }' |
            Should -BeTrue
    }

    # The member's name and shape are invisible outside the assembly: no consumer can call, override
    # or even name it. Only the fact of closure is part of the contract, so that is all that is
    # rendered, and a rename or a changed parameter list at an unchanged version is not a change.
    It "renders the closure as one marker rather than the member's signature" {
        $surface = Get-FixtureSurface -Namespace "ClosureRendered" `
            -Body '    public abstract class Contract { private protected abstract void Close(); }'

        $joined = $surface -join "`n"

        $joined.Contains(
            'CLOSURE ClosureRendered.Contract by=private protected abstract member',
            [System.StringComparison]::Ordinal
        ) | Should -BeTrue
        $joined.Contains('Contract.Close', [System.StringComparison]::Ordinal) | Should -BeFalse
    }

    It "does not see the closing member renamed" {
        Test-SurfaceChange -Namespace "ClosureRenamed" `
            -Before '    public abstract class Contract { public abstract void Apply(); private protected abstract void Close(); }' `
            -After '    public abstract class Contract { public abstract void Apply(); private protected abstract void Seal(); }' |
            Should -BeFalse
    }

    It "does not see the closing member's signature changed" {
        Test-SurfaceChange -Namespace "ClosureReshaped" `
            -Before '    public abstract class Contract { public abstract void Apply(); private protected abstract void Close(); }' `
            -After '    public abstract class Contract { public abstract void Apply(); private protected abstract int Close(string reason); }' |
            Should -BeFalse
    }

    It "renders one marker when two members close the hierarchy" {
        $surface = Get-FixtureSurface -Namespace "ClosureTwice" `
            -Body '    public abstract class Contract { private protected abstract void Close(); private protected abstract void Seal(); }'

        @($surface | Where-Object { $_.StartsWith('CLOSURE ClosureTwice.Contract', [System.StringComparison]::Ordinal) }).Count |
            Should -Be 1
    }

    It "reads an abstract private protected property accessor as a closure and not as a property" {
        $surface = Get-FixtureSurface -Namespace "ClosureProperty" `
            -Body '    public abstract class Contract { private protected abstract int Depth { get; } }'

        $joined = $surface -join "`n"

        $joined.Contains('CLOSURE ClosureProperty.Contract by=private protected abstract member', [System.StringComparison]::Ordinal) |
            Should -BeTrue
        $joined.Contains('Depth', [System.StringComparison]::Ordinal) | Should -BeFalse
    }

    It "still ignores a non-abstract private protected member" {
        Test-SurfaceChange -Namespace "PrivateProtectedConcrete" `
            -Before '    public abstract class Contract { public abstract void Apply(); }' `
            -After '    public abstract class Contract { public abstract void Apply(); private protected void Hook() { } private protected virtual void Extend() { } }' |
            Should -BeFalse
    }

    It "reads the custom-validation contract's own closing member" {
        $customValidation = Join-Path $script:repositoryRoot "src/dms/core/EdFi.DataManagementService.CustomValidation/bin/Release/net10.0/EdFi.DataManagementService.CustomValidation.dll"

        if (-not (Test-Path -LiteralPath $customValidation)) {
            Set-ItResult -Inconclusive -Because "the custom-validation contract has not been built in Release; run ./build-dms.ps1 -Command Build -Configuration Release"
        }

        $surface = [string[]] @(& $script:reader -AssemblyPath $customValidation)

        $joined = $surface -join "`n"

        $joined.Contains(
            'CLOSURE EdFi.DataManagementService.CustomValidation.CustomValidationFailure by=private protected abstract member',
            [System.StringComparison]::Ordinal
        ) | Should -BeTrue
        $joined.Contains('EnsureClosed', [System.StringComparison]::Ordinal) | Should -BeFalse
    }
}

Describe "Get-ContractPublicSurface refuses malformed nullability metadata" {
    # Compiles a fixture whose one public member carries a NullableAttribute in the byte[] form
    # with a distinctive flag sequence, then corrupts one byte of that blob. Dictionary<string,
    # List<string?>> is [1,1,1,2]: the blob is prolog 01 00, count 04 00 00 00, flags 01 01 01 02,
    # named-argument count 00 00.
    BeforeAll {
        function New-CorruptedNullableFixture {
            [CmdletBinding(SupportsShouldProcess)]
            [OutputType([string])]
            param(
                [Parameter(Mandatory)][string] $Namespace,
                [Parameter(Mandatory)][int] $Offset,
                [Parameter(Mandatory)][byte] $Value
            )

            $path = Join-Path $script:fixtureRoot "$Namespace-corrupt-$([guid]::NewGuid().ToString('N')).dll"

            if (-not $PSCmdlet.ShouldProcess($path, "Corrupt fixture")) {
                return $null
            }

            Add-Type -OutputAssembly $path -OutputType Library -TypeDefinition @"
#nullable enable
using System.Collections.Generic;
namespace $Namespace
{
    public class Contract
    {
        public Dictionary<string, List<string?>> Map() => new();
    }
}
"@

            $anchor = [byte[]] @(0x01, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x02, 0x00, 0x00)
            $bytes = [System.IO.File]::ReadAllBytes($path)
            $blobMatches = @()

            for ($index = 0; $index -le $bytes.Length - $anchor.Length; $index++) {
                $found = $true

                for ($position = 0; $position -lt $anchor.Length; $position++) {
                    if ($bytes[$index + $position] -ne $anchor[$position]) {
                        $found = $false
                        break
                    }
                }

                if ($found) {
                    $blobMatches += $index
                }
            }

            if ($blobMatches.Count -ne 1) {
                throw "Expected exactly one NullableAttribute byte[] blob in the fixture and found $($blobMatches.Count)."
            }

            $bytes[$blobMatches[0] + $Offset] = $Value
            [System.IO.File]::WriteAllBytes($path, $bytes)

            return $path
        }
    }

    It "reads the uncorrupted fixture as [1,1,1,2]" {
        $surface = Get-FixtureSurface -Namespace "NullableIntact" -Body '    public class Contract { public Dictionary<string, List<string?>> Map() => new(); }'

        ($surface -join "`n").Contains('nullable=[1,1,1,2]', [System.StringComparison]::Ordinal) | Should -BeTrue
    }

    It "refuses a byte[] NullableAttribute whose prolog is not 0x0001" {
        $path = New-CorruptedNullableFixture -Namespace "NullablePrologCorrupt" -Offset 0 -Value 0x02

        { & $script:reader -AssemblyPath $path } | Should -Throw -ExpectedMessage "*prolog*"
    }

    It "refuses a byte[] NullableAttribute whose declared count exceeds its bytes" {
        $path = New-CorruptedNullableFixture -Namespace "NullableCountCorrupt" -Offset 2 -Value 0x09

        { & $script:reader -AssemblyPath $path } | Should -Throw -ExpectedMessage "*declares 9 flag(s)*"
    }

    It "refuses a nullability flag outside 0, 1 and 2" {
        $path = New-CorruptedNullableFixture -Namespace "NullableFlagCorrupt" -Offset 9 -Value 0x03

        { & $script:reader -AssemblyPath $path } | Should -Throw -ExpectedMessage "*carries flag 3*"
    }
}
