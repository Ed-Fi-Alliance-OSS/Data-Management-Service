# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Validates the correlated inputs consumed by the reusable database-template workflows.

.DESCRIPTION
    A template artifact has one Data Standard identity expressed in several places: the workflow
    input, the package id suffix, the selected environment file, the Configuration Service data
    standard value, and the core SCHEMA_PACKAGES entry. This script fails before the external Data
    Standard checkout or any Docker work unless all of those representations agree exactly.

    Both reusable workflows call this script so their validation cannot drift independently.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Minimal", "Populated")]
    [string]$WorkflowKind,

    [Parameter(Mandatory)]
    [string]$StandardVersion,

    [Parameter(Mandatory)]
    [string]$PackageName,

    [Parameter(Mandatory)]
    [string]$EnvironmentFile,

    [Parameter(Mandatory)]
    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine,

    [bool]$PublishPackage = $true,

    [bool]$VerifyRestore = $false,

    [bool]$RequirePopulatedData = $false,

    # Test seam for exercising malformed and mismatched env files without changing the tracked
    # workflow fixtures. Normal workflow calls use the repository docker-compose directory.
    [string]$DockerComposeRoot = (Join-Path $PSScriptRoot "../docker-compose")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$environmentContracts = [ordered]@{
    ".env.template"      = @{ StandardVersion = "5.2.0"; IsSmoke = $false }
    ".env.template.ds61" = @{ StandardVersion = "6.1.0"; IsSmoke = $false }
    ".env.smoke"         = @{ StandardVersion = "5.2.0"; IsSmoke = $true }
    ".env.smoke.ds61"    = @{ StandardVersion = "6.1.0"; IsSmoke = $true }
}

$dockerComposeRootFullPath = [System.IO.Path]::GetFullPath($DockerComposeRoot)
$environmentFileFullPath = if ([System.IO.Path]::IsPathRooted($EnvironmentFile)) {
    [System.IO.Path]::GetFullPath($EnvironmentFile)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $dockerComposeRootFullPath $EnvironmentFile))
}

$environmentContract = $null
foreach ($entry in $environmentContracts.GetEnumerator()) {
    $allowedPath = [System.IO.Path]::GetFullPath((Join-Path $dockerComposeRootFullPath $entry.Key))
    if ([string]::Equals($environmentFileFullPath, $allowedPath, [System.StringComparison]::Ordinal)) {
        $environmentContract = $entry.Value
        break
    }
}

if ($null -eq $environmentContract) {
    throw "environment_file must resolve to one of: $($environmentContracts.Keys -join ', '); got '$EnvironmentFile'."
}

if (-not (Test-Path -LiteralPath $environmentFileFullPath -PathType Leaf)) {
    throw "environment_file was not found: $environmentFileFullPath"
}

if ($StandardVersion -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$') {
    throw "standard_version must be an exact three-part numeric version; got '$StandardVersion'."
}
$standardMajor = $Matches["major"]
$standardMinor = $Matches["minor"]
$standardSeries = "$standardMajor.$standardMinor"
$standardPackageToken = "$standardMajor$standardMinor"

if ($StandardVersion -ne [string]$environmentContract.StandardVersion) {
    throw "environment_file '$EnvironmentFile' selects Data Standard $($environmentContract.StandardVersion), not requested standard_version '$StandardVersion'."
}

Import-Module (Join-Path $PSScriptRoot "../docker-compose/env-utility.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "../schema-package-utility.psm1") -Force

$environmentValues = ReadValuesFromEnvFile $environmentFileFullPath
$schemaPackages = @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $environmentFileFullPath)

foreach ($schemaPackage in $schemaPackages) {
    if ([string]::IsNullOrWhiteSpace([string]$schemaPackage.name) -or
        [string]::IsNullOrWhiteSpace([string]$schemaPackage.version)) {
        throw "SCHEMA_PACKAGES in '$EnvironmentFile' contains an entry without both name and version."
    }
}

$corePackagePattern = '^EdFi\.DataStandard(?<token>\d+)\.ApiSchema$'
$corePackages = @($schemaPackages | Where-Object { ([string]$_.name) -match $corePackagePattern })
if ($corePackages.Count -ne 1) {
    throw "SCHEMA_PACKAGES in '$EnvironmentFile' must contain exactly one Data Standard core package; found $($corePackages.Count)."
}

$corePackageMatch = [regex]::Match([string]$corePackages[0].name, $corePackagePattern)
if ($corePackageMatch.Groups["token"].Value -ne $standardPackageToken) {
    throw "SCHEMA_PACKAGES core '$($corePackages[0].name)' does not match requested standard_version '$StandardVersion'."
}

# Reject a mixed Data Standard package set, not just a mismatched core. Built-in extensions use
# the same EdFi.DataStandard<NN> prefix; allowing a 5.2 extension beside a 6.1 core would make the
# artifact internally inconsistent even though the core-only check above passed.
$dataStandardPackagePattern = '^EdFi\.DataStandard(?<token>\d+)(?:\..+)?\.ApiSchema$'
foreach ($schemaPackage in $schemaPackages) {
    $packageMatch = [regex]::Match([string]$schemaPackage.name, $dataStandardPackagePattern)
    if ($packageMatch.Success -and $packageMatch.Groups["token"].Value -ne $standardPackageToken) {
        throw "SCHEMA_PACKAGES package '$($schemaPackage.name)' does not match requested standard_version '$StandardVersion'."
    }
}

$configuredDataStandardSeries = [string]$environmentValues["DMS_CONFIG_DATA_STANDARD_VERSION"]
if ([string]::IsNullOrWhiteSpace($configuredDataStandardSeries)) {
    $configuredDataStandardSeries = "5.2"
}
if ($configuredDataStandardSeries -ne $standardSeries) {
    throw "DMS_CONFIG_DATA_STANDARD_VERSION '$configuredDataStandardSeries' in '$EnvironmentFile' does not match requested standard_version '$StandardVersion'."
}

$isSmoke = [bool]$environmentContract.IsSmoke
if ($WorkflowKind -eq "Minimal" -and $isSmoke) {
    throw "The Minimal template workflow cannot use smoke environment_file '$EnvironmentFile'."
}
if ($isSmoke -and $PublishPackage) {
    throw "environment_file '$EnvironmentFile' is smoke-only and must not be published; set publish_package to false."
}
if ($RequirePopulatedData -and -not $VerifyRestore) {
    throw "require_populated_data requires verify_restore."
}

# The tracked env files (Minimal/Populated templates and smoke) are shared across engines and
# therefore always carry the PostgreSQL restore identity, whether or not the artifact is smoke.
# The MSSQL engine overlay rewrites only its engine token at runtime; the requested output
# artifact kind/engine is validated independently below.
$environmentTemplateKind = if ($isSmoke) { "Smoke" } else { "Populated" }
$expectedEnvironmentTemplatePackage = "EdFi.Api.$environmentTemplateKind.Template.PostgreSql.$StandardVersion"
$configuredEnvironmentTemplatePackage = [string]$environmentValues["DATABASE_TEMPLATE_PACKAGE"]
if ($configuredEnvironmentTemplatePackage -ne $expectedEnvironmentTemplatePackage) {
    throw "DATABASE_TEMPLATE_PACKAGE '$configuredEnvironmentTemplatePackage' in '$EnvironmentFile' must be '$expectedEnvironmentTemplatePackage'."
}

$packageTemplateKind = if ($isSmoke) { "Smoke" } else { $WorkflowKind }
$packageEngineToken = if ($DatabaseEngine -eq "mssql") { "MsSql" } else { "PostgreSql" }
$expectedPackageName = "EdFi.Api.$packageTemplateKind.Template.$packageEngineToken.$StandardVersion"
if ($PackageName -ne $expectedPackageName) {
    throw "The selected workflow inputs require package_name '$expectedPackageName'; got '$PackageName'."
}

Write-Output "Template workflow inputs are correlated: $WorkflowKind / $DatabaseEngine / DS $StandardVersion / $EnvironmentFile / $PackageName"
