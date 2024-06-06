# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
$ErrorActionPreference = "Stop"


$bulkLoadVersion = "7.2"

## Keeping this function around for future use. It used an administrative key & secret to create a new
## set of client credentials. Someday, we'll need this for the Data Management Service too.

#<#
# .SYNOPSIS
#  Create a vendor client using the admin key and secret.
#>
# function New-ApiClient {
#   [CmdletBinding()]
#   param (
#     [string]
#     [Parameter(Mandatory=$True)]
#     $BaseUrl,

#     [string]
#     [Parameter(Mandatory=$True)]
#     $AdminKey,

#     [string]
#     [Parameter(Mandatory=$True)]
#     $AdminSecret
#   )
#   # Authenticate as admin
#   $tokenUrl = "$($BaseUrl)/oauth/token"
#   $body = @{
#     "grant_type"    = "client_credentials"
#     "client_id"     = $AdminKey
#     "client_secret" = $AdminSecret
#   } | ConvertTo-Json
#   $headers = @{
#     "content-type" = "application/json"
#   }

#   $response = Invoke-RestMethod -Method POST -Body $body -Headers $headers -Uri $tokenUrl
#   $accessToken = $response.access_token

#   # Create a client account
#   $body = @{
#     clientName = "Bulk Loader"
#     roles      = @(
#       "vendor"
#     )
#   } | ConvertTo-Json

#   $headers.Add("Authorization", "bearer $accessToken")

#   $clientUrl = "$($BaseUrl)/oauth/clients"
#   $response = Invoke-RestMethod -Method POST -Body $body -Headers $headers -Uri $clientUrl
#   $key = $response.client_id
#   $secret = $response.client_secret

#   return @{
#     key=$key
#     secret=$secret
#   }
# }

function Initialize-ToolsAndDirectories {

  $bulkLoader = (Join-Path -Path (Get-BulkLoadClient $bulkLoadVersion).Trim() -ChildPath "tools/net*/any/EdFi.BulkLoadClient.Console.dll")
  $bulkLoader = Resolve-Path $bulkLoader

  $xsdDirectory = "$($PSScriptRoot)/../.packages/XSD"
  New-Item -Path $xsdDirectory -Type Directory -Force | Out-Null
  $xsdDirectory = (Resolve-Path "$($PSScriptRoot)/../.packages/XSD")
  $null = Get-EdFiXsd -XsdDirectory $xsdDirectory

  return @{
    WorkingDirectory = (New-Item -Path "$($PsScriptRoot)/../.working" -Type Directory -Force)
    XsdDirectory = $xsdDirectory
    BulkLoaderExe =  $bulkLoader
  }
}

function Import-SampleData {
  [CmdletBinding()]
  param (
    [ValidateSet("Southridge","GrandBend","PartialGrandBend")]
    $Template = "Southridge",

    [string]
    $Version
  )

  if ($Template -eq "Southridge") {
    return (Get-SouthridgeSampleData)
  } else {
    return (Get-SampleData $Version).Trim()
  }
}

function Write-XmlFiles {
  [CmdletBinding()]
  param (
    [string]
    [Parameter(Mandatory=$True)]
    $BaseUrl,

    [string]
    [Parameter(Mandatory=$True)]
    $Key,

    [string]
    [Parameter(Mandatory=$True)]
    $Secret,

    [string]
    [Parameter(Mandatory=$True)]
    $SampleDataDirectory,

    [hashtable]
    [Parameter(Mandatory=$True)]
    $Paths
  )

  # Load descriptors
  $options = @(
    "-b", $BaseUrl,
    "-d", $SampleDataDirectory,
    "-w", $Paths.WorkingDirectory,
    "-k", $Key,
    "-s", $Secret,
    "-c", "100",
    "-r", "0",
    "-l", "500",
    "-t", "50",
    "-f",
    "-n",
    "-x", $Paths.XsdDirectory
  )

  Write-host -ForegroundColor Cyan $Paths.BulkLoaderExe $options
  &dotnet $Paths.BulkLoaderExe $options
}

function Write-Descriptors {
  [CmdletBinding()]
  param (
    [string]
    [Parameter(Mandatory=$True)]
    $BaseUrl,

    [string]
    [Parameter(Mandatory=$True)]
    $Key,

    [string]
    [Parameter(Mandatory=$True)]
    $Secret,

    [hashtable]
    [Parameter(Mandatory=$True)]
    $Paths
  )

  $parameters = @{
    BaseUrl = $BaseUrl
    Key = $Key
    Secret = $Secret
    SampleDataDirectory = Join-Path -Path $Paths.SampleDataDirectory -ChildPath "Bootstrap"
    Paths = $Paths
  }
  Write-XmlFiles @parameters
}

function Write-GrandBend {
  [CmdletBinding()]
  param (
    [string]
    [Parameter(Mandatory=$True)]
    $BaseUrl,

    [string]
    [Parameter(Mandatory=$True)]
    $Key,

    [string]
    [Parameter(Mandatory=$True)]
    $Secret,

    [hashtable]
    [Parameter(Mandatory=$True)]
    $Paths
  )

  $parameters = @{
    BaseUrl = $BaseUrl
    Key = $Key
    Secret = $Secret
    SampleDataDirectory = Join-Path -Path $Paths.SampleDataDirectory -ChildPath "Sample XML"
    Paths = $Paths
  }
  Write-XmlFiles @parameters
}

function Write-PartialGrandBend {
  [CmdletBinding()]
  param (
    [string]
    [Parameter(Mandatory=$True)]
    $BaseUrl,

    [string]
    [Parameter(Mandatory=$True)]
    $Key,

    [string]
    [Parameter(Mandatory=$True)]
    $Secret,

    [hashtable]
    [Parameter(Mandatory=$True)]
    $Paths
  )

  $fullDir = Join-Path -Path $Paths.SampleDataDirectory -ChildPath "Sample XML"
  $partialDir = Join-Path -Path $Paths.SampleDataDirectory -ChildPath "Partial"

  New-Item -ItemType Directory $partialDir -Force | Out-Null
  Copy-Item -Path $fullDir/Standards.xml -Destination $partialDir -Force | Out-Null
  Copy-Item -Path $fullDir/EducationOrganization.xml -Destination $partialDir -Force | Out-Null
  Copy-Item -Path $fullDir/EducationOrgCalendar.xml -Destination $partialDir -Force | Out-Null

  $parameters = @{
    BaseUrl = $BaseUrl
    Key = $Key
    Secret = $Secret
    SampleDataDirectory = $partialDir
    Paths = $Paths
  }
  Write-XmlFiles @parameters
}

function Write-Southridge {
  [CmdletBinding()]
  param (
    [string]
    [Parameter(Mandatory=$True)]
    $BaseUrl,

    [string]
    [Parameter(Mandatory=$True)]
    $Key,

    [string]
    [Parameter(Mandatory=$True)]
    $Secret,

    [hashtable]
    [Parameter(Mandatory=$True)]
    $Paths
  )

  $parameters = @{
    BaseUrl = $BaseUrl
    Key = $Key
    Secret = $Secret
    SampleDataDirectory = $Paths.SampleDataDirectory
    Paths = $Paths
  }
  Write-XmlFiles @parameters
}

Export-ModuleMember *
