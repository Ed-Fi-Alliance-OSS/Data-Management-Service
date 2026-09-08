# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Smoke-test entry script intentionally writes operator progress to the console.')]
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

  [ValidateSet("EdFi.Api.TestSdk", "EdFi.Api.Sdk", "EdFi.OdsApi.Sdk")]
  [string]
  $SdkNamespace = "EdFi.Api.TestSdk",

  # Optional explicit OAuth token URL; without it the console resolves the token
  # endpoint from the DMS discovery document
  [string]
  $OAuthUrl
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

# SmokeTest.Console reflects over the generated SDK to find each resource's verb
# methods and builds its API client from the SDK's configuration type. This pin
# carries the two behaviors the four-package smoke surface needs: it maps one
# resource per POST method, so Homograph's homonym resources (Contact, School,
# Staff, Student, StudentSchoolAssociation) that share a core OpenAPI tag and land
# on the same generated Api class no longer collide; and it falls back to the
# pre-host-builder SDK configuration for stock openapi-generator SDKs (the in-run
# DMS TestSdk and EdFi.OdsApi.Sdk 7.3.10132), which lack the newer
# {SdkNamespace}.Client.HostConfiguration type.
$path = Get-SmokeTestTool -PackageVersion '7.3.20185'

$parameters = @{
  BaseUrl = $BaseUrl
  Key = $Key
  Secret = $Secret
  ToolPath = $path
  TestSet = "NonDestructiveSdk"
  SdkPath = $sdkDllPath
  SdkNamespace = $SdkNamespace
}

if ($OAuthUrl) {
  $parameters.OAuthUrl = $OAuthUrl
}

Invoke-SmokeTestUtility @parameters
