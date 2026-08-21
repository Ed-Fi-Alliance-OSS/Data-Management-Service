# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Captures the DMS traditional-paging performance baseline.

.DESCRIPTION
    Overlays the performance harness onto a worktree of the pre-change subject commit,
    verifies the worktree is clean apart from the overlay, validates the running database
    container images against the expected digests, builds the harness, and runs the
    evidence fixtures. The source harness directory and this script must be committed
    clean before anything is copied: the manifests record the source HEAD as the runner
    commit, so a dirty overlay source would produce artifacts claiming a commit that does
    not match the code that ran. Connection strings must already be set:
    ConnectionStrings__DatabaseConnection always, and ConnectionStrings__MssqlAdmin when
    the mssql provider is selected.

.EXAMPLE
    ./eng/performance/invoke-traditional-baseline.ps1 -Provider postgresql,mssql `
        -ResultsDirectory C:\perf\baseline
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('postgresql', 'mssql')]
    [string[]] $Provider,

    [Parameter(Mandatory = $true)]
    [string] $ResultsDirectory,

    [string] $BaselineCommit = '5656477957eb2f18e827b7969e5079b424596ae0',

    [string] $WorktreePath,

    [string] $PostgresContainerName = 'dms-postgresql',

    [string] $MssqlContainerName = 'dms-mssql-integration-2025',

    [string] $ExpectedPostgresDigest = 'sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0',

    [string] $ExpectedMssqlDigest = 'sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a',

    [string] $Fixture = 'primary-500k',

    # The documented primary-baseline iteration plan and deep offset, pinned here so the
    # recorded run provenance does not depend on harness-side defaults. A non-default
    # -Fixture needs a -DeepOffset that fits inside its row count.
    [int] $WarmupIterations = 5,

    [int] $MeasuredIterations = 30,

    [long] $DeepOffset = 450000,

    [string] $StorageNote = 'local docker volume, not tmpfs',

    [switch] $ReuseWorktree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$overlayPrefix = 'src/dms/tests/EdFi.DataManagementService.Performance.Harness'
$sourceRoot = (Resolve-Path (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '..')).Path

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

function Assert-CleanOverlay {
    param([Parameter(Mandatory = $true)][string] $Worktree)
    $statusLines = @(Invoke-Git -Repository $Worktree -ArgumentList @('status', '--porcelain'))
    $violationList = @()
    foreach ($line in $statusLines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $path = ($line.Substring(3).Trim() -replace '\\', '/').TrimEnd('/')
        # Segment-boundary match: a sibling directory sharing the prefix text must not pass.
        if (-not ($path -eq $overlayPrefix -or $path.StartsWith($overlayPrefix + '/'))) {
            $violationList += $path
        }
    }
    if ($violationList.Count -gt 0) {
        throw "The baseline worktree is dirty outside the approved overlay: $($violationList -join ', ')"
    }
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
        throw ("The source harness/wrapper files are dirty or untracked; commit them first so the " +
            "recorded runner commit matches the copied sources: $($dirtyList -join ', ')")
    }
}

function Assert-EnvironmentVariable {
    param([Parameter(Mandatory = $true)][string] $Name)
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Name must be set before capturing the baseline."
    }
}

if (-not $WorktreePath) {
    $WorktreePath = Join-Path (Split-Path $sourceRoot -Parent) 'dms-perf-baseline'
}

Assert-EnvironmentVariable -Name 'ConnectionStrings__DatabaseConnection'
if ($Provider -contains 'mssql') {
    Assert-EnvironmentVariable -Name 'ConnectionStrings__MssqlAdmin'
}

# The manifests record HEAD as the runner commit while the overlay copies working-tree
# files, so evidence capture refuses dirty/untracked harness or wrapper sources. Unrelated
# dirty paths elsewhere in the repository stay allowed.
$wrapperRelativePath = [IO.Path]::GetRelativePath($sourceRoot, $PSCommandPath) -replace '\\', '/'
Assert-CleanSourceState -Repository $sourceRoot -PathSpec @($overlayPrefix, $wrapperRelativePath)

$runnerCommit = (Invoke-Git -Repository $sourceRoot -ArgumentList @('rev-parse', 'HEAD')).Trim()
Write-Output "Runner commit:  $runnerCommit"
Write-Output "Subject commit: $BaselineCommit"

if (Test-Path $WorktreePath) {
    if (-not $ReuseWorktree) {
        throw "Worktree path '$WorktreePath' already exists. Pass -ReuseWorktree to reuse it."
    }
}
else {
    Invoke-Git -Repository $sourceRoot -ArgumentList @('worktree', 'add', $WorktreePath, $BaselineCommit) | Out-Null
}

$worktreeHead = (Invoke-Git -Repository $WorktreePath -ArgumentList @('rev-parse', 'HEAD')).Trim()
if ($worktreeHead -ne $BaselineCommit) {
    throw "The worktree at '$WorktreePath' is at $worktreeHead, not the expected baseline $BaselineCommit."
}

$worktreeResolved = (Resolve-Path $WorktreePath).Path
$sourceHarness = Join-Path $sourceRoot ($overlayPrefix -replace '/', [IO.Path]::DirectorySeparatorChar)
$targetHarness = Join-Path $worktreeResolved ($overlayPrefix -replace '/', [IO.Path]::DirectorySeparatorChar)

# The overlay must be exact: a reused worktree may hold stale harness files that robocopy /E
# would leave in place. Deleting first is safe only after proving the target sits inside the
# worktree.
if (-not $targetHarness.StartsWith($worktreeResolved + [IO.Path]::DirectorySeparatorChar)) {
    throw "Refusing to delete '$targetHarness': it is not inside the worktree '$worktreeResolved'."
}
if (Test-Path $targetHarness) {
    Remove-Item -LiteralPath $targetHarness -Recurse -Force
}

robocopy $sourceHarness $targetHarness /E /XD bin obj /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "Copying the harness overlay failed with robocopy exit code $LASTEXITCODE."
}

Assert-CleanOverlay -Worktree $WorktreePath

$harnessProject = Join-Path $targetHarness 'EdFi.DataManagementService.Performance.Harness.csproj'
dotnet build $harnessProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw 'Building the harness in the baseline worktree failed.'
}

foreach ($selectedProvider in $Provider) {
    if ($selectedProvider -eq 'postgresql') {
        $identity = Resolve-ContainerImageIdentity -ContainerName $PostgresContainerName
        Assert-ExpectedDigest -ContainerName $PostgresContainerName -ActualDigest $identity.Digest -ExpectedDigest $ExpectedPostgresDigest
        $fixtureFilter = 'FullyQualifiedName~Given_Postgresql_TraditionalBaselineRun'
    }
    else {
        $identity = Resolve-ContainerImageIdentity -ContainerName $MssqlContainerName
        Assert-ExpectedDigest -ContainerName $MssqlContainerName -ActualDigest $identity.Digest -ExpectedDigest $ExpectedMssqlDigest
        $fixtureFilter = 'FullyQualifiedName~Given_Mssql_TraditionalBaselineRun'
    }

    Write-Output "Provider $selectedProvider on image $($identity.Tag) @ $($identity.Digest)"

    $env:PERF_RESULTS_DIR = $ResultsDirectory
    $env:PERF_RUNNER_COMMIT = $runnerCommit
    $env:PERF_FIXTURE = $Fixture
    $env:PERF_WARMUP_ITERATIONS = "$WarmupIterations"
    $env:PERF_MEASURED_ITERATIONS = "$MeasuredIterations"
    $env:PERF_DEEP_OFFSET = "$DeepOffset"
    $env:PERF_IMAGE_TAG = $identity.Tag
    $env:PERF_IMAGE_DIGEST = $identity.Digest
    $env:PERF_STORAGE_NOTE = $StorageNote

    dotnet test $harnessProject -c Release --no-build --filter $fixtureFilter
    if ($LASTEXITCODE -ne 0) {
        throw "The $selectedProvider baseline run failed."
    }
}

Write-Output "Baseline artifacts written under $ResultsDirectory"
