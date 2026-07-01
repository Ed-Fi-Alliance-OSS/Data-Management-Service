# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

# Guarded fallback so the module stays usable without bootstrap-manifest.psm1 on the path
# (e.g. when the module is dot-sourced in isolation or imported by name).
if (-not (Get-Command Format-LogSafeText -ErrorAction SilentlyContinue))
{
    function script:Format-LogSafeText
    {
        <#
        .SYNOPSIS
        Sanitizes a value for safe inclusion in log output (whitelist of letters, digits, and safe punctuation).
        #>
        param(
            $Value
        )

        if ($null -eq $Value)
        {
            return ""
        }

        $text = [string]$Value
        if ([string]::IsNullOrEmpty($text))
        {
            return ""
        }

        $builder = [System.Text.StringBuilder]::new()
        foreach ($character in $text.ToCharArray())
        {
            if ([char]::IsLetterOrDigit($character) -or
                $character -eq " " -or
                $character -eq "_" -or
                $character -eq "-" -or
                $character -eq "." -or
                $character -eq ":" -or
                $character -eq "/")
            {
                $null = $builder.Append($character)
            }
        }

        return $builder.ToString()
    }
}

$script:StandardFeedUrl = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json"
$script:StandardCorePackageId = "EdFi.DataStandard52.ApiSchema"
$script:StandardCoreProjectName = "Ed-Fi"
$script:StandardCoreEndpointName = "ed-fi"
$script:StandardPackageVersion = "1.0.329"

# Known extension claims metadata keyed by Title-cased project name (matching the projectName
# field in bootstrap-api-schema-manifest.json, which sources its value from ApiSchema.json).
#
# NamespacePrefix is recorded ONLY for extensions whose resources carry a DISTINCT seed namespace,
# which becomes an authorized vendor namespace on the SeedLoader credential. Per
# 00-schema-and-security-selection.md, bootstrap records only known built-in prefixes and must NOT
# infer them from schema content. Sample resources use uri://sample.ed-fi.org (SampleExtension.feature
# authorizes Sample with "uri://ed-fi.org, uri://sample.ed-fi.org"), so its prefix is recorded.
# Homograph resources use the core uri://ed-fi.org namespace (HomographExtension.feature authorizes
# Homograph with "uri://ed-fi.org" only) and its claims use NoFurtherAuthorizationRequired, so
# Homograph intentionally records NO distinct namespace prefix.
$script:KnownExtensionClaimsMetadata = @{
    "Sample" = @{
        FragmentFileName = "004-sample-extension-claimset.json"
        NamespacePrefix  = "uri://sample.ed-fi.org"
    }
    "Homograph" = @{
        # No NamespacePrefix by design: Homograph resources use the core uri://ed-fi.org namespace
        # (see the block comment above). Recording uri://homograph.ed-fi.org would authorize a
        # namespace no Homograph resource uses and would violate the "must not infer prefixes" rule.
        FragmentFileName = "005-homograph-extension-claimset.json"
    }
    "TPDM" = @{
        # No FragmentFileName by design: unlike the Sample/Homograph test extensions, the TPDM
        # claims hierarchy (domains/tpdm and its resource claims) ships EMBEDDED in the CMS base
        # claims (Claims/Standards/ds52/Claims.json), so there is no bootstrap-staged fragment.
        # No NamespacePrefix: TPDM resources use the core uri://ed-fi.org namespace. TPDM is a
        # DS 5.2 extension only; in DS 6.1 the TPDM model is folded into core and no separate
        # extension package exists.
    }
}

function Get-StandardSchemaFeed {
    <#
    .SYNOPSIS
    Returns the pinned default NuGet feed URL for Ed-Fi standard ApiSchema packages.
    #>
    return $script:StandardFeedUrl
}

function Get-StandardCorePackage {
    <#
    .SYNOPSIS
    Returns the core ApiSchema package identity (id, version, and canonical project/endpoint
    tokens) as a PSCustomObject. ProjectToken and EndpointToken are the projectName and
    projectEndpointName the core package manifest must declare; consumers assert them so a
    mislabeled core package fails fast.
    #>
    return [pscustomobject]@{
        Id            = $script:StandardCorePackageId
        Version       = $script:StandardPackageVersion
        ProjectToken  = $script:StandardCoreProjectName
        EndpointToken = $script:StandardCoreEndpointName
    }
}

function Get-StandardKnownExtensionInfo {
    <#
    .SYNOPSIS
    Returns bootstrap-managed claims metadata for extensions that ship one, or $null for
    extensions that don't have known metadata.

    .DESCRIPTION
    Used by prepare-dms-claims.ps1 to auto-stage claim fragments for extensions present in a staged
    schema set. Known extensions return a hashtable with an optional FragmentFileName (absent for
    TPDM, whose claims ship embedded in the CMS base claims) and an optional NamespacePrefix.
    Extensions absent from the known map return $null - this is by design and does not indicate an
    error; such an extension simply requires a caller-supplied ClaimsDirectoryPath. This lookup
    serves the claims phase regardless of how the schema set was staged (expert -ApiSchemaPath or
    standard-mode SCHEMA_PACKAGES staging).

    .PARAMETER ProjectName
    The projectName as recorded in the ApiSchema manifest (e.g. "Sample", "Homograph", "TPDM").
    Case-sensitive match against the known metadata map.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]
        $ProjectName
    )

    if ($script:KnownExtensionClaimsMetadata.ContainsKey($ProjectName)) {
        return $script:KnownExtensionClaimsMetadata[$ProjectName]
    }

    return $null
}

Export-ModuleMember -Function `
    Get-StandardSchemaFeed, `
    Get-StandardCorePackage, `
    Get-StandardKnownExtensionInfo
