#!/usr/bin/env pwsh

# Shared validation functions for performance tests

function Test-DatabaseFunction {
    <#
    .SYNOPSIS
    Validates that a database function exists and is callable

    .PARAMETER Container
    Docker container name

    .PARAMETER Database
    Database name

    .PARAMETER FunctionName
    Function name to validate

    .RETURNS
    $true if function exists, $false otherwise
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Container,

        [Parameter(Mandatory=$true)]
        [string]$Database,

        [Parameter(Mandatory=$true)]
        [string]$FunctionName
    )

    Write-Host "Checking if function '$FunctionName' exists in $Database..." -ForegroundColor Cyan

    # Extract just the function name without parameters
    $functionBase = $FunctionName -replace '\(.*\)$', ''

    # Check if function exists
    $checkQuery = "\df $functionBase"
    $result = docker exec $Container psql -U postgres -d "$Database" -c "$checkQuery" 2>&1

    if ($result -match $functionBase) {
        Write-Host "  ✓ Function exists" -ForegroundColor Green
        return $true
    } else {
        Write-Host "  ✗ Function does not exist" -ForegroundColor Red
        Write-Host "  Available functions:" -ForegroundColor Yellow
        $availableFunctions = docker exec $Container psql -U postgres -d "$Database" -t -c "SELECT proname FROM pg_proc WHERE proname LIKE 'sp_imart%'" 2>&1
        Write-Host $availableFunctions -ForegroundColor Yellow
        return $false
    }
}

function Test-FunctionExecution {
    <#
    .SYNOPSIS
    Tests if a function can be executed and returns valid data

    .PARAMETER Container
    Docker container name

    .PARAMETER Database
    Database name

    .PARAMETER FunctionCall
    Complete SQL function call

    .RETURNS
    Object with IsValid and Count properties
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Container,

        [Parameter(Mandatory=$true)]
        [string]$Database,

        [Parameter(Mandatory=$true)]
        [string]$FunctionCall
    )

    Write-Host "Testing function execution..." -ForegroundColor Cyan

    $output = docker exec $Container psql -U postgres -d "$Database" -c "$FunctionCall" 2>&1

    # Extract count from output
    $countMatch = $output | Select-String "^\s*(\d+)\s*$"
    $count = if ($countMatch) { $countMatch.Matches[0].Groups[1].Value } else { "Unknown" }

    $result = @{
        IsValid = ($count -ne "Unknown" -and $count -match '^\d+$')
        Count = $count
        Output = $output -join "`n"
    }

    if ($result.IsValid) {
        Write-Host "  ✓ Function executed successfully, returned $count rows" -ForegroundColor Green
    } else {
        Write-Host "  ✗ Function execution failed or returned invalid data" -ForegroundColor Red
        Write-Host "  Output: $($result.Output)" -ForegroundColor Yellow
    }

    return $result
}

function Test-ResultValidity {
    <#
    .SYNOPSIS
    Validates a test result for data quality issues

    .PARAMETER Result
    Test result object with Count and DurationMs properties

    .RETURNS
    Validation result object
    #>
    param(
        [Parameter(Mandatory=$true)]
        [PSCustomObject]$Result
    )

    $validation = @{
        IsValid = $true
        Warnings = @()
        Errors = @()
    }

    # Check if count is valid
    if ($Result.Count -eq "Unknown" -or -not ($Result.Count -match '^\d+$')) {
        $validation.IsValid = $false
        $validation.Errors += "Invalid count value: '$($Result.Count)'"
    }

    # Sanity check for timing (warn if processing >10k records in <500ms)
    if ($Result.Count -match '^\d+$') {
        $recordCount = [int]$Result.Count
        if ($recordCount -gt 10000 -and $Result.DurationMs -lt 500) {
            $validation.Warnings += "Suspiciously fast: $($Result.DurationMs)ms for $recordCount records"
        }

        # Warn if timing suggests <0.01ms per record (likely not real processing)
        $msPerRecord = $Result.DurationMs / $recordCount
        if ($msPerRecord -lt 0.01) {
            $validation.Warnings += "Unrealistic performance: $([math]::Round($msPerRecord, 4))ms per record"
        }
    }

    return $validation
}

function Write-ValidationSummary {
    <#
    .SYNOPSIS
    Writes a summary of validation results

    .PARAMETER Results
    Array of test results
    #>
    param(
        [Parameter(Mandatory=$true)]
        [array]$Results
    )

    Write-Host "`nValidation Summary:" -ForegroundColor Cyan

    $invalidResults = $Results | Where-Object { $_.Count -eq "Unknown" -or -not ($_.Count -match '^\d+$') }
    $validResults = $Results | Where-Object { $_.Count -ne "Unknown" -and $_.Count -match '^\d+$' }

    if ($invalidResults.Count -gt 0) {
        Write-Host "  ✗ Invalid results: $($invalidResults.Count) of $($Results.Count)" -ForegroundColor Red
        foreach ($invalid in $invalidResults) {
            Write-Host "    - Run $($invalid.Run): Count='$($invalid.Count)'" -ForegroundColor Red
        }
    }

    if ($validResults.Count -gt 0) {
        Write-Host "  ✓ Valid results: $($validResults.Count) of $($Results.Count)" -ForegroundColor Green

        # Check for consistency
        $uniqueCounts = $validResults | Select-Object -ExpandProperty Count -Unique
        if ($uniqueCounts.Count -eq 1) {
            Write-Host "  ✓ Consistent row count: $uniqueCounts" -ForegroundColor Green
        } else {
            Write-Host "  ⚠ Inconsistent row counts: $($uniqueCounts -join ', ')" -ForegroundColor Yellow
        }

        # Check for performance anomalies
        $suspiciousResults = @()
        foreach ($result in $validResults) {
            if ([int]$result.Count -gt 10000 -and $result.DurationMs -lt 500) {
                $suspiciousResults += $result
            }
        }

        if ($suspiciousResults.Count -gt 0) {
            Write-Host "  ⚠ Suspiciously fast executions: $($suspiciousResults.Count)" -ForegroundColor Yellow
            foreach ($suspicious in $suspiciousResults) {
                Write-Host "    - Run $($suspicious.Run): $($suspicious.DurationMs)ms for $($suspicious.Count) records" -ForegroundColor Yellow
            }
        }
    }

    # Return overall validity
    return $invalidResults.Count -eq 0
}

function Stop-OnInvalidResult {
    <#
    .SYNOPSIS
    Stops execution if result is invalid

    .PARAMETER Result
    Test result to validate

    .PARAMETER FunctionName
    Name of function being tested (for error message)
    #>
    param(
        [Parameter(Mandatory=$true)]
        [PSCustomObject]$Result,

        [Parameter(Mandatory=$true)]
        [string]$FunctionName
    )

    if ($Result.Count -eq "Unknown" -or -not ($Result.Count -match '^\d+$')) {
        Write-Host "`n❌ CRITICAL ERROR: Function '$FunctionName' is not returning valid data!" -ForegroundColor Red
        Write-Host "   Count value: '$($Result.Count)'" -ForegroundColor Red
        Write-Host "   This indicates the function may not exist or is failing to execute properly." -ForegroundColor Red
        Write-Host "`n   Please ensure the function is loaded in the database before running this test." -ForegroundColor Yellow
        Write-Host "   To load the function, run the appropriate SQL script:" -ForegroundColor Yellow
        Write-Host "   psql -U postgres -d [database] -f [sql_script_file]" -ForegroundColor Cyan
        exit 1
    }
}

# Note: These functions are now available through dot-sourcing
# Export-ModuleMember would only work if this was a .psm1 module file