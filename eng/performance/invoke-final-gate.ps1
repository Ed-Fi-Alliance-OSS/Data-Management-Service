# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Runs the DMS partitioned-cursor-paging performance final gate and writes its report.

.DESCRIPTION
    Runs the final-gate evidence fixtures against the current branch (no worktree overlay:
    the subject under test is HEAD), then evaluates the results against the DMS-1391
    traditional baseline into final-report.md and final-report.json. The harness and this
    wrapper must be committed clean, because the manifests record HEAD as both the runner
    and subject commit. Container identity is pinned the same way the baseline capture
    pins it: the running database container's image digest is validated against the
    expected digest, and the ambient connection string's endpoint is rewritten to that
    container's published port binding, so the measured run cannot lease from a different
    server. Connection strings must already be set: ConnectionStrings__DatabaseConnection
    for postgresql, ConnectionStrings__MssqlAdmin for mssql.

    Per provider, two evidence runs execute: the primary run (one 500k load measured
    across the pristine, authorized, and filtered phases) and the descriptor run (its own
    25k fixture). The DMS-1391 baseline directories must be supplied: measured artifacts
    are attached to their Jira story rather than kept in the repository, so download and
    extract the baseline attachment and point -PostgresqlBaselineDirectory and
    -MssqlBaselineDirectory at the extracted run directories.

    -ReportOnly skips all measurement and evaluates explicitly supplied artifact
    directories, so reviewers can regenerate the report from an extracted evidence set.

.EXAMPLE
    ./eng/performance/invoke-final-gate.ps1 -Provider postgresql,mssql `
        -ResultsDirectory C:\perf\final-gate

.EXAMPLE
    ./eng/performance/invoke-final-gate.ps1 -ReportOnly `
        -ReportDirectory C:\perf\final-gate\final-report `
        -PostgresqlBaselineDirectory C:\perf\baseline\postgresql-primary-500k-... `
        -PostgresqlPrimaryDirectory C:\perf\final-gate\postgresql-final-primary-... `
        -PostgresqlDescriptorsDirectory C:\perf\final-gate\postgresql-final-descriptors-...
#>
[CmdletBinding()]
param(
    [ValidateSet('postgresql', 'mssql')]
    [string[]] $Provider = @(),

    [string] $ResultsDirectory,

    [string] $ReportDirectory,

    [string] $PostgresContainerName = 'dms-postgresql',

    [string] $MssqlContainerName = 'dms-mssql-integration-2025',

    [string] $ExpectedPostgresDigest = 'sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0',

    [string] $ExpectedMssqlDigest = 'sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a',

    [string] $Fixture = 'primary-500k',

    [string] $DescriptorFixture = 'descriptors-25k',

    # The documented final-gate iteration plan and deep offset, pinned here so the
    # recorded run provenance does not depend on harness-side defaults. Ten warmups and
    # sixty measured iterations: with thirty, p95 is the second-slowest sample, so two
    # host-side stalls flip a tail gate; with sixty it takes three, and the extra warmups
    # absorb the stalls that cluster in the first measured iterations. The harness floors
    # (5 / 30) stay where they are so the original DMS-1391 artifacts still validate. A
    # non-default -Fixture needs a -DeepOffset that fits inside its row count.
    [int] $WarmupIterations = 10,

    [int] $MeasuredIterations = 60,

    [long] $DeepOffset = 450000,

    [string] $StorageNote = 'local docker volume, not tmpfs',

    # Baseline artifact directories, required for the report step: extracted from the
    # DMS-1391 baseline attachment on the Jira story.
    [string] $PostgresqlBaselineDirectory,

    [string] $MssqlBaselineDirectory,

    # Final-gate artifact directories for -ReportOnly; after a measurement run they are
    # discovered as the newest matching run under -ResultsDirectory instead.
    [string] $PostgresqlPrimaryDirectory,

    [string] $PostgresqlDescriptorsDirectory,

    [string] $MssqlPrimaryDirectory,

    [string] $MssqlDescriptorsDirectory,

    [switch] $ReportOnly,

    [switch] $SkipReport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$harnessPrefix = 'src/dms/tests/EdFi.DataManagementService.Performance.Harness'
$sourceRoot = (Resolve-Path (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '..')).Path
$harnessProject = Join-Path $sourceRoot ($harnessPrefix -replace '/', [IO.Path]::DirectorySeparatorChar) |
    Join-Path -ChildPath 'EdFi.DataManagementService.Performance.Harness.csproj'

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][string[]] $ArgumentList
    )
    $result = git -C $Repository @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "git $($ArgumentList -join ' ') failed in $Repository"
    }
    return $result
}

function Resolve-ContainerImageIdentity {
    param([Parameter(Mandatory = $true)][string] $ContainerName)
    $tag = docker inspect $ContainerName --format '{{.Config.Image}}'
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tag)) {
        throw "Cannot resolve the image tag for container '$ContainerName'. Is it running?"
    }
    $tag = $tag.Split('@')[0]

    $imageId = docker inspect $ContainerName --format '{{.Image}}'
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($imageId)) {
        throw "Cannot resolve the image id for container '$ContainerName'."
    }

    $repoDigest = docker image inspect $imageId --format '{{index .RepoDigests 0}}'
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoDigest) -or $repoDigest -eq '<no value>') {
        throw "Cannot resolve a registry digest for container '$ContainerName'; refusing to run without a recorded digest."
    }
    $digest = $repoDigest.Split('@')[1]

    return [pscustomobject]@{ Tag = $tag; Digest = $digest }
}

function Assert-ExpectedDigest {
    param(
        [Parameter(Mandatory = $true)][string] $ContainerName,
        [Parameter(Mandatory = $true)][string] $ActualDigest,
        [Parameter(Mandatory = $true)][string] $ExpectedDigest
    )
    if ($ActualDigest -ne $ExpectedDigest) {
        throw "Container '$ContainerName' runs digest $ActualDigest but $ExpectedDigest is expected. Refusing to capture evidence on an unpinned image."
    }
}

function Resolve-ContainerEndpointFromPortBindingJson {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $PortBindingJson,
        [Parameter(Mandatory = $true)][string] $ContainerPort,
        [Parameter(Mandatory = $true)][string] $ContainerName
    )
    if ([string]::IsNullOrWhiteSpace($PortBindingJson) -or $PortBindingJson.Trim() -eq 'null') {
        throw "Container '$ContainerName' reports no port bindings; cannot resolve its published $ContainerPort endpoint."
    }
    $portMap = $PortBindingJson | ConvertFrom-Json
    $portProperty = $portMap.PSObject.Properties[$ContainerPort]
    if ($null -eq $portProperty -or $null -eq $portProperty.Value -or @($portProperty.Value).Count -eq 0) {
        throw "Container '$ContainerName' does not publish $ContainerPort to the host; refusing to run against an unreachable pinned container."
    }
    $binding = @($portProperty.Value)[0]
    $bindingHost = ([string]$binding.HostIp).Trim()
    # Wildcard and blank bindings are reachable on the loopback name; anything else (for
    # example an explicit 127.0.0.1 bind) is used verbatim.
    if ($bindingHost -eq '' -or $bindingHost -eq '0.0.0.0' -or $bindingHost -eq '::') {
        $bindingHost = 'localhost'
    }
    $portValue = 0
    if (-not [int]::TryParse(([string]$binding.HostPort).Trim(), [ref] $portValue) -or $portValue -lt 1 -or $portValue -gt 65535) {
        throw "Container '$ContainerName' publishes $ContainerPort with an unusable host port '$($binding.HostPort)'."
    }
    return [pscustomobject]@{ Host = $bindingHost; Port = $portValue }
}

function Resolve-ContainerPublishedEndpoint {
    param(
        [Parameter(Mandatory = $true)][string] $ContainerName,
        [Parameter(Mandatory = $true)][string] $ContainerPort
    )
    $portBindingJson = (docker inspect $ContainerName --format '{{json .NetworkSettings.Ports}}') -join ''
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot inspect the port bindings of container '$ContainerName'. Is it running?"
    }
    return Resolve-ContainerEndpointFromPortBindingJson -PortBindingJson $portBindingJson `
        -ContainerPort $ContainerPort -ContainerName $ContainerName
}

function ConvertTo-PostgresEndpointPinnedConnectionString {
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $EndpointHost,
        [Parameter(Mandatory = $true)][int] $EndpointPort
    )
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        # PSBase reaches the real ConnectionString property: PowerShell adapts this type
        # through its type descriptor, which exposes keywords as properties instead.
        $builder.PSBase.ConnectionString = $ConnectionString
    }
    catch {
        throw "The PostgreSQL connection-string template could not be parsed: $($_.Exception.Message)"
    }
    # Npgsql accepts Server as an alias of Host; every endpoint synonym must go so the
    # template cannot override the pinned endpoint.
    foreach ($endpointKey in @('host', 'server', 'port')) {
        [void]$builder.Remove($endpointKey)
    }
    $builder['host'] = $EndpointHost
    $builder['port'] = [string]$EndpointPort
    return $builder.PSBase.ConnectionString
}

function ConvertTo-MssqlEndpointPinnedConnectionString {
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $EndpointHost,
        [Parameter(Mandatory = $true)][int] $EndpointPort
    )
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        # PSBase reaches the real ConnectionString property: PowerShell adapts this type
        # through its type descriptor, which exposes keywords as properties instead.
        $builder.PSBase.ConnectionString = $ConnectionString
    }
    catch {
        throw "The SQL Server connection-string template could not be parsed: $($_.Exception.Message)"
    }
    # SqlClient's Data Source synonyms; every one must go so the template cannot override
    # the pinned endpoint.
    foreach ($endpointKey in @('data source', 'server', 'address', 'addr', 'network address')) {
        [void]$builder.Remove($endpointKey)
    }
    $builder['data source'] = "$EndpointHost,$EndpointPort"
    return $builder.PSBase.ConnectionString
}

function Assert-CleanSourceState {
    param(
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][string[]] $PathSpec
    )
    # --untracked-files=all lists files inside untracked directories individually, so the
    # failure names every offending file rather than a bare directory.
    $statusLines = @(Invoke-Git -Repository $Repository -ArgumentList (
            @('status', '--porcelain', '--untracked-files=all', '--') + $PathSpec))
    $dirtyList = @()
    foreach ($line in $statusLines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $dirtyList += ($line.Substring(3).Trim() -replace '\\', '/')
    }
    if ($dirtyList.Count -gt 0) {
        throw ("The harness/wrapper files are dirty or untracked; commit them first so the " +
            "recorded runner commit matches the code that ran: $($dirtyList -join ', ')")
    }
}

function Assert-EnvironmentVariable {
    param([Parameter(Mandatory = $true)][string] $Name)
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Name must be set before running the final gate."
    }
}

function Find-NewestRunDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Pattern
    )
    $candidates = @(Get-ChildItem -Path $Root -Directory -Filter $Pattern -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending)
    if ($candidates.Count -eq 0) {
        throw "No run directory matching '$Pattern' exists under '$Root'."
    }
    return $candidates[0].FullName
}

function Invoke-HarnessFixture {
    param(
        [Parameter(Mandatory = $true)][string] $Filter,
        [Parameter(Mandatory = $true)][string] $FailureMessage
    )
    dotnet test $harnessProject -c Release --no-build --filter $Filter
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

if (-not $ReportOnly -and $Provider.Count -eq 0) {
    throw 'Pass -Provider (postgresql and/or mssql), or -ReportOnly with explicit artifact directories.'
}
if (-not $ReportOnly -and [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    throw '-ResultsDirectory is required unless -ReportOnly is set.'
}
if ($ReportOnly -and $SkipReport) {
    throw '-ReportOnly and -SkipReport are mutually exclusive.'
}

if (-not $ReportDirectory) {
    if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
        throw '-ReportDirectory is required when -ReportOnly runs without -ResultsDirectory.'
    }
    $ReportDirectory = Join-Path $ResultsDirectory 'final-report'
}

# The manifests record HEAD as both the runner and subject commit, so evidence capture
# refuses dirty/untracked harness or wrapper sources. Unrelated dirty paths elsewhere are
# the in-pipeline dirty-path guard's concern.
$wrapperRelativePath = [IO.Path]::GetRelativePath($sourceRoot, $PSCommandPath) -replace '\\', '/'
if (-not $ReportOnly) {
    Assert-CleanSourceState -Repository $sourceRoot -PathSpec @($harnessPrefix, $wrapperRelativePath)
}

$runnerCommit = (Invoke-Git -Repository $sourceRoot -ArgumentList @('rev-parse', 'HEAD')).Trim()
Write-Output "Runner/subject commit: $runnerCommit"

dotnet build $harnessProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw 'Building the performance harness failed.'
}

if (-not $ReportOnly) {
    # Connection-string templates are provider-specific preconditions: an mssql-only gate
    # must not demand the PostgreSQL template, and vice versa.
    if ($Provider -contains 'postgresql') {
        Assert-EnvironmentVariable -Name 'ConnectionStrings__DatabaseConnection'
    }
    if ($Provider -contains 'mssql') {
        Assert-EnvironmentVariable -Name 'ConnectionStrings__MssqlAdmin'
    }

    # The digest pins which image runs; the endpoint rewrite pins which server is measured.
    foreach ($selectedProvider in $Provider) {
        if ($selectedProvider -eq 'postgresql') {
            $identity = Resolve-ContainerImageIdentity -ContainerName $PostgresContainerName
            Assert-ExpectedDigest -ContainerName $PostgresContainerName -ActualDigest $identity.Digest -ExpectedDigest $ExpectedPostgresDigest
            $endpoint = Resolve-ContainerPublishedEndpoint -ContainerName $PostgresContainerName -ContainerPort '5432/tcp'
            $env:ConnectionStrings__DatabaseConnection = ConvertTo-PostgresEndpointPinnedConnectionString `
                -ConnectionString $env:ConnectionStrings__DatabaseConnection `
                -EndpointHost $endpoint.Host -EndpointPort $endpoint.Port
            $endpointVariableName = 'ConnectionStrings__DatabaseConnection'
            $primaryFilter = 'FullyQualifiedName~Given_Postgresql_FinalGatePrimaryRun'
            $descriptorFilter = 'FullyQualifiedName~Given_Postgresql_FinalGateDescriptorRun'
        }
        else {
            $identity = Resolve-ContainerImageIdentity -ContainerName $MssqlContainerName
            Assert-ExpectedDigest -ContainerName $MssqlContainerName -ActualDigest $identity.Digest -ExpectedDigest $ExpectedMssqlDigest
            $endpoint = Resolve-ContainerPublishedEndpoint -ContainerName $MssqlContainerName -ContainerPort '1433/tcp'
            $env:ConnectionStrings__MssqlAdmin = ConvertTo-MssqlEndpointPinnedConnectionString `
                -ConnectionString $env:ConnectionStrings__MssqlAdmin `
                -EndpointHost $endpoint.Host -EndpointPort $endpoint.Port
            $endpointVariableName = 'ConnectionStrings__MssqlAdmin'
            $primaryFilter = 'FullyQualifiedName~Given_Mssql_FinalGatePrimaryRun'
            $descriptorFilter = 'FullyQualifiedName~Given_Mssql_FinalGateDescriptorRun'
        }

        Write-Output "Provider $selectedProvider on image $($identity.Tag) @ $($identity.Digest)"
        Write-Output "Measured endpoint $($endpoint.Host):$($endpoint.Port) rewritten into $endpointVariableName"

        $env:PERF_RESULTS_DIR = $ResultsDirectory
        $env:PERF_RUNNER_COMMIT = $runnerCommit
        $env:PERF_FIXTURE = $Fixture
        $env:PERF_DESCRIPTOR_FIXTURE = $DescriptorFixture
        $env:PERF_WARMUP_ITERATIONS = "$WarmupIterations"
        $env:PERF_MEASURED_ITERATIONS = "$MeasuredIterations"
        $env:PERF_DEEP_OFFSET = "$DeepOffset"
        $env:PERF_IMAGE_TAG = $identity.Tag
        $env:PERF_IMAGE_DIGEST = $identity.Digest
        $env:PERF_STORAGE_NOTE = $StorageNote

        Invoke-HarnessFixture -Filter $primaryFilter -FailureMessage "The $selectedProvider primary final-gate run failed."
        Invoke-HarnessFixture -Filter $descriptorFilter -FailureMessage "The $selectedProvider descriptor final-gate run failed."
    }
}

if ($SkipReport) {
    Write-Output "Final-gate artifacts written under $ResultsDirectory (report skipped)"
    return
}

# Resolve the evidence directories the report evaluates. After a measurement run the newest
# matching run directories are used; -ReportOnly relies on explicit parameters. Baseline
# directories are always explicit: they come from the Jira attachment, not the repository.
$reportVariables = @{}
foreach ($reportProvider in @('postgresql', 'mssql')) {
    $isSelected = $Provider -contains $reportProvider
    $primaryParameter = if ($reportProvider -eq 'postgresql') { $PostgresqlPrimaryDirectory } else { $MssqlPrimaryDirectory }
    $descriptorsParameter = if ($reportProvider -eq 'postgresql') { $PostgresqlDescriptorsDirectory } else { $MssqlDescriptorsDirectory }
    $baselineParameter = if ($reportProvider -eq 'postgresql') { $PostgresqlBaselineDirectory } else { $MssqlBaselineDirectory }

    if (-not $ReportOnly -and $isSelected) {
        if (-not $primaryParameter) {
            $primaryParameter = Find-NewestRunDirectory -Root $ResultsDirectory -Pattern "$reportProvider-final-primary-*"
        }
        if (-not $descriptorsParameter) {
            $descriptorsParameter = Find-NewestRunDirectory -Root $ResultsDirectory -Pattern "$reportProvider-final-descriptors-*"
        }
    }

    if (-not $primaryParameter -and -not $descriptorsParameter) {
        continue
    }
    if (-not $baselineParameter) {
        $suppliedParameter = if ($reportProvider -eq 'postgresql') { '-PostgresqlBaselineDirectory' } else { '-MssqlBaselineDirectory' }
        throw ("The report step needs the $reportProvider DMS-1391 baseline directory; pass " +
            "$suppliedParameter. The baseline runs are attached to their Jira story rather " +
            'than kept in the repository, so extract that attachment first.')
    }

    $suffix = $reportProvider.ToUpperInvariant()
    $reportVariables["PERF_BASELINE_DIR_$suffix"] = $baselineParameter
    $reportVariables["PERF_FINAL_PRIMARY_DIR_$suffix"] = $primaryParameter
    $reportVariables["PERF_FINAL_DESCRIPTORS_DIR_$suffix"] = $descriptorsParameter
}

if ($reportVariables.Count -eq 0) {
    throw 'No evidence directories are available for the report step; pass the *Directory parameters.'
}

foreach ($name in @('PERF_BASELINE_DIR_POSTGRESQL', 'PERF_FINAL_PRIMARY_DIR_POSTGRESQL', 'PERF_FINAL_DESCRIPTORS_DIR_POSTGRESQL',
        'PERF_BASELINE_DIR_MSSQL', 'PERF_FINAL_PRIMARY_DIR_MSSQL', 'PERF_FINAL_DESCRIPTORS_DIR_MSSQL')) {
    if ($reportVariables.ContainsKey($name)) {
        [Environment]::SetEnvironmentVariable($name, $reportVariables[$name])
    }
    else {
        # Remove-Item leaves no blank value behind; a stale directory from an earlier run
        # in the same shell must not widen this report.
        Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
    }
}
$env:PERF_REPORT_DIR = $ReportDirectory

Invoke-HarnessFixture -Filter 'FullyQualifiedName~Given_FinalGateReportRun' `
    -FailureMessage 'The final-gate report step failed.'

Write-Output "Final-gate report written to $ReportDirectory"
