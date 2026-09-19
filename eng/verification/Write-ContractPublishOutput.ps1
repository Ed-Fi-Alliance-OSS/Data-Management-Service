# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Validates the one decision Invoke-ContractPublishCheck.ps1 wrote and turns it into job outputs.

.DESCRIPTION
    A workflow step that reads `$result.ShouldPush.ToString()` off whatever a script wrote to the
    pipeline decides by accident: two objects render as System.Object[] and a string has no
    ShouldPush. This helper is the one place that conversion happens. It collects the complete
    success stream first, and only when there is exactly one object typed
    EdFi.ContractPublishDecision, with a boolean ShouldPush and a known single-line Reason, does it
    write anything: should-push and reason to GITHUB_OUTPUT when that variable is set. Anything
    else throws with the output file untouched.

.EXAMPLE
    ./eng/verification/Invoke-ContractPublishCheck.ps1 @arguments |
        ./eng/verification/Write-ContractPublishOutput.ps1

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
    $Decision
)

begin {
    $ErrorActionPreference = "Stop"

    $collected = [System.Collections.Generic.List[object]]::new()
    $knownReasons = @("absent", "unchanged")
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

    $shouldPush = $decision.ShouldPush

    if ($shouldPush -isnot [bool]) {
        throw "The decision's ShouldPush is '$shouldPush' rather than a boolean. Nothing is written to the job outputs."
    }

    $reason = [string] $decision.Reason

    if (-not ($knownReasons | Where-Object { [string]::Equals($_, $reason, [System.StringComparison]::Ordinal) })) {
        throw "The decision's Reason is '$reason', which is not one of: $($knownReasons -join ', '). Nothing is written to the job outputs."
    }

    $lines = @(
        "should-push=$($shouldPush.ToString().ToLowerInvariant())",
        "reason=$reason"
    )

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        $lines | Out-File -FilePath $env:GITHUB_OUTPUT -Append
    }

    Write-Information "$($decision.PackageId) $($decision.PackageVersion): $($lines -join '; ')" -InformationAction Continue

    return $decision
}
