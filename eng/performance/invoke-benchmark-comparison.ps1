# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Runs one DMS-1019 benchmark comparison: alternating baseline/candidate side runs of the
    performance harness on one provider, then the comparison report.

.DESCRIPTION
    A thin entry point over BenchmarkComparison.psm1 (Invoke-BenchmarkComparison). The harness,
    its profile, the module and this script must be committed clean. ConnectionStrings__DatabaseConnection
    (and ConnectionStrings__MssqlAdmin for mssql) must be set; they are credential templates
    whose endpoint is rewritten to the digest-pinned container's published port.

    Each invocation writes to a fresh <ResultsDirectory>/<provider>-<purpose>-<timestamp>-<candidate8>
    directory and keeps a transcript there as wrapper.log. A setup failure leaves the transcript
    in ResultsDirectory as wrapper-<timestamp>-<id>.log. -Provider accepts pgsql as an alias of
    postgresql; provider and purpose are case-insensitive.

    Exit codes: 1 setup failed or the report could not be produced; otherwise the comparison's
    exit code (0 acceptable or suspected regression, 2 invalid).

.EXAMPLE
    ./eng/performance/invoke-benchmark-comparison.ps1 -Provider postgresql -ResultsDirectory C:\perf\bench `
        -BaselineCommit df7e23425beef2de46fa98b3b5b48578cb2d3692 -Purpose Calibration -Pairs 5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BaselineCommit,
    [string] $CandidateCommit,
    [Parameter(Mandatory)][ValidateSet('postgresql', 'pgsql', 'mssql')][string] $Provider,
    [Parameter(Mandatory)][string] $ResultsDirectory,
    [ValidateRange(0, 99)][int] $Pairs = 0,
    [string] $WorktreeRoot,
    [switch] $Smoke,
    [ValidateSet('Candidate', 'Calibration', 'Sizing', 'Repeatability', 'Detection', 'BoundaryProbe')][string] $Purpose = 'Candidate',
    # '<scenarioId>=<factor>;...', candidate sides only; for Sizing or Candidate.
    [string] $InjectSlowdown,
    [string] $PostgresContainerName = 'dms-postgresql',
    [string] $MssqlContainerName = 'dms-mssql-integration-2025',
    [string] $ExpectedPostgresDigest = 'sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873',
    [string] $ExpectedMssqlDigest = 'sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a'
)

Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'BenchmarkComparison.psm1') -Force

New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
# Unique per invocation: two invocations started in the same second must not share a transcript.
$transcript = Join-Path $ResultsDirectory ('wrapper-{0}-{1}.log' -f [DateTime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"), [guid]::NewGuid().ToString('N').Substring(0, 8))
Start-Transcript -LiteralPath $transcript | Out-Null
try {
    $result = Invoke-BenchmarkComparison @PSBoundParameters
}
finally {
    Stop-Transcript | Out-Null
}

# The module names the invocation directory during setup; a setup failure (including another
# comparison holding the host lock) has none and leaves the transcript in ResultsDirectory.
if ($null -ne $result.InvocationDirectory) {
    Move-Item -LiteralPath $transcript -Destination (Join-Path $result.InvocationDirectory 'wrapper.log')
}

exit $result.ExitCode
