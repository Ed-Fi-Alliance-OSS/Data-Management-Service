# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param (
    # Stop services instead of starting them
    [Switch]
    $d,

    # Delete volumes after stopping services
    [Switch]
    $v,

    # Environment file
    [string]
    $EnvironmentFile = "./.env",

    # Force a rebuild
    [Switch]
    $r,

    # Identity provider type
    [string]
    [ValidateSet("keycloak", "self-contained")]
    $IdentityProvider="self-contained"
)

Import-Module ./env-utility.psm1 -Force
$envValues = ReadValuesFromEnvFile $EnvironmentFile
$datastore = if ($envValues["DMS_CONFIG_DATASTORE"]) { $envValues["DMS_CONFIG_DATASTORE"] } else { "postgresql" }
$databaseComposeFile = if ($datastore -eq "mssql") { "mssql.yml" } else { "postgresql.yml" }

$files = @(
    "-f",
    $databaseComposeFile,
    "-f",
    "local-config.yml",
    "-f",
    "keycloak.yml"
)

if ($d) {
    if ($v) {
        Write-Output "Shutting down with volume delete"
        docker compose $files -p cs-local down -v
    }
    else {
        Write-Output "Shutting down"
        docker compose $files -p cs-local down
    }
}
else {

    $existingNetwork = docker network ls --filter name="dms" -q
    if (! $existingNetwork) {
        docker network create dms
    }

    $upArgs = @(
        "--detach"
    )
    if ($r) {
        Write-Output "Building images with no cache (this may take a few minutes)..."
        docker compose $files --env-file $EnvironmentFile -p cs-local build --no-cache
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to build images. Exit code $LASTEXITCODE"
        }
    }
    # Identity provider configuration
    $identityClientSecrets = Resolve-IdentityClientSecretConfiguration -EnvValues $envValues
    $env:DMS_CONFIG_IDENTITY_PROVIDER=$IdentityProvider
    Write-Output "Identity Provider $IdentityProvider"
    if($IdentityProvider -eq "keycloak")
    {
        $env:OAUTH_TOKEN_ENDPOINT = $envValues.KEYCLOAK_OAUTH_TOKEN_ENDPOINT
        $env:DMS_JWT_AUTHORITY = $envValues.KEYCLOAK_DMS_JWT_AUTHORITY
        $env:DMS_JWT_METADATA_ADDRESS = $envValues.KEYCLOAK_DMS_JWT_METADATA_ADDRESS
        $env:DMS_CONFIG_IDENTITY_AUTHORITY = $envValues.KEYCLOAK_DMS_JWT_AUTHORITY
    }
    elseif ($IdentityProvider -eq "self-contained") {
        $env:OAUTH_TOKEN_ENDPOINT = $envValues.SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT
        $env:DMS_JWT_AUTHORITY = $envValues.SELF_CONTAINED_DMS_JWT_AUTHORITY
        $env:DMS_JWT_METADATA_ADDRESS = $envValues.SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS
        $env:DMS_CONFIG_IDENTITY_AUTHORITY = $envValues.SELF_CONTAINED_DMS_JWT_AUTHORITY
    }

    # Resolve the effective configuration database name once for both identity providers (the topology
    # resolution). It is the target the runtime contract below validates and materializes against.
    $configTarget = Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues

    # Snapshot the caller's DMS_CONFIG_DATABASE_CONNECTION_STRING before the contract may export a
    # materialized value below. The export lands in the PROCESS environment (docker-compose gives shell
    # state precedence over --env-file) and would otherwise outlive this script: a later invocation in the
    # same shell would treat an already-set connection string as a caller-authored override. The finally
    # block restores the prior state on every exit path - success, throw, or teardown - so it never leaks.
    $cmsConnectionStringSnapshot = Get-ProcessEnvironmentVariableSnapshot -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING"
    try {
        # Resolve the effective Configuration Service runtime contract once, for BOTH identity providers,
        # before any Docker action. It enforces engine agreement (a shell DMS_CONFIG_DATASTORE cannot differ
        # from the engine the env file selects, which starts the Compose database and OpenIddict), the
        # connection engine/database invariant, and datastore-name agreement - so neither self-contained
        # OpenIddict nor Keycloak's EnsureDatabase can be pointed at a wrong engine or database. The
        # standalone lane reads the RAW env file (DMS_CONFIG_DATABASE_NAME is not materialized to a literal)
        # and omits the SA-password default so it fails fast rather than embedding a guessed credential.
        $contract = Resolve-EffectiveConfigRuntimeContract -EnvValues $envValues -InfrastructureEngine $datastore -ConfigDatabaseName $configTarget.DatabaseName

        # On a SQL Server stack with no connection string, Compose would substitute the PostgreSQL-only
        # compose-file fallback, so the contract materialized a SQL Server connection targeting the
        # configuration database. Export it so Compose reads it (shell over --env-file); setup-openiddict.ps1
        # targets the database via -DbType/-DbName, not this string, so the two cannot diverge.
        if ($contract.CmsConnectionString.Source -eq "Materialized") {
            $env:DMS_CONFIG_DATABASE_CONNECTION_STRING = $contract.CmsConnectionString.Value
            Write-Output "No SQL Server configuration connection string was set; using one targeting database '$($contract.CmsDatabaseName)' (the compose fallback is PostgreSQL-only)."
        }

        # Self-contained OpenIddict initialization must run before the Configuration Service starts, so
        # start the database first and guarded-create the CMS database and OpenIddict key store before
        # CMS boots (mirrors start-local-dms.ps1 / start-published-dms.ps1). CMS then finds its database
        # and signing key on first boot instead of relying on its own EnsureDatabase to create it.
        Write-Output "Starting database..."
        docker compose $files --env-file $EnvironmentFile -p cs-local up --detach --wait db
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to start the database container, with exit code $LASTEXITCODE."
        }

        if ($IdentityProvider -eq "self-contained")
        {
            Write-Output "Init db public and private keys for OpenIddict..."
            # Every OpenIddict parameter comes from the one runtime contract, so -InitDb (pre-CMS) and the
            # -InsertData calls below target exactly the engine, database, and SA credential Compose uses for
            # CMS - resolved once, above, for both identity providers. PostgreSQL self-contained connects via
            # container-local psql without a password, so DbPassword is present only for SQL Server.
            $identityDbArgs = @{
                DbType = $contract.OpenIddict.DbType
                DbUser = $contract.OpenIddict.DbUser
                DbPort = $contract.OpenIddict.DbPort
                DbName = $contract.OpenIddict.DbName
            }
            if ($contract.OpenIddict.DbPassword) {
                $identityDbArgs.DbPassword = $contract.OpenIddict.DbPassword
            }

            ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile @identityDbArgs
        }

        Write-Output "Starting locally-built DMS config service"

        docker compose $files --env-file $EnvironmentFile -p cs-local up $upArgs

        if ($LASTEXITCODE -ne 0) {
            throw "Unable to start local Docker environment, with exit code $LASTEXITCODE."
        }

        # Database readiness is enforced by the compose healthcheck (the config service
        # depends_on the db with condition service_healthy, and `docker compose up`
        # does not return until that dependency is satisfied). This sleep only covers
        # Keycloak and config-service warmup before the setup scripts run.
        Start-Sleep 25
        if($IdentityProvider -eq "keycloak")
        {
            # Create client with default edfi_admin_api/full_access scope
            ./setup-keycloak.ps1 -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength

            # Create client with edfi_admin_api/readonly_access scope
            ./setup-keycloak.ps1 -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength

            # Create client with edfi_admin_api/authMetadata_readonly_access scope
            ./setup-keycloak.ps1 -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access"
        }
        elseif ($IdentityProvider -eq "self-contained")
        {
            Write-Output "Starting self-contained initialization script..."
            # -InitDb already created the database and OpenIddict key store before CMS started (above);
            # register the OpenIddict clients now that CMS has deployed its dmscs schema. Reuses the
            # engine-aware parameters computed above.
            # Create client with default edfi_admin_api/full_access scope
            ./setup-openiddict.ps1 -InsertData -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbArgs

            # Create client with edfi_admin_api/readonly_access scope
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbArgs

            # Create client with edfi_admin_api/authMetadata_readonly_access scope
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile @identityDbArgs
        }
    }
    finally {
        # Restore DMS_CONFIG_DATABASE_CONNECTION_STRING to its pre-export state so the materialized value
        # cannot leak into a later invocation in the same shell (see the snapshot rationale above).
        Restore-ProcessEnvironmentVariable -Snapshot $cmsConnectionStringSnapshot
    }
}
