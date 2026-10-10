# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Database-template restore smoke: the real restore sequence against a live Docker stack.

.DESCRIPTION
    Manual end-to-end smoke for the bootstrap restore branch. Builds a REAL new-format
    template package with the producer (dumped from a datastore this smoke bootstraps), signs
    it with an ephemeral development attestor, and then exercises the restore wrapper against
    a live Docker stack - both the success shapes and the fail-closed shapes.

    The script is intentionally MANUAL, mirroring Invoke-BootstrapDockerSmoke.ps1: it is not
    wired into CI and is not run by the normal Pester suite (a static-contract spec,
    BootstrapRestoreSmoke.Tests.ps1, pins this script's surface without Docker).

    Legs (select with -Leg; each leg starts from its own stack state):
      package-directory   Full restore via -PackageDirectory on a fresh volume, then a -KeepVolumes
                          stop and a second restore of the same package onto that restored target;
                          after each restore asserts DMS health, the restored dms.EffectiveSchema
                          singleton, (when the source was seeded) restored descriptor rows, the
                          SourceIdentity proof, and the served-data API read. The two restored
                          identities must differ from each other and from the package's.
      separate-config     Same restore with -SeparateConfigDatabase against a pre-existing
                          separate-topology stack; asserts a marker table planted in
                          edfi_configurationservice BEFORE the restore survives it.
      directory-feed      Restore WITHOUT -PackageDirectory, resolving the same package from a
                          local directory feed via DATABASE_TEMPLATE_FEED_URL +
                          DATABASE_TEMPLATE_NUGET_VERSION - the feed resolution + trust path
                          end to end without Azure. (The HTTP companion-package transport is
                          covered by mocked unit tests.)
      tampered-package    A byte-flipped copy of the .nupkg; asserts the restore fails closed
                          BEFORE any Docker activity (no compose containers exist afterwards).
      contaminated-package (PostgreSQL only) A re-signed package whose artifact carries an
                          injected extra schema that its manifest inventory also declares, so
                          staging and the candidate cross-check pass and the failure lands in
                          scratch validation's DMS-only gate; asserts the target database is
                          absent afterwards (fresh volume), no generated restore databases
                          remain, and no active .bootstrap workspace was created.
      running-stack       Attempts a restore while the stack is RUNNING; asserts the stop
                          proof refuses, naming the running containers.
      extension-selection Opt-in, -Wrapper published only, without -DataStandardVersion: derives a
                          core-only env from the run's env (SCHEMA_PACKAGES reduced to its core
                          package, the run's image tags kept), builds a core-only Minimal package
                          from a core-only source into its own directory, then (1) proves the
                          default package is refused under the core-only env by the package-to-
                          candidate cross-check, with no container started, the project's volumes
                          unchanged, and no workspace left behind, and (2) restores the core-only
                          package and asserts, besides every post-restore probe, that the active
                          workspace's staged packages equal the core-only env's, and that the
                          workspace, the restore manifest, and the target catalog each select
                          exactly the edfi project with one shared effective schema hash.
      populated           Opt-in: the package-directory shape built from a Populated-seeded
                          source, adding the non-descriptor document count probe and the schools
                          API read. Long.

    Prerequisites: an ISOLATED Docker daemon (see Foreign stacks); pwsh 7+; the .NET SDK (the
    smoke builds api-schema-tools); network access for the image builds and the source
    bootstrap's package downloads.

    Foreign stacks: before anything is created, stopped, or removed, the smoke inventories every
    container (running or stopped) and volume labelled with the dms-local or dms-published
    compose project. If any exist, it refuses and changes nothing - its teardowns run with -d -v
    and would destroy them - unless -ConfirmForeignStackRemoval is passed, which tears exactly
    those projects down and is recorded in the results. A refused run never reaches any teardown.
    That removal uses this run's engine compose files, so the smoke then inventories again. A
    remaining container refuses the run. A remaining volume is allowed only when its observed
    compose labels identify it as storage the OTHER engine's compose file declares (this run's
    teardowns do not declare it, so they leave it in place); it is recorded with that reason and
    re-inspected at the end of the run. Any other remaining volume - the selected engine's storage,
    an unknown volume, missing or conflicting labels, or one that cannot be inspected - refuses the
    run before any build, and that refusal makes no further Docker change.

    Provenance: after the preflight the smoke builds api-schema-tools (Debug, the configuration
    the bootstrap resolver prefers) and proves the resolver returns that exact executable, builds
    the DMS and CMS images itself (docker buildx build --load, with the Dockerfile, contexts, and
    VERSION argument of build-dms.ps1 / build-config.ps1 DockerBuild) under run-unique tags it
    first proves absent, takes each image's identity from the build's own --iidfile, forwards the
    run tags to the selected wrapper through a smoke-owned env file, removes only those tags at the
    end, and records what each started stack actually ran (docker inspect), the built package and
    its attestation, the package selection each started stack's active workspace staged (the
    workspace manifest's schema.selectedPackages, bound to the stack start that produced it), and
    the worktree revision and status at start and end. When a restore keeps an unchanged
    workspace, the smoke observes that retained staged selection; it does not independently prove
    the whole workspace tree equal. The results JSON classifies the run: it is final evidence only
    when every one of those was observed and matched, the worktree was clean throughout, and
    -ExploratoryPackage was not set; otherwise it lists the reasons it is not.

    Served data: every successful restore is also read back through the DMS API with a bearer
    token. The bootstrap admin client lists the CMS data stores; exactly one route-unqualified data
    store must target the restored database. A smoke application bound to it gets one DMS token,
    which every probe request against that stack reuses; a teardown or wrapper run discards it.
    academicSubjectDescriptors must answer HTTP 200 with a non-empty JSON array (with
    -SkipSourceSeed an empty array passes and is reported as schema-only), and the populated leg
    also reads schools. The CMS per-client token limit keeps its default. Endpoints, data-store
    selection, and read counts go to the results, each record tagged with its restore; tokens and
    secrets never do. A run is final evidence only when every successful restore its legs perform
    has exactly one complete seeded read of its own (the negative legs need none, and a
    -SkipSourceSeed run's schema-only reads never qualify).

    SourceIdentity: the source database's dms.DataStoreIdentity.SourceIdentity is read immediately
    before the producer builds each package and again after it, and both reads are bound to the
    built .nupkg's SHA-256 (each package fixture - default-minimal, core-only-minimal,
    default-populated - has its own package directory and binding). On the
    first successful restore of a package, while that stack is up, the smoke inspects the package
    independently: it replays (PostgreSQL) or restores (SQL Server) the artifact from that
    exact .nupkg into a run-owned scratch database restore_smoke_inspect_<12 hex>, selects the
    identity, and drops the database again, on failure too; it never reseeds it and never reads the
    identity from the artifact's text. The inspected identity must equal the bound pre-backup
    capture. Every successful restore must leave exactly one valid, nonzero UUID in the target,
    different from the package's identity and from every earlier restore's in the run. The results
    record each capture, inspection (with its cleanup), and restored identity, and a run is final
    evidence only when each successful restore its legs perform has that complete identity proof.

    Trust: the smoke NEVER bypasses attestation. It registers an ephemeral development
    producer (restore-smoke-<hex>) in the git-ignored local trust overlay via
    new-template-dev-trust.ps1 and removes exactly that producer again in the finally block,
    leaving a pre-existing overlay untouched.

.PARAMETER EnvironmentFile
    Env file forwarded to every phase. Defaults to eng/docker-compose/.env.example.

.PARAMETER DatabaseEngine
    postgresql (default) or mssql. The contaminated-package leg is PostgreSQL-only and is
    skipped with a warning on mssql.

.PARAMETER Leg
    Which legs to run, in order. Defaults to the core matrix:
    package-directory, separate-config, directory-feed, tampered-package,
    contaminated-package, running-stack. extension-selection and populated are opt-in.

.PARAMETER PackageVersion
    NuGet version for the locally built template package. Defaults to 1.0.999.

.PARAMETER StandardVersion
    Data Standard version segment for the package identity. Defaults to <DataStandardVersion>.0.

.PARAMETER DataStandardVersion
    5.2 (default) or 6.1. The local wrapper always receives it; the published wrapper receives it
    only when it is passed explicitly, because bootstrap-published-dms.ps1 composes the Data
    Standard overlay only for an explicit value.

.PARAMETER Wrapper
    local (default) or published: selects bootstrap-<wrapper>-dms.ps1, the matching
    start-<wrapper>-dms.ps1 teardown, and the dms-<wrapper> compose project the assertions use.

.PARAMETER ConfirmForeignStackRemoval
    Allows the run to tear down dms-local / dms-published containers and volumes it did not create.
    Pass only on an isolated host or with the stack owner's recorded approval. The run continues
    only when nothing remains afterwards except volumes identified as the other engine's storage.

.PARAMETER ExploratoryPackage
    Marks the run exploratory: its results are never classified as final evidence.

.PARAMETER SkipSourceSeed
    Build the source datastore without seeding (schema only). Faster; descriptor-content
    probes are skipped, and the API read accepts an empty descriptor array (reported as
    schema-only). The default seeds the Minimal template so the restored data is real.

.PARAMETER ResultsPath
    Optional path; if supplied, writes the run's JSON summary: status, steps (status + timings),
    provenance, and the final/non-final classification with its reasons.

.PARAMETER SkipTeardown
    Leaves the stack and workspaces in place after the run for interactive debugging. The
    ephemeral trust producer and the package work directory are still removed.

.EXAMPLE
    pwsh ./Invoke-BootstrapRestoreSmoke.ps1

.EXAMPLE
    pwsh ./Invoke-BootstrapRestoreSmoke.ps1 -DatabaseEngine mssql -Leg package-directory

.EXAMPLE
    pwsh ./Invoke-BootstrapRestoreSmoke.ps1 -Wrapper local -DataStandardVersion 6.1 -ResultsPath ./restore-smoke.json
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Manual smoke script intentionally writes operator progress and step banners to the console.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive: parameters are consumed inside nested script blocks and helper functions.')]
[CmdletBinding()]
param(
    [string]$EnvironmentFile,

    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine = "postgresql",

    [ValidateSet("package-directory", "separate-config", "directory-feed", "tampered-package", "contaminated-package", "running-stack", "extension-selection", "populated")]
    [string[]]$Leg = @("package-directory", "separate-config", "directory-feed", "tampered-package", "contaminated-package", "running-stack"),

    [string]$PackageVersion = "1.0.999",

    [string]$StandardVersion,

    [ValidateSet("5.2", "6.1")]
    [string]$DataStandardVersion = "5.2",

    [ValidateSet("local", "published")]
    [string]$Wrapper = "local",

    [switch]$SkipSourceSeed,

    [string]$ResultsPath,

    [switch]$SkipTeardown,

    [switch]$ConfirmForeignStackRemoval,

    [switch]$ExploratoryPackage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:DockerComposeRoot = Split-Path -Parent $PSScriptRoot
$script:RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $script:DockerComposeRoot "../.."))
$script:TemplatesRoot = Join-Path $script:RepoRoot "eng/DatabaseTemplates"
$script:BootstrapRoot = Join-Path $script:DockerComposeRoot ".bootstrap"
$script:RestoreWorkspaceRoot = Join-Path $script:DockerComposeRoot ".bootstrap-restore"
# The env file each schema selection's stacks start from: default is the image env (set by the
# build-images step); core-only is written by the extension-selection leg.
$script:SelectionEnvironmentFiles = @{}
$script:LocalTrustOverlayPath = Join-Path $script:DockerComposeRoot "template-trust-policy.local.json"
$script:StepResults = [System.Collections.Generic.List[pscustomobject]]::new()
$script:WorkDirectory = $null
$script:SmokeProducerName = $null
$script:SmokeProducerRegistered = $false

Import-Module (Join-Path $PSScriptRoot "RestoreSmokeProbes.psm1") -Force

$script:RunId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
# One DMS token per started stack (Test-RestoreSmokeApiRead); cleared by every teardown and wrapper run.
$script:ApiSession = New-RestoreSmokeApiSession
$script:WrapperProfile = Get-RestoreSmokeWrapperProfile -Wrapper $Wrapper
$script:DataStandardVersionSupplied = $PSBoundParameters.ContainsKey("DataStandardVersion")
$script:ResolvedStandardVersion = Resolve-RestoreSmokeStandardVersion -StandardVersion $StandardVersion -DataStandardVersion $DataStandardVersion
$script:CurrentStepName = $null
# The stack start the next observation belongs to: set by every wrapper run, cleared by every teardown.
$script:CurrentStackStart = $null
# No teardown, of any project, runs until the preflight has proven that no foreign stack exists
# (or the caller confirmed its removal). This includes the failure teardown in the finally block.
$script:TeardownAuthorized = $false
$script:TeardownWithheldReason = "the preflight did not authorize any Docker change"
$script:OriginalSchemaToolPath = $env:DMS_SCHEMA_TOOL_PATH
$script:Provenance = [ordered]@{
    RunId                        = $script:RunId
    StartedUtc                   = [System.DateTime]::UtcNow.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    Wrapper                      = $Wrapper
    DataStandardVersion          = $DataStandardVersion
    DataStandardVersionSupplied  = $script:DataStandardVersionSupplied
    DataStandardVersionForwarded = ($Wrapper -eq "local" -or $script:DataStandardVersionSupplied)
    StandardVersion              = $script:ResolvedStandardVersion
    DatabaseEngine               = $DatabaseEngine
    TargetDatabaseName           = $null
    Legs                         = @($Leg)
    ExploratoryPackage           = [bool]$ExploratoryPackage
    ForeignStack                 = $null
    SourceAtStart                = $null
    SourceAtEnd                  = $null
    SchemaTools                  = $null
    # Exists before the build step, so tag ownership and every build record survive a later
    # build's failure for cleanup and for the results.
    Images                       = (New-RestoreSmokeImageLedger)
    ForwardedImageKeys           = $null
    # One record per wrapper run (stack start), taken before the wrapper runs; each stack observation
    # names the start it belongs to.
    StackStarts                  = [System.Collections.Generic.List[object]]::new()
    StackObservations            = [System.Collections.Generic.List[object]]::new()
    Packages                     = [System.Collections.Generic.List[object]]::new()
    ApiReads                     = [System.Collections.Generic.List[object]]::new()
    # One pre-backup capture per built package, bound to its SHA-256; one independent inspection
    # per package a restore used; one restored identity per successful restore.
    SourceIdentityBindings       = [System.Collections.Generic.List[object]]::new()
    PackageInspections           = [System.Collections.Generic.List[object]]::new()
    RestoredIdentities           = [System.Collections.Generic.List[object]]::new()
    # The non-default selection envs the run derived, one selection proof per restore of such a
    # selection, and extension-selection's refusal of the default package under the core-only env.
    SelectionEnvironments        = [System.Collections.Generic.List[object]]::new()
    SelectionProofs              = [System.Collections.Generic.List[object]]::new()
    SelectionRefusals            = [System.Collections.Generic.List[object]]::new()
}

function Write-SmokeStep {
    param([string]$Label)

    $banner = "=" * 78
    Write-Host ""
    Write-Host $banner
    Write-Host "[restore-smoke] $Label"
    Write-Host $banner
}

function Invoke-SmokeStep {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Body
    )

    Write-SmokeStep $Name
    $script:CurrentStepName = $Name
    $startTime = Get-Date
    $status = "ok"
    $errorMessage = $null
    try {
        & $Body
    }
    catch {
        $status = "failed"
        $errorMessage = $_.Exception.Message
        throw
    }
    finally {
        $duration = (Get-Date) - $startTime
        $script:StepResults.Add([pscustomobject]@{
            Name = $Name
            Status = $status
            DurationSeconds = [math]::Round($duration.TotalSeconds, 2)
            Error = $errorMessage
        })
    }
}

function Resolve-SmokeEnvironmentFile {
    if ([string]::IsNullOrWhiteSpace($EnvironmentFile)) {
        return (Join-Path $script:DockerComposeRoot ".env.example")
    }
    if ([System.IO.Path]::IsPathRooted($EnvironmentFile)) {
        return $EnvironmentFile
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $EnvironmentFile))
}

function Get-EnvFileValue {
    param(
        [string]$Path,
        [string]$Key,
        [string]$DefaultValue = ""
    )

    if (Test-Path -LiteralPath $Path) {
        foreach ($line in Get-Content -LiteralPath $Path) {
            if ($line -match '^\s*#') { continue }
            if ($line -match "^\s*$([regex]::Escape($Key))\s*=\s*(.*)$") {
                return $matches[1].Trim().Trim('"').Trim("'")
            }
        }
    }
    return $DefaultValue
}

function Invoke-SmokeTeardown {
    param(
        [switch]$KeepVolumes,

        [pscustomobject]$WrapperProfile = $script:WrapperProfile
    )

    if (-not $script:TeardownAuthorized) {
        throw "Teardown of '$($WrapperProfile.ComposeProject)' was requested before the foreign-stack preflight authorized any Docker change."
    }
    Reset-RestoreSmokeApiSession -Session $script:ApiSession
    $script:CurrentStackStart = $null

    Push-Location $script:DockerComposeRoot
    try {
        $teardownArgs = @{ d = $true; EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = $DatabaseEngine }
        if (-not $KeepVolumes) {
            $teardownArgs.v = $true
            $teardownArgs.RemoveBootstrap = $true
        }
        & "$script:DockerComposeRoot/$($WrapperProfile.TeardownScriptName)" @teardownArgs
    }
    finally {
        Pop-Location
    }
}

function Invoke-SmokeSql {
    <#
    .SYNOPSIS
    One scalar-ish query against the live db container, engine-dispatched (the same transports
    the restore consumer uses).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$DatabaseName,

        [Parameter(Mandatory)]
        [string]$Query
    )

    $global:LASTEXITCODE = 0
    if ($DatabaseEngine -eq "mssql") {
        $saPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!"
        $output = docker exec -e "SQLCMDPASSWORD=$saPassword" dms-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d $DatabaseName -C -b -h -1 -W -Q $Query 2>&1
    }
    else {
        $output = docker exec dms-postgresql psql -U postgres -d $DatabaseName -tA -c $Query 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke SQL against '$DatabaseName' failed: $(($output | Out-String).Trim())"
    }
    return @($output | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { ([string]$_).Trim() })
}

function Test-SmokeDatabasePresent {
    param(
        [Parameter(Mandatory)]
        [string]$DatabaseName
    )

    if ($DatabaseEngine -eq "mssql") {
        $rows = Invoke-SmokeSql -DatabaseName "master" -Query "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'$DatabaseName') IS NOT NULL THEN 1 ELSE 0 END;"
        return (@($rows)[0] -eq "1")
    }
    $rows = Invoke-SmokeSql -DatabaseName "postgres" -Query "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName';"
    return (@($rows).Count -gt 0)
}

function Wait-SmokeDmsHealth {
    param(
        [int]$TimeoutSeconds = 180
    )

    $dmsPort = Get-EnvFileValue -Path $script:ResolvedEnvironmentFile -Key "DMS_HTTP_PORTS" -DefaultValue "8080"
    $healthUrl = "http://localhost:$dmsPort/health"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                Write-Host "[restore-smoke] DMS healthy at $healthUrl"
                Add-SmokeStackObservation
                return
            }
        }
        catch {
            Start-Sleep -Seconds 3
        }
    }
    throw "DMS did not report healthy at $healthUrl within $TimeoutSeconds seconds."
}

function Add-SmokeStackObservation {
    <#
    .SYNOPSIS
    Records what the healthy stack actually runs (images per service) and the package selection
    its active workspace staged (Read-RestoreSmokeStagedSelection), labelled with the current step
    and bound to the stack start that produced it. A restore leaves no derived env file in the
    workspace, so the staged selection is read from the committed workspace manifest.
    #>
    $observation = Get-RestoreSmokeStackObservation -ComposeProject $script:WrapperProfile.ComposeProject -Label ([string]$script:CurrentStepName)
    $stackStart = $null
    if ($null -ne $script:CurrentStackStart) {
        $stackStart = $script:CurrentStackStart.StackStart
    }
    $observation | Add-Member -NotePropertyName StackStart -NotePropertyValue $stackStart
    $observation | Add-Member -NotePropertyName StagedSelection -NotePropertyValue (Read-RestoreSmokeStagedSelection -BootstrapRoot $script:BootstrapRoot)
    $script:Provenance.StackObservations.Add($observation)
}

function Get-SmokeEnvironmentSelection {
    # The schema selection whose env file a wrapper run passes: a non-default selection's own file,
    # otherwise "default" (the image env and the files derived from it with its SCHEMA_PACKAGES).
    param(
        [Parameter(Mandatory)]
        [string]$EnvironmentFile
    )

    $fullPath = [System.IO.Path]::GetFullPath($EnvironmentFile)
    foreach ($pair in $script:SelectionEnvironmentFiles.GetEnumerator()) {
        if ([System.IO.Path]::GetFullPath([string]$pair.Value) -eq $fullPath) {
            return [string]$pair.Key
        }
    }
    return "default"
}

function Invoke-RestoreWrapper {
    <#
    .SYNOPSIS
    Runs the selected bootstrap wrapper with the given arguments (plus the Data Standard selection
    rule) from the docker-compose directory, with the stale-exit-code hygiene the wrapper suites use.
    #>
    param(
        [Parameter(Mandatory)]
        [hashtable]$Arguments
    )

    $wrapperArguments = Get-RestoreSmokeWrapperArgumentSet `
        -Arguments $Arguments `
        -Wrapper $Wrapper `
        -DataStandardVersion $DataStandardVersion `
        -DataStandardVersionSupplied $script:DataStandardVersionSupplied

    # Recorded before the wrapper runs, so the workspace it finds and the start time precede
    # anything this run stages; the stack observation that follows is bound to it.
    $environmentFile = $script:ResolvedEnvironmentFile
    if ($Arguments.ContainsKey("EnvironmentFile")) {
        $environmentFile = [string]$Arguments.EnvironmentFile
    }
    $script:CurrentStackStart = New-RestoreSmokeStackStart `
        -Sequence ($script:Provenance.StackStarts.Count + 1) `
        -Label ([string]$script:CurrentStepName) `
        -Selection (Get-SmokeEnvironmentSelection -EnvironmentFile $environmentFile) `
        -EnvironmentFile $environmentFile `
        -BootstrapRoot $script:BootstrapRoot `
        -Restore:($Arguments.ContainsKey("RestoreTemplate"))
    $script:Provenance.StackStarts.Add($script:CurrentStackStart)

    Reset-RestoreSmokeApiSession -Session $script:ApiSession
    Push-Location $script:DockerComposeRoot
    try {
        $global:LASTEXITCODE = 0
        & "$script:DockerComposeRoot/$($script:WrapperProfile.BootstrapScriptName)" @wrapperArguments
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
            throw "$($script:WrapperProfile.BootstrapScriptName) failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Get-SmokePackageDirectory {
    <#
    .SYNOPSIS
    The work-directory folder holding one package fixture's built package: each fixture has its own
    (the default and core-only Minimal packages share a template kind), so a run that builds
    several never mixes their packages or their evidence.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackageFixture
    )

    return (Join-Path $script:WorkDirectory (Get-RestoreSmokePackageFixture -Name $PackageFixture).DirectoryName)
}

function Get-SmokeSelectionEnvironmentFile {
    <#
    .SYNOPSIS
    The env file a schema selection's stacks start from. Every one derives from the image env, so
    the run's image tags reach every stack.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Selection
    )

    if ($Selection -eq "default") {
        return $script:ResolvedEnvironmentFile
    }
    if (-not $script:SelectionEnvironmentFiles.ContainsKey($Selection)) {
        throw "No env file was written for the '$Selection' schema selection."
    }
    return $script:SelectionEnvironmentFiles[$Selection]
}

function Get-SmokePackageSha256 {
    # The SHA-256 of the one template .nupkg in a package directory.
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory
    )

    $packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter "*.nupkg" -File | Where-Object { $_.Name -notlike "*.Attestation.*" })
    if ($packages.Count -ne 1) {
        throw "Expected exactly one template .nupkg in '$PackageDirectory', found $($packages.Count)."
    }
    return [pscustomobject]@{ PackageFile = $packages[0].Name; PackagePath = $packages[0].FullName; Sha256 = (Get-RestoreSmokeFileSha256 -Path $packages[0].FullName) }
}

function Assert-RestoredSelection {
    <#
    .SYNOPSIS
    The selection proof for one successful restore of a non-default selection: records, under
    -RestoreExecution and before judging, what the active workspace staged and selected, what the
    restored target's catalog holds, and where the running Configuration Service keeps its data (its
    container's effective configuration), then throws on any defect Get-RestoreSmokeSelectionDefect
    finds against the fixture's projects, the selection env's packages, the restored package's restore
    manifest (its package record, by SHA-256 and fixture), and that topology.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RestoreExecution,

        [Parameter(Mandatory)]
        [string]$PackageFixture,

        [Parameter(Mandatory)]
        [string]$PackageDirectory
    )

    $fixture = Get-RestoreSmokePackageFixture -Name $PackageFixture
    $package = Get-SmokePackageSha256 -PackageDirectory $PackageDirectory
    $proof = [pscustomobject]@{
        RestoreExecution = $RestoreExecution
        PackageFixture   = $fixture.Name
        Label            = [string]$script:CurrentStepName
        PackageFile      = $package.PackageFile
        PackageSha256    = $package.Sha256
        Workspace        = (Read-RestoreSmokeWorkspaceSelection -BootstrapRoot $script:BootstrapRoot)
        Catalog          = (Get-RestoreSmokeCatalogSelection -DatabaseEngine $DatabaseEngine -DatabaseName $script:TargetDatabaseName)
        ConfigTopology   = (Get-RestoreSmokeConfigTopologyEvidence -ComposeProject $script:WrapperProfile.ComposeProject)
    }
    $script:Provenance.SelectionProofs.Add($proof)

    $environments = @($script:Provenance.SelectionEnvironments | Where-Object { $_.Selection -ceq $fixture.Selection })
    if ($environments.Count -ne 1) {
        throw "Restore ${RestoreExecution}: expected one recorded $($fixture.Selection) env, found $($environments.Count)."
    }
    $packageRecords = @($script:Provenance.Packages | Where-Object { $_.Sha256 -ceq $package.Sha256 -and $_.PackageFixture -ceq $fixture.Name })
    $packageRecord = $null
    if ($packageRecords.Count -eq 1) {
        $packageRecord = $packageRecords[0]
    }
    $defects = @(Get-RestoreSmokeSelectionDefect -Proof $proof -Package $packageRecord -ExpectedProject ([string[]]@($fixture.ProjectSchemas)) -ExpectedPackage ([string[]]@($environments[0].SelectedPackages)) -DatabaseEngine $DatabaseEngine)
    if ($defects.Count -gt 0) {
        throw "Restore ${RestoreExecution} did not take the $($fixture.Selection) selection: $($defects -join '; ')."
    }
    $topology = Resolve-RestoreSmokeConfigTopology -Evidence $proof.ConfigTopology -DatabaseEngine $DatabaseEngine -TargetDatabaseName $script:TargetDatabaseName
    Write-Host "[restore-smoke] restore $RestoreExecution selection: workspace, restore manifest, and catalog select $(@($fixture.ProjectSchemas) -join ', ') with effective schema hash $($proof.Catalog.EffectiveSchemaHash); Configuration Service topology $($topology.Topology), catalog Configuration Service schemas [$(@($proof.Catalog.ConfigurationServiceSchemas) -join ', ')]"
}

function Assert-SmokeSelectionRefusal {
    <#
    .SYNOPSIS
    extension-selection's negative: restores the default package under the core-only env and
    records, before judging, the project's containers and volumes, the active workspace, and the
    restore candidates before and after the attempt and the refusal message. Throws on any defect
    Get-RestoreSmokeSelectionRefusalDefect finds: the refusal must be the package-to-candidate
    cross-check's, and nothing may have started or been left behind.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory,

        [Parameter(Mandatory)]
        [string]$EnvironmentFile
    )

    $package = Get-SmokePackageSha256 -PackageDirectory $PackageDirectory
    $refusal = [pscustomobject]@{
        Leg                  = "extension-selection"
        PackageFixture       = "default-minimal"
        EnvironmentSelection = "core-only"
        Label                = [string]$script:CurrentStepName
        PackageFile          = $package.PackageFile
        PackageSha256        = $package.Sha256
        Before               = $null
        After                = $null
        Refused              = $false
        Message              = $null
    }
    $script:Provenance.SelectionRefusals.Add($refusal)

    $stateArguments = @{ ComposeProject = $script:WrapperProfile.ComposeProject; BootstrapRoot = $script:BootstrapRoot; RestoreWorkspaceRoot = $script:RestoreWorkspaceRoot }
    $refusal.Before = Get-RestoreSmokeRefusalState @stateArguments
    try {
        Invoke-RestoreWrapper -Arguments @{
            EnvironmentFile  = $EnvironmentFile
            DatabaseEngine   = $DatabaseEngine
            RestoreTemplate  = "Minimal"
            PackageDirectory = $PackageDirectory
        }
    }
    catch {
        $refusal.Refused = $true
        $refusal.Message = ConvertTo-RestoreSmokeLogSafeText $_.Exception.Message
    }
    $refusal.After = Get-RestoreSmokeRefusalState @stateArguments

    $defaultPackage = @($script:Provenance.Packages | Where-Object { $_.PackageFixture -ceq "default-minimal" })
    $coreOnlyPackage = @($script:Provenance.Packages | Where-Object { $_.PackageFixture -ceq "core-only-minimal" })
    $defects = @(Get-RestoreSmokeSelectionRefusalDefect -Refusal $refusal -DefaultPackage ($defaultPackage | Select-Object -First 1) -SelectedPackage ($coreOnlyPackage | Select-Object -First 1))
    if ($defects.Count -gt 0) {
        throw "The default package under the core-only env did not fail in the restore preflight as required: $($defects -join '; ')."
    }
    Write-Host "[restore-smoke] default package refused under the core-only env before any container started: $($refusal.Message)"
}

function Assert-RestoredSourceIdentity {
    <#
    .SYNOPSIS
    The SourceIdentity proof for one successful restore. The target must hold exactly one valid,
    nonzero UUID that differs from the identity of the package it was restored from and from every
    earlier restore's identity in this run. The package identity comes from an independent
    inspection of the exact .nupkg in -PackageDirectory (once per package SHA-256, on its first
    restore, while this stack is up) and must equal the pre-backup capture bound to that SHA-256.
    The target read is recorded under -RestoreExecution before anything is judged.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RestoreExecution,

        [Parameter(Mandatory)]
        [string]$PackageFixture,

        [Parameter(Mandatory)]
        [string]$PackageDirectory
    )

    $fixture = Get-RestoreSmokePackageFixture -Name $PackageFixture
    $packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter "*.nupkg" -File | Where-Object { $_.Name -notlike "*.Attestation.*" })
    if ($packages.Count -ne 1) {
        throw "Restore ${RestoreExecution}: expected exactly one template .nupkg in '$PackageDirectory', found $($packages.Count)."
    }
    $packageSha256 = Get-RestoreSmokeFileSha256 -Path $packages[0].FullName

    $earlierRestores = $script:Provenance.RestoredIdentities.ToArray()
    $targetRead = Get-RestoreSmokeSourceIdentityRead -DatabaseEngine $DatabaseEngine -DatabaseName $script:TargetDatabaseName
    $script:Provenance.RestoredIdentities.Add([pscustomobject]@{
            RestoreExecution = $RestoreExecution
            PackageFixture   = $fixture.Name
            TemplateKind     = $fixture.TemplateKind
            Label            = [string]$script:CurrentStepName
            TargetDatabase   = $script:TargetDatabaseName
            PackageFile      = $packages[0].Name
            PackageSha256    = $packageSha256
            ExitCode         = $targetRead.ExitCode
            Rows             = $targetRead.Rows
            Identity         = $targetRead.Identity
            Reason           = $targetRead.Reason
        })
    if ($targetRead.ExitCode -ne 0) {
        throw "Restore ${RestoreExecution}: $($targetRead.Reason)"
    }

    $inspections = @($script:Provenance.PackageInspections | Where-Object { $_.PackageSha256 -ceq $packageSha256 })
    if ($inspections.Count -eq 0) {
        $inspection = Invoke-RestoreSmokePackageInspection `
            -PackagePath $packages[0].FullName `
            -TemplateKind $fixture.TemplateKind `
            -DatabaseEngine $DatabaseEngine `
            -WorkDirectory $script:WorkDirectory `
            -RestoreManifestFileName (Get-RestoreManifestFileName)
        $script:Provenance.PackageInspections.Add($inspection)
        Write-Host "[restore-smoke] inspected $($inspection.PackageFile) in $($inspection.InspectionDatabase): SourceIdentity $($inspection.Identity); cleanup complete=$($inspection.Cleanup.Complete)"
        $inspections = @($inspection)
    }
    $packageInspection = $inspections[0]
    if (-not [string]::IsNullOrWhiteSpace($packageInspection.Reason)) {
        throw "Restore ${RestoreExecution}: the independent inspection of $($packages[0].Name) failed: $($packageInspection.Reason)"
    }
    if ($packageInspection.Cleanup.Complete -ne $true) {
        throw "Restore ${RestoreExecution}: the package inspection's cleanup did not complete: $($packageInspection.Cleanup.Reasons -join '; ')"
    }

    $bindings = @($script:Provenance.SourceIdentityBindings | Where-Object { $_.PackageSha256 -ceq $packageSha256 })
    if ($bindings.Count -ne 1 -or $bindings[0].Bound -ne $true) {
        throw "Restore ${RestoreExecution}: no bound pre-backup SourceIdentity capture exists for $($packages[0].Name) (SHA-256 $packageSha256); the package changed after it was built, or its capture failed."
    }
    if ($bindings[0].BeforeBackup.Identity -cne $packageInspection.Identity) {
        throw "Restore ${RestoreExecution}: the package's inspected SourceIdentity $($packageInspection.Identity) differs from the pre-backup capture $($bindings[0].BeforeBackup.Identity) bound to its SHA-256."
    }

    $defects = @(Get-RestoreSmokeRestoredIdentityDefect -Row $targetRead.Rows -PackageIdentity $packageInspection.Identity -EarlierRestore $earlierRestores)
    if ($defects.Count -gt 0) {
        throw "Restore ${RestoreExecution}: $($defects -join '; ')."
    }
    Write-Host "[restore-smoke] restore $RestoreExecution SourceIdentity $($targetRead.Identity) differs from the package's $($packageInspection.Identity) and from $($earlierRestores.Count) earlier restore(s)"
}

function Assert-RestoredDatastore {
    <#
    .SYNOPSIS
    The post-restore probes: DMS health, the dms.EffectiveSchema singleton, (when the source
    was seeded) at least one restored descriptor row, (Populated) non-descriptor documents, the
    SourceIdentity proof against the package in -PackageDirectory, for a non-default schema
    selection the selection proof, and the authenticated served-data read through the DMS API of the
    stack -EnvironmentFile started. Every record carries -RestoreExecution (the restore's id from
    Get-RestoreSmokeRestoreExecutionId), whose package fixture - and so template kind and selection -
    comes from the same execution map the classifier uses.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RestoreExecution,

        # The directory the restore resolved its package from (-PackageDirectory or the feed).
        [Parameter(Mandatory)]
        [string]$PackageDirectory,

        [string]$EnvironmentFile = $script:ResolvedEnvironmentFile
    )

    $fixture = Get-RestoreSmokeRestoreExecutionFixture -RestoreExecution $RestoreExecution
    $RequirePopulatedData = $fixture.TemplateKind -eq "Populated"
    Wait-SmokeDmsHealth

    $effectiveSchemaCountQuery = if ($DatabaseEngine -eq "mssql") {
        "SET NOCOUNT ON; SELECT COUNT(*) FROM [dms].[EffectiveSchema];"
    }
    else {
        'SELECT COUNT(*) FROM dms."EffectiveSchema";'
    }
    $effectiveSchemaCount = [int](@(Invoke-SmokeSql -DatabaseName $script:TargetDatabaseName -Query $effectiveSchemaCountQuery)[0])
    if ($effectiveSchemaCount -ne 1) {
        throw "Restored target '$script:TargetDatabaseName' has $effectiveSchemaCount dms.EffectiveSchema rows; expected exactly 1."
    }

    if (-not $SkipSourceSeed) {
        $descriptorCountQuery = if ($DatabaseEngine -eq "mssql") {
            "SET NOCOUNT ON; SELECT COUNT(*) FROM [dms].[Descriptor];"
        }
        else {
            'SELECT COUNT(*) FROM dms."Descriptor";'
        }
        $descriptorCount = [int](@(Invoke-SmokeSql -DatabaseName $script:TargetDatabaseName -Query $descriptorCountQuery)[0])
        if ($descriptorCount -lt 1) {
            throw "Restored target '$script:TargetDatabaseName' has no descriptor rows; the template content did not survive the restore."
        }
        Write-Host "[restore-smoke] restored descriptor rows: $descriptorCount"
    }

    if ($RequirePopulatedData) {
        $populatedCountQuery = if ($DatabaseEngine -eq "mssql") {
            "SET NOCOUNT ON; SELECT COUNT(*) FROM [dms].[Document] d JOIN [dms].[ResourceKey] rk ON rk.[ResourceKeyId] = d.[ResourceKeyId] WHERE rk.[ResourceName] NOT LIKE '%Descriptor' AND rk.[ResourceName] NOT LIKE '%SchoolYear%';"
        }
        else {
            'SELECT COUNT(*) FROM dms."Document" d JOIN dms."ResourceKey" rk ON rk."ResourceKeyId" = d."ResourceKeyId" WHERE rk."ResourceName" NOT LIKE ' + "'%Descriptor' AND rk.`"ResourceName`" NOT ILIKE '%SchoolYear%';"
        }
        $populatedCount = [int](@(Invoke-SmokeSql -DatabaseName $script:TargetDatabaseName -Query $populatedCountQuery)[0])
        if ($populatedCount -lt 1) {
            throw "Restored target '$script:TargetDatabaseName' has no non-descriptor documents; the Populated template content did not survive."
        }
        Write-Host "[restore-smoke] restored populated documents: $populatedCount"
    }

    Assert-RestoredSourceIdentity -RestoreExecution $RestoreExecution -PackageFixture $fixture.Name -PackageDirectory $PackageDirectory
    if ($fixture.Selection -ne "default") {
        Assert-RestoredSelection -RestoreExecution $RestoreExecution -PackageFixture $fixture.Name -PackageDirectory $PackageDirectory
    }

    $apiEndpoint = Resolve-RestoreSmokeApiEndpoint -EnvironmentFile $EnvironmentFile
    $apiRead = Test-RestoreSmokeApiRead `
        -Session $script:ApiSession `
        -Endpoint $apiEndpoint `
        -TargetDatabaseName $script:TargetDatabaseName `
        -DatabaseEngine $DatabaseEngine `
        -RequirePopulatedData:$RequirePopulatedData `
        -SchemaOnly:$SkipSourceSeed
    $apiRead | Add-Member -NotePropertyName RestoreExecution -NotePropertyValue $RestoreExecution
    $apiRead | Add-Member -NotePropertyName Label -NotePropertyValue ([string]$script:CurrentStepName)
    $script:Provenance.ApiReads.Add($apiRead)
    $readSummary = ($apiRead.Reads | ForEach-Object { "$($_.Resource)=$($_.Count)" }) -join ", "
    Write-Host "[restore-smoke] served-data API read for restore $RestoreExecution ($($apiRead.Mode)): data store $($apiRead.DataStoreId), HTTP 200, $readSummary (token reused: $($apiRead.TokenReused))"
}

function Build-SmokeSourceAndPackage {
    <#
    .SYNOPSIS
    Bootstraps a source datastore from the fixture's selection env, builds the fixture's attested
    template package from it into the fixture's own work-directory folder, and tears the stack down
    to fresh volumes.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackageFixture
    )

    $fixture = Get-RestoreSmokePackageFixture -Name $PackageFixture
    $TemplateKind = $fixture.TemplateKind
    $sourceEnvironmentFile = Get-SmokeSelectionEnvironmentFile -Selection $fixture.Selection

    Invoke-SmokeStep -Name "build-source-datastore-$($fixture.Name)" -Body {
        # The SOURCE stack runs separate topology: the producer's DMS-only gate requires a
        # dedicated DMS datastore, and the default shared topology would put the Configuration
        # Service's dmscs schema and OpenIddict identity state into the very database the
        # template is dumped from (the gate refuses exactly that - proven by this smoke's
        # development history).
        $sourceArgs = @{ EnvironmentFile = $sourceEnvironmentFile; DatabaseEngine = $DatabaseEngine; SeparateConfigDatabase = $true }
        if (-not $SkipSourceSeed) {
            $sourceArgs.LoadSeedData = $true
            $sourceArgs.SeedTemplate = $TemplateKind
        }
        Invoke-RestoreWrapper -Arguments $sourceArgs
        Wait-SmokeDmsHealth
    }

    Invoke-SmokeStep -Name "build-attested-package-$($fixture.Name)" -Body {
        $packageDirectory = Get-SmokePackageDirectory -PackageFixture $fixture.Name
        New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
        Import-Module (Join-Path $script:TemplatesRoot "Template-RestoreCore.psm1") -Force

        # The source's SourceIdentity is read immediately before the producer runs and again after
        # it, and both reads are bound to the SHA-256 of the exact .nupkg this build leaves.
        $bound = Invoke-RestoreSmokeIdentityBoundPackageBuild `
            -PackageFixture $fixture.Name `
            -DatabaseEngine $DatabaseEngine `
            -SourceDatabaseName $script:TargetDatabaseName `
            -PackageDirectory $packageDirectory `
            -RestoreManifestFileName (Get-RestoreManifestFileName) `
            -BindingList $script:Provenance.SourceIdentityBindings `
            -PackageList $script:Provenance.Packages `
            -BuildPackage {
            Push-Location $script:TemplatesRoot
            try {
                Import-Module (Join-Path $script:TemplatesRoot "Template-Management.psm1") -Force
                $configFileName = if ($TemplateKind -eq "Populated") { "./PopulatedTemplateSettings.psd1" } else { "./MinimalTemplateSettings.psd1" }
                Build-TemplateNuGetPackage `
                    -ConfigFilePath $configFileName `
                    -StandardVersion $script:ResolvedStandardVersion `
                    -PackageVersion $PackageVersion `
                    -TemplateKind $TemplateKind `
                    -DatabaseName $script:TargetDatabaseName `
                    -DumpAllUserSchemas `
                    -DatabaseEngine $DatabaseEngine `
                    -AttestationSignerKeyPath $script:SmokeSignerKeyPath `
                    -AttestationProducer $script:SmokeProducerName

                # Collect the outputs into the private work directory and clear the build artifacts
                # out of the repo tree (including the transient restore-manifest.json the producer
                # writes beside them).
                $transientManifestPath = Join-Path $script:TemplatesRoot "restore-manifest.json"
                if (Test-Path -LiteralPath $transientManifestPath) {
                    Remove-Item -LiteralPath $transientManifestPath -Force
                }
                foreach ($builtFile in @(Get-ChildItem -Path $script:TemplatesRoot -File |
                            Where-Object { $_.Name -like "EdFi.Api.$TemplateKind.Template.*" })) {
                    if ($builtFile.Extension -in @(".nupkg", ".json")) {
                        Move-Item -LiteralPath $builtFile.FullName -Destination (Join-Path $packageDirectory $builtFile.Name) -Force
                    }
                    else {
                        Remove-Item -LiteralPath $builtFile.FullName -Force
                    }
                }
            }
            finally {
                Pop-Location
            }

            $builtPackages = @(Get-ChildItem -Path $packageDirectory -Filter "*.nupkg" | Where-Object { $_.Name -notlike "*.Attestation.*" })
            if ($builtPackages.Count -ne 1) {
                throw "Expected exactly one built template .nupkg in '$packageDirectory', found $($builtPackages.Count)."
            }
            $attestations = @(Get-ChildItem -Path $packageDirectory -Filter "*.nupkg.attestation.json")
            if ($attestations.Count -ne 1) {
                throw "Expected exactly one sibling attestation document in '$packageDirectory', found $($attestations.Count)."
            }
            Write-Host "[restore-smoke] built $($builtPackages[0].Name) + attestation"
        }
        Write-Host "[restore-smoke] source SourceIdentity $($bound.Binding.BeforeBackup.Identity) bound to $($bound.Package.PackageFile) (SHA-256 $($bound.Package.Sha256))"
    }

    Invoke-SmokeStep -Name "teardown-source-stack-$($fixture.Name)" -Body {
        Invoke-SmokeTeardown
    }
}

# =============================================================================
# Run
# =============================================================================

$script:BaseEnvironmentFile = Resolve-SmokeEnvironmentFile
$script:ResolvedEnvironmentFile = $script:BaseEnvironmentFile
$targetKey = if ($DatabaseEngine -eq "mssql") { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }
$script:TargetDatabaseName = Get-EnvFileValue -Path $script:ResolvedEnvironmentFile -Key $targetKey -DefaultValue "edfi_datamanagementservice"
$script:Provenance.TargetDatabaseName = $script:TargetDatabaseName
$script:Provenance.SourceAtStart = Get-RestoreSmokeSourceRevision -RepoRoot $script:RepoRoot
$packageDirectoryPath = $null
$exitCode = 0

Write-Host ("[restore-smoke] revision={0} clean={1} wrapper={2} dataStandard={3} (forwarded={4}) standardVersion={5} engine={6} exploratory={7}" -f `
        $script:Provenance.SourceAtStart.Revision, $script:Provenance.SourceAtStart.Clean, $Wrapper, $DataStandardVersion,
        $script:Provenance.DataStandardVersionForwarded, $script:ResolvedStandardVersion, $DatabaseEngine, [bool]$ExploratoryPackage)

try {
    Invoke-SmokeStep -Name "preflight" -Body {
        # A leg selection the run cannot perform as specified is refused before any Docker call.
        Assert-RestoreSmokeLegSelection -Leg $Leg -Wrapper $Wrapper -DataStandardVersionSupplied $script:DataStandardVersionSupplied

        $global:LASTEXITCODE = 0
        docker info --format '{{.ServerVersion}}' | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Docker daemon is not available; the restore smoke requires a running Docker engine."
        }

        # Read-only inventory BEFORE anything is created, stopped, or removed.
        $inventory = Get-RestoreSmokeForeignStackInventory
        $script:Provenance.ForeignStack = [ordered]@{
            Inventory        = $inventory.Projects
            RemovalConfirmed = [bool]$ConfirmForeignStackRemoval
            RemovedProjects  = [System.Collections.Generic.List[string]]::new()
            RemainingAfterRemoval = $null
            EngineVolumeDefinitions = $null
            LeftoverVolumes  = $null
            AllowedVolumesAtEnd = $null
        }
        Assert-RestoreSmokeNoForeignStack -Inventory $inventory -ConfirmForeignStackRemoval:$ConfirmForeignStackRemoval
        $engineVolumes = $null
        if ($inventory.HasForeignState) {
            # Read before anything is removed: without the repository's engine-volume definitions,
            # what the removal leaves behind could not be identified.
            $engineVolumes = Get-RestoreSmokeEngineVolumeDefinition -DockerComposeRoot $script:DockerComposeRoot
            $script:Provenance.ForeignStack.EngineVolumeDefinitions = $engineVolumes
        }
        $script:TeardownAuthorized = $true

        $script:WorkDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "dms-restore-smoke-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:WorkDirectory -Force | Out-Null
        Write-Host "[restore-smoke] work directory: $script:WorkDirectory"
        Write-Host "[restore-smoke] engine=$DatabaseEngine target=$script:TargetDatabaseName legs=$($Leg -join ', ')"

        if ($inventory.HasForeignState) {
            Write-Host "[restore-smoke] -ConfirmForeignStackRemoval: tearing down $(Format-RestoreSmokeForeignStackInventory -Inventory $inventory)"
        }
        foreach ($foreignProject in $inventory.ForeignProjects) {
            Invoke-SmokeTeardown -WrapperProfile (Get-RestoreSmokeWrapperProfile -Wrapper ($foreignProject -replace '^dms-', ''))
            $script:Provenance.ForeignStack.RemovedProjects.Add($foreignProject)
        }
        if ($script:WrapperProfile.ComposeProject -notin $inventory.ForeignProjects) {
            Invoke-SmokeTeardown
        }

        if ($inventory.HasForeignState) {
            # The teardown uses this run's engine compose files, so another engine's containers or
            # volumes in the same project can survive it. Authorization is suspended while what
            # remains is identified: nothing has been created since the removal, so a refusal here
            # leaves the remaining state as found, and the failure teardown (-d -v) cannot remove a
            # volume this check did not identify as safe.
            $script:TeardownAuthorized = $false
            $script:TeardownWithheldReason = "the confirmed removal left Docker state the preflight did not accept; it is left as found"
            $remaining = Get-RestoreSmokeForeignStackInventory
            $script:Provenance.ForeignStack.RemainingAfterRemoval = $remaining.Projects
            $leftoverVolumes = Get-RestoreSmokeLeftoverVolumeClassification -Inventory $remaining -DatabaseEngine $DatabaseEngine -Definition $engineVolumes
            $script:Provenance.ForeignStack.LeftoverVolumes = $leftoverVolumes
            if (@($remaining.Projects | ForEach-Object { $_.Containers }).Count -gt 0) {
                throw "The confirmed removal left containers in place: $(Format-RestoreSmokeForeignStackInventory -Inventory $remaining). Remove them before rerunning."
            }
            if ($leftoverVolumes.Blocked.Count -gt 0) {
                throw ("The confirmed removal left volumes this run cannot identify as unrelated to its $DatabaseEngine storage: " +
                    (Format-RestoreSmokeLeftoverVolumeClassification -Classification $leftoverVolumes -Decision blocked) +
                    ". Nothing further was stopped or removed. Account for them with their owner before rerunning.")
            }
            foreach ($volume in $leftoverVolumes.Allowed) {
                Write-Host "[restore-smoke] leaving volume $($volume.Name) in place: $($volume.Reason)"
            }
            $script:TeardownAuthorized = $true
        }
    }

    Invoke-SmokeStep -Name "build-schema-tools" -Body {
        Import-Module (Join-Path $script:DockerComposeRoot "bootstrap-schema-tool.psm1")
        $schemaToolProject = Join-Path $script:RepoRoot "src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"
        $schemaToolRecord = Invoke-RestoreSmokeSchemaToolBuild `
            -ProjectPath $schemaToolProject `
            -Resolver { param($RequestedPath) Resolve-DmsSchemaTool -RequestedPath $RequestedPath }
        $script:Provenance.SchemaTools = $schemaToolRecord
        Write-Host "[restore-smoke] $($schemaToolRecord.BuildCommand) -> exit $($schemaToolRecord.BuildExitCode)"
        if (-not $schemaToolRecord.Verified) {
            throw "SchemaTools provenance could not be established: $($schemaToolRecord.Reason)"
        }
        # Phases that read DMS_SCHEMA_TOOL_PATH use the same executable the resolver returns.
        $env:DMS_SCHEMA_TOOL_PATH = $schemaToolRecord.ExecutablePath
        Write-Host "[restore-smoke] api-schema-tools $($schemaToolRecord.InformationalVersion) sha256=$($schemaToolRecord.Sha256AtBuild)"
    }

    Invoke-SmokeStep -Name "build-images" -Body {
        $images = $script:Provenance.Images
        $imagePlan = Get-RestoreSmokeImageBuildPlan -RepoRoot $script:RepoRoot -WorkDirectory $script:WorkDirectory -RunId $script:RunId -Wrapper $Wrapper
        Register-RestoreSmokeImageTagOwnership -Plan $imagePlan -Ledger $images
        Write-Host "[restore-smoke] run tags claimed (absent before the build): $($images.OwnedTags -join ', ')"
        try {
            Invoke-RestoreSmokeImageBuild -Plan $imagePlan -Ledger $images
        }
        finally {
            foreach ($key in @("Dms", "Config")) {
                $record = $images[$key]
                if ($null -ne $record) {
                    Write-Host "[restore-smoke] $key image: exit $($record.ExitCode); verified=$($record.Verified); $($record.ImageId) $($record.Reason)"
                }
            }
        }

        $imageEnvironmentFile = Join-Path $script:WorkDirectory ".env.smoke-images"
        if ($Wrapper -eq "published") {
            # published-dms.yml pins the edfialliance/ed-fi-api repository and takes only the tag.
            $publishedReference = [string]$images.Dms.Tags[-1]
            $publishedTag = $publishedReference.Substring($publishedReference.LastIndexOf(":") + 1)
            $forwarded = Write-RestoreSmokeImageEnvironmentFile -BaseEnvironmentFile $script:BaseEnvironmentFile -TargetPath $imageEnvironmentFile `
                -Wrapper published -PublishedDmsTag $publishedTag -ConfigImage $images.Config.Tags[0]
        }
        else {
            $forwarded = Write-RestoreSmokeImageEnvironmentFile -BaseEnvironmentFile $script:BaseEnvironmentFile -TargetPath $imageEnvironmentFile `
                -Wrapper local -DmsImage $images.Dms.Tags[0] -ConfigImage $images.Config.Tags[0]
        }
        $script:Provenance.ForwardedImageKeys = $forwarded
        $script:ResolvedEnvironmentFile = $imageEnvironmentFile
        Write-Host "[restore-smoke] forwarding the in-run images through $imageEnvironmentFile"
    }

    Invoke-SmokeStep -Name "register-ephemeral-dev-trust" -Body {
        # A unique producer per run: a pre-existing developer overlay is never touched beyond
        # the additive entry this run removes again in the finally block. There is no
        # trust bypass anywhere in the restore branch - the smoke signs for real.
        $script:SmokeProducerName = "restore-smoke-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
        $trustResult = & (Join-Path $script:TemplatesRoot "new-template-dev-trust.ps1") `
            -Purpose Dev `
            -ProducerName $script:SmokeProducerName `
            -KeyDirectory $script:WorkDirectory
        $script:SmokeSignerKeyPath = $trustResult.PrivateKeyPath
        $script:SmokeProducerRegistered = $true
        Write-Host "[restore-smoke] registered producer '$script:SmokeProducerName'"
    }

    $legSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$Leg, [System.StringComparer]::OrdinalIgnoreCase)
    $neededFixtures = @(Get-RestoreSmokeLegPackageFixture -Leg $Leg)

    # The default Minimal package serves every Minimal leg and extension-selection's refusal; the
    # core-only and Populated packages are built where their legs start.
    if ($neededFixtures -contains "default-minimal") {
        Build-SmokeSourceAndPackage -PackageFixture "default-minimal"
        $packageDirectoryPath = Get-SmokePackageDirectory -PackageFixture "default-minimal"
    }

    if ($legSet.Contains("package-directory")) {
        Invoke-SmokeStep -Name "leg-package-directory" -Body {
            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile  = $script:ResolvedEnvironmentFile
                DatabaseEngine   = $DatabaseEngine
                RestoreTemplate  = "Minimal"
                PackageDirectory = $packageDirectoryPath
            }
            Assert-RestoredDatastore -RestoreExecution (Get-RestoreSmokeRestoreExecutionId -Leg "package-directory") -PackageDirectory $packageDirectoryPath
        }
        # The repeated single-target restore: stop the stack with its volumes and workspace kept,
        # then restore the same package onto the already-restored target. The second restore must
        # receive a new SourceIdentity again, different from the first and from the package's.
        Invoke-SmokeStep -Name "leg-package-directory-stop" -Body { Invoke-SmokeTeardown -KeepVolumes }
        Invoke-SmokeStep -Name "leg-package-directory-repeat" -Body {
            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile  = $script:ResolvedEnvironmentFile
                DatabaseEngine   = $DatabaseEngine
                RestoreTemplate  = "Minimal"
                PackageDirectory = $packageDirectoryPath
            }
            Assert-RestoredDatastore -RestoreExecution (Get-RestoreSmokeRestoreExecutionId -Leg "package-directory" -Ordinal 2) -PackageDirectory $packageDirectoryPath
        }
        Invoke-SmokeStep -Name "leg-package-directory-teardown" -Body { Invoke-SmokeTeardown }
    }

    if ($legSet.Contains("separate-config")) {
        Invoke-SmokeStep -Name "leg-separate-config" -Body {
            # Bring up a separate-topology stack, plant a marker in the dedicated CMS
            # database, stop the stack (volumes kept), then restore. The marker surviving
            # proves the restore never touched edfi_configurationservice.
            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile        = $script:ResolvedEnvironmentFile
                DatabaseEngine         = $DatabaseEngine
                SeparateConfigDatabase = $true
            }
            Wait-SmokeDmsHealth
            $markerQuery = if ($DatabaseEngine -eq "mssql") {
                "SET NOCOUNT ON; CREATE TABLE dbo.restore_smoke_marker (marker int); INSERT INTO dbo.restore_smoke_marker VALUES (42);"
            }
            else {
                "CREATE TABLE restore_smoke_marker (marker int); INSERT INTO restore_smoke_marker VALUES (42);"
            }
            $null = Invoke-SmokeSql -DatabaseName "edfi_configurationservice" -Query $markerQuery

            Invoke-SmokeTeardown -KeepVolumes

            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile        = $script:ResolvedEnvironmentFile
                DatabaseEngine         = $DatabaseEngine
                RestoreTemplate        = "Minimal"
                PackageDirectory       = $packageDirectoryPath
                SeparateConfigDatabase = $true
            }
            Assert-RestoredDatastore -RestoreExecution (Get-RestoreSmokeRestoreExecutionId -Leg "separate-config") -PackageDirectory $packageDirectoryPath

            $markerCountQuery = if ($DatabaseEngine -eq "mssql") {
                "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.restore_smoke_marker;"
            }
            else {
                "SELECT COUNT(*) FROM restore_smoke_marker;"
            }
            $markerCount = [int](@(Invoke-SmokeSql -DatabaseName "edfi_configurationservice" -Query $markerCountQuery)[0])
            if ($markerCount -ne 1) {
                throw "The edfi_configurationservice marker did not survive the restore (rows: $markerCount); the restore touched the separate CMS database."
            }
            Write-Host "[restore-smoke] separate CMS database untouched (marker survived)"
        }
        Invoke-SmokeStep -Name "leg-separate-config-teardown" -Body { Invoke-SmokeTeardown }
    }

    if ($legSet.Contains("directory-feed")) {
        Invoke-SmokeStep -Name "leg-directory-feed" -Body {
            # Feed-shaped resolution without Azure: the work directory IS the feed, selected
            # by DATABASE_TEMPLATE_FEED_URL, with the exact NuGet version pinned by
            # DATABASE_TEMPLATE_NUGET_VERSION.
            $feedEnvironmentFile = Join-Path $script:WorkDirectory ".env.directory-feed"
            $baseContent = Get-Content -LiteralPath $script:ResolvedEnvironmentFile -Raw
            $feedContent = $baseContent.TrimEnd() + "`n" +
                "DATABASE_TEMPLATE_FEED_URL=$packageDirectoryPath`n" +
                "DATABASE_TEMPLATE_NUGET_VERSION=$PackageVersion`n"
            Set-Content -LiteralPath $feedEnvironmentFile -Value $feedContent -Encoding utf8

            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile = $feedEnvironmentFile
                DatabaseEngine  = $DatabaseEngine
                RestoreTemplate = "Minimal"
            }
            Assert-RestoredDatastore -RestoreExecution (Get-RestoreSmokeRestoreExecutionId -Leg "directory-feed") -PackageDirectory $packageDirectoryPath -EnvironmentFile $feedEnvironmentFile
        }
        Invoke-SmokeStep -Name "leg-directory-feed-teardown" -Body { Invoke-SmokeTeardown }
    }

    if ($legSet.Contains("tampered-package")) {
        Invoke-SmokeStep -Name "leg-tampered-package" -Body {
            $tamperedDirectory = Join-Path $script:WorkDirectory "tampered"
            New-Item -ItemType Directory -Path $tamperedDirectory -Force | Out-Null
            Copy-Item -Path (Join-Path $packageDirectoryPath "*") -Destination $tamperedDirectory
            $tamperedPackage = @(Get-ChildItem -Path $tamperedDirectory -Filter "*.nupkg" | Where-Object { $_.Name -notlike "*.Attestation.*" })[0]
            Add-Content -LiteralPath $tamperedPackage.FullName -Value "tampered" -AsByteStream:$false

            $failed = $false
            try {
                Invoke-RestoreWrapper -Arguments @{
                    EnvironmentFile  = $script:ResolvedEnvironmentFile
                    DatabaseEngine   = $DatabaseEngine
                    RestoreTemplate  = "Minimal"
                    PackageDirectory = $tamperedDirectory
                }
            }
            catch {
                $failed = $true
                Write-Host "[restore-smoke] tampered package refused: $($_.Exception.Message)"
            }
            if (-not $failed) {
                throw "A tampered package was NOT refused; the trust gate failed."
            }

            # Fails BEFORE any Docker activity: no compose containers may exist for the project.
            $global:LASTEXITCODE = 0
            $containers = @(docker ps -a --filter "label=com.docker.compose.project=$($script:WrapperProfile.ComposeProject)" --format '{{.Names}}' 2>&1 |
                    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
            if ($LASTEXITCODE -ne 0) {
                throw "docker ps failed while proving no containers were created."
            }
            if ($containers.Count -gt 0) {
                throw "Tampered-package refusal happened AFTER Docker activity; containers exist: $($containers -join ', ')."
            }
            Write-Host "[restore-smoke] refusal happened before any Docker activity (no containers)"
        }
    }

    if ($legSet.Contains("contaminated-package")) {
        if ($DatabaseEngine -ne "postgresql") {
            Write-Warning "The contaminated-package leg is PostgreSQL-only (a .bak artifact cannot be text-edited); skipping on $DatabaseEngine."
        }
        else {
            Invoke-SmokeStep -Name "leg-contaminated-package" -Body {
                # Post-process a legit package so staging AND the candidate cross-check pass
                # while the ARTIFACT smuggles an extra schema: append the schema to the dump,
                # declare it in the manifest inventory (recomputed hashes), rezip, and re-sign
                # with the dev key. The failure must land in scratch validation's DMS-only
                # gate, before anything touches the (absent) target.
                Import-Module (Join-Path $script:TemplatesRoot "Template-RestoreCore.psm1") -Force
                Push-Location $script:TemplatesRoot
                try {
                    Import-Module (Join-Path $script:TemplatesRoot "Template-Management.psm1") -Force
                }
                finally {
                    Pop-Location
                }

                $contaminatedDirectory = Join-Path $script:WorkDirectory "contaminated"
                $expandDirectory = Join-Path $script:WorkDirectory "contaminated-expand"
                New-Item -ItemType Directory -Path $contaminatedDirectory, $expandDirectory -Force | Out-Null
                $sourcePackage = @(Get-ChildItem -Path $packageDirectoryPath -Filter "*.nupkg" | Where-Object { $_.Name -notlike "*.Attestation.*" })[0]
                $zipCopy = Join-Path $script:WorkDirectory "contaminated.zip"
                Copy-Item -LiteralPath $sourcePackage.FullName -Destination $zipCopy
                Expand-Archive -Path $zipCopy -DestinationPath $expandDirectory

                $manifestFile = @(Get-ChildItem -Path $expandDirectory -Filter (Get-RestoreManifestFileName) -Recurse -File)[0]
                $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json -AsHashtable
                $artifactFile = @(Get-ChildItem -Path $expandDirectory -Filter ([string]$manifest.artifactFileName) -Recurse -File)[0]

                Add-Content -LiteralPath $artifactFile.FullName -Value "`nCREATE SCHEMA smoke_intruder;`nCREATE TABLE smoke_intruder.intruder_table (i int);`n"

                $manifest.inventory.schemas += @{ schemaName = "smoke_intruder"; objects = @(@{ name = "intruder_table"; type = "table" }) }
                $manifest.inventorySha256 = Get-CanonicalInventoryHash -Inventory $manifest.inventory
                $manifest.artifactSha256 = (Get-FileHash -LiteralPath $artifactFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestFile.FullName -Encoding utf8

                $contaminatedPackagePath = Join-Path $contaminatedDirectory $sourcePackage.Name
                Compress-Archive -Path (Join-Path $expandDirectory "*") -DestinationPath ($contaminatedPackagePath + ".zip")
                Move-Item -LiteralPath ($contaminatedPackagePath + ".zip") -Destination $contaminatedPackagePath

                Invoke-TemplatePackageAttestation `
                    -Config @{ Id = [string]$manifest.packageId } `
                    -PackageVersion ([string]$manifest.packageVersion) `
                    -BackupDirectory $contaminatedDirectory `
                    -AttestationSignerKeyPath $script:SmokeSignerKeyPath `
                    -AttestationProducer $script:SmokeProducerName

                $failed = $false
                try {
                    Invoke-RestoreWrapper -Arguments @{
                        EnvironmentFile  = $script:ResolvedEnvironmentFile
                        DatabaseEngine   = $DatabaseEngine
                        RestoreTemplate  = "Minimal"
                        PackageDirectory = $contaminatedDirectory
                    }
                }
                catch {
                    $failed = $true
                    Write-Host "[restore-smoke] contaminated package refused: $($_.Exception.Message)"
                    if ($_.Exception.Message -notlike "*smoke_intruder*") {
                        throw "The contaminated package was refused, but not by the DMS-only gate naming smoke_intruder: $($_.Exception.Message)"
                    }
                }
                if (-not $failed) {
                    throw "A contaminated package was NOT refused; the scratch DMS-only gate failed."
                }

                # Fresh volume: the target must still be ABSENT, no generated restore databases
                # may remain, and no active workspace may have been committed.
                if (Test-SmokeDatabasePresent -DatabaseName $script:TargetDatabaseName) {
                    throw "The target database '$script:TargetDatabaseName' exists after a failed scratch validation on a fresh volume."
                }
                $generatedDatabases = @(Invoke-SmokeSql -DatabaseName "postgres" -Query "SELECT datname FROM pg_database WHERE datname LIKE 'edfi_dms_restore_%';")
                if (@($generatedDatabases).Count -gt 0) {
                    throw "Generated restore databases remain after the failure: $($generatedDatabases -join ', ')."
                }
                if (Test-Path -LiteralPath $script:BootstrapRoot) {
                    throw "An active .bootstrap workspace exists after a pre-commit failure; the candidate must never be committed on a scratch-validation failure."
                }
                Write-Host "[restore-smoke] target absent, no generated databases, no workspace committed"
            }
            Invoke-SmokeStep -Name "leg-contaminated-package-teardown" -Body { Invoke-SmokeTeardown }
        }
    }

    if ($legSet.Contains("running-stack")) {
        Invoke-SmokeStep -Name "leg-running-stack" -Body {
            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile = $script:ResolvedEnvironmentFile
                DatabaseEngine  = $DatabaseEngine
            }
            Wait-SmokeDmsHealth

            $failed = $false
            try {
                Invoke-RestoreWrapper -Arguments @{
                    EnvironmentFile  = $script:ResolvedEnvironmentFile
                    DatabaseEngine   = $DatabaseEngine
                    RestoreTemplate  = "Minimal"
                    PackageDirectory = $packageDirectoryPath
                }
            }
            catch {
                $failed = $true
                if ($_.Exception.Message -notlike "*still has running containers*") {
                    throw "The running-stack restore was refused, but not by the stop proof: $($_.Exception.Message)"
                }
                Write-Host "[restore-smoke] stop proof refused the running stack: $($_.Exception.Message)"
            }
            if (-not $failed) {
                throw "A restore against a RUNNING stack was not refused by the stop proof."
            }
        }
        Invoke-SmokeStep -Name "leg-running-stack-teardown" -Body { Invoke-SmokeTeardown }
    }

    if ($legSet.Contains("extension-selection")) {
        Invoke-SmokeStep -Name "write-core-only-environment" -Body {
            # Derived from the image env, so the run's own image tags reach the core-only stacks.
            $coreOnlyEnvironmentFile = Join-Path $script:WorkDirectory ".env.smoke-core-only"
            $selectionEnvironment = Write-RestoreSmokeCoreOnlyEnvironmentFile -BaseEnvironmentFile $script:ResolvedEnvironmentFile -TargetPath $coreOnlyEnvironmentFile
            $script:Provenance.SelectionEnvironments.Add($selectionEnvironment)
            $script:SelectionEnvironmentFiles["core-only"] = $coreOnlyEnvironmentFile
            Write-Host "[restore-smoke] core-only env selects $($selectionEnvironment.SelectedPackages -join ', '); removed $($selectionEnvironment.RemovedPackages -join ', ')"
        }
        Build-SmokeSourceAndPackage -PackageFixture "core-only-minimal"
        $coreOnlyPackageDirectory = Get-SmokePackageDirectory -PackageFixture "core-only-minimal"

        # Every stack is down (the source teardown removed volumes and the workspace), so a refusal
        # that leaves no container, no new volume, and no workspace failed before Docker.
        Invoke-SmokeStep -Name "leg-extension-selection-mismatch" -Body {
            Assert-SmokeSelectionRefusal -PackageDirectory $packageDirectoryPath -EnvironmentFile (Get-SmokeSelectionEnvironmentFile -Selection "core-only")
        }
        Invoke-SmokeStep -Name "leg-extension-selection" -Body {
            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile  = (Get-SmokeSelectionEnvironmentFile -Selection "core-only")
                DatabaseEngine   = $DatabaseEngine
                RestoreTemplate  = "Minimal"
                PackageDirectory = $coreOnlyPackageDirectory
            }
            Assert-RestoredDatastore -RestoreExecution (Get-RestoreSmokeRestoreExecutionId -Leg "extension-selection") -PackageDirectory $coreOnlyPackageDirectory -EnvironmentFile (Get-SmokeSelectionEnvironmentFile -Selection "core-only")
        }
        Invoke-SmokeStep -Name "leg-extension-selection-teardown" -Body { Invoke-SmokeTeardown }
    }

    if ($legSet.Contains("populated")) {
        Build-SmokeSourceAndPackage -PackageFixture "default-populated"
        $populatedPackageDirectory = Get-SmokePackageDirectory -PackageFixture "default-populated"
        Invoke-SmokeStep -Name "leg-populated" -Body {
            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile  = $script:ResolvedEnvironmentFile
                DatabaseEngine   = $DatabaseEngine
                RestoreTemplate  = "Populated"
                PackageDirectory = $populatedPackageDirectory
            }
            Assert-RestoredDatastore -RestoreExecution (Get-RestoreSmokeRestoreExecutionId -Leg "populated") -PackageDirectory $populatedPackageDirectory
        }
        Invoke-SmokeStep -Name "leg-populated-teardown" -Body { Invoke-SmokeTeardown }
    }

    Write-SmokeStep "restore smoke PASSED ($($Leg -join ', '))"
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host "[restore-smoke] FAILED: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if (-not $SkipTeardown -and $exitCode -ne 0) {
        if ($script:TeardownAuthorized) {
            try { Invoke-SmokeTeardown } catch { Write-Warning "Final teardown failed: $($_.Exception.Message)" }
        }
        else {
            Write-Host "[restore-smoke] no teardown: $script:TeardownWithheldReason"
        }
    }

    if ($script:Provenance.Images.OwnedTags.Count -gt 0) {
        # Only the run tags this run proved absent and claimed; never a shared tag or an image ID.
        try {
            Remove-RestoreSmokeOwnedImageTag -Ledger $script:Provenance.Images -Keep:$SkipTeardown
        }
        catch {
            Write-Warning "Run tag cleanup failed: $($_.Exception.Message)"
        }
        foreach ($cleanup in $script:Provenance.Images.Cleanup) {
            Write-Host "[restore-smoke] run tag $($cleanup.Tag): $($cleanup.Action) $($cleanup.Detail)"
        }
    }

    $foreignStack = $script:Provenance.ForeignStack
    if ($null -ne $foreignStack -and $null -ne $foreignStack.LeftoverVolumes -and $foreignStack.LeftoverVolumes.Allowed.Count -gt 0) {
        # Every teardown of this run is done: each leftover volume the preflight allowed must still
        # exist, unchanged.
        try {
            $foreignStack.AllowedVolumesAtEnd = @(Get-RestoreSmokeAllowedVolumeState -Classification $foreignStack.LeftoverVolumes)
        }
        catch {
            Write-Warning "Could not re-inspect the allowed leftover volumes: $($_.Exception.Message)"
        }
        foreach ($volumeState in @($foreignStack.AllowedVolumesAtEnd | Where-Object { $null -ne $_ })) {
            Write-Host "[restore-smoke] allowed leftover volume $($volumeState.Name): preserved=$($volumeState.Preserved) $($volumeState.Reason)"
        }
    }

    $env:DMS_SCHEMA_TOOL_PATH = $script:OriginalSchemaToolPath

    if ($script:SmokeProducerRegistered -and (Test-Path -LiteralPath $script:LocalTrustOverlayPath)) {
        # Remove exactly this run's producer from the local overlay; a pre-existing overlay
        # keeps every other entry. An overlay left with no producers is removed entirely.
        try {
            $overlay = Get-Content -LiteralPath $script:LocalTrustOverlayPath -Raw | ConvertFrom-Json -AsHashtable
            $overlay.producers = @($overlay.producers | Where-Object { [string]$_.name -ne $script:SmokeProducerName })
            if (@($overlay.producers).Count -eq 0) {
                Remove-Item -LiteralPath $script:LocalTrustOverlayPath -Force
            }
            else {
                $overlay | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $script:LocalTrustOverlayPath -Encoding utf8
            }
            Write-Host "[restore-smoke] removed ephemeral producer '$script:SmokeProducerName' from the local trust overlay"
        }
        catch {
            Write-Warning "Could not remove the ephemeral trust producer '$script:SmokeProducerName': $($_.Exception.Message)"
        }
    }

    if ($null -ne $script:WorkDirectory -and (Test-Path -LiteralPath $script:WorkDirectory)) {
        Remove-Item -LiteralPath $script:WorkDirectory -Recurse -Force -ErrorAction Continue
    }

    $schemaToolRecord = $script:Provenance.SchemaTools
    if ($null -ne $schemaToolRecord -and $null -ne $schemaToolRecord.ExecutablePath -and (Test-Path -LiteralPath $schemaToolRecord.ExecutablePath -PathType Leaf)) {
        $schemaToolRecord.Sha256AtEnd = Get-RestoreSmokeFileSha256 -Path $schemaToolRecord.ExecutablePath
    }
    $script:Provenance.SourceAtEnd = Get-RestoreSmokeSourceRevision -RepoRoot $script:RepoRoot
    $classification = Get-RestoreSmokeResultClassification -Provenance $script:Provenance

    if (-not [string]::IsNullOrWhiteSpace($ResultsPath)) {
        [ordered]@{
            Status         = if ($exitCode -eq 0) { "passed" } else { "failed" }
            Steps          = $script:StepResults
            Provenance     = $script:Provenance
            Classification = $classification
        } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ResultsPath -Encoding utf8
        Write-Host "[restore-smoke] results written to $ResultsPath"
    }

    Write-Host ""
    Write-Host "[restore-smoke] step summary:"
    foreach ($step in $script:StepResults) {
        Write-Host ("  {0,-45} {1,-7} {2,8}s" -f $step.Name, $step.Status, $step.DurationSeconds)
    }
    if ($classification.Final) {
        Write-Host "[restore-smoke] evidence classification: FINAL"
    }
    else {
        Write-Host "[restore-smoke] evidence classification: NON-FINAL"
        foreach ($reason in $classification.Reasons) {
            Write-Host "  - $reason"
        }
    }
}

exit $exitCode
