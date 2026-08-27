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
    # The two role names CMS enforces are supported overrides: local-config.yml maps
    # IdentitySettings:ConfigServiceRole / :ClientRole from DMS_CONFIG_IDENTITY_SERVICE_ROLE /
    # DMS_CONFIG_IDENTITY_CLIENT_ROLE, and BOTH identity setup scripts -- setup-keycloak.ps1 and
    # setup-openiddict.ps1 -InsertData -- fall back to their own cms-client / dms-client defaults when
    # the parameters are omitted. Resolved once, above the provider branch, so neither provider
    # registers clients against a role the configured CMS does not require: that registration
    # succeeds and tokens mint, and the failure surfaces later as DMS unable to read claim sets. The
    # defaults are the compose file's own fallbacks, so an unset override resolves exactly as before.
    $identityRoleParams = @{
        ConfigServiceRole = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "DMS_CONFIG_IDENTITY_SERVICE_ROLE" -DefaultValue "cms-client"
        DmsClientRole     = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "DMS_CONFIG_IDENTITY_CLIENT_ROLE" -DefaultValue "dms-client"
    }
    if($IdentityProvider -eq "keycloak")
    {
        # Create client with default edfi_admin_api/full_access scope
        ./setup-keycloak.ps1 @identityRoleParams -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-keycloak.ps1 @identityRoleParams -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-keycloak.ps1 @identityRoleParams -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access"
    }
    elseif ($IdentityProvider -eq "self-contained")
    {
    	Write-Output "Starting self-contained initialization script..."
        Write-Output "Init db public and private keys for OpenIddict..."
        $dbType = if ($datastore -eq "mssql") { "MSSQL" } else { "Postgresql" }
        # POSTGRES_USER is a supported override - postgresql.yml passes ${POSTGRES_USER:-postgres}
        # to the container - so the PostgreSQL superuser is resolved with the same Compose precedence
        # the container saw rather than assumed. The compose file's own fallback is the default here,
        # so an unset override resolves exactly as before. SQL Server has no equivalent override:
        # mssql.yml authenticates as the fixed sa account.
        $dbUser = if ($datastore -eq "mssql") { "sa" } else { Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "POSTGRES_USER" -DefaultValue "postgres" }
        $dbPort = if ($datastore -eq "mssql") { "ENV:MSSQL_PORT" } else { "ENV:POSTGRES_PORT" }
        ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile -DbType $dbType -DbUser $dbUser -DbPort $dbPort
        # Create client with default edfi_admin_api/full_access scope
        ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile -DbType $dbType -DbUser $dbUser -DbPort $dbPort

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile -DbType $dbType -DbUser $dbUser -DbPort $dbPort

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile -DbType $dbType -DbUser $dbUser -DbPort $dbPort
    }
}
