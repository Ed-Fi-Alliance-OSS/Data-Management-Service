# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Runs part of the bulk upload of the Grand Bend dataset, aka "populated
# template" - restricted to the data needed to run the performance testing kit.
# This enables a faster setup, at the expense of having less data in the system.

#Requires -Version 7
$ErrorActionPreference = "Stop"

Import-Module ./modules/Package-Management.psm1 -Force
Import-Module ./modules/Get-XSD.psm1 -Force
Import-Module ./modules/BulkLoad.psm1 -Force

# 8080 is the default k8s port
$baseUrl = "http://localhost:8080"
$adminKey = "this will be ignored right now"
$adminSecret = "also will be ignored"
$sampleDataVersion = "5.0.0"

# For future use, once we have real OAuth.
# $newClient = New-ApiClient -BaseUrl $baseUrl -AdminKey $adminKey -AdminSecret $adminSecret
$newClient = @{
        key="$adminKey"
        secret="$adminSecret"
      }

$paths = Initialize-ToolsAndDirectories
$paths.SampleDataDirectory = Import-SampleData -Template "GrandBend" -Version $sampleDataVersion

$parameters = @{
  BaseUrl = $baseUrl
  Key = $newClient.key
  Secret = $newClient.secret
  Paths = $paths
}

Write-Descriptors @parameters
Write-PartialGrandBend  @parameters
