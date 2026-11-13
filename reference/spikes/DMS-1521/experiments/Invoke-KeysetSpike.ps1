<#
.SYNOPSIS
    Runs the DMS-1521 keyset temp-table spike on one engine.

.DESCRIPTION
    Starts (or verifies) the spike's dedicated, digest-pinned database container on a Docker volume,
    sets the environment, builds the KeysetSpike runner, and runs an interleaved sequence of variants
    over the selected fixture runs. Each run writes one directory under -ResultsDirectory.

    Variants: B0 is pristine main (prototype.patch must NOT be applied). A (pg-analyze), B (baseline
    mode of the patch), and I (inline) need prototype.patch applied; the script checks this with the
    read-only `git apply --check -R` and refuses a variant whose patch state is wrong. The script never
    applies or removes the patch.

    The container is stopped at the end unless -KeepContainer is passed. Run one engine at a time.

.EXAMPLE
    pwsh -File reference/spikes/DMS-1521/experiments/Invoke-KeysetSpike.ps1 -Engine postgresql -Sequence B0 -Smoke

.EXAMPLE
    pwsh -File reference/spikes/DMS-1521/experiments/Invoke-KeysetSpike.ps1 -Engine mssql -Sequence B,I,B,I,B,I
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('postgresql', 'mssql')]
    [string] $Engine,

    # Comma-separated or an array: pwsh -File passes 'B,I,B' as one string.
    [Parameter(Mandatory = $true)]
    [string[]] $Sequence,

    [string[]] $Fixtures = @('resource', 'descriptor', 'readacceleration'),

    [switch] $Smoke,

    [string] $ResultsDirectory = 'C:\perf\dms-1521',

    [string] $SequenceId = (Get-Date -Format 'yyyyMMdd-HHmm'),

    [int] $WarmupIterations = 10,

    [int] $MeasuredIterations = 60,

    [string] $PageSizes = '25,500',

    [string] $ReplayConcurrency = '1,8,32',

    [int] $ReplayWarmupPerWorker = 3,

    [int] $ReplayIterationsPerWorker = 20,

    [switch] $NoCalibrate,

    [switch] $NoCounters,

    # Replay concurrency levels measured inside a counter window (default: 1 on PostgreSQL; 1 and 32 on SQL Server).
    [string] $CounterConcurrency,

    # Requests (or fallback replays) per page size in each PostgreSQL auto_explain window.
    [int] $AutoExplainRequests = 20,

    [switch] $NoAutoExplain,

    # Task 8 screening: the forms to replay (comma-separated, starting with baseline); default the first five.
    [string] $Forms,

    [switch] $KeepContainer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '..', '..', '..')).Path
$project = Join-Path -Path $PSScriptRoot -ChildPath 'KeysetSpike' -AdditionalChildPath 'KeysetSpike.csproj'
$patch = Join-Path $PSScriptRoot 'prototype.patch'
$password = 'Dms1521_Spike!'

# Pinned images: the digests eng/performance/invoke-traditional-baseline.ps1 pins.
$containers = @{
    postgresql = @{
        Name = 'dms-1521-postgresql'
        Volume = 'dms-1521-pgdata'
        Tag = 'postgres:18.6-alpine'
        Digest = 'sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873'
        Port = 15521
    }
    mssql = @{
        Name = 'dms-1521-mssql'
        Volume = 'dms-1521-mssqldata'
        Tag = 'mcr.microsoft.com/mssql/server:2025-latest'
        Digest = 'sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a'
        Port = 14351
    }
}
$Sequence = @($Sequence | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Fixtures = @($Fixtures | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($item in $Sequence) {
    if ($item -cnotin @('B0', 'A', 'B', 'I')) { throw "Unknown variant '$item'; expected B0, A, B, or I." }
}
foreach ($item in $Fixtures) {
    if ($item -notin @('resource', 'descriptor', 'readacceleration', 'probes', 'descriptorprobes', 'forms')) { throw "Unknown fixture '$item'." }
    if ($item -like '*probes' -and $Engine -ne 'postgresql') { throw "Fixture '$item' is PostgreSQL only." }
    # Task 8 screening of derived forms of the task 6 captures (DMS1521_FORMS_SOURCE, default t6s1).
    if ($item -eq 'forms' -and $Engine -ne 'mssql') { throw "Fixture 'forms' is SQL Server only." }
}

$container = $containers[$Engine]
$image = "$($container.Tag.Split(':')[0])@$($container.Digest)"

$variantSwitch = @{ B0 = 'baseline'; A = 'pg-analyze'; B = 'baseline'; I = 'inline' }
$fixtureClass = @{
    resource = 'KeysetSpikeResourceRun'
    descriptor = 'KeysetSpikeDescriptorRun'
    readacceleration = 'KeysetSpikeReadAccelerationRun'
    probes = 'KeysetCarrierProbes'
    descriptorprobes = 'KeysetCarrierDescriptorProbes'
    forms = 'KeysetFormsScreening'
}
$engineClassPrefix = if ($Engine -eq 'postgresql') { 'Given_Postgresql_' } else { 'Given_Mssql_' }

function Test-PatchApplied {
    if (-not (Test-Path $patch)) { return $false }
    git -C $repoRoot apply --check -R $patch 2>$null | Out-Null
    return $LASTEXITCODE -eq 0
}

function Assert-VariantPatchState([string] $Variant) {
    $applied = Test-PatchApplied
    if ($Variant -eq 'B0' -and $applied) {
        throw 'Variant B0 is pristine main, but prototype.patch is applied. Remove it first.'
    }
    if ($Variant -ne 'B0' -and -not $applied) {
        throw "Variant $Variant needs prototype.patch applied (missing or not applied: $patch)."
    }
    if ($Variant -eq 'A' -and $Engine -ne 'postgresql') {
        throw 'Variant A (pg-analyze) is PostgreSQL only.'
    }
}

function Open-SpikeContainer {
    $existing = docker ps -a --filter "name=^$($container.Name)$" --format '{{.Names}}'
    # auto_explain.log_min_duration stays at its default (-1, off) and is never set with -c: command-line
    # settings outrank ALTER SYSTEM, which the runner's auto_explain windows use. A container created
    # with the flag (before task 3) is recreated; its volume and data are kept.
    if ($existing -and $Engine -eq 'postgresql') {
        $arguments = docker inspect $container.Name --format '{{join .Args " "}}'
        if ($arguments -match 'auto_explain\.log_min_duration') {
            docker rm -f $container.Name | Out-Null
            $existing = $null
        }
    }
    if (-not $existing) {
        docker volume create $container.Volume | Out-Null
        if ($Engine -eq 'postgresql') {
            # PostgreSQL 18 images keep PGDATA under /var/lib/postgresql/18/docker; mount the parent.
            docker run -d --name $container.Name `
                -e "POSTGRES_PASSWORD=$password" `
                -p "127.0.0.1:$($container.Port):5432" `
                -v "$($container.Volume):/var/lib/postgresql" `
                $image `
                -c max_locks_per_transaction=256 `
                -c shared_preload_libraries=pg_stat_statements,auto_explain `
                -c pg_stat_statements.track_planning=on `
                -c pg_stat_statements.track_utility=on `
                -c track_io_timing=on `
                -c auto_explain.log_format=json `
                -c auto_explain.log_analyze=on `
                -c auto_explain.log_buffers=on `
                -c auto_explain.log_timing=on | Out-Null
        }
        else {
            docker run -d --name $container.Name `
                -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$password" `
                -p "127.0.0.1:$($container.Port):1433" `
                -v "$($container.Volume):/var/opt/mssql" `
                $image | Out-Null
        }
        if ($LASTEXITCODE -ne 0) { throw "docker run failed for $($container.Name)." }
    }
    else {
        docker start $container.Name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "docker start failed for $($container.Name)." }
    }

    $imageId = docker inspect $container.Name --format '{{.Image}}'
    $repoDigests = docker image inspect $imageId --format '{{join .RepoDigests " "}}'
    if ($repoDigests -notmatch [regex]::Escape($container.Digest)) {
        throw "Container $($container.Name) does not run the pinned digest $($container.Digest) (has: $repoDigests)."
    }

    $deadline = (Get-Date).AddMinutes(3)
    while ($true) {
        if ($Engine -eq 'postgresql') {
            docker exec $container.Name pg_isready -U postgres 2>$null | Out-Null
        }
        else {
            docker exec $container.Name /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $password -Q 'SELECT 1' 2>$null | Out-Null
        }
        if ($LASTEXITCODE -eq 0) { break }
        if ((Get-Date) -gt $deadline) { throw "$($container.Name) did not become ready." }
        Start-Sleep -Seconds 2
    }

    if ($Engine -eq 'postgresql') {
        # Clear a window an interrupted run may have left on.
        docker exec $container.Name psql -U postgres -v ON_ERROR_STOP=1 -qAt `
            -c 'ALTER SYSTEM RESET auto_explain.log_min_duration' -c 'SELECT pg_reload_conf()' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not reset auto_explain.log_min_duration.' }
    }
}

function Close-SpikeContainer {
    docker stop $container.Name 2>$null | Out-Null
}

# --- Environment --------------------------------------------------------------------------------
$env:DOTNET_SYSTEM_NET_DISABLEIPV6 = '1'
if ($Engine -eq 'postgresql') {
    $env:ConnectionStrings__DatabaseConnection = "host=127.0.0.1;port=$($container.Port);username=postgres;password=$password;database=edfi_dms1521;pooling=true;maximum pool size=100;Include Error Detail=true;"
    Remove-Item Env:ConnectionStrings__MssqlAdmin -ErrorAction SilentlyContinue
}
else {
    $env:ConnectionStrings__MssqlAdmin = "Server=127.0.0.1,$($container.Port);User Id=sa;Password=$password;TrustServerCertificate=true;Encrypt=true"
    Remove-Item Env:ConnectionStrings__DatabaseConnection -ErrorAction SilentlyContinue
}

$env:PERF_RESULTS_DIR = $ResultsDirectory
$env:PERF_IMAGE_TAG = $container.Tag
$env:PERF_IMAGE_DIGEST = $container.Digest
$env:PERF_STORAGE_NOTE = "docker volume $($container.Volume), not tmpfs"
# Root-level DMS-1521 plan and review notes (DMS-1521.md, DMS-1521-task.json, review reports) cannot
# affect the code under test; list them by exact name, because the guard matches whole path segments.
$planNotes = @(Get-ChildItem -Path $repoRoot -File -Filter 'DMS-1521*' | ForEach-Object { $_.Name })
# The overlay must cover every file prototype.patch touches: the harness guard rejects any other dirty
# path. The two HydrationExecutorTests.cs files are task 7's test adaptation; the runner never runs them.
$env:PERF_ALLOW_DIRTY_PREFIXES = (@('reference/spikes/DMS-1521') + $planNotes + @(
    'src/dms/backend/EdFi.DataManagementService.Backend.External/Plans',
    'src/dms/backend/EdFi.DataManagementService.Backend.Plans',
    'src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs',
    'src/dms/backend/EdFi.DataManagementService.Backend/DescriptorReadHandler.cs',
    'src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/HydrationExecutorTests.cs',
    'src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/HydrationExecutorTests.cs',
    'src/dms/tests/EdFi.DataManagementService.Tests.Integration/Scenarios'
)) -join ';'
if ($Smoke) {
    $env:PERF_FIXTURE = 'smoke-10k'
    $env:PERF_DESCRIPTOR_FIXTURE = 'descriptors-smoke-2k'
    $env:PERF_DEEP_OFFSET = '9000'
    $env:PERF_WARMUP_ITERATIONS = '2'
    $env:PERF_MEASURED_ITERATIONS = '3'
    $env:DMS1521_REPLAY_CONCURRENCY = '1,8'
    $env:DMS1521_REPLAY_WARMUP = '1'
    $env:DMS1521_REPLAY_ITERATIONS = '3'
}
else {
    $env:PERF_FIXTURE = 'primary-500k'
    $env:PERF_DESCRIPTOR_FIXTURE = 'descriptors-25k'
    $env:PERF_DEEP_OFFSET = '450000'
    $env:PERF_WARMUP_ITERATIONS = "$WarmupIterations"
    $env:PERF_MEASURED_ITERATIONS = "$MeasuredIterations"
    $env:DMS1521_REPLAY_CONCURRENCY = $ReplayConcurrency
    $env:DMS1521_REPLAY_WARMUP = "$ReplayWarmupPerWorker"
    $env:DMS1521_REPLAY_ITERATIONS = "$ReplayIterationsPerWorker"
}
$env:DMS1521_RESULTS_DIR = $ResultsDirectory
if ($Forms) { $env:DMS1521_FORMS = $Forms } else { Remove-Item Env:DMS1521_FORMS -ErrorAction SilentlyContinue }
$env:DMS1521_SEQUENCE_ID = $SequenceId
$env:DMS1521_PAGE_SIZES = $PageSizes
$env:DMS1521_COUNTERS = if ($NoCounters) { 'false' } else { 'true' }
if (-not $CounterConcurrency) {
    $CounterConcurrency = if ($Engine -eq 'mssql' -and -not $Smoke) { '1,32' } elseif ($Engine -eq 'mssql') { '1,8' } else { '1' }
}
$env:DMS1521_COUNTER_CONCURRENCY = $CounterConcurrency
# PostgreSQL driver-path plans (auto_explain windows, read back with docker logs).
$env:DMS1521_PG_CONTAINER = $containers.postgresql.Name
$env:DMS1521_AUTO_EXPLAIN = if ($NoAutoExplain) { 'false' } else { 'true' }
$env:DMS1521_AUTO_EXPLAIN_REQUESTS = if ($Smoke) { '3' } else { "$AutoExplainRequests" }

# --- Run ----------------------------------------------------------------------------------------
foreach ($variant in ($Sequence | Select-Object -Unique)) { Assert-VariantPatchState $variant }
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

$log = Join-Path $ResultsDirectory "$SequenceId-$Engine.log"
$failures = @()
try {
    Open-SpikeContainer
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw 'KeysetSpike build failed.' }

    $calibrated = $NoCalibrate.IsPresent
    $position = 0
    foreach ($variant in $Sequence) {
        $position++
        Assert-VariantPatchState $variant
        $env:DMS1521_VARIANT_LABEL = "$variant$position"
        $env:DMS1521_KEYSET_VARIANT = $variantSwitch[$variant]
        foreach ($fixture in $Fixtures) {
            $env:DMS1521_CALIBRATE = if ($fixture -in @('resource', 'forms') -and -not $calibrated) { 'true' } else { 'false' }
            $filter = "FullyQualifiedName~KeysetSpike.$engineClassPrefix$($fixtureClass[$fixture]).It_"
            $started = Get-Date
            "[$started] $Engine $variant$position $fixture" | Tee-Object -FilePath $log -Append
            dotnet test $project -c Release --no-build --filter $filter --logger 'console;verbosity=normal' 2>&1 |
                Tee-Object -FilePath $log -Append
            $exit = $LASTEXITCODE
            "[$(Get-Date)] exit $exit after $([int]((Get-Date) - $started).TotalSeconds) s" | Tee-Object -FilePath $log -Append
            if ($exit -ne 0) { $failures += "$variant$position/$fixture" }
            elseif ($fixture -in @('resource', 'forms')) { $calibrated = $true }
        }
    }
}
finally {
    if (-not $KeepContainer) { Close-SpikeContainer }
}

if ($failures.Count -gt 0) {
    throw "Failed runs: $($failures -join ', '). See $log."
}
Write-Output "All runs completed. Log: $log"
