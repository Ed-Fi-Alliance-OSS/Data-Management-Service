# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[CmdletBinding()]
param(
    [string]$EnvironmentFile,
    [long[]]$InstanceId = @(),
    [int[]]$SchoolYear = @()
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Import-Module "$PSScriptRoot/bootstrap-manifest.psm1" -Force -Global
Import-Module "$PSScriptRoot/bootstrap-schema-tool.psm1" -Force -Global
Import-Module "$PSScriptRoot/bootstrap-schema-workspace.psm1" -Force -Global
Import-Module "$PSScriptRoot/env-utility.psm1" -Force -Global
Import-Module "$PSScriptRoot/../Dms-Management.psm1" -Force

$script:BootstrapDmsInstanceClientId = "dms-instance-admin"
$script:BootstrapDmsInstanceClientSecret = "ValidClientSecret1234567890!Abcd"

if (-not (Get-Command Format-LogSafeText -ErrorAction SilentlyContinue)) {
    function Format-LogSafeText {
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
}

function Resolve-ProvisionEnvironmentFile {
    param(
        [string]
        $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $defaultEnv = Join-Path $PSScriptRoot ".env"
        $fallbackEnv = Join-Path $PSScriptRoot ".env.example"
        $Path = if (Test-Path -LiteralPath $defaultEnv) { $defaultEnv } else { $fallbackEnv }
    }
    elseif (-not [System.IO.Path]::IsPathRooted($Path)) {
        $Path = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Environment file not found: $(Format-LogSafeText $Path)."
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Get-EnvValueOrDefault {
    param(
        [hashtable]
        $EnvValues,

        [string]
        $Name,

        [string]
        $DefaultValue = ""
    )

    if ($EnvValues.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace([string]$EnvValues[$Name])) {
        return [string]$EnvValues[$Name]
    }

    return $DefaultValue
}

function Get-ProvisionProperty {
    param(
        $Object,

        [string[]]
        $Names
    )

    foreach ($name in $Names) {
        if ($Object -is [System.Collections.IDictionary] -and $Object.ContainsKey($name)) {
            return $Object[$name]
        }

        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) {
            return $property.Value
        }
    }

    return $null
}

function Get-ProvisionRouteContexts {
    param(
        $Instance
    )

    $routeContexts = Get-ProvisionProperty -Object $Instance -Names @("dmsInstanceRouteContexts", "DmsInstanceRouteContexts", "routeContexts", "RouteContexts")
    if ($null -eq $routeContexts -or $routeContexts -is [string]) {
        return @()
    }

    return @($routeContexts)
}

function Resolve-EnvPlaceholdersInText {
    param(
        [string]
        $Text,

        [hashtable]
        $EnvValues
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    return [regex]::Replace(
        $Text,
        '\$\{(?<Name>[A-Za-z0-9_]+)\}',
        {
            param($match)

            $name = $match.Groups["Name"].Value
            if (-not $EnvValues.ContainsKey($name)) {
                throw "Connection string references env value '$name', but that key is absent from the environment file."
            }

            return [string]$EnvValues[$name]
        }
    )
}

function Convert-ConnectionStringToHashtable {
    param(
        [string]
        $ConnectionString
    )

    $values = @{}
    foreach ($segment in @($ConnectionString -split ";")) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }

        $parts = $segment.Split("=", 2)
        if ($parts.Length -ne 2) {
            continue
        }

        $values[$parts[0].Trim().ToLowerInvariant()] = $parts[1].Trim()
    }

    return $values
}

function Get-DatabaseNameFromConnectionString {
    param(
        [string]
        $ConnectionString,

        [switch]
        $AllowMissing
    )

    $parts = Convert-ConnectionStringToHashtable -ConnectionString $ConnectionString
    foreach ($key in @("database", "initial catalog")) {
        if ($parts.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$parts[$key])) {
            return [string]$parts[$key]
        }
    }

    if ($AllowMissing) {
        return $null
    }

    throw "CMS DMS instance connection string did not contain a database name."
}

function ConvertFrom-CmsEncryptedConnectionString {
    param(
        [string]
        $ProtectedConnectionString,

        [hashtable]
        $EnvValues
    )

    $encryptionKey = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "DMS_CONFIG_DATABASE_ENCRYPTION_KEY"
    if ([string]::IsNullOrWhiteSpace($encryptionKey)) {
        throw "CMS DMS instance connection string is encrypted, but DMS_CONFIG_DATABASE_ENCRYPTION_KEY is not set in the environment file."
    }

    try {
        $encryptedBytes = [Convert]::FromBase64String($ProtectedConnectionString)
    }
    catch {
        throw "CMS DMS instance connection string did not contain a database name and was not valid CMS encrypted base64."
    }

    if ($encryptedBytes.Length -le 16) {
        throw "CMS DMS instance encrypted connection string payload is invalid."
    }

    $keyText = $encryptionKey.PadRight(32, "0").Substring(0, 32)
    $keyBytes = [System.Text.Encoding]::UTF8.GetBytes($keyText)
    $iv = [byte[]]::new(16)
    [Array]::Copy($encryptedBytes, 0, $iv, 0, 16)

    $cipherText = [byte[]]::new($encryptedBytes.Length - 16)
    [Array]::Copy($encryptedBytes, 16, $cipherText, 0, $cipherText.Length)

    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Key = $keyBytes
        $aes.IV = $iv
        $decryptor = $aes.CreateDecryptor()
        try {
            $plainTextBytes = $decryptor.TransformFinalBlock($cipherText, 0, $cipherText.Length)
            return [System.Text.Encoding]::UTF8.GetString($plainTextBytes)
        }
        finally {
            $decryptor.Dispose()
        }
    }
    catch {
        throw "CMS DMS instance encrypted connection string could not be decrypted with DMS_CONFIG_DATABASE_ENCRYPTION_KEY."
    }
    finally {
        $aes.Dispose()
    }
}

function Resolve-CmsInstanceConnectionString {
    param(
        [string]
        $ConnectionString,

        [hashtable]
        $EnvValues
    )

    $resolvedConnectionString = Resolve-EnvPlaceholdersInText -Text $ConnectionString -EnvValues $EnvValues
    if ($null -ne (Get-DatabaseNameFromConnectionString -ConnectionString $resolvedConnectionString -AllowMissing)) {
        return $resolvedConnectionString
    }

    $decryptedConnectionString = ConvertFrom-CmsEncryptedConnectionString `
        -ProtectedConnectionString $resolvedConnectionString `
        -EnvValues $EnvValues

    return Resolve-EnvPlaceholdersInText -Text $decryptedConnectionString -EnvValues $EnvValues
}

function New-HostSidePostgresConnectionString {
    param(
        [hashtable]
        $EnvValues,

        [string]
        $DatabaseName
    )

    $port = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "POSTGRES_PORT" -DefaultValue "5432"
    $user = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "POSTGRES_USER" -DefaultValue "postgres"
    $password = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "POSTGRES_PASSWORD"

    return "host=localhost;port=$port;username=$user;password=$password;database=$DatabaseName;"
}

function Resolve-ProvisionTargetInstances {
    param(
        [object[]]
        $Instances,

        [long[]]
        $InstanceId = @(),

        [int[]]
        $SchoolYear = @(),

        [string]
        $Tenant = ""
    )

    if ($InstanceId.Count -gt 0 -and $SchoolYear.Count -gt 0) {
        throw "-InstanceId and -SchoolYear are mutually exclusive. Pass only one selector."
    }

    if ($InstanceId.Count -gt 0) {
        $selected = [System.Collections.ArrayList]::new()
        foreach ($id in $InstanceId) {
            $matches = @($Instances | Where-Object { [long](Get-ProvisionProperty -Object $_ -Names @("id", "Id")) -eq [long]$id })
            if ($matches.Count -eq 0) {
                throw "DMS instance $(Format-LogSafeText $id) was not found in CMS for tenant '$(Format-LogSafeText $Tenant)'."
            }

            $null = $selected.Add($matches[0])
        }

        return @($selected)
    }

    if ($SchoolYear.Count -gt 0) {
        $selected = [System.Collections.ArrayList]::new()
        foreach ($year in $SchoolYear) {
            $matches = @($Instances | Where-Object {
                $matched = $false
                foreach ($routeContext in Get-ProvisionRouteContexts -Instance $_) {
                    $contextKey = [string](Get-ProvisionProperty -Object $routeContext -Names @("contextKey", "ContextKey"))
                    $contextValue = [string](Get-ProvisionProperty -Object $routeContext -Names @("contextValue", "ContextValue"))
                    if ($contextKey -eq "schoolYear" -and $contextValue -eq [string]$year) {
                        $matched = $true
                        break
                    }
                }
                $matched
            })

            if ($matches.Count -eq 0) {
                throw "No DMS instance found with route context schoolYear=$(Format-LogSafeText $year) for tenant '$(Format-LogSafeText $Tenant)'."
            }

            if ($matches.Count -gt 1) {
                $ids = ($matches | ForEach-Object { Get-ProvisionProperty -Object $_ -Names @("id", "Id") }) -join ", "
                throw "Multiple DMS instances found with route context schoolYear=$(Format-LogSafeText $year) (instance ids: $(Format-LogSafeText $ids)). Clean up duplicate CMS state before provisioning."
            }

            $null = $selected.Add($matches[0])
        }

        return @($selected)
    }

    if ($Instances.Count -eq 0) {
        throw "No DMS instances found in CMS. Run configure-local-dms-instance.ps1 before provisioning schemas."
    }

    if ($Instances.Count -gt 1) {
        $listing = ($Instances | ForEach-Object {
            "id=$(Format-LogSafeText (Get-ProvisionProperty -Object $_ -Names @('id', 'Id'))) name=$(Format-LogSafeText (Get-ProvisionProperty -Object $_ -Names @('instanceName', 'InstanceName')))"
        }) -join "`n"
        throw "Multiple DMS instances exist; cannot auto-select. Pass -InstanceId or -SchoolYear to target specific instances:`n$listing"
    }

    return @($Instances[0])
}

function New-ProvisionTarget {
    param(
        $Instance,

        [hashtable]
        $EnvValues
    )

    $instanceId = [long](Get-ProvisionProperty -Object $Instance -Names @("id", "Id"))
    $connectionString = [string](Get-ProvisionProperty -Object $Instance -Names @("connectionString", "ConnectionString"))
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        throw "CMS DMS instance $(Format-LogSafeText $instanceId) does not include a connection string."
    }

    $resolvedConnectionString = Resolve-CmsInstanceConnectionString `
        -ConnectionString $connectionString `
        -EnvValues $EnvValues
    $databaseName = Get-DatabaseNameFromConnectionString -ConnectionString $resolvedConnectionString
    $routeContexts = @(
        Get-ProvisionRouteContexts -Instance $Instance | ForEach-Object {
            [pscustomobject]@{
                ContextKey = [string](Get-ProvisionProperty -Object $_ -Names @("contextKey", "ContextKey"))
                ContextValue = [string](Get-ProvisionProperty -Object $_ -Names @("contextValue", "ContextValue"))
            }
        }
    )

    return [pscustomobject]@{
        InstanceId = $instanceId
        RouteContexts = $routeContexts
        DatabaseName = $databaseName
        NormalizedDatabaseName = $databaseName.ToLowerInvariant()
        HostConnectionString = New-HostSidePostgresConnectionString -EnvValues $EnvValues -DatabaseName $databaseName
    }
}

function Invoke-DmsSchemaProvision {
    param(
        [string]
        $ToolPath,

        [string[]]
        $SchemaPaths,

        [string]
        $ConnectionString,

        [string]
        $DatabaseName
    )

    $arguments = @("ddl", "provision")
    foreach ($schemaPath in $SchemaPaths) {
        $arguments += @("--schema", $schemaPath)
    }
    $arguments += @(
        "--connection-string", $ConnectionString,
        "--dialect", "pgsql",
        "--create-database"
    )

    Write-Information "Invoking dms-schema ddl provision for database $(Format-LogSafeText $DatabaseName) with $($SchemaPaths.Count) schema file(s)." -InformationAction Continue

    if ($ToolPath.EndsWith(".ps1", [System.StringComparison]::OrdinalIgnoreCase)) {
        & pwsh -NoLogo -NoProfile -File $ToolPath @arguments
    }
    else {
        & $ToolPath @arguments
    }

    $exitCode = $LASTEXITCODE
    Write-Information "dms-schema exit code for database $(Format-LogSafeText $DatabaseName): $(Format-LogSafeText $exitCode)." -InformationAction Continue
    if ($exitCode -ne 0) {
        throw "dms-schema ddl provision failed for database $(Format-LogSafeText $DatabaseName) with exit code $(Format-LogSafeText $exitCode)."
    }
}

function Invoke-ProvisionDmsSchema {
    param(
        [string]
        $EnvironmentFile,

        [long[]]
        $InstanceId = @(),

        [int[]]
        $SchoolYear = @()
    )

    if ($InstanceId.Count -gt 0 -and $SchoolYear.Count -gt 0) {
        throw "-InstanceId and -SchoolYear are mutually exclusive. Pass only one selector."
    }

    $resolvedEnvironmentFile = Resolve-ProvisionEnvironmentFile -Path $EnvironmentFile
    $envValues = ReadValuesFromEnvFile -EnvironmentFile $resolvedEnvironmentFile
    $cmsUrl = Resolve-CmsBaseUrl -EnvValues $envValues
    $tenant = Get-EnvValueOrDefault -EnvValues $envValues -Name "CONFIG_SERVICE_TENANT"

    $schemaWorkspace = Resolve-BootstrapSchemaWorkspace
    $schemaPaths = [string[]]@($schemaWorkspace.CoreSchemaPath) + [string[]]@($schemaWorkspace.ExtensionSchemaPaths)
    Write-Information "Schema workspace ready. Core schema: $(Format-LogSafeText $schemaWorkspace.CoreSchemaPath). Extensions: $($schemaWorkspace.ExtensionSchemaPaths.Count)." -InformationAction Continue

    Add-CmsClient `
        -CmsUrl $cmsUrl `
        -ClientId $script:BootstrapDmsInstanceClientId `
        -ClientSecret $script:BootstrapDmsInstanceClientSecret `
        -DisplayName "DMS Instance Setup Administrator"

    $configToken = Get-CmsToken `
        -CmsUrl $cmsUrl `
        -ClientId $script:BootstrapDmsInstanceClientId `
        -ClientSecret $script:BootstrapDmsInstanceClientSecret

    $instances = @(Get-DmsInstances -CmsUrl $cmsUrl -AccessToken $configToken -Tenant $tenant)
    $selectedInstances = Resolve-ProvisionTargetInstances `
        -Instances $instances `
        -InstanceId $InstanceId `
        -SchoolYear $SchoolYear `
        -Tenant $tenant

    $targets = @($selectedInstances | ForEach-Object { New-ProvisionTarget -Instance $_ -EnvValues $envValues })
    $groups = $targets | Group-Object -Property NormalizedDatabaseName
    $schemaTool = Resolve-DmsSchemaTool -RequestedPath $env:DMS_SCHEMA_TOOL_PATH
    Write-Information "dms-schema resolved: $(Format-LogSafeText $schemaTool)." -InformationAction Continue

    foreach ($group in $groups) {
        $target = @($group.Group)[0]
        $ids = ($group.Group | ForEach-Object { $_.InstanceId }) -join ", "
        Write-Information "Provisioning target database $(Format-LogSafeText $target.DatabaseName) for instance id(s): $(Format-LogSafeText $ids)." -InformationAction Continue
        Invoke-DmsSchemaProvision `
            -ToolPath $schemaTool `
            -SchemaPaths $schemaPaths `
            -ConnectionString $target.HostConnectionString `
            -DatabaseName $target.DatabaseName
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }

Invoke-ProvisionDmsSchema `
    -EnvironmentFile $EnvironmentFile `
    -InstanceId $InstanceId `
    -SchoolYear $SchoolYear
