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
