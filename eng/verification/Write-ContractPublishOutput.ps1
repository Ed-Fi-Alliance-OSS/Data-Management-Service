# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Validates the one decision Invoke-ContractPublishCheck.ps1 wrote and turns it into job outputs.

.DESCRIPTION
    A workflow step that reads `$result.ShouldPush.ToString()` off whatever a script wrote to the
    pipeline decides by accident: two objects render as System.Object[], a string has no ShouldPush,
    and a decision from the wrong mode would pass a push intent off as a publication. This helper is
    the one place that conversion happens. It collects the complete success stream first, and only
    when there is exactly one object typed EdFi.ContractPublishDecision, in the expected mode, with
    boolean ShouldPush, AttachEvidence and PublishedBytesIdentical and a known single-line Reason,
    does it write anything: should-push, attach-evidence, published-bytes-identical and reason to
    GITHUB_OUTPUT when that variable is set. Anything else throws with the output file untouched.

.EXAMPLE
    ./eng/verification/Invoke-ContractPublishCheck.ps1 @arguments |
        ./eng/verification/Write-ContractPublishOutput.ps1 -ExpectedMode decide

.OUTPUTS
    The validated decision object, unchanged.
#>
[CmdletBinding()]
[OutputType([pscustomobject])]
param(
    # The decision, from the pipeline or as a direct argument. Whatever arrives is collected and
    # counted before anything is written.
    [Parameter(ValueFromPipeline)]
    [AllowNull()]
    $Decision,

    # The mode the calling step ran the check in. A decide-mode object in a confirm step, or the
    # reverse, is refused.
    [Parameter(Mandatory)]
    [ValidateSet("decide", "confirm")]
    [string]
    $ExpectedMode
)

begin {
    $ErrorActionPreference = "Stop"

    $collected = [System.Collections.Generic.List[object]]::new()
    $knownReasons = @("absent", "unchanged", "confirmed")
}

process {
    if ($null -eq $Decision) {
        $collected.Add($null)

        return
    }

    foreach ($item in @($Decision)) {
        $collected.Add($item)
    }
}

end {
    if ($collected.Count -ne 1) {
        throw "Expected exactly one publish decision object and received $($collected.Count). Nothing is written to the job outputs; the step that produced this is broken."
    }

    $decision = $collected[0]

    if ($null -eq $decision) {
        throw "Expected a publish decision object and received null. Nothing is written to the job outputs."
    }

    $typeName = [string] $decision.PSObject.TypeNames[0]

    if (-not [string]::Equals($typeName, "EdFi.ContractPublishDecision", [System.StringComparison]::Ordinal)) {
        throw "Expected an EdFi.ContractPublishDecision and received '$typeName'. Nothing is written to the job outputs."
    }

    $mode = [string] $decision.Mode

    if (-not [string]::Equals($mode, $ExpectedMode, [System.StringComparison]::Ordinal)) {
        throw "Expected a decision from $ExpectedMode mode and received one from '$mode'. A decision to push is not a publication and a publication check is not a push decision; nothing is written to the job outputs."
    }

    foreach ($member in @("ShouldPush", "AttachEvidence", "PublishedBytesIdentical")) {
        $value = $decision.$member

        if ($value -isnot [bool]) {
            throw "The decision's $member is '$value' rather than a boolean. Nothing is written to the job outputs."
        }
    }

    $reason = [string] $decision.Reason

    if (-not ($knownReasons | Where-Object { [string]::Equals($_, $reason, [System.StringComparison]::Ordinal) })) {
        throw "The decision's Reason is '$reason', which is not one of: $($knownReasons -join ', '). Nothing is written to the job outputs."
    }

    $lines = @(
        "should-push=$($decision.ShouldPush.ToString().ToLowerInvariant())",
        "attach-evidence=$($decision.AttachEvidence.ToString().ToLowerInvariant())",
        "published-bytes-identical=$($decision.PublishedBytesIdentical.ToString().ToLowerInvariant())",
        "reason=$reason"
    )

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        $lines | Out-File -FilePath $env:GITHUB_OUTPUT -Append
    }

    Write-Information "$($decision.PackageId) $($decision.PackageVersion) [$mode]: $($lines -join '; ')" -InformationAction Continue

    return $decision
}
