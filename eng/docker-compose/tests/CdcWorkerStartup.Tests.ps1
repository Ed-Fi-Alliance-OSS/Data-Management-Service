# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Variables supply the dynamic scope of executable startup blocks extracted from the scripts under test.')]
param()

Describe 'CDC infrastructure startup' {
    BeforeAll {
        $script:root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        function Get-StartupBlock($Name, $Marker) {
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $script:root $Name), [ref]$null, [ref]$null)
            $node = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and ($n.Extent.Text.StartsWith('if ($CdcKafkaInfrastructure)') -or $n.Extent.Text.StartsWith('if ($CdcDatabaseInfrastructure)')) }, $true) |
                Where-Object { $_.Extent.Text.Contains($Marker) } | Select-Object -First 1
            if ($null -eq $node) { throw 'Missing executable CDC infrastructure phase.' }
            return [scriptblock]::Create($node.Extent.Text)
        }
        function docker { }
    }

    BeforeEach {
        $d = $false
        $EnableKafka = $false
        $EnableKafkaUI = $false
        $CdcKafkaInfrastructure = $false
        $script:calls = [Collections.Generic.List[string]]::new()
        Mock docker {
            $script:calls.Add((@($args | ForEach-Object { $_ }) -join ' '))
            $global:LASTEXITCODE = 0
        }
    }

    It '<Script> preserves SQL Server preparation without Kafka selection on <Engine>' -ForEach @(
        @{ Script = 'start-local-dms.ps1'; Engine = 'postgresql' },
        @{ Script = 'start-local-dms.ps1'; Engine = 'mssql' },
        @{ Script = 'start-published-dms.ps1'; Engine = 'postgresql' },
        @{ Script = 'start-published-dms.ps1'; Engine = 'mssql' }
    ) {
        $CdcDatabaseInfrastructure = $true
        $InfraOnly = $true
        $DatabaseEngine = $Engine
        $files = @()
        . (Get-StartupBlock $Script 'CDC database preparation requires')
        ($files -contains 'mssql-cdc.yml') | Should -Be ($Engine -eq 'mssql')
        $files | Should -Not -Contain 'kafka-cdc.yml'
        $files | Should -Not -Contain 'kafka.yml'
        $script:calls.Count | Should -Be 0
    }

    It '<Script> rejects <Flag> during initial CDC database preparation' -ForEach @(
        foreach ($script in @('start-local-dms.ps1', 'start-published-dms.ps1')) {
            foreach ($flag in @('EnableKafka', 'EnableKafkaUI', 'CdcKafkaInfrastructure', 'd', 'writers')) {
                @{ Script = $script; Flag = $flag }
            }
        }
    ) {
        $CdcDatabaseInfrastructure = $true
        $InfraOnly = $true
        $files = @()
        if ($Flag -eq 'writers') { $InfraOnly = $false } else { Set-Variable $Flag $true }
        { . (Get-StartupBlock $Script 'CDC database preparation requires') } | Should -Throw '*without Kafka startup or teardown flags*'
        $script:calls.Count | Should -Be 0
    }

    It '<Script> keeps Connect stopped with CDC and UI on <Engine>' -ForEach @(
        @{ Script = 'start-local-dms.ps1'; Engine = 'postgresql' },
        @{ Script = 'start-local-dms.ps1'; Engine = 'mssql' },
        @{ Script = 'start-published-dms.ps1'; Engine = 'postgresql' },
        @{ Script = 'start-published-dms.ps1'; Engine = 'mssql' }
    ) {
        $CdcKafkaInfrastructure = $true
        $EnableKafkaUI = $true
        $enableKafkaInfrastructure = $true
        $DatabaseEngine = $Engine
        $EnvironmentFile = 'selected.env'
        $files = @('-f', 'kafka-cdc.yml', '-f', 'kafka-ui.yml')
        $upArgs = @('-d')
        & (Get-StartupBlock $Script 'Starting CDC broker') | Out-Null
        $script:calls.Count | Should -Be 1
        $script:calls[0] | Should -Match '--env-file selected.env -p dms-(local|published) up -d --wait kafka$'
        $script:calls[0] | Should -Not -Match 'kafka-cdc-worker|kafka-postgresql-source|--profile'
    }

    It 'propagates broker startup failure' {
        Mock docker { $global:LASTEXITCODE = 1 }
        $CdcKafkaInfrastructure = $true
        $files = @('-f', 'kafka-cdc.yml')
        $EnvironmentFile = 'selected.env'
        $upArgs = @('-d')
        { & (Get-StartupBlock 'start-local-dms.ps1' 'Starting CDC broker') } | Should -Throw '*Failed to start CDC broker*'
    }

    It '<Script> rejects writer startup through the CDC infrastructure phase' -ForEach @(
        @{ Script = 'start-local-dms.ps1' }, @{ Script = 'start-published-dms.ps1' }
    ) {
        $CdcKafkaInfrastructure = $true
        $InfraOnly = $false
        $d = $false
        { & (Get-StartupBlock $Script 'CDC infrastructure startup requires') } | Should -Throw '*requires -InfraOnly*'
    }
}

Describe 'CDC Compose service selection' {
    BeforeAll {
        $script:root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        $script:originalImage = $env:CDC_CONNECT_IMAGE
        $env:CDC_CONNECT_IMAGE = 'example/qualified@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    }
    AfterAll { $env:CDC_CONNECT_IMAGE = $script:originalImage }

    It 'resolves <Engine> CDC with UI=<Ui> and requires explicit worker service selection' -ForEach @(
        @{ Engine = 'postgresql'; Ui = $false }, @{ Engine = 'postgresql'; Ui = $true },
        @{ Engine = 'mssql'; Ui = $false }, @{ Engine = 'mssql'; Ui = $true }
    ) {
        $arguments = @('compose', '-f', (Join-Path $script:root 'kafka-cdc.yml'))
        if ($Ui) { $arguments += @('-f', (Join-Path $script:root 'kafka-ui.yml')) }
        $arguments += @('config', '--format', 'json')
        $config = (& docker @arguments | ConvertFrom-Json)
        $LASTEXITCODE | Should -Be 0
        $config.services.kafka | Should -Not -BeNullOrEmpty
        $config.services.PSObject.Properties.Name | Should -Not -Contain 'kafka-cdc-worker'
        $config.services.PSObject.Properties.Name | Should -Not -Contain 'kafka-postgresql-source'
        if ($Ui) { $config.services.'kafka-ui' | Should -Not -BeNullOrEmpty }

        $all = (& docker compose -f (Join-Path $script:root 'kafka-cdc.yml') --profile cdc-managed-worker config --format json | ConvertFrom-Json)
        $LASTEXITCODE | Should -Be 0
        $worker = $all.services.'kafka-cdc-worker'
        $worker.image | Should -Be $env:CDC_CONNECT_IMAGE
        $worker.environment.OFFSET_STORAGE_TOPIC | Should -Be 'dms-connect-offsets'
        $worker.environment.CONNECT_CONNECTOR_CLIENT_CONFIG_OVERRIDE_POLICY | Should -Be 'All'
        $worker.ports | Where-Object { $_.target -eq 9404 } | ForEach-Object { $_.host_ip | Should -Be '127.0.0.1' }
        $worker.PSObject.Properties | Where-Object Name -eq 'depends_on' |
            ForEach-Object Value | Should -BeNullOrEmpty
    }

    It 'selects the shipped qualified image when no override is supplied' {
        $override = $env:CDC_CONNECT_IMAGE
        try {
            $env:CDC_CONNECT_IMAGE = $null
            $config = (& docker compose -f (Join-Path $script:root 'kafka-cdc.yml') --profile cdc-managed-worker config --format json | ConvertFrom-Json)
            $LASTEXITCODE | Should -Be 0
            $record = Get-Content -Raw (Join-Path $script:root '../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json') | ConvertFrom-Json
            $config.services.'kafka-cdc-worker'.image | Should -Be $record.image
        }
        finally { $env:CDC_CONNECT_IMAGE = $override }
    }

    It 'preserves the ordinary legacy Kafka and UI-only service inventory' {
        $config = (& docker compose -f (Join-Path $script:root 'kafka.yml') -f (Join-Path $script:root 'kafka-ui.yml') config --format json | ConvertFrom-Json)
        $LASTEXITCODE | Should -Be 0
        $config.services.PSObject.Properties.Name | Should -Contain 'kafka-postgresql-source'
        $config.services.PSObject.Properties.Name | Should -Not -Contain 'kafka-cdc-worker'
    }
}
