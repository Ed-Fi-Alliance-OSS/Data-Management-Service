# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.DESCRIPTION
    Measure Bulk Load Performance
#>
param(
    [ValidateSet("GrandBend", "PartialGrandBend", "Southridge")]
    $Template = "Southridge",

    [Switch]
    $Update
)

if($Update) {
  # Run First to create the data (Without measuring)
  Write-Host "Creating data"
  Invoke-Expression "./Invoke-Load$Template.ps1"
}

Write-Host "Starting Measure for $Template..."
$timing = Measure-Command { Invoke-Expression "./Invoke-Load$Template.ps1"  }

Write-Output "Total Time: $timing"
