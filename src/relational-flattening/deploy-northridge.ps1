# ============================================================================
# Deploy Original Northridge Database
# ============================================================================
# Simple script to deploy the original Ed-Fi Northridge database from backup
# Creates a clean original database for testing and development
# ============================================================================

[CmdletBinding()]
param(
    [string]$DatabaseName = "northridge_original",
    [string]$Port = "54330"
)

# Configuration
$BackupDir = "EdFi_Ods_Northridge_v73_20250909_PG13"
$BackupFile = "EdFi_Ods_Northridge_v73_20250909_PG13.sql"
$ContainerName = "postgres-" + $DatabaseName

function Write-Step { param($Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Success { param($Message) Write-Host "✓ $Message" -ForegroundColor Green }
function Write-Error { param($Message) Write-Host "✗ $Message" -ForegroundColor Red }

function Wait-ForDatabase {
    param([string]$ContainerName, [int]$TimeoutSeconds = 60)

    Write-Step "Waiting for PostgreSQL to be ready..."
    $start = Get-Date

    while ((Get-Date) - $start -lt [TimeSpan]::FromSeconds($TimeoutSeconds)) {
        try {
            $result = docker exec $ContainerName pg_isready -U postgres 2>$null
            if ($LASTEXITCODE -eq 0) {
                Write-Success "PostgreSQL is ready"
                return $true
            }
        } catch {
            # Continue waiting
        }

        Write-Host "." -NoNewline
        Start-Sleep -Seconds 3
    }

    Write-Error "PostgreSQL timeout after ${TimeoutSeconds} seconds"
    return $false
}

function Restore-Database {
    param([string]$ContainerName, [string]$DatabaseName, [string]$BackupFile)

    Write-Step "Creating database $DatabaseName..."
    $createCmd = "CREATE DATABASE ""$DatabaseName"";"
    $result = docker exec $ContainerName psql -U postgres -c $createCmd 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to create database"
        return $false
    }

    Write-Step "Restoring database from backup..."
    $restoreResult = docker exec $ContainerName psql -U postgres -d $DatabaseName -f "/backup/$BackupFile" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to restore database from backup:"
        Write-Host $restoreResult -ForegroundColor Red
        return $false
    }

    Write-Step "Verifying restored data..."
    $countResult = docker exec $ContainerName psql -U postgres -d $DatabaseName -t -c "SELECT COUNT(*) FROM edfi.student;" 2>&1
    if ($LASTEXITCODE -eq 0) {
        $countStr = $countResult -join "" | Out-String
        $count = [int]($countStr.Trim())
        if ($count -gt 0) {
            Write-Success "Database restored successfully with $count students"
            return $true
        } else {
            Write-Error "Database contains no student data"
            return $false
        }
    } else {
        Write-Error "Database restore verification failed: $countResult"
        return $false
    }
}

Write-Host "🚀 Original Northridge Database Deployment" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Green

# Check if container already exists and is running
$existingContainer = docker ps -a --format "{{.Names}}" | Select-String -Pattern "^$ContainerName$"
if ($existingContainer) {
    $containerStatus = docker ps --format "{{.Names}} {{.Status}}" | Select-String -Pattern "^$ContainerName"
    if ($containerStatus -and $containerStatus.ToString().Contains("Up")) {
        Write-Success "Container $ContainerName is already running"
        Write-Host ""
        Write-Host "Database Connection:" -ForegroundColor Yellow
        Write-Host "  Host: localhost"
        Write-Host "  Port: $Port"
        Write-Host "  Database: $DatabaseName"
        Write-Host "  Username: postgres"
        Write-Host "  Password: password123"
        Write-Host "  Connection: docker exec -it $ContainerName psql -U postgres -d $DatabaseName"
        return
    } else {
        Write-Step "Starting existing container..."
        docker start $ContainerName
        if ($LASTEXITCODE -eq 0) {
            Write-Success "Container started successfully"
        } else {
            Write-Error "Failed to start existing container"
            exit 1
        }
    }
} else {
    # Deploy new container
    Write-Step "Deploying Database Container..."

    # Verify backup directory exists
    if (-not (Test-Path $BackupDir)) {
        Write-Error "Backup directory not found: $BackupDir"
        exit 1
    }

    $containerId = docker run --name $ContainerName `
        -e POSTGRES_PASSWORD=password123 `
        -p "${Port}:5432" `
        -v "${PWD}/${BackupDir}:/backup" `
        -d postgres:13-alpine

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to deploy container"
        exit 1
    }

    Write-Success "Container deployed: $($containerId.Substring(0,12))"
}

# Wait for database to be ready and restore backup
if (-not (Wait-ForDatabase $ContainerName)) {
    Write-Error "PostgreSQL failed to start"
    exit 1
}

if (-not (Restore-Database $ContainerName $DatabaseName $BackupFile)) {
    Write-Error "Database restore failed"
    exit 1
}

# Final verification
Write-Step "Final database verification..."
$studentCount = docker exec $ContainerName psql -U postgres -d $DatabaseName -t -c "SELECT COUNT(*) FROM edfi.student;"
$tableCount = docker exec $ContainerName psql -U postgres -d $DatabaseName -t -c "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'edfi';"

Write-Success "Database deployment complete:"
Write-Host "  Students: $($studentCount.Trim())"
Write-Host "  EdFi Tables: $($tableCount.Trim())"

Write-Host ""
Write-Success "🎉 Database Ready!"
Write-Host ""
Write-Host "Database Connection:" -ForegroundColor Yellow
Write-Host "  Host: localhost"
Write-Host "  Port: $Port"
Write-Host "  Database: $DatabaseName"
Write-Host "  Username: postgres"
Write-Host "  Password: password123"
Write-Host "  Connection: docker exec -it $ContainerName psql -U postgres -d $DatabaseName"
Write-Host ""

Write-Host "Database is ready for use!" -ForegroundColor Green