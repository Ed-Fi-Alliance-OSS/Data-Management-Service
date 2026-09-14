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

Describe 'CDC prerequisite CLI handoff' {
    BeforeEach {
        $script:tool = Join-Path $TestDrive 'schema-tools.ps1'
        @'
param()
$args | ConvertTo-Json -Compress | Set-Content (Join-Path $PSScriptRoot 'arguments.json')
'{"workflowId":"a1cb46ea-4c32-4f14-a65a-d841bca5c9b2","target":{"deploymentKey":"local","tenantKey":"default","dataStoreId":"42","instanceKey":"datastore-42","generation":1,"provider":"SqlServer"},"creationReceipt":{"receiptId":"9b571ac6-6841-40a6-bb62-dc9281b9c6a3","outcome":"Created"},"physicalSourceFingerprint":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}'
exit 0
'@ | Set-Content $script:tool
        $script:invoke = @{
            ToolPath = $script:tool; SchemaPaths = @('core.json'); ConnectionString = 'private'
            DatabaseName = 'private'; Dialect = 'mssql'; DataStoreId = 42
            CdcBindingStatePath = (Join-Path $TestDrive 'state')
        }
    }

    It 'forwards explicit local server preparation independently of database ownership' {
        Invoke-DmsSchemaProvision @script:invoke -CdcProjectionPrerequisites 'owned-local-sql-server' | Out-Null
        $arguments = Get-Content (Join-Path $TestDrive 'arguments.json') -Raw | ConvertFrom-Json
        $arguments | Should -Contain '--cdc-projection-prerequisites'
        $arguments | Should -Contain 'owned-local-sql-server'
    }

    It 'can request inspection without server mutation authority' {
        Invoke-DmsSchemaProvision @script:invoke -CdcProjectionPrerequisites 'inspect' | Out-Null
        $arguments = Get-Content (Join-Path $TestDrive 'arguments.json') -Raw | ConvertFrom-Json
        $arguments | Should -Contain 'inspect'
        $arguments | Should -Not -Contain 'owned-local-sql-server'
    }

    It 'leaves ordinary managed provisioning unchanged' {
        Invoke-DmsSchemaProvision @script:invoke | Out-Null
        $arguments = Get-Content (Join-Path $TestDrive 'arguments.json') -Raw | ConvertFrom-Json
        $arguments | Should -Not -Contain '--cdc-projection-prerequisites'
    }

    It 'rejects preparation without managed provenance' {
        $script:invoke.CdcBindingStatePath = ''
        { Invoke-DmsSchemaProvision @script:invoke -CdcProjectionPrerequisites 'inspect' } | Should -Throw '*requires managed*'
    }
}

Describe 'CDC prerequisite phase ordering' {
    BeforeAll {
        $tokens = $null
        $errors = $null
        foreach ($entry in @(
            @{ File = '../provision-dms-schema.ps1'; Function = 'Invoke-ProvisionDmsSchema' },
            @{ File = '../configure-local-data-store.ps1'; Function = 'ConvertTo-ConfigureResult' },
            @{ File = '../provision-dms-schema.ps1'; Function = 'Assert-CdcOwnedLocalSqlServer' }
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


    It 'checks live server ownership before passing preparation authority to SchemaTools' {
        Mock New-ProvisionTarget { return @{ DataStoreId = 42; TargetKey = 'local'; HostConnectionString = 'private'; DatabaseName = 'private'; Host = 'localhost'; Port = 1435; Dialect = 'mssql'; Username = 'private' } }
        $script:trace = @()
        Mock Assert-CdcOwnedLocalSqlServer { $script:trace += 'ownership' }
        Mock Invoke-DmsSchemaProvision { $script:trace += 'provision' }
        Invoke-ProvisionDmsSchema -EnvironmentFile 'local.env' -DataStoreId @(42) -CdcBindingStatePath (Join-Path $TestDrive 'state') -PrepareCdcProjectionPrerequisites
        $script:trace | Should -Be @('ownership', 'provision')
        Should -Invoke Invoke-DmsSchemaProvision -Times 1 -Exactly -ParameterFilter { $CdcProjectionPrerequisites -eq 'owned-local-sql-server' }
    }

    It 'stops before provisioning when server ownership evidence is unavailable' {
        Mock New-ProvisionTarget { return @{ DataStoreId = 42; TargetKey = 'local'; HostConnectionString = 'private'; DatabaseName = 'private'; Host = 'localhost'; Port = 1435; Dialect = 'mssql'; Username = 'private' } }
        Mock Assert-CdcOwnedLocalSqlServer { throw 'Unavailable authority' }
        { Invoke-ProvisionDmsSchema -EnvironmentFile 'local.env' -DataStoreId @(42) -CdcBindingStatePath (Join-Path $TestDrive 'state') -PrepareCdcProjectionPrerequisites } | Should -Throw '*Unavailable authority*'
        Should -Invoke Invoke-DmsSchemaProvision -Times 0 -Exactly
    }

    It 'preserves PostgreSQL without SQL Server preparation' {
        Mock Assert-CdcOwnedLocalSqlServer { throw 'Must not inspect SQL Server' }
        Invoke-ProvisionDmsSchema -EnvironmentFile 'local.env' -DataStoreId @(42) -CdcBindingStatePath (Join-Path $TestDrive 'state') -PrepareCdcProjectionPrerequisites
        Should -Invoke Invoke-DmsSchemaProvision -Times 1 -Exactly -ParameterFilter { $CdcProjectionPrerequisites -eq 'inspect' }
        Should -Invoke Assert-CdcOwnedLocalSqlServer -Times 0 -Exactly
    }
}

Describe 'CDC local SQL Server authority' {
    BeforeAll {
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:source, [ref]$tokens, [ref]$errors)
        $node = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-CdcOwnedLocalSqlServer' }, $true)
        # An AST-extracted function otherwise sees the test directory as PSScriptRoot.
        $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        . ([scriptblock]::Create($node.Extent.Text.Replace('$PSScriptRoot', '$script:composeRoot')))
        function Test-ProvisionTargetIsLocalComposeDatabase { param($Target, $EnvValues) return $true }
        function Get-LocalComposeDatabaseHostSideEndpoint { param($Dialect, $EnvValues) return @{ Port = 1435 } }
        function Test-PortNumberEquivalent { param($Left, $Right) return [int]$Left -eq [int]$Right }
        function docker { param([Parameter(ValueFromRemainingArguments)]$Arguments) }
    }

    BeforeEach {
        $script:evidence = @{ running = $true; project = 'cs-local'; service = 'db'; directory = $script:composeRoot; ports = @(@{ HostIp = '127.0.0.1'; HostPort = '1435' }) }
        Mock docker { $global:LASTEXITCODE = 0; return ($script:evidence | ConvertTo-Json -Depth 5 -Compress) }
    }

    It 'accepts this running Compose server with the selected published endpoint' {
        { Assert-CdcOwnedLocalSqlServer -Target @{} -EnvValues @{} } | Should -Not -Throw
    }

    It 'rejects <field> mismatch' -ForEach @(
        @{ field = 'running'; value = $false }, @{ field = 'project'; value = 'other' },
        @{ field = 'service'; value = 'other' }, @{ field = 'directory'; value = '/unrelated' },
        @{ field = 'ports'; value = @(@{ HostIp = '127.0.0.1'; HostPort = '1436' }) },
        @{ field = 'ports'; value = @(@{ HostIp = '0.0.0.0'; HostPort = '1435' }) },
        @{ field = 'ports'; value = @() }
    ) {
        $script:evidence[$field] = $value
        { Assert-CdcOwnedLocalSqlServer -Target @{} -EnvValues @{} } | Should -Throw '*live ownership and port evidence*'
    }

    It 'rejects unavailable Docker evidence without leaking output' {
        Mock docker { $global:LASTEXITCODE = 1; return 'private' }
        { Assert-CdcOwnedLocalSqlServer -Target @{} -EnvValues @{} } | Should -Throw '*live ownership and port evidence*'
    }

    It 'does not infer authority over an external server from database ownership' {
        Mock Test-ProvisionTargetIsLocalComposeDatabase { return $false }
        { Assert-CdcOwnedLocalSqlServer -Target @{} -EnvValues @{} } | Should -Throw '*owned local Compose*'
        Should -Invoke docker -Times 0 -Exactly
    }
}
