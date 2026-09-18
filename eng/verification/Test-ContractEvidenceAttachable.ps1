# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Decides whether one contract's SBOM and provenance may be attached to a release.

.DESCRIPTION
    Takes the results GitHub hands a dependent job and answers one question: did this run publish
    these exact bytes, with the evidence to attach? Every input is the raw string a `needs` context
    provides, including the empty string a skipped or failed job leaves in its outputs.

      - the publish job did not succeed (it refused a changed contract, failed, was skipped or was
        cancelled) -> no
      - the publish job succeeded but did not report attach-evidence as true or false -> fail, because
        a successful publisher that produced no decision is a broken producer, not a "no"
      - attach-evidence false -> no: the feed does not serve this run's bytes
      - the SBOM or provenance job did not succeed -> no
      - otherwise -> yes

    Each contract is decided on its own; the sibling contract's outcome is not an input.

.OUTPUTS
    One object with Attach (bool) and Reason. When GITHUB_OUTPUT is set, `attach=true|false` is also
    appended to it for the calling job's later steps.
#>
[CmdletBinding()]
[OutputType([pscustomobject])]
param(
    [AllowEmptyString()]
    [string]
    $PublishResult = "",

    [AllowEmptyString()]
    [string]
    $AttachEvidence = "",

    [AllowEmptyString()]
    [string]
    $SbomResult = "",

    [AllowEmptyString()]
    [string]
    $ProvenanceResult = ""
)

$ErrorActionPreference = "Stop"

function Format-Result {
    [CmdletBinding()]
    [OutputType([string])]
    param([AllowEmptyString()][string] $Value)

    if ([string]::IsNullOrEmpty($Value)) {
        return "(none)"
    }

    return $Value
}

# Ordinal throughout: these are exact tokens GitHub emits, and -eq or -ceq would compare by culture.
function Test-Token {
    [CmdletBinding()]
    [OutputType([bool])]
    param([AllowEmptyString()][string] $Value, [Parameter(Mandatory)][string] $Expected)

    return [string]::Equals($Value, $Expected, [System.StringComparison]::Ordinal)
}

$attach = $false

if (-not (Test-Token -Value $PublishResult -Expected "success")) {
    $reason = "The publish job's result was $(Format-Result $PublishResult), so this run published nothing to attach evidence for."
}
elseif (-not (Test-Token -Value $AttachEvidence -Expected "true") -and -not (Test-Token -Value $AttachEvidence -Expected "false")) {
    throw "The publish job succeeded but reported attach-evidence as $(Format-Result $AttachEvidence) rather than true or false. A publisher that produced no decision cannot be trusted either way."
}
elseif (Test-Token -Value $AttachEvidence -Expected "false") {
    $reason = "The publish job did not confirm that the feed serves this run's bytes, so this run's evidence describes nothing on the feed."
}
elseif (-not (Test-Token -Value $SbomResult -Expected "success")) {
    $reason = "The SBOM job's result was $(Format-Result $SbomResult), so there is no SBOM to attach."
}
elseif (-not (Test-Token -Value $ProvenanceResult -Expected "success")) {
    $reason = "The provenance job's result was $(Format-Result $ProvenanceResult), so there is no provenance to attach."
}
else {
    $attach = $true
    $reason = "The publish job confirmed the feed serves this run's bytes and both evidence jobs succeeded."
}

Write-Information "attach=$($attach.ToString().ToLowerInvariant()): $reason" -InformationAction Continue

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "attach=$($attach.ToString().ToLowerInvariant())" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
}

return [pscustomobject]@{
    PSTypeName = "EdFi.ContractEvidenceDecision"
    Attach     = $attach
    Reason     = $reason
}
