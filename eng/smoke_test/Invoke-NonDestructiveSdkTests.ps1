# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

param(
  [string]
  $Key = "sampleKey",

  [string]
  $Secret = "sampleSecret",

  # 8080 is the default k8s port
  # 5198 is the default when running F5
  [string]
  $BaseUrl = "http://localhost:5198",

  # Optional SDK path - if not provided, will download SDK
  [string]
  $SdkPath,

  [ValidateSet("EdFi.DmsApi.TestSdk", "EdFi.DmsApi.Sdk", "EdFi.OdsApi.Sdk")]
  [string]
  $SdkNamespace = "EdFi.DmsApi.TestSdk"
)

$ErrorActionPreference = "Stop"

Import-Module ../Package-Management.psm1 -Force
Import-Module ./modules/SmokeTest.psm1

# Use provided SDK path or download it
if ($SdkPath) {
  Write-Host "Using provided SDK path: $SdkPath"
  $sdkDllPath = $SdkPath
} else {
  Write-Host "No SDK path provided, downloading SDK..."
  $sdkDllPath = Get-ApiSdkDll
}

$path = Get-SmokeTestTool -PackageVersion '7.3.10008' -PreRelease

$parameters = @{
  BaseUrl = $BaseUrl
  Key = $Key
  Secret = $Secret
  ToolPath = $path
  TestSet = "NonDestructiveSdk"
  SdkPath = $sdkDllPath
  SdkNamespace = $SdkNamespace
}

Invoke-SmokeTestUtility @parameters
