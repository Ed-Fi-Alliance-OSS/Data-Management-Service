# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param (
    [string] $DbType = "Postgresql", # or "MSSQL"
    [string] $ConnectionString = "Host=localhost;Port=5435;Database=edfi_datamanagementservice;Username=postgres;Password=abcdefgh1!",
    [string] $EnvironmentFile = "./.env",
    [string] $DbHost = "localhost",
    [string] $DbPort = "ENV:POSTGRES_PORT",
    [string] $DbName = "ENV:POSTGRES_DB_NAME",
    [string] $DbUser = "postgres",
    [string]$DbPassword = "ENV:POSTGRES_PASSWORD",
    [string] $NewClientId = "DmsConfigurationService",
    [string] $NewClientName = "DMS Configuration Service",
    [string] $NewClientSecret = "s3creT@09",
    [string] $DmsClientRole = "dms-client",
    [string] $ConfigServiceRole = "cms-client",
    [string] $ClientScopeName = "edfi_admin_api/full_access",
    [string] $ClaimName = "namespacePrefixes",
    [string] $ClaimValue = "http://ed-fi.org",
    [string] $EncryptionKey = "ENV:DMS_CONFIG_IDENTITY_ENCRYPTION_KEY",
    [int] $TokenLifespan = 1800,
    [switch] $InitDb = $false,
    [switch] $InsertData = $true
)
Import-Module ./env-utility.psm1
$envValues = ReadValuesFromEnvFile $EnvironmentFile

function New-OpenIddictRole {
    param([string]$RoleName)
    $roleId = [guid]::NewGuid().ToString()
    $sqlInsert = "INSERT INTO dmscs.OpenIddictRole (Id, Name) VALUES ('$roleId', '$RoleName') ON CONFLICT (Name) DO NOTHING RETURNING Id;"
    $result = Invoke-DbQuery $sqlInsert

    $sqlSelect = "SELECT Id FROM dmscs.OpenIddictRole WHERE Name = '$RoleName';"
    $existing = Invoke-DbQuery $sqlSelect
    return $existing[2]
}

function New-OpenIddictScope {
    param([string]$ScopeName, [string]$Description)
    $scopeId = [guid]::NewGuid().ToString()
    $sqlInsert = "INSERT INTO dmscs.OpenIddictScope (Id, Name, Description) VALUES ('$scopeId', '$ScopeName', '$Description') ON CONFLICT (Name) DO NOTHING RETURNING Id;"
    $result = Invoke-DbQuery $sqlInsert
    $sqlSelect = "SELECT Id FROM dmscs.OpenIddictScope WHERE Name = '$ScopeName';"
    $existing = Invoke-DbQuery $sqlSelect
    return $existing[2]
}

function New-OpenIddictApplication {
    param([string]$ClientId, [string]$ClientName, [string]$ClientSecret)
    $appId = [guid]::NewGuid().ToString()
    $sqlInsert = "INSERT INTO dmscs.OpenIddictApplication (Id, ClientId, ClientSecret, DisplayName, Type) VALUES ('$appId', '$ClientId', '$ClientSecret', '$ClientName', 'confidential') ON CONFLICT (ClientId) DO NOTHING RETURNING Id; "
    $result = Invoke-DbQuery $sqlInsert

    $sqlSelect = "SELECT Id FROM dmscs.OpenIddictApplication WHERE ClientId = '$ClientId';"
    $existing = Invoke-DbQuery $sqlSelect
    return $existing[2]
}

function Add-OpenIddictClientRole {
    param([string]$AppId, [string]$RoleId)
    $sql = "INSERT INTO dmscs.OpenIddictClientRole (ClientId, RoleId) VALUES ('$AppId', '$RoleId') ON CONFLICT DO NOTHING;"
    Invoke-DbQuery $sql
}

function Add-OpenIddictApplicationScope {
    param([string]$AppId, [string]$ScopeId)
    $sql = "INSERT INTO dmscs.OpenIddictApplicationScope (ApplicationId, ScopeId) VALUES ('$AppId', '$ScopeId') ON CONFLICT DO NOTHING;"
    Invoke-DbQuery $sql
}

function Add-OpenIddictCustomClaim {
    param([string]$AppId, [string]$ClaimName, [string]$ClaimValue)
    $protocolMapper = @{ $ClaimName = $ClaimValue } | ConvertTo-Json -Compress
    $escapedJson = $protocolMapper -replace "'", "''"
    $sql = @"
UPDATE dmscs.OpenIddictApplication
SET ProtocolMappers = COALESCE(ProtocolMappers, '{}'::jsonb) || '$escapedJson'::jsonb
WHERE Id = '$AppId';
"@
    Invoke-DbQuery $sql
}

function Update-OpenIddictApplicationPermissions {
    param([string]$AppId, [string]$Scope)
    $permissionsString = "{$Scope}" -replace "'", "''"
    $sql = @"
UPDATE dmscs.OpenIddictApplication
SET Permissions = '$permissionsString'
WHERE Id = '$AppId';
"@
    Invoke-DbQuery $sql
}

function Resolve-EnvValue {
    param(
        [string]$Value
    )
    if ($Value -like "ENV:*") {
        $envVarName = $Value.Substring(4)
        $envVal = $envValues["$envVarName"]
        if ($envVal) { return $envVal }
        throw "ENV file or variable not found for $envVarName"
    }
    return $Value
}
function Build-ConnectionString {
    param(
        [string]$DbType,
        [string]$DbHost,
        [string]$DbPort,
        [string]$DbName,
        [string]$DbUser,
        [string]$DbPassword,
        [string]$EnvironmentFile = $null
    )
    $DbHost = Resolve-EnvValue $DbHost
    $DbPort = Resolve-EnvValue $DbPort
    $DbName = Resolve-EnvValue $DbName
    $DbUser = Resolve-EnvValue $DbUser
    $DbPassword = Resolve-EnvValue $DbPassword
    if ($DbType -eq "Postgresql") {
        return "Host=$DbHost;Port=$DbPort;Database=$DbName;Username=$DbUser;Password=$DbPassword"
    }
    elseif ($DbType -eq "MSSQL") {
        return "Server=$DbHost,$DbPort;Database=$DbName;User Id=$DbUser;Password=$DbPassword"
    }
    else {
        throw "Unsupported DbType: $DbType"
    }
}

function Get-EffectiveConnectionString {
    param(
        [string]$EnvironmentFile,
        [string]$ConnectionString,
        [string]$DbType,
        [string]$DbHost,
        [string]$DbPort,
        [string]$DbName,
        [string]$DbUser,
        [string]$DbPassword
    )
    # If EnvironmentFile is set, always use DB param group and ignore ConnectionString
    if ($EnvironmentFile) {
        return Build-ConnectionString -DbType $DbType -DbHost $DbHost -DbPort $DbPort -DbName $DbName -DbUser $DbUser -DbPassword $DbPassword -EnvironmentFile $EnvironmentFile
    }
    # If ConnectionString starts with ENV:, read from env file
    if ($ConnectionString -like "ENV:*") {
        $envVarName = $ConnectionString.Substring(4)
        if ($EnvironmentFile) {
            $envConnStr = Resolve-EnvValue $envVarName
            if ($envConnStr) { return $envConnStr }
        }
        throw "ENV file or variable not found for $envVarName"
    }
    # If ConnectionString is empty, build from parameters (which may use ENV: prefix)
    if (-not $ConnectionString) {
        return Build-ConnectionString -DbType $DbType -DbHost $DbHost -DbPort $DbPort -DbName $DbName -DbUser $DbUser -DbPassword $DbPassword -EnvironmentFile $EnvironmentFile
    }
    # Otherwise, use the provided ConnectionString
    return $ConnectionString
}

function Invoke-DbQuery {
    param([string]$Sql)
    $effectiveConnectionString = Get-EffectiveConnectionString -EnvironmentFile $EnvironmentFile -ConnectionString $ConnectionString -DbType $DbType -DbHost $DbHost -DbPort $DbPort -DbName $DbName -DbUser $DbUser -DbPassword $DbPassword
    if ($DbType -eq "Postgresql") {
        # Parse semicolon-separated connection string
        $params = @{}
        foreach ($pair in $effectiveConnectionString -split ';') {
            if ($pair -match '=') {
                $kv = $pair -split '=', 2
                $params[$kv[0].Trim()] = $kv[1].Trim()
            }
        }
        $dbHost = $params['Host']
        $port = $params['Port']
        $db = $params['Database']
        $user = $params['Username']
        $pass = $params['Password']
        $env:PGPASSWORD = $pass
        psql -h $dbHost -p $port -U $user -d $db -c "$Sql"
        Remove-Item Env:PGPASSWORD
    }
    elseif ($DbType -eq "MSSQL") {
        # Use sqlcmd or Invoke-Sqlcmd for MSSQL
        Write-Error "MSSQL not implemented yet."
    }
    else {
        Write-Error "Unsupported database type: $DbType"
    }
}

# Main logic
function Invoke-InitDbScripts {
    Write-Host "InitDb specified: running database initialization scripts..."
    # Run embedded SQL script contents
    Write-Host "Create schema if not exists: dmscs"
    Invoke-DbQuery @'
CREATE SCHEMA IF NOT EXISTS dmscs;
'@

    Write-Host "Create table for OpenIddict keys if not exists: dmscs.OpenIddictKey"
    Invoke-DbQuery @'
CREATE TABLE IF NOT EXISTS dmscs.OpenIddictKey (
    Id SERIAL PRIMARY KEY,
    KeyId VARCHAR(64) NOT NULL,
    PublicKey BYTEA NOT NULL, -- binary format for public key
    PrivateKey TEXT NOT NULL, -- encrypted with pgcrypto
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    ExpiresAt TIMESTAMP,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE
);
'@

    Write-Host "Create extension if not exists: pgcrypto"
    Invoke-DbQuery @'
CREATE EXTENSION IF NOT EXISTS pgcrypto;
'@
    # Generate and output OpenIddictKey insert SQL
    Write-Host "Generating OpenIddictKey insert statement..."
    $encryptionKey = Resolve-EnvValue $EncryptionKey
    $keyInsertSql = & "$PSScriptRoot/Generate-OpenIddictKey-Insert.ps1" -EncryptionKey $encryptionKey
    Write-Host "Insert public and private keys into dmscs.OpenIddictKey"
    Invoke-DbQuery $keyInsertSql
    Write-Host "Database initialization scripts completed."
}

if ($InitDb) {
    Invoke-InitDbScripts
}

if ($InsertData) {
    $appId = New-OpenIddictApplication -ClientId $NewClientId -ClientName $NewClientName -ClientSecret $NewClientSecret
    $dmsRoleId = New-OpenIddictRole -RoleName $DmsClientRole
    $configRoleId = New-OpenIddictRole -RoleName $ConfigServiceRole
    $scopeId = New-OpenIddictScope -ScopeName $ClientScopeName -Description $ClientScopeName
    Add-OpenIddictClientRole -AppId $appId.Trim() -RoleId $dmsRoleId.Trim()
    Add-OpenIddictClientRole -AppId $appId.Trim() -RoleId $configRoleId.Trim()
    Add-OpenIddictApplicationScope -AppId $appId.Trim() -ScopeId $scopeId.Trim()
    Update-OpenIddictApplicationPermissions -AppId $appId.Trim() -Scope  $ClientScopeName
    Add-OpenIddictCustomClaim -AppId $appId.Trim() -ClaimName $ClaimName -ClaimValue $ClaimValue
    Write-Output "OpenIddict client, roles, scope, and claim created successfully."
}
