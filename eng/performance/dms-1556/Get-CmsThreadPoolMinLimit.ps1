# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 (Q17): capture a CMS dump via dotnet-monitor and read the effective thread-pool
minimum from it with the pinned dotnet-dump `threadpool` command.

.DESCRIPTION
Dump analysis reads captured runtime state; nothing executes ThreadPool.GetMinThreads in
the target. The CMS image is Alpine (linux-musl), so the dump is analyzed inside an
Alpine SDK container with a pinned dotnet-dump version, and the script verifies the
`Worker Min Limit` label is present in the output instead of assuming it.

Collect dumps OUTSIDE timed burst windows (before the first or after the last timed
round) so dump collection does not contaminate timeout evidence.

Outputs: the dump (.dmp), the full threadpool command output (.threadpool.txt), and a
JSON record with the parsed value, tool version, and the container's thread-pool-relevant
environment.
#>
[CmdletBinding()]
param(
    # dotnet-monitor sidecar from eng/docker-compose/local-config-diagnostics.yml.
    [string] $MonitorBaseUrl = 'http://localhost:52323',

    # Must match the CMS image's runtime: the CMS Dockerfile builds on Alpine, so the
    # analysis container is an Alpine SDK image (digest resolved 2026-09-29).
    [string] $AnalysisImage = 'mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:3cc3bbbbf93d82104892f42aa9106b6be4d120346dea0649643a97c801525256',

    # Pinned per Q17 so the output format (incl. the `Worker Min Limit` label) is stable
    # across the investigation. Resolved and label-verified 2026-09-29.
    [string] $DotnetDumpVersion = '10.0.745401',

    [string] $CmsContainerName = 'ed-fi-api-config-service',

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$dumpName = "cms-threadpool-$timestamp.dmp"
$dumpPath = Join-Path $OutputDirectory $dumpName
$analysisPath = Join-Path $OutputDirectory "cms-threadpool-$timestamp.threadpool.txt"
$recordPath = Join-Path $OutputDirectory "cms-threadpool-$timestamp.json"

Write-Output "Resolving CMS process from $MonitorBaseUrl/processes..."
$processes = @(Invoke-RestMethod -Uri "$MonitorBaseUrl/processes")
if ($processes.Count -ne 1) {
    throw "Expected exactly one process at the sidecar, found $($processes.Count). Is the overlay applied to more than one service?"
}
$processId = $processes[0].pid

Write-Output "Collecting full dump of pid $processId (run this outside timed burst windows)..."
Invoke-WebRequest -Uri "$MonitorBaseUrl/dump?pid=$processId&type=Full" -OutFile $dumpPath -TimeoutSec 300
$dumpSizeMb = [Math]::Round((Get-Item -LiteralPath $dumpPath).Length / 1MB, 1)
Write-Output "Dump written: $dumpPath ($dumpSizeMb MB)"

Write-Output "Analyzing with dotnet-dump $DotnetDumpVersion in $AnalysisImage..."
$mountDir = (Resolve-Path -LiteralPath $OutputDirectory).Path
$analysisScript = "dotnet tool install --tool-path /tools dotnet-dump --version $DotnetDumpVersion 1>&2 && /tools/dotnet-dump analyze /dumps/$dumpName -c threadpool -c exit"
$analysisOutput = & docker run --rm -v "${mountDir}:/dumps" $AnalysisImage sh -c $analysisScript 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "dotnet-dump analysis container exited with code $LASTEXITCODE."
}
$analysisOutput | Set-Content -LiteralPath $analysisPath -Encoding utf8

# Q17: verify the pinned version's actual output labels rather than assuming them. The
# diagnostics ThreadPoolCommand reports threadPool.MinThreads as `Worker Min Limit`.
$outputText = $analysisOutput -join "`n"
if ($outputText -notmatch '(?m)Worker\s+Min\s+Limit') {
    Write-Error "Analysis output does not contain the expected 'Worker Min Limit' label; inspect $analysisPath and re-verify the pinned dotnet-dump version's output format."
    exit 1
}
$workerMinLimit = $null
if ($outputText -match '(?m)Worker\s+Min\s+Limit:?\s+(\d+)') {
    $workerMinLimit = [int]$Matches[1]
}

$cmsEnvironment = @()
try {
    $envLines = docker inspect $CmsContainerName --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null
    if ($LASTEXITCODE -eq 0) {
        $cmsEnvironment = @($envLines | Where-Object { $_ -match '^(DOTNET_ThreadPool|DOTNET_PROCESSOR_COUNT)' })
    }
}
catch {
    # Best effort; the dump value stands on its own.
    Write-Verbose "Could not inspect ${CmsContainerName}: $($_.Exception.Message)"
}

$record = [pscustomobject]@{
    capturedUtc       = [DateTime]::UtcNow.ToString('o')
    pid               = $processId
    dump              = $dumpName
    dumpSizeMb        = $dumpSizeMb
    analysisImage     = $AnalysisImage
    dotnetDumpVersion = $DotnetDumpVersion
    workerMinLimit    = $workerMinLimit
    cmsEnvironment    = $cmsEnvironment
    analysisOutput    = (Split-Path -Leaf $analysisPath)
}
$record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $recordPath -Encoding utf8

Write-Output "Worker Min Limit: $workerMinLimit"
Write-Output "Record written to $recordPath"
