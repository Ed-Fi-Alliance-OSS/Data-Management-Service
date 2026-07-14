# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Bootstrap entry script intentionally writes operator progress to the console.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Function names match established bootstrap helper terminology and existing call sites.')]
[CmdletBinding()]
param(
    [string]$EnvironmentFile,
    [string]$BootstrapManifestPath,
    [long[]]$DataStoreId = @(),
    [int[]]$SchoolYear = @(),
    [string]$DmsBaseUrl,
    [ValidateSet("keycloak", "self-contained")]
    [string]$IdentityProvider,
    [ValidateSet("Minimal", "Populated")]
    [string]$SeedTemplate,
    [string]$SeedDataPath,
    [string[]]$AdditionalNamespacePrefix = @()
)

$ErrorActionPreference = "Stop"

Import-Module "$PSScriptRoot/bootstrap-manifest.psm1" -Force
Import-Module "$PSScriptRoot/env-utility.psm1" -Force
Import-Module "$PSScriptRoot/data-standard.psm1" -Force
Import-Module "$PSScriptRoot/../Package-Management.psm1" -Force
Import-Module "$PSScriptRoot/../Dms-Management.psm1" -Force

# Canonical BulkLoadClient version comes from the shared repo pin in eng/Package-Management.psm1.
$script:BulkLoadClientPackageVersion = Get-BulkLoadClientPinnedVersion
$script:DataStandardRefTag = "v5.2.0"

# ODS package metadata that BulkLoadClient should never see. Shared between
# Assert-SeedDataPathHasXml (preflight) and New-SeedWorkspace (staging) so the
# preflight cannot accept a directory the workspace would later filter to empty.
$script:SeedXmlExcludePatterns = @(
    "^Manifest.*\.xml$",
    "^\[Content_Types\]\.xml$",
    "(^|[\\/])_rels([\\/]|$)"
)

function Test-SeedXmlIsLoadable {
    param(
        [string]$RelativePath,
        [string]$FileName
    )
    foreach ($pattern in $script:SeedXmlExcludePatterns) {
        if ($RelativePath -match $pattern -or $FileName -match $pattern) {
            return $false
        }
    }
    return $true
}

function Resolve-SeedBootstrapWorkspaceRelativePath {
    param(
        [string]$RelativePath,
        [string]$ManifestField
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw "Bootstrap manifest field '$(Format-LogSafeText $ManifestField)' must not be empty."
    }

    $normalizedPath = $RelativePath.Replace("\", "/")
    if ([System.IO.Path]::IsPathRooted($RelativePath) -or
        $normalizedPath.StartsWith("/") -or
        $normalizedPath -match "^[A-Za-z]:($|/)") {
        throw "Bootstrap manifest field '$(Format-LogSafeText $ManifestField)' must be relative to the bootstrap workspace: $(Format-LogSafeText $RelativePath)"
    }

    $pathSegments = $normalizedPath.Split([char[]]@('/'), [System.StringSplitOptions]::None)
    $invalidPathSegments = @($pathSegments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq "." -or $_ -eq ".." })
    if ($invalidPathSegments.Count -gt 0) {
        throw "Bootstrap manifest field '$(Format-LogSafeText $ManifestField)' must not contain empty, current, or parent path segments: $(Format-LogSafeText $RelativePath)"
    }

    return Resolve-BootstrapWorkspaceRelativePath -RelativePath $RelativePath -ManifestField $ManifestField
}

function Resolve-SeedApiSchemaWorkspacePath {
    param(
        [string]$RelativePath,
        [string]$ApiSchemaWorkspaceRoot,
        [string]$ManifestField
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw "ApiSchema manifest field '$(Format-LogSafeText $ManifestField)' must not be empty."
    }

    $normalizedPath = $RelativePath.Replace("\", "/")
    if ([System.IO.Path]::IsPathRooted($RelativePath) -or
        $normalizedPath.StartsWith("/") -or
        $normalizedPath -match "^[A-Za-z]:($|/)") {
        throw "ApiSchema manifest field '$(Format-LogSafeText $ManifestField)' must be relative to the staged ApiSchema workspace: $(Format-LogSafeText $RelativePath)"
    }

    $pathSegments = $normalizedPath.Split([char[]]@('/'), [System.StringSplitOptions]::None)
    $invalidPathSegments = @($pathSegments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq "." -or $_ -eq ".." })
    if ($invalidPathSegments.Count -gt 0) {
        throw "ApiSchema manifest field '$(Format-LogSafeText $ManifestField)' must not contain empty, current, or parent path segments: $(Format-LogSafeText $RelativePath)"
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($ApiSchemaWorkspaceRoot)
    $resolvedPath = [System.IO.Path]::GetFullPath((Join-Path $resolvedRoot $normalizedPath))
    $relativeToRoot = [System.IO.Path]::GetRelativePath($resolvedRoot, $resolvedPath).Replace("\", "/")

    if ($relativeToRoot.StartsWith("../", [System.StringComparison]::Ordinal) -or
        $relativeToRoot.Equals("..", [System.StringComparison]::Ordinal) -or
        [System.IO.Path]::IsPathRooted($relativeToRoot)) {
        throw "ApiSchema manifest field '$(Format-LogSafeText $ManifestField)' escapes the staged ApiSchema workspace: $(Format-LogSafeText $RelativePath)"
    }

    return $resolvedPath
}

# ---------------------------------------------------------------------------
# Section A - BulkLoadClient resolver
# ---------------------------------------------------------------------------

function Resolve-BootstrapBulkLoadClient {
    <#
    .SYNOPSIS
    Resolves the repo-pinned BulkLoadClient Console DLL path. Fails fast when resolution is ambiguous or absent.
    #>
    $packageLeaf = "edfi.suite3.bulkloadclient.console.$($script:BulkLoadClientPackageVersion)"
    $candidateCacheRoots = @(
        (Get-Location).Path,
        $PSScriptRoot,
        [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
    )

    $packageDir = $null
    $seenCacheRoots = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($cacheRoot in $candidateCacheRoots) {
        if (-not $seenCacheRoots.Add($cacheRoot)) {
            continue
        }

        $candidatePackageDir = [System.IO.Path]::GetFullPath((Join-Path $cacheRoot ".packages/$packageLeaf"))
        if (Test-Path -LiteralPath $candidatePackageDir -PathType Container) {
            $packageDir = $candidatePackageDir
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($packageDir)) {
        try {
            $packageDir = (Get-BulkLoadClient -PackageVersion $script:BulkLoadClientPackageVersion).Trim()
        }
        catch {
            throw "BulkLoadClient package resolution failed for version $(Format-LogSafeText $script:BulkLoadClientPackageVersion): $(Format-LogSafeText ($_.Exception.Message))"
        }
    }

    if ([string]::IsNullOrWhiteSpace($packageDir)) {
        throw "BulkLoadClient package resolution returned an empty path for version $(Format-LogSafeText $script:BulkLoadClientPackageVersion)."
    }

    # Parse "net<major>.<minor>" to numeric [Version] for deterministic ordering. Lexical
    # descending sort would put "net9.0" above "net10.0", silently skipping newer TFMs.
    $tfmVersion = {
        param($name)
        if ($name -match '^net(\d+)\.(\d+)$') {
            return [Version]::new([int]$Matches[1], [int]$Matches[2])
        }
        return [Version]::new(0, 0)
    }

    # Collect all net* target framework directories under tools/net*/any/
    $candidates = @(
        Get-ChildItem -LiteralPath $packageDir -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match [regex]::Escape([System.IO.Path]::DirectorySeparatorChar) + "tools" + [regex]::Escape([System.IO.Path]::DirectorySeparatorChar) + "net" } |
            Where-Object { $_.Name -eq "any" } |
            Sort-Object -Property { & $tfmVersion $_.Parent.Name } -Descending
    )

    if ($candidates.Count -eq 0) {
        # Fall back to glob pattern like BulkLoad.psm1 does
        $globPattern = Join-Path $packageDir "tools/net*/any/EdFi.BulkLoadClient.Console.dll"
        $resolved = @(Get-Item -Path $globPattern -ErrorAction SilentlyContinue)
        if ($resolved.Count -eq 0) {
            throw "No EdFi.BulkLoadClient.Console.dll found under $(Format-LogSafeText $packageDir) tools/net*/any/."
        }
        if ($resolved.Count -gt 1) {
            # Pick deterministically: sort by parent TFM numerically (latest net* first)
            $resolved = @($resolved | Sort-Object -Property { & $tfmVersion $_.Directory.Parent.Name } -Descending)
        }
        return $resolved[0].FullName
    }

    # Pick the latest net* target (descending sort already done above)
    $chosenAnyDir = $candidates[0].FullName
    $dlls = @(Get-ChildItem -LiteralPath $chosenAnyDir -Filter "EdFi.BulkLoadClient.Console.dll" -ErrorAction SilentlyContinue)
    if ($dlls.Count -eq 0) {
        throw "No EdFi.BulkLoadClient.Console.dll found in $(Format-LogSafeText $chosenAnyDir)."
    }
    if ($dlls.Count -gt 1) {
        throw "Multiple EdFi.BulkLoadClient.Console.dll files found in $(Format-LogSafeText $chosenAnyDir). Cannot determine which to use."
    }

    return $dlls[0].FullName
}

function Assert-BulkLoadClientXmlInterface {
    <#
    .SYNOPSIS
    Verifies that the resolved BulkLoadClient DLL exposes the XML-mode CLI surface this
    bootstrap story depends on. Fails fast before any health check, credential creation,
    or workspace materialization so a broken or incompatible tool is caught early.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$BulkLoadClientDll,

        [scriptblock]$HelpInvoker = $null
    )

    # Required XML-mode flags. The wrapper invokes BulkLoadClient with -d (data dir),
    # -x (staged XSD directory), plus the OAuth/base-URL/key/secret quartet.
    $requiredFlags = @("-b", "-d", "-w", "-k", "-s", "-o", "-x")

    if ($null -ne $HelpInvoker) {
        $helpOutput = & $HelpInvoker $BulkLoadClientDll
        $exitCode = 0
    }
    else {
        $helpOutput = & dotnet $BulkLoadClientDll --help 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -ne 0) {
        throw "BulkLoadClient --help probe failed with exit code $(Format-LogSafeText $exitCode). The pinned package may be incompatible with this bootstrap story."
    }

    $missing = @($requiredFlags | Where-Object { $helpOutput -notmatch "(^|\s)$([regex]::Escape($_))(,|\s)" })
    if ($missing.Count -gt 0) {
        throw "BulkLoadClient XML-mode interface is unavailable. Missing required flags in --help output: $($missing -join ', '). The pinned BulkLoadClient package does not expose the XML loading interface this bootstrap story depends on."
    }
}

# ---------------------------------------------------------------------------
# Section A2 - Core seed data materialization helpers
# ---------------------------------------------------------------------------

function Invoke-SchoolYearTypeRestPrecondition {
    <#
    .SYNOPSIS
    POSTs SchoolYearType rows to DMS for the configured year range. v5.x data standards model
    SchoolYearType as a closed XSD enumeration that is not loadable through any bulk interchange,
    so seed delivery creates these rows directly through the DMS REST API before any BulkLoadClient
    pass. Idempotent: existing rows respond 409 and are tolerated.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper - no -WhatIf end-to-end.')]
    param(
        [Parameter(Mandatory)] [string]$DmsBaseUrl,
        [Parameter(Mandatory)] [string]$Key,
        [Parameter(Mandatory)] [string]$Secret,
        [Parameter(Mandatory)] [string]$OAuthUrl,
        [int]$FirstYear = 1991,
        [int]$LastYear  = 2037,
        [int]$CurrentYear = (Get-CurrentSchoolYear),

        [scriptblock]$WebInvoker = $null
    )

    $tokenBody = ConvertTo-FormBody -Data ([ordered]@{
        client_id     = $Key
        client_secret = $Secret
        grant_type    = "client_credentials"
    })
    $tokenParams = @{
        Uri         = $OAuthUrl
        Method      = "Post"
        Body        = $tokenBody
        ContentType = "application/x-www-form-urlencoded"
    }
    $tokenResp = if ($null -ne $WebInvoker) {
        & $WebInvoker @tokenParams
    }
    else {
        Invoke-RestMethod @tokenParams
    }
    $accessToken = $tokenResp.access_token
    if ([string]::IsNullOrWhiteSpace($accessToken)) {
        throw "SchoolYearType precondition failed: OAuth token endpoint returned empty access_token."
    }

    $endpoint = "$DmsBaseUrl/data/ed-fi/schoolYearTypes"
    $headers = @{
        "Authorization" = "Bearer $accessToken"
        "Content-Type"  = "application/json"
    }

    $created = 0
    $exists  = 0
    for ($year = $FirstYear; $year -le $LastYear; $year++) {
        $body = @{
            schoolYear            = $year
            schoolYearDescription = "$($year - 1)-$year"
            currentSchoolYear     = ($year -eq $CurrentYear)
        } | ConvertTo-Json -Compress

        $requestParams = @{
            Uri                = $endpoint
            Method             = "Post"
            Headers            = $headers
            Body               = $body
            SkipHttpErrorCheck = $true
        }
        $r = if ($null -ne $WebInvoker) {
            & $WebInvoker @requestParams
        }
        else {
            Invoke-WebRequest @requestParams
        }
        switch ($r.StatusCode) {
            201 { $created++ }
            200 { $created++ }
            409 { $exists++ }
            default {
                throw "SchoolYearType precondition failed for year $(Format-LogSafeText $year): HTTP $(Format-LogSafeText $r.StatusCode) - $(Format-LogSafeText $r.Content)"
            }
        }
    }

    Write-Host "SchoolYearType precondition: created=$created  already-existed=$exists  range=$FirstYear-$LastYear"
}

function Resolve-SeedDataStandardRefTag {
    <#
    .SYNOPSIS
    Derives the Ed-Fi-Data-Standard repo ref tag to use for built-in seed materialization from the
    effective Data Standard version recorded in the resolved environment file.

    .DESCRIPTION
    DMS_CONFIG_DATA_STANDARD_VERSION is written by the .env.bootstrap.ds52 / .env.bootstrap.ds61
    local-bootstrap overlays that bootstrap-wrapper.psm1 composes onto the effective environment
    file for -DataStandardVersion 5.2 / 6.1 (see New-DataStandardDerivedEnvFile in
    env-utility.psm1). Falls back to the historical v5.2.0 default when the key is absent or
    blank, so environments without an explicit Data Standard selection are unaffected. Any other
    value throws: the supported set is maintained here as well as in the entry-point ValidateSet
    gates and overlay files, and a version added there but forgotten here must fail at this
    decision point rather than silently materializing v5.2.0 sample XML and XSDs against a
    different-version stack.

    .PARAMETER EnvValues
    Hashtable returned by ReadValuesFromEnvFile.
    #>
    param(
        [hashtable]$EnvValues
    )

    $dataStandardVersion = (Get-EnvValue -EnvValues $EnvValues -Name "DMS_CONFIG_DATA_STANDARD_VERSION" -DefaultValue "").Trim()

    switch ($dataStandardVersion) {
        "" { return "v5.2.0" }
        "5.2" { return "v5.2.0" }
        "6.1" { return "v6.1.0" }
        default {
            throw "Unsupported DMS_CONFIG_DATA_STANDARD_VERSION '$(Format-LogSafeText $dataStandardVersion)'. Supported values: 5.2, 6.1; leave blank for the v5.2.0 default."
        }
    }
}

function Resolve-BootstrapDataStandard {
    <#
    .SYNOPSIS
    Resolves the pinned Ed-Fi-Data-Standard repo tag (default v5.2.0) into the ignored .bootstrap workspace
    and returns its extracted root path. The repo provides the single source of truth for descriptors,
    sample XML, and bulk-load XSDs at a version-consistent tag.
    #>
    $bootstrapRoot = Get-BootstrapRoot
    New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null

    $cacheRoot = Join-Path $bootstrapRoot "data-standard"

    try {
        $repoRoot = Get-DataStandardRepo -RefTag $script:DataStandardRefTag -CacheRoot $cacheRoot
    }
    catch {
        throw "Data Standard repo resolution failed for tag $(Format-LogSafeText $script:DataStandardRefTag): $(Format-LogSafeText ($_.Exception.Message))"
    }

    if (-not (Test-Path -LiteralPath $repoRoot -PathType Container)) {
        throw "Data Standard repo directory not found after fetch: $(Format-LogSafeText $repoRoot)"
    }

    return $repoRoot
}

function Initialize-CoreSeedSource {
    <#
    .SYNOPSIS
    Populates a template subdirectory under DestinationRoot from the v5.2.0 Ed-Fi-Data-Standard repo,
    split into two sibling subdirectories so the orchestrator can sequence the descriptor pass and the
    resource pass independently.

    For Minimal:
      <templateDir>/descriptors/  - every *.xml from <repo>/Descriptors/

    For Populated:
      <templateDir>/descriptors/  - every *.xml from <repo>/Descriptors/
                                    PLUS every *Descriptor.xml from <repo>/Samples/Sample XML/
                                    (sample-only descriptors that the resource XML references but that
                                    are not duplicated under <repo>/Descriptors/; they must load before
                                    the resource pass to avoid foreign-key 409s).
      <templateDir>/resources/    - every non-*Descriptor.xml from <repo>/Samples/Sample XML/

    SchoolYearType rows are NOT staged through XML. v5.x data standards model SchoolYearType as a closed
    XSD enumeration that no Interchange-*.xsd allows at the top level, so seed delivery POSTs the required
    rows via Invoke-SchoolYearTypeRestPrecondition before any BulkLoadClient pass.

    Returns a hashtable with keys: TemplateDirectory, DescriptorsDirectory, ResourcesDirectory (null for Minimal).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper - no -WhatIf end-to-end.')]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Minimal", "Populated")]
        [string]$Template,

        [Parameter(Mandatory)]
        [string]$DataStandardRoot,

        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $templateDir = Join-Path $DestinationRoot $Template.ToLowerInvariant()
    $descriptorsDir = Join-Path $templateDir "descriptors"
    $resourcesDir = Join-Path $templateDir "resources"

    # Recreate the template directory empty
    if (Test-Path -LiteralPath $templateDir) {
        Remove-Item -LiteralPath $templateDir -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Path $descriptorsDir -Force | Out-Null

    $descriptorsSourceDir = Join-Path $DataStandardRoot "Descriptors"
    if (-not (Test-Path -LiteralPath $descriptorsSourceDir -PathType Container)) {
        throw "Data Standard 'Descriptors' directory not found at $(Format-LogSafeText $descriptorsSourceDir)."
    }

    $descriptorFiles = @(
        Get-ChildItem -LiteralPath $descriptorsSourceDir -File -Filter "*.xml" -ErrorAction SilentlyContinue |
            Sort-Object -Property Name
    )
    if ($descriptorFiles.Count -eq 0) {
        throw "Data Standard 'Descriptors' directory has no XML files at $(Format-LogSafeText $descriptorsSourceDir)."
    }

    foreach ($file in $descriptorFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $descriptorsDir $file.Name) -ErrorAction Stop
    }

    $resourcesDirEffective = $null
    if ($Template -eq "Populated") {
        $sampleXmlDir = Join-Path $DataStandardRoot "Samples/Sample XML"
        if (-not (Test-Path -LiteralPath $sampleXmlDir -PathType Container)) {
            throw "Data Standard 'Samples/Sample XML' directory not found at $(Format-LogSafeText $sampleXmlDir)."
        }

        $allSampleXml = @(
            Get-ChildItem -LiteralPath $sampleXmlDir -File -Filter "*.xml" -ErrorAction SilentlyContinue |
                Sort-Object -Property Name
        )

        # Sample-side descriptors (e.g. AncestryEthnicOriginDescriptor, BusRouteDescriptor, ...) live
        # alongside resource XMLs in Samples/Sample XML/ but must be staged into the descriptors tier
        # so they load before any resource that references them. When a name collides with one already
        # copied from Descriptors/ (e.g. DiagnosisDescriptor at v5.2.0), the sample-side payload wins
        # because it carries the richer Populated-tier descriptor values.
        $sampleDescriptorFiles = @($allSampleXml | Where-Object { $_.Name -like "*Descriptor.xml" })
        $existingDescriptorNames = @($descriptorFiles | ForEach-Object { $_.Name })
        $overwriteCount = 0
        foreach ($file in $sampleDescriptorFiles) {
            if ($existingDescriptorNames -contains $file.Name) { $overwriteCount++ }
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $descriptorsDir $file.Name) -Force -ErrorAction Stop
        }
        if ($overwriteCount -gt 0) {
            Write-Host "Populated descriptor staging: $overwriteCount sample-side descriptor file(s) overwrote a same-named file from Descriptors/ (sample-side payload retained)."
        }

        $resourceFiles = @($allSampleXml | Where-Object { $_.Name -notlike "*Descriptor.xml" })
        if ($resourceFiles.Count -eq 0) {
            throw "Data Standard 'Samples/Sample XML' directory has no resource XML files at $(Format-LogSafeText $sampleXmlDir)."
        }

        New-Item -ItemType Directory -Path $resourcesDir -Force | Out-Null
        foreach ($file in $resourceFiles) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $resourcesDir $file.Name) -ErrorAction Stop
        }
        $resourcesDirEffective = $resourcesDir
    }

    return @{
        TemplateDirectory    = $templateDir
        DescriptorsDirectory = $descriptorsDir
        ResourcesDirectory   = $resourcesDirEffective
    }
}

# ---------------------------------------------------------------------------
# Section B - Manifest read + validation
# ---------------------------------------------------------------------------

function Read-SeedManifest {
    <#
    .SYNOPSIS
    Reads and validates the bootstrap manifest for seed delivery. Enforces required sub-keys
    beyond the base Read-BootstrapManifest validation.
    #>
    param(
        [string]$Path
    )

    $manifest = Read-BootstrapManifest -Path $Path
    if ($null -eq $manifest) {
        throw "Bootstrap manifest not found at '$(Format-LogSafeText $Path)'."
    }

    # schema section
    if (-not $manifest.ContainsKey("schema") -or $manifest["schema"] -isnot [System.Collections.IDictionary]) {
        throw "Bootstrap manifest is missing or has a malformed 'schema' section."
    }
    $schema = $manifest["schema"]

    $selectionMode = $schema["selectionMode"]
    if ($selectionMode -notin @("Standard", "ApiSchemaPath")) {
        throw "Bootstrap manifest 'schema.selectionMode' must be Standard or ApiSchemaPath, got: $(Format-LogSafeText $selectionMode)"
    }

    if (-not $schema.ContainsKey("selectedExtensions") -or $schema["selectedExtensions"] -isnot [System.Collections.IList]) {
        throw "Bootstrap manifest 'schema.selectedExtensions' must be an array."
    }

    if (-not $schema.ContainsKey("effectiveSchemaHash") -or [string]::IsNullOrWhiteSpace($schema["effectiveSchemaHash"])) {
        throw "Bootstrap manifest 'schema.effectiveSchemaHash' must be a non-empty string."
    }

    if (-not $schema.ContainsKey("apiSchemaManifestPath") -or $schema["apiSchemaManifestPath"] -isnot [string]) {
        throw "Bootstrap manifest 'schema.apiSchemaManifestPath' must be a string."
    }
    Resolve-BootstrapWorkspaceRelativePath -RelativePath $schema["apiSchemaManifestPath"] -ManifestField "schema.apiSchemaManifestPath" | Out-Null

    # claims section
    if (-not $manifest.ContainsKey("claims") -or $manifest["claims"] -isnot [System.Collections.IDictionary]) {
        throw "Bootstrap manifest is missing or has a malformed 'claims' section."
    }
    $claims = $manifest["claims"]

    if (-not $claims.ContainsKey("directory") -or $claims["directory"] -isnot [string]) {
        throw "Bootstrap manifest 'claims.directory' must be a string."
    }
    Resolve-BootstrapWorkspaceRelativePath -RelativePath $claims["directory"] -ManifestField "claims.directory" | Out-Null

    if (-not $claims.ContainsKey("fingerprint") -or [string]::IsNullOrWhiteSpace($claims["fingerprint"])) {
        throw "Bootstrap manifest 'claims.fingerprint' must be a non-empty string."
    }

    if (-not $claims.ContainsKey("expectedVerificationChecks") -or $claims["expectedVerificationChecks"] -isnot [System.Collections.IList]) {
        throw "Bootstrap manifest 'claims.expectedVerificationChecks' must be an array."
    }

    # seed section
    if (-not $manifest.ContainsKey("seed") -or $manifest["seed"] -isnot [System.Collections.IDictionary]) {
        throw "Bootstrap manifest is missing or has a malformed 'seed' section."
    }
    $seed = $manifest["seed"]

    if (-not $seed.ContainsKey("extensionNamespacePrefixes") -or $seed["extensionNamespacePrefixes"] -isnot [System.Collections.IList]) {
        throw "Bootstrap manifest 'seed.extensionNamespacePrefixes' must be an array of strings."
    }

    foreach ($prefix in @($seed["extensionNamespacePrefixes"])) {
        if ($prefix -isnot [string]) {
            throw "Bootstrap manifest 'seed.extensionNamespacePrefixes' must contain only strings."
        }
    }

    return $manifest
}

# ---------------------------------------------------------------------------
# Section C - Seed source selection
# ---------------------------------------------------------------------------

function Resolve-ExtensionSeedSources {
    <#
    .SYNOPSIS
    Given the manifest's selected extensions and the seed catalog, returns additional extension
    seed source directories or informational warnings for extensions without a catalog entry.
    Only called from the built-in seed path; custom-path mode returns from Resolve-SeedSource
    before this is reached.
    #>
    param(
        [hashtable]$Manifest,
        [string]$CatalogPath
    )

    $extraDirs = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()

    $seedCatalog = @{ extensions = @{} }
    if (Test-Path -LiteralPath $CatalogPath) {
        try {
            $seedCatalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json -AsHashtable
        }
        catch {
            throw "Seed catalog at '$(Format-LogSafeText $CatalogPath)' contains malformed JSON: $(Format-LogSafeText ($_.Exception.Message))"
        }
    }

    $selectedExtensions = @($Manifest["schema"]["selectedExtensions"])
    $catalogDir = Split-Path -Parent $CatalogPath
    $extensions = $seedCatalog["extensions"]

    foreach ($ext in $selectedExtensions) {
        if ($extensions -isnot [System.Collections.IDictionary] -or -not $extensions.ContainsKey($ext)) {
            $warnings.Add("No built-in seed package for extension '$(Format-LogSafeText $ext)' in the seed catalog. Extension seed data was not loaded; supply -SeedDataPath to load extension data.")
            continue
        }

        # Extension is catalog-advertised; resolve and fail-fast if missing.
        $extEntry = $extensions[$ext]
        if ($extEntry -isnot [System.Collections.IDictionary] -or -not $extEntry.ContainsKey("directory")) {
            throw "Seed catalog entry for extension '$(Format-LogSafeText $ext)' is malformed (missing 'directory')."
        }

        $extDir = Join-Path $catalogDir $extEntry["directory"]
        if (-not (Test-Path -LiteralPath $extDir -PathType Container)) {
            throw "Advertised seed package directory for extension '$(Format-LogSafeText $ext)' is missing: $(Format-LogSafeText $extDir)"
        }
        $extraDirs.Add($extDir)
    }

    return @{ ExtraDirectories = @($extraDirs); Warnings = @($warnings) }
}

function Assert-SeedDataPathHasXml {
    <#
    .SYNOPSIS
    Validates a user-supplied -SeedDataPath: must exist as a directory and contain at least
    one candidate *.xml file (recursive files only, matching New-SeedWorkspace's search and exclusion
    rules). Fails fast with a clear message before any health checks, credential creation,
    or BulkLoadClient invocation. Applying the same exclusion as workspace staging avoids
    the case where a folder of pure ODS package metadata (Manifest*.xml, [Content_Types].xml,
    _rels/) passes validation but stages zero loadable files.
    #>
    param(
        [string]$SeedDataPath
    )

    if (-not (Test-Path -LiteralPath $SeedDataPath -PathType Container)) {
        throw "-SeedDataPath does not exist or is not a directory: $(Format-LogSafeText $SeedDataPath)."
    }

    $loadableFiles = @(
        Get-ChildItem -LiteralPath $SeedDataPath -File -Filter "*.xml" -Recurse -ErrorAction SilentlyContinue |
            Where-Object {
                $relPath = $_.FullName.Substring($SeedDataPath.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, '/')
                Test-SeedXmlIsLoadable -RelativePath $relPath -FileName $_.Name
            }
    )
    if ($loadableFiles.Count -eq 0) {
        throw "-SeedDataPath contains no loadable *.xml files after excluding ODS package metadata (Manifest*.xml, [Content_Types].xml, _rels/): $(Format-LogSafeText $SeedDataPath)."
    }
}

function Assert-SeedSelectionInputs {
    <#
    .SYNOPSIS
    Validates user-supplied seed selection flags against the manifest's schema mode.
    Throws immediately on invalid input so that external-asset resolution (BulkLoadClient
    package, pinned data-standard repo tag) does not mask flag-validation failures.
    Pure flag/mode validation only - no IO on user-supplied paths.
    #>
    param(
        [hashtable]$Manifest,
        [string]$SeedTemplate,
        [string]$SeedDataPath,
        [long[]]$DataStoreId = @(),
        [int[]]$SchoolYear = @()
    )

    if (-not [string]::IsNullOrWhiteSpace($SeedTemplate) -and -not [string]::IsNullOrWhiteSpace($SeedDataPath)) {
        throw "-SeedTemplate and -SeedDataPath are mutually exclusive. Provide at most one."
    }

    if ($DataStoreId.Count -gt 0 -and $SchoolYear.Count -gt 0) {
        throw "-DataStoreId and -SchoolYear are mutually exclusive. -SchoolYear drives both data store selection and the /{year} URL segment for BulkLoadClient; -DataStoreId targets a specific instance without per-year URL routing. Pass only one."
    }

    if ($DataStoreId.Count -gt 1) {
        $idList = ($DataStoreId | ForEach-Object { Format-LogSafeText ([string]$_) }) -join ", "
        throw "Multiple -DataStoreId values ($idList) are not supported. The route-unqualified -DataStoreId path issues a single bulk-load pass against the unqualified base URL; a credential authorized for multiple data stores cannot be disambiguated without a route qualifier. Pass one -DataStoreId per run, or use -SchoolYear for multi-data store per-year loading."
    }

    $selectionMode = $Manifest["schema"]["selectionMode"]
    if ($selectionMode -eq "ApiSchemaPath") {
        if (-not [string]::IsNullOrWhiteSpace($SeedTemplate)) {
            throw "Expert mode (manifest schema.selectionMode=ApiSchemaPath) does not support -SeedTemplate. Provide -SeedDataPath instead."
        }
        if ([string]::IsNullOrWhiteSpace($SeedDataPath)) {
            throw "Expert mode (manifest schema.selectionMode=ApiSchemaPath) requires -SeedDataPath. No built-in seed template is available in this mode."
        }
    }
}

function Resolve-SeedSource {
    <#
    .SYNOPSIS
    Implements the seed-source selection rules from the plan. Returns a hashtable describing
    the seed kind, template name, source directory, and any extension warnings.
    BuiltInSourceRoot is the absolute path to the transient seed-source/ workspace directory
    (already populated by Initialize-CoreSeedSource before this call for built-in templates).
    CatalogPath is the absolute path to eng/docker-compose/seed-catalog.json.
    #>
    param(
        [hashtable]$Manifest,
        [string]$SeedTemplate,
        [string]$SeedDataPath,
        [string]$BuiltInSourceRoot,
        [string]$CatalogPath
    )

    Assert-SeedSelectionInputs `
        -Manifest $Manifest `
        -SeedTemplate $SeedTemplate `
        -SeedDataPath $SeedDataPath

    $selectionMode = $Manifest["schema"]["selectionMode"]

    # ApiSchemaPath mode: validation above already ensured -SeedDataPath was provided and
    # -SeedTemplate was not. Confirm the path holds XML and return.
    if ($selectionMode -eq "ApiSchemaPath") {
        Assert-SeedDataPathHasXml -SeedDataPath $SeedDataPath
        return @{
            Kind = "CustomPath"
            Template = $null
            SourceDirectory = $SeedDataPath
            ExtensionWarnings = @()
        }
    }

    # Standard mode rules
    $customPathSupplied = -not [string]::IsNullOrWhiteSpace($SeedDataPath)
    if ($customPathSupplied) {
        Assert-SeedDataPathHasXml -SeedDataPath $SeedDataPath
        Write-Warning "Using -SeedDataPath in Standard mode: built-in core and extension seed source lookup is skipped. The supplied path is used as-is."
        return @{
            Kind = "CustomPath"
            Template = $null
            SourceDirectory = $SeedDataPath
            ExtensionWarnings = @()
        }
    }

    # Standard mode with -SeedTemplate or default; use the materialized workspace directory.
    $effectiveTemplate = if ([string]::IsNullOrWhiteSpace($SeedTemplate)) { "Minimal" } else { $SeedTemplate }
    $builtInDir = Join-Path $BuiltInSourceRoot $effectiveTemplate.ToLowerInvariant()

    if (-not (Test-Path -LiteralPath $builtInDir -PathType Container)) {
        throw "Built-in seed source directory for template '$(Format-LogSafeText $effectiveTemplate)' is missing: $(Format-LogSafeText $builtInDir). Ensure the seed source was materialized via Initialize-CoreSeedSource before calling Resolve-SeedSource."
    }

    # New shape: <builtInDir>/descriptors/ is mandatory; <builtInDir>/resources/ is Populated-only.
    $descriptorsDir = Join-Path $builtInDir "descriptors"
    $resourcesDir   = Join-Path $builtInDir "resources"
    if (-not (Test-Path -LiteralPath $descriptorsDir -PathType Container)) {
        throw "Built-in seed source for template '$(Format-LogSafeText $effectiveTemplate)' is missing the required descriptors/ subdirectory at $(Format-LogSafeText $descriptorsDir)."
    }
    $descriptorXml = @(Get-ChildItem -LiteralPath $descriptorsDir -File -Filter "*.xml" -ErrorAction SilentlyContinue)
    if ($descriptorXml.Count -eq 0) {
        throw "Built-in seed source descriptors/ directory is empty for template '$(Format-LogSafeText $effectiveTemplate)': $(Format-LogSafeText $descriptorsDir)."
    }
    if ($effectiveTemplate -eq "Populated") {
        if (-not (Test-Path -LiteralPath $resourcesDir -PathType Container)) {
            throw "Built-in Populated seed source is missing the required resources/ subdirectory at $(Format-LogSafeText $resourcesDir)."
        }
        $resourceXml = @(Get-ChildItem -LiteralPath $resourcesDir -File -Filter "*.xml" -ErrorAction SilentlyContinue)
        if ($resourceXml.Count -eq 0) {
            throw "Built-in Populated seed source resources/ directory is empty: $(Format-LogSafeText $resourcesDir)."
        }
    }

    $extResult = Resolve-ExtensionSeedSources `
        -Manifest $Manifest `
        -CatalogPath $CatalogPath

    return @{
        Kind                 = "BuiltIn"
        Template             = $effectiveTemplate
        SourceDirectory      = $builtInDir
        DescriptorsDirectory = $descriptorsDir
        ResourcesDirectory   = $(if ($effectiveTemplate -eq "Populated") { $resourcesDir } else { $null })
        ExtensionWarnings    = $extResult.Warnings
        ExtraDirectories     = $extResult.ExtraDirectories
    }
}

# ---------------------------------------------------------------------------
# Section D - Workspace materialization
# ---------------------------------------------------------------------------

function Remove-SeedWorkspace {
    <#
    .SYNOPSIS
    Removes the .bootstrap/seed/ subtree if it exists.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper - no -WhatIf end-to-end.')]
    param(
        [string]$BootstrapRoot
    )

    $seedDir = Join-Path $BootstrapRoot "seed"
    if (Test-Path -LiteralPath $seedDir) {
        Remove-Item -LiteralPath $seedDir -Recurse -Force -ErrorAction Stop
    }
}

function Get-BulkLoadClientInterchangeNameFromXsdFileName {
    param(
        [string]$FileName
    )

    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($FileName)
    if ($baseName -match '^Interchange-(?<name>.+)$') {
        return $Matches.name
    }

    if ($baseName -match '^(?:.+-)?EXTENSION-Interchange-(?<name>.+?)(?:-Extension)?$') {
        return $Matches.name
    }

    return $null
}

function Get-BulkLoadClientInterchangeNames {
    <#
    .SYNOPSIS
    Reads BulkLoadClient interchange names from core and extension Interchange XSD files.
    #>
    param(
        [string]$XsdDirectory
    )

    if (-not (Test-Path -LiteralPath $XsdDirectory -PathType Container)) {
        throw "BulkLoadClient XSD directory does not exist or is not a directory: $(Format-LogSafeText $XsdDirectory)"
    }

    $names = [System.Collections.Generic.List[string]]::new()
    $xsdFiles = @(
        Get-ChildItem -LiteralPath $XsdDirectory -File -Filter "*.xsd" -ErrorAction SilentlyContinue |
            Sort-Object -Property Name
    )

    foreach ($file in $xsdFiles) {
        $name = Get-BulkLoadClientInterchangeNameFromXsdFileName -FileName $file.Name
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $names.Add($name)
        }
    }

    if ($names.Count -eq 0) {
        throw "BulkLoadClient XSD directory contains no core or extension Interchange XSD files: $(Format-LogSafeText $XsdDirectory)"
    }

    return @($names | Sort-Object -Unique)
}

function Resolve-SeedExactInterchangeName {
    param(
        [string]$Name,
        [string[]]$InterchangeNames
    )

    foreach ($interchangeName in $InterchangeNames) {
        if ($Name.Equals($interchangeName, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $interchangeName
        }
    }

    return $null
}

function Resolve-SeedLeafInterchangeName {
    param(
        [string]$LeafName,
        [string[]]$InterchangeNames
    )

    $leafBase = [System.IO.Path]::GetFileNameWithoutExtension($LeafName)
    foreach ($interchangeName in @($InterchangeNames | Sort-Object -Property Length -Descending)) {
        if ($leafBase.Equals($interchangeName, [System.StringComparison]::OrdinalIgnoreCase) -or
            $leafBase.StartsWith("$interchangeName-", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $interchangeName
        }
    }

    return $null
}

function Resolve-SeedXmlRootInterchangeName {
    param(
        [string]$RootName,
        [string[]]$InterchangeNames
    )

    if ($RootName -match '^Interchange(?<name>.+)$') {
        return Resolve-SeedExactInterchangeName -Name $Matches.name -InterchangeNames $InterchangeNames
    }

    return $null
}

function Resolve-SeedXmlSchemaLocationInterchangeName {
    param(
        [string[]]$SchemaLocationValues,
        [string[]]$InterchangeNames
    )

    foreach ($schemaLocation in $SchemaLocationValues) {
        if ([string]::IsNullOrWhiteSpace($schemaLocation)) {
            continue
        }

        $tokens = @($schemaLocation -split '\s+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        foreach ($token in $tokens) {
            $normalizedToken = $token.Replace('\', '/')
            $leaf = ($normalizedToken -split '/')[-1]
            if (-not $leaf.EndsWith(".xsd", [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $name = Get-BulkLoadClientInterchangeNameFromXsdFileName -FileName $leaf
            if ([string]::IsNullOrWhiteSpace($name)) {
                continue
            }

            $interchangeName = Resolve-SeedExactInterchangeName -Name $name -InterchangeNames $InterchangeNames
            if (-not [string]::IsNullOrWhiteSpace($interchangeName)) {
                return $interchangeName
            }
        }
    }

    return $null
}

function Resolve-SeedXmlDeclaredInterchangeName {
    param(
        [string]$FilePath,
        [string[]]$InterchangeNames
    )

    if ([string]::IsNullOrWhiteSpace($FilePath)) {
        return $null
    }

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null

    $reader = $null
    try {
        $reader = [System.Xml.XmlReader]::Create($FilePath, $settings)
        while ($reader.Read()) {
            if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element) {
                continue
            }

            $rootInterchangeName = Resolve-SeedXmlRootInterchangeName -RootName $reader.LocalName -InterchangeNames $InterchangeNames
            if (-not [string]::IsNullOrWhiteSpace($rootInterchangeName)) {
                return $rootInterchangeName
            }

            $schemaLocation = $reader.GetAttribute("schemaLocation", "http://www.w3.org/2001/XMLSchema-instance")
            $noNamespaceSchemaLocation = $reader.GetAttribute("noNamespaceSchemaLocation", "http://www.w3.org/2001/XMLSchema-instance")
            return Resolve-SeedXmlSchemaLocationInterchangeName `
                -SchemaLocationValues @($schemaLocation, $noNamespaceSchemaLocation) `
                -InterchangeNames $InterchangeNames
        }
    }
    catch {
        throw "Seed XML declaration cannot be inspected for BulkLoadClient interchange discovery at $(Format-LogSafeText $FilePath): $(Format-LogSafeText ($_.Exception.Message))"
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
    }

    return $null
}

function Get-SeedFileTargetName {
    <#
    .SYNOPSIS
    Computes the BulkLoadClient-compatible target path for one source XML file.
    Preserves already-compatible names and normalizes wrapper directories so the final
    path is InterchangeName.xml, InterchangeName-*.xml, or InterchangeName/*.xml.
    #>
    param(
        [string]$RelativePath,
        [string]$SourceFilePath,
        [string[]]$InterchangeNames,
        [string]$SourceInterchangeName
    )

    $normalizedRelativePath = $RelativePath.Replace('\', '/')
    $segments = @($normalizedRelativePath -split '/' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($segments.Count -eq 0) {
        throw "Seed XML path is empty."
    }

    $leaf = $segments[$segments.Count - 1]
    if (-not $leaf.EndsWith(".xml", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Seed XML path does not end with .xml: $(Format-LogSafeText $RelativePath)"
    }

    $separator = [string][System.IO.Path]::DirectorySeparatorChar
    if ($segments.Count -gt 1) {
        $parent = $segments[$segments.Count - 2]
        $parentInterchangeName = Resolve-SeedExactInterchangeName -Name $parent -InterchangeNames $InterchangeNames
        if (-not [string]::IsNullOrWhiteSpace($parentInterchangeName)) {
            return "$parentInterchangeName$separator$leaf"
        }
    }

    $leafInterchangeName = Resolve-SeedLeafInterchangeName -LeafName $leaf -InterchangeNames $InterchangeNames
    if (-not [string]::IsNullOrWhiteSpace($leafInterchangeName)) {
        return $leaf
    }

    if (-not [string]::IsNullOrWhiteSpace($SourceInterchangeName)) {
        return "$SourceInterchangeName$separator$leaf"
    }

    $declaredInterchangeName = Resolve-SeedXmlDeclaredInterchangeName -FilePath $SourceFilePath -InterchangeNames $InterchangeNames
    if (-not [string]::IsNullOrWhiteSpace($declaredInterchangeName)) {
        return "$declaredInterchangeName$separator$leaf"
    }

    throw "Seed XML path is not discoverable by BulkLoadClient. Use InterchangeName.xml, InterchangeName-*.xml, or InterchangeName/*.xml: $(Format-LogSafeText $RelativePath)"
}

function Get-SeedWorkspacePlan {
    <#
    .SYNOPSIS
    Computes staged source-to-target copies and validates BulkLoadClient discoverability without
    touching the seed workspace.
    #>
    param(
        [string[]]$SourceDirectories,
        [string[]]$InterchangeNames
    )

    $knownInterchangeNames = @($InterchangeNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    if ($knownInterchangeNames.Count -eq 0) {
        throw "Seed workspace requires at least one BulkLoadClient interchange name."
    }

    $plan = [System.Collections.Generic.List[pscustomobject]]::new()
    $targetNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $collisions = [System.Collections.Generic.List[string]]::new()
    $invalidPaths = [System.Collections.Generic.List[string]]::new()

    foreach ($sourceDir in $SourceDirectories) {
        $leaf = Split-Path -Leaf $sourceDir
        $parent = Split-Path -Parent $sourceDir
        $parentLeaf = if ([string]::IsNullOrWhiteSpace($parent)) { "" } else { Split-Path -Leaf $parent }
        $sourceKey = if ([string]::IsNullOrWhiteSpace($parentLeaf)) { $leaf } else { "${parentLeaf}__${leaf}" }
        $sourceInterchangeName = Resolve-SeedExactInterchangeName -Name $leaf -InterchangeNames $knownInterchangeNames

        $xmlFiles = @(
            Get-ChildItem -LiteralPath $sourceDir -File -Filter "*.xml" -Recurse -ErrorAction SilentlyContinue |
                Where-Object {
                    $relPath = $_.FullName.Substring($sourceDir.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, '/')
                    Test-SeedXmlIsLoadable -RelativePath $relPath -FileName $_.Name
                }
        )

        foreach ($file in $xmlFiles) {
            $relPath = $file.FullName.Substring($sourceDir.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, '/').Replace('\', '/')
            try {
                $targetName = Get-SeedFileTargetName -RelativePath $relPath -SourceFilePath $file.FullName -InterchangeNames $knownInterchangeNames -SourceInterchangeName $sourceInterchangeName
            }
            catch {
                $invalidPaths.Add("Invalid seed XML path from $(Format-LogSafeText $sourceKey):$(Format-LogSafeText $relPath) - $(Format-LogSafeText ($_.Exception.Message))")
                continue
            }

            if (-not $targetNames.Add($targetName)) {
                $collisions.Add("Target name collision: $(Format-LogSafeText $targetName) from $(Format-LogSafeText $sourceKey):$(Format-LogSafeText $relPath)")
            }
            else {
                $plan.Add([pscustomobject]@{
                    Source = $file.FullName
                    TargetName = $targetName
                })
            }
        }
    }

    if ($invalidPaths.Count -gt 0) {
        throw "Seed workspace contains XML files BulkLoadClient cannot discover - aborting before any copy:`n$($invalidPaths -join "`n")"
    }

    if ($collisions.Count -gt 0) {
        throw "Seed workspace would have target path collisions - aborting before any copy:`n$($collisions -join "`n")"
    }

    return @($plan)
}

function Assert-SeedWorkspacePathsAreDiscoverable {
    <#
    .SYNOPSIS
    Validates that seed XML paths can be staged into BulkLoadClient-discoverable target paths.
    #>
    param(
        [string[]]$SourceDirectories,
        [string[]]$InterchangeNames
    )

    $null = Get-SeedWorkspacePlan -SourceDirectories $SourceDirectories -InterchangeNames $InterchangeNames
}

function New-SeedWorkspace {
    <#
    .SYNOPSIS
    Materializes XML seed files from one or more source directories into a deterministic
    BulkLoadClient data directory under .bootstrap/seed/data/. Returns workspace paths.

    Collision detection runs before any copy so the workspace is never partially populated when
    a collision is found. Staging preserves or normalizes files into BulkLoadClient-compatible
    target paths and fails on real target collisions instead of prefixing every file.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper - no -WhatIf end-to-end.')]
    param(
        [string]$BootstrapRoot,
        [string[]]$SourceDirectories,
        [string[]]$InterchangeNames
    )

    $seedRoot = Join-Path $BootstrapRoot "seed"
    $dataDir = Join-Path $seedRoot "data"
    $workingDir = Join-Path $seedRoot "working"

    # Wipe and recreate from scratch on every run
    if (Test-Path -LiteralPath $seedRoot) {
        Remove-Item -LiteralPath $seedRoot -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    New-Item -ItemType Directory -Path $workingDir -Force | Out-Null

    # Collect all planned copies first so collision detection can run before any copy.
    # Exclusion rules live in $script:SeedXmlExcludePatterns so Assert-SeedDataPathHasXml
    # filters the same way at preflight.
    $plan = Get-SeedWorkspacePlan -SourceDirectories $SourceDirectories -InterchangeNames $InterchangeNames

    # All clear; perform the copies.
    $stagedFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $plan) {
        $dest = Join-Path $dataDir $entry.TargetName
        $destParent = Split-Path -Parent $dest
        if (-not [string]::IsNullOrWhiteSpace($destParent) -and -not (Test-Path -LiteralPath $destParent)) {
            New-Item -ItemType Directory -Path $destParent -Force | Out-Null
        }
        Copy-Item -LiteralPath $entry.Source -Destination $dest -ErrorAction Stop
        $stagedFiles.Add($dest)
    }

    return @{
        DataDirectory = $dataDir
        WorkingDirectory = $workingDir
        StagedFiles = @($stagedFiles)
    }
}

# ---------------------------------------------------------------------------
# Section E - Credential, health, selector, XSD, and invocation helpers (W3)
# ---------------------------------------------------------------------------

function Wait-DmsHealthy {
    <#
    .SYNOPSIS
    Polls GET {DmsBaseUrl}/health every 2 seconds until the endpoint returns HTTP 200 or
    the timeout elapses. Throws with the URL on timeout (no secrets in the message).
    #>
    param(
        [string]$DmsBaseUrl,
        [int]$TimeoutSeconds = 60
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    $healthUrl = "$DmsBaseUrl/health"

    while ($true) {
        try {
            $response = Invoke-WebRequest -Uri $healthUrl -Method Get -TimeoutSec 5 -ErrorAction Stop
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            # Not healthy yet; continue polling.
            $null = $_
        }

        if ([datetime]::UtcNow -ge $deadline) {
            throw "DMS health check timed out after $(Format-LogSafeText $TimeoutSeconds) seconds. Endpoint: $(Format-LogSafeText $healthUrl)"
        }

        Start-Sleep -Seconds 2
    }
}

function Resolve-SeedTargetDataStores {
    <#
    .SYNOPSIS
    Resolves which data store IDs to target for seed delivery.
    Rules (in priority order):
      - Explicit -DataStoreId: each id is looked up; rejected when the instance carries any
        dataStoreContexts entries because DMS-1152 does not compose arbitrary qualifier
        URLs from a bare id (use -SchoolYear or another selector once added).
      - Explicit -SchoolYear: list instances; match by dataStoreContexts[contextKey=schoolYear, contextValue=$year].
        A year with zero matches throws with the year named.
      - Neither selector with exactly one instance: auto-select.
      - Neither selector with zero instances: throw with guidance.
      - Neither selector with multiple: throw with (id, name) list and instructions.
    Returns @{ DataStoreIds = [long[]] }
    #>
    param(
        [string]$CmsUrl,
        [string]$AccessToken,
        [long[]]$DataStoreId = @(),
        [int[]]$SchoolYear = @(),
        [string]$Tenant = ""
    )

    # CMS instance list is needed for every branch now; explicit -DataStoreId must verify the
    # instance exists and has zero route qualifiers, because DMS rejects requests whose URL
    # qualifier count does not match the instance's dataStoreContexts.
    $instances = @(Get-DataStore -CmsUrl $CmsUrl -AccessToken $AccessToken -Tenant $Tenant)

    if ($DataStoreId.Count -gt 0) {
        foreach ($explicitId in $DataStoreId) {
            $matchedInstance = $instances | Where-Object { [long]$_.id -eq [long]$explicitId } | Select-Object -First 1
            if ($null -eq $matchedInstance) {
                throw "Data store $(Format-LogSafeText $explicitId) was not found in CMS for tenant '$(Format-LogSafeText $Tenant)'. Verify the data store id and tenant scope before re-running."
            }

            $routeContexts = @()
            if ($matchedInstance.dataStoreContexts -is [System.Collections.IEnumerable]) {
                $routeContexts = @($matchedInstance.dataStoreContexts)
            }
            if ($routeContexts.Count -gt 0) {
                $contextKeys = ($routeContexts | ForEach-Object { [string]$_.contextKey }) -join ", "
                throw "Data store $(Format-LogSafeText $explicitId) carries $($routeContexts.Count) route context(s) ($(Format-LogSafeText $contextKeys)). -DataStoreId targets only route-unqualified data stores in DMS-1152; use -SchoolYear (or the matching selector) so the request URL can include the required qualifier segments."
            }
        }
        return @{
            DataStoreIds = [long[]]$DataStoreId
        }
    }

    if ($SchoolYear.Count -gt 0) {
        $matchedIds = [System.Collections.Generic.List[long]]::new()
        foreach ($year in $SchoolYear) {
            $matchedInstances = @($instances | Where-Object {
                $inst = $_
                $hasMatch = $false
                if ($inst.dataStoreContexts -is [System.Collections.IEnumerable]) {
                    foreach ($rc in @($inst.dataStoreContexts)) {
                        if ($rc.contextKey -eq "schoolYear" -and $rc.contextValue -eq [string]$year) {
                            $hasMatch = $true
                            break
                        }
                    }
                }
                $hasMatch
            })

            if ($matchedInstances.Count -eq 0) {
                throw "No data store found with route context schoolYear=$(Format-LogSafeText $year). Ensure data stores were created before running seed delivery."
            }
            if ($matchedInstances.Count -gt 1) {
                $ids = ($matchedInstances | ForEach-Object { $_.id }) -join ", "
                throw "Multiple data stores found with route context schoolYear=$(Format-LogSafeText $year) (data store ids: $(Format-LogSafeText $ids)). Use -DataStoreId to disambiguate, or clean up duplicate CMS state before re-running."
            }
            $matchedIds.Add([long]$matchedInstances[0].id)
        }

        $uniqueIds = @($matchedIds | Sort-Object -Unique)
        return @{
            DataStoreIds = [long[]]$uniqueIds
        }
    }

    # No selector; auto-select if exactly one instance.
    if ($instances.Count -eq 0) {
        throw "No DMS data stores found in CMS. Run configure-local-data-store.ps1 to create data stores, then re-run seed delivery."
    }

    if ($instances.Count -gt 1) {
        $listing = ($instances | ForEach-Object {
            "  id=$(Format-LogSafeText $_.id) name=$(Format-LogSafeText $_.name)"
        }) -join "`n"
        throw "Multiple data stores exist; cannot auto-select. Pass -DataStoreId or -SchoolYear to target specific data stores:`n$listing"
    }

    $soleDataStore = $instances[0]
    $soleRouteContexts = @()
    if ($soleDataStore.dataStoreContexts -is [System.Collections.IEnumerable]) {
        $soleRouteContexts = @($soleDataStore.dataStoreContexts)
    }
    if ($soleRouteContexts.Count -gt 0) {
        $contextKeys = ($soleRouteContexts | ForEach-Object { [string]$_.contextKey }) -join ", "
        throw "Single data store $(Format-LogSafeText $soleDataStore.id) carries $($soleRouteContexts.Count) route context(s) ($(Format-LogSafeText $contextKeys)). Auto-select cannot compose the required URL qualifier segments. Pass -SchoolYear (or the matching selector) to target this data store with the correct route."
    }

    return @{
        DataStoreIds = [long[]]@([long]$soleDataStore.id)
    }
}

function Get-SeedXsdDirectory {
    <#
    .SYNOPSIS
    Reads the ApiSchema manifest referenced by the bootstrap manifest and copies *.xsd files
    from each project's xsdDirectory into the workspace xsd/ subdirectory.
    Fails fast when no xsdDirectory is found in any project.
    Returns the absolute path to the consolidated xsd directory.
    #>
    param(
        [hashtable]$Manifest,
        [string]$WorkspaceRoot,
        [string]$BootstrapRoot,
        [switch]$ExtensionProjectsOnly
    )

    $relApiSchemaManifestPath = Resolve-SeedBootstrapWorkspaceRelativePath `
        -RelativePath $Manifest["schema"]["apiSchemaManifestPath"] `
        -ManifestField "schema.apiSchemaManifestPath"
    $absApiSchemaManifestPath = [System.IO.Path]::GetFullPath((Join-Path $BootstrapRoot $relApiSchemaManifestPath))

    if (-not (Test-Path -LiteralPath $absApiSchemaManifestPath -PathType Leaf)) {
        throw "ApiSchema manifest not found at $(Format-LogSafeText $absApiSchemaManifestPath). Run prepare-dms-schema.ps1 first."
    }

    try {
        $apiSchemaManifest = Get-Content -LiteralPath $absApiSchemaManifestPath -Raw | ConvertFrom-Json -AsHashtable
    }
    catch {
        throw "ApiSchema manifest contains malformed JSON: $(Format-LogSafeText ($_.Exception.Message))"
    }

    $apiSchemaWorkspaceRoot = Split-Path -Parent $absApiSchemaManifestPath

    # Wipe and recreate the XSD destination on every entry so a re-prepared schema or branch
    # switch can't quietly reuse stale XSDs with the same filename from a previous run.
    $xsdDestDir = Join-Path $WorkspaceRoot "xsd"
    if (Test-Path -LiteralPath $xsdDestDir) {
        Remove-Item -LiteralPath $xsdDestDir -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Path $xsdDestDir -Force | Out-Null

    $projects = $null
    if ($apiSchemaManifest -is [System.Collections.IDictionary] -and $apiSchemaManifest.ContainsKey("projects")) {
        $projects = $apiSchemaManifest["projects"]
    }
    elseif ($apiSchemaManifest -is [System.Collections.IList]) {
        $projects = $apiSchemaManifest
    }

    # Collect planned copies first so collisions across projects can be detected before any IO,
    # mirroring New-SeedWorkspace's collision contract. Two projects exporting the same XSD leaf
    # name into the flattened directory would otherwise silently overwrite each other.
    $xsdPlan = [System.Collections.Generic.List[pscustomobject]]::new()
    $xsdTargetNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $xsdSourceDirectories = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $xsdCollisions = [System.Collections.Generic.List[string]]::new()

    if ($null -ne $projects) {
        $selectedExtensionNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        if ($Manifest["schema"].ContainsKey("selectedExtensions")) {
            foreach ($selectedExtension in @($Manifest["schema"]["selectedExtensions"])) {
                if (-not [string]::IsNullOrWhiteSpace($selectedExtension)) {
                    $null = $selectedExtensionNames.Add([string]$selectedExtension)
                }
            }
        }

        foreach ($project in @($projects)) {
            $xsdDir = $null
            $projectName = $null
            $projectEndpointName = $null
            $isExtensionProject = $false
            if ($project -is [System.Collections.IDictionary]) {
                if ($project.ContainsKey("xsdDirectory")) { $xsdDir = $project["xsdDirectory"] }
                if ($project.ContainsKey("projectName")) { $projectName = $project["projectName"] }
                if ($project.ContainsKey("projectEndpointName")) { $projectEndpointName = $project["projectEndpointName"] }
                if ($project.ContainsKey("isExtensionProject")) { $isExtensionProject = [bool]$project["isExtensionProject"] }
            }
            else {
                if ($null -ne $project.xsdDirectory) { $xsdDir = $project.xsdDirectory }
                if ($null -ne $project.projectName) { $projectName = $project.projectName }
                if ($null -ne $project.projectEndpointName) { $projectEndpointName = $project.projectEndpointName }
                if ($null -ne $project.isExtensionProject) { $isExtensionProject = [bool]$project.isExtensionProject }
            }

            if (-not $isExtensionProject -and -not [string]::IsNullOrWhiteSpace($projectEndpointName)) {
                $isExtensionProject = $selectedExtensionNames.Contains([string]$projectEndpointName)
            }

            if ($ExtensionProjectsOnly -and -not $isExtensionProject) {
                continue
            }

            if ([string]::IsNullOrWhiteSpace($xsdDir)) {
                continue
            }

            # xsdDirectory entries are relative to the staged ApiSchema workspace, not to
            # the root .bootstrap manifest directory.
            $declaredXsdDir = [string]$xsdDir
            $xsdDir = Resolve-SeedApiSchemaWorkspacePath `
                -RelativePath $declaredXsdDir `
                -ApiSchemaWorkspaceRoot $apiSchemaWorkspaceRoot `
                -ManifestField "projects[].xsdDirectory"

            $projectLabel = if ([string]::IsNullOrWhiteSpace($projectName)) { $xsdDir } else { $projectName }

            if (-not (Test-Path -LiteralPath $xsdDir -PathType Container)) {
                throw "ApiSchema manifest project '$(Format-LogSafeText $projectLabel)' advertises xsdDirectory '$(Format-LogSafeText $xsdDir)' but that directory does not exist. Re-run prepare-dms-schema.ps1, or check for a stale .bootstrap workspace."
            }

            if (-not $xsdSourceDirectories.Add($xsdDir)) {
                continue
            }

            $xsdFiles = @(Get-ChildItem -LiteralPath $xsdDir -Filter "*.xsd" -File -Recurse -ErrorAction SilentlyContinue)
            foreach ($xsdFile in $xsdFiles) {
                if (-not $xsdTargetNames.Add($xsdFile.Name)) {
                    $xsdCollisions.Add("Target name collision: $(Format-LogSafeText $xsdFile.Name) from project $(Format-LogSafeText $projectLabel) ($(Format-LogSafeText $xsdFile.FullName))")
                }
                else {
                    $xsdPlan.Add([pscustomobject]@{
                        Source = $xsdFile.FullName
                        TargetName = $xsdFile.Name
                    })
                }
            }
        }
    }

    if ($xsdCollisions.Count -gt 0) {
        throw "Staged XSD files would have deterministic name collisions - aborting before any copy:`n$($xsdCollisions -join "`n")"
    }

    if ($xsdPlan.Count -eq 0) {
        throw "No staged XSD files found in any project's xsdDirectory from $(Format-LogSafeText $absApiSchemaManifestPath). Verify the ApiSchema manifest projects include an xsdDirectory entry and that prepare-dms-schema.ps1 completed successfully."
    }

    foreach ($entry in $xsdPlan) {
        Copy-Item -LiteralPath $entry.Source -Destination (Join-Path $xsdDestDir $entry.TargetName) -ErrorAction Stop
    }

    return $xsdDestDir
}

function Copy-SeedXsdFilesIntoDirectory {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper - no -WhatIf end-to-end.')]
    param(
        [string]$SourceDirectory,
        [string]$DestinationDirectory,
        [switch]$SkipExisting
    )

    $xsdFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -Filter "*.xsd" -File -Recurse -ErrorAction SilentlyContinue)
    foreach ($xsdFile in $xsdFiles) {
        $destination = Join-Path $DestinationDirectory $xsdFile.Name
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            if ($SkipExisting) {
                continue
            }

            throw "Staged XSD files would have deterministic name collisions: $(Format-LogSafeText $xsdFile.Name)"
        }

        Copy-Item -LiteralPath $xsdFile.FullName -Destination $destination -ErrorAction Stop
    }
}

function New-BuiltInSeedXsdDirectory {
    <#
    .SYNOPSIS
    Resolves the XSD directory used by built-in seed loading. Core seed XML remains validated
    against the pinned Ed-Fi-Data-Standard tag, while catalog-backed extension seed packages
    add extension XSDs from the staged ApiSchema workspace.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper - no -WhatIf end-to-end.')]
    param(
        [string]$DataStandardXsdDirectory,
        [hashtable]$Manifest,
        [string]$BootstrapRoot,
        [switch]$IncludeExtensionXsds
    )

    if (-not (Test-Path -LiteralPath $DataStandardXsdDirectory -PathType Container)) {
        throw "Bulk XSD directory not found in Ed-Fi-Data-Standard tag $(Format-LogSafeText $script:DataStandardRefTag): $(Format-LogSafeText $DataStandardXsdDirectory)"
    }

    if (-not $IncludeExtensionXsds) {
        return $DataStandardXsdDirectory
    }

    $extensionXsdWorkspaceRoot = Join-Path $BootstrapRoot "extension-xsd"
    $extensionXsdDir = Get-SeedXsdDirectory `
        -Manifest $Manifest `
        -WorkspaceRoot $extensionXsdWorkspaceRoot `
        -BootstrapRoot $BootstrapRoot `
        -ExtensionProjectsOnly

    $combinedXsdDir = Join-Path $BootstrapRoot "xsd"
    if (Test-Path -LiteralPath $combinedXsdDir) {
        Remove-Item -LiteralPath $combinedXsdDir -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Path $combinedXsdDir -Force | Out-Null

    Copy-SeedXsdFilesIntoDirectory -SourceDirectory $DataStandardXsdDirectory -DestinationDirectory $combinedXsdDir
    Copy-SeedXsdFilesIntoDirectory -SourceDirectory $extensionXsdDir -DestinationDirectory $combinedXsdDir -SkipExisting

    return $combinedXsdDir
}

function Invoke-BulkLoadClient {
    <#
    .SYNOPSIS
    Invokes the BulkLoadClient Console DLL with the required bootstrap flags.
    Passes stdout/stderr through directly (no swallowing). Throws on non-zero exit code.
    The $Invoker scriptblock seam allows tests to capture args without running dotnet.
    XSD validation is left enabled because the bootstrap path sources both sample XML and
    bulk-load XSDs from the same Ed-Fi-Data-Standard tag (v5.2.0 or v6.1.0, resolved by
    Resolve-SeedDataStandardRefTag), so the two are version-consistent by construction.

    Connection/concurrency/retry tuning is required: DMS's Polly circuit breaker
    (FailureRatio=0.01, MinimumThroughput=2, 10s sampling window, 30s break) trips almost
    immediately under the BulkLoadClient's unbounded default concurrency, causing all
    subsequent requests to 500 (BrokenCircuit) and retry-storming the rate limiter
    (PermitLimit=5000/10s, QueueLimit=0) into a flood of 429s. The reference values used
    by the CI bulk-load path (eng/bulkLoad/modules/BulkLoad.psm1 -c 100 -l 500 -t 50)
    prove the approach; bootstrap seed delivery uses conservative equivalents for the
    relational backend.
    #>
    param(
        [string]$BulkLoadClientDll,
        [string]$DmsBaseUrl,
        [string]$DataDirectory,
        [string]$WorkingDirectory,
        [string]$Key,
        [string]$Secret,
        [string]$OAuthUrl,
        [string]$XsdDirectory,
        [scriptblock]$Invoker = $null
    )

    # Conservative tuning for the relational backend's circuit-breaker sensitivity and
    # rate-limiter headroom (PermitLimit=5000/10s, QueueLimit=0):
    # -c  max concurrent HTTP connections
    # -l  max simultaneous API requests (same as -c but guards a different internal queue)
    # -t  task buffer capacity
    # -r  per-resource retry count (tolerate transient 500s before a circuit trip)
    # Low concurrency prevents exhausting the 5000/10s rate-limit window on large files
    # such as StudentTranscript.xml (~15k CourseTranscript records). Reference CI values
    # in eng/bulkLoad/modules/BulkLoad.psm1 use -c 100 -l 500 for non-rate-limited paths.
    $connectionLimit  = 10
    $maxRequests      = 10
    $taskCapacity     = 5
    $retries          = 2

    $bulkLoadArgs = @(
        "-b", $DmsBaseUrl,
        "-d", $DataDirectory,
        "-w", $WorkingDirectory,
        "-k", $Key,
        "-s", $Secret,
        "-o", $OAuthUrl,
        "-x", $XsdDirectory,
        "-c", [string]$connectionLimit,
        "-l", [string]$maxRequests,
        "-t", [string]$taskCapacity,
        "-r", [string]$retries
    )

    if ($null -ne $Invoker) {
        $exitCode = & $Invoker $BulkLoadClientDll $bulkLoadArgs
        if ($exitCode -ne 0) {
            throw "BulkLoadClient exited with non-zero code: $(Format-LogSafeText $exitCode)"
        }
        return $true
    }

    & dotnet $BulkLoadClientDll @bulkLoadArgs
    if ($LASTEXITCODE -ne 0) {
        throw "BulkLoadClient exited with non-zero code: $(Format-LogSafeText $LASTEXITCODE). Seed workspace retained for inspection."
    }
}

# ---------------------------------------------------------------------------
# Section F - Top-level orchestrator
# ---------------------------------------------------------------------------

# Skip orchestration when dot-sourced (tests load helpers without running the pipeline).
if ($MyInvocation.InvocationName -eq '.') { return }

# Resolve environment file through the shared resolver (explicit path, else .env - seeded
# once from .env.example when absent, so the example itself is never consumed at runtime).
$EnvironmentFile = Resolve-LocalSettingsEnvironmentFile -Path $EnvironmentFile

# Derive the pinned Ed-Fi-Data-Standard ref tag from the resolved environment file now, before
# any BuiltIn seed-source materialization reads $script:DataStandardRefTag (see
# Resolve-BootstrapDataStandard below). Overrides the v5.2.0 default declared above when the
# environment selects Data Standard 6.1.
$dataStandardEnvValues = ReadValuesFromEnvFile -EnvironmentFile $EnvironmentFile
$script:DataStandardRefTag = Resolve-SeedDataStandardRefTag -EnvValues $dataStandardEnvValues

# Resolve bootstrap manifest path AND its parent workspace ("bootstrap root").
# When -BootstrapManifestPath is supplied externally, the manifest's parent directory
# becomes the bootstrap root so downstream resolution (seed source materialization,
# XSD path joins from the ApiSchema manifest's relative paths) targets the same
# workspace the manifest came from. Without this coupling, an external -BootstrapManifestPath
# would read content from one location but resolve relative paths against ./.bootstrap/.
if ([string]::IsNullOrWhiteSpace($BootstrapManifestPath)) {
    $bootstrapRoot = Get-BootstrapRoot
    $BootstrapManifestPath = Join-Path $bootstrapRoot "bootstrap-manifest.json"
}
else {
    $BootstrapManifestPath = [System.IO.Path]::GetFullPath($BootstrapManifestPath)
    $bootstrapRoot = Split-Path -Parent $BootstrapManifestPath
}

# Fail fast: validate manifest before anything else
$manifest = Read-SeedManifest -Path $BootstrapManifestPath

# Fail fast on seed-flag/manifest-mode mismatches BEFORE resolving any external assets
# (BulkLoadClient package, GitHub-tag data-standard repo) so that invalid user input
# surfaces with its actual error rather than being masked by package/network failures.
Assert-SeedSelectionInputs `
    -Manifest $manifest `
    -SeedTemplate $SeedTemplate `
    -SeedDataPath $SeedDataPath `
    -DataStoreId $DataStoreId `
    -SchoolYear $SchoolYear

# Validate the local seed path now too; a typo in -SeedDataPath should surface here, not
# behind a NuGet/feed/dotnet failure during BulkLoadClient resolution. Resolve-SeedSource
# will repeat this check downstream; the duplicate is intentional and cheap.
if (-not [string]::IsNullOrWhiteSpace($SeedDataPath)) {
    Assert-SeedDataPathHasXml -SeedDataPath $SeedDataPath
}

# Fail fast: resolve BulkLoadClient BEFORE credentials or workspace (Task 1 ordering)
Write-Host "Resolving BulkLoadClient $(Format-LogSafeText $script:BulkLoadClientPackageVersion)..."
$bulkLoadClientDll = Resolve-BootstrapBulkLoadClient
Write-Host "BulkLoadClient resolved: $(Format-LogSafeText $bulkLoadClientDll)"

# Fail fast: probe the CLI surface to confirm the XML loading interface is available
Write-Host "Verifying BulkLoadClient XML-mode interface..."
Assert-BulkLoadClientXmlInterface -BulkLoadClientDll $bulkLoadClientDll
Write-Host "BulkLoadClient XML-mode interface verified."

# Resolve catalog and ignored built-in source workspace paths. $bootstrapRoot was
# resolved alongside $BootstrapManifestPath above so external manifest paths route
# downstream IO to the same workspace.
$catalogPath = Join-Path $PSScriptRoot "seed-catalog.json"
$seedSourceRoot = Join-Path $bootstrapRoot "seed-source"

# Materialize built-in seed source from pinned Ed-Fi-Data-Standard repo tag when needed
$dataStandardRoot = $null
$needsBuiltIn = ($manifest["schema"]["selectionMode"] -eq "Standard") -and ([string]::IsNullOrWhiteSpace($SeedDataPath))
if ($needsBuiltIn) {
    $effectiveTemplate = if ($SeedTemplate) { $SeedTemplate } else { "Minimal" }
    New-Item -ItemType Directory -Path $seedSourceRoot -Force | Out-Null
    Write-Host "Resolving Ed-Fi-Data-Standard $(Format-LogSafeText $script:DataStandardRefTag)..."
    $dataStandardRoot = Resolve-BootstrapDataStandard
    Write-Host "Materializing built-in $(Format-LogSafeText $effectiveTemplate) seed source into ignored .bootstrap workspace..."
    Initialize-CoreSeedSource -Template $effectiveTemplate -DataStandardRoot $dataStandardRoot -DestinationRoot $seedSourceRoot
    Write-Host "Built-in seed source materialized."
}

# Seed source resolution (Task 2)
$seedSource = Resolve-SeedSource `
    -Manifest $manifest `
    -SeedTemplate $SeedTemplate `
    -SeedDataPath $SeedDataPath `
    -BuiltInSourceRoot $seedSourceRoot `
    -CatalogPath $catalogPath

foreach ($warning in $seedSource.ExtensionWarnings) {
    Write-Warning $warning
}

# Determine the tier list. Built-in templates always run as a descriptor tier; Populated additionally
# runs a resource tier. Custom -SeedDataPath runs as a single "custom" tier preserving the original
# single-pass shape. Extension extras attach to the resource pass for built-in Populated; otherwise
# they attach to the single tier.
$extensionExtraDirs = @()
if ($seedSource.ContainsKey("ExtraDirectories")) {
    $extensionExtraDirs = @($seedSource.ExtraDirectories)
}

$tiers = [System.Collections.Generic.List[hashtable]]::new()
if ($seedSource.Kind -eq "BuiltIn") {
    if ($null -ne $seedSource.ResourcesDirectory) {
        # Populated: resources tier carries catalog-backed extension extras so they load after core
        # descriptors + resources.
        $tiers.Add(@{
            Name             = "descriptors"
            SourceDirectory  = $seedSource.DescriptorsDirectory
            ExtraDirectories = @()
        })
        $tiers.Add(@{
            Name             = "resources"
            SourceDirectory  = $seedSource.ResourcesDirectory
            ExtraDirectories = $extensionExtraDirs
        })
    }
    else {
        # Minimal: only the descriptors tier exists, so it carries catalog-backed extension extras.
        # Without this, ExtraDirectories would be silently dropped for Minimal even when seed-catalog.json
        # declared extension seed data.
        $tiers.Add(@{
            Name             = "descriptors"
            SourceDirectory  = $seedSource.DescriptorsDirectory
            ExtraDirectories = $extensionExtraDirs
        })
    }
}
else {
    $tiers.Add(@{
        Name             = "custom"
        SourceDirectory  = $seedSource.SourceDirectory
        ExtraDirectories = $extensionExtraDirs
    })
}

# Resolve the XSD source BEFORE any CMS mutation (SeedLoader credentials, vendor, application).
# A missing-XSD failure here used to leave dangling CMS state because credential creation ran first.
#   BuiltIn (Minimal/Populated): use the pinned data-standard repo's Schemas/Bulk/ so XML and XSD
#     versions agree by construction. BulkLoadClient validation stays ON.
#   CustomPath (-SeedDataPath / ApiSchemaPath mode): fall back to ApiSchema-manifest-staged XSDs;
#     the developer's payload is expected to validate against their own schema.
$runSchoolYearTypePrecondition = ($seedSource.Kind -eq "BuiltIn")
if ($seedSource.Kind -eq "BuiltIn") {
    if ([string]::IsNullOrWhiteSpace($dataStandardRoot)) {
        throw "Internal error: BuiltIn seed source requires dataStandardRoot to be resolved before XSD resolution."
    }
    $resolvedXsdDir = New-BuiltInSeedXsdDirectory `
        -DataStandardXsdDirectory (Join-Path $dataStandardRoot "Schemas/Bulk") `
        -Manifest $manifest `
        -BootstrapRoot $bootstrapRoot `
        -IncludeExtensionXsds:($extensionExtraDirs.Count -gt 0)
}
else {
    $resolvedXsdDir = Get-SeedXsdDirectory `
        -Manifest $manifest `
        -WorkspaceRoot $bootstrapRoot `
        -BootstrapRoot $bootstrapRoot
}

# Validate the same staged target-path contract that New-SeedWorkspace will use, but do it
# before DMS health checks and CMS SeedLoader credential creation so bad custom seed paths
# do not leave vendor/application state behind.
$preflightInterchangeNames = Get-BulkLoadClientInterchangeNames -XsdDirectory $resolvedXsdDir
foreach ($tier in $tiers) {
    $tierSourceDirs = @($tier.SourceDirectory) + @($tier.ExtraDirectories)
    Write-Host "Preflighting seed workspace materialization for $(Format-LogSafeText $tier.Name) tier..."
    Assert-SeedWorkspacePathsAreDiscoverable -SourceDirectories $tierSourceDirs -InterchangeNames $preflightInterchangeNames
}

# --- Step 1: Env resolution ---
$envValues = ReadValuesFromEnvFile -EnvironmentFile $EnvironmentFile
$cmsUrl = Resolve-CmsBaseUrl -EnvValues $envValues
$identityProvider = Resolve-IdentityProvider -EnvValues $envValues -OverrideProvider $IdentityProvider
$tenant = if ($envValues.ContainsKey("CONFIG_SERVICE_TENANT")) { [string]$envValues.CONFIG_SERVICE_TENANT } else { "" }
$resolvedDmsBaseUrl = if (-not [string]::IsNullOrWhiteSpace($DmsBaseUrl)) {
    $DmsBaseUrl
}
else {
    Resolve-DockerLocalDmsBaseUrl -EnvValues $envValues
}

# --- Step 2: DMS health check (before credentials) ---
# DMS maps /health only at the unqualified root (see HealthCheckEndpointModule.cs), so this
# probe cannot be extended to per-year `{base}/{year}/health` URLs; those 404 and stall. A
# per-year route-context preflight would need a different endpoint (e.g. `/{year}/metadata`)
# and is tracked as a follow-up; for now, route-context misconfig surfaces at the first
# per-year POST in Step 7+.
Write-Host "Checking DMS health at $(Format-LogSafeText $resolvedDmsBaseUrl)..."
Wait-DmsHealthy -DmsBaseUrl $resolvedDmsBaseUrl

# --- Step 3: Namespace prefix list ---
$nsPrefixes = Get-SeedLoaderNamespacePrefixes `
    -ExtensionPrefixes @($manifest["seed"]["extensionNamespacePrefixes"]) `
    -AdditionalPrefixes $AdditionalNamespacePrefix

# --- Step 4: CMS admin token ---
Write-Host "Obtaining CMS admin token..."
Add-CmsClient -CmsUrl $cmsUrl
$cmsToken = Get-CmsToken -CmsUrl $cmsUrl

# --- Step 4b: CMS-side SeedLoader claim-set preflight ---
# Add-Application stores ClaimSetName as a string. A stale Config image without the embedded
# SeedLoader claim set would accept credential creation, then BulkLoadClient would surface
# confusing 401/403 noise. Fail fast against the live CMS instead.
Write-Host "Verifying CMS has the SeedLoader claim set loaded..."
Assert-CmsSeedLoaderClaimSetLoaded -CmsUrl $cmsUrl -AccessToken $cmsToken -Tenant $tenant

# --- Step 5: Instance selector resolution ---
Write-Host "Resolving target data stores..."
$targets = Resolve-SeedTargetDataStores `
    -CmsUrl $cmsUrl `
    -AccessToken $cmsToken `
    -DataStoreId $DataStoreId `
    -SchoolYear $SchoolYear `
    -Tenant $tenant

# --- Step 6: SeedLoader credentials ---
# EdOrg scoping uses New-SeedLoaderCredentials' default (top-level LEA/SEA IDs from the
# standard bootstrap path). Per bootstrap-design.md Section 7.2, DMS-916 does not add a second
# parameter surface for arbitrary seed-specific EdOrg scoping; custom -SeedDataPath
# scenarios are supported as alternate payload sources, not as a custom authorization-model
# designer.
Write-Host "Creating SeedLoader credentials (in-memory only)..."
# Forward the existing admin token so the helper skips its internal Add-CmsClient + Get-CmsToken
# round-trip; the orchestrator already authenticated as sys-admin at Step 4.
$creds = New-SeedLoaderCredentials `
    -CmsUrl $cmsUrl `
    -NamespacePrefixes $nsPrefixes `
    -DataStoreIds $targets.DataStoreIds `
    -Tenant $tenant `
    -AdminToken $cmsToken

# --- Step 7+: Tier-aware load loop ---
# For each DMS target (single instance or per-year), run:
#   (a) SchoolYearType REST precondition
#   (b) BulkLoadClient pass per tier (descriptors, then resources, then any custom path tier)
#
# Each tier stages its own seed workspace because the source directories differ, then re-stages
# the XSD copy alongside it. The workspace is recreated each call by design; descriptors persist
# in the database between tier invocations, so wiping the staged XML between passes is safe.

function Invoke-SeedTierLoad {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper - no -WhatIf end-to-end.')]
    param(
        [hashtable]$Tier,
        [string]$BulkLoadClientDll,
        [string]$DmsBaseUrl,
        [string]$OAuthUrl,
        [string]$Key,
        [string]$Secret,
        [string]$BootstrapRoot,
        [string]$XsdDirectory
    )

    $tierSourceDirs = @($Tier.SourceDirectory) + @($Tier.ExtraDirectories)
    $interchangeNames = Get-BulkLoadClientInterchangeNames -XsdDirectory $XsdDirectory

    Write-Host "Materializing seed workspace for $(Format-LogSafeText $Tier.Name) tier..."
    $workspace = New-SeedWorkspace -BootstrapRoot $BootstrapRoot -SourceDirectories $tierSourceDirs -InterchangeNames $interchangeNames
    Write-Host "Workspace ready at $(Format-LogSafeText $workspace.DataDirectory); staged $(Format-LogSafeText ($workspace.StagedFiles.Count)) XML file(s)."

    Write-Host "Invoking BulkLoadClient against $(Format-LogSafeText $Tier.Name) tier..."
    Invoke-BulkLoadClient `
        -BulkLoadClientDll $BulkLoadClientDll `
        -DmsBaseUrl $DmsBaseUrl `
        -DataDirectory $workspace.DataDirectory `
        -WorkingDirectory $workspace.WorkingDirectory `
        -Key $Key `
        -Secret $Secret `
        -OAuthUrl $OAuthUrl `
        -XsdDirectory $XsdDirectory
}

# $runSchoolYearTypePrecondition and $resolvedXsdDir were resolved above (before any CMS mutation).
# The SchoolYearType precondition is only invoked for BuiltIn templates (Minimal/Populated). Custom
# -SeedDataPath payloads bring their own SchoolYear lifecycle; the bootstrap script does not mutate
# data outside the developer-supplied XML for custom tiers.

if ($SchoolYear.Count -gt 0) {
    Write-Host "School-year mode: $(Format-LogSafeText ($SchoolYear.Count)) year(s) x $(Format-LogSafeText ($tiers.Count)) tier(s)."
    foreach ($year in $SchoolYear) {
        # Build {base}[/{tenant}]/{year} per CoreEndpointModule.BuildRoutePattern. Without this,
        # multi-tenant runs target /{year}/data/... and DMS 404s because the tenant segment is
        # absent from the route.
        $perYearBase = Resolve-DmsRouteUrl `
            -BaseUrl $resolvedDmsBaseUrl `
            -Tenant $tenant `
            -RouteQualifierValues @([string]$year)
        $perYearOAuth = Resolve-OAuthTokenUrl `
            -EnvValues $envValues `
            -IdentityProvider $identityProvider `
            -SchoolYear $year

        if ($runSchoolYearTypePrecondition) {
            Write-Host "Running SchoolYearType REST precondition for year $(Format-LogSafeText $year)..."
            Invoke-SchoolYearTypeRestPrecondition `
                -DmsBaseUrl $perYearBase `
                -Key $creds.Key `
                -Secret $creds.Secret `
                -OAuthUrl $perYearOAuth
        }
        else {
            Write-Host "Skipping SchoolYearType REST precondition for custom seed path (year $(Format-LogSafeText $year))."
        }

        foreach ($tier in $tiers) {
            Write-Host "Loading $(Format-LogSafeText $tier.Name) tier for school year $(Format-LogSafeText $year)..."
            Invoke-SeedTierLoad `
                -Tier $tier `
                -BulkLoadClientDll $bulkLoadClientDll `
                -DmsBaseUrl $perYearBase `
                -OAuthUrl $perYearOAuth `
                -Key $creds.Key `
                -Secret $creds.Secret `
                -BootstrapRoot $bootstrapRoot `
                -XsdDirectory $resolvedXsdDir
        }
    }
}
else {
    $oauthUrl = Resolve-OAuthTokenUrl -EnvValues $envValues -IdentityProvider $identityProvider

    # Build {base}[/{tenant}] per CoreEndpointModule.BuildRoutePattern. In multi-tenant mode the
    # tenant segment is required before /data/..., so an unqualified base 404s the first POST.
    $singleInstanceBase = Resolve-DmsRouteUrl -BaseUrl $resolvedDmsBaseUrl -Tenant $tenant

    if ($runSchoolYearTypePrecondition) {
        Write-Host "Running SchoolYearType REST precondition..."
        Invoke-SchoolYearTypeRestPrecondition `
            -DmsBaseUrl $singleInstanceBase `
            -Key $creds.Key `
            -Secret $creds.Secret `
            -OAuthUrl $oauthUrl
    }
    else {
        Write-Host "Skipping SchoolYearType REST precondition for custom seed path."
    }

    foreach ($tier in $tiers) {
        Write-Host "Loading $(Format-LogSafeText $tier.Name) tier..."
        Invoke-SeedTierLoad `
            -Tier $tier `
            -BulkLoadClientDll $bulkLoadClientDll `
            -DmsBaseUrl $singleInstanceBase `
            -OAuthUrl $oauthUrl `
            -Key $creds.Key `
            -Secret $creds.Secret `
            -BootstrapRoot $bootstrapRoot `
            -XsdDirectory $resolvedXsdDir
    }
}

# --- Step 10: Cleanup on success ---
Remove-SeedWorkspace -BootstrapRoot $bootstrapRoot
Write-Host "Seed delivery complete. Workspace cleaned up."
