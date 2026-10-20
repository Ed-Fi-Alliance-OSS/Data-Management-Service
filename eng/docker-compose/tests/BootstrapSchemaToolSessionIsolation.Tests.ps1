# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe 'Schema-tool test session isolation' {
    It 'restores caller dotnet function state <HasCallerFunction> after <Probe>' -ForEach @(
        foreach ($hasCallerFunction in @($false, $true)) {
            @{
                HasCallerFunction = $hasCallerFunction
                Suite = 'BootstrapStandardSchemaSelection.Tests.ps1'
                Probe = 'It_DMS1283_publishes_once_and_reuses_the_exact_published_path'
            }
            @{
                HasCallerFunction = $hasCallerFunction
                Suite = 'BootstrapStandardSchemaSelection.Tests.ps1'
                Probe = 'It_DMS1283_prepare_invalid_environment_fails_closed_despite_valid_cache'
            }
            @{
                HasCallerFunction = $hasCallerFunction
                Suite = 'BootstrapSchemaDeploymentSafety.Tests.ps1'
                Probe = 'It_DMS1283_provision_invalid_environment_tool_fails_closed'
            }
        }
    ) {
        # A fresh process lets us inspect the session AFTER the fixture teardown, without
        # contaminating the parent lane if a global dotnet stub survives Invoke-Pester.
        $childScript = Join-Path ([System.IO.Path]::GetTempPath()) "dms-schema-session-$([guid]::NewGuid().ToString('N')).ps1"
        try {
            @'
param([string]$SuitePath, [string]$Probe, [int]$HasCallerFunction)
Import-Module Pester -RequiredVersion 5.7.1
if ($HasCallerFunction) { function global:dotnet { 'caller-owned-dotnet' } }
$before = Get-Item Function:\dotnet -ErrorAction SilentlyContinue
$originalScriptBlock = if ($null -ne $before) { $before.ScriptBlock } else { $null }
$configuration = New-PesterConfiguration
$configuration.Run.Path = $SuitePath
$configuration.Run.PassThru = $true
$configuration.Filter.FullName = "*$Probe"
$configuration.Output.Verbosity = 'None'
$result = Invoke-Pester -Configuration $configuration
$after = Get-Item Function:\dotnet -ErrorAction SilentlyContinue
[pscustomobject]@{
    Passed = $result.PassedCount
    Failed = $result.FailedCount + $result.FailedBlocksCount + $result.FailedContainersCount
    HasFunction = $null -ne $after
    Restored = if ($HasCallerFunction) {
        $null -ne $after -and [object]::ReferenceEquals($originalScriptBlock, $after.ScriptBlock)
    } else { $null -eq $after }
} | ConvertTo-Json -Compress
'@ | Set-Content -LiteralPath $childScript -Encoding utf8

            $launcher = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
            $state = (& $launcher -NoProfile -File $childScript (Join-Path $PSScriptRoot $Suite) $Probe ([int]$HasCallerFunction) |
                Select-Object -Last 1) | ConvertFrom-Json

            $state.Passed | Should -Be 1 -Because 'the fixture must execute before its cleanup can be checked'
            $state.Failed | Should -Be 0
            $state.HasFunction | Should -Be $HasCallerFunction
            $state.Restored | Should -BeTrue -Because 'fixture dotnet stubs must not replace or leak into the caller session'
        }
        finally {
            Remove-Item -LiteralPath $childScript -Force -ErrorAction SilentlyContinue
        }
    }
}
