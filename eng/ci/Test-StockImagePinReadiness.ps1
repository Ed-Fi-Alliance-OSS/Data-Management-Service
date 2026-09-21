# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
    .SYNOPSIS
        Decides whether the stock-image plugin proof has a publication to run against.
    .DESCRIPTION
        The proof names one published Ed-Fi API image. Until the qualifying release exists there is
        nothing to name, so the committed pin carries nulls and a status of "pending".

        Pending and invalid are different answers and this script keeps them apart. A pending pin on
        a schedule is the expected state before publication: it reports not-ready, exits zero, and
        the caller skips the proof without recording a result. Everything else - a malformed
        document, an unknown status, a pending pin carrying values, a published pin missing or
        malforming any of them - throws, because a pin that cannot be trusted must not be able to
        masquerade as "not published yet". A run someone asked for by hand also throws on a pending
        pin: they asked for the proof, and silence would look like it had run.

        Coherence is checked rather than assumed. The pinned tag must be the tag the pinned GitHub
        release actually produces, which Get-DmsPrereleaseImageTag.ps1 computes - the same rule the
        publication workflow runs, so the two cannot drift.
    .PARAMETER PinFile
        The pin document to read.
    .PARAMETER EventName
        The GitHub event this is running for. Any value other than "schedule" is treated as a
        deliberate request, on which a pending pin is a failure.
    .PARAMETER OutputPath
        Destination for the key=value lines. Defaults to $GITHUB_OUTPUT; when neither is set the
        lines go to standard output so the script can be run by hand.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]
    $PinFile,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]
    $EventName,

    [string]
    $OutputPath = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'

$edFiApiRepository = 'edfialliance/ed-fi-api'
$configurationServiceRepository = 'edfialliance/ed-fi-api-configuration-service'
$digestPattern = '^sha256:[0-9a-f]{64}$'
$commitPattern = '^[0-9a-f]{40}$'
$movingTag = @('pre', 'latest')

# One version identity and nothing else: "1.0.0" or "1.0.0-alpha.3", which is the form that names a
# single package. A range like "[1.0.0,2.0.0)" and a wildcard like "1.0.*" name a set rather than a
# version and are refused. The bracketed exact form "[1.0.0]" is refused as well: it selects the
# right package, but it is NuGet resolution syntax rather than a version, and accepting two
# spellings of one value is how the pin and the harness end up disagreeing about what was pinned.
#
# This pattern records identity. It does not by itself make a restore resolve that exact version -
# NuGet reads a bare version in a PackageReference as a floor - so the harness brackets the value
# when it writes a reference and verifies the restored version against the pin afterwards.
$exactVersionPattern = '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?(\+[0-9A-Za-z][0-9A-Za-z.-]*)?$'
$feedUrlPattern = '^https://'

if (-not (Test-Path -LiteralPath $PinFile -PathType Leaf)) {
    throw "The stock image pin file does not exist: $PinFile"
}

try {
    $pin = Get-Content -LiteralPath $PinFile -Raw | ConvertFrom-Json
}
catch {
    throw "The stock image pin file is not valid JSON: $PinFile. $($_.Exception.Message)"
}

# Reads a nested value without throwing on an absent parent, so a document missing a whole section
# produces the same named diagnostic as one missing a single field.
function Get-PinValue {
    param([Parameter(Mandatory)] [string[]] $Path)

    $node = $pin
    foreach ($segment in $Path) {
        if ($null -eq $node -or $null -eq $node.PSObject.Properties[$segment]) {
            return $null
        }

        $node = $node.$segment
    }

    # Comma-wrapped, so an array value survives the return. A bare `return $node` is enumerated by
    # the pipeline, which turns a one-element array into its element and would let a single object
    # be mistaken for a singleton array by every caller.
    return , $node
}

function Get-PinName([string[]]$Path) {
    return ($Path -join '.')
}

function Assert-PinValue {
    param(
        [Parameter(Mandatory)] [string[]] $Path,
        [string] $Pattern,
        [string[]] $NotIn = @(),
        [string] $MustEqual
    )

    $value = Get-PinValue -Path $Path
    $name = Get-PinName $Path

    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        throw "The stock image pin is published but $name is missing. A published pin must carry every field; publication is not partially recordable."
    }

    $text = [string]$value

    if ($PSBoundParameters.ContainsKey('MustEqual') -and $text -cne $MustEqual) {
        throw "The stock image pin's $name is '$text', but this proof is only about '$MustEqual'."
    }

    # Case-sensitive. -notmatch ignores case, and a digest is lower-case hex by definition: an
    # upper-case one would pass a case-insensitive check here and then be refused by the daemon,
    # which is the worst place to find out.
    if (-not [string]::IsNullOrEmpty($Pattern) -and $text -cnotmatch $Pattern) {
        throw "The stock image pin's $name is '$text', which does not match $Pattern."
    }

    foreach ($rejected in $NotIn) {
        if ($text -ceq $rejected) {
            throw "The stock image pin's $name is '$text', a moving tag. This proof has to name one artifact, and a moving tag names whichever one was published last."
        }
    }

    return $text
}

# Every field the published branch requires, so the pending branch can say the document is
# incoherent using exactly the same list.
$requiredPath = @(
    , @('edFiApi', 'tag')
    , @('edFiApi', 'digest')
    , @('configurationService', 'digest')
    , @('release', 'githubRelease')
    , @('release', 'sourceCommit')
    , @('release', 'publicationRunUrl')
    , @('provisioning', 'schemaToolsPackageVersion')
    , @('provisioning', 'dataStandardVersion')
    , @('provisioning', 'schemaPackages')
    , @('contracts', 'pluginsPackageVersion')
    , @('contracts', 'customValidationPackageVersion')
)

$status = [string](Get-PinValue -Path @('status'))

if ($status -cnotin @('pending', 'published')) {
    throw "The stock image pin's status is '$status'. It has to be 'pending' or 'published'; an unrecognized status is not a reason to skip the proof."
}

$ready = $false
$reason = ''

if ($status -ceq 'pending') {
    # A pending pin carrying values is not pending, it is a half-recorded publication, and skipping
    # on it would hide whichever half is wrong.
    $populated = @($requiredPath | Where-Object { -not [string]::IsNullOrWhiteSpace([string](Get-PinValue -Path $_)) })

    if ($populated.Count -gt 0) {
        $names = ($populated | ForEach-Object { Get-PinName $_ }) -join ', '
        throw "The stock image pin's status is 'pending' but it already carries $names. Set status to 'published' once the release is recorded, or clear those fields."
    }

    if ($EventName -cne 'schedule') {
        throw "The stock image pin is still pending, so there is no published image to prove anything against. This run was requested deliberately, so it fails rather than reporting success without running the proof."
    }

    $reason = 'The qualifying publication has not happened yet, so the stock-image proof did not run. This is not evidence that it passes.'
}
else {
    Assert-PinValue -Path @('edFiApi', 'repository') -MustEqual $edFiApiRepository | Out-Null
    Assert-PinValue -Path @('configurationService', 'repository') -MustEqual $configurationServiceRepository | Out-Null

    $tag = Assert-PinValue -Path @('edFiApi', 'tag') -NotIn $movingTag
    Assert-PinValue -Path @('edFiApi', 'digest') -Pattern $digestPattern | Out-Null
    Assert-PinValue -Path @('configurationService', 'digest') -Pattern $digestPattern | Out-Null

    # A tag carrying its own digest would pass every check above and then be pinned twice, once
    # coherently and once not.
    if ($tag.Contains('@')) {
        throw "The stock image pin's edFiApi.tag is '$tag', which carries a digest. The tag and the digest are separate fields."
    }

    $release = Assert-PinValue -Path @('release', 'githubRelease')
    Assert-PinValue -Path @('release', 'sourceCommit') -Pattern $commitPattern | Out-Null
    Assert-PinValue -Path @('release', 'publicationRunUrl') -Pattern '^https://' | Out-Null
    Assert-PinValue -Path @('provisioning', 'schemaToolsPackageVersion') -Pattern $exactVersionPattern | Out-Null
    Assert-PinValue -Path @('provisioning', 'dataStandardVersion') | Out-Null
    Assert-PinValue -Path @('contracts', 'pluginsPackageVersion') -Pattern $exactVersionPattern | Out-Null
    Assert-PinValue -Path @('contracts', 'customValidationPackageVersion') -Pattern $exactVersionPattern | Out-Null

    # The schema package set, which is what actually selects schema content: dataStandardVersion
    # above is only the label the container reports. Each entry has to name the package, an exact
    # version and the feed it came from, so neither a catalog default nor an ambient feed can
    # substitute an input the pinned release was never verified against.
    $schemaPackage = Get-PinValue -Path @('provisioning', 'schemaPackages')

    if ($null -eq $schemaPackage) {
        throw "The stock image pin is published but provisioning.schemaPackages is missing. A published pin must carry every field; publication is not partially recordable."
    }

    # An array, not merely something enumerable. A single object would otherwise be read as a
    # one-package set, and the harness serializes the original field to SCHEMA_PACKAGES, so the set
    # this validated and the set the deployment consumes have to be the same shape.
    if ($schemaPackage -isnot [System.Collections.IList]) {
        throw "The stock image pin's provisioning.schemaPackages is a $($schemaPackage.GetType().Name) rather than an array. It has to be an array even when it names one package, because it is written out verbatim as SCHEMA_PACKAGES."
    }

    if ($schemaPackage.Count -eq 0) {
        throw "The stock image pin is published but provisioning.schemaPackages is empty. It has to name every schema package the pinned release was verified against; an empty set would let the catalog pick."
    }

    # Every entry is validated at its own index, and nothing is filtered out. Dropping a malformed
    # entry would validate a set the deployment never receives: the whole field is what gets
    # written to SCHEMA_PACKAGES, holes included.
    for ($index = 0; $index -lt $schemaPackage.Count; $index++) {
        $package = $schemaPackage[$index]
        $where = "provisioning.schemaPackages[$index]"

        if ($null -eq $package) {
            throw "The stock image pin's $where is null. A null entry names no package, and it is carried into SCHEMA_PACKAGES as written."
        }

        if ($package -is [string] -or $package -is [ValueType] -or $package -is [System.Collections.IList]) {
            throw "The stock image pin's $where is a $($package.GetType().Name) rather than an object carrying name, version and feedUrl."
        }

        foreach ($field in @('name', 'version', 'feedUrl')) {
            if ($package.PSObject.Properties.Name -notcontains $field -or
                [string]::IsNullOrWhiteSpace([string]$package.$field)) {
                throw "The stock image pin's $where is missing $field. A schema package is identified by its name, an exact version and the feed it came from."
            }
        }

        if ([string]$package.version -cnotmatch $exactVersionPattern) {
            throw "The stock image pin's $where.version is '$($package.version)', which is not an exact version identity. A range or a wildcard names a set of packages rather than one."
        }

        if ([string]$package.feedUrl -cnotmatch $feedUrlPattern) {
            throw "The stock image pin's $where.feedUrl is '$($package.feedUrl)', which is not an https feed address."
        }
    }

    # The tag the recorded release actually publishes, computed by the rule the publication workflow
    # runs rather than restated here. A pin naming a release and a tag that release never produced
    # describes no artifact at all.
    $expected = & (Join-Path $PSScriptRoot 'Get-DmsPrereleaseImageTag.ps1') `
        -ReleaseRef $release -ImageName $edFiApiRepository -OutputPath ''
    $expectedTag = ($expected | Where-Object { $_ -like 'DMSTAGS=*' }) -replace '^DMSTAGS=', ''

    if ("${edFiApiRepository}:$tag" -cnotin $expectedTag.Split(',')) {
        throw "The stock image pin names release '$release' and tag '$tag', but that release publishes $expectedTag. The pinned tag has to be one the pinned release produced."
    }

    $ready = $true
    $reason = "Pinned to $edFiApiRepository`:$tag at $(Get-PinValue -Path @('edFiApi', 'digest')) from release $release."
}

$lines = @(
    "ready=$($ready.ToString().ToLowerInvariant())"
    "reason=$reason"
)

# Echoed to the log as well as the output file: when the proof is skipped, this is the line that
# says why, and it has to be readable without opening the pin.
$lines | ForEach-Object { Write-Output $_ }

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    Add-Content -LiteralPath $OutputPath -Value $lines
}
