# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.DESCRIPTION
Building blocks for the Ed-Fi API Publisher snapshot interoperability harness
(eng/verification/Invoke-PublisherSnapshotInteropCheck.ps1).

The functions fall in two groups. The pure ones (name generation, environment-file shaping, manifest
shaping, endpoint resolution, scenario resolution) touch neither Docker nor the network and are what
the Docker-free unit tests exercise. The rest drive the local Docker stack through the existing
eng/docker-compose scripts and eng/Dms-Management.psm1 helpers rather than reimplementing them.

Credentials (client keys and secrets) are returned to the caller in memory so a later step can hand
them to the Publisher at run time. Nothing in this module writes a key, secret, token, or connection
string to disk: the run manifest is built from an explicit allow-list of fields and then scanned.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).Path
$script:DockerComposeDirectory = Join-Path $script:RepositoryRoot "eng/docker-compose"
$script:OverlayComposeFile = Join-Path $PSScriptRoot "interop.compose.yml"
$script:PublishedOverlayComposeFile = Join-Path $PSScriptRoot "interop-published.compose.yml"

Import-Module (Join-Path $script:RepositoryRoot "eng/Dms-Management.psm1") -Force
Import-Module (Join-Path $script:DockerComposeDirectory "env-utility.psm1") -Force
Import-Module (Join-Path $script:DockerComposeDirectory "database-safety.psm1") -Force

# The Publisher image the harness validates, pinned by digest. Tags move; the digest does not.
$script:DefaultPublisherImage = "edfialliance/ods-api-publisher:v1.4.0@sha256:cacbeff911a5484bea98d09ea4eebdc6301ae9e12511c0e505318ea190d4d59c"

# Values the harness raises above the stack defaults. The same values are written in
# interop.compose.yml (the cache and rate limit) so the overlay stays a plain compose file.
$script:DataStoreCacheExpirationSeconds = 10
$script:RatePermitLimit = 100000
$script:ConfigurationServiceImageRepository = "edfialliance/ed-fi-api-configuration-service"
$script:SelectionLoggerName = "EdFi.DataManagementService.Core.Middleware.SelectEffectiveDataStoreTargetMiddleware"
$script:PostgresMaxConnections = 300

# Education organizations every harness client is authorized for. Later stages create their schools
# with these ids so the seeded clients can reach them.
$script:EducationOrganizationIds = @(930190101, 930190102, 930190103)

# The stack hardcodes these container names and the compose project, so only one can exist at a time.
$script:ComposeProjectName = "dms-local"
$script:PublishedComposeProjectName = "dms-published"
$script:StackContainerNames = @("ed-fi-api", "ed-fi-api-config-service", "dms-postgresql", "dms-mssql")

# Scenarios that have an implementation. Later stages add their names here as they are built.
$script:ImplementedScenarios = @("StackOnly", "Isolated", "NoSnapshot")
$script:AllScenarios = @("Isolated", "NoSnapshot")

# Snapshot arrangement. The schemas are the ones the data store database holds (checked against a live
# bootstrap); the read-only login is granted USAGE and SELECT on each.
$script:SnapshotSchemas = @("dms", "edfi", "tpdm", "auth", "tracked_changes_edfi", "tracked_changes_tpdm")
$script:MssqlDataDirectory = "/var/opt/mssql/data"
$script:SnapshotReaderPrefix = "dms_snapshot_reader"
$script:SeedRetryLimit = 3

$script:SecretPropertyPattern = "(?i)secret|password|token|connection|credential|apikey|clientkey|^key$"
$script:SecretValuePattern = "(?i)(password|pwd)\s*=|bearer\s+[a-z0-9._-]{8,}"

#region Pure helpers

<#
.SYNOPSIS
    Returns the default pinned Publisher image reference.
#>
function Get-InteropDefaultPublisherImage {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    return $script:DefaultPublisherImage
}

<#
.SYNOPSIS
    Returns the digest of a pinned image reference, or an empty string for an unpinned tag.
#>
function Get-InteropImageDigest {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Image
    )

    if ($Image -match "@(sha256:[0-9a-f]{64})$") {
        return $Matches[1]
    }

    return ""
}

<#
.SYNOPSIS
    Builds the run folder name, a UTC timestamp followed by the engine.
#>
function Get-InteropRunFolderName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [datetime]$TimestampUtc,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine
    )

    return "{0}-{1}" -f $TimestampUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"), $Engine
}

<#
.SYNOPSIS
    Builds the token that makes every name in one invocation unique: a UTC stamp plus a random suffix.

.DESCRIPTION
    The Configuration Service rejects duplicate vendor, application, and data store names, and a second
    invocation against the same stack must not collide with the first. The suffix covers two
    invocations in the same second.
#>
function New-InteropRunToken {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure string factory despite the New- verb; it creates no system state.')]
    param(
        [datetime]$TimestampUtc = [datetime]::UtcNow,

        [string]$Suffix = ([guid]::NewGuid().ToString("N").Substring(0, 4))
    )

    return "{0}-{1}" -f $TimestampUtc.ToUniversalTime().ToString("yyyyMMddHHmmss"), $Suffix.ToLowerInvariant()
}

<#
.SYNOPSIS
    Builds a unique display name for a vendor, application, or data store created by one invocation.
#>
function Get-InteropResourceName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$RunToken,

        [Parameter(Mandatory)]
        [string]$Role
    )

    return "interop-$RunToken-$Role"
}

<#
.SYNOPSIS
    Builds the database name for a data store created by one invocation.
#>
function Get-InteropDatabaseName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$RunToken,

        [Parameter(Mandatory)]
        [string]$Role
    )

    $safeRole = ($Role -replace "[^A-Za-z0-9]", "_").ToLowerInvariant()
    $safeToken = ($RunToken -replace "[^A-Za-z0-9]", "_").ToLowerInvariant()
    return "edfi_interop_${safeToken}_$safeRole"
}

<#
.SYNOPSIS
    Applies key overrides to the lines of an environment file.

.DESCRIPTION
    An existing uncommented assignment of a key is replaced in place (every occurrence, so a later
    duplicate cannot win over the override); a key with none is appended. Commented lines are left
    alone. The input lines are never modified.
#>
function ConvertTo-InteropEnvironmentContent {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$BaseLine,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Override
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $applied = [System.Collections.Generic.HashSet[string]]::new()

    foreach ($line in $BaseLine) {
        $replacement = $null
        foreach ($key in $Override.Keys) {
            if ($line -match ("^\s*(export\s+)?{0}\s*=" -f [regex]::Escape([string]$key))) {
                $replacement = "{0}={1}" -f $key, $Override[$key]
                $null = $applied.Add([string]$key)
                break
            }
        }

        $lines.Add($(if ($null -ne $replacement) { $replacement } else { $line }))
    }

    $missing = @($Override.Keys | Where-Object { -not $applied.Contains([string]$_) })
    if ($missing.Count -gt 0) {
        $lines.Add("")
        $lines.Add("# Publisher snapshot interoperability harness overrides")
        foreach ($key in $missing) {
            $lines.Add(("{0}={1}" -f $key, $Override[$key]))
        }
    }

    return $lines.ToArray()
}

<#
.SYNOPSIS
    Returns the environment keys the harness sets on top of the tracked .env.example.
#>
function Get-InteropEnvironmentOverride {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [string]$OverlayComposeFile = $script:OverlayComposeFile,

        [string]$PublishedOverlayComposeFile = $script:PublishedOverlayComposeFile,

        [string]$DmsImageTag = "",

        [string]$ConfigurationServiceImage = ""
    )

    $override = [ordered]@{
        DMS_PLUGINS_COMPOSE_FILES = $OverlayComposeFile
    }

    if ($DmsImageTag -or $ConfigurationServiceImage) {
        Assert-InteropPublishedImageSelection -DmsImageTag $DmsImageTag -ConfigurationServiceImage $ConfigurationServiceImage

        # DMS_IMAGE_TAG selects the Ed-Fi API image (published-dms.compose.yml). The Configuration Service
        # image is named whole, because its repository publishes its own tags and digests and a tag
        # that names one repository's build says nothing about the other's. The second overlay gives the
        # published DMS service the container name the harness addresses (ed-fi-api).
        $override["DMS_IMAGE_TAG"] = $DmsImageTag
        $override["CONFIGURATION_SERVICE_DOCKER_IMAGE"] = $ConfigurationServiceImage
        $override["DMS_PLUGINS_COMPOSE_FILES"] = "$OverlayComposeFile;$PublishedOverlayComposeFile"
    }

    # SQL Server keeps the stock MSSQL_MEMORY_LIMIT_MB (4096). The run holds four DMS databases on one
    # instance, and below that limit SQL Server cannot compile the largest schema DDL batch for the third
    # or fourth of them (error 701).

    return $override
}

<#
.SYNOPSIS
    Throws unless a published-image selection is a pair: an Ed-Fi API tag and the whole Configuration Service
    image reference that matches it.

.DESCRIPTION
    The two images live in separate repositories with separate version lines, so the harness never guesses one
    from the other. The Configuration Service reference must name the Ed-Fi Configuration Service repository and
    carry a tag, a digest, or both, which keeps a typo or a foreign image from reaching a compose file.
#>
function Assert-InteropPublishedImageSelection {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [string]$DmsImageTag = "",

        [AllowEmptyString()]
        [string]$ConfigurationServiceImage = ""
    )

    if (-not $DmsImageTag) {
        throw "-ConfigurationServiceImage needs -DmsImageTag; the two images are selected together."
    }
    if ($DmsImageTag -notmatch "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$") {
        throw "DMS image tag '$DmsImageTag' is not a valid image tag."
    }
    if (-not $ConfigurationServiceImage) {
        throw "-DmsImageTag needs -ConfigurationServiceImage: the Configuration Service publishes its own tags and digests, so its image cannot be derived from the Ed-Fi API tag."
    }

    $repository = [regex]::Escape($script:ConfigurationServiceImageRepository)
    $pattern = "^$repository(:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(@sha256:[0-9a-f]{64})?$"
    if ($ConfigurationServiceImage -notmatch $pattern -or $ConfigurationServiceImage -eq $script:ConfigurationServiceImageRepository) {
        throw "Configuration Service image '$ConfigurationServiceImage' must be '$script:ConfigurationServiceImageRepository' with a tag, a @sha256 digest, or both."
    }
}

<#
.SYNOPSIS
    Reads an environment file into a hashtable of raw values (the stack's own reader).
#>
function Get-InteropEnvironmentValue {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [string]$EnvironmentFile
    )

    return ReadValuesFromEnvFile -EnvironmentFile $EnvironmentFile
}

<#
.SYNOPSIS
    Returns the database name the bootstrap registers the source data store against.
#>
function Get-InteropSourceDatabaseName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues
    )

    $key = if ($Engine -eq "mssql") { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }
    return Get-EnvValue -EnvValues $EnvironmentValues -Name $key -DefaultValue "edfi_datamanagementservice"
}

<#
.SYNOPSIS
    Resolves the host URLs of DMS and the Configuration Service from environment-file values.
#>
function Get-InteropEndpoint {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues
    )

    $dmsPort = Get-EnvValue -EnvValues $EnvironmentValues -Name "DMS_HTTP_PORTS" -DefaultValue "8080"
    $cmsPort = Get-EnvValue -EnvValues $EnvironmentValues -Name "CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS" -DefaultValue "8081"

    return [pscustomobject]@{
        DmsUrl = "http://localhost:$dmsPort"
        CmsUrl = "http://localhost:$cmsPort"
    }
}

<#
.SYNOPSIS
    Expands -Scenario into the ordered list of scenarios to run, and refuses any that is not built yet.
#>
function Resolve-InteropScenario {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("StackOnly", "Isolated", "NoSnapshot", "All")]
        [string]$Scenario
    )

    $requested = if ($Scenario -eq "All") { $script:AllScenarios } else { @($Scenario) }
    $notBuilt = @($requested | Where-Object { $_ -notin $script:ImplementedScenarios })
    if ($notBuilt.Count -gt 0) {
        throw "Scenario '$($notBuilt -join "', '")' is not implemented yet. Implemented: $($script:ImplementedScenarios -join ', ')."
    }

    return [string[]]$requested
}

<#
.SYNOPSIS
    Lists every setting the harness changes from the stack defaults, with the reason.
#>
function Get-InteropNonDefaultSetting {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [string]$PublisherImage = $script:DefaultPublisherImage,

        [string]$SeedTemplate = "Minimal",

        [switch]$SkipStackStart,

        [switch]$Rebuild,

        [string]$DmsImageTag = "",

        [string]$ConfigurationServiceImage = ""
    )

    $settings = [System.Collections.Generic.List[object]]::new()
    $settings.Add([pscustomobject]@{ Name = "DataStoreCacheExpirationSeconds"; Value = [string]$script:DataStoreCacheExpirationSeconds; Default = "600"; Reason = "a new data store or snapshot derivative takes effect in seconds, not minutes" })
    $settings.Add([pscustomobject]@{ Name = "RateLimit__PermitLimit"; Value = [string]$script:RatePermitLimit; Default = "20000"; Reason = "the Publisher exceeds the default request rate" })
    $settings.Add([pscustomobject]@{ Name = "Serilog__MinimumLevel__Override__$script:SelectionLoggerName"; Value = "Debug"; Default = "Information"; Reason = "per-request data store target selection is logged" })
    $settings.Add([pscustomobject]@{ Name = "SeedTemplate"; Value = $SeedTemplate; Default = "none"; Reason = "source data store seed" })

    if ($Engine -eq "postgresql") {
        $settings.Add([pscustomobject]@{ Name = "PostgreSQL max_connections"; Value = [string]$script:PostgresMaxConnections; Default = "100"; Reason = "two data stores on one DMS exhaust the default connection limit" })
    }

    if ($PublisherImage -ne $script:DefaultPublisherImage) {
        $settings.Add([pscustomobject]@{ Name = "PublisherImage"; Value = $PublisherImage; Default = $script:DefaultPublisherImage; Reason = "caller override" })
    }
    if ($DmsImageTag) {
        $settings.Add([pscustomobject]@{ Name = "DmsImageTag"; Value = $DmsImageTag; Default = "locally built image (ed-fi-api-local)"; Reason = "the run validates a published image; a locally built image advertises the pinned 8.0.0 Discovery version" })
        $settings.Add([pscustomobject]@{ Name = "ConfigurationServiceImage"; Value = $ConfigurationServiceImage; Default = "locally built image (ed-fi-api-config-local)"; Reason = "the Configuration Service image that matches the published Ed-Fi API image" })
    }
    if ($SkipStackStart) {
        $settings.Add([pscustomobject]@{ Name = "SkipStackStart"; Value = "true"; Default = "false"; Reason = "an existing stack was reused" })
    }
    if ($Rebuild) {
        $settings.Add([pscustomobject]@{ Name = "Rebuild"; Value = "true"; Default = "false"; Reason = "DMS and CMS images were rebuilt" })
    }

    return $settings.ToArray()
}

<#
.SYNOPSIS
    Shapes the run manifest from an explicit allow-list of fields and refuses anything secret-looking.

.DESCRIPTION
    Data stores and clients are projected to ids and names. Even if a caller passes objects that carry
    a key, secret, or connection string, those properties are not copied. The result is then scanned
    for secret-shaped property names and values as a second line of defense.
#>
function ConvertTo-InteropManifest {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)]
        [string]$RunId,

        [Parameter(Mandatory)]
        [string]$Engine,

        [Parameter(Mandatory)]
        [string[]]$Scenario,

        [Parameter(Mandatory)]
        [datetime]$StartedUtc,

        [Parameter(Mandatory)]
        [pscustomobject]$Versions,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$NonDefaultSetting,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$DataStore,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Client,

        [System.Collections.IDictionary]$Arrangement = $null
    )

    $manifest = [ordered]@{
        runId              = $RunId
        engine             = $Engine
        scenarios          = @($Scenario)
        startedUtc         = $StartedUtc.ToUniversalTime().ToString("o")
        versions           = [ordered]@{
            dmsImageName            = $Versions.DmsImageName
            dmsImageId              = $Versions.DmsImageId
            dmsDiscoveryVersion     = $Versions.DmsDiscoveryVersion
            publisherImage          = $Versions.PublisherImage
            publisherImageDigest    = $Versions.PublisherImageDigest
            dataStandard            = $Versions.DataStandard
            identityProvider        = $Versions.IdentityProvider
            engine                  = $Versions.Engine
            engineVersion           = $(if ($Versions.PSObject.Properties.Name -contains "EngineVersion") { $Versions.EngineVersion } else { $null })
            repositoryCommit        = $(if ($Versions.PSObject.Properties.Name -contains "RepositoryCommit") { $Versions.RepositoryCommit } else { $null })
        }
        nonDefaultSettings = @($NonDefaultSetting | ForEach-Object {
                [ordered]@{ name = $_.Name; value = $_.Value; default = $_.Default; reason = $_.Reason }
            })
        dataStores         = @($DataStore | ForEach-Object {
                [ordered]@{ role = $_.Role; id = $_.Id; name = $_.Name; databaseName = $_.DatabaseName }
            })
        clients            = @($Client | ForEach-Object {
                [ordered]@{
                    role            = $_.Role
                    applicationId   = $_.ApplicationId
                    applicationName = $_.ApplicationName
                    vendorId        = $_.VendorId
                    vendorName      = $_.VendorName
                    claimSetName    = $_.ClaimSetName
                    dataStoreId     = $_.DataStoreId
                }
            })
    }

    # Published-image provenance: written only when the run recorded it, so a manifest from a local build
    # keeps its shape. The digests are what a reader pulls to reproduce the run.
    $imageProvenance = [ordered]@{
        imageSource                       = "ImageSource"
        dmsImageDigest                    = "DmsImageDigest"
        dmsImageRevision                  = "DmsImageRevision"
        configurationServiceImageName     = "ConfigurationServiceImageName"
        configurationServiceImageId       = "ConfigurationServiceImageId"
        configurationServiceImageDigest   = "ConfigurationServiceImageDigest"
        configurationServiceImageRevision = "ConfigurationServiceImageRevision"
    }
    foreach ($key in $imageProvenance.Keys) {
        $property = $imageProvenance[$key]
        if ($Versions.PSObject.Properties.Name -contains $property -and -not [string]::IsNullOrWhiteSpace([string]$Versions.$property)) {
            $manifest["versions"][$key] = [string]$Versions.$property
        }
    }

    if ($null -ne $Arrangement) {
        $manifest["arrangement"] = $Arrangement
    }

    Assert-InteropManifestHasNoSecret -Manifest $manifest
    return $manifest
}

<#
.SYNOPSIS
    Throws when a manifest carries a secret-shaped property name or value.
#>
function Assert-InteropManifestHasNoSecret {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Manifest
    )

    $json = ConvertTo-Json -InputObject $Manifest -Depth 10
    $document = ConvertFrom-Json -InputObject $json -Depth 10
    $stack = [System.Collections.Generic.Stack[object]]::new()
    $stack.Push($document)

    while ($stack.Count -gt 0) {
        $node = $stack.Pop()
        if ($node -is [System.Management.Automation.PSCustomObject]) {
            foreach ($property in $node.PSObject.Properties) {
                if ($property.Name -match $script:SecretPropertyPattern) {
                    throw "The run manifest has a secret-shaped property '$($property.Name)'. Manifests hold ids and names only."
                }
                $stack.Push($property.Value)
            }
        }
        elseif ($node -is [System.Collections.IEnumerable] -and $node -isnot [string]) {
            foreach ($item in $node) {
                $stack.Push($item)
            }
        }
        elseif ($node -is [string] -and $node -match $script:SecretValuePattern) {
            throw "The run manifest has a secret-shaped value. Manifests hold ids and names only."
        }
    }
}

#endregion

#region Snapshot helpers (pure)

<#
.SYNOPSIS
    Throws unless a value is safe to place unquoted in generated SQL as a database, role, or login name.
#>
function Assert-InteropSqlIdentifier {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$What
    )

    if ($Value -notmatch "^[A-Za-z_][A-Za-z0-9_]{0,62}$") {
        throw "The $What '$Value' is not a plain SQL identifier (letters, digits, underscore; at most 63 characters)."
    }
}

<#
.SYNOPSIS
    Builds the name of a snapshot database: <source database>_snapshot_<UTC timestamp>.
#>
function Get-InteropSnapshotName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$SourceDatabaseName,

        [Parameter(Mandatory)]
        [datetime]$TimestampUtc
    )

    $name = "{0}_snapshot_{1}" -f $SourceDatabaseName, $TimestampUtc.ToUniversalTime().ToString("yyyyMMddHHmmss")
    Assert-InteropSqlIdentifier -Value $name -What "snapshot database name"
    return $name
}

<#
.SYNOPSIS
    Builds the read-only login name for one snapshot, unique per snapshot because the login is cluster-wide.
#>
function Get-InteropSnapshotLoginName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [datetime]$TimestampUtc
    )

    $name = "{0}_{1}" -f $script:SnapshotReaderPrefix, $TimestampUtc.ToUniversalTime().ToString("yyyyMMddHHmmss")
    Assert-InteropSqlIdentifier -Value $name -What "snapshot login name"
    return $name
}

<#
.SYNOPSIS
    Generates a random throwaway password for a read-only snapshot login. Letters and digits only, so it is
    safe inside a SQL literal and a connection string. The caller keeps it in memory; it is never stored.
#>
function New-InteropReadOnlyPassword {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure random-string factory despite the New- verb; it creates no system state.')]
    param(
        [ValidateRange(16, 128)]
        [int]$Length = 32
    )

    $alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789"
    $characters = for ($index = 0; $index -lt $Length; $index++) {
        $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)]
    }

    return -join $characters
}

<#
.SYNOPSIS
    Builds the connection string DMS uses to reach a snapshot, as seen from the Docker network.

.DESCRIPTION
    Uses the read-only login (never the administrator) and the container host name the DMS container
    resolves. The result carries the password, so it is a secret: register it and drop it, never log it.
#>
function Get-InteropSnapshotConnectionString {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The throwaway password is generated in memory and must be embedded as plaintext in the connection string DMS parses.')]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [string]$SnapshotName,

        [Parameter(Mandatory)]
        [string]$LoginName,

        [Parameter(Mandatory)]
        [string]$Password
    )

    Assert-InteropSqlIdentifier -Value $SnapshotName -What "snapshot database name"
    Assert-InteropSqlIdentifier -Value $LoginName -What "snapshot login name"

    if ($Engine -eq "mssql") {
        return New-DataStoreConnectionString -DatabaseEngine mssql -DbHost "dms-mssql" -Port 1433 `
            -Username $LoginName -Password $Password -DatabaseName $SnapshotName
    }

    return New-DataStoreConnectionString -DatabaseEngine postgresql -DbHost "dms-postgresql" -Port 5432 `
        -Username $LoginName -Password $Password -DatabaseName $SnapshotName
}

function Assert-InteropSqlPassword {
    [CmdletBinding()]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Validates the throwaway plaintext password before it is placed in a SQL literal.')]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Password
    )

    if ($Password -notmatch "^[A-Za-z0-9]{16,128}$") {
        throw "The read-only password must be 16 to 128 letters and digits (use New-InteropReadOnlyPassword)."
    }
}

<#
.SYNOPSIS
    Builds the PostgreSQL statements that create the read-only role and grant it the minimum DMS needs.

.DESCRIPTION
    Run as the administrator inside the restored snapshot database. PUBLIC is revoked, so TEMPORARY has
    to be granted back (DMS creates temp tables on a GET-many); the sequence needs its own SELECT because
    SELECT ON ALL TABLES does not cover it, and /changeQueries/v1/availableChangeVersions reads it.
#>
function Get-InteropPostgresReadOnlySql {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The throwaway password is generated in memory and placed in a one-off CREATE ROLE statement.')]
    param(
        [Parameter(Mandatory)]
        [string]$DatabaseName,

        [Parameter(Mandatory)]
        [string]$LoginName,

        [Parameter(Mandatory)]
        [string]$Password,

        [string[]]$Schema = $script:SnapshotSchemas
    )

    Assert-InteropSqlIdentifier -Value $DatabaseName -What "snapshot database name"
    Assert-InteropSqlIdentifier -Value $LoginName -What "snapshot login name"
    Assert-InteropSqlPassword -Password $Password
    if ($Schema.Count -eq 0) {
        throw "At least one schema is required."
    }
    foreach ($name in $Schema) {
        Assert-InteropSqlIdentifier -Value $name -What "schema name"
    }
    $schemaList = $Schema -join ", "

    return @(
        "CREATE ROLE $LoginName LOGIN PASSWORD '$Password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;"
        "REVOKE ALL ON DATABASE $DatabaseName FROM PUBLIC;"
        "GRANT CONNECT, TEMPORARY ON DATABASE $DatabaseName TO $LoginName;"
        "GRANT USAGE ON SCHEMA $schemaList TO $LoginName;"
        "GRANT SELECT ON ALL TABLES IN SCHEMA $schemaList TO $LoginName;"
        "GRANT SELECT ON SEQUENCE dms.""ChangeVersionSequence"" TO $LoginName;"
    ) -join "`n"
}

<#
.SYNOPSIS
    Builds the SQL Server statements that create the read-only login and user and grant the minimum DMS
    needs. They must run in the primary BEFORE the snapshot is created: a snapshot is read-only, so a
    grant inside it fails with Msg 3906 and cannot be repaired afterward.
#>
function Get-InteropSqlServerReadOnlySql {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The throwaway password is generated in memory and placed in a one-off CREATE LOGIN statement.')]
    param(
        [Parameter(Mandatory)]
        [string]$SourceDatabaseName,

        [Parameter(Mandatory)]
        [string]$LoginName,

        [Parameter(Mandatory)]
        [string]$Password
    )

    Assert-InteropSqlIdentifier -Value $SourceDatabaseName -What "source database name"
    Assert-InteropSqlIdentifier -Value $LoginName -What "snapshot login name"
    Assert-InteropSqlPassword -Password $Password

    # EXECUTE on GetMaxChangeVersion serves availableChangeVersions; VIEW DEFINITION on the sequence is
    # needed because the function reads sys.sequences, and catalog views hide objects the caller cannot
    # see (without it the function returns NULL).
    return @(
        "SET QUOTED_IDENTIFIER ON;"
        "CREATE LOGIN $LoginName WITH PASSWORD = N'$Password', CHECK_POLICY = OFF, DEFAULT_DATABASE = master;"
        "USE $SourceDatabaseName;"
        "CREATE USER $LoginName FOR LOGIN $LoginName;"
        "ALTER ROLE db_datareader ADD MEMBER $LoginName;"
        "GRANT EXECUTE ON OBJECT::dms.GetMaxChangeVersion TO $LoginName;"
        "GRANT VIEW DEFINITION ON OBJECT::dms.ChangeVersionSequence TO $LoginName;"
    ) -join "`n"
}

<#
.SYNOPSIS
    Builds the SQL Server CREATE DATABASE ... AS SNAPSHOT OF statement, with one sparse file per logical
    data file of the source. Log files are not listed: a snapshot has none.
#>
function Get-InteropSqlServerSnapshotSql {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$SnapshotName,

        [Parameter(Mandatory)]
        [string]$SourceDatabaseName,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$LogicalFileName,

        [string]$DataDirectory = $script:MssqlDataDirectory
    )

    Assert-InteropSqlIdentifier -Value $SnapshotName -What "snapshot database name"
    Assert-InteropSqlIdentifier -Value $SourceDatabaseName -What "source database name"
    if ($LogicalFileName.Count -eq 0) {
        throw "A snapshot needs the logical name of every data file of '$SourceDatabaseName'; none were given."
    }

    $files = for ($index = 0; $index -lt $LogicalFileName.Count; $index++) {
        $logical = $LogicalFileName[$index]
        if ([string]::IsNullOrWhiteSpace($logical)) {
            throw "Logical file name number $($index + 1) is empty."
        }
        $escaped = $logical.Replace("'", "''")
        "(NAME = N'$escaped', FILENAME = N'$($DataDirectory.TrimEnd('/'))/${SnapshotName}_$($index + 1).ss')"
    }

    return "CREATE DATABASE $SnapshotName ON $($files -join ', ') AS SNAPSHOT OF $SourceDatabaseName;"
}

<#
.SYNOPSIS
    Builds the statements that remove a snapshot and its read-only login, by name.
#>
function Get-InteropSnapshotRemovalSql {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [string]$SnapshotName,

        [Parameter(Mandatory)]
        [string]$LoginName,

        [string]$SourceDatabaseName = ""
    )

    Assert-InteropSqlIdentifier -Value $SnapshotName -What "snapshot database name"
    Assert-InteropSqlIdentifier -Value $LoginName -What "snapshot login name"

    if ($Engine -eq "postgresql") {
        return "DROP DATABASE IF EXISTS $SnapshotName WITH (FORCE);`nDROP ROLE IF EXISTS $LoginName;"
    }

    Assert-InteropSqlIdentifier -Value $SourceDatabaseName -What "source database name"
    return @(
        "SET QUOTED_IDENTIFIER ON;"
        "IF DB_ID(N'$SnapshotName') IS NOT NULL DROP DATABASE $SnapshotName;"
        "USE $SourceDatabaseName;"
        "IF USER_ID(N'$LoginName') IS NOT NULL DROP USER $LoginName;"
        "IF SUSER_ID(N'$LoginName') IS NOT NULL DROP LOGIN $LoginName;"
    ) -join "`n"
}

<#
.SYNOPSIS
    Derives how long to wait for a derivative registration or removal to take effect from the cache
    expiry. Measured effect: about one expiry plus a refresh, so three expiries plus a margin is a bound,
    not a sleep.
#>
function Get-InteropSnapshotWaitBound {
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [ValidateRange(1, 86400)]
        [int]$CacheExpirationSeconds = $script:DataStoreCacheExpirationSeconds
    )

    return (3 * $CacheExpirationSeconds) + 30
}

<#
.SYNOPSIS
    Interprets one Use-Snapshot read: 200 means the snapshot is served (Active), a 404 "Snapshot not found."
    means no snapshot derivative is in effect (Absent). Anything else is a fault, never a state to wait out.
#>
function Resolve-InteropSnapshotProbeState {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [int]$StatusCode,

        [AllowEmptyString()]
        [string]$Body = ""
    )

    if ($StatusCode -eq 200) {
        return "Active"
    }
    if ($StatusCode -eq 404 -and $Body -match "Snapshot not found") {
        return "Absent"
    }

    throw "A Use-Snapshot read answered HTTP $StatusCode, which is neither the snapshot being served (200) nor absent (404 'Snapshot not found.'). On 5xx check the read-only login's grants and the connection string."
}

<#
.SYNOPSIS
    Tells whether bootstrap output carries the known BulkLoadClient log4net race signature.
#>
function Test-InteropSeedRaceFailure {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [AllowEmptyString()]
        [string]$Text = ""
    )

    # The exception text, or the AggregateException wrapper around it ("One or more errors occurred. (Stack empty.)").
    return $Text -match "InvalidOperationException:\s*Stack empty|One or more errors occurred\. \(Stack empty\.\)"
}

#endregion

#region Docker and stack helpers

<#
.SYNOPSIS
    Writes a progress line to the information stream.
#>
function Write-InteropStep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    Write-Information "[interop] $Message" -InformationAction Continue
}

<#
.SYNOPSIS
    Polls a condition until it returns true or the timeout passes.
#>
function Wait-InteropCondition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [scriptblock]$Test,

        [int]$TimeoutSeconds = 180,

        [int]$IntervalSeconds = 2
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $passed = $false
        try { $passed = [bool](& $Test) } catch { $passed = $false }
        if ($passed) {
            return
        }
        Start-Sleep -Seconds $IntervalSeconds
    } while ([datetime]::UtcNow -lt $deadline)

    throw "Timed out after $TimeoutSeconds seconds waiting for: $Description"
}

<#
.SYNOPSIS
    Runs a docker command and returns its output, throwing on a non-zero exit code.
#>
function Invoke-InteropDocker {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string[]]$Argument
    )

    $global:LASTEXITCODE = 0
    $output = & docker @Argument 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Argument[0]) failed with exit code ${LASTEXITCODE}: $($output -join ' ')"
    }

    return ($output -join "`n").Trim()
}

<#
.SYNOPSIS
    Lists existing containers that belong to the hardcoded dms-local or dms-published stack.
#>
function Get-InteropStackContainer {
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    $names = @()
    foreach ($project in @($script:ComposeProjectName, $script:PublishedComposeProjectName)) {
        $byProject = Invoke-InteropDocker -Argument @("ps", "-a", "--filter", "label=com.docker.compose.project=$project", "--format", "{{.Names}}")
        $names += @($byProject -split "`n")
    }
    $byName = Invoke-InteropDocker -Argument @("ps", "-a", "--format", "{{.Names}}")
    $names += @($byName -split "`n" | Where-Object { $_ -in $script:StackContainerNames })
    return [string[]]@($names | Where-Object { $_ } | Sort-Object -Unique)
}

<#
.SYNOPSIS
    Refuses to start a stack when dms-local or dms-published containers this invocation did not start already exist.

.DESCRIPTION
    The repository hardcodes the compose project name and the container names, so a second stack would
    either collide or, worse, adopt another run's containers and databases.
#>
function Assert-InteropNoForeignStack {
    [CmdletBinding()]
    param()

    $existing = @(Get-InteropStackContainer)
    if ($existing.Count -gt 0) {
        throw "Refusing to start a stack: '$script:ComposeProjectName' or '$script:PublishedComposeProjectName' containers already exist ($($existing -join ', ')). Tear them down first (pwsh ./bootstrap-local-dms.ps1 -d -v, or ./bootstrap-published-dms.ps1 -d -v, from eng/docker-compose), or pass -SkipStackStart to reuse them."
    }
}

<#
.SYNOPSIS
    Writes the harness-owned environment file for a run and returns its path.

.DESCRIPTION
    A copy of the tracked .env.example with the harness overrides applied. The tracked env files are
    never edited. The file lives in the run folder, which is git-ignored, and holds only the same
    development defaults the tracked example already carries.
#>
function Write-InteropEnvironmentFile {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$RunFolder,

        [string]$DmsImageTag = "",

        [string]$ConfigurationServiceImage = ""
    )

    $baseLines = [string[]](Get-Content -LiteralPath (Join-Path $script:DockerComposeDirectory ".env.example"))
    $override = Get-InteropEnvironmentOverride -DmsImageTag $DmsImageTag -ConfigurationServiceImage $ConfigurationServiceImage
    $content = ConvertTo-InteropEnvironmentContent -BaseLine $baseLines -Override $override
    $path = Join-Path $RunFolder "stack.env"
    Set-Content -LiteralPath $path -Value $content
    return $path
}

<#
.SYNOPSIS
    Waits until an HTTP endpoint answers 200.
#>
function Wait-InteropHttp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Url,

        [Parameter(Mandatory)]
        [string]$Description,

        [int]$TimeoutSeconds = 180
    )

    Wait-InteropCondition -Description "$Description at $Url" -TimeoutSeconds $TimeoutSeconds -Test {
        (Invoke-WebRequest -Uri $Url -SkipHttpErrorCheck -TimeoutSec 5).StatusCode -eq 200
    }
}

<#
.SYNOPSIS
    Reads PostgreSQL's max_connections from the stack container.
#>
function Get-InteropPostgresMaxConnection {
    [CmdletBinding()]
    [OutputType([int])]
    param()

    $value = Invoke-InteropDocker -Argument @("exec", "dms-postgresql", "psql", "-U", "postgres", "-tAc", "SHOW max_connections")
    return [int]$value
}

<#
.SYNOPSIS
    Raises PostgreSQL max_connections for two data stores on one DMS and restarts what holds connections.

.DESCRIPTION
    The default of 100 is too low once DMS serves two data stores. ALTER SYSTEM needs a server restart
    to take effect, after which the Configuration Service and DMS reconnect. The result is verified
    with SHOW max_connections.
#>
function Set-InteropPostgresMaxConnection {
    [CmdletBinding()]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness setup step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Endpoint
    )

    if ((Get-InteropPostgresMaxConnection) -ge $script:PostgresMaxConnections) {
        return
    }

    Write-InteropStep "Raising PostgreSQL max_connections to $script:PostgresMaxConnections."
    $null = Invoke-InteropDocker -Argument @("exec", "dms-postgresql", "psql", "-U", "postgres", "-c", "ALTER SYSTEM SET max_connections = $script:PostgresMaxConnections;")
    $null = Invoke-InteropDocker -Argument @("restart", "dms-postgresql")
    Wait-InteropCondition -Description "PostgreSQL to accept connections" -TimeoutSeconds 120 -Test {
        $global:LASTEXITCODE = 0
        $null = & docker exec dms-postgresql psql -U postgres -tAc "SELECT 1" 2>&1
        $LASTEXITCODE -eq 0
    }
    $null = Invoke-InteropDocker -Argument @("restart", "ed-fi-api-config-service", "ed-fi-api")
    Wait-InteropHttp -Url "$($Endpoint.CmsUrl)/health" -Description "the Configuration Service"
    Wait-InteropHttp -Url "$($Endpoint.DmsUrl)/health" -Description "DMS"

    $actual = Get-InteropPostgresMaxConnection
    if ($actual -lt $script:PostgresMaxConnections) {
        throw "PostgreSQL max_connections is $actual after the change; expected at least $script:PostgresMaxConnections."
    }
}

<#
.SYNOPSIS
    Runs a script with its console output shown and also captured, and reports a failure instead of throwing.

.DESCRIPTION
    A failure is a non-zero exit code or a terminating error. The output is collected as it is produced, so
    it survives a terminating error. The exit code is read from the script, never through the pipeline.
#>
function Invoke-InteropScriptCaptured {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [hashtable]$Argument
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $failure = $null
    $global:LASTEXITCODE = 0
    try {
        & $Path @Argument *>&1 | ForEach-Object { $lines.Add(($_ | Out-String).TrimEnd()); $_ } | Out-Host
        if ($LASTEXITCODE -ne 0) {
            $failure = "exit code $LASTEXITCODE"
        }
    }
    catch {
        $failure = $_.Exception.Message
    }

    return [pscustomobject]@{ Failure = $failure; Output = ($lines -join "`n") }
}

<#
.SYNOPSIS
    Recognizes the SQL Server out-of-memory failure (error 701) a schema provisioning hits when SQL Server has too
    little memory for the run.

.DESCRIPTION
    The largest schema DDL batch creates every table at once, and compiling it needs more memory than SQL Server
    has left once a few DMS databases share the instance under a lowered MSSQL_MEMORY_LIMIT_MB, or when a crowded
    Docker VM makes SQL Server lower its own memory target. The batch then fails with "insufficient system memory
    in resource pool" every time, so a retry does not help: the harness names the cause instead.
#>
function Test-InteropSqlServerMemoryFailure {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [AllowEmptyString()]
        [string]$Text = ""
    )

    return ($Text -match "insufficient system memory in resource pool") -or ($Text -match "Error Number:701,")
}

<#
.SYNOPSIS
    Starts the local stack for an engine through the repository's bootstrap script.

.DESCRIPTION
    Delegates to eng/docker-compose/bootstrap-local-dms.ps1 with the self-contained identity provider
    and the Minimal seed template, using the harness-owned environment file (which layers in
    interop.compose.yml). Then applies the PostgreSQL connection-limit trap. Images are not rebuilt
    unless -Rebuild is passed.

    With -DmsImageTag the stack comes up from published images instead, through
    eng/docker-compose/bootstrap-published-dms.ps1 (the dms-published project): the Ed-Fi API image at that tag and
    the Configuration Service image named by -ConfigurationServiceImage, which the environment file already carries.
    The images are pulled for linux/amd64 first when they are not present. The schema of every data store is still
    provisioned with the SchemaTools built from this checkout, so the checkout must be at, or have the same
    relational mapping as, the revision the published image was built from; a mismatch shows as an
    EffectiveSchemaHash 503 on the first request.
#>
function Start-InteropStack {
    [CmdletBinding()]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness setup step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [string]$EnvironmentFile,

        [ValidateSet("Minimal", "Populated")]
        [string]$SeedTemplate = "Minimal",

        [switch]$Rebuild,

        [string]$DmsImageTag = "",

        [string]$ConfigurationServiceImage = ""
    )

    Assert-InteropNoForeignStack

    $published = [bool]$DmsImageTag
    if ($published) {
        if ($Rebuild) {
            throw "-Rebuild builds the local images; it cannot be combined with -DmsImageTag, which runs published images."
        }
        Assert-InteropPublishedImageSelection -DmsImageTag $DmsImageTag -ConfigurationServiceImage $ConfigurationServiceImage
        Assert-InteropPublishedImage -Image "edfialliance/ed-fi-api:$DmsImageTag"
        Assert-InteropPublishedImage -Image $ConfigurationServiceImage
    }
    $bootstrapScript = if ($published) { "bootstrap-published-dms.ps1" } else { "bootstrap-local-dms.ps1" }

    $bootstrapArguments = @{
        EnvironmentFile  = $EnvironmentFile
        DatabaseEngine   = $Engine
        IdentityProvider = "self-contained"
        LoadSeedData     = $true
        SeedTemplate     = $SeedTemplate
    }
    if ($Rebuild) {
        $bootstrapArguments["Rebuild"] = $true
    }

    Write-InteropStep "Starting the $Engine stack with $bootstrapScript (seed template $SeedTemplate)."
    Push-Location $script:DockerComposeDirectory
    try {
        $result = Invoke-InteropScriptCaptured -Path (Join-Path $script:DockerComposeDirectory $bootstrapScript) -Argument $bootstrapArguments

        # The seed loader (BulkLoadClient) has a known log4net race that fails a fresh bootstrap now and then.
        # Only that signature is retried, and only the seed step: everything before it already succeeded.
        $seedArguments = @{
            EnvironmentFile  = $EnvironmentFile
            IdentityProvider = "self-contained"
            SeedTemplate     = $SeedTemplate
        }
        $attempt = 0
        while ($null -ne $result.Failure) {
            if (-not (Test-InteropSeedRaceFailure -Text "$($result.Output)`
$($result.Failure)")) {
                throw "$bootstrapScript failed: $($result.Failure)"
            }
            if ($attempt -ge $script:SeedRetryLimit) {
                throw "$bootstrapScript still hit the BulkLoadClient 'Stack empty' race after $attempt seed retries: $($result.Failure)"
            }

            $attempt++
            Write-InteropStep "The seed load hit the known BulkLoadClient 'Stack empty' race; retry $attempt of $script:SeedRetryLimit."
            $result = Invoke-InteropScriptCaptured -Path (Join-Path $script:DockerComposeDirectory "load-dms-seed-data.ps1") -Argument $seedArguments
        }
    }
    finally {
        Pop-Location
    }

    $endpoint = Get-InteropEndpoint -EnvironmentValues (ReadValuesFromEnvFile -EnvironmentFile $EnvironmentFile)
    if ($Engine -eq "postgresql") {
        Set-InteropPostgresMaxConnection -Endpoint @{ CmsUrl = $endpoint.CmsUrl; DmsUrl = $endpoint.DmsUrl }
    }
}

<#
.SYNOPSIS
    Verifies that an existing stack is up and usable, for -SkipStackStart.
#>
function Assert-InteropStackUsable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues
    )

    $endpoint = Get-InteropEndpoint -EnvironmentValues $EnvironmentValues
    $databaseContainer = if ($Engine -eq "mssql") { "dms-mssql" } else { "dms-postgresql" }
    foreach ($container in @($databaseContainer, "ed-fi-api-config-service", "ed-fi-api")) {
        $state = Invoke-InteropDocker -Argument @("ps", "--filter", "name=^/${container}$", "--filter", "status=running", "--format", "{{.Names}}")
        if ($state -ne $container) {
            throw "-SkipStackStart needs a running stack, but container '$container' is not running. Start one without -SkipStackStart."
        }
    }

    foreach ($probe in @(@{ Url = "$($endpoint.CmsUrl)/health"; Name = "the Configuration Service" }, @{ Url = "$($endpoint.DmsUrl)/health"; Name = "DMS" })) {
        Wait-InteropHttp -Url $probe.Url -Description $probe.Name -TimeoutSeconds 30
    }

    if ($Engine -eq "postgresql" -and (Get-InteropPostgresMaxConnection) -lt $script:PostgresMaxConnections) {
        throw "-SkipStackStart found PostgreSQL max_connections below $script:PostgresMaxConnections, which two data stores on one DMS need. Start the stack without -SkipStackStart so the harness raises it."
    }
}

#endregion

#region Configuration Service and DMS operations

<#
.SYNOPSIS
    Mints the Configuration Service admin token once; callers reuse it for the whole run.

.DESCRIPTION
    The Configuration Service caps active tokens per client and rate-limits /connect/token, so the
    harness never mints one per call.
#>
function Get-InteropAdminToken {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues
    )

    $admin = Resolve-BootstrapAdminClient -EnvValues $EnvironmentValues
    return Get-CmsToken -CmsUrl $CmsUrl -ClientId $admin.ClientId -ClientSecret $admin.ClientSecret
}

<#
.SYNOPSIS
    Finds the seeded source data store: the lowest-id data store the harness did not create.
#>
function Get-InteropSourceDataStore {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [string]$AdminToken,

        [string]$DatabaseName = ""
    )

    $stores = @(Get-DataStore -CmsUrl $CmsUrl -AccessToken $AdminToken -Limit 500)
    $candidates = @($stores | Where-Object { $_.name -notlike "interop-*" } | Sort-Object { [long]$_.id })
    if ($candidates.Count -eq 0) {
        throw "The Configuration Service has no source data store that the harness did not create."
    }

    return [pscustomobject]@{
        Role         = "source"
        Id           = [long]$candidates[0].id
        Name         = [string]$candidates[0].name
        DatabaseName = $DatabaseName
    }
}

<#
.SYNOPSIS
    Creates and provisions an additional data store, and returns its id and connection details.

.DESCRIPTION
    Builds the registration connection string the way the E2E setup does for an additional database
    (New-E2EDataStoreConnectionStrings), registers it with the Configuration Service (Add-DataStore),
    and provisions the relational schema with provision-dms-schema.ps1 -DataStoreId, which runs the
    SchemaTools built from this checkout (the same source as the DMS image) and creates the database.
    The returned ConnectionString is for in-memory use only and is never written to the manifest.
#>
function New-InteropDataStore {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness setup step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [string]$AdminToken,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [string]$EnvironmentFile,

        [Parameter(Mandatory)]
        [string]$RunToken,

        [Parameter(Mandatory)]
        [string]$Role
    )

    $name = Get-InteropResourceName -RunToken $RunToken -Role $Role
    $databaseName = Get-InteropDatabaseName -RunToken $RunToken -Role $Role
    $environmentValues = ReadValuesFromEnvFile -EnvironmentFile $EnvironmentFile
    $connectionStrings = New-E2EDataStoreConnectionStrings -DatabaseEngine $Engine -EnvironmentValues $environmentValues -DatabaseName $databaseName

    Write-InteropStep "Registering data store '$name' (database $databaseName)."
    $id = [long](Add-DataStore -CmsUrl $CmsUrl -Name $name -ConnectionString $connectionStrings.RegistrationConnectionString -DatabaseEngine $Engine -AccessToken $AdminToken)

    Write-InteropStep "Provisioning the schema of data store $id."
    Push-Location $script:DockerComposeDirectory
    try {
        $provisionArguments = @{ EnvironmentFile = $EnvironmentFile; DatabaseEngine = $Engine; DataStoreId = $id }
        $provision = Invoke-InteropScriptCaptured -Path (Join-Path $script:DockerComposeDirectory "provision-dms-schema.ps1") -Argument $provisionArguments
        if ($null -ne $provision.Failure) {
            $message = "provision-dms-schema.ps1 failed for data store ${id}: $($provision.Failure)"
            if ($Engine -eq "mssql" -and (Test-InteropSqlServerMemoryFailure -Text "$($provision.Output)`n$($provision.Failure)")) {
                $message += " SQL Server ran out of memory compiling the schema DDL (error 701), which a retry does not clear." +
                    " Leave MSSQL_MEMORY_LIMIT_MB at its 4096 default and free memory in the Docker VM, then start a new run."
            }
            throw $message
        }
    }
    finally {
        Pop-Location
    }

    return [pscustomobject]@{
        Role             = $Role
        Id               = $id
        Name             = $name
        DatabaseName     = $databaseName
        Engine           = $Engine
        ConnectionString = $connectionStrings.RegistrationConnectionString
    }
}

<#
.SYNOPSIS
    Creates a vendor, an application, and its client credentials with a claim set, bound to one data store.

.DESCRIPTION
    Names are unique per invocation because the Configuration Service rejects duplicates. The returned
    Key and Secret are for in-memory use only.
#>
function New-InteropClient {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness setup step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [string]$AdminToken,

        [Parameter(Mandatory)]
        [string]$RunToken,

        [Parameter(Mandatory)]
        [string]$Role,

        [Parameter(Mandatory)]
        [string]$ClaimSetName,

        [Parameter(Mandatory)]
        [long]$DataStoreId
    )

    $vendorName = Get-InteropResourceName -RunToken $RunToken -Role "$Role-vendor"
    $applicationName = Get-InteropResourceName -RunToken $RunToken -Role $Role

    Write-InteropStep "Creating client '$applicationName' ($ClaimSetName) on data store $DataStoreId."
    $vendorId = Add-Vendor -CmsUrl $CmsUrl -Company $vendorName -NamespacePrefixes "uri://ed-fi.org" -AccessToken $AdminToken
    $application = Add-Application -CmsUrl $CmsUrl -ApplicationName $applicationName -ClaimSetName $ClaimSetName `
        -VendorId $vendorId -AccessToken $AdminToken -EducationOrganizationIds $script:EducationOrganizationIds `
        -DataStoreIds @($DataStoreId)

    return [pscustomobject]@{
        Role            = $Role
        VendorId        = [long]$vendorId
        VendorName      = $vendorName
        ApplicationId   = [long]$application.Id
        ApplicationName = $applicationName
        ClaimSetName    = $ClaimSetName
        DataStoreId     = $DataStoreId
        Key             = [string]$application.Key
        Secret          = [string]$application.Secret
    }
}

<#
.SYNOPSIS
    Mints a DMS bearer token for a client.
#>
function Get-InteropClientToken {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [pscustomobject]$Client
    )

    return [string](Get-DmsToken -DmsUrl $DmsUrl -Key $Client.Key -Secret $Client.Secret)
}

<#
.SYNOPSIS
    Calls the DMS Resources API with a bearer token.
#>
function Invoke-InteropDmsRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Token,

        [ValidateSet("Get", "Post")]
        [string]$Method = "Get",

        [object]$Body = $null
    )

    $parameters = @{
        Uri         = "$DmsUrl$Path"
        Method      = $Method
        Headers     = @{ Authorization = "Bearer $Token" }
        ContentType = "application/json"
    }
    if ($null -ne $Body) {
        $parameters["Body"] = ConvertTo-Json -InputObject $Body -Depth 10
    }

    return Invoke-RestMethod @parameters
}

<#
.SYNOPSIS
    Copies every schoolYearType from the source to a target, returning how many were copied.

.DESCRIPTION
    The Publisher never publishes schoolYearTypes, so a target would reject resources that reference
    them. The reader token reads the source and the writer token writes the target.
#>
function Copy-InteropSchoolYear {
    [CmdletBinding()]
    [OutputType([int])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness setup step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$SourceToken,

        [Parameter(Mandatory)]
        [string]$TargetToken
    )

    # Invoke-RestMethod can hand back a JSON array as one object; foreach flattens either shape.
    $response = Invoke-InteropDmsRequest -DmsUrl $DmsUrl -Path "/data/ed-fi/schoolYearTypes?limit=500" -Token $SourceToken
    $items = @(foreach ($entry in $response) { $entry })
    if ($items.Count -eq 0) {
        throw "The source has no schoolYearTypes. It is not seeded; re-run without -SkipStackStart on a clean stack."
    }

    foreach ($item in $items) {
        $body = [ordered]@{
            currentSchoolYear     = $item.currentSchoolYear
            schoolYear            = $item.schoolYear
            schoolYearDescription = $item.schoolYearDescription
        }
        $null = Invoke-InteropDmsRequest -DmsUrl $DmsUrl -Path "/data/ed-fi/schoolYearTypes" -Token $TargetToken -Method Post -Body $body
    }

    return $items.Count
}

<#
.SYNOPSIS
    Proves the Debug override reached DMS: a header-less GET must log "Selected Primary target for data store <id>".
#>
function Assert-InteropSelectionLogging {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter(Mandatory)]
        [long]$DataStoreId
    )

    $since = [datetime]::UtcNow.AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    $null = Invoke-InteropDmsRequest -DmsUrl $DmsUrl -Path "/data/ed-fi/schoolYearTypes?limit=1" -Token $Token

    $pattern = "Selected Primary target for data store\D{0,6}$DataStoreId\b"
    Wait-InteropCondition -Description "a 'Selected Primary target for data store $DataStoreId' line in the DMS log (is interop.compose.yml applied?)" -TimeoutSeconds 30 -IntervalSeconds 1 -Test {
        $global:LASTEXITCODE = 0
        $log = (& docker logs ed-fi-api --since $since 2>&1) -join "`n"
        $log -match $pattern
    }
}

<#
.SYNOPSIS
    Picks the registry digest of an image from its RepoDigests, or an empty string when it has none.

.DESCRIPTION
    A locally built image has no RepoDigests. A pulled one lists one reference per repository it was pulled
    from, so the entry is chosen by the repository of the image name, never taken by position.
#>
function Select-InteropRepoDigest {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$ImageName,

        [AllowEmptyCollection()]
        [string[]]$RepoDigest = @()
    )

    $repository = ($ImageName -replace "@sha256:[0-9a-f]{64}$", "") -replace ":[^/:]+$", ""
    foreach ($reference in $RepoDigest) {
        if ($reference -match "^(?<repository>.+)@(?<digest>sha256:[0-9a-f]{64})$" -and $Matches["repository"] -eq $repository) {
            return $Matches["digest"]
        }
    }

    return ""
}

<#
.SYNOPSIS
    Throws unless the Discovery version a published image advertises belongs to the release line its tag names.

.DESCRIPTION
    A tag such as 8.1.0-beta2.23 names the 8.1 line, so Discovery must read 8.1.x. A locally built image, or a
    stale container that is not the image the tag selects, advertises the AssemblyVersion its build pinned
    (8.0.0), and a record about it would carry the wrong release name.
#>
function Assert-InteropPublishedDiscoveryVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$DmsImageTag,

        [AllowEmptyString()]
        [string]$DiscoveryVersion = ""
    )

    if ($DmsImageTag -notmatch "^(?<line>[0-9]+\.[0-9]+)\.") {
        throw "DMS image tag '$DmsImageTag' does not start with a release line such as 8.1.; cannot check the Discovery version against it."
    }

    $line = $Matches["line"]
    if ($DiscoveryVersion -notmatch "^$([regex]::Escape($line))\.") {
        throw "The published image tagged '$DmsImageTag' should advertise a $line.x Discovery version, but Discovery reads '$DiscoveryVersion'. The running container is not that image."
    }
}

<#
.SYNOPSIS
    Reads the image a running container uses: name, image id, registry digest, and the source revision the
    image was built from (the OCI revision label), each empty when the image does not carry it.
#>
function Get-InteropContainerImage {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$Container
    )

    $name = Invoke-InteropDocker -Argument @("inspect", $Container, "--format", "{{.Config.Image}}")
    $id = Invoke-InteropDocker -Argument @("inspect", $Container, "--format", "{{.Image}}")
    $repoDigest = @((Invoke-InteropDocker -Argument @("image", "inspect", $id, "--format", "{{range .RepoDigests}}{{.}}{{println}}{{end}}")) -split "`n" | Where-Object { $_ })
    $revision = Invoke-InteropDocker -Argument @("image", "inspect", $id, "--format", '{{index .Config.Labels "org.opencontainers.image.revision"}}')

    return [pscustomobject]@{
        Name     = $name
        Id       = $id
        Digest   = Select-InteropRepoDigest -ImageName $name -RepoDigest $repoDigest
        Revision = $(if ($revision -eq "<no value>") { "" } else { $revision })
    }
}

<#
.SYNOPSIS
    Makes sure a published image is present locally, pulling it for linux/amd64 when it is not.

.DESCRIPTION
    The published images are amd64-only, so on an arm64 host the platform is named explicitly; the stack then
    runs them under emulation.
#>
function Assert-InteropPublishedImage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Image
    )

    $global:LASTEXITCODE = 0
    $null = & docker image inspect $Image 2>&1
    if ($LASTEXITCODE -eq 0) {
        return
    }

    Write-InteropStep "Pulling the published image $Image (linux/amd64)."
    $null = Invoke-InteropDocker -Argument @("pull", "--platform", "linux/amd64", $Image)
}

<#
.SYNOPSIS
    Collects the versions under test.
#>
function Get-InteropVersion {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$PublisherImage,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues,

        [switch]$Published
    )

    $discovery = Invoke-RestMethod -Uri "$DmsUrl/" -TimeoutSec 10
    $dataStandard = ""
    if ($discovery.PSObject.Properties.Name -contains "dataModels") {
        $dataStandard = (@($discovery.dataModels | ForEach-Object { "$($_.name) $($_.version)" })) -join ", "
    }

    $publisherDigest = Get-InteropImageDigest -Image $PublisherImage
    if (-not $publisherDigest) {
        $publisherDigest = Invoke-InteropDocker -Argument @("image", "inspect", $PublisherImage, "--format", "{{.Id}}")
    }

    $engineVersion = "unknown"
    try {
        $engineVersion = if ($Engine -eq "mssql") {
            "SQL Server $(Invoke-InteropSql -Engine mssql -EnvironmentValues $EnvironmentValues -Sql "SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(32))")"
        }
        else {
            ((Invoke-InteropDocker -Argument @("exec", "dms-postgresql", "postgres", "--version")) -replace "^postgres \(PostgreSQL\)\s*", "PostgreSQL ").Trim()
        }
    }
    catch {
        Write-InteropStep "Could not read the $Engine server version: $($_.Exception.Message)"
    }

    $repositoryCommit = "unknown"
    $global:LASTEXITCODE = 0
    $commit = (& git -C $script:RepositoryRoot rev-parse --short=12 HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $commit) {
        $repositoryCommit = "$commit"
        $global:LASTEXITCODE = 0
        $dirty = (& git -C $script:RepositoryRoot status --porcelain --untracked-files=no 2>$null)
        if ($LASTEXITCODE -eq 0 -and $dirty) {
            $repositoryCommit = "$commit (working tree had uncommitted changes)"
        }
    }

    $dmsImage = Get-InteropContainerImage -Container "ed-fi-api"
    $configurationServiceImage = Get-InteropContainerImage -Container "ed-fi-api-config-service"

    return [pscustomobject]@{
        EngineVersion       = $engineVersion
        RepositoryCommit    = $repositoryCommit
        ImageSource         = $(if ($Published) { "published" } else { "local-build" })
        DmsImageName        = $dmsImage.Name
        DmsImageId          = $dmsImage.Id
        DmsImageDigest      = $dmsImage.Digest
        DmsImageRevision    = $dmsImage.Revision
        ConfigurationServiceImageName     = $configurationServiceImage.Name
        ConfigurationServiceImageId       = $configurationServiceImage.Id
        ConfigurationServiceImageDigest   = $configurationServiceImage.Digest
        ConfigurationServiceImageRevision = $configurationServiceImage.Revision
        DmsDiscoveryVersion = [string]$discovery.version
        PublisherImage      = $PublisherImage
        PublisherImageDigest = $publisherDigest
        DataStandard        = $dataStandard
        IdentityProvider    = Get-EnvValue -EnvValues $EnvironmentValues -Name "CONFIGURATION_SERVICE_IDENTITY_PROVIDER" -DefaultValue "self-contained"
        Engine              = $Engine
    }
}

<#
.SYNOPSIS
    Makes sure the pinned Publisher image is present locally, pulling it by digest if it is not.
#>
function Assert-InteropPublisherImage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Image
    )

    $global:LASTEXITCODE = 0
    $null = & docker image inspect $Image 2>&1
    if ($LASTEXITCODE -eq 0) {
        return
    }

    Write-InteropStep "Pulling the Publisher image $Image (linux/amd64 only)."
    $null = Invoke-InteropDocker -Argument @("pull", "--platform", "linux/amd64", $Image)
}

<#
.SYNOPSIS
    Writes manifest.json into the run folder and returns its path.
#>
function Write-InteropRunManifest {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes one file in a git-ignored run folder; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$RunFolder,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Manifest
    )

    Assert-InteropManifestHasNoSecret -Manifest $Manifest
    $path = Join-Path $RunFolder "manifest.json"
    ConvertTo-Json -InputObject $Manifest -Depth 10 | Set-Content -LiteralPath $path
    return $path
}

#endregion

#region Snapshot operations

<#
.SYNOPSIS
    Runs SQL as the database administrator inside the engine container, redacting secrets from any failure.

.DESCRIPTION
    PostgreSQL runs psql with ON_ERROR_STOP; SQL Server runs sqlcmd with QUOTED_IDENTIFIER ON (sqlcmd
    defaults it OFF, which breaks statements touching dms.*). The SQL Server administrator password is
    handed to the container through the environment, not the command line. Output is returned as text.
#>
function Invoke-InteropSql {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory)]
        [string]$Sql,

        # PostgreSQL only: the database to connect to.
        [string]$Database = "postgres",

        [string[]]$Redact = @()
    )

    $global:LASTEXITCODE = 0
    if ($Engine -eq "mssql") {
        $env:SQLCMDPASSWORD = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name "MSSQL_SA_PASSWORD" -DefaultValue "abcdefgh1!"
        try {
            $output = & docker exec -e SQLCMDPASSWORD dms-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W -Q "SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON; $Sql" 2>&1
        }
        finally {
            Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
        }
    }
    else {
        $user = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name "POSTGRES_USER" -DefaultValue "postgres"
        $output = & docker exec dms-postgresql psql -U $user -v ON_ERROR_STOP=1 -d $Database -c $Sql 2>&1
    }

    $text = ($output -join "`n").Trim()
    if ($LASTEXITCODE -ne 0) {
        foreach ($secret in $Redact) {
            if ($secret) { $text = $text.Replace($secret, "<redacted>") }
        }
        throw "The $Engine statement failed with exit code ${LASTEXITCODE}: $text"
    }

    return $text
}

<#
.SYNOPSIS
    Runs a PostgreSQL client tool (createdb, pg_dump, pg_restore) as the administrator in the container.
#>
function Invoke-InteropPostgresTool {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory)]
        [string]$Tool,

        [Parameter(Mandatory)]
        [string[]]$Argument
    )

    $user = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name "POSTGRES_USER" -DefaultValue "postgres"
    return Invoke-InteropDocker -Argument (@("exec", "dms-postgresql", $Tool, "-U", $user) + $Argument)
}

<#
.SYNOPSIS
    Creates a point-in-time snapshot of the source database and a read-only login for it, as an operator would.

.DESCRIPTION
    PostgreSQL has no database snapshot, so the snapshot is a pg_dump / pg_restore copy into
    <source>_snapshot_<timestamp>, followed by the read-only role and its minimum grants. SQL Server uses
    CREATE DATABASE ... AS SNAPSHOT OF over every logical data file; its login, user, and grants are
    created in the primary first, because a snapshot is read-only and inherits its users at creation.

    Returns the snapshot name, the login name, and the Docker-network connection string DMS needs. The
    connection string carries a random throwaway password that exists only in memory: it is a secret,
    to be registered with Register-InteropSnapshotDerivative and never logged or written.
#>
function New-InteropSnapshot {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness arrangement step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues,

        [datetime]$TimestampUtc = [datetime]::UtcNow
    )

    $sourceDatabase = Get-InteropSourceDatabaseName -Engine $Engine -EnvironmentValues $EnvironmentValues
    $snapshotName = Get-InteropSnapshotName -SourceDatabaseName $sourceDatabase -TimestampUtc $TimestampUtc
    $loginName = Get-InteropSnapshotLoginName -TimestampUtc $TimestampUtc
    $password = New-InteropReadOnlyPassword
    $removal = @{ Engine = $Engine; EnvironmentValues = $EnvironmentValues; SnapshotName = $snapshotName; LoginName = $loginName; SourceDatabaseName = $sourceDatabase }

    Write-InteropStep "Creating $Engine snapshot '$snapshotName' of '$sourceDatabase' with read-only login '$loginName'."
    try {
        if ($Engine -eq "postgresql") {
            $dump = "/tmp/$snapshotName.dump"
            $null = Invoke-InteropPostgresTool -EnvironmentValues $EnvironmentValues -Tool "createdb" -Argument @("-E", "UTF8", $snapshotName)
            try {
                $null = Invoke-InteropPostgresTool -EnvironmentValues $EnvironmentValues -Tool "pg_dump" -Argument @("-Fc", "-f", $dump, $sourceDatabase)
                $null = Invoke-InteropPostgresTool -EnvironmentValues $EnvironmentValues -Tool "pg_restore" -Argument @("--no-owner", "--no-privileges", "-d", $snapshotName, $dump)
            }
            finally {
                $null = & docker exec dms-postgresql rm -f $dump 2>&1
            }

            $sql = Get-InteropPostgresReadOnlySql -DatabaseName $snapshotName -LoginName $loginName -Password $password
            $null = Invoke-InteropSql -Engine $Engine -EnvironmentValues $EnvironmentValues -Database $snapshotName -Sql $sql -Redact @($password)
        }
        else {
            $files = Invoke-InteropSql -Engine $Engine -EnvironmentValues $EnvironmentValues `
                -Sql "SELECT name FROM $sourceDatabase.sys.database_files WHERE type_desc = N'ROWS' ORDER BY file_id;"
            $logicalNames = [string[]]@($files -split "\r?\n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })

            $grants = Get-InteropSqlServerReadOnlySql -SourceDatabaseName $sourceDatabase -LoginName $loginName -Password $password
            $null = Invoke-InteropSql -Engine $Engine -EnvironmentValues $EnvironmentValues -Sql $grants -Redact @($password)

            $snapshotSql = Get-InteropSqlServerSnapshotSql -SnapshotName $snapshotName -SourceDatabaseName $sourceDatabase -LogicalFileName $logicalNames
            $null = Invoke-InteropSql -Engine $Engine -EnvironmentValues $EnvironmentValues -Sql $snapshotSql
        }
    }
    catch {
        $failure = $_.Exception.Message
        try { Remove-InteropSnapshot @removal } catch { Write-InteropStep "Cleanup after the failed snapshot also failed: $($_.Exception.Message)" }
        throw $failure
    }

    return [pscustomobject]@{
        Engine             = $Engine
        SnapshotName       = $snapshotName
        LoginName          = $loginName
        SourceDatabaseName = $sourceDatabase
        ConnectionString   = Get-InteropSnapshotConnectionString -Engine $Engine -SnapshotName $snapshotName -LoginName $loginName -Password $password
    }
}

<#
.SYNOPSIS
    Drops a snapshot database and its read-only login by name. Deregister the derivative first.
#>
function Remove-InteropSnapshot {
    [CmdletBinding()]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness cleanup step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory)]
        [string]$SnapshotName,

        [Parameter(Mandatory)]
        [string]$LoginName,

        [string]$SourceDatabaseName = ""
    )

    $sql = Get-InteropSnapshotRemovalSql -Engine $Engine -SnapshotName $SnapshotName -LoginName $LoginName -SourceDatabaseName $SourceDatabaseName
    Write-InteropStep "Dropping $Engine snapshot '$SnapshotName' and login '$LoginName'."
    # PostgreSQL refuses DROP DATABASE inside the implicit transaction of a multi-statement string, so each
    # statement runs on its own.
    $statements = if ($Engine -eq "postgresql") { @($sql -split "\r?\n") } else { @($sql) }
    foreach ($statement in $statements) {
        $null = Invoke-InteropSql -Engine $Engine -EnvironmentValues $EnvironmentValues -Sql $statement
    }
}

<#
.SYNOPSIS
    Registers a snapshot as a derivative of a data store and returns the derivative id.

.DESCRIPTION
    A failed registration throws the status code only: the response body can echo the connection string,
    which carries the read-only password (same rule as the E2E AttachDataStoreDerivatives helper).
#>
function Register-InteropSnapshotDerivative {
    [CmdletBinding()]
    [OutputType([long])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness arrangement step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [string]$AdminToken,

        [Parameter(Mandatory)]
        [long]$DataStoreId,

        [Parameter(Mandatory)]
        [string]$ConnectionString
    )

    $body = ConvertTo-Json -InputObject ([ordered]@{
            dataStoreId      = $DataStoreId
            derivativeType   = "Snapshot"
            connectionString = $ConnectionString
        })

    $response = Invoke-WebRequest -Uri "$CmsUrl/v3/dataStoreDerivatives" -Method Post -Body $body -ContentType "application/json" `
        -Headers @{ Authorization = "Bearer $AdminToken" } -SkipHttpErrorCheck -TimeoutSec 30
    if ([int]$response.StatusCode -ne 201) {
        throw "Registering the snapshot derivative for data store $DataStoreId failed with HTTP $([int]$response.StatusCode). The response body is not shown because it can echo the connection string."
    }

    $id = (ConvertFrom-Json -InputObject (Get-InteropResponseText -Response $response)).id
    if ($null -eq $id) {
        throw "Registering the snapshot derivative for data store $DataStoreId answered 201 without an id."
    }

    Write-InteropStep "Registered snapshot derivative $id on data store $DataStoreId."
    return [long]$id
}

<#
.SYNOPSIS
    Deregisters a snapshot derivative.
#>
function Remove-InteropSnapshotDerivative {
    [CmdletBinding()]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness cleanup step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [string]$AdminToken,

        [Parameter(Mandatory)]
        [long]$DerivativeId
    )

    $response = Invoke-WebRequest -Uri "$CmsUrl/v3/dataStoreDerivatives/$DerivativeId" -Method Delete `
        -Headers @{ Authorization = "Bearer $AdminToken" } -SkipHttpErrorCheck -TimeoutSec 30
    if ([int]$response.StatusCode -ne 204) {
        throw "Removing snapshot derivative $DerivativeId failed with HTTP $([int]$response.StatusCode)."
    }

    Write-InteropStep "Removed snapshot derivative $DerivativeId."
}

<#
.SYNOPSIS
    Returns a web response body as text. PowerShell hands back bytes, not a string, for content types it does
    not recognise as text (for example application/problem+json).
#>
function Get-InteropResponseText {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [Microsoft.PowerShell.Commands.WebResponseObject]$Response
    )

    if ($Response.Content -is [byte[]]) {
        return [System.Text.Encoding]::UTF8.GetString($Response.Content)
    }

    return [string]$Response.Content
}

<#
.SYNOPSIS
    Reads a DMS path, optionally asking for the snapshot, and returns the status code and body without
    throwing on an HTTP error status.
#>
function Invoke-InteropDmsProbe {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Token,

        [switch]$UseSnapshot
    )

    $headers = @{ Authorization = "Bearer $Token" }
    if ($UseSnapshot) {
        $headers["Use-Snapshot"] = "true"
    }

    $response = Invoke-WebRequest -Uri "$DmsUrl$Path" -Headers $headers -SkipHttpErrorCheck -TimeoutSec 30
    $contentType = if ($response.Headers.ContainsKey("Content-Type")) { [string]@($response.Headers["Content-Type"])[0] } else { "" }
    return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; ContentType = $contentType; Content = Get-InteropResponseText -Response $response }
}

<#
.SYNOPSIS
    Waits until a Use-Snapshot read shows the snapshot is served (Active) or no longer served (Absent).

.DESCRIPTION
    Polls the observed state, never a fixed sleep, and returns the seconds waited. The bound derives from the
    configured data store cache expiry. A 5xx or any other unexpected status fails at once, because it is a
    grant or connection fault that waiting will not fix.
#>
function Wait-InteropSnapshotState {
    [CmdletBinding()]
    [OutputType([double])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter(Mandatory)]
        [ValidateSet("Active", "Absent")]
        [string]$Expect,

        [int]$CacheExpirationSeconds = $script:DataStoreCacheExpirationSeconds,

        [int]$TimeoutSeconds = (Get-InteropSnapshotWaitBound -CacheExpirationSeconds $CacheExpirationSeconds),

        [int]$IntervalMilliseconds = 500
    )

    $started = [datetime]::UtcNow
    $deadline = $started.AddSeconds($TimeoutSeconds)
    do {
        $probe = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path "/data/ed-fi/schoolYearTypes?limit=1" -Token $Token -UseSnapshot
        $state = Resolve-InteropSnapshotProbeState -StatusCode $probe.StatusCode -Body $probe.Content
        if ($state -eq $Expect) {
            $waited = ([datetime]::UtcNow - $started).TotalSeconds
            Write-InteropStep ("Snapshot state '{0}' observed after {1:N1} seconds." -f $Expect, $waited)
            return $waited
        }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    } while ([datetime]::UtcNow -lt $deadline)

    throw "Timed out after $TimeoutSeconds seconds waiting for the snapshot state '$Expect' (last observed '$state')."
}

<#
.SYNOPSIS
    Self-check that the snapshot, not the primary, answers a Use-Snapshot read.

.DESCRIPTION
    Writes one distinguishable descriptor to the source primary AFTER the snapshot, then requires it present
    without the header and absent with it, and requires availableChangeVersions (the surface that exposes
    missing grants on both engines) to answer 200 through the snapshot with a lower newest change version
    than the primary. Throws with the observed values on any mismatch; returns $true otherwise. The
    descriptor stays in the source primary, so only call this where that is acceptable.
#>
function Test-InteropSnapshotServesSnapshotData {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$WriterToken,

        [Parameter(Mandatory)]
        [string]$ReaderToken,

        [Parameter(Mandatory)]
        [string]$RunToken
    )

    $codeValue = "Interop-After-$RunToken"
    $null = Invoke-InteropDmsRequest -DmsUrl $DmsUrl -Path "/data/ed-fi/contentClassDescriptors" -Token $WriterToken -Method Post -Body ([ordered]@{
            codeValue   = $codeValue
            shortDescription = $codeValue
            description = $codeValue
            namespace   = "uri://ed-fi.org/ContentClassDescriptor"
        })

    $path = "/data/ed-fi/contentClassDescriptors?codeValue=$([uri]::EscapeDataString($codeValue))&limit=10"
    $primary = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path $path -Token $ReaderToken
    $snapshot = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path $path -Token $ReaderToken -UseSnapshot
    $primaryCount = @((ConvertFrom-Json -InputObject $primary.Content) | ForEach-Object { $_ }).Count
    $snapshotCount = @((ConvertFrom-Json -InputObject $snapshot.Content) | ForEach-Object { $_ }).Count

    if ($primary.StatusCode -ne 200 -or $primaryCount -ne 1) {
        throw "The post-snapshot document '$codeValue' should be present on the primary: HTTP $($primary.StatusCode), $primaryCount found."
    }
    if ($snapshot.StatusCode -ne 200 -or $snapshotCount -ne 0) {
        throw "The post-snapshot document '$codeValue' must be absent through Use-Snapshot: HTTP $($snapshot.StatusCode), $snapshotCount found."
    }

    $primaryVersion = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path "/changeQueries/v1/availableChangeVersions" -Token $ReaderToken
    $snapshotVersion = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path "/changeQueries/v1/availableChangeVersions" -Token $ReaderToken -UseSnapshot
    if ($primaryVersion.StatusCode -ne 200 -or $snapshotVersion.StatusCode -ne 200) {
        throw "availableChangeVersions answered HTTP $($primaryVersion.StatusCode) on the primary and $($snapshotVersion.StatusCode) through the snapshot; a 500 through the snapshot means the read-only login lacks a grant."
    }
    $primaryNewest = [long](ConvertFrom-Json -InputObject $primaryVersion.Content).newestChangeVersion
    $snapshotNewest = [long](ConvertFrom-Json -InputObject $snapshotVersion.Content).newestChangeVersion
    if ($snapshotNewest -ge $primaryNewest) {
        throw "The snapshot's newest change version ($snapshotNewest) should be below the primary's ($primaryNewest) after a post-snapshot write."
    }

    Write-InteropStep "Snapshot isolation confirmed: '$codeValue' on the primary only; newest change version $snapshotNewest (snapshot) versus $primaryNewest (primary)."
    return $true
}

#endregion

#region Source data arrangement

# Every marker is one classPeriod of the run's school, so one resource family carries all seven. classPeriods
# support updates, deletes, and key changes (classPeriodName is part of the key), the Publisher requests
# /deletes and /keyChanges for them, and (unlike descriptors) deletes are a real source surface for them.
# Pre-snapshot markers are changed before the snapshot exists and so are visible through it. Post-snapshot
# markers are changed afterwards and are visible on the primary only.
$script:ArrangementMarkerNames = @("U1", "D1", "K1", "I2", "U2", "D2", "K2")
$script:ArrangementSchoolId = 930190101
$script:ArrangementClassPeriodNameLimit = 60

<#
.SYNOPSIS
    Builds the classPeriodName of a marker, unique to the run so a reused stack cannot pass on stale data.

.DESCRIPTION
    Variant distinguishes the two names of a key change ("A" before, "B" after). The name stays within the
    classPeriodName length limit.
#>
function Get-InteropMarkerName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$RunToken,

        [Parameter(Mandatory)]
        [string]$Marker,

        [string]$Variant = ""
    )

    if ($script:ArrangementMarkerNames -cnotcontains $Marker) {
        throw "Marker '$Marker' is not one of $($script:ArrangementMarkerNames -join ', ')."
    }
    if ($RunToken -notmatch "^[A-Za-z0-9-]{1,40}$") {
        throw "Run token '$RunToken' must be 1 to 40 letters, digits, and hyphens."
    }

    $name = "IP-$Marker-$RunToken"
    if ($Variant) {
        $name = "$name-$Variant"
    }
    if ($name.Length -gt $script:ArrangementClassPeriodNameLimit) {
        throw "Marker name '$name' is longer than the $script:ArrangementClassPeriodNameLimit characters classPeriodName allows."
    }

    return $name
}

<#
.SYNOPSIS
    Builds the seven markers of the arrangement: what each document looks like before and after its change.

.DESCRIPTION
    Update (U), Delete (D), and Key change (K) markers exist before the baseline publish, so they are on the
    target at the starting state and the later incremental publish has something to update, delete, and
    re-key. The Insert marker (I2) does not exist until after the snapshot. Pre-snapshot markers (1) are
    changed before the snapshot, post-snapshot markers (2) after it. OfficialAttendancePeriod is the non-key
    field an update changes; it is false on every document that exists at the baseline. DocumentId is filled
    in once the documents exist.
#>
function New-InteropArrangementPlan {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure factory despite the New- verb; it creates no system state.')]
    param(
        [Parameter(Mandatory)]
        [string]$RunToken,

        [int]$SchoolId = $script:ArrangementSchoolId
    )

    $definitions = @(
        @{ Marker = "U1"; Kind = "Update"; Phase = "PreSnapshot" }
        @{ Marker = "D1"; Kind = "Delete"; Phase = "PreSnapshot" }
        @{ Marker = "K1"; Kind = "KeyChange"; Phase = "PreSnapshot" }
        @{ Marker = "I2"; Kind = "Insert"; Phase = "PostSnapshot" }
        @{ Marker = "U2"; Kind = "Update"; Phase = "PostSnapshot" }
        @{ Marker = "D2"; Kind = "Delete"; Phase = "PostSnapshot" }
        @{ Marker = "K2"; Kind = "KeyChange"; Phase = "PostSnapshot" }
    )

    foreach ($definition in $definitions) {
        $kind = $definition.Kind
        $nameBefore = if ($kind -eq "KeyChange") { Get-InteropMarkerName -RunToken $RunToken -Marker $definition.Marker -Variant "A" } else { Get-InteropMarkerName -RunToken $RunToken -Marker $definition.Marker }
        $nameAfter = if ($kind -eq "KeyChange") { Get-InteropMarkerName -RunToken $RunToken -Marker $definition.Marker -Variant "B" } else { $nameBefore }

        [pscustomobject]@{
            Marker           = $definition.Marker
            Kind             = $kind
            Phase            = $definition.Phase
            Resource         = "classPeriods"
            SchoolId         = $SchoolId
            NameBefore       = $nameBefore
            NameAfter        = $nameAfter
            ExistsBefore     = ($kind -ne "Insert")
            ExistsAfter      = ($kind -ne "Delete")
            AttendanceBefore = $(if ($kind -eq "Insert") { $null } else { $false })
            AttendanceAfter  = $(if ($kind -eq "Delete") { $null } elseif ($kind -eq "Update") { $true } else { $false })
            DocumentId       = ""
        }
    }
}

<#
.SYNOPSIS
    Says what a marker must look like on one side: the Primary, or the Snapshot.

.DESCRIPTION
    The primary always shows the marker after its change. The snapshot shows the marker after its change when
    the change was made before the snapshot (phase PreSnapshot) and before it otherwise. PresentName is the
    classPeriodName that must be found (null when the document must not exist), AbsentName the names that
    must not be found. DeleteListed and KeyChangeListed say whether the document's id must appear in that
    side's /deletes or /keyChanges (null where the marker's kind does not use that list).
#>
function Get-InteropMarkerExpectation {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Marker,

        [Parameter(Mandatory)]
        [ValidateSet("Primary", "Snapshot")]
        [string]$Side
    )

    $showsAfter = ($Side -eq "Primary") -or ($Marker.Phase -eq "PreSnapshot")
    $exists = if ($showsAfter) { $Marker.ExistsAfter } else { $Marker.ExistsBefore }
    $name = if ($showsAfter) { $Marker.NameAfter } else { $Marker.NameBefore }
    $attendance = if ($showsAfter) { $Marker.AttendanceAfter } else { $Marker.AttendanceBefore }

    $presentName = if ($exists) { $name } else { $null }
    $absentName = @(@($Marker.NameBefore, $Marker.NameAfter) | Select-Object -Unique | Where-Object { $_ -ne $presentName })

    return [pscustomobject]@{
        Marker                   = $Marker.Marker
        Side                     = $Side
        ShowsChange              = $showsAfter
        PresentName              = $presentName
        AbsentName               = [string[]]$absentName
        OfficialAttendancePeriod = $(if ($exists) { $attendance } else { $null })
        DeleteListed             = $(if ($Marker.Kind -eq "Delete") { $showsAfter } else { $null })
        KeyChangeListed          = $(if ($Marker.Kind -eq "KeyChange") { $showsAfter } else { $null })
    }
}

<#
.SYNOPSIS
    Compares what was read for a marker with what it should look like, returning one message per mismatch.

.DESCRIPTION
    Observation holds Present (a map of classPeriodName to whether a read by that natural key found it),
    OfficialAttendancePeriod of the found document, and DeleteListed and KeyChangeListed (whether the
    document's id was in /deletes or /keyChanges). An empty result means the observation matches.
#>
function Get-InteropMarkerMismatch {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Expectation,

        [Parameter(Mandatory)]
        [pscustomobject]$Observation
    )

    $prefix = "Marker $($Expectation.Marker) on the $($Expectation.Side)"
    $mismatches = [System.Collections.Generic.List[string]]::new()

    if ($null -ne $Expectation.PresentName) {
        if (-not $Observation.Present.ContainsKey($Expectation.PresentName) -or -not $Observation.Present[$Expectation.PresentName]) {
            $mismatches.Add("$prefix should have '$($Expectation.PresentName)' but it was not found.")
        }
        elseif ($null -ne $Expectation.OfficialAttendancePeriod -and $Observation.OfficialAttendancePeriod -ne $Expectation.OfficialAttendancePeriod) {
            $mismatches.Add("$prefix should have officialAttendancePeriod $($Expectation.OfficialAttendancePeriod) on '$($Expectation.PresentName)' but read '$($Observation.OfficialAttendancePeriod)'.")
        }
    }

    foreach ($name in $Expectation.AbsentName) {
        if ($Observation.Present.ContainsKey($name) -and $Observation.Present[$name]) {
            $mismatches.Add("$prefix should not have '$name' but it was found.")
        }
    }

    if ($null -ne $Expectation.DeleteListed -and $Observation.DeleteListed -ne $Expectation.DeleteListed) {
        $mismatches.Add("$prefix should $(if ($Expectation.DeleteListed) { 'list' } else { 'not list' }) the document in /deletes but it $(if ($Observation.DeleteListed) { 'did' } else { 'did not' }).")
    }
    if ($null -ne $Expectation.KeyChangeListed -and $Observation.KeyChangeListed -ne $Expectation.KeyChangeListed) {
        $mismatches.Add("$prefix should $(if ($Expectation.KeyChangeListed) { 'list' } else { 'not list' }) the document in /keyChanges but it $(if ($Observation.KeyChangeListed) { 'did' } else { 'did not' }).")
    }

    return [string[]]$mismatches.ToArray()
}

<#
.SYNOPSIS
    Builds the request path that finds a classPeriod by its natural key (name and school).
#>
function Get-InteropClassPeriodPath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [int]$SchoolId
    )

    return "/data/ed-fi/classPeriods?classPeriodName=$([uri]::EscapeDataString($Name))&schoolId=$SchoolId&limit=10"
}

<#
.SYNOPSIS
    Builds a classPeriod request body. The id is included for an update; the attendance flag is omitted when null.
#>
function New-InteropClassPeriodBody {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure factory despite the New- verb; it creates no system state.')]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [int]$SchoolId,

        [Nullable[bool]]$OfficialAttendancePeriod = $null,

        [string]$Id = ""
    )

    $body = [ordered]@{}
    if ($Id) {
        $body["id"] = $Id
    }
    $body["classPeriodName"] = $Name
    $body["schoolReference"] = [ordered]@{ schoolId = $SchoolId }
    if ($null -ne $OfficialAttendancePeriod) {
        $body["officialAttendancePeriod"] = [bool]$OfficialAttendancePeriod
    }

    return $body
}

<#
.SYNOPSIS
    Shapes the arrangement for the run manifest from an explicit allow-list of fields.

.DESCRIPTION
    Holds the marker map (resource, natural key and attendance value before and after, document id, and
    what each side must show), the starting change version T0, and the change versions seen after the
    snapshot and after the post-snapshot changes. Nothing secret is copied.
#>
function ConvertTo-InteropArrangementManifest {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)]
        [pscustomobject[]]$Marker,

        [Parameter(Mandatory)]
        [long]$StartingChangeVersion,

        [Parameter(Mandatory)]
        [long]$SnapshotNewestChangeVersion,

        [Parameter(Mandatory)]
        [long]$PrimaryNewestChangeVersion,

        [Parameter(Mandatory)]
        [int]$BaselineExitCode,

        [Parameter(Mandatory)]
        [string]$RunToken,

        [string]$SnapshotName = ""
    )

    $shape = {
        param($expectation)
        [ordered]@{
            presentName              = $expectation.PresentName
            absentNames              = @($expectation.AbsentName)
            officialAttendancePeriod = $expectation.OfficialAttendancePeriod
            deleteListed             = $expectation.DeleteListed
            keyChangeListed          = $expectation.KeyChangeListed
        }
    }

    return [ordered]@{
        runSuffix                   = $RunToken
        baselineExitCode            = $BaselineExitCode
        startingChangeVersion       = $StartingChangeVersion
        snapshotNewestChangeVersion = $SnapshotNewestChangeVersion
        primaryNewestChangeVersion  = $PrimaryNewestChangeVersion
        snapshotName                = $SnapshotName
        markers                     = @($Marker | ForEach-Object {
                [ordered]@{
                    marker                   = $_.Marker
                    kind                     = $_.Kind
                    phase                    = $_.Phase
                    resource                 = $_.Resource
                    schoolId                 = $_.SchoolId
                    documentId               = $_.DocumentId
                    naturalKeyBefore         = $_.NameBefore
                    naturalKeyAfter          = $_.NameAfter
                    existsBefore             = $_.ExistsBefore
                    existsAfter              = $_.ExistsAfter
                    officialAttendanceBefore = $_.AttendanceBefore
                    officialAttendanceAfter  = $_.AttendanceAfter
                    expectedPrimary          = (& $shape (Get-InteropMarkerExpectation -Marker $_ -Side Primary))
                    expectedSnapshot         = (& $shape (Get-InteropMarkerExpectation -Marker $_ -Side Snapshot))
                }
            })
    }
}

<#
.SYNOPSIS
    Writes to the DMS Resources API with a bearer token and returns the status code, failing on an unexpected one.

.DESCRIPTION
    Expected defaults are 200 or 201 for a POST, 204 or 200 for a PUT, and 204 for a DELETE. The failure text
    carries the response body, which DMS writes without credentials.
#>
function Invoke-InteropDmsWrite {
    [CmdletBinding()]
    [OutputType([int])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness arrangement step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter(Mandatory)]
        [ValidateSet("Post", "Put", "Delete")]
        [string]$Method,

        [object]$Body = $null,

        [int[]]$ExpectedStatus = @()
    )

    if ($ExpectedStatus.Count -eq 0) {
        $ExpectedStatus = switch ($Method) {
            "Post" { @(200, 201) }
            "Put" { @(200, 204) }
            "Delete" { @(204) }
        }
    }

    $parameters = @{
        Uri                = "$DmsUrl$Path"
        Method             = $Method
        Headers            = @{ Authorization = "Bearer $Token" }
        ContentType        = "application/json"
        SkipHttpErrorCheck = $true
        TimeoutSec         = 60
    }
    if ($null -ne $Body) {
        $parameters["Body"] = ConvertTo-Json -InputObject $Body -Depth 10
    }

    $response = Invoke-WebRequest @parameters
    $status = [int]$response.StatusCode
    if ($ExpectedStatus -notcontains $status) {
        throw "$($Method.ToUpperInvariant()) $Path answered HTTP $status (expected $($ExpectedStatus -join ' or ')): $(Get-InteropResponseText -Response $response)"
    }

    return $status
}

<#
.SYNOPSIS
    Finds a classPeriod by natural key through a token, returning the document or $null. Reads the snapshot
    when asked.
#>
function Get-InteropClassPeriod {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [int]$SchoolId,

        [switch]$UseSnapshot
    )

    $probe = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path (Get-InteropClassPeriodPath -Name $Name -SchoolId $SchoolId) -Token $Token -UseSnapshot:$UseSnapshot
    if ($probe.StatusCode -ne 200) {
        throw "Reading classPeriod '$Name'$(if ($UseSnapshot) { ' through Use-Snapshot' }) answered HTTP $($probe.StatusCode): $($probe.Content)"
    }

    $items = @((ConvertFrom-Json -InputObject $probe.Content) | ForEach-Object { $_ })
    if ($items.Count -gt 1) {
        throw "Reading classPeriod '$Name' found $($items.Count) documents; the natural key should find at most one."
    }

    return $(if ($items.Count -eq 1) { $items[0] } else { $null })
}

<#
.SYNOPSIS
    Reads the source's available change versions (oldest and newest), optionally through the snapshot.
#>
function Get-InteropChangeVersion {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$Token,

        [switch]$UseSnapshot
    )

    $probe = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path "/changeQueries/v1/availableChangeVersions" -Token $Token -UseSnapshot:$UseSnapshot
    if ($probe.StatusCode -ne 200) {
        throw "availableChangeVersions$(if ($UseSnapshot) { ' through Use-Snapshot' }) answered HTTP $($probe.StatusCode): $($probe.Content)"
    }

    $document = ConvertFrom-Json -InputObject $probe.Content
    return [pscustomobject]@{ Oldest = [long]$document.oldestChangeVersion; Newest = [long]$document.newestChangeVersion }
}

<#
.SYNOPSIS
    Reads one marker through a token on one side and returns the observation the mismatch check compares.

.DESCRIPTION
    Finds the document by each of its natural keys, reads its attendance flag, and checks whether its id is
    in /deletes or /keyChanges at or after MinChangeVersion. A read that does not answer 200 throws.
#>
function Get-InteropMarkerObservation {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter(Mandatory)]
        [pscustomobject]$Marker,

        [Parameter(Mandatory)]
        [long]$MinChangeVersion,

        [switch]$UseSnapshot
    )

    $present = @{}
    $attendance = $null
    foreach ($name in @($Marker.NameBefore, $Marker.NameAfter) | Select-Object -Unique) {
        $document = Get-InteropClassPeriod -DmsUrl $DmsUrl -Token $Token -Name $name -SchoolId $Marker.SchoolId -UseSnapshot:$UseSnapshot
        $present[$name] = ($null -ne $document)
        if ($null -ne $document -and $document.PSObject.Properties.Name -contains "officialAttendancePeriod") {
            $attendance = [bool]$document.officialAttendancePeriod
        }
    }

    $listPaths = @{
        deletes    = "/data/ed-fi/classPeriods/deletes?minChangeVersion=$MinChangeVersion&limit=500"
        keyChanges = "/data/ed-fi/classPeriods/keyChanges?minChangeVersion=$MinChangeVersion&limit=500"
    }
    $listed = {
        param($Suffix)
        $probe = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path $listPaths[$Suffix] -Token $Token -UseSnapshot:$UseSnapshot
        if ($probe.StatusCode -ne 200) {
            throw "Reading /classPeriods/$Suffix$(if ($UseSnapshot) { ' through Use-Snapshot' }) answered HTTP $($probe.StatusCode): $($probe.Content)"
        }
        $ids = @((ConvertFrom-Json -InputObject $probe.Content) | ForEach-Object { [string]$_.id })
        return ($ids -contains $Marker.DocumentId)
    }

    return [pscustomobject]@{
        Present                  = $present
        OfficialAttendancePeriod = $attendance
        DeleteListed             = $(if ($Marker.Kind -eq "Delete") { [bool](& $listed "deletes") } else { $null })
        KeyChangeListed          = $(if ($Marker.Kind -eq "KeyChange") { [bool](& $listed "keyChanges") } else { $null })
    }
}

<#
.SYNOPSIS
    Self-check that every marker reads as expected through the source reader, on the primary and through
    the snapshot, before any Publisher run depends on it.

.DESCRIPTION
    Reads with the reader token, not the writer token, because the reader is education-organization scoped:
    this proves every marker is visible to the identity the Publisher uses. Without the header it requires
    the post-snapshot changes (I2, U2, D2, K2) to show; with Use-Snapshot: true it requires the
    pre-snapshot changes (U1, D1, K1) to show and the post-snapshot ones not to. It also requires the
    snapshot's newest change version to be below the primary's. A missing snapshot (404) or one that
    serves the primary (post-snapshot changes visible through the header) fails here. Every mismatch is
    reported in one error; returns $true otherwise.
#>
function Test-InteropArrangementVisibility {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$ReaderToken,

        [Parameter(Mandatory)]
        [pscustomobject[]]$Marker,

        [Parameter(Mandatory)]
        [long]$MinChangeVersion
    )

    $mismatches = [System.Collections.Generic.List[string]]::new()
    foreach ($side in "Primary", "Snapshot") {
        foreach ($item in $Marker) {
            $expectation = Get-InteropMarkerExpectation -Marker $item -Side $side
            try {
                $observation = Get-InteropMarkerObservation -DmsUrl $DmsUrl -Token $ReaderToken -Marker $item -MinChangeVersion $MinChangeVersion -UseSnapshot:($side -eq "Snapshot")
                foreach ($mismatch in Get-InteropMarkerMismatch -Expectation $expectation -Observation $observation) {
                    $mismatches.Add($mismatch)
                }
            }
            catch {
                $mismatches.Add("Marker $($item.Marker) on the ${side}: $($_.Exception.Message)")
            }
        }
    }

    try {
        $primaryVersion = Get-InteropChangeVersion -DmsUrl $DmsUrl -Token $ReaderToken
        $snapshotVersion = Get-InteropChangeVersion -DmsUrl $DmsUrl -Token $ReaderToken -UseSnapshot
        if ($snapshotVersion.Newest -ge $primaryVersion.Newest) {
            $mismatches.Add("The snapshot's newest change version ($($snapshotVersion.Newest)) should be below the primary's ($($primaryVersion.Newest)) after the post-snapshot changes.")
        }
    }
    catch {
        $mismatches.Add("Change versions: $($_.Exception.Message)")
    }

    if ($mismatches.Count -gt 0) {
        throw "The source arrangement is not visible as designed ($($mismatches.Count) mismatches):`n  $($mismatches -join "`n  ")"
    }

    Write-InteropStep "Arrangement self-check passed: $($Marker.Count) markers read as expected on the primary and through the snapshot."
    return $true
}

<#
.SYNOPSIS
    Merges the arrangement into the run folder's manifest.json (creating it when absent) and returns its path.
#>
function Add-InteropArrangementToManifest {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes one file in a git-ignored run folder; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$RunFolder,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Arrangement
    )

    $path = Join-Path $RunFolder "manifest.json"
    $manifest = [ordered]@{}
    if (Test-Path -LiteralPath $path) {
        $manifest = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $path -Raw) -AsHashtable -Depth 20
    }
    $manifest["arrangement"] = $Arrangement

    return Write-InteropRunManifest -RunFolder $RunFolder -Manifest $manifest
}

<#
.SYNOPSIS
    Arranges the source so each Publisher surface is distinguishable between the snapshot and the primary,
    and returns the marker map.

.DESCRIPTION
    Sequence:
      0. Create the run-specific base set through the source writer: the descriptors and school the markers
         need, and every marker document that exists at the baseline.
      1. Baseline publish (Run A, --ignoreIsolation=true --includeDescriptors=true) so the target holds the
         starting state, and the same baseline into every AdditionalBaselineTargetWriter. T0 is the source's
         newest change version read right after them.
      2. Pre-snapshot changes on the primary: U1 (update), D1 (delete), K1 (key change).
      3. Create and register the snapshot, wait for Active, and record its newest change version.
      4. Post-snapshot changes on the primary: I2 (insert), U2, D2, K2.
      5. The self-check (Test-InteropArrangementVisibility), then the manifest.
    There is no GET-by-id arrangement: the Publisher never issues a source GET-by-id against a DMS target.
    The returned Snapshot carries the in-memory connection string; never log or write it.
#>
function Set-InteropSourceArrangement {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness arrangement step on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [string]$AdminToken,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory)]
        [string]$RunFolder,

        [Parameter(Mandatory)]
        [string]$RunToken,

        [Parameter(Mandatory)]
        [long]$SourceDataStoreId,

        [Parameter(Mandatory)]
        [pscustomobject]$SourceReader,

        [Parameter(Mandatory)]
        [pscustomobject]$TargetWriter,

        [Parameter(Mandatory)]
        [string]$SourceWriterToken,

        [Parameter(Mandatory)]
        [string]$SourceReaderToken,

        [Parameter(Mandatory)]
        [string]$PublisherImage,

        [pscustomobject[]]$AdditionalBaselineTargetWriter = @()
    )

    $markers = @(New-InteropArrangementPlan -RunToken $RunToken)
    $schoolId = $markers[0].SchoolId
    $writer = @{ DmsUrl = $DmsUrl; Token = $SourceWriterToken }

    Write-InteropStep "Arrangement 0: creating the base set (school $schoolId and $(@($markers | Where-Object { $_.ExistsBefore }).Count) classPeriods) on the source primary."
    $null = Invoke-InteropDmsWrite @writer -Method Post -Path "/data/ed-fi/gradeLevelDescriptors" -Body ([ordered]@{
            codeValue = "Tenth Grade"; shortDescription = "Tenth Grade"; description = "Tenth Grade"; namespace = "uri://ed-fi.org/GradeLevelDescriptor"
        })
    $null = Invoke-InteropDmsWrite @writer -Method Post -Path "/data/ed-fi/educationOrganizationCategoryDescriptors" -Body ([ordered]@{
            codeValue = "School"; shortDescription = "School"; description = "School"; namespace = "uri://ed-fi.org/EducationOrganizationCategoryDescriptor"
        })
    $null = Invoke-InteropDmsWrite @writer -Method Post -Path "/data/ed-fi/schools" -Body ([ordered]@{
            schoolId                        = $schoolId
            nameOfInstitution               = "Interop Snapshot School $RunToken"
            gradeLevels                     = @([ordered]@{ gradeLevelDescriptor = "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" })
            educationOrganizationCategories = @([ordered]@{ educationOrganizationCategoryDescriptor = "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" })
        })

    foreach ($marker in $markers | Where-Object { $_.ExistsBefore }) {
        $null = Invoke-InteropDmsWrite @writer -Method Post -Path "/data/ed-fi/classPeriods" `
            -Body (New-InteropClassPeriodBody -Name $marker.NameBefore -SchoolId $schoolId -OfficialAttendancePeriod $marker.AttendanceBefore)
        $document = Get-InteropClassPeriod -DmsUrl $DmsUrl -Token $SourceWriterToken -Name $marker.NameBefore -SchoolId $schoolId
        if ($null -eq $document) {
            throw "Marker $($marker.Marker): classPeriod '$($marker.NameBefore)' was not found right after it was created."
        }
        $marker.DocumentId = [string]$document.id
    }

    Write-InteropStep "Arrangement 1: baseline publish (Run A, --ignoreIsolation=true) so the target holds the starting state."
    $runA = Invoke-InteropPublisher -RunFolder $RunFolder -RunName "baseline-a" -Image $PublisherImage `
        -SourceClient $SourceReader -TargetClient $TargetWriter -Option @("--ignoreIsolation=true", "--includeDescriptors=true")
    if ($runA.ExitCode -ne 0) {
        throw "The baseline publish (Run A) exited $($runA.ExitCode); expected 0. See $($runA.Folder)."
    }

    # Further targets get the same baseline before any pre-snapshot change, so each holds the starting state.
    # The source primary keeps changing after T0, so a target cannot be rebuilt at T0 later.
    $additionalBaselines = @(foreach ($extraWriter in $AdditionalBaselineTargetWriter) {
            $extraRun = Invoke-InteropPublisher -RunFolder $RunFolder -RunName "baseline-a-$($extraWriter.Role)" -Image $PublisherImage `
                -SourceClient $SourceReader -TargetClient $extraWriter -Option @("--ignoreIsolation=true", "--includeDescriptors=true")
            if ($extraRun.ExitCode -ne 0) {
                throw "The baseline publish into '$($extraWriter.Role)' exited $($extraRun.ExitCode); expected 0. See $($extraRun.Folder)."
            }
            $extraRun
        })

    $startingVersion = (Get-InteropChangeVersion -DmsUrl $DmsUrl -Token $SourceReaderToken).Newest
    Write-InteropStep "Starting change version T0 = $startingVersion (the source's newest change version right after Run A)."

    $change = {
        param($Marker)
        switch ($Marker.Kind) {
            "Delete" {
                $null = Invoke-InteropDmsWrite @writer -Method Delete -Path "/data/ed-fi/classPeriods/$($Marker.DocumentId)"
            }
            "Insert" {
                $null = Invoke-InteropDmsWrite @writer -Method Post -Path "/data/ed-fi/classPeriods" `
                    -Body (New-InteropClassPeriodBody -Name $Marker.NameAfter -SchoolId $schoolId -OfficialAttendancePeriod $Marker.AttendanceAfter)
                $created = Get-InteropClassPeriod -DmsUrl $DmsUrl -Token $SourceWriterToken -Name $Marker.NameAfter -SchoolId $schoolId
                if ($null -eq $created) {
                    throw "Marker $($Marker.Marker): the inserted classPeriod was not found right after it was created."
                }
                $Marker.DocumentId = [string]$created.id
            }
            default {
                $null = Invoke-InteropDmsWrite @writer -Method Put -Path "/data/ed-fi/classPeriods/$($Marker.DocumentId)" `
                    -Body (New-InteropClassPeriodBody -Name $Marker.NameAfter -SchoolId $schoolId -OfficialAttendancePeriod $Marker.AttendanceAfter -Id $Marker.DocumentId)
            }
        }
    }

    Write-InteropStep "Arrangement 2: pre-snapshot changes on the primary (U1, D1, K1)."
    foreach ($marker in $markers | Where-Object { $_.Phase -eq "PreSnapshot" }) {
        & $change $marker
    }

    Write-InteropStep "Arrangement 3: creating and registering the snapshot."
    $snapshot = New-InteropSnapshot -Engine $Engine -EnvironmentValues $EnvironmentValues
    $derivativeId = Register-InteropSnapshotDerivative -CmsUrl $CmsUrl -AdminToken $AdminToken -DataStoreId $SourceDataStoreId -ConnectionString $snapshot.ConnectionString
    $null = Wait-InteropSnapshotState -DmsUrl $DmsUrl -Token $SourceReaderToken -Expect Active
    $snapshotVersion = (Get-InteropChangeVersion -DmsUrl $DmsUrl -Token $SourceReaderToken -UseSnapshot).Newest
    Write-InteropStep "The snapshot's newest change version is $snapshotVersion."

    Write-InteropStep "Arrangement 4: post-snapshot changes on the primary (I2, U2, D2, K2)."
    foreach ($marker in $markers | Where-Object { $_.Phase -eq "PostSnapshot" }) {
        & $change $marker
    }

    $primaryVersion = (Get-InteropChangeVersion -DmsUrl $DmsUrl -Token $SourceReaderToken).Newest
    $null = Test-InteropArrangementVisibility -DmsUrl $DmsUrl -ReaderToken $SourceReaderToken -Marker $markers -MinChangeVersion $startingVersion

    $manifestArrangement = ConvertTo-InteropArrangementManifest -Marker $markers -StartingChangeVersion $startingVersion `
        -SnapshotNewestChangeVersion $snapshotVersion -PrimaryNewestChangeVersion $primaryVersion `
        -BaselineExitCode $runA.ExitCode -RunToken $RunToken -SnapshotName $snapshot.SnapshotName
    $manifestPath = Add-InteropArrangementToManifest -RunFolder $RunFolder -Arrangement $manifestArrangement
    Write-InteropStep "Marker map written to $manifestPath."

    return [pscustomobject]@{
        RunToken                    = $RunToken
        SchoolId                    = $schoolId
        Markers                     = $markers
        StartingChangeVersion       = $startingVersion
        SnapshotNewestChangeVersion = $snapshotVersion
        PrimaryNewestChangeVersion  = $primaryVersion
        BaselinePublisher           = $runA
        AdditionalBaselinePublisher = $additionalBaselines
        Snapshot                    = $snapshot
        DerivativeId                = $derivativeId
        Manifest                    = $manifestArrangement
    }
}

#endregion

#region Publisher runs and request attribution

# The source surfaces one Publisher extraction can issue against DMS. GetById is listed so a line or
# request that would be a GET-by-id is reported rather than dropped, even though Publisher v1.4.0 never
# issues one against a DMS target.
$script:SourceSurfaceNames = @(
    "GetMany", "Partitions", "CursorPage", "TotalCount", "Deletes", "KeyChanges", "AvailableChangeVersions", "GetById"
)

$script:GuidSegmentPattern = "/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"

<#
.SYNOPSIS
    Builds the name of a Publisher container for one run. Every container the harness creates carries the
    dms1370- prefix so it can be recognized, and removed, by name.
#>
function Get-InteropPublisherContainerName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$RunName
    )

    if ($RunName -notmatch "^[a-z0-9][a-z0-9-]{0,60}$") {
        throw "Run name '$RunName' must be lowercase letters, digits, and hyphens (at most 61 characters)."
    }

    return "dms1370-pub-$RunName"
}

<#
.SYNOPSIS
    Builds the Publisher command-line arguments for one run, with the keys and secrets either in clear
    (to run the container) or replaced by placeholders (to record in the run folder).

.DESCRIPTION
    Both names are always set, because the Publisher requests availableChangeVersions only when both
    --sourceName and --targetName are present. Option carries the run's own arguments, for example
    --ignoreIsolation=true or --lastChangeVersionProcessed=N.
#>
function Get-InteropPublisherArgument {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [string]$SourceUrl,

        [Parameter(Mandatory)]
        [string]$TargetUrl,

        [string]$SourceName = "dms1370-source",

        [string]$TargetName = "dms1370-target",

        [Parameter(Mandatory)]
        [string]$SourceKey,

        [Parameter(Mandatory)]
        [string]$SourceSecret,

        [Parameter(Mandatory)]
        [string]$TargetKey,

        [Parameter(Mandatory)]
        [string]$TargetSecret,

        [string[]]$Option = @(),

        [switch]$Redact
    )

    $sourceKeyValue = if ($Redact) { "<SOURCE_KEY>" } else { $SourceKey }
    $sourceSecretValue = if ($Redact) { "<SOURCE_SECRET>" } else { $SourceSecret }
    $targetKeyValue = if ($Redact) { "<TARGET_KEY>" } else { $TargetKey }
    $targetSecretValue = if ($Redact) { "<TARGET_SECRET>" } else { $TargetSecret }

    return @(
        "--sourceName=$SourceName"
        "--sourceUrl=$SourceUrl"
        "--sourceKey=$sourceKeyValue"
        "--sourceSecret=$sourceSecretValue"
        "--targetName=$TargetName"
        "--targetUrl=$TargetUrl"
        "--targetKey=$targetKeyValue"
        "--targetSecret=$targetSecretValue"
    ) + @($Option)
}

<#
.SYNOPSIS
    Parses a docker timestamp (up to nine fractional digits) into a UTC datetime.
#>
function ConvertFrom-InteropDockerTimestamp {
    [CmdletBinding()]
    [OutputType([datetime])]
    param(
        [Parameter(Mandatory)]
        [string]$Text
    )

    # .NET parses at most seven fractional digits.
    $trimmed = $Text.Trim() -replace "(\.\d{7})\d+", '$1'
    return [datetimeoffset]::Parse($trimmed, [System.Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}

<#
.SYNOPSIS
    Returns the docker logs --since and --until values for a container run window.

.DESCRIPTION
    docker logs works in whole seconds here, so Since is the start floored to the second and Until is the
    end rounded up to the next whole second plus one more, which keeps the last requests of the run in the
    capture. The extra margin is trimmed again by filtering parsed records to the exact window.
#>
function Get-InteropLogWindow {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [datetime]$StartedUtc,

        [Parameter(Mandatory)]
        [datetime]$FinishedUtc
    )

    $format = "yyyy-MM-dd'T'HH:mm:ss'Z'"
    $start = $StartedUtc.ToUniversalTime()
    $finish = $FinishedUtc.ToUniversalTime()
    $since = [datetime]::new($start.Year, $start.Month, $start.Day, $start.Hour, $start.Minute, $start.Second, [System.DateTimeKind]::Utc)
    $floorFinish = [datetime]::new($finish.Year, $finish.Month, $finish.Day, $finish.Hour, $finish.Minute, $finish.Second, [System.DateTimeKind]::Utc)
    $until = $floorFinish.AddSeconds(2)

    return [pscustomobject]@{
        Since = $since.ToString($format, [System.Globalization.CultureInfo]::InvariantCulture)
        Until = $until.ToString($format, [System.Globalization.CultureInfo]::InvariantCulture)
    }
}

<#
.SYNOPSIS
    Fails when a file holds any of the given secret values.

.DESCRIPTION
    The run folder keeps the Publisher Debug log, which is meant to carry no credentials. This is the
    check that proves it for the run just made.
#>
function Assert-InteropFileHasNoSecret {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Path,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string[]]$Secret
    )

    foreach ($file in $Path) {
        if (-not (Test-Path -LiteralPath $file)) {
            continue
        }

        $content = Get-Content -LiteralPath $file -Raw
        if ([string]::IsNullOrEmpty($content)) {
            continue
        }

        foreach ($value in $Secret) {
            if (-not [string]::IsNullOrEmpty($value) -and $content.Contains($value)) {
                throw "A credential value was found in '$file'; refusing to keep it."
            }
        }
    }
}

<#
.SYNOPSIS
    Runs the pinned Publisher image once against the DMS stack and returns what happened.

.DESCRIPTION
    The container joins the DMS compose network (so Discovery URLs, which follow the request Host header,
    resolve in-network), mounts the harness Publisher configuration read-only plus a logs folder inside the
    run folder, and writes its Debug log there. Keys and secrets are handed over as container arguments
    and are never written to the run folder: arguments.txt holds a redacted copy.

    The container is named dms1370-pub-<RunName>, polled to completion with a bounded wait, read once for
    its exit code and window, and removed by name. A Publisher exit code other than zero is returned to the
    caller, not thrown: a scenario that expects a failing run (no snapshot) asserts on it.
#>
function Invoke-InteropPublisher {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Runs a throwaway container on a local test stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$RunFolder,

        [Parameter(Mandatory)]
        [string]$RunName,

        [Parameter(Mandatory)]
        [string]$Image,

        [Parameter(Mandatory)]
        [pscustomobject]$SourceClient,

        [Parameter(Mandatory)]
        [pscustomobject]$TargetClient,

        [string[]]$Option = @(),

        [string]$SourceUrl = "http://ed-fi-api:8080",

        [string]$TargetUrl = "http://ed-fi-api:8080",

        [string]$Network = "dms",

        [int]$TimeoutSeconds = 1800
    )

    $containerName = Get-InteropPublisherContainerName -RunName $RunName
    $publisherFolder = Join-Path $RunFolder "publisher-$RunName"
    if (Test-Path -LiteralPath $publisherFolder) {
        throw "Publisher run folder '$publisherFolder' already exists; run names must be unique within a run."
    }

    $configFolder = Join-Path $publisherFolder "config"
    $logFolder = Join-Path $publisherFolder "logs"
    $null = New-Item -ItemType Directory -Path $configFolder, $logFolder -Force
    Copy-Item -Path (Join-Path $PSScriptRoot "publisher-config/*.json") -Destination $configFolder

    $argumentParameters = @{
        SourceUrl    = $SourceUrl
        TargetUrl    = $TargetUrl
        SourceKey    = $SourceClient.Key
        SourceSecret = $SourceClient.Secret
        TargetKey    = $TargetClient.Key
        TargetSecret = $TargetClient.Secret
        Option       = $Option
    }
    $publisherArgument = Get-InteropPublisherArgument @argumentParameters
    $redactedArgument = Get-InteropPublisherArgument @argumentParameters -Redact
    $redactedArgument | Set-Content -LiteralPath (Join-Path $publisherFolder "arguments.txt")

    $dockerArgument = @(
        "run", "-d", "--name", $containerName, "--platform", "linux/amd64", "--network", $Network, "-w", "/app"
        "-v", "$((Join-Path $configFolder 'apiPublisherSettings.json')):/app/apiPublisherSettings.json:ro"
        "-v", "$((Join-Path $configFolder 'configurationStoreSettings.json')):/app/configurationStoreSettings.json:ro"
        "-v", "$((Join-Path $configFolder 'plainTextNamedConnections.json')):/app/plainTextNamedConnections.json:ro"
        "-v", "$((Join-Path $configFolder 'logging.json')):/app/logging.json:ro"
        "-v", "${logFolder}:/app/logs"
        "--entrypoint", "dotnet", $Image, "EdFiApiPublisher.dll"
    ) + $publisherArgument

    Write-InteropStep "Publisher run '$RunName': $($redactedArgument -join ' ')"
    try {
        $global:LASTEXITCODE = 0
        $startOutput = & docker @dockerArgument 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "docker run for '$containerName' failed with exit code ${LASTEXITCODE}: $($startOutput -join ' ' -replace '--source(Key|Secret)=\S+|--target(Key|Secret)=\S+', '<redacted>')"
        }

        try {
            Wait-InteropCondition -Description "Publisher container $containerName to exit" -TimeoutSeconds $TimeoutSeconds -IntervalSeconds 1 -Test {
                $global:LASTEXITCODE = 0
                $status = & docker inspect -f "{{.State.Status}}" $containerName 2>&1
                ($LASTEXITCODE -eq 0) -and ([string]$status -eq "exited")
            }
        }
        catch {
            $null = & docker stop $containerName 2>&1
            throw
        }

        # Read straight into a variable, never through a pipe, and check the exit code of docker itself first.
        $global:LASTEXITCODE = 0
        $inspect = & docker inspect -f "{{.State.ExitCode}} {{.State.StartedAt}} {{.State.FinishedAt}} {{.State.OOMKilled}}" $containerName 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "docker inspect of '$containerName' failed with exit code ${LASTEXITCODE}: $($inspect -join ' ')"
        }

        $parts = ([string]$inspect).Trim() -split "\s+"
        $exitCode = [int]$parts[0]
        $startedUtc = ConvertFrom-InteropDockerTimestamp -Text $parts[1]
        $finishedUtc = ConvertFrom-InteropDockerTimestamp -Text $parts[2]

        $global:LASTEXITCODE = 0
        $consoleOutput = & docker logs $containerName 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "docker logs of '$containerName' failed with exit code $LASTEXITCODE."
        }
        $consolePath = Join-Path $publisherFolder "console.log"
        $consoleOutput | Set-Content -LiteralPath $consolePath
    }
    finally {
        # Remove only the container this function created, by name.
        $null = & docker rm -f $containerName 2>&1
    }

    $debugLogPath = Join-Path $logFolder "publisher.serilog.log"
    Set-Content -LiteralPath (Join-Path $publisherFolder "exit-code.txt") -Value $exitCode
    Assert-InteropFileHasNoSecret -Path @($debugLogPath, $consolePath) `
        -Secret @($SourceClient.Key, $SourceClient.Secret, $TargetClient.Key, $TargetClient.Secret)

    Write-InteropStep "Publisher run '$RunName' exited $exitCode after $([math]::Round(($finishedUtc - $startedUtc).TotalSeconds, 1)) s."
    return [pscustomobject]@{
        RunName         = $RunName
        ContainerName   = $containerName
        ExitCode        = $exitCode
        StartedUtc      = $startedUtc
        FinishedUtc     = $finishedUtc
        Folder          = $publisherFolder
        DebugLogPath    = $debugLogPath
        ConsoleLogPath  = $consolePath
        ArgumentsPath   = (Join-Path $publisherFolder "arguments.txt")
    }
}

<#
.SYNOPSIS
    Classifies one source request, by method, path, and query string, into a surface.

.DESCRIPTION
    Writes, authentication, discovery, and metadata requests are named so they are not mistaken for source
    surfaces. A GET whose shape matches no known surface is "Unknown", never dropped. The path decides
    /partitions, /deletes, and /keyChanges before the query is looked at, so a totalCount read of
    /deletes is a Deletes request, which is how the Publisher's own log counts it.
#>
function Get-InteropRequestSurface {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Method,

        [Parameter(Mandatory)]
        [string]$Path,

        [string]$Query = ""
    )

    if ($Path.StartsWith("/oauth", [System.StringComparison]::OrdinalIgnoreCase)) { return "Authentication" }
    if ($Path -eq "/" -or $Path.StartsWith("/metadata", [System.StringComparison]::OrdinalIgnoreCase)) { return "Discovery" }
    if ($Path -ieq "/changeQueries/v1/availableChangeVersions") { return "AvailableChangeVersions" }
    if (-not $Path.StartsWith("/data/", [System.StringComparison]::OrdinalIgnoreCase)) { return "Other" }
    if ($Method -ine "GET") { return "Write" }

    if ($Path.EndsWith("/partitions", [System.StringComparison]::OrdinalIgnoreCase)) { return "Partitions" }
    if ($Path.EndsWith("/deletes", [System.StringComparison]::OrdinalIgnoreCase)) { return "Deletes" }
    if ($Path.EndsWith("/keyChanges", [System.StringComparison]::OrdinalIgnoreCase)) { return "KeyChanges" }
    if ($Query -match "(^|[?&])pageToken=") { return "CursorPage" }
    if ($Query -match "(^|[?&])totalCount=true(&|$)") { return "TotalCount" }
    if ($Query -match "(^|[?&])offset=") { return "GetMany" }
    if ($Path -match $script:GuidSegmentPattern) { return "GetById" }

    return "Unknown"
}

<#
.SYNOPSIS
    Parses a "Selected {Kind} target for data store {Id} - TraceId: {TraceId}" log line.

.DESCRIPTION
    Accepts the DMS JSON console event (using its structured properties) or the plain rendered text.
    Returns $null for any other line, including the "A snapshot was requested but ..." lines.
#>
function ConvertFrom-InteropSelectionLine {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Line
    )

    if ($Line.Length -eq 0 -or -not $Line.Contains("target for data store")) {
        return $null
    }

    $timestamp = $null
    $kind = $null
    $dataStoreId = $null
    $traceId = $null

    if ($Line.StartsWith("{")) {
        try { $entry = $Line | ConvertFrom-Json } catch { return $null }
        $properties = $entry.PSObject.Properties["Properties"]
        $template = $entry.PSObject.Properties["MessageTemplate"]
        if ($null -eq $properties -or $null -eq $template -or [string]$template.Value -notlike "Selected {TargetKind} target for data store*") {
            return $null
        }

        $kind = [string]$properties.Value.TargetKind
        $dataStoreId = [string]$properties.Value.DataStoreId
        $traceId = [string]$properties.Value.TraceId
        $timestamp = ConvertFrom-InteropLogTimestamp -Line $Line
    }
    elseif ($Line -match 'Selected "?(?<kind>[A-Za-z]+)"? target for data store "?(?<id>\d+)"? - TraceId: "?(?<trace>[\w:]+)"?') {
        $kind = $Matches["kind"]
        $dataStoreId = $Matches["id"]
        $traceId = $Matches["trace"]
    }
    else {
        return $null
    }

    if ([string]::IsNullOrEmpty($kind) -or $dataStoreId -notmatch "^\d+$") {
        return $null
    }

    return [pscustomobject]@{
        Kind        = $kind
        DataStoreId = [long]$dataStoreId
        TraceId     = $traceId
        TimestampUtc = $timestamp
    }
}

<#
.SYNOPSIS
    Reads the top-level Timestamp of a DMS JSON log line without parsing the whole event.
#>
function ConvertFrom-InteropLogTimestamp {
    [CmdletBinding()]
    [OutputType([Nullable[datetime]])]
    param(
        [Parameter(Mandatory)]
        [string]$Line
    )

    if ($Line -match '^\{"Timestamp":"(?<ts>[^"]+)"') {
        $parsed = [datetimeoffset]::MinValue
        if ([datetimeoffset]::TryParse($Matches["ts"], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$parsed)) {
            return $parsed.UtcDateTime
        }
    }

    return $null
}

<#
.SYNOPSIS
    Reads the DMS container log for a window as lines of text.
#>
function Get-InteropDmsLogLine {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [string]$Since,

        [Parameter(Mandatory)]
        [string]$Until,

        [string]$ContainerName = "ed-fi-api"
    )

    $global:LASTEXITCODE = 0
    $output = & docker logs $ContainerName --since $Since --until $Until 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "docker logs of '$ContainerName' failed with exit code $LASTEXITCODE."
    }

    return [string[]]@($output | ForEach-Object { [string]$_ })
}

<#
.SYNOPSIS
    Counts the "Selected {Kind} target" lines for one data store, per kind.

.DESCRIPTION
    Lines for any other data store (the target, for example) are not counted. Primary and Snapshot are
    always present in the result, with zero when absent; any other kind appears too.
#>
function Get-InteropTargetSelections {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'The plural names are the harness vocabulary: the function returns counts per kind or per surface.')]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Line,

        [Parameter(Mandatory)]
        [long]$DataStoreId,

        [Nullable[datetime]]$FromUtc = $null,

        [Nullable[datetime]]$ToUtc = $null
    )

    $counts = [ordered]@{ Primary = 0; Snapshot = 0 }
    foreach ($text in $Line) {
        $selection = ConvertFrom-InteropSelectionLine -Line $text
        if ($null -eq $selection -or $selection.DataStoreId -ne $DataStoreId) {
            continue
        }

        if ($null -ne $selection.TimestampUtc) {
            if (($null -ne $FromUtc -and $selection.TimestampUtc -lt $FromUtc) -or ($null -ne $ToUtc -and $selection.TimestampUtc -gt $ToUtc)) {
                continue
            }
        }

        if (-not $counts.Contains($selection.Kind)) {
            $counts[$selection.Kind] = 0
        }
        $counts[$selection.Kind]++
    }

    return $counts
}

<#
.SYNOPSIS
    Joins the DMS "Request starting", HttpRequestCompleted, and "Selected {Kind} target" events into one
    record per request.

.DESCRIPTION
    "Request starting" carries Method, Path, QueryString, and RequestId. HttpRequestCompleted (the
    frontend event, not the "core request" one) carries StatusCode and a TraceId equal to that RequestId.
    The "Selected ... target" line carries the same TraceId, plus the data store id and target kind. A
    request with no selection line (token, Discovery, and change-query calls do not pass the middleware)
    has a null DataStoreId and TargetKind. Records are returned in the order the requests started.
#>
function ConvertTo-InteropDmsRequest {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Line,

        [Nullable[datetime]]$FromUtc = $null,

        [Nullable[datetime]]$ToUtc = $null
    )

    $requests = [ordered]@{}
    $completed = @{}
    $selected = @{}

    foreach ($text in $Line) {
        if ($text.Length -lt 2 -or $text[0] -ne "{") {
            continue
        }

        if ($text.Contains('"MessageTemplate":"Request starting')) {
            $entry = $text | ConvertFrom-Json
            $properties = $entry.Properties
            $requests[[string]$properties.RequestId] = [pscustomobject]@{
                RequestId    = [string]$properties.RequestId
                TimestampUtc = ConvertFrom-InteropLogTimestamp -Line $text
                Method       = [string]$properties.Method
                Path         = [string]$properties.Path
                Query        = [string]$properties.QueryString
            }
        }
        elseif ($text.Contains('"EventName":"HttpRequestCompleted"') -and -not $text.Contains("DMS core request completed")) {
            $entry = $text | ConvertFrom-Json
            $completed[[string]$entry.Properties.TraceId] = [int]$entry.Properties.StatusCode
        }
        elseif ($text.Contains("target for data store")) {
            $selection = ConvertFrom-InteropSelectionLine -Line $text
            if ($null -ne $selection) {
                $selected[$selection.TraceId] = $selection
            }
        }
    }

    foreach ($request in $requests.Values) {
        if ($null -ne $request.TimestampUtc) {
            if (($null -ne $FromUtc -and $request.TimestampUtc -lt $FromUtc) -or ($null -ne $ToUtc -and $request.TimestampUtc -gt $ToUtc)) {
                continue
            }
        }

        $selection = $selected[$request.RequestId]
        [pscustomobject]@{
            RequestId    = $request.RequestId
            TimestampUtc = $request.TimestampUtc
            Method       = $request.Method
            Path         = $request.Path
            Query        = $request.Query
            StatusCode   = $(if ($completed.ContainsKey($request.RequestId)) { $completed[$request.RequestId] } else { $null })
            Surface      = Get-InteropRequestSurface -Method $request.Method -Path $request.Path -Query $request.Query
            DataStoreId  = $(if ($null -ne $selection) { $selection.DataStoreId } else { $null })
            TargetKind   = $(if ($null -ne $selection) { $selection.Kind } else { $null })
        }
    }
}

<#
.SYNOPSIS
    Counts joined DMS requests per surface, optionally for one data store and one target kind.

.DESCRIPTION
    Counts holds every source surface (zero when absent) plus any other surface seen (Write, Discovery,
    Authentication, Unknown, and so on). Unclassified lists the requests whose shape matched no surface.
#>
function Get-InteropSurfaceCount {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [pscustomobject[]]$Request,

        [Nullable[long]]$DataStoreId = $null,

        [string]$TargetKind = ""
    )

    $counts = [ordered]@{}
    foreach ($name in $script:SourceSurfaceNames) {
        $counts[$name] = 0
    }

    $unclassified = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $Request) {
        if ($null -ne $DataStoreId -and $item.DataStoreId -ne $DataStoreId) {
            continue
        }
        if ($TargetKind -ne "" -and $item.TargetKind -ne $TargetKind) {
            continue
        }

        if (-not $counts.Contains($item.Surface)) {
            $counts[$item.Surface] = 0
        }
        $counts[$item.Surface]++
        if ($item.Surface -eq "Unknown") {
            $unclassified.Add("$($item.Method) $($item.Path)$($item.Query)")
        }
    }

    return [pscustomobject]@{ Counts = $counts; Unclassified = $unclassified.ToArray() }
}

<#
.SYNOPSIS
    Classifies one Publisher Debug log line as a source request, returning its surface or $null.

.DESCRIPTION
    The log carries the level tokens DBUG, INFO, WARN, EROR, and FATL, wraps resource names and URLs in
    stray double quotes, and has an unbalanced ")" on the item-count line, so nothing here assumes
    balanced quotes. Only the availableChangeVersions line carries a full URL; the others name the resource
    path and the kind of request. A line that looks like a source request but matches no known form returns
    "Unknown". A line that is not a source request returns $null.
#>
function Get-InteropPublisherLineSurface {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Line
    )

    if ($Line -notmatch '^\[[^\]]+\] \[(DBUG|INFO|WARN|EROR|FATL)\] \[[^\]]*\] \S+ - (?<message>.*)$') {
        return $null
    }

    $message = $Matches["message"]
    if ($message -match "Available change versions request from|Unable to get current change version") { return "AvailableChangeVersions" }
    if ($message -match "Attempting to retrieve missing") { return "GetById" }

    $resource = ""
    if ($message -match '^"?(?<resource>/[A-Za-z0-9_/-]+)') {
        $resource = $Matches["resource"]
    }

    if ($message -match "Probing source API for cursor paging support") { return "Partitions" }
    if ($message -match "Probing source API for deletes support") { return "Deletes" }
    if ($message -match "Probing source API for key changes support") { return "KeyChanges" }
    if ($message -match "Probing source API") { return "Unknown" }

    if ($message -match "Getting partition tokens from source") { return "Partitions" }

    if ($message -match "Getting item count from source") {
        if ($resource.EndsWith("/deletes")) { return "Deletes" }
        if ($resource.EndsWith("/keyChanges")) { return "KeyChanges" }
        return "TotalCount"
    }

    if ($message -match "Retrieving page items") {
        if ($message -match "Retrieving page items of partition \d+, page \d+") { return "CursorPage" }
        if ($message -match "Retrieving page items \d+ to \d+") {
            if ($resource.EndsWith("/deletes")) { return "Deletes" }
            if ($resource.EndsWith("/keyChanges")) { return "KeyChanges" }
            return "GetMany"
        }

        return "Unknown"
    }

    return $null
}

<#
.SYNOPSIS
    Counts the source requests in a Publisher Debug log, per surface.

.DESCRIPTION
    Counts holds the seven surfaces plus GetById (zero when absent, reported when found). Unclassified
    carries every line that looked like a source request but matched no known form.
#>
function Get-InteropSourceSurfaces {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'The plural names are the harness vocabulary: the function returns counts per kind or per surface.')]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Line
    )

    $counts = [ordered]@{}
    foreach ($name in $script:SourceSurfaceNames) {
        $counts[$name] = 0
    }

    $unclassified = [System.Collections.Generic.List[string]]::new()
    $getById = [System.Collections.Generic.List[string]]::new()
    foreach ($text in $Line) {
        $surface = Get-InteropPublisherLineSurface -Line $text
        if ($null -eq $surface) {
            continue
        }

        if ($surface -eq "Unknown") {
            $unclassified.Add($text)
            continue
        }

        $counts[$surface]++
        if ($surface -eq "GetById") {
            $getById.Add($text)
        }
    }

    return [pscustomobject]@{ Counts = $counts; Unclassified = $unclassified.ToArray(); GetByIdLines = $getById.ToArray() }
}

<#
.SYNOPSIS
    Compares per-surface counts from the Publisher log and from the DMS side, returning the differences.
#>
function Compare-InteropSurfaceCount {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$PublisherCount,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$DmsCount
    )

    foreach ($name in $script:SourceSurfaceNames) {
        $publisher = if ($PublisherCount.Contains($name)) { [int]$PublisherCount[$name] } else { 0 }
        $dms = if ($DmsCount.Contains($name)) { [int]$DmsCount[$name] } else { 0 }
        if ($publisher -ne $dms) {
            [pscustomobject]@{ Surface = $name; Publisher = $publisher; Dms = $dms }
        }
    }
}

#endregion

#region Scenario results

<#
.SYNOPSIS
    Starts a scenario result: the assertions, the operator-facing log lines, and supporting details that end
    up in results.json next to manifest.json.
#>
function New-InteropResult {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory object only.')]
    param(
        [Parameter(Mandatory)]
        [string]$Scenario,

        [Parameter(Mandatory)]
        [string]$Engine,

        [Parameter(Mandatory)]
        [string]$RunId
    )

    return [ordered]@{
        runId      = $RunId
        scenario   = $Scenario
        engine     = $Engine
        assertions = [System.Collections.Generic.List[object]]::new()
        logLines   = [ordered]@{}
        details    = [ordered]@{}
    }
}

<#
.SYNOPSIS
    Renders a value the way assertions compare it: scalars by their invariant string, collections as compact
    JSON, so 2 and "2" agree and @(404) and @(404, 404) do not.
#>
function ConvertTo-InteropComparable {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return "<null>"
    }
    if ($Value -is [string]) {
        return $Value
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        return ConvertTo-Json -InputObject $Value -Compress -Depth 10
    }

    return [System.Convert]::ToString($Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

<#
.SYNOPSIS
    Records one assertion (name, expected, observed, PASS or FAIL, and where the evidence is) and returns it.

.DESCRIPTION
    The outcome is PASS only when the expected and observed values render identically (case-sensitive).
#>
function Add-InteropAssertion {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Appends to an in-memory list only.')]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Result,

        [Parameter(Mandatory)]
        [string]$Name,

        [AllowNull()]
        [object]$Expected,

        [AllowNull()]
        [object]$Observed,

        [Parameter(Mandatory)]
        [string]$Evidence
    )

    $same = (ConvertTo-InteropComparable -Value $Expected) -ceq (ConvertTo-InteropComparable -Value $Observed)
    $assertion = [ordered]@{
        name     = $Name
        expected = $Expected
        observed = $Observed
        outcome  = $(if ($same) { "PASS" } else { "FAIL" })
        evidence = $Evidence
    }
    $Result["assertions"].Add($assertion)

    return $assertion
}

<#
.SYNOPSIS
    Returns the assertions of a result that did not pass. A result with no assertions counts as failed, so
    a scenario that asserted nothing cannot pass.
#>
function Get-InteropResultFailure {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Result
    )

    return @($Result["assertions"] | Where-Object { $_["outcome"] -ne "PASS" })
}

<#
.SYNOPSIS
    Writes results.json into the run folder and returns its path.

.DESCRIPTION
    The file is scanned for secret-shaped properties and values, and for the literal credentials in Secret,
    before it is kept. Overall is FAIL when any assertion failed or none was recorded.
#>
function Write-InteropResultFile {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes one file in a git-ignored run folder; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$RunFolder,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Result,

        [AllowEmptyCollection()]
        [string[]]$Secret = @(),

        [datetime]$FinishedUtc = [datetime]::UtcNow,

        [ValidatePattern('^results[A-Za-z0-9.-]*\.json$')]
        [string]$FileName = "results.json"
    )

    $assertions = @($Result["assertions"])
    $failed = @(Get-InteropResultFailure -Result $Result)
    $document = [ordered]@{
        runId       = $Result["runId"]
        scenario    = $Result["scenario"]
        engine      = $Result["engine"]
        finishedUtc = $FinishedUtc.ToUniversalTime().ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        overall     = $(if ($assertions.Count -gt 0 -and $failed.Count -eq 0) { "PASS" } else { "FAIL" })
        passed      = $assertions.Count - $failed.Count
        failed      = $failed.Count
        assertions  = $assertions
        logLines    = $Result["logLines"]
        details     = $Result["details"]
    }

    Assert-InteropManifestHasNoSecret -Manifest $document
    $path = Join-Path $RunFolder $FileName
    ConvertTo-Json -InputObject $document -Depth 10 | Set-Content -LiteralPath $path
    if ($Secret.Count -gt 0) {
        Assert-InteropFileHasNoSecret -Path @($path) -Secret $Secret
    }

    return $path
}

#endregion

#region NoSnapshot scenario

# The values below pin what a Publisher v1.4.0 run does against a DMS 8.1 source that has no snapshot
# when --ignoreIsolation is not passed. They were observed live (PostgreSQL, Publisher image
# edfialliance/ods-api-publisher:v1.4.0, run "b-no-snapshot", Publisher baseline spike, 2026-11-03):
#   - exit code 2, from docker wait and docker inspect;
#   - 386 DMS "404 Snapshot not found" responses: 1 availableChangeVersions, 1 partitions?number=1 probe,
#     384 limit=0&totalCount=true count reads (one per scheduled resource, none retried);
#   - 386 DMS log lines "A snapshot was requested but data store N (...) has none configured", one per
#     rejected request;
#   - 0 POST, PUT, or DELETE in the DMS log; the target kept only its seeded schoolYearTypes;
#   - one WARN (availableChangeVersions), one INFO (/partitions), one FATL (source read errors), and
#     many EROR lines (count request failures that carry the Snapshot Not Found body).
# The dataset-size numbers (386, 384) are deliberately not pinned: the scenario pins the structure and
# derives the count reads from the Publisher's own log of the same run. The retire path must
# answer exactly like the never-configured path (same status, type, title, detail, and content type).
# If a future Publisher build changes any of this, the assertions fail instead of the documentation going
# stale silently.
$script:NoSnapshotExpectation = [ordered]@{
    PublisherExitCode               = 2
    AvailableChangeVersionsStatuses = @(404)
    PartitionsProbeCount            = 1
    PartitionsProbeStatuses         = @(404)
    CountReadStatuses               = @(404)
    OtherSourceReadCount            = 0
    SourceReadTwoXxCount            = 0
    TargetWriteCount                = 0
    SnapshotActiveStatus            = 200
    NotFoundStatus                  = 404
    NotFoundContentType             = "application/problem+json"
    NotFoundType                    = "urn:ed-fi:api:not-found"
    NotFoundTitle                   = "Not Found"
    NotFoundDetail                  = "Snapshot not found."
    WarnLineText                    = "Unable to get current change version from source API"
    InfoLineText                    = "Source API does not expose /partitions"
    FatalLineText                   = "source read error(s) mean an unknown number of documents was never attempted"
    ErrorLineText                   = "Unable to obtain total count due to request failure"
    ErrorLineBodyText               = "Snapshot not found."
}

# The DMS log text written once per rejected Use-Snapshot request when no snapshot is configured.
$script:NoneConfiguredLineText = "A snapshot was requested but data store"

<#
.SYNOPSIS
    Returns the pinned NoSnapshot expectations, optionally with some values replaced.

.DESCRIPTION
    Override exists to prove the verifier can fail: pass a deliberately wrong value and the matching
    assertion must report FAIL. An unknown key throws, so a typo cannot silently weaken the check.
#>
function Get-InteropNoSnapshotExpectation {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [System.Collections.IDictionary]$Override = @{}
    )

    $expectation = [ordered]@{}
    foreach ($key in $script:NoSnapshotExpectation.Keys) {
        $expectation[$key] = $script:NoSnapshotExpectation[$key]
    }

    foreach ($key in $Override.Keys) {
        if (-not $expectation.Contains($key)) {
            throw "Unknown NoSnapshot expectation '$key'. Known: $($expectation.Keys -join ', ')."
        }
        $expectation[$key] = $Override[$key]
    }

    return $expectation
}

<#
.SYNOPSIS
    Reduces an HTTP problem response to the fields the scenario compares: status, media type, type, title,
    and detail. The correlation id differs on every request and is left out.
#>
function ConvertTo-InteropProblemSummary {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)]
        [int]$StatusCode,

        [AllowEmptyString()]
        [string]$ContentType = "",

        [AllowEmptyString()]
        [string]$Content = ""
    )

    $body = $null
    try { $body = ConvertFrom-Json -InputObject $Content -ErrorAction Stop } catch { $body = $null }

    $read = { param($Name) if ($null -ne $body -and $null -ne $body.PSObject.Properties[$Name]) { [string]$body.PSObject.Properties[$Name].Value } else { $null } }
    return [ordered]@{
        status      = $StatusCode
        contentType = ($ContentType -split ";")[0].Trim()
        type        = & $read "type"
        title       = & $read "title"
        detail      = & $read "detail"
    }
}

<#
.SYNOPSIS
    Finds the first Publisher Debug log line at a level whose message contains the text, and the
    continuation lines that follow it (lines that do not start a new timestamped entry).

.DESCRIPTION
    Returns $null when no line matches. Line is the verbatim text, so it can be quoted as the operator saw it.
#>
function Get-InteropOperatorLine {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Line,

        [Parameter(Mandatory)]
        [ValidateSet("DBUG", "INFO", "WARN", "EROR", "FATL")]
        [string]$Level,

        [Parameter(Mandatory)]
        [string]$Text
    )

    $entryStart = '^\[\d{4}-\d{2}-\d{2} [^\]]+\] \['
    for ($index = 0; $index -lt $Line.Count; $index++) {
        $candidate = $Line[$index]
        if ($candidate -notmatch ($entryStart + $Level + '\] ') -or -not $candidate.Contains($Text)) {
            continue
        }

        $continuation = [System.Collections.Generic.List[string]]::new()
        for ($next = $index + 1; $next -lt $Line.Count -and $Line[$next] -notmatch $entryStart; $next++) {
            $continuation.Add($Line[$next])
        }

        return [pscustomobject]@{ Line = $candidate; Continuation = $continuation.ToArray() }
    }

    return $null
}

<#
.SYNOPSIS
    Turns what a no-snapshot Publisher run did into plain observed values: the DMS-side requests of the run's
    window, the Publisher's own lines, the target's document counts, and the two Snapshot Not Found bodies.

.DESCRIPTION
    Pure: every input is passed in. DmsRequest is the output of ConvertTo-InteropDmsRequest for the window.
    A request with no completion event is reported with status -1.
#>
function Get-InteropNoSnapshotObservation {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [int]$ExitCode,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$PublisherLine,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [pscustomobject[]]$DmsRequest,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$DmsLine,

        [Nullable[datetime]]$FromUtc = $null,

        [Nullable[datetime]]$ToUtc = $null,

        [Parameter(Mandatory)]
        [long]$TargetDocumentsBefore,

        [Parameter(Mandatory)]
        [long]$TargetDocumentsAfter,

        [Parameter(Mandatory)]
        [int]$SnapshotActiveStatus,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Retired,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$NeverConfigured,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Expectation
    )

    $statusSet = {
        param($Items)
        [int[]]@(@($Items) | ForEach-Object { if ($null -eq $_.StatusCode) { -1 } else { [int]$_.StatusCode } } | Sort-Object -Unique)
    }

    $request = @($DmsRequest)
    $availableChangeVersions = @($request | Where-Object { $_.Surface -eq "AvailableChangeVersions" })
    $partitions = @($request | Where-Object { $_.Surface -eq "Partitions" })
    $countReads = @($request | Where-Object { $_.Surface -eq "TotalCount" })
    $otherReads = @($request | Where-Object { $_.Surface -in @("CursorPage", "GetMany", "Deletes", "KeyChanges", "GetById") })
    $writes = @($request | Where-Object { $_.Surface -eq "Write" })
    $sourceRequests = @($availableChangeVersions) + @($partitions) + @($countReads) + @($otherReads)

    $noneConfigured = 0
    foreach ($text in $DmsLine) {
        if (-not $text.Contains($script:NoneConfiguredLineText)) {
            continue
        }
        $timestamp = ConvertFrom-InteropLogTimestamp -Line $text
        if ($null -ne $timestamp -and (($null -ne $FromUtc -and $timestamp -lt $FromUtc) -or ($null -ne $ToUtc -and $timestamp -gt $ToUtc))) {
            continue
        }
        $noneConfigured++
    }

    # The Publisher's own count of the count reads it made: one "Getting item count from source" line per
    # read. It is not the number of distinct resources (the live run counted 384 reads for 381 distinct
    # resources, because the Publisher reads a few resources twice).
    $publisherCountReads = (Get-InteropSourceSurfaces -Line $PublisherLine).Counts["TotalCount"]
    $summaryLine = $PublisherLine | Where-Object { $_ -match '^\s+! \d+ source read error\(s\) occurred' } | Select-Object -First 1

    return [pscustomobject]@{
        ExitCode                        = $ExitCode
        AvailableChangeVersionsStatuses = @(& $statusSet $availableChangeVersions)
        PartitionsProbeCount            = $partitions.Count
        PartitionsProbeStatuses         = @(& $statusSet $partitions)
        CountReadCount                  = $countReads.Count
        CountReadStatuses               = @(& $statusSet $countReads)
        PublisherCountReadCount         = $publisherCountReads
        OtherSourceReadCount            = $otherReads.Count
        SourceReadTwoXxCount            = @($sourceRequests | Where-Object { $_.StatusCode -ge 200 -and $_.StatusCode -lt 300 }).Count
        RejectedSourceRequestCount      = @($sourceRequests | Where-Object { $_.StatusCode -eq 404 }).Count
        NoneConfiguredLineCount         = $noneConfigured
        TargetWriteCount                = $writes.Count
        TargetDocumentsBefore           = $TargetDocumentsBefore
        TargetDocumentsAfter            = $TargetDocumentsAfter
        SnapshotActiveStatus            = $SnapshotActiveStatus
        Retired                         = $Retired
        NeverConfigured                 = $NeverConfigured
        Warn                            = Get-InteropOperatorLine -Line $PublisherLine -Level WARN -Text $Expectation["WarnLineText"]
        Info                            = Get-InteropOperatorLine -Line $PublisherLine -Level INFO -Text $Expectation["InfoLineText"]
        Fatal                           = Get-InteropOperatorLine -Line $PublisherLine -Level FATL -Text $Expectation["FatalLineText"]
        Error                           = Get-InteropOperatorLine -Line $PublisherLine -Level EROR -Text $Expectation["ErrorLineText"]
        ProcessingFailed                = Get-InteropOperatorLine -Line $PublisherLine -Level EROR -Text "Processing failed:"
        Summary                         = $summaryLine
    }
}

<#
.SYNOPSIS
    Compares an observation with the expectations and returns one record (Name, Expected, Observed,
    Evidence) per assertion. Pure: nothing is recorded or written here.

.DESCRIPTION
    Expectations that depend on the run (the count reads must equal the count reads the Publisher logged,
    the none-configured log lines must equal the rejected requests, the target must end as it began, the
    retire body must equal the never-configured body) take their expected value from the observation, and a
    guard assertion keeps the zero-equals-zero case from passing vacuously.
#>
function Get-InteropNoSnapshotAssertion {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Expectation,

        [Parameter(Mandatory)]
        [pscustomobject]$Observation,

        [string]$PublisherLogEvidence = "publisher log",

        [string]$DmsEvidence = "DMS request log"
    )

    $e = $Expectation
    $o = $Observation
    $assertion = {
        param($Name, $Expected, $Observed, $Evidence)
        [pscustomobject]@{ Name = $Name; Expected = $Expected; Observed = $Observed; Evidence = $Evidence }
    }
    $present = { param($Entry) $null -ne $Entry }

    $pinnedProblem = [ordered]@{
        status      = $e["NotFoundStatus"]
        contentType = $e["NotFoundContentType"]
        type        = $e["NotFoundType"]
        title       = $e["NotFoundTitle"]
        detail      = $e["NotFoundDetail"]
    }

    & $assertion "The Snapshot Not Found response of a data store that never had a snapshot matches the pinned shape" $pinnedProblem $o.NeverConfigured "header read on the target data store"
    & $assertion "The Snapshot Not Found response after the snapshot is retired equals the never-configured response" $o.NeverConfigured $o.Retired "header read after the derivative was removed"
    & $assertion "A real snapshot answers Use-Snapshot reads with 200 before it is retired" $e["SnapshotActiveStatus"] $o.SnapshotActiveStatus "header read before the derivative was removed"

    & $assertion "Publisher exit code" $e["PublisherExitCode"] $o.ExitCode "exit-code.txt"
    & $assertion "availableChangeVersions status codes" $e["AvailableChangeVersionsStatuses"] $o.AvailableChangeVersionsStatuses $DmsEvidence
    & $assertion "partitions probe requests" $e["PartitionsProbeCount"] $o.PartitionsProbeCount $DmsEvidence
    & $assertion "partitions probe status codes" $e["PartitionsProbeStatuses"] $o.PartitionsProbeStatuses $DmsEvidence
    & $assertion "count read status codes" $e["CountReadStatuses"] $o.CountReadStatuses $DmsEvidence
    & $assertion "The Publisher made at least one count read" $true ($o.PublisherCountReadCount -gt 0) $PublisherLogEvidence
    & $assertion "DMS count reads equal the count reads the Publisher logged" $o.PublisherCountReadCount $o.CountReadCount "$PublisherLogEvidence and $DmsEvidence"
    & $assertion "Other source reads (cursor pages, offset pages, deletes, keyChanges, get-by-id)" $e["OtherSourceReadCount"] $o.OtherSourceReadCount $DmsEvidence
    & $assertion "Source reads that answered 2xx" $e["SourceReadTwoXxCount"] $o.SourceReadTwoXxCount $DmsEvidence
    & $assertion "DMS none-configured log lines equal the rejected source requests" $o.RejectedSourceRequestCount $o.NoneConfiguredLineCount "DMS container log"
    & $assertion "POST, PUT, and DELETE requests that reached DMS" $e["TargetWriteCount"] $o.TargetWriteCount $DmsEvidence
    & $assertion "Target documents after the run equal the target documents before it" $o.TargetDocumentsBefore $o.TargetDocumentsAfter "target database row count"

    & $assertion "The WARN line about availableChangeVersions is present" $true (& $present $o.Warn) $PublisherLogEvidence
    & $assertion "The INFO line about /partitions is present" $true (& $present $o.Info) $PublisherLogEvidence
    & $assertion "The FATL line about source read errors is present" $true (& $present $o.Fatal) $PublisherLogEvidence
    & $assertion "An EROR count-failure line is present" $true (& $present $o.Error) $PublisherLogEvidence
    & $assertion "The EROR count-failure line carries the Snapshot Not Found body" $true ($null -ne $o.Error -and (@($o.Error.Line) + @($o.Error.Continuation) -join "`n").Contains($e["ErrorLineBodyText"])) $PublisherLogEvidence
}

<#
.SYNOPSIS
    Counts the documents in a data store database, to show what a Publisher run did or did not write.
#>
function Get-InteropDocumentCount {
    [CmdletBinding()]
    [OutputType([long])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory)]
        [string]$DatabaseName
    )

    Assert-InteropSqlIdentifier -Value $DatabaseName -What "target database name"
    if ($Engine -eq "postgresql") {
        $text = Invoke-InteropSql -Engine $Engine -EnvironmentValues $EnvironmentValues -Database $DatabaseName -Sql 'SELECT count(*) FROM dms."Document"'
    }
    else {
        $text = Invoke-InteropSql -Engine $Engine -EnvironmentValues $EnvironmentValues -Sql "SELECT COUNT_BIG(*) FROM $DatabaseName.dms.Document"
    }

    $number = @($text -split "\r?\n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match "^\d+$" }) | Select-Object -First 1
    if ($null -eq $number) {
        throw "Could not read a document count for '$DatabaseName' from: $text"
    }

    return [long]$number
}

<#
.SYNOPSIS
    Runs the NoSnapshot scenario: a Publisher run against a source with no snapshot and no
    --ignoreIsolation, asserted against the pinned expectations, and writes results.json.

.DESCRIPTION
    1. Registers a real snapshot, reads once through Use-Snapshot (200), removes the derivative, waits for
       Absent, and reads once more: that 404 body is the retire path.
    2. Reads the same shape from the fresh target data store, which never had a snapshot: that 404 body is
       the never-configured path. The two must be identical.
    3. Runs the Publisher (names set, --includeDescriptors=true, no --ignoreIsolation) into the fresh
       target, then joins the DMS log of the run's window to the Publisher's own lines.
    The snapshot database and login are dropped afterwards. Returns the result; the caller decides the exit
    code from Get-InteropResultFailure.
#>
function Invoke-InteropNoSnapshotScenario {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness scenario on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [string]$AdminToken,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory)]
        [string]$RunFolder,

        [Parameter(Mandatory)]
        [string]$RunId,

        [Parameter(Mandatory)]
        [pscustomobject]$SourceStore,

        [Parameter(Mandatory)]
        [pscustomobject]$TargetStore,

        [Parameter(Mandatory)]
        [pscustomobject]$SourceReader,

        [Parameter(Mandatory)]
        [pscustomobject]$TargetWriter,

        [Parameter(Mandatory)]
        [string]$SourceReaderToken,

        [Parameter(Mandatory)]
        [string]$TargetWriterToken,

        [Parameter(Mandatory)]
        [string]$PublisherImage,

        [System.Collections.IDictionary]$ExpectationOverride = @{},

        [string]$ResultFileName = "results.json"
    )

    $expectation = Get-InteropNoSnapshotExpectation -Override $ExpectationOverride
    if ($ExpectationOverride.Count -gt 0) {
        Write-InteropStep "NOTE: expectations overridden for $($ExpectationOverride.Keys -join ', '); this run verifies the verifier, not the Publisher."
    }

    $result = New-InteropResult -Scenario "NoSnapshot" -Engine $Engine -RunId $RunId
    $probePath = "/data/ed-fi/schoolYearTypes?limit=1"
    $summarize = {
        param($Probe)
        ConvertTo-InteropProblemSummary -StatusCode $Probe.StatusCode -ContentType $Probe.ContentType -Content $Probe.Content
    }

    $snapshot = $null
    $derivativeId = $null
    try {
        Write-InteropStep "NoSnapshot 1: registering a real snapshot so the retire path is real."
        $snapshot = New-InteropSnapshot -Engine $Engine -EnvironmentValues $EnvironmentValues
        $derivativeId = Register-InteropSnapshotDerivative -CmsUrl $CmsUrl -AdminToken $AdminToken -DataStoreId $SourceStore.Id -ConnectionString $snapshot.ConnectionString
        $null = Wait-InteropSnapshotState -DmsUrl $DmsUrl -Token $SourceReaderToken -Expect Active
        $active = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path $probePath -Token $SourceReaderToken -UseSnapshot

        Write-InteropStep "NoSnapshot 2: removing the snapshot derivative and waiting for Absent."
        Remove-InteropSnapshotDerivative -CmsUrl $CmsUrl -AdminToken $AdminToken -DerivativeId $derivativeId
        $derivativeId = $null
        $null = Wait-InteropSnapshotState -DmsUrl $DmsUrl -Token $SourceReaderToken -Expect Absent
        $retired = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path $probePath -Token $SourceReaderToken -UseSnapshot

        Write-InteropStep "NoSnapshot 3: reading the same shape from the target data store, which never had a snapshot."
        $never = Invoke-InteropDmsProbe -DmsUrl $DmsUrl -Path $probePath -Token $TargetWriterToken -UseSnapshot

        $targetBefore = Get-InteropDocumentCount -Engine $Engine -EnvironmentValues $EnvironmentValues -DatabaseName $TargetStore.DatabaseName

        Write-InteropStep "NoSnapshot 4: Publisher run without --ignoreIsolation into the fresh target."
        $run = Invoke-InteropPublisher -RunFolder $RunFolder -RunName "no-snapshot" -Image $PublisherImage `
            -SourceClient $SourceReader -TargetClient $TargetWriter -Option @("--includeDescriptors=true")

        $targetAfter = Get-InteropDocumentCount -Engine $Engine -EnvironmentValues $EnvironmentValues -DatabaseName $TargetStore.DatabaseName
    }
    finally {
        if ($null -ne $derivativeId) {
            try { Remove-InteropSnapshotDerivative -CmsUrl $CmsUrl -AdminToken $AdminToken -DerivativeId $derivativeId } catch { Write-InteropStep "Cleanup could not remove the derivative: $($_.Exception.Message)" }
        }
        if ($null -ne $snapshot) {
            try {
                Remove-InteropSnapshot -Engine $Engine -EnvironmentValues $EnvironmentValues -SnapshotName $snapshot.SnapshotName `
                    -LoginName $snapshot.LoginName -SourceDatabaseName $snapshot.SourceDatabaseName
            }
            catch { Write-InteropStep "Cleanup could not drop the snapshot '$($snapshot.SnapshotName)': $($_.Exception.Message)" }
        }
    }

    $window = Get-InteropLogWindow -StartedUtc $run.StartedUtc -FinishedUtc $run.FinishedUtc
    $dmsLine = Get-InteropDmsLogLine -Since $window.Since -Until $window.Until
    $dmsRequest = @(ConvertTo-InteropDmsRequest -Line $dmsLine -FromUtc $run.StartedUtc -ToUtc $run.FinishedUtc)
    $publisherLine = [string[]]@(Get-Content -LiteralPath $run.DebugLogPath)

    $requestSummaryPath = Join-Path $run.Folder "dms-requests.json"
    ConvertTo-Json -InputObject @($dmsRequest | ForEach-Object {
            [ordered]@{ method = $_.Method; path = $_.Path; query = $_.Query; status = $_.StatusCode; surface = $_.Surface }
        }) -Depth 4 | Set-Content -LiteralPath $requestSummaryPath

    $observation = Get-InteropNoSnapshotObservation -ExitCode $run.ExitCode -PublisherLine $publisherLine `
        -DmsRequest $dmsRequest -DmsLine $dmsLine -FromUtc $run.StartedUtc -ToUtc $run.FinishedUtc `
        -TargetDocumentsBefore $targetBefore -TargetDocumentsAfter $targetAfter -SnapshotActiveStatus $active.StatusCode `
        -Retired (& $summarize $retired) -NeverConfigured (& $summarize $never) -Expectation $expectation

    $relative = { param($Path) [System.IO.Path]::GetRelativePath($RunFolder, $Path) }
    $assertions = Get-InteropNoSnapshotAssertion -Expectation $expectation -Observation $observation `
        -PublisherLogEvidence (& $relative $run.DebugLogPath) -DmsEvidence (& $relative $requestSummaryPath)
    foreach ($item in $assertions) {
        $recorded = Add-InteropAssertion -Result $result -Name $item.Name -Expected $item.Expected -Observed $item.Observed -Evidence $item.Evidence
        Write-InteropStep ("{0}: {1} (expected {2}, observed {3})" -f $recorded["outcome"], $recorded["name"], (ConvertTo-InteropComparable -Value $recorded["expected"]), (ConvertTo-InteropComparable -Value $recorded["observed"]))
    }

    $lines = $result["logLines"]
    $lines["warn"] = if ($null -ne $observation.Warn) { $observation.Warn.Line } else { $null }
    $lines["info"] = if ($null -ne $observation.Info) { $observation.Info.Line } else { $null }
    $lines["fatal"] = if ($null -ne $observation.Fatal) { $observation.Fatal.Line } else { $null }
        $lines["error"] = if ($null -ne $observation.Error) { $observation.Error.Line } else { $null }
    $lines["errorContext"] = if ($null -ne $observation.Error) { @($observation.Error.Continuation) } else { @() }
    $lines["summary"] = $observation.Summary
    $lines["processingFailed"] = if ($null -ne $observation.ProcessingFailed) { $observation.ProcessingFailed.Line } else { $null }
    $lines["processingFailedContext"] = if ($null -ne $observation.ProcessingFailed) { @($observation.ProcessingFailed.Continuation | Select-Object -First 1) } else { @() }

    $details = $result["details"]
    $details["publisherExitCode"] = $run.ExitCode
    $details["publisherFolder"] = & $relative $run.Folder
    $details["publisherCountReads"] = $observation.PublisherCountReadCount
    $details["rejectedSourceRequests"] = $observation.RejectedSourceRequestCount
    $details["countReads"] = $observation.CountReadCount
    $details["targetDocumentsBefore"] = $targetBefore
    $details["targetDocumentsAfter"] = $targetAfter
    $details["retiredResponse"] = $observation.Retired
    $details["neverConfiguredResponse"] = $observation.NeverConfigured
    $details["expectationOverride"] = @($ExpectationOverride.Keys)

    $secrets = @($SourceReader.Key, $SourceReader.Secret, $TargetWriter.Key, $TargetWriter.Secret, $SourceReaderToken, $TargetWriterToken, $AdminToken)
    $path = Write-InteropResultFile -RunFolder $RunFolder -Result $result -Secret $secrets -FileName $ResultFileName
    Write-InteropStep "Results: $path"

    return $result
}

#endregion

#region Isolated scenario

# What an incremental publish from T0 must do against the snapshot (Run B) and, as the negative control, against
# the primary (Run C). Observed live on PostgreSQL with Publisher v1.4.0, so a future Publisher build that
# changes any of it fails the scenario instead of the documentation going stale.
#   Run B: the target reflects the pre-snapshot changes (U1, D1, K1) and none of the post-snapshot ones.
#   Run C: --ignoreIsolation=true reads the primary, so every marker is reflected.
$script:IsolatedExpectation = [ordered]@{
    RunExitCodes                = @(0, 0)
    RunBReflected               = [ordered]@{ U1 = $true; D1 = $true; K1 = $true; I2 = $false; U2 = $false; D2 = $false; K2 = $false }
    RunCReflected               = [ordered]@{ U1 = $true; D1 = $true; K1 = $true; I2 = $true; U2 = $true; D2 = $true; K2 = $true }
    RunBPrimarySelections       = 0
    RunBReadReplicaSelections   = 0
    RunCSnapshotSelections      = 0
    RunCReadReplicaSelections   = 0
    MinimumSurfaceCount         = 1
}

# The Publisher modes the two runs of one pair must show: a cursor run pages with /partitions and pageToken, an
# offset run pages with offset and limit, and neither run mixes the two.
$script:IsolatedSurfaceNames = @("GetMany", "Partitions", "CursorPage", "TotalCount", "Deletes", "KeyChanges", "AvailableChangeVersions")

<#
.SYNOPSIS
    Returns the pinned Isolated expectations, optionally with some values replaced.

.DESCRIPTION
    Override exists to prove the verifier can fail: pass a deliberately wrong value and the matching
    assertion must report FAIL. An unknown key throws, so a typo cannot silently weaken the check.
#>
function Get-InteropIsolatedExpectation {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [System.Collections.IDictionary]$Override = @{}
    )

    $expectation = [ordered]@{}
    foreach ($key in $script:IsolatedExpectation.Keys) {
        $value = $script:IsolatedExpectation[$key]
        if ($value -is [System.Collections.IDictionary]) {
            $copy = [ordered]@{}
            foreach ($inner in $value.Keys) { $copy[$inner] = $value[$inner] }
            $value = $copy
        }
        $expectation[$key] = $value
    }

    foreach ($key in $Override.Keys) {
        if (-not $expectation.Contains($key)) {
            throw "Unknown Isolated expectation '$key'. Known: $($expectation.Keys -join ', ')."
        }
        $expectation[$key] = $Override[$key]
    }

    return $expectation
}

<#
.SYNOPSIS
    Returns the names of the pinned expectations of the Isolated scenario.
#>
function Get-InteropIsolatedExpectationName {
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    return [string[]]@($script:IsolatedExpectation.Keys)
}

<#
.SYNOPSIS
    Sums several count maps (surface counts or selection counts) into one, key by key.
#>
function Get-InteropCountUnion {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.IDictionary[]]$Count
    )

    $union = [ordered]@{}
    foreach ($map in $Count) {
        foreach ($key in $map.Keys) {
            if (-not $union.Contains($key)) {
                $union[$key] = 0
            }
            $union[$key] += [int]$map[$key]
        }
    }

    return $union
}

<#
.SYNOPSIS
    Reads what the Publisher recorded as processed from its Debug log: the newest change version it read from
    the source, and the change-version window it extracted.

.DESCRIPTION
    The plainText configuration store cannot persist the processed version, so the log is the only record.
    Newest lists every distinct newestChangeVersion of "Available change versions request ... returned OK"
    lines. WindowStart and WindowEnd are the lowest start and highest end of the "in change versions A to B"
    lines (null when there are none).
#>
function Get-InteropPublisherChangeVersion {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Line
    )

    $newest = [System.Collections.Generic.SortedSet[long]]::new()
    $starts = [System.Collections.Generic.List[long]]::new()
    $ends = [System.Collections.Generic.List[long]]::new()
    foreach ($text in $Line) {
        if ($text -match 'Available change versions request from .*returned OK: .*"newestChangeVersion"\s*:\s*(?<newest>\d+)') {
            $null = $newest.Add([long]$Matches["newest"])
        }
        elseif ($text -match 'in change versions (?<start>\d+) to (?<end>\d+)\.') {
            $starts.Add([long]$Matches["start"])
            $ends.Add([long]$Matches["end"])
        }
    }

    return [pscustomobject]@{
        Newest      = [long[]]@($newest)
        WindowStart = $(if ($starts.Count -gt 0) { [long]($starts | Measure-Object -Minimum).Minimum } else { $null })
        WindowEnd   = $(if ($ends.Count -gt 0) { [long]($ends | Measure-Object -Maximum).Maximum } else { $null })
        WindowLines = $starts.Count
    }
}

<#
.SYNOPSIS
    Reduces one Publisher run to plain observed values: exit code, what DMS selected for the source data
    store, the per-surface request counts, and the change versions the Publisher logged. Pure.

.DESCRIPTION
    DmsRequest is the output of ConvertTo-InteropDmsRequest for the run's window and DmsLine the DMS log
    lines of the same window. Selections counts the "Selected {Kind} target" lines for the source data store
    (Primary and Snapshot always present). NonSnapshotRequests and NonPrimaryRequests count the requests that
    were attributed to the source data store but to another kind; UnattributedRequests counts source-shaped
    GET data requests with no selection line, so a request cannot dodge the attribution by not being joined.
#>
function Get-InteropPublisherRunObservation {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$RunName,

        [Parameter(Mandatory)]
        [int]$ExitCode,

        [Parameter(Mandatory)]
        [long]$SourceDataStoreId,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [pscustomobject[]]$DmsRequest,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$DmsLine,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$PublisherLine,

        [Nullable[datetime]]$FromUtc = $null,

        [Nullable[datetime]]$ToUtc = $null
    )

    $selections = Get-InteropTargetSelections -Line $DmsLine -DataStoreId $SourceDataStoreId -FromUtc $FromUtc -ToUtc $ToUtc
    $surfaces = Get-InteropSurfaceCount -Request $DmsRequest -DataStoreId $SourceDataStoreId
    $sourceRequests = @($DmsRequest | Where-Object { $_.DataStoreId -eq $SourceDataStoreId })
    $dataSurfaces = $script:SourceSurfaceNames + @("Unknown")

    return [pscustomobject]@{
        RunName               = $RunName
        ExitCode              = $ExitCode
        Selections            = $selections
        SourceRequestCount    = $sourceRequests.Count
        NonSnapshotRequests   = @($sourceRequests | Where-Object { $_.TargetKind -cne "Snapshot" }).Count
        NonPrimaryRequests    = @($sourceRequests | Where-Object { $_.TargetKind -cne "Primary" }).Count
        UnattributedRequests  = @($DmsRequest | Where-Object { $null -eq $_.DataStoreId -and $_.Surface -in $dataSurfaces }).Count
        Surfaces              = $surfaces.Counts
        Unclassified          = @($surfaces.Unclassified)
        PublisherSurfaces     = (Get-InteropSourceSurfaces -Line $PublisherLine).Counts
        ChangeVersion         = Get-InteropPublisherChangeVersion -Line $PublisherLine
    }
}

<#
.SYNOPSIS
    Decides whether a target shows a marker after its change ("After"), before it ("Before"), or neither.

.DESCRIPTION
    Observation holds Present (classPeriodName to whether a read by that natural key found it) and
    OfficialAttendancePeriod of the found document. A marker is in a state when exactly that state's name is
    present (or none, for a state in which the document does not exist) and, for an update, the attendance
    flag matches. "Neither" means a half-applied change, for example both names of a key change found.
#>
function Get-InteropTargetMarkerState {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Marker,

        [Parameter(Mandatory)]
        [pscustomobject]$Observation
    )

    $presentNames = $Observation.Present
    $readAttendance = $Observation.OfficialAttendancePeriod
    $isState = {
        param($Exists, $Name, $Attendance)
        foreach ($candidate in @($Marker.NameBefore, $Marker.NameAfter) | Select-Object -Unique) {
            $found = $presentNames.ContainsKey($candidate) -and [bool]$presentNames[$candidate]
            $wanted = $Exists -and $candidate -ceq $Name
            if ($found -ne $wanted) { return $false }
        }
        if ($Exists -and $null -ne $Attendance -and $readAttendance -ne $Attendance) { return $false }
        return $true
    }

    $after = & $isState $Marker.ExistsAfter $Marker.NameAfter $Marker.AttendanceAfter
    $before = & $isState $Marker.ExistsBefore $Marker.NameBefore $Marker.AttendanceBefore
    if ($after -and -not $before) { return "After" }
    if ($before -and -not $after) { return "Before" }
    return "Neither"
}

<#
.SYNOPSIS
    Reads every marker from a target through a token and returns its state ("After", "Before", or "Neither"),
    keyed by marker name.

.DESCRIPTION
    Finds each marker by its natural keys through the target's own Resources API. A read that does not answer
    200 throws.
#>
function Get-InteropTargetMarkerStateMap {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter(Mandatory)]
        [pscustomobject[]]$Marker
    )

    $states = [ordered]@{}
    foreach ($item in $Marker) {
        $present = @{}
        $attendance = $null
        foreach ($name in @($item.NameBefore, $item.NameAfter) | Select-Object -Unique) {
            $document = Get-InteropClassPeriod -DmsUrl $DmsUrl -Token $Token -Name $name -SchoolId $item.SchoolId
            $present[$name] = ($null -ne $document)
            if ($null -ne $document -and $document.PSObject.Properties.Name -contains "officialAttendancePeriod") {
                $attendance = [bool]$document.officialAttendancePeriod
            }
        }
        $states[$item.Marker] = Get-InteropTargetMarkerState -Marker $item -Observation ([pscustomobject]@{ Present = $present; OfficialAttendancePeriod = $attendance })
    }

    return $states
}

<#
.SYNOPSIS
    Reads one finished Publisher run: the DMS log of its window joined to the request records, the run's
    dms-requests.json summary, and the Publisher's own Debug log, reduced to an observation.
#>
function Read-InteropPublisherRun {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Run,

        [Parameter(Mandatory)]
        [long]$SourceDataStoreId
    )

    $window = Get-InteropLogWindow -StartedUtc $Run.StartedUtc -FinishedUtc $Run.FinishedUtc
    $dmsLine = Get-InteropDmsLogLine -Since $window.Since -Until $window.Until
    $dmsRequest = @(ConvertTo-InteropDmsRequest -Line $dmsLine -FromUtc $Run.StartedUtc -ToUtc $Run.FinishedUtc)
    $publisherLine = [string[]]@(Get-Content -LiteralPath $Run.DebugLogPath)

    ConvertTo-Json -InputObject @($dmsRequest | ForEach-Object {
            [ordered]@{ method = $_.Method; path = $_.Path; query = $_.Query; status = $_.StatusCode; surface = $_.Surface; dataStoreId = $_.DataStoreId; targetKind = $_.TargetKind }
        }) -Depth 4 | Set-Content -LiteralPath (Join-Path $Run.Folder "dms-requests.json")

    return Get-InteropPublisherRunObservation -RunName $Run.RunName -ExitCode $Run.ExitCode -SourceDataStoreId $SourceDataStoreId `
        -DmsRequest $dmsRequest -DmsLine $dmsLine -PublisherLine $publisherLine -FromUtc $Run.StartedUtc -ToUtc $Run.FinishedUtc
}

<#
.SYNOPSIS
    Evaluates the Isolated scenario: one record (Name, Expected, Observed, Evidence) per assertion. Pure.

.DESCRIPTION
    RunB and RunC are arrays of Get-InteropPublisherRunObservation results (a cursor run then an offset run),
    TargetStateB and TargetStateC map each marker to "After", "Before", or "Neither" on the target after those
    runs. The assertions are the expectations of the story (Run B against the snapshot) and the control (Run C
    with --ignoreIsolation=true). The control flips are computed from the observations alone, not from the
    pinned expectations, so a wrong pinned expectation cannot hide a check that stopped discriminating.
#>
function Get-InteropIsolatedAssertion {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Expectation,

        [Parameter(Mandatory)]
        [pscustomobject]$Arrangement,

        [Parameter(Mandatory)]
        [pscustomobject[]]$RunB,

        [Parameter(Mandatory)]
        [pscustomobject[]]$RunC,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$TargetStateB,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$TargetStateC,

        [string]$Evidence = "run results"
    )

    $e = $Expectation
    $a = $Arrangement
    $record = {
        param($Name, $Expected, $Observed, $Why)
        [pscustomobject]@{ Name = $Name; Expected = $Expected; Observed = $Observed; Evidence = $Why }
    }
    $union = { param($Runs, $Property) Get-InteropCountUnion -Count @($Runs | ForEach-Object { $_.$Property }) }
    $reflected = { param($States, $Marker) $States.Contains($Marker) -and $States[$Marker] -ceq "After" }
    $markerNames = @($a.Markers | ForEach-Object { $_.Marker })

    $selectionsB = & $union $RunB "Selections"
    $selectionsC = & $union $RunC "Selections"
    $surfacesB = & $union $RunB "Surfaces"
    $surfacesC = & $union $RunC "Surfaces"
    $count = { param($Map, $Key) if ($Map.Contains($Key)) { [int]$Map[$Key] } else { 0 } }

    & $record "Arrangement: T0 is below the snapshot's newest change version" $true ($a.StartingChangeVersion -lt $a.SnapshotNewestChangeVersion) $Evidence
    & $record "Arrangement: the snapshot's newest change version is below the primary's" $true ($a.SnapshotNewestChangeVersion -lt $a.PrimaryNewestChangeVersion) $Evidence

    foreach ($side in @(
            @{ Label = "Run B"; Runs = $RunB; Selections = $selectionsB; Surfaces = $surfacesB }
            @{ Label = "Run C"; Runs = $RunC; Selections = $selectionsC; Surfaces = $surfacesC }
        )) {
        $label = $side.Label
        $runs = @($side.Runs)
        $cursorRun = $runs[0]
        $offsetRun = $runs[1]

        & $record "$label exit codes (cursor run, offset run)" $e["RunExitCodes"] @($runs | ForEach-Object { $_.ExitCode }) "exit-code.txt of $label"
        & $record "$label cursor run pages with /partitions and pageToken only" $true `
        (((& $count $cursorRun.Surfaces "Partitions") -gt 0) -and ((& $count $cursorRun.Surfaces "CursorPage") -gt 0) -and ((& $count $cursorRun.Surfaces "GetMany") -eq 0)) $Evidence
        & $record "$label offset run pages with offset and limit only" $true `
        (((& $count $offsetRun.Surfaces "GetMany") -gt 0) -and ((& $count $offsetRun.Surfaces "CursorPage") -eq 0) -and ((& $count $offsetRun.Surfaces "Partitions") -eq 0)) $Evidence
        & $record "$label made no source request the DMS log left unattributed" 0 ([int](($runs | Measure-Object -Property UnattributedRequests -Sum).Sum)) $Evidence
        & $record "$label made no request of a shape the harness cannot classify" 0 (@($runs | ForEach-Object { $_.Unclassified }).Count) $Evidence

        foreach ($surface in $script:IsolatedSurfaceNames) {
            $n = & $count $side.Surfaces $surface
            & $record "$label issued $surface at least once (union of the cursor and offset runs)" $true ($n -ge $e["MinimumSurfaceCount"]) "$Evidence; $surface count $n"
        }
    }

    # Run B: every source request answered from the snapshot.
    & $record "Run B Primary selections for the source data store" $e["RunBPrimarySelections"] (& $count $selectionsB "Primary") $Evidence
    & $record "Run B ReadReplica selections for the source data store" $e["RunBReadReplicaSelections"] (& $count $selectionsB "ReadReplica") $Evidence
    & $record "Run B selected the Snapshot for the source data store" $true ((& $count $selectionsB "Snapshot") -gt 0) "$Evidence; Snapshot selections $(& $count $selectionsB 'Snapshot')"
    & $record "Run B source requests attributed to a target other than Snapshot" 0 ([int](($RunB | Measure-Object -Property NonSnapshotRequests -Sum).Sum)) $Evidence
    foreach ($marker in $e["RunBReflected"].Keys) {
        & $record "Run B target reflects $marker" $e["RunBReflected"][$marker] (& $reflected $TargetStateB $marker) "target B read after Run B; state $($TargetStateB[$marker])"
    }

    # Run C: the control reads the primary.
    & $record "Run C Snapshot selections for the source data store" $e["RunCSnapshotSelections"] (& $count $selectionsC "Snapshot") $Evidence
    & $record "Run C ReadReplica selections for the source data store" $e["RunCReadReplicaSelections"] (& $count $selectionsC "ReadReplica") $Evidence
    & $record "Run C selected the Primary for the source data store" $true ((& $count $selectionsC "Primary") -gt 0) "$Evidence; Primary selections $(& $count $selectionsC 'Primary')"
    & $record "Run C source requests attributed to a target other than Primary" 0 ([int](($RunC | Measure-Object -Property NonPrimaryRequests -Sum).Sum)) $Evidence
    foreach ($marker in $e["RunCReflected"].Keys) {
        & $record "Run C target reflects $marker" $e["RunCReflected"][$marker] (& $reflected $TargetStateC $marker) "target C read after Run C; state $($TargetStateC[$marker])"
    }

    # Change versions the Publisher recorded as processed.
    foreach ($side in @(
            @{ Label = "Run B"; Runs = $RunB; Newest = $a.SnapshotNewestChangeVersion; Other = $a.PrimaryNewestChangeVersion; Whose = "the snapshot's" }
            @{ Label = "Run C"; Runs = $RunC; Newest = $a.PrimaryNewestChangeVersion; Other = $a.SnapshotNewestChangeVersion; Whose = "the primary's" }
        )) {
        $versions = @($side.Runs | ForEach-Object { $_.ChangeVersion })
        $newestSeen = @($versions | ForEach-Object { $_.Newest } | Sort-Object -Unique)
        $ends = @($versions | ForEach-Object { $_.WindowEnd } | Where-Object { $null -ne $_ } | Sort-Object -Unique)
        $starts = @($versions | ForEach-Object { $_.WindowStart } | Where-Object { $null -ne $_ } | Sort-Object -Unique)
        & $record "$($side.Label) processed change version equals $($side.Whose) newestChangeVersion ($($side.Newest))" @([long]$side.Newest) $newestSeen "Publisher log of $($side.Label): availableChangeVersions response"
        & $record "$($side.Label) change-version window ends at $($side.Whose) newestChangeVersion" @([long]$side.Newest) $ends "Publisher log of $($side.Label): change versions window"
        & $record "$($side.Label) change-version window starts after T0" @([long]($a.StartingChangeVersion + 1)) $starts "Publisher log of $($side.Label): change versions window"
    }

    # Control flips, computed from the observations alone.
    $primaryB = & $count $selectionsB "Primary"
    $snapshotB = & $count $selectionsB "Snapshot"
    $primaryC = & $count $selectionsC "Primary"
    $snapshotC = & $count $selectionsC "Snapshot"
    & $record "Control flip: source selections go from Snapshot (Run B) to Primary (Run C)" $true `
    (($snapshotB -gt 0) -and ($primaryB -eq 0) -and ($primaryC -gt 0) -and ($snapshotC -eq 0)) "Run B Snapshot $snapshotB Primary $primaryB; Run C Snapshot $snapshotC Primary $primaryC"

    $newestB = @($RunB | ForEach-Object { $_.ChangeVersion.Newest } | Sort-Object -Unique)
    $newestC = @($RunC | ForEach-Object { $_.ChangeVersion.Newest } | Sort-Object -Unique)
    & $record "Control flip: processed change version goes from the snapshot's to the primary's" $true `
    (($newestB.Count -eq 1) -and ($newestC.Count -eq 1) -and ($newestB[0] -eq $a.SnapshotNewestChangeVersion) -and ($newestC[0] -eq $a.PrimaryNewestChangeVersion) -and ($newestB[0] -ne $newestC[0])) `
        "Run B $($newestB -join ',') Run C $($newestC -join ',')"

    foreach ($marker in $a.Markers | Where-Object { $_.Phase -eq "PostSnapshot" }) {
        $name = $marker.Marker
        $absentB = -not (& $reflected $TargetStateB $name)
        $presentC = & $reflected $TargetStateC $name
        & $record "Control flip: $name is not reflected after Run B and is reflected after Run C" $true ($absentB -and $presentC) `
            "target B state $($TargetStateB[$name]); target C state $($TargetStateC[$name])"
    }

    foreach ($marker in $a.Markers | Where-Object { $_.Phase -eq "PreSnapshot" }) {
        $name = $marker.Marker
        & $record "Control: $name is reflected after Run B and after Run C" $true ((& $reflected $TargetStateB $name) -and (& $reflected $TargetStateC $name)) `
            "target B state $($TargetStateB[$name]); target C state $($TargetStateC[$name])"
    }

    $unknown = @($markerNames | Where-Object { -not $TargetStateB.Contains($_) -or -not $TargetStateC.Contains($_) })
    & $record "Every marker was read from both targets" @() $unknown $Evidence
}

<#
.SYNOPSIS
    Runs the Isolated scenario: an incremental Publisher extraction from T0 against a snapshot without
    --ignoreIsolation (Run B), and the same extraction with --ignoreIsolation=true as the negative control
    (Run C), asserted against the pinned expectations. Writes the results file.

.DESCRIPTION
    1. Creates two fresh targets (B and C), each with a Publisher writer and a verifier client, and copies
       the schoolYearTypes into both.
    2. Arranges the source (Set-InteropSourceArrangement): the baseline publish runs into both targets before
       any pre-snapshot change, so both hold T0, then U1, D1, K1, the snapshot, and I2, U2, D2, K2.
    3. Run B is two Publisher runs into target B, both from T0 and without --ignoreIsolation: a cursor run
       (the default paging) and an offset run (--disableCursorPaging=true, scoped to classPeriods, the only
       resource the markers change). A run uses cursor or offset paging, never both, so the seven source
       surfaces are asserted over the union of the two.
    4. Run C is the same pair into target C with --ignoreIsolation=true.
    5. Reads every marker from both targets and evaluates the assertions and control flips.
    The snapshot database, login, and derivative are removed afterwards. Returns the result and what the
    manifest records (data stores, clients, arrangement).
#>
function Invoke-InteropIsolatedScenario {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Harness scenario on a throwaway local stack; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string]$DmsUrl,

        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [string]$AdminToken,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$Engine,

        [Parameter(Mandatory)]
        [hashtable]$EnvironmentValues,

        [Parameter(Mandatory)]
        [string]$EnvironmentFile,

        [Parameter(Mandatory)]
        [string]$RunFolder,

        [Parameter(Mandatory)]
        [string]$RunId,

        [Parameter(Mandatory)]
        [string]$RunToken,

        [Parameter(Mandatory)]
        [pscustomobject]$SourceStore,

        [Parameter(Mandatory)]
        [pscustomobject]$SourceReader,

        [Parameter(Mandatory)]
        [string]$SourceReaderToken,

        [Parameter(Mandatory)]
        [string]$SourceWriterToken,

        [Parameter(Mandatory)]
        [string]$PublisherImage,

        [System.Collections.IDictionary]$ExpectationOverride = @{},

        [string]$ResultFileName = "results.json"
    )

    $expectation = Get-InteropIsolatedExpectation -Override $ExpectationOverride
    if ($ExpectationOverride.Count -gt 0) {
        Write-InteropStep "NOTE: expectations overridden for $($ExpectationOverride.Keys -join ', '); this run verifies the verifier, not the Publisher."
    }

    $result = New-InteropResult -Scenario "Isolated" -Engine $Engine -RunId $RunId

    $dataStores = [System.Collections.Generic.List[object]]::new()
    $clients = [System.Collections.Generic.List[object]]::new()

    Write-InteropStep "Isolated 1: creating targets B and C with a Publisher writer and a verifier each."
    $targets = @{}
    foreach ($letter in "b", "c") {
        $store = New-InteropDataStore -CmsUrl $CmsUrl -AdminToken $AdminToken -Engine $Engine -EnvironmentFile $EnvironmentFile -RunToken $RunToken -Role "target-$letter"
        $clientArguments = @{ CmsUrl = $CmsUrl; AdminToken = $AdminToken; RunToken = $RunToken; DataStoreId = $store.Id }
        $writer = New-InteropClient @clientArguments -Role "target-$letter-writer" -ClaimSetName "EdFiAPIPublisherWriter"
        $verifier = New-InteropClient @clientArguments -Role "target-$letter-verifier" -ClaimSetName "EdFiSandbox"
        $writerToken = Get-InteropClientToken -DmsUrl $DmsUrl -Client $writer
        $verifierToken = Get-InteropClientToken -DmsUrl $DmsUrl -Client $verifier
        $copied = Copy-InteropSchoolYear -DmsUrl $DmsUrl -SourceToken $SourceReaderToken -TargetToken $writerToken
        Write-InteropStep "Copied $copied schoolYearTypes from the source to target $($letter.ToUpperInvariant())."
        $targets[$letter] = [pscustomobject]@{ Store = $store; Writer = $writer; WriterToken = $writerToken; VerifierToken = $verifierToken }
        $dataStores.Add($store)
        $clients.Add($writer)
        $clients.Add($verifier)
    }

    $arrangement = $null
    try {
        Write-InteropStep "Isolated 2: arranging the source (baseline into both targets, then the snapshot and the changes)."
        $arrangement = Set-InteropSourceArrangement -DmsUrl $DmsUrl -CmsUrl $CmsUrl -AdminToken $AdminToken -Engine $Engine `
            -EnvironmentValues $EnvironmentValues -RunFolder $RunFolder -RunToken $RunToken -SourceDataStoreId $SourceStore.Id `
            -SourceReader $SourceReader -TargetWriter $targets["b"].Writer -SourceWriterToken $SourceWriterToken `
            -SourceReaderToken $SourceReaderToken -PublisherImage $PublisherImage -AdditionalBaselineTargetWriter @($targets["c"].Writer)

        $window = "--lastChangeVersionProcessed=$($arrangement.StartingChangeVersion)"
        $runObservation = @{}
        foreach ($control in @(
                @{ Label = "b"; Text = "Run B (snapshot, no --ignoreIsolation)"; Extra = @() }
                @{ Label = "c"; Text = "Run C (control, --ignoreIsolation=true)"; Extra = @("--ignoreIsolation=true") }
            )) {
            $target = $targets[$control.Label]
            $observations = @()
            foreach ($mode in @(
                    @{ Name = "cursor"; Option = @($window, "--includeDescriptors=true") }
                    @{ Name = "offset"; Option = @($window, "--disableCursorPaging=true", "--includeOnly=/ed-fi/classPeriods") }
                )) {
                Write-InteropStep "Isolated $($control.Label.ToUpperInvariant()): $($control.Text), $($mode.Name) paging."
                $run = Invoke-InteropPublisher -RunFolder $RunFolder -RunName "$($control.Label)-$($mode.Name)" -Image $PublisherImage `
                    -SourceClient $SourceReader -TargetClient $target.Writer -Option (@($mode.Option) + @($control.Extra))
                $observations += Read-InteropPublisherRun -Run $run -SourceDataStoreId $SourceStore.Id
            }
            $runObservation[$control.Label] = $observations
        }

        Write-InteropStep "Isolated 3: reading every marker from both targets."
        $stateB = Get-InteropTargetMarkerStateMap -DmsUrl $DmsUrl -Token $targets["b"].VerifierToken -Marker $arrangement.Markers
        $stateC = Get-InteropTargetMarkerStateMap -DmsUrl $DmsUrl -Token $targets["c"].VerifierToken -Marker $arrangement.Markers
    }
    finally {
        if ($null -ne $arrangement) {
            try {
                Remove-InteropSnapshotDerivative -CmsUrl $CmsUrl -AdminToken $AdminToken -DerivativeId $arrangement.DerivativeId
                $null = Wait-InteropSnapshotState -DmsUrl $DmsUrl -Token $SourceReaderToken -Expect Absent
            }
            catch { Write-InteropStep "Cleanup could not retire the derivative: $($_.Exception.Message)" }
            try {
                Remove-InteropSnapshot -Engine $Engine -EnvironmentValues $EnvironmentValues -SnapshotName $arrangement.Snapshot.SnapshotName `
                    -LoginName $arrangement.Snapshot.LoginName -SourceDatabaseName $arrangement.Snapshot.SourceDatabaseName
            }
            catch { Write-InteropStep "Cleanup could not drop the snapshot '$($arrangement.Snapshot.SnapshotName)': $($_.Exception.Message)" }
        }
    }

    $assertions = Get-InteropIsolatedAssertion -Expectation $expectation -Arrangement $arrangement -RunB $runObservation["b"] -RunC $runObservation["c"] `
        -TargetStateB $stateB -TargetStateC $stateC -Evidence "dms-requests.json and publisher.serilog.log of the runs"
    foreach ($item in $assertions) {
        $recorded = Add-InteropAssertion -Result $result -Name $item.Name -Expected $item.Expected -Observed $item.Observed -Evidence $item.Evidence
        Write-InteropStep ("{0}: {1} (expected {2}, observed {3})" -f $recorded["outcome"], $recorded["name"], (ConvertTo-InteropComparable -Value $recorded["expected"]), (ConvertTo-InteropComparable -Value $recorded["observed"]))
    }

    $details = $result["details"]
    $details["startingChangeVersion"] = $arrangement.StartingChangeVersion
    $details["snapshotNewestChangeVersion"] = $arrangement.SnapshotNewestChangeVersion
    $details["primaryNewestChangeVersion"] = $arrangement.PrimaryNewestChangeVersion
    $details["targetStateB"] = $stateB
    $details["targetStateC"] = $stateC
    $details["expectationOverride"] = @($ExpectationOverride.Keys)
    foreach ($label in "b", "c") {
        $details["run$($label.ToUpperInvariant())"] = @($runObservation[$label] | ForEach-Object {
                [ordered]@{
                    run                    = $_.RunName
                    exitCode               = $_.ExitCode
                    selections             = $_.Selections
                    surfaces               = $_.Surfaces
                    publisherLogSurfaces   = $_.PublisherSurfaces
                    newestChangeVersions   = @($_.ChangeVersion.Newest)
                    windowStart            = $_.ChangeVersion.WindowStart
                    windowEnd              = $_.ChangeVersion.WindowEnd
                    folder                 = "publisher-$($_.RunName)"
                }
            })
    }

    $secrets = @($SourceReader.Key, $SourceReader.Secret, $SourceReaderToken, $SourceWriterToken, $AdminToken)
    foreach ($target in $targets.Values) {
        $secrets += @($target.Writer.Key, $target.Writer.Secret, $target.WriterToken, $target.VerifierToken)
    }
    $path = Write-InteropResultFile -RunFolder $RunFolder -Result $result -Secret $secrets -FileName $ResultFileName
    Write-InteropStep "Results: $path"

    return [pscustomobject]@{
        Result      = $result
        DataStore   = $dataStores.ToArray()
        Client      = $clients.ToArray()
        Arrangement = $arrangement.Manifest
    }
}

#endregion

#region Validation record

# The checkout-independent reference the record links for the release gate.
$script:GateEpicPath = "reference/design/backend-redesign/epics/10-update-tracking-change-queries/EPIC.md"
$script:RecordEngineOrder = @("postgresql", "mssql")
$script:RecordEngineLabel = @{ postgresql = "PostgreSQL"; mssql = "SQL Server" }
$script:RecordHostPathPattern = '(?i)(/Users/|/home/|/private/|/var/folders/|/tmp/|[A-Za-z]:\\|\\\\Users\\)'
$script:RecordConnectionFragmentPattern = '(?i)\b(server|host|data source|user id|uid|password|pwd|database|initial catalog|port|trustservercertificate|encrypt)\s*=\s*[^;\r\n]*;?'
$script:RecordBearerPattern = '(?i)bearer\s+[A-Za-z0-9._~+/=-]{8,}'
$script:RecordRedaction = "[redacted]"
# Like the manifest pattern, but a plural "connections" (a limit such as max_connections) is not a secret.
$script:RecordSecretNamePattern = "(?i)secret|password|token|connection(?!s\b)|credential|apikey|clientkey|^key$"

<#
.SYNOPSIS
    Returns the acceptance criteria the per-engine runs prove.

.DESCRIPTION
    Each requirement is a pattern that must match at least one assertion name of the scenario's results file;
    every matched assertion must PASS.
#>
function Get-InteropRecordCriterion {
    [CmdletBinding()]
    [OutputType([object[]])]
    param()

    $surfaces = @("GetMany", "Partitions", "CursorPage", "TotalCount", "Deletes", "KeyChanges", "AvailableChangeVersions")
    return @(
        [pscustomobject]@{
            Number      = 2
            Title       = "Publisher runs isolated without the opt-out"
            Scenario    = "Isolated"
            Requirement = @(
                "^Run B exit codes"
                "^Run B Primary selections"
                "^Run B ReadReplica selections"
                "^Run B selected the Snapshot"
                "^Run B source requests attributed to a target other than Snapshot"
                "^Run B made no source request the DMS log left unattributed"
                "^Run C selected the Primary"
                "^Run C source requests attributed to a target other than Primary"
                "^Control flip: source selections go from Snapshot"
            )
        }
        [pscustomobject]@{
            Number      = 3
            Title       = "Post-snapshot writes are excluded"
            Scenario    = "Isolated"
            Requirement = @(
                foreach ($marker in "I2", "U2", "D2", "K2") {
                    "^Run B target reflects $marker$"
                    "^Run C target reflects $marker$"
                    "^Control flip: $marker is not reflected after Run B"
                }
            )
        }
        [pscustomobject]@{
            Number      = 4
            Title       = "Every Publisher source surface used the snapshot within one extraction"
            Scenario    = "Isolated"
            Requirement = @(
                foreach ($surface in $surfaces) { "^Run B issued $surface at least once" }
                "^Run B selected the Snapshot"
                "^Run B source requests attributed to a target other than Snapshot"
            )
        }
        [pscustomobject]@{
            Number      = 5
            Title       = "No-snapshot Publisher behavior recorded"
            Scenario    = "NoSnapshot"
            Requirement = @(
                "^Publisher exit code$"
                "^availableChangeVersions status codes$"
                "^count read status codes$"
                "^The Snapshot Not Found response after the snapshot is retired equals the never-configured response$"
                "^The FATL line about source read errors is present$"
                "^The EROR count-failure line carries the Snapshot Not Found body$"
            )
        }
    )
}

<#
.SYNOPSIS
    Redacts credentials, connection-string fragments, bearer tokens, and host paths from text bound for the
    validation record.

.DESCRIPTION
    When Name is secret-shaped (secret, password, token, connection, key, and so on) the whole value is
    replaced, because a secret does not have to look like one.
#>
function Protect-InteropRecordText {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Text,

        [string]$Name = ""
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }
    if ($Name -and $Name -match $script:RecordSecretNamePattern) {
        return $script:RecordRedaction
    }

    $safe = [regex]::Replace($Text, $script:RecordConnectionFragmentPattern, $script:RecordRedaction)
    $safe = [regex]::Replace($safe, $script:RecordBearerPattern, $script:RecordRedaction)
    return [regex]::Replace($safe, $script:RecordHostPathPattern, "[host path]/")
}

<#
.SYNOPSIS
    Throws when the finished record text holds a secret-shaped value or a host path. The last line of defense
    behind Protect-InteropRecordText.
#>
function Assert-InteropRecordHasNoSecret {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Text
    )

    if ($Text -match $script:SecretValuePattern -or $Text -match $script:RecordBearerPattern) {
        throw "The validation record holds a secret-shaped value; refusing to produce it."
    }
    if ($Text -match $script:RecordConnectionFragmentPattern) {
        throw "The validation record holds a connection-string fragment; refusing to produce it."
    }
    if ($Text -match $script:RecordHostPathPattern) {
        throw "The validation record holds a host-specific path; refusing to produce it."
    }
}

<#
.SYNOPSIS
    Loads one run folder: manifest.json plus every results file in it, keyed by scenario.
#>
function Read-InteropRun {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$RunFolder
    )

    $manifestPath = Join-Path $RunFolder "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Run folder '$(Split-Path -Leaf $RunFolder)' has no manifest.json."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable -Depth 20
    Assert-InteropManifestHasNoSecret -Manifest $manifest

    $results = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $RunFolder -Filter "results*.json" -File | Sort-Object Name) {
        $result = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable -Depth 20
        Assert-InteropManifestHasNoSecret -Manifest $result
        $scenario = [string]$result["scenario"]
        if ($results.Contains($scenario)) {
            throw "Run folder '$(Split-Path -Leaf $RunFolder)' has two results files for the $scenario scenario."
        }
        $result["fileName"] = $file.Name
        $results[$scenario] = $result
    }

    return [pscustomobject]@{
        Name     = [string]$manifest["runId"]
        Manifest = $manifest
        Results  = $results
    }
}

function Format-InteropRecordUtc {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value -or "$Value" -eq "") {
        return "not recorded"
    }
    $stamp = [datetimeoffset]::MinValue
    if ($Value -is [datetime]) {
        $stamp = [datetimeoffset]$Value.ToUniversalTime()
    }
    elseif (-not [datetimeoffset]::TryParse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref]$stamp)) {
        return Protect-InteropRecordText -Text ([string]$Value)
    }

    return $stamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", [System.Globalization.CultureInfo]::InvariantCulture) + " UTC"
}

function Get-InteropRecordCell {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [object]$Text,

        [string]$Name = ""
    )

    $safe = Protect-InteropRecordText -Text ([string]$Text) -Name $Name
    return ($safe -replace "\|", "\|" -replace "\r?\n", " ").Trim()
}

<#
.SYNOPSIS
    Reconstructs the exact harness command from a run manifest, so the record reproduces the run.
#>
function Get-InteropHarnessCommand {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Manifest
    )

    $scenarios = @($Manifest["scenarios"] | ForEach-Object { [string]$_ })
    $scenarioArgument = if ($scenarios.Count -gt 1) { "All" } else { $scenarios[0] }
    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.Add("pwsh eng/verification/Invoke-PublisherSnapshotInteropCheck.ps1")
    $parts.Add("-Engine $($Manifest['engine'])")
    $parts.Add("-Scenario $scenarioArgument")

    foreach ($setting in @($Manifest["nonDefaultSettings"])) {
        switch ($setting["name"]) {
            "PublisherImage" { $parts.Add("-PublisherImage $($setting['value'])") }
            "DmsImageTag" { $parts.Add("-DmsImageTag $($setting['value'])") }
            "ConfigurationServiceImage" { $parts.Add("-ConfigurationServiceImage $($setting['value'])") }
            "SkipStackStart" { $parts.Add("-SkipStackStart") }
            "Rebuild" { $parts.Add("-Rebuild") }
        }
    }

    return ($parts -join " ")
}

# Evaluates one criterion against one run: not run when the results file is absent, FAIL when an assertion it
# depends on failed or was never recorded, PASS only when every required assertion is present and PASS.
function Get-InteropRecordOutcome {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Run,

        [Parameter(Mandatory)]
        [pscustomobject]$Criterion
    )

    $results = $Run.Results[$Criterion.Scenario]
    if ($null -eq $results) {
        return [pscustomobject]@{ Outcome = "not run"; Evidence = "the $($Criterion.Scenario) scenario has no results in this run folder"; Assertions = @(); Missing = @() }
    }

    $assertions = @($results["assertions"])
    $matched = [System.Collections.Generic.List[object]]::new()
    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($pattern in $Criterion.Requirement) {
        $hits = @($assertions | Where-Object { $_["name"] -match $pattern })
        if ($hits.Count -eq 0) {
            $missing.Add($pattern)
        }
        foreach ($hit in $hits) {
            if (-not $matched.Contains($hit)) { $matched.Add($hit) }
        }
    }

    $failed = @($matched | Where-Object { $_["outcome"] -ne "PASS" })
    $evidence = "``$($Run.Name)/$($results['fileName'])``, $($matched.Count) assertions"
    $outcome = "PASS"
    if ($failed.Count -gt 0) {
        $outcome = "FAIL"
        $evidence += ", $($failed.Count) failed"
    }
    if ($missing.Count -gt 0) {
        $outcome = "FAIL"
        $evidence += ", $($missing.Count) required assertions never recorded"
    }

    return [pscustomobject]@{ Outcome = $outcome; Evidence = $evidence; Assertions = $matched.ToArray(); Missing = $missing.ToArray() }
}

function Get-InteropRecordVersionOutcome {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Run
    )

    $versions = $Run.Manifest["versions"]
    $required = @("dmsImageName", "dmsImageId", "dmsDiscoveryVersion", "publisherImage", "publisherImageDigest", "dataStandard", "identityProvider", "engine", "engineVersion")
    $absent = @($required | Where-Object { -not $versions.Contains($_) -or [string]::IsNullOrWhiteSpace([string]$versions[$_]) -or [string]$versions[$_] -eq "unknown" })
    $outcome = if ($absent.Count -eq 0) { "PASS" } else { "FAIL" }
    $evidence = "``$($Run.Name)/manifest.json``, $($required.Count - $absent.Count) of $($required.Count) fields recorded"
    if ($absent.Count -gt 0) {
        $evidence += "; missing $($absent -join ', ')"
    }

    return [pscustomobject]@{ Outcome = $outcome; Evidence = $evidence }
}

<#
.SYNOPSIS
    Renders the validation record markdown from loaded runs. Pure: no file or Docker access.

.DESCRIPTION
    Output is deterministic (engines in a fixed order, assertions in recorded order) apart from the run
    timestamps it reports. Every free-text value passes through Protect-InteropRecordText, and the finished
    text is scanned before it is returned.
#>
function ConvertTo-InteropValidationRecord {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [pscustomobject[]]$Run
    )

    $runs = @($script:RecordEngineOrder | ForEach-Object { $engine = $_; $Run | Where-Object { $_.Manifest["engine"] -eq $engine } })
    if ($runs.Count -ne @($Run).Count) {
        throw "Every run must be for one of: $($script:RecordEngineOrder -join ', '), once each."
    }
    if (@($runs | ForEach-Object { $_.Manifest["engine"] } | Sort-Object -Unique).Count -ne $runs.Count) {
        throw "Two run folders are for the same engine; give one run per engine."
    }

    $dmsVersions = @($runs | ForEach-Object { [string]$_.Manifest["versions"]["dmsDiscoveryVersion"] } | Sort-Object -Unique)
    if ($dmsVersions.Count -ne 1) {
        throw "The runs advertise different DMS versions ($($dmsVersions -join ', ')); one record covers one DMS version."
    }
    $publisherDigests = @($runs | ForEach-Object { [string]$_.Manifest["versions"]["publisherImage"] } | Sort-Object -Unique)
    if ($publisherDigests.Count -ne 1) {
        throw "The runs used different Publisher images; one record covers one Publisher build."
    }
    foreach ($item in $runs) {
        foreach ($results in $item.Results.Values) {
            $details = $results["details"]
            $overrides = @()
            if ($null -ne $details -and $details.Contains("expectationOverride")) {
                $overrides = @($details["expectationOverride"] | Where-Object { $null -ne $_ })
            }
            if ($overrides.Count -gt 0) {
                throw "Run '$($item.Name)' ($($results['fileName'])) used -ExpectationOverride ($($overrides -join ', ')); an override run proves the verifier can fail and is not release evidence."
            }
        }
    }

    $criteria = Get-InteropRecordCriterion
    $lines = [System.Collections.Generic.List[string]]::new()
    $add = { param([string]$Line) $lines.Add($Line) }
    $dmsVersion = Get-InteropRecordCell -Text $dmsVersions[0]
    $publisherTag = if ($publisherDigests[0] -match ":(v[0-9][^@:]*)(@|$)") { $Matches[1] } else { "(see Versions)" }

    & $add "# API Publisher snapshot interoperability validation record"
    & $add ""
    & $add "DMS $dmsVersion, validated against Ed-Fi API Publisher $publisherTag with snapshot isolation."
    & $add "The record states what the harness ran, what each run proved, and what is proven by the CI suites instead."
    & $add "Regenerate it with ``Write-InteropValidationRecord`` from the module ``eng/verification/PublisherSnapshotInterop/PublisherSnapshotInterop.psm1``, passing the run folder of each engine."
    & $add ""

    # Per-engine pass summary, used by the summary table and the cross-engine criterion.
    $laneOutcome = @{}
    foreach ($item in $runs) {
        $lane = @(2, 3, 4 | ForEach-Object {
                $number = $_
                Get-InteropRecordOutcome -Run $item -Criterion ($criteria | Where-Object { $_.Number -eq $number })
            })
        $laneOutcome[$item.Manifest["engine"]] = if (@($lane | Where-Object { $_.Outcome -eq "FAIL" }).Count -gt 0) { "FAIL" }
        elseif (@($lane | Where-Object { $_.Outcome -eq "not run" }).Count -gt 0) { "not run" }
        else { "PASS" }
    }

    & $add "## Summary"
    & $add ""
    & $add "| Engine | Run folder | Isolated | NoSnapshot | Started |"
    & $add "| --- | --- | --- | --- | --- |"
    foreach ($engine in $script:RecordEngineOrder) {
        $item = $runs | Where-Object { $_.Manifest["engine"] -eq $engine }
        if ($null -eq $item) {
            & $add "| $($script:RecordEngineLabel[$engine]) | not run | not run | not run | not run |"
            continue
        }
        $cells = foreach ($scenario in "Isolated", "NoSnapshot") {
            $results = $item.Results[$scenario]
            if ($null -eq $results) { "not run" } else { "$($results['overall']) ($($results['passed']) passed, $($results['failed']) failed)" }
        }
        & $add "| $($script:RecordEngineLabel[$engine]) | ``$(Get-InteropRecordCell -Text $item.Name)`` | $($cells[0]) | $($cells[1]) | $(Format-InteropRecordUtc -Value $item.Manifest['startedUtc']) |"
    }
    & $add ""

    & $add "## Release gate status"
    & $add ""
    & $add "The release gate for the runtime changes is tracked in ``$script:GateEpicPath`` under Follow-on Stories (spawned by DMS-1190)."
    & $add ""
    & $add "- DMS-1369 is Done (PR #1257, ``55e914e2f``)."
    & $add "- DMS-1371 is Done (DMS-side PR #1205, ``b0b68972c``; the upstream packages are at ``1.0.335``)."
    & $add ""
    & $add "The gate is satisfied on main."
    & $add "It is re-confirmed when the 8.1 release candidate is assembled."
    & $add ""

    & $add "## Acceptance criteria outside the per-engine runs"
    & $add ""
    & $add "Cited means the criterion is proven by the named artifact, not by these runs."
    & $add "A cited row is not a PASS of this record."
    & $add ""
    & $add "| # | Criterion | Outcome | Evidence |"
    & $add "| --- | --- | --- | --- |"
    $bothEngines = if (@($runs).Count -eq 2) { $laneOutcome["postgresql"], $laneOutcome["mssql"] } else { @() }
    $bothOutcome = if ($runs.Count -lt 2) { "not run" } elseif ($bothEngines -contains "FAIL") { "FAIL" } elseif ($bothEngines -contains "not run") { "not run" } else { "PASS" }
    $bothEvidence = if ($runs.Count -lt 2) {
        $ran = @($runs | ForEach-Object { $script:RecordEngineLabel[$_.Manifest["engine"]] }) -join ", "
        "only $ran ran; the engine section below states its own outcome"
    }
    else { "isolated run, post-snapshot exclusion, and per-surface assertions on both engines; see each engine section" }
    & $add "| 6 | Both engines: isolated run, post-snapshot exclusion, and per-surface assertions on PostgreSQL and on SQL Server | $bothOutcome | $bothEvidence |"
    & $add "| 7 | Per-request contract still green | cited | the CI scenarios in Per-request contract below |"
    & $add "| 8 | Operator workflow complete | cited | ``docs/SNAPSHOTS-AND-READ-REPLICAS.md`` |"
    & $add "| 9 | Stale 503 statement removed | cited | ``docs/SNAPSHOTS-AND-READ-REPLICAS.md`` and ``docs/API-CLIENT-AND-INSTANCE-CONFIGURATION.md`` |"
    & $add "| 10 | Release notes complete | cited | ``docs/changelog/8.1.0.md`` |"
    & $add "| 11 | Gate status recorded honestly | cited | Release gate status above |"
    & $add "| 12 | Repeatable with troubleshooting | cited | ``eng/verification/PublisherSnapshotInterop/README.md`` |"
    & $add "| 13 | Markdown and script lint clean | cited | the repository's markdown and PSScriptAnalyzer checks and the Pester step of the pull request CI |"
    & $add ""

    & $add "## Per-request contract"
    & $add ""
    & $add "The per-request behavior is proven by the CI suites below and is re-run at the release candidate."
    & $add "The runs in this record do not re-prove it."
    & $add "CI of record: ``Run PostgreSQL DMS E2E (self-contained, Shard 1)``, ``Run SQL Server DMS E2E (self-contained, representative)``, and the API integration suite (``Given_Postgresql_DerivativeRouting`` and ``Given_Mssql_DerivativeRouting``)."
    & $add ""
    & $add "| Behavior | Cited scenarios |"
    & $add "| --- | --- |"
    & $add "| ``405`` with ``Allow: GET`` for POST, PUT, and DELETE that ask for a snapshot | ``SnapshotAndReadReplicaRouting.feature`` 06 and 07; ``DerivativeRoutingScenario.cs`` ``It_rejects_the_invalid_mutation_shapes_that_ask_for_a_snapshot`` |"
    & $add "| Snapshot Not Found when the snapshot is not configured | ``SnapshotFailures.feature`` 01; ``SnapshotAndReadReplicaRouting.feature`` 09 (an unknown resource keeps its endpoint ``404``); ``DerivativeRoutingScenario.cs`` ``It_returns_not_found_when_no_snapshot_is_configured`` |"
    & $add "| Snapshot Not Found when the snapshot cannot be reached | ``SnapshotFailures.feature`` 02 |"
    & $add "| ``Use-Snapshot`` absent or ``false`` does not select the snapshot | ``SnapshotAndReadReplicaRouting.feature`` 01 and 08; ``DerivativeRoutingScenario.cs`` header-absent and header-false cases in ``It_keeps_the_route_semantics_answer_for_the_invalid_shapes`` and ``It_leaves_a_valid_write_alone_without_a_snapshot_request`` |"
    & $add "| Snapshot precedence over a read replica, and automatic replica selection | ``SnapshotAndReadReplicaRouting.feature`` 01 and 05; ``DerivativeRoutingScenario.cs`` ``It_serves_an_eligible_read_from_the_replica`` and ``It_prefers_the_snapshot_over_the_replica`` |"
    & $add "| ``/deletes``, ``/keyChanges``, and ``availableChangeVersions`` served by the selected target | ``SnapshotAndReadReplicaRouting.feature`` 03, 04, and 05 |"
    & $add ""

    & $add "## GET-by-id"
    & $add ""
    & $add "GET-by-id served from a snapshot is cited, not exercised through the Publisher: ``SnapshotAndReadReplicaRouting.feature`` scenario 02 proves it in CI."
    & $add ""
    & $add "Finding against ``29-snapshot-support.md`` section API Publisher Interoperability: the Publisher never issues a source GET-by-id against a DMS target."
    & $add "That section lists GET-by-id among the requests one extraction issues, so it overstates what a Publisher extraction exercises."
    & $add "The seven source surfaces a DMS extraction does issue are GET-many, ``/partitions``, cursor pages, ``totalCount``, ``/deletes``, ``/keyChanges``, and ``/availableChangeVersions``."
    & $add ""

    foreach ($item in $runs) {
        $engine = [string]$item.Manifest["engine"]
        $versions = $item.Manifest["versions"]
        $label = $script:RecordEngineLabel[$engine]

        & $add "## Engine: $label"
        & $add ""
        & $add "### Versions"
        & $add ""
        & $add "| Item | Value |"
        & $add "| --- | --- |"
        $versionRows = @(
            ,@("DMS image", $versions["dmsImageName"])
            ,@("DMS image digest", $(if ($versions.Contains("dmsImageDigest") -and $versions["dmsImageDigest"]) { $versions["dmsImageDigest"] } else { $versions["dmsImageId"] }))
            ,@("DMS image source revision", $(if ($versions.Contains("dmsImageRevision")) { $versions["dmsImageRevision"] } else { $null }), $true)
            ,@("Configuration Service image", $(if ($versions.Contains("configurationServiceImageName")) { $versions["configurationServiceImageName"] } else { $null }), $true)
            ,@("Configuration Service image digest", $(if ($versions.Contains("configurationServiceImageDigest")) { $versions["configurationServiceImageDigest"] } else { $null }), $true)
            ,@("Configuration Service image source revision", $(if ($versions.Contains("configurationServiceImageRevision")) { $versions["configurationServiceImageRevision"] } else { $null }), $true)
            ,@("DMS repository commit when the run started", $versions["repositoryCommit"])
            ,@("Discovery version", $versions["dmsDiscoveryVersion"])
            ,@("Publisher image", $versions["publisherImage"])
            ,@("Publisher image digest", $versions["publisherImageDigest"])
            ,@("Data Standard", $versions["dataStandard"])
            ,@("Identity provider", $versions["identityProvider"])
            ,@("Engine", $versions["engine"])
            ,@("Engine version", $versions["engineVersion"])
            ,@("Run started", (Format-InteropRecordUtc -Value $item.Manifest["startedUtc"]))
        )
        foreach ($row in $versionRows) {
            # Rows marked optional belong to a published-image run; a local build has no registry digest or revision.
            if ($row.Count -gt 2 -and $row[2] -and [string]::IsNullOrWhiteSpace([string]$row[1])) {
                continue
            }
            $value = if ([string]::IsNullOrWhiteSpace([string]$row[1])) { "not recorded" } else { [string]$row[1] }
            & $add "| $($row[0]) | ``$(Get-InteropRecordCell -Text $value)`` |"
        }
        & $add ""

        & $add "### Non-default settings"
        & $add ""
        & $add "| Setting | Value | Default | Reason |"
        & $add "| --- | --- | --- | --- |"
        foreach ($setting in @($item.Manifest["nonDefaultSettings"])) {
            $name = [string]$setting["name"]
            & $add "| ``$(Get-InteropRecordCell -Text $name)`` | ``$(Get-InteropRecordCell -Text $setting['value'] -Name $name)`` | ``$(Get-InteropRecordCell -Text $setting['default'] -Name $name)`` | $(Get-InteropRecordCell -Text $setting['reason']) |"
        }
        & $add ""

        & $add "### Harness command"
        & $add ""
        & $add '```powershell'
        & $add (Protect-InteropRecordText -Text (Get-InteropHarnessCommand -Manifest $item.Manifest))
        & $add '```'
        & $add ""

        & $add "### Acceptance criteria outcomes"
        & $add ""
        & $add "| # | Criterion | Outcome | Evidence |"
        & $add "| --- | --- | --- | --- |"
        $versionOutcome = Get-InteropRecordVersionOutcome -Run $item
        & $add "| 1 | Versions and configuration recorded | $($versionOutcome.Outcome) | $(Get-InteropRecordCell -Text $versionOutcome.Evidence) |"
        $evidenceLists = [System.Collections.Generic.List[object]]::new()
        foreach ($criterion in $criteria) {
            $outcome = Get-InteropRecordOutcome -Run $item -Criterion $criterion
            & $add "| $($criterion.Number) | $(Get-InteropRecordCell -Text $criterion.Title) | $($outcome.Outcome) | $(Get-InteropRecordCell -Text $outcome.Evidence) |"
            $evidenceLists.Add([pscustomobject]@{ Criterion = $criterion; Outcome = $outcome })
        }
        & $add ""

        & $add "### Assertions behind each outcome"
        & $add ""
        foreach ($entry in $evidenceLists) {
            & $add "Criterion $($entry.Criterion.Number), $(Protect-InteropRecordText -Text $entry.Criterion.Title)."
            & $add ""
            if (@($entry.Outcome.Assertions).Count -eq 0) {
                & $add "- no assertions: $(Protect-InteropRecordText -Text $entry.Outcome.Evidence)"
            }
            foreach ($assertion in @($entry.Outcome.Assertions)) {
                & $add "- $($assertion['outcome']): $(Get-InteropRecordCell -Text $assertion['name'])"
            }
            foreach ($pattern in @($entry.Outcome.Missing)) {
                & $add "- FAIL: no assertion matches ``$(Get-InteropRecordCell -Text $pattern)``"
            }
            & $add ""
        }
    }

    $text = (($lines -join "`n").TrimEnd() + "`n")
    Assert-InteropRecordHasNoSecret -Text $text
    return $text
}

<#
.SYNOPSIS
    Writes the validation record for one or more run folders to validation-<DMS version>.md and returns its path.

.DESCRIPTION
    Invoke it after the harness has run, with one run folder per engine (the folders -Scenario All wrote):

        Import-Module ./eng/verification/PublisherSnapshotInterop/PublisherSnapshotInterop.psm1
        Write-InteropValidationRecord -RunFolder <postgresql run folder>, <mssql run folder>

    The record goes to eng/verification/PublisherSnapshotInterop/results by default. The DMS version in the
    file name is the Discovery version the runs advertise.
#>
function Write-InteropValidationRecord {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes one markdown file the caller names; -WhatIf adds no value.')]
    param(
        [Parameter(Mandatory)]
        [string[]]$RunFolder,

        [string]$OutputFolder = (Join-Path $PSScriptRoot "results")
    )

    $runs = @($RunFolder | ForEach-Object { Read-InteropRun -RunFolder $_ })
    $text = ConvertTo-InteropValidationRecord -Run $runs
    $version = [string]$runs[0].Manifest["versions"]["dmsDiscoveryVersion"]
    $safeVersion = ($version -replace "[^A-Za-z0-9.+-]", "-")
    if (-not $safeVersion) {
        throw "The runs record no Discovery version, so the record file cannot be named."
    }

    $null = New-Item -ItemType Directory -Path $OutputFolder -Force
    $path = Join-Path $OutputFolder "validation-$safeVersion.md"
    [System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
    return $path
}

#endregion

Export-ModuleMember -Function `
    Get-InteropDefaultPublisherImage, Get-InteropImageDigest, Get-InteropRunFolderName, New-InteropRunToken, `
    Get-InteropResourceName, Get-InteropDatabaseName, ConvertTo-InteropEnvironmentContent, Get-InteropEnvironmentOverride, `
    Get-InteropEndpoint, Get-InteropEnvironmentValue, Get-InteropSourceDatabaseName, Resolve-InteropScenario, Get-InteropNonDefaultSetting, ConvertTo-InteropManifest, `
    Assert-InteropManifestHasNoSecret, Write-InteropStep, Wait-InteropCondition, Invoke-InteropDocker, Get-InteropStackContainer, `
    Assert-InteropNoForeignStack, Write-InteropEnvironmentFile, Wait-InteropHttp, Get-InteropPostgresMaxConnection, `
    Set-InteropPostgresMaxConnection, Start-InteropStack, Assert-InteropStackUsable, Get-InteropAdminToken, `
    Get-InteropSourceDataStore, New-InteropDataStore, New-InteropClient, Get-InteropClientToken, Invoke-InteropDmsRequest, `
    Copy-InteropSchoolYear, Assert-InteropSelectionLogging, Get-InteropVersion, Assert-InteropPublisherImage, Write-InteropRunManifest, `
    Assert-InteropSqlIdentifier, Get-InteropSnapshotName, Get-InteropSnapshotLoginName, New-InteropReadOnlyPassword, `
    Get-InteropSnapshotConnectionString, Get-InteropPostgresReadOnlySql, Get-InteropSqlServerReadOnlySql, `
    Get-InteropSqlServerSnapshotSql, Get-InteropSnapshotRemovalSql, Get-InteropSnapshotWaitBound, `
    Resolve-InteropSnapshotProbeState, Test-InteropSeedRaceFailure, Invoke-InteropScriptCaptured, Invoke-InteropSql, `
    New-InteropSnapshot, Remove-InteropSnapshot, Register-InteropSnapshotDerivative, Remove-InteropSnapshotDerivative, `
    Get-InteropResponseText, Invoke-InteropDmsProbe, Wait-InteropSnapshotState, Test-InteropSnapshotServesSnapshotData, `
    Get-InteropPublisherContainerName, Get-InteropPublisherArgument, ConvertFrom-InteropDockerTimestamp, Get-InteropLogWindow, `
    Assert-InteropFileHasNoSecret, Invoke-InteropPublisher, Get-InteropRequestSurface, ConvertFrom-InteropSelectionLine, `
    ConvertFrom-InteropLogTimestamp, Get-InteropDmsLogLine, Get-InteropTargetSelections, ConvertTo-InteropDmsRequest, `
    Get-InteropSurfaceCount, Get-InteropPublisherLineSurface, Get-InteropSourceSurfaces, Compare-InteropSurfaceCount, `
    Get-InteropMarkerName, New-InteropArrangementPlan, Get-InteropMarkerExpectation, Get-InteropMarkerMismatch, `
    Get-InteropClassPeriodPath, New-InteropClassPeriodBody, ConvertTo-InteropArrangementManifest, Invoke-InteropDmsWrite, `
    Get-InteropClassPeriod, Get-InteropChangeVersion, Get-InteropMarkerObservation, Test-InteropArrangementVisibility, `
    Add-InteropArrangementToManifest, Set-InteropSourceArrangement, `
    New-InteropResult, ConvertTo-InteropComparable, Add-InteropAssertion, Get-InteropResultFailure, Write-InteropResultFile, `
    Get-InteropNoSnapshotExpectation, ConvertTo-InteropProblemSummary, Get-InteropOperatorLine, `
    Get-InteropNoSnapshotObservation, Get-InteropNoSnapshotAssertion, Get-InteropDocumentCount, Invoke-InteropNoSnapshotScenario, `
    Get-InteropIsolatedExpectation, Get-InteropIsolatedExpectationName, Get-InteropCountUnion, Get-InteropPublisherChangeVersion, `
    Get-InteropPublisherRunObservation, Get-InteropTargetMarkerState, Get-InteropTargetMarkerStateMap, Read-InteropPublisherRun, `
    Get-InteropIsolatedAssertion, Invoke-InteropIsolatedScenario, `
    Test-InteropSqlServerMemoryFailure, Assert-InteropPublishedImageSelection, Assert-InteropPublishedDiscoveryVersion, Assert-InteropPublishedImage, Select-InteropRepoDigest, Get-InteropContainerImage, `
    Protect-InteropRecordText, Assert-InteropRecordHasNoSecret, Read-InteropRun, Get-InteropRecordCriterion, Get-InteropHarnessCommand, `
    ConvertTo-InteropValidationRecord, Write-InteropValidationRecord
