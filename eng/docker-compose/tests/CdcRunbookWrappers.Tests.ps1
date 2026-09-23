# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe 'CDC runbook wrapper drift guards' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'cdc-runbook-snippets.ps1')
        $script:markdown = Get-Content (Join-Path $PSScriptRoot '../../../reference/cdc-documentation/operations-runbook.md') -Raw
    }
    It 'rejects <Fault> in a copied marked invocation' -ForEach @(
        @{ Fault = 'missing'; Old = 'cdc-pg-bootstrap-local'; New = 'absent' },
        @{ Fault = 'argument'; Old = '-DatabaseEngine postgresql'; New = '-UnknownArgument postgresql' },
        @{ Fault = 'duplicate argument'; Old = '-DatabaseEngine postgresql'; New = '-DatabaseEngine postgresql -DatabaseEngine postgresql' },
        @{ Fault = 'environment'; Old = "(Resolve-Path './eng/docker-compose/.env').Path"; New = "(Resolve-Path './eng/docker-compose/.env.e2e').Path" },
        @{ Fault = 'provider'; Old = '-DatabaseEngine postgresql'; New = '-DatabaseEngine sqlserver' },
        @{ Fault = 'expression'; Old = "'./.local/cdc/postgresql.json'"; New = '(Get-Content private.json)' }
    ) {
        { Get-CdcRunbookInvocation 'cdc-pg-bootstrap-local' $TestDrive $script:markdown.Replace($Old, $New) } | Should -Throw
    }
    It 'rejects a duplicate selected marker' {
        { Get-CdcRunbookInvocation 'cdc-pg-bootstrap-local' $TestDrive ($script:markdown + "`n<!-- cdc-snippet: cdc-pg-bootstrap-local -->") } | Should -Throw '*duplicate*'
    }
}

Describe 'CDC live snippet process boundary' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'cdc-runbook-snippets.ps1')
        Import-Module (Join-Path $PSScriptRoot '../env-utility.psm1') -DisableNameChecking
    }
    It 'runs the bound shipped wrapper and retains a nonzero process result privately' {
        Mock Invoke-NativeCommandWithInput {
            [pscustomobject]@{ ExitCode = 1; FailureKind = 'None'; StandardOutput = 'private-output-sentinel'; StandardError = 'private-error-sentinel' }
        }
        $result = Invoke-CdcRunbookLiveWrapper -Id 'cdc-pg-bootstrap-local' -FixtureRoot $TestDrive
        $result.ExitCode | Should -Be 1
        $result.SnippetId | Should -Be 'cdc-pg-bootstrap-local'
        ($result | ConvertTo-Json) | Should -Not -Match 'sentinel'
        Get-Content "$($result.LogPrefix).stdout" | Should -Be 'private-output-sentinel'
        Get-Content "$($result.LogPrefix).stderr" | Should -Be 'private-error-sentinel'
        Should -Invoke Invoke-NativeCommandWithInput -Times 1 -Exactly -ParameterFilter {
            $FilePath -eq 'pwsh' -and $ArgumentList -contains '-EnableKafkaCdc' -and
            $ArgumentList -contains (Join-Path $TestDrive '.local/cdc/postgresql.json') -and
            $ArgumentList -contains (Join-Path $TestDrive '.local/cdc/state-pg') -and
            $ArgumentList -contains (Join-Path $TestDrive '.env') -and $InputText -eq ''
        }
    }
    It 'does not start a process for a missing marked wrapper' {
        Mock Invoke-NativeCommandWithInput { throw 'must not execute' }
        { Invoke-CdcRunbookLiveWrapper -Id 'cdc-absent' -FixtureRoot $TestDrive } | Should -Throw '*Missing*'
        Should -Invoke Invoke-NativeCommandWithInput -Times 0
    }
}
