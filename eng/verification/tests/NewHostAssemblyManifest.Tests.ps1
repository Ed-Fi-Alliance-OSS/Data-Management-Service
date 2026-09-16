# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Fixture proof for New-HostAssemblyManifest.ps1 and Assert-HostAssemblyManifest.ps1, and nothing
# more. These cases prove the scanning and framework-selection rules are right; they do not prove
# anything about the artifact an operator downloads. That is a separate run of the same two scripts
# against the real released image by digest, recorded on the ticket, and neither stands in for the
# other.
#
# Every fixture is assembled from assemblies that really exist on disk - the repository's own built
# contract assembly and assemblies from the installed shared frameworks - so every metadata read is a
# real one. A fabricated file would exercise the file walk and none of the reading.
#
# Docker is never used here. The generator's -ExtractedRoot parameter set takes a directory already
# laid out as its extraction stage, which is the seam that makes every rule below testable.

BeforeAll {
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
    $script:generator = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../New-HostAssemblyManifest.ps1"))
    $script:verifier = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Assert-HostAssemblyManifest.ps1"))

    Import-Module (Join-Path $script:repositoryRoot "package-helpers.psm1") -Force
    $script:contractVersion = Get-PluginsContractVersion

    $script:entryAssemblyName = "TestHost"
    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1500-manifest-tests-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    # The repository's own contract assembly, which the fixtures put at the top level of app/ so that
    # the declared-version assertion is made against a real build rather than a hand-written row.
    $script:contractAssembly = Join-Path $script:repositoryRoot "src/plugins/EdFi.Api.Plugins/bin/Release/net10.0/EdFi.Api.Plugins.dll"
    $script:contractAssemblyAvailable = Test-Path -LiteralPath $script:contractAssembly

    # Real shared-framework assemblies from the installed runtime. The fixture's directory names, not
    # these files' versions, are what selection reads, so any installed 10.0.x serves for any name.
    #
    # Located through `dotnet --list-runtimes`, which the runtime itself owns and which prints the
    # containing directory on every platform. An earlier revision composed the path from
    # $env:ProgramFiles, which is unset on the Linux runner the whole eng/verification/tests directory
    # runs on, so BeforeAll threw on a null path and every case in this file failed before any of them
    # ran. A Windows-only variable cannot locate a runtime for a suite that runs on Linux.
    function Get-InstalledFrameworkDirectory {
        [CmdletBinding()]
        [OutputType([string])]
        param([Parameter(Mandatory)][string] $FrameworkName)

        $candidates = @()

        foreach ($line in (& dotnet --list-runtimes)) {
            if ($line -notmatch '^(\S+)\s+(\S+)\s+\[(.+)\]\s*$') {
                continue
            }
            if ($Matches[1] -ne $FrameworkName) {
                continue
            }

            # A prerelease version does not parse, and a directory this fixture cannot order is a
            # directory it should not pick.
            $parsed = [version] "0.0.0"
            if (-not [version]::TryParse($Matches[2], [ref] $parsed)) {
                continue
            }

            $candidates += [pscustomobject]@{
                Version = $parsed
                Path = Join-Path $Matches[3] $Matches[2]
            }
        }

        $best = @($candidates | Sort-Object -Property Version -Descending) | Select-Object -First 1

        if ($null -eq $best) {
            return $null
        }

        return $best.Path
    }

    $script:installedAspNet = Get-InstalledFrameworkDirectory -FrameworkName "Microsoft.AspNetCore.App"
    $script:installedNetCore = Get-InstalledFrameworkDirectory -FrameworkName "Microsoft.NETCore.App"

    $script:configurationAbstractions = if ($null -ne $script:installedAspNet) {
        Join-Path $script:installedAspNet "Microsoft.Extensions.Configuration.Abstractions.dll"
    }
    else {
        $null
    }

    # The downloader-only sentinel: a real assembly the fixture places under app/ApiSchemaDownloader/
    # and nowhere else, so a row for it can only mean the sweep went below the top level.
    $script:downloaderSentinelSource = if ($null -ne $script:installedAspNet) {
        Join-Path $script:installedAspNet "Microsoft.AspNetCore.Mvc.Core.dll"
    }
    else {
        $null
    }

    $script:runtimeAssembly = if ($null -ne $script:installedNetCore) {
        Join-Path $script:installedNetCore "System.Runtime.dll"
    }
    else {
        $null
    }

    $script:fixturesAvailable = $script:contractAssemblyAvailable -and
        $null -ne $script:configurationAbstractions -and (Test-Path -LiteralPath $script:configurationAbstractions) -and
        $null -ne $script:downloaderSentinelSource -and (Test-Path -LiteralPath $script:downloaderSentinelSource) -and
        $null -ne $script:runtimeAssembly -and (Test-Path -LiteralPath $script:runtimeAssembly)

    $script:sentinelAssemblyName = if ($script:fixturesAvailable) {
        [System.Reflection.AssemblyName]::GetAssemblyName($script:downloaderSentinelSource).Name
    }
    else {
        "Microsoft.AspNetCore.Mvc.Core"
    }

    # SupportsShouldProcess because the name carries a state-changing verb and PSScriptAnalyzer's
    # PSUseShouldProcessForStateChangingFunctions requires it.
    function New-ManifestFixture {
        [CmdletBinding(SupportsShouldProcess)]
        param(
            [Parameter(Mandatory)][string] $Name,

            # Version directories created under shared/Microsoft.NETCore.App and
            # shared/Microsoft.AspNetCore.App. The first entry is the one the assemblies land in.
            [string[]] $NetCoreVersions = @("10.0.3"),
            [string[]] $AspNetVersions = @("10.0.3"),

            # The runtimeconfig the entry application carries. Defaults to the shape the released
            # image has: a frameworks array naming both, with no roll-forward override.
            [string] $RuntimeConfig,

            # The version the fixture Dockerfile's FROM line carries, which the generator
            # cross-checks against the selected ASP.NET Core framework.
            [string] $BasePinVersion = "10.0.3",

            [switch] $OmitDownloaderSentinel,
            [switch] $IncludeNonManagedFile
        )

        $root = Join-Path $script:fixtureRoot "$Name-$([guid]::NewGuid().ToString('N'))"

        if (-not $PSCmdlet.ShouldProcess($root, "Create manifest fixture")) {
            return $null
        }

        $app = Join-Path $root "app"
        New-Item -ItemType Directory -Path $app -Force | Out-Null
        Copy-Item -LiteralPath $script:contractAssembly -Destination $app

        if (-not $OmitDownloaderSentinel) {
            $downloader = Join-Path $app "ApiSchemaDownloader"
            New-Item -ItemType Directory -Path $downloader -Force | Out-Null
            Copy-Item -LiteralPath $script:downloaderSentinelSource -Destination $downloader
        }

        if ($IncludeNonManagedFile) {
            # A .dll that is not a managed assembly. The host's own default context could not serve
            # it as one either, so it belongs in no section rather than failing generation.
            [System.IO.File]::WriteAllBytes((Join-Path $app "native-lookalike.dll"), [byte[]] (1..64))
        }

        $config = if ([string]::IsNullOrWhiteSpace($RuntimeConfig)) {
            @'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
      { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
    ]
  }
}
'@
        }
        else {
            $RuntimeConfig
        }

        [System.IO.File]::WriteAllText((Join-Path $app "$($script:entryAssemblyName).runtimeconfig.json"), $config)

        foreach ($pair in @(
                @{ Framework = "Microsoft.NETCore.App"; Versions = $NetCoreVersions; Assembly = $script:runtimeAssembly },
                @{ Framework = "Microsoft.AspNetCore.App"; Versions = $AspNetVersions; Assembly = $script:configurationAbstractions }
            )) {
            $first = $true
            foreach ($version in $pair.Versions) {
                $directory = Join-Path (Join-Path (Join-Path $root "shared") $pair.Framework) $version
                New-Item -ItemType Directory -Path $directory -Force | Out-Null

                # Only the first version directory is populated, so a case that expects a particular
                # directory to be selected fails loudly rather than silently reading an empty one.
                if ($first) {
                    Copy-Item -LiteralPath $pair.Assembly -Destination $directory
                    $first = $false
                }
            }
        }

        $dockerfile = Join-Path $root "Fixture.Dockerfile"
        [System.IO.File]::WriteAllText(
            $dockerfile,
            "FROM mcr.microsoft.com/dotnet/aspnet:$BasePinVersion-alpine3.23@sha256:0000 AS runtimebase`n"
        )

        return [pscustomobject]@{
            Root = $root
            Dockerfile = $dockerfile
            OutputPath = Join-Path $root "host-assembly-manifest.md"
        }
    }

    function Invoke-Generator {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][object] $Fixture,
            [AllowEmptyCollection()][string[]] $ImageEnvironment = @()
        )

        return & $script:generator `
            -ExtractedRoot $Fixture.Root `
            -ImageEnvironment $ImageEnvironment `
            -EntryAssemblyName $script:entryAssemblyName `
            -DockerfilePath $Fixture.Dockerfile `
            -OutputPath $Fixture.OutputPath
    }

    function Get-SectionRow {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string] $ManifestPath,
            [Parameter(Mandatory)][string] $HeadingPrefix
        )

        $rows = @()
        $inSection = $false

        foreach ($line in ([System.IO.File]::ReadAllText($ManifestPath)) -split "`n") {
            if ($line -match '^#{1,6}\s+(.+?)\s*$') {
                $inSection = $Matches[1].StartsWith($HeadingPrefix, [StringComparison]::Ordinal)
                continue
            }

            if (-not $inSection -or -not $line.StartsWith("|")) {
                continue
            }

            $cells = @(($line.Trim().Trim("|") -split "\|") | ForEach-Object { $_.Trim() })
            if ($cells.Count -ge 2 -and $cells[0] -ne "Assembly" -and $cells[0] -notmatch '^-{3,}$') {
                $rows += [pscustomobject]@{ Assembly = $cells[0]; Version = $cells[1] }
            }
        }

        return $rows
    }

    function Test-FixturesAvailable {
        [CmdletBinding()]
        [OutputType([bool])]
        param()

        return $script:fixturesAvailable
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "New-HostAssemblyManifest scanning scope" {

    It "lists the contract assembly from the top level of app at its declared version" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs the Release build of EdFi.Api.Plugins and an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "scope-happy"
        Invoke-Generator -Fixture $fixture | Out-Null

        $rows = Get-SectionRow -ManifestPath $fixture.OutputPath -HeadingPrefix "Application assemblies"
        $plugins = @($rows | Where-Object { $_.Assembly -eq "EdFi.Api.Plugins" })

        $plugins.Count | Should -Be 1
        $plugins[0].Version | Should -Be "$(($script:contractVersion -split '-', 2)[0]).0"
    }

    It "lists the shared-framework assembly a deps.json-based generator would omit" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "scope-shared"
        Invoke-Generator -Fixture $fixture | Out-Null

        $rows = Get-SectionRow -ManifestPath $fixture.OutputPath -HeadingPrefix "Shared framework: "

        @($rows | ForEach-Object { $_.Assembly }) |
            Should -Contain "Microsoft.Extensions.Configuration.Abstractions" -Because "a framework-dependent publish declares none of these, so only an image scan finds them"
    }

    It "excludes an assembly that exists only under app/ApiSchemaDownloader" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "scope-downloader"
        Invoke-Generator -Fixture $fixture | Out-Null

        $manifest = [System.IO.File]::ReadAllText($fixture.OutputPath)

        # Asserted by the sentinel's own assembly name rather than by the folder name, because most
        # of the downloader's dependencies have names that say nothing about where they live.
        $manifest | Should -Not -BeLike "*| $($script:sentinelAssemblyName) |*"
    }

    It "excludes a .dll that is not a managed assembly instead of failing" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "scope-native" -IncludeNonManagedFile
        { Invoke-Generator -Fixture $fixture } | Should -Not -Throw

        $rows = Get-SectionRow -ManifestPath $fixture.OutputPath -HeadingPrefix "Application assemblies"
        @($rows | ForEach-Object { $_.Assembly }) | Should -Not -Contain "native-lookalike"
    }
}

Describe "New-HostAssemblyManifest framework selection" {

    It "selects the highest patch inside the requested major.minor and never a higher major" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "select-major" -AspNetVersions @("10.0.3", "11.0.0")
        Invoke-Generator -Fixture $fixture | Out-Null

        $manifest = [System.IO.File]::ReadAllText($fixture.OutputPath)

        $manifest | Should -BeLike "*## Shared framework: Microsoft.AspNetCore.App 10.0.3*"
        $manifest | Should -Not -BeLike "*Microsoft.AspNetCore.App 11.0.0*"
    }

    It "selects the highest patch when several inside the requested major.minor are installed" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "select-patch" -AspNetVersions @("10.0.3", "10.0.1")
        Invoke-Generator -Fixture $fixture | Out-Null

        [System.IO.File]::ReadAllText($fixture.OutputPath) |
            Should -BeLike "*## Shared framework: Microsoft.AspNetCore.App 10.0.3*"
    }

    It "refuses a request that would need a minor roll forward" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "select-minor" -AspNetVersions @("10.1.0")

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*would need a minor or major roll forward*"
    }

    It "refuses an installed patch below the requested version" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $config = @'
{
  "runtimeOptions": {
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
      { "name": "Microsoft.AspNetCore.App", "version": "10.0.9" }
    ]
  }
}
'@

        $fixture = New-ManifestFixture -Name "select-floor" -AspNetVersions @("10.0.3") -RuntimeConfig $config

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*requested at 10.0.9 and the image carries only 10.0.3*"
    }

    It "refuses a framework the image does not carry at all" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $config = @'
{
  "runtimeOptions": {
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
      { "name": "Acme.Absent.App", "version": "1.0.0" }
    ]
  }
}
'@

        $fixture = New-ManifestFixture -Name "select-absent" -RuntimeConfig $config

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*carries no directory for shared framework 'Acme.Absent.App'*"
    }

    It "refuses a version directory whose name does not parse as a version" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "select-ambiguous" -AspNetVersions @("10.0.3", "preview")

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*does not parse as a version*"
    }

    It "accepts the singular framework object form" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $config = @'
{
  "runtimeOptions": {
    "framework": { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
  }
}
'@

        $fixture = New-ManifestFixture -Name "select-singular" -RuntimeConfig $config
        { Invoke-Generator -Fixture $fixture } | Should -Not -Throw

        [System.IO.File]::ReadAllText($fixture.OutputPath) |
            Should -BeLike "*## Shared framework: Microsoft.AspNetCore.App 10.0.3*"
    }
}

Describe "New-HostAssemblyManifest unsupported input" {

    It "refuses a roll-forward override in runtimeOptions" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $config = @'
{
  "runtimeOptions": {
    "rollForward": "Major",
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
      { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
    ]
  }
}
'@

        $fixture = New-ManifestFixture -Name "unsupported-rollforward" -RuntimeConfig $config

        { Invoke-Generator -Fixture $fixture } | Should -Throw -ExpectedMessage "*declares 'rollForward'*"
    }

    It "refuses a roll-forward override on a single framework reference" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $config = @'
{
  "runtimeOptions": {
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
      { "name": "Microsoft.AspNetCore.App", "version": "10.0.0", "rollForward": "LatestMinor" }
    ]
  }
}
'@

        $fixture = New-ManifestFixture -Name "unsupported-reference-rollforward" -RuntimeConfig $config

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*framework reference declares 'rollForward'*"
    }

    It "refuses the legacy rollForwardOnNoCandidateFx setting" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $config = @'
{
  "runtimeOptions": {
    "rollForwardOnNoCandidateFx": 2,
    "frameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
      { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
    ]
  }
}
'@

        $fixture = New-ManifestFixture -Name "unsupported-legacy" -RuntimeConfig $config

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*declares 'rollForwardOnNoCandidateFx'*"
    }

    It "refuses a self-contained publish" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $config = @'
{
  "runtimeOptions": {
    "includedFrameworks": [
      { "name": "Microsoft.NETCore.App", "version": "10.0.3" }
    ]
  }
}
'@

        $fixture = New-ManifestFixture -Name "unsupported-selfcontained" -RuntimeConfig $config

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*which is a self-contained publish*"
    }

    It "refuses an image that sets a roll-forward environment variable" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "unsupported-environment"

        { Invoke-Generator -Fixture $fixture -ImageEnvironment @("DOTNET_ROLL_FORWARD=Major") } |
            Should -Throw -ExpectedMessage "*declares DOTNET_ROLL_FORWARD.*"
    }

    It "refuses a selected framework whose own runtimeconfig depends on a version the image lacks" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        # A shared framework can pin a lower framework, and the released image's ASP.NET Core does.
        # This proves that dependency is read and put through the same selection rules rather than
        # taken on trust: a version above anything installed is refused here exactly as it would be
        # if the application had asked for it.
        $fixture = New-ManifestFixture -Name "dependency-unsatisfiable"

        $aspNetConfig = @'
{
  "runtimeOptions": {
    "rollForward": "LatestPatch",
    "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.9" }
  }
}
'@
        [System.IO.File]::WriteAllText(
            (Join-Path $fixture.Root "shared/Microsoft.AspNetCore.App/10.0.3/Microsoft.AspNetCore.App.runtimeconfig.json"),
            $aspNetConfig
        )

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*requested at 10.0.9 and the image carries only 10.0.3*"
    }

    It "refuses a selected framework that depends on a framework the application does not name" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "dependency-unknown"

        [System.IO.File]::WriteAllText(
            (Join-Path $fixture.Root "shared/Microsoft.AspNetCore.App/10.0.3/Microsoft.AspNetCore.App.runtimeconfig.json"),
            '{ "runtimeOptions": { "framework": { "name": "Acme.Hidden.App", "version": "1.0.0" } } }'
        )

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*which the application does not name*"
    }

    It "refuses a roll-forward policy on a selected framework's own nested framework reference" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        # Validating only the parent runtimeOptions let this through: the manifest was emitted
        # successfully while the nested entry asked for a policy the runtime would have applied and
        # this tool does not implement.
        $fixture = New-ManifestFixture -Name "nested-policy"

        [System.IO.File]::WriteAllText(
            (Join-Path $fixture.Root "shared/Microsoft.AspNetCore.App/10.0.3/Microsoft.AspNetCore.App.runtimeconfig.json"),
            '{ "runtimeOptions": { "rollForward": "LatestPatch", "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0", "rollForward": "Disable" } } }'
        )

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*Shared framework 'Microsoft.AspNetCore.App' framework reference declares 'rollForward'*"
    }

    It "refuses the legacy settings on a selected framework's own nested framework reference" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "nested-legacy"

        [System.IO.File]::WriteAllText(
            (Join-Path $fixture.Root "shared/Microsoft.AspNetCore.App/10.0.3/Microsoft.AspNetCore.App.runtimeconfig.json"),
            '{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0", "applyPatches": false } } }'
        )

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*framework reference declares 'applyPatches'*"
    }

    It "still accepts the shape the released image's ASP.NET Core framework actually carries" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        # Copied from the released image: LatestPatch on the parent, a plain nested reference. The
        # refusals above must not close on this.
        $fixture = New-ManifestFixture -Name "nested-released-shape"

        [System.IO.File]::WriteAllText(
            (Join-Path $fixture.Root "shared/Microsoft.AspNetCore.App/10.0.3/Microsoft.AspNetCore.App.runtimeconfig.json"),
            '{ "runtimeOptions": { "tfm": "net10.0", "rollForward": "LatestPatch", "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.3" } } }'
        )

        { Invoke-Generator -Fixture $fixture } | Should -Not -Throw
    }

    It "refuses a selected framework that declares a roll-forward policy other than LatestPatch" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "dependency-policy"

        [System.IO.File]::WriteAllText(
            (Join-Path $fixture.Root "shared/Microsoft.AspNetCore.App/10.0.3/Microsoft.AspNetCore.App.runtimeconfig.json"),
            '{ "runtimeOptions": { "rollForward": "LatestMinor" } }'
        )

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*declares rollForward 'LatestMinor'*"
    }
}

Describe "New-HostAssemblyManifest header cross-checks" {

    It "refuses a checkout whose base image pin disagrees with the inspected image" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "pin-mismatch" -BasePinVersion "10.0.9"

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*not the same pair*"
    }

    It "refuses a base pin whose version merely begins with the selected one" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        # 10.0.3 is a substring of 10.0.30. An unanchored text comparison accepted this pair, which
        # is the wrong answer in the direction that matters: it lets a checkout and an image that are
        # not the same pair produce a manifest claiming they are.
        $fixture = New-ManifestFixture -Name "pin-prefix" -BasePinVersion "10.0.30"

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*carrying 10.0.30, while the inspected image carries 10.0.3*"
    }

    It "refuses a base pin whose version merely ends with the selected one" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "pin-suffix" -BasePinVersion "110.0.3"

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*carrying 110.0.3, while the inspected image carries 10.0.3*"
    }

    It "admits a base pin whose version equals the selected one" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "pin-exact" -BasePinVersion "10.0.3"

        { Invoke-Generator -Fixture $fixture } | Should -Not -Throw
    }

    It "refuses a Dockerfile with no runtime base stage rather than reading its first FROM" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "pin-no-stage"

        [System.IO.File]::WriteAllText(
            $fixture.Dockerfile,
            "FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-alpine3.23 AS somethingelse`nFROM somethingelse AS setup`n"
        )

        { Invoke-Generator -Fixture $fixture } |
            Should -Throw -ExpectedMessage "*No 'FROM <image> AS runtimebase' instruction found*"
    }

    It "reads the named runtime base stage rather than whichever FROM comes first" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        # The shape src/dms/Nuget.Dockerfile has: the runtime base first, then a stage built on it.
        # Reordered here so that reading the first FROM would pick up a stage name rather than an
        # image reference and the version cross-check would have nothing to parse.
        $fixture = New-ManifestFixture -Name "pin-stage-order"

        [System.IO.File]::WriteAllText(
            $fixture.Dockerfile,
            @(
                "FROM scratch AS preamble",
                "FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-alpine3.23@sha256:0000 AS runtimebase",
                "FROM runtimebase AS setup",
                ""
            ) -join "`n"
        )

        { Invoke-Generator -Fixture $fixture } | Should -Not -Throw

        [System.IO.File]::ReadAllText($fixture.OutputPath) |
            Should -BeLike "*mcr.microsoft.com/dotnet/aspnet:10.0.3-alpine3.23@sha256:0000*"
    }

    It "refuses an image whose declared runtime version disagrees with what it carries" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "declared-mismatch"

        { Invoke-Generator -Fixture $fixture -ImageEnvironment @("ASPNET_VERSION=10.0.9") } |
            Should -Throw -ExpectedMessage "*The extraction and the image disagree*"
    }

    It "records the base pin as a fact about the checkout rather than as image provenance" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "pin-labelled"
        Invoke-Generator -Fixture $fixture | Out-Null

        $manifest = [System.IO.File]::ReadAllText($fixture.OutputPath)

        $manifest | Should -BeLike "*in this checkout*"
        $manifest | Should -BeLike "*not*provenance for the image*"
    }
}

Describe "Assert-HostAssemblyManifest" {

    It "admits a generated manifest at the contract's declared version" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs the Release build of EdFi.Api.Plugins"
        }

        $fixture = New-ManifestFixture -Name "assert-happy"
        Invoke-Generator -Fixture $fixture | Out-Null

        & $script:verifier -ManifestPath $fixture.OutputPath -ExpectedPluginsVersion $script:contractVersion |
            Should -BeLike "*Microsoft.Extensions.Configuration.Abstractions present*"
    }

    It "refuses a manifest stating a contract version other than the declared one" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs the Release build of EdFi.Api.Plugins"
        }

        $fixture = New-ManifestFixture -Name "assert-version"
        Invoke-Generator -Fixture $fixture | Out-Null

        {
            & $script:verifier -ManifestPath $fixture.OutputPath -ExpectedPluginsVersion "9.9.9"
        } | Should -Throw -ExpectedMessage "*The loader's skew preflight compares this value*"
    }

    It "refuses a manifest whose shared-framework section lost Configuration.Abstractions" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        # What a regression to a .deps.json-based generator looks like in the output.
        $fixture = New-ManifestFixture -Name "assert-deps-regression"
        Invoke-Generator -Fixture $fixture | Out-Null

        $manifest = [System.IO.File]::ReadAllText($fixture.OutputPath).Replace(
            "| Microsoft.Extensions.Configuration.Abstractions |", "| Microsoft.Extensions.Something.Else |")
        [System.IO.File]::WriteAllText($fixture.OutputPath, $manifest)

        {
            & $script:verifier -ManifestPath $fixture.OutputPath -ExpectedPluginsVersion $script:contractVersion
        } | Should -Throw -ExpectedMessage "*would omit it*"
    }

    It "refuses a manifest that lists the downloader's entry assembly" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $fixture = New-ManifestFixture -Name "assert-downloader"
        Invoke-Generator -Fixture $fixture | Out-Null

        # A complete two-column row, because a malformed one is not a row the verifier parses and the
        # case would pass without ever exercising the sentinel.
        $manifest = [System.IO.File]::ReadAllText($fixture.OutputPath).Replace(
            "| EdFi.Api.Plugins |",
            "| EdFi.DataManagementService.ApiSchemaDownloader | 8.0.1.0 |`n| EdFi.Api.Plugins |")
        [System.IO.File]::WriteAllText($fixture.OutputPath, $manifest)

        {
            & $script:verifier -ManifestPath $fixture.OutputPath -ExpectedPluginsVersion $script:contractVersion
        } | Should -Throw -ExpectedMessage "*reaching below the top level of /app*"
    }

    It "refuses a manifest carrying fewer than the two shared-framework sections" {
        if (-not (Test-FixturesAvailable)) {
            Set-ItResult -Inconclusive -Because "the fixture needs an installed .NET shared framework"
        }

        $config = @'
{
  "runtimeOptions": {
    "framework": { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
  }
}
'@

        $fixture = New-ManifestFixture -Name "assert-one-framework" -RuntimeConfig $config
        Invoke-Generator -Fixture $fixture | Out-Null

        {
            & $script:verifier -ManifestPath $fixture.OutputPath -ExpectedPluginsVersion $script:contractVersion
        } | Should -Throw -ExpectedMessage "*shared-framework section(s)*"
    }
}
