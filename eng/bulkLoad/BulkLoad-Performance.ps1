# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.DESCRIPTION
    Measure Bulk Load Performance
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingInvokeExpression', '', Justification='This use is safe; the $Template is restricted to safe inputs.')]
param(
    [ValidateSet("GrandBend", "PartialGrandBend", "Southridge")]
    $Template = "Southridge",

    [Switch]
    $Update
)

if($Update) {
  # Run First to create the data (Without measuring)
  Write-Output "Creating data"

  Invoke-Expression "./Invoke-Load$Template.ps1"
}

Write-Output "Starting Measure for $Template..."
$timing = Measure-Command { Invoke-Expression "./Invoke-Load$Template.ps1"  }

Write-Output "Total Time: $timing"
