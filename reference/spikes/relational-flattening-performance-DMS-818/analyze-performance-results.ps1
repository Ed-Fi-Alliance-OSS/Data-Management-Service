#!/usr/bin/env pwsh

# Script to analyze performance test results from multiple runs

$results = @{}

# Function to calculate statistics from multiple runs
function Get-RunStatistics {
    param (
        [string]$TestName,
        [string]$FilePattern
    )

    $runData = @()

    # Try to find run files
    $runFiles = Get-ChildItem -Path "." -Filter "$FilePattern-run*.json" 2>$null

    if ($runFiles.Count -eq 0) {
        # Try alternate pattern (for last run that might not have -run suffix)
        $runFiles = Get-ChildItem -Path "." -Filter "$FilePattern-results.json" 2>$null
    }

    foreach ($file in $runFiles) {
        $data = Get-Content $file.FullName | ConvertFrom-Json
        if ($data.Statistics) {
            $runData += @{
                Average = $data.Statistics.AverageMs
                Min = $data.Statistics.MinimumMs
                Max = $data.Statistics.MaximumMs
                StdDev = $data.Statistics.StdDevMs
                RowCount = if ($data.Results -and $data.Results.Count -gt 0) { $data.Results[0].Count } else { 0 }
            }
        }
    }

    if ($runData.Count -gt 0) {
        $avgOfAverages = ($runData | ForEach-Object { $_.Average } | Measure-Object -Average).Average
        $minOfMins = ($runData | ForEach-Object { $_.Min } | Measure-Object -Minimum).Minimum
        $maxOfMaxs = ($runData | ForEach-Object { $_.Max } | Measure-Object -Maximum).Maximum
        $avgStdDev = ($runData | ForEach-Object { $_.StdDev } | Measure-Object -Average).Average
        $rowCount = $runData[0].RowCount

        return @{
            TestName = $TestName
            RunCount = $runData.Count
            GrandAverage = [math]::Round($avgOfAverages, 2)
            BestTime = [math]::Round($minOfMins, 2)
            WorstTime = [math]::Round($maxOfMaxs, 2)
            AvgStdDev = [math]::Round($avgStdDev, 2)
            RowCount = $rowCount
            RunAverages = $runData | ForEach-Object { [math]::Round($_.Average, 2) }
        }
    }

    return $null
}

# Collect statistics for each test type
$tests = @(
    @{ Name = "Original (Baseline)"; Pattern = "performance-original" },
    @{ Name = "Original Optimized"; Pattern = "performance-original-optimized" },
    @{ Name = "Views"; Pattern = "performance-views" },
    @{ Name = "Views Optimized"; Pattern = "performance-views-optimized" },
    @{ Name = "Joins"; Pattern = "performance-joins" },
    @{ Name = "Joins Optimized"; Pattern = "performance-optimized" }  # Note: joins-optimized saves as optimized
)

Write-Host "PostgreSQL Query Performance Analysis" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

$allResults = @()

foreach ($test in $tests) {
    $stats = Get-RunStatistics -TestName $test.Name -FilePattern $test.Pattern
    if ($stats) {
        $allResults += $stats
    }
}

# Sort by average performance
$allResults = $allResults | Sort-Object GrandAverage

# Find baseline (Original)
$baseline = $allResults | Where-Object { $_.TestName -eq "Original (Baseline)" }
$baselineAvg = if ($baseline) { $baseline.GrandAverage } else { 1 }

# Display results
Write-Host "Performance Rankings (5 Runs per Query Type)" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""

Write-Host ("Rank | Query Type | Grand Average | Speed vs Original | Row Count | Runs") -ForegroundColor Yellow
Write-Host ("-----|------------|---------------|-------------------|-----------|------") -ForegroundColor Yellow

$rank = 1
foreach ($result in $allResults) {
    $speedRatio = [math]::Round($baselineAvg / $result.GrandAverage, 2)
    $speedText = if ($result.TestName -eq "Original (Baseline)") {
        "1.00x (baseline)"
    } elseif ($speedRatio -gt 1) {
        "$($speedRatio)x faster"
    } else {
        "$($speedRatio)x slower"
    }

    $rankDisplay = if ($rank -eq 1) { "🥇 1" } elseif ($rank -eq 2) { "🥈 2" } elseif ($rank -eq 3) { "🥉 3" } else { "  $rank" }

    Write-Host ("{0,-5} | {1,-30} | {2,10} ms | {3,-17} | {4,9} | {5}" -f $rankDisplay, $result.TestName, $result.GrandAverage, $speedText, $result.RowCount, $result.RunCount)
    $rank++
}

Write-Host ""
Write-Host "Detailed Statistics" -ForegroundColor Green
Write-Host "==================" -ForegroundColor Green
Write-Host ""

foreach ($result in $allResults) {
    Write-Host "$($result.TestName):" -ForegroundColor Cyan
    Write-Host "  Run Averages: $($result.RunAverages -join ', ') ms"
    Write-Host "  Grand Average: $($result.GrandAverage) ms"
    Write-Host "  Best Time: $($result.BestTime) ms"
    Write-Host "  Worst Time: $($result.WorstTime) ms"
    Write-Host "  Avg Std Dev: $($result.AvgStdDev) ms"
    Write-Host "  Row Count: $($result.RowCount)"
    Write-Host ""
}

# Export to JSON for further analysis
$allResults | ConvertTo-Json -Depth 10 | Out-File "performance-analysis-summary.json"
Write-Host "Results saved to performance-analysis-summary.json" -ForegroundColor Green