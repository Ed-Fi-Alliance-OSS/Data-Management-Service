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

    Count mode reads dms.Document joined to dms.ResourceKey, which is the same grouping on both
    engines, so the two sides are comparable by construction.

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
    Reconcile mode: assert this total on both sides.

.PARAMETER ExpectedResourceCount
    Reconcile mode: assert this distinct resource count on both sides.

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

    [Parameter(ParameterSetName = "Reconcile")]
    [long]
    $ExpectedDocumentCount = 0,

    [Parameter(ParameterSetName = "Reconcile")]
    [int]
    $ExpectedResourceCount = 0
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
SELECT rk."ResourceName" || '|' || COUNT(*)::text
FROM dms."Document" d
JOIN dms."ResourceKey" rk ON rk."ResourceKeyId" = d."ResourceKeyId"
GROUP BY rk."ResourceName"
ORDER BY rk."ResourceName";
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

    # Emits the same 'name|count' shape as the PostgreSQL query so both sides parse identically.
    $sql = "SET NOCOUNT ON; " +
        "SELECT rk.ResourceName + '|' + CAST(COUNT_BIG(*) AS varchar(32)) " +
        "FROM dms.Document d JOIN dms.ResourceKey rk ON rk.ResourceKeyId = d.ResourceKeyId " +
        "GROUP BY rk.ResourceName ORDER BY rk.ResourceName;"

    # The running container is the authoritative source for the SA password: .env keeps
    # MSSQL_SA_PASSWORD commented out and the live value is resolved into the derived env file. Reading
    # it back avoids both a duplicated secret and a plaintext parameter on this script.
    $password = Get-ContainerEnvironmentValue -ContainerName $ContainerName -VariableName "MSSQL_SA_PASSWORD"

    # SQLCMDPASSWORD rather than -P, matching the repository convention: it keeps the password out of
    # the container argument list.
    $output = docker exec -e SQLCMDPASSWORD=$password $ContainerName /opt/mssql-tools18/bin/sqlcmd `
        -S localhost -U $User -C -N -b -d $DatabaseName -h -1 -W -Q $sql 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd failed against '$DatabaseName' (exit $LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }

    return $output
}

function ConvertTo-CountRow {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.List[object]])]
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Line)

    $rows = [System.Collections.Generic.List[object]]::new()

    foreach ($item in $Line) {
        $text = ([string]$item).Trim()
        if ([string]::IsNullOrWhiteSpace($text) -or -not $text.Contains("|")) {
            continue
        }

        $separatorIndex = $text.LastIndexOf("|")
        $name = $text.Substring(0, $separatorIndex).Trim()
        $countText = $text.Substring($separatorIndex + 1).Trim()

        $parsedCount = [long]0
        if (-not [long]::TryParse($countText, [ref] $parsedCount)) {
            continue
        }

        $rows.Add([pscustomobject]@{ ResourceName = $name; DocumentCount = $parsedCount })
    }

    return $rows
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

    $rows | Sort-Object -Property ResourceName | Export-Csv -LiteralPath $OutputPath -NoTypeInformation

    $total = ($rows | Measure-Object -Property DocumentCount -Sum).Sum
    Write-Output "Resources: $($rows.Count)"
    Write-Output "Documents: $total"
    Write-Output "Written to: $OutputPath"
    return
}

# ---------- Reconcile mode ----------

$left = Import-Csv -LiteralPath $LeftPath
$right = Import-Csv -LiteralPath $RightPath

$leftMap = @{}
foreach ($row in $left) { $leftMap[$row.ResourceName] = [long]$row.DocumentCount }

$rightMap = @{}
foreach ($row in $right) { $rightMap[$row.ResourceName] = [long]$row.DocumentCount }

$leftTotal = ($leftMap.Values | Measure-Object -Sum).Sum
$rightTotal = ($rightMap.Values | Measure-Object -Sum).Sum

$leftLabel = Split-Path -Path $LeftPath -Leaf
$rightLabel = Split-Path -Path $RightPath -Leaf

# A full outer join over the union of names: a resource missing from either side has to be visible,
# which a per-side loop over one map cannot show.
$allName = ($leftMap.Keys + $rightMap.Keys) | Sort-Object -Unique

$leftOnly = [System.Collections.Generic.List[object]]::new()
$rightOnly = [System.Collections.Generic.List[object]]::new()
$countDiffer = [System.Collections.Generic.List[object]]::new()

foreach ($name in $allName) {
    $inLeft = $leftMap.ContainsKey($name)
    $inRight = $rightMap.ContainsKey($name)

    if ($inLeft -and -not $inRight) {
        $leftOnly.Add([pscustomobject]@{ ResourceName = $name; Left = $leftMap[$name]; Right = $null })
    }
    elseif ($inRight -and -not $inLeft) {
        $rightOnly.Add([pscustomobject]@{ ResourceName = $name; Left = $null; Right = $rightMap[$name] })
    }
    elseif ($leftMap[$name] -ne $rightMap[$name]) {
        $countDiffer.Add([pscustomobject]@{
                ResourceName = $name
                Left         = $leftMap[$name]
                Right        = $rightMap[$name]
                Difference   = $rightMap[$name] - $leftMap[$name]
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
    Write-Output "  left-only : $($row.ResourceName) = $($row.Left)"
}
foreach ($row in $rightOnly) {
    Write-Output "  right-only: $($row.ResourceName) = $($row.Right)"
}
foreach ($row in $countDiffer) {
    Write-Output "  differs   : $($row.ResourceName) left=$($row.Left) right=$($row.Right) diff=$($row.Difference)"
}

if ($leftOnly.Count -gt 0) { $failure.Add("$($leftOnly.Count) resource(s) present only on the left") }
if ($rightOnly.Count -gt 0) { $failure.Add("$($rightOnly.Count) resource(s) present only on the right") }
if ($countDiffer.Count -gt 0) { $failure.Add("$($countDiffer.Count) shared resource(s) with unequal counts") }

# Totals are asserted separately from the per-resource diff. Equal totals with offsetting per-resource
# differences is a real failure mode, and checking only the total would pass it.
if ($ExpectedDocumentCount -gt 0) {
    if ($leftTotal -ne $ExpectedDocumentCount) { $failure.Add("left total $leftTotal <> expected $ExpectedDocumentCount") }
    if ($rightTotal -ne $ExpectedDocumentCount) { $failure.Add("right total $rightTotal <> expected $ExpectedDocumentCount") }
}
if ($ExpectedResourceCount -gt 0) {
    if ($leftMap.Count -ne $ExpectedResourceCount) { $failure.Add("left resources $($leftMap.Count) <> expected $ExpectedResourceCount") }
    if ($rightMap.Count -ne $ExpectedResourceCount) { $failure.Add("right resources $($rightMap.Count) <> expected $ExpectedResourceCount") }
}

Write-Output ""

if ($failure.Count -eq 0) {
    Write-Output "PASS: zero differences in both directions."
    return
}

foreach ($item in $failure) { Write-Output "FAIL: $item" }
throw "Reconciliation failed: $($failure -join '; ')"
