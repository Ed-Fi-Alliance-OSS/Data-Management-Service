# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
Runs PSScriptAnalyzer against staged PowerShell files.

.DESCRIPTION
Filters staged paths down to existing PowerShell script, module, and data files, then runs
PSScriptAnalyzer. The script exits with a non-zero status when analyzer findings are found
so it can be used from a Git pre-commit hook.
#>

[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repositoryRoot

function Expand-StagedPathArgument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $Value -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function ConvertTo-RelativeDisplayPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    $resolvedPath = Resolve-Path -LiteralPath $Value
    $relativePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $resolvedPath.Path)
    return $relativePath.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
}

if ($null -eq (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
    throw "PSScriptAnalyzer is not installed. Run ./setup-dev-environment.ps1 to restore required PowerShell resources."
}

Import-Module PSScriptAnalyzer -ErrorAction Stop

$powerShellFileExtensions = @(".ps1", ".psm1", ".psd1")
$analysisPaths = @(
    @(
        foreach ($pathArgument in $Path) {
            foreach ($candidatePath in Expand-StagedPathArgument -Value $pathArgument) {
                if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
                    continue
                }

                if ([System.IO.Path]::GetExtension($candidatePath) -notin $powerShellFileExtensions) {
                    continue
                }

                (Resolve-Path -LiteralPath $candidatePath).Path
            }
        }
    ) | Sort-Object -Unique
)

if ($analysisPaths.Count -eq 0) {
    Write-Information "No staged PowerShell files to analyze." -InformationAction Continue
    exit 0
}

$diagnostics = @(
    foreach ($analysisPath in $analysisPaths) {
        Invoke-ScriptAnalyzer -Path $analysisPath -ErrorAction Stop
    }
)

if ($diagnostics.Count -eq 0) {
    Write-Information "PSScriptAnalyzer completed without findings." -InformationAction Continue
    exit 0
}

foreach ($diagnostic in ($diagnostics | Sort-Object ScriptPath, Line, Column, RuleName)) {
    $displayPath = ConvertTo-RelativeDisplayPath -Value $diagnostic.ScriptPath
    "{0}:{1}:{2}: {3} {4}: {5}" -f $displayPath,
    $diagnostic.Line,
    $diagnostic.Column,
    $diagnostic.Severity,
    $diagnostic.RuleName,
    $diagnostic.Message
}

[Console]::Error.WriteLine("$($diagnostics.Count) PSScriptAnalyzer finding(s) found.")
exit 1
