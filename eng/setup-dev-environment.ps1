# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
Script to set up the development environment for the repository.

.DESCRIPTION
This script restores .NET tools and installs Husky for managing Git hooks.
#>

# Move to the root of the Git repository
$repoRoot = git rev-parse --show-toplevel
Set-Location $repoRoot

Write-Host "Setting up the development environment..."

# Restore .NET tools
Write-Host "Restoring .NET tools..."
dotnet tool restore

# Install CSharpier
Write-Host "Installing CSharpier..."
dotnet tool install --local csharpier

# Install Husky
Write-Host "Installing Husky..."
dotnet husky install

Write-Host "Development environment setup complete."
