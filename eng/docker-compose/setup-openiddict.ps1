# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Bootstrap script intentionally writes operator progress and SQL diagnostics to the console.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive: the script parameters are consumed inside the nested helper functions, and this rule does not track usage across function scopes.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'DbPassword', Justification = 'Carries an ENV: indirection sentinel resolved from the .env file and handed to sqlcmd, which requires plaintext; SecureString adds no protection across that boundary.')]
[CmdletBinding()]
param (
    [string] $DbType = "Postgresql", # or "MSSQL"
    [string] $ConnectionString = "Host=localhost;Port=5435;Database=edfi_datamanagementservice;Username=postgres;",
    [string] $EnvironmentFile = "./.env",
    [string] $PostgresContainerName = "dms-postgresql",
    [string] $MssqlContainerName = "dms-mssql",
    [string] $DbPassword = "ENV:MSSQL_SA_PASSWORD",
    [string] $DbHost = "localhost",
    [string] $DbPort = "ENV:POSTGRES_PORT",
    [string] $DbName = "ENV:POSTGRES_DB_NAME",
    [string] $DbUser = "postgres",
    [string] $NewClientId = "DmsConfigurationService",
    [string] $NewClientName = "DMS Configuration Service",
    [string] $NewClientSecret = "ValidClientSecret1234567890!Abcd",
    [int] $ClientSecretMinimumLength = 32,
    [int] $ClientSecretMaximumLength = 128,
    [string] $DmsClientRole = "dms-client",
    [string] $ConfigServiceRole = "cms-client",
    [string] $ClientScopeName = "edfi_admin_api/full_access",
    [string] $ClaimName = "namespacePrefixes",
    [string] $ClaimValue = "http://ed-fi.org",
    [string] $EncryptionKey = "ENV:DMS_CONFIG_IDENTITY_ENCRYPTION_KEY",
    [int] $TokenLifespan = 1800,
    [switch] $InitDb = $false,
    [switch] $InsertData = $false,
    [string] $HashIterations = "ENV:DMS_CONFIG_IDENTITY_HASHING_ITERATIONS"
)
Import-Module ./env-utility.psm1
Import-Module ./OpenIddict-Crypto.psm1

$script:DbType = $DbType
$script:ConnectionString = $ConnectionString
$script:PostgresContainerName = $PostgresContainerName
$script:DbHost = $DbHost
$script:DbPort = $DbPort
$script:DbName = $DbName
$script:DbUser = $DbUser
$script:ClientSecretMinimumLength = $ClientSecretMinimumLength
$script:ClientSecretMaximumLength = $ClientSecretMaximumLength
$script:EncryptionKey = $EncryptionKey
$script:HashIterations = $HashIterations

Write-Verbose "TokenLifespan is not applied by setup-openiddict.ps1; OpenIddict token lifetime is configured by the service. Requested value: $TokenLifespan"

$envValues = $null
if ($EnvironmentFile) {
    $envValues = ReadValuesFromEnvFile $EnvironmentFile
}

function Get-ScalarResult {
    param($Result)
    if ($DbType -eq "MSSQL") {
        return ($Result | Where-Object { $_ -and $_.Trim() -ne '' } | Select-Object -First 1).Trim()
    }
    return $Result[2]
}

function New-OpenIddictRole {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper is invoked non-interactively against a local setup database.')]
    param([string]$RoleName)
    $roleId = [guid]::NewGuid().ToString()
    if ($DbType -eq "MSSQL") {
        $sqlInsert = "IF NOT EXISTS (SELECT 1 FROM dmscs.OpenIddictRole WHERE Name = '$RoleName') INSERT INTO dmscs.OpenIddictRole (Id, Name) VALUES ('$roleId', '$RoleName');"
    }
    else {
        $sqlInsert = @"
INSERT INTO "dmscs"."OpenIddictRole" ("Id", "Name")
VALUES ('$roleId', '$RoleName')
ON CONFLICT ON CONSTRAINT "UX_OpenIddictRole_Name" DO NOTHING
RETURNING "Id";
"@
    }
    Invoke-DbQuery $sqlInsert | Out-Null

    if ($DbType -eq "MSSQL") {
        $sqlSelect = "SELECT Id FROM dmscs.OpenIddictRole WHERE Name = '$RoleName';"
    }
    else {
        $sqlSelect = @"
SELECT "Id" FROM "dmscs"."OpenIddictRole" WHERE "Name" = '$RoleName';
"@
    }
    $existing = Invoke-DbQuery $sqlSelect
    return Get-ScalarResult $existing
}

function New-OpenIddictScope {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper is invoked non-interactively against a local setup database.')]
    param([string]$ScopeName, [string]$Description)
    $scopeId = [guid]::NewGuid().ToString()
    if ($DbType -eq "MSSQL") {
        $sqlInsert = "IF NOT EXISTS (SELECT 1 FROM dmscs.OpenIddictScope WHERE Name = '$ScopeName') INSERT INTO dmscs.OpenIddictScope (Id, Name, Description) VALUES ('$scopeId', '$ScopeName', '$Description');"
    }
    else {
        $sqlInsert = @"
INSERT INTO "dmscs"."OpenIddictScope" ("Id", "Name", "Description")
VALUES ('$scopeId', '$ScopeName', '$Description')
ON CONFLICT ON CONSTRAINT "UX_OpenIddictScope_Name" DO NOTHING
RETURNING "Id";
"@
    }
    Invoke-DbQuery $sqlInsert | Out-Null
    if ($DbType -eq "MSSQL") {
        $sqlSelect = "SELECT Id FROM dmscs.OpenIddictScope WHERE Name = '$ScopeName';"
    }
    else {
        $sqlSelect = @"
SELECT "Id" FROM "dmscs"."OpenIddictScope" WHERE "Name" = '$ScopeName';
"@
    }
    $existing = Invoke-DbQuery $sqlSelect
    return Get-ScalarResult $existing
}

function New-OpenIddictApplication {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper is invoked non-interactively against a local setup database.')]
    param([string]$ClientId, [string]$ClientName, [string]$ClientSecret)
    $appId = [guid]::NewGuid().ToString()
    $iterations = [int32](Resolve-EnvValue $script:HashIterations)
    # Hash the client secret using ASP.NET password hashing
    $hashedSecret = New-AspNetPasswordHash -PlainTextSecret $ClientSecret -Iterations $iterations
    if ($DbType -eq "MSSQL") {
        $sqlInsert = "IF NOT EXISTS (SELECT 1 FROM dmscs.OpenIddictApplication WHERE ClientId = '$ClientId') INSERT INTO dmscs.OpenIddictApplication (Id, ClientId, ClientSecret, DisplayName, Type) VALUES ('$appId', '$ClientId', '$hashedSecret', '$ClientName', 'confidential');"
    }
    else {
        $sqlInsert = @"
INSERT INTO "dmscs"."OpenIddictApplication" ("Id", "ClientId", "ClientSecret", "DisplayName", "Type")
VALUES ('$appId', '$ClientId', '$hashedSecret', '$ClientName', 'confidential')
ON CONFLICT ON CONSTRAINT "UX_OpenIddictApplication_ClientId" DO NOTHING
RETURNING "Id";
"@
    }
    Invoke-DbQuery $sqlInsert | Out-Null

    if ($DbType -eq "MSSQL") {
        $sqlSelect = "SELECT Id FROM dmscs.OpenIddictApplication WHERE ClientId = '$ClientId';"
    }
    else {
        $sqlSelect = @"
SELECT "Id" FROM "dmscs"."OpenIddictApplication" WHERE "ClientId" = '$ClientId';
"@
    }
    $existing = Invoke-DbQuery $sqlSelect
    return Get-ScalarResult $existing
}

function Test-ClientSecretLength {
    param([string]$ClientSecret)

    if ($ClientSecret.Length -lt $script:ClientSecretMinimumLength -or $ClientSecret.Length -gt $script:ClientSecretMaximumLength) {
        throw "NewClientSecret must be between $($script:ClientSecretMinimumLength) and $($script:ClientSecretMaximumLength) characters long."
    }
}

function Test-ClientSecretComplexity {
    param([string]$ClientSecret)

    $complexityPattern = '^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[!@#$%^&*()\-_=\+\[\]{}:;,.?]).{' + $script:ClientSecretMinimumLength + ',' + $script:ClientSecretMaximumLength + '}$'

    if ($ClientSecret -notmatch $complexityPattern) {
        throw "NewClientSecret must contain at least one lowercase letter, one uppercase letter, one number, and one special character from !@#$%^&*()-_=+[]{}:;,.? and must be between $($script:ClientSecretMinimumLength) and $($script:ClientSecretMaximumLength) characters long."
    }
}

function Add-OpenIddictClientRole {
    param([string]$AppId, [string]$RoleId)
    if ($DbType -eq "MSSQL") {
        $sql = "IF NOT EXISTS (SELECT 1 FROM dmscs.OpenIddictClientRole WHERE ClientId = '$AppId' AND RoleId = '$RoleId') INSERT INTO dmscs.OpenIddictClientRole (ClientId, RoleId) VALUES ('$AppId', '$RoleId');"
    }
    else {
        $sql = @"
INSERT INTO "dmscs"."OpenIddictClientRole" ("ClientId", "RoleId")
VALUES ('$AppId', '$RoleId')
ON CONFLICT ON CONSTRAINT "PK_OpenIddictClientRole" DO NOTHING;
"@
    }
    Invoke-DbQuery $sql
}

function Add-OpenIddictApplicationScope {
    param([string]$AppId, [string]$ScopeId)
    if ($DbType -eq "MSSQL") {
        $sql = "IF NOT EXISTS (SELECT 1 FROM dmscs.OpenIddictApplicationScope WHERE ApplicationId = '$AppId' AND ScopeId = '$ScopeId') INSERT INTO dmscs.OpenIddictApplicationScope (ApplicationId, ScopeId) VALUES ('$AppId', '$ScopeId');"
    }
    else {
        $sql = @"
INSERT INTO "dmscs"."OpenIddictApplicationScope" ("ApplicationId", "ScopeId")
VALUES ('$AppId', '$ScopeId')
ON CONFLICT ON CONSTRAINT "PK_OpenIddictApplicationScope" DO NOTHING;
"@
    }
    Invoke-DbQuery $sql
}

function Add-OpenIddictCustomClaim {
    param([string]$AppId, [string]$ClaimName, [string]$ClaimValue)
    if (-not $ClaimValue) {
        Write-Host "ClaimValue is empty, skipping claim addition."
        return
    }

    if ($DbType -eq "MSSQL") {
        $sql = @"
UPDATE dmscs.OpenIddictApplication
SET ProtocolMappers = JSON_MODIFY(
    COALESCE(ProtocolMappers, '[]'),
    'append $',
    JSON_QUERY('{"claim.name":"$ClaimName","claim.value":"$ClaimValue","jsonType.label":"String"}')
)
WHERE Id = '$AppId';
"@
        Invoke-DbQuery -Sql $sql
        return
    }

    # Use PostgreSQL jsonb_build functions to avoid shell escaping issues entirely
    # This builds the equivalent of [{"claim.name": "...", "claim.value": "...", "jsonType.label": "String"}]
    $sql = @"
UPDATE "dmscs"."OpenIddictApplication"
SET "ProtocolMappers" = COALESCE("ProtocolMappers", '[]'::jsonb) ||
    jsonb_build_array(
        jsonb_build_object(
            'claim.name', '$ClaimName',
            'claim.value', '$ClaimValue',
            'jsonType.label', 'String'
        )
    )
WHERE "Id" = '$AppId';
"@
    Invoke-DbQuery -Sql $sql
}

function Update-OpenIddictApplicationPermissions {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper is invoked non-interactively against a local setup database.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Function updates the OpenIddict permissions collection column.')]
    param([string]$AppId, [string]$Scope)
    if ($DbType -eq "MSSQL") {
        $sql = @"
UPDATE dmscs.OpenIddictApplication
SET Permissions = '["$Scope"]'
WHERE Id = '$AppId';
"@
        Invoke-DbQuery $sql
        return
    }

    $permissionsString = "{$Scope}" -replace "'", "''"
    $sql = @"
UPDATE "dmscs"."OpenIddictApplication"
SET "Permissions" = '$permissionsString'
WHERE "Id" = '$AppId';
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
        [string]$DbUser
    )
    $DbHost = Resolve-EnvValue $DbHost
    $DbPort = Resolve-EnvValue $DbPort
    $DbName = Resolve-EnvValue $DbName
    $DbUser = Resolve-EnvValue $DbUser
    if ($DbType -eq "Postgresql") {
        return "Host=$DbHost;Port=$DbPort;Database=$DbName;Username=$DbUser;"
    }
    elseif ($DbType -eq "MSSQL") {
        return "Server=$DbHost,$DbPort;Database=$DbName;User Id=$DbUser;"
    }
    else {
        throw "Unsupported DbType: $DbType"
    }
}

function Get-EffectiveConnectionString {
    param(
        [string]$ConnectionString,
        [string]$DbType,
        [string]$DbHost,
        [string]$DbPort,
        [string]$DbName,
        [string]$DbUser
    )
    # If EnvironmentFile is set, always use DB param group and ignore ConnectionString
    if ($EnvironmentFile) {
        return Build-ConnectionString -DbType $DbType -DbHost $DbHost -DbPort $DbPort -DbName $DbName -DbUser $DbUser
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
        return Build-ConnectionString -DbType $DbType -DbHost $DbHost -DbPort $DbPort -DbName $DbName -DbUser $DbUser
    }
    # Otherwise, use the provided ConnectionString
    return $ConnectionString
}

function Invoke-DbQuery {
    param(
        [string]$Sql,
        [switch]$Debug,
        [switch]$UseMasterDatabase
    )

    if ($Debug) {
        Write-Host "Debug: Raw SQL to execute:" -ForegroundColor Yellow
        Write-Host $Sql -ForegroundColor Gray
    }

    $effectiveConnectionString = Get-EffectiveConnectionString -ConnectionString $script:ConnectionString -DbType $script:DbType -DbHost $script:DbHost -DbPort $script:DbPort -DbName $script:DbName -DbUser $script:DbUser
    if ($script:DbType -eq "Postgresql") {
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

        if (-not [string]::IsNullOrEmpty($script:PostgresContainerName)) {
            Write-Verbose "Executing psql in container: $($script:PostgresContainerName)"
            docker exec $script:PostgresContainerName psql -U $user -d $db -c $Sql
        }
        else {
            Write-Verbose "Executing psql against host: $dbHost"
            if ($port) {
                psql -h $dbHost -p $port -U $user -d $db -c $Sql
            }
            else {
                psql -h $dbHost -U $user -d $db -c $Sql
            }
        }

        if ($LASTEXITCODE -ne 0) {
            throw "PostgreSQL command failed with exit code $LASTEXITCODE."
        }
    }
    elseif ($script:DbType -eq "MSSQL") {
        $params = @{}
        foreach ($pair in $effectiveConnectionString -split ';') {
            if ($pair -match '=') {
                $kv = $pair -split '=', 2
                $params[$kv[0].Trim()] = $kv[1].Trim()
            }
        }
        $db = if ($UseMasterDatabase) { 'master' } else { $params['Database'] }
        $user = $params['User Id']
        $password = Resolve-EnvValue $DbPassword

        Write-Verbose "Executing sqlcmd against $MssqlContainerName"
        # Invoke docker directly (no Invoke-Expression) so the SQL travels as one
        # argument with no shell re-parsing; -b makes sqlcmd exit nonzero on SQL
        # errors so failures throw instead of leaking error text into results;
        # -I sets QUOTED_IDENTIFIER ON, required by XML data type methods.
        $output = docker exec $MssqlContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U $user -P $password -d $db -C -b -I -h -1 -W -Q $Sql 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "sqlcmd failed (exit $LASTEXITCODE): $output"
        }
        return $output
    }
    else {
        Write-Error "Unsupported database type: $($script:DbType)"
    }
}

# Main logic
function Invoke-InitDbScripts {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Function runs a sequence of database initialization scripts.')]
    param()

    Write-Host "InitDb specified: running database initialization scripts..."

    if ($DbType -eq "MSSQL") {
        Write-Host "Create database if not exists"
        $dbName = Resolve-EnvValue $DbName
        Invoke-DbQuery -UseMasterDatabase "IF DB_ID(N'$dbName') IS NULL CREATE DATABASE [$dbName];"

        Write-Host "Create schema if not exists: dmscs"
        Invoke-DbQuery "IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'dmscs') EXEC('CREATE SCHEMA dmscs');"

        Write-Host "Create table for OpenIddict keys if not exists: dmscs.OpenIddictKey"
        Invoke-DbQuery @'
IF OBJECT_ID('dmscs.OpenIddictKey', 'U') IS NULL
BEGIN
    CREATE TABLE dmscs.OpenIddictKey (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        KeyId NVARCHAR(64) NOT NULL,
        PublicKey VARBINARY(MAX) NOT NULL,
        PrivateKey VARBINARY(MAX) NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(256),
        LastModifiedAt DATETIME2,
        ModifiedBy NVARCHAR(256),
        ExpiresAt DATETIME2,
        IsActive BIT NOT NULL DEFAULT 1
    );
END;
'@

        # Generate and output OpenIddictKey insert SQL
        Write-Host "Generating OpenIddictKey insert statement..."
        $encryptionKey = Resolve-EnvValue $EncryptionKey
        $keyInsertSql = New-OpenIddictKeyInsertSql -EncryptionKey $encryptionKey -DbType $DbType
        Write-Host "Insert public and private keys into dmscs.OpenIddictKey"
        Invoke-DbQuery $keyInsertSql
        Write-Host "Database initialization scripts completed."
        return
    }

    # Run embedded SQL script contents
    Write-Host "Create schema if not exists: dmscs"
    Invoke-DbQuery @'
CREATE SCHEMA IF NOT EXISTS "dmscs";
'@

    Write-Host "Create table for OpenIddict keys if not exists: ""dmscs"".""OpenIddictKey"""
    Invoke-DbQuery @'
CREATE TABLE IF NOT EXISTS "dmscs"."OpenIddictKey" (
    "Id" SERIAL,
    "KeyId" VARCHAR(64) NOT NULL,
    "PublicKey" BYTEA NOT NULL, -- binary format for public key
    "PrivateKey" TEXT NOT NULL, -- encrypted with pgcrypto
    "CreatedAt" TIMESTAMP NOT NULL DEFAULT NOW(),
    "CreatedBy" VARCHAR(256),
    "LastModifiedAt" TIMESTAMP,
    "ModifiedBy" VARCHAR(256),
    "ExpiresAt" TIMESTAMP,
    "IsActive" BOOLEAN NOT NULL DEFAULT TRUE
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'PK_OpenIddictKey'
          AND conrelid = '"dmscs"."OpenIddictKey"'::regclass
    ) THEN
        ALTER TABLE "dmscs"."OpenIddictKey" ADD CONSTRAINT "PK_OpenIddictKey" PRIMARY KEY ("Id");
    END IF;
END$$;
'@

    Write-Host "Create extension if not exists: pgcrypto"
    Invoke-DbQuery @'
CREATE EXTENSION IF NOT EXISTS pgcrypto;
'@
    # Generate and output OpenIddictKey insert SQL
    Write-Host "Generating OpenIddictKey insert statement..."
    $encryptionKey = Resolve-EnvValue $script:EncryptionKey
    $keyInsertSql = New-OpenIddictKeyInsertSql -EncryptionKey $encryptionKey
    Write-Host "Insert public and private keys into ""dmscs"".""OpenIddictKey"""
    Invoke-DbQuery $keyInsertSql
    Write-Host "Database initialization scripts completed."
}

if ($InitDb) {
    Invoke-InitDbScripts
}

if ($InsertData) {
    Test-ClientSecretLength -ClientSecret $NewClientSecret
    Test-ClientSecretComplexity -ClientSecret $NewClientSecret
    $appId = New-OpenIddictApplication -ClientId $NewClientId -ClientName $NewClientName -ClientSecret $NewClientSecret

    $dmsRoleId = New-OpenIddictRole -RoleName $DmsClientRole
    Add-OpenIddictClientRole -AppId $appId.Trim() -RoleId $dmsRoleId.Trim()
    $configRoleId = New-OpenIddictRole -RoleName $ConfigServiceRole
    Add-OpenIddictClientRole -AppId $appId.Trim() -RoleId $configRoleId.Trim()

    $scopeId = New-OpenIddictScope -ScopeName $ClientScopeName -Description $ClientScopeName
    Add-OpenIddictApplicationScope -AppId $appId.Trim() -ScopeId $scopeId.Trim()
    Update-OpenIddictApplicationPermissions -AppId $appId.Trim() -Scope  $ClientScopeName
    Add-OpenIddictCustomClaim -AppId $appId.Trim() -ClaimName $ClaimName -ClaimValue $ClaimValue

    Write-Output "OpenIddict client, roles, scope, and claim created successfully."
}
