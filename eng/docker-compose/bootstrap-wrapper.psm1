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
    Returns the env file to forward to the phase commands. When the sibling modules are present,
    materializes a per-run derived file for the wrapper path, forcing DMS startup database
    provisioning off. When -LoadSeedData is requested, also delegates to env-utility's
    Resolve-BootstrapDerivedEnv to apply the canonical seed profile (loose circuit-breaker). The
    user's base env file is left untouched.

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
        [switch]$LoadSeedDataRequested
    )

    $BaseEnvironmentFile = Resolve-WrapperEnvironmentFilePath -BaseEnvironmentFile $BaseEnvironmentFile

    $envUtilityPath = Join-Path $PSScriptRoot "env-utility.psm1"
    $manifestPath   = Join-Path $PSScriptRoot "bootstrap-manifest.psm1"
    if (-not (Test-Path -LiteralPath $envUtilityPath) -or -not (Test-Path -LiteralPath $manifestPath)) {
        return $BaseEnvironmentFile
    }

    Import-Module $envUtilityPath -Force
    Import-Module $manifestPath -Force

    $derivedPath = Join-Path (Get-BootstrapRoot) ".env.derived"
    if ($LoadSeedDataRequested) {
        $result = Resolve-BootstrapDerivedEnv `
            -BaseEnvironmentFile $BaseEnvironmentFile `
            -DerivedTargetPath $derivedPath
        Write-Information "Bootstrap-derived env written: $derivedPath (FAILURE_RATIO=0.95)." -InformationAction Continue
        return $result
    }

    Write-DerivedEnvFile `
        -BaseEnvironmentFile $BaseEnvironmentFile `
        -TargetPath $derivedPath `
        -KeyOverrides @{}

    Write-Information "Bootstrap-derived env written: $derivedPath." -InformationAction Continue
    return $derivedPath
}

function Assert-WrapperStagedSchemaWorkspace {
    <#
    .SYNOPSIS
    Validates the staged schema handoff before the wrapper starts Docker services. The guard
    degrades only in isolated Pester sandboxes that intentionally copy the wrapper without
    its sibling helper modules.
    #>
    $workspaceModulePath = Join-Path $PSScriptRoot "bootstrap-schema-workspace.psm1"
    if (-not (Test-Path -LiteralPath $workspaceModulePath)) {
        return
    }

    Import-Module $workspaceModulePath -Force
    Resolve-BootstrapSchemaWorkspace | Out-Null
}

function Test-WrapperManifestClaimsStaged {
    <#
    .SYNOPSIS
    Returns $true only when the on-disk bootstrap manifest already carries BOTH the claims and
    seed sections, i.e. prepare-dms-claims.ps1 has completed for the staged workspace.

    The wrapper uses this to detect a schema-only manifest — one written by prepare-dms-schema.ps1
    without a following prepare-dms-claims.ps1 — so the staging phase can complete it before any
    Docker/CMS side effects. Without this, the schema-only manifest passes Assert-WrapperStagedSchemaWorkspace
    (which validates the schema workspace only) and infrastructure starts; start-local-dms.ps1 then
    activates staged claims and runs the claims-ready gate, both of which require the claims/seed
    sections and throw after the stack is already up.

    Bare JSON parse keeps the wrapper independent of bootstrap-manifest.psm1 in sandboxed Pester
    invocations (mirrors the ApiSchemaPath preflight in Invoke-BootstrapWrapper). Property presence
    is tested via PSObject.Properties so the check is safe regardless of Set-StrictMode. A missing,
    empty, malformed, or unreadable manifest is reported as "claims not staged" so the staging phase
    runs prepare-dms-claims.ps1, which then surfaces the authoritative manifest error.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ManifestPath
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return $false
    }

    try {
        $rawManifestContent = Get-Content -LiteralPath $ManifestPath -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($rawManifestContent)) {
            return $false
        }

        $parsedManifest = $rawManifestContent | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $parsedManifest) {
            return $false
        }

        $claimsProperty = $parsedManifest.PSObject.Properties['claims']
        $seedProperty = $parsedManifest.PSObject.Properties['seed']
        return ($null -ne $claimsProperty -and $null -ne $claimsProperty.Value -and
                $null -ne $seedProperty -and $null -ne $seedProperty.Value)
    }
    catch {
        return $false
    }
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

function Resolve-WrapperSelectedDataStoreIds {
    <#
    .SYNOPSIS
    Extracts the configured data store ids from configure-local-data-store.ps1's
    structured result, preferring the documented SelectedDataStoreIds property and falling
    back to DataStoreIds. See command-boundaries.md Section 3.4.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Helper returns a collection of data store ids; the plural noun is the documented contract.')]
    param(
        [Parameter(Mandatory)]
        $ConfigureResult
    )

    if ($null -eq $ConfigureResult) {
        return [long[]]@()
    }

    foreach ($name in @("SelectedDataStoreIds", "DataStoreIds")) {
        if ($ConfigureResult -is [System.Collections.IDictionary]) {
            if ($ConfigureResult.Contains($name)) {
                $value = $ConfigureResult[$name]
                if ($null -ne $value) {
                    return [long[]]@($value)
                }
            }
            continue
        }

        $property = $ConfigureResult.PSObject.Properties[$name]
        if ($null -ne $property -and $null -ne $property.Value) {
            return [long[]]@($property.Value)
        }
    }

    throw "configure-local-data-store.ps1 result is missing SelectedDataStoreIds (and the DataStoreIds alias)."
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

        [Switch]$NoDataStore,

        [Switch]$AddSmokeTestCredentials,

        [string]$SchoolYearRange = "",

        # IDE workflow: stop before DMS startup so the developer can launch DMS in an IDE debugger.
        # When combined with -DmsBaseUrl, after configure + provision the wrapper waits for the
        # IDE-hosted DMS to become healthy before returning (or optionally loading seed data).
        # Must not be forwarded to start-published-dms.ps1 (D5: IDE shapes are local-only).
        [Switch]$InfraOnly,

        # IDE workflow: base URL of an IDE-hosted DMS process to health-wait after infrastructure
        # startup, configure, and provision. Valid only with -InfraOnly; rejected without it.
        # The value is held locally and NOT forwarded to the initial start-script infra invocation;
        # it is forwarded only to the post-provision start-script health-wait invocation and,
        # when -LoadSeedData is also requested, to load-dms-seed-data.ps1.
        [string]$DmsBaseUrl,

        # Database engine for the DMS datastore ("postgresql" or "mssql"). Forwarded to the
        # configure phase always, and to the start phases only for start-local-dms.ps1 (mssql.yml
        # is a local-only tier; start-published-dms.ps1 has no -DatabaseEngine parameter).
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql",

        # Data standard version for the local-bootstrap package surface. For
        # start-local-dms.ps1 the .env.bootstrap.<token> overlay is always composed onto the
        # base env file (DS 5.2, the default: core + TPDM; DS 6.1: core only) before any phase
        # reads it, so local bootstraps have a canonical package surface no matter what the base
        # env file's own SCHEMA_PACKAGES holds. For start-published-dms.ps1 the overlay is
        # composed ONLY when the caller explicitly supplies this parameter: published bootstraps
        # predate overlay composition, and composing unconditionally would silently replace a
        # custom base env file's SCHEMA_PACKAGES / DATABASE_TEMPLATE_PACKAGE values for published
        # workflows that never asked for a data-standard override. The default is declared here
        # as well as on the entry scripts because PowerShell defaults are not part of
        # $PSBoundParameters and would otherwise not reach this function. Deliberately NOT
        # forwarded to the start scripts: their -DataStandardVersion composes the shared
        # E2E-shaped .env.ds<NN> overlays (which add the Sample/Homograph test extensions) and
        # would override this surface.
        [ValidateSet("5.2", "6.1")]
        [string]$DataStandardVersion = "5.2"
    )

    $ErrorActionPreference = "Stop"

    # mssql.yml is a local-only datastore tier. Only start-local-dms.ps1 understands
    # -DatabaseEngine; start-published-dms.ps1 does not, so the engine is forwarded to the start
    # phases only for the local start script. The configure phase always accepts it.
    $startScriptSupportsDatabaseEngine = ($StartScriptName -eq "start-local-dms.ps1")

    # Fail fast: IDE workflow shape parameter validation — runs before any phase invocation.
    # -DmsBaseUrl is only valid with -InfraOnly; reject it without -InfraOnly so a misuse
    # never reaches Docker or CMS state.
    $dmsBaseUrlSupplied = $PSBoundParameters.ContainsKey('DmsBaseUrl') -and -not [string]::IsNullOrWhiteSpace($DmsBaseUrl)
    if ($dmsBaseUrlSupplied -and -not $InfraOnly) {
        throw "-DmsBaseUrl requires -InfraOnly. Use: bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl <url>"
    }

    # -LoadSeedData combined with -InfraOnly but WITHOUT -DmsBaseUrl is rejected because the
    # terminal pre-DMS shape has no running DMS process: seed delivery requires a healthy DMS
    # endpoint and there is none when the workflow stops before DMS startup.
    if ($LoadSeedData -and $InfraOnly -and -not $dmsBaseUrlSupplied) {
        throw "-LoadSeedData with -InfraOnly requires -DmsBaseUrl. The terminal pre-DMS shape stops before any DMS process is running; seed delivery requires a healthy DMS endpoint. Supply -DmsBaseUrl to enable the health-wait continuation and seed phase."
    }

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

    # $seedDataPathSupplied is read by the seed-flag preflights below and by the caller-CWD
    # path normalization that runs regardless of -LoadSeedData, so both predicates live at
    # function entry rather than inside the -LoadSeedData branch.
    $seedTemplateSupplied = $PSBoundParameters.ContainsKey('SeedTemplate') -and -not [string]::IsNullOrWhiteSpace($SeedTemplate)
    $seedDataPathSupplied = $PSBoundParameters.ContainsKey('SeedDataPath') -and -not [string]::IsNullOrWhiteSpace($SeedDataPath)

    # Pre-start seed preflights: catch mutually-exclusive seed-source flags and ApiSchemaPath combos
    # here so a known-bad -LoadSeedData invocation doesn't first spin up Docker + CMS state.
    # load-dms-seed-data.ps1's Read-SeedManifest / Assert-SeedSelectionInputs remain the authoritative
    # validators; the checks below are a fail-fast convenience.
    if ($LoadSeedData) {
        if ($seedTemplateSupplied -and $seedDataPathSupplied) {
            throw "-SeedTemplate and -SeedDataPath are mutually exclusive. Provide at most one."
        }

        # The schema/claims staging phase below guarantees a Standard-mode manifest exists before any
        # Docker or CMS state is created: when no workspace is staged it stages core-only standard mode,
        # for which -SeedTemplate is valid, so there is nothing to pre-validate. Only when a manifest
        # ALREADY exists (a manual/expert pre-stage flow) do the ApiSchemaPath seed-source rules apply;
        # they are checked here so a known-bad -LoadSeedData invocation fails fast before the start phase.
        $expectedManifest = Join-Path $PSScriptRoot ".bootstrap/bootstrap-manifest.json"
        if (Test-Path -LiteralPath $expectedManifest -PathType Leaf) {
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

        # Data-standard selection: compose the LOCAL-BOOTSTRAP overlay (.env.bootstrap.<token>,
        # default DS 5.2) onto the base env before anything reads it, so identity resolution,
        # prepare, configure, provision, and the start phases all see the composed
        # SCHEMA_PACKAGES / data-standard settings from one canonical path. These bootstrap-scoped
        # overlays carry the minimal local surfaces (DS 5.2: core + TPDM; DS 6.1: core only) and
        # are deliberately distinct from the shared .env.ds<NN> overlays, whose E2E/SDK surfaces
        # include the Sample/Homograph test extensions required by CI. For the same reason
        # -DataStandardVersion is NOT forwarded to the start scripts below: they would re-compose
        # the shared overlay over this derived file and silently restore the E2E-shaped
        # SCHEMA_PACKAGES. The start phases receive the derived file through -EnvironmentFile
        # instead.
        #
        # start-local-dms.ps1 ALWAYS composes: local bootstraps are the canonical DS 5.2/6.1 entry
        # point and have no other source of a package surface. start-published-dms.ps1 composes
        # ONLY when the caller explicitly supplies -DataStandardVersion: published bootstraps
        # predate overlay composition and existing custom-base-env workflows rely on their own
        # SCHEMA_PACKAGES / DATABASE_TEMPLATE_PACKAGE values reaching every phase untouched.
        # Custom or extension schema sets remain expert -ApiSchemaPath territory either way.
        $composeDataStandardOverlay = ($StartScriptName -eq "start-local-dms.ps1") -or
            $PSBoundParameters.ContainsKey('DataStandardVersion')
        if ($composeDataStandardOverlay) {
            # env-utility is imported here because the wrapper's other imports live inside helper
            # functions that run after this block.
            Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force
            $baseEnvFile = Resolve-DataStandardEnvironmentFile `
                -DataStandardVersion $DataStandardVersion `
                -BaseEnvironmentFile $baseEnvFile `
                -DockerComposeRoot $PSScriptRoot `
                -OverlayPrefix ".env.bootstrap"
        }

        # Database engine selection: compose the MSSQL engine overlay (.env.mssql) onto the base
        # env whenever -DatabaseEngine mssql is requested, so identity resolution, the configure
        # phase (which always receives -DatabaseEngine), and the start phases all see
        # DMS_DATASTORE=mssql and the SQL Server connection strings from one canonical path.
        # Without this, the CMS data store could be provisioned for MSSQL while the DMS container
        # itself still starts on its postgresql default (local-dms.yml AppSettings__Datastore
        # comes only from the env file). Applied AFTER the data-standard overlay above so
        # composition order is deterministic; the two overlays touch disjoint keys. Guarded for
        # the isolated wrapper-argument Pester fixtures, which sandbox the wrapper without the
        # env-utility sibling module.
        $envUtilityPathForEngineOverlay = Join-Path $PSScriptRoot "env-utility.psm1"
        if ($DatabaseEngine -eq "mssql" -and (Test-Path -LiteralPath $envUtilityPathForEngineOverlay)) {
            Import-Module $envUtilityPathForEngineOverlay -Force
            $baseEnvFile = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine $DatabaseEngine `
                -BaseEnvironmentFile $baseEnvFile `
                -DockerComposeRoot $PSScriptRoot
        }

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
        # with the bootstrap profile so the circuit breaker tolerates the bulk-load failure ratio.
        $effectiveEnvFile = Get-EffectiveBootstrapEnvFile `
            -BaseEnvironmentFile $baseEnvFile `
            -LoadSeedDataRequested:$LoadSeedData

        # Schema/claims staging phase. The standard happy path needs no manual pre-staging
        # (bootstrap-design.md Section 9.4.1): when no workspace is staged yet, stage core-only standard
        # mode so a clean checkout runs `bootstrap-local-dms.ps1` with no preceding prepare step. When a
        # schema workspace is already staged (a manual/expert prepare flow, or a prior run), reuse it
        # as-is rather than rewriting a workspace that may still be bind-mounted into a running stack.
        # There is no -Extensions parameter; extension/custom schema sets are staged via expert
        # -ApiSchemaPath before invoking the wrapper. prepare-dms-schema.ps1 owns all validation and the
        # rerun contract.
        #
        # Claims completion is staged whenever the manifest lacks the claims/seed sections: both after a
        # fresh schema stage above, and when a pre-existing manifest carries schema but not claims/seed
        # (prepare-dms-schema.ps1 was run without prepare-dms-claims.ps1). That schema-only state is
        # incomplete: it passes Assert-WrapperStagedSchemaWorkspace (schema-only validation) but
        # start-local-dms.ps1 then activates staged claims and runs the claims-ready gate, both of which
        # require claims/seed and would throw only after Docker/CMS startup. Completing it here keeps the
        # failure (if any) ahead of all infrastructure side effects. prepare-dms-claims.ps1 requires the
        # schema section + staged ApiSchema manifest (guaranteed by the schema stage) and is a guarded
        # rerun when the claims workspace already matches.
        $prepareSchemaScript = "$PSScriptRoot/prepare-dms-schema.ps1"
        $prepareClaimsScript = "$PSScriptRoot/prepare-dms-claims.ps1"
        $stagedManifestPath = Join-Path $PSScriptRoot ".bootstrap/bootstrap-manifest.json"
        $stagedManifestPresent = Test-Path -LiteralPath $stagedManifestPath -PathType Leaf

        # Reset the native exit-code sentinel before each prepare invocation (same pattern as the
        # start/configure/provision phases below). prepare-dms-*.ps1 signal failure by throwing and may
        # run no native command, so a stale nonzero $LASTEXITCODE left by an earlier command in the
        # session would otherwise make a successful staging step throw a false "failed with exit code"
        # before infrastructure starts.
        if ((Test-Path -LiteralPath $prepareSchemaScript) -and -not $stagedManifestPresent) {
            $global:LASTEXITCODE = 0
            # Forward the same effective env file used by the other phases so standard-mode staging
            # can drive itself from its SCHEMA_PACKAGES value (core plus any extensions) instead of
            # the catalog-pinned core-only default. This keeps the staged workspace's effective schema
            # hash in sync with what the DMS container entrypoint resolves from the same env file.
            & $prepareSchemaScript -EnvironmentFile $effectiveEnvFile
            if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
                throw "prepare-dms-schema.ps1 failed with exit code $LASTEXITCODE."
            }
        }

        if ((Test-Path -LiteralPath $prepareClaimsScript) -and
            (-not $stagedManifestPresent -or -not (Test-WrapperManifestClaimsStaged -ManifestPath $stagedManifestPath))) {
            $global:LASTEXITCODE = 0
            & $prepareClaimsScript
            if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
                throw "prepare-dms-claims.ps1 failed with exit code $LASTEXITCODE."
            }
        }

        Assert-WrapperStagedSchemaWorkspace

        # Infrastructure phase
        $startArgs = @{
            IdentityProvider = $resolvedIdentityProvider
            InfraOnly = $true
            EnableConfig = $true
        }
        if ($EnableKafkaUI) { $startArgs.EnableKafkaUI = $true }
        if ($EnableSwaggerUI) { $startArgs.EnableSwaggerUI = $true }
        if ($AddExtensionSecurityMetadata) { $startArgs.AddExtensionSecurityMetadata = $true }
        if ($startScriptSupportsDatabaseEngine) { $startArgs.DatabaseEngine = $DatabaseEngine }
        $startArgs.EnvironmentFile = $effectiveEnvFile

        # Reset the native exit-code sentinel so the check below reflects only this start invocation and
        # not a stale value left by an earlier command. The start scripts signal failure by throwing;
        # docker-compose paths set a real exit code that overwrites this reset.
        $global:LASTEXITCODE = 0
        & "$PSScriptRoot/$StartScriptName" @startArgs
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
            throw "$StartScriptName failed with exit code $LASTEXITCODE."
        }

        $configureScriptPath = "$PSScriptRoot/configure-local-data-store.ps1"
        $provisionScriptPath = "$PSScriptRoot/provision-dms-schema.ps1"
        if (-not (Test-Path -LiteralPath $configureScriptPath) -or -not (Test-Path -LiteralPath $provisionScriptPath)) {
            # Isolated wrapper Pester fixtures copy only the wrapper and stub phase scripts. The
            # production checkout always has these siblings, so the real wrapper path continues
            # below through configure -> provision -> DMS-only -> seed.
            # The -InfraOnly branch bypasses configure/provision/DMS-only the same way it would in
            # production: if the siblings are absent this is a test sandbox, so just return early.
            if (-not $LoadSeedData) { return }

            $seedArgs = @{ IdentityProvider = $resolvedIdentityProvider }
            $seedArgs.EnvironmentFile = $effectiveEnvFile
            if ($PSBoundParameters.ContainsKey('SeedTemplate')) { $seedArgs.SeedTemplate = $SeedTemplate }
            if ($PSBoundParameters.ContainsKey('SeedDataPath')) { $seedArgs.SeedDataPath = $SeedDataPath }
            if ($AdditionalNamespacePrefix.Count -gt 0) { $seedArgs.AdditionalNamespacePrefix = $AdditionalNamespacePrefix }
            if (-not [string]::IsNullOrWhiteSpace($SchoolYearRange)) {
                $seedArgs.SchoolYear = @($rangeStartYear..$rangeEndYear)
            }
            if ($dmsBaseUrlSupplied) { $seedArgs.DmsBaseUrl = $DmsBaseUrl }

            & "$PSScriptRoot/load-dms-seed-data.ps1" @seedArgs
            if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
                throw "load-dms-seed-data.ps1 failed with exit code $LASTEXITCODE."
            }
            return
        }

        $configureArgs = @{ EnvironmentFile = $effectiveEnvFile }
        if ($NoDataStore) { $configureArgs.NoDataStore = $true }
        if ($AddSmokeTestCredentials) { $configureArgs.AddSmokeTestCredentials = $true }
        if (-not [string]::IsNullOrWhiteSpace($SchoolYearRange)) { $configureArgs.SchoolYearRange = $SchoolYearRange }
        $configureArgs.DatabaseEngine = $DatabaseEngine

        # configure-local-data-store.ps1 throws on failure (no exit code); clear any stale native exit code first.
        $global:LASTEXITCODE = 0
        $configurationResult = & "$PSScriptRoot/configure-local-data-store.ps1" @configureArgs
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
            throw "configure-local-data-store.ps1 failed with exit code $LASTEXITCODE."
        }

        $configurationResults = @($configurationResult)
        if ($configurationResults.Count -ne 1) {
            throw "configure-local-data-store.ps1 must return exactly one structured result object. Returned $($configurationResults.Count)."
        }
        $configured = $configurationResults[0]
        $configuredDataStoreIds = [long[]]@(Resolve-WrapperSelectedDataStoreIds -ConfigureResult $configured)

        $provisionArgs = @{
            EnvironmentFile = $effectiveEnvFile
            DataStoreId = $configuredDataStoreIds
        }

        # provision-dms-schema.ps1 throws on failure (no exit code); clear any stale native exit code first.
        $global:LASTEXITCODE = 0
        & "$PSScriptRoot/provision-dms-schema.ps1" @provisionArgs
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
            throw "provision-dms-schema.ps1 failed with exit code $LASTEXITCODE."
        }

        if ($InfraOnly) {
            # IDE workflow: infrastructure + configure + provision complete. The wrapper now branches:
            #
            #   Primary (terminal): -InfraOnly alone — print IDE guidance and stop. The developer
            #   launches DMS in an IDE debugger manually. No -DmsOnly invocation runs here.
            #
            #   Continuation: -InfraOnly -DmsBaseUrl <url> — after provision, invoke the start
            #   script again with -InfraOnly -DmsBaseUrl to trigger the health-wait. The value is
            #   intentionally NOT forwarded to the earlier infra invocation above (command-boundaries
            #   §3.3: start-local-dms.ps1 owns the health-wait; the wrapper holds the URL until now).
            if (-not $dmsBaseUrlSupplied) {
                # Terminal pre-DMS shape: IDE guidance was already printed by provision-dms-schema.ps1.
                # The caller launches DMS in their IDE and then manually runs seed or health-wait.
                # Terminal guidance contract (DMS-1153 AC): do NOT present a second
                # start-local-dms.ps1 run as a resume mechanism. The continuation shape is a
                # fresh wrapper invocation — there is no persisted stop/resume state. Because
                # this terminal run already created the data store, a follow-up wrapper run
                # must pass -NoDataStore so the configure phase reuses it instead of creating
                # a duplicate (verified live: a plain re-run creates a second data store).
                Write-Information "" -InformationAction Continue
                Write-Information "--- IDE Workflow: Pre-DMS preparation complete ---" -InformationAction Continue
                Write-Information "Infrastructure is running, schema is provisioned. Launch DMS in your IDE debugger." -InformationAction Continue
                Write-Information "Optional seed delivery once your IDE-hosted DMS is healthy:" -InformationAction Continue
                Write-Information "  load-dms-seed-data.ps1 -DmsBaseUrl <url> [...]" -InformationAction Continue
                Write-Information "For a wrapper-managed health-wait and seed (fresh wrapper run; -NoDataStore reuses the data store this run created):" -InformationAction Continue
                Write-Information "  bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl <url> -NoDataStore [-LoadSeedData ...]" -InformationAction Continue
                Write-Information "  Note: -NoDataStore supports exactly one route-unqualified data store. If this run used" -InformationAction Continue
                Write-Information "  -SchoolYearRange (or created route-qualified data stores), do NOT re-run the wrapper:" -InformationAction Continue
                Write-Information "  re-supplying -SchoolYearRange creates a NEW set of data stores instead of selecting these." -InformationAction Continue
                Write-Information "  Seed the data stores this run created directly once your IDE-hosted DMS is healthy:" -InformationAction Continue
                Write-Information "    load-dms-seed-data.ps1 -DmsBaseUrl <url> -SchoolYear <years...> [...]" -InformationAction Continue
                return
            }

            # Continuation shape: trigger the health-wait via start-local-dms.ps1 -InfraOnly -DmsBaseUrl.
            # -DmsBaseUrl is deliberately withheld from the first (infra) invocation above and only
            # forwarded here, after configure + provision are complete.
            $healthWaitArgs = @{
                IdentityProvider = $resolvedIdentityProvider
                InfraOnly = $true
                EnableConfig = $true
                EnvironmentFile = $effectiveEnvFile
                DmsBaseUrl = $DmsBaseUrl
            }
            if ($EnableKafkaUI) { $healthWaitArgs.EnableKafkaUI = $true }
            if ($EnableSwaggerUI) { $healthWaitArgs.EnableSwaggerUI = $true }
            if ($AddExtensionSecurityMetadata) { $healthWaitArgs.AddExtensionSecurityMetadata = $true }
            if ($startScriptSupportsDatabaseEngine) { $healthWaitArgs.DatabaseEngine = $DatabaseEngine }

            & "$PSScriptRoot/$StartScriptName" @healthWaitArgs
            if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
                throw "$StartScriptName -InfraOnly -DmsBaseUrl health-wait failed with exit code $LASTEXITCODE."
            }

            # Seed phase is wrapper-level opt-in; only runs in the continuation shape.
            if (-not $LoadSeedData) { return }

            $seedArgs = @{ IdentityProvider = $resolvedIdentityProvider }
            $seedArgs.EnvironmentFile = $effectiveEnvFile
            $seedArgs.DmsBaseUrl = $DmsBaseUrl
            if ($PSBoundParameters.ContainsKey('SeedTemplate')) { $seedArgs.SeedTemplate = $SeedTemplate }
            if ($PSBoundParameters.ContainsKey('SeedDataPath')) { $seedArgs.SeedDataPath = $SeedDataPath }
            if ($AdditionalNamespacePrefix.Count -gt 0) { $seedArgs.AdditionalNamespacePrefix = $AdditionalNamespacePrefix }

            if (-not [string]::IsNullOrWhiteSpace($SchoolYearRange)) {
                $seedArgs.SchoolYear = @($rangeStartYear..$rangeEndYear)
            }
            elseif (-not $configured.HasRouteQualifiedDataStores -and $configuredDataStoreIds.Count -eq 1) {
                $seedArgs.DataStoreId = [long[]]@([long]$configuredDataStoreIds[0])
            }

            & "$PSScriptRoot/load-dms-seed-data.ps1" @seedArgs
            if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
                throw "load-dms-seed-data.ps1 -InfraOnly continuation failed with exit code $LASTEXITCODE."
            }
            return
        }

        $dmsStartArgs = @{
            IdentityProvider = $resolvedIdentityProvider
            DmsOnly = $true
            EnvironmentFile = $effectiveEnvFile
        }
        if ($EnableKafkaUI) { $dmsStartArgs.EnableKafkaUI = $true }
        if ($EnableSwaggerUI) { $dmsStartArgs.EnableSwaggerUI = $true }
        if ($AddExtensionSecurityMetadata) { $dmsStartArgs.AddExtensionSecurityMetadata = $true }
        if ($startScriptSupportsDatabaseEngine) { $dmsStartArgs.DatabaseEngine = $DatabaseEngine }

        & "$PSScriptRoot/$StartScriptName" @dmsStartArgs
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
            throw "$StartScriptName -DmsOnly failed with exit code $LASTEXITCODE."
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
        elseif (-not $configured.HasRouteQualifiedDataStores -and $configuredDataStoreIds.Count -eq 1) {
            $seedArgs.DataStoreId = [long[]]@([long]$configuredDataStoreIds[0])
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

Export-ModuleMember -Function Invoke-BootstrapWrapper, Resolve-WrapperSelectedDataStoreIds
