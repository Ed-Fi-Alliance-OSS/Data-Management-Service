# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# The checks both collection lanes rely on, kept here so that each can be exercised with synthetic
# input (collection.Tests.ps1) without a deployment. Every check throws on failure; a lane turns a
# throw into a recorded failure and a nonzero exit. The power reader and power condition P are here
# too; a lane decides what an unmet condition means at each of its checks.

Set-StrictMode -Version Latest

# The Configuration Service production assemblies each lane fingerprints.
$script:FingerprintedAssemblies = @(
    'EdFi.DmsConfigurationService.Frontend.AspNetCore.dll',
    'EdFi.DmsConfigurationService.Backend.dll',
    'EdFi.DmsConfigurationService.Backend.OpenIddict.dll',
    'EdFi.DmsConfigurationService.Backend.Postgresql.dll',
    'EdFi.DmsConfigurationService.Backend.Mssql.dll'
)

# The exact settings a run records, and the value each must have for the pre-registered method.
$script:SettingsAllowlist = @(
    'AppSettings__Datastore',
    'AppSettings__IdentityProvider',
    'AppSettings__MultiTenancy',
    'IdentitySettings__ClientSecretHashingIterations',
    'IdentitySettings__AllowRegistration'
)

# The explicit collector fixture and the tests it must report as run and passed.
$script:CollectorFixture = 'Given_token_endpoint_requests_measured_for_timing_evidence'
$script:CollectorTests = @('It_records_one_sample_per_arm_per_round', 'It_writes_the_run_header')

# Power condition P requires this scheme, the built-in Balanced plan.
$script:BalancedSchemeGuid = '381b4222-f694-41f0-9685-ff5bb260df2e'

# The fields of a power reading, the only ones a check or refusal record copies.
$script:PowerReadingFields = @(
    'utc',
    'call_succeeded',
    'win32_error',
    'ac_line_status',
    'battery_flag',
    'battery_life_percent',
    'system_status_flag',
    'scheme_query_succeeded',
    'scheme_query_failure',
    'scheme_guid'
)

# GetSystemPowerStatus, with the last error read inside the same call, before any other native
# operation can replace it. The guard keeps a repeated module import from defining the type again.
if (-not ('DmsTimingEvidence.SystemPower' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;

namespace DmsTimingEvidence;

public sealed class SystemPowerReading
{
    public bool Succeeded { get; init; }
    public int Win32Error { get; init; }
    public byte ACLineStatus { get; init; }
    public byte BatteryFlag { get; init; }
    public byte BatteryLifePercent { get; init; }
    public byte SystemStatusFlag { get; init; }
}

public static class SystemPower
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static SystemPowerReading Read()
    {
        bool succeeded = GetSystemPowerStatus(out SystemPowerStatus status);
        int error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        return new SystemPowerReading
        {
            Succeeded = succeeded,
            Win32Error = error,
            ACLineStatus = status.ACLineStatus,
            BatteryFlag = status.BatteryFlag,
            BatteryLifePercent = status.BatteryLifePercent,
            SystemStatusFlag = status.SystemStatusFlag,
        };
    }
}
'@
}

<#
.SYNOPSIS
    The names of the Configuration Service production assemblies each lane fingerprints.
#>
function Get-FingerprintedAssemblyName { $script:FingerprintedAssemblies }

<#
.SYNOPSIS
    The exact settings each lane records and checks.
#>
function Get-SettingAllowlist { $script:SettingsAllowlist }

<#
.SYNOPSIS
    The explicit collector fixture a lane runs.
#>
function Get-CollectorFixtureName { $script:CollectorFixture }

<#
.SYNOPSIS
    The full, normalized form of a folder, as both the root checks and the later file operations use it.
.DESCRIPTION
    Resolves a relative path against the current PowerShell location, as New-Item and Join-Path do,
    and removes `.` and `..` segments and trailing separators; GetFullPath also expands the 8.3
    short names of existing segments. Refuses any existing segment that is a reparse point (a
    junction or symbolic link), because it lets two different strings name the same folder and would
    defeat the containment checks.
#>
function Resolve-LanePath {
    param([Parameter(Mandatory)] [string] $Path)
    $full = [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) { $full = $full.TrimEnd('\', '/') }
    $segment = $full
    while ($segment.Length -gt $root.Length) {
        if (([System.IO.Directory]::Exists($segment) -or [System.IO.File]::Exists($segment)) -and
            [System.IO.File]::GetAttributes($segment).HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
            throw "'$Path' passes through a junction or symbolic link at '$segment'."
        }
        $segment = [System.IO.Path]::GetDirectoryName($segment)
    }
    $full
}

function Test-PathWithin([string] $Path, [string] $Root) {
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $full = (Resolve-LanePath $Path).TrimEnd($separator) + $separator
    $rootFull = (Resolve-LanePath $Root).TrimEnd($separator) + $separator
    $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)
}

# True when the two folders are the same, or one contains the other.
function Test-PathOverlap([string] $Path, [string] $Other) {
    (Test-PathWithin $Path $Other) -or (Test-PathWithin $Other $Path)
}

<#
.SYNOPSIS
    Refuses roots that would let a run dirty the collector checkout or mix private and public files.
.DESCRIPTION
    The evidence staging folder, the private work folder and the prepared target must each neither
    equal, contain nor lie inside the collector checkout, and the staging folder must neither equal,
    contain nor lie inside the work folder or the target. The work folder and the target may share a
    parent.
#>
function Assert-LaneRoot {
    param(
        [Parameter(Mandatory)] [string] $CollectorRoot,
        [Parameter(Mandatory)] [string] $OutputDir,
        [Parameter(Mandatory)] [string] $WorkRoot,
        [Parameter(Mandatory)] [string] $TargetRoot
    )
    foreach ($entry in @(@('OutputDir', $OutputDir), @('WorkRoot', $WorkRoot), @('TargetRoot', $TargetRoot))) {
        if (Test-PathOverlap $entry[1] $CollectorRoot) { throw "$($entry[0]) must not equal, contain or lie inside the collector checkout." }
    }
    if (Test-PathOverlap $OutputDir $WorkRoot) {
        throw 'OutputDir and WorkRoot must not be the same folder or contain each other.'
    }
    if (Test-PathOverlap $OutputDir $TargetRoot) {
        throw 'OutputDir and TargetRoot must not be the same folder or contain each other.'
    }
}

<#
.SYNOPSIS
    Refuses a folder that equals, contains or lies inside any worktree registered to a repository.
.DESCRIPTION
    Takes the output of `git worktree list --porcelain`, so every worktree of the repository counts,
    not only the one a command was given.
#>
function Assert-OutsideWorktree {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $WorktreeListing
    )
    $worktrees = @($WorktreeListing | Where-Object { $_ -like 'worktree *' } | ForEach-Object { $_.Substring('worktree '.Length) })
    if ($worktrees.Count -eq 0) { throw 'The repository reported no worktrees.' }
    foreach ($worktree in $worktrees) {
        if (Test-PathOverlap $Path $worktree) { throw "$Name must not equal, contain or lie inside the worktree '$worktree'." }
    }
}

<#
.SYNOPSIS
    Parses `sha256sum` output into exactly the expected named fingerprints.
.DESCRIPTION
    Every nonblank line must be a 64-character lowercase hex digest and a file name. The result must
    hold each expected name exactly once and nothing else; anything missing, extra, repeated or
    malformed is a failure.
#>
function ConvertFrom-Sha256Listing {
    param(
        [AllowEmptyCollection()] [string[]] $Lines,
        [string[]] $ExpectedName = $script:FingerprintedAssemblies
    )
    $fingerprints = [ordered]@{}
    foreach ($line in @($Lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        if ($line.Trim() -notmatch '^([0-9a-f]{64}) [ *](\S+)$') { throw 'A fingerprint line is malformed.' }
        $name = Split-Path -Leaf $Matches[2]
        if ($fingerprints.Contains($name)) { throw "A fingerprint for $name appears twice." }
        $fingerprints[$name] = $Matches[1]
    }
    Assert-AssemblyFingerprint -Fingerprint $fingerprints -ExpectedName $ExpectedName
    $fingerprints
}

<#
.SYNOPSIS
    Requires exactly the expected named fingerprints, each a valid SHA-256 digest.
#>
function Assert-AssemblyFingerprint {
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Fingerprint,
        [string[]] $ExpectedName = $script:FingerprintedAssemblies
    )
    $names = @($Fingerprint.Keys)
    $missing = @($ExpectedName | Where-Object { $names -notcontains $_ })
    $extra = @($names | Where-Object { $ExpectedName -notcontains $_ })
    if ($missing.Count -gt 0) { throw "Fingerprints are missing for: $($missing -join ', ')." }
    if ($extra.Count -gt 0) { throw "Unexpected fingerprints for: $($extra -join ', ')." }
    foreach ($name in $ExpectedName) {
        if ([string]$Fingerprint[$name] -notmatch '^[0-9a-f]{64}$') { throw "The fingerprint of $name is not a SHA-256 digest." }
    }
}

<#
.SYNOPSIS
    Requires the pre-registered values of every allowlisted setting.
.DESCRIPTION
    Takes the observed values (setting name to string). A missing value is a failure.
#>
function Assert-RunSetting {
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Value,
        [Parameter(Mandatory)] [ValidateSet('postgresql', 'mssql')] [string] $Datastore
    )
    $expected = [ordered]@{
        AppSettings__Datastore                          = $Datastore
        AppSettings__IdentityProvider                   = 'self-contained'
        AppSettings__MultiTenancy                       = 'false'
        IdentitySettings__ClientSecretHashingIterations = '210000'
        IdentitySettings__AllowRegistration             = 'true'
    }
    foreach ($name in $expected.Keys) {
        if (-not $Value.Contains($name) -or $null -eq $Value[$name]) { throw "$name has no observed value." }
        if (-not [string]::Equals([string]$Value[$name], $expected[$name], [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$name is '$($Value[$name])', not the pre-registered '$($expected[$name])'."
        }
    }
}

<#
.SYNOPSIS
    Lists every difference between two deployment snapshots.
.DESCRIPTION
    Compares the named identity fields, the allowlisted setting values and the assembly fingerprints.
    A value missing on either side is a difference, so two missing values never compare equal.
#>
function Compare-LaneSnapshot {
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Before,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $After,
        [Parameter(Mandatory)] [string[]] $IdentityField
    )
    $differences = [System.Collections.Generic.List[string]]::new()
    $pairs = [System.Collections.Generic.List[object]]::new()
    foreach ($field in $IdentityField) { $pairs.Add(@($field, $Before[$field], $After[$field])) }
    foreach ($name in $script:SettingsAllowlist) { $pairs.Add(@("setting $name", $Before.settings[$name], $After.settings[$name])) }
    foreach ($name in $script:FingerprintedAssemblies) { $pairs.Add(@("assembly $name", $Before.assemblies[$name], $After.assemblies[$name])) }
    foreach ($pair in $pairs) {
        $label, $left, $right = $pair
        if ($null -eq $left -or $null -eq $right -or [string]$left -eq '' -or [string]$right -eq '') {
            $differences.Add("$label is missing")
        }
        elseif ([string]$left -ne [string]$right) {
            $differences.Add("$label changed")
        }
    }
    , $differences.ToArray()
}

<#
.SYNOPSIS
    Requires a collector run that actually ran the explicit fixture and wrote this run's samples.
.DESCRIPTION
    The runner must exit 0; its TRX must report exactly the fixture's two tests, run and passed, and
    nothing else; the output folder must hold exactly one samples CSV and one header JSON for this
    run, written after it started, whose header matches the run and whose CSV has one row per arm per
    round. Returns a summary built only from these checked values, never from runner output.
#>
function Assert-CollectorRun {
    param(
        [Parameter(Mandatory)] [int] $ExitCode,
        [Parameter(Mandatory)] [string] $TrxPath,
        [Parameter(Mandatory)] [string] $OutputDir,
        [Parameter(Mandatory)] [string] $RunLabel,
        [Parameter(Mandatory)] [ValidateSet('postgresql', 'mssql')] [string] $Backend,
        [Parameter(Mandatory)] [int] $Rounds,
        [Parameter(Mandatory)] [int] $Seed,
        [Parameter(Mandatory)] [datetime] $StartedUtc
    )
    if ($ExitCode -ne 0) { throw "The collector exited $ExitCode." }
    if (-not (Test-Path -LiteralPath $TrxPath)) { throw 'The collector wrote no test results.' }
    [xml] $trx = Get-Content -LiteralPath $TrxPath -Raw
    $namespace = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $results = @(Select-Xml -Xml $trx -XPath '//t:UnitTestResult' -Namespace $namespace | ForEach-Object { $_.Node })
    $definitions = @{}
    foreach ($node in @(Select-Xml -Xml $trx -XPath '//t:UnitTest' -Namespace $namespace | ForEach-Object { $_.Node })) {
        $definitions[$node.id] = $node.TestMethod.className
    }
    if ($results.Count -ne $script:CollectorTests.Count) { throw "The collector reported $($results.Count) tests, not $($script:CollectorTests.Count)." }
    foreach ($result in $results) {
        if ($result.outcome -ne 'Passed') { throw "$($result.testName) did not pass." }
        if ($script:CollectorTests -notcontains $result.testName) { throw "Unexpected test $($result.testName)." }
        if ([string]$definitions[$result.testId] -notlike "*$($script:CollectorFixture)") { throw 'A reported test is not the collector fixture.' }
    }
    if (@($results.testName | Sort-Object -Unique).Count -ne $script:CollectorTests.Count) { throw 'A collector test is missing.' }

    # File times come from a coarser clock than Get-Date and can read up to one timer tick (about
    # 16 ms) earlier than a start taken just before the write. One second absorbs that; a run takes
    # minutes, so an earlier run's files stay excluded, and the exactly-one check refuses any extra.
    $notBefore = $StartedUtc.AddSeconds(-1)
    $pattern = "$RunLabel-endpoint-$Backend-*"
    $csv = @(Get-ChildItem -LiteralPath $OutputDir -Filter "$pattern.csv" | Where-Object { $_.LastWriteTimeUtc -ge $notBefore })
    $json = @(Get-ChildItem -LiteralPath $OutputDir -Filter "$pattern.json" | Where-Object { $_.LastWriteTimeUtc -ge $notBefore })
    if ($csv.Count -ne 1 -or $json.Count -ne 1) { throw "Expected one samples file and one header for this run, found $($csv.Count) and $($json.Count)." }
    $header = Get-Content -LiteralPath $json[0].FullName -Raw | ConvertFrom-Json
    if ($header.run_label -ne $RunLabel -or $header.boundary -ne "endpoint-$Backend" -or $header.rounds -ne $Rounds -or $header.seed -ne $Seed) {
        throw 'The run header does not match this run.'
    }
    if ($header.samples_file -ne $csv[0].Name) { throw 'The run header names a different samples file.' }
    $rows = @(Get-Content -LiteralPath $csv[0].FullName).Count - 1
    $expectedRows = $Rounds * @($header.arms).Count
    if ($rows -ne $expectedRows) { throw "The samples file has $rows rows, not $expectedRows." }
    [ordered]@{
        tests_passed = $results.Count
        samples_file = $csv[0].Name
        header_file  = $json[0].Name
        sample_rows  = $rows
    }
}

# The native call alone, so that tests can stand in for it.
function Read-SystemPowerStatus { [DmsTimingEvidence.SystemPower]::Read() }

<#
.SYNOPSIS
    Reads the power source and the active power scheme once.
.DESCRIPTION
    Returns the raw GetSystemPowerStatus fields and the active scheme from
    `powercfg /getactivescheme`. A failed native call leaves every power field null and records its
    Win32 error; a successful call records no error. A failed scheme query (an exception, a nonzero
    exit, or output without exactly one GUID) leaves the scheme null and records a fixed reason code,
    never the command's own output. Nothing here decides condition P; Get-PowerConditionFailure does.
#>
function Get-PowerSource {
    $utc = (Get-Date).ToUniversalTime().ToString('o')
    $native = Read-SystemPowerStatus
    $reading = [ordered]@{
        utc                    = $utc
        call_succeeded         = [bool]$native.Succeeded
        win32_error            = $null
        ac_line_status         = $null
        battery_flag           = $null
        battery_life_percent   = $null
        system_status_flag     = $null
        scheme_query_succeeded = $false
        scheme_query_failure   = $null
        scheme_guid            = $null
    }
    if ($reading.call_succeeded) {
        $reading.ac_line_status = [int]$native.ACLineStatus
        $reading.battery_flag = [int]$native.BatteryFlag
        $reading.battery_life_percent = [int]$native.BatteryLifePercent
        $reading.system_status_flag = [int]$native.SystemStatusFlag
    }
    else {
        $reading.win32_error = [int]$native.Win32Error
    }

    try {
        $global:LASTEXITCODE = 0
        $output = @(& powercfg /getactivescheme 2>$null)
        $exit = $LASTEXITCODE
        $guids = @([regex]::Matches(($output -join "`n"), '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}') | ForEach-Object { $_.Value.ToLowerInvariant() })
        if ($exit -ne 0) { $reading.scheme_query_failure = 'exit_nonzero' }
        elseif ($guids.Count -eq 0) { $reading.scheme_query_failure = 'no_guid' }
        elseif ($guids.Count -gt 1) { $reading.scheme_query_failure = 'several_guids' }
        else {
            $reading.scheme_query_succeeded = $true
            $reading.scheme_guid = $guids[0]
        }
    }
    catch {
        $reading.scheme_query_failure = 'command_error'
    }
    $reading
}

<#
.SYNOPSIS
    The parts of power condition P a reading does not meet; none means P holds.
.DESCRIPTION
    P is: the native call succeeded, ACLineStatus is 1 (online), SystemStatusFlag is 0 (battery saver
    off), the scheme query succeeded, and the scheme is Balanced. Unknown (255) and every failure are
    not P. A missing reading is `observation_error`.
#>
function Get-PowerConditionFailure {
    param([AllowNull()] [System.Collections.IDictionary] $Reading)
    if ($null -eq $Reading) { return , @('observation_error') }
    # Exact types and values only, so that no string or Boolean can pass by loose comparison.
    $unmet = [System.Collections.Generic.List[string]]::new()
    if ($Reading.call_succeeded -isnot [bool] -or -not $Reading.call_succeeded) {
        $unmet.Add('call_failed')
    }
    else {
        $ac = $Reading.ac_line_status
        if ($ac -isnot [int]) { $unmet.Add('ac_line_unexpected') }
        elseif ($ac -eq 0) { $unmet.Add('ac_line_offline') }
        elseif ($ac -eq 255) { $unmet.Add('ac_line_unknown') }
        elseif ($ac -ne 1) { $unmet.Add('ac_line_unexpected') }
        $flag = $Reading.system_status_flag
        if ($flag -isnot [int]) { $unmet.Add('system_status_unexpected') }
        elseif ($flag -eq 1) { $unmet.Add('battery_saver_on') }
        elseif ($flag -ne 0) { $unmet.Add('system_status_unexpected') }
    }
    if ($Reading.scheme_query_succeeded -isnot [bool] -or -not $Reading.scheme_query_succeeded) { $unmet.Add('scheme_query_failed') }
    elseif ($Reading.scheme_guid -isnot [string] -or $Reading.scheme_guid -cne $script:BalancedSchemeGuid) { $unmet.Add('scheme_not_balanced') }
    , $unmet.ToArray()
}

<#
.SYNOPSIS
    One power check of a run: when, which parts of P were unmet, and the reading's own fields.
.DESCRIPTION
    Copies only the reading fields Get-PowerSource defines, so nothing else a reading may carry
    reaches provenance or a refusal record.
#>
function ConvertTo-PowerCheckRecord {
    param(
        [Parameter(Mandatory)] [ValidateSet('before_setup', 'before_launch', 'after_exit')] [string] $Check,
        [AllowNull()] [System.Collections.IDictionary] $Reading
    )
    $copy = $null
    if ($null -ne $Reading) {
        $copy = [ordered]@{}
        foreach ($field in $script:PowerReadingFields) { $copy[$field] = $Reading[$field] }
    }
    $unmet = Get-PowerConditionFailure $copy
    [ordered]@{
        check         = $Check
        utc           = if ($null -ne $copy -and $copy.utc) { $copy.utc } else { (Get-Date).ToUniversalTime().ToString('o') }
        condition_met = $unmet.Count -eq 0
        unmet         = $unmet
        reading       = $copy
    }
}

<#
.SYNOPSIS
    Appends one refusal to the stage's refusal log, as one JSON line. Never rewrites the file.
.DESCRIPTION
    A refusal is a check before the collector launch that did not meet P. It is not an attempt: the
    environment may be corrected and the run started again, and every refusal stays in the log.
#>
function Add-PowerRefusal {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Lane,
        [Parameter(Mandatory)] [string] $RunLabel,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Record
    )
    if ($Record.condition_met) { throw 'A check that met power condition P is not a refusal.' }
    if ($Record.check -eq 'after_exit') { throw 'A check after the collector launch is part of an attempt, not a refusal.' }
    $line = [ordered]@{
        utc       = $Record.utc
        lane      = $Lane
        run_label = $RunLabel
        check     = $Record.check
        unmet     = $Record.unmet
        reading   = $Record.reading
    } | ConvertTo-Json -Compress -Depth 4
    [System.IO.File]::AppendAllText($Path, $line + "`n", [System.Text.UTF8Encoding]::new($false))
}

Export-ModuleMember -Function Get-FingerprintedAssemblyName, Get-SettingAllowlist, Get-CollectorFixtureName, Resolve-LanePath, Assert-LaneRoot, Assert-OutsideWorktree, ConvertFrom-Sha256Listing, Assert-AssemblyFingerprint, Assert-RunSetting, Compare-LaneSnapshot, Assert-CollectorRun, Get-PowerSource, Get-PowerConditionFailure, ConvertTo-PowerCheckRecord, Add-PowerRefusal
