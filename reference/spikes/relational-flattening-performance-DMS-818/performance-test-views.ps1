#!/usr/bin/env pwsh

# Import validation functions
. ./performance-test-common.ps1

$container = "postgres-northridge-flattened"
$database = "northridge-flattened"
$functionName = "sp_imart_transform_dim_student_edfi_postgres_views"
$functionCall = "SELECT COUNT(*) FROM $functionName();"

Write-Host "Performance Test: Views Function" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan
Write-Host "Container: $container" -ForegroundColor Yellow
Write-Host "Database: $database" -ForegroundColor Yellow
Write-Host "Function: $functionName" -ForegroundColor Yellow
Write-Host ""

# Pre-test validation
if (-not (Test-DatabaseFunction -Container $container -Database $database -FunctionName $functionName)) {
    Write-Host "`n❌ CRITICAL ERROR: Function '$functionName' does not exist!" -ForegroundColor Red
    Write-Host "   Please load the function from sp_iMart_Transform_DIM_STUDENT_edfi_Postgres_Views.sql" -ForegroundColor Yellow
    Write-Host "   Run: docker exec $container psql -U postgres -d '$database' -f /path/to/sp_iMart_Transform_DIM_STUDENT_edfi_Postgres_Views.sql" -ForegroundColor Cyan
    exit 1
}

# Test function execution
$testExecution = Test-FunctionExecution -Container $container -Database $database -FunctionCall $functionCall
if (-not $testExecution.IsValid) {
    Write-Host "`n❌ CRITICAL ERROR: Function cannot be executed or returns invalid data!" -ForegroundColor Red
    Write-Host "   Please verify the function is properly implemented." -ForegroundColor Yellow
    Write-Host "   Test output: $($testExecution.Output)" -ForegroundColor Red
    exit 1
}

Write-Host "`nStarting performance test..."
Write-Host "Running $functionName() 10 times on $database"
Write-Host ""

$results = @()

for ($i = 1; $i -le 10; $i++) {
    Write-Host "Run $i of 10..." -NoNewline

    $startTime = Get-Date

    $output = docker exec $container psql -U postgres -d "$database" -c "$functionCall" 2>&1

    $endTime = Get-Date
    $duration = ($endTime - $startTime).TotalMilliseconds

    # Extract count from output
    $countMatch = $output | Select-String "^\s*(\d+)\s*$"
    $count = if ($countMatch) { $countMatch.Matches[0].Groups[1].Value } else { "Unknown" }

    $result = [PSCustomObject]@{
        Run = $i
        Count = $count
        DurationMs = [math]::Round($duration, 2)
        StartTime = $startTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
        EndTime = $endTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
    }

    # Validate this result
    Stop-OnInvalidResult -Result $result -FunctionName $functionName

    # Check for performance anomalies
    $validation = Test-ResultValidity -Result $result
    if ($validation.Warnings.Count -gt 0) {
        Write-Host " ⚠" -ForegroundColor Yellow
        foreach ($warning in $validation.Warnings) {
            Write-Host "  Warning: $warning" -ForegroundColor Yellow
        }
    } else {
        Write-Host " ✓" -ForegroundColor Green
    }

    $results += $result
    Write-Host "  Duration: $($result.DurationMs)ms, Count: $($result.Count)"
}

Write-Host "`nResults Summary:"
$results | Format-Table -AutoSize

# Calculate statistics
$durations = $results | ForEach-Object { $_.DurationMs }
$avgDuration = ($durations | Measure-Object -Average).Average
$minDuration = ($durations | Measure-Object -Minimum).Minimum
$maxDuration = ($durations | Measure-Object -Maximum).Maximum
$stdDev = if ($durations.Count -gt 1) {
    [math]::Sqrt(($durations | ForEach-Object { [math]::Pow($_ - $avgDuration, 2) } | Measure-Object -Average).Average)
} else { 0 }

Write-Host "Statistics:" -ForegroundColor Cyan
Write-Host "  Average: $([math]::Round($avgDuration, 2))ms"
Write-Host "  Minimum: $minDuration ms"
Write-Host "  Maximum: $maxDuration ms"
Write-Host "  Std Dev: $([math]::Round($stdDev, 2))ms"

# Validation summary
$isValid = Write-ValidationSummary -Results $results

if (-not $isValid) {
    Write-Host "`n⚠ WARNING: Test completed but some results were invalid!" -ForegroundColor Yellow
    Write-Host "  This may indicate an intermittent problem with the function." -ForegroundColor Yellow
}

# Export results to JSON for later use
$exportData = @{
    TestName = "Views Function"
    TestDate = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Container = $container
    Database = $database
    Function = $functionName
    Results = $results
    Statistics = @{
        AverageMs = [math]::Round($avgDuration, 2)
        MinimumMs = $minDuration
        MaximumMs = $maxDuration
        StdDevMs = [math]::Round($stdDev, 2)
        ValidResults = ($results | Where-Object { $_.Count -match '^\d+$' }).Count
        InvalidResults = ($results | Where-Object { $_.Count -eq "Unknown" }).Count
    }
}

$exportData | ConvertTo-Json -Depth 3 | Out-File -FilePath "performance-views-results.json"
Write-Host "`nResults saved to performance-views-results.json" -ForegroundColor Green

# Exit with error if validation failed
if (-not $isValid) {
    exit 1
}