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

    # $null when the two surfaces are identical. -CaseSensitive and -SyncWindow 0 are both load
    # bearing: the surface is sorted ordinally, and a case-insensitive comparison would call a
    # case-only rename identical.
    # An empty surface is a legitimate result: an assembly whose only public type became internal
    # exports nothing an implementer can bind to, and that is precisely a change worth failing a
    # publish over. So both sides admit null and empty.
    function Compare-Surface {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][string[]] $Left,
            [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][string[]] $Right
        )

        return Compare-Object `
            -ReferenceObject ([string[]] @($Left)) `
            -DifferenceObject ([string[]] @($Right)) `
            -CaseSensitive `
            -SyncWindow 0
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

        # `in` is a required custom modifier on an ordinary byref, so a reader that discarded custom
        # modifiers would call this pair identical.
        Test-SurfaceChange -Namespace "InParameter" `
            -Before "    public class Contract { public void Apply(ref int value) { } }" `
            -After "    public class Contract { public void Apply(in int value) { } }" |
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

    It "ignores a parameter renamed without a type change" {
        # A parameter name is not part of what a positional caller binds to. Named arguments do bind
        # to it, so this is a deliberate boundary rather than an oversight: it keeps the comparison
        # to what the metadata signature states.
        Test-SurfaceChange -Namespace "ParameterRenamed" `
            -Before "    public class Contract { public void Apply(int value) { } }" `
            -After "    public class Contract { public void Apply(int input) { } }" |
            Should -BeFalse
    }
}
