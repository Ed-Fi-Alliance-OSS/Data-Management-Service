# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
Verify the downloaded tool package and generate its SBOM in the current directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageFile,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $Sha256
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PackageFile)) {
    throw "Expected SecurityMetadataTools package was not found: $PackageFile"
}
if ((Get-FileHash -LiteralPath $PackageFile -Algorithm SHA256).Hash -ine $Sha256) {
    throw 'SecurityMetadataTools package differs from the build artifact.'
}
(Resolve-Path -LiteralPath $PackageFile).Path > buildfilelist.txt

dotnet tool install --global Microsoft.Sbom.DotNetTool
if ($LASTEXITCODE -ne 0) { throw 'SBOM tool installation failed.' }

sbom-tool generate `
    -b ./ `
    -bl ./buildfilelist.txt `
    -bc ./ `
    -pn 'EdFi.Api.SecurityMetadataTools' `
    -pv $Version `
    -nsb https://ed-fi.org `
    -m ./ `
    -ps 'Ed-Fi Alliance'
if ($LASTEXITCODE -ne 0) { throw 'SecurityMetadataTools SBOM generation failed.' }

Copy-Item _manifest/spdx_2.2/manifest.spdx.* .
