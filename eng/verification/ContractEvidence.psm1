# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.DESCRIPTION
The digest, archive and identity rules shared by the release-evidence helpers: Publish-ReleaseAsset.ps1
attaches a contract's SBOM and provenance to a GitHub release idempotently, and
Invoke-ContractEvidenceBackfill.ps1 does the same later from a run's retained artifacts.

Everything here takes paths and parsed objects. Nothing contacts a network, and importing the module
mutates nothing: the two helpers own every request and every write.

The trust boundary these rules draw is deliberately narrow. A provenance file is trusted because it is
the SLSA generator's own output retrieved from the GitHub workflow run that produced it, and it is
compared and uploaded byte for byte. Reading its statement here binds it to one package and one run
attempt; it does not verify the signature, and nothing here is a substitute for a verifier such as
slsa-verifier. A subject hash that matches is a consistency check, not proof of provenance.
#>

$ErrorActionPreference = "Stop"

# The one entry an SBOM release asset carries. The release lane zips the SPDX manifest alone, and a
# reader of the release page expects to find exactly that inside the zip.
$script:sbomManifestEntryName = "manifest.spdx.json"

<#
.DESCRIPTION
Ordinal string equality. Every exact-name and identity comparison in these helpers goes through
this: PowerShell's -eq and -ceq compare by culture, under which a name containing an ignorable
character such as a soft hyphen (U+00AD) equals the name without it, and an asset or artifact
selected "by exact name" would then be a different asset.
#>
function Test-OrdinalEqual {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]
        $Left,

        [AllowNull()]
        [AllowEmptyString()]
        [string]
        $Right
    )

    return [string]::Equals($Left, $Right, [System.StringComparison]::Ordinal)
}

<#
.DESCRIPTION
The lowercase hexadecimal SHA-256 of one file's bytes.
#>
function Get-FileSha256 {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]
        $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot hash $Path : it does not exist."
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

<#
.DESCRIPTION
The content digest of a provenance file: the SHA-256 of the whole envelope, signatures included.

Whole bytes, deliberately. The statement inside names a subject digest, but a second envelope that
repeats the right subject with a different signature is not the same evidence, and accepting it in
place of the generator's output would let anything that quotes the package hash pass as provenance.
#>
function Get-ProvenanceContentDigest {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]
        $Path
    )

    return Get-FileSha256 -Path $Path
}

<#
.DESCRIPTION
The content digest of an SBOM release asset: the SHA-256 of the manifest inside the zip.

The zip's own bytes are not compared because Compress-Archive is not reproducible, so two archives of
one manifest differ. The archive must hold exactly one entry, named for the manifest; an extra file, a
duplicate entry or another name is refused rather than ignored, so an asset that carries more than the
manifest can neither be created nor accepted as equal to one that does not.
#>
function Get-SbomArchiveContentDigest {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]
        $Path,

        [string]
        $ManifestEntryName = $script:sbomManifestEntryName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot read the SBOM archive $Path : it does not exist."
    }

    $archive = $null

    try {
        try {
            $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        }
        catch {
            throw "Cannot read the SBOM archive $Path : it is not a zip archive. $($_.Exception.Message)"
        }

        $entries = @($archive.Entries)

        if ($entries.Count -ne 1) {
            $names = ($entries | ForEach-Object { $_.FullName }) -join ", "
            throw "The SBOM archive $Path carries $($entries.Count) entries ($names); exactly one, $ManifestEntryName, is allowed."
        }

        if (-not (Test-OrdinalEqual -Left $entries[0].FullName -Right $ManifestEntryName)) {
            throw "The SBOM archive $Path carries '$($entries[0].FullName)' rather than $ManifestEntryName."
        }

        $stream = $entries[0].Open()

        try {
            $sha256 = [System.Security.Cryptography.SHA256]::Create()

            try {
                return [System.Convert]::ToHexString($sha256.ComputeHash($stream)).ToLowerInvariant()
            }
            finally {
                $sha256.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
    }
}

<#
.DESCRIPTION
Writes the SBOM release asset: a zip holding exactly the manifest, under the entry name the digest
function expects. Refuses to overwrite, so a caller that reuses a path sees the reuse.
#>
function New-SbomArchive {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]
        $ManifestPath,

        [Parameter(Mandatory)]
        [string]
        $DestinationPath,

        [string]
        $ManifestEntryName = $script:sbomManifestEntryName
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Cannot archive the SBOM: $ManifestPath does not exist."
    }

    if (Test-Path -LiteralPath $DestinationPath) {
        throw "Cannot archive the SBOM: $DestinationPath already exists."
    }

    if (-not $PSCmdlet.ShouldProcess($DestinationPath, "Write SBOM archive")) {
        return $DestinationPath
    }

    $archive = [System.IO.Compression.ZipFile]::Open($DestinationPath, [System.IO.Compression.ZipArchiveMode]::Create)

    try {
        [void] [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $ManifestPath,
            $ManifestEntryName,
            [System.IO.Compression.CompressionLevel]::Optimal
        )
    }
    finally {
        $archive.Dispose()
    }

    return $DestinationPath
}

<#
.DESCRIPTION
Reads a DSSE provenance envelope and returns its decoded in-toto statement.

The envelope must carry the in-toto payload type, a base64 payload and at least one signature, and the
payload must decode to a statement with a type, a predicate type, subjects and a predicate. Anything
else is refused: a file that is not an envelope cannot be evidence of anything.
#>
function Read-ProvenanceStatement {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]
        $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot read the provenance envelope $Path : it does not exist."
    }

    try {
        $envelope = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 64
    }
    catch {
        throw "Cannot read the provenance envelope $Path : it is not JSON. $($_.Exception.Message)"
    }

    if ($null -eq $envelope -or -not (Test-OrdinalEqual -Left ([string] $envelope.payloadType) -Right "application/vnd.in-toto+json")) {
        throw "The provenance envelope $Path does not declare payloadType application/vnd.in-toto+json."
    }

    if ([string]::IsNullOrWhiteSpace([string] $envelope.payload)) {
        throw "The provenance envelope $Path carries no payload."
    }

    # Nulls are dropped before counting: @($null) is a one-element array, so a missing or null
    # signatures member would otherwise read as one signature. An entry without a sig value is not
    # a signature either. This is structure, not verification: the envelope has to have the shape of
    # a signed envelope before its statement is read at all.
    $signatures = @($envelope.signatures | Where-Object { $null -ne $_ })

    if ($signatures.Count -eq 0) {
        throw "The provenance envelope $Path carries no signatures."
    }

    foreach ($signature in $signatures) {
        if ([string]::IsNullOrWhiteSpace([string] $signature.sig)) {
            throw "The provenance envelope $Path carries a signature entry with no sig value."
        }
    }

    try {
        $statementText = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String([string] $envelope.payload))
        $statement = $statementText | ConvertFrom-Json -Depth 64
    }
    catch {
        throw "The provenance envelope $Path carries a payload that is not a base64 JSON statement. $($_.Exception.Message)"
    }

    foreach ($member in @("_type", "predicateType", "subject", "predicate")) {
        if ($null -eq $statement.PSObject.Properties[$member]) {
            throw "The provenance statement in $Path carries no '$member'."
        }
    }

    return [pscustomobject]@{
        PayloadType    = [string] $envelope.payloadType
        SignatureCount = $signatures.Count
        Statement      = $statement
    }
}

<#
.DESCRIPTION
Binds a decoded provenance statement to one package and one workflow run, throwing on the first
mismatch.

What is checked: the predicate type and the builder are the generator this repository calls; there is
exactly one subject, named for the package file and carrying its SHA-256; the invocation's config
source names this repository, the expected workflow file and the run's head commit; and the recorded
run id, and run attempt when one is given, are the run the artifacts were retrieved from. Together
those say "this is the generator's statement about these bytes from this run". They do not verify the
signature.
#>
function Assert-ProvenanceDescribesPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Statement,

        [Parameter(Mandatory)]
        [string]
        $SubjectName,

        [Parameter(Mandatory)]
        [string]
        $SubjectSha256,

        # owner/name, as the GitHub API reports it.
        [Parameter(Mandatory)]
        [string]
        $Repository,

        [Parameter(Mandatory)]
        [string]
        $WorkflowPath,

        [Parameter(Mandatory)]
        [string]
        $HeadSha,

        [Parameter(Mandatory)]
        [string]
        $RunId,

        # Empty means the attempt is not checked.
        [string]
        $RunAttempt = "",

        [string]
        $BuilderIdPrefix = "https://github.com/Ed-Fi-Alliance-OSS/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@",

        [string]
        $PredicateType = "https://slsa.dev/provenance/v0.2"
    )

    if (-not (Test-OrdinalEqual -Left ([string] $Statement.predicateType) -Right $PredicateType)) {
        throw "The provenance statement declares predicateType '$($Statement.predicateType)', not $PredicateType."
    }

    $builderId = [string] $Statement.predicate.builder.id

    if (-not $builderId.StartsWith($BuilderIdPrefix, [System.StringComparison]::Ordinal)) {
        throw "The provenance statement names builder '$builderId', which is not the generator this repository calls ($BuilderIdPrefix...)."
    }

    $subjects = @($Statement.subject)

    if ($subjects.Count -ne 1) {
        throw "The provenance statement names $($subjects.Count) subjects; exactly one, $SubjectName, is expected."
    }

    if (-not (Test-OrdinalEqual -Left ([string] $subjects[0].name) -Right $SubjectName)) {
        throw "The provenance statement names subject '$($subjects[0].name)', not $SubjectName."
    }

    $subjectDigest = [string] $subjects[0].digest.sha256

    if ([string]::IsNullOrWhiteSpace($subjectDigest)) {
        throw "The provenance statement's subject carries no sha256 digest."
    }

    if (-not (Test-OrdinalEqual -Left $subjectDigest.ToLowerInvariant() -Right $SubjectSha256.ToLowerInvariant())) {
        throw "The provenance statement's subject sha256 is $subjectDigest; the package is $SubjectSha256. This provenance does not describe this package."
    }

    $configSource = $Statement.predicate.invocation.configSource
    $expectedUriPrefix = "git+https://github.com/$Repository@"

    if (-not ([string] $configSource.uri).StartsWith($expectedUriPrefix, [System.StringComparison]::Ordinal)) {
        throw "The provenance statement's config source is '$($configSource.uri)', not this repository ($expectedUriPrefix...)."
    }

    if (-not (Test-OrdinalEqual -Left ([string] $configSource.entryPoint) -Right $WorkflowPath)) {
        throw "The provenance statement's entry point is '$($configSource.entryPoint)', not $WorkflowPath."
    }

    if (-not (Test-OrdinalEqual -Left ([string] $configSource.digest.sha1).ToLowerInvariant() -Right $HeadSha.ToLowerInvariant())) {
        throw "The provenance statement's source commit is '$($configSource.digest.sha1)'; the run's head is $HeadSha."
    }

    $environment = $Statement.predicate.invocation.environment

    if (-not (Test-OrdinalEqual -Left ([string] $environment.github_run_id) -Right $RunId)) {
        throw "The provenance statement records run '$($environment.github_run_id)', not $RunId."
    }

    if (-not [string]::IsNullOrWhiteSpace($RunAttempt) -and -not (Test-OrdinalEqual -Left ([string] $environment.github_run_attempt) -Right $RunAttempt)) {
        throw "The provenance statement records run attempt '$($environment.github_run_attempt)', not $RunAttempt."
    }
}

<#
.DESCRIPTION
Checks that an SPDX manifest describes one package file with one digest, throwing on the first
mismatch.

The root is resolved through documentDescribes rather than by package name, because the generator
emits more than one package entry carrying the package's name. The files list must hold exactly one
entry for the package file and that entry's SHA-256 must be the package's, so an SBOM for a rebuilt
package cannot be attached beside the original's provenance.
#>
function Assert-SbomDescribesPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]
        $ManifestPath,

        [Parameter(Mandatory)]
        [string]
        $PackageId,

        [Parameter(Mandatory)]
        [string]
        $PackageVersion,

        [Parameter(Mandatory)]
        [string]
        $PackageFileName,

        [Parameter(Mandatory)]
        [string]
        $PackageSha256
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Cannot read the SBOM manifest $ManifestPath : it does not exist."
    }

    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -Depth 64
    }
    catch {
        throw "Cannot read the SBOM manifest $ManifestPath : it is not JSON. $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace([string] $manifest.spdxVersion)) {
        throw "The SBOM manifest $ManifestPath declares no spdxVersion."
    }

    $described = @($manifest.documentDescribes)

    if ($described.Count -ne 1) {
        throw "The SBOM manifest $ManifestPath describes $($described.Count) root packages; exactly one is expected."
    }

    $rootId = [string] $described[0]
    $roots = @($manifest.packages | Where-Object { Test-OrdinalEqual -Left ([string] $_.SPDXID) -Right $rootId })

    if ($roots.Count -ne 1) {
        throw "The SBOM manifest $ManifestPath names root package '$rootId' but carries $($roots.Count) package entries with that id."
    }

    if (-not (Test-OrdinalEqual -Left ([string] $roots[0].name) -Right $PackageId)) {
        throw "The SBOM manifest $ManifestPath describes package '$($roots[0].name)', not $PackageId."
    }

    if (-not (Test-OrdinalEqual -Left ([string] $roots[0].versionInfo) -Right $PackageVersion)) {
        throw "The SBOM manifest $ManifestPath describes $PackageId version '$($roots[0].versionInfo)', not $PackageVersion."
    }

    $expectedFileName = "./$PackageFileName"
    $files = @($manifest.files | Where-Object { Test-OrdinalEqual -Left ([string] $_.fileName) -Right $expectedFileName })

    if ($files.Count -ne 1) {
        throw "The SBOM manifest $ManifestPath lists $($files.Count) files named $expectedFileName; exactly one is expected."
    }

    $checksums = @($files[0].checksums | Where-Object { Test-OrdinalEqual -Left ([string] $_.algorithm) -Right "SHA256" })

    if ($checksums.Count -ne 1) {
        throw "The SBOM manifest $ManifestPath carries $($checksums.Count) SHA256 checksums for $expectedFileName; exactly one is expected."
    }

    $listed = ([string] $checksums[0].checksumValue).ToLowerInvariant()

    if (-not (Test-OrdinalEqual -Left $listed -Right $PackageSha256.ToLowerInvariant())) {
        throw "The SBOM manifest $ManifestPath records SHA256 $listed for $expectedFileName; the package is $PackageSha256. This SBOM does not describe this package."
    }
}

<#
.DESCRIPTION
The lowercase SHA-256 inside a pack job's hash-code output, which is the base64 of one sha256sum line
("<hash>  <file>").
#>
function ConvertFrom-Sha256SumBase64 {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]
        $Value
    )

    try {
        $line = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Value.Trim()))
    }
    catch {
        throw "'$Value' is not base64, so it is not a sha256sum line."
    }

    $hash = ($line.Trim() -split '\s+')[0].ToLowerInvariant()

    if ($hash -notmatch '^[0-9a-f]{64}$') {
        throw "'$line' does not begin with a SHA-256 digest."
    }

    return $hash
}

Export-ModuleMember -Function `
    Test-OrdinalEqual, `
    Get-FileSha256, `
    Get-ProvenanceContentDigest, `
    Get-SbomArchiveContentDigest, `
    New-SbomArchive, `
    Read-ProvenanceStatement, `
    Assert-ProvenanceDescribesPackage, `
    Assert-SbomDescribesPackage, `
    ConvertFrom-Sha256SumBase64
