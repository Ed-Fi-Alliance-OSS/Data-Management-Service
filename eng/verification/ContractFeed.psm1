# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.DESCRIPTION
The one shared copy of the NuGet feed seams used by the contract publish check and the identity
wire-contract gate: service-index resolution, published-version lookup and package download.

Each default returns a script block created inside this module, so the block is bound to this
module's scope and resolves Get-FeedRequestHeader and the HTTP cmdlets here wherever it is invoked.
Each one throws with the feed's own status in the message, so a lane failure names what the feed
said rather than only that something failed.
#>

$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    Builds the HTTP headers that authenticate a request to a NuGet feed.

.DESCRIPTION
    Returns an empty table when no key is given, otherwise a Basic Authorization header carrying the
    key as the password of a basic credential.
#>
function Get-FeedRequestHeader {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([string] $ApiKey)

    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        return @{}
    }

    # Azure Artifacts accepts a PAT as the password of a basic credential; the user name is ignored.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("nuget:$ApiKey"))

    return @{ Authorization = "Basic $encoded" }
}

<#
.SYNOPSIS
    Returns the default script block that resolves a feed's PackageBaseAddress/3.0.0 endpoint.

.DESCRIPTION
    The block reads the NuGet v3 service index and returns the PackageBaseAddress/3.0.0 resource,
    or throws with the feed's own status. Its success is what makes a later 404 from the package
    index mean "absent" rather than "unreadable".
#>
function Get-DefaultResolvePackageBaseAddress {
    [CmdletBinding()]
    [OutputType([scriptblock])]
    param()

    return {
        param([string] $IndexUrl, [string] $ApiKey)

        $headers = Get-FeedRequestHeader -ApiKey $ApiKey

        try {
            $response = Invoke-RestMethod -Uri $IndexUrl -Headers $headers -Method Get
        }
        catch {
            throw "The feed's service index at $IndexUrl could not be read: $($_.Exception.Message). A feed that cannot be read is not an empty feed."
        }

        $resource = @(
            $response.resources |
                Where-Object { $_.'@type' -like "PackageBaseAddress/3.0.0*" }
        )

        if ($resource.Count -eq 0) {
            throw "The feed's service index at $IndexUrl advertises no PackageBaseAddress/3.0.0 resource."
        }

        return $resource[0].'@id'
    }
}

<#
.SYNOPSIS
    Returns the default script block that lists a package id's published versions.

.DESCRIPTION
    The block returns @{ Found = <bool>; Versions = @(...) }. A 404 from the package index of a feed
    whose service index answered is Found = $false; any other failure or a malformed body throws.
#>
function Get-DefaultPublishedVersionLookup {
    [CmdletBinding()]
    [OutputType([scriptblock])]
    param()

    return {
        param([string] $BaseAddress, [string] $NormalizedId, [string] $ApiKey)

        $headers = Get-FeedRequestHeader -ApiKey $ApiKey
        $url = "$BaseAddress/$NormalizedId/index.json"

        try {
            $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__

            # 404 from the package index of a feed whose service index answered is the documented
            # way NuGet says "this id has never been published here".
            if ($status -eq 404) {
                return @{ Found = $false; Versions = @() }
            }

            throw "The feed answered $status for $url : $($_.Exception.Message). That is not an absent package."
        }

        # A 200 that carries no versions array is a malformed response rather than an empty package
        # index: the endpoint's whole contract is that array, and reading its absence as "no versions
        # published" would turn a broken feed into a push.
        if ($null -eq $response -or $null -eq $response.versions) {
            throw "The feed answered 200 for $url with no versions array. That is a malformed response, not an absent package."
        }

        # Not wrapped with @(). A scalar here means the endpoint returned something other than the
        # array it is defined to return, and auto-wrapping it would hide that behind a one-element
        # list.
        if ($response.versions -isnot [System.Collections.IEnumerable] -or $response.versions -is [string]) {
            throw "The feed answered 200 for $url with a versions property that is not an array. That is a malformed response, not an absent package."
        }

        return @{ Found = $true; Versions = $response.versions }
    }
}

<#
.SYNOPSIS
    Returns the default script block that downloads one published package to a path.

.DESCRIPTION
    The block downloads the flat-container nupkg for an id and version to the destination path, or
    throws with the feed's own status.
#>
function Get-DefaultSavePublishedPackage {
    [CmdletBinding()]
    [OutputType([scriptblock])]
    param()

    return {
        param([string] $BaseAddress, [string] $NormalizedId, [string] $NormalizedVersion, [string] $Destination, [string] $ApiKey)

        $headers = Get-FeedRequestHeader -ApiKey $ApiKey
        $url = "$BaseAddress/$NormalizedId/$NormalizedVersion/$NormalizedId.$NormalizedVersion.nupkg"

        try {
            Invoke-WebRequest -Uri $url -Headers $headers -Method Get -OutFile $Destination
        }
        catch {
            throw "The published package at $url could not be downloaded: $($_.Exception.Message). The version index listed this version, so a failure here is a feed fault rather than an absent package."
        }
    }
}

Export-ModuleMember -Function Get-DefaultResolvePackageBaseAddress, Get-DefaultPublishedVersionLookup, Get-DefaultSavePublishedPackage
