#!/usr/bin/env pwsh

Write-Host "Starting performance test for joins function..."
Write-Host "Running sp_imart_transform_dim_student_edfi_postgres_joins() 10 times on northridge-flattened"

$results = @()

for ($i = 1; $i -le 10; $i++) {
    Write-Host "Run $i of 10..."

    $startTime = Get-Date

    $output = docker exec postgres-northridge-flattened psql -U postgres -d "northridge-flattened" -c "SELECT COUNT(*) FROM sp_imart_transform_dim_student_edfi_postgres_joins();" 2>&1

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

Write-Host "Statistics:"
Write-Host "  Average: $([math]::Round($avgDuration, 2))ms"
Write-Host "  Minimum: $minDuration ms"
Write-Host "  Maximum: $maxDuration ms"

# Export results to JSON for later use
$results | ConvertTo-Json -Depth 3 | Out-File -FilePath "performance-joins-results.json"
Write-Host "`nResults saved to performance-joins-results.json"