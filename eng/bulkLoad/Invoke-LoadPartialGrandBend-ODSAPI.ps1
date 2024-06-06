# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Runs part of the bulk upload of the Grand Bend dataset, aka "populated
# template" - restricted to the data needed to run the performance testing kit.
# This enables a faster setup, at the expense of having less data in the system.

# Tuned for use with the ODS/API in shared instance mode. Before running this
# script, make sure that the Docker containers are running and that the
# bootstrap key/secret have been setup. Both of these steps are handled by
# running `./reset.ps1`.

#Requires -Version 7

param(
  [string]
  $Key = "sampleKey",

  [string]
  $Secret = "sampleSecret",

  [string]
  $BaseUrl = "http://localhost"
)

$ErrorActionPreference = "Stop"

Import-Module ./modules/Package-Management.psm1 -Force
Import-Module ./modules/Get-XSD.psm1 -Force
Import-Module ./modules/BulkLoad.psm1 -Force
$sampleDataVersion = "3.3.1-b"

$paths = Initialize-ToolsAndDirectories
$paths.SampleDataDirectory = Import-SampleData -Template "GrandBend" -Version $sampleDataVersion

$parameters = @{
  BaseUrl = $BaseUrl
  Key = $Key
  Secret = $Secret
  Paths = $paths
}

Write-Descriptors @parameters
Write-PartialGrandBend  @parameters
