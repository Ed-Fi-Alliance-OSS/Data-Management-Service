# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Attaches files to a GitHub release so that a retry completes a partial attach. GitHub refuses an
# upload under a name the release already has, so a job that uploads blindly fails on its retry once
# any asset reached the release. Here every asset is checked before any is uploaded: one the release
# lacks is uploaded, one it already has with the same content is left as it is, and one it has with
# different content is refused, because a release asset is never replaced.

$ErrorActionPreference = 'Stop'
# No -Force, for the reason package-helpers.psm1 gives.
Import-Module (Join-Path $PSScriptRoot 'release-classification.psm1')

function Get-GitHubRelease {
    <#
    .SYNOPSIS
        Returns the GitHub release for a tag, or $null when the repository has none.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]
        $Repository,

        [Parameter(Mandatory)]
        [string]
        $Tag,

        [Parameter(Mandatory)]
        [string]
        $Token
    )

    $headers = @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $Token"
    }
    try {
        return Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/tags/$([uri]::EscapeDataString($Tag))" -Headers $headers
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 404) { return $null }
        throw
    }
}

function Save-GitHubReleaseAsset {
    <#
    .SYNOPSIS
        Downloads one existing release asset to a path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]
        $Asset,

        [Parameter(Mandatory)]
        [string]
        $Destination,

        [Parameter(Mandatory)]
        [string]
        $Token
    )

    $headers = @{
        Accept = 'application/octet-stream'
        Authorization = "Bearer $Token"
    }
    Invoke-WebRequest -Uri $Asset.url -Headers $headers -OutFile $Destination
}

function Send-GitHubReleaseAsset {
    <#
    .SYNOPSIS
        Uploads a file to a release as an asset with the given name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]
        $Repository,

        [Parameter(Mandatory)]
        [object]
        $Release,

        [Parameter(Mandatory)]
        [string]
        $Name,

        [Parameter(Mandatory)]
        [string]
        $Path,

        [Parameter(Mandatory)]
        [string]
        $Token
    )

    $headers = @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $Token"
    }
    $uri = "https://uploads.github.com/repos/$Repository/releases/$($Release.id)/assets?name=$([uri]::EscapeDataString($Name))"
    Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType 'application/octet-stream' -InFile $Path | Out-Null
}

function Get-SingleZipEntrySha256 {
    <#
    .SYNOPSIS
        Returns the SHA256 of a zip's only entry when it has exactly one, with the given name, else $null.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]
        $ZipFile,

        [Parameter(Mandatory)]
        [string]
        $EntryName
    )

    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($ZipFile)
    }
    catch [IO.InvalidDataException] {
        return $null
    }
    try {
        if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne $EntryName) { return $null }
        $stream = $archive.Entries[0].Open()
        try { return (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Publish-ReleaseAsset {
    <#
    .SYNOPSIS
        Attaches files to the GitHub release for a tag, completing a partial attach and never
        replacing an asset.
    .DESCRIPTION
        The tag and every asset are checked before the release is looked up, and every asset is
        compared with the release before any is uploaded. An asset the release lacks is uploaded. An
        asset it already has is downloaded and compared: a Bytes asset byte for byte, and a ZipEntry
        asset by the one file in its zip, because Compress-Archive does not write the same zip bytes
        twice. An equal asset is left as it is, so a retry completes a partial attach. A different one
        throws before anything is uploaded.
    .PARAMETER Repository
        The repository as owner/name, for example $env:GITHUB_REPOSITORY.
    .PARAMETER ReleaseTag
        The tag of the release, a v version such as v8.1.0 or v8.1.0-beta.0.1.
    .PARAMETER Token
        A GitHub token that can read the release and upload assets to it.
    .PARAMETER Asset
        Each asset as a hashtable or object with Name, Path and Comparison. With Bytes, the file at
        Path is uploaded as Name. With ZipEntry, the file at Path is zipped alone, under its file name,
        and the zip is uploaded as Name.
    .PARAMETER GetRelease
        Returns the release for a repository, tag and token, or $null when there is none.
    .PARAMETER DownloadAsset
        Downloads an existing asset of the release to a path, with a token.
    .PARAMETER UploadAsset
        Uploads a file to the release of a repository as a named asset, with a token.
    #>
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    param(
        [Parameter(Mandatory)]
        [string]
        $Repository,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $ReleaseTag,

        [Parameter(Mandatory)]
        [string]
        $Token,

        [Parameter(Mandatory)]
        [object[]]
        $Asset,

        [scriptblock]
        $GetRelease = { param($Repository, $Tag, $Token) Get-GitHubRelease -Repository $Repository -Tag $Tag -Token $Token },

        [scriptblock]
        $DownloadAsset = { param($Asset, $Destination, $Token) Save-GitHubReleaseAsset -Asset $Asset -Destination $Destination -Token $Token },

        [scriptblock]
        $UploadAsset = {
            param($Repository, $Release, $Name, $Path, $Token)
            Send-GitHubReleaseAsset -Repository $Repository -Release $Release -Name $Name -Path $Path -Token $Token
        }
    )

    Assert-ReleaseRef -ReleaseRef $ReleaseTag -Prefix v
    if ($Repository -cnotmatch '\A[A-Za-z0-9-]+/[A-Za-z0-9._-]+\z') {
        throw "Repository '$([regex]::Replace($Repository, '[^\x20-\x7E]', '?'))' is not an owner/name repository."
    }

    $assets = @(foreach ($item in $Asset) {
            $name = [string] $item.Name
            $path = [string] $item.Path
            $comparison = [string] $item.Comparison
            if ([string]::IsNullOrWhiteSpace($name)) {
                throw 'Every release asset needs a Name.'
            }
            if ($comparison -cnotin 'Bytes', 'ZipEntry') {
                throw "Release asset $name has Comparison '$comparison'. It must be Bytes or ZipEntry."
            }
            if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Release asset $name has no file at '$path'."
            }
            [pscustomobject]@{ Name = $name; Path = (Resolve-Path -LiteralPath $path).Path; Comparison = $comparison }
        })
    if ($assets.Count -eq 0) {
        throw 'No release asset was given.'
    }
    foreach ($group in $assets | Group-Object -Property Name -CaseSensitive | Where-Object Count -gt 1) {
        throw "Release asset $($group.Name) is named more than once."
    }

    $release = & $GetRelease $Repository $ReleaseTag $Token
    if ($null -eq $release) {
        throw "Release tag '$ReleaseTag' has no GitHub release in $Repository to attach $($assets.Name -join ' and ') to."
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) "release-assets-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $toUpload = [Collections.Generic.List[object]]::new()
        for ($index = 0; $index -lt $assets.Count; $index++) {
            $item = $assets[$index]
            $expected = (Get-FileHash -LiteralPath $item.Path -Algorithm SHA256).Hash
            $upload = $item.Path
            if ($item.Comparison -ceq 'ZipEntry') {
                $upload = Join-Path $work "upload-$index.zip"
                Compress-Archive -LiteralPath $item.Path -DestinationPath $upload
            }

            $existing = @($release.assets | Where-Object { $_.name -ceq $item.Name })
            if ($existing.Count -eq 0) {
                $toUpload.Add([pscustomobject]@{ Name = $item.Name; Path = $upload })
                continue
            }

            $downloaded = Join-Path $work "existing-$index"
            & $DownloadAsset $existing[0] $downloaded $Token
            $actual = if ($item.Comparison -ceq 'ZipEntry') {
                Get-SingleZipEntrySha256 -ZipFile $downloaded -EntryName ([IO.Path]::GetFileName($item.Path))
            }
            else {
                (Get-FileHash -LiteralPath $downloaded -Algorithm SHA256).Hash
            }
            if ($actual -cne $expected) {
                throw "Release $ReleaseTag already has an asset named $($item.Name) with different content. A release asset is never replaced, so nothing was uploaded."
            }
            Write-Information "Release $ReleaseTag already has $($item.Name) with this content; it is left as it is."
        }

        foreach ($item in $toUpload) {
            if ($PSCmdlet.ShouldProcess("$($item.Name) on release $ReleaseTag", 'Upload')) {
                Write-Information "Uploading $($item.Name) to release $ReleaseTag."
                & $UploadAsset $Repository $release $item.Name $item.Path $Token
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}

Export-ModuleMember -Function Publish-ReleaseAsset
