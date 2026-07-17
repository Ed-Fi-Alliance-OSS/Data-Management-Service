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

    # Resolve the effective Configuration Service database once for both identity providers: the
    # self-contained OpenIddict initialization and the SQL Server connection materialization below both
    # target the database docker-compose resolves for CMS.
    $configTarget = Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues

    # docker-compose resolves the CMS connection as ${DMS_CONFIG_DATABASE_CONNECTION_STRING:-<fallback>},
    # and the fallback in local-config.yml is a hardcoded PostgreSQL connection to dms-postgresql. On a
    # SQL Server stack that fallback is the wrong engine. Unlike the full-stack lanes (which compose the
    # .env.mssql overlay into a derived env file), this lane reads the raw env file, so materialize a
    # SQL Server connection when none is set - for BOTH identity providers (self-contained OpenIddict
    # initializes SQL Server while CMS would otherwise get the PostgreSQL fallback; Keycloak's CMS
    # EnsureDatabase likewise). Exporting it as a process value gives it docker-compose precedence over
    # the --env-file fallback; setup-openiddict.ps1 targets the database via -DbType/-DbName, not this
    # string, so the two cannot diverge.
    $materializedCmsConnectionString = Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName $configTarget.DatabaseName

    # Snapshot the caller's DMS_CONFIG_DATABASE_CONNECTION_STRING before exporting the materialized SQL
    # Server value below. The export lands in the PROCESS environment (so docker-compose reads it with
    # shell-over---env-file precedence) and would otherwise outlive this script: a later invocation in the
    # same shell treats an already-set connection string as a caller-authored override, reusing the prior
    # database, carrying a SQL Server connection into a PostgreSQL run, or slipping past the database-name-
    # only agreement guard when the names match. The finally block restores the prior state on every exit
    # path - success, throw, or teardown - so the export never leaks.
    $cmsConnectionStringSnapshot = Get-ProcessEnvironmentVariableSnapshot -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING"
    try {
        if ($materializedCmsConnectionString) {
            $env:DMS_CONFIG_DATABASE_CONNECTION_STRING = $materializedCmsConnectionString
            Write-Output "No SQL Server configuration connection string was set; using one targeting database '$($configTarget.DatabaseName)' (the compose fallback is PostgreSQL-only)."
        }

        # Docker Compose gives the caller's shell environment precedence over --env-file for every ${...}, so
        # a shell-exported connection string, configuration-database name, or (when the fallback resolves
        # through it) POSTGRES_DB_NAME would point the Configuration Service at a different database than the
        # selected topology - and this must be caught for BOTH identity providers before CMS boots. In
        # self-contained mode setup-openiddict.ps1 initializes one database while CMS would connect to
        # another; in Keycloak mode CMS's EnsureDatabase silently CREATES and uses the shell-redirected
        # database, violating the topology. A wrong-engine shell connection is likewise rejected here. Run the
        # guard before starting the database, including when the env file omits the connection string (compose
        # then uses its fallback database). This lane passes the RAW env file (no derived
        # DMS_CONFIG_DATABASE_NAME literal), so -ConfigDatabaseNameNotMaterialized tells the guard to model
        # compose re-resolving the seam with shell precedence and catch an override (e.g. POSTGRES_DB_NAME)
        # the connection string routes through.
        $guardArgs = @{ ExpectedDatabaseName = $configTarget.DatabaseName; EnvValues = $envValues }
        # DatastoreKey guards the compose fallback's datastore tail (${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}}).
        # When a SQL Server connection was materialized above, compose uses THAT (not the fallback), so the
        # key is moot - passing it would wrongly reject a stray shell POSTGRES_DB_NAME that affects nothing in
        # this config-only stack (no DMS datastore container, and the fallback is overridden).
        if ($configTarget.DatastoreKey -and -not $materializedCmsConnectionString) { $guardArgs.DatastoreKey = $configTarget.DatastoreKey }
        Assert-ConfigDatabaseProcessEnvironmentAgreement @guardArgs -ConfigDatabaseNameNotMaterialized

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
            $dbType = if ($datastore -eq "mssql") { "MSSQL" } else { "Postgresql" }
            $dbUser = if ($datastore -eq "mssql") { "sa" } else { "postgres" }
            $dbPort = if ($datastore -eq "mssql") { "ENV:MSSQL_PORT" } else { "ENV:POSTGRES_PORT" }
            # OpenIddict targets the same Configuration Service database docker-compose resolves for CMS
            # ($configTarget, resolved above for both identity providers): a caller-authored connection string
            # is authoritative across every env shape (including config-only E2E stacks that name the CMS
            # database via POSTGRES_DB_NAME regardless of engine); when it is absent, compose uses its fallback
            # database, which the SQL Server materialization above and this name derivation agree on - rather
            # than setup-openiddict.ps1's POSTGRES_DB_NAME default (which ignores DMS_CONFIG_DATABASE_NAME).
            $identityDbArgs = @{ DbName = $configTarget.DatabaseName }

            # setup-openiddict.ps1 connects to the SQL Server container as 'sa'. That container's SA password
            # is docker-compose's ${MSSQL_SA_PASSWORD:-...} (mssql.yml), which gives a shell export precedence
            # over the env file, and the materialized CMS connection above embeds that same effective value.
            # setup-openiddict.ps1 would otherwise resolve DbPassword from the env-file map alone, so a shell
            # override would leave -InitDb (pre-CMS) and the -InsertData calls authenticating with the wrong
            # password against a shell-overridden container. Pass the effective password so both agree with the
            # container. PostgreSQL self-contained connects via container-local psql without a password, so
            # this is SQL Server only.
            if ($datastore -eq "mssql") {
                $identityDbArgs.DbPassword = Resolve-EffectiveMssqlSaPassword -EnvValues $envValues
            }

            # The process-environment agreement guard already ran above (for both identity providers) before
            # the database started, so the effective Configuration Service database is confirmed to match the
            # one OpenIddict initializes here.
            ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile -DbType $dbType -DbUser $dbUser -DbPort $dbPort @identityDbArgs
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
            ./setup-openiddict.ps1 -InsertData -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile -DbType $dbType -DbUser $dbUser -DbPort $dbPort @identityDbArgs

            # Create client with edfi_admin_api/readonly_access scope
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile -DbType $dbType -DbUser $dbUser -DbPort $dbPort @identityDbArgs

            # Create client with edfi_admin_api/authMetadata_readonly_access scope
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile -DbType $dbType -DbUser $dbUser -DbPort $dbPort @identityDbArgs
        }
    }
    finally {
        # Restore DMS_CONFIG_DATABASE_CONNECTION_STRING to its pre-export state so the materialized value
        # cannot leak into a later invocation in the same shell (see the snapshot rationale above).
        Restore-ProcessEnvironmentVariable -Snapshot $cmsConnectionStringSnapshot
    }
}
