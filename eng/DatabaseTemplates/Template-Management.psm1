# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Import-Module ../Package-Management.psm1 -Force
Import-Module ../Dms-Management.psm1 -Force
Import-Module ../SchoolYear-Loader.psm1 -Force -Global

<#
.SYNOPSIS
    Initializes the Ed-Fi Bulk Load client and working directories.

.DESCRIPTION
    Sets up paths for the bulk loader, XSDs, and temporary directories required for loading sample data.

.PARAMETER BulkLoadVersion
    Diagnostic override of the repo-pinned BulkLoadClient version. Must be an exact
    three-part numeric version (e.g. 7.3.20144); partial or wildcard values are rejected.
    Normal template builds omit it and use the shared pin from Package-Management.psm1.

.PARAMETER BaseDirectory
    The base directory where temporary folders will be created.

.OUTPUTS
    Hashtable containing paths to the bulk loader executable, XSD directory, and working directory.
#>
function Initialize-BulkLoad {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Template tooling intentionally writes operator progress to the console; the function returns path metadata, so Write-Output would corrupt the pipeline result.')]
    param(
        [string]
        $BulkLoadVersion = ''
    )

    if ([string]::IsNullOrWhiteSpace($BulkLoadVersion)) {
        $BulkLoadVersion = Get-BulkLoadClientPinnedVersion
    }
    elseif ($BulkLoadVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "Initialize-BulkLoad: -BulkLoadVersion is a diagnostic override and requires an exact three-part numeric version (current pin: $(Get-BulkLoadClientPinnedVersion)). Partial or wildcard versions float to the newest matching feed build and are not allowed for template builds."
    }

    $bulkLoadClientExe = (Join-Path -Path (Get-BulkLoadClient -PackageVersion $BulkLoadVersion).Trim() -ChildPath "tools/net*/any/EdFi.BulkLoadClient.Console.dll")
    $bulkLoadClientExe = Resolve-Path $bulkLoadClientExe

    $xsdDirectory = "$($PSScriptRoot)/tmp/XSD"
    New-Item -Path $xsdDirectory -Type Directory -Force | Out-Null

    $workingDirectory = "$($PSScriptRoot)/tmp/.working"
    New-Item -Path $workingDirectory -Type Directory -Force | Out-Null

    Write-Host
    Write-Host "Initialized Bulk Load and working directories" -ForegroundColor Green -NoNewline
    Write-Host

    return @{
        WorkingDirectory  = (Resolve-Path $workingDirectory)
        XsdDirectory      = (Resolve-Path $xsdDirectory)
        bulkLoadClientExe = $bulkLoadClientExe
    }
}

<#
.SYNOPSIS
    Initializes the Ed-Fi Data Management System (DMS) application.

.DESCRIPTION
    This function performs the following tasks in sequence:
    - Adds a new client to the DMS configuration.
    - Retrieves an access token for the client.
    - Adds a vendor entity to the system.
    - Initializes a new application for the vendor using a predefined claim set.
    It returns the application ID, key and secret required for authenticated communication with the DMS API.

.PARAMETER CmsUrl
    The base URL of the DMS configuration service.

.PARAMETER CmsToken
    The CMS access token for authorization.

.PARAMETER ClaimSetName
    The claim set to assign to the application.

.PARAMETER ApplicationName
    The name of the application to create.

.OUTPUTS
    A hashtable containing the Id, Key and Secret for the initialized DMS application.

.EXAMPLE
    $secrets = Get-KeySecret -CmsUrl "http://localhost:8081" -CmsToken $token -ClaimSetName "EdfiSandbox"
#>
function Get-KeySecret() {
    param (
        [Parameter(Mandatory = $true)]
        [string]$CmsUrl,

        [Parameter(Mandatory = $true)]
        [string]$CmsToken,

        [Parameter(Mandatory = $true)]
        [string]$ClaimSetName,

        [string]$ApplicationName = "Demo application",

        [long[]]$DataStoreIds = @(),

        # Education organizations the application is scoped to. When empty, Add-Application applies
        # its own default; pass an explicit set to authorize relationship-scoped resources beyond
        # the default district hierarchy (e.g. the DS 6.1 Educator Preparation Provider orgs).
        [long[]]$EducationOrganizationIds = @()
    )

    $params = @{
        CmsUrl      = $CmsUrl
        AccessToken = $CmsToken
    }

    # Add Vendor
    $params.VendorId = Add-Vendor @params

    # Add an Application and get Id, Key and Secret
    $params.ClaimSetName = $ClaimSetName
    $params.ApplicationName = $ApplicationName
    $params.DataStoreIds = $DataStoreIds
    if ($EducationOrganizationIds.Count -gt 0) {
        $params.EducationOrganizationIds = $EducationOrganizationIds
    }
    $keySecret = Add-Application @params

    return $keySecret
}

<#
    .SYNOPSIS
        Invokes the bulk data loader tool with configurable options.

    .DESCRIPTION
        Runs the Bulk Loader executable with specified parameters for API connection,
        concurrency, retries, metadata handling, and file paths. Supports toggling flags
        like force reload and XML validation.

    .PARAMETER BaseUrl
        The base API URL.

    .PARAMETER Key
        API key for authentication.

    .PARAMETER Secret
        API secret for authentication.

    .PARAMETER SampleDataDirectory
        Directory path containing sample data files.

    .PARAMETER Paths
        Hashtable of important paths (WorkingDirectory, XsdDirectory, BulkLoadClientExe).

    .PARAMETER Extension
        File extension filter (default "ed-fi").

    .PARAMETER ForceReloadMetadata
        Switch to force reload metadata from metadata URL (-f flag).

    .PARAMETER SkipXmlValidation
        Switch to skip XML validation against XSD (-n flag).

    .PARAMETER ForceReloadData
        Switch to clear working directory *.hash files to prepare for fresh load

    .PARAMETER MaxConcurrentConnections
        Max concurrent connections to API (-c flag). Default 100.

    .PARAMETER RetryCount
        Number of times to retry submitting a resource (-r flag). Default 1.

    .PARAMETER MaxSimultaneousRequests
        Max simultaneous API requests (-l flag). Default 500.

    .PARAMETER MaxBufferedTasks
        Max concurrent tasks buffered (-t flag). Default 50.

    .PARAMETER AllowUnresolvedReferences
        Treat the load as successful when its only failures are HTTP 409 unresolved-reference errors
        (up to MaxToleratedUnresolvedReferences). Used for the populated template load: DS 6.1
        interleaves educator-preparation records into core sample files, so a few records reference
        educator-prep entities that the template intentionally excludes. Any other failure still fails.

    .PARAMETER MaxToleratedUnresolvedReferences
        Cap on tolerated unresolved-reference failures when -AllowUnresolvedReferences is set (default
        50); a larger count signals an over-broad exclusion and fails the load.

    .EXAMPLE
        Invoke-BulkLoad -BaseUrl "http://api.local" -Key "abc" -Secret "123" -SampleDataDirectory "C:\Data" -Paths $paths -ForceReloadMetadata -SkipXmlValidation
#>
function Invoke-BulkLoad {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Template tooling intentionally writes operator progress to the console.')]
    [CmdletBinding()]
    param (
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [string]$Key,

        [Parameter(Mandatory = $true)]
        [string]$Secret,

        [Parameter(Mandatory = $true)]
        [string]$SampleDataDirectory,

        [Parameter(Mandatory = $true)]
        [hashtable]$BulkLoadClientPaths,

        [string]$Extension = $null,

        [switch]$ForceReloadMetadata,

        [switch]$SkipXmlValidation,

        [switch]$ForceReloadData,

        [int]$MaxConcurrentConnections = 100,

        [int]$RetryCount = 1,

        [int]$MaxSimultaneousRequests = 500,

        [int]$MaxBufferedTasks = 50,

        [switch]$AllowUnresolvedReferences,

        [int]$MaxToleratedUnresolvedReferences = 50
    )

    if ($ForceReloadData) {
        Get-ChildItem -Path $BulkLoadClientPaths.WorkingDirectory -Filter *.hash -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }

    $options = @(
        "-b", $BaseUrl,
        "-d", $SampleDataDirectory,
        "-w", $BulkLoadClientPaths.WorkingDirectory,
        "-k", $Key,
        "-s", $Secret,
        "-c", $MaxConcurrentConnections.ToString(),
        "-r", $RetryCount.ToString(),
        "-l", $MaxSimultaneousRequests.ToString(),
        "-t", $MaxBufferedTasks.ToString(),
        "-x", $BulkLoadClientPaths.XsdDirectory,
        "-o", "$BaseUrl/oauth/token"
    )

    if ($ForceReloadMetadata) { $options += "-f" }
    if ($SkipXmlValidation) { $options += "-n" }
    if (-not [string]::IsNullOrEmpty($Extension)) {
        $options += "-e"
        $options += $Extension
    }

    $previousColor = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = "Cyan"
    Write-Output "Executing: dotnet $($BulkLoadClientPaths.bulkLoadClientExe) $($options -join ' ')"
    $host.UI.RawUI.ForegroundColor = $previousColor

    # Tee the loader output to a log so a non-zero exit can be classified by failure type.
    $bulkLoadLog = Join-Path ([System.IO.Path]::GetTempPath()) ("bulkload-" + [System.Guid]::NewGuid().ToString('N') + ".log")
    & dotnet $BulkLoadClientPaths.bulkLoadClientExe @options 2>&1 | Tee-Object -FilePath $bulkLoadLog
    $bulkLoadExitCode = $LASTEXITCODE

    if ($bulkLoadExitCode -ne 0) {
        # A populated template intentionally omits educator-preparation data, but DS 6.1 interleaves
        # educator-prep records into otherwise-core sample files (e.g. StudentAssessmentSample.xml has
        # assessments for educator-prep students defined in the excluded Candidate.xml). Those few
        # stragglers fail with HTTP 409 unresolved-reference because the entity they point at was
        # excluded. With -AllowUnresolvedReferences, tolerate ONLY that case (and only a small,
        # capped number) so any real failure - 403/500/validation/etc. - still fails the build.
        #
        # Stream the (possibly very large) log for HTTP 4xx/5xx failure lines only, then classify them
        # via Get-BulkLoadFailureClassification (which is unit-tested in isolation).
        $failureLines = @(
            Select-String -Path $bulkLoadLog -Pattern ' - (4\d\d|5\d\d) - \{' |
                Select-Object -ExpandProperty Line
        )
        $classification = Get-BulkLoadFailureClassification `
            -FailureLines $failureLines `
            -AllowUnresolvedReferences:$AllowUnresolvedReferences `
            -MaxToleratedUnresolvedReferences $MaxToleratedUnresolvedReferences

        if ($classification.Tolerated) {
            Write-Warning "BulkLoad reported $($classification.UnresolvedReferenceCount) unresolved-reference (409) failures and no other errors (cap $MaxToleratedUnresolvedReferences). These are the expected stragglers from records that reference excluded educator-preparation data; treating the load as successful."
        }
        else {
            Write-Error "BulkLoad failed with exit code $bulkLoadExitCode"
            exit $bulkLoadExitCode
        }
    }

    Write-Host
    Write-Host "BulkLoad executed successfully" -ForegroundColor Green -NoNewline
    Write-Host
}

<#
.SYNOPSIS
    Classifies a failed BulkLoad's HTTP 4xx/5xx failure lines to decide whether the non-zero exit is
    tolerable (treated as success).

.DESCRIPTION
    A non-zero BulkLoad exit is tolerable ONLY when ALL of the following hold:
      * -AllowUnresolvedReferences was requested (the populated DS 6.1 filtered-load case); and
      * every parsed failure line is an HTTP 409 unresolved-reference (no other 4xx/5xx failure); and
      * the unresolved-reference count is greater than zero and within MaxToleratedUnresolvedReferences.
    Any fatal (non-unresolved-reference) failure, zero unresolved references, an over-threshold count,
    or -AllowUnresolvedReferences being absent (e.g. DS 5.2 release validation) makes the load NOT
    tolerable, so the build fails. Extracted from Invoke-BulkLoad so the decision is unit-testable.

.PARAMETER FailureLines
    The HTTP 4xx/5xx failure lines already parsed from the BulkLoad log (Invoke-BulkLoad streams the log
    with Select-String so the full, possibly very large, log is never loaded into memory here).

.PARAMETER AllowUnresolvedReferences
    Opts into tolerating unresolved-reference-only failures. Absent => strict (nothing is tolerated).

.PARAMETER MaxToleratedUnresolvedReferences
    Inclusive upper bound on tolerated unresolved-reference failures (default 50). A larger count signals
    an over-broad exclusion and is not tolerated.
#>
function Get-BulkLoadFailureClassification {
    param (
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$FailureLines,

        [switch]$AllowUnresolvedReferences,

        [int]$MaxToleratedUnresolvedReferences = 50
    )

    # A failure is tolerable ONLY when it is an HTTP 409 unresolved-reference. Everything else is fatal:
    # a non-409 line (even one that happens to contain "unresolved-reference", e.g. a 500), a 409 without
    # the marker (e.g. a duplicate-key conflict), or any other 4xx/5xx. The caller pre-filters FailureLines
    # to ' - <code> - {' lines, so matching ' - 409 - {' here pins the HTTP status to 409.
    $fatalFailures = @($FailureLines | Where-Object {
            -not ($_ -match ' - 409 - \{' -and $_ -match 'unresolved-reference')
        })
    $unresolvedReferenceCount = @($FailureLines | Where-Object {
            $_ -match ' - 409 - \{' -and $_ -match 'unresolved-reference'
        }).Count

    $tolerated = (
        $AllowUnresolvedReferences.IsPresent -and
        $fatalFailures.Count -eq 0 -and
        $unresolvedReferenceCount -gt 0 -and
        $unresolvedReferenceCount -le $MaxToleratedUnresolvedReferences
    )

    return [pscustomobject]@{
        Tolerated                = $tolerated
        UnresolvedReferenceCount = $unresolvedReferenceCount
        FatalFailureCount        = $fatalFailures.Count
    }
}

<#
.SYNOPSIS
    Returns engine-specific BulkLoadClient tuning for template generation.

.DESCRIPTION
    PostgreSQL keeps Invoke-BulkLoad's established defaults. MSSQL uses conservative
    relational-backend settings so a populated load stays below DMS's fixed-window rate limiter
    without creating enough concurrent writes to deadlock SQL Server dimension inserts. Extra
    retries cover the remaining transient relational conflicts without cascading failures into
    unresolved references or authorization errors.
#>
function Get-TemplateBulkLoadTuning {
    param (
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql"
    )

    if ($DatabaseEngine -eq "mssql") {
        return @{
            MaxConcurrentConnections = 5
            MaxSimultaneousRequests   = 5
            MaxBufferedTasks          = 2
            RetryCount                = 5
        }
    }

    # An empty splat preserves Invoke-BulkLoad's PostgreSQL defaults byte-for-byte.
    return @{}
}

<#
.SYNOPSIS
    Dumps the specified database to a file.

.DESCRIPTION
    For PostgreSQL, uses Docker to execute `pg_dump` inside a running PostgreSQL container,
    targeting only the specified schemas.

    For MSSQL, runs a full `BACKUP DATABASE` inside the running SQL Server container via
    `sqlcmd`, then copies the resulting `.bak` file out with `docker cp` and removes the
    transient in-container backup file. A `.bak` is always a full-database artifact - SQL
    Server has no per-schema equivalent to `pg_dump -n` - so `DatabaseSchemas` is ignored
    on MSSQL.

.PARAMETER DatabaseEngine
    "postgresql" or "mssql". Defaults to "postgresql".

.PARAMETER ContainerName
    Name of the Docker container running the database. Defaults to "dms-postgresql" for
    PostgreSQL and "dms-mssql" for MSSQL.

.PARAMETER DatabaseName
    Name of the database to dump.

.PARAMETER DatabaseSchemas
    Array of schemas to include in the dump. PostgreSQL only; ignored on MSSQL, where a
    `BACKUP DATABASE` always captures the entire database.

.PARAMETER BackupDirectory
    Directory where the dump file will be saved.

.PARAMETER BackupFileName
    Filename to use for the backup.

.PARAMETER MssqlPassword
    The MSSQL "sa" password. Defaults to environment variable MSSQL_SA_PASSWORD or "abcdefgh1!".
#>
function Invoke-DatabaseDump {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Template tooling intentionally writes operator progress to the console.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); the account is always "sa" so there is no companion username parameter, and a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
    param (
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql",

        [string]$ContainerName = $(if ($DatabaseEngine -eq "mssql") { "dms-mssql" } else { "dms-postgresql" }),

        [string]$DatabaseName = "edfi_datamanagementservice",
        [string[]]$DatabaseSchemas,
        [string]$BackupDirectory,
        [string]$BackupFileName,

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!"
    )

    $backupPath = Join-Path $BackupDirectory $BackupFileName

    if ($DatabaseEngine -eq "mssql") {
        if ($DatabaseName -notmatch "^[A-Za-z0-9_]+$") {
            throw "Database name '$DatabaseName' contains unsupported characters."
        }

        if ($BackupFileName -notmatch "^[A-Za-z0-9_.-]+$") {
            throw "Backup file name '$BackupFileName' contains unsupported characters."
        }

        $containerBackupPath = "/var/opt/mssql/data/$BackupFileName"

        try {
            $backupSql = "BACKUP DATABASE [$DatabaseName] TO DISK = N'$containerBackupPath' WITH INIT;"
            & docker exec -e "SQLCMDPASSWORD=$MssqlPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q $backupSql
            if ($LASTEXITCODE -ne 0) { throw "BACKUP DATABASE of '$DatabaseName' failed in container '$ContainerName'." }

            & docker cp "$($ContainerName):$containerBackupPath" $backupPath
            if ($LASTEXITCODE -ne 0) { throw "Failed to copy backup '$containerBackupPath' out of container '$ContainerName'." }
        }
        finally {
            # BACKUP DATABASE can leave a complete or partial file behind even when sqlcmd or
            # docker cp fails. Always make a best-effort cleanup without masking the primary
            # backup/copy failure (the restore path follows the same cleanup contract).
            try {
                & docker exec $ContainerName rm -f $containerBackupPath 2>$null
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "Could not remove transient backup '$containerBackupPath' from container '$ContainerName'." -WarningAction Continue
                }
            }
            catch {
                Write-Warning "Could not remove transient backup '$containerBackupPath' from container '$ContainerName': $($_.Exception.Message)" -WarningAction Continue
            }
        }

        Write-Host
        Write-Host "Backup Created: " -ForegroundColor Green -NoNewline
        Write-Host (Resolve-Path $backupPath)
        Write-Host

        return
    }

    $options = @("exec", $ContainerName, "pg_dump", "-U", "postgres", $DatabaseName)

    foreach ($schema in $DatabaseSchemas) {
        $options += "-n"
        $options += $schema
    }

    # Schema-scoped pg_dump never emits CREATE EXTENSION statements, but the dumped
    # dms.uuidv5() function requires pgcrypto's digest(). Emit the extension bootstrap
    # ahead of the dump so the template restores as a self-contained, writable database.
    "CREATE EXTENSION IF NOT EXISTS ""pgcrypto"";" | Out-File -FilePath $backupPath -Encoding utf8

    & docker @options | Out-File -FilePath $backupPath -Encoding utf8 -Append

    Write-Host
    Write-Host "Backup Created: " -ForegroundColor Green -NoNewline
    Write-Host (Resolve-Path $backupPath)
    Write-Host
}

<#
.SYNOPSIS
    Generates a .csproj NuGet project for the database backup.

.DESCRIPTION
    Uses project metadata to create a NuGet-compatible .csproj file and includes the database backup.

.PARAMETER Config
    Hashtable with values from the configuration file.

.PARAMETER BackupDirectory
    The directory containing the backup file and where the project will be created.
#>
function New-DatabaseTemplateCsproj {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Template tooling intentionally writes operator progress to the console.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Template build helper is invoked non-interactively and writes into a controlled temporary package workspace.')]
    param (
        [hashtable]$Config,
        [string]$BackupDirectory
    )

    $BackupDirectory = Resolve-Path $BackupDirectory
    $csprojPath = Join-Path $BackupDirectory $Config.PackageProjectName
    $backupPath = Join-Path $BackupDirectory $Config.DatabaseBackupName

    $params = @{
        CsprojPath     = $csprojPath
        Id             = $Config.Id
        Title          = $Config.Title
        Description    = $Config.Description
        Authors        = $Config.Authors
        ProjectUrl     = $Config.ProjectUrl
        License        = $Config.License
        ForceOverwrite = $true
    }

    New-CsprojForNuget @params

    Add-FileToCsProjForNuget -CsprojPath $csprojPath -SourceTargetPair @{ source = $backupPath; target = "." }

    Write-Host "Project Created: " -ForegroundColor Green -NoNewline
    Write-Host (Get-ChildItem $csprojPath).FullName
}

<#
.SYNOPSIS
    Builds a NuGet package from the .csproj.

.DESCRIPTION
    Restores and packs the .csproj file into a .nupkg file using the provided version.

.PARAMETER PackageVersion
    Version to assign to the NuGet package.

.PARAMETER Config
    Hashtable with values from the configuration file.

.PARAMETER BackupDirectory
    Directory where the .csproj and output package exist.
#>
function Build-NuGetPackage {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Template tooling intentionally writes operator progress to the console.')]
    param (
        [string]$PackageVersion,
        [hashtable]$Config,
        [string]$BackupDirectory
    )

    $csprojPath = Join-Path $BackupDirectory $Config.PackageProjectName

    &dotnet restore $csprojPath | Out-Null
    &dotnet pack $csprojPath `
        --no-build `
        --no-restore `
        --output $BackupDirectory `
        -p:NoDefaultExcludes=true `
        -p:Version="$PackageVersion"

    Write-Host "Package Created: " -ForegroundColor Green -NoNewline
    Write-Host (Get-ChildItem $csprojPath).FullName
    Write-Host
}

<#
.SYNOPSIS
    Asserts that a data store id is registered in the Configuration Service.

.DESCRIPTION
    The Configuration Service protects data store connection strings in API
    responses, so a caller that needs a specific target database must pass the
    id returned when the data store was registered; membership of that id in
    the registered set is the only property that can be verified here.
#>
function Assert-DataStoreIdRegistered {
    param (
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$DataStores,

        [Parameter(Mandatory = $true)]
        [long]$DataStoreId
    )

    $registeredIds = @($DataStores | ForEach-Object { [long]$_.id })

    if ($DataStoreId -notin $registeredIds) {
        $registeredIdList = if ($registeredIds.Count -eq 0) { "<none>" } else { $registeredIds -join ', ' }
        throw "Data store id '$DataStoreId' is not registered in the Configuration Service. Registered ids: $registeredIdList."
    }
}

<#
.SYNOPSIS
    Resolves which data store id a template build should bind applications to.

.DESCRIPTION
    A caller-supplied data store id is validated for membership and used as-is.
    Without one, an empty data store list returns $null so the caller registers
    a new data store. Existing data stores cannot be matched to a requested
    database because the Configuration Service protects connection strings in
    API responses, so a bound database name with pre-existing data stores is an
    error; callers that bind neither parameter keep first-data-store behavior.
#>
function Resolve-DataStoreIdForTemplate {
    param (
        [AllowEmptyCollection()]
        [object[]]$DataStores = @(),

        [System.Nullable[long]]$RequestedDataStoreId = $null,

        [bool]$DatabaseNameBound = $false
    )

    if ($null -ne $RequestedDataStoreId) {
        Assert-DataStoreIdRegistered -DataStores $DataStores -DataStoreId $RequestedDataStoreId
        return [long]$RequestedDataStoreId
    }

    if ($DataStores.Count -eq 0) {
        return $null
    }

    if ($DatabaseNameBound) {
        throw "Existing data stores cannot be verified against a requested database because the Configuration Service protects connection strings in API responses. Pass -DataStoreId for the data store that targets the requested database."
    }

    return [long]$DataStores[0].id
}

<#
.SYNOPSIS
    Discovers the non-system schemas present in a database.

.DESCRIPTION
    For PostgreSQL, queries pg_namespace inside the running PostgreSQL container, excluding
    pg_* system schemas, information_schema, and public.

    For MSSQL, queries sys.schemas inside the running SQL Server container, excluding the
    built-in dbo, guest, sys, and INFORMATION_SCHEMA schemas, plus the db_* fixed-role
    schemas every database carries (e.g. db_datareader, db_owner).

    Throws when the database has no user schemas, which indicates it was never provisioned.

.PARAMETER DatabaseEngine
    "postgresql" or "mssql". Defaults to "postgresql".

.PARAMETER ContainerName
    Name of the Docker container running the database. Defaults to "dms-postgresql" for
    PostgreSQL and "dms-mssql" for MSSQL.

.PARAMETER DatabaseName
    Name of the database to inspect.

.PARAMETER MssqlPassword
    The MSSQL "sa" password. Defaults to environment variable MSSQL_SA_PASSWORD or "abcdefgh1!".
#>
function Get-UserSchemaNames {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Function intentionally returns a collection of discovered database schema names.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); the account is always "sa" so there is no companion username parameter, and a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
    param (
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql",

        [string]$ContainerName = $(if ($DatabaseEngine -eq "mssql") { "dms-mssql" } else { "dms-postgresql" }),

        [Parameter(Mandatory = $true)]
        [string]$DatabaseName,

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!"
    )

    if ($DatabaseEngine -eq "mssql") {
        if ($DatabaseName -notmatch "^[A-Za-z0-9_]+$") {
            throw "Database name '$DatabaseName' contains unsupported characters."
        }

        $query = "SET NOCOUNT ON; SELECT name FROM sys.schemas WHERE name NOT IN ('dbo', 'guest', 'sys', 'INFORMATION_SCHEMA') AND name NOT LIKE 'db[_]%' ORDER BY name;"
        $output = & docker exec -e "SQLCMDPASSWORD=$MssqlPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d $DatabaseName -C -b -h -1 -W -Q $query

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to discover schemas in database '$DatabaseName'."
        }

        $schemas = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })

        if ($schemas.Count -eq 0) {
            throw "No user schemas found in database '$DatabaseName'; the database does not appear to be provisioned."
        }

        return $schemas
    }

    $query = "SELECT nspname FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname NOT IN ('information_schema', 'public') ORDER BY nspname;"
    $output = & docker exec $ContainerName psql -U postgres -d $DatabaseName -tA -c $query

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to discover schemas in database '$DatabaseName'."
    }

    $schemas = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })

    if ($schemas.Count -eq 0) {
        throw "No user schemas found in database '$DatabaseName'; the database does not appear to be provisioned."
    }

    return $schemas
}

<#
.SYNOPSIS
    Restores a database template package into a freshly created database.

.DESCRIPTION
    Locates the single .nupkg in the package directory and restores its database
    artifact inside the running database container. Returns the name of the
    restored package file.

    For PostgreSQL, extracts the package's .sql dump, terminates any lingering
    connections to the target database, drops and recreates it, and restores
    the dump into it.

    For MSSQL, extracts the package's .bak backup, copies it into the container,
    puts any pre-existing target database into single-user mode and drops it,
    reads every logical data/log file name via `RESTORE FILELISTONLY`, and runs
    `RESTORE DATABASE ... WITH MOVE, REPLACE` with one MOVE clause per file to
    relocate all of them under the target database's own name.

.PARAMETER PackageDirectory
    Directory containing the template .nupkg (default: current directory).

.PARAMETER DatabaseName
    The database to drop, recreate, and restore the dump into.

.PARAMETER DatabaseEngine
    "postgresql" or "mssql". Defaults to "postgresql".

.PARAMETER ContainerName
    The running database container hosting the database. Defaults to
    "dms-postgresql" for PostgreSQL and "dms-mssql" for MSSQL.

.PARAMETER MssqlPassword
    The MSSQL "sa" password. Defaults to environment variable MSSQL_SA_PASSWORD or "abcdefgh1!".
#>
function Restore-TemplatePackage {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Template tooling intentionally writes operator progress to the console.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); the account is always "sa" so there is no companion username parameter, and a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
    param (
        [string]$PackageDirectory = ".",

        [Parameter(Mandatory = $true)]
        [string]$DatabaseName,

        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql",

        [string]$ContainerName = $(if ($DatabaseEngine -eq "mssql") { "dms-mssql" } else { "dms-postgresql" }),

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!"
    )

    if ($DatabaseName -notmatch "^[A-Za-z0-9_]+$") {
        throw "Database name '$DatabaseName' contains unsupported characters."
    }

    $package = Get-ChildItem -Path $PackageDirectory -Filter *.nupkg | Select-Object -First 1

    if ($null -eq $package) {
        throw "No .nupkg found in '$PackageDirectory'."
    }

    Write-Host "Restoring template package '$($package.Name)' into database '$DatabaseName'" -ForegroundColor Cyan

    $extractDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "template-restore-$([Guid]::NewGuid().ToString('N'))"
    $containerBakPath = $null

    try {
        New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
        $zipPath = Join-Path $extractDirectory "package.zip"
        Copy-Item $package.FullName $zipPath
        Expand-Archive -Path $zipPath -DestinationPath (Join-Path $extractDirectory "contents")

        if ($DatabaseEngine -eq "mssql") {
            $bakFile = Get-ChildItem -Path (Join-Path $extractDirectory "contents") -Filter *.bak -Recurse | Select-Object -First 1

            if ($null -eq $bakFile) {
                throw "No .bak backup found inside package '$($package.Name)'."
            }

            $containerBakPath = "/var/opt/mssql/data/template-restore-$([Guid]::NewGuid().ToString('N')).bak"

            & docker cp $bakFile.FullName "$($ContainerName):$containerBakPath" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed to copy the backup into container '$ContainerName'." }

            $existsQuery = "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'$DatabaseName') IS NOT NULL THEN 1 ELSE 0 END;"
            $existsOutput = & docker exec -e "SQLCMDPASSWORD=$MssqlPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d master -C -b -h -1 -W -Q $existsQuery
            if ($LASTEXITCODE -ne 0) { throw "Failed to check whether database '$DatabaseName' already exists." }

            $databaseExists = (@($existsOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1) -eq "1")

            if ($databaseExists) {
                $dropSql = "ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DatabaseName];"
                & docker exec -e "SQLCMDPASSWORD=$MssqlPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d master -C -b -Q $dropSql | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Failed to drop existing database '$DatabaseName'." }
            }

            $fileListQuery = "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$containerBakPath';"
            $fileListOutput = & docker exec -e "SQLCMDPASSWORD=$MssqlPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d master -C -b -h -1 -W -s "|" -Q $fileListQuery
            if ($LASTEXITCODE -ne 0) { throw "Failed to read the file list from backup '$($bakFile.Name)'." }

            # Collect every data (D) and log (L) row, not just the first of each: a multi-file backup
            # (secondary data file, extra filegroup, additional log) must relocate every file it lists,
            # or RESTORE DATABASE fails because some file is left pointing at its original in-container
            # path. Today's DMS templates are single data + single log, but this keeps a multi-file
            # .bak restorable too.
            $dataLogicalNames = [System.Collections.Generic.List[string]]::new()
            $logLogicalNames = [System.Collections.Generic.List[string]]::new()
            foreach ($line in $fileListOutput) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                $fields = $line -split '\|'
                if ($fields.Count -lt 3) { continue }
                $logicalName = $fields[0].Trim()
                $fileType = $fields[2].Trim()
                if ($fileType -eq "D") { $dataLogicalNames.Add($logicalName) }
                elseif ($fileType -eq "L") { $logLogicalNames.Add($logicalName) }
            }

            if ($dataLogicalNames.Count -eq 0 -or $logLogicalNames.Count -eq 0) {
                throw "Could not determine the data and log logical file names from backup '$($bakFile.Name)'."
            }

            # Emit one MOVE clause per file. The primary data file and first log keep today's plain
            # $DatabaseName-derived names, so a single-file .bak still produces the exact same RESTORE
            # command as before; every additional file gets a name suffixed with its own logical name,
            # so each lands at its own deterministic path under the same target data directory. Unlike
            # the MOVE...FROM side (which only needs single-quote escaping inside its N'' literal), an
            # extra logical name is interpolated into a new physical path here, so it is validated
            # against the same safe-character allow-list already used for $DatabaseName before use.
            $moveClauses = [System.Collections.Generic.List[string]]::new()
            for ($i = 0; $i -lt $dataLogicalNames.Count; $i++) {
                $logicalName = $dataLogicalNames[$i]
                if ($i -eq 0) {
                    $physicalName = "$DatabaseName.mdf"
                }
                else {
                    if ($logicalName -notmatch "^[A-Za-z0-9_]+$") {
                        throw "Data file logical name '$logicalName' from backup '$($bakFile.Name)' contains unsupported characters and cannot be used to derive a restore path."
                    }
                    $physicalName = "${DatabaseName}_${logicalName}.ndf"
                }
                $moveClauses.Add("MOVE N'$($logicalName.Replace("'", "''"))' TO N'/var/opt/mssql/data/$physicalName'")
            }
            for ($i = 0; $i -lt $logLogicalNames.Count; $i++) {
                $logicalName = $logLogicalNames[$i]
                if ($i -eq 0) {
                    $physicalName = "${DatabaseName}_log.ldf"
                }
                else {
                    if ($logicalName -notmatch "^[A-Za-z0-9_]+$") {
                        throw "Log file logical name '$logicalName' from backup '$($bakFile.Name)' contains unsupported characters and cannot be used to derive a restore path."
                    }
                    $physicalName = "${DatabaseName}_${logicalName}.ldf"
                }
                $moveClauses.Add("MOVE N'$($logicalName.Replace("'", "''"))' TO N'/var/opt/mssql/data/$physicalName'")
            }

            $restoreSql = "RESTORE DATABASE [$DatabaseName] FROM DISK = N'$containerBakPath' WITH $($moveClauses -join ', '), REPLACE;"
            & docker exec -e "SQLCMDPASSWORD=$MssqlPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d master -C -b -Q $restoreSql | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Restore of '$($bakFile.Name)' into '$DatabaseName' failed." }

            return $package.Name
        }

        $sqlFile = Get-ChildItem -Path (Join-Path $extractDirectory "contents") -Filter *.sql -Recurse | Select-Object -First 1

        if ($null -eq $sqlFile) {
            throw "No .sql dump found inside package '$($package.Name)'."
        }

        # A connected session blocks DROP DATABASE; terminate any lingering ones as defense in
        # depth (restores target freshly created verification databases, so nothing should be
        # connected here).
        & docker exec $ContainerName psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$DatabaseName' AND pid <> pg_backend_pid();" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to terminate existing connections to database '$DatabaseName'." }

        & docker exec $ContainerName psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $DatabaseName;" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to drop existing database '$DatabaseName'." }

        & docker exec $ContainerName psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $DatabaseName;" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to create database '$DatabaseName'." }

        & docker cp $sqlFile.FullName "$($ContainerName):/tmp/template-restore.sql" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to copy the dump into container '$ContainerName'." }

        & docker exec $ContainerName psql -U postgres -d $DatabaseName -v ON_ERROR_STOP=1 -f /tmp/template-restore.sql | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Restore of '$($sqlFile.Name)' into '$DatabaseName' failed." }

        return $package.Name
    }
    finally {
        # The transient in-container backup is removed on failure paths too, or repeated failed
        # restores accumulate GUID-named .bak files in /var/opt/mssql/data. Best-effort: a
        # cleanup hiccup after a successful restore should not fail the restore itself.
        if ($null -ne $containerBakPath) {
            & docker exec $ContainerName rm -f $containerBakPath 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Could not remove transient backup '$containerBakPath' from container '$ContainerName'."
            }
        }
        if (Test-Path $extractDirectory) {
            Remove-Item $extractDirectory -Recurse -Force
        }
    }
}

<#
.SYNOPSIS
    Builds a NuGet package containing a database backup.

.DESCRIPTION
    Substitutes "{StandardVersion}", "{DatabaseEngine}", and "{ArtifactExtension}" placeholders
    in the .psd1 configuration, dumps the target database (per -DatabaseEngine), and packages
    the resulting artifact (.sql for PostgreSQL, .bak for MSSQL) into a NuGet package.

.PARAMETER ConfigFilePath
    The path to the PowerShell data file (.psd1) containing configuration settings for the package.
    Defaults to "./MinimalTemplateSettings.psd1".

.PARAMETER StandardVersion
    The standard version string to substitute for "{StandardVersion}" placeholders in the configuration.

.PARAMETER PackageVersion
    The version to assign to the generated NuGet package.

.PARAMETER DatabaseName
    The database to dump into the template package.

.PARAMETER DumpAllUserSchemas
    Dump every non-system schema of the target database instead of only the dms schema.
    PostgreSQL only; MSSQL always backs up the entire database.

.PARAMETER DatabaseEngine
    "postgresql" or "mssql". Defaults to "postgresql". Substitutes the "{DatabaseEngine}"
    placeholder with "PostgreSql" or "MsSql" and the "{ArtifactExtension}" placeholder with
    "sql" or "bak" respectively.

.PARAMETER MssqlPassword
    The MSSQL "sa" password. Defaults to environment variable MSSQL_SA_PASSWORD or "abcdefgh1!".

.EXAMPLE
    Build-TemplateNuGetPackage -ConfigFilePath "./MinimalTemplateSettings.psd1" -StandardVersion "5.3.0" -PackageVersion "1.0.0"
#>
function Build-TemplateNuGetPackage {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Template tooling intentionally writes operator progress to the console.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); the account is always "sa" so there is no companion username parameter, and a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The MSSQL password is handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$ConfigFilePath,

        [Parameter(Mandatory = $true)]
        [string]$StandardVersion,

        [Parameter(Mandatory = $true)]
        [string]$PackageVersion,

        [string]$DatabaseName = "edfi_datamanagementservice",

        [switch]$DumpAllUserSchemas,

        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql",

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!"
    )

    $config = Import-PowerShellDataFile -Path $ConfigFilePath

    # Mirrors PostgreSql/.sql, the literal tokens the .psd1 files carried before these
    # placeholders were introduced, so PostgreSQL output stays byte-for-byte unchanged.
    $engineToken = if ($DatabaseEngine -eq "mssql") { "MsSql" } else { "PostgreSql" }
    $artifactExtension = if ($DatabaseEngine -eq "mssql") { "bak" } else { "sql" }

    foreach ($key in @($config.Keys)) {
        if ($null -ne $key -and $config[$key] -is [string]) {
            $config[$key] = $config[$key].Replace("{StandardVersion}", $StandardVersion)
            $config[$key] = $config[$key].Replace("{DatabaseEngine}", $engineToken)
            $config[$key] = $config[$key].Replace("{ArtifactExtension}", $artifactExtension)
        }
    }

    $databaseSchemas =
        if ($DumpAllUserSchemas) {
            $discoveredSchemas = @(Get-UserSchemaNames -DatabaseEngine $DatabaseEngine -DatabaseName $DatabaseName -MssqlPassword $MssqlPassword)
            Write-Host "Dumping all user schemas from '$DatabaseName': $($discoveredSchemas -join ', ')"
            $discoveredSchemas
        }
        else {
            @("dms")
        }

    Invoke-DatabaseDump -DatabaseEngine $DatabaseEngine -DatabaseName $DatabaseName -DatabaseSchemas $databaseSchemas -BackupDirectory './' -BackupFileName $config.DatabaseBackupName -MssqlPassword $MssqlPassword

    New-DatabaseTemplateCsproj -Config $config -BackupDirectory './'

    Build-NuGetPackage -PackageVersion $PackageVersion -Config $config -BackupDirectory './'
}

<#
.SYNOPSIS
    Extracts the distinct education organization ids defined in a populated sample data set.

.DESCRIPTION
    The populated sandbox application authorizes most resources by relationship to its claimed
    education organizations (RelationshipsWithEdOrgsOnly), so it must claim every education
    organization the sample data references. In Data Standard 6.1 the TPDM model folded into
    core, so the sample set adds an Educator Preparation Provider hierarchy (its own schools,
    LEAs, and post-secondary institutions) that is disjoint from the Grand Bend district. Rather
    than hard-code a per-version id list, this scans the EducationOrganization sample files for
    every education-organization identity/reference id so the sandbox is scoped to exactly the
    edorgs present — covering the EPP orgs in 6.1 and staying correct as the sample data evolves.
    Program ids and other non-edorg identifiers are intentionally excluded.

.PARAMETER SampleDataDirectory
    Directory of populated sample data files to scan (the EducationOrganization*.xml files).
#>
function Get-EducationOrganizationIdsFromSampleData {
    param (
        [Parameter(Mandatory = $true)]
        [string]$SampleDataDirectory
    )

    # Ed-Fi education-organization id element names (identity and reference). The generic
    # EducationOrganizationId covers any edorg referenced without a type-specific element.
    $edOrgIdElementNames = @(
        'EducationOrganizationId',
        'SchoolId',
        'LocalEducationAgencyId',
        'StateEducationAgencyId',
        'EducationServiceCenterId',
        'PostSecondaryInstitutionId',
        'EducatorPreparationProviderId',
        'OrganizationDepartmentId',
        'CommunityOrganizationId',
        'CommunityProviderId',
        'EducationOrganizationNetworkId'
    )
    $pattern = '<(' + ($edOrgIdElementNames -join '|') + ')>(\d+)</\1>'

    $ids = [System.Collections.Generic.HashSet[long]]::new()
    Get-ChildItem -Path $SampleDataDirectory -Filter 'EducationOrganization*.xml' -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            $content = Get-Content -Path $_.FullName -Raw
            foreach ($match in [regex]::Matches($content, $pattern)) {
                [void]$ids.Add([long]$match.Groups[2].Value)
            }
        }

    return @($ids | Sort-Object)
}

<#
.SYNOPSIS
    Returns the bulky educator-preparation sample file names to omit from a populated template load.

.DESCRIPTION
    In Data Standard 6.1 the TPDM model folded into core, so the v6.1.0 sample set adds a large
    educator-preparation data set the DS 5.2 populated template never carried — e.g.
    ProfessionalDevelopment.xml (~30 MB), Candidate.xml (~16 MB), PerformanceEvaluation.xml (~8 MB),
    plus the smaller "*-EdPrep" interchange variants. A populated *template* should stay comparable
    to DS 5.2, so these are excluded. AssessmentMetadata-EdPrep.xml is deliberately KEPT: it is
    self-contained, namespace-authorized assessment metadata that core StudentAssessment sample data
    references, so excluding it would orphan those references. Returns an empty set for sample data
    with no educator-prep files (e.g. DS 5.2), making the filter a no-op.

.PARAMETER SourceDirectory
    Directory of populated sample data files to inspect.
#>
function Get-EducatorPreparationSampleFileName {
    param (
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory
    )

    $keep = @('AssessmentMetadata-EdPrep.xml')

    $standaloneEpdmFiles = @(
        'Candidate.xml',
        'Path.xml',
        'PerformanceEvaluation.xml',
        'ProfessionalDevelopment.xml',
        'RecruitmentAndStaffing.xml'
    )

    $edPrepVariants = @(
        Get-ChildItem -Path $SourceDirectory -Filter '*-EdPrep.xml' -File -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name |
            Where-Object { $_ -notin $keep }
    )

    return @(
        $standaloneEpdmFiles + $edPrepVariants |
            Where-Object { Test-Path -Path (Join-Path $SourceDirectory $_) -PathType Leaf } |
            Sort-Object -Unique
    )
}

<#
.SYNOPSIS
    Returns a populated sample directory with the educator-preparation files removed.

.DESCRIPTION
    The BulkLoadClient loads every file in its target directory, so excluding files means pointing
    the load at a filtered copy. Copies the source directory into a fresh temporary directory minus
    the files from Get-EducatorPreparationSampleFileName; the original checkout is left untouched.
    Returns the original directory unchanged when there is nothing to exclude (e.g. DS 5.2).

.PARAMETER SourceDirectory
    Directory of populated sample data files to filter.
#>
function New-EducatorPreparationFilteredSampleDirectory {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal build helper invoked non-interactively; it only creates a temporary working copy of the sample directory, so -WhatIf/-Confirm semantics add no value.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Template tooling intentionally writes operator progress to the console; the function returns the directory path, so Write-Output would corrupt the pipeline result.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory
    )

    $excludedFiles = Get-EducatorPreparationSampleFileName -SourceDirectory $SourceDirectory
    if ($excludedFiles.Count -eq 0) {
        return $SourceDirectory
    }

    $destination = Join-Path ([System.IO.Path]::GetTempPath()) ("populated-sample-core-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    Get-ChildItem -Path $SourceDirectory -File |
        Where-Object { $excludedFiles -notcontains $_.Name } |
        ForEach-Object { Copy-Item -Path $_.FullName -Destination (Join-Path $destination $_.Name) -Force }

    Write-Host "Excluded $($excludedFiles.Count) educator-preparation sample file(s) from the populated load:" -ForegroundColor Yellow
    foreach ($excluded in $excludedFiles) {
        Write-Host "  - $excluded"
    }

    return $destination
}

enum TemplateType {
    Minimal
    Populated
}

<#
.SYNOPSIS
    Builds a database template and NuGet package.

.PARAMETER TemplateType
    Specifies the type of template to build. Valid values are:
    - Minimal
    - Populated

.PARAMETER DmsUrl
    The base URL of the DMS.

.PARAMETER CmsUrl
    The base URL of the CMS.

.PARAMETER MinimalSampleDataDirectory
    Directory containing the minimal sample data files to be loaded.

.PARAMETER PopulatedSampleDataDirectory
    Directory containing the populated sample data files to be loaded.

.PARAMETER Extension
    File extension filter for the sample data files (e.g., "ed-fi", "tpdm" ...).

.PARAMETER ConfigFilePath
    Path to the PowerShell data file (.psd1) containing the NuGet package settings.

.PARAMETER StandardVersion
    The Ed-Fi standard version to use in the NuGet package name.

.PARAMETER PackageVersion
    The version to assign to the generated NuGet package.

.PARAMETER ApplicationName
    The name of the application to create. Defaults to "Demo application".

.PARAMETER DataStoreDatabaseName
    Database the CMS-registered data store must target; also the database that is dumped into the template.

.PARAMETER DataStoreId
    Id of an already-registered data store to bind applications to. The Configuration Service
    protects data store connection strings in API responses, so the registrar of the data store
    must hand its id forward; requires -DataStoreDatabaseName so writes and the dump target the
    same database.

.PARAMETER DumpAllUserSchemas
    Dump every non-system schema of the target database instead of only the dms schema.
    PostgreSQL only; MSSQL always backs up the entire database.

.PARAMETER PostgresPassword
    The PostgreSQL password for database connection. Defaults to environment variable POSTGRES_PASSWORD or "abcdefgh1!".

.PARAMETER DatabaseEngine
    "postgresql" or "mssql". Defaults to "postgresql". Selects the engine used when dumping
    the data store database into the template package.

.PARAMETER MssqlPassword
    The MSSQL "sa" password. Defaults to environment variable MSSQL_SA_PASSWORD or "abcdefgh1!".

.EXAMPLE
    Build-Template -TemplateType Minimal `
        -DmsUrl "http://localhost:8080" `
        -CmsUrl "http://localhost:8081" `
        -MinimalSampleDataDirectory "./MinimalData" `
        -PopulatedSampleDataDirectory "./PopulatedData" `
        -Extension "ed-fi" `
        -ConfigFilePath "./MinimalTemplateSettings.psd1" `
        -StandardVersion "5.3.0" `
        -PackageVersion "1.0.0" `
        -PostgresPassword "mypassword"
#>
function Build-Template {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'The PostgreSQL and MSSQL passwords are handed to a database connection string / sqlcmd where they must be plaintext; there is no companion username credential and a PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The PostgreSQL and MSSQL passwords are handed to a database connection string / sqlcmd where they must be plaintext; SecureString adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Template tooling intentionally writes operator progress to the console.')]
    param (

        [Parameter(Mandatory = $true)]
        [TemplateType]$TemplateType,

        [Parameter(Mandatory = $true)]
        [string]$DmsUrl,

        [Parameter(Mandatory = $true)]
        [string]$CmsUrl,

        [Parameter(Mandatory = $true)]
        [string]$MinimalSampleDataDirectory,

        [string]$PopulatedSampleDataDirectory,

        [Parameter(Mandatory = $true)]
        [string]$Extension,

        [Parameter(Mandatory = $true)]
        [string]$ConfigFilePath,

        [Parameter(Mandatory = $true)]
        [string]$StandardVersion,

        [Parameter(Mandatory = $true)]
        [string]$PackageVersion,

        [string]$ApplicationName = "Demo application",

        [string]$DataStoreDatabaseName = "edfi_datamanagementservice",

        [long]$DataStoreId,

        [switch]$DumpAllUserSchemas,

        [string]$PostgresPassword = $env:POSTGRES_PASSWORD ?? "abcdefgh1!",

        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql",

        [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!"
    )

    if ($PSBoundParameters.ContainsKey('DataStoreId') -and -not $PSBoundParameters.ContainsKey('DataStoreDatabaseName')) {
        throw "-DataStoreId requires -DataStoreDatabaseName so the dump targets the same database the bound applications write to."
    }

    Add-CmsClient -CmsUrl $CmsUrl
    $cmsToken = Get-CmsToken -CmsUrl $CmsUrl

    # Resolve the data store to bind applications to. The CMS-registered data store
    # connection string controls where bulk-load writes land but is protected in API
    # responses, so a caller that targets a specific database must hand the data store id
    # forward from registration; callers that bind neither parameter keep the original
    # first-data-store behavior.
    $dataStores = @(Get-DataStore -CmsUrl $CmsUrl -AccessToken $cmsToken)
    $targetDataStoreId = Resolve-DataStoreIdForTemplate `
        -DataStores $dataStores `
        -RequestedDataStoreId ($PSBoundParameters.ContainsKey('DataStoreId') ? $DataStoreId : $null) `
        -DatabaseNameBound ($PSBoundParameters.ContainsKey('DataStoreDatabaseName'))
    if ($null -eq $targetDataStoreId) {
        $postgresCredential = ConvertTo-PostgresCredential -UserName "postgres" -Secret $PostgresPassword
        $targetDataStoreId = Add-DataStore -CmsUrl $CmsUrl -AccessToken $cmsToken -PostgresCredential $postgresCredential -PostgresDbName $DataStoreDatabaseName
    }

    # Create Bootstrap application and assign to the data store
    $bootstrapApp = Get-KeySecret -CmsUrl $CmsUrl -CmsToken $CmsToken -ClaimSetName 'BootstrapDescriptorsandEdOrgs' -ApplicationName "$ApplicationName Bootstrap" -DataStoreIds @($targetDataStoreId)

    $dmsToken = Get-DmsToken -DmsUrl $DmsUrl -Key $bootstrapApp.Key -Secret $bootstrapApp.Secret

    Invoke-SchoolYearLoader -DmsUrl $DmsUrl -DmsToken $dmsToken

    $bulkLoadClientPaths = Initialize-BulkLoad
    $bulkLoadTuning = Get-TemplateBulkLoadTuning -DatabaseEngine $DatabaseEngine

    Invoke-BulkLoad -BaseUrl $DmsUrl `
        -Key $bootstrapApp.Key `
        -Secret $bootstrapApp.Secret `
        -SampleDataDirectory $MinimalSampleDataDirectory `
        -Extension $Extension `
        -BulkLoadClientPaths $bulkLoadClientPaths `
        -ForceReloadData `
        -ForceReloadMetadata `
        @bulkLoadTuning

    if ($TemplateType -eq [TemplateType]::Populated) {

        if ([string]::IsNullOrWhiteSpace($PopulatedSampleDataDirectory)) {
            throw "PopulatedSampleDataDirectory must be specified when TemplateType is 'Populated'."
        }

        # Keep the populated template comparable to DS 5.2: drop the bulky DS 6.1 educator-preparation
        # data set (ProfessionalDevelopment/Candidate/PerformanceEvaluation/etc.) while keeping the
        # AssessmentMetadata-EdPrep assessments that core StudentAssessment sample data references.
        # No-op for sample data with no educator-prep files (DS 5.2). See Get-EducatorPreparationSampleFileName.
        #
        # Decide whether educator-prep filtering applies from the excluded-file set directly (and BEFORE
        # filtering), using the unit-tested Get-EducatorPreparationSampleFileName helper. Do NOT derive this
        # by comparing the filtered directory to the $PopulatedSampleDataDirectory parameter: PowerShell
        # variable names are case-insensitive, so $populatedSampleDataDirectory and $PopulatedSampleDataDirectory
        # are the SAME variable and would always compare equal (the filtered result is assigned back into the
        # parameter), which would silently disable tolerance for the DS 6.1 load.
        $educatorPrepFiltered = @(Get-EducatorPreparationSampleFileName -SourceDirectory $PopulatedSampleDataDirectory).Count -gt 0
        $populatedSampleDataDirectory = New-EducatorPreparationFilteredSampleDirectory -SourceDirectory $PopulatedSampleDataDirectory

        # The sandbox authorizes most resources by relationship to its claimed education organizations
        # (RelationshipsWithEdOrgsOnly), so scope it to every edorg the loaded (filtered) sample data
        # defines rather than the default district pair.
        $sandboxEducationOrganizationIds = @(
            Get-EducationOrganizationIdsFromSampleData -SampleDataDirectory $populatedSampleDataDirectory
        )

        $sandboxParams = @{
            CmsUrl          = $CmsUrl
            CmsToken        = $CmsToken
            ClaimSetName    = 'EdFiSandbox'
            ApplicationName = "$ApplicationName Sandbox"
            DataStoreIds    = @($targetDataStoreId)
        }
        if ($sandboxEducationOrganizationIds.Count -gt 0) {
            Write-Host "Scoping sandbox application to $($sandboxEducationOrganizationIds.Count) education organizations from the populated sample data."
            $sandboxParams.EducationOrganizationIds = $sandboxEducationOrganizationIds
        }

        # Create Sandbox application and assign to the data store
        $sandboxApp = Get-KeySecret @sandboxParams

        $dmsToken = Get-DmsToken -DmsUrl $DmsUrl -Key $sandboxApp.Key -Secret $sandboxApp.Secret

        # Only the filtered DS 6.1 load (educator-prep excluded; $educatorPrepFiltered computed above) can
        # leave a few kept records pointing at excluded educator-prep entities (unresolved-reference 409s),
        # so tolerate those ONLY then. DS 5.2 (and any version with nothing filtered) stays strict, so its
        # release validation is not weakened: any non-zero BulkLoad exit fails the build.
        Invoke-BulkLoad -BaseUrl $DmsUrl `
            -Key $sandboxApp.Key `
            -Secret $sandboxApp.Secret `
            -SampleDataDirectory $populatedSampleDataDirectory `
            -Extension $Extension `
            -BulkLoadClientPaths $bulkLoadClientPaths `
            -ForceReloadData `
            -ForceReloadMetadata `
            -AllowUnresolvedReferences:$educatorPrepFiltered `
            @bulkLoadTuning
    }

    Build-TemplateNuGetPackage -ConfigFilePath $ConfigFilePath -StandardVersion $StandardVersion -PackageVersion $PackageVersion -DatabaseName $DataStoreDatabaseName -DumpAllUserSchemas:$DumpAllUserSchemas -DatabaseEngine $DatabaseEngine -MssqlPassword $MssqlPassword
}

Export-ModuleMember -Function Build-Template, Get-UserSchemaNames, Restore-TemplatePackage
