# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#requires -version 7

$ErrorActionPreference = "Stop"

<#
.DESCRIPTION
Builds a pre-release version number based on the last tag in the commit history
and the number of commits since then.
#>
function Get-VersionNumber {

    $prefix = "v"

    # Install the MinVer CLI tool
    &dotnet tool install --global minver-cli

    $version = $(&minver -t $prefix)

    "dms-v=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
    "dms-semver=$($version -Replace $prefix)" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
}

<#
.DESCRIPTION
Promotes a package in Azure Artifacts to a view, e.g. pre-release or release.
#>
function Invoke-Promote {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive')]
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    param(
        # NuGet Packages API URL
        [Parameter(Mandatory = $true)]
        [String]
        $PackagesURL,

        # Azure Artifacts user name
        [Parameter(Mandatory = $true)]
        [String]
        $Username,

        # Azure Artifacts password
        [Parameter(Mandatory = $true)]
        [SecureString]
        $Password,

        # View to promote into
        [Parameter(Mandatory = $true)]
        [String]
        $ViewId,

        # Git ref (short) for the release tag ex: v1.3.5
        [Parameter(Mandatory = $true)]
        $ReleaseRef
    )


    $package = "EdFi.DataManagementService"
    $version = $ReleaseRef -replace "v", ""

    $PackagesURL = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/$package/versions/$version/views/$ViewId?api-version=7.1"

    $parameters = @{
        Method      = "POST"
        ContentType = "application/json"
        Credential  = [PSCredential]::new($Username, $Password)
        URI         = $PackagesURL
    }

    Write-Output "Web request parameters:"
    $parameters | Out-Host

    if ($PSCmdlet.ShouldProcess($PackagesURL)) {
        $response = Invoke-RestMethod @parameters -UseBasicParsing
        $response | ConvertTo-Json -Depth 10 | Out-Host
    }
}

Export-ModuleMember -Function Get-VersionNumber, Invoke-Promote
