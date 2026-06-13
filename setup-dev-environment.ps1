# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
Script to set up the development environment for the repository.

.DESCRIPTION
This script restores .NET tools and PowerShell resources, then installs Husky for managing Git hooks.
#>

Write-Information "Setting up the development environment..." -InformationAction Continue

# Restore .NET tools
Write-Information "Restoring .NET tools..." -InformationAction Continue
dotnet tool restore

# Restore PowerShell resources
Write-Information "Restoring PowerShell resources..." -InformationAction Continue
$requiredPowerShellResourcesFile = Join-Path $PSScriptRoot "eng/RequiredResources.psd1"

if ($null -eq (Get-Command Install-PSResource -ErrorAction SilentlyContinue)) {
    Write-Information "Installing Microsoft.PowerShell.PSResourceGet..." -InformationAction Continue
    Install-Module -Name Microsoft.PowerShell.PSResourceGet -Scope CurrentUser -Force -AllowClobber
    Import-Module Microsoft.PowerShell.PSResourceGet -Force
}

Install-PSResource `
    -RequiredResourceFile $requiredPowerShellResourcesFile `
    -Scope CurrentUser `
    -TrustRepository `
    -AcceptLicense

# Install CSharpier
Write-Information "Installing CSharpier..." -InformationAction Continue
dotnet tool install --local csharpier

# Install Husky
Write-Information "Installing Husky..." -InformationAction Continue
dotnet husky install

Write-Information "Development environment setup complete." -InformationAction Continue
