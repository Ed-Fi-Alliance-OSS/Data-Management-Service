# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester stand-ins keep the production parameter signatures for named-argument forwarding checks.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Pester stand-ins use the existing production command names.')]
param()

BeforeAll {
    $script:source = Join-Path $PSScriptRoot '../provision-dms-schema.ps1'
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:source, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw 'Provision script did not parse.' }
    $functionAst = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Invoke-DmsSchemaProvision'
    }, $true)
    . ([scriptblock]::Create($functionAst.Extent.Text))
    function Format-LogSafeText { param($Value) return [string]$Value }
}

Describe 'Managed creation receipt handoff' {
    BeforeEach {
        Remove-Item (Join-Path $TestDrive 'arguments.json') -ErrorAction SilentlyContinue
        $script:tool = Join-Path $TestDrive 'schema-tools.ps1'
        @'
param()
$args | ConvertTo-Json -Compress | Set-Content (Join-Path $PSScriptRoot 'arguments.json')
'{"workflowId":"a1cb46ea-4c32-4f14-a65a-d841bca5c9b2","target":{"deploymentKey":"local","tenantKey":"default","dataStoreId":"42","instanceKey":"datastore-42","generation":1,"provider":"Postgresql"},"creationReceipt":{"receiptId":"9b571ac6-6841-40a6-bb62-dc9281b9c6a3","outcome":"Created"},"physicalSourceFingerprint":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}'
exit 0
'@ | Set-Content $script:tool
        $script:invoke = @{
            ToolPath = $script:tool
            SchemaPaths = @('core.json', 'extension.json')
            ConnectionString = 'Host=localhost;Database=private-source;Password=private-secret'
            DatabaseName = 'private-source'
            CdcBindingStatePath = (Join-Path $TestDrive 'state')
            DataStoreId = 42
        }
    }

    It 'passes selected CMS identity and separate state root even without a Kafka selection' {
        $result = Invoke-DmsSchemaProvision @script:invoke
        $result.creationReceipt.outcome | Should -BeExactly 'Created'
        $result.target.dataStoreId | Should -BeExactly '42'
        $arguments = Get-Content (Join-Path $TestDrive 'arguments.json') -Raw | ConvertFrom-Json
        $arguments | Should -Contain '--create-database'
        $arguments | Should -Contain '--managed-state-path'
        $arguments | Should -Contain $script:invoke.CdcBindingStatePath
        $arguments | Should -Contain 'datastore-42'
        $arguments | Should -Not -Contain '--enable-kafka-cdc'
    }

    It 'returns reused evidence explicitly' {
        (Get-Content $script:tool -Raw).Replace('"Created"', '"Reused"') | Set-Content $script:tool
        (Invoke-DmsSchemaProvision @script:invoke).creationReceipt.outcome | Should -BeExactly 'Reused'
    }

    It 'rejects a receipt for a different selected data store' {
        (Get-Content $script:tool -Raw).Replace('"dataStoreId":"42"', '"dataStoreId":"43"') | Set-Content $script:tool
        { Invoke-DmsSchemaProvision @script:invoke } | Should -Throw '*mismatched receipt*'
    }

    It 'does not expose native output when the tool fails' {
        "'private-secret'; exit 1" | Set-Content $script:tool
        { Invoke-DmsSchemaProvision @script:invoke } | Should -Throw '*Managed schema provisioning failed*'
    }

    It 'rejects malformed output rather than inferring ownership from successful DDL' {
        "'Provisioning complete'; exit 0" | Set-Content $script:tool
        { Invoke-DmsSchemaProvision @script:invoke } | Should -Throw '*valid receipt*'
    }

    It 'retains legacy native output for unmanaged provisioning' {
        $script:invoke.CdcBindingStatePath = ''
        "'Provisioning complete'; exit 0" | Set-Content $script:tool
        Invoke-DmsSchemaProvision @script:invoke | Should -BeExactly 'Provisioning complete'
    }

    It 'rejects missing selected identity before launching a tool' {
        $script:invoke.DataStoreId = 0
        { Invoke-DmsSchemaProvision @script:invoke } | Should -Throw '*positive selected data store*'
        Test-Path (Join-Path $TestDrive 'arguments.json') | Should -BeFalse
    }
}

Describe 'Managed provisioning uses the configure phase selection' {
    BeforeAll {
        $tokens = $null
        $errors = $null
        foreach ($entry in @(
            @{ File = '../provision-dms-schema.ps1'; Function = 'Invoke-ProvisionDmsSchema' },
            @{ File = '../configure-local-data-store.ps1'; Function = 'ConvertTo-ConfigureResult' }
        )) {
            $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $entry.File), [ref]$tokens, [ref]$errors)
            $name = $entry.Function
            $node = $ast.Find({ param($n)
                $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name
            }, $true)
            . ([scriptblock]::Create($node.Extent.Text))
        }
        function Resolve-ProvisionEnvironmentFile { param($Path) return $Path }
        function Resolve-DatabaseEngineEnvironmentFile { param($DatabaseEngine, $BaseEnvironmentFile, $DockerComposeRoot) return $BaseEnvironmentFile }
        function ReadValuesFromEnvFile { param($EnvironmentFile) return @{} }
        function Resolve-CmsBaseUrl { param($EnvValues) return 'http://cms' }
        function Get-EnvValueOrDefault { param($EnvValues, $Name) return '' }
        function Resolve-BootstrapSchemaWorkspace { return @{ CoreSchemaPath = 'core.json'; ExtensionSchemaPaths = @() } }
        function Resolve-BootstrapAdminClient { param($EnvValues) return @{ ClientId = 'bootstrap'; ClientSecret = 'test' } }
        function Get-CmsToken { param($CmsUrl, $ClientId, $ClientSecret) return 'test-token' }
        function Get-DataStore { param($CmsUrl, $AccessToken, $Tenant, $Limit) return @(@{ id = 42 }, @{ id = 43 }) }
        function Resolve-ProvisionTargetInstances { param($Instances, $DataStoreId, $SchoolYear, $Tenant) return @($Instances | Where-Object { $_.id -in $DataStoreId }) }
        function Resolve-DmsSchemaTool { param($RequestedPath) return 'schema-tool' }
        function New-ProvisionTarget {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test stand-in constructs an in-memory target object only.')]
            param($Instance, $EnvValues, $SchemaToolPath)
            return @{ DataStoreId = $Instance.id; TargetKey = 'local-target'; HostConnectionString = 'private'; DatabaseName = 'private'; Host = 'localhost'; Port = 5432; Dialect = 'pgsql'; Username = 'private' }
        }
        function Test-ProvisionTargetIsLocalComposeDatabase { param($Target, $EnvValues) return $true }
        function Write-ProvisionSummary { param($EnvValues, $SchemaWorkspace, $ProvisionedTargets) }
    }

    BeforeEach {
        Mock Invoke-DmsSchemaProvision { return @{ CreationReceipt = @{ Outcome = 'Created' } } }
        Mock Write-ProvisionSummary {}
    }

    It 'forwards structured selected IDs to the controller without inferring creation from CMS' {
        $configured = ConvertTo-ConfigureResult -DataStoreIds @(42)
        $result = Invoke-ProvisionDmsSchema -EnvironmentFile 'local.env' -DataStoreId $configured.SelectedDataStoreIds -CdcBindingStatePath (Join-Path $TestDrive 'state')
        $result.CreationReceipt.Outcome | Should -BeExactly 'Created'
        Should -Invoke Invoke-DmsSchemaProvision -Times 1 -Exactly -ParameterFilter {
            $DataStoreId -eq 42 -and $CdcBindingStatePath -eq (Join-Path $TestDrive 'state') -and $Generation -eq 1
        }
        Should -Invoke Write-ProvisionSummary -Times 0 -Exactly
    }

    It 'rejects shared selected aliases before any provider mutation' {
        $configured = ConvertTo-ConfigureResult -DataStoreIds @(42, 43)
        { Invoke-ProvisionDmsSchema -EnvironmentFile 'local.env' -DataStoreId $configured.SelectedDataStoreIds -CdcBindingStatePath (Join-Path $TestDrive 'state') } | Should -Throw '*multiple data store aliases*'
        Should -Invoke Invoke-DmsSchemaProvision -Times 0 -Exactly
    }

    It 'does not claim managed ownership of an external database server' {
        Mock Test-ProvisionTargetIsLocalComposeDatabase { return $false }
        { Invoke-ProvisionDmsSchema -EnvironmentFile 'local.env' -DataStoreId @(42) -CdcBindingStatePath (Join-Path $TestDrive 'state') } | Should -Throw '*local Compose database service*'
        Should -Invoke Invoke-DmsSchemaProvision -Times 0 -Exactly
    }
}
