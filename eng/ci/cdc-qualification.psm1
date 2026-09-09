# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

function Get-CdcQualificationReport {
    <# .SYNOPSIS
    Classifies complete TRX results without treating skipped or unavailable cases as evidence.
    #>
    param([string] $Path, [int] $ExitCode)

    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ Status = 'ReportUnavailable'; Total = 0; Passed = 0; Failed = 0; Skipped = 0 }
    }
    [xml] $trx = Get-Content -LiteralPath $Path -Raw
    $results = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $passed = @($results | Where-Object outcome -eq 'Passed').Count
    $failed = @($results | Where-Object outcome -eq 'Failed').Count
    $skipped = $results.Count - $passed - $failed
    $environmentFailures = @($results | Where-Object {
        $explicitPrerequisite = $_.InnerText -match 'CDC_TEMPLATE_PINNED_IMAGE_DOCKER_PREREQUISITE_FAILURE|CDC packaged-history qualification requires|provider cleanup qualification requires its admin endpoint'
        $setupConnectionFailure = $_.InnerText -match '\bRunSetUp\b' -and
            $_.InnerText -match '(?i)connection.*(?:refused|error)|server was not found'
        $_.outcome -ne 'Passed' -and ($explicitPrerequisite -or $setupConnectionFailure)
    }).Count
    $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    $complete = $null -ne $counters -and [int] $counters.total -eq $results.Count -and
        [int] $counters.executed -eq $results.Count -and [int] $counters.passed -eq $passed -and
        [int] $counters.GetAttribute('failed') -eq $failed
    if ($null -ne $counters) {
        foreach ($attribute in @('error', 'timeout', 'aborted', 'passedButRunAborted', 'inconclusive', 'notRunnable', 'notExecuted', 'disconnected', 'pending', 'inProgress')) {
            if ([int] $counters.GetAttribute($attribute) -gt 0) { $complete = $false }
        }
    }
    $status = if ($failed -gt $environmentFailures) { 'Failed' }
    elseif ($environmentFailures -gt 0) { 'EnvironmentUnavailable' }
    elseif ($ExitCode -ne 0 -or $failed -gt 0) { 'Failed' }
    elseif ($skipped -gt 0) { 'SkippedQualification' }
    elseif (-not $complete -or $results.Count -eq 0) { 'ReportIncomplete' }
    else { 'Passed' }
    return [ordered]@{
        Status = $status; Total = $results.Count; Passed = $passed; Failed = $failed
        Skipped = $skipped; EnvironmentFailures = $environmentFailures
        Diagnostics = @($results | Where-Object outcome -ne 'Passed' | ForEach-Object {
            [ordered]@{
                TestId = $_.GetAttribute('testId')
                Codes = @([regex]::Matches($_.InnerText, '\bCDC_[A-Z0-9_]+\b') | ForEach-Object Value | Select-Object -Unique)
            }
        })
    }
}

function ConvertTo-CdcSafeEvidence {
    param([AllowNull()] $Value, [string] $Key = '')

    # Structured fixtures already omit raw logs/configuration and document bodies. This second
    # boundary drops sensitive fields and arbitrary prose before publishing their attachments.
    if ($Key -match '(?i)password|secret|credential|connectionstring|payload|documentbody|hostname|database(name)?$|sourceidentifier') {
        return '[redacted]'
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($name in $Value.Keys) { $copy[$name] = ConvertTo-CdcSafeEvidence $Value[$name] $name }
        return $copy
    }
    if ($Value -is [array]) {
        $items = @($Value | ForEach-Object { ConvertTo-CdcSafeEvidence $_ })
        return ,$items
    }
    if ($Value -is [string]) {
        if ($Value.Length -gt 240 -or $Value -match '(?i)sentinel|secret|EdFi_Dms1|password|://|localhost|127\.0\.0\.1|edfi_datastore|admission_[a-f0-9]{32}|dms-cdc-template-[a-f0-9]{32}|cdc-(?:policy|history|admission)-[a-f0-9]|(?:Host|Server|User Id)=|[{}]') {
            return '[redacted]'
        }
    }
    return $Value
}

function Export-CdcQualificationEvidence {
    <# .SYNOPSIS
    Publishes result metadata and structured attachments without raw assertion output.
    #>
    param([string] $RawDirectory, [string] $Destination)

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $attachmentPattern = '^(?:admission-evidence-|cdc-controller-|managed-lifecycle-|native-recovery-|cdc-history-|record-size-|\d+-)[a-zA-Z0-9_.-]+\.json$'
    foreach ($file in Get-ChildItem -LiteralPath $RawDirectory -Filter '*.trx' -Recurse) {
        [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw
        # Assertion diffs and stdout can contain a whole record or an unlabelled password.
        # Publish outcomes/timings and structured attachments; never publish raw test output.
        foreach ($node in @($trx.SelectNodes('//*[local-name()="Output" or local-name()="CollectorDataEntries" or local-name()="RunInfos"]'))) {
            $node.ParentNode.RemoveChild($node) | Out-Null
        }
        foreach ($node in @($trx.SelectNodes('//*[local-name()="ResultFile"]'))) {
            $name = [IO.Path]::GetFileName($node.path.Replace('\', '/'))
            if ($name -match $attachmentPattern) { $node.path = $name }
            else { $node.ParentNode.RemoveChild($node) | Out-Null }
        }
        foreach ($node in $trx.SelectNodes('//*[local-name()="UnitTestResult"]')) {
            $node.SetAttribute('relativeResultsDirectory', '.')
        }
        foreach ($attribute in $trx.SelectNodes('//@*')) {
            # TestCase arguments can themselves contain a sentinel or a credential-bearing URL.
            # Namespace declarations are structural XML, not diagnostic values.
            # History attachment basenames contain only a generated NUnit ID and GUID.
            # Their directory was removed above; do not mistake this safe filename for
            # a private history source identifier and break its published JSON link.
            $generatedHistoryAttachment = $attribute.Name -eq 'path' -and
                $attribute.OwnerElement.LocalName -eq 'ResultFile' -and
                $attribute.Value -cmatch '^cdc-history-\d+(?:-\d+)*-[a-f0-9]{32}\.json$'
            if ($generatedHistoryAttachment) { continue }
            if ($attribute.Name -ne 'xmlns' -and $attribute.Prefix -ne 'xmlns') {
                $attribute.Value = ConvertTo-CdcSafeEvidence $attribute.Value
            }
        }
        $trx.Save((Join-Path $Destination $file.Name))
    }
    foreach ($file in Get-ChildItem -LiteralPath $RawDirectory -Filter '*.json' -Recurse) {
        # Only test attachments, never runtime settings, workflow journals or source documents.
        if ($file.Name -notmatch $attachmentPattern) { continue }
        $value = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate -Depth 100
        $safeValue = ConvertTo-CdcSafeEvidence $value
        ConvertTo-Json -InputObject $safeValue -Depth 100 |
            Set-Content -LiteralPath (Join-Path $Destination $file.Name)
    }
}

Export-ModuleMember -Function Get-CdcQualificationReport, Export-CdcQualificationEvidence
