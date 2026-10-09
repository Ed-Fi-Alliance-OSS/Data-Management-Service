# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Decides whether a dms-pre- or cs-pre- prerelease was built for a v tag (a tagged release build) or
# for an ordinary merge to main. The merge-or-tag workflows create the same kind of prerelease for
# both, so on-prerelease.yml and on-release.yml read the answer from here.
#
# The rule is "the v tag for this version exists". It replaces "the version does not contain
# alpha", which only held while the newest v tag was a final release: MinVer then gives main builds
# X.Y.Z-alpha.0.N. After a prerelease v tag such as v8.1.0-beta.0.1, main builds are
# 8.1.0-beta.0.1.N, which carry no "alpha" and are still not releases.

$script:PrereleasePrefix = @('dms-pre-', 'cs-pre-')

function Get-PrereleaseVersion {
    <#
    .SYNOPSIS
        Returns the version a dms-pre- or cs-pre- prerelease names, or nothing for any other ref.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $ReleaseRef
    )

    foreach ($prefix in $script:PrereleasePrefix) {
        if ($ReleaseRef.StartsWith($prefix, [StringComparison]::Ordinal) -and $ReleaseRef.Length -gt $prefix.Length) {
            return $ReleaseRef.Substring($prefix.Length)
        }
    }
}

function Get-ReleaseTagVersion {
    <#
    .SYNOPSIS
        Returns the version of every v tag on the remote, once each.
    .DESCRIPTION
        ls-remote lists an annotated tag twice, as the tag and as its peeled commit (^{}); both
        name the same version. A remote that cannot be read throws: answering "not tagged" would
        publish a release build as an ordinary one.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [string]
        $Remote
    )

    $lines = & git ls-remote --tags $Remote 'refs/tags/v*' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list the v tags on '$Remote' (git ls-remote exited $LASTEXITCODE): $($lines -join ' ')"
    }

    return [string[]]@(
        $lines |
            ForEach-Object { ([string]$_).Split("`t")[-1] } |
            Where-Object { $_.StartsWith('refs/tags/v', [StringComparison]::Ordinal) } |
            ForEach-Object { $_.Substring('refs/tags/v'.Length) -replace '\^\{\}$', '' } |
            Sort-Object -Unique
    )
}

function Test-TaggedReleaseBuild {
    <#
    .SYNOPSIS
        Returns whether a dms-pre- or cs-pre- prerelease was built for a v tag.
    .PARAMETER TaggedVersion
        The versions Get-ReleaseTagVersion returned, read once and reused across many refs.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]
        $ReleaseRef,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]
        $TaggedVersion
    )

    $version = Get-PrereleaseVersion -ReleaseRef $ReleaseRef
    if (-not $version) {
        throw "'$ReleaseRef' is not a dms-pre- or cs-pre- prerelease, so it has no version to classify."
    }

    return $TaggedVersion -ccontains $version
}

Export-ModuleMember -Function Get-PrereleaseVersion, Get-ReleaseTagVersion, Test-TaggedReleaseBuild
