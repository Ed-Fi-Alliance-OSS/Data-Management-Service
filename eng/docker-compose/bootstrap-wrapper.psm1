# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Shared body for the bootstrap-(local|published)-dms.ps1 thin wrappers.

.DESCRIPTION
    Both wrappers expose the same developer-facing parameter surface and share an
    identical implementation that only differs in which start script they sequence
    (start-local-dms.ps1 vs start-published-dms.ps1). This module is the single
    source of truth for that implementation; the entry scripts are thin dispatch
    shells that pass their bound parameters plus the target start script name.

    Direct invocation of load-dms-seed-data.ps1 remains supported and bypasses
    this module entirely.
#>

function Get-EffectiveBootstrapEnvFile {
    <#
    .SYNOPSIS
    Returns the env file to forward to the phase commands. When -LoadSeedData is requested and
    the sibling modules are present, delegates to env-utility's Resolve-BootstrapDerivedEnv to
    materialize a per-run derived file with the canonical bootstrap profile (loose circuit-breaker;
    Sample/Homograph excluded only when no custom -SeedDataPath is supplied). The user's base env
    file is left untouched.

    Wrapper-only convenience: this materialization is intentionally scoped to wrapper invocation
    so each phase command keeps its supplied env file authoritative (see
    `command-boundaries.md` Section 3 phase ownership and `02-api-seed-delivery.md` acceptance criteria
    for the disclosure to direct-invocation callers).

    The wrapper-argument Pester tests sandbox the wrapper + this module in a temp directory
    without the env-utility/manifest siblings; the Test-Path guard below makes the shim degrade
    gracefully to "return base env as-is" in that case.
    #>
    param(
        [string]$BaseEnvironmentFile,
        [switch]$LoadSeedDataRequested,
        [switch]$SeedDataPathSupplied
    )

    $BaseEnvironmentFile = Resolve-WrapperEnvironmentFilePath -BaseEnvironmentFile $BaseEnvironmentFile

    if (-not $LoadSeedDataRequested) { return $BaseEnvironmentFile }

    $envUtilityPath = Join-Path $PSScriptRoot "env-utility.psm1"
    $manifestPath   = Join-Path $PSScriptRoot "bootstrap-manifest.psm1"
    if (-not (Test-Path -LiteralPath $envUtilityPath) -or -not (Test-Path -LiteralPath $manifestPath)) {
        return $BaseEnvironmentFile
    }

    Import-Module $envUtilityPath -Force
    Import-Module $manifestPath -Force

    # Only apply the Sample/Homograph SCHEMA_PACKAGES exclusion when the seed source is built-in.
    # Custom -SeedDataPath callers may reference Sample/Homograph resources in their XML and need
    # those schemas active. The exclusion is a built-in-template-specific BulkLoadClient 7.3.1
    # workaround, not a general policy.
    $filterSampleHomograph = -not $SeedDataPathSupplied

    $derivedPath = Join-Path (Get-BootstrapRoot) ".env.derived"
    $result = Resolve-BootstrapDerivedEnv `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -DerivedTargetPath $derivedPath `
        -FilterSampleHomograph:$filterSampleHomograph
    $filterNote = if ($filterSampleHomograph) { "Sample/Homograph filtered (built-in seed path)" } else { "Sample/Homograph retained (-SeedDataPath supplied)" }
    Write-Information "Bootstrap-derived env written: $derivedPath (FAILURE_RATIO=0.95; $filterNote)." -InformationAction Continue
    return $result
}

function Resolve-WrapperEnvironmentFilePath {
    <#
    .SYNOPSIS
    Resolves the base env file path using the same defaults as the wrapper and start scripts.
    #>
    param(
        [string]$BaseEnvironmentFile
    )

    if ([string]::IsNullOrWhiteSpace($BaseEnvironmentFile)) {
        $defaultEnv = Join-Path $PSScriptRoot ".env"
        $fallbackEnv = Join-Path $PSScriptRoot ".env.example"
        $BaseEnvironmentFile = if (Test-Path -LiteralPath $defaultEnv) { $defaultEnv } else { $fallbackEnv }
    }
    elseif (-not [System.IO.Path]::IsPathRooted($BaseEnvironmentFile)) {
        $BaseEnvironmentFile = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $BaseEnvironmentFile))
    }

    return $BaseEnvironmentFile
}

function Resolve-WrapperIdentityProvider {
    <#
    .SYNOPSIS
    Resolves the identity provider used by BOTH the start and seed phases so a single wrapper
    invocation can't end up with infra started under one provider and seed authenticated under
    another. Order: explicit wrapper flag, effective env file's DMS_CONFIG_IDENTITY_PROVIDER,
    "self-contained" fallback (matching the start scripts' default). Degrades to "self-contained"
    when the env-utility sibling module is unavailable (wrapper-argument Pester test sandbox).
    #>
    param(
        [string]$ExplicitProvider,
        [switch]$ExplicitProviderSupplied,
        [string]$EffectiveEnvironmentFile
    )

    $supported = @("keycloak", "self-contained")

    if ($ExplicitProviderSupplied -and -not [string]::IsNullOrWhiteSpace($ExplicitProvider)) {
        $provider = $ExplicitProvider.Trim().ToLowerInvariant()
        if ($supported -notcontains $provider) {
            throw "Unsupported identity provider '$ExplicitProvider'. Supported values: $($supported -join ', ')."
        }
        return $provider
    }

    $envUtilityPath = Join-Path $PSScriptRoot "env-utility.psm1"
    if (
        -not (Test-Path -LiteralPath $envUtilityPath) `
        -or [string]::IsNullOrWhiteSpace($EffectiveEnvironmentFile) `
        -or -not (Test-Path -LiteralPath $EffectiveEnvironmentFile)
    ) {
        return "self-contained"
    }

    Import-Module $envUtilityPath -Force
    $envValues = ReadValuesFromEnvFile -EnvironmentFile $EffectiveEnvironmentFile
    if ($envValues -is [System.Collections.IDictionary] -and $envValues.ContainsKey("DMS_CONFIG_IDENTITY_PROVIDER")) {
        $envValue = [string]$envValues["DMS_CONFIG_IDENTITY_PROVIDER"]
        if (-not [string]::IsNullOrWhiteSpace($envValue)) {
            $provider = $envValue.Trim().ToLowerInvariant()
            if ($supported -notcontains $provider) {
                throw "Unsupported identity provider '$envValue' (from env file). Supported values: $($supported -join ', ')."
            }
            return $provider
        }
    }
    return "self-contained"
}

function Invoke-BootstrapWrapper {
    <#
    .SYNOPSIS
    Executes the bootstrap wrapper body: env-file resolution, infrastructure phase, optional
    seed phase. Targets the start script identified by -StartScriptName.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("start-local-dms.ps1", "start-published-dms.ps1")]
        [string]$StartScriptName,

        [Switch]$LoadSeedData,

        [ValidateSet("Minimal", "Populated")]
        [string]$SeedTemplate,

        [string]$SeedDataPath,

        [string[]]$AdditionalNamespacePrefix = @(),

        [string]$EnvironmentFile,

        [ValidateSet("keycloak", "self-contained")]
        [string]$IdentityProvider,

        [Switch]$EnableKafkaUI,

        [Switch]$EnableSwaggerUI,

        [Switch]$EnableConfig,

        [Switch]$AddExtensionSecurityMetadata,

        [string]$SchoolYearRange = ""
    )

    $ErrorActionPreference = "Stop"

    # Fail fast: validate -SchoolYearRange before any phase invocation. The format also gets parsed
    # again below for the seed phase; hoisting both the regex check and the StartYear <= EndYear
    # check here prevents a malformed or descending range from silently triggering Docker startup +
    # CMS state mutation before the late parse-time throw.
    if (-not [string]::IsNullOrWhiteSpace($SchoolYearRange)) {
        if ($SchoolYearRange -notmatch '^(\d{4})-(\d{4})$') {
            throw "Invalid -SchoolYearRange '$SchoolYearRange'. Expected StartYear-EndYear (e.g. 2024-2025)."
        }
        $rangeStartYear = [int]$Matches[1]
        $rangeEndYear = [int]$Matches[2]
        if ($rangeStartYear -gt $rangeEndYear) {
            throw "Invalid -SchoolYearRange '$SchoolYearRange'. StartYear ($rangeStartYear) must be less than or equal to EndYear ($rangeEndYear)."
        }
    }

    # $seedDataPathSupplied is also read by Get-EffectiveBootstrapEnvFile below, which runs
    # regardless of -LoadSeedData, so both predicates live at function entry rather than inside
    # the -LoadSeedData branch.
    $seedTemplateSupplied = $PSBoundParameters.ContainsKey('SeedTemplate') -and -not [string]::IsNullOrWhiteSpace($SeedTemplate)
    $seedDataPathSupplied = $PSBoundParameters.ContainsKey('SeedDataPath') -and -not [string]::IsNullOrWhiteSpace($SeedDataPath)

    # Pre-start seed preflights: catch missing manifest, mutually-exclusive seed-source flags,
    # and ApiSchemaPath combos here so a known-bad -LoadSeedData invocation doesn't first spin up
    # Docker + CMS state. load-dms-seed-data.ps1's Read-SeedManifest / Assert-SeedSelectionInputs
    # remain the authoritative validators; the checks below are a fail-fast convenience.
    if ($LoadSeedData) {
        $expectedManifest = Join-Path $PSScriptRoot ".bootstrap/bootstrap-manifest.json"
        if (-not (Test-Path -LiteralPath $expectedManifest -PathType Leaf)) {
            throw "Bootstrap manifest not found at $expectedManifest. Run prepare-dms-schema.ps1 to stage the manifest before invoking the wrapper with -LoadSeedData."
        }

        if ($seedTemplateSupplied -and $seedDataPathSupplied) {
            throw "-SeedTemplate and -SeedDataPath are mutually exclusive. Provide at most one."
        }

        # Bare JSON parse keeps the wrapper independent of bootstrap-manifest.psm1 in sandboxed
        # Pester invocations. Malformed/empty manifests defer to load-dms-seed-data.ps1's
        # Read-SeedManifest for the authoritative error rather than throwing here.
        $manifestSelectionMode = $null
        try {
            $rawManifestContent = Get-Content -LiteralPath $expectedManifest -Raw -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace($rawManifestContent)) {
                $parsedManifest = $rawManifestContent | ConvertFrom-Json -ErrorAction Stop
                if ($null -ne $parsedManifest -and $null -ne $parsedManifest.schema -and -not [string]::IsNullOrWhiteSpace([string]$parsedManifest.schema.selectionMode)) {
                    $manifestSelectionMode = [string]$parsedManifest.schema.selectionMode
                }
            }
        }
        catch {
            $manifestSelectionMode = $null
        }

        if ($manifestSelectionMode -eq "ApiSchemaPath") {
            if ($seedTemplateSupplied) {
                throw "Expert mode (bootstrap-manifest.schema.selectionMode=ApiSchemaPath) does not support -SeedTemplate. Provide -SeedDataPath instead."
            }
            if (-not $seedDataPathSupplied) {
                throw "Expert mode (bootstrap-manifest.schema.selectionMode=ApiSchemaPath) requires -SeedDataPath. No built-in seed template is available in this mode."
            }
        }
    }

    # Normalize -SeedDataPath against the caller's CWD before Push-Location $PSScriptRoot, so a
    # relative path supplied from any directory still resolves correctly. The Push-Location below
    # would otherwise reinterpret the relative path against eng/docker-compose/.
    if ($seedDataPathSupplied -and -not [System.IO.Path]::IsPathRooted($SeedDataPath)) {
        $SeedDataPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $SeedDataPath))
    }

    # Same caller-CWD normalization for -EnvironmentFile: Get-EffectiveBootstrapEnvFile's relative-
    # path fallback uses (Get-Location).Path, which becomes eng/docker-compose after Push-Location.
    # Without this, `bootstrap-local-dms.ps1 -EnvironmentFile eng/docker-compose/.env.e2e -LoadSeedData`
    # from the repo root would resolve to eng/docker-compose/eng/docker-compose/.env.e2e and the
    # derived-env materialization would fail before any phase starts.
    if ($PSBoundParameters.ContainsKey('EnvironmentFile') -and -not [string]::IsNullOrWhiteSpace($EnvironmentFile) -and -not [System.IO.Path]::IsPathRooted($EnvironmentFile)) {
        $EnvironmentFile = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $EnvironmentFile))
    }

    Push-Location $PSScriptRoot
    try {
        $baseEnvFile = Resolve-WrapperEnvironmentFilePath -BaseEnvironmentFile $EnvironmentFile

        # Resolve identity provider once and forward the same value to both phases. This runs before
        # derived-env materialization so an unsupported env-file value fails without writing .env.derived.
        # The start
        # scripts default to "self-contained" and would otherwise diverge from the seed phase,
        # which resolves DMS_CONFIG_IDENTITY_PROVIDER from the env file. Without this, a single
        # wrapper invocation could start infra under one provider and authenticate seeds under
        # another.
        $resolvedIdentityProvider = Resolve-WrapperIdentityProvider `
            -ExplicitProvider $IdentityProvider `
            -ExplicitProviderSupplied:($PSBoundParameters.ContainsKey('IdentityProvider')) `
            -EffectiveEnvironmentFile $baseEnvFile

        # Resolve the effective env file. When seed loading is requested, materialize a derived env
        # with the bootstrap profile so BulkLoadClient 7.3.1 doesn't NRE on Sample's inline-object
        # array shapes and the circuit breaker tolerates the bulk-load failure ratio.
        $effectiveEnvFile = Get-EffectiveBootstrapEnvFile `
            -BaseEnvironmentFile $baseEnvFile `
            -LoadSeedDataRequested:$LoadSeedData `
            -SeedDataPathSupplied:$seedDataPathSupplied

        # Infrastructure phase
        $startArgs = @{ IdentityProvider = $resolvedIdentityProvider }
        if ($EnableKafkaUI) { $startArgs.EnableKafkaUI = $true }
        if ($EnableSwaggerUI) { $startArgs.EnableSwaggerUI = $true }
        # CMS is required when seed loading is requested (SeedLoader credential creation goes through CMS).
        if ($EnableConfig -or $LoadSeedData) { $startArgs.EnableConfig = $true }
        if ($AddExtensionSecurityMetadata) { $startArgs.AddExtensionSecurityMetadata = $true }
        $startArgs.EnvironmentFile = $effectiveEnvFile
        if (-not [string]::IsNullOrWhiteSpace($SchoolYearRange)) { $startArgs.SchoolYearRange = $SchoolYearRange }

        & "$PSScriptRoot/$StartScriptName" @startArgs
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
            throw "$StartScriptName failed with exit code $LASTEXITCODE."
        }

        # Seed phase is wrapper-level opt-in
        if (-not $LoadSeedData) { return }

        $seedArgs = @{ IdentityProvider = $resolvedIdentityProvider }
        $seedArgs.EnvironmentFile = $effectiveEnvFile
        if ($PSBoundParameters.ContainsKey('SeedTemplate')) { $seedArgs.SeedTemplate = $SeedTemplate }
        if ($PSBoundParameters.ContainsKey('SeedDataPath')) { $seedArgs.SeedDataPath = $SeedDataPath }
        if ($AdditionalNamespacePrefix.Count -gt 0) { $seedArgs.AdditionalNamespacePrefix = $AdditionalNamespacePrefix }

        # Reuse the validated range captured by the pre-Push-Location block above instead of
        # re-running the regex; both blocks share the same $SchoolYearRange parameter and the
        # earlier validation already guaranteed StartYear <= EndYear.
        if (-not [string]::IsNullOrWhiteSpace($SchoolYearRange)) {
            $seedArgs.SchoolYear = @($rangeStartYear..$rangeEndYear)
        }

        & "$PSScriptRoot/load-dms-seed-data.ps1" @seedArgs
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
            throw "load-dms-seed-data.ps1 failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

Export-ModuleMember -Function Invoke-BootstrapWrapper
