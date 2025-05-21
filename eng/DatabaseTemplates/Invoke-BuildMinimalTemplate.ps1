# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Automates MinimalTemplate build by bulk loading and packaging of Ed-Fi sample data.

.DESCRIPTION
    This script performs the following tasks:
    - Initializes a bulk load client
    - Loads sample data into the Ed-Fi Data Management Service
    - Dumps the resulting database
    - Packages the database backup into a NuGet package
#>

[CmdletBinding()]
param (
    [ValidateNotNullOrEmpty()]
    [string]$ConfigUrl = "http://localhost:8081",

    [ValidateNotNullOrEmpty()]
    [string]$DmsUrl = "http://localhost:8080",

    [ValidateNotNullOrEmpty()]
    [string]$NamespacePrefixes = "uri://ed-fi.org",

    [ValidateNotNullOrEmpty()]
    [string]$ClaimSetName = "EdfiSandbox",

    [ValidateNotNullOrEmpty()]
    [string]$Extension,

    [Parameter(Mandatory = $true)]
    [string]$SampleDataDirectory,

    [Parameter(Mandatory = $true)]
    [string]$PackageVersion
)

Import-Module ../Package-Management.psm1 -Force
Import-Module ./DmsManagement.psm1 -Force

$config = Import-PowerShellDataFile -Path "./MinimalTemplateSettings.psd1"

$BackupDirectory = './MinimalTemplate'

<#
.SYNOPSIS
    Initializes the Ed-Fi Bulk Load client and working directories.

.DESCRIPTION
    Sets up paths for the bulk loader, XSDs, and temporary directories required for loading sample data.

.PARAMETER BulkLoadVersion
    The version of the Ed-Fi Bulk Load client to use.

.PARAMETER BaseDirectory
    The base directory where temporary folders will be created.

.OUTPUTS
    Hashtable containing paths to the bulk loader executable, XSD directory, and working directory.
#>
function Initialize-BulkLoad {
    param(
        $BulkLoadVersion = "7.2"
    )

    $bulkLoader = (Join-Path -Path (Get-BulkLoadClient $BulkLoadVersion).Trim() -ChildPath "tools/net*/any/EdFi.BulkLoadClient.Console.dll")
    $bulkLoader = Resolve-Path $bulkLoader

    $xsdDirectory = "$($PSScriptRoot)/tmp/XSD"
    New-Item -Path $xsdDirectory -Type Directory -Force | Out-Null

    $workingDirectory = "$($PSScriptRoot)/tmp/.working"
    New-Item -Path $workingDirectory -Type Directory -Force | Out-Null

    Write-Host
    Write-Host "Initialized Bulk Load and working directories" -ForegroundColor Green -NoNewline
    Write-Host

    return @{
        WorkingDirectory = (Resolve-Path $workingDirectory)
        XsdDirectory     = (Resolve-Path $xsdDirectory)
        BulkLoaderExe    = $bulkLoader
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
    It returns the key and secret required for authenticated communication with the DMS API.

.PARAMETER ConfigUrl
    The base URL of the DMS configuration service.

.OUTPUTS
    A hashtable containing the Key and Secret for the initialized DMS application.

.EXAMPLE
    $secrets = Initialize-DataManagementSystem -ConfigUrl "http://localhost:8081"

.NOTES
    This function depends on the helper functions: Add-Client, Get-AccessToken, Add-Vendor, and Initialize-Application.
    These should be defined in the DmsManagement.psm1 module.
#>
function Initialize-DataManagementSystem() {
    param (
        [Parameter(Mandatory = $true)]
        [string]$ConfigUrl
    )

    $configParams = @{
        ConfigUrl = $ConfigUrl
    }

    # Add Client and get access token
    Add-Client @configParams
    $accessToken = Get-AccessToken @configParams

    # Add Vendor
    $configParams.AccessToken = $accessToken
    $vendorId = Add-Vendor @configParams

    # Add an Application and get Key and Secret
    $configParams.VendorId = $vendorId
    $configParams.ClaimSetName = 'BootstrapDescriptorsandEdOrgs'
    $secrets = Initialize-Application @configParams

    Write-Host
    Write-Host "Initialized Data Management System Application" -ForegroundColor Green -NoNewline
    Write-Host

    return $secrets
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
        Hashtable of important paths (WorkingDirectory, XsdDirectory, BulkLoaderExe).

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

    .EXAMPLE
        Invoke-BulkLoad -BaseUrl "http://api.local" -Key "abc" -Secret "123" -SampleDataDirectory "C:\Data" -Paths $paths -ForceReloadMetadata -SkipXmlValidation
#>
function Invoke-BulkLoad {
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
        [hashtable]$Paths,

        [string]$Extension = $null,

        [switch]$ForceReloadMetadata,

        [switch]$SkipXmlValidation,

        [switch]$ForceReloadData,

        [int]$MaxConcurrentConnections = 100,

        [int]$RetryCount = 1,

        [int]$MaxSimultaneousRequests = 500,

        [int]$MaxBufferedTasks = 50
    )

    if ($ForceReloadData) {
        Get-ChildItem -Path $Paths.WorkingDirectory -Filter *.hash -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }

    $options = @(
        "-b", $BaseUrl,
        "-d", $SampleDataDirectory,
        "-w", $Paths.WorkingDirectory,
        "-k", $Key,
        "-s", $Secret,
        "-c", $MaxConcurrentConnections.ToString(),
        "-r", $RetryCount.ToString(),
        "-l", $MaxSimultaneousRequests.ToString(),
        "-t", $MaxBufferedTasks.ToString(),
        "-x", $Paths.XsdDirectory,
        "-o", "$BaseUrl/oauth/token"
    )

    if ($ForceReloadMetadata) { $options += "-f" }
    if ($SkipXmlValidation)   { $options += "-n" }
    if (-not [string]::IsNullOrEmpty($Extension)) {
        $options += "-e"
        $options += $Extension
    }

    $previousColor = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = "Cyan"
    Write-Output "Executing: dotnet $($Paths.BulkLoaderExe) $($options -join ' ')"
    $host.UI.RawUI.ForegroundColor = $previousColor

    & dotnet $Paths.BulkLoaderExe @options

    Write-Host
    Write-Host "BulkLoad executed successfully" -ForegroundColor Green -NoNewline
    Write-Host
}

<#
.SYNOPSIS
    Dumps the specified PostgreSQL database to a file.

.DESCRIPTION
    Uses Docker to execute `pg_dump` inside a running PostgreSQL container, targeting only the specified schemas.

.PARAMETER ContainerName
    Name of the Docker container running PostgreSQL.

.PARAMETER DatabaseName
    Name of the database to dump.

.PARAMETER DatabaseSchemas
    Array of schemas to include in the dump.

.PARAMETER BackupDirectory
    Directory where the dump file will be saved.

.PARAMETER BackupFileName
    Filename to use for the backup.
#>
function Invoke-DatabaseDump {
    param (
        [string]$ContainerName = "dms-postgresql",
        [string]$DatabaseName = "edfi_datamanagementservice",
        [string[]]$DatabaseSchemas,
        [string]$BackupDirectory,
        [string]$BackupFileName
    )

    $BackupPath = Join-Path $BackupDirectory $BackupFileName

    $options = @("exec", $ContainerName, "pg_dump", "-U", "postgres", $DatabaseName)

    foreach ($schema in $DatabaseSchemas) {
        $options += "-n"
        $options += $schema
    }

    & docker @options | Out-File -FilePath $BackupPath -Encoding utf8

    Write-Host
    Write-Host "Backup Created: " -ForegroundColor Green -NoNewline
    Write-Host (Resolve-Path $BackupPath)
    Write-Host
}

<#
.SYNOPSIS
    Loads school year types into the Ed-Fi Data Management Service (DMS).

.DESCRIPTION
    Iterates through a range of school years and posts each as a `schoolYearType` entity
    to the DMS API. Marks one year as the current school year. This function helps seed
    the system with school year data typically required before loading other sample data.

.PARAMETER StartYear
    The first school year to load. Defaults to 1991.

.PARAMETER EndYear
    The last school year to load. Defaults to 2037.

.PARAMETER CurrentSchoolYear
    The school year to mark as the current one. Defaults to 2025.

.PARAMETER DMSUrl
    The base URL of the Data Management Service API.

.PARAMETER BearerToken
    The authentication token used to authorize requests to the DMS API.

.EXAMPLE
    Invoke-SchoolYearLoader -DMSUrl "http://localhost:8080" -BearerToken $token

.NOTES
    This function requires the helper function `Invoke-Api` to send HTTP requests.
#>
function Invoke-SchoolYearLoader {
    param (
        [int]$StartYear = 1991,
        [int]$EndYear = 2037,
        [int]$CurrentSchoolYear = 2025,
        [string]$DMSUrl,
        [string]$BearerToken
    )

    for ($year = $StartYear; $year -le $EndYear; $year++) {
        $schoolYearType = @{
            schoolYear            = $year
            currentSchoolYear     = ($year -eq $CurrentSchoolYear)
            schoolYearDescription = "$($year - 1)-$year"
        }

        $invokeParams = @{
            Method      = 'Post'
            BaseUrl     = $DMSUrl
            RelativeUrl = 'data/ed-fi/schoolYearTypes'
            ContentType = 'application/json'
            Body        = ($schoolYearType | ConvertTo-Json -Depth 5)
            Headers     = @{ Authorization = "bearer $BearerToken" }
        }

        Invoke-Api @invokeParams | Out-Null
    }

    Write-Host
    Write-Host "School Years Loaded" -ForegroundColor Green -NoNewline
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
    param (
        [hashtable]$Config,
        [string]$BackupDirectory
    )

    $BackupDirectory = Resolve-Path $BackupDirectory
    $csprojPath = Join-Path $BackupDirectory $Config.PackageProjectName
    $backupPath = Join-Path $BackupDirectory $Config.DatabaseBackupName

    $params = @{
        CsprojPath               = $csprojPath
        Id                       = $Config.Id
        Title                    = $Config.Title
        Description              = $Config.Description
        Authors                  = $Config.Authors
        ProjectUrl               = $Config.ProjectUrl
        License                  = $Config.License
        ForceOverwrite           = $true
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

if (-not (Test-Path $BackupDirectory)) {
    New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
}

$paths = Initialize-BulkLoad

$application = Initialize-DataManagementSystem -ConfigUrl $ConfigUrl

$bearerToken = Get-BearerToken -DmsUrl $DmsUrl -Key $application.Key -Secret $application.Secret

Invoke-SchoolYearLoader -DMSUrl $DmsUrl -BearerToken $bearerToken

Invoke-BulkLoad -BaseUrl $DmsUrl `
    -Key $application.Key `
    -Secret $application.Secret `
    -SampleDataDirectory $SampleDataDirectory `
    -Extension $Extension `
    -Paths $paths `
    -ForceReloadMetadata $true

Invoke-DatabaseDump -DatabaseSchemas "dms" -BackupDirectory $BackupDirectory -BackupFileName $config.DatabaseBackupName

New-DatabaseTemplateCsproj -Config $config -BackupDirectory $BackupDirectory

Build-NuGetPackage -PackageVersion $PackageVersion -Config $config -BackupDirectory $BackupDirectory
