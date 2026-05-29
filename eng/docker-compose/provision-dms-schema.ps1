# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    DMS-1151 schema provisioning phase (PostgreSQL-only).
.DESCRIPTION
    Invokes SchemaTools against each distinct effective target database from the
    selected CMS instances. Hard-codes --dialect pgsql; MSSQL keysets are rejected
    by Resolve-TargetDialect with an actionable error. MSSQL targets are out of
    scope for DMS-1151; revisit if/when the bootstrap epic adds MSSQL support in
    a successor story.

    See command-boundaries.md Section 3.5 for the phase contract and
    01-schema-deployment-safety.md for the DMS-1151 story.
#>

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

    return Resolve-LocalSettingsEnvironmentFile -Path $Path -DockerComposeRoot $PSScriptRoot
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

    return Get-EnvValue -EnvValues $EnvValues -Name $Name -DefaultValue $DefaultValue
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
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns a collection of route contexts; the plural noun reflects the return shape.')]
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
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive: $EnvValues is consumed inside the nested regex replacement script block.')]
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
                throw "Connection string references env value '$(Format-LogSafeText $name)', but that key is absent from the environment file."
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

function Resolve-TargetDialect {
    param(
        [hashtable]
        $ConnectionStringParts
    )

    # DMS-916 / DMS-1151 currently provisions only PostgreSQL. Reject MSSQL-style key sets
    # early so an inadvertent provider mismatch surfaces with an actionable error rather
    # than a cryptic SchemaTools failure.
    $mssqlMarkers = @("server", "initial catalog", "user id", "trusted_connection")
    foreach ($marker in $mssqlMarkers) {
        if ($ConnectionStringParts.ContainsKey($marker)) {
            throw "CMS DMS instance connection string uses MSSQL-style key '$(Format-LogSafeText $marker)'. Only PostgreSQL provisioning is supported."
        }
    }

    if (-not $ConnectionStringParts.ContainsKey("host") -and -not $ConnectionStringParts.ContainsKey("server")) {
        throw "CMS DMS instance connection string is missing a host key. Cannot determine the provisioning dialect."
    }

    return "pgsql"
}

function Convert-CmsConnectionStringToHostSideTarget {
    <#
    .SYNOPSIS
    Builds an effective host-side provisioning target from a single CMS-stored DMS instance
    connection string. Translates the Docker-internal PostgreSQL hostname/port to the host-side
    mapped port while preserving the instance-specific username, password, and database name.
    Non-Docker hosts (e.g. external PostgreSQL servers configured per instance) are preserved
    as-is.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $ConnectionString,

        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues
    )

    $parts = Convert-ConnectionStringToHashtable -ConnectionString $ConnectionString
    $dialect = Resolve-TargetDialect -ConnectionStringParts $parts

    $databaseName = ""
    foreach ($key in @("database")) { # initial catalog is an MSSQL marker already rejected by Resolve-TargetDialect
        if ($parts.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$parts[$key])) {
            $databaseName = [string]$parts[$key]
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($databaseName)) {
        throw "CMS DMS instance connection string did not contain a database name."
    }

    $instanceHost = if ($parts.ContainsKey("host")) { [string]$parts["host"] } else { "" }
    # PostgreSQL canonically defaults to 5432 when port is omitted. Docker-internal targets
    # (host=dms-postgresql) keep 5432 here and are translated to localhost:POSTGRES_PORT by
    # the host-side translation block below.
    $instancePort = if ($parts.ContainsKey("port") -and -not [string]::IsNullOrWhiteSpace([string]$parts["port"])) {
        [string]$parts["port"]
    }
    else {
        "5432"
    }
    $instanceUser = if ($parts.ContainsKey("username")) { [string]$parts["username"] }
                    elseif ($parts.ContainsKey("user id")) { [string]$parts["user id"] }
                    else { "" }
    $instancePassword = if ($parts.ContainsKey("password")) { [string]$parts["password"] } else { "" }

    if ([string]::IsNullOrWhiteSpace($instanceHost)) {
        throw "CMS DMS instance connection string is missing the host key."
    }
    if ([string]::IsNullOrWhiteSpace($instanceUser)) {
        throw "CMS DMS instance connection string is missing the username key."
    }

    # Translate Docker-internal PostgreSQL coordinates to host-side coordinates. Any other
    # host (e.g. an external managed PostgreSQL server) is left untouched so per-instance
    # routing is preserved.
    $effectiveHost = $instanceHost
    $effectivePort = $instancePort
    if ($instanceHost.Equals("dms-postgresql", [System.StringComparison]::OrdinalIgnoreCase) -and
        $instancePort -eq "5432") {
        $effectiveHost = "localhost"
        $effectivePort = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "POSTGRES_PORT" -DefaultValue "5432"
    }

    $hostConnectionString = "host=$effectiveHost;port=$effectivePort;username=$instanceUser;password=$instancePassword;database=$databaseName;"

    # TargetKey identifies an effective provisioning target. Two instances pointing at the
    # same physical database (same dialect, translated host, port, database, and user) share
    # a provisioning invocation. Differing on any field - including username - produces a
    # separate target so we never collapse instances that happen to share a database name on
    # different hosts or under different roles.
    $targetKey = ("{0}|{1}|{2}|{3}|{4}" -f
        $dialect,
        $effectiveHost.ToLowerInvariant(),
        $effectivePort,
        $databaseName.ToLowerInvariant(),
        $instanceUser.ToLowerInvariant())

    return [pscustomobject]@{
        Dialect = $dialect
        Host = $effectiveHost
        Port = $effectivePort
        Username = $instanceUser
        DatabaseName = $databaseName
        HostConnectionString = $hostConnectionString
        TargetKey = $targetKey
    }
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
    # CMS encrypts connection strings with AES-CBC / PKCS7; set explicitly rather than relying on .NET defaults.
    $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
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


function Resolve-ProvisionTargetInstances {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the collection of selected target instances; the plural noun reflects the return shape.')]
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
            $matchedInstances = @($Instances | Where-Object { [long](Get-ProvisionProperty -Object $_ -Names @("id", "Id")) -eq [long]$id })
            if ($matchedInstances.Count -eq 0) {
                throw "DMS instance $(Format-LogSafeText $id) was not found in CMS for tenant '$(Format-LogSafeText $Tenant)'."
            }

            $null = $selected.Add($matchedInstances[0])
        }

        return @($selected)
    }

    if ($SchoolYear.Count -gt 0) {
        $selected = [System.Collections.ArrayList]::new()
        foreach ($year in $SchoolYear) {
            $matchedInstances = @($Instances | Where-Object {
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

            if ($matchedInstances.Count -eq 0) {
                throw "No DMS instance found with route context schoolYear=$(Format-LogSafeText $year) for tenant '$(Format-LogSafeText $Tenant)'."
            }

            if ($matchedInstances.Count -gt 1) {
                $ids = ($matchedInstances | ForEach-Object { Get-ProvisionProperty -Object $_ -Names @("id", "Id") }) -join ", "
                throw "Multiple DMS instances found with route context schoolYear=$(Format-LogSafeText $year) (instance ids: $(Format-LogSafeText $ids)). Clean up duplicate CMS state before provisioning."
            }

            $null = $selected.Add($matchedInstances[0])
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
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory provisioning target object; no system state changes and no -WhatIf surface.')]
    param(
        $Instance,

        [hashtable]
        $EnvValues
    )

    $rawInstanceId = Get-ProvisionProperty -Object $Instance -Names @("id", "Id")
    if ($null -eq $rawInstanceId) {
        throw "CMS DMS instance is missing an id."
    }
    $instanceId = [long]$rawInstanceId
    $connectionString = [string](Get-ProvisionProperty -Object $Instance -Names @("connectionString", "ConnectionString"))
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        throw "CMS DMS instance $(Format-LogSafeText $instanceId) does not include a connection string."
    }

    $resolvedConnectionString = Resolve-CmsInstanceConnectionString `
        -ConnectionString $connectionString `
        -EnvValues $EnvValues
    $target = Convert-CmsConnectionStringToHostSideTarget `
        -ConnectionString $resolvedConnectionString `
        -EnvValues $EnvValues
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
        Dialect = $target.Dialect
        Host = $target.Host
        Port = $target.Port
        Username = $target.Username
        DatabaseName = $target.DatabaseName
        HostConnectionString = $target.HostConnectionString
        TargetKey = $target.TargetKey
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

function Get-ProvisionIdeGuidance {
    <#
    .SYNOPSIS
    Builds the IDE next-step guidance block emitted by provision-dms-schema.ps1 after a
    successful pre-start provisioning. Returns a list of guidance lines. Pure / side-effect-
    free so the guidance can be exercised in tests independently of console formatting.
    #>
    param(
        [Parameter(Mandatory)]
        $SchemaWorkspace,

        [object[]]
        $ProvisionedTargets = @()
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("--- Schema Provisioning Summary ---")

    if ($ProvisionedTargets.Count -eq 0) {
        $lines.Add("No DMS instance targets were provisioned (selectors matched zero instances).")
    }
    else {
        $lines.Add("Provisioned $($ProvisionedTargets.Count) database target(s):")
        foreach ($target in $ProvisionedTargets) {
            $instanceList = ($target.InstanceIds | ForEach-Object { [string]$_ }) -join ", "
            $lines.Add("  - database=$(Format-LogSafeText $target.DatabaseName) host=$(Format-LogSafeText $target.Host) port=$(Format-LogSafeText $target.Port) user=$(Format-LogSafeText $target.Username) instance-ids=[$instanceList] status=$($target.Status)")
        }
    }

    $lines.Add("")
    $lines.Add("--- IDE next-step guidance ---")
    $lines.Add("Bootstrap manifest:      $(Format-LogSafeText $SchemaWorkspace.BootstrapManifestPath)")
    $lines.Add("  ApiSchema manifest:    $(Format-LogSafeText $SchemaWorkspace.ApiSchemaManifestPath)")
    if ($SchemaWorkspace.EffectiveSchemaHash) {
        $lines.Add("  Effective schema hash: $(Format-LogSafeText $SchemaWorkspace.EffectiveSchemaHash)")
    }
    $lines.Add("")
    $lines.Add("Expected DMS runtime appsettings for IDE-hosted launch (activation deferred to Story 04 - DMS-1154):")
    $apiSchemaRoot = [System.IO.Path]::GetDirectoryName($SchemaWorkspace.ApiSchemaManifestPath)
    $lines.Add("  AppSettings__UseApiSchemaPath = true")
    $lines.Add("  AppSettings__ApiSchemaPath    = $(Format-LogSafeText $apiSchemaRoot)")
    $lines.Add("Note: bootstrap does not flip these flags. Story 04 owns enabling staged-schema runtime loading;")
    $lines.Add("      until then DMS containers and IDE launches keep using the DLL-backed schema assemblies.")

    return $lines.ToArray()
}

function Test-CmsReadOnlyAccessEnvPresent {
    <#
    .SYNOPSIS
    Returns $true when the env file explicitly supplies at least one of the three
    CONFIG_SERVICE_CLIENT_* keys with a non-blank value. Used to gate the optional
    CMSReadOnlyAccess guidance so defaults alone do not advertise the block as available.
    #>
    param(
        [hashtable]
        $EnvValues
    )

    if ($null -eq $EnvValues) {
        return $false
    }

    foreach ($name in @("CONFIG_SERVICE_CLIENT_ID", "CONFIG_SERVICE_CLIENT_SCOPE", "CONFIG_SERVICE_CLIENT_SECRET")) {
        if ($EnvValues.ContainsKey($name) -and -not [string]::IsNullOrWhiteSpace([string]$EnvValues[$name])) {
            return $true
        }
    }

    return $false
}

function Get-ProvisionCmsReadOnlyAccessGuidance {
    <#
    .SYNOPSIS
    Produces optional CMSReadOnlyAccess guidance lines for IDE-hosted launch. Returns an empty
    array when none of the three CONFIG_SERVICE_CLIENT_* keys are explicitly present in the
    env file. Per command-boundaries.md Section 3.4, "may include" means "include when actually
    populated"; a default-derived client id alone does not satisfy that contract.
    #>
    param(
        [hashtable]
        $EnvValues
    )

    if (-not (Test-CmsReadOnlyAccessEnvPresent -EnvValues $EnvValues)) {
        return @()
    }

    $clientId = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_ID" -DefaultValue "CMSReadOnlyAccess"
    $scope = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_SCOPE" -DefaultValue "edfi_admin_api/readonly_access"
    $secret = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_SECRET"

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("")
    $lines.Add("Configuration Service read-only access (configured by start-local-dms.ps1's identity setup):")
    $lines.Add("  ConfigurationServiceSettings__ClientId = $(Format-LogSafeText $clientId)")
    $lines.Add("  ConfigurationServiceSettings__Scope    = $(Format-LogSafeText $scope)")
    if ([string]::IsNullOrWhiteSpace($secret)) {
        $lines.Add("  ConfigurationServiceSettings__ClientSecret = (set in your IDE/env; not staged by provisioning)")
    }
    else {
        $lines.Add("  ConfigurationServiceSettings__ClientSecret = (present in environment file)")
    }

    return $lines.ToArray()
}

function Write-ProvisionSummary {
    <#
    .SYNOPSIS
    Writes the post-provisioning summary and the IDE next-step guidance derived from the
    staged schema workspace and the targets just provisioned. The Story 04 dependency is
    surfaced explicitly so operators don't assume the staged path is the active runtime path.
    #>
    param(
        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues,

        [Parameter(Mandatory)]
        $SchemaWorkspace,

        [object[]]
        $ProvisionedTargets = @()
    )

    $guidance = Get-ProvisionIdeGuidance -SchemaWorkspace $SchemaWorkspace -ProvisionedTargets $ProvisionedTargets
    foreach ($line in $guidance) {
        Write-Information $line -InformationAction Continue
    }

    foreach ($line in Get-ProvisionCmsReadOnlyAccessGuidance -EnvValues $EnvValues) {
        Write-Information $line -InformationAction Continue
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

    # DMS-1151: bootstrap admin token acquisition. Per command-boundaries.md Section 3.4/Section 3.5,
    # configure-local-dms-instance.ps1 owns the /connect/register side effect for the bootstrap
    # admin client; this phase is auth-consumer only. Client id/secret are resolved through the
    # shared -EnvironmentFile helper so configure and provision always agree on the admin client
    # (DMS_BOOTSTRAP_ADMIN_CLIENT_ID / DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET). If authentication
    # fails, surface an actionable error rather than silently re-registering against a CMS
    # environment where /connect/register may be locked down.
    $bootstrapAdmin = Resolve-BootstrapAdminClient -EnvValues $envValues
    try
    {
        $configToken = Get-CmsToken `
            -CmsUrl $cmsUrl `
            -ClientId $bootstrapAdmin.ClientId `
            -ClientSecret $bootstrapAdmin.ClientSecret
    }
    catch
    {
        throw "Bootstrap admin client '$(Format-LogSafeText $bootstrapAdmin.ClientId)' could not be authenticated against $(Format-LogSafeText $cmsUrl). Run configure-local-dms-instance.ps1 first to register the bootstrap admin client, or refresh its credentials. Underlying error: $(Format-LogSafeText ($_.Exception.Message))"
    }

    $instances = @(Get-DmsInstances -CmsUrl $cmsUrl -AccessToken $configToken -Tenant $tenant -Limit 500)
    if ($instances.Count -ge 500) {
        throw "DMS instance count reached the CMS query page size (500); pagination is not implemented in this bootstrap provisioning path. Reduce CMS instances or implement paging before provisioning."
    }
    $selectedInstances = Resolve-ProvisionTargetInstances `
        -Instances $instances `
        -InstanceId $InstanceId `
        -SchoolYear $SchoolYear `
        -Tenant $tenant

    $targets = @($selectedInstances | ForEach-Object { New-ProvisionTarget -Instance $_ -EnvValues $envValues })
    # Group by the effective provisioning target identity (dialect + translated host + port +
    # database + user). Grouping only by database name would silently collapse two instances
    # that share a database name on different physical hosts or under different users.
    $groups = $targets | Group-Object -Property TargetKey
    $schemaTool = Resolve-DmsSchemaTool -RequestedPath $env:DMS_SCHEMA_TOOL_PATH
    Write-Information "dms-schema resolved: $(Format-LogSafeText $schemaTool)." -InformationAction Continue

    $provisionedTargets = [System.Collections.ArrayList]::new()

    foreach ($group in $groups) {
        $target = @($group.Group)[0]
        $instanceIds = @($group.Group | ForEach-Object { [long]$_.InstanceId })
        $ids = ($instanceIds | ForEach-Object { [string]$_ }) -join ", "
        Write-Information "Provisioning target database $(Format-LogSafeText $target.DatabaseName) on $(Format-LogSafeText $target.Host):$(Format-LogSafeText $target.Port) for instance id(s): $(Format-LogSafeText $ids)." -InformationAction Continue
        Invoke-DmsSchemaProvision `
            -ToolPath $schemaTool `
            -SchemaPaths $schemaPaths `
            -ConnectionString $target.HostConnectionString `
            -DatabaseName $target.DatabaseName

        $null = $provisionedTargets.Add([pscustomobject]@{
            DatabaseName = $target.DatabaseName
            Host = $target.Host
            Port = $target.Port
            Dialect = $target.Dialect
            Username = $target.Username
            InstanceIds = [long[]]$instanceIds
            Status = "Provisioned"
        })
    }

    Write-ProvisionSummary `
        -EnvValues $envValues `
        -SchemaWorkspace $schemaWorkspace `
        -ProvisionedTargets @($provisionedTargets)
}

if ($MyInvocation.InvocationName -eq '.') { return }

Invoke-ProvisionDmsSchema `
    -EnvironmentFile $EnvironmentFile `
    -InstanceId $InstanceId `
    -SchoolYear $SchoolYear
