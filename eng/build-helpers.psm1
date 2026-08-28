# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

function Invoke-RegenerateFile {
    param (
        [string]
        $Path,

        [string]
        $NewContent
    )

    $oldContent = Get-Content -Path $Path

    if ($new_content -ne $oldContent) {
        $relative_path = Resolve-Path -Relative $Path
        Write-Command "Generating $relative_path"
        [System.IO.File]::WriteAllText($Path, $NewContent, [System.Text.Encoding]::UTF8)
    }
}

function Invoke-Execute {
    param (
        [ScriptBlock]
        $Command
    )

    $global:lastexitcode = 0
    Invoke-Command -ScriptBlock $Command | Out-Host

    if ($lastexitcode -ne 0) {
        throw "Error executing command: $Command"
    }
}

function Invoke-Step {
    param (
        [ScriptBlock]
        $block
    )

    $command = $block.ToString().Trim()

    Write-NewLine
    Write-Command $command

    &$block
}

function Invoke-Main {
    param (
        [ScriptBlock]
        $MainBlock
    )

    try {
        &$MainBlock
        Write-NewLine
        Write-Success "Build Succeeded"
        exit 0
    } catch [Exception] {
        Write-NewLine
        Write-Error $_.Exception.Message
        Write-NewLine
        Write-Error "Build Failed"
        exit 1
    }
}

<#
    .DESCRIPTION
    Resolve the built test assemblies for a test target, failing when the glob matches nothing.

    An empty match means the target would execute zero tests, and a target that executes zero tests
    must never report success: there would be no signal separating "everything passed" from "nothing
    ran". The glob is easy to miss (assemblies built under a different -Configuration, a renamed test
    project, a changed directory layout, or a skipped build step), so this is a hard failure.
#>
function Get-RequiredTestAssembly {
    param (
        # Root of the solution whose test projects are being searched
        [string]
        $SolutionRoot,

        # File search filter, e.g. "*.Tests.Unit"
        [string]
        $Filter,

        # .NET build configuration the assemblies were built under
        [string]
        $Configuration
    )

    $testAssemblyPath = "$SolutionRoot/*/$Filter/bin/$Configuration/"

    # @() so the emptiness test below is a count. Get-ChildItem returns a bare FileInfo when exactly
    # one file matches, and FileInfo.Length is the file's size in bytes, not an item count.
    $testAssemblies = @(Get-ChildItem -Path $testAssemblyPath -Filter "$Filter.dll" -Recurse)

    if ($testAssemblies.Count -eq 0) {
        throw "no test assemblies found in $testAssemblyPath. Nothing matching '$Filter' was built for configuration '$Configuration', so this target would run zero tests."
    }

    return $testAssemblies
}

<#
    .DESCRIPTION
    Display a command and its arguments on the console
#>
function Write-Command($message){
    Write-MessageColorOutput CYAN $message
}

<#
    .DESCRIPTION
    Display a command and its arguments on the console
#>
function Write-Success($message){
    Write-MessageColorOutput GREEN $message
}

<#
    .DESCRIPTION
    Display a command and its arguments on the console
#>
function Write-Info($message){
    Write-MessageColorOutput YELLOW $message
}

<#
    .DESCRIPTION
    Add a new break line in the console
#>
function Write-NewLine(){
    Write-MessageColorOutput WHITE "`n"
}

<#
    .DESCRIPTION
    Writes a message to the output with a specified text color.
#>
function Write-MessageColorOutput
{
    param(
        [ValidateSet("Black","DarkBlue","DarkGreen","DarkCyan","DarkRed","DarkMagenta",
        "DarkYellow","Gray","DarkGray","Blue","Green","Cyan","Red","Magenta","Yellow","White",
        ErrorMessage="Please specify a valid color name from the list.",
        IgnoreCase=$true)]
        [String]
        $ForegroundColor
    )

    # save the current color
    $fc = $host.UI.RawUI.ForegroundColor

    # set the new color
    $host.UI.RawUI.ForegroundColor = $ForegroundColor

    # output
    if ($args) {
        Write-Output $args
    }
    else {
        $input | Write-Output
    }

    # restore the original color
    $host.UI.RawUI.ForegroundColor = $fc
}

<#
    .DESCRIPTION
    Resolve the test projects a unit-test run must cover, failing rather than reporting an empty
    glob as success - the same rule Get-RequiredTestAssembly applies to assemblies.
#>
function Get-RequiredUnitTestProject {
    param (
        # Root of the solution whose test projects are being searched
        [string]
        $SolutionRoot,

        # Project directory filter, e.g. "*.Tests.Unit"
        [string]
        $Filter
    )

    # The wildcard carries the file pattern rather than a -Filter argument: a -Filter combined with
    # a wildcard directory path silently matches nothing here, which would read as "no unit tests".
    $projectPath = "$SolutionRoot/*/$Filter/$Filter.csproj"

    $projects = @(Get-ChildItem -Path $projectPath)

    if ($projects.Count -eq 0) {
        throw "no test projects found in $projectPath. Nothing matching '$Filter' exists, so this target would run zero tests."
    }

    return $projects
}

<#
    .DESCRIPTION
    Build the contents of a solution filter (.slnf). SolutionPath is relative to the filter file;
    each ProjectPath is relative to the solution file, matching how the solution itself records them.
#>
function ConvertTo-SolutionFilterContent {
    param (
        [string]
        $SolutionPath,

        [string[]]
        $ProjectPath
    )

    return [pscustomobject]@{
        solution = [pscustomobject]@{
            path     = $SolutionPath
            projects = @($ProjectPath)
        }
    } | ConvertTo-Json -Depth 4
}

<#
    .DESCRIPTION
    Enforce a total line and branch coverage threshold against a Cobertura report, reproducing
    coverlet's --threshold-type line --threshold-type branch --threshold-stat total. The rates are
    read as XML attributes rather than matched out of the file's text, and parsed with the invariant
    culture: on a comma-decimal machine, culture-sensitive parsing turns 0.61 into 61 and the gate
    passes no matter what was measured. Returns the measured percentages so a caller can report them.
#>
function Assert-CoverageThreshold {
    param (
        # Path to a Cobertura coverage report
        [string]
        $Path,

        # Minimum acceptable percentage, applied to line and branch totals independently
        [int]
        $Threshold
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Coverage report '$Path' was not produced, so the $Threshold% threshold cannot be enforced. Treating a missing report as a pass would silently disable the coverage gate."
    }

    try {
        [xml] $report = Get-Content -LiteralPath $Path -Raw
    }
    catch {
        throw "Coverage report '$Path' is not valid XML, so the $Threshold% threshold cannot be enforced: $($_.Exception.Message)"
    }

    # Deliberately not $report.coverage: ReportGenerator writes a
    # <!DOCTYPE coverage SYSTEM ...> declaration ahead of the root element, and the dotted XML
    # adapter then matches the DocumentType node as well and hands back both. DocumentElement names
    # the root and only the root.
    $coverage = $report.DocumentElement

    if ($null -eq $coverage -or $coverage.Name -ne 'coverage') {
        throw "Coverage report '$Path' has no <coverage> root element, so the $Threshold% threshold cannot be enforced."
    }

    $measured = [ordered]@{}

    foreach ($rateName in @('line-rate', 'branch-rate')) {
        $rawRate = $coverage.GetAttribute($rateName)

        if ([string]::IsNullOrWhiteSpace($rawRate)) {
            throw "Coverage report '$Path' does not declare $rateName, so the $Threshold% threshold cannot be enforced."
        }

        $parsedRate = 0.0
        $parsed = [double]::TryParse(
            $rawRate,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref] $parsedRate
        )

        if (-not $parsed) {
            throw "Coverage report '$Path' declares $rateName as '$rawRate', which is not a number, so the $Threshold% threshold cannot be enforced."
        }

        # Held as the raw rate, unrounded. Rounding to a display percentage before the comparison
        # opens a false-pass window just under the gate: 0.57995 is 57.995%, which rounds to 58.00
        # and would pass a threshold it is below.
        $measured[$rateName] = $parsedRate
    }

    # Compared as rates rather than percentages. Scaling to a percentage first makes an
    # exactly-at-threshold report fail, because 0.58 * 100 is 57.99999999999999 as a double, while
    # 0.58 and 58 / 100.0 are the same double. Comparing rates is what keeps the boundary exact in
    # both directions.
    $thresholdRate = $Threshold / 100.0

    $below = @(
        $measured.Keys | Where-Object { $measured[$_] -lt $thresholdRate }
    )

    if ($below.Count -gt 0) {
        $detail = ($measured.Keys | ForEach-Object { "$_ $([math]::Round($measured[$_] * 100, 2))%" }) -join ', '
        throw "Coverage is below the $Threshold% threshold for $($below -join ' and '). Measured: $detail."
    }

    return [pscustomobject]@{
        LinePercentage   = [math]::Round($measured['line-rate'] * 100, 2)
        BranchPercentage = [math]::Round($measured['branch-rate'] * 100, 2)
        Threshold        = $Threshold
    }
}

Export-ModuleMember -Function *
