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
            $ArgumentList -contains (Join-Path $TestDrive '.env') -and $InputText -eq '' -and $TimeoutSeconds -eq 600
        }
    }
    It 'preserves a failing native snippet exit code in its result and progress report' {
        $script:runbookProgress = @{}
        $script:runbookCase = 'cdc-stack-teardown'
        $savedEvidenceDirectory = $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY
        try {
            $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY = $TestDrive
            $result = Invoke-CdcRunbookScript -Id 'cdc-stack-teardown' -FixtureRoot $TestDrive `
                -Code "pwsh -NoProfile -NonInteractive -Command 'exit 7'"
            $result.FailureKind | Should -Be 'None'
            $result.ExitCode | Should -Be 7
            $progress = Get-Content (Join-Path $TestDrive 'runbook-progress.json') -Raw | ConvertFrom-Json
            $progress.'cdc-stack-teardown'[0].Phase | Should -Be 'Teardown'
            $progress.'cdc-stack-teardown'[0].ExitCode | Should -Be 7
        } finally {
            $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY = $savedEvidenceDirectory
            Remove-Variable runbookProgress, runbookOperation, runbookOperationCase, runbookCase, runbookTimer -Scope Script -ErrorAction SilentlyContinue
        }
    }
    It 'allows preparation and the internal readiness wait for <Id>' -ForEach @(
        @{ Id = 'cdc-pg-e2e-setup' },
        @{ Id = 'cdc-sqlserver-e2e-setup' }
    ) {
        # Simulate 300 seconds of preparation plus the supported 600-second wait.
        # Exercise the real wrapper's native-process boundary without a long wall-clock test.
        Mock Invoke-NativeCommandWithInput {
            param($TimeoutSeconds)
            [pscustomobject]@{
                ExitCode = 0
                FailureKind = $(if ($TimeoutSeconds -le 900) { 'Timeout' } else { 'None' })
                StandardOutput = ''; StandardError = ''
            }
        }
        $result = Invoke-CdcRunbookLiveWrapper -Id $Id -FixtureRoot $TestDrive
        $result.FailureKind | Should -Be 'None'
        Should -Invoke Invoke-NativeCommandWithInput -Times 1 -Exactly -ParameterFilter { $TimeoutSeconds -eq 1800 }
    }
    It 'honors an explicit E2E setup deadline and preserves its timeout result' {
        Mock Invoke-NativeCommandWithInput {
            [pscustomobject]@{ ExitCode = 0; FailureKind = 'Timeout'; StandardOutput = ''; StandardError = '' }
        }
        $result = Invoke-CdcRunbookLiveWrapper -Id 'cdc-sqlserver-e2e-setup' -FixtureRoot $TestDrive -TimeoutSeconds 1
        $result.FailureKind | Should -Be 'Timeout'
        Should -Invoke Invoke-NativeCommandWithInput -Times 1 -Exactly -ParameterFilter { $TimeoutSeconds -eq 1 }
    }
    It 'does not start a process for a missing marked wrapper' {
        Mock Invoke-NativeCommandWithInput { throw 'must not execute' }
        { Invoke-CdcRunbookLiveWrapper -Id 'cdc-absent' -FixtureRoot $TestDrive } | Should -Throw '*Missing*'
        Should -Invoke Invoke-NativeCommandWithInput -Times 0
    }
}

Describe 'CDC live lifecycle snippet boundary' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'cdc-runbook-snippets.ps1')
        . (Join-Path $PSScriptRoot 'cdc-runbook-lifecycle.ps1')
        Import-Module (Join-Path $PSScriptRoot '../env-utility.psm1') -DisableNameChecking
    }
    It 'executes the retained inventory snippet before requesting managed stop' {
        $script:repo = $TestDrive
        $state = Join-Path $TestDrive 'state'
        $settings = Join-Path $TestDrive 'settings.json'
        $inventory = Join-Path $TestDrive 'inventory.json'
        $null = New-Item -ItemType Directory -Force -Path (Join-Path $state 'bindings'), (Join-Path $TestDrive 'eng/docker-compose/.bootstrap')
        '{}' | Set-Content $settings
        '{}' | Set-Content $inventory
        '{}' | Set-Content (Join-Path $state 'bindings/binding.json')
        Mock Invoke-CdcRunbookLiveWrapper { throw 'Reached managed stop' }

        { Invoke-CdcRunbookLifecycle -Entry @{ SettingsPath = $settings } -InventoryPath $inventory `
            -StatePath $state -Project 'dms-local' -FixtureRoot $TestDrive } | Should -Throw 'Reached managed stop'

        Should -Invoke Invoke-CdcRunbookLiveWrapper -Times 1 -Exactly -ParameterFilter { $Id -eq 'cdc-managed-stop' -and $FixtureRoot -eq $TestDrive }
        (Get-Content (Join-Path $TestDrive 'cdc-state-inventory.stdout') -Raw).TrimStart() | Should -Match '^FullName\b'
    }
}

Describe 'CDC provider image environment retention' -ForEach @(
    @{ Engine = 'postgresql'; ImageVariable = 'POSTGRES_IMAGE'; DefaultImage = 'postgres:16.8-alpine@sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0' },
    @{ Engine = 'mssql'; ImageVariable = 'MSSQL_IMAGE'; DefaultImage = 'mcr.microsoft.com/mssql/server:2025-latest' }
) {
    BeforeAll {
        $script:composeSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        Import-Module (Join-Path $script:composeSource 'env-utility.psm1') -DisableNameChecking
        Import-Module (Join-Path $script:composeSource 'database-safety.psm1') -DisableNameChecking
        $imageLine = [regex]::Match((Get-Content (Join-Path $script:composeSource "$Engine.yml") -Raw), '(?m)^\s*image:\s*(\S+)\s*$')
        $script:imageExpression = $imageLine.Groups[1].Value
        # Synthetic immutable reference deliberately differs from both shipped defaults.
        $script:overrideImage = 'example.invalid/provider@sha256:' + ('a' * 64)
        $script:savedImage = [Environment]::GetEnvironmentVariable($ImageVariable)
        Remove-Item "Env:$ImageVariable" -ErrorAction SilentlyContinue
    }
    AfterAll {
        if ($null -eq $script:savedImage) { Remove-Item "Env:$ImageVariable" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($ImageVariable, $script:savedImage) }
    }
    It 'preserves the <Engine> default without an override' {
        Get-ComposeResolvedEnvValue -EnvironmentValues @{ CheckedImage = $script:imageExpression } -Name CheckedImage |
            Should -Be $DefaultImage
    }
    It 'resolves an explicit immutable <Engine> override' {
        $values = @{ CheckedImage = $script:imageExpression }
        $values[$ImageVariable] = $script:overrideImage
        Get-ComposeResolvedEnvValue -EnvironmentValues $values -Name CheckedImage | Should -Be $script:overrideImage
    }
    It 'retains the <Engine> image through <Surface> schema and engine overlays and re-entry' -ForEach @(
        @{ Surface = 'local'; Base = '.env.example'; Prefix = '.env.bootstrap' },
        @{ Surface = 'published'; Base = '.env.example'; Prefix = '.env.bootstrap' },
        @{ Surface = 'E2E'; Base = '.env.e2e'; Prefix = '.env' }
    ) {
        $root = Join-Path $TestDrive "$Engine-$Surface"
        $null = New-Item -ItemType Directory $root -Force
        foreach ($name in @($Base, "$Prefix.ds52", '.env.mssql')) {
            Copy-Item (Join-Path $script:composeSource $name) $root
        }
        $basePath = Join-Path $root $Base
        Add-Content $basePath "`n$ImageVariable=$script:overrideImage"
        $schema = Resolve-DataStandardEnvironmentFile -DataStandardVersion '5.2' -BaseEnvironmentFile $basePath -DockerComposeRoot $root -OverlayPrefix $Prefix
        $effective = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $Engine -BaseEnvironmentFile $schema -DockerComposeRoot $root
        $retained = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $Engine -BaseEnvironmentFile $effective -DockerComposeRoot $root
        foreach ($path in @($schema, $effective, $retained)) {
            $values = ReadValuesFromEnvFile $path
            $values[$ImageVariable] | Should -Be $script:overrideImage
            $values.CheckedImage = $script:imageExpression
            Get-ComposeResolvedEnvValue -EnvironmentValues $values -Name CheckedImage | Should -Be $script:overrideImage
        }
    }
}
