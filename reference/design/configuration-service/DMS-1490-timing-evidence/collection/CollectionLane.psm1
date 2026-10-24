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
    'scheme_query_status',
    'scheme_query_failure',
    'scheme_guid'
)

# The active scheme comes from this read-only native query; it never changes the scheme.
$script:SchemeQueryProvider = 'PowerGetActiveScheme (powrprof.dll)'

# GetSystemPowerStatus, with the last error read inside the same call, before any other native
# operation can replace it; and PowerGetActiveScheme, whose status is its own return value and
# whose GUID buffer the caller owns and releases with LocalFree. The scheme query takes the query,
# the GUID read and the release as delegates, so that tests can drive every status and allocation
# path with memory they allocate, without calling the power API. The guard keeps a repeated module
# import from defining the type again, and refuses an older definition left in the session.
$existing = 'DmsTimingEvidence.SystemPower' -as [type]
if ($null -ne $existing -and $null -eq $existing.GetMethod('ReadActiveScheme')) {
    throw 'An older DmsTimingEvidence.SystemPower is loaded in this session; start a new PowerShell session.'
}
if ($null -eq $existing) {
    Add-Type -TypeDefinition @'
using System;
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

public sealed class SchemeQueryResult
{
    public uint Status { get; init; }
    public IntPtr Buffer { get; init; }
}

public sealed class ActiveSchemeReading
{
    public bool Succeeded { get; init; }
    public uint? Status { get; init; }
    public string Failure { get; init; }
    public string SchemeGuid { get; init; }
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

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

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

    public static SchemeQueryResult QueryActiveScheme()
    {
        IntPtr buffer = IntPtr.Zero;
        uint status = PowerGetActiveScheme(IntPtr.Zero, out buffer);
        return new SchemeQueryResult { Status = status, Buffer = buffer };
    }

    public static Guid ReadSchemeGuid(IntPtr buffer) => Marshal.PtrToStructure<Guid>(buffer);

    // LocalFree returns null when it released the memory.
    public static bool ReleaseSchemeGuid(IntPtr buffer) => LocalFree(buffer) == IntPtr.Zero;

    public static ActiveSchemeReading ReadActiveScheme() =>
        InterpretActiveScheme(QueryActiveScheme, ReadSchemeGuid, ReleaseSchemeGuid);

    // The buffer is owned, and released exactly once, only after a successful query returned it;
    // after a failed query it is not the caller's and is never released. A failed release fails
    // the reading.
    public static ActiveSchemeReading InterpretActiveScheme(
        Func<SchemeQueryResult> query,
        Func<IntPtr, Guid> read,
        Func<IntPtr, bool> release
    )
    {
        SchemeQueryResult result;
        try
        {
            result = query();
        }
        catch (Exception)
        {
            return new ActiveSchemeReading { Failure = "interop_error" };
        }
        if (result is null)
        {
            return new ActiveSchemeReading { Failure = "interop_error" };
        }
        if (result.Status != 0)
        {
            return new ActiveSchemeReading { Status = result.Status, Failure = "status_nonzero" };
        }
        if (result.Buffer == IntPtr.Zero)
        {
            return new ActiveSchemeReading { Status = 0, Failure = "null_result" };
        }
        string failure = null;
        Guid scheme = Guid.Empty;
        try
        {
            scheme = read(result.Buffer);
        }
        catch (Exception)
        {
            failure = "read_error";
        }
        bool released;
        try
        {
            released = release(result.Buffer);
        }
        catch (Exception)
        {
            released = false;
        }
        if (failure is null && !released)
        {
            failure = "release_error";
        }
        if (failure is null && scheme == Guid.Empty)
        {
            failure = "empty_guid";
        }
        if (failure is not null)
        {
            return new ActiveSchemeReading { Status = 0, Failure = failure };
        }
        return new ActiveSchemeReading { Succeeded = true, Status = 0, SchemeGuid = scheme.ToString("D") };
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

# The native calls alone, so that tests can stand in for them.
function Read-SystemPowerStatus { [DmsTimingEvidence.SystemPower]::Read() }
function Read-ActivePowerScheme { [DmsTimingEvidence.SystemPower]::ReadActiveScheme() }

<#
.SYNOPSIS
    The provider of the active-scheme reading, as provenance records it.
#>
function Get-SchemeQueryProvider { $script:SchemeQueryProvider }

<#
.SYNOPSIS
    Reads the power source and the active power scheme once.
.DESCRIPTION
    Returns the raw GetSystemPowerStatus fields and the active scheme from PowerGetActiveScheme,
    a read-only native query that creates no process. A failed power-status call leaves every power
    field null and records its Win32 error; a successful call records no error. The scheme query
    records its own status (the query's return value, never the power-status last error). A failed
    query leaves the scheme null and records a fixed reason code: status_nonzero, null_result,
    read_error, release_error, empty_guid, invalid_result (a GUID not in the normalized form) or
    interop_error. Nothing here decides condition P; Get-PowerConditionFailure does.
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
        scheme_query_status    = $null
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
        $scheme = Read-ActivePowerScheme
        if ($null -ne $scheme.Status) { $reading.scheme_query_status = [long]$scheme.Status }
        if (-not $scheme.Succeeded) {
            $reading.scheme_query_failure = if ([string]::IsNullOrEmpty($scheme.Failure)) { 'interop_error' } else { [string]$scheme.Failure }
        }
        elseif ([string]$scheme.SchemeGuid -cnotmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') {
            $reading.scheme_query_failure = 'invalid_result'
        }
        else {
            $reading.scheme_query_succeeded = $true
            $reading.scheme_guid = [string]$scheme.SchemeGuid
        }
    }
    catch {
        $reading.scheme_query_failure = 'interop_error'
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

# The power sampler (spec section 18.2). A separate process observes the power source once a
# second; the lane requires its readiness before the collector launch and judges its coverage of
# the collection afterwards, on the system-wide QueryPerformanceCounter. Every function below that
# waits or reads the host takes its clock, pause and readers as parameters, so that the tests can
# drive it with synthetic signals instead of sleeps.

# The columns of the power file. Each row is one mandatory power observation. The optional values
# go to a second file, written after the power row of the same tick, so that no optional read can
# hold back a power observation; a blocked read shows up as a gap in the power file instead.
$script:SamplerPowerColumns = @('run_id', 'seq', 'qpc', 'utc', 'observation_error') + @($script:PowerReadingFields | Where-Object { $_ -ne 'utc' })

# The optional host counters: column name, then category, counter and instance.
$script:HostCounters = [ordered]@{
    processor_time_pct        = @('Processor', '% Processor Time', '_Total')
    processor_utility_pct     = @('Processor Information', '% Processor Utility', '_Total')
    processor_performance_pct = @('Processor Information', '% Processor Performance', '_Total')
    processor_frequency_mhz   = @('Processor Information', 'Processor Frequency', '_Total')
    processor_queue_length    = @('System', 'Processor Queue Length', '')
    available_mbytes          = @('Memory', 'Available MBytes', '')
}

# The roles whose CPU the sampler reports. No other process is named in its output.
$script:SamplerRoles = @('cms', 'localdb', 'docker_vm')

function ConvertTo-SamplerField($Value) {
    if ($null -eq $Value) { return '' }
    if ($Value -is [bool]) { return $(if ($Value) { 'true' } else { 'false' }) }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime().ToString('o', [cultureinfo]::InvariantCulture) }
    if ($Value -is [double]) { return $Value.ToString('F3', [cultureinfo]::InvariantCulture) }
    $text = [string]::Format([cultureinfo]::InvariantCulture, '{0}', $Value)
    if ($text.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -ge 0) { throw 'A sampler field holds a separator.' }
    $text
}

function Add-SamplerLine([string] $Path, [object[]] $Value) {
    $line = ($Value | ForEach-Object { ConvertTo-SamplerField $_ }) -join ','
    [System.IO.File]::AppendAllText($Path, $line + "`n", [System.Text.UTF8Encoding]::new($false))
}

function ConvertFrom-SamplerBool([string] $Text) {
    if ($Text -ceq 'true') { return $true }
    if ($Text -ceq 'false') { return $false }
    throw 'A sampler Boolean is malformed.'
}

function ConvertFrom-SamplerInteger([string] $Text, [switch] $AllowEmpty, [switch] $Long) {
    if ($Text -eq '') {
        if ($AllowEmpty) { return $null }
        throw 'A required sampler number is missing.'
    }
    $style = [System.Globalization.NumberStyles]::None
    if ($Long) {
        $parsed = [long]0
        if (-not [long]::TryParse($Text, $style, [cultureinfo]::InvariantCulture, [ref]$parsed)) { throw 'A sampler number is malformed.' }
        return $parsed
    }
    $value = 0
    if (-not [int]::TryParse($Text, $style, [cultureinfo]::InvariantCulture, [ref]$value)) { throw 'A sampler number is malformed.' }
    $value
}

<#
.SYNOPSIS
    One power-file row from a tick's clock and power reading, in column order.
.DESCRIPTION
    A reading that could not be taken (null) is a row with observation_error set and every power
    field empty, so that it can never satisfy condition P.
#>
function ConvertTo-PowerSampleValue {
    param(
        [Parameter(Mandatory)] [string] $RunId,
        [Parameter(Mandatory)] [long] $Seq,
        [Parameter(Mandatory)] [long] $Qpc,
        [Parameter(Mandatory)] [datetime] $Utc,
        [AllowNull()] [System.Collections.IDictionary] $Reading
    )
    $values = [System.Collections.Generic.List[object]]::new()
    $values.AddRange([object[]]@($RunId, $Seq, $Qpc, $Utc, ($null -eq $Reading)))
    foreach ($field in $script:SamplerPowerColumns | Select-Object -Skip 5) {
        $values.Add($(if ($null -ne $Reading) { $Reading[$field] } else { $null }))
    }
    , $values.ToArray()
}

<#
.SYNOPSIS
    Parses one power-file line strictly into typed fields; anything malformed throws.
.DESCRIPTION
    Reads the raw line, not a CSV parser's view of it, so that a line cut short is malformed instead
    of reading as empty trailing fields. No field holds a separator (the writer refuses one), so
    the line must split into exactly one field per column.
#>
function ConvertFrom-PowerSampleRow {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Line)
    $fields = $Line.Split(',')
    if ($fields.Count -ne $script:SamplerPowerColumns.Count) { throw 'A sampler line has the wrong number of fields.' }
    $text = @{}
    for ($i = 0; $i -lt $fields.Count; $i++) { $text[$script:SamplerPowerColumns[$i]] = $fields[$i] }
    $utc = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($text.utc, 'o', [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind, [ref]$utc)) {
        throw 'A sampler time is malformed.'
    }
    if ($text.run_id -eq '') { throw 'A sampler row has no run identity.' }
    [ordered]@{
        run_id                 = $text.run_id
        seq                    = ConvertFrom-SamplerInteger $text.seq -Long
        qpc                    = ConvertFrom-SamplerInteger $text.qpc -Long
        utc                    = $utc.ToUniversalTime()
        observation_error      = ConvertFrom-SamplerBool $text.observation_error
        call_succeeded         = if ($text.call_succeeded -eq '') { $null } else { ConvertFrom-SamplerBool $text.call_succeeded }
        win32_error            = ConvertFrom-SamplerInteger $text.win32_error -AllowEmpty
        ac_line_status         = ConvertFrom-SamplerInteger $text.ac_line_status -AllowEmpty
        battery_flag           = ConvertFrom-SamplerInteger $text.battery_flag -AllowEmpty
        battery_life_percent   = ConvertFrom-SamplerInteger $text.battery_life_percent -AllowEmpty
        system_status_flag     = ConvertFrom-SamplerInteger $text.system_status_flag -AllowEmpty
        scheme_query_succeeded = if ($text.scheme_query_succeeded -eq '') { $null } else { ConvertFrom-SamplerBool $text.scheme_query_succeeded }
        scheme_query_status    = ConvertFrom-SamplerInteger $text.scheme_query_status -AllowEmpty -Long
        scheme_query_failure   = if ($text.scheme_query_failure -eq '') { $null } else { $text.scheme_query_failure }
        scheme_guid            = if ($text.scheme_guid -eq '') { $null } else { $text.scheme_guid }
    }
}

# The parts of P a parsed sampler row does not meet.
function Get-SamplePowerFailure([System.Collections.IDictionary] $Row) {
    if ($Row.observation_error) { return , @('observation_error') }
    Get-PowerConditionFailure $Row
}

<#
.SYNOPSIS
    The coverage failures of a sampler's power file for one collection; none means coverage held.
.DESCRIPTION
    Takes the file's lines, header first. Judged on qpc only. The header must be exactly the power
    columns. Over the whole file, every row must parse and carry this run's identity,
    seq must run 1, 2, 3, ... without a break, qpc must increase, and the wall clock must agree with
    qpc between consecutive rows (|delta utc - delta qpc| <= MaxClockStepSeconds). There must be a row
    at or before the launch and one at or after the exit. From the last row at or before the launch
    to the first row at or after the exit, no gap may exceed MaxGapSeconds and every row, those two
    included, must meet condition P. Coverage detects gaps; it cannot show what the power source did
    between two rows.
#>
function Test-SamplerCoverage {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Line,
        [Parameter(Mandatory)] [string] $RunId,
        [Parameter(Mandatory)] [long] $QpcFrequency,
        [Parameter(Mandatory)] [long] $LaunchQpc,
        [Parameter(Mandatory)] [long] $ExitQpc,
        [double] $MaxGapSeconds = 3,
        [double] $MaxClockStepSeconds = 1
    )
    if ($Line.Count -eq 0) { return , @('no_rows') }
    if ($Line[0] -cne ($script:SamplerPowerColumns -join ',')) { return , @('header_malformed') }
    if ($Line.Count -eq 1) { return , @('no_rows') }
    try { $rows = @($Line | Select-Object -Skip 1 | ForEach-Object { ConvertFrom-PowerSampleRow $_ }) }
    catch { return , @('row_malformed') }

    $failures = [System.Collections.Generic.List[string]]::new()
    $add = { param([string] $Code) if (-not $failures.Contains($Code)) { $failures.Add($Code) } }
    for ($i = 0; $i -lt $rows.Count; $i++) {
        if ($rows[$i].run_id -cne $RunId) { & $add 'identity_changed' }
        if ($rows[$i].seq -ne $i + 1) { & $add 'seq_not_contiguous' }
        if ($i -gt 0) {
            $qpcSeconds = ($rows[$i].qpc - $rows[$i - 1].qpc) / $QpcFrequency
            $utcSeconds = ($rows[$i].utc - $rows[$i - 1].utc).TotalSeconds
            if ($qpcSeconds -le 0) { & $add 'qpc_not_increasing' }
            if ([Math]::Abs($utcSeconds - $qpcSeconds) -gt $MaxClockStepSeconds) { & $add 'clock_step' }
        }
    }
    $first = -1
    $last = -1
    for ($i = 0; $i -lt $rows.Count; $i++) {
        if ($rows[$i].qpc -le $LaunchQpc) { $first = $i }
        if ($last -lt 0 -and $rows[$i].qpc -ge $ExitQpc) { $last = $i }
    }
    if ($first -lt 0) { & $add 'no_row_before_launch' }
    if ($last -lt 0) { & $add 'no_row_after_exit' }
    if ($first -ge 0 -and $last -ge 0) {
        for ($i = $first; $i -le $last; $i++) {
            if ($i -gt $first -and ($rows[$i].qpc - $rows[$i - 1].qpc) / $QpcFrequency -gt $MaxGapSeconds) { & $add 'gap_exceeded' }
            if ((Get-SamplePowerFailure $rows[$i]).Count -gt 0) { & $add 'power_unmet_in_window' }
        }
    }
    , $failures.ToArray()
}

<#
.SYNOPSIS
    Picks a role's single process from candidates, or the reason it has none.
.DESCRIPTION
    No candidate is `process_absent`. Several, or one whose start time cannot be read, is
    `process_ambiguous`: the identity cannot be fixed, so it is not guessed.
#>
function Resolve-RoleIdentity {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Candidate)
    if ($Candidate.Count -eq 0) { return [ordered]@{ identity = $null; na = 'process_absent' } }
    if ($Candidate.Count -gt 1 -or $null -eq $Candidate[0].start_ticks) { return [ordered]@{ identity = $null; na = 'process_ambiguous' } }
    [ordered]@{ identity = [ordered]@{ pid = [int]$Candidate[0].pid; start_ticks = [long]$Candidate[0].start_ticks }; na = $null }
}

<#
.SYNOPSIS
    The sqlservr processes of one LocalDB instance, without their command lines.
.DESCRIPTION
    A LocalDB instance's command line names its folder, `...\Instances\<instance>\`. The command
    line selects the process here and goes no further.
#>
function Select-LocalDbCandidate {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Process,
        [Parameter(Mandatory)] [string] $Instance
    )
    $pattern = '(?i)\\Instances\\' + [regex]::Escape($Instance) + '\\'
    , @($Process | Where-Object { [string]$_.command_line -match $pattern } | ForEach-Object { [ordered]@{ pid = [int]$_.pid } })
}

# A process's start time and total CPU, or null when no process has that id.
function Read-ProcessCpu([int] $ProcessId) {
    try { $process = [System.Diagnostics.Process]::GetProcessById($ProcessId) }
    catch [System.ArgumentException] { return $null }
    try { [ordered]@{ start_ticks = $process.StartTime.ToUniversalTime().Ticks; cpu_seconds = $process.TotalProcessorTime.TotalSeconds } }
    finally { $process.Dispose() }
}

# The candidate processes of one role on this host. Start times come from the same source as
# Read-ProcessCpu, so that a later identity check compares like with like.
function Find-RoleCandidate([string] $Name, [System.Collections.IDictionary] $Spec) {
    $withStart = {
        param([int] $ProcessId)
        $start = $null
        try { $start = (Read-ProcessCpu $ProcessId).start_ticks } catch { $start = $null }
        [ordered]@{ pid = $ProcessId; start_ticks = $start }
    }
    switch ($Name) {
        'cms' {
            $found = & $withStart $Spec.pid
            if ($null -eq $found.start_ticks -or $found.start_ticks -ne [long]$Spec.start_ticks) { return , @() }
            return , @($found)
        }
        'localdb' {
            $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'sqlservr.exe'" | ForEach-Object { [ordered]@{ pid = [int]$_.ProcessId; command_line = [string]$_.CommandLine } })
            $selected = Select-LocalDbCandidate -Process $processes -Instance $Spec.instance
            return , @($selected | ForEach-Object { & $withStart $_.pid })
        }
        'docker_vm' {
            return , @(Get-Process -Name 'vmmemWSL', 'vmmem' -ErrorAction SilentlyContinue | ForEach-Object { & $withStart $_.Id })
        }
        default { throw "Unknown sampler role $Name." }
    }
}

<#
.SYNOPSIS
    The sampler's optional-reading state: counters created once, role identities fixed, baselines.
.DESCRIPTION
    Called once, before the first tick, so that no tick waits on counter discovery and every role is
    fixed at the ready handshake. A counter that cannot be created is `counter_unavailable` for the
    whole run. The host access is injectable for tests.
#>
function Initialize-HostSampleState {
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Role,
        [Parameter(Mandatory)] [long] $Qpc,
        [Parameter(Mandatory)] [long] $QpcFrequency,
        [int] $LogicalCpus = [Environment]::ProcessorCount,
        [int] $SamplerProcessId = $PID,
        [scriptblock] $NewCounter = {
            param([string] $Category, [string] $Counter, [string] $Instance)
            $created = [System.Diagnostics.PerformanceCounter]::new($Category, $Counter, $Instance, $true)
            [void]$created.NextValue()
            $created
        },
        [scriptblock] $FindCandidate = { param([string] $Name, [System.Collections.IDictionary] $Spec) Find-RoleCandidate $Name $Spec },
        [scriptblock] $ReadProcessCpu = { param([int] $ProcessId) Read-ProcessCpu $ProcessId }
    )
    foreach ($name in $Role.Keys) { if ($script:SamplerRoles -notcontains $name) { throw "Unknown sampler role $name." } }
    $state = @{
        LogicalCpus    = $LogicalCpus
        QpcFrequency   = $QpcFrequency
        ReadProcessCpu = $ReadProcessCpu
        Counters       = [ordered]@{}
        Roles          = [ordered]@{}
    }
    foreach ($name in $script:HostCounters.Keys) {
        $definition = $script:HostCounters[$name]
        try { $state.Counters[$name] = @{ Counter = (& $NewCounter $definition[0] $definition[1] $definition[2]); Na = $null } }
        catch { $state.Counters[$name] = @{ Counter = $null; Na = 'counter_unavailable' } }
    }
    # Role discovery and the sampler's own CPU are optional: a failure there leaves that role
    # unknown (`counter_error`, never a guessed candidate) and must not stop the power rows.
    $tracked = [ordered]@{}
    foreach ($name in $script:SamplerRoles) {
        if ($Role.Contains($name)) {
            try {
                # Assigned first: @() around a call that returns its list as one object would nest it.
                $found = & $FindCandidate $name $Role[$name]
                [object[]] $candidates = @()
                if ($null -ne $found) { $candidates = @($found) }
                $resolved = Resolve-RoleIdentity -Candidate $candidates
                $tracked["cpu_$($name)_pct"] = @{ Identity = $resolved.identity; Na = $resolved.na }
            }
            catch {
                $tracked["cpu_$($name)_pct"] = @{ Identity = $null; Na = 'counter_error' }
            }
        }
    }
    try {
        $self = & $ReadProcessCpu $SamplerProcessId
        $tracked['cpu_sampler_pct'] = @{ Identity = $(if ($null -ne $self) { [ordered]@{ pid = $SamplerProcessId; start_ticks = [long]$self.start_ticks } }); Na = $(if ($null -eq $self) { 'process_absent' }) }
    }
    catch {
        $tracked['cpu_sampler_pct'] = @{ Identity = $null; Na = 'counter_error' }
    }
    foreach ($name in $tracked.Keys) {
        $entry = $tracked[$name]
        $entry.LastCpu = $null
        $entry.LastQpc = $Qpc
        if ($null -ne $entry.Identity) {
            try {
                $current = & $ReadProcessCpu $entry.Identity.pid
                if ($null -eq $current -or [long]$current.start_ticks -ne $entry.Identity.start_ticks) { $entry.Na = 'process_changed' }
                else { $entry.LastCpu = [double]$current.cpu_seconds }
            }
            catch { $entry.LastCpu = $null }
        }
        $state.Roles[$name] = $entry
    }
    $state
}

<#
.SYNOPSIS
    The optional columns, in order: each value column followed by its `_na` reason column, then
    `cpu_background_floored`.
#>
function Get-HostSampleColumn {
    param([Parameter(Mandatory)] [hashtable] $State)
    $columns = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @($State.Counters.Keys) + @($State.Roles.Keys) + 'cpu_background_pct') {
        $columns.Add($name)
        $columns.Add("$($name)_na")
    }
    $columns.Add('cpu_background_floored')
    , $columns.ToArray()
}

<#
.SYNOPSIS
    The public form of the fixed role identities: the id and start time, or the reason there is none.
#>
function Get-HostSampleRole {
    param([Parameter(Mandatory)] [hashtable] $State)
    $roles = [ordered]@{}
    foreach ($name in $State.Roles.Keys) {
        $entry = $State.Roles[$name]
        $roles[($name -replace '^cpu_(.+)_pct$', '$1')] = if ($null -ne $entry.Identity) {
            [ordered]@{ pid = $entry.Identity.pid; start_utc = ([datetime]::new($entry.Identity.start_ticks, [DateTimeKind]::Utc)).ToString('o'); na = $entry.Na }
        }
        else { [ordered]@{ pid = $null; start_utc = $null; na = $entry.Na } }
    }
    $roles
}

<#
.SYNOPSIS
    One tick's optional values, as column name to text. An unavailable value is `NA` with a reason
    code, never 0.
.DESCRIPTION
    CPU % of a process is its TotalProcessorTime delta over the qpc interval since its last good
    read, divided by the logical CPU count, times 100. A process whose id or start time changed is
    `process_changed` from then on. Background CPU is total % Processor Time minus the roles and the
    sampler, floored at 0 with the floor flagged; it is NA when any of its inputs is.
#>
function Read-HostSample {
    param(
        [Parameter(Mandatory)] [hashtable] $State,
        [Parameter(Mandatory)] [long] $Qpc
    )
    $values = [ordered]@{}
    $reasons = [ordered]@{}
    foreach ($name in $State.Counters.Keys) {
        $entry = $State.Counters[$name]
        if ($null -ne $entry.Na) { $reasons[$name] = $entry.Na; continue }
        try {
            $value = [double]$entry.Counter.NextValue()
            if ([double]::IsNaN($value) -or [double]::IsInfinity($value)) { $reasons[$name] = 'counter_error' }
            else { $values[$name] = $value }
        }
        catch { $reasons[$name] = 'counter_error' }
    }
    foreach ($name in $State.Roles.Keys) {
        $entry = $State.Roles[$name]
        if ($null -ne $entry.Na) { $reasons[$name] = $entry.Na; continue }
        try { $current = & $State.ReadProcessCpu $entry.Identity.pid }
        catch { $reasons[$name] = 'counter_error'; continue }
        if ($null -eq $current -or [long]$current.start_ticks -ne $entry.Identity.start_ticks) {
            $entry.Na = 'process_changed'
            $reasons[$name] = 'process_changed'
            continue
        }
        $interval = ($Qpc - $entry.LastQpc) / $State.QpcFrequency
        if ($null -eq $entry.LastCpu -or $interval -le 0) {
            $reasons[$name] = 'counter_error'
        }
        else {
            $values[$name] = ([double]$current.cpu_seconds - $entry.LastCpu) / $interval / $State.LogicalCpus * 100
        }
        $entry.LastCpu = [double]$current.cpu_seconds
        $entry.LastQpc = $Qpc
    }
    $floored = $null
    $inputs = @('processor_time_pct') + @($State.Roles.Keys)
    $missing = @($inputs | Where-Object { -not $values.Contains($_) })
    if ($missing.Count -gt 0) {
        $reasons['cpu_background_pct'] = $reasons[$missing[0]]
    }
    else {
        $background = $values['processor_time_pct']
        foreach ($name in $State.Roles.Keys) { $background -= $values[$name] }
        $floored = $background -lt 0
        $values['cpu_background_pct'] = [Math]::Max(0.0, $background)
    }
    $row = [ordered]@{}
    $columns = Get-HostSampleColumn -State $State
    foreach ($column in $columns) {
        if ($column -eq 'cpu_background_floored') { $row[$column] = $(if ($null -eq $floored) { 'NA' } else { ConvertTo-SamplerField $floored }) }
        elseif ($column.EndsWith('_na')) { $name = $column.Substring(0, $column.Length - 3); $row[$column] = $(if ($reasons.Contains($name)) { $reasons[$name] } else { '' }) }
        elseif ($values.Contains($column)) { $row[$column] = ConvertTo-SamplerField ([double]$values[$column]) }
        else { $row[$column] = 'NA' }
    }
    $row
}

<#
.SYNOPSIS
    The sampler's loop: one power row, then one optional row, per tick, until the stop signal.
.DESCRIPTION
    Each tick reads the clock (qpc and UTC) and the power source first and appends the power row;
    after the first row it writes the ready record. Only then does it read the optional values.
    When the stop signal exists at the start of a tick, that tick is the last, so a row always
    follows the signal. Refuses existing output files. The clock, pause, readers and an optional
    tick limit are parameters, so that tests run the loop with synthetic signals and no sleeps.
#>
function Invoke-PowerSampler {
    param(
        [Parameter(Mandatory)] [string] $RunId,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Identity,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Role,
        [Parameter(Mandatory)] [string[]] $OptionalColumn,
        [Parameter(Mandatory)] [scriptblock] $ReadOptional,
        [Parameter(Mandatory)] [string] $PowerPath,
        [Parameter(Mandatory)] [string] $OptionalPath,
        [Parameter(Mandatory)] [string] $ReadyPath,
        [Parameter(Mandatory)] [string] $StopPath,
        [long] $QpcFrequency = [System.Diagnostics.Stopwatch]::Frequency,
        [double] $IntervalSeconds = 1,
        [int] $MaxTicks = 0,
        [scriptblock] $ReadClock = { [ordered]@{ qpc = [System.Diagnostics.Stopwatch]::GetTimestamp(); utc = [datetime]::UtcNow } },
        [scriptblock] $ReadPower = { Get-PowerSource },
        [scriptblock] $Pause = { param([int] $Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
    )
    foreach ($path in $PowerPath, $OptionalPath, $ReadyPath) {
        if (Test-Path -LiteralPath $path) { throw "The sampler output $(Split-Path -Leaf $path) already exists." }
    }
    Add-SamplerLine $PowerPath $script:SamplerPowerColumns
    Add-SamplerLine $OptionalPath (@('run_id', 'seq', 'qpc') + $OptionalColumn)
    $start = & $ReadClock
    $seq = 0
    while ($true) {
        $stopping = Test-Path -LiteralPath $StopPath
        $seq++
        $clock = if ($seq -eq 1) { $start } else { & $ReadClock }
        $reading = $null
        try { $reading = & $ReadPower } catch { $reading = $null }
        $powerValue = ConvertTo-PowerSampleValue -RunId $RunId -Seq $seq -Qpc $clock.qpc -Utc $clock.utc -Reading $reading
        Add-SamplerLine $PowerPath $powerValue
        if ($seq -eq 1) {
            $ready = [ordered]@{
                schema           = 'power-sampler-ready/2'
                run_id           = $RunId
                scheme_provider  = $script:SchemeQueryProvider
                identity         = $Identity
                qpc_frequency    = $QpcFrequency
                interval_seconds = $IntervalSeconds
                roles            = $Role
                power_columns    = $script:SamplerPowerColumns
                optional_columns = $OptionalColumn
                first_row        = ($powerValue | ForEach-Object { ConvertTo-SamplerField $_ }) -join ','
            }
            $temporary = "$ReadyPath.tmp"
            [System.IO.File]::WriteAllText($temporary, ($ready | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
            [System.IO.File]::Move($temporary, $ReadyPath)
        }
        $optionalQpc = (& $ReadClock).qpc
        $optional = $null
        try { $optional = & $ReadOptional $optionalQpc } catch { $optional = $null }
        $optionalValue = @($RunId, $seq, $optionalQpc) + @($OptionalColumn | ForEach-Object {
                if ($null -ne $optional -and $optional.Contains($_)) { $optional[$_] }
                elseif ($_.EndsWith('_na')) { 'counter_error' }
                else { 'NA' }
            })
        Add-SamplerLine $OptionalPath $optionalValue
        if ($stopping -or ($MaxTicks -gt 0 -and $seq -ge $MaxTicks)) { break }
        $next = $start.qpc + [long]($seq * $IntervalSeconds * $QpcFrequency)
        $wait = [Math]::Floor(($next - (& $ReadClock).qpc) * 1000.0 / $QpcFrequency)
        if ($wait -gt 0) { & $Pause ([int]$wait) }
    }
}

# Quotes one argument for a Windows command line, as CommandLineToArgvW reads it back.
function ConvertTo-CommandLineArgument([string] $Value) {
    if ($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }
    '"' + (($Value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

<#
.SYNOPSIS
    Starts the sampler script in its own pwsh process, with its console output in the work folder.
#>
function Open-PowerSampler {
    param(
        [Parameter(Mandatory)] [string] $ScriptPath,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Argument,
        [Parameter(Mandatory)] [string] $StandardOutputPath,
        [Parameter(Mandatory)] [string] $StandardErrorPath
    )
    $list = [System.Collections.Generic.List[string]]::new()
    $list.AddRange([string[]]@('-NoProfile', '-NonInteractive', '-File', $ScriptPath))
    foreach ($name in $Argument.Keys) {
        $value = $Argument[$name]
        if ($value -is [switch] -or $value -is [bool]) { if ($value) { $list.Add("-$name") } }
        else { $list.Add("-$name"); $list.Add([string]$value) }
    }
    $process = Start-Process -FilePath ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) -ArgumentList (($list | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' ') -RedirectStandardOutput $StandardOutputPath -RedirectStandardError $StandardErrorPath -NoNewWindow -PassThru
    # Reading the handle now keeps the exit code readable after the process ends.
    $null = $process.Handle
    $process
}

<#
.SYNOPSIS
    Waits for the sampler's ready record and checks it, or returns why the sampler is not ready.
.DESCRIPTION
    Returns failure null and the parsed first row only when, within TimeoutSeconds of elapsed
    monotonic time, the record exists, has the ready schema, names this run (id, label, lane stamp)
    and this sampler process (id and start time), holds row 1 of this run, that row meets P, and
    the process is still alive both when the record is found and after it is read. Otherwise
    failure is one of sampler_exited_before_ready, sampler_ready_timeout, sampler_ready_malformed
    (unreadable, wrong schema, or a missing or malformed identity field), sampler_identity_mismatch
    or sampler_ready_not_p (with the unmet parts of P).

    The deadline and liveness are checked on every pass, including the one that finds the record,
    so a record that appears late, or a sampler that has already died, is never accepted. The clock
    returns monotonic seconds (by default the QueryPerformanceCounter, which a wall-clock change
    does not move); it and the pause are parameters, so that tests reach the timeout without waiting.
#>
function Wait-PowerSamplerReady {
    param(
        [Parameter(Mandatory)] [psobject] $Process,
        [Parameter(Mandatory)] [string] $ReadyPath,
        [Parameter(Mandatory)] [string] $RunId,
        [Parameter(Mandatory)] [string] $RunLabel,
        [Parameter(Mandatory)] [string] $LaneStamp,
        [double] $TimeoutSeconds = 30,
        [scriptblock] $Clock = { [System.Diagnostics.Stopwatch]::GetTimestamp() / [double][System.Diagnostics.Stopwatch]::Frequency },
        [scriptblock] $Pause = { param([int] $Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
    )
    $result = [ordered]@{ failure = $null; unmet = @(); ready = $null; first_row = $null }
    $started = [double](& $Clock)
    $alive = {
        try { return -not $Process.HasExited } catch { return $false }
    }
    while ($true) {
        $found = Test-Path -LiteralPath $ReadyPath
        if (-not (& $alive)) { $result.failure = 'sampler_exited_before_ready'; return $result }
        if ([double](& $Clock) - $started -ge $TimeoutSeconds) { $result.failure = 'sampler_ready_timeout'; return $result }
        if ($found) { break }
        & $Pause 200
    }
    try {
        $ready = Get-Content -LiteralPath $ReadyPath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
        $row = ConvertFrom-PowerSampleRow ([string]$ready.first_row)
        if ($ready.schema -cne 'power-sampler-ready/2') { throw 'The ready record has another schema.' }
        $identity = $ready.identity
        if ($identity -isnot [System.Collections.IDictionary]) { throw 'The ready record has no identity.' }
        foreach ($field in 'run_label', 'lane_stamp', 'sampler_start_utc') {
            if ($identity[$field] -isnot [string] -or $identity[$field] -eq '') { throw "The ready identity has no $field." }
        }
        if ($ready.run_id -isnot [string] -or -not ($identity.sampler_pid -is [int] -or $identity.sampler_pid -is [long])) { throw 'The ready identity is malformed.' }
        $readyStart = [datetime]::MinValue
        if (-not [datetime]::TryParseExact($identity.sampler_start_utc, 'o', [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind, [ref]$readyStart)) {
            throw 'The ready start time is malformed.'
        }
    }
    catch {
        $result.failure = 'sampler_ready_malformed'
        return $result
    }
    $result.ready = $ready
    $result.first_row = $row
    try { $processStart = $Process.StartTime.ToUniversalTime().Ticks } catch { $processStart = $null }
    if ($ready.run_id -cne $RunId -or $identity.run_label -cne $RunLabel -or $identity.lane_stamp -cne $LaneStamp -or
        $identity.sampler_pid -ne $Process.Id -or $null -eq $processStart -or $readyStart.ToUniversalTime().Ticks -ne $processStart -or
        $row.run_id -cne $RunId -or $row.seq -ne 1) {
        $result.failure = 'sampler_identity_mismatch'
        return $result
    }
    # Checked again after reading: a sampler that died meanwhile is not ready.
    if (-not (& $alive)) { $result.failure = 'sampler_exited_before_ready'; return $result }
    if ([double](& $Clock) - $started -ge $TimeoutSeconds) { $result.failure = 'sampler_ready_timeout'; return $result }
    $unmet = Get-SamplePowerFailure $row
    if ($unmet.Count -gt 0) {
        $result.failure = 'sampler_ready_not_p'
        $result.unmet = $unmet
    }
    $result
}

<#
.SYNOPSIS
    A refusal record for a sampler that was not ready before the collector launch.
#>
function ConvertTo-SamplerRefusalRecord {
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Ready)
    if ($null -eq $Ready.failure) { throw 'A ready sampler is not a refusal.' }
    $record = ConvertTo-PowerCheckRecord -Check before_launch -Reading $Ready.first_row
    $record.condition_met = $false
    $record.unmet = @($Ready.failure) + @($Ready.unmet)
    $record
}

<#
.SYNOPSIS
    Signals the sampler to stop and waits for it; kills it if it does not stop in time.
.DESCRIPTION
    Records whether it was still alive when signalled, whether it stopped within the timeout, its
    exit code, and, if it did not, whether a kill was requested, the type of any error the kill or
    the wait after it raised (the underlying exception, not PowerShell's method-call wrapper), and
    whether termination was confirmed. `terminated` is true only when
    a wait reported the process gone or the process reports it has exited; a requested kill alone
    never counts. One kill and one bounded wait, no retry loop. Takes any object with the
    HasExited, Id and ExitCode properties and the WaitForExit(int) and Kill(bool) methods of a
    process, so that tests use stand-ins.
#>
function Close-PowerSampler {
    param(
        [Parameter(Mandatory)] [psobject] $Process,
        [Parameter(Mandatory)] [string] $StopPath,
        [int] $TimeoutMilliseconds = 10000,
        [int] $KillWaitMilliseconds = 5000
    )
    $hasExited = { try { return [bool]$Process.HasExited } catch { return $false } }
    $result = [ordered]@{
        alive_at_stop   = -not (& $hasExited)
        stopped_in_time = $false
        exit_code       = $null
        kill_requested  = $false
        kill_error      = $null
        wait_error      = $null
        terminated      = $false
    }
    [System.IO.File]::WriteAllText($StopPath, 'stop')
    $stopped = $false
    try { $stopped = [bool]$Process.WaitForExit($TimeoutMilliseconds) } catch { $result.wait_error = $_.Exception.GetBaseException().GetType().FullName }
    if ($stopped) {
        $result.stopped_in_time = $true
        $result.terminated = $true
        try { $result.exit_code = [int]$Process.ExitCode } catch { $result.exit_code = $null }
        return $result
    }
    $result.kill_requested = $true
    try { $Process.Kill($true) } catch { $result.kill_error = $_.Exception.GetBaseException().GetType().FullName }
    $gone = $false
    try { $gone = [bool]$Process.WaitForExit($KillWaitMilliseconds) } catch { $result.wait_error = $_.Exception.GetBaseException().GetType().FullName }
    $result.terminated = $gone -or (& $hasExited)
    $result
}

<#
.SYNOPSIS
    The lifecycle failures in a Close-PowerSampler result; none means the sampler lived and stopped cleanly.
#>
function Get-SamplerLifecycleFailure([System.Collections.IDictionary] $Stop) {
    $failures = [System.Collections.Generic.List[string]]::new()
    if (-not $Stop.alive_at_stop) { $failures.Add('sampler_dead_at_exit') }
    if (-not $Stop.stopped_in_time) { $failures.Add('sampler_slow_shutdown') }
    elseif ($Stop.exit_code -ne 0) { $failures.Add('sampler_exit_nonzero') }
    if (-not $Stop.terminated) { $failures.Add('sampler_not_terminated') }
    , $failures.ToArray()
}

Export-ModuleMember -Function Get-FingerprintedAssemblyName, Get-SettingAllowlist, Get-CollectorFixtureName, Resolve-LanePath, Assert-LaneRoot, Assert-OutsideWorktree, ConvertFrom-Sha256Listing, Assert-AssemblyFingerprint, Assert-RunSetting, Compare-LaneSnapshot, Assert-CollectorRun, Get-PowerSource, Get-PowerConditionFailure, ConvertTo-PowerCheckRecord, Add-PowerRefusal, Get-SchemeQueryProvider, ConvertTo-PowerSampleValue, ConvertFrom-PowerSampleRow, Test-SamplerCoverage, Resolve-RoleIdentity, Select-LocalDbCandidate, Initialize-HostSampleState, Get-HostSampleColumn, Get-HostSampleRole, Read-HostSample, Invoke-PowerSampler, Open-PowerSampler, Wait-PowerSamplerReady, ConvertTo-SamplerRefusalRecord, Close-PowerSampler, Get-SamplerLifecycleFailure
