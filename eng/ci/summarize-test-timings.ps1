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

function Resolve-TrxFiles {
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

function New-TrxNamespaceManager {
    param(
        [xml]
        $Document
    )

    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($Document.NameTable)
    $namespaceManager.AddNamespace("trx", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010") | Out-Null
    return ,$namespaceManager
}

function Select-TrxNodes {
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

function ConvertFrom-TrxFile {
    param(
        [System.IO.FileInfo]
        $TrxFile,

        [string]
        $Suite
    )

    [xml]$trx = Get-Content -LiteralPath $TrxFile.FullName -Raw
    $namespaceManager = New-TrxNamespaceManager -Document $trx
    $definitions = @{}

    $unitTestNodes = Select-TrxNodes `
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

    $resultNodes = Select-TrxNodes `
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

function New-TimingMarkdown {
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

$trxFiles = @(Resolve-TrxFiles -CandidatePaths $Path)

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

if ($trxFiles.Count -eq 0) {
    $message = "No TRX files found for timing summary. Searched: $($Path -join ', ')"
    Write-Warning $message

    $markdown = "## $SuiteName`n`n$message"
    $markdownPath = Join-Path $OutputDirectory "test-timings.md"
    $markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8

    if ($AppendToGitHubStepSummary -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        $markdown | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }

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

$markdown = New-TimingMarkdown `
    -Results $results `
    -Title $SuiteName `
    -MaxSlowTests $SlowTestCount `
    -MaxSlowFixtures $SlowFixtureCount

$markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8

if ($AppendToGitHubStepSummary -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $markdown | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}

Write-Output "Parsed $($results.Count) test result(s) from $($trxFiles.Count) TRX file(s)."
Write-Output "Timing CSV: $csvPath"
Write-Output "Timing JSON: $jsonPath"
Write-Output "Timing Markdown: $markdownPath"
