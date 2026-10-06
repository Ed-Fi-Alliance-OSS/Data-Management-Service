# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Shared private inputs only. Callers retain infrastructure, settings generation and procedure ownership.
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../env-utility.psm1') -DisableNameChecking

function New-CdcFixtureInput {
    <# .SYNOPSIS
    Prepares private environment, credentials, base configuration and connection input for an owned fixture.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only caller-owned disposable private fixture inputs.')]
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([string] $Repo, [string] $FixtureRoot, [bool] $SqlServer, [bool] $E2e,
        [string] $ProviderImage, [bool] $LargeWorker = $false, [hashtable] $AdditionalValues = @{})
    $ErrorActionPreference = 'Stop'
    $local = Join-Path $FixtureRoot '.local/cdc'
    $null = New-Item -ItemType Directory -Path $local -Force
    & chmod 700 $FixtureRoot (Split-Path $local) $local
    if ($LASTEXITCODE -ne 0) { throw 'Private input permissions failed.' }
    $providerName = if ($SqlServer) { 'sqlserver' } else { 'postgresql' }
    $stateName = if ($SqlServer) { 'state-sqlserver' } else { 'state-pg' }
    $source = if ($E2e) { '.env.e2e' } else { '.env.example' }
    $environmentName = if ($E2e) { '.env.e2e' } else { '.env' }
    $environmentFile = Join-Path $FixtureRoot $environmentName
    Copy-Item (Join-Path $Repo "eng/docker-compose/$source") $environmentFile
    $password = [guid]::NewGuid().ToString('N') + 'Ab1!'
    $connectorPassword = [guid]::NewGuid().ToString('N') + 'Ab1!'
    $environmentText = Get-Content $environmentFile -Raw
    $fixtureValues = @{ POSTGRES_USER = 'postgres'; POSTGRES_PASSWORD = $password; POSTGRES_PORT = '5435'; CDC_DATABASE_PASSWORD = $connectorPassword }
    if (-not $ProviderImage) { throw 'Provider image required.' }
    $imageVariable = if ($SqlServer) { 'MSSQL_IMAGE' } else { 'POSTGRES_IMAGE' }
    $fixtureValues[$imageVariable] = $providerImage
    # Cold plugin scans need headroom on high-core qualification hosts. Keep the
    # declared worker policy and Compose heap identical from initial creation.
    if ($LargeWorker) { $fixtureValues.CDC_WORKER_HEAP_MIB = '1024' }
    if ($SqlServer) {
        $fixtureValues.MSSQL_SA_PASSWORD = $password; $fixtureValues.MSSQL_PORT = '1435'
        $fixtureValues.DMS_DATASTORE = 'mssql'; $fixtureValues.DMS_CONFIG_DATASTORE = 'mssql'
    }
    foreach ($name in $AdditionalValues.Keys) { $fixtureValues[$name] = $AdditionalValues[$name] }
    foreach ($pair in $fixtureValues.GetEnumerator()) {
        $pattern = '(?m)^' + [regex]::Escape($pair.Key) + '=.*$'
        $assignment = "$($pair.Key)=$($pair.Value)"
        if ([regex]::IsMatch($environmentText, $pattern)) { $environmentText = [regex]::Replace($environmentText, $pattern, $assignment) }
        else { $environmentText += "`n$assignment`n" }
    }
    $environmentText | Set-Content $environmentFile
    & chmod 600 $environmentFile
    $values = ReadValuesFromEnvFile $environmentFile
    $database = if ($E2e) { $values.E2E_DATABASE_NAME } else { 'edfi_cdc' }
    $suffix = if ($E2e) { '-e2e' } else { '' }
    $settingsPath = Join-Path $local "$providerName$suffix.json"
    $statePath = Join-Path $local "$stateName$suffix"
    $base = Get-Content (Join-Path $Repo 'src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json') -Raw | ConvertFrom-Json -AsHashtable
    $base.ConfigurationServiceSettings.BaseUrl = 'http://127.0.0.1:8081'
    $base.ConfigurationServiceSettings.ClientId = $values.CONFIG_SERVICE_CLIENT_ID
    $base.ConfigurationServiceSettings.ClientSecret = $values.CONFIG_SERVICE_CLIENT_SECRET
    $base.ConfigurationServiceSettings.Scope = $values.CONFIG_SERVICE_CLIENT_SCOPE
    $base.ConfigurationServiceSettings.EncryptionKey = $values.DMS_CONFIG_DATABASE_ENCRYPTION_KEY
    $base.AppSettings.AuthenticationService = 'http://127.0.0.1:8081/connect/token'
    $base.JwtAuthentication.Authority = 'http://127.0.0.1:8081'
    $base.JwtAuthentication.MetadataAddress = 'http://127.0.0.1:8081/.well-known/openid-configuration'
    $base | ConvertTo-Json -Depth 64 | Set-Content (Join-Path $local 'dms-base.json')
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Database'] = $database; $builder['Password'] = $password
    if ($SqlServer) {
        $builder['Server'] = '127.0.0.1,1435'; $builder['User Id'] = 'sa'
        $builder['Encrypt'] = 'true'; $builder['TrustServerCertificate'] = 'true'; $builder['Command Timeout'] = '180'
    } else {
        $builder['Host'] = '127.0.0.1'; $builder['Port'] = '5435'; $builder['Username'] = 'postgres'
    }
    $inputPath = Join-Path $FixtureRoot 'setup-connection.txt'
    $builder.ConnectionString | Set-Content $inputPath
    & chmod 600 $inputPath (Join-Path $local 'dms-base.json')
    if ($LASTEXITCODE -ne 0) { throw 'Private input permissions failed.' }
    return @{ EnvironmentFile = $environmentFile; SettingsPath = $settingsPath; StatePath = $statePath;
        Local = $local; InputPath = $inputPath; Database = $database; Values = $values;
        Password = $password; ConnectorPassword = $connectorPassword; ImageVariable = $imageVariable }
}

function Invoke-CdcFixtureSql {
    <# .SYNOPSIS
    Sends fixture SQL over stdin using the SQL Server container's own admin credential.
    #>
    param([string] $Sql, [string] $Database = 'master')
    Invoke-NativeCommandWithInput -FilePath 'docker' -ArgumentList @('exec', '-i', 'dms-mssql', 'sh', '-c',
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W -d "$1"', 'sqlcmd', $Database) -InputText ("SET NOCOUNT ON;`n" + $Sql)
}

function Initialize-CdcFixturePrincipal {
    <# .SYNOPSIS
    Checks target absence and creates only the restricted connector role or login.
    #>
    param([bool] $SqlServer, [hashtable] $Inputs)
    # Deployment principal preparation only; the target must remain absent for managed admission.
    $database = $Inputs.Database.Replace("'", "''")
    $password = $Inputs.ConnectorPassword.Replace("'", "''")
    if ($SqlServer) {
        $server = (& docker inspect dms-mssql | ConvertFrom-Json)[0]
        if ($LASTEXITCODE -ne 0 -or $server.Config.Env -cnotcontains "MSSQL_SA_PASSWORD=$($Inputs.Password)") {
            throw 'Provider credential mismatch.'
        }
        $result = Invoke-CdcFixtureSql "IF DB_ID(N'$database') IS NOT NULL THROW 51000, 'Target must be absent', 1; CREATE LOGIN cdc_reader WITH PASSWORD = '$password';"
    } else {
        $sql = "DO `$`$ BEGIN IF EXISTS (SELECT FROM pg_database WHERE datname = '$database') THEN RAISE EXCEPTION 'Target must be absent'; END IF; END `$`$; CREATE ROLE cdc_reader WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '$password';"
        $result = Invoke-NativeCommandWithInput -FilePath 'docker' -ArgumentList @('exec', '-i', 'dms-postgresql', 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1') -InputText $sql
    }
    if ($result.FailureKind -ne 'None' -or $result.ExitCode -ne 0) { throw 'Restricted principal preparation failed.' }
}

Export-ModuleMember -Function New-CdcFixtureInput, Initialize-CdcFixturePrincipal, Invoke-CdcFixtureSql
