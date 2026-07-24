# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Behavioral proof of the OpenIddict argument handoff (Iteration 3). Each start script's self-contained
# identity block maps the runtime contract's OpenIddict coordinates onto the ACTUAL arguments that reach
# setup-openiddict.ps1, choosing the engine-correct container-name parameter. The construction block and
# every setup-openiddict invocation are extracted from the real script via AST and executed against a
# synthetic contract with a capture-stub setup-openiddict.ps1, so the concrete DbHost/DbPort/DbUser/DbType/
# DbName, the container-name parameter, and the SA password RECEIVED are asserted - not merely present in
# source text. The shell-override -> contract.OpenIddict link is proven separately by the live Compose oracle
# (RuntimeConfigContract); together they cover the full chain shell -> contract -> setup-openiddict arguments,
# which is why the synthetic coordinates here use overridden (non-default) port/user values.

BeforeAll {
    $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

    function script:Get-OpenIddictHandoffCapture {
        param(
            [Parameter(Mandatory)][string]$ScriptName,
            [Parameter(Mandatory)][ValidateSet('postgresql', 'mssql')][string]$Engine,
            [Parameter(Mandatory)][object]$Contract
        )

        $scriptPath = Join-Path $script:composeRoot $ScriptName
        $tokens = $null; $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors) { throw "Parse errors in ${ScriptName}: $($parseErrors[0].Message)" }

        # The params variable is $identityDbParams (full-stack starts) or $identityDbArgs (standalone config).
        $paramsVar = if ($ast.Extent.Text -match '\$identityDbParams\s*=\s*@\{') { 'identityDbParams' } else { 'identityDbArgs' }
        $needle = "`$$paramsVar = @{"

        # Smallest if-block that constructs the params hashtable (the self-contained identity block).
        $ctor = $ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.Contains($needle)
            }, $true) | Sort-Object { $_.Extent.Text.Length } | Select-Object -First 1
        if ($null -eq $ctor) { throw "Could not locate the identity construction block in $ScriptName" }

        # Every setup-openiddict.ps1 invocation NOT already inside the construction block (the standalone-config
        # InitDb call lives inside its construction if-block, so excluding it avoids firing that call twice).
        $calls = $ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.CommandAst] -and
                $n.CommandElements.Count -gt 0 -and
                ([string]$n.CommandElements[0].Extent.Text) -match 'setup-openiddict\.ps1'
            }, $true)
        $outsideCalls = @($calls | Where-Object {
                $_.Extent.StartOffset -lt $ctor.Extent.StartOffset -or $_.Extent.EndOffset -gt $ctor.Extent.EndOffset
            })

        $bodyText = $ctor.Extent.Text
        if ($outsideCalls.Count -gt 0) {
            $bodyText += "`n" + (($outsideCalls | ForEach-Object { $_.Extent.Text }) -join "`n")
        }

        $runnerText = @"
param(`$contract, `$DatabaseEngine, `$datastore, `$IdentityProvider, `$EnvironmentFile, `$identityClientSecrets)
$bodyText
"@
        $runner = [scriptblock]::Create($runnerText)

        $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-openiddict-handoff-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        $captureFile = Join-Path $work "capture.jsonl"
        # Capture stub: mirrors the setup-openiddict.ps1 parameter surface the invocations use and appends the
        # bound arguments (one compact JSON record per call) rather than doing any work.
        $stub = @'
[CmdletBinding()]
param(
    [switch]$InitDb, [switch]$InsertData,
    [string]$EnvironmentFile,
    $DbType, $DbUser, $DbHost, $DbPort, $DbName,
    [string]$MssqlContainerName, [string]$PostgresContainerName, $DbPassword,
    [string]$NewClientId, [string]$NewClientName, [string]$ClientScopeName,
    [string]$NewClientSecret, $ClientSecretMinimumLength, $ClientSecretMaximumLength
)
$record = [ordered]@{ Mode = $(if ($InitDb) { 'InitDb' } elseif ($InsertData) { 'InsertData' } else { 'unknown' }) }
foreach ($name in @('DbType', 'DbUser', 'DbHost', 'DbPort', 'DbName', 'MssqlContainerName', 'PostgresContainerName', 'DbPassword')) {
    if ($PSBoundParameters.ContainsKey($name)) { $record[$name] = [string]$PSBoundParameters[$name] }
}
Add-Content -LiteralPath $env:DMS_OPENIDDICT_CAPTURE -Value ($record | ConvertTo-Json -Compress)
'@
        Set-Content -LiteralPath (Join-Path $work "setup-openiddict.ps1") -Value $stub -NoNewline

        $secrets = [pscustomobject]@{
            DmsConfigurationServiceClientSecret = 's1'
            CmsReadOnlyAccessClientSecret       = 's2'
            ClientSecretMinimumLength           = 20
            ClientSecretMaximumLength           = 30
        }

        $priorCapture = $env:DMS_OPENIDDICT_CAPTURE
        $env:DMS_OPENIDDICT_CAPTURE = $captureFile
        Push-Location $work
        try {
            & $runner -contract $Contract -DatabaseEngine $Engine -datastore $Engine -IdentityProvider 'self-contained' -EnvironmentFile 'ignored.env' -identityClientSecrets $secrets | Out-Null
        }
        finally {
            Pop-Location
            if ($null -eq $priorCapture) { Remove-Item -LiteralPath Env:DMS_OPENIDDICT_CAPTURE -ErrorAction SilentlyContinue } else { $env:DMS_OPENIDDICT_CAPTURE = $priorCapture }
        }

        $records = @()
        if (Test-Path -LiteralPath $captureFile) {
            $records = @(Get-Content -LiteralPath $captureFile | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
        }
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        return , $records
    }

    function script:New-OpenIddictContract {
        param([ValidateSet('postgresql', 'mssql')][string]$Engine, [string]$User, [int]$Port)
        [pscustomobject]@{
            OpenIddict = [pscustomobject]@{
                DbType          = if ($Engine -eq 'mssql') { 'MSSQL' } else { 'Postgresql' }
                DbUser          = $User
                DbHost          = '127.0.0.1'
                DbPort          = $Port
                DbName          = 'edfi_datamanagementservice'
                DbContainerName = if ($Engine -eq 'mssql') { 'dms-mssql' } else { 'dms-postgresql' }
                DbPassword      = if ($Engine -eq 'mssql') { 'abcdefgh1!' } else { $null }
            }
        }
    }
}

Describe "OpenIddict argument handoff - <Script>" -ForEach @(
    @{ Script = 'start-local-dms.ps1' }
    @{ Script = 'start-published-dms.ps1' }
    @{ Script = 'start-local-config.ps1' }
) {
    It "passes the PostgreSQL contract coordinates (incl. an overridden port/user) and the Postgres container name to every OpenIddict call" {
        $contract = New-OpenIddictContract -Engine postgresql -User 'alt_admin' -Port 5599
        $records = Get-OpenIddictHandoffCapture -ScriptName $Script -Engine postgresql -Contract $contract

        $records.Count | Should -BeGreaterThan 0 -Because "the self-contained lane invokes setup-openiddict at least once"
        @($records | Where-Object { $_.Mode -eq 'InitDb' }).Count | Should -BeGreaterThan 0 -Because "the pre-CMS InitDb creation must run"
        foreach ($r in $records) {
            $r.DbType | Should -Be 'Postgresql'
            $r.DbUser | Should -Be 'alt_admin'
            $r.DbHost | Should -Be '127.0.0.1'
            $r.DbPort | Should -Be '5599'
            $r.DbName | Should -Be 'edfi_datamanagementservice'
            $r.PostgresContainerName | Should -Be 'dms-postgresql'
            $r.PSObject.Properties.Name | Should -Not -Contain 'MssqlContainerName' -Because "the PostgreSQL lane selects the Postgres container-name parameter"
            $r.PSObject.Properties.Name | Should -Not -Contain 'DbPassword' -Because "PostgreSQL self-contained connects without a password"
        }
    }

    It "passes the SQL Server contract coordinates (incl. an overridden port), the mssql container name, and the SA password to every OpenIddict call" {
        $contract = New-OpenIddictContract -Engine mssql -User 'sa' -Port 1599
        $records = Get-OpenIddictHandoffCapture -ScriptName $Script -Engine mssql -Contract $contract

        $records.Count | Should -BeGreaterThan 0
        @($records | Where-Object { $_.Mode -eq 'InitDb' }).Count | Should -BeGreaterThan 0
        foreach ($r in $records) {
            $r.DbType | Should -Be 'MSSQL'
            $r.DbUser | Should -Be 'sa'
            $r.DbHost | Should -Be '127.0.0.1'
            $r.DbPort | Should -Be '1599'
            $r.DbName | Should -Be 'edfi_datamanagementservice'
            $r.MssqlContainerName | Should -Be 'dms-mssql'
            $r.DbPassword | Should -Be 'abcdefgh1!'
            $r.PSObject.Properties.Name | Should -Not -Contain 'PostgresContainerName' -Because "the SQL Server lane selects the mssql container-name parameter"
        }
    }
}
