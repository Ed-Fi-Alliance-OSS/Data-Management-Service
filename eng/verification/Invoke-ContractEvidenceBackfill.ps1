# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Attaches a contract's original SBOM and provenance to a release from the run that published it.

.DESCRIPTION
    The prerelease workflow attaches a contract's SBOM and provenance to the GitHub release only when
    the run that packed them also pushed the package and then confirmed the feed serves exactly those
    bytes. Three shapes leave a published contract with no evidence on the release page:

      - alpha-first: the contract's version first reached the feed on a dms-*-alpha prerelease, whose
        run attaches nothing to any release; the next non-alpha prerelease packs different bytes,
        finds the version already published, and correctly attaches nothing for them.
      - a later repack: the run that pushed the version failed after the push, and the run that
        followed rebuilt the package; the rebuilt bytes are not the ones on the feed.
      - a partial upload: the run pushed and confirmed, one asset uploaded, the other did not.

    This script is the recovery path for all three, and it is deliberately narrow. It never creates
    evidence. It retrieves the original run's own artifacts - the packed nupkg, the SPDX manifest and
    the SLSA generator's signed provenance - proves they belong together and to the bytes the feed
    serves, and uploads the SBOM (zipped) and the provenance envelope exactly as retrieved. If any of
    that cannot be proven, nothing is uploaded.

    What is proven before the first upload:

      1. The run exists in the expected repository, ran the expected workflow file, and (when an
         attempt is given) is that attempt.
      2. Each artifact is selected by its exact name within that run, or by an explicit artifact id
         that must still carry that name and belong to that run and repository. Two artifacts of one
         name, which a re-run can leave behind, are refused with the remedy of passing the id.
      3. The retrieved nupkg's SHA-256 equals the SHA-256 of the archive the feed serves for that id
         and version. Absent from the feed, there is nothing to attest.
      4. The provenance envelope decodes to a statement from the generator this repository calls,
         naming exactly that nupkg and that SHA-256, this repository, this workflow file, the run's
         head commit, this run id and (when given) this attempt.
      5. The SPDX manifest's root package, resolved through documentDescribes, is this package id and
         version, and its one file entry for the nupkg carries the same SHA-256.

    Trust boundary, stated plainly: the provenance is trusted because it is the generator's output
    read back from the run that produced it through the GitHub API, and it is compared and uploaded
    byte for byte. Its signature is not verified here, and nothing here replaces a verifier such as
    slsa-verifier. A subject hash that matches is a consistency check against the wrong artifact,
    not proof that the envelope is genuine.

    Retention bounds what can be recovered. The generator uploads its provenance artifact with a
    five-day retention. Every prerelease run since the retained-copy jobs were added also keeps that
    same envelope, unchanged, as a second artifact named <provenance-name>-retained with thirty-day
    retention, alongside the thirty-day nupkg and SBOM artifacts; select it with
    -ProvenanceArtifactName <provenance-name>-retained, and the file inside is checked and uploaded
    exactly as the original would be. Runs from before that change have only the generator's
    five-day artifact: nothing was preserved retroactively. An expired or missing artifact fails
    here with its expiry, and no expired evidence can be recreated.

    Uploads go through Publish-ReleaseAsset.ps1, so a retry after a partial upload skips an asset
    whose content is already there, replaces an interrupted one, and refuses to overwrite an asset
    holding different bytes.

.EXAMPLE
    ./eng/verification/Invoke-ContractEvidenceBackfill.ps1 `
        -Repository Ed-Fi-Alliance-OSS/Data-Management-Service `
        -RunId 35352662171 `
        -PackageId EdFi.Api.Plugins -PackageVersion 1.0.0 `
        -ProvenanceArtifactName edfiApiPlugins.intoto.jsonl `
        -ReleaseTag v8.1.0 `
        -WorkingDirectory (Join-Path ([IO.Path]::GetTempPath()) "backfill-$([guid]::NewGuid().ToString('N'))")

    The alpha-first shape: the run id is the alpha prerelease run that pushed 1.0.0, the tag is the
    later final release the evidence belongs on. GH_TOKEN or GITHUB_TOKEN supplies the token.

.OUTPUTS
    One object with PackageSha256, RunId, Sbom and Provenance, the last two being
    Publish-ReleaseAsset results.
#>
[CmdletBinding()]
[OutputType([pscustomobject])]
param(
    # owner/name.
    [Parameter(Mandatory)]
    [string]
    $Repository,

    # The workflow run that packed, pushed and generated the evidence.
    [Parameter(Mandatory)]
    [string]
    $RunId,

    [Parameter(Mandatory)]
    [string]
    $PackageId,

    [Parameter(Mandatory)]
    [string]
    $PackageVersion,

    # The release the evidence is attached to, by tag.
    [Parameter(Mandatory)]
    [string]
    $ReleaseTag,

    # The exact artifact name the generator uploaded the provenance under, or the name of a copy of
    # the same bytes.
    [Parameter(Mandatory)]
    [string]
    $ProvenanceArtifactName,

    # A scratch directory for downloads. Must be empty or absent.
    [Parameter(Mandatory)]
    [string]
    $WorkingDirectory,

    # When given, the run metadata and the provenance must both record this attempt.
    [string]
    $RunAttempt = "",

    # Explicit artifact ids, for when a name matches more than one artifact in the run. Each must
    # still carry the expected name and belong to the run.
    [string]
    $NuGetArtifactId = "",

    [string]
    $SbomArtifactId = "",

    [string]
    $ProvenanceArtifactId = "",

    [string]
    $NuGetArtifactName = "",

    [string]
    $SbomArtifactName = "",

    [string]
    $WorkflowPath = ".github/workflows/on-prerelease.yml",

    [string]
    $BuilderIdPrefix = "https://github.com/Ed-Fi-Alliance-OSS/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@",

    [string]
    $ServiceIndexUrl = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",

    [string]
    $FeedApiKey = "",

    # GitHub token. Defaults to GH_TOKEN, then GITHUB_TOKEN, at run time.
    [string]
    $Token = "",

    [string]
    $ApiBaseUrl = "https://api.github.com",

    [string]
    $UploadBaseUrl = "https://uploads.github.com",

    # Returns the run's metadata (id, path, head_sha, run_attempt, repository.id, repository.full_name).
    [scriptblock]
    $GetRun,

    # Returns every artifact of the run.
    [scriptblock]
    $ListRunArtifacts,

    # Returns one artifact by id.
    [scriptblock]
    $GetArtifact,

    # Downloads and extracts one artifact into a directory.
    [scriptblock]
    $DownloadArtifact,

    # The feed seams, with the same shapes Invoke-ContractPublishCheck.ps1 uses.
    [scriptblock]
    $ResolvePackageBaseAddress,

    [scriptblock]
    $SavePublishedPackage,

    # Returns the release object for a tag.
    [scriptblock]
    $ResolveRelease,

    # Performs one upload through Publish-ReleaseAsset.ps1.
    [scriptblock]
    $Publish
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "ContractEvidence.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "ContractPackageComparison.psm1") -Force

if ([string]::IsNullOrWhiteSpace($NuGetArtifactName)) {
    $NuGetArtifactName = "$PackageId-NuGet"
}

if ([string]::IsNullOrWhiteSpace($SbomArtifactName)) {
    $SbomArtifactName = "$PackageId-SBOM"
}

if ([string]::IsNullOrWhiteSpace($Token)) {
    $Token = if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { $env:GH_TOKEN } else { $env:GITHUB_TOKEN }
}

$usingDefaultGitHubSeam = @($GetRun, $ListRunArtifacts, $GetArtifact, $DownloadArtifact, $ResolveRelease, $Publish) | Where-Object { $null -eq $_ }

if (@($usingDefaultGitHubSeam).Count -gt 0 -and [string]::IsNullOrWhiteSpace($Token)) {
    throw "A GitHub token is required to read the run's artifacts and upload release assets. Set GH_TOKEN or GITHUB_TOKEN, or pass -Token."
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

function Get-FeedRequestHeader {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([string] $ApiKey)

    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        return @{}
    }

    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("nuget:$ApiKey"))

    return @{ Authorization = "Basic $encoded" }
}

if ($null -eq $GetRun) {
    $GetRun = {
        param([string] $ApiBaseUrl, [string] $Repository, [string] $RunId, [string] $RunAttempt, [string] $Token)

        $url = if ([string]::IsNullOrWhiteSpace($RunAttempt)) {
            "$ApiBaseUrl/repos/$Repository/actions/runs/$RunId"
        }
        else {
            "$ApiBaseUrl/repos/$Repository/actions/runs/$RunId/attempts/$RunAttempt"
        }

        try {
            return Invoke-RestMethod -Uri $url -Headers (Get-GitHubHeader -Token $Token) -Method Get
        }
        catch {
            throw "The run could not be read from $url : $($_.Exception.Message)"
        }
    }
}

if ($null -eq $ListRunArtifacts) {
    $ListRunArtifacts = {
        param([string] $ApiBaseUrl, [string] $Repository, [string] $RunId, [string] $Token)

        $headers = Get-GitHubHeader -Token $Token
        $artifacts = [System.Collections.Generic.List[object]]::new()
        $page = 1

        do {
            $url = "$ApiBaseUrl/repos/$Repository/actions/runs/$RunId/artifacts?per_page=100&page=$page"

            try {
                $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get
            }
            catch {
                throw "The run's artifacts could not be listed from $url : $($_.Exception.Message)"
            }

            $batch = @($response.artifacts)

            foreach ($artifact in $batch) {
                $artifacts.Add($artifact)
            }

            $page++
        } while ($batch.Count -eq 100)

        # Written to the pipeline as separate objects; the caller collects them with @(). A unary
        # comma here would hand the caller one array object instead of the artifacts.
        return $artifacts.ToArray()
    }
}

if ($null -eq $GetArtifact) {
    $GetArtifact = {
        param([string] $ApiBaseUrl, [string] $Repository, [string] $ArtifactId, [string] $Token)

        $url = "$ApiBaseUrl/repos/$Repository/actions/artifacts/$ArtifactId"

        try {
            return Invoke-RestMethod -Uri $url -Headers (Get-GitHubHeader -Token $Token) -Method Get
        }
        catch {
            throw "The artifact could not be read from $url : $($_.Exception.Message)"
        }
    }
}

if ($null -eq $DownloadArtifact) {
    $DownloadArtifact = {
        param($Artifact, [string] $DestinationDirectory, [string] $Token)

        New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
        $zip = Join-Path $DestinationDirectory "artifact.zip"

        try {
            Invoke-WebRequest -Uri $Artifact.archive_download_url -Headers (Get-GitHubHeader -Token $Token) -Method Get -OutFile $zip
        }
        catch {
            throw "The artifact $($Artifact.name) ($($Artifact.id)) could not be downloaded: $($_.Exception.Message)"
        }

        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $DestinationDirectory "content")
        Remove-Item -LiteralPath $zip
    }
}

if ($null -eq $ResolvePackageBaseAddress) {
    $ResolvePackageBaseAddress = {
        param([string] $IndexUrl, [string] $ApiKey)

        try {
            $response = Invoke-RestMethod -Uri $IndexUrl -Headers (Get-FeedRequestHeader -ApiKey $ApiKey) -Method Get
        }
        catch {
            throw "The feed's service index at $IndexUrl could not be read: $($_.Exception.Message)"
        }

        $resource = @($response.resources | Where-Object { $_.'@type' -like "PackageBaseAddress/3.0.0*" })

        if ($resource.Count -eq 0) {
            throw "The feed's service index at $IndexUrl advertises no PackageBaseAddress/3.0.0 resource."
        }

        return $resource[0].'@id'
    }
}

if ($null -eq $SavePublishedPackage) {
    $SavePublishedPackage = {
        param([string] $BaseAddress, [string] $NormalizedId, [string] $NormalizedVersion, [string] $Destination, [string] $ApiKey)

        $url = "$BaseAddress/$NormalizedId/$NormalizedVersion/$NormalizedId.$NormalizedVersion.nupkg"

        try {
            Invoke-WebRequest -Uri $url -Headers (Get-FeedRequestHeader -ApiKey $ApiKey) -Method Get -OutFile $Destination
        }
        catch {
            throw "The published package at $url could not be downloaded: $($_.Exception.Message). A version that is not on the feed has nothing to attest."
        }
    }
}

if ($null -eq $ResolveRelease) {
    $ResolveRelease = {
        param([string] $ApiBaseUrl, [string] $Repository, [string] $ReleaseTag, [string] $Token)

        $url = "$ApiBaseUrl/repos/$Repository/releases/tags/$([uri]::EscapeDataString($ReleaseTag))"

        try {
            return Invoke-RestMethod -Uri $url -Headers (Get-GitHubHeader -Token $Token) -Method Get
        }
        catch {
            throw "The release for tag $ReleaseTag could not be read from $url : $($_.Exception.Message)"
        }
    }
}

if ($null -eq $Publish) {
    $Publish = {
        param([hashtable] $Arguments)

        return & (Join-Path $PSScriptRoot "Publish-ReleaseAsset.ps1") @Arguments
    }
}

if (Test-Path -LiteralPath $WorkingDirectory) {
    if (@(Get-ChildItem -LiteralPath $WorkingDirectory -Force).Count -gt 0) {
        throw "Refusing to work in $WorkingDirectory : it is not empty, and this script never deletes caller content."
    }
}
else {
    New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
}

# 1. The run.
$run = & $GetRun $ApiBaseUrl $Repository $RunId $RunAttempt $Token

# Every identity comparison below is ordinal (Test-OrdinalEqual): these are exact ids, names and
# paths, and a culture comparison would let a name differing by an ignorable character pass.
if ($null -eq $run -or -not (Test-OrdinalEqual -Left ([string] $run.id) -Right $RunId)) {
    throw "The run metadata does not describe run $RunId (it reports '$($run.id)')."
}

if (-not (Test-OrdinalEqual -Left ([string] $run.repository.full_name) -Right $Repository)) {
    throw "Run $RunId belongs to '$($run.repository.full_name)', not $Repository."
}

if (-not (Test-OrdinalEqual -Left ([string] $run.path) -Right $WorkflowPath)) {
    throw "Run $RunId ran '$($run.path)', not $WorkflowPath. Only that workflow's runs publish the contracts."
}

if (-not [string]::IsNullOrWhiteSpace($RunAttempt) -and -not (Test-OrdinalEqual -Left ([string] $run.run_attempt) -Right $RunAttempt)) {
    throw "Run $RunId reports attempt '$($run.run_attempt)', not $RunAttempt."
}

if ([string]::IsNullOrWhiteSpace([string] $run.head_sha)) {
    throw "Run $RunId reports no head commit."
}

$repositoryId = [string] $run.repository.id
$headSha = ([string] $run.head_sha).ToLowerInvariant()

# 2. The artifacts, by exact name or by an explicit id that must still be that artifact.
$listedArtifacts = $null

function Select-RunArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $ExpectedName,
        [AllowEmptyString()][string] $ExplicitId,
        [Parameter(Mandatory)][string] $IdParameterName
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitId)) {
        $artifact = & $GetArtifact $ApiBaseUrl $Repository $ExplicitId $Token

        if ($null -eq $artifact -or -not (Test-OrdinalEqual -Left ([string] $artifact.id) -Right $ExplicitId)) {
            throw "Artifact $ExplicitId could not be read."
        }

        if (-not (Test-OrdinalEqual -Left ([string] $artifact.name) -Right $ExpectedName)) {
            throw "Artifact $ExplicitId is named '$($artifact.name)', not $ExpectedName. An explicit id selects among artifacts of that name; it does not substitute another artifact."
        }
    }
    else {
        if ($null -eq $script:listedArtifacts) {
            $script:listedArtifacts = @(& $ListRunArtifacts $ApiBaseUrl $Repository $RunId $Token)
        }

        $candidates = @($script:listedArtifacts | Where-Object { Test-OrdinalEqual -Left ([string] $_.name) -Right $ExpectedName })

        if ($candidates.Count -eq 0) {
            throw "Run $RunId carries no artifact named $ExpectedName. Either the run never produced it or it has expired and been removed. The generator's own provenance artifact is kept five days; runs since the retained-copy jobs were added also keep it as <provenance-name>-retained for thirty days, as are the nupkg and SBOM artifacts. Expired evidence cannot be recreated."
        }

        if ($candidates.Count -gt 1) {
            $ids = ($candidates | ForEach-Object { $_.id }) -join ", "
            throw "Run $RunId carries $($candidates.Count) artifacts named $ExpectedName (ids $ids), which a re-run can leave behind. Pass -$IdParameterName with the id of the one that was published."
        }

        $artifact = $candidates[0]
    }

    if (-not (Test-OrdinalEqual -Left ([string] $artifact.workflow_run.id) -Right $RunId)) {
        throw "Artifact $($artifact.id) ($($artifact.name)) belongs to run '$($artifact.workflow_run.id)', not $RunId."
    }

    if (-not (Test-OrdinalEqual -Left ([string] $artifact.workflow_run.repository_id) -Right $repositoryId)) {
        throw "Artifact $($artifact.id) ($($artifact.name)) belongs to repository id '$($artifact.workflow_run.repository_id)', not $repositoryId ($Repository)."
    }

    if ($artifact.expired -eq $true) {
        throw "Artifact $($artifact.id) ($($artifact.name)) expired at $($artifact.expires_at). The generator's own provenance artifact is kept five days; runs since the retained-copy jobs were added also keep it as <provenance-name>-retained for thirty days, as are the nupkg and SBOM artifacts. Expired evidence cannot be recreated, and no other bytes may be attested in its place."
    }

    return $artifact
}

$nupkgArtifact = Select-RunArtifact -ExpectedName $NuGetArtifactName -ExplicitId $NuGetArtifactId -IdParameterName "NuGetArtifactId"
$sbomArtifact = Select-RunArtifact -ExpectedName $SbomArtifactName -ExplicitId $SbomArtifactId -IdParameterName "SbomArtifactId"
$provenanceArtifact = Select-RunArtifact -ExpectedName $ProvenanceArtifactName -ExplicitId $ProvenanceArtifactId -IdParameterName "ProvenanceArtifactId"

# 3. Their contents.
function Get-ArtifactFile {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] $Artifact,
        [Parameter(Mandatory)][string] $Filter,
        [Parameter(Mandatory)][string] $Description
    )

    $destination = Join-Path $WorkingDirectory "artifact-$($Artifact.id)"
    & $DownloadArtifact $Artifact $destination $Token

    $files = @(Get-ChildItem -LiteralPath $destination -Recurse -File -Filter $Filter)

    if ($files.Count -ne 1) {
        throw "Artifact $($Artifact.id) ($($Artifact.name)) holds $($files.Count) files matching $Filter; exactly one $Description is expected."
    }

    return $files[0].FullName
}

$packageFileName = "$PackageId.$PackageVersion.nupkg"
$nupkgPath = Get-ArtifactFile -Artifact $nupkgArtifact -Filter $packageFileName -Description "package"
$manifestPath = Get-ArtifactFile -Artifact $sbomArtifact -Filter "manifest.spdx.json" -Description "SPDX manifest"
$provenancePath = Get-ArtifactFile -Artifact $provenanceArtifact -Filter "*.intoto.jsonl" -Description "provenance envelope"

$packageSha256 = Get-FileSha256 -Path $nupkgPath

# 4. The feed serves exactly these bytes.
$normalizedId = ConvertTo-NormalizedPackageId -PackageId $PackageId
$normalizedVersion = ConvertTo-NormalizedPackageVersion -Version $PackageVersion
$baseAddress = ([string] (& $ResolvePackageBaseAddress $ServiceIndexUrl $FeedApiKey)).TrimEnd('/')

if ([string]::IsNullOrWhiteSpace($baseAddress)) {
    throw "The feed's service index resolved to no package base address."
}

$feedPackagePath = Join-Path $WorkingDirectory "$normalizedId.$normalizedVersion.feed.nupkg"
& $SavePublishedPackage $baseAddress $normalizedId $normalizedVersion $feedPackagePath $FeedApiKey

if (-not (Test-Path -LiteralPath $feedPackagePath -PathType Leaf)) {
    throw "The feed's archive for $PackageId $PackageVersion was not written to $feedPackagePath, although the download reported no error."
}

$feedSha256 = Get-FileSha256 -Path $feedPackagePath

if (-not (Test-OrdinalEqual -Left $feedSha256 -Right $packageSha256)) {
    throw "The feed serves $PackageId $PackageVersion with SHA-256 $feedSha256; run $RunId's artifact is $packageSha256. This run did not publish the bytes on the feed, so its evidence must not be attached. Find the run that did."
}

# 5. The evidence describes these bytes and this run.
$statement = Read-ProvenanceStatement -Path $provenancePath

Assert-ProvenanceDescribesPackage `
    -Statement $statement.Statement `
    -SubjectName $packageFileName `
    -SubjectSha256 $packageSha256 `
    -Repository $Repository `
    -WorkflowPath $WorkflowPath `
    -HeadSha $headSha `
    -RunId $RunId `
    -RunAttempt $RunAttempt `
    -BuilderIdPrefix $BuilderIdPrefix

Assert-SbomDescribesPackage `
    -ManifestPath $manifestPath `
    -PackageId $PackageId `
    -PackageVersion $PackageVersion `
    -PackageFileName $packageFileName `
    -PackageSha256 $packageSha256

# 6. The release, and the uploads, each idempotent.
$release = & $ResolveRelease $ApiBaseUrl $Repository $ReleaseTag $Token

if ($null -eq $release -or [string]::IsNullOrWhiteSpace([string] $release.id)) {
    throw "No release was resolved for tag $ReleaseTag."
}

$sbomArchivePath = New-SbomArchive -ManifestPath $manifestPath -DestinationPath (Join-Path $WorkingDirectory "$PackageId-SBOM.zip")

$sbomResult = & $Publish @{
    Repository       = $Repository
    ReleaseId        = [string] $release.id
    AssetName        = "$PackageId-SBOM.zip"
    FilePath         = $sbomArchivePath
    WorkingDirectory = (Join-Path $WorkingDirectory "existing-sbom")
    Token            = $Token
    ContentDigest    = { param([string] $Path) Get-SbomArchiveContentDigest -Path $Path }
    ExpectedDigest   = (Get-FileSha256 -Path $manifestPath)
    ApiBaseUrl       = $ApiBaseUrl
    UploadBaseUrl    = $UploadBaseUrl
}

$provenanceResult = & $Publish @{
    Repository       = $Repository
    ReleaseId        = [string] $release.id
    AssetName        = [System.IO.Path]::GetFileName($provenancePath)
    FilePath         = $provenancePath
    WorkingDirectory = (Join-Path $WorkingDirectory "existing-provenance")
    Token            = $Token
    ContentDigest    = { param([string] $Path) Get-ProvenanceContentDigest -Path $Path }
    ApiBaseUrl       = $ApiBaseUrl
    UploadBaseUrl    = $UploadBaseUrl
}

Write-Information "Attached run $RunId's evidence for $PackageId $PackageVersion ($packageSha256) to release ${ReleaseTag}: SBOM $($sbomResult.Action), provenance $($provenanceResult.Action)." -InformationAction Continue

return [pscustomobject]@{
    PSTypeName    = "EdFi.ContractEvidenceBackfill"
    PackageId     = $PackageId
    PackageVersion = $PackageVersion
    PackageSha256 = $packageSha256
    RunId         = $RunId
    ReleaseId     = [string] $release.id
    Sbom          = $sbomResult
    Provenance    = $provenanceResult
}
