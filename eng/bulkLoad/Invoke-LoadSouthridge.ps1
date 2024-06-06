# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Runs the complete bulk upload of the Southridge dataset

#Requires -Version 7
$ErrorActionPreference = "Stop"

Import-Module ./modules/Package-Management.psm1 -Force
Import-Module ./modules/Get-XSD.psm1 -Force
Import-Module ./modules/BulkLoad.psm1 -Force

$baseUrl = "http://localhost:3000/local"
$adminKey = "meadowlark_admin_key_1"
$adminSecret = "meadowlark_admin_secret_1"

$newClient = New-MeadowlarkApiClient -BaseUrl $baseUrl -AdminKey $adminKey -AdminSecret $adminSecret

$paths = Initialize-ToolsAndDirectories
$paths.SampleDataDirectory = Import-SampleData -Template "Southridge"

$parameters = @{
  BaseUrl = $baseUrl
  Key = $newClient.key
  Secret = $newClient.secret
  Paths = $paths
}

Write-Southridge  @parameters
