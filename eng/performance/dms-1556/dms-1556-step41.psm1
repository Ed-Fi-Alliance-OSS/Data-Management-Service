# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
DMS-1556 step 4.1 analysis over the FIXED image's captures: CMS log parsing, the fixed
code's dependency log signals (spec 4.9), per-response stage classification through the
correlation id, and authentication sync waits in managed-stack captures. Pure functions
over retained files; used by Invoke-Step41Outage.ps1 and Get-Step41Report.ps1. Requires
dms-1556-analysis.psm1 (ConvertTo-DmsUtc) to be imported first.
#>

Set-StrictMode -Version Latest

# Message templates of the fixed code, matched as substrings of MessageTemplate. The last
# entry is the RETIRED baseline string: the fixed code never emits it, so a zero count
# says nothing about whether dependency failures occurred (spec Phase 4, changed signal).
$script:SignalPatterns = [ordered]@{
    boundaryUnavailable       = 'Authentication could not reach a decision: the {Category} is unavailable (trace {TraceId})'
    jwksUnavailable           = 'The JWKS could not be served: the {Category} is unavailable (trace {TraceId})'
    loadFailed                = 'Signing-key load failed ('
    snapshotPublished         = 'Signing-key snapshot {Version} published from'
    snapshotEmpty             = 'Signing-key snapshot {Version} is empty'
    lateLoadDiscarded         = 'A signing-key load that outlived its deadline has finished'
    unknownKeyRefresh         = 'was not in the signing-key snapshot; unknown-key refresh outcome'
    introspectionUnavailable  = 'Token validation could not reach a decision'
    retiredKeyFetchFailure    = 'Failed to fetch public keys for JWKS'
}

function Get-DmsCmsLogEntry {
    <#
    .SYNOPSIS
    Parses the CMS JSON log once. Keeps the raw timestamp text (ConvertFrom-Json would
    shift ISO strings to local time) and the raw line for correlation-id matching. Only
    Warning/Error/Fatal lines and the signing-key Information lines are parsed: nothing
    else is read by the classification or the signal counts, and skipping Debug and
    request-logging lines before JSON parsing keeps a pause run's ~300,000-line log
    tractable. Log completeness is judged with Get-DmsCmsLogFirstUtc, not from these
    entries.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param([Parameter(Mandatory)][string] $Path)

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line -notmatch '^\{"Timestamp":"(?<ts>[^"]+)"') { continue }
        $timestampText = $Matches.ts
        if ($line -notmatch '"Level":"(Warning|Error|Fatal)"' -and -not $line.Contains('Signing-key')) { continue }
        try { $entry = $line | ConvertFrom-Json } catch { continue }
        $props = if ($entry.PSObject.Properties['Properties']) { $entry.Properties } else { $null }
        $exceptionText = if ($entry.PSObject.Properties['Exception'] -and $entry.Exception) { [string]$entry.Exception } else { '' }
        $exceptionLines = @($exceptionText -split "`n")
        # The innermost '---> Type: message' line names the driver failure behind a typed
        # dependency exception (e.g. 53300 versus a connect timeout).
        $innerLines = @($exceptionLines | Where-Object { $_ -match '^\s*---> ' } | ForEach-Object { ($_ -replace '^\s*---> ', '').Trim() })
        $entries.Add([pscustomobject]@{
                utc         = ConvertTo-DmsUtc -Text $timestampText
                level       = [string]$entry.Level
                template    = [string]$entry.MessageTemplate
                category    = if ($props -and $props.PSObject.Properties['Category']) { [string]$props.Category } else { '' }
                trigger     = if ($props -and $props.PSObject.Properties['Trigger']) { [string]$props.Trigger } else { '' }
                requestPath = if ($props -and $props.PSObject.Properties['RequestPath']) { [string]$props.RequestPath } else { '' }
                exception   = if ($entry.PSObject.Properties['Exception'] -and $entry.Exception) { ([string]$entry.Exception -split '[\s:]', 2)[0] } else { '' }
                exceptionHead = if ($exceptionText) { $exceptionLines[0].Trim() } else { '' }
                innerException = if ($innerLines.Count) { $innerLines[-1] } else { '' }
                rendered    = [string]$entry.RenderedMessage
                line        = $line
            })
    }
    return , $entries.ToArray()
}

function Get-DmsCmsLogFirstUtc {
    <#
    .SYNOPSIS
    UTC instant of the first JSON log line of any level; $null when there is none. A log
    whose first line is later than the burst start lost lines to rotation.
    #>
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory)][string] $Path)

    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line -match '^\{"Timestamp":"(?<ts>[^"]+)"') { return ConvertTo-DmsUtc -Text $Matches.ts }
    }
    return $null
}

function Get-DmsSignalCount {
    <#
    .SYNOPSIS
    Counts each SignalPatterns entry in a window, by level/category/trigger, with the
    signing-key load failures and snapshot publications listed in order.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][object[]] $Entries,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc
    )

    $window = @($Entries | Where-Object { $_.utc -and $_.utc -ge $StartUtc -and $_.utc -le $EndUtc })
    $counts = [ordered]@{}
    foreach ($name in $script:SignalPatterns.Keys) {
        $matched = @($window | Where-Object { $_.template.Contains($script:SignalPatterns[$name]) })
        $byDetail = [ordered]@{}
        foreach ($group in ($matched | Group-Object -Property { '{0}|{1}|{2}' -f $_.level, $_.category, $_.trigger })) {
            $byDetail[$group.Name] = $group.Count
        }
        $counts[$name] = [pscustomobject]@{
            total      = $matched.Count
            byLevelCategoryTrigger = $byDetail
            firstUtc   = if ($matched.Count) { ($matched | Sort-Object utc | Select-Object -First 1).utc.ToString('o') } else { $null }
            lastUtc    = if ($matched.Count) { ($matched | Sort-Object utc | Select-Object -Last 1).utc.ToString('o') } else { $null }
        }
    }
    $loadFailures = @($window | Where-Object { $_.template.Contains($script:SignalPatterns.loadFailed) } | Sort-Object utc |
            ForEach-Object { [pscustomobject]@{ utc = $_.utc.ToString('o'); message = $_.rendered } })
    $publications = @($window | Where-Object { $_.template.Contains($script:SignalPatterns.snapshotPublished) } | Sort-Object utc |
            ForEach-Object { [pscustomobject]@{ utc = $_.utc.ToString('o'); message = $_.rendered } })
    return [pscustomobject]@{ counts = $counts; loadFailures = $loadFailures; publications = $publications }
}

function Get-DmsResponseClassification {
    <#
    .SYNOPSIS
    Classifies every response of a run by the stage that produced it, through the
    correlation id: 'authentication' (the boundary's Error names the category),
    'post-authentication' (an Error on the /v3/profiles path and no boundary line),
    'transport', or 'unclassified'. A 503 is checked for the dependency contract
    (Retry-After 30, problem+json, no challenge) independently of the log correlation.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)][object[]] $Rows,
        [Parameter(Mandatory)][object[]] $Entries
    )

    $byTrace = @{}
    foreach ($entry in $Entries) {
        if ($entry.line -match '"TraceId":"(?<id>[0-9A-Z]+:[0-9A-F]+)"') {
            $id = $Matches.id
            if (-not $byTrace.ContainsKey($id)) { $byTrace[$id] = [System.Collections.Generic.List[object]]::new() }
            $byTrace[$id].Add($entry)
        }
    }
    foreach ($row in $Rows) {
        $status = [int]$row.statusCode
        $stage = 'ok'
        $category = ''
        $detail = ''
        if ($status -ne 200) {
            $lines = if ($row.traceId -and $byTrace.ContainsKey($row.traceId)) { @($byTrace[$row.traceId]) } else { @() }
            $boundary = @($lines | Where-Object { $_.template.Contains($script:SignalPatterns.boundaryUnavailable) }) | Select-Object -First 1
            $errors = @($lines | Where-Object { $_.level -in @('Error', 'Fatal') -and -not $_.template.Contains($script:SignalPatterns.boundaryUnavailable) })
            if ($boundary) {
                $stage = 'authentication'
                $category = $boundary.category
                $detail = $boundary.innerException
            }
            elseif ($status -eq 0) {
                $stage = 'transport'
                $detail = [string]$row.error
            }
            elseif ($errors.Count -gt 0 -and @($errors | Where-Object { $_.requestPath -match '/v3/profiles' }).Count -gt 0) {
                $stage = 'post-authentication'
                $first = @($errors | Where-Object { $_.requestPath -match '/v3/profiles' })[0]
                $detail = if ($first.innerException) { $first.innerException } elseif ($first.exceptionHead) { $first.exceptionHead } else { $first.template }
            }
            else {
                $stage = 'unclassified'
                $detail = (@($lines | ForEach-Object { $_.template }) | Select-Object -Unique) -join ' / '
            }
        }
        [pscustomobject]@{
            round = [int]$row.round; index = [int]$row.index; admittedUtc = [string]$row.admittedUtc
            bodyCompletedUtc = [string]$row.bodyCompletedUtc; durationMs = [double]$row.durationMs
            statusCode = $status; bodyValid = [string]$row.bodyValid; traceId = [string]$row.traceId
            retryAfter = [string]$row.retryAfter; contentType = [string]$row.contentType; wwwAuthenticate = [string]$row.wwwAuthenticate
            stage = $stage; category = $category; detail = $detail
            dependencyContract = if ($status -eq 503) { $row.retryAfter -eq '30' -and $row.contentType -eq 'application/problem+json' -and -not $row.wwwAuthenticate } else { $null }
        }
    }
}

function Get-DmsAuthenticationSyncWaitCount {
    <#
    .SYNOPSIS
    Threads of one /stacks capture that sit in a synchronous Task wait with an
    authentication frame (JwtBearer, the signing-key components, the token manager, or
    IdentityModel) on the same stack: the shape the baseline resolver produced (F1).
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param([Parameter(Mandatory)][string] $Path)

    $count = 0
    if (Test-Path -LiteralPath $Path) {
        $text = [System.IO.File]::ReadAllText($Path)
        foreach ($block in ($text -split '(?m)^(?=Thread: )')) {
            if ($block -match 'Task\.InternalWait' -and $block -match 'JwtBearer|SigningKey|TokenManager|IdentityModel') { $count++ }
        }
    }
    return $count
}

Export-ModuleMember -Function Get-DmsCmsLogEntry, Get-DmsCmsLogFirstUtc, Get-DmsSignalCount, Get-DmsResponseClassification, Get-DmsAuthenticationSyncWaitCount -Variable SignalPatterns
