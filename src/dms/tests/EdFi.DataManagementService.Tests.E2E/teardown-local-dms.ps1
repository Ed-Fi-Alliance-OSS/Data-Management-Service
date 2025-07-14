# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Tears down the Ed-Fi DMS local Docker environment created by start-local-dms.ps1
.DESCRIPTION
    This script reverses the process of start-local-dms.ps1 by:
    - Stopping all containers in the dms-local stack
    - Removing all associated volumes
    - Removing locally-built images (dms-local-dms and dms-local-config)
    - Removing the dms network

    The script dynamically reads docker-compose YAML files to determine what to tear down.
    It is the companion to setup-local-dms.ps1.
#>

[CmdletBinding()]
param()

Write-Host @"
Ed-Fi DMS Local Environment Teardown
====================================
"@ -ForegroundColor Cyan

# Store current location and navigate to docker-compose directory
$originalLocation = Get-Location
$dockerComposeDir = Join-Path $PSScriptRoot "../../../../eng/docker-compose"

try {
    Set-Location $dockerComposeDir

    # Define expected YAML files
    $expectedYamlFiles = @(
        "postgresql.yml",
        "keycloak.yml",
        "local-dms.yml",
        "local-config.yml",
        "kafka-opensearch.yml",
        "kafka-elasticsearch.yml",
        "kafka-opensearch-ui.yml",
        "kafka-elasticsearch-ui.yml",
        "swagger-ui.yml",
        "published-dms.yml",
        "published-config.yml"
    )

    # Check if Docker is running
    try {
        docker version | Out-Null
    }
    catch {
        Write-Error "Docker is not running or not installed. Please start Docker and try again."
        exit 1
    }

    # Set SCHEMA_PACKAGES to empty to prevent warning
    [System.Environment]::SetEnvironmentVariable("SCHEMA_PACKAGES", "", "Process")

    # Check for unexpected YAML files
    Write-Host "`nChecking for unexpected YAML files..." -ForegroundColor Yellow
    $allYamlFiles = Get-ChildItem -Path . -Filter "*.yml" | Select-Object -ExpandProperty Name
    $unexpectedFiles = $allYamlFiles | Where-Object { $_ -notin $expectedYamlFiles }

    if ($unexpectedFiles.Count -gt 0) {
        foreach ($file in $unexpectedFiles) {
            Write-Warning "Found unexpected YAML file: $file, taking no action"
        }
        Write-Host ""
    }

    # Find which expected compose files exist
    Write-Host "Processing expected compose files..." -ForegroundColor Green
    $existingComposeFiles = @()
    $volumes = @()
    $containers = @()
    $builtImages = @()

    foreach ($yamlFile in $expectedYamlFiles) {
        if (Test-Path $yamlFile) {
            Write-Host "Found: $yamlFile" -ForegroundColor Gray
            $existingComposeFiles += @("-f", $yamlFile)

            # Parse YAML file for resources
            $content = Get-Content $yamlFile -Raw

            # Extract container names
            $containerMatches = [regex]::Matches($content, 'container_name:\s*(.+)')
            foreach ($match in $containerMatches) {
                $containerName = $match.Groups[1].Value.Trim()
                if ($containerName -and $containerName -notin $containers) {
                    $containers += $containerName
                }
            }

            # Extract volumes (from top-level volumes section)
            if ($content -match 'volumes:\s*\n((?:\s+\w+:.*\n?)+)') {
                $volumeSection = $matches[1]
                $volumeMatches = [regex]::Matches($volumeSection, '^\s+(\w+):', [System.Text.RegularExpressions.RegexOptions]::Multiline)
                foreach ($match in $volumeMatches) {
                    $volumeName = $match.Groups[1].Value
                    if ($volumeName -and $volumeName -notin $volumes) {
                        $volumes += $volumeName
                    }
                }
            }

            # Extract services with build contexts (for image removal)
            $serviceMatches = [regex]::Matches($content, 'services:\s*\n((?:\s+\w+:(?:\n(?:\s{4,}.*)?)*)+)', [System.Text.RegularExpressions.RegexOptions]::Singleline)
            foreach ($serviceMatch in $serviceMatches) {
                $servicesContent = $serviceMatch.Groups[1].Value
                $buildMatches = [regex]::Matches($servicesContent, '^\s+(\w+):\s*\n(?:.*\n)*?\s+build:', [System.Text.RegularExpressions.RegexOptions]::Multiline)
                foreach ($buildMatch in $buildMatches) {
                    $serviceName = $buildMatch.Groups[1].Value
                    if ($serviceName -and $serviceName -notin $builtImages) {
                        $builtImages += $serviceName
                    }
                }
            }
        }
    }

    if ($existingComposeFiles.Count -eq 0) {
        Write-Host "`nNo compose files found. Nothing to tear down." -ForegroundColor Yellow
        exit 0
    }

    # Stop all containers using docker-compose
    Write-Host "`nStopping all containers..." -ForegroundColor Yellow
    try {
        # Use the same environment file that was used during setup
        $envFile = "./.env.e2e"
        if (-not (Test-Path $envFile)) {
            $envFile = "./.env"
        }
        
        # Run docker compose with environment file and filter out SCHEMA_PACKAGES warning
        $output = docker compose $existingComposeFiles --env-file $envFile -p dms-local down -v 2>&1
        
        # Filter out the SCHEMA_PACKAGES warning from output
        $filteredOutput = $output | Where-Object { 
            $_ -notmatch 'SCHEMA_PACKAGES.*variable is not set' 
        }
        
        if ($filteredOutput) {
            $filteredOutput | ForEach-Object { Write-Host $_ }
        }
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Containers stopped successfully." -ForegroundColor Green
        }
    }
    catch {
        Write-Warning "Error stopping containers: $_"
    }
    
    # Force stop any remaining containers with dms or kafka in the name
    Write-Host "`nForce stopping any remaining containers..." -ForegroundColor Yellow
    $remainingContainers = docker ps -a --format "{{.Names}}" | Where-Object { $_ -match "(dms|kafka)" }
    if ($remainingContainers) {
        foreach ($container in $remainingContainers) {
            Write-Host "- Force removing container: $container" -ForegroundColor Gray
            docker rm -f $container 2>&1 | Out-Null
        }
    }

    # Remove volumes
    if ($volumes.Count -gt 0) {
        Write-Host "`nRemoving volumes..." -ForegroundColor Yellow
        foreach ($volume in $volumes) {
            # Try with project name prefix
            $volumeWithPrefix = "dms-local_$volume"

            Write-Host "- Removing $volume..." -NoNewline

            # First try with prefix
            $removed = $false
            if (docker volume ls -q | Where-Object { $_ -eq $volumeWithPrefix }) {
                try {
                    docker volume rm $volumeWithPrefix 2>&1 | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Host " done" -ForegroundColor Green
                        $removed = $true
                    }
                }
                catch {
                    Write-Host " error" -ForegroundColor Red
                    Write-Warning "  $_"
                }
            }

            # If not found with prefix, try without
            if (-not $removed) {
                if (docker volume ls -q | Where-Object { $_ -eq $volume }) {
                    try {
                        docker volume rm $volume 2>&1 | Out-Null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Host " done" -ForegroundColor Green
                        }
                        else {
                            Write-Host " not found or already removed" -ForegroundColor Gray
                        }
                    }
                    catch {
                        Write-Host " error" -ForegroundColor Red
                        Write-Warning "  $_"
                    }
                }
                else {
                    Write-Host " not found" -ForegroundColor Gray
                }
            }
        }
    }

    # Also remove any remaining volumes with dms-local prefix
    Write-Host "`nChecking for additional dms-local volumes..." -ForegroundColor Yellow
    $remainingVolumes = docker volume ls --format "{{.Name}}" | Where-Object { $_ -match "^dms-local_" }
    if ($remainingVolumes) {
        foreach ($volume in $remainingVolumes) {
            Write-Host "- Removing $volume..." -NoNewline
            try {
                docker volume rm $volume 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Host " done" -ForegroundColor Green
                }
            }
            catch {
                Write-Host " error" -ForegroundColor Red
                Write-Warning "  $_"
            }
        }
    }

    # Remove orphaned volumes (volumes not attached to any container)
    Write-Host "`nRemoving orphaned volumes..." -ForegroundColor Yellow
    try {
        # Get list of orphaned volumes
        $orphanedVolumes = docker volume ls -qf dangling=true
        if ($orphanedVolumes) {
            $orphanedCount = ($orphanedVolumes | Measure-Object).Count
            Write-Host "Found $orphanedCount orphaned volume(s)" -ForegroundColor Gray
            
            # Remove each orphaned volume
            foreach ($volume in $orphanedVolumes) {
                Write-Host "- Removing orphaned volume $volume..." -NoNewline
                try {
                    docker volume rm $volume 2>&1 | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Host " done" -ForegroundColor Green
                    }
                    else {
                        Write-Host " failed" -ForegroundColor Red
                    }
                }
                catch {
                    Write-Host " error" -ForegroundColor Red
                    Write-Warning "  $_"
                }
            }
        }
        else {
            Write-Host "No orphaned volumes found" -ForegroundColor Gray
        }
    }
    catch {
        Write-Warning "Error checking for orphaned volumes: $_"
    }

    # Remove locally-built images
    if ($builtImages.Count -gt 0) {
        Write-Host "`nRemoving locally-built images..." -ForegroundColor Yellow
        foreach ($imageName in $builtImages) {
            # Try both naming conventions (hyphen and underscore)
            $imageVariants = @(
                "dms-local-$imageName",
                "dms-local_$imageName",
                "dms-local-${imageName}-1",
                "dms-local_${imageName}_1"
            )

            foreach ($imageVariant in $imageVariants) {
                $imageId = docker images -q $imageVariant 2>$null
                if ($imageId) {
                    Write-Host "- Removing $imageVariant..." -NoNewline
                    try {
                        docker rmi $imageId -f 2>&1 | Out-Null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Host " done" -ForegroundColor Green
                            break
                        }
                    }
                    catch {
                        Write-Host " error" -ForegroundColor Red
                        Write-Warning "  $_"
                    }
                }
            }
        }
    }

    # Remove the dms network
    Write-Host "`nRemoving dms network..." -NoNewline
    try {
        docker network rm dms 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host " done" -ForegroundColor Green
        }
        else {
            Write-Host " not found or has connected containers" -ForegroundColor Gray
        }
    }
    catch {
        Write-Host " error" -ForegroundColor Red
        Write-Warning "  $_"
    }

    # Verification step - check that everything was removed
    Write-Host "`nVerifying cleanup..." -ForegroundColor Yellow
    $verificationFailed = $false
    
    # Check for any remaining containers
    $remainingContainers = docker ps -a --format "{{.Names}}" | Where-Object { $_ -match "(dms|kafka)" }
    if ($remainingContainers) {
        Write-Warning "Found remaining containers that were not removed:"
        foreach ($container in $remainingContainers) {
            Write-Warning "  - $container"
        }
        $verificationFailed = $true
    }
    else {
        Write-Host "✓ All containers removed" -ForegroundColor Green
    }
    
    # Check for any remaining volumes
    $remainingVolumes = docker volume ls --format "{{.Name}}" | Where-Object { $_ -match "^dms-local_" }
    if ($remainingVolumes) {
        Write-Warning "Found remaining volumes that were not removed:"
        foreach ($volume in $remainingVolumes) {
            Write-Warning "  - $volume"
        }
        $verificationFailed = $true
    }
    else {
        Write-Host "✓ All volumes removed" -ForegroundColor Green
    }
    
    # Check for any remaining images
    $remainingImages = @()
    foreach ($imageName in @("dms", "config")) {
        $imageVariants = @(
            "dms-local-$imageName",
            "dms-local_$imageName"
        )
        foreach ($imageVariant in $imageVariants) {
            if (docker images -q $imageVariant 2>$null) {
                $remainingImages += $imageVariant
            }
        }
    }
    
    if ($remainingImages) {
        Write-Warning "Found remaining images that were not removed:"
        foreach ($image in $remainingImages) {
            Write-Warning "  - $image"
        }
        $verificationFailed = $true
    }
    else {
        Write-Host "✓ All locally-built images removed" -ForegroundColor Green
    }
    
    # Check if network was removed
    $networkExists = docker network ls --format "{{.Name}}" | Where-Object { $_ -eq "dms" }
    if ($networkExists) {
        Write-Warning "The 'dms' network still exists"
        $verificationFailed = $true
    }
    else {
        Write-Host "✓ Network removed" -ForegroundColor Green
    }
    
    if ($verificationFailed) {
        Write-Host "`nTeardown completed with warnings!" -ForegroundColor Yellow
        Write-Host "Some resources may need manual cleanup." -ForegroundColor Yellow
        exit 1
    }
    else {
        Write-Host "`nTeardown complete!" -ForegroundColor Green
        Write-Host "All resources have been successfully removed." -ForegroundColor Green
    }
    
    Write-Host "To setup this environment again, run: ./setup-local-dms.ps1" -ForegroundColor Cyan
}
finally {
    # Return to original location
    Set-Location $originalLocation
}