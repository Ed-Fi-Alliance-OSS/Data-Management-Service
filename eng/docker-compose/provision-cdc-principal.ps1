# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Creates the database principal the Debezium connector authenticates as, in the instance
    database a CDC binding will capture from.
.DESCRIPTION
    Provider setup grants this principal its capture access; it never creates it. The PostgreSQL
    publication provider issues GRANT CONNECT/USAGE/SELECT against it, and the SQL Server provider
    adds it to the binding's gating role and throws
    'CDC SQL Server connector database principal is missing.' outright when it is absent. So the
    principal has to exist before `cdc enable` runs, and creating it is a deployment act rather
    than something the control plane may do for itself.

    This runs only under the CDC opt-in, so a run that never enables CDC creates no login it will
    not use. It is idempotent: an existing principal is left exactly as it is, including its
    password, because rotating a password here would break a connector already registered against
    it.

    The principal is deliberately NOT the administrative login. Debezium would then read the source
    as a superuser, and on SQL Server `sa` resolves to `dbo`, which cannot be added to a database
    role at all - so the gating-role grant provider setup depends on could not be applied.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The connector and SA passwords are read as plaintext from the environment file and handed to psql/sqlcmd on docker exec, where they are visible in host-side argv regardless; SecureString adds no protection across that boundary. Consistent with provision-e2e-database.ps1.')]
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$EnvironmentFile = "./.env",

    # The instance database the binding captures from. The principal is created as a server-level
    # login (SQL Server) or cluster role (PostgreSQL), and on SQL Server additionally as a user in
    # this database, which is the scope the gating-role membership is granted in.
    [Parameter(Mandatory)]
    [string]$DatabaseName,

    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine = "postgresql",

    [string]$PostgresContainerName = "dms-postgresql",

    [string]$MssqlContainerName = "dms-mssql"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force
# Shared safe-name guard, so a database name that reaches the statements below has already been
# judged by the same rule the provisioning scripts apply.
Import-Module (Join-Path $PSScriptRoot "database-safety.psm1") -Force

function Get-CdcPrincipalPostgresqlSql {
    <#
    .SYNOPSIS
        The PostgreSQL statement that creates the connector role when it is absent.

    .DESCRIPTION
        REPLICATION is required: the connector reads a logical replication slot. Every other
        attribute is withheld - the role is not a superuser and cannot create databases or roles -
        because its whole job is to decode the publication provider setup grants it.

        Returned as a pure string so the statement shape is unit testable without a live server.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $PrincipalName,

        [Parameter(Mandatory)]
        [string]
        $Password
    )

    $principalLiteral = $PrincipalName.Replace("'", "''")
    $passwordLiteral = $Password.Replace("'", "''")

    return @"
DO `$cdc_principal`$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '$principalLiteral') THEN
        EXECUTE format(
            'CREATE ROLE %I WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD %L',
            '$principalLiteral',
            '$passwordLiteral'
        );
    END IF;
END
`$cdc_principal`$;
"@
}

function Get-CdcPrincipalMssqlLoginSql {
    <#
    .SYNOPSIS
        The SQL Server statement that creates the connector login when it is absent.

    .DESCRIPTION
        CHECK_POLICY = OFF keeps a local stack from being refused by the container's password
        policy; this login exists only for a development instance database.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $PrincipalName,

        [Parameter(Mandatory)]
        [string]
        $Password
    )

    $principalIdentifier = "[" + $PrincipalName.Replace("]", "]]") + "]"
    $principalLiteral = $PrincipalName.Replace("'", "''")
    $passwordLiteral = $Password.Replace("'", "''")

    return @"
IF SUSER_ID(N'$principalLiteral') IS NULL
    CREATE LOGIN $principalIdentifier WITH PASSWORD = '$passwordLiteral', CHECK_POLICY = OFF;
"@
}

function Get-CdcPrincipalMssqlUserSql {
    <#
    .SYNOPSIS
        The SQL Server statement that maps the connector login into the instance database.

    .DESCRIPTION
        A separate batch from the login because the gating-role membership provider setup adds is a
        database-scoped grant on this user, and the database is selected by the invocation rather
        than by a USE inside the batch.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $PrincipalName
    )

    $principalIdentifier = "[" + $PrincipalName.Replace("]", "]]") + "]"
    $principalLiteral = $PrincipalName.Replace("'", "''")

    return @"
IF USER_ID(N'$principalLiteral') IS NULL
    CREATE USER $principalIdentifier FOR LOGIN $principalIdentifier;
"@
}

$environmentFilePath = Resolve-LocalSettingsEnvironmentFile -Path $EnvironmentFile -DockerComposeRoot $PSScriptRoot
$envValues = ReadValuesFromEnvFile $environmentFilePath
$connectorPrincipal = Get-CdcConnectorPrincipalConfiguration -EnvValues $envValues

Assert-SafeDatabaseName -DatabaseName $DatabaseName

if (-not $PSCmdlet.ShouldProcess($connectorPrincipal.PrincipalName, "Create the CDC connector database principal")) {
    return
}

Write-Output "Creating the CDC connector database principal '$($connectorPrincipal.PrincipalName)' for '$DatabaseName' ($DatabaseEngine)..."

if ($DatabaseEngine -eq "mssql") {
    $saPassword = Get-EnvValue -EnvValues $envValues -Name "MSSQL_SA_PASSWORD" -DefaultValue "abcdefgh1!"

    $loginSql = Get-CdcPrincipalMssqlLoginSql `
        -PrincipalName $connectorPrincipal.PrincipalName `
        -Password $connectorPrincipal.Password

    & docker exec -e "SQLCMDPASSWORD=$saPassword" $MssqlContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -Q $loginSql -C -b
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create the CDC connector login '$($connectorPrincipal.PrincipalName)'."
    }

    $userSql = Get-CdcPrincipalMssqlUserSql -PrincipalName $connectorPrincipal.PrincipalName

    & docker exec -e "SQLCMDPASSWORD=$saPassword" $MssqlContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d $DatabaseName -Q $userSql -C -b
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to map the CDC connector login '$($connectorPrincipal.PrincipalName)' into '$DatabaseName'."
    }
}
else {
    $postgresUser = Get-EnvValue -EnvValues $envValues -Name "POSTGRES_USER" -DefaultValue "postgres"

    $roleSql = Get-CdcPrincipalPostgresqlSql `
        -PrincipalName $connectorPrincipal.PrincipalName `
        -Password $connectorPrincipal.Password

    # Issued against `postgres` rather than the instance database: a PostgreSQL role is
    # cluster-scoped, and provider setup grants it CONNECT on the instance database itself.
    & docker exec $PostgresContainerName psql -U $postgresUser -d postgres -v ON_ERROR_STOP=1 -c $roleSql
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create the CDC connector role '$($connectorPrincipal.PrincipalName)'."
    }
}

Write-Output "CDC connector database principal '$($connectorPrincipal.PrincipalName)' is present."
