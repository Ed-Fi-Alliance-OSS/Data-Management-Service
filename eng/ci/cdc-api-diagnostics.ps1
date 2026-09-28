# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Private checkpoint text is parsed with an exact grammar, never copied as raw stdout.
# This is diagnostic data, not a second source of scenario outcomes.
function Get-CdcApiDiagnostic {
    param([string] $Path, [string] $InvocationId)
    $result = [ordered]@{ Availability = 'Missing'; Truncated = $false; Rejected = 0; Checkpoints = @() }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $result }
    if ((Get-Item -LiteralPath $Path).Length -gt 1050661) { $result.Availability = 'Oversized'; return $result }
    $lines = [IO.File]::ReadAllLines($Path)
    if ($lines.Count -eq 0 -or $lines[0] -cne $InvocationId) { $result.Availability = 'IdentityMismatch'; return $result }
    $result.Availability = 'Available'
    $result.Truncated = $lines.Count -gt 2049
    $number = '[0-9]{1,19}'
    $checkpoints = @('admitted-identity-held-create', 'held-create', 'create-consumed', 'held-update', 'update-consumed',
        'delete-consumed', 'materialized-N', 'API-N+1', 'next-blocked', 'converged', 'cleanup',
        'deleted-while-held', 'tombstone-consumed-while-held', 'after-projector-drain', 'absent-after-fenced-drain',
        'published-before-rebuild', 'rebuild-consumed', 'repopulated-and-drained', 'cleanup-deletes-consumed',
        'before-shutdown-consumed', 'outage-work-only', 'recovery-consumed', 'converged-and-acknowledged',
        'queued-without-direct-fill', 'healthy-publication', 'healthy-before-fault', 'api-success-work-held-during-fault',
        'in-fault-write-recovered', 'same-binding-recovery-consumed', 'fresh-recovery-publication',
        'cleanup-delete-consumed', 'healthy-before-loss', 'api-success-after-containment-work-held')
    $label = '(?:' + (($checkpoints | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')'
    $events = @('projector-released-and-drained', 'old-runtime-disposed', 'replacement-initialized', 'second-work-page-held',
        'api-write-and-read-before-shutdown', 'api-write-and-read-fully-stopped', 'api-write-and-read-draining',
        'unknown-not-ready-offset-unavailable', 'rejected-Restart-Connect-Unavailable', 'rejected-Resume-Connect-Unavailable',
        'offset-delegation-restored', 'connector-stopped-tasks-zero-offsets-deleted-once-authoritative-Missing',
        'lost-NotReady-ConnectOffsetMissing-persisted-contained', 'retained-incident-rejected-Restart',
        'retained-incident-rejected-Resume', 'healthy-evidence-decorator-removed-real-offsets-Missing')
    $eventGrammar = '(?:' + ($events -join '|') + ')'
    $utc = '[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}\+00:00'
    $patterns = @(
        "${label}: sourceVersion=$number cacheVersion=$number workVersion=$number",
        "${label}: (public|progress) partition=$number start=$number end=$number",
        "healthy-publication:(public|progress):partition=$number`:start=$number`:end=$number",
        "${label}:provider-fence-(started|completed)",
        "${eventGrammar}(: utc=$utc)?",
        "N-completed: candidateVersion=$number outcome=StaleCandidateSuppressed",
        "tombstone-consumed-while-held: partition=$number offset=$number",
        'administration: completed=(ResolveTarget|AcquireMutex|Preflight|EnterResetting|ClearCache|ClearWork|EnterRebuilding|CaptureBoundary|SeedBaseline|DrainWork|EnterTracking|Complete) lifecycle=(Disabled|Resetting|Rebuilding|Tracking) cacheAhead=(True|False)',
        "rebuild-complete: liveKeys=$number consumerKeys=$number explicitBaseline=true",
        "work-page: size=$number selected=$number",
        "replacement-operations: baselineBoundaries=$number baselinePages=$number inventoryPages=$number dropped=$number",
        "fresh-controller-retained-incident:healthy-offset-reads=$number"
    )
    $grammar = '\ACDC-E2E-0[1-8]:(?:' + ($patterns -join '|') + ')\z'
    $items = [Collections.Generic.List[object]]::new()
    foreach ($line in ($lines | Select-Object -Skip 1 -First 2048)) {
        if ($line.Length -le 512 -and ($line -cmatch $grammar -or
            $line -cmatch '\AAttachment:(?:effectiveSchema=[a-fA-F0-9]{64}|runtime=[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,5}|pageSize=[0-9]{1,9})\z')) {
            $items.Add($line)
        } else { $result.Rejected++ }
    }
    $result.Checkpoints = @($items.ToArray())
    return $result
}

function Get-CdcApiRuntimeInput {
    param([string] $Path, [string] $InvocationId)
    $safe = [ordered]@{ Availability = 'Missing'; Images = @() }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $safe }
    if ((Get-Item -LiteralPath $Path).Length -gt 16384) { $safe.Availability = 'Oversized'; return $safe }
    try {
        $inputData = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 8
        if ($inputData.InvocationId -cne $InvocationId) { $safe.Availability = 'IdentityMismatch'; return $safe }
        $safe.Availability = ConvertTo-CdcApiEnum $inputData.Availability @('Available', 'Partial')
        $safe.CleanupMode = ConvertTo-CdcApiEnum $inputData.CleanupMode @('Governed', 'OwnedPartial')
        $safe.ResourcesAbsent = $inputData.ResourcesAbsent -is [bool] -and $inputData.ResourcesAbsent -ceq $true
        $safe.OwnedCounts = [ordered]@{}
        foreach ($kind in @('container', 'volume', 'network')) {
            $count = $inputData.OwnedCounts[$kind]
            $safe.OwnedCounts[$kind] = if (($count -is [int] -or $count -is [long]) -and $count -ge 0 -and $count -le 1000) { $count } else { 0 }
        }
        $safe.Images = @($inputData.Images | Select-Object -First 8 | ForEach-Object {
            if ($_.Service -is [string] -and $_.ImageId -is [string] -and $_.Service -cin @('db', 'kafka', 'kafka-cdc-worker', 'dms', 'config') -and
                $_.ImageId -cmatch '\Asha256:[a-f0-9]{64}\z') {
                @{ Service = $_.Service; ImageId = $_.ImageId; ManifestDigest = $(if ($_.ManifestDigest -is [string] -and $_.ManifestDigest -cmatch '\Asha256:[a-f0-9]{64}\z') { $_.ManifestDigest } else { '' }) }
            }
        })
    } catch { $safe.Availability = 'Invalid' }
    return $safe
}

function Get-CdcApiTraceability {
    @(1..8 | ForEach-Object {
        $invariants = @('CDC-INV-07')
        if ($_ -le 6) { $invariants += 'CDC-INV-03' }
        if ($_ -in @(1, 2, 5, 7, 8)) { $invariants += 'CDC-INV-06' }
        if ($_ -in @(5, 7, 8)) { $invariants += 'CDC-INV-11' }
        @{ Id = ('CDC-E2E-{0:00}' -f $_); Invariants = $invariants }
    })
}

function ConvertTo-CdcApiEnum {
    param($Value, [string[]] $Allowed)
    if ($Value -is [string] -and $Value -cin $Allowed) { return $Value }
    return 'Invalid'
}

# Called inside owned cleanup before retirement, while actual container images still exist.
# A diagnostics failure never prevents governed cleanup; the exported availability records it.
function Write-CdcApiRuntimeInput {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Private diagnostics only; no infrastructure mutation.')]
    param([string] $Path, [string] $InvocationId, [Collections.IDictionary] $Resources, [bool] $Governed)
    $snapshot = [ordered]@{ InvocationId = $InvocationId; Availability = 'Available'; Images = @()
        CleanupMode = $(if ($Governed) { 'Governed' } else { 'OwnedPartial' }); ResourcesAbsent = $false
        OwnedCounts = [ordered]@{} }
    foreach ($kind in @('container', 'volume', 'network')) { $snapshot.OwnedCounts[$kind] = @($Resources[$kind]).Count }
    if (@($Resources.container).Count -gt 8) { $snapshot.Availability = 'Partial' }
    foreach ($container in @($Resources.container | Select-Object -First 8)) {
        try {
            $format = '{"Service":{{json (index .Config.Labels "com.docker.compose.service")}},"ImageId":{{json .Image}},"Reference":{{json .Config.Image}}}'
            $probe = Invoke-NativeCommandWithInput -FilePath docker -ArgumentList @('inspect', '--format', $format, $container) -InputText '' -TimeoutSeconds 10
            if ($probe.ExitCode -ne 0 -or $probe.FailureKind -ne 'None') { throw 'Inspect' }
            $item = $probe.StandardOutput | ConvertFrom-Json -AsHashtable
            $digest = if ($item.Reference -cmatch '@(sha256:[a-f0-9]{64})\z') { $Matches[1] } else { '' }
            $snapshot.Images += @{ Service = $item.Service; ImageId = $item.ImageId; ManifestDigest = $digest }
        } catch { $snapshot.Availability = 'Partial' }
    }
    $snapshot | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path
}
