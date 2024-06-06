# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Download standard XSD files from a running Ed-Fi ODS/API, so that they can
    be used with bulk upload into Meadowlark.
#>
function Get-EdFiXsd {
    param(
        [string]
        $OdsApiBaseUrl = "https://api.ed-fi.org/v7.1/api/",

        [string]
        $XsdDirectory = ".packages/XSD"
    )

    Write-Host "Downloading XSD files..."

    $serviceBase = Invoke-RestMethod $OdsApiBaseUrl
    $xsdMetadata = $serviceBase.urls.xsdMetadata
    $xsdResource = Invoke-RestMethod $xsdMetadata

    $edFiXsd = $xsdResource | Where-Object { $_.name -eq "ed-fi" } | Select-Object "files" -ExpandProperty "files"

    $xsdFiles = Invoke-RestMethod $edFiXsd


    $xsdFiles | ForEach-Object {
        $xsd = $_
        $fileName = $xsd.Split("/")[-1]
        $output = "$($XsdDirectory)/$($fileName)"

        if ($false -eq (Test-Path $output)) {
            Write-host "Downloading $xsd"
            Invoke-RestMethod $xsd -OutFile $output
        }
    }

    Write-Host "All XSD files have been retrieved."

    return $XsdDirectory
}

Export-ModuleMember -Function Get-EdFiXsd
