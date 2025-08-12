# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param (
    [string] $DbType = "Postgresql", # or "MSSQL"
    [string] $ConnectionString = "Host=localhost;Port=5435;Database=edfi_datamanagementservice;Username=postgres;Password=abcdefgh1!",
    [string] $NewClientId = "DmsConfigurationService",
    [string] $NewClientName = "DMS Configuration Service",
    [string] $NewClientSecret = "s3creT@09",
    [string] $DmsClientRole = "dms-client",
    [string] $ConfigServiceRole = "cms-client",
    [string] $ClientScopeName = "edfi_admin_api/full_access",
    [string] $ClaimName = "namespacePrefixes",
    [string] $ClaimValue = "http://ed-fi.org",
    [int] $TokenLifespan = 1800
)

function Get_Access_Token {
    # Placeholder: This will use API as in Keycloak, to be updated later
    Write-Output "Get_Access_Token: Not implemented yet."
    return $null
}

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

function Invoke-DbQuery {
    param([string]$Sql)
    if ($DbType -eq "Postgresql") {
        # Parse semicolon-separated connection string
        $params = @{}
        foreach ($pair in $ConnectionString -split ';') {
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
    } elseif ($DbType -eq "MSSQL") {
        # Use sqlcmd or Invoke-Sqlcmd for MSSQL
        Write-Error "MSSQL not implemented yet."
    } else {
        Write-Error "Unsupported database type: $DbType"
    }
}

# Main logic
$access_token = Get_Access_Token

$appId = New-OpenIddictApplication -ClientId $NewClientId -ClientName $NewClientName -ClientSecret $NewClientSecret
$dmsRoleId = New-OpenIddictRole -RoleName $DmsClientRole
$configRoleId = New-OpenIddictRole -RoleName $ConfigServiceRole
$scopeId = New-OpenIddictScope -ScopeName $ClientScopeName -Description "Full access to EdFi Admin API"
Add-OpenIddictClientRole -AppId $appId.Trim() -RoleId $dmsRoleId.Trim()
Add-OpenIddictClientRole -AppId $appId.Trim() -RoleId $configRoleId.Trim()
Add-OpenIddictApplicationScope -AppId $appId.Trim() -ScopeId $scopeId.Trim()
# Add-OpenIddictCustomClaim -AppId $appId.Trim() -ClaimName $ClaimName -ClaimValue $ClaimValue

Write-Output "OpenIddict client, roles, scope, and claim created successfully."
