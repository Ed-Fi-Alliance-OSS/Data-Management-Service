# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    The power sampler a collection lane starts in its own process (spec section 18.2).
.DESCRIPTION
    Creates the optional counters and fixes the role processes once, then observes the power
    source once a second: each tick appends a power row to -PowerPath first and an optional row to
    -OptionalPath after it. The first row is followed by the ready record at -ReadyPath. When
    -StopPath appears, one last tick runs and the script exits 0. Any failure writes its text to
    -DiagnosticsPath (the work folder) and exits 1.

    Roles: -CmsProcessId with -CmsStartTicks (the process the lane launched), -LocalDbInstance (the
    single sqlservr whose command line names that instance; the command line is never written) and
    -DockerVm (the single vmmemWSL or vmmem process). Nothing else on the host is named.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RunId,
    [Parameter(Mandatory)] [string] $RunLabel,
    [Parameter(Mandatory)] [string] $LaneStamp,
    [Parameter(Mandatory)] [string] $PowerPath,
    [Parameter(Mandatory)] [string] $OptionalPath,
    [Parameter(Mandatory)] [string] $ReadyPath,
    [Parameter(Mandatory)] [string] $StopPath,
    [Parameter(Mandatory)] [string] $DiagnosticsPath,
    [int] $CmsProcessId = 0,
    [long] $CmsStartTicks = 0,
    [string] $LocalDbInstance = '',
    [switch] $DockerVm
)

$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Force
    $role = [ordered]@{}
    if ($CmsProcessId -gt 0) { $role.cms = @{ pid = $CmsProcessId; start_ticks = $CmsStartTicks } }
    if ($LocalDbInstance -ne '') { $role.localdb = @{ instance = $LocalDbInstance } }
    if ($DockerVm) { $role.docker_vm = @{} }

    $self = [System.Diagnostics.Process]::GetCurrentProcess()
    $state = Initialize-HostSampleState -Role $role -Qpc ([System.Diagnostics.Stopwatch]::GetTimestamp()) -QpcFrequency ([System.Diagnostics.Stopwatch]::Frequency)
    $identity = [ordered]@{
        run_label         = $RunLabel
        lane_stamp        = $LaneStamp
        sampler_pid       = $self.Id
        sampler_start_utc = $self.StartTime.ToUniversalTime().ToString('o')
    }
    Invoke-PowerSampler -RunId $RunId -Identity $identity -Role (Get-HostSampleRole -State $state) -OptionalColumn (Get-HostSampleColumn -State $state) `
        -ReadOptional { param([long] $Qpc) Read-HostSample -State $state -Qpc $Qpc } `
        -PowerPath $PowerPath -OptionalPath $OptionalPath -ReadyPath $ReadyPath -StopPath $StopPath
    exit 0
}
catch {
    "$($_ | Out-String)" | Out-File -LiteralPath $DiagnosticsPath -Append -Encoding utf8NoBOM
    exit 1
}
