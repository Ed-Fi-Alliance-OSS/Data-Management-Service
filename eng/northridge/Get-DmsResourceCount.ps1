# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Captures per-resource DMS document counts, and reconciles two count sets in both directions.

.DESCRIPTION
    Two datasets agreeing on a total document count can still disagree resource by resource, and a
    one-directional diff cannot see a resource that is present on one side and absent on the other.
    This script therefore emits a full per-resource count set per engine, and reconciles two sets with
    a full outer join that reports three separate figures: left-only resources, right-only resources,
    and shared resources whose counts differ.

    Count mode reads dms.Document joined to dms.ResourceKey, grouped by ProjectName and ResourceName
    -- the pair dms.ResourceKey is unique on, so two projects that share a resource name stay two
    rows -- which is the same grouping on both engines, so the two sides are comparable by
    construction. Every non-blank line the client tool prints has to parse as that pair and a count;
    one that does not stops the run, because a skipped row is a resource that silently drops out of
    both the count set and the reconciliation.

    Reconcile mode refuses three ways of passing while proving nothing. One file passed as both sides
    is rejected, because a count set reconciled against itself differs nowhere and hits every
    expected total. An empty count set on either side is rejected rather than reconciled, because
    two header-only files differ nowhere. And the expected document and resource totals are
    required, because two count sets that agree with each other are still only evidence about each
    other.

.PARAMETER Engine
    Engine to count. 'postgresql' uses psql in a container; 'mssql' uses sqlcmd in a container.

.PARAMETER Database
    Database to count.

.PARAMETER OutputPath
    Where to write the count set, as CSV. Use a location outside the repository.

.PARAMETER Container
    Name of the running database container. Defaults per engine.

.PARAMETER DatabaseUser
    Database user. Defaults per engine.

.PARAMETER LeftPath
    Reconcile mode: the first count set, normally PostgreSQL.

.PARAMETER RightPath
    Reconcile mode: the second count set, normally SQL Server.

.PARAMETER ExpectedDocumentCount
    Reconcile mode: assert this total on both sides. Required, because the reconciliation is
    acceptance evidence: two count sets can agree with each other and still both be wrong, and
    without a total to hit there is nothing that distinguishes agreement from correctness.

.PARAMETER ExpectedResourceCount
    Reconcile mode: assert this distinct resource count on both sides. Required, for the same reason.

.EXAMPLE
    ./Get-DmsResourceCount.ps1 -Engine postgresql -Database northridge_target -OutputPath /tmp/nr/pg.csv

.EXAMPLE
    ./Get-DmsResourceCount.ps1 -LeftPath /tmp/nr/pg.csv -RightPath /tmp/nr/mssql.csv `
        -ExpectedDocumentCount 10576801 -ExpectedResourceCount 210

    Reconciles both directions and fails on any difference.
#>

[CmdletBinding(DefaultParameterSetName = "Count")]
param(
    [Parameter(Mandatory, ParameterSetName = "Count")]
    [ValidateSet("postgresql", "mssql")]
    [string]
    $Engine,

    [Parameter(Mandatory, ParameterSetName = "Count")]
    [string]
    $Database,

    [Parameter(Mandatory, ParameterSetName = "Count")]
    [string]
    $OutputPath,

    [Parameter(ParameterSetName = "Count")]
    [string]
    $Container,

    [Parameter(ParameterSetName = "Count")]
    [string]
    $DatabaseUser,

    [Parameter(Mandatory, ParameterSetName = "Reconcile")]
    [string]
    $LeftPath,

    [Parameter(Mandatory, ParameterSetName = "Reconcile")]
    [string]
    $RightPath,

    [Parameter(Mandatory, ParameterSetName = "Reconcile")]
    [ValidateRange([System.Management.Automation.ValidateRangeKind]::Positive)]
    [long]
    $ExpectedDocumentCount,

    [Parameter(Mandatory, ParameterSetName = "Reconcile")]
    [ValidateRange([System.Management.Automation.ValidateRangeKind]::Positive)]
    [int]
    $ExpectedResourceCount
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-ContainerEnvironmentValue {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $ContainerName,
        [Parameter(Mandatory)] [string] $VariableName
    )

    $lines = docker inspect $ContainerName --format '{{range .Config.Env}}{{println .}}{{end}}'
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect container '$ContainerName'."
    }

    foreach ($line in $lines) {
        $text = [string]$line
        if ($text.StartsWith("$VariableName=", [System.StringComparison]::Ordinal)) {
            return $text.Substring($VariableName.Length + 1)
        }
    }

    throw "Container '$ContainerName' does not define $VariableName."
}

function Get-PostgresqlResourceCount {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)] [string] $ContainerName,
        [Parameter(Mandatory)] [string] $User,
        [Parameter(Mandatory)] [string] $DatabaseName
    )

    $sql = @"
SELECT rk."ProjectName" || '|' || rk."ResourceName" || '|' || COUNT(*)::text
FROM dms."Document" d
JOIN dms."ResourceKey" rk ON rk."ResourceKeyId" = d."ResourceKeyId"
GROUP BY rk."ProjectName", rk."ResourceName"
ORDER BY rk."ProjectName", rk."ResourceName";
"@

    $output = $sql | docker exec -i $ContainerName psql -U $User -d $DatabaseName `
        -v ON_ERROR_STOP=1 --no-align --tuples-only --quiet 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "psql failed against '$DatabaseName' (exit $LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }

    return $output
}

function Get-MssqlResourceCount {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)] [string] $ContainerName,
        [Parameter(Mandatory)] [string] $User,
        [Parameter(Mandatory)] [string] $DatabaseName
    )

    # Emits the same 'project|name|count' shape as the PostgreSQL query so both sides parse identically.
    $sql = "SET NOCOUNT ON; " +
        "SELECT rk.ProjectName + '|' + rk.ResourceName + '|' + CAST(COUNT_BIG(*) AS varchar(32)) " +
        "FROM dms.Document d JOIN dms.ResourceKey rk ON rk.ResourceKeyId = d.ResourceKeyId " +
        "GROUP BY rk.ProjectName, rk.ResourceName ORDER BY rk.ProjectName, rk.ResourceName;"

    # The running container is the authoritative source for the SA password: .env keeps
    # MSSQL_SA_PASSWORD commented out and the live value is resolved into the derived env file. Reading
    # it back avoids both a duplicated secret and a plaintext parameter on this script.
    $password = Get-ContainerEnvironmentValue -ContainerName $ContainerName -VariableName "MSSQL_SA_PASSWORD"

    # SQLCMDPASSWORD rather than -P, matching the repository convention: it keeps the password out of
    # the container process argument list. The docker command still carries the value host-side.
    $output = docker exec -e SQLCMDPASSWORD=$password $ContainerName /opt/mssql-tools18/bin/sqlcmd `
        -S localhost -U $User -C -N -b -d $DatabaseName -h -1 -W -Q $sql 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd failed against '$DatabaseName' (exit $LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }

    return $output
}

# Every non-blank line is 'ProjectName|ResourceName|Count', and one that is not stops the run. psql and
# sqlcmd print their diagnostics on the same stream the rows arrive on, so a warning, a banner or a
# truncated row would otherwise be skipped -- and a skipped row is a resource missing from the count
# set with nothing to say so, which the reconciliation reads as agreement when the other side is
# missing it too. Blank lines are the tools' trailing empty element and carry no row. No output at all
# -- a null or empty argument -- is no rows: the caller's empty-set check is what reports that, not a
# parameter binding error here.
function ConvertTo-CountRow {
    [CmdletBinding()]
    # Object[] rather than List[object]: the comma operator that keeps an empty result a collection
    # wraps the return, and the declared type has to match what callers actually receive.
    [OutputType([System.Object[]])]
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowNull()] [object[]] $Line)

    $rows = [System.Collections.Generic.List[object]]::new()

    foreach ($item in $Line) {
        $text = ([string]$item).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        $field = $text.Split("|")
        if ($field.Count -ne 3) {
            throw "Count output line '$text' is not 'ProjectName|ResourceName|Count' (found $($field.Count - 1) separator(s)); refusing to skip it."
        }

        $projectName = $field[0].Trim()
        $resourceName = $field[1].Trim()
        $countText = $field[2].Trim()
        if ([string]::IsNullOrWhiteSpace($projectName) -or [string]::IsNullOrWhiteSpace($resourceName)) {
            throw "Count output line '$text' has an empty ProjectName or ResourceName; refusing to skip it."
        }

        $parsedCount = [long]0
        if (-not [long]::TryParse($countText, [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture, [ref] $parsedCount)) {
            throw "Count output line '$text' has a count '$countText' that is not a non-negative integer; refusing to skip it."
        }

        $rows.Add([pscustomobject]@{ ProjectName = $projectName; ResourceName = $resourceName; DocumentCount = $parsedCount })
    }

    # Comma operator: an empty list returned bare unrolls to $null, and under strict mode the caller's
    # .Count on it throws instead of reaching the "no resource counts" error that names the cause.
    return ,$rows
}

# The reconciliation key is the pair dms.ResourceKey is unique on. Keyed by ResourceName alone, two
# projects' resources of one name -- core and an extension both defining, say, a Student -- collapse
# to one entry: the second is refused as a repeat or overwrites the first, and either way a count that
# should have been compared per project is not. The key joins the pair with a control character no
# project or resource name carries; the label shown in output is ProjectName/ResourceName.
function Get-CountSetKey {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $ProjectName,
        [Parameter(Mandatory)] [string] $ResourceName
    )

    return $ProjectName + [char]0x1F + $ResourceName
}

function ConvertTo-CountMap {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.Dictionary[string, object]])]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Row,
        [Parameter(Mandatory)] [string] $Label
    )

    $map = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $Row) {
        foreach ($column in @("ProjectName", "ResourceName", "DocumentCount")) {
            if (-not ($item.PSObject.Properties.Name -ccontains $column)) {
                throw "$Label has no $column column. A count set keyed by ResourceName alone cannot be reconciled per project -- regenerate it in Count mode."
            }
        }

        $projectName = [string]$item.ProjectName
        $resourceName = [string]$item.ResourceName
        if ([string]::IsNullOrWhiteSpace($projectName) -or [string]::IsNullOrWhiteSpace($resourceName)) {
            throw "$Label has a row with an empty ProjectName or ResourceName."
        }

        $parsedCount = [long]0
        if (-not [long]::TryParse([string]$item.DocumentCount, [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture, [ref] $parsedCount)) {
            throw "$Label has a DocumentCount '$($item.DocumentCount)' for $projectName/$resourceName that is not a non-negative integer."
        }

        $key = Get-CountSetKey -ProjectName $projectName -ResourceName $resourceName
        if ($map.ContainsKey($key)) {
            throw "$Label repeats resource '$projectName/$resourceName'."
        }
        $map[$key] = [pscustomobject]@{ ProjectName = $projectName; ResourceName = $resourceName; DocumentCount = $parsedCount }
    }

    return $map
}

# Reconcile mode's two paths have to name two files. The empty-set and expected-total checks cannot
# see one file passed twice: it reconciles against itself with zero differences on every axis and
# hits both totals, a PASS that says nothing about the other engine. Paths are compared after
# resolution -- a relative spelling against an absolute one, a symbolic link against its target --
# and without regard to case, because two spellings of one file are one file on Windows and macOS.
# Two distinct files with identical contents are not this guard's concern: that is what a successful
# reconciliation looks like.
function Get-CanonicalFilePath {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Count set '$Path' does not exist or is not a file."
    }

    $item = Get-Item -LiteralPath $Path
    $target = $item.ResolveLinkTarget($true)
    if ($null -ne $target) {
        return $target.FullName
    }

    return $item.FullName
}

if ($PSCmdlet.ParameterSetName -eq "Count") {

    if ([string]::IsNullOrWhiteSpace($Container)) {
        $Container = if ($Engine -eq "postgresql") { "dms-postgresql" } else { "dms-mssql" }
    }

    if ([string]::IsNullOrWhiteSpace($DatabaseUser)) {
        $DatabaseUser = if ($Engine -eq "postgresql") { "postgres" } else { "sa" }
    }

    Write-Output "Counting documents by resource: engine=$Engine database=$Database container=$Container"

    if ($Engine -eq "postgresql") {
        $raw = Get-PostgresqlResourceCount -ContainerName $Container -User $DatabaseUser -DatabaseName $Database
    }
    else {
        $raw = Get-MssqlResourceCount -ContainerName $Container -User $DatabaseUser -DatabaseName $Database
    }

    $rows = ConvertTo-CountRow -Line $raw

    if ($rows.Count -eq 0) {
        throw "No resource counts were returned from '$Database'. An empty result is a failure, not a pass."
    }

    $outputParent = Split-Path -Path $OutputPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($outputParent) -and -not (Test-Path -LiteralPath $outputParent)) {
        New-Item -Path $outputParent -ItemType Directory -Force | Out-Null
    }

    $rows | Sort-Object -Property ProjectName, ResourceName | Export-Csv -LiteralPath $OutputPath -NoTypeInformation

    $total = ($rows | Measure-Object -Property DocumentCount -Sum).Sum
    Write-Output "Resources: $($rows.Count)"
    Write-Output "Documents: $total"
    Write-Output "Written to: $OutputPath"
    return
}

# ---------- Reconcile mode ----------

$leftFile = Get-CanonicalFilePath -Path $LeftPath
$rightFile = Get-CanonicalFilePath -Path $RightPath
if ([string]::Equals($leftFile, $rightFile, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "-LeftPath and -RightPath both resolve to '$leftFile'. A count set reconciled against itself differs nowhere and proves nothing -- pass the other engine's count set."
}

# @() rather than the bare result: a header-only CSV imports as nothing at all, and the guard below
# has to be able to read .Count on it. Two such files reconcile to zero differences on every axis,
# which is a pass that proves the two files were empty and nothing else.
$left = @(Import-Csv -LiteralPath $LeftPath)
$right = @(Import-Csv -LiteralPath $RightPath)

$emptyInput = [System.Collections.Generic.List[string]]::new()
if ($left.Count -eq 0) { $emptyInput.Add("left count set '$LeftPath' holds no rows") }
if ($right.Count -eq 0) { $emptyInput.Add("right count set '$RightPath' holds no rows") }
if ($emptyInput.Count -gt 0) {
    throw "Nothing to reconcile: $($emptyInput -join '; '). An empty count set is a failure, not a pass -- regenerate it in Count mode."
}

$leftMap = ConvertTo-CountMap -Row $left -Label "Left count set '$LeftPath'"
$rightMap = ConvertTo-CountMap -Row $right -Label "Right count set '$RightPath'"

$leftTotal = ($leftMap.Values | Measure-Object -Property DocumentCount -Sum).Sum
$rightTotal = ($rightMap.Values | Measure-Object -Property DocumentCount -Sum).Sum

$leftLabel = Split-Path -Path $LeftPath -Leaf
$rightLabel = Split-Path -Path $RightPath -Leaf

# A full outer join over the union of keys: a resource missing from either side has to be visible,
# which a per-side loop over one map cannot show.
$allKeySet = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($key in @($leftMap.Keys) + @($rightMap.Keys)) {
    [void]$allKeySet.Add([string]$key)
}
$allKey = [string[]]@($allKeySet)

$leftOnly = [System.Collections.Generic.List[object]]::new()
$rightOnly = [System.Collections.Generic.List[object]]::new()
$countDiffer = [System.Collections.Generic.List[object]]::new()

foreach ($key in $allKey) {
    $inLeft = $leftMap.ContainsKey($key)
    $inRight = $rightMap.ContainsKey($key)
    $entry = if ($inLeft) { $leftMap[$key] } else { $rightMap[$key] }
    $label = "$($entry.ProjectName)/$($entry.ResourceName)"

    if ($inLeft -and -not $inRight) {
        $leftOnly.Add([pscustomobject]@{ Resource = $label; Left = $entry.DocumentCount; Right = $null })
    }
    elseif ($inRight -and -not $inLeft) {
        $rightOnly.Add([pscustomobject]@{ Resource = $label; Left = $null; Right = $entry.DocumentCount })
    }
    elseif ($leftMap[$key].DocumentCount -ne $rightMap[$key].DocumentCount) {
        $countDiffer.Add([pscustomobject]@{
                Resource   = $label
                Left       = $leftMap[$key].DocumentCount
                Right      = $rightMap[$key].DocumentCount
                Difference = $rightMap[$key].DocumentCount - $leftMap[$key].DocumentCount
            })
    }
}

Write-Output "Reconciliation: $leftLabel (left) against $rightLabel (right)"
Write-Output ""
Write-Output "  Resources, left           : $($leftMap.Count)"
Write-Output "  Resources, right          : $($rightMap.Count)"
Write-Output "  Documents, left           : $leftTotal"
Write-Output "  Documents, right          : $rightTotal"
Write-Output "  Left-only resources       : $($leftOnly.Count)"
Write-Output "  Right-only resources      : $($rightOnly.Count)"
Write-Output "  Shared, counts differ     : $($countDiffer.Count)"

$failure = [System.Collections.Generic.List[string]]::new()

foreach ($row in $leftOnly) {
    Write-Output "  left-only : $($row.Resource) = $($row.Left)"
}
foreach ($row in $rightOnly) {
    Write-Output "  right-only: $($row.Resource) = $($row.Right)"
}
foreach ($row in $countDiffer) {
    Write-Output "  differs   : $($row.Resource) left=$($row.Left) right=$($row.Right) diff=$($row.Difference)"
}

if ($leftOnly.Count -gt 0) { $failure.Add("$($leftOnly.Count) resource(s) present only on the left") }
if ($rightOnly.Count -gt 0) { $failure.Add("$($rightOnly.Count) resource(s) present only on the right") }
if ($countDiffer.Count -gt 0) { $failure.Add("$($countDiffer.Count) shared resource(s) with unequal counts") }

# Totals are asserted separately from the per-resource diff. Equal totals with offsetting per-resource
# differences is a real failure mode, and checking only the total would pass it. Both expectations are
# mandatory parameters constrained to be positive, so there is no path on which they go unchecked.
if ($leftTotal -ne $ExpectedDocumentCount) { $failure.Add("left total $leftTotal <> expected $ExpectedDocumentCount") }
if ($rightTotal -ne $ExpectedDocumentCount) { $failure.Add("right total $rightTotal <> expected $ExpectedDocumentCount") }
if ($leftMap.Count -ne $ExpectedResourceCount) { $failure.Add("left resources $($leftMap.Count) <> expected $ExpectedResourceCount") }
if ($rightMap.Count -ne $ExpectedResourceCount) { $failure.Add("right resources $($rightMap.Count) <> expected $ExpectedResourceCount") }

Write-Output ""

if ($failure.Count -eq 0) {
    Write-Output "PASS: zero differences in both directions."
    return
}

foreach ($item in $failure) { Write-Output "FAIL: $item" }
throw "Reconciliation failed: $($failure -join '; ')"
