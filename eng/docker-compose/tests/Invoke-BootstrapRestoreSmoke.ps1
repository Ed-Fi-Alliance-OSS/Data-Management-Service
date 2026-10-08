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
      package-directory   Full restore via -PackageDirectory on a fresh volume; asserts DMS
                          health, the restored dms.EffectiveSchema singleton, and (when the
                          source was seeded) restored descriptor rows.
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
      populated           Opt-in: the package-directory shape built from a Populated-seeded
                          source, adding the non-descriptor document count probe. Long.

    Prerequisites: an ISOLATED Docker daemon (see Foreign stacks); pwsh 7+; the .NET SDK (the
    smoke builds api-schema-tools); network access for the image builds and the source
    bootstrap's package downloads.

    Foreign stacks: before anything is created, stopped, or removed, the smoke inventories every
    container (running or stopped) and volume labelled with the dms-local or dms-published
    compose project. If any exist, it refuses and changes nothing - its teardowns run with -d -v
    and would destroy them - unless -ConfirmForeignStackRemoval is passed, which tears exactly
    those projects down and is recorded in the results. A refused run never reaches any teardown.

    Provenance: after the preflight the smoke builds api-schema-tools (Debug, the configuration
    the bootstrap resolver prefers) and proves the resolver returns that exact executable, builds
    the DMS and CMS images itself (docker buildx build --load, with the Dockerfile, contexts, and
    VERSION argument of build-dms.ps1 / build-config.ps1 DockerBuild) under run-unique tags it
    first proves absent, takes each image's identity from the build's own --iidfile, forwards the
    run tags to the selected wrapper through a smoke-owned env file, removes only those tags at the
    end, and records what each started stack actually ran (docker inspect), the built package and
    its attestation, the effective
    SCHEMA_PACKAGES, and the worktree revision and status at start and end. The results JSON
    classifies the run: it is final evidence only when every one of those was observed and
    matched, the worktree was clean throughout, and -ExploratoryPackage was not set; otherwise it
    lists the reasons it is not.

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
    contaminated-package, running-stack.

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
    Pass only on an isolated host or with the stack owner's recorded approval.

.PARAMETER ExploratoryPackage
    Marks the run exploratory: its results are never classified as final evidence.

.PARAMETER SkipSourceSeed
    Build the source datastore without seeding (schema only). Faster; descriptor-content
    probes are skipped. The default seeds the Minimal template so the restored data is real.

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

    [ValidateSet("package-directory", "separate-config", "directory-feed", "tampered-package", "contaminated-package", "running-stack", "populated")]
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
$script:LocalTrustOverlayPath = Join-Path $script:DockerComposeRoot "template-trust-policy.local.json"
$script:StepResults = [System.Collections.Generic.List[pscustomobject]]::new()
$script:WorkDirectory = $null
$script:SmokeProducerName = $null
$script:SmokeProducerRegistered = $false

Import-Module (Join-Path $PSScriptRoot "RestoreSmokeProbes.psm1") -Force

$script:RunId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$script:WrapperProfile = Get-RestoreSmokeWrapperProfile -Wrapper $Wrapper
$script:DataStandardVersionSupplied = $PSBoundParameters.ContainsKey("DataStandardVersion")
$script:ResolvedStandardVersion = Resolve-RestoreSmokeStandardVersion -StandardVersion $StandardVersion -DataStandardVersion $DataStandardVersion
$script:CurrentStepName = $null
# No teardown, of any project, runs until the preflight has proven that no foreign stack exists
# (or the caller confirmed its removal). This includes the failure teardown in the finally block.
$script:TeardownAuthorized = $false
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
    StackObservations            = [System.Collections.Generic.List[object]]::new()
    Packages                     = [System.Collections.Generic.List[object]]::new()
    SourceIdentity               = $null
    SourceIdentityReason         = "the pre-backup SourceIdentity capture is not implemented yet (Step 2.3)"
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
    Records what the healthy stack actually runs (images per service) and the SCHEMA_PACKAGES the
    wrapper forwarded to its start phase, labelled with the current step.
    #>
    $observation = Get-RestoreSmokeStackObservation -ComposeProject $script:WrapperProfile.ComposeProject -Label ([string]$script:CurrentStepName)
    $schemaPackages = Get-RestoreSmokeEffectiveSchemaPackageList -DerivedEnvironmentFile (Join-Path $script:BootstrapRoot ".env.derived")
    $observation | Add-Member -NotePropertyName EffectiveSchemaPackages -NotePropertyValue $schemaPackages
    $script:Provenance.StackObservations.Add($observation)
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

function Assert-RestoredDatastore {
    <#
    .SYNOPSIS
    The post-restore probes: DMS health, the dms.EffectiveSchema singleton, and (when the
    source was seeded) at least one restored descriptor row.
    #>
    param(
        [switch]$RequirePopulatedData
    )

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
}

function Build-SmokeSourceAndPackage {
    <#
    .SYNOPSIS
    Bootstraps a source datastore, builds the attested template package from it into the work
    directory, and tears the stack down to fresh volumes.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Minimal", "Populated")]
        [string]$TemplateKind
    )

    Invoke-SmokeStep -Name "build-source-datastore-$($TemplateKind.ToLowerInvariant())" -Body {
        # The SOURCE stack runs separate topology: the producer's DMS-only gate requires a
        # dedicated DMS datastore, and the default shared topology would put the Configuration
        # Service's dmscs schema and OpenIddict identity state into the very database the
        # template is dumped from (the gate refuses exactly that - proven by this smoke's
        # development history).
        $sourceArgs = @{ EnvironmentFile = $script:ResolvedEnvironmentFile; DatabaseEngine = $DatabaseEngine; SeparateConfigDatabase = $true }
        if (-not $SkipSourceSeed) {
            $sourceArgs.LoadSeedData = $true
            $sourceArgs.SeedTemplate = $TemplateKind
        }
        Invoke-RestoreWrapper -Arguments $sourceArgs
        Wait-SmokeDmsHealth
    }

    Invoke-SmokeStep -Name "build-attested-package-$($TemplateKind.ToLowerInvariant())" -Body {
        $packageDirectory = Join-Path $script:WorkDirectory "package"
        New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

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

        Import-Module (Join-Path $script:TemplatesRoot "Template-RestoreCore.psm1") -Force
        $packageProvenance = Get-RestoreSmokePackageProvenance `
            -PackageDirectory $packageDirectory `
            -RestoreManifestFileName (Get-RestoreManifestFileName) `
            -TemplateKind $TemplateKind
        $script:Provenance.Packages.Add($packageProvenance)
        if (-not $packageProvenance.Verified) {
            throw "Package provenance could not be established: $($packageProvenance.Reason)"
        }
    }

    Invoke-SmokeStep -Name "teardown-source-stack" -Body {
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
        }
        Assert-RestoreSmokeNoForeignStack -Inventory $inventory -ConfirmForeignStackRemoval:$ConfirmForeignStackRemoval
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
            # volumes in the same project can survive it: record what actually remains.
            $remaining = Get-RestoreSmokeForeignStackInventory
            $script:Provenance.ForeignStack.RemainingAfterRemoval = $remaining.Projects
            if (@($remaining.Projects | ForEach-Object { $_.Containers }).Count -gt 0) {
                throw "The confirmed removal left containers in place: $(Format-RestoreSmokeForeignStackInventory -Inventory $remaining). Remove them before rerunning."
            }
            if ($remaining.HasForeignState) {
                Write-Host "[restore-smoke] the confirmed removal left volumes this run's engine does not use: $(Format-RestoreSmokeForeignStackInventory -Inventory $remaining)"
            }
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
    $minimalLegs = @("package-directory", "separate-config", "directory-feed", "tampered-package", "contaminated-package", "running-stack") |
        Where-Object { $legSet.Contains($_) }

    if (@($minimalLegs).Count -gt 0) {
        Build-SmokeSourceAndPackage -TemplateKind "Minimal"
        $packageDirectoryPath = Join-Path $script:WorkDirectory "package"
    }

    if ($legSet.Contains("package-directory")) {
        Invoke-SmokeStep -Name "leg-package-directory" -Body {
            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile  = $script:ResolvedEnvironmentFile
                DatabaseEngine   = $DatabaseEngine
                RestoreTemplate  = "Minimal"
                PackageDirectory = $packageDirectoryPath
            }
            Assert-RestoredDatastore
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
            Assert-RestoredDatastore

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
            Assert-RestoredDatastore
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

    if ($legSet.Contains("populated")) {
        Build-SmokeSourceAndPackage -TemplateKind "Populated"
        $populatedPackageDirectory = Join-Path $script:WorkDirectory "package"
        Invoke-SmokeStep -Name "leg-populated" -Body {
            Invoke-RestoreWrapper -Arguments @{
                EnvironmentFile  = $script:ResolvedEnvironmentFile
                DatabaseEngine   = $DatabaseEngine
                RestoreTemplate  = "Populated"
                PackageDirectory = $populatedPackageDirectory
            }
            Assert-RestoredDatastore -RequirePopulatedData
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
            Write-Host "[restore-smoke] no teardown: the preflight did not authorize any Docker change"
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
