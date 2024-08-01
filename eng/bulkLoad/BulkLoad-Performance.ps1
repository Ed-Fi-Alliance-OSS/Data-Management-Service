# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.DESCRIPTION
    Measure Bulk Load Performance
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingInvokeExpression', '', Justification = 'This use is safe; the $Template is restricted to safe inputs.')]
param(
    [ValidateSet("GrandBend", "PartialGrandBend", "Southridge", "PartialGrandBend-ODSAPI")]
    $Template = "Southridge",

    [Switch]
    $Update,

    [string]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('ReviewUnusedParameter', '', Justification = 'false positive')]
    $Key = "minimalKey",

    [string]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('ReviewUnusedParameter', '', Justification = 'false positive')]
    $Secret = "minimalSecret",

    # 8080 is the default k8s port
    # 5198 is the default when running F5
    [string]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('ReviewUnusedParameter', '', Justification = 'false positive')]
    $BaseUrl = "http://localhost:8080",
    # For ODS/API. Don't run with self-signed SSL, as the bulk loader won't like that.
    #$BaseUrl = "http://localhost/api",

    # When false (default), only loads descriptors
    [switch]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('ReviewUnusedParameter', '', Justification = 'false positive')]
    $FullDataSet
)

if ($Update) {
    # Run First to create the data (Without measuring)
    Write-Output "Creating data"

    Invoke-Expression "./Invoke-Load$Template.ps1"
}

Write-Output "Starting Measure for $Template..."
$timing = Measure-Command {
    $command = "./Invoke-Load$($Template).ps1 -Key $Key -Secret $Secret -BaseUrl $BaseUrl"
    if ($FullDataSet) {
        $command += " -FullDataSet"
    }
    Invoke-Expression $command | Tee-Object ./$($Template).log
}

Write-Output "Total Time: $timing"
