# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Tears down the Ed-Fi DMS Configuration Service local Docker environment created by setup-local-cms.ps1
.DESCRIPTION
    This script reverses the process of setup-local-cms.ps1 by:
    - Stopping all containers in the cs-local stack (not dms-local)
    - Removing all associated volumes
    - Removing locally-built images (cs-local-config)
    - Removing the dms network if no other containers are using it

    The script targets the cs-local Docker Compose stack to match CI/CD behavior.
    It is the companion to setup-local-cms.ps1.
#>

[CmdletBinding()]
param()

Write-Host @"
Ed-Fi DMS Configuration Service Local Environment Teardown
==========================================================
"@ -ForegroundColor Cyan

# Store current location and navigate to docker-compose directory
$originalLocation = Get-Location
$dockerComposeDir = Join-Path $PSScriptRoot "../../../../eng/docker-compose"

try {
    Set-Location $dockerComposeDir

    # Define the compose files used by CMS (matching start-local-config.ps1)
    $composeFiles = @(
        "-f", "postgresql.yml",
        "-f", "local-config.yml", 
        "-f", "keycloak.yml"
    )

    # Check if Docker is running
    Write-Host "Checking Docker status..." -ForegroundColor Yellow
    $dockerCheck = $null
    try {
        $dockerCheck = docker version 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Docker command failed"
        }
    }
    catch {
        Write-Host ""
        Write-Error "Docker is not running or not installed. Please start Docker and try again."
        Write-Host ""
        Write-Host "Error details:" -ForegroundColor Red
        if ($dockerCheck) {
            Write-Host $dockerCheck -ForegroundColor Red
        } else {
            Write-Host $_.Exception.Message -ForegroundColor Red
        }
        exit 1
    }
    Write-Host "Docker is running ✓" -ForegroundColor Green
    Write-Host ""

    # Stop all containers using docker-compose with cs-local project name
    Write-Host "Stopping cs-local stack containers..." -ForegroundColor Yellow
    try {
        # Use the same environment file that was used during setup
        $envFile = "./.env.config.e2e"
        if (-not (Test-Path $envFile)) {
            Write-Warning "Environment file $envFile not found, using default .env"
            $envFile = "./.env"
        }
        
        # Run docker compose with cs-local project name (matches start-local-config.ps1)
        Write-Host "Running: docker compose $composeFiles --env-file $envFile -p cs-local down -v" -ForegroundColor Gray
        $output = docker compose $composeFiles --env-file $envFile -p cs-local down -v 2>&1
        
        if ($output) {
            $output | ForEach-Object { Write-Host $_ }
        }
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "cs-local stack stopped successfully." -ForegroundColor Green
        } else {
            Write-Warning "Some issues occurred while stopping cs-local stack (exit code: $LASTEXITCODE)"
        }
    }
    catch {
        Write-Warning "Error stopping cs-local containers: $_"
    }
    
    # Force stop any remaining cs-local containers
    Write-Host "`nForce stopping any remaining cs-local containers..." -ForegroundColor Yellow
    $remainingContainers = docker ps -a --format "{{.Names}}" | Where-Object { $_ -match "cs-local" }
    if ($remainingContainers) {
        foreach ($container in $remainingContainers) {
            Write-Host "- Force removing container: $container" -ForegroundColor Gray
            docker rm -f $container 2>&1 | Out-Null
        }
        Write-Host "Remaining cs-local containers removed." -ForegroundColor Green
    } else {
        Write-Host "No remaining cs-local containers found." -ForegroundColor Green
    }

    # Remove cs-local volumes
    Write-Host "`nRemoving cs-local volumes..." -ForegroundColor Yellow
    $remainingVolumes = docker volume ls --format "{{.Name}}" | Where-Object { $_ -match "^cs-local_" }
    if ($remainingVolumes) {
        foreach ($volume in $remainingVolumes) {
            Write-Host "- Removing $volume..." -NoNewline
            try {
                docker volume rm $volume 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Host " done" -ForegroundColor Green
                } else {
                    Write-Host " failed" -ForegroundColor Red
                }
            }
            catch {
                Write-Host " error" -ForegroundColor Red
                Write-Warning "  $_"
            }
        }
    } else {
        Write-Host "No cs-local volumes found." -ForegroundColor Green
    }

    # Remove locally-built cs-local images
    Write-Host "`nRemoving cs-local images..." -ForegroundColor Yellow
    $imageVariants = @(
        "cs-local-config",
        "cs-local_config"
    )

    $foundImages = $false
    foreach ($imageVariant in $imageVariants) {
        $imageId = docker images -q $imageVariant 2>$null
        if ($imageId) {
            $foundImages = $true
            Write-Host "- Removing $imageVariant..." -NoNewline
            try {
                docker rmi $imageId -f 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Host " done" -ForegroundColor Green
                } else {
                    Write-Host " failed" -ForegroundColor Red
                }
            }
            catch {
                Write-Host " error" -ForegroundColor Red
                Write-Warning "  $_"
            }
        }
    }
    
    if (-not $foundImages) {
        Write-Host "No cs-local images found." -ForegroundColor Green
    }

    # Check if dms network should be removed (only if no containers are using it)
    Write-Host "`nChecking dms network..." -ForegroundColor Yellow
    $networkExists = docker network ls --format "{{.Name}}" | Where-Object { $_ -eq "dms" }
    if ($networkExists) {
        # Check if any containers are connected to the dms network
        $connectedContainers = docker network inspect dms --format '{{range .Containers}}{{.Name}} {{end}}' 2>$null
        if ($connectedContainers -and $connectedContainers.Trim()) {
            Write-Host "dms network has connected containers, leaving it in place." -ForegroundColor Gray
        } else {
            Write-Host "Removing unused dms network..." -NoNewline
            try {
                docker network rm dms 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Host " done" -ForegroundColor Green
                } else {
                    Write-Host " failed or in use" -ForegroundColor Gray
                }
            }
            catch {
                Write-Host " error" -ForegroundColor Red
                Write-Warning "  $_"
            }
        }
    } else {
        Write-Host "dms network not found." -ForegroundColor Green
    }

    # Verification step
    Write-Host "`nVerifying cs-local cleanup..." -ForegroundColor Yellow
    $verificationFailed = $false
    
    # Check for any remaining cs-local containers
    $remainingContainers = docker ps -a --format "{{.Names}}" | Where-Object { $_ -match "cs-local" }
    if ($remainingContainers) {
        Write-Warning "Found remaining cs-local containers:"
        foreach ($container in $remainingContainers) {
            Write-Warning "  - $container"
        }
        $verificationFailed = $true
    } else {
        Write-Host "✓ All cs-local containers removed" -ForegroundColor Green
    }
    
    # Check for any remaining cs-local volumes
    $remainingVolumes = docker volume ls --format "{{.Name}}" | Where-Object { $_ -match "^cs-local_" }
    if ($remainingVolumes) {
        Write-Warning "Found remaining cs-local volumes:"
        foreach ($volume in $remainingVolumes) {
            Write-Warning "  - $volume"
        }
        $verificationFailed = $true
    } else {
        Write-Host "✓ All cs-local volumes removed" -ForegroundColor Green
    }
    
    # Check for any remaining cs-local images
    $remainingImages = @()
    foreach ($imageVariant in $imageVariants) {
        if (docker images -q $imageVariant 2>$null) {
            $remainingImages += $imageVariant
        }
    }
    
    if ($remainingImages) {
        Write-Warning "Found remaining cs-local images:"
        foreach ($image in $remainingImages) {
            Write-Warning "  - $image"
        }
        $verificationFailed = $true
    } else {
        Write-Host "✓ All cs-local images removed" -ForegroundColor Green
    }
    
    if ($verificationFailed) {
        Write-Host "`ncs-local teardown completed with warnings!" -ForegroundColor Yellow
        Write-Host "Some resources may need manual cleanup." -ForegroundColor Yellow
        exit 1
    } else {
        Write-Host "`ncs-local teardown complete!" -ForegroundColor Green
        Write-Host "All cs-local resources have been successfully removed." -ForegroundColor Green
        Write-Host "The dms-local stack (if running) remains unaffected." -ForegroundColor Cyan
    }
    
    Write-Host "To setup the CMS environment again, run: ./setup-local-cms.ps1" -ForegroundColor Cyan
}
finally {
    # Return to original location
    Set-Location $originalLocation
}