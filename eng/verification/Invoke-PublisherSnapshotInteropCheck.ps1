# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Release-validation harness proving Ed-Fi API Publisher can extract from a DMS source with snapshot
    isolation, without --ignoreIsolation.

.DESCRIPTION
    Builds the local DMS stack for one database engine and arranges what every scenario needs:

      - a seeded source data store (the Minimal seed template),
      - one empty target data store, created fresh on every invocation,
      - a source reader client (EdFiAPIPublisherReader), a source writer client (EdFiSandbox, which can
        write and read change queries to arrange changes), and a target writer client
        (EdFiAPIPublisherWriter), all with names unique to the invocation,
      - the schoolYearTypes copied from the source to the target, which the Publisher never publishes,
      - a manifest.json in a per-run folder recording versions, every non-default setting, data store
        ids, and client ids. The manifest never contains keys, secrets, tokens, or connection strings.

    The stack is started through eng/docker-compose/bootstrap-local-dms.ps1 with a harness-owned
    environment file (generated into the run folder, never a tracked file) that layers
    PublisherSnapshotInterop/interop.compose.yml onto the DMS service: it logs which data store target
    (Primary or Snapshot) served every request, shortens the data store cache interval, and raises the
    rate limit. The script proves the logging override took effect before it reports success.

    The repository hardcodes the dms-local compose project and its container names, so the script
    refuses to start a stack when dms-local containers already exist.

    Isolated is the positive proof. It creates two fresh targets (B and C), runs the baseline publish
    (--ignoreIsolation=true) into both before any change, makes pre-snapshot changes (an update, a delete,
    and a key change: U1, D1, K1), creates and registers the snapshot, then makes post-snapshot changes
    (an insert and another update, delete, and key change: I2, U2, D2, K2). Run B is an incremental
    publish from the starting change version T0 without --ignoreIsolation, as a cursor run and an offset
    run (a Publisher run uses one paging mode, never both), into target B. It must attribute every source
    request to the Snapshot, issue all seven source surfaces (GET-many, /partitions, cursor pages,
    totalCount, /deletes, /keyChanges, /availableChangeVersions), leave I2, U2, D2, and K2 off the target,
    and record the snapshot's newest change version as processed. The negative control, Run C, is the same
    pair into target C with --ignoreIsolation=true: it must read the Primary and make I2, U2, D2, and K2
    appear. If the control does not flip, the scenario fails, because the checks would not discriminate.

    All runs Isolated and then NoSnapshot on one stack. Each scenario has its own fresh targets and its
    own results file (results-Isolated.json and results-NoSnapshot.json); a single scenario writes
    results.json.

    NoSnapshot covers the operator mistake the release notes warn about: a source with no snapshot and no
    --ignoreIsolation. It registers and then retires a real snapshot, checks that the retire path answers
    Snapshot Not Found exactly like a data store that never had one, runs the Publisher without the
    opt-out into the fresh target, and asserts the run against pinned expectations. results.json in the
    run folder holds every assertion (name, expected, observed, PASS or FAIL, evidence pointer) and the
    operator-facing log lines verbatim. The script exits 1 when any assertion fails.

    Tear the stack down afterwards from eng/docker-compose:
        pwsh ./bootstrap-local-dms.ps1 -d -v [-DatabaseEngine mssql] -EnvironmentFile <run folder>/stack.env

.PARAMETER Engine
    The database engine for the whole stack: postgresql or mssql.

.PARAMETER Scenario
    StackOnly, Isolated, NoSnapshot, or All. All runs Isolated, then NoSnapshot.

.PARAMETER PublisherImage
    The Publisher image, pinned by digest. It is amd64-only and runs under emulation on arm64 hosts.

.PARAMETER SkipStackStart
    Reuse a stack that is already up (started by an earlier invocation of this script) instead of
    starting one. The script verifies the stack is usable and the source is seeded, then creates a
    fresh target and clients.

.PARAMETER Rebuild
    Passed to bootstrap-local-dms.ps1 to rebuild the DMS and Configuration Service images. By default
    the existing local images are used.

.PARAMETER DmsImageTag
    Runs the stack from published images instead of images built from this checkout: the Ed-Fi API image
    edfialliance/ed-fi-api:<tag>, started through eng/docker-compose/bootstrap-published-dms.ps1 (the dms-published
    project). A locally built image advertises the Discovery version its AssemblyVersion pins (8.0.0), so a release
    validation record needs a published image. Needs -ConfigurationServiceImage. The data stores are still
    provisioned with the SchemaTools built from this checkout, so check the checkout out at (or at a revision with
    the same relational mapping as) the published image's source revision, which the image records in its
    org.opencontainers.image.revision label; a mismatch shows up as an EffectiveSchemaHash 503.

.PARAMETER ConfigurationServiceImage
    The whole Configuration Service image reference that matches -DmsImageTag, for example
    edfialliance/ed-fi-api-configuration-service@sha256:<digest>. The Configuration Service publishes its own
    tags and digests, so it cannot be derived from the Ed-Fi API tag. Tear the published stack down afterwards
    with: pwsh ./bootstrap-published-dms.ps1 -d -v -EnvironmentFile <run folder>/stack.env [-DatabaseEngine mssql]

.PARAMETER ExpectationOverride
    Replaces pinned expectations by name, for example @{ PublisherExitCode = 1 } (NoSnapshot) or
    @{ RunBReflected = @{ I2 = $true } } (Isolated; see Get-InteropIsolatedExpectation). It exists to
    prove the verifier can fail: with a deliberately wrong value the matching assertion must report FAIL
    and the script must exit 1. An unknown name, or one for a scenario that is not selected, is an error.

.PARAMETER RunRoot
    The folder that holds one subfolder per run, named <UTC timestamp>-<engine>. Git-ignored by default.

.EXAMPLE
    pwsh eng/verification/Invoke-PublisherSnapshotInteropCheck.ps1 -Engine postgresql -Scenario StackOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("postgresql", "mssql")]
    [string]
    $Engine,

    [ValidateSet("StackOnly", "Isolated", "NoSnapshot", "All")]
    [string]
    $Scenario = "All",

    # Must equal Get-InteropDefaultPublisherImage in the module, which a unit test asserts.
    [string]
    $PublisherImage = "edfialliance/ods-api-publisher:v1.4.0@sha256:cacbeff911a5484bea98d09ea4eebdc6301ae9e12511c0e505318ea190d4d59c",

    [switch]
    $SkipStackStart,

    [switch]
    $Rebuild,

    [string]
    $DmsImageTag = "",

    [string]
    $ConfigurationServiceImage = "",

    [hashtable]
    $ExpectationOverride = @{},

    [string]
    $RunRoot = (Join-Path $PSScriptRoot "PublisherSnapshotInterop/runs")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "PublisherSnapshotInterop/PublisherSnapshotInterop.psm1") -Force

# Fail on an unbuilt scenario before anything touches Docker.
$scenarios = Resolve-InteropScenario -Scenario $Scenario

# Fail on a bad published-image selection before anything touches Docker.
$published = [bool]$DmsImageTag -or [bool]$ConfigurationServiceImage
if ($published) {
    Assert-InteropPublishedImageSelection -DmsImageTag $DmsImageTag -ConfigurationServiceImage $ConfigurationServiceImage
    if ($Rebuild) {
        throw "-Rebuild builds the local images; it cannot be combined with -DmsImageTag, which runs published images."
    }
}

# Route each override to the scenario that owns it, so a typo or a misplaced key fails before Docker starts.
$overrideByScenario = @{ Isolated = @{}; NoSnapshot = @{} }
$overrideOwner = @{ Isolated = @(Get-InteropIsolatedExpectationName); NoSnapshot = @((Get-InteropNoSnapshotExpectation).Keys) }
foreach ($overrideKey in $ExpectationOverride.Keys) {
    $owner = @($overrideOwner.Keys | Where-Object { $overrideOwner[$_] -contains $overrideKey })
    if ($owner.Count -ne 1) {
        throw "Unknown expectation '$overrideKey'. Known: $(($overrideOwner.Values | ForEach-Object { $_ }) -join ', ')."
    }
    if ($scenarios -notcontains $owner[0]) {
        throw "Expectation '$overrideKey' belongs to the $($owner[0]) scenario, which -Scenario $Scenario does not run."
    }
    $overrideByScenario[$owner[0]][$overrideKey] = $ExpectationOverride[$overrideKey]
}

$global:LASTEXITCODE = 0
$null = & docker info 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Docker is not reachable. Start Docker and re-run."
}

# Refuse before creating anything on disk when another stack owns the hardcoded dms-local names.
if (-not $SkipStackStart) {
    Assert-InteropNoForeignStack
}

$startedUtc = [datetime]::UtcNow
$runToken = New-InteropRunToken -TimestampUtc $startedUtc
$runFolderName = Get-InteropRunFolderName -TimestampUtc $startedUtc -Engine $Engine
$runFolder = Join-Path ([System.IO.Path]::GetFullPath($RunRoot)) $runFolderName
if (Test-Path -LiteralPath $runFolder) {
    throw "Run folder '$runFolder' already exists. Wait a second and re-run."
}
$null = New-Item -ItemType Directory -Path $runFolder -Force

$environmentFile = Write-InteropEnvironmentFile -RunFolder $runFolder -DmsImageTag $DmsImageTag -ConfigurationServiceImage $ConfigurationServiceImage
$environmentValues = Get-InteropEnvironmentValue -EnvironmentFile $environmentFile
$endpoint = Get-InteropEndpoint -EnvironmentValues $environmentValues

if ($SkipStackStart) {
    Write-InteropStep "Reusing the existing stack; verifying it is usable."
    Assert-InteropStackUsable -Engine $Engine -EnvironmentValues $environmentValues
}
else {
    Start-InteropStack -Engine $Engine -EnvironmentFile $environmentFile -Rebuild:$Rebuild -DmsImageTag $DmsImageTag -ConfigurationServiceImage $ConfigurationServiceImage
}

Assert-InteropPublisherImage -Image $PublisherImage

# One admin token for the whole run: the Configuration Service caps active tokens per client.
$adminToken = Get-InteropAdminToken -CmsUrl $endpoint.CmsUrl -EnvironmentValues $environmentValues

# The bootstrap registers the source data store against the stack's default database.
$sourceDatabaseName = Get-InteropSourceDatabaseName -Engine $Engine -EnvironmentValues $environmentValues
$sourceStore = Get-InteropSourceDataStore -CmsUrl $endpoint.CmsUrl -AdminToken $adminToken -DatabaseName $sourceDatabaseName
$targetStore = New-InteropDataStore -CmsUrl $endpoint.CmsUrl -AdminToken $adminToken -Engine $Engine `
    -EnvironmentFile $environmentFile -RunToken $runToken -Role "target"

$clientArguments = @{ CmsUrl = $endpoint.CmsUrl; AdminToken = $adminToken; RunToken = $runToken }
$sourceReader = New-InteropClient @clientArguments -Role "source-reader" -ClaimSetName "EdFiAPIPublisherReader" -DataStoreId $sourceStore.Id
$sourceWriter = New-InteropClient @clientArguments -Role "source-writer" -ClaimSetName "EdFiSandbox" -DataStoreId $sourceStore.Id
$targetWriter = New-InteropClient @clientArguments -Role "target-writer" -ClaimSetName "EdFiAPIPublisherWriter" -DataStoreId $targetStore.Id

$sourceReaderToken = Get-InteropClientToken -DmsUrl $endpoint.DmsUrl -Client $sourceReader
$targetWriterToken = Get-InteropClientToken -DmsUrl $endpoint.DmsUrl -Client $targetWriter

# The Publisher never publishes schoolYearTypes, so copy them to the fresh target.
$copiedSchoolYears = Copy-InteropSchoolYear -DmsUrl $endpoint.DmsUrl -SourceToken $sourceReaderToken -TargetToken $targetWriterToken
Write-InteropStep "Copied $copiedSchoolYears schoolYearTypes from the source to the target."

# The data store cache is shortened, so a request authorized for the source selects its Primary at once.
Assert-InteropSelectionLogging -DmsUrl $endpoint.DmsUrl -Token $sourceReaderToken -DataStoreId $sourceStore.Id
Write-InteropStep "Verified DMS logs 'Selected Primary target for data store $($sourceStore.Id)'."

$versions = Get-InteropVersion -DmsUrl $endpoint.DmsUrl -PublisherImage $PublisherImage -Engine $Engine -EnvironmentValues $environmentValues -Published:$published
if ($published) {
    Assert-InteropPublishedDiscoveryVersion -DmsImageTag $DmsImageTag -DiscoveryVersion $versions.DmsDiscoveryVersion
    Write-InteropStep "Discovery reads $($versions.DmsDiscoveryVersion); DMS image $($versions.DmsImageName) ($($versions.DmsImageDigest)), Configuration Service image $($versions.ConfigurationServiceImageName) ($($versions.ConfigurationServiceImageDigest))."
}
$settings = Get-InteropNonDefaultSetting -Engine $Engine -PublisherImage $PublisherImage -SkipStackStart:$SkipStackStart -Rebuild:$Rebuild `
    -DmsImageTag $DmsImageTag -ConfigurationServiceImage $ConfigurationServiceImage

$failedAssertions = @()
$manifestDataStores = @($sourceStore, $targetStore)
$manifestClients = @($sourceReader, $sourceWriter, $targetWriter)
$manifestArrangement = $null
$resultFileFor = { param($Name) if (@($scenarios).Count -gt 1) { "results-$Name.json" } else { "results.json" } }

foreach ($scenarioName in $scenarios) {
    switch ($scenarioName) {
        "StackOnly" { Write-InteropStep "StackOnly: the stack frame is ready; nothing further to run." }
        "Isolated" {
            $sourceWriterToken = Get-InteropClientToken -DmsUrl $endpoint.DmsUrl -Client $sourceWriter
            $isolated = Invoke-InteropIsolatedScenario -DmsUrl $endpoint.DmsUrl -CmsUrl $endpoint.CmsUrl -AdminToken $adminToken `
                -Engine $Engine -EnvironmentValues $environmentValues -EnvironmentFile $environmentFile -RunFolder $runFolder `
                -RunId $runFolderName -RunToken $runToken -SourceStore $sourceStore -SourceReader $sourceReader `
                -SourceReaderToken $sourceReaderToken -SourceWriterToken $sourceWriterToken -PublisherImage $PublisherImage `
                -ExpectationOverride $overrideByScenario["Isolated"] -ResultFileName (& $resultFileFor "Isolated")
            $failedAssertions += @(Get-InteropResultFailure -Result $isolated.Result)
            if (@($isolated.Result["assertions"]).Count -eq 0) {
                throw "The Isolated scenario recorded no assertions."
            }
            $manifestDataStores += @($isolated.DataStore)
            $manifestClients += @($isolated.Client)
            $manifestArrangement = $isolated.Arrangement
        }
        "NoSnapshot" {
            $scenarioResult = Invoke-InteropNoSnapshotScenario -DmsUrl $endpoint.DmsUrl -CmsUrl $endpoint.CmsUrl -AdminToken $adminToken `
                -Engine $Engine -EnvironmentValues $environmentValues -RunFolder $runFolder -RunId $runFolderName `
                -SourceStore $sourceStore -TargetStore $targetStore -SourceReader $sourceReader -TargetWriter $targetWriter `
                -SourceReaderToken $sourceReaderToken -TargetWriterToken $targetWriterToken -PublisherImage $PublisherImage `
                -ExpectationOverride $overrideByScenario["NoSnapshot"] -ResultFileName (& $resultFileFor "NoSnapshot")
            $failedAssertions += @(Get-InteropResultFailure -Result $scenarioResult)
            if (@($scenarioResult["assertions"]).Count -eq 0) {
                throw "The NoSnapshot scenario recorded no assertions."
            }
        }
        default { throw "Scenario '$scenarioName' is not implemented yet." }
    }
}

$manifest = ConvertTo-InteropManifest -RunId $runFolderName -Engine $Engine -Scenario $scenarios -StartedUtc $startedUtc `
    -Versions $versions -NonDefaultSetting $settings `
    -DataStore $manifestDataStores -Client $manifestClients -Arrangement $manifestArrangement
$manifestPath = Write-InteropRunManifest -RunFolder $runFolder -Manifest $manifest

Write-InteropStep "Source data store $($sourceStore.Id), target data store $($targetStore.Id)."
Write-InteropStep "Manifest: $manifestPath"
Write-InteropStep "Run folder: $runFolder"

if ($failedAssertions.Count -gt 0) {
    Write-InteropStep "FAILED: $($failedAssertions.Count) assertion(s) did not match: $(($failedAssertions | ForEach-Object { $_['name'] }) -join '; ')"
    exit 1
}
