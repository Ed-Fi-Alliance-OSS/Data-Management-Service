# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.


#requires -version 7

$ErrorActionPreference = "Stop"

function Get-VersionNumber {

    $prefix = "v"

    # Install the MinVer CLI tool
    &dotnet tool install --global minver-cli

    $version = $(&minver -t $prefix)

    "dms-v=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
    "dms-semver=$($version -Replace $prefix)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
}

function Invoke-DotnetPack {
    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $true)]
        $Version
    )

    &dotnet pack -p:PackageVersion=$Version -o ./
}

function Invoke-NuGetPush {
    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $true)]
        $NuGetFeed,

        [string]
        [Parameter(Mandatory = $true)]
        $NuGetApiKey
    )

    (Get-ChildItem -Path $_ -Name -Include *.nupkg) | ForEach-Object {
        &dotnet nuget push $_ --api-key $NuGetApiKey --source $NuGetFeed
    }
}


function Get-PackagesFromAzure {

    $uri = "$FeedsURL/packages?api-version=6.0-preview.1"
    $result = @{ }

    foreach ($packageName in $Packages) {
        $packageQueryUrl = "$uri&packageNameQuery=$packageName"
        $packagesResponse = (Invoke-WebRequest -Uri $packageQueryUrl -UseBasicParsing).Content | ConvertFrom-Json
        $latestPackageVersion = ($packagesResponse.value.versions | Where-Object { $_.isLatest -eq $True } | Select-Object -ExpandProperty version)

        Write-Output "Package Name: $packageName"
        Write-Output "Package Version: $latestPackageVersion"

        $result.add(
            $packageName.ToLower().Trim(),
            $latestPackageVersion
        )
    }
    return $result
}

function Invoke-Promote {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive')]
    param(
        [Parameter(Mandatory = $true)]
        [String]
        $FeedsURL,

        [Parameter(Mandatory = $true)]
        [String]
        $PackagesURL,

        [Parameter(Mandatory = $true)]
        [String]
        $Username,

        [Parameter(Mandatory = $true)]
        [SecureString]
        $Password,

        [Parameter(Mandatory = $true)]
        [String]
        $View
    )


    $body = @{
        data      = @{
            viewId = $View
        }
        operation = 0
        packages  = @("EdFi.DataManagement.Service")
    }

    $latestPackages = Get-PackagesFromAzure

    foreach ($key in $latestPackages.Keys) {
        $body.packages += @{
            id           = $key
            version      = $latestPackages[$key]
            protocolType = "NuGet"
        }
    }

    $parameters = @{
        Method      = "POST"
        ContentType = "application/json"
        Credential  = New-Object -TypeName PSCredential -ArgumentList $Username, $Password
        URI         = "$PackagesURL/nuget/packagesBatch?api-version=5.0-preview.1"
        Body        = ConvertTo-Json $Body -Depth 10
    }

    $parameters | Out-Host
    $parameters.URI | Out-Host
    $parameters.Body | Out-Host

    $response = Invoke-WebRequest @parameters -UseBasicParsing
    $response | ConvertTo-Json -Depth 10 | Out-Host
}

Export-ModuleMember -Function Get-VersionNumber, Invoke-DotnetPack, Invoke-NuGetPush, Invoke-Promote
