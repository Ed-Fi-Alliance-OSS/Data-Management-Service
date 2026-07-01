# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param(
    [string[]]
    $Path = @("TestResults"),

    [string]
    $OutputDirectory = "TestResults/test-timings",

    [string]
    $SuiteName = "Test Timings",

    [int]
    $SlowTestCount = 20,

    [int]
    $SlowFixtureCount = 10,

    [switch]
    $AppendToGitHubStepSummary
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-TrxFile {
    param(
        [string[]]
        $CandidatePaths
    )

    $files = New-Object System.Collections.Generic.List[System.IO.FileInfo]

    foreach ($candidatePath in $CandidatePaths) {
        if ([string]::IsNullOrWhiteSpace($candidatePath)) {
            continue
        }

        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            $file = Get-Item -LiteralPath $candidatePath
            if ($file.Extension -eq ".trx") {
                $files.Add($file)
            }

            continue
        }

        if (Test-Path -LiteralPath $candidatePath -PathType Container) {
            Get-ChildItem -LiteralPath $candidatePath -Filter "*.trx" -Recurse -File | ForEach-Object {
                $files.Add($_)
            }

            continue
        }

        Get-ChildItem -Path $candidatePath -Filter "*.trx" -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            $files.Add($_)
        }
    }

    $files |
        Sort-Object -Property FullName -Unique
}

function Resolve-MssqlFixtureTimingFile {
    param(
        [string[]]
        $CandidatePaths
    )

    $files = New-Object System.Collections.Generic.List[System.IO.FileInfo]

    foreach ($candidatePath in $CandidatePaths) {
        if ([string]::IsNullOrWhiteSpace($candidatePath)) {
            continue
        }

        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            $file = Get-Item -LiteralPath $candidatePath
            if ($file.Name -eq "mssql-fixture-setup-timings.csv") {
                $files.Add($file)
            }

            continue
        }

        if (Test-Path -LiteralPath $candidatePath -PathType Container) {
            Get-ChildItem -LiteralPath $candidatePath -Filter "mssql-fixture-setup-timings.csv" -Recurse -File | ForEach-Object {
                $files.Add($_)
            }

            continue
        }

        Get-ChildItem -Path $candidatePath -Filter "mssql-fixture-setup-timings.csv" -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            $files.Add($_)
        }
    }

    $files |
        Sort-Object -Property FullName -Unique
}

function Get-TrxNamespaceManager {
    param(
        [xml]
        $Document
    )

    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($Document.NameTable)
    $namespaceManager.AddNamespace("trx", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010") | Out-Null
    return ,$namespaceManager
}

function Select-TrxNode {
    param(
        [xml]
        $Document,

        [System.Xml.XmlNamespaceManager]
        $NamespaceManager,

        [string]
        $NamespacedXPath,

        [string]
        $FallbackXPath
    )

    $nodes = $Document.SelectNodes($NamespacedXPath, $NamespaceManager)
    if ($nodes.Count -gt 0) {
        return $nodes
    }

    $Document.SelectNodes($FallbackXPath)
}

function Select-TrxSingleNode {
    param(
        [System.Xml.XmlNode]
        $Node,

        [System.Xml.XmlNamespaceManager]
        $NamespaceManager,

        [string]
        $NamespacedXPath,

        [string]
        $FallbackXPath
    )

    $childNode = $Node.SelectSingleNode($NamespacedXPath, $NamespaceManager)
    if ($null -ne $childNode) {
        return $childNode
    }

    $Node.SelectSingleNode($FallbackXPath)
}

function ConvertTo-TestDuration {
    param(
        [string]
        $Duration
    )

    if ([string]::IsNullOrWhiteSpace($Duration)) {
        return [TimeSpan]::Zero
    }

    $parsedDuration = [TimeSpan]::Zero
    if ([TimeSpan]::TryParse($Duration, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsedDuration)) {
        return $parsedDuration
    }

    Write-Warning "Could not parse TRX duration '$Duration'. Treating it as zero."
    [TimeSpan]::Zero
}

function Format-Duration {
    param(
        [double]
        $Seconds
    )

    $duration = [TimeSpan]::FromSeconds($Seconds)
    if ($duration.TotalHours -ge 1) {
        return "{0:0}:{1:00}:{2:00}.{3:000}" -f [Math]::Floor($duration.TotalHours), $duration.Minutes, $duration.Seconds, $duration.Milliseconds
    }

    "{0:0}:{1:00}.{2:000}" -f [Math]::Floor($duration.TotalMinutes), $duration.Seconds, $duration.Milliseconds
}

function Format-MarkdownText {
    param(
        [AllowNull()]
        [string]
        $Value
    )

    if ($null -eq $Value) {
        return ""
    }

    $Value.Replace("|", "\|").Replace("`r", " ").Replace("`n", " ")
}

function Get-CsvValue {
    param(
        [psobject]
        $Record,

        [string]
        $Name,

        [string]
        $Default = ""
    )

    $property = $Record.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $Default
    }

    $value = [string]$property.Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Default
    }

    $value
}

function ConvertTo-MssqlTimingDurationSeconds {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Mssql is a product acronym in the helper name, not a plural noun.')]
    param(
        [string]
        $Value
    )

    $durationSeconds = 0.0
    if ([double]::TryParse($Value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$durationSeconds)) {
        return [Math]::Round($durationSeconds, 3)
    }

    Write-Warning "Could not parse MSSQL fixture timing duration '$Value'. Treating it as zero."
    0.0
}

function ConvertTo-MssqlTimingBoolean {
    param(
        [string]
        $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    $normalizedValue = $Value.Trim()
    return $normalizedValue -in @("1", "true", "True", "TRUE", "yes", "Yes", "YES")
}

function Resolve-MssqlTimingShard {
    param(
        [System.IO.FileInfo]
        $File,

        [psobject]
        $Record
    )

    $shard = Get-CsvValue -Record $Record -Name "Shard"
    if (-not [string]::IsNullOrWhiteSpace($shard)) {
        return $shard
    }

    if ($File.FullName -match "mssql-shard-(?<shard>[^\\/]+)") {
        return $Matches["shard"]
    }

    if ($File.FullName -match "mssql-api") {
        return "api"
    }

    ""
}

function ConvertFrom-MssqlFixtureTimingFile {
    param(
        [System.IO.FileInfo]
        $TimingFile
    )

    Import-Csv -LiteralPath $TimingFile.FullName | ForEach-Object {
        $phase = Get-CsvValue -Record $_ -Name "Phase" -Default "create-provisioned"
        $leaseStrategy = Get-CsvValue -Record $_ -Name "LeaseStrategy" -Default "direct"

        [pscustomobject]@{
            SourceFile = $TimingFile.FullName
            SourceFileName = $TimingFile.Name
            TimestampUtc = Get-CsvValue -Record $_ -Name "TimestampUtc"
            Outcome = Get-CsvValue -Record $_ -Name "Outcome"
            DurationSeconds = ConvertTo-MssqlTimingDurationSeconds -Value (Get-CsvValue -Record $_ -Name "DurationSeconds" -Default "0")
            DatabaseName = Get-CsvValue -Record $_ -Name "DatabaseName"
            CommandTimeoutSeconds = Get-CsvValue -Record $_ -Name "CommandTimeoutSeconds"
            FixtureSignature = Get-CsvValue -Record $_ -Name "FixtureSignature"
            GeneratedDdlHash = Get-CsvValue -Record $_ -Name "GeneratedDdlHash"
            Phase = $phase
            LeaseStrategy = $leaseStrategy
            Shard = Resolve-MssqlTimingShard -File $TimingFile -Record $_
            TestWorkerId = Get-CsvValue -Record $_ -Name "TestWorkerId"
            CallerMemberName = Get-CsvValue -Record $_ -Name "CallerMemberName"
            CallerFilePath = Get-CsvValue -Record $_ -Name "CallerFilePath"
            CallerLineNumber = Get-CsvValue -Record $_ -Name "CallerLineNumber"
            IsDiagnostic = ConvertTo-MssqlTimingBoolean -Value (Get-CsvValue -Record $_ -Name "IsDiagnostic")
            Detail = Get-CsvValue -Record $_ -Name "Detail"
            BatchOrdinal = Get-CsvValue -Record $_ -Name "BatchOrdinal"
            BatchCount = Get-CsvValue -Record $_ -Name "BatchCount"
            BatchHash = Get-CsvValue -Record $_ -Name "BatchHash"
        }
    }
}

function Format-MssqlFixtureTimingMarkdown {
    param(
        [object[]]
        $Results,

        [int]
        $MaxSlowGroups
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $primaryResults = @($Results | Where-Object { -not $_.IsDiagnostic })
    $diagnosticResults = @($Results | Where-Object { $_.IsDiagnostic })
    $totalSeconds = ($primaryResults | Measure-Object -Property DurationSeconds -Sum).Sum
    if ($null -eq $totalSeconds) {
        $totalSeconds = 0
    }

    $failedCount = @($primaryResults | Where-Object { $_.Outcome -ne "Succeeded" }).Count
    $diagnosticFailedCount = @($diagnosticResults | Where-Object { $_.Outcome -ne "Succeeded" }).Count

    $lines.Add("## MSSQL Fixture Setup Timings")
    $lines.Add("")
    $lines.Add("| Metric | Value |")
    $lines.Add("| --- | ---: |")
    $lines.Add("| Timing rows | $($Results.Count) |")
    $lines.Add("| Diagnostic rows | $($diagnosticResults.Count) |")
    $lines.Add("| Failed rows | $failedCount |")
    $lines.Add("| Failed diagnostic rows | $diagnosticFailedCount |")
    $lines.Add("| Total recorded duration | $(Format-Duration -Seconds $totalSeconds) |")
    $lines.Add("")

    $phaseTotals = $primaryResults |
        Group-Object -Property Shard, Phase |
        ForEach-Object {
            $first = $_.Group | Select-Object -First 1
            $groupTotalSeconds = ($_.Group | Measure-Object -Property DurationSeconds -Sum).Sum
            $groupFailures = @($_.Group | Where-Object { $_.Outcome -ne "Succeeded" }).Count

            [pscustomobject]@{
                Shard = $first.Shard
                Phase = $first.Phase
                Count = $_.Count
                FailedCount = $groupFailures
                TotalSeconds = [Math]::Round($groupTotalSeconds, 3)
            }
        } |
        Sort-Object -Property TotalSeconds -Descending

    $lines.Add("### Phase Totals")
    $lines.Add("")
    $lines.Add("| Total | Rows | Failures | Shard | Phase |")
    $lines.Add("| ---: | ---: | ---: | --- | --- |")
    foreach ($phaseTotal in $phaseTotals) {
        $lines.Add("| $(Format-Duration -Seconds $phaseTotal.TotalSeconds) | $($phaseTotal.Count) | $($phaseTotal.FailedCount) | $(Format-MarkdownText -Value $phaseTotal.Shard) | $(Format-MarkdownText -Value $phaseTotal.Phase) |")
    }

    if (@($phaseTotals).Count -eq 0) {
        $lines.Add("| 0:00.000 | 0 | 0 | | No MSSQL fixture timing data found |")
    }

    $lines.Add("")
    $lines.Add("### Slowest Setup Groups")
    $lines.Add("")
    $lines.Add("| Total | Rows | Failures | Shard | Phase | Fixture Signature | Caller File |")
    $lines.Add("| ---: | ---: | ---: | --- | --- | --- | --- |")

    $slowGroups = $primaryResults |
        Group-Object -Property Shard, FixtureSignature, CallerFilePath, Phase |
        ForEach-Object {
            $first = $_.Group | Select-Object -First 1
            $groupTotalSeconds = ($_.Group | Measure-Object -Property DurationSeconds -Sum).Sum
            $groupFailures = @($_.Group | Where-Object { $_.Outcome -ne "Succeeded" }).Count

            [pscustomobject]@{
                Shard = $first.Shard
                FixtureSignature = if ([string]::IsNullOrWhiteSpace($first.FixtureSignature)) { "(unknown)" } else { $first.FixtureSignature }
                CallerFilePath = if ([string]::IsNullOrWhiteSpace($first.CallerFilePath)) { "(unknown)" } else { $first.CallerFilePath }
                Phase = $first.Phase
                Count = $_.Count
                FailedCount = $groupFailures
                TotalSeconds = [Math]::Round($groupTotalSeconds, 3)
            }
        } |
        Sort-Object -Property TotalSeconds -Descending |
        Select-Object -First $MaxSlowGroups

    foreach ($group in $slowGroups) {
        $lines.Add("| $(Format-Duration -Seconds $group.TotalSeconds) | $($group.Count) | $($group.FailedCount) | $(Format-MarkdownText -Value $group.Shard) | $(Format-MarkdownText -Value $group.Phase) | $(Format-MarkdownText -Value $group.FixtureSignature) | $(Format-MarkdownText -Value $group.CallerFilePath) |")
    }

    if (@($slowGroups).Count -eq 0) {
        $lines.Add("| 0:00.000 | 0 | 0 | | | No MSSQL fixture timing data found | |")
    }

    if ($diagnosticResults.Count -ne 0) {
        $lines.Add("")
        $lines.Add("### Slowest Diagnostic Rows")
        $lines.Add("")
        $lines.Add("| Duration | Outcome | Shard | Phase | Batch | Batch Hash | Fixture Signature | Caller File |")
        $lines.Add("| ---: | --- | --- | --- | ---: | --- | --- | --- |")

        $slowDiagnosticRows = $diagnosticResults |
            Sort-Object -Property DurationSeconds -Descending |
            Select-Object -First $MaxSlowGroups

        foreach ($row in $slowDiagnosticRows) {
            $batch = if ([string]::IsNullOrWhiteSpace($row.BatchOrdinal)) { "" } else { "$($row.BatchOrdinal)/$($row.BatchCount)" }
            $lines.Add("| $(Format-Duration -Seconds $row.DurationSeconds) | $(Format-MarkdownText -Value $row.Outcome) | $(Format-MarkdownText -Value $row.Shard) | $(Format-MarkdownText -Value $row.Phase) | $(Format-MarkdownText -Value $batch) | $(Format-MarkdownText -Value $row.BatchHash) | $(Format-MarkdownText -Value $row.FixtureSignature) | $(Format-MarkdownText -Value $row.CallerFilePath) |")
        }
    }

    $lines -join [Environment]::NewLine
}

function Write-MssqlFixtureTimingSummary {
    param(
        [object[]]
        $Results,

        [string]
        $OutputDirectory,

        [int]
        $MaxSlowGroups,

        [switch]
        $AppendToGitHubStepSummary
    )

    if ($Results.Count -eq 0) {
        return
    }

    $normalizedPath = Join-Path $OutputDirectory "mssql-fixture-setup-timings-normalized.csv"
    $summaryCsvPath = Join-Path $OutputDirectory "mssql-fixture-setup-summary.csv"
    $summaryJsonPath = Join-Path $OutputDirectory "mssql-fixture-setup-summary.json"
    $summaryMarkdownPath = Join-Path $OutputDirectory "mssql-fixture-setup-summary.md"

    $sortedResults = @($Results | Sort-Object -Property DurationSeconds -Descending)
    $sortedResults |
        Export-Csv -LiteralPath $normalizedPath -NoTypeInformation -Encoding utf8

    $summaryResults = @($Results | Where-Object { -not $_.IsDiagnostic })
    $summary = @(
        $summaryResults |
            Group-Object -Property Shard, FixtureSignature, CallerFilePath, Phase |
            ForEach-Object {
                $first = $_.Group | Select-Object -First 1
                $groupTotalSeconds = ($_.Group | Measure-Object -Property DurationSeconds -Sum).Sum
                $groupFailures = @($_.Group | Where-Object { $_.Outcome -ne "Succeeded" }).Count

                [pscustomobject]@{
                    Shard = $first.Shard
                    FixtureSignature = $first.FixtureSignature
                    CallerFilePath = $first.CallerFilePath
                    Phase = $first.Phase
                    LeaseStrategy = $first.LeaseStrategy
                    Count = $_.Count
                    FailedCount = $groupFailures
                    TotalSeconds = [Math]::Round($groupTotalSeconds, 3)
                }
            } |
            Sort-Object -Property TotalSeconds -Descending
    )

    $summary |
        Export-Csv -LiteralPath $summaryCsvPath -NoTypeInformation -Encoding utf8

    ConvertTo-Json -InputObject $summary -Depth 5 |
        Set-Content -LiteralPath $summaryJsonPath -Encoding utf8

    $markdown = Format-MssqlFixtureTimingMarkdown -Results $Results -MaxSlowGroups $MaxSlowGroups
    $markdown | Set-Content -LiteralPath $summaryMarkdownPath -Encoding utf8

    if ($AppendToGitHubStepSummary -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        $markdown | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }

    Write-Output "Parsed $($Results.Count) MSSQL fixture setup timing row(s)."
    Write-Output "MSSQL fixture timing normalized CSV: $normalizedPath"
    Write-Output "MSSQL fixture timing summary CSV: $summaryCsvPath"
    Write-Output "MSSQL fixture timing summary JSON: $summaryJsonPath"
    Write-Output "MSSQL fixture timing summary Markdown: $summaryMarkdownPath"
}

function ConvertFrom-TrxFile {
    param(
        [System.IO.FileInfo]
        $TrxFile,

        [string]
        $Suite
    )

    [xml]$trx = Get-Content -LiteralPath $TrxFile.FullName -Raw
    $namespaceManager = Get-TrxNamespaceManager -Document $trx
    $definitions = @{}

    $unitTestNodes = Select-TrxNode `
        -Document $trx `
        -NamespaceManager $namespaceManager `
        -NamespacedXPath "//trx:TestDefinitions/trx:UnitTest" `
        -FallbackXPath "//TestDefinitions/UnitTest"

    foreach ($unitTestNode in $unitTestNodes) {
        $testId = $unitTestNode.GetAttribute("id")
        if ([string]::IsNullOrWhiteSpace($testId)) {
            continue
        }

        $testMethodNode = Select-TrxSingleNode `
            -Node $unitTestNode `
            -NamespaceManager $namespaceManager `
            -NamespacedXPath "trx:TestMethod" `
            -FallbackXPath "TestMethod"

        $definitions[$testId] = [pscustomobject]@{
            DefinitionName = $unitTestNode.GetAttribute("name")
            ClassName = if ($null -eq $testMethodNode) { "" } else { $testMethodNode.GetAttribute("className") }
            MethodName = if ($null -eq $testMethodNode) { "" } else { $testMethodNode.GetAttribute("name") }
        }
    }

    $resultNodes = Select-TrxNode `
        -Document $trx `
        -NamespaceManager $namespaceManager `
        -NamespacedXPath "//trx:Results/trx:UnitTestResult" `
        -FallbackXPath "//Results/UnitTestResult"

    foreach ($resultNode in $resultNodes) {
        $testId = $resultNode.GetAttribute("testId")
        $definition = if ($definitions.ContainsKey($testId)) { $definitions[$testId] } else { $null }
        $duration = ConvertTo-TestDuration -Duration $resultNode.GetAttribute("duration")
        $className = if ($null -eq $definition) { "" } else { $definition.ClassName }
        $methodName = if ($null -eq $definition) { "" } else { $definition.MethodName }
        $testName = $resultNode.GetAttribute("testName")

        if ([string]::IsNullOrWhiteSpace($testName) -and $null -ne $definition) {
            $testName = $definition.DefinitionName
        }

        [pscustomobject]@{
            SuiteName = $Suite
            SourceFile = $TrxFile.FullName
            SourceFileName = $TrxFile.Name
            TestId = $testId
            ExecutionId = $resultNode.GetAttribute("executionId")
            Outcome = $resultNode.GetAttribute("outcome")
            DurationSeconds = [Math]::Round($duration.TotalSeconds, 3)
            Duration = $duration.ToString("c", [System.Globalization.CultureInfo]::InvariantCulture)
            ClassName = $className
            MethodName = $methodName
            TestName = $testName
        }
    }
}

function Format-TimingMarkdown {
    param(
        [object[]]
        $Results,

        [string]
        $Title,

        [int]
        $MaxSlowTests,

        [int]
        $MaxSlowFixtures
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $totalSeconds = ($Results | Measure-Object -Property DurationSeconds -Sum).Sum
    if ($null -eq $totalSeconds) {
        $totalSeconds = 0
    }

    $averageSeconds = if ($Results.Count -eq 0) { 0 } else { $totalSeconds / $Results.Count }
    $passedCount = @($Results | Where-Object { $_.Outcome -eq "Passed" }).Count
    $failedCount = @($Results | Where-Object { $_.Outcome -eq "Failed" }).Count
    $skippedCount = @($Results | Where-Object { $_.Outcome -in @("NotExecuted", "Skipped") }).Count

    $lines.Add("## $Title")
    $lines.Add("")
    $lines.Add("| Metric | Value |")
    $lines.Add("| --- | ---: |")
    $lines.Add("| Test count | $($Results.Count) |")
    $lines.Add("| Passed | $passedCount |")
    $lines.Add("| Failed | $failedCount |")
    $lines.Add("| Skipped | $skippedCount |")
    $lines.Add("| Total test duration | $(Format-Duration -Seconds $totalSeconds) |")
    $lines.Add("| Average test duration | $(Format-Duration -Seconds $averageSeconds) |")
    $lines.Add("")

    $slowFixtures = $Results |
        Group-Object -Property ClassName |
        ForEach-Object {
            $fixtureTotalSeconds = ($_.Group | Measure-Object -Property DurationSeconds -Sum).Sum
            $slowestTest = $_.Group | Sort-Object -Property DurationSeconds -Descending | Select-Object -First 1

            [pscustomobject]@{
                ClassName = if ([string]::IsNullOrWhiteSpace($_.Name)) { "(unknown fixture)" } else { $_.Name }
                Count = $_.Count
                TotalSeconds = [Math]::Round($fixtureTotalSeconds, 3)
                SlowestTestSeconds = $slowestTest.DurationSeconds
                SlowestTest = $slowestTest.TestName
            }
        } |
        Sort-Object -Property TotalSeconds -Descending |
        Select-Object -First $MaxSlowFixtures

    $lines.Add("### Slowest Fixtures")
    $lines.Add("")
    $lines.Add("| Total | Tests | Slowest Test | Fixture |")
    $lines.Add("| ---: | ---: | ---: | --- |")
    foreach ($fixture in $slowFixtures) {
        $lines.Add("| $(Format-Duration -Seconds $fixture.TotalSeconds) | $($fixture.Count) | $(Format-Duration -Seconds $fixture.SlowestTestSeconds) | $(Format-MarkdownText -Value $fixture.ClassName) |")
    }

    if (@($slowFixtures).Count -eq 0) {
        $lines.Add("| 0:00.000 | 0 | 0:00.000 | No test timing data found |")
    }

    $lines.Add("")
    $lines.Add("### Slowest Tests")
    $lines.Add("")
    $lines.Add("| Duration | Outcome | Test | Fixture | Source |")
    $lines.Add("| ---: | --- | --- | --- | --- |")

    $slowTests = $Results |
        Sort-Object -Property DurationSeconds -Descending |
        Select-Object -First $MaxSlowTests

    foreach ($test in $slowTests) {
        $lines.Add("| $(Format-Duration -Seconds $test.DurationSeconds) | $(Format-MarkdownText -Value $test.Outcome) | $(Format-MarkdownText -Value $test.TestName) | $(Format-MarkdownText -Value $test.ClassName) | $(Format-MarkdownText -Value $test.SourceFileName) |")
    }

    if (@($slowTests).Count -eq 0) {
        $lines.Add("| 0:00.000 | | No test timing data found | | |")
    }

    $lines -join [Environment]::NewLine
}

$trxFiles = @(Resolve-TrxFile -CandidatePaths $Path)

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$mssqlTimingFiles = @(Resolve-MssqlFixtureTimingFile -CandidatePaths ($Path + @($OutputDirectory)))
$mssqlTimingResults = @(
    foreach ($mssqlTimingFile in $mssqlTimingFiles) {
        ConvertFrom-MssqlFixtureTimingFile -TimingFile $mssqlTimingFile
    }
)

if ($trxFiles.Count -eq 0) {
    $message = "No TRX files found for timing summary. Searched: $($Path -join ', ')"
    Write-Warning $message

    $markdown = "## $SuiteName`n`n$message"
    $markdownPath = Join-Path $OutputDirectory "test-timings.md"
    $markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8

    if ($AppendToGitHubStepSummary -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        $markdown | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }

    Write-MssqlFixtureTimingSummary `
        -Results $mssqlTimingResults `
        -OutputDirectory $OutputDirectory `
        -MaxSlowGroups $SlowFixtureCount `
        -AppendToGitHubStepSummary:$AppendToGitHubStepSummary

    exit 0
}

$results = @(
    foreach ($trxFile in $trxFiles) {
        ConvertFrom-TrxFile -TrxFile $trxFile -Suite $SuiteName
    }
)

$csvPath = Join-Path $OutputDirectory "test-timings.csv"
$jsonPath = Join-Path $OutputDirectory "test-timings.json"
$markdownPath = Join-Path $OutputDirectory "test-timings.md"

$sortedResults = @($results | Sort-Object -Property DurationSeconds -Descending)

$sortedResults |
    Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8

ConvertTo-Json -InputObject $sortedResults -Depth 5 |
    Set-Content -LiteralPath $jsonPath -Encoding utf8

$markdown = Format-TimingMarkdown `
    -Results $results `
    -Title $SuiteName `
    -MaxSlowTests $SlowTestCount `
    -MaxSlowFixtures $SlowFixtureCount

$markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8

if ($AppendToGitHubStepSummary -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $markdown | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}

Write-MssqlFixtureTimingSummary `
    -Results $mssqlTimingResults `
    -OutputDirectory $OutputDirectory `
    -MaxSlowGroups $SlowFixtureCount `
    -AppendToGitHubStepSummary:$AppendToGitHubStepSummary

Write-Output "Parsed $($results.Count) test result(s) from $($trxFiles.Count) TRX file(s)."
Write-Output "Timing CSV: $csvPath"
Write-Output "Timing JSON: $jsonPath"
Write-Output "Timing Markdown: $markdownPath"
