# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Engine-dispatched database-template restore phase. Restore-DatabaseTemplate resolves the
# effective database engine (PostgreSQL or SQL Server), the target database name, and the
# Minimal|Populated template package for a composed bootstrap environment file, then restores
# that package into the running database container via Template-Management.psm1's
# Restore-TemplatePackage. It is invoked by the bootstrap wrappers (bootstrap-local-dms.ps1 /
# bootstrap-published-dms.ps1) as an alternative to schema provisioning: the restored database
# already carries the effective schema, so DMS startup validates it against the effective
# schema hash instead of provisioning DDL.

function Format-LogSafeText {
    <#
    .SYNOPSIS
    Sanitizes a string for safe logging by allowing only safe characters (letters, digits,
    space, and _-.:/). Values sourced from the environment file - package ids, database names,
    connection-string fragments - are external data and must be sanitized before they reach
    Write-Information. Mirrors the Format-LogSafeText helper in provision-dms-schema.ps1.
    #>
    param($Value)

    if ($null -eq $Value) { return "" }
    $text = [string]$Value
    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $text.ToCharArray()) {
        if ([char]::IsLetterOrDigit($character) -or
            $character -eq " " -or
            $character -eq "_" -or
            $character -eq "-" -or
            $character -eq "." -or
            $character -eq ":" -or
            $character -eq "/") {
            $null = $builder.Append($character)
        }
    }

    return $builder.ToString()
}

function Get-EnvValueOrDefault {
    param(
        [hashtable]$EnvValues,
        [string]$Name,
        [string]$DefaultValue = ""
    )

    return Get-EnvValue -EnvValues $EnvValues -Name $Name -DefaultValue $DefaultValue
}

function Resolve-RestoreDatabaseEngine {
    <#
    .SYNOPSIS
    Resolves the database engine ("postgresql" or "mssql") the restore phase targets from the
    composed environment file.

    .DESCRIPTION
    DMS_DATASTORE is the primary signal, matching Resolve-ExpectedProvisioningDialect in
    provision-dms-schema.ps1. When it is absent or blank, falls back to connection-string-shape
    detection on DATABASE_CONNECTION_STRING_ADMIN, using the same definitive-marker precedence
    as Resolve-TargetDialect in provision-dms-schema.ps1: host/username/port/sslmode identify
    PostgreSQL; server/data source/initial catalog/user id/trusted_connection identify SQL
    Server. Defaults to "postgresql" when neither signal is present, matching local-dms.yml's
    compose-level default (AppSettings__Datastore: ${DMS_DATASTORE:-postgresql}).
    #>
    param(
        [hashtable]$EnvValues
    )

    $engineValue = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "DMS_DATASTORE"
    if (-not [string]::IsNullOrWhiteSpace($engineValue)) {
        $normalizedEngine = $engineValue.Trim().ToLowerInvariant()
        if ($normalizedEngine -eq "mssql") { return "mssql" }
        if ($normalizedEngine -eq "postgresql") { return "postgresql" }
    }

    $adminConnectionString = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "DATABASE_CONNECTION_STRING_ADMIN"
    if (-not [string]::IsNullOrWhiteSpace($adminConnectionString)) {
        try {
            $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
            $builder.set_ConnectionString($adminConnectionString)

            foreach ($marker in @("host", "username", "port", "sslmode")) {
                if ($builder.ContainsKey($marker)) { return "postgresql" }
            }

            foreach ($marker in @("server", "data source", "initial catalog", "user id", "trusted_connection")) {
                if ($builder.ContainsKey($marker)) { return "mssql" }
            }
        }
        catch {
            # Not a parseable connection string; fall through to the default below.
            Write-Verbose "Admin connection string is not parseable; defaulting to postgresql."
        }
    }

    return "postgresql"
}

function Resolve-RestoreDatabaseName {
    <#
    .SYNOPSIS
    Resolves the target database name from the same engine-specific env key
    configure-local-data-store.ps1 uses: POSTGRES_DB_NAME for PostgreSQL, MSSQL_DB_NAME for
    SQL Server. Both default to "edfi_datamanagementservice", matching that script's defaults.
    #>
    param(
        [hashtable]$EnvValues,

        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql"
    )

    if ($DatabaseEngine -eq "mssql") {
        return Get-EnvValueOrDefault -EnvValues $EnvValues -Name "MSSQL_DB_NAME" -DefaultValue "edfi_datamanagementservice"
    }

    return Get-EnvValueOrDefault -EnvValues $EnvValues -Name "POSTGRES_DB_NAME" -DefaultValue "edfi_datamanagementservice"
}

function Resolve-RestoreTemplatePackageId {
    <#
    .SYNOPSIS
    Resolves the DATABASE_TEMPLATE_PACKAGE id to restore, with its Minimal|Populated template
    segment swapped to match -RestoreTemplate.

    .DESCRIPTION
    DATABASE_TEMPLATE_PACKAGE is already engine-correct for the composed environment file
    (Resolve-DatabaseEngineEnvironmentFile rewrites its engine segment when composing the MSSQL
    overlay), so only the template segment is swapped here via Convert-TemplatePackageToken.
    When the env file does not set DATABASE_TEMPLATE_PACKAGE, falls back to the historical
    PostgreSQL Minimal default and rewrites both its engine and template segments to match the
    resolved engine and the requested template.
    #>
    param(
        [hashtable]$EnvValues,

        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql",

        [Parameter(Mandatory)]
        [ValidateSet("Minimal", "Populated")]
        [string]$RestoreTemplate
    )

    $packageId = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "DATABASE_TEMPLATE_PACKAGE"
    if ([string]::IsNullOrWhiteSpace($packageId)) {
        $defaultPackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
        Write-Information "Environment variable DATABASE_TEMPLATE_PACKAGE is not set. Falling back to default package: $(Format-LogSafeText $defaultPackageId)." -InformationAction Continue
        $engineToken = if ($DatabaseEngine -eq "mssql") { "MsSql" } else { "PostgreSql" }
        return Convert-TemplatePackageToken -PackageId $defaultPackageId -Engine $engineToken -Template $RestoreTemplate
    }

    return Convert-TemplatePackageToken -PackageId $packageId -Template $RestoreTemplate
}

function Restore-DatabaseTemplate {
    <#
    .SYNOPSIS
    Restores a Minimal or Populated database-template package into the running database
    container for a composed bootstrap environment file.

    .DESCRIPTION
    Reads the composed environment file to resolve the effective database engine
    (Resolve-RestoreDatabaseEngine), the target database name (Resolve-RestoreDatabaseName),
    and the template package id with its Minimal|Populated segment swapped to -RestoreTemplate
    (Resolve-RestoreTemplatePackageId). The package is downloaded via Get-NugetPackage unless
    -PackageDirectory points at an already-extracted package (used for pre-publish validation
    and tests), then restored into the target database via Template-Management.psm1's
    Restore-TemplatePackage, which drops and recreates the database (PostgreSQL: .sql dump;
    SQL Server: .bak backup).

    Invoked by the bootstrap wrappers as an alternative to schema provisioning: the restored
    database already carries the effective schema, so DMS startup validates it against the
    effective schema hash without any additional DDL work.

    .PARAMETER EnvironmentFile
    Path to the composed bootstrap environment file.

    .PARAMETER RestoreTemplate
    Which built-in template to restore: "Minimal" or "Populated".

    .PARAMETER PackageDirectory
    Directory already containing the extracted template .nupkg. When omitted, the package is
    downloaded via Get-NugetPackage.

    .EXAMPLE
    Restore-DatabaseTemplate -EnvironmentFile ./.env -RestoreTemplate Populated
    #>
    param(
        [Parameter(Mandatory)]
        [string]$EnvironmentFile,

        [Parameter(Mandatory)]
        [ValidateSet("Minimal", "Populated")]
        [string]$RestoreTemplate,

        [string]$PackageDirectory
    )

    Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force

    $envValues = ReadValuesFromEnvFile -EnvironmentFile $EnvironmentFile
    $databaseEngine = Resolve-RestoreDatabaseEngine -EnvValues $envValues
    $databaseName = Resolve-RestoreDatabaseName -EnvValues $envValues -DatabaseEngine $databaseEngine
    $packageId = Resolve-RestoreTemplatePackageId -EnvValues $envValues -DatabaseEngine $databaseEngine -RestoreTemplate $RestoreTemplate

    Write-Information "Restoring $RestoreTemplate database template for database '$(Format-LogSafeText $databaseName)' (engine: $databaseEngine) using package '$(Format-LogSafeText $packageId)'." -InformationAction Continue

    $resolvedPackageDirectory =
        if (-not [string]::IsNullOrWhiteSpace($PackageDirectory)) {
            $PackageDirectory
        }
        else {
            Import-Module (Join-Path $PSScriptRoot "../Package-Management.psm1") -Force
            Get-NugetPackage -PackageName $packageId -PreRelease
        }

    Import-Module (Join-Path $PSScriptRoot "../DatabaseTemplates/Template-Management.psm1") -Force

    $restoreArgs = @{
        PackageDirectory = $resolvedPackageDirectory
        DatabaseName = $databaseName
        DatabaseEngine = $databaseEngine
    }
    if ($databaseEngine -eq "mssql") {
        $restoreArgs.MssqlPassword = Get-EnvValueOrDefault -EnvValues $envValues -Name "MSSQL_SA_PASSWORD" -DefaultValue "abcdefgh1!"
    }

    $restoredPackageName = Restore-TemplatePackage @restoreArgs

    Write-Information "Database template restored successfully from package '$(Format-LogSafeText $restoredPackageName)'." -InformationAction Continue
}

Export-ModuleMember -Function Restore-DatabaseTemplate
