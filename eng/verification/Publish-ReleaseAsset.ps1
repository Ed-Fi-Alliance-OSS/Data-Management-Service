# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Attaches one file to a GitHub release, idempotently and without ever replacing valid evidence.

.DESCRIPTION
    A release asset is attached at most once per name, and a retry after a partial failure has to be
    safe. So the release's assets are read first, and the outcome depends on what is already there:

      - no asset of that name          -> upload
      - one asset in state "starter"   -> an interrupted upload left it; delete it, then upload
      - one asset in state "uploaded"  -> download it and compare content digests: equal skips,
                                          different fails, because the release already carries
                                          evidence for other bytes and nothing here may replace it
      - more than one asset, or any other state -> fail

    "Content digest" is a scriptblock so the caller decides what equality means: the SBOM asset is a
    zip whose bytes are not reproducible, so its digest is the manifest's; a provenance file is
    compared as whole bytes. The local file's digest is computed before anything is read or written
    remotely, which is also where a malformed local archive fails, and when the caller supplies the
    digest it expects the file to have, a mismatch fails before the first request.

.NOTES
    The four GitHub calls are injectable seams so every outcome is testable without a token or a
    network. The defaults perform the real REST requests and need -Token.

.OUTPUTS
    One object typed EdFi.ReleaseAssetPublication with Action (uploaded | skipped | replaced-starter),
    AssetName, Digest and AssetId.
#>
[CmdletBinding()]
[OutputType([pscustomobject])]
param(
    # owner/name.
    [Parameter(Mandatory)]
    [string]
    $Repository,

    [Parameter(Mandatory)]
    [string]
    $ReleaseId,

    [Parameter(Mandatory)]
    [string]
    $AssetName,

    [Parameter(Mandatory)]
    [string]
    $FilePath,

    # A scratch directory for the download of an existing asset. Must be empty or absent.
    [Parameter(Mandatory)]
    [string]
    $WorkingDirectory,

    # Required by the default seams; unused when all four are injected.
    [string]
    $Token = "",

    # Path -> digest string. Defaults to the SHA-256 of the file's bytes.
    [scriptblock]
    $ContentDigest,

    # When supplied, the local file's content digest must equal this before anything else happens.
    [string]
    $ExpectedDigest = "",

    [string]
    $ApiBaseUrl = "https://api.github.com",

    [string]
    $UploadBaseUrl = "https://uploads.github.com",

    # Returns every asset of the release, or throws.
    [scriptblock]
    $GetAssets,

    # Downloads one asset to a path, or throws.
    [scriptblock]
    $DownloadAsset,

    # Deletes one asset, or throws.
    [scriptblock]
    $DeleteAsset,

    # Uploads the file under the asset name and returns the created asset, or throws.
    [scriptblock]
    $UploadAsset
)

$ErrorActionPreference = "Stop"

# Imported once per session rather than with -Force. A -Force import from this nested script would
# unload the instance a caller's script blocks are bound to: a -ContentDigest block handed in by
# Invoke-ContractEvidenceBackfill.ps1 would then stop resolving the module's functions mid-call.
$evidenceModulePath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "ContractEvidence.psm1"))

if (-not @(Get-Module | Where-Object { [System.IO.Path]::GetFullPath([string] $_.Path) -eq $evidenceModulePath }).Count) {
    Import-Module $evidenceModulePath
}

function Get-GitHubHeader {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory)][string] $Token, [string] $Accept = "application/vnd.github+json")

    return @{
        Accept                 = $Accept
        Authorization          = "Bearer $Token"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
}

$usingDefaultSeam = ($null -eq $GetAssets) -or ($null -eq $DownloadAsset) -or ($null -eq $DeleteAsset) -or ($null -eq $UploadAsset)

if ($usingDefaultSeam -and [string]::IsNullOrWhiteSpace($Token)) {
    throw "A token is required to read, delete or upload release assets. Pass -Token, or inject all four seams."
}

if ($null -eq $GetAssets) {
    $GetAssets = {
        param([string] $ApiBaseUrl, [string] $Repository, [string] $ReleaseId, [string] $Token)

        $headers = Get-GitHubHeader -Token $Token
        $assets = [System.Collections.Generic.List[object]]::new()
        $page = 1

        # Paginated to the end. A release with more than one page of assets would otherwise hide the
        # one being looked for and the upload would collide with it.
        do {
            $url = "$ApiBaseUrl/repos/$Repository/releases/$ReleaseId/assets?per_page=100&page=$page"

            try {
                $batch = @(Invoke-RestMethod -Uri $url -Headers $headers -Method Get)
            }
            catch {
                throw "The release's assets could not be listed from $url : $($_.Exception.Message)"
            }

            foreach ($asset in $batch) {
                $assets.Add($asset)
            }

            $page++
        } while ($batch.Count -eq 100)

        # Written to the pipeline as separate objects; the caller collects them with @(). A unary
        # comma here would hand the caller one array object instead of the assets.
        return $assets.ToArray()
    }
}

if ($null -eq $DownloadAsset) {
    $DownloadAsset = {
        param($Asset, [string] $Destination, [string] $Token)

        # The asset's API url with the octet-stream accept header answers with a redirect to the
        # bytes; Invoke-WebRequest follows it.
        $headers = Get-GitHubHeader -Token $Token -Accept "application/octet-stream"

        try {
            Invoke-WebRequest -Uri $Asset.url -Headers $headers -Method Get -OutFile $Destination
        }
        catch {
            throw "The existing asset $($Asset.name) could not be downloaded from $($Asset.url): $($_.Exception.Message)"
        }
    }
}

if ($null -eq $DeleteAsset) {
    $DeleteAsset = {
        param([string] $ApiBaseUrl, [string] $Repository, $Asset, [string] $Token)

        $url = "$ApiBaseUrl/repos/$Repository/releases/assets/$($Asset.id)"

        try {
            Invoke-RestMethod -Uri $url -Headers (Get-GitHubHeader -Token $Token) -Method Delete | Out-Null
        }
        catch {
            throw "The incomplete asset $($Asset.name) ($($Asset.id)) could not be deleted: $($_.Exception.Message)"
        }
    }
}

if ($null -eq $UploadAsset) {
    $UploadAsset = {
        param([string] $UploadBaseUrl, [string] $Repository, [string] $ReleaseId, [string] $AssetName, [string] $FilePath, [string] $Token)

        $url = "$UploadBaseUrl/repos/$Repository/releases/$ReleaseId/assets?name=$([uri]::EscapeDataString($AssetName))"
        $headers = Get-GitHubHeader -Token $Token
        $headers["Content-Type"] = "application/octet-stream"

        try {
            return Invoke-RestMethod -Uri $url -Headers $headers -Method Post -InFile $FilePath
        }
        catch {
            throw "Uploading $AssetName to $url failed: $($_.Exception.Message)"
        }
    }
}

if ($null -eq $ContentDigest) {
    $ContentDigest = { param([string] $Path) Get-FileSha256 -Path $Path }
}

function Get-PublicationResult {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Action,
        [Parameter(Mandatory)][string] $Digest,
        [AllowNull()] $AssetId
    )

    return [pscustomobject]@{
        PSTypeName = "EdFi.ReleaseAssetPublication"
        Action     = $Action
        AssetName  = $AssetName
        Digest     = $Digest
        AssetId    = $AssetId
    }
}

if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
    throw "The file to attach was not found: $FilePath"
}

if ((Get-Item -LiteralPath $FilePath).Length -eq 0) {
    throw "The file to attach is empty: $FilePath"
}

# The local content first, before any request: a malformed archive or a file that is not the one the
# caller meant fails here with nothing touched.
$localDigest = [string] (& $ContentDigest $FilePath)

if ([string]::IsNullOrWhiteSpace($localDigest)) {
    throw "The content digest of $FilePath is empty; nothing is uploaded."
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedDigest) -and $localDigest -cne $ExpectedDigest.Trim().ToLowerInvariant()) {
    throw "The content digest of $FilePath is $localDigest, but $($ExpectedDigest.Trim().ToLowerInvariant()) was expected. This is not the file that was meant to be attached; nothing is uploaded."
}

$allAssets = @(& $GetAssets $ApiBaseUrl $Repository $ReleaseId $Token)
$existing = @($allAssets | Where-Object { [string] $_.name -ceq $AssetName })

if ($existing.Count -gt 1) {
    throw "Release $ReleaseId carries $($existing.Count) assets named $AssetName. That cannot be reconciled automatically; inspect the release."
}

if ($existing.Count -eq 0) {
    Write-Information "Uploading $AssetName ($localDigest) to release $ReleaseId." -InformationAction Continue
    $created = & $UploadAsset $UploadBaseUrl $Repository $ReleaseId $AssetName $FilePath $Token

    return Get-PublicationResult -Action "uploaded" -Digest $localDigest -AssetId $created.id
}

$asset = $existing[0]
$state = [string] $asset.state

if ($state -ceq "starter") {
    # An upload that never completed. It blocks the name and describes nothing, so it is the one
    # thing that may be removed.
    Write-Information "Release $ReleaseId carries an incomplete $AssetName (state starter, id $($asset.id)); deleting it before uploading." -InformationAction Continue
    & $DeleteAsset $ApiBaseUrl $Repository $asset $Token
    $created = & $UploadAsset $UploadBaseUrl $Repository $ReleaseId $AssetName $FilePath $Token

    return Get-PublicationResult -Action "replaced-starter" -Digest $localDigest -AssetId $created.id
}

if ($state -cne "uploaded") {
    throw "Release $ReleaseId carries $AssetName in state '$state'. Only a complete (uploaded) asset can be compared and only an incomplete (starter) one can be removed; inspect the release."
}

if (Test-Path -LiteralPath $WorkingDirectory) {
    if (@(Get-ChildItem -LiteralPath $WorkingDirectory -Force).Count -gt 0) {
        throw "Refusing to work in $WorkingDirectory : it is not empty, and this script never deletes caller content."
    }
}
else {
    New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
}

$downloaded = Join-Path $WorkingDirectory "existing-$AssetName"
& $DownloadAsset $asset $downloaded $Token

if (-not (Test-Path -LiteralPath $downloaded -PathType Leaf)) {
    throw "The existing asset $AssetName was not written to $downloaded, although the download reported no error."
}

$remoteDigest = [string] (& $ContentDigest $downloaded)

if ($remoteDigest -ceq $localDigest) {
    Write-Information "Release $ReleaseId already carries $AssetName with content digest $localDigest; nothing to do." -InformationAction Continue

    return Get-PublicationResult -Action "skipped" -Digest $localDigest -AssetId $asset.id
}

throw "Release $ReleaseId already carries $AssetName with content digest $remoteDigest, and the file to attach has $localDigest. The release holds evidence for different bytes; nothing is replaced. Establish which bytes were published before changing the release."
