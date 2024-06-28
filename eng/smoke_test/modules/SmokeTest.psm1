# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
$ErrorActionPreference = "Stop"

function Invoke-SmokeTestUtility {

    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $BaseUrl,

        [string]
        [Parameter(Mandatory = $True)]
        $Key,

        [string]
        [Parameter(Mandatory = $True)]
        $Secret,

        [string]
        [Parameter(Mandatory = $True)]
        $ToolPath,

        [ValidateSet("NonDestructiveApi", "NonDestructiveSdk", "DestructiveSdk")]
        [Parameter(Mandatory = $True)]
        $TestSet,

        [string]
        $SdkPath
    )

    $options = @(
        "-b", $BaseUrl,
        "-k", $Key,
        "-s", $Secret,
        "-t", $TestSet
    )

    if($TestSet -ne "NonDestructiveApi")
    {
        if(!$SdkPath)
        {
            Write-Error "Please provide valid SDK path"
            return
        }
        $options += @("-l", $SdkPath)
    }

    $previousForegroundColor = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = "Cyan"
    Write-Output $ToolPath $options
    $host.UI.RawUI.ForegroundColor = $previousForegroundColor

    $path = (Join-Path -Path ($ToolPath).Trim() -ChildPath "tools/net8.0/any/EdFi.SmokeTest.Console.dll")
    &dotnet $path $options
}

Export-ModuleMember *
