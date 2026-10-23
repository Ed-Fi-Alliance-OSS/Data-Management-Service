# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Consumer side of the database-template restore branch: package acquisition (feed or
    explicit directory), exact-bytes authentication against the trust policy, immutable
    private staging with manifest and artifact validation, and restore-target-name safety.

.DESCRIPTION
    Everything in this module runs BEFORE any Docker activity: a package that cannot be
    authenticated, staged, and validated never reaches extraction into the active
    environment, container startup, workspace creation, or database work. The bootstrap
    wrapper owns when these stages run; this module owns how.

    Trust is fail-closed with no bypass: feed-resolved and -PackageDirectory packages go
    through identical authentication, and local origin is never trust.
#>

# $PSScriptRoot-anchored and without -Force: a forced nested import can strip an
# already-loaded copy from the caller's session.
Import-Module (Join-Path $PSScriptRoot "../DatabaseTemplates/Template-RestoreCore.psm1")
Import-Module (Join-Path $PSScriptRoot "../DatabaseTemplates/Template-RestoreTrust.psm1")
Import-Module (Join-Path $PSScriptRoot "bootstrap-package-resolver.psm1")
Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1")
Import-Module (Join-Path $PSScriptRoot "bootstrap-schema-tool.psm1")
Import-Module (Join-Path $PSScriptRoot "env-utility.psm1")

$script:RestoreWorkspaceRoot = Join-Path $PSScriptRoot ".bootstrap-restore"

# The lowest server_version_num DMS supports (PostgreSQL 18.0); the same floor as
# PgsqlDatabaseProvisioner.MinimumServerVersionNum in SchemaTools (DMS-1447).
$script:RestorePostgresqlMinimumServerVersionNum = 180000

# Default NuGet v3 service index for template packages, matching the feed the template CI
# workflows publish to (and the feed .env.example's SCHEMA_PACKAGES entries use). Overridden
# by the DATABASE_TEMPLATE_FEED_URL environment key, which also accepts a local directory
# path or file:// URL (a "directory feed") so the trusted path is testable without Azure.
$script:DefaultTemplateFeedUrl = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json"

function Get-RestoreWorkspaceRoot {
    <#
    .SYNOPSIS
    Absolute path of the git-ignored restore workspace root (eng/docker-compose/.bootstrap-restore),
    holding transient package stages and, in later phases, candidate workspaces.
    #>
    return $script:RestoreWorkspaceRoot
}

function Resolve-RestoreTemplatePackageIdentity {
    <#
    .SYNOPSIS
    Derives the template package identity from the effective environment: the package id is
    DATABASE_TEMPLATE_PACKAGE with its kind segment swapped to the requested Minimal or
    Populated and its engine segment swapped to the selected engine's token, so a stale or
    engine-mismatched base value cannot select the wrong package. The NuGet package version
    comes from DATABASE_TEMPLATE_NUGET_VERSION - deliberately named NUGET_VERSION because it
    is the package version (e.g. 1.0.123), not the Data Standard version the package id
    already encodes.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Minimal", "Populated")]
        [string]$RestoreTemplate,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [switch]$RequireNugetVersion
    )

    $basePackageId = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name "DATABASE_TEMPLATE_PACKAGE"
    if ([string]::IsNullOrWhiteSpace($basePackageId)) {
        throw "DATABASE_TEMPLATE_PACKAGE is not set in the effective environment; it names the template package id (e.g. EdFi.Api.Populated.Template.PostgreSql.5.2.0)."
    }
    $basePackageId = $basePackageId.Trim()

    $kindMatches = [System.Text.RegularExpressions.Regex]::Matches($basePackageId, '\.(Minimal|Populated)\.Template\.')
    if ($kindMatches.Count -ne 1) {
        throw "DATABASE_TEMPLATE_PACKAGE '$basePackageId' must contain exactly one '.Minimal.Template.' or '.Populated.Template.' segment; found $($kindMatches.Count)."
    }
    $packageId = $basePackageId.Remove($kindMatches[0].Index, $kindMatches[0].Length).Insert($kindMatches[0].Index, ".$RestoreTemplate.Template.")

    $engineToken = if ($DatabaseEngine -eq "mssql") { "MsSql" } else { "PostgreSql" }
    $engineMatches = [System.Text.RegularExpressions.Regex]::Matches($packageId, '\.Template\.(PostgreSql|MsSql)\.')
    if ($engineMatches.Count -ne 1) {
        throw "DATABASE_TEMPLATE_PACKAGE '$basePackageId' must contain exactly one '.Template.PostgreSql.' or '.Template.MsSql.' segment; found $($engineMatches.Count)."
    }
    $packageId = $packageId.Remove($engineMatches[0].Index, $engineMatches[0].Length).Insert($engineMatches[0].Index, ".Template.$engineToken.")

    # The trailing id segment is the Data Standard version the package encodes (e.g.
    # EdFi.Api.Minimal.Template.PostgreSql.5.2.0 -> 5.2.0). It is extracted with the same
    # strict grammar as the kind/engine segments so the manifest's dataStandardVersion can be
    # proven against it - a distinct concept from the package's own NuGet version.
    $dataStandardVersion = $packageId.Substring($engineMatches[0].Index + ".Template.$engineToken.".Length)
    if ($dataStandardVersion -cnotmatch '^\d+(\.\d+)+\z') {
        throw "DATABASE_TEMPLATE_PACKAGE '$basePackageId' must end with the Data Standard version segment after the engine token (e.g. '...Template.PostgreSql.5.2.0'), but found '$dataStandardVersion'."
    }

    $packageVersion = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name "DATABASE_TEMPLATE_NUGET_VERSION"
    if ([string]::IsNullOrWhiteSpace($packageVersion)) {
        if ($RequireNugetVersion) {
            throw "DATABASE_TEMPLATE_NUGET_VERSION is not set in the effective environment. Feed resolution requires the exact NuGet package version of the template package (e.g. 1.0.123) - this is the package's own version, NOT the Data Standard version encoded in DATABASE_TEMPLATE_PACKAGE - and never floats to latest."
        }
        $packageVersion = ""
    }
    else {
        $packageVersion = $packageVersion.Trim()
    }

    return [pscustomobject]@{
        PackageId           = $packageId
        PackageVersion      = $packageVersion
        DataStandardVersion = $dataStandardVersion
    }
}

function Get-RestorePackageVersionFromFileName {
    <#
    .SYNOPSIS
    Parses the NuGet package version out of a package file name given its package id
    ("<id>.<version>.nupkg", case-insensitively). A file that does not match the expected id
    is a selection error, not a parse fallback.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$PackageFileName,

        [Parameter(Mandatory = $true)]
        [string]$PackageId
    )

    $pattern = "^" + [System.Text.RegularExpressions.Regex]::Escape($PackageId) + '\.(?<version>\d[^\\/]*)\.nupkg$'
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $PackageFileName, $pattern,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    if (-not $match.Success) {
        throw "Package file '$PackageFileName' does not match the expected template package id '$PackageId' ('<id>.<version>.nupkg')."
    }

    return $match.Groups["version"].Value
}

function Find-RestoreTemplatePackage {
    <#
    .SYNOPSIS
    Locates the template package bytes and their detached attestation document, without
    extracting or trusting anything.

    .DESCRIPTION
    Explicit-directory mode (-PackageDirectory): the directory must contain exactly one
    non-companion .nupkg, its file name must match the environment-derived package id, and
    the attestation is the sibling "<nupkg>.attestation.json" file.

    Feed mode: DATABASE_TEMPLATE_FEED_URL (default: the Ed-Fi Azure Artifacts v3 index)
    supplies the feed; a local directory path or file:// URL is a directory feed. The exact
    DATABASE_TEMPLATE_NUGET_VERSION is required - resolution never floats to latest. On an
    HTTP feed the attestation travels as the companion package "<PackageId>.Attestation" at
    the identical version (feeds serve only .nupkg files); on a directory feed it is the
    sibling attestation file. A missing attestation fails closed - there is no
    unsigned-package bypass.

    .OUTPUTS
    PSCustomObject: PackageId, PackageVersion, PackagePath, AttestationJson,
    AttestationSource, DownloadDirectory (transient; $null in explicit-directory mode).
    #>
    param (
        [Parameter(Mandatory = $true)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Minimal", "Populated")]
        [string]$RestoreTemplate,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [string]$PackageDirectory = "",

        # Root for the transient HTTP-feed download directory; defaults to the restore
        # workspace root.
        [string]$DownloadRoot = ""
    )

    if ([string]::IsNullOrWhiteSpace($DownloadRoot)) {
        $DownloadRoot = $script:RestoreWorkspaceRoot
    }

    $failClosedGuidance = "Restore has no unsigned-package bypass; attest the package (see eng/DatabaseTemplates/new-template-dev-trust.ps1) or obtain an attested one."

    if (-not [string]::IsNullOrWhiteSpace($PackageDirectory)) {
        if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
            throw "Package directory was not found: '$PackageDirectory'."
        }

        $identity = Resolve-RestoreTemplatePackageIdentity `
            -EnvironmentValues $EnvironmentValues `
            -RestoreTemplate $RestoreTemplate `
            -DatabaseEngine $DatabaseEngine

        $templatePackages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter "*.nupkg" -File |
                Where-Object { $_.Name -notlike "*.Attestation.*" })
        if ($templatePackages.Count -ne 1) {
            $listing = if ($templatePackages.Count -eq 0) { "<none>" } else { (@($templatePackages | ForEach-Object { $_.Name }) -join ", ") }
            throw "Expected exactly one template .nupkg (companion attestation packages excluded) in '$PackageDirectory', found $($templatePackages.Count): $listing."
        }
        $packageFile = $templatePackages[0]

        $packageVersion = Get-RestorePackageVersionFromFileName -PackageFileName $packageFile.Name -PackageId $identity.PackageId
        if (-not [string]::IsNullOrWhiteSpace($identity.PackageVersion) -and
            -not $identity.PackageVersion.Equals($packageVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Package '$($packageFile.Name)' carries version '$packageVersion' but DATABASE_TEMPLATE_NUGET_VERSION requests '$($identity.PackageVersion)'."
        }

        $attestationPath = Join-Path $PackageDirectory (Get-TemplateAttestationFileName -PackageFileName $packageFile.Name)
        if (-not (Test-Path -LiteralPath $attestationPath -PathType Leaf)) {
            throw "No attestation document was found at '$attestationPath'. $failClosedGuidance"
        }

        return [pscustomobject]@{
            PackageId           = $identity.PackageId
            PackageVersion      = $packageVersion
            DataStandardVersion = $identity.DataStandardVersion
            PackagePath         = $packageFile.FullName
            AttestationJson     = (Get-Content -LiteralPath $attestationPath -Raw)
            AttestationSource   = $attestationPath
            DownloadDirectory   = $null
        }
    }

    # Feed mode.
    $identity = Resolve-RestoreTemplatePackageIdentity `
        -EnvironmentValues $EnvironmentValues `
        -RestoreTemplate $RestoreTemplate `
        -DatabaseEngine $DatabaseEngine `
        -RequireNugetVersion

    $feedUrl = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name "DATABASE_TEMPLATE_FEED_URL" -DefaultValue $script:DefaultTemplateFeedUrl
    $feedUrl = $feedUrl.Trim()

    $isDirectoryFeed = $false
    $feedDirectory = $null
    if ($feedUrl.StartsWith("file://", [System.StringComparison]::OrdinalIgnoreCase)) {
        $feedDirectory = ([System.Uri]::new($feedUrl)).LocalPath
        $isDirectoryFeed = $true
    }
    elseif (Test-Path -LiteralPath $feedUrl -PathType Container) {
        $feedDirectory = $feedUrl
        $isDirectoryFeed = $true
    }

    if ($isDirectoryFeed) {
        $packagePath = Resolve-LocalFolderPackage -FolderPath $feedDirectory -PackageId $identity.PackageId -Version $identity.PackageVersion
        $packageFileName = [System.IO.Path]::GetFileName($packagePath)
        $attestationPath = Join-Path $feedDirectory (Get-TemplateAttestationFileName -PackageFileName $packageFileName)
        if (-not (Test-Path -LiteralPath $attestationPath -PathType Leaf)) {
            throw "No attestation document was found beside the directory-feed package at '$attestationPath'. $failClosedGuidance"
        }

        return [pscustomobject]@{
            PackageId           = $identity.PackageId
            PackageVersion      = $identity.PackageVersion
            DataStandardVersion = $identity.DataStandardVersion
            PackagePath         = $packagePath
            AttestationJson     = (Get-Content -LiteralPath $attestationPath -Raw)
            AttestationSource   = $attestationPath
            DownloadDirectory   = $null
        }
    }

    # HTTP v3 feed: download the template package and its companion attestation package into
    # a transient download directory inside the restore workspace. Downloading writes only
    # into this module's own private directory; nothing is extracted or trusted yet.
    $downloadDirectory = Join-Path $DownloadRoot "download-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null

    try {
        $packagePath = Resolve-HttpV3Package `
            -ServiceIndexUrl $feedUrl `
            -PackageId $identity.PackageId `
            -Version $identity.PackageVersion `
            -DownloadDirectory $downloadDirectory

        $companionPackageId = "$($identity.PackageId).Attestation"
        $companionPath = $null
        try {
            $companionPath = Resolve-HttpV3Package `
                -ServiceIndexUrl $feedUrl `
                -PackageId $companionPackageId `
                -Version $identity.PackageVersion `
                -DownloadDirectory $downloadDirectory
        }
        catch {
            throw "The companion attestation package '$companionPackageId' version '$($identity.PackageVersion)' could not be resolved from the feed: $($_.Exception.Message) HTTP feeds serve only .nupkg files, so the attestation travels as this companion package. $failClosedGuidance"
        }

        # The companion carries exactly one attestation document; it needs no independent
        # trust because authenticity comes from the signature verified against policy anchors.
        $companionExtractDirectory = Join-Path $downloadDirectory "companion-contents"
        Expand-Nupkg -NupkgPath $companionPath -DestinationDirectory $companionExtractDirectory
        $attestationFiles = @(Get-ChildItem -LiteralPath $companionExtractDirectory -Filter "*.nupkg.attestation.json" -Recurse -File)
        if ($attestationFiles.Count -ne 1) {
            throw "The companion attestation package '$companionPackageId' must contain exactly one attestation document, found $($attestationFiles.Count)."
        }

        return [pscustomobject]@{
            PackageId           = $identity.PackageId
            PackageVersion      = $identity.PackageVersion
            DataStandardVersion = $identity.DataStandardVersion
            PackagePath         = $packagePath
            AttestationJson     = (Get-Content -LiteralPath $attestationFiles[0].FullName -Raw)
            AttestationSource   = "$companionPackageId@$($identity.PackageVersion)"
            DownloadDirectory   = $downloadDirectory
        }
    }
    catch {
        if (Test-Path -LiteralPath $downloadDirectory) {
            Remove-Item -LiteralPath $downloadDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Assert-TrustedRestorePackage {
    <#
    .SYNOPSIS
    Authenticates the exact .nupkg bytes against the merged trust policy before anything is
    extracted: the detached attestation must verify against a policy anchor and bind this
    package's SHA-256 and identity. Fail-closed: any verdict other than trusted throws, and
    there is no bypass for local origin.

    .OUTPUTS
    PSCustomObject { PackageSha256, Producer }.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Package,

        [string]$TrackedPolicyPath = "",

        [string]$LocalPolicyPath = ""
    )

    if ([string]::IsNullOrWhiteSpace($TrackedPolicyPath)) {
        $TrackedPolicyPath = Join-Path $PSScriptRoot "template-trust-policy.json"
    }
    if ([string]::IsNullOrWhiteSpace($LocalPolicyPath)) {
        $LocalPolicyPath = Join-Path $PSScriptRoot "template-trust-policy.local.json"
    }

    $trustPolicy = Read-TemplateTrustPolicy -TrackedPolicyPath $TrackedPolicyPath -LocalPolicyPath $LocalPolicyPath
    $packageSha256 = Get-FileSha256Hex -Path $Package.PackagePath

    $verdict = Test-TemplateAttestation `
        -AttestationJson $Package.AttestationJson `
        -PackageSha256 $packageSha256 `
        -ExpectedPackageId $Package.PackageId `
        -ExpectedPackageVersion $Package.PackageVersion `
        -TrustPolicy $trustPolicy

    if (-not $verdict.IsTrusted) {
        throw "Template package '$([System.IO.Path]::GetFileName($Package.PackagePath))' failed authentication: $($verdict.Reason) Restore has no unsigned-package bypass; local origin is not trust."
    }

    return [pscustomobject]@{
        PackageSha256 = $packageSha256
        Producer      = $verdict.Producer
    }
}

function Assert-RestoreManifestMatchesRequest {
    <#
    .SYNOPSIS
    Fails unless a (shape-valid) restore manifest declares exactly the engine, template
    kind, and Data Standard version this restore run selected - the Data Standard version
    being the one the resolved package id encodes in its trailing segment, a distinct
    concept from the package's own NuGet version. The shape contract already pins the fixed
    DmsDatastoreOnly content profile and every field type.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Manifest,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Minimal", "Populated")]
        [string]$RestoreTemplate,

        [Parameter(Mandatory = $true)]
        [string]$DataStandardVersion
    )

    if ([string]$Manifest.databaseEngine -cne $DatabaseEngine) {
        throw "The restore manifest declares databaseEngine '$($Manifest.databaseEngine)' but this restore selected '$DatabaseEngine'."
    }
    if ([string]$Manifest.templateKind -cne $RestoreTemplate) {
        throw "The restore manifest declares templateKind '$($Manifest.templateKind)' but this restore requested '$RestoreTemplate'."
    }
    if (-not ([string]$Manifest.dataStandardVersion).Equals($DataStandardVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Data Standard mismatch: the restore manifest declares dataStandardVersion '$($Manifest.dataStandardVersion)' but the resolved package id encodes Data Standard '$DataStandardVersion'. This is a Data Standard problem, not a package (NuGet) version problem."
    }
}

function Initialize-RestorePackageStage {
    <#
    .SYNOPSIS
    Stages the authenticated package into a private workspace: reads the .nupkg ONCE into
    memory, proves those bytes are the authenticated bytes, and takes everything else from
    that same buffer. The archive metadata is validated directly from the in-memory archive
    before any artifact content is read: unique entry names, exactly one restore manifest
    (shape, requested engine/kind/Data Standard, package identity) and one nuspec agreeing on
    the identity, and exactly one manifest-declared database artifact with no undeclared
    artifact beside it. Only then is the artifact entry hashed against the manifest. Nothing is
    extracted to disk and no file is reopened by path, so a file replaced on disk can never be
    read under the authenticated hash.

    The authenticated buffer is RETAINED on the stage (PackageBytes, with the artifact's entry
    name in ArtifactEntryName) and is the only source either transfer reads: scratch validation
    and target restore each stream the artifact entry from it into the database container
    (Copy-RestoreStagedArtifactToContainer). The files written under StageDirectory (the
    package and manifest records) never feed a transfer, so replacing them - or the source
    package - after this read cannot change what scratch or target restore consume. Staged
    files are marked read-only as a guard against accidents only.

    .OUTPUTS
    PSCustomObject: StageDirectory, PackagePath, PackageSha256, PackageBytes,
    ArtifactEntryName, Manifest, ManifestPath, ArtifactSha256, Producer.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Package,

        [Parameter(Mandatory = $true)]
        [string]$AuthenticatedPackageSha256,

        [Parameter(Mandatory = $true)]
        [string]$Producer,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Minimal", "Populated")]
        [string]$RestoreTemplate,

        [string]$StageRoot = ""
    )

    if ([string]::IsNullOrWhiteSpace($StageRoot)) {
        $StageRoot = $script:RestoreWorkspaceRoot
    }

    $stageDirectory = Join-Path $StageRoot "stage-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
    # Provider-resolved: the .NET file and zip APIs below resolve relative paths against the
    # process working directory, which Push-Location does not update.
    $stageDirectory = (Resolve-Path -LiteralPath $stageDirectory).ProviderPath

    $packageArchive = $null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop

        # The one read of the package. The hash, the extraction, and every validated file
        # below come from this buffer, which no other process can replace.
        $packageBytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Package.PackagePath).ProviderPath)

        # The read must carry the exact authenticated bytes; a mismatch means the source file
        # changed between authentication and staging.
        $stagedPackageSha256 = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($packageBytes)).ToLowerInvariant()
        if ($stagedPackageSha256 -cne $AuthenticatedPackageSha256) {
            throw "The staged package's SHA-256 '$stagedPackageSha256' no longer matches the authenticated bytes '$AuthenticatedPackageSha256'; the package changed between authentication and staging."
        }

        # A record of the staged package; nothing reads it back.
        $stagedPackagePath = Join-Path $stageDirectory ([System.IO.Path]::GetFileName($Package.PackagePath))
        [System.IO.File]::WriteAllBytes($stagedPackagePath, $packageBytes)

        $packageFileName = [System.IO.Path]::GetFileName($Package.PackagePath)
        $packageArchive = [System.IO.Compression.ZipArchive]::new(
            [System.IO.MemoryStream]::new($packageBytes, $false),
            [System.IO.Compression.ZipArchiveMode]::Read)

        # Archive metadata first, straight from the in-memory archive: no artifact content is
        # read until every check below has passed.
        $fileEntries = @($packageArchive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        $duplicateEntryNames = @($fileEntries |
                Group-Object { $_.FullName.Replace('\', '/').ToLowerInvariant() } |
                Where-Object { $_.Count -gt 1 } |
                ForEach-Object { $_.Group[0].FullName })
        if ($duplicateEntryNames.Count -gt 0) {
            throw "Package '$packageFileName' holds more than one archive entry for: $($duplicateEntryNames -join ', ')."
        }

        $manifestEntries = @($fileEntries | Where-Object { $_.Name -ieq (Get-RestoreManifestFileName) })
        if ($manifestEntries.Count -ne 1) {
            throw "Package '$packageFileName' must contain exactly one $(Get-RestoreManifestFileName), found $($manifestEntries.Count). Packages without a restore manifest are not eligible for restore."
        }
        $manifestText = Read-RestorePackageArchiveEntryText -Entry $manifestEntries[0]
        $manifest = Read-RestoreManifest -Path "$packageFileName!/$($manifestEntries[0].FullName)" -Content $manifestText

        Assert-RestoreManifestMatchesRequest -Manifest $manifest -DatabaseEngine $DatabaseEngine -RestoreTemplate $RestoreTemplate -DataStandardVersion $Package.DataStandardVersion

        # Identity triangle: the request, the manifest, and the nuspec must all agree on the
        # package id and version.
        if (-not ([string]$manifest.packageId).Equals($Package.PackageId, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$manifest.packageVersion).Equals($Package.PackageVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The restore manifest declares package identity '$($manifest.packageId)@$($manifest.packageVersion)' but this restore resolved '$($Package.PackageId)@$($Package.PackageVersion)'."
        }

        $nuspecEntries = @($fileEntries | Where-Object { $_.Name -like "*.nuspec" })
        if ($nuspecEntries.Count -ne 1) {
            throw "Package '$packageFileName' must contain exactly one .nuspec, found $($nuspecEntries.Count)."
        }
        [xml]$nuspecXml = Read-RestorePackageArchiveEntryText -Entry $nuspecEntries[0]
        $nuspecId = [string]$nuspecXml.package.metadata.id
        $nuspecVersion = [string]$nuspecXml.package.metadata.version
        if (-not $nuspecId.Equals($Package.PackageId, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not $nuspecVersion.Equals($Package.PackageVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The package nuspec declares identity '$nuspecId@$nuspecVersion' but this restore resolved '$($Package.PackageId)@$($Package.PackageVersion)'."
        }

        # Exactly one database artifact: the manifest-declared entry, and no undeclared
        # .sql/.bak beside it that a later stage could be tricked into consuming.
        $declaredArtifactEntries = @($fileEntries | Where-Object { $_.Name -ieq [string]$manifest.artifactFileName })
        if ($declaredArtifactEntries.Count -ne 1) {
            throw "Package '$packageFileName' must contain exactly one artifact named '$($manifest.artifactFileName)', found $($declaredArtifactEntries.Count)."
        }
        $artifactEntry = $declaredArtifactEntries[0]
        $undeclaredArtifactEntries = @($fileEntries | Where-Object {
                ($_.Name -like "*.sql" -or $_.Name -like "*.bak") -and -not [object]::ReferenceEquals($_, $artifactEntry)
            })
        if ($undeclaredArtifactEntries.Count -gt 0) {
            $listing = (@($undeclaredArtifactEntries | ForEach-Object { $_.Name }) -join ", ")
            throw "Package '$packageFileName' contains database artifacts beyond the manifest-declared '$($manifest.artifactFileName)': $listing."
        }

        # Only now is artifact content read: hashed straight from the archive entry, never from
        # a file on disk.
        $artifactSha256 = Get-RestorePackageArchiveEntrySha256 -Entry $artifactEntry
        if ($artifactSha256 -cne [string]$manifest.artifactSha256) {
            throw "The packaged artifact's SHA-256 '$artifactSha256' does not match the manifest's artifactSha256 '$($manifest.artifactSha256)'."
        }

        # A record of the validated manifest; nothing reads it back. The artifact is never
        # written to the stage: both transfers stream it from PackageBytes.
        $manifestRecordPath = Join-Path $stageDirectory (Get-RestoreManifestFileName)
        [System.IO.File]::WriteAllText($manifestRecordPath, $manifestText, [System.Text.UTF8Encoding]::new($false))

        # Read-only guards against accidental edits only; a process that can write here can
        # also clear the flag. Transfers never read these files - they stream PackageBytes.
        foreach ($stagedFile in @(Get-ChildItem -LiteralPath $stageDirectory -Recurse -File)) {
            $stagedFile.IsReadOnly = $true
        }

        return [pscustomobject]@{
            StageDirectory    = $stageDirectory
            PackagePath       = $stagedPackagePath
            PackageSha256     = $AuthenticatedPackageSha256
            PackageBytes      = $packageBytes
            ArtifactEntryName = $artifactEntry.FullName
            Manifest          = $manifest
            ManifestPath      = $manifestRecordPath
            ArtifactSha256    = $artifactSha256
            Producer          = $Producer
        }
    }
    catch {
        Remove-RestorePackageStage -StageDirectory $stageDirectory
        throw
    }
    finally {
        if ($null -ne $packageArchive) {
            $packageArchive.Dispose()
        }
    }
}

function Get-RestorePackageArchiveEntrySha256 {
    <#
    .SYNOPSIS
    The lowercase SHA-256 of one in-memory archive entry's bytes.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $entryStream = $Entry.Open()
    try {
        return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($entryStream)).ToLowerInvariant()
    }
    finally {
        $entryStream.Dispose()
    }
}

function Read-RestorePackageArchiveEntryText {
    <#
    .SYNOPSIS
    The UTF-8 text of one in-memory archive entry (a byte-order mark is honored).
    #>
    param (
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $reader = [System.IO.StreamReader]::new($Entry.Open(), [System.Text.Encoding]::UTF8, $true)
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

function Remove-RestorePackageStage {
    <#
    .SYNOPSIS
    Removes a package stage directory, clearing the read-only attributes staging set.
    Tolerates an already-absent directory so failure-path cleanup is idempotent.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal restore cleanup helper; the restore flow does not expose -WhatIf end to end, and a silent no-op would leave staged package bytes behind.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$StageDirectory
    )

    if (-not (Test-Path -LiteralPath $StageDirectory)) {
        return
    }

    foreach ($stagedFile in @(Get-ChildItem -LiteralPath $StageDirectory -Recurse -File -Force)) {
        if ($stagedFile.IsReadOnly) {
            $stagedFile.IsReadOnly = $false
        }
    }
    Remove-Item -LiteralPath $StageDirectory -Recurse -Force
}

function Resolve-RestoreTargetDatabaseName {
    <#
    .SYNOPSIS
    Resolves the physical database the restore will replace, using exactly the same keys and
    defaults configure-local-data-store.ps1 registers (POSTGRES_DB_NAME / MSSQL_DB_NAME,
    default edfi_datamanagementservice), so the restored database and the registered data
    store can never diverge.

    The env file is evaluated the way Compose and the topology validation evaluate it:
    sequentially, with case-sensitive keys and ambient precedence. A case-insensitive table
    would merge RESTORE_DB and restore_db and could select a database Compose does not.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$EnvironmentFile,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    $keyName = if ($DatabaseEngine -eq "mssql") { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }
    $evaluation = Resolve-DotenvFileSequentially -Path $EnvironmentFile
    $targetDatabaseName = Get-SequentialEffectiveValue -Evaluation $evaluation -Name $keyName -DefaultValue "edfi_datamanagementservice"

    return $targetDatabaseName.Trim()
}

function Assert-RestoreTargetDatabaseNameSafe {
    <#
    .SYNOPSIS
    Parameter-time target safety: the selected restore target must be a safe identifier,
    never a reserved system database (PostgreSQL postgres/template0/template1, SQL Server
    master/model/msdb/tempdb), and - with -SeparateConfigDatabase - never the dedicated
    Configuration Service database (on SQL Server also never the topology's fixed
    edfi_configurationservice), which restore must never replace. In shared topology the
    target IS the shared database by design, so no separate-CMS comparison applies. The same
    checks run again against the live catalog before any destructive work.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$TargetDatabaseName,

        [switch]$SeparateConfigDatabase,

        [string]$EffectiveConfigDatabaseName = ""
    )

    Assert-SafeRestoreDatabaseName -DatabaseEngine $DatabaseEngine -DatabaseName $TargetDatabaseName -Purpose "restore target"

    if ($SeparateConfigDatabase) {
        if ([string]::IsNullOrWhiteSpace($EffectiveConfigDatabaseName)) {
            throw "-SeparateConfigDatabase requires the effective Configuration Service database name (CONFIGURATION_SERVICE_DATABASE_NAME) so the restore target can be proven distinct from it."
        }
        if ($TargetDatabaseName.Trim().Equals($EffectiveConfigDatabaseName.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The restore target database resolves to the dedicated Configuration Service database ('$($EffectiveConfigDatabaseName.Trim())') selected by -SeparateConfigDatabase. Restore never replaces a separate Configuration Service database."
        }
        # The SQL Server separate topology fixes the CMS database at edfi_configurationservice
        # (Assert-MssqlTopologyPhysicalConsistency), and that check runs only after restore. An
        # ambient CONFIGURATION_SERVICE_DATABASE_NAME can make the effective name above differ
        # from it, so the fixed name is refused whatever that name is.
        if ($DatabaseEngine -eq "mssql" -and
            $TargetDatabaseName.Trim().Equals("edfi_configurationservice", [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The restore target database resolves to 'edfi_configurationservice', the dedicated Configuration Service database the SQL Server separate topology selected by -SeparateConfigDatabase uses. Restore never replaces a separate Configuration Service database."
        }
    }
}


function New-RestoreCandidateWorkspace {
    <#
    .SYNOPSIS
    Builds a restore candidate workspace by running the UNCHANGED prepare phases redirected into
    a private candidate directory via DMS_BOOTSTRAP_ROOT_OVERRIDE.

    .DESCRIPTION
    The override is set strictly around the two prepare invocations and cleared in a finally
    block with Remove-Item (a $null assignment can leave a present-but-blank value on some
    hosts), so no later phase command can ever observe it. A prepare failure removes the partial
    candidate and rethrows. The active .bootstrap workspace is never written here; only the
    prepare phases honor the override, and every consuming phase command refuses to run while
    it is set.

    The one thing read from the active workspace is the published schema tool
    (tools/api-schema-tools, the documented `dotnet publish` location): it is copied into the
    candidate BEFORE the prepare phases run, so their tool discovery under the override finds
    it, and the whole-tree commit carries it into the next active workspace instead of deleting
    it. Schema, claims, and seed state are still regenerated by the prepare phases.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Restore-internal staging helper; the restore flow does not expose -WhatIf end to end, and a silent no-op would produce no candidate for the cross-check to validate.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$EnvironmentFile,

        [string]$WorkspaceRoot = "",

        # Test seams: the production prepare scripts stage real packages and need the schema
        # tool; tests substitute recording stubs that honor the same override contract.
        [string]$PrepareSchemaScriptPath = "",

        [string]$PrepareClaimsScriptPath = "",

        # Test seam; production default is the module-adjacent active workspace.
        [string]$ActiveBootstrapRoot = ""
    )

    if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
        $WorkspaceRoot = $script:RestoreWorkspaceRoot
    }
    if ([string]::IsNullOrWhiteSpace($ActiveBootstrapRoot)) {
        $ActiveBootstrapRoot = Join-Path $PSScriptRoot ".bootstrap"
    }
    if ([string]::IsNullOrWhiteSpace($PrepareSchemaScriptPath)) {
        $PrepareSchemaScriptPath = Join-Path $PSScriptRoot "prepare-dms-schema.ps1"
    }
    if ([string]::IsNullOrWhiteSpace($PrepareClaimsScriptPath)) {
        $PrepareClaimsScriptPath = Join-Path $PSScriptRoot "prepare-dms-claims.ps1"
    }

    $candidateDirectory = Join-Path $WorkspaceRoot "candidate-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $candidateDirectory -Force | Out-Null

    try {
        $publishedToolDirectory = Join-Path $ActiveBootstrapRoot "tools/api-schema-tools"
        if (Test-Path -LiteralPath $publishedToolDirectory -PathType Container) {
            $candidateToolsDirectory = Join-Path $candidateDirectory "tools"
            New-Item -ItemType Directory -Path $candidateToolsDirectory -Force | Out-Null
            Copy-Item -LiteralPath $publishedToolDirectory -Destination $candidateToolsDirectory -Recurse -Force -ErrorAction Stop
        }

        $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = $candidateDirectory
        foreach ($preparePhase in @(
            [pscustomobject]@{ ScriptPath = $PrepareSchemaScriptPath; Arguments = @{ EnvironmentFile = $EnvironmentFile } },
            [pscustomobject]@{ ScriptPath = $PrepareClaimsScriptPath; Arguments = @{} }
        )) {
            # Prepare scripts signal failure by throwing and run no trailing native command;
            # reset the native-exit sentinel so a stale nonzero value from an earlier command in
            # the session is never misread as a phase failure.
            $global:LASTEXITCODE = 0
            $prepareArguments = $preparePhase.Arguments
            & $preparePhase.ScriptPath @prepareArguments | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "Candidate prepare phase '$([System.IO.Path]::GetFileName($preparePhase.ScriptPath))' exited with code $LASTEXITCODE."
            }
        }

        $candidateManifestPath = Join-Path $candidateDirectory "bootstrap-manifest.json"
        if (-not (Test-Path -LiteralPath $candidateManifestPath -PathType Leaf)) {
            throw "The prepare phases completed but produced no candidate bootstrap manifest at '$candidateManifestPath'."
        }

        return [pscustomobject]@{
            CandidateDirectory    = $candidateDirectory
            CandidateManifestPath = $candidateManifestPath
        }
    }
    catch {
        if (Test-Path -LiteralPath $candidateDirectory) {
            Remove-Item -LiteralPath $candidateDirectory -Recurse -Force
        }
        throw
    }
    finally {
        Remove-Item Env:\DMS_BOOTSTRAP_ROOT_OVERRIDE -ErrorAction SilentlyContinue
        # The prepare scripts re-import bootstrap-manifest -Force -Global while the override is
        # in effect, baking the CANDIDATE root into the session's module instance - which
        # outlives the override and would poison any later no-Force nested bind (the schema
        # workspace validation resolves the moved candidate path; proven by the live restore
        # smoke). Re-evaluate the module now that the override is cleared so every later
        # consumer resolves the ACTIVE root again.
        Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force -Global
    }
}

function ConvertTo-RestoreProjectSchemaName {
    <#
    .SYNOPSIS
    Normalizes a project endpoint name to the resource schema name the database uses: lowercase
    with hyphens removed (endpoint 'ed-fi' -> schema 'edfi'), the same rule the template
    producer's database-side schema names follow. An endpoint that normalizes to an empty name
    is an input defect.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$ProjectEndpointName
    )

    $schemaName = $ProjectEndpointName.ToLowerInvariant().Replace("-", "")
    if ([string]::IsNullOrWhiteSpace($schemaName)) {
        throw "Project endpoint name '$ProjectEndpointName' normalizes to an empty schema name."
    }
    return $schemaName
}

function Get-RestoreCandidateSchemaFact {
    <#
    .SYNOPSIS
    Reads the facts the package<->candidate cross-check needs from a candidate workspace: the
    schema section's dataStandardVersion, apiSchemaFormatVersion, effectiveSchemaHash, and
    selectedExtensions, plus the core project endpoint and the staged schema file paths (core
    first) from the candidate's ApiSchema manifest. Every fact is required - a candidate the
    prepare phases produced without one is not comparable and fails here rather than passing a
    weaker cross-check. Candidate-supplied paths are containment-validated before use: parent
    traversal or absolute paths in schema.apiSchemaManifestPath or projects[].schemaPath fail
    closed, so every path handed to the schema tool stays inside the candidate.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$CandidateDirectory
    )

    $manifestPath = Join-Path $CandidateDirectory "bootstrap-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Candidate workspace '$CandidateDirectory' has no bootstrap-manifest.json."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest -isnot [System.Collections.IDictionary] -or
        -not $manifest.Contains("schema") -or
        $manifest["schema"] -isnot [System.Collections.IDictionary]) {
        throw "Candidate manifest '$manifestPath' has no schema section."
    }
    $schemaSection = $manifest["schema"]

    foreach ($requiredField in @("dataStandardVersion", "apiSchemaFormatVersion", "effectiveSchemaHash", "apiSchemaManifestPath")) {
        if (-not $schemaSection.Contains($requiredField) -or [string]::IsNullOrWhiteSpace([string]$schemaSection[$requiredField])) {
            throw "Candidate manifest '$manifestPath' schema section is missing '$requiredField'."
        }
    }

    $selectedExtensions = @()
    if ($schemaSection.Contains("selectedExtensions") -and $null -ne $schemaSection["selectedExtensions"]) {
        if ($schemaSection["selectedExtensions"] -isnot [System.Collections.IList]) {
            throw "Candidate manifest '$manifestPath' schema.selectedExtensions must be a JSON array."
        }
        $selectedExtensions = @($schemaSection["selectedExtensions"] | ForEach-Object { [string]$_ })
    }

    # Candidate-supplied paths are validated with the same rules the runtime workspace resolver
    # enforces (relative only, no empty/./.. segments) BEFORE any join, so a malformed candidate
    # can never point the cross-check - and through it the schema tool - at files outside the
    # candidate, including the active .bootstrap workspace.
    $apiSchemaManifestRelativePath = Resolve-BootstrapWorkspaceRelativePath `
        -RelativePath ([string]$schemaSection["apiSchemaManifestPath"]) `
        -ManifestField "schema.apiSchemaManifestPath"
    $apiSchemaManifestPath = Join-Path $CandidateDirectory $apiSchemaManifestRelativePath
    if (-not (Test-Path -LiteralPath $apiSchemaManifestPath -PathType Leaf)) {
        throw "Candidate ApiSchema manifest is missing: '$apiSchemaManifestPath'."
    }
    $apiSchemaManifest = Get-Content -LiteralPath $apiSchemaManifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($apiSchemaManifest -isnot [System.Collections.IDictionary] -or
        -not $apiSchemaManifest.Contains("projects") -or
        $apiSchemaManifest["projects"] -isnot [System.Collections.IList] -or
        @($apiSchemaManifest["projects"]).Count -lt 1) {
        throw "Candidate ApiSchema manifest '$apiSchemaManifestPath' declares no projects."
    }

    $apiSchemaWorkspaceRoot = Split-Path -Parent $apiSchemaManifestPath
    $coreEndpointNames = [System.Collections.Generic.List[string]]::new()
    $schemaFilePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($project in @($apiSchemaManifest["projects"])) {
        if ($project -isnot [System.Collections.IDictionary]) {
            throw "Candidate ApiSchema manifest '$apiSchemaManifestPath' has a malformed project entry."
        }
        $schemaRelativePath = Resolve-BootstrapWorkspaceRelativePath `
            -RelativePath ([string]$project["schemaPath"]) `
            -ManifestField "projects[].schemaPath"
        $schemaFilePaths.Add((Join-Path $apiSchemaWorkspaceRoot $schemaRelativePath))
        if (-not [bool]$project["isExtensionProject"]) {
            $coreEndpointNames.Add([string]$project["projectEndpointName"])
        }
    }
    if ($coreEndpointNames.Count -ne 1) {
        throw "Candidate ApiSchema manifest '$apiSchemaManifestPath' must declare exactly one core project, found $($coreEndpointNames.Count)."
    }

    return [pscustomobject]@{
        DataStandardVersion     = [string]$schemaSection["dataStandardVersion"]
        ApiSchemaFormatVersion  = [string]$schemaSection["apiSchemaFormatVersion"]
        EffectiveSchemaHash     = [string]$schemaSection["effectiveSchemaHash"]
        SelectedExtensions      = [string[]]$selectedExtensions
        CoreProjectEndpointName = $coreEndpointNames[0]
        SchemaFilePaths         = [string[]]@($schemaFilePaths)
    }
}

function Get-RestoreCandidateRelationalMappingVersion {
    <#
    .SYNOPSIS
    Runs 'api-schema-tools ddl emit --ddl-manifest' over the candidate's staged schema files
    (core first) and returns the emitted relational_mapping_version. Output goes into a fresh
    subdirectory of the private package stage - never into the candidate tree - because the tool
    requires an empty output directory and the candidate must stay byte-identical to what the
    prepare phases produced.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string[]]$SchemaFilePath,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$OutputRoot,

        [string]$SchemaToolPath = ""
    )

    $toolPath = Resolve-DmsSchemaTool -RequestedPath $SchemaToolPath
    $dialect = if ($DatabaseEngine -eq "mssql") { "mssql" } else { "pgsql" }
    $outputDirectory = Join-Path $OutputRoot "ddl-validation-$([Guid]::NewGuid().ToString('N'))"

    $arguments = @("ddl", "emit", "--schema") + $SchemaFilePath + @("--output", $outputDirectory, "--dialect", $dialect, "--ddl-manifest")
    $global:LASTEXITCODE = 0
    $output = if ($toolPath.EndsWith(".ps1", [System.StringComparison]::OrdinalIgnoreCase)) {
        & pwsh -NoLogo -NoProfile -File $toolPath @arguments 2>&1
    } else {
        & $toolPath @arguments 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "api-schema-tools ddl emit failed with exit code $LASTEXITCODE during the candidate cross-check. Output: $(($output | Out-String).Trim())"
    }

    $ddlManifestPath = Join-Path $outputDirectory "ddl.manifest.json"
    if (-not (Test-Path -LiteralPath $ddlManifestPath -PathType Leaf)) {
        throw "api-schema-tools ddl emit reported success but produced no ddl.manifest.json at '$ddlManifestPath'."
    }
    $ddlManifest = Get-Content -LiteralPath $ddlManifestPath -Raw | ConvertFrom-Json
    $mappingProperty = $ddlManifest.PSObject.Properties['relational_mapping_version']
    if ($null -eq $mappingProperty -or [string]::IsNullOrWhiteSpace([string]$mappingProperty.Value)) {
        throw "ddl.manifest.json at '$ddlManifestPath' carries no relational_mapping_version."
    }

    return [string]$mappingProperty.Value
}

function Assert-RestoreManifestMatchesCandidate {
    <#
    .SYNOPSIS
    Proves a staged template package's restore manifest describes exactly the schema state the
    candidate workspace would run: engine, DocumentJson physical baseline, Data Standard
    version, ApiSchema format version, effective schema hash, relational mapping version, and
    the full project-schema set (candidate endpoint names normalized to schema names, e.g.
    'ed-fi' -> 'edfi'). Pure comparison - callers own candidate/stage removal on mismatch.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Manifest,

        [Parameter(Mandatory = $true)]
        $CandidateFact,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$CandidateRelationalMappingVersion
    )

    if ([string]$Manifest.databaseEngine -cne $DatabaseEngine) {
        throw "The restore manifest declares databaseEngine '$($Manifest.databaseEngine)' but this restore selected '$DatabaseEngine'."
    }

    $baselineDocumentJsonType = Get-RestoreDocumentJsonBaselineType -DatabaseEngine $DatabaseEngine
    if ([string]$Manifest.documentJsonColumnType -cne $baselineDocumentJsonType) {
        throw "DocumentJson physical baseline mismatch: the restore manifest declares documentJsonColumnType '$($Manifest.documentJsonColumnType)' but this checkout's $DatabaseEngine baseline is '$baselineDocumentJsonType'."
    }

    if (-not ([string]$Manifest.dataStandardVersion).Equals($CandidateFact.DataStandardVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Data Standard mismatch: the restore manifest declares dataStandardVersion '$($Manifest.dataStandardVersion)' but the candidate workspace staged Data Standard '$($CandidateFact.DataStandardVersion)'."
    }

    if (-not ([string]$Manifest.apiSchemaFormatVersion).Equals($CandidateFact.ApiSchemaFormatVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ApiSchema format version mismatch: the restore manifest declares apiSchemaFormatVersion '$($Manifest.apiSchemaFormatVersion)' but the candidate workspace staged '$($CandidateFact.ApiSchemaFormatVersion)'."
    }

    # Both sides are lowercase hex by contract (manifest shape + Invoke-DmsSchemaHash), so the
    # comparison is deliberately case-sensitive - a casing difference means a contract breach,
    # not an equality.
    if ([string]$Manifest.effectiveSchemaHash -cne $CandidateFact.EffectiveSchemaHash) {
        throw "Effective schema hash mismatch: the restore manifest declares '$($Manifest.effectiveSchemaHash)' but the candidate workspace staged '$($CandidateFact.EffectiveSchemaHash)'."
    }

    if ([string]$Manifest.relationalMappingVersion -cne $CandidateRelationalMappingVersion) {
        throw "Relational mapping version mismatch: the restore manifest declares '$($Manifest.relationalMappingVersion)' but the candidate schema set emits '$CandidateRelationalMappingVersion'."
    }

    $candidateProjectSchemaNames = @(ConvertTo-RestoreProjectSchemaName -ProjectEndpointName $CandidateFact.CoreProjectEndpointName) +
        @($CandidateFact.SelectedExtensions | ForEach-Object { ConvertTo-RestoreProjectSchemaName -ProjectEndpointName $_ })
    $manifestProjectList = @(@($Manifest.projects) | Sort-Object) -join ", "
    $candidateProjectList = @($candidateProjectSchemaNames | Sort-Object) -join ", "
    if ($manifestProjectList -cne $candidateProjectList) {
        throw "Project set mismatch: the restore manifest declares projects [$manifestProjectList] but the candidate workspace stages [$candidateProjectList]."
    }
}

function Invoke-RestoreCandidateCrossCheck {
    <#
    .SYNOPSIS
    Runs the full package<->candidate cross-check before any Docker activity: reads the
    candidate facts, emits the candidate schema set's relational mapping version into the
    private package stage, and asserts every comparable field. On ANY failure both transient
    inputs are discarded - the candidate workspace and the staged package - and the failure
    rethrows; the active .bootstrap workspace and the target database are never touched here.
    Returns the candidate facts on success.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Restore-internal validation step; the restore flow does not expose -WhatIf end to end, and a silent no-op would skip the cross-check gate.')]
    param (
        [Parameter(Mandatory = $true)]
        $Manifest,

        [Parameter(Mandatory = $true)]
        [string]$CandidateDirectory,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$StageDirectory,

        [string]$SchemaToolPath = ""
    )

    try {
        $candidateFact = Get-RestoreCandidateSchemaFact -CandidateDirectory $CandidateDirectory
        $candidateMappingVersion = Get-RestoreCandidateRelationalMappingVersion `
            -SchemaFilePath $candidateFact.SchemaFilePaths `
            -DatabaseEngine $DatabaseEngine `
            -OutputRoot $StageDirectory `
            -SchemaToolPath $SchemaToolPath
        Assert-RestoreManifestMatchesCandidate `
            -Manifest $Manifest `
            -CandidateFact $candidateFact `
            -DatabaseEngine $DatabaseEngine `
            -CandidateRelationalMappingVersion $candidateMappingVersion
        return $candidateFact
    }
    catch {
        if (Test-Path -LiteralPath $CandidateDirectory) {
            Remove-Item -LiteralPath $CandidateDirectory -Recurse -Force
        }
        Remove-RestorePackageStage -StageDirectory $StageDirectory
        throw
    }
}


function Get-RestoreDatabaseOnlyComposeFile {
    <#
    .SYNOPSIS
    The compose-file arguments of the database-only slice, mirroring exactly what
    start-local-dms.ps1 / start-published-dms.ps1 assemble for -DbOnly: the engine's database
    file, plus the PostgreSQL tmpfs override under the same condition the start scripts use
    (the POSTGRES_USE_TMPFS process-environment opt-in, PostgreSQL only). No application, CMS,
    or bootstrap compose files belong here - the slice defines only the "db" service.

    .DESCRIPTION
    Paths are $PSScriptRoot-anchored so the set resolves identically regardless of the caller's
    working directory; the compose project is pinned separately by the caller's -p argument.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    $databaseComposeFile = if ($DatabaseEngine -eq "mssql") { "mssql.yml" } else { "postgresql.yml" }
    $composeArguments = [System.Collections.Generic.List[string]]::new()
    $composeArguments.Add("-f")
    $composeArguments.Add((Join-Path $PSScriptRoot $databaseComposeFile))

    $usePostgresqlTmpfs = [string]::Equals(
        $env:POSTGRES_USE_TMPFS,
        "true",
        [System.StringComparison]::OrdinalIgnoreCase
    )
    if ($usePostgresqlTmpfs -and $DatabaseEngine -eq "postgresql") {
        $composeArguments.Add("-f")
        $composeArguments.Add((Join-Path $PSScriptRoot "postgresql-tmpfs.yml"))
    }

    return [string[]]$composeArguments.ToArray()
}

function Stop-RestoreDatabaseOnlySlice {
    <#
    .SYNOPSIS
    Stops ONLY the "db" service of the restore's database-only slice, using the same compose
    shape that started it: the database-only compose files, the preflight environment file, and
    the run's compose project. A bare "docker compose -p <project> stop db" (no -f, no
    --env-file) asks Compose to resolve a project it cannot see, so it is never used here.

    .DESCRIPTION
    Deliberately "stop", never "down": no volumes, no networks, and no workspaces are removed -
    the restore flow's only destructive step is the guarded target replacement. Failures are
    fail-closed through Invoke-RestoreDockerCommand (unresolvable docker, invocation failure,
    and nonzero exit all throw).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Restore-internal lifecycle step that stops one container; the restore flow does not expose -WhatIf end to end, and a silent no-op would leave the database running into the workspace commit.')]
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("dms-local", "dms-published")]
        [string]$ProjectName,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$EnvironmentFile
    )

    $composeArguments = @(Get-RestoreDatabaseOnlyComposeFile -DatabaseEngine $DatabaseEngine)
    $argumentList = @("compose") + $composeArguments + @("--env-file", $EnvironmentFile, "-p", $ProjectName, "stop", "db")
    $null = Invoke-RestoreDockerCommand `
        -ArgumentList $argumentList `
        -FailureMessage "Failed to stop the database-only slice ('db') of compose project '$ProjectName'."
}

function Assert-DmsComposeProjectStopped {
    <#
    .SYNOPSIS
    Stop proof for one DMS compose project: fails when any container of the project is still
    running, listing the container names, and fails CLOSED when docker itself errors - an
    indeterminate answer is never treated as "stopped". The restore flow must hold this proof
    immediately before replacing the bootstrap workspace or the target database.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("dms-local", "dms-published")]
        [string]$ProjectName
    )

    # A missing or unresolvable docker must read as indeterminate, never as stopped: a
    # CommandNotFound failure neither sets $LASTEXITCODE nor flows through stream redirection,
    # and under a Continue error preference it would silently fall through to the empty-output
    # "stopped" verdict. Resolve explicitly and invoke inside a fail-closed try.
    try {
        $null = Get-Command docker -ErrorAction Stop
    }
    catch {
        throw "Stop proof is indeterminate: the 'docker' command is not available in this session, so it cannot be proven that compose project '$ProjectName' is stopped. Refusing to continue."
    }

    $global:LASTEXITCODE = 0
    try {
        $output = docker ps --filter "label=com.docker.compose.project=$ProjectName" --format '{{.Names}}' 2>&1
    }
    catch {
        throw "Stop proof is indeterminate: invoking 'docker ps' failed while checking compose project '$ProjectName' ($(($_.Exception.Message | Out-String).Trim())). Refusing to continue without proof that the project is stopped."
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Stop proof is indeterminate: 'docker ps' exited with code $LASTEXITCODE while checking compose project '$ProjectName' ($(($output | Out-String).Trim())). Refusing to continue without proof that the project is stopped."
    }

    $runningContainers = @($output | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($runningContainers.Count -gt 0) {
        throw "Compose project '$ProjectName' still has running containers: $($runningContainers -join ', '). Stop the stack before the restore touches the bootstrap workspace or the target database."
    }
}

function Test-RestoreWorkspaceTreeEqual {
    <#
    .SYNOPSIS
    Recursive byte-identity comparison of two directory trees: the sorted relative path sets
    must match exactly (ordinal, so a casing difference reads as different - the fail-closed
    direction, since "different" leads to replacement) and every file pair must carry the same
    SHA-256. Enumeration uses -Force so dotfiles - hidden by default on Linux - are compared
    too; Get-BootstrapWorkspaceFingerprint is insufficient here because it covers only the
    ApiSchema workspace and normalizes text content.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$ReferenceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DifferenceDirectory
    )

    $referenceFiles = @(Get-ChildItem -LiteralPath $ReferenceDirectory -Recurse -File -Force |
            Sort-Object -Property FullName)
    $differenceFiles = @(Get-ChildItem -LiteralPath $DifferenceDirectory -Recurse -File -Force |
            Sort-Object -Property FullName)

    $referencePaths = @($referenceFiles | ForEach-Object {
            [System.IO.Path]::GetRelativePath($ReferenceDirectory, $_.FullName).Replace("\", "/") } | Sort-Object)
    $differencePaths = @($differenceFiles | ForEach-Object {
            [System.IO.Path]::GetRelativePath($DifferenceDirectory, $_.FullName).Replace("\", "/") } | Sort-Object)

    if ($referencePaths.Count -ne $differencePaths.Count) {
        return $false
    }
    for ($index = 0; $index -lt $referencePaths.Count; $index++) {
        if ($referencePaths[$index] -cne $differencePaths[$index]) {
            return $false
        }
        $referenceHash = Get-FileSha256Hex -Path (Join-Path $ReferenceDirectory $referencePaths[$index])
        $differenceHash = Get-FileSha256Hex -Path (Join-Path $DifferenceDirectory $differencePaths[$index])
        if ($referenceHash -cne $differenceHash) {
            return $false
        }
    }
    return $true
}

function Publish-RestoreCandidateWorkspace {
    <#
    .SYNOPSIS
    Commits a validated restore candidate as the active bootstrap workspace, whole-tree only.

    .DESCRIPTION
    Re-proves the stop precondition internally (both known compose projects, fail-closed on an
    indeterminate docker answer) before reading or writing anything, because a running service
    holds the active workspace bind-mounted. Then: a candidate byte-identical to the active tree
    is discarded and the active tree reused as-is; anything else replaces the ENTIRE tree - never
    a subtree, so no partial/mixed workspace is ever published. The active tree is renamed aside
    into the restore workspace (a sibling, so on the same volume), the candidate is renamed into
    place, and the previous tree is removed only after that succeeds. If publishing the
    candidate fails, the previous tree is renamed back and the failure propagates; the candidate
    is left for the caller's cleanup. This is recovery from a failed publication, not crash
    atomicity: a process killed between the renames leaves the previous tree under
    .bootstrap-restore/previous-*.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Restore-internal commit step; the restore flow does not expose -WhatIf end to end, and a silent no-op would leave the active workspace inconsistent with the validated candidate.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$CandidateDirectory,

        # Test seam; production default is the module-adjacent active workspace.
        [string]$ActiveBootstrapRoot = ""
    )

    if ([string]::IsNullOrWhiteSpace($ActiveBootstrapRoot)) {
        $ActiveBootstrapRoot = Join-Path $PSScriptRoot ".bootstrap"
    }

    if (-not (Test-Path -LiteralPath (Join-Path $CandidateDirectory "bootstrap-manifest.json") -PathType Leaf)) {
        throw "Refusing to commit candidate '$CandidateDirectory': it has no bootstrap-manifest.json, so it is not a complete prepare-phase product."
    }

    # Internal stop-proof re-assert (D10 precondition): the wrapper orders this call after its
    # own stop proof, but the commit itself must never run against a live stack.
    Assert-DmsComposeProjectStopped -ProjectName "dms-local"
    Assert-DmsComposeProjectStopped -ProjectName "dms-published"

    if (Test-Path -LiteralPath $ActiveBootstrapRoot) {
        if (Test-RestoreWorkspaceTreeEqual -ReferenceDirectory $ActiveBootstrapRoot -DifferenceDirectory $CandidateDirectory) {
            Remove-Item -LiteralPath $CandidateDirectory -Recurse -Force
            return [pscustomobject]@{
                Replaced            = $false
                ActiveBootstrapRoot = $ActiveBootstrapRoot
            }
        }

        # Retained CDC configuration and nested source state of EITHER compose project outlive a
        # full stop, so the stop proofs above cannot protect them; the shared CDC guard decides.
        Import-Module (Join-Path $PSScriptRoot "cdc-lifecycle.psm1")
        if (Test-CdcBootstrapWorkspaceProtected -BootstrapRoot $ActiveBootstrapRoot) {
            throw "Refusing to replace the active bootstrap workspace: it holds retained CDC configuration or state for dms-local or dms-published. Nothing was replaced and the restore target was not touched. Reconcile or retire that CDC deployment through its own lifecycle, then rerun the restore."
        }

    }

    $activeParentDirectory = Split-Path -Parent $ActiveBootstrapRoot
    if (-not [string]::IsNullOrWhiteSpace($activeParentDirectory)) {
        New-Item -ItemType Directory -Path $activeParentDirectory -Force -ErrorAction Stop | Out-Null
    }

    # Every filesystem step below is terminating (-ErrorAction Stop): this module does not inherit
    # the caller's $ErrorActionPreference, and a non-terminating provider error would otherwise slip
    # past the rollback (nesting the candidate inside the active tree, or deleting the backup).
    # The previous tree is set aside, never deleted, until the candidate is in place.
    $previousBootstrapRoot = $null
    if (Test-Path -LiteralPath $ActiveBootstrapRoot) {
        $previousParentDirectory = Join-Path $activeParentDirectory ".bootstrap-restore"
        New-Item -ItemType Directory -Path $previousParentDirectory -Force -ErrorAction Stop | Out-Null
        $previousBootstrapRoot = Join-Path $previousParentDirectory "previous-$([Guid]::NewGuid().ToString('N'))"
        Move-Item -LiteralPath $ActiveBootstrapRoot -Destination $previousBootstrapRoot -ErrorAction Stop
    }

    try {
        Move-Item -LiteralPath $CandidateDirectory -Destination $ActiveBootstrapRoot -ErrorAction Stop
    }
    catch {
        $publishFailure = ($_.Exception.Message | Out-String).Trim()
        if ($null -ne $previousBootstrapRoot) {
            try {
                # A failed cross-volume move can leave a partial copy; it is never the workspace.
                if (Test-Path -LiteralPath $ActiveBootstrapRoot) {
                    Remove-Item -LiteralPath $ActiveBootstrapRoot -Recurse -Force -ErrorAction Stop
                }
                Move-Item -LiteralPath $previousBootstrapRoot -Destination $ActiveBootstrapRoot -ErrorAction Stop
            }
            catch {
                throw "Publishing the restore candidate failed ($publishFailure), and the previous bootstrap workspace could not be restored: $(($_.Exception.Message | Out-String).Trim()) It is kept intact at '$previousBootstrapRoot'; move it back to '$ActiveBootstrapRoot' before rerunning. The restore target was not touched."
            }
            throw "Publishing the restore candidate failed: $publishFailure The previous bootstrap workspace was restored and the restore target was not touched."
        }
        throw "Publishing the restore candidate failed: $publishFailure No bootstrap workspace existed before, none was published, and the restore target was not touched."
    }

    if ($null -ne $previousBootstrapRoot) {
        try {
            Remove-Item -LiteralPath $previousBootstrapRoot -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "The candidate was published, but the previous bootstrap workspace at '$previousBootstrapRoot' could not be removed: $(($_.Exception.Message | Out-String).Trim()) Remove it manually."
        }
    }

    return [pscustomobject]@{
        Replaced            = $true
        ActiveBootstrapRoot = $ActiveBootstrapRoot
    }
}


function Get-RestoreDatabaseContainerName {
    <#
    .SYNOPSIS
    The compose container name of the selected engine's database service (postgresql.yml /
    mssql.yml both name the service 'db' with a fixed container_name).
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    if ($DatabaseEngine -eq "mssql") {
        return "dms-mssql"
    }
    return "dms-postgresql"
}

function New-RestorePreflightEnvironment {
    <#
    .SYNOPSIS
    Materializes the database-only preflight environment without target exposure.

    .DESCRIPTION
    PostgreSQL: fresh-volume initialization (postgresql-init.sh createdb) creates whatever
    POSTGRES_DB_NAME names, so the preflight -DbOnly run gets a DERIVED env whose
    POSTGRES_DB_NAME is a generated non-target preflight database name - the selected target can
    never be created by initialization, only by the guarded restore itself. Everything else in
    the effective env is carried over verbatim. SQL Server creates no database at startup, so
    the effective env passes through unchanged and no preflight database exists to clean.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Restore-internal derived-env materialization into the private restore workspace; the restore flow does not expose -WhatIf end to end.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$EnvironmentFile,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$TargetDatabaseName,

        # The PostgreSQL administrator the container is initialized with (POSTGRES_USER).
        [string]$PostgresUser = "postgres",

        # Test seam; production default is a derived-file area inside the restore workspace.
        [string]$DerivedDirectory = ""
    )

    if ($DatabaseEngine -eq "mssql") {
        return [pscustomobject]@{
            EnvironmentFile        = $EnvironmentFile
            PreflightDatabaseName  = ""
            IsDerived              = $false
        }
    }

    # The image's own initialization also creates POSTGRES_DB, which postgresql.yml does not set,
    # so it defaults to the POSTGRES_USER name; a target spelled like the administrator would be
    # created by fresh-volume initialization no matter what POSTGRES_DB_NAME says. PostgreSQL
    # truncates names past 63 bytes, so a longer administrator name could still create the target.
    if ([System.Text.Encoding]::UTF8.GetByteCount($PostgresUser) -gt 63) {
        throw "The PostgreSQL administrator name (POSTGRES_USER) is longer than 63 bytes, and PostgreSQL truncates the database the image creates with that name, so preflight could create the restore target. Nothing was restored. Use a POSTGRES_USER of at most 63 bytes. The value is withheld."
    }
    if ($PostgresUser.Trim().Equals($TargetDatabaseName.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The restore target database has the same name as the PostgreSQL administrator (POSTGRES_USER), and the PostgreSQL image creates a database with that name when it initializes a fresh volume, so preflight could create the target. Nothing was restored. Use a different POSTGRES_USER or restore target. Both values are withheld."
    }

    if ([string]::IsNullOrWhiteSpace($DerivedDirectory)) {
        $DerivedDirectory = Join-Path $script:RestoreWorkspaceRoot "derived"
    }

    $preflightDatabaseName = New-RestorePreflightDatabaseName
    # The generator's prefix+crypto-suffix construction makes these unreachable, but the
    # preflight name standing in for the target would defeat the whole non-exposure design, so
    # both properties are asserted rather than assumed.
    Assert-SafeRestoreDatabaseName -DatabaseEngine $DatabaseEngine -DatabaseName $preflightDatabaseName -Purpose "restore preflight database"
    if ($preflightDatabaseName.Equals($TargetDatabaseName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The generated preflight database name '$preflightDatabaseName' collides with the restore target; refusing to run preflight against the target."
    }

    $derivedEnvironmentPath = Join-Path $DerivedDirectory ".env.restore-preflight"
    Write-DerivedEnvFile `
        -BaseEnvironmentFile $EnvironmentFile `
        -TargetPath $derivedEnvironmentPath `
        -KeyOverrides @{ POSTGRES_DB_NAME = $preflightDatabaseName }

    return [pscustomobject]@{
        EnvironmentFile        = $derivedEnvironmentPath
        PreflightDatabaseName  = $preflightDatabaseName
        IsDerived              = $true
    }
}

function Wait-RestorePostgresqlTcpReady {
    <#
    .SYNOPSIS
    Waits, within a bound, until the restore preflight PostgreSQL server accepts TCP connections.

    .DESCRIPTION
    On a fresh volume the official image runs initialization against a temporary server that
    listens only on the Unix socket, stops it, and then starts the permanent server. The start
    scripts' socket-default pg_isready can succeed against that temporary server, so restore
    safety and scratch work could reach it while it shuts down. Only the permanent server
    listens on TCP, so pg_isready -h 127.0.0.1 succeeding inside the container means
    initialization has finished. Each probe is bounded by Test-NativeCommandWithTimeout and the
    number of probes is bounded, so a server that never listens on TCP fails the run. The SQL
    transports stay on the socket; only this gate uses TCP.
    #>
    param (
        # The PostgreSQL administrator the container was initialized with (POSTGRES_USER).
        [string]$PostgresUser = "postgres",

        [string]$ContainerName = (Get-RestoreDatabaseContainerName -DatabaseEngine "postgresql"),

        [ValidateRange(1, 1000)]
        [int]$MaxAttempts = 40,

        [ValidateRange(1, 60)]
        [int]$RetryIntervalSeconds = 3
    )

    # postgresql.yml publishes the container's fixed port 5432; inside the container the server
    # always listens on 5432, whatever POSTGRES_PORT maps on the host.
    $probeArguments = @("exec", $ContainerName, "pg_isready", "-h", "127.0.0.1", "-p", "5432", "-U", $PostgresUser)
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if (Test-NativeCommandWithTimeout -FilePath "docker" -ArgumentList $probeArguments -TimeoutSeconds 10) {
            return
        }
        if ($attempt -lt $MaxAttempts) {
            Start-Sleep -Seconds $RetryIntervalSeconds
        }
    }

    throw "PostgreSQL ($ContainerName) did not accept TCP connections on 127.0.0.1:5432 after $MaxAttempts readiness probes, so fresh-volume initialization may not have finished. Nothing was restored."
}

function Invoke-RestoreCatalogQuery {
    <#
    .SYNOPSIS
    Runs one read-only catalog query against a database inside the running engine container,
    returning its standard-output rows only. Same psql/sqlcmd transports as the template
    producer's catalog reader, hardened fail-closed: an unresolvable docker, an invocation
    failure, or a nonzero exit each throw - a silent empty answer must never read as "no rows".
    Standard error is kept apart from the rows (a diagnostic on a successful query is not
    data) and is reported with the failure message when the query fails.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); the MSSQL account is always "sa", and -PostgresUser names a PostgreSQL account that has no password here (psql authenticates over the container socket), so a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$DatabaseName,

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!",

        # The PostgreSQL administrator the container was initialized with (POSTGRES_USER,
        # default postgres). The restore wrapper resolves it as the start scripts do.
        [string]$PostgresUser = "postgres",

        [Parameter(Mandatory = $true)]
        [string]$Query,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    try {
        $null = Get-Command docker -ErrorAction Stop
    }
    catch {
        throw "$FailureMessage The 'docker' command is not available in this session."
    }

    $global:LASTEXITCODE = 0
    try {
        if ($DatabaseEngine -eq "mssql") {
            $output = docker exec -e "SQLCMDPASSWORD=$MssqlPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d $DatabaseName -C -b -h -1 -W -Q $Query 2>&1
        }
        else {
            $output = docker exec $ContainerName psql -U $PostgresUser -d $DatabaseName -tA -c $Query 2>&1
        }
    }
    catch {
        throw "$FailureMessage Invoking docker exec failed: $(($_.Exception.Message | Out-String).Trim())"
    }
    # 2>&1 delivers each standard-error line as an ErrorRecord and each standard-output line as
    # a string, so the two streams separate by type; each keeps its own line order.
    $rows = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
    if ($LASTEXITCODE -ne 0) {
        $diagnostics = (@($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
        throw "$FailureMessage ($diagnostics)"
    }

    return $rows
}

function Get-RestorePostgresqlPlatformQuerySql {
    <#
    .SYNOPSIS
    One-row PostgreSQL platform fact for the restore gate: '<server_version_num>|<template1
    encoding>' (the encoding is empty when template1 cannot be read, which fails closed).
    #>
    return "SELECT current_setting('server_version_num') || '|' || COALESCE((SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname = 'template1'), '');"
}

function Get-RestoreEngineMajorVersion {
    <#
    .SYNOPSIS
    Leading-integer major version of an engine version string ('16.8' -> 16,
    '17.0.900.7' -> 17). An unparsable value fails closed - version comparisons must never
    silently pass on garbage.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$EngineVersion,

        [Parameter(Mandatory = $true)]
        [string]$SourceDescription
    )

    $match = [System.Text.RegularExpressions.Regex]::Match($EngineVersion.Trim(), '^(?<major>\d+)')
    if (-not $match.Success) {
        throw "Cannot parse a major version from $SourceDescription engine version '$EngineVersion'."
    }
    return [int]$match.Groups["major"].Value
}

function Assert-RestoreTargetSafety {
    <#
    .SYNOPSIS
    The non-destructive live target-safety gate, re-run immediately before any destructive
    restore step, in the design's order: reserved-name normalization + denylist and the
    separate-CMS exclusion (parameter-time), the live-catalog reserved check (a system/template
    database can hide behind a non-reserved NAME), the running-service check (only the db
    container of either compose project may be up), the live-session check (no client may be
    connected to the target, including idle clients outside these projects), the live-server
    engine major check (the live major must be >= the manifest's - a newer artifact never replays onto an older
    server), the PostgreSQL platform floor (PostgreSQL 18+ with a UTF-8 template1, the
    contract SchemaTools provisioning enforces), and the CDC-binding refusal (a CDC-bound
    target needs the governed new-generation recovery workflow, not an in-place replace).
    Every indeterminate answer fails closed.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'Forwarded to Invoke-RestoreCatalogQuery, whose sqlcmd transport carries the password via SQLCMDPASSWORD on docker exec; the MSSQL account is always "sa", and -PostgresUser names a PostgreSQL account that has no password here (psql authenticates over the container socket), so a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Forwarded to Invoke-RestoreCatalogQuery; SecureString adds no protection across the docker exec boundary.')]
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$TargetDatabaseName,

        [Parameter(Mandatory = $true)]
        $Manifest,

        [string]$ContainerName = "",

        [switch]$SeparateConfigDatabase,

        [string]$EffectiveConfigDatabaseName = "",

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!",

        [string]$PostgresUser = "postgres"
    )

    if ([string]::IsNullOrWhiteSpace($ContainerName)) {
        $ContainerName = Get-RestoreDatabaseContainerName -DatabaseEngine $DatabaseEngine
    }
    $adminDatabaseName = if ($DatabaseEngine -eq "mssql") { "master" } else { "postgres" }

    # 1) Parameter-time checks: reserved-name normalization + denylist + separate-CMS exclusion.
    #    These run before any docker activity, so a denylisted target never reaches a query.
    Assert-RestoreTargetDatabaseNameSafe `
        -DatabaseEngine $DatabaseEngine `
        -TargetDatabaseName $TargetDatabaseName `
        -SeparateConfigDatabase:$SeparateConfigDatabase `
        -EffectiveConfigDatabaseName $EffectiveConfigDatabaseName

    $catalogQueryArguments = @{
        DatabaseEngine = $DatabaseEngine
        ContainerName  = $ContainerName
        DatabaseName   = $adminDatabaseName
        MssqlPassword  = $MssqlPassword
        PostgresUser   = $PostgresUser
    }

    # 2) Live-catalog reserved check: the NAME denylist cannot see a template/system database
    #    hiding behind a custom name (PG datistemplate/datallowconn, MSSQL database_id <= 4).
    #    TargetDatabaseName is charset-validated above, so embedding it in SQL is safe.
    $reservedCatalogQuery = if ($DatabaseEngine -eq "mssql") {
        "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name = N'$TargetDatabaseName' AND database_id <= 4;"
    }
    else {
        "SELECT datname FROM pg_database WHERE datname = '$TargetDatabaseName' AND (datistemplate OR NOT datallowconn);"
    }
    $reservedRows = @(Invoke-RestoreCatalogQuery @catalogQueryArguments `
            -Query $reservedCatalogQuery `
            -FailureMessage "Live-catalog reserved check failed for restore target '$TargetDatabaseName'.")
    if (@($reservedRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
        throw "The live catalog marks restore target '$TargetDatabaseName' as a reserved/system database (template, connection-disabled, or system id). Restore never replaces it."
    }

    # 3) Running-service check: with bind-mounts, "is anything pointed at the target" cannot be
    #    answered from configuration - so only the db service of either compose project may be
    #    running. Fail-closed like the stop proof: unresolvable docker or a nonzero exit is
    #    indeterminate, and a container without a readable service label is refused too.
    try {
        $null = Get-Command docker -ErrorAction Stop
    }
    catch {
        throw "Target safety is indeterminate: the 'docker' command is not available in this session, so running services cannot be ruled out."
    }
    foreach ($projectName in @("dms-local", "dms-published")) {
        $global:LASTEXITCODE = 0
        try {
            $containerRows = @(docker ps --filter "label=com.docker.compose.project=$projectName" --format '{{.Names}}|{{.Label "com.docker.compose.service"}}' 2>&1)
        }
        catch {
            throw "Target safety is indeterminate: invoking 'docker ps' failed while checking compose project '$projectName' ($(($_.Exception.Message | Out-String).Trim()))."
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Target safety is indeterminate: 'docker ps' exited with code $LASTEXITCODE while checking compose project '$projectName' ($(($containerRows | Out-String).Trim()))."
        }
        $nonDatabaseContainers = @($containerRows |
                ForEach-Object { [string]$_ } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Where-Object {
                    $rowParts = $_.Split("|", 2)
                    $rowParts.Count -lt 2 -or [string]::IsNullOrWhiteSpace($rowParts[1]) -or $rowParts[1].Trim() -cne "db"
                })
        if ($nonDatabaseContainers.Count -gt 0) {
            throw "Compose project '$projectName' has running non-database containers while the restore is about to touch the target: $($nonDatabaseContainers -join ', '). Only the db service may be up."
        }
    }

    # 3b) Live sessions catch host processes and clients outside the two compose projects.
    #     Query from the admin database and include idle sessions (connection pools still use
    #     the target). The later force-disconnect is only a backstop for a connection racing
    #     this gate, never permission to replace a database with existing clients.
    $sessionQuery = if ($DatabaseEngine -eq "mssql") {
        "SET NOCOUNT ON; SELECT session_id FROM sys.dm_exec_sessions WHERE database_id = DB_ID(N'$TargetDatabaseName') AND session_id <> @@SPID;"
    }
    else {
        "SELECT pid FROM pg_stat_activity WHERE datname = '$TargetDatabaseName' AND backend_type = 'client backend' AND pid <> pg_backend_pid();"
    }
    $sessionRows = @(Invoke-RestoreCatalogQuery @catalogQueryArguments `
            -Query $sessionQuery `
            -FailureMessage "Live-session check failed for restore target '$TargetDatabaseName'.")
    if (@($sessionRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
        throw "Restore target '$TargetDatabaseName' has connected sessions. Stop all clients using this database, including the Configuration Service and DMS, before replacing it."
    }

    # 4) D8a live-server engine major: the live major must be >= the manifest's engine major
    #    (MSSQL cannot restore a newer-version .bak; PostgreSQL replay is held to the same rule
    #    for determinism). Unparsable versions fail closed.
    $liveVersionRows = @(Invoke-RestoreCatalogQuery @catalogQueryArguments `
            -Query (Get-EngineVersionQuerySql -DatabaseEngine $DatabaseEngine) `
            -FailureMessage "Live engine-version check failed for restore target '$TargetDatabaseName'.")
    $liveVersionText = ([string]($liveVersionRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1))
    if ([string]::IsNullOrWhiteSpace($liveVersionText)) {
        throw "Live engine-version check returned no version for restore target '$TargetDatabaseName'; refusing on an indeterminate answer."
    }
    $liveMajor = Get-RestoreEngineMajorVersion -EngineVersion $liveVersionText -SourceDescription "the live server's"
    $manifestMajor = Get-RestoreEngineMajorVersion -EngineVersion ([string]$Manifest.engineVersion) -SourceDescription "the restore manifest's"
    if ($liveMajor -lt $manifestMajor) {
        throw "The live server major version $liveMajor is older than the restore manifest's engine major version $manifestMajor ('$($Manifest.engineVersion)'); a template from a newer engine never replays onto an older server."
    }

    # 4b) PostgreSQL platform floor, the contract SchemaTools provisioning enforces (DMS-1447):
    #     server_version_num >= 180000 and a UTF-8 creation encoding. Restore bypasses that
    #     provisioner and creates the scratch and target databases with a plain CREATE DATABASE,
    #     which inherits template1's encoding, so template1 is what must be UTF-8.
    if ($DatabaseEngine -eq "postgresql") {
        $platformRows = @(Invoke-RestoreCatalogQuery @catalogQueryArguments `
                -Query (Get-RestorePostgresqlPlatformQuerySql) `
                -FailureMessage "PostgreSQL platform check failed for restore target '$TargetDatabaseName'.")
        $platformRow = $platformRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1
        $platformText = if ($null -eq $platformRow) { "" } else { ([string]$platformRow).Trim() }
        $platformMatch = [System.Text.RegularExpressions.Regex]::Match($platformText, '^(?<version>\d+)\|(?<encoding>[A-Za-z0-9_]*)$')
        if (-not $platformMatch.Success) {
            throw "PostgreSQL platform check returned an unreadable answer '$platformText' for restore target '$TargetDatabaseName'; refusing on an indeterminate answer."
        }
        $serverVersionNum = [int]$platformMatch.Groups["version"].Value
        $creationEncoding = $platformMatch.Groups["encoding"].Value
        $requirement = "DMS requires PostgreSQL $($script:RestorePostgresqlMinimumServerVersionNum / 10000) or later with a UTF-8 database encoding; restore creates its scratch and target databases from the server's template1, which must be UTF-8."
        if ($serverVersionNum -lt $script:RestorePostgresqlMinimumServerVersionNum) {
            throw "$requirement Detected: server_version_num $serverVersionNum."
        }
        if ($creationEncoding -cne "UTF8") {
            $detectedEncoding = if ([string]::IsNullOrEmpty($creationEncoding)) { "unavailable" } else { $creationEncoding }
            throw "$requirement Detected: template1 encoding $detectedEncoding."
        }
    }

    # 5) CDC-binding refusal: a CDC-bound target must go through the governed new-generation
    #    recovery workflow, never an in-place replacement.
    if ($DatabaseEngine -eq "mssql") {
        $cdcRows = @(Invoke-RestoreCatalogQuery @catalogQueryArguments `
                -Query "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name = N'$TargetDatabaseName' AND is_cdc_enabled = 1;" `
                -FailureMessage "CDC-binding check failed for restore target '$TargetDatabaseName'.")
        if (@($cdcRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
            throw "Restore target '$TargetDatabaseName' has CDC enabled (sys.databases.is_cdc_enabled = 1). Replacing a CDC-bound database requires the governed new-generation recovery workflow; restore refuses."
        }
    }
    else {
        $slotRows = @(Invoke-RestoreCatalogQuery @catalogQueryArguments `
                -Query "SELECT slot_name FROM pg_replication_slots WHERE database = '$TargetDatabaseName';" `
                -FailureMessage "CDC-binding (replication slot) check failed for restore target '$TargetDatabaseName'.")
        if (@($slotRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
            throw "Restore target '$TargetDatabaseName' is bound by replication slot(s): $(@($slotRows) -join ', '). Replacing a CDC-bound database requires the governed new-generation recovery workflow; restore refuses."
        }

        $targetExistsRows = @(Invoke-RestoreCatalogQuery @catalogQueryArguments `
                -Query "SELECT 1 FROM pg_database WHERE datname = '$TargetDatabaseName';" `
                -FailureMessage "Target-existence check failed for restore target '$TargetDatabaseName'.")
        if (@($targetExistsRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
            # Publications live INSIDE the target database, so this query only runs when the
            # target exists.
            $publicationRows = @(Invoke-RestoreCatalogQuery `
                    -DatabaseEngine $DatabaseEngine `
                    -ContainerName $ContainerName `
                    -DatabaseName $TargetDatabaseName `
                    -MssqlPassword $MssqlPassword `
                    -PostgresUser $PostgresUser `
                    -Query "SELECT pubname FROM pg_publication;" `
                    -FailureMessage "CDC-binding (publication) check failed for restore target '$TargetDatabaseName'.")
            if (@($publicationRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
                throw "Restore target '$TargetDatabaseName' contains publication(s): $(@($publicationRows) -join ', '). Replacing a CDC-bound database requires the governed new-generation recovery workflow; restore refuses."
            }
        }
    }
}


function Invoke-RestoreDockerCommand {
    <#
    .SYNOPSIS
    Runs one docker command with the restore flow's fail-closed contract: unresolvable docker,
    invocation failure, and a nonzero exit each throw the caller's failure message - a silent
    empty answer never reads as success. Returns the raw output rows.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    try {
        $null = Get-Command docker -ErrorAction Stop
    }
    catch {
        throw "$FailureMessage The 'docker' command is not available in this session."
    }

    $global:LASTEXITCODE = 0
    try {
        $output = docker @ArgumentList 2>&1
    }
    catch {
        throw "$FailureMessage Invoking docker failed: $(($_.Exception.Message | Out-String).Trim())"
    }
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage ($(($output | Out-String).Trim()))"
    }

    return @($output)
}

function Assert-RestoreContainerArtifactSha256 {
    <#
    .SYNOPSIS
    The transfer-integrity check at the point of consumption: hashes the artifact copy INSIDE
    the database container, after the copy and before psql or RESTORE reads that same path, and
    fails unless it equals the authenticated manifest's artifactSha256.

    .DESCRIPTION
    The copy is streamed from the stage's retained authenticated bytes
    (Copy-RestoreStagedArtifactToContainer), so no host file can change what is sent; this
    check additionally proves that what LANDED in the container is what was sent (a truncated
    or corrupted transfer is refused before any consumer runs). The in-container copy is
    written by this run into the container's own filesystem, which only a Docker API client or
    a process inside the container can change - and either can already run SQL against the
    server directly, so the restore does not defend against them.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$ContainerArtifactPath,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedSha256,

        [Parameter(Mandatory = $true)]
        [string]$ConsumerDescription
    )

    $hashRows = Invoke-RestoreDockerCommand `
        -ArgumentList @("exec", $ContainerName, "sha256sum", $ContainerArtifactPath) `
        -FailureMessage "Failed to hash the artifact copied into container '$ContainerName'; refusing $ConsumerDescription."
    $hashLine = [string]@($hashRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1)
    $actualSha256 = ($hashLine.Trim() -split '\s+')[0].ToLowerInvariant()
    if ($actualSha256 -cnotmatch '^[0-9a-f]{64}\z') {
        throw "sha256sum in container '$ContainerName' returned no SHA-256 for the copied artifact; refusing $ConsumerDescription."
    }
    if ($actualSha256 -cne $ExpectedSha256) {
        throw "The artifact copied into container '$ContainerName' has SHA-256 '$actualSha256', not the authenticated manifest's artifactSha256 '$ExpectedSha256'; the transfer into the container did not deliver the authenticated bytes. Refusing $ConsumerDescription."
    }
}

function Assert-RestoreRetainedPackage {
    <#
    .SYNOPSIS
    Precondition of every transfer: the stage carries its retained package buffer, those bytes
    are still the authenticated package (PackageSha256), and they hold exactly one entry named
    ArtifactEntryName. Reads only memory; no host path is opened.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Stage,

        [Parameter(Mandatory = $true)]
        [string]$AbortDescription
    )

    # PSObject lookups: under strict mode a missing property would throw an unrelated message.
    $packageBytesProperty = $Stage.PSObject.Properties["PackageBytes"]
    $entryNameProperty = $Stage.PSObject.Properties["ArtifactEntryName"]
    if ($null -eq $packageBytesProperty -or $packageBytesProperty.Value -isnot [byte[]] -or
        $null -eq $entryNameProperty -or [string]::IsNullOrWhiteSpace([string]$entryNameProperty.Value)) {
        throw "The restore stage does not carry its retained authenticated package bytes; $AbortDescription"
    }
    $retainedSha256 = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([byte[]]$Stage.PackageBytes)).ToLowerInvariant()
    if ($retainedSha256 -cne [string]$Stage.PackageSha256) {
        throw "The retained package bytes hash to '$retainedSha256', not the authenticated '$($Stage.PackageSha256)'; $AbortDescription"
    }
}

function Write-RestoreSingleFileTar {
    <#
    .SYNOPSIS
    Writes a one-file POSIX ustar archive (mode 0644, uid/gid 0) whose data is read from
    -Source, and returns the SHA-256 of exactly the data bytes written.

    .DESCRIPTION
    docker cp reads a tar archive from standard input. System.Formats.Tar cannot stream an
    unseekable source (a compressed zip entry) into an unseekable destination (a process
    pipe), so the 512-byte header is written here; the size comes from the caller, and a
    source that yields a different number of bytes is refused.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$FileName,

        [Parameter(Mandatory = $true)]
        [long]$Length,

        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Source
    )

    $nameBytes = [System.Text.Encoding]::ASCII.GetBytes($FileName)
    if ($FileName -cnotmatch '^[A-Za-z0-9._-]{1,100}\z') {
        throw "Tar entry name '$FileName' must be 1-100 characters of [A-Za-z0-9._-]."
    }
    # 11 octal digits is the ustar size field's limit.
    if ($Length -lt 0 -or $Length -ge 8589934592) {
        throw "Tar entry '$FileName' length $Length is outside the ustar size range."
    }

    $header = [byte[]]::new(512)
    $writeField = {
        param([int]$Offset, [string]$Text)
        $fieldBytes = [System.Text.Encoding]::ASCII.GetBytes($Text)
        [System.Array]::Copy($fieldBytes, 0, $header, $Offset, $fieldBytes.Length)
    }
    [System.Array]::Copy($nameBytes, 0, $header, 0, $nameBytes.Length)
    & $writeField 100 "0000644"
    & $writeField 108 "0000000"
    & $writeField 116 "0000000"
    & $writeField 124 ([System.Convert]::ToString($Length, 8).PadLeft(11, '0'))
    & $writeField 136 ([System.Convert]::ToString([DateTimeOffset]::UtcNow.ToUnixTimeSeconds(), 8).PadLeft(11, '0'))
    & $writeField 148 "        "
    & $writeField 156 "0"
    & $writeField 257 "ustar"
    & $writeField 263 "00"
    & $writeField 265 "root"
    & $writeField 297 "root"
    $checksum = 0
    foreach ($headerByte in $header) { $checksum += $headerByte }
    & $writeField 148 ([System.Convert]::ToString($checksum, 8).PadLeft(6, '0'))
    $header[154] = 0
    $header[155] = 32
    $Destination.Write($header, 0, $header.Length)

    $hasher = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $buffer = [byte[]]::new(1048576)
        [long]$written = 0
        while (($read = $Source.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $hasher.AppendData($buffer, 0, $read)
            $Destination.Write($buffer, 0, $read)
            $written += $read
        }
        if ($written -ne $Length) {
            throw "Tar entry '$FileName' declared $Length bytes but its source yielded $written."
        }
        $padding = (512 - ($Length % 512)) % 512
        # Data padding plus the two zero blocks that end the archive.
        $trailer = [byte[]]::new($padding + 1024)
        $Destination.Write($trailer, 0, $trailer.Length)
        $Destination.Flush()

        return [System.Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $hasher.Dispose()
    }
}

function Invoke-RestoreProcessWithInput {
    <#
    .SYNOPSIS
    Runs one process with the restore flow's fail-closed contract, feeding its standard input
    from -InputWriter (a scriptblock that receives the raw input stream) and returning what the
    writer returned. A writer failure kills the process before its input is completed; a
    start failure, writer failure, or nonzero exit throws the caller's failure message. The
    process and its streams are disposed on every path.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage,

        [Parameter(Mandatory = $true)]
        [scriptblock]$InputWriter
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = $null
    try {
        try {
            $process = [System.Diagnostics.Process]::Start($startInfo)
        }
        catch {
            throw "$FailureMessage Starting '$FilePath' failed: $(($_.Exception.Message | Out-String).Trim())"
        }
        # Drained concurrently so a chatty process can never block on a full output pipe while
        # this side is still writing its input.
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()

        $writerResult = $null
        $writerError = $null
        try {
            $writerResult = & $InputWriter $process.StandardInput.BaseStream
        }
        catch {
            $writerError = $_
            # Never let a partial input complete: the process dies before it sees end-of-input.
            if (-not $process.HasExited) {
                $process.Kill($true)
            }
        }
        finally {
            try {
                $process.StandardInput.Dispose()
            }
            catch {
                # A process that already exited closed the pipe; its exit code reports why.
                $null = $_
            }
        }

        $process.WaitForExit()
        $processOutput = (@($standardOutputTask.GetAwaiter().GetResult(), $standardErrorTask.GetAwaiter().GetResult()) -join "`n").Trim()
        if ($null -ne $writerError) {
            throw "$FailureMessage Streaming its input failed: $(($writerError.Exception.Message | Out-String).Trim()) ($processOutput)"
        }
        if ($process.ExitCode -ne 0) {
            throw "$FailureMessage ($processOutput)"
        }

        return $writerResult
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }
}

function Invoke-RestoreDockerInputCommand {
    <#
    .SYNOPSIS
    Invoke-RestoreProcessWithInput for the docker executable (resolved as an application, not
    a function or alias), with Invoke-RestoreDockerCommand's unavailable-docker message.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage,

        [Parameter(Mandatory = $true)]
        [scriptblock]$InputWriter
    )

    $dockerApplication = Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $dockerApplication) {
        throw "$FailureMessage The 'docker' command is not available in this session."
    }

    return Invoke-RestoreProcessWithInput `
        -FilePath $dockerApplication.Source `
        -ArgumentList $ArgumentList `
        -FailureMessage $FailureMessage `
        -InputWriter $InputWriter
}

function Get-PostgresqlArtifactReplayArgumentList {
    <#
    .SYNOPSIS
        Builds the docker exec arguments for replaying the staged SQL artifact into a database.
    .DESCRIPTION
        Shared by the scratch and target replays so their replay options cannot drift: both stop
        on the first error and run as one transaction, so the scratch validation exercises the
        same replay semantics that later replace the target.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$PostgresUser,

        [Parameter(Mandatory = $true)]
        [string]$DatabaseName,

        [Parameter(Mandatory = $true)]
        [string]$ContainerArtifactPath
    )

    return @("exec", $ContainerName, "psql", "-U", $PostgresUser, "-d", $DatabaseName, "-v", "ON_ERROR_STOP=1", "--single-transaction", "-f", $ContainerArtifactPath)
}

function Copy-RestoreStagedArtifactToContainer {
    <#
    .SYNOPSIS
    Copies the staged database artifact to -ContainerArtifactPath inside the container FROM THE
    STAGE'S RETAINED AUTHENTICATED BYTES: the artifact entry is read from a read-only view of
    Stage.PackageBytes and piped as a one-file tar to `docker cp - <container>:<directory>`.

    .DESCRIPTION
    No host file is opened, so replacing the staged .nupkg or manifest record, or the source
    package on disk, cannot change the bytes either transfer sends. The SHA-256 of exactly the
    bytes streamed is compared with the manifest's artifactSha256 before returning; the caller
    then hashes the in-container copy (Assert-RestoreContainerArtifactSha256) as the
    transfer-integrity check. Every archive, stream, and process is disposed on all paths.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Stage,

        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$ContainerArtifactPath,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    $separatorIndex = $ContainerArtifactPath.LastIndexOf('/')
    if ($separatorIndex -lt 1) {
        throw "$FailureMessage Container path '$ContainerArtifactPath' must be an absolute file path below a directory."
    }
    $containerDirectory = $ContainerArtifactPath.Substring(0, $separatorIndex)
    $containerFileName = $ContainerArtifactPath.Substring($separatorIndex + 1)
    $expectedSha256 = [string]$Stage.Manifest.artifactSha256

    $packageArchive = [System.IO.Compression.ZipArchive]::new(
        [System.IO.MemoryStream]::new([byte[]]$Stage.PackageBytes, $false),
        [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        $artifactEntries = @($packageArchive.Entries | Where-Object { $_.FullName -ceq [string]$Stage.ArtifactEntryName })
        if ($artifactEntries.Count -ne 1) {
            throw "$FailureMessage The retained package holds $($artifactEntries.Count) entries named '$($Stage.ArtifactEntryName)'; expected exactly one."
        }
        $artifactEntry = $artifactEntries[0]
        # The closure below runs in its own dynamic module, which cannot see this module's
        # private functions; it captures the tar writer itself.
        $writeTar = ${function:Write-RestoreSingleFileTar}

        $sentSha256 = Invoke-RestoreDockerInputCommand `
            -ArgumentList @("cp", "-", "$($ContainerName):$containerDirectory") `
            -FailureMessage $FailureMessage `
            -InputWriter {
                param([System.IO.Stream]$InputStream)
                $entryStream = $artifactEntry.Open()
                try {
                    & $writeTar -Destination $InputStream -FileName $containerFileName -Length $artifactEntry.Length -Source $entryStream
                }
                finally {
                    $entryStream.Dispose()
                }
            }.GetNewClosure()
    }
    finally {
        $packageArchive.Dispose()
    }

    if ([string]$sentSha256 -cne $expectedSha256) {
        throw "$FailureMessage The bytes streamed from the retained package hash to '$sentSha256', not the manifest's artifactSha256 '$expectedSha256'."
    }
}

function Get-RestoreDatabaseCatalogFact {
    <#
    .SYNOPSIS
    Reads the restore-manifest facts from a database through the consumer's fail-closed
    transport: the dms.EffectiveSchema singleton, engine version, SQL Server compatibility
    level, physical DocumentJson storage type, and the canonical inventory. Mirrors the
    producer's Get-TemplateSourceCatalogFacts field-for-field (both build on the same
    Template-RestoreCore queries and parsers) so Assert-RestoreManifestMatchesDatabase consumes
    either side's record. For PostgreSQL the artifact scope is derived from the database's own
    enumerated schemas minus the always-present 'public' - a replayed scratch database contains
    exactly the artifact's content, so the same Select-InventorySchemaScope transformation the
    producer applied reproduces the manifest's inventory shape.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'Forwarded to Invoke-RestoreCatalogQuery, whose sqlcmd transport carries the password via SQLCMDPASSWORD on docker exec; the MSSQL account is always "sa", and -PostgresUser names a PostgreSQL account that has no password here (psql authenticates over the container socket), so a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Forwarded to Invoke-RestoreCatalogQuery; SecureString adds no protection across the docker exec boundary.')]
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$DatabaseName,

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!",

        [string]$PostgresUser = "postgres"
    )

    $queryParameters = @{
        DatabaseEngine = $DatabaseEngine
        ContainerName  = $ContainerName
        DatabaseName   = $DatabaseName
        MssqlPassword  = $MssqlPassword
        PostgresUser   = $PostgresUser
    }

    $effectiveSchemaRow = Invoke-RestoreCatalogQuery @queryParameters `
        -Query (Get-EffectiveSchemaRowQuerySql -DatabaseEngine $DatabaseEngine) `
        -FailureMessage "Failed to read dms.EffectiveSchema from '$DatabaseName'; the restored database is not a provisioned DMS datastore."
    $effectiveSchema = ConvertFrom-EffectiveSchemaRow -Row $effectiveSchemaRow

    $engineVersionRows = Invoke-RestoreCatalogQuery @queryParameters `
        -Query (Get-EngineVersionQuerySql -DatabaseEngine $DatabaseEngine) `
        -FailureMessage "Failed to read the engine version from '$ContainerName'."
    $engineVersion = ([string]@($engineVersionRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)).Trim()
    if ([string]::IsNullOrWhiteSpace($engineVersion)) {
        throw "The engine version query returned no value from '$ContainerName'."
    }

    $databaseCompatibilityLevel = $null
    if ($DatabaseEngine -eq "mssql") {
        $compatibilityRows = Invoke-RestoreCatalogQuery @queryParameters `
            -Query (Get-DatabaseCompatibilityLevelQuerySql -DatabaseName $DatabaseName) `
            -FailureMessage "Failed to read the compatibility level of '$DatabaseName'."
        $compatibilityText = ([string]@($compatibilityRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)).Trim()
        $parsedCompatibilityLevel = 0
        if (-not [int]::TryParse($compatibilityText, [ref]$parsedCompatibilityLevel)) {
            throw "The compatibility level of '$DatabaseName' is not an integer: '$compatibilityText'."
        }
        $databaseCompatibilityLevel = $parsedCompatibilityLevel
    }

    $documentJsonRows = Invoke-RestoreCatalogQuery @queryParameters `
        -Query (Get-DocumentJsonColumnTypeQuerySql -DatabaseEngine $DatabaseEngine) `
        -FailureMessage "Failed to read the DocumentJson column type from '$DatabaseName'."
    $documentJsonColumnType = ConvertFrom-DocumentJsonColumnTypeRow -Row $documentJsonRows

    $schemaRows = Invoke-RestoreCatalogQuery @queryParameters `
        -Query (Get-InventorySchemaQuerySql -DatabaseEngine $DatabaseEngine -Purpose InventoryEnumeration) `
        -FailureMessage "Failed to enumerate the schemas of '$DatabaseName'."
    $objectRows = Invoke-RestoreCatalogQuery @queryParameters `
        -Query (Get-InventoryObjectQuerySql -DatabaseEngine $DatabaseEngine) `
        -FailureMessage "Failed to enumerate the objects of '$DatabaseName'."
    $principalRows = @()
    if ($DatabaseEngine -eq "mssql") {
        $principalRows = Invoke-RestoreCatalogQuery @queryParameters `
            -Query (Get-InventoryPrincipalQuerySql -DatabaseEngine $DatabaseEngine) `
            -FailureMessage "Failed to enumerate the database principals of '$DatabaseName'."
    }
    $databaseLevelObjects = @(Invoke-RestoreCatalogQuery @queryParameters `
            -Query (Get-InventoryDatabaseObjectQuerySql -DatabaseEngine $DatabaseEngine) `
            -FailureMessage "Failed to enumerate the database-level objects of '$DatabaseName'." |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { ([string]$_).Trim() })

    $fullInventory = ConvertFrom-InventoryQueryRow -SchemaRow $schemaRows -ObjectRow $objectRows -PrincipalRow $principalRows

    $artifactInventory = $fullInventory
    if ($DatabaseEngine -eq "postgresql") {
        $artifactSchemaNames = @($fullInventory.schemas |
                ForEach-Object { [string]$_.schemaName } |
                Where-Object { $_ -cne "public" })
        $artifactInventory = Select-InventorySchemaScope `
            -Inventory $fullInventory `
            -SchemaName ([string[]](@($artifactSchemaNames) + @("public"))) `
            -ExcludePrincipals
    }

    return [pscustomobject]@{
        ApiSchemaFormatVersion     = $effectiveSchema.ApiSchemaFormatVersion
        EffectiveSchemaHash        = $effectiveSchema.EffectiveSchemaHash
        ResourceKeyCount           = $effectiveSchema.ResourceKeyCount
        ResourceKeySeedHashB64     = $effectiveSchema.ResourceKeySeedHashB64
        EngineVersion              = $engineVersion
        DatabaseCompatibilityLevel = $databaseCompatibilityLevel
        DocumentJsonColumnType     = $documentJsonColumnType
        FullInventory              = $fullInventory
        ArtifactInventory          = $artifactInventory
        DatabaseLevelObjects       = [string[]]$databaseLevelObjects
    }
}

function Remove-RestoreExpectedDatabase {
    <#
    .SYNOPSIS
    Drops the generated database the candidate schema's DDL was replayed into. The name must
    match the expected-database generator's exact shape, so this helper can never be pointed at
    a real database.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Drops only generator-shaped throwaway expected databases; the restore flow does not expose -WhatIf end to end.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'Forwarded to the sqlcmd transport, which carries the password via SQLCMDPASSWORD on docker exec; the MSSQL account is always "sa", and -PostgresUser names a PostgreSQL account that has no password here (psql authenticates over the container socket), so a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Forwarded to the sqlcmd transport; SecureString adds no protection across the docker exec boundary.')]
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedDatabaseName,

        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!",

        [string]$PostgresUser = "postgres"
    )

    if ($ExpectedDatabaseName -cnotmatch '^edfi_dms_restore_expected_[0-9a-f]{12}\z') {
        throw "Refusing to drop '$ExpectedDatabaseName': only generated restore expected databases (edfi_dms_restore_expected_<12 hex>) may be dropped by this helper."
    }

    $adminQueryArguments = @{
        DatabaseEngine = $DatabaseEngine
        ContainerName  = $ContainerName
        DatabaseName   = $(if ($DatabaseEngine -eq "mssql") { "master" } else { "postgres" })
        MssqlPassword  = $MssqlPassword
        PostgresUser   = $PostgresUser
    }

    if ($DatabaseEngine -eq "postgresql") {
        $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
            -Query "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$ExpectedDatabaseName' AND pid <> pg_backend_pid();" `
            -FailureMessage "Failed to terminate connections to expected database '$ExpectedDatabaseName' during cleanup."
        $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
            -Query "DROP DATABASE IF EXISTS `"$ExpectedDatabaseName`";" `
            -FailureMessage "Failed to drop expected database '$ExpectedDatabaseName'."
    }
    else {
        $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
            -Query "IF DB_ID(N'$ExpectedDatabaseName') IS NOT NULL BEGIN ALTER DATABASE [$ExpectedDatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$ExpectedDatabaseName]; END" `
            -FailureMessage "Failed to drop expected database '$ExpectedDatabaseName'."
    }
}

function Get-RestoreAuthoritativeObjectInventory {
    <#
    .SYNOPSIS
    Derives the AUTHORITATIVE object inventory from the candidate schema itself: emits the
    candidate's DDL with api-schema-tools, replays it into a generated throwaway database, and
    inventories that database with the same catalog reader used for the restored scratch.

    .DESCRIPTION
    This is the only inventory in the flow the package cannot influence. The package manifest
    describes the artifact, so a signed package carrying an extra object that its own manifest
    also declares passes every manifest-versus-database comparison; judging the restored scratch
    against the candidate's own DDL closes that hole. The expected database and the transient
    in-container DDL file are removed in finally on every path. After a successful derivation a
    failed expected-database drop is the error, so no inventory reaches the scratch verdict while
    that database may remain; after a failed derivation it is only reported.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Restore-internal derivation against a generated throwaway database; the restore flow does not expose -WhatIf end to end.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'Forwarded to the sqlcmd transport, which carries the password via SQLCMDPASSWORD on docker exec; the MSSQL account is always "sa", and -PostgresUser names a PostgreSQL account that has no password here (psql authenticates over the container socket), so a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Forwarded to the sqlcmd transport; SecureString adds no protection across the docker exec boundary.')]
    param (
        [Parameter(Mandatory = $true)]
        [string[]]$SchemaFilePath,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$OutputRoot,

        [string]$SchemaToolPath = "",

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!",

        [string]$PostgresUser = "postgres"
    )

    # Emit the candidate schema set's DDL into the private stage (never the candidate tree).
    $toolPath = Resolve-DmsSchemaTool -RequestedPath $SchemaToolPath
    $dialect = if ($DatabaseEngine -eq "mssql") { "mssql" } else { "pgsql" }
    $emitDirectory = Join-Path $OutputRoot "expected-ddl-$([Guid]::NewGuid().ToString('N'))"
    $emitArguments = @("ddl", "emit", "--schema") + $SchemaFilePath + @("--output", $emitDirectory, "--dialect", $dialect)

    $global:LASTEXITCODE = 0
    $emitOutput = if ($toolPath.EndsWith(".ps1", [System.StringComparison]::OrdinalIgnoreCase)) {
        & pwsh -NoLogo -NoProfile -File $toolPath @emitArguments 2>&1
    } else {
        & $toolPath @emitArguments 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "api-schema-tools ddl emit failed with exit code $LASTEXITCODE while deriving the authoritative object inventory. Output: $(($emitOutput | Out-String).Trim())"
    }

    $ddlPath = Join-Path $emitDirectory "$dialect.sql"
    if (-not (Test-Path -LiteralPath $ddlPath -PathType Leaf)) {
        throw "api-schema-tools ddl emit reported success but produced no '$dialect.sql' at '$ddlPath'."
    }

    $expectedDatabaseName = New-RestoreExpectedDatabaseName
    Assert-SafeRestoreDatabaseName -DatabaseEngine $DatabaseEngine -DatabaseName $expectedDatabaseName -Purpose "restore expected database"

    $adminQueryArguments = @{
        DatabaseEngine = $DatabaseEngine
        ContainerName  = $ContainerName
        DatabaseName   = $(if ($DatabaseEngine -eq "mssql") { "master" } else { "postgres" })
        MssqlPassword  = $MssqlPassword
        PostgresUser   = $PostgresUser
    }
    $containerDdlPath = $null
    $expectedInventory = $null
    $derivationSucceeded = $false

    try {
        if ($DatabaseEngine -eq "postgresql") {
            # The candidate DDL references the locked-down restore-global role and pgcrypto's
            # digest() (via dms.uuidv5), exactly as a restored artifact does.
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query (Get-PostgresqlRestoreGlobalRoleSql) `
                -FailureMessage "Failed to ensure the PostgreSQL restore-global role before the expected-schema replay."
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query "DROP DATABASE IF EXISTS `"$expectedDatabaseName`";" `
                -FailureMessage "Failed to drop a pre-existing expected database '$expectedDatabaseName'."
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query "CREATE DATABASE `"$expectedDatabaseName`";" `
                -FailureMessage "Failed to create expected database '$expectedDatabaseName'."
            $null = Invoke-RestoreCatalogQuery `
                -DatabaseEngine $DatabaseEngine `
                -ContainerName $ContainerName `
                -DatabaseName $expectedDatabaseName `
                -MssqlPassword $MssqlPassword `
                -PostgresUser $PostgresUser `
                -Query "CREATE EXTENSION IF NOT EXISTS `"pgcrypto`";" `
                -FailureMessage "Failed to create the pgcrypto extension in expected database '$expectedDatabaseName'."

            $containerDdlPath = "/tmp/restore-expected-$([Guid]::NewGuid().ToString('N')).sql"
            $null = Invoke-RestoreDockerCommand `
                -ArgumentList @("cp", $ddlPath, "$($ContainerName):$containerDdlPath") `
                -FailureMessage "Failed to copy the candidate DDL into container '$ContainerName'."
            $null = Invoke-RestoreDockerCommand `
                -ArgumentList @("exec", $ContainerName, "psql", "-U", $PostgresUser, "-d", $expectedDatabaseName, "-v", "ON_ERROR_STOP=1", "-f", $containerDdlPath) `
                -FailureMessage "Replay of the candidate DDL into expected database '$expectedDatabaseName' failed."
        }
        else {
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query "IF DB_ID(N'$expectedDatabaseName') IS NULL CREATE DATABASE [$expectedDatabaseName];" `
                -FailureMessage "Failed to create expected database '$expectedDatabaseName'."

            # Not /tmp: docker cp leaves the file root-owned, and sticky /tmp then refuses the
            # mssql user's cleanup rm. The data directory is where the .bak artifacts go too.
            $containerDdlPath = "/var/opt/mssql/data/restore-expected-$([Guid]::NewGuid().ToString('N')).sql"
            $null = Invoke-RestoreDockerCommand `
                -ArgumentList @("cp", $ddlPath, "$($ContainerName):$containerDdlPath") `
                -FailureMessage "Failed to copy the candidate DDL into container '$ContainerName'."
            $null = Invoke-RestoreDockerCommand `
                -ArgumentList @("exec", "-e", "SQLCMDPASSWORD=$MssqlPassword", $ContainerName, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-d", $expectedDatabaseName, "-C", "-b", "-i", $containerDdlPath) `
                -FailureMessage "Replay of the candidate DDL into expected database '$expectedDatabaseName' failed."
        }

        $expectedFacts = Get-RestoreDatabaseCatalogFact `
            -DatabaseEngine $DatabaseEngine `
            -ContainerName $ContainerName `
            -DatabaseName $expectedDatabaseName `
            -MssqlPassword $MssqlPassword `
            -PostgresUser $PostgresUser

        $expectedInventory = $expectedFacts.FullInventory
        $derivationSucceeded = $true
    }
    finally {
        # Two independent cleanups: a failed file removal must never skip the database drop.
        if ($null -ne $containerDdlPath) {
            try {
                $null = Invoke-RestoreDockerCommand `
                    -ArgumentList @("exec", $ContainerName, "rm", "-f", $containerDdlPath) `
                    -FailureMessage "Failed to remove the transient in-container DDL '$containerDdlPath'."
            }
            catch {
                Write-Warning "Expected-DDL cleanup did not complete: $(($_.Exception.Message | Out-String).Trim()) Remove '$containerDdlPath' in container '$ContainerName' manually if it remains."
            }
        }
        try {
            Remove-RestoreExpectedDatabase `
                -DatabaseEngine $DatabaseEngine `
                -ExpectedDatabaseName $expectedDatabaseName `
                -ContainerName $ContainerName `
                -MssqlPassword $MssqlPassword `
                -PostgresUser $PostgresUser
        }
        catch {
            $cleanupFailure = "$(($_.Exception.Message | Out-String).Trim()) Remove expected database '$expectedDatabaseName' in container '$ContainerName' manually if it remains."
            # After a successful derivation the cleanup failure is the error, so no inventory
            # reaches scratch validation, the workspace commit, or the target replacement.
            # Otherwise the derivation failure already propagating stays the error and this is
            # reported.
            if ($derivationSucceeded) {
                throw "Authoritative inventory derivation passed, but its expected-database cleanup did not complete: $cleanupFailure"
            }
            Write-Warning "Expected-database cleanup did not complete: $cleanupFailure"
        }
    }

    return $expectedInventory
}

function Assert-RestoreScratchMatchesAuthoritativeInventory {
    <#
    .SYNOPSIS
    Fails unless the restored scratch database's schema/object inventory matches the
    authoritative inventory derived from the candidate schema's own DDL, naming every extra and
    missing object. Object-level only: principals are out of scope here (a backup legitimately
    carries principals a freshly provisioned database does not, and the DMS-only gate already
    judges them).
    #>
    param (
        [Parameter(Mandatory = $true)]
        $AuthoritativeInventory,

        [Parameter(Mandatory = $true)]
        $ScratchInventory,

        [Parameter(Mandatory = $true)]
        [string]$ScratchDatabaseName
    )

    $verdict = Compare-RestoreObjectInventory `
        -ExpectedInventory $AuthoritativeInventory `
        -ActualInventory $ScratchInventory
    if ($verdict.IsMatch) {
        return
    }

    $differences = [System.Collections.Generic.List[string]]::new()
    if (@($verdict.ExtraSchemas).Count -gt 0) {
        $differences.Add("schemas the candidate schema does not define: $(@($verdict.ExtraSchemas) -join ', ')")
    }
    if (@($verdict.MissingSchemas).Count -gt 0) {
        $differences.Add("schemas the candidate schema defines but the artifact lacks: $(@($verdict.MissingSchemas) -join ', ')")
    }
    if (@($verdict.ExtraObjects).Count -gt 0) {
        $differences.Add("objects the candidate schema does not define: $(@($verdict.ExtraObjects) -join ', ')")
    }
    if (@($verdict.MissingObjects).Count -gt 0) {
        $differences.Add("objects the candidate schema defines but the artifact lacks: $(@($verdict.MissingObjects) -join ', ')")
    }

    throw "Restore scratch database '$ScratchDatabaseName' does not match the authoritative inventory derived from the candidate schema - $($differences -join '; '). The package manifest cannot vouch for this: it describes the artifact, so contamination the manifest also declares is only visible against the candidate's own DDL."
}

function Invoke-RestoreScratchValidation {
    <#
    .SYNOPSIS
    Replays the staged artifact into a throwaway scratch database and proves the package's
    claims against reality before anything destructive happens near the target.

    .DESCRIPTION
    The artifact is streamed into the container from the stage's retained authenticated
    package bytes (never from a host path, so a replaced staging file cannot change it) and
    hashed THERE against the manifest (the transfer-integrity check: the copy psql or RESTORE
    reads is what was sent), replayed into
    a freshly generated scratch database (never the target), and then validated: the DMS-only
    gate over the full scratch inventory, the PostgreSQL privilege gate (only the grants the
    authoritative DDL makes may survive a replay), the shared
    manifest-vs-database comparator (effective-schema fields, physical baseline including
    compatibility level and DocumentJson storage type, engine major, canonical inventory hash),
    the manifest<->candidate schema agreement, the project set against the validated partition,
    the Populated-kind non-descriptor data predicate, and the capture of the package's
    dms.DataStoreIdentity.SourceIdentity for the post-restore reseed verification. The scratch
    database and the transient in-container artifact copy are dropped in finally on every path.
    When validation passed, a cleanup failure throws and no proof is returned; when validation
    failed, its error stays the error and the cleanup failure is a loud warning.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Restore-internal validation step against a generated throwaway database; the restore flow does not expose -WhatIf end to end.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'Forwarded to the sqlcmd transport, which carries the password via SQLCMDPASSWORD on docker exec; the MSSQL account is always "sa", and -PostgresUser names a PostgreSQL account that has no password here (psql authenticates over the container socket), so a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Forwarded to the sqlcmd transport; SecureString adds no protection across the docker exec boundary.')]
    param (
        [Parameter(Mandatory = $true)]
        $Stage,

        [Parameter(Mandatory = $true)]
        $CandidateFact,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Minimal", "Populated")]
        [string]$RestoreTemplate,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [string]$ContainerName = "",

        [string]$SchemaToolPath = "",

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!",

        [string]$PostgresUser = "postgres"
    )

    if ([string]::IsNullOrWhiteSpace($ContainerName)) {
        $ContainerName = Get-RestoreDatabaseContainerName -DatabaseEngine $DatabaseEngine
    }
    $manifest = $Stage.Manifest
    $adminDatabaseName = if ($DatabaseEngine -eq "mssql") { "master" } else { "postgres" }

    $scratchDatabaseName = New-RestoreScratchDatabaseName
    Assert-SafeRestoreDatabaseName -DatabaseEngine $DatabaseEngine -DatabaseName $scratchDatabaseName -Purpose "restore scratch database"

    # The replay source is the stage's retained authenticated bytes, never a host path; check
    # that it is present and intact BEFORE any docker activity.
    Assert-RestoreRetainedPackage -Stage $Stage -AbortDescription "aborting before any database activity."

    $adminQueryArguments = @{
        DatabaseEngine = $DatabaseEngine
        ContainerName  = $ContainerName
        DatabaseName   = $adminDatabaseName
        MssqlPassword  = $MssqlPassword
        PostgresUser   = $PostgresUser
    }
    $containerArtifactPath = $null
    $scratchProof = $null

    try {
        if ($DatabaseEngine -eq "postgresql") {
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query (Get-PostgresqlRestoreGlobalRoleSql) `
                -FailureMessage "Failed to ensure the PostgreSQL restore-global role before the scratch replay."
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$scratchDatabaseName' AND pid <> pg_backend_pid();" `
                -FailureMessage "Failed to terminate connections to scratch database '$scratchDatabaseName'."
            # Quoted for consistency with every PostgreSQL identifier interpolation in this
            # module (generated names are lowercase, so quoting changes nothing here).
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query "DROP DATABASE IF EXISTS `"$scratchDatabaseName`";" `
                -FailureMessage "Failed to drop a pre-existing scratch database '$scratchDatabaseName'."
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query "CREATE DATABASE `"$scratchDatabaseName`";" `
                -FailureMessage "Failed to create scratch database '$scratchDatabaseName'."

            $containerArtifactPath = "/tmp/restore-scratch-$([Guid]::NewGuid().ToString('N')).sql"
            Copy-RestoreStagedArtifactToContainer `
                -Stage $Stage `
                -ContainerName $ContainerName `
                -ContainerArtifactPath $containerArtifactPath `
                -FailureMessage "Failed to copy the staged artifact into container '$ContainerName'."
            Assert-RestoreContainerArtifactSha256 `
                -ContainerName $ContainerName `
                -ContainerArtifactPath $containerArtifactPath `
                -ExpectedSha256 ([string]$manifest.artifactSha256) `
                -ConsumerDescription "the scratch replay"
            $null = Invoke-RestoreDockerCommand `
                -ArgumentList (Get-PostgresqlArtifactReplayArgumentList -ContainerName $ContainerName -PostgresUser $PostgresUser -DatabaseName $scratchDatabaseName -ContainerArtifactPath $containerArtifactPath) `
                -FailureMessage "Replay of the artifact into scratch database '$scratchDatabaseName' failed."
        }
        else {
            $containerArtifactPath = "/var/opt/mssql/data/restore-scratch-$([Guid]::NewGuid().ToString('N')).bak"
            Copy-RestoreStagedArtifactToContainer `
                -Stage $Stage `
                -ContainerName $ContainerName `
                -ContainerArtifactPath $containerArtifactPath `
                -FailureMessage "Failed to copy the staged artifact into container '$ContainerName'."
            Assert-RestoreContainerArtifactSha256 `
                -ContainerName $ContainerName `
                -ContainerArtifactPath $containerArtifactPath `
                -ExpectedSha256 ([string]$manifest.artifactSha256) `
                -ConsumerDescription "the scratch restore"

            $fileListRows = Invoke-RestoreDockerCommand `
                -ArgumentList @("exec", "-e", "SQLCMDPASSWORD=$MssqlPassword", $ContainerName, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-d", "master", "-C", "-b", "-h", "-1", "-W", "-s", "|", "-Q", "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$containerArtifactPath';") `
                -FailureMessage "Failed to read the file list from the staged artifact."
            $backupFileList = ConvertFrom-MssqlBackupFileList -FileListOutput ([string[]]$fileListRows) -BackupFileName ([string]$manifest.artifactFileName)
            $moveClauses = New-MssqlRestoreMoveClause `
                -DatabaseName $scratchDatabaseName `
                -DataLogicalNames $backupFileList.DataLogicalNames `
                -LogLogicalNames $backupFileList.LogLogicalNames `
                -BackupFileName ([string]$manifest.artifactFileName)

            $null = Invoke-RestoreDockerCommand `
                -ArgumentList @("exec", "-e", "SQLCMDPASSWORD=$MssqlPassword", $ContainerName, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-d", "master", "-C", "-b", "-Q", "RESTORE DATABASE [$scratchDatabaseName] FROM DISK = N'$containerArtifactPath' WITH $($moveClauses -join ', '), REPLACE;") `
                -FailureMessage "Restore of the staged artifact into scratch database '$scratchDatabaseName' failed."
        }

        $scratchFacts = Get-RestoreDatabaseCatalogFact `
            -DatabaseEngine $DatabaseEngine `
            -ContainerName $ContainerName `
            -DatabaseName $scratchDatabaseName `
            -MssqlPassword $MssqlPassword `
            -PostgresUser $PostgresUser

        $scratchPartition = Assert-DmsOnlyInventory `
            -DatabaseEngine $DatabaseEngine `
            -Inventory $scratchFacts.FullInventory `
            -DatabaseLevelObject $scratchFacts.DatabaseLevelObjects `
            -SourceDescription "Restore scratch database '$scratchDatabaseName'"

        # The replay re-ran every GRANT and ALTER ... OWNER the artifact carries against this
        # server's roles; only the ownership and privileges the authoritative DDL produces may
        # survive into the target. Ownership is read as the restoring administrator, so whatever
        # it is named, the objects it owns are its own; the privilege rule trusts owners, so
        # ownership is judged first.
        if ($DatabaseEngine -eq "postgresql") {
            $ownershipRows = Invoke-RestoreCatalogQuery `
                -DatabaseEngine $DatabaseEngine `
                -ContainerName $ContainerName `
                -DatabaseName $scratchDatabaseName `
                -PostgresUser $PostgresUser `
                -Query (Get-PostgresqlTemplateOwnershipQuerySql) `
                -FailureMessage "Failed to read the object ownership of scratch database '$scratchDatabaseName'."
            $null = Assert-PostgresqlArtifactOwnership `
                -OwnershipRow $ownershipRows `
                -SourceDescription "Restore scratch database '$scratchDatabaseName'" `
                -FailureSuffix "The package is refused before the target is touched."

            $privilegeRows = Invoke-RestoreCatalogQuery `
                -DatabaseEngine $DatabaseEngine `
                -ContainerName $ContainerName `
                -DatabaseName $scratchDatabaseName `
                -PostgresUser $PostgresUser `
                -Query (Get-PostgresqlPrivilegeQuerySql) `
                -FailureMessage "Failed to read the privileges of scratch database '$scratchDatabaseName'."
            Assert-PostgresqlArtifactPrivilege `
                -PrivilegeRow $privilegeRows `
                -SourceDescription "Restore scratch database '$scratchDatabaseName'" `
                -FailureSuffix "The package is refused before the target is touched."
        }
        else {
            # The RESTORE re-created the backup's permissions and role memberships, built-in
            # principals included; only a new database's defaults may survive into the target.
            $securityRows = Invoke-RestoreCatalogQuery `
                -DatabaseEngine $DatabaseEngine `
                -ContainerName $ContainerName `
                -DatabaseName $scratchDatabaseName `
                -MssqlPassword $MssqlPassword `
                -Query (Get-MssqlSecurityStateQuerySql) `
                -FailureMessage "Failed to read the permissions and role memberships of scratch database '$scratchDatabaseName'."
            Assert-MssqlArtifactSecurityState `
                -SecurityRow $securityRows `
                -SourceDescription "Restore scratch database '$scratchDatabaseName'" `
                -FailureSuffix "The package is refused before the target is touched."
        }

        Assert-RestoreManifestMatchesDatabase `
            -Manifest $manifest `
            -Facts $scratchFacts `
            -DatabaseEngine $DatabaseEngine `
            -DatabaseDescription "Restore scratch database '$scratchDatabaseName'"

        # Manifest<->candidate agreement (the cross-check proved it once; scratch validation
        # re-asserts it so the three-way equality scratch == manifest == candidate is explicit).
        if ([string]$manifest.effectiveSchemaHash -cne [string]$CandidateFact.EffectiveSchemaHash) {
            throw "The restore manifest's effectiveSchemaHash '$($manifest.effectiveSchemaHash)' does not match the candidate workspace's '$($CandidateFact.EffectiveSchemaHash)'."
        }
        if (-not ([string]$manifest.apiSchemaFormatVersion).Equals([string]$CandidateFact.ApiSchemaFormatVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The restore manifest's apiSchemaFormatVersion '$($manifest.apiSchemaFormatVersion)' does not match the candidate workspace's '$($CandidateFact.ApiSchemaFormatVersion)'."
        }

        # The AUTHORITATIVE gate: judge the restored scratch against an inventory derived from
        # the candidate schema's own DDL, not against the package's manifest. Everything above
        # compares the artifact to claims the artifact itself carries, so a signed package whose
        # manifest declares its own extra objects would pass; this is the only comparison the
        # package cannot influence. It runs before the workspace commit and before any target
        # replacement, and its expected database is dropped by the helper's own finally.
        $authoritativeInventory = Get-RestoreAuthoritativeObjectInventory `
            -SchemaFilePath $CandidateFact.SchemaFilePaths `
            -DatabaseEngine $DatabaseEngine `
            -ContainerName $ContainerName `
            -OutputRoot $Stage.StageDirectory `
            -SchemaToolPath $SchemaToolPath `
            -MssqlPassword $MssqlPassword `
            -PostgresUser $PostgresUser
        Assert-RestoreScratchMatchesAuthoritativeInventory `
            -AuthoritativeInventory $authoritativeInventory `
            -ScratchInventory $scratchFacts.FullInventory `
            -ScratchDatabaseName $scratchDatabaseName

        $manifestProjects = @($manifest.projects)
        $scratchProjects = @($scratchPartition.ProjectSchemaNames)
        if (($manifestProjects -join "|") -cne ($scratchProjects -join "|")) {
            throw "The restore manifest declares projects [$($manifestProjects -join ', ')] but scratch database '$scratchDatabaseName' contains [$($scratchProjects -join ', ')]."
        }

        if ($RestoreTemplate -eq "Populated") {
            # Same predicate shape as verify-template-restore.ps1's populated assertion: at
            # least one non-descriptor, non-school-year document must survive the replay. No
            # hard-coded counts, so data-standard sample changes cannot break it.
            $populatedCountQuery = if ($DatabaseEngine -eq "mssql") {
                "SET NOCOUNT ON; SELECT COUNT(*) FROM [dms].[Document] d JOIN [dms].[ResourceKey] rk ON rk.[ResourceKeyId] = d.[ResourceKeyId] WHERE rk.[ResourceName] NOT LIKE '%Descriptor' AND rk.[ResourceName] NOT LIKE '%SchoolYear%';"
            }
            else {
                'SELECT COUNT(*) FROM dms."Document" d JOIN dms."ResourceKey" rk ON rk."ResourceKeyId" = d."ResourceKeyId" WHERE rk."ResourceName" NOT LIKE ' + "'%Descriptor' AND rk.`"ResourceName`" NOT ILIKE '%SchoolYear%';"
            }
            $populatedRows = Invoke-RestoreCatalogQuery `
                -DatabaseEngine $DatabaseEngine `
                -ContainerName $ContainerName `
                -DatabaseName $scratchDatabaseName `
                -MssqlPassword $MssqlPassword `
                -PostgresUser $PostgresUser `
                -Query $populatedCountQuery `
                -FailureMessage "Failed to count populated documents in scratch database '$scratchDatabaseName'."
            $populatedCountText = ([string]@($populatedRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)).Trim()
            $populatedCount = 0
            if (-not [int]::TryParse($populatedCountText, [ref]$populatedCount)) {
                throw "The populated-document count from scratch database '$scratchDatabaseName' is not an integer: '$populatedCountText'."
            }
            if ($populatedCount -lt 1) {
                throw "The package declares templateKind 'Populated' but scratch database '$scratchDatabaseName' contains no non-descriptor, non-school-year documents."
            }
        }

        # Capture the PACKAGE's SourceIdentity (no reseed here - the target replacement reseeds
        # and then verifies the new value differs from this one).
        $identityRows = @(Invoke-RestoreCatalogQuery `
                -DatabaseEngine $DatabaseEngine `
                -ContainerName $ContainerName `
                -DatabaseName $scratchDatabaseName `
                -MssqlPassword $MssqlPassword `
                -PostgresUser $PostgresUser `
                -Query (Get-SourceIdentitySelectSql -DatabaseEngine $DatabaseEngine) `
                -FailureMessage "Failed to read dms.DataStoreIdentity.SourceIdentity from scratch database '$scratchDatabaseName'." |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        if ($identityRows.Count -ne 1) {
            throw "Scratch database '$scratchDatabaseName' must contain exactly one dms.DataStoreIdentity row, found $($identityRows.Count). The package is not a valid DMS datastore template."
        }
        $packageSourceIdentity = ([string]$identityRows[0]).Trim()

        $scratchProof = [pscustomobject]@{
            PackageSourceIdentity = $packageSourceIdentity
            ScratchFacts          = $scratchFacts
            ProjectSchemaNames    = [string[]]$scratchProjects
        }
    }
    finally {
        try {
            Remove-RestoreScratchDatabase `
                -DatabaseEngine $DatabaseEngine `
                -ScratchDatabaseName $scratchDatabaseName `
                -ContainerName $ContainerName `
                -MssqlPassword $MssqlPassword `
                -PostgresUser $PostgresUser
            if ($null -ne $containerArtifactPath) {
                $null = Invoke-RestoreDockerCommand `
                    -ArgumentList @("exec", $ContainerName, "rm", "-f", $containerArtifactPath) `
                    -FailureMessage "Failed to remove the transient in-container artifact '$containerArtifactPath'."
            }
        }
        catch {
            $cleanupFailure = "$(($_.Exception.Message | Out-String).Trim()) Remove scratch database '$scratchDatabaseName' and '$containerArtifactPath' in container '$ContainerName' manually if they remain."
            # A proof only exists when validation passed. Then the cleanup failure is the error,
            # so no proof reaches the workspace commit or the target replacement. Otherwise the
            # validation failure already propagating stays the error and this is reported.
            if ($null -ne $scratchProof) {
                throw "Scratch validation passed, but its cleanup did not complete: $cleanupFailure"
            }
            Write-Warning "Scratch cleanup did not complete: $cleanupFailure"
        }
    }

    return $scratchProof
}


function Remove-RestoreScratchDatabase {
    <#
    .SYNOPSIS
    Drops one generated restore scratch database. The name must match the scratch generator's
    exact shape - this helper can never be pointed at a real database. Throws on failure so
    the caller decides whether cleanup failures are fatal (scratch validation throws after a
    passed validation and warns after a failed one).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Drops only generator-shaped throwaway scratch databases; the restore flow does not expose -WhatIf end to end.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'Forwarded to the sqlcmd transport, which carries the password via SQLCMDPASSWORD on docker exec; the MSSQL account is always "sa", and -PostgresUser names a PostgreSQL account that has no password here (psql authenticates over the container socket), so a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Forwarded to the sqlcmd transport; SecureString adds no protection across the docker exec boundary.')]
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [string]$ScratchDatabaseName,

        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!",

        [string]$PostgresUser = "postgres"
    )

    if ($ScratchDatabaseName -cnotmatch '^edfi_dms_restore_scratch_[0-9a-f]{12}\z') {
        throw "Refusing to drop '$ScratchDatabaseName': only generated restore scratch databases (edfi_dms_restore_scratch_<12 hex>) may be dropped by this helper."
    }

    $adminQueryArguments = @{
        DatabaseEngine = $DatabaseEngine
        ContainerName  = $ContainerName
        DatabaseName   = $(if ($DatabaseEngine -eq "mssql") { "master" } else { "postgres" })
        MssqlPassword  = $MssqlPassword
        PostgresUser   = $PostgresUser
    }

    if ($DatabaseEngine -eq "postgresql") {
        $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
            -Query "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$ScratchDatabaseName' AND pid <> pg_backend_pid();" `
            -FailureMessage "Failed to terminate connections to scratch database '$ScratchDatabaseName' during cleanup."
        $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
            -Query "DROP DATABASE IF EXISTS `"$ScratchDatabaseName`";" `
            -FailureMessage "Failed to drop scratch database '$ScratchDatabaseName'."
    }
    else {
        # SINGLE_USER only applies to an ONLINE database: a scratch left RESTORING by a failed
        # RESTORE refuses the ALTER, and that error would fail the cleanup.
        $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
            -Query "IF DB_ID(N'$ScratchDatabaseName') IS NOT NULL BEGIN IF DATABASEPROPERTYEX(N'$ScratchDatabaseName', 'Status') = N'ONLINE' ALTER DATABASE [$ScratchDatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$ScratchDatabaseName]; END" `
            -FailureMessage "Failed to drop scratch database '$ScratchDatabaseName'."
    }
}

function Remove-RestorePreflightDatabase {
    <#
    .SYNOPSIS
    Drops the generated PostgreSQL preflight database, best-effort: a failure warns loudly and
    never throws, because this cleanup runs on failure paths where the server may already be
    unreachable, and the generated name cannot collide with a real database. With -Required
    the failure throws instead, so a successful preflight cannot proceed to the workspace
    commit while the database may remain. SQL Server creates no preflight database (a blank
    name is the documented no-op). The name must match the preflight generator's exact
    shape - this helper can never be pointed at a real database.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Drops only generator-shaped throwaway preflight databases; the restore flow does not expose -WhatIf end to end.')]
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [string]$PreflightDatabaseName = "",

        [string]$ContainerName = "",

        [string]$PostgresUser = "postgres",

        [switch]$Required
    )

    if ($DatabaseEngine -eq "mssql" -or [string]::IsNullOrWhiteSpace($PreflightDatabaseName)) {
        return
    }

    if ($PreflightDatabaseName -cnotmatch '^edfi_dms_restore_preflight_[0-9a-f]{12}\z') {
        throw "Refusing to drop '$PreflightDatabaseName': only generated restore preflight databases (edfi_dms_restore_preflight_<12 hex>) may be dropped by this helper."
    }

    if ([string]::IsNullOrWhiteSpace($ContainerName)) {
        $ContainerName = Get-RestoreDatabaseContainerName -DatabaseEngine $DatabaseEngine
    }

    try {
        $null = Invoke-RestoreCatalogQuery `
            -DatabaseEngine $DatabaseEngine `
            -ContainerName $ContainerName `
            -DatabaseName "postgres" `
            -PostgresUser $PostgresUser `
            -Query "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$PreflightDatabaseName' AND pid <> pg_backend_pid();" `
            -FailureMessage "Failed to terminate connections to preflight database '$PreflightDatabaseName'."
        $null = Invoke-RestoreCatalogQuery `
            -DatabaseEngine $DatabaseEngine `
            -ContainerName $ContainerName `
            -DatabaseName "postgres" `
            -PostgresUser $PostgresUser `
            -Query "DROP DATABASE IF EXISTS `"$PreflightDatabaseName`";" `
            -FailureMessage "Failed to drop preflight database '$PreflightDatabaseName'."
    }
    catch {
        $cleanupFailure = "Preflight database cleanup did not complete: $(($_.Exception.Message | Out-String).Trim()) Drop '$PreflightDatabaseName' in container '$ContainerName' manually if it remains."
        if ($Required) {
            throw $cleanupFailure
        }
        Write-Warning $cleanupFailure
    }
}

function Invoke-RestoreTargetReplacement {
    <#
    .SYNOPSIS
    The destructive step: replaces the target database with the scratch-validated artifact,
    then reseeds and verifies the datastore identity.

    .DESCRIPTION
    Re-runs the full non-destructive target-safety gate (Assert-RestoreTargetSafety), streams
    the artifact into the container from the SAME retained authenticated bytes scratch
    validation replayed (never from a host path), hashes the copy inside the container (the
    transfer-integrity check), runs the target-safety gate once more immediately before the
    first destructive statement (a service may have started during the transfer), then
    dispatches per engine: PostgreSQL ensures the shared restore-global role, terminates
    connections, drops and recreates the target, and replays via psql ON_ERROR_STOP in a
    single transaction;
    SQL Server reads the copied backup's file list and builds one MOVE per logical file to
    target-derived physical names before that last gate, then single-user-drops an existing
    target and restores with those MOVE clauses plus REPLACE. Afterwards dms.DataStoreIdentity.SourceIdentity is reseeded and verified against
    ALL FOUR conditions (exactly one row, a valid UUID, not the empty UUID, different from the
    package value captured in scratch) - any failure is a hard error before any service can
    select the target. The transient in-container artifact copy is removed in finally.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'The guarded destructive restore step itself; the restore flow does not expose -WhatIf end to end, and every precondition is enforced by the preceding gates.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'Forwarded to the sqlcmd transport, which carries the password via SQLCMDPASSWORD on docker exec; the MSSQL account is always "sa", and -PostgresUser names a PostgreSQL account that has no password here (psql authenticates over the container socket), so a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Forwarded to the sqlcmd transport; SecureString adds no protection across the docker exec boundary.')]
    param (
        [Parameter(Mandatory = $true)]
        $Stage,

        [Parameter(Mandatory = $true)]
        [string]$TargetDatabaseName,

        [Parameter(Mandatory = $true)]
        [string]$PackageSourceIdentity,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [string]$ContainerName = "",

        [switch]$SeparateConfigDatabase,

        [string]$EffectiveConfigDatabaseName = "",

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!",

        [string]$PostgresUser = "postgres"
    )

    if ([string]::IsNullOrWhiteSpace($ContainerName)) {
        $ContainerName = Get-RestoreDatabaseContainerName -DatabaseEngine $DatabaseEngine
    }
    $manifest = $Stage.Manifest
    $adminDatabaseName = if ($DatabaseEngine -eq "mssql") { "master" } else { "postgres" }

    # The full non-destructive gate: name safety, live reserved catalog, running services and
    # sessions, live engine major, PostgreSQL platform floor, CDC bindings. It runs here, so a target that is
    # already unsafe never receives the transfer, and again after the transfer is verified,
    # immediately before the first statement that touches the target: CMS or DMS can start
    # while the artifact is copied.
    $targetSafetyArguments = @{
        DatabaseEngine              = $DatabaseEngine
        TargetDatabaseName          = $TargetDatabaseName
        Manifest                    = $manifest
        ContainerName               = $ContainerName
        SeparateConfigDatabase      = $SeparateConfigDatabase
        EffectiveConfigDatabaseName = $EffectiveConfigDatabaseName
        MssqlPassword               = $MssqlPassword
        PostgresUser                = $PostgresUser
    }
    Assert-RestoreTargetSafety @targetSafetyArguments

    # The target consumes the same retained authenticated bytes scratch validation replayed,
    # never a host path; check that they are present and intact before the copy.
    Assert-RestoreRetainedPackage -Stage $Stage -AbortDescription "aborting before any destructive statement."

    $adminQueryArguments = @{
        DatabaseEngine = $DatabaseEngine
        ContainerName  = $ContainerName
        DatabaseName   = $adminDatabaseName
        MssqlPassword  = $MssqlPassword
        PostgresUser   = $PostgresUser
    }
    $targetQueryArguments = @{
        DatabaseEngine = $DatabaseEngine
        ContainerName  = $ContainerName
        DatabaseName   = $TargetDatabaseName
        MssqlPassword  = $MssqlPassword
        PostgresUser   = $PostgresUser
    }
    $containerArtifactPath = $null

    try {
        # The copy happens FIRST on both engines: a copy failure must leave the target
        # completely untouched, and the in-container hash must pass before the target is
        # emptied - no window between emptying the target and copying.
        $containerArtifactPath = if ($DatabaseEngine -eq "postgresql") {
            "/tmp/restore-target-$([Guid]::NewGuid().ToString('N')).sql"
        }
        else {
            "/var/opt/mssql/data/restore-target-$([Guid]::NewGuid().ToString('N')).bak"
        }
        Copy-RestoreStagedArtifactToContainer `
            -Stage $Stage `
            -ContainerName $ContainerName `
            -ContainerArtifactPath $containerArtifactPath `
            -FailureMessage "Failed to copy the staged artifact into container '$ContainerName'."
        Assert-RestoreContainerArtifactSha256 `
            -ContainerName $ContainerName `
            -ContainerArtifactPath $containerArtifactPath `
            -ExpectedSha256 ([string]$manifest.artifactSha256) `
            -ConsumerDescription "any change to target database '$TargetDatabaseName'"

        # SQL Server: read and parse the backup's file list and build the target's MOVE clauses
        # BEFORE the existing target is dropped, so a metadata failure leaves it in place.
        $moveClauses = @()
        if ($DatabaseEngine -eq "mssql") {
            $fileListRows = Invoke-RestoreDockerCommand `
                -ArgumentList @("exec", "-e", "SQLCMDPASSWORD=$MssqlPassword", $ContainerName, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-d", "master", "-C", "-b", "-h", "-1", "-W", "-s", "|", "-Q", "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$containerArtifactPath';") `
                -FailureMessage "Failed to read the file list from the staged artifact."
            $backupFileList = ConvertFrom-MssqlBackupFileList -FileListOutput ([string[]]$fileListRows) -BackupFileName ([string]$manifest.artifactFileName)
            $moveClauses = New-MssqlRestoreMoveClause `
                -DatabaseName $TargetDatabaseName `
                -DataLogicalNames $backupFileList.DataLogicalNames `
                -LogLogicalNames $backupFileList.LogLogicalNames `
                -BackupFileName ([string]$manifest.artifactFileName)
        }

        Assert-RestoreTargetSafety @targetSafetyArguments

        if ($DatabaseEngine -eq "postgresql") {
            # Idempotent role init keeps the target replay self-sufficient even when invoked
            # standalone (scratch validation normally ensured the role moments earlier).
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query (Get-PostgresqlRestoreGlobalRoleSql) `
                -FailureMessage "Failed to ensure the PostgreSQL restore-global role before the target replay."
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$TargetDatabaseName' AND pid <> pg_backend_pid();" `
                -FailureMessage "Failed to terminate connections to target database '$TargetDatabaseName'."
            # Identifiers are double-quoted so PostgreSQL cannot case-fold a mixed-case target
            # (e.g. EdFi_DMS) onto a different database; the safe-name charset excludes the
            # double quote, so no escaping is needed.
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query "DROP DATABASE IF EXISTS `"$TargetDatabaseName`";" `
                -FailureMessage "Failed to drop target database '$TargetDatabaseName'."
            $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                -Query "CREATE DATABASE `"$TargetDatabaseName`";" `
                -FailureMessage "Failed to create target database '$TargetDatabaseName'."

            # One transaction: a statement failure rolls back everything the replay created,
            # so a failed replay cannot leave partial contents that later startup could mistake
            # for a completed restore. The target was already dropped and recreated above.
            $null = Invoke-RestoreDockerCommand `
                -ArgumentList (Get-PostgresqlArtifactReplayArgumentList -ContainerName $ContainerName -PostgresUser $PostgresUser -DatabaseName $TargetDatabaseName -ContainerArtifactPath $containerArtifactPath) `
                -FailureMessage "Replay of the artifact into target database '$TargetDatabaseName' failed, so target restoration did not complete. The target was already dropped and recreated; rerun the restore before using '$TargetDatabaseName'."
        }
        else {
            $existsRows = @(Invoke-RestoreCatalogQuery @adminQueryArguments `
                    -Query "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'$TargetDatabaseName') IS NOT NULL THEN 1 ELSE 0 END;" `
                    -FailureMessage "Failed to check whether target database '$TargetDatabaseName' exists.")
            $targetExists = (@($existsRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1) -eq "1")
            if ($targetExists) {
                # SINGLE_USER only applies to an ONLINE database: a target left RESTORING by an
                # earlier failed RESTORE refuses the ALTER, and that error would fail the replacement.
                # A racing client can claim the single-user slot before DROP. Restore MULTI_USER
                # on failure so the existing database is not stranded, then propagate the error.
                $null = Invoke-RestoreCatalogQuery @adminQueryArguments `
                    -Query "BEGIN TRY IF DATABASEPROPERTYEX(N'$TargetDatabaseName', 'Status') = N'ONLINE' ALTER DATABASE [$TargetDatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$TargetDatabaseName]; END TRY BEGIN CATCH IF DB_ID(N'$TargetDatabaseName') IS NOT NULL AND DATABASEPROPERTYEX(N'$TargetDatabaseName', 'Status') = N'ONLINE' ALTER DATABASE [$TargetDatabaseName] SET MULTI_USER WITH ROLLBACK IMMEDIATE; THROW; END CATCH;" `
                    -FailureMessage "Failed to drop existing target database '$TargetDatabaseName'."
            }

            $null = Invoke-RestoreDockerCommand `
                -ArgumentList @("exec", "-e", "SQLCMDPASSWORD=$MssqlPassword", $ContainerName, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-d", "master", "-C", "-b", "-Q", "RESTORE DATABASE [$TargetDatabaseName] FROM DISK = N'$containerArtifactPath' WITH $($moveClauses -join ', '), REPLACE;") `
                -FailureMessage "Restore of the staged artifact into target database '$TargetDatabaseName' failed."
        }

        # Reseed the datastore identity, then verify ALL FOUR conditions. Any failure is a hard
        # error before any service can select the target.
        $null = Invoke-RestoreCatalogQuery @targetQueryArguments `
            -Query (Get-SourceIdentityReseedSql -DatabaseEngine $DatabaseEngine) `
            -FailureMessage "Failed to reseed dms.DataStoreIdentity.SourceIdentity in target database '$TargetDatabaseName'."
        $identityRows = @(Invoke-RestoreCatalogQuery @targetQueryArguments `
                -Query (Get-SourceIdentitySelectSql -DatabaseEngine $DatabaseEngine) `
                -FailureMessage "Failed to read dms.DataStoreIdentity.SourceIdentity from target database '$TargetDatabaseName'.")
        $identityVerdict = Test-RestoredSourceIdentityValue `
            -SourceIdentityRow ([string[]]$identityRows) `
            -PackageSourceIdentity $PackageSourceIdentity
        if (-not $identityVerdict.IsValid) {
            throw "Restored target '$TargetDatabaseName' failed the SourceIdentity verification: $($identityVerdict.Reason) No service may select this target until this is resolved."
        }

        return [pscustomobject]@{
            TargetDatabaseName      = $TargetDatabaseName
            RestoredSourceIdentity  = ([string]@($identityRows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -First 1)).Trim()
        }
    }
    finally {
        try {
            if ($null -ne $containerArtifactPath) {
                $null = Invoke-RestoreDockerCommand `
                    -ArgumentList @("exec", $ContainerName, "rm", "-f", $containerArtifactPath) `
                    -FailureMessage "Failed to remove the transient in-container artifact '$containerArtifactPath'."
            }
        }
        catch {
            Write-Warning "Target-replacement cleanup did not complete: $(($_.Exception.Message | Out-String).Trim()) Remove '$containerArtifactPath' in container '$ContainerName' manually if it remains."
        }
    }
}

Export-ModuleMember -Function `
    Get-RestoreWorkspaceRoot, `
    Resolve-RestoreTemplatePackageIdentity, `
    Get-RestorePackageVersionFromFileName, `
    Find-RestoreTemplatePackage, `
    Assert-TrustedRestorePackage, `
    Assert-RestoreManifestMatchesRequest, `
    Initialize-RestorePackageStage, `
    Remove-RestorePackageStage, `
    New-RestoreCandidateWorkspace, `
    ConvertTo-RestoreProjectSchemaName, `
    Get-RestoreCandidateSchemaFact, `
    Get-RestoreCandidateRelationalMappingVersion, `
    Assert-RestoreManifestMatchesCandidate, `
    Invoke-RestoreCandidateCrossCheck, `
    Get-RestoreDatabaseOnlyComposeFile, `
    Stop-RestoreDatabaseOnlySlice, `
    Assert-DmsComposeProjectStopped, `
    Test-RestoreWorkspaceTreeEqual, `
    Publish-RestoreCandidateWorkspace, `
    Get-RestoreDatabaseContainerName, `
    New-RestorePreflightEnvironment, `
    Wait-RestorePostgresqlTcpReady, `
    Invoke-RestoreCatalogQuery, `
    Get-RestoreEngineMajorVersion, `
    Get-RestorePostgresqlPlatformQuerySql, `
    Assert-RestoreTargetSafety, `
    Get-RestoreDatabaseCatalogFact, `
    Invoke-RestoreScratchValidation, `
    Remove-RestoreScratchDatabase, `
    Remove-RestoreExpectedDatabase, `
    Get-RestoreAuthoritativeObjectInventory, `
    Assert-RestoreScratchMatchesAuthoritativeInventory, `
    Remove-RestorePreflightDatabase, `
    Invoke-RestoreTargetReplacement, `
    Resolve-RestoreTargetDatabaseName, `
    Assert-RestoreTargetDatabaseNameSafe
