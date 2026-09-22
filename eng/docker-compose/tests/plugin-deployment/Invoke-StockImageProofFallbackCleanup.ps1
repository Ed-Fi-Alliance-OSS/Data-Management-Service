# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
    .SYNOPSIS
        Tears down a stock-image proof deployment, but only one this repository's proof created.
    .DESCRIPTION
        The backstop for a proof process that died before its own cleanup ran. It exists because a
        scheduled job needs an always() step, and an always() step that simply brings the compose
        project down would remove whatever happens to be there - including a stack the proof itself
        refused to touch at its preflight, which is the opposite of what the proof's ownership model
        promises.

        So this removes nothing on its own authority. Invoke-StockImagePluginProof.ps1 writes a
        receipt after its preflight has established the project is unoccupied and immediately before
        the first operation that can create it, naming the environment file that would tear that
        deployment down. Its own cleanup removes the receipt when it succeeds.

        No receipt therefore means one of three things, and the answer is the same for all three:
        the proof never ran, it refused before creating anything, or it cleaned up after itself.
        This script runs no Docker command at all in that case.

        A receipt is honoured only when it describes this repository's proof: the fixed compose
        project, this compose directory, and an environment file inside the workspace it records.
        Anything else is refused rather than acted on, because a receipt that does not describe this
        proof is not permission to remove anything.

        Evidence is never touched. It lives outside the workspace for that reason.
    .PARAMETER ReceiptPath
        The receipt to act on. Defaults to the path the proof writes.
    .PARAMETER PassThru
        Emit what was done, for a caller that wants to assert on it.
#>
[CmdletBinding()]
param(
    [string]
    $ReceiptPath,

    [switch]
    $PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$composeRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $composeRoot)
$composeProject = 'dms-published'

Import-Module (Join-Path $PSScriptRoot 'stock-image-proof.psm1') -Force

if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $ReceiptPath = Join-Path $repositoryRoot '.ai-work/stock-image-proof-cleanup.json'
}

$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)

function Write-Line([string]$text) {
    Write-Information $text -InformationAction Continue
}

function Get-Outcome {
    param(
        [Parameter(Mandatory)] [string] $Action,
        [Parameter(Mandatory)] [string] $Reason,
        [string] $EnvironmentFile = '',
        [bool] $Emit = $false
    )

    Write-Line "$Action`: $Reason"

    if (-not $Emit) {
        return $null
    }

    return [pscustomobject]@{
        Action          = $Action
        Reason          = $Reason
        EnvironmentFile = $EnvironmentFile
    }
}

if (-not (Test-Path -LiteralPath $ReceiptPath -PathType Leaf)) {
    # The whole point. Nothing is inspected, nothing is enumerated, and no daemon is contacted:
    # a run that created nothing must not be able to remove anything.
    return Get-Outcome -Action 'skipped' -Emit ([bool]$PassThru) `
        -Reason "no cleanup receipt at $ReceiptPath, so this proof has nothing outstanding"
}

try {
    $receipt = Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json
}
catch {
    throw "The cleanup receipt at $ReceiptPath is not valid JSON, so what it permits cannot be read: $($_.Exception.Message)"
}

$project = Get-JsonMember -Object $receipt -Name 'composeProject'
$recordedComposeRoot = Get-JsonMember -Object $receipt -Name 'composeRoot'
$environmentFile = Get-JsonMember -Object $receipt -Name 'environmentFile'
$workspaceRoot = Get-JsonMember -Object $receipt -Name 'workspaceRoot'

$blocker = @()

if ("$project" -cne $composeProject) {
    $blocker += "it names compose project '$project' rather than '$composeProject'"
}

if ([string]::IsNullOrWhiteSpace("$recordedComposeRoot") -or
    [IO.Path]::GetFullPath("$recordedComposeRoot").TrimEnd('\', '/') -ine $composeRoot.TrimEnd('\', '/')) {
    $blocker += "it was written for compose directory '$recordedComposeRoot' rather than this one"
}

if ([string]::IsNullOrWhiteSpace("$environmentFile") -or [string]::IsNullOrWhiteSpace("$workspaceRoot")) {
    $blocker += 'it does not name both an environment file and the workspace that produced it'
}
elseif (-not (Test-OwnedDeletionPath -Path ([IO.Path]::GetFullPath("$environmentFile")) -OwnedDirectory @([IO.Path]::GetFullPath("$workspaceRoot")))) {
    $blocker += "its environment file '$environmentFile' is outside the workspace '$workspaceRoot' it records"
}
elseif ($null -ne (Get-ReparsePointAncestor -Path ([IO.Path]::GetFullPath("$environmentFile")))) {
    $blocker += "its environment file '$environmentFile' is reached through a link or junction"
}

if ($blocker.Count -gt 0) {
    throw ("The cleanup receipt at $ReceiptPath does not describe this repository's stock-image proof, so it is not " +
        'permission to remove anything:' + [Environment]::NewLine +
        (($blocker | ForEach-Object { "  - $_" }) -join [Environment]::NewLine))
}

if (-not (Test-Path -LiteralPath $environmentFile -PathType Leaf)) {
    throw ("The cleanup receipt at $ReceiptPath names environment file '$environmentFile', which no longer exists. " +
        'Tearing the project down without it would compose different services than the run did, so this stops here.')
}

Write-Line "Tearing down $composeProject with the environment file the run recorded."

Push-Location $composeRoot
try {
    # The recorded environment file, not a default: the deployment that may still be up composed
    # its overlays from that file, and a different one brings a different set of services down.
    & pwsh -NoProfile -File (Join-Path $composeRoot 'bootstrap-published-dms.ps1') `
        -EnvironmentFile $environmentFile -d -v

    if ($LASTEXITCODE -ne 0) {
        throw "Tearing down $composeProject exited $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Remove-Item -LiteralPath $ReceiptPath -Force

return Get-Outcome -Action 'cleaned' -Emit ([bool]$PassThru) -EnvironmentFile $environmentFile `
    -Reason "tore down $composeProject and removed the receipt"
