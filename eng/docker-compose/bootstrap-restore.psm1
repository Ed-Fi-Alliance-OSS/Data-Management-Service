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
    Stages the authenticated package into a private, immutable workspace: copies the .nupkg,
    proves the copy still carries the authenticated bytes, extracts it, validates the restore
    manifest (shape, requested engine/kind, package identity against the nuspec and the
    request), requires exactly one database artifact matching the manifest's declared name
    and SHA-256 with no undeclared artifacts beside it, and marks every staged file
    read-only. Scratch and target restore later consume these exact bytes, re-hashed before
    each use, so validation cannot be separated from execution by a file-replacement race.

    .OUTPUTS
    PSCustomObject: StageDirectory, PackagePath, PackageSha256, Manifest, ManifestPath,
    ArtifactPath, ArtifactSha256, Producer.
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

    try {
        $stagedPackagePath = Join-Path $stageDirectory ([System.IO.Path]::GetFileName($Package.PackagePath))
        Copy-Item -LiteralPath $Package.PackagePath -Destination $stagedPackagePath

        # The staged copy must still carry the exact authenticated bytes; a mismatch means
        # the source file changed between authentication and staging.
        $stagedPackageSha256 = Get-FileSha256Hex -Path $stagedPackagePath
        if ($stagedPackageSha256 -cne $AuthenticatedPackageSha256) {
            throw "The staged package's SHA-256 '$stagedPackageSha256' no longer matches the authenticated bytes '$AuthenticatedPackageSha256'; the package changed between authentication and staging."
        }

        $contentsDirectory = Join-Path $stageDirectory "contents"
        Expand-Nupkg -NupkgPath $stagedPackagePath -DestinationDirectory $contentsDirectory

        $manifestFiles = @(Get-ChildItem -LiteralPath $contentsDirectory -Filter (Get-RestoreManifestFileName) -Recurse -File)
        if ($manifestFiles.Count -ne 1) {
            throw "Package '$([System.IO.Path]::GetFileName($Package.PackagePath))' must contain exactly one $(Get-RestoreManifestFileName), found $($manifestFiles.Count). Packages without a restore manifest are not eligible for restore."
        }
        $manifest = Read-RestoreManifest -Path $manifestFiles[0].FullName

        Assert-RestoreManifestMatchesRequest -Manifest $manifest -DatabaseEngine $DatabaseEngine -RestoreTemplate $RestoreTemplate -DataStandardVersion $Package.DataStandardVersion

        # Identity triangle: the request, the manifest, and the nuspec must all agree on the
        # package id and version.
        if (-not ([string]$manifest.packageId).Equals($Package.PackageId, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([string]$manifest.packageVersion).Equals($Package.PackageVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The restore manifest declares package identity '$($manifest.packageId)@$($manifest.packageVersion)' but this restore resolved '$($Package.PackageId)@$($Package.PackageVersion)'."
        }

        $nuspecFiles = @(Get-ChildItem -LiteralPath $contentsDirectory -Filter "*.nuspec" -Recurse -File)
        if ($nuspecFiles.Count -ne 1) {
            throw "Package '$([System.IO.Path]::GetFileName($Package.PackagePath))' must contain exactly one .nuspec, found $($nuspecFiles.Count)."
        }
        [xml]$nuspecXml = Get-Content -LiteralPath $nuspecFiles[0].FullName -Raw
        $nuspecId = [string]$nuspecXml.package.metadata.id
        $nuspecVersion = [string]$nuspecXml.package.metadata.version
        if (-not $nuspecId.Equals($Package.PackageId, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not $nuspecVersion.Equals($Package.PackageVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The package nuspec declares identity '$nuspecId@$nuspecVersion' but this restore resolved '$($Package.PackageId)@$($Package.PackageVersion)'."
        }

        # Exactly one database artifact: the manifest-declared file, and no undeclared
        # .sql/.bak beside it that a later stage could be tricked into consuming.
        $declaredArtifacts = @(Get-ChildItem -LiteralPath $contentsDirectory -Filter ([string]$manifest.artifactFileName) -Recurse -File)
        if ($declaredArtifacts.Count -ne 1) {
            throw "Package '$([System.IO.Path]::GetFileName($Package.PackagePath))' must contain exactly one artifact named '$($manifest.artifactFileName)', found $($declaredArtifacts.Count)."
        }
        $artifactPath = $declaredArtifacts[0].FullName

        $allArtifactShapedFiles = @(Get-ChildItem -LiteralPath $contentsDirectory -Recurse -File |
                Where-Object { $_.Extension -ieq ".sql" -or $_.Extension -ieq ".bak" })
        $undeclaredArtifacts = @($allArtifactShapedFiles | Where-Object { $_.FullName -ne $artifactPath })
        if ($undeclaredArtifacts.Count -gt 0) {
            $listing = (@($undeclaredArtifacts | ForEach-Object { $_.Name }) -join ", ")
            throw "Package '$([System.IO.Path]::GetFileName($Package.PackagePath))' contains database artifacts beyond the manifest-declared '$($manifest.artifactFileName)': $listing."
        }

        $artifactSha256 = Get-FileSha256Hex -Path $artifactPath
        if ($artifactSha256 -cne [string]$manifest.artifactSha256) {
            throw "The packaged artifact's SHA-256 '$artifactSha256' does not match the manifest's artifactSha256 '$($manifest.artifactSha256)'."
        }

        # Immutability: every staged file is marked read-only, and later consumers re-hash
        # the artifact immediately before each use.
        foreach ($stagedFile in @(Get-ChildItem -LiteralPath $stageDirectory -Recurse -File)) {
            $stagedFile.IsReadOnly = $true
        }

        return [pscustomobject]@{
            StageDirectory = $stageDirectory
            PackagePath    = $stagedPackagePath
            PackageSha256  = $AuthenticatedPackageSha256
            Manifest       = $manifest
            ManifestPath   = $manifestFiles[0].FullName
            ArtifactPath   = $artifactPath
            ArtifactSha256 = $artifactSha256
            Producer       = $Producer
        }
    }
    catch {
        Remove-RestorePackageStage -StageDirectory $stageDirectory
        throw
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
    #>
    param (
        [Parameter(Mandatory = $true)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    $keyName = if ($DatabaseEngine -eq "mssql") { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }
    $targetDatabaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name $keyName -DefaultValue "edfi_datamanagementservice"

    return $targetDatabaseName.Trim()
}

function Assert-RestoreTargetDatabaseNameSafe {
    <#
    .SYNOPSIS
    Parameter-time target safety: the selected restore target must be a safe identifier,
    never a reserved system database (PostgreSQL postgres/template0/template1, SQL Server
    master/model/msdb/tempdb), and - with -SeparateConfigDatabase - never the dedicated
    Configuration Service database, which restore must never replace. In shared topology the
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
            throw "-SeparateConfigDatabase requires the effective Configuration Service database name (DMS_CONFIG_DATABASE_NAME) so the restore target can be proven distinct from it."
        }
        if ($TargetDatabaseName.Trim().Equals($EffectiveConfigDatabaseName.Trim(), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The restore target database resolves to the dedicated Configuration Service database ('$($EffectiveConfigDatabaseName.Trim())') selected by -SeparateConfigDatabase. Restore never replaces a separate CMS database."
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
    candidate and rethrows. The active .bootstrap workspace is never read or written here; only
    the prepare phases honor the override, and every consuming phase command refuses to run
    while it is set.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Restore-internal staging helper; the restore flow does not expose -WhatIf end to end, and a silent no-op would produce no candidate for the cross-check to validate.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$EnvironmentFile,

        [string]$WorkspaceRoot = "",

        # Test seams: the production prepare scripts stage real packages and need the schema
        # tool; tests substitute recording stubs that honor the same override contract.
        [string]$PrepareSchemaScriptPath = "",

        [string]$PrepareClaimsScriptPath = ""
    )

    if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
        $WorkspaceRoot = $script:RestoreWorkspaceRoot
    }
    if ([string]::IsNullOrWhiteSpace($PrepareSchemaScriptPath)) {
        $PrepareSchemaScriptPath = Join-Path $PSScriptRoot "prepare-dms-schema.ps1"
    }
    if ([string]::IsNullOrWhiteSpace($PrepareClaimsScriptPath)) {
        $PrepareClaimsScriptPath = Join-Path $PSScriptRoot "prepare-dms-claims.ps1"
    }

    $candidateDirectory = Join-Path $WorkspaceRoot "candidate-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $candidateDirectory -Force | Out-Null

    $env:DMS_BOOTSTRAP_ROOT_OVERRIDE = $candidateDirectory
    try {
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
    is discarded and the active tree reused as-is; anything else removes the ENTIRE active tree
    and moves the candidate into place - never a subtree, so no partial/mixed workspace can ever
    exist. A crash between remove and move leaves no active workspace and an intact candidate;
    the next run re-stages from scratch (the accepted failure shape).
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

        Remove-Item -LiteralPath $ActiveBootstrapRoot -Recurse -Force
    }

    $activeParentDirectory = Split-Path -Parent $ActiveBootstrapRoot
    if (-not [string]::IsNullOrWhiteSpace($activeParentDirectory)) {
        New-Item -ItemType Directory -Path $activeParentDirectory -Force | Out-Null
    }
    Move-Item -LiteralPath $CandidateDirectory -Destination $ActiveBootstrapRoot

    return [pscustomobject]@{
        Replaced            = $true
        ActiveBootstrapRoot = $ActiveBootstrapRoot
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
    Assert-DmsComposeProjectStopped, `
    Test-RestoreWorkspaceTreeEqual, `
    Publish-RestoreCandidateWorkspace, `
    Resolve-RestoreTargetDatabaseName, `
    Assert-RestoreTargetDatabaseNameSafe
