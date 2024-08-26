# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
$ErrorActionPreference = "Stop"


$bulkLoadVersion = "7.2"

function Initialize-ToolsAndDirectories {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Overly aggressive rule.')]
    param()

    $bulkLoader = (Join-Path -Path (Get-BulkLoadClient $bulkLoadVersion).Trim() -ChildPath "tools/net*/any/EdFi.BulkLoadClient.Console.dll")
    $bulkLoader = Resolve-Path $bulkLoader

    $xsdDirectory = "$($PSScriptRoot)/../.packages/XSD"
    New-Item -Path $xsdDirectory -Type Directory -Force | Out-Null
    $xsdDirectory = (Resolve-Path "$($PSScriptRoot)/../.packages/XSD")
    $null = Get-EdFiXsd -XsdDirectory $xsdDirectory

    return @{
        WorkingDirectory = (New-Item -Path "$($PsScriptRoot)/../.working" -Type Directory -Force)
        XsdDirectory     = $xsdDirectory
        BulkLoaderExe    = $bulkLoader
    }
}

function Import-SampleData {
    [CmdletBinding()]
    param (
        [ValidateSet("Southridge", "GrandBend", "PartialGrandBend")]
        $Template = "Southridge",

        [string]
        $Version
    )

    if ($Template -eq "Southridge") {
        return (Get-SouthridgeSampleData)
    }
    else {
        $preRelease = $Version.Contains('-') # example: 5.1.0-dev.1

        return (Get-SampleData -PackageVersion $Version -PreRelease:$preRelease).Trim()
    }
}

function Write-XmlFiles {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Overly aggressive rule.')]

    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $BaseUrl,

        [string]
        [Parameter(Mandatory = $True)]
        $Key,

        [string]
        [Parameter(Mandatory = $True)]
        $Secret,

        [string]
        [Parameter(Mandatory = $True)]
        $SampleDataDirectory,

        [hashtable]
        [Parameter(Mandatory = $True)]
        $Paths
    )

    # Want to reload everything, therefore need to clear out the working directory
    Get-ChildItem -Path $Paths.WorkingDirectory -Filter *.hash | ForEach-Object { Remove-Item $_ }

    # Load descriptors
    $options = @(
        "-b", $BaseUrl,
        "-d", $SampleDataDirectory,
        "-w", $Paths.WorkingDirectory,
        "-k", $Key,
        "-s", $Secret,
        "-c", "100",                        # Maximum concurrent connections to api
        "-r", "1",                          # The number of times to retry submitting a resource
        "-l", "500",                        # Max number of simultaneous API requests
        "-t", "50",                         # Maximum concurrent tasks to be buffered
        "-f",                               # Force reload of metadata from metadata url
        "-n",                               # Do not validate the XML document against the XSD before processing
        "-x", $Paths.XsdDirectory
    )

    $previousForegroundColor = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = "Cyan"
    Write-Output $Paths.BulkLoaderExe $options
    $host.UI.RawUI.ForegroundColor = $previousForegroundColor

    &dotnet $Paths.BulkLoaderExe $options
}

function Write-Descriptors {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Overly aggressive rule.')]

    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $BaseUrl,

        [string]
        [Parameter(Mandatory = $True)]
        $Key,

        [string]
        [Parameter(Mandatory = $True)]
        $Secret,

        [hashtable]
        [Parameter(Mandatory = $True)]
        $Paths
    )

    $parameters = @{
        BaseUrl             = $BaseUrl
        Key                 = $Key
        Secret              = $Secret
        SampleDataDirectory = Join-Path -Path $Paths.SampleDataDirectory -ChildPath "Bootstrap"
        Paths               = $Paths
    }
    Write-XmlFiles @parameters
}

function Write-GrandBend {
    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $BaseUrl,

        [string]
        [Parameter(Mandatory = $True)]
        $Key,

        [string]
        [Parameter(Mandatory = $True)]
        $Secret,

        [hashtable]
        [Parameter(Mandatory = $True)]
        $Paths
    )

    $parameters = @{
        BaseUrl             = $BaseUrl
        Key                 = $Key
        Secret              = $Secret
        SampleDataDirectory = Join-Path -Path $Paths.SampleDataDirectory -ChildPath "Sample XML"
        Paths               = $Paths
    }
    Write-XmlFiles @parameters
}

function Write-PartialGrandBend {
    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $BaseUrl,

        [string]
        [Parameter(Mandatory = $True)]
        $Key,

        [string]
        [Parameter(Mandatory = $True)]
        $Secret,

        [hashtable]
        [Parameter(Mandatory = $True)]
        $Paths
    )

    $fullDir = Join-Path -Path $Paths.SampleDataDirectory -ChildPath "Sample XML"
    $partialDir = Join-Path -Path $Paths.SampleDataDirectory -ChildPath "Partial"

    New-Item -ItemType Directory $partialDir -Force | Out-Null
    Copy-Item -Path $fullDir/Standards.xml -Destination $partialDir -Force | Out-Null
    Copy-Item -Path $fullDir/EducationOrganization.xml -Destination $partialDir -Force | Out-Null
    Copy-Item -Path $fullDir/EducationOrgCalendar.xml -Destination $partialDir -Force | Out-Null

    $parameters = @{
        BaseUrl             = $BaseUrl
        Key                 = $Key
        Secret              = $Secret
        SampleDataDirectory = $partialDir
        Paths               = $Paths
    }
    Write-XmlFiles @parameters
}

function Write-Southridge {
    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $BaseUrl,

        [string]
        [Parameter(Mandatory = $True)]
        $Key,

        [string]
        [Parameter(Mandatory = $True)]
        $Secret,

        [hashtable]
        [Parameter(Mandatory = $True)]
        $Paths
    )

    $parameters = @{
        BaseUrl             = $BaseUrl
        Key                 = $Key
        Secret              = $Secret
        SampleDataDirectory = $Paths.SampleDataDirectory
        Paths               = $Paths
    }
    Write-XmlFiles @parameters
}

Export-ModuleMember *
