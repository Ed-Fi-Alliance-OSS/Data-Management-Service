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
    The version of the Ed-Fi Bulk Load client to use.

.PARAMETER BaseDirectory
    The base directory where temporary folders will be created.

.OUTPUTS
    Hashtable containing paths to the bulk loader executable, XSD directory, and working directory.
#>
function Initialize-BulkLoad {
    param(
        $BulkLoadVersion = '7.3'
    )

    $bulkLoadClientExe = (Join-Path -Path (Get-BulkLoadClient $BulkLoadVersion).Trim() -ChildPath "tools/net*/any/EdFi.BulkLoadClient.Console.dll")
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

        [long[]]$DmsInstanceIds = @()
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
    $params.DmsInstanceIds = $DmsInstanceIds
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
        [hashtable]$BulkLoadClientPaths,

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

    & dotnet $BulkLoadClientPaths.bulkLoadClientExe @options
    if ($LASTEXITCODE -ne 0) {
        Write-Error "BulkLoad failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
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

    $backupPath = Join-Path $BackupDirectory $BackupFileName

    $options = @("exec", $ContainerName, "pg_dump", "-U", "postgres", $DatabaseName)

    foreach ($schema in $DatabaseSchemas) {
        $options += "-n"
        $options += $schema
    }

    & docker @options | Out-File -FilePath $backupPath -Encoding utf8

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
    Builds a NuGet package containing a PostgreSQL database backup.

.PARAMETER ConfigFilePath
    The path to the PowerShell data file (.psd1) containing configuration settings for the package.
    Defaults to "./MinimalTemplateSettings.psd1".

.PARAMETER StandardVersion
    The standard version string to substitute for "{StandardVersion}" placeholders in the configuration.

.PARAMETER PackageVersion
    The version to assign to the generated NuGet package.

.EXAMPLE
    Build-TemplateNuGetPackage -ConfigFilePath "./MinimalTemplateSettings.psd1" -StandardVersion "5.3.0" -PackageVersion "1.0.0"
#>
function Build-TemplateNuGetPackage {
    param (
        [Parameter(Mandatory = $true)]
        [string]$ConfigFilePath,

        [Parameter(Mandatory = $true)]
        [string]$StandardVersion,

        [Parameter(Mandatory = $true)]
        [string]$PackageVersion
    )

    $config = Import-PowerShellDataFile -Path $ConfigFilePath

    foreach ($key in @($config.Keys)) {
        if ($null -ne $key -and $config[$key] -is [string]) {
            $config[$key] = $config[$key].Replace("{StandardVersion}", $StandardVersion)
        }
    }

    Invoke-DatabaseDump -DatabaseSchemas "dms" -BackupDirectory './' -BackupFileName $config.DatabaseBackupName

    New-DatabaseTemplateCsproj -Config $config -BackupDirectory './'

    Build-NuGetPackage -PackageVersion $PackageVersion -Config $config -BackupDirectory './'
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

.PARAMETER PostgresPassword
    The PostgreSQL password for database connection. Defaults to environment variable POSTGRES_PASSWORD or "abcdefgh1!".

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

        [string]$PostgresPassword = $env:POSTGRES_PASSWORD ?? "abcdefgh1!"
    )

    Add-CmsClient -CmsUrl $CmsUrl
    $cmsToken = Get-CmsToken -CmsUrl $CmsUrl

    # Get or create DMS instance
    $dmsInstances = Get-DmsInstances -CmsUrl $CmsUrl -AccessToken $cmsToken
    if ($dmsInstances.Count -eq 0) {
        $dmsInstanceId = Add-DmsInstance -CmsUrl $CmsUrl -AccessToken $cmsToken -PostgresPassword $PostgresPassword
    } else {
        $dmsInstanceId = $dmsInstances[0].id
    }

    # Create Bootstrap application and assign to DMS instance
    $bootstrapApp = Get-KeySecret -CmsUrl $CmsUrl -CmsToken $CmsToken -ClaimSetName 'BootstrapDescriptorsandEdOrgs' -ApplicationName "$ApplicationName Bootstrap" -DmsInstanceIds @($dmsInstanceId)

    $dmsToken = Get-DmsToken -DmsUrl $DmsUrl -Key $bootstrapApp.Key -Secret $bootstrapApp.Secret

    Invoke-SchoolYearLoader -DmsUrl $DmsUrl -DmsToken $dmsToken

    $bulkLoadClientPaths = Initialize-BulkLoad

    Invoke-BulkLoad -BaseUrl $DmsUrl `
        -Key $bootstrapApp.Key `
        -Secret $bootstrapApp.Secret `
        -SampleDataDirectory $MinimalSampleDataDirectory `
        -Extension $Extension `
        -BulkLoadClientPaths $bulkLoadClientPaths `
        -ForceReloadData `
        -ForceReloadMetadata

    if ($TemplateType -eq [TemplateType]::Populated) {

        if ([string]::IsNullOrWhiteSpace($PopulatedSampleDataDirectory)) {
            throw "PopulatedSampleDataDirectory must be specified when TemplateType is 'Populated'."
        }

        # Create Sandbox application and assign to DMS instance
        $sandboxApp = Get-KeySecret -CmsUrl $CmsUrl -CmsToken $CmsToken -ClaimSetName 'EdFiSandbox' -ApplicationName "$ApplicationName Sandbox" -DmsInstanceIds @($dmsInstanceId)

        $dmsToken = Get-DmsToken -DmsUrl $DmsUrl -Key $sandboxApp.Key -Secret $sandboxApp.Secret

        Invoke-BulkLoad -BaseUrl $DmsUrl `
            -Key $sandboxApp.Key `
            -Secret $sandboxApp.Secret `
            -SampleDataDirectory $PopulatedSampleDataDirectory `
            -Extension $Extension `
            -BulkLoadClientPaths $bulkLoadClientPaths `
            -ForceReloadData `
            -ForceReloadMetadata
    }

    Build-TemplateNuGetPackage -ConfigFilePath $ConfigFilePath -StandardVersion $StandardVersion -PackageVersion $PackageVersion
}

Export-ModuleMember -Function Build-Template
