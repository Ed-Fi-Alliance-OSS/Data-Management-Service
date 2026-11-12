# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Gates the served identity wire document against the baseline of the last published identity contract.

.DESCRIPTION
    A thin entry point over IdentityWireContract.psm1, where the policy and its explanation live.
    Writes one object typed EdFi.IdentityWireContractDecision (Outcome, PublishedVersion,
    AnchorCommit) to the success stream, and fails with a message naming the problem otherwise.
    It never pushes.
#>
[CmdletBinding()]
param(
    # The normalized served document the host serves at the gated commit.
    [Parameter(Mandatory)]
    [string]
    $ServedDocumentPath,

    # The identity contract version, as Get-IdentityContractVersion reports it.
    [Parameter(Mandatory)]
    [string]
    $ContractVersion,

    [string]
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path,

    # Repo-relative path of the baseline file.
    [string]
    $BaselinePath = "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OpenApi/Fixtures/identity-v2-wire-baseline.json",

    [string]
    $PackageId = "EdFi.Api.Identity",

    [string]
    $ServiceIndexUrl = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",

    [string]
    $FeedApiKey = "",

    # Returns the feed's PackageBaseAddress/3.0.0 endpoint, or throws.
    [scriptblock]
    $ResolvePackageBaseAddress,

    # Returns @{ Found = <bool>; Versions = @(...) } for a package id, or throws.
    [scriptblock]
    $GetPublishedVersions,

    # Downloads one package to a path, or throws.
    [scriptblock]
    $SavePublishedPackage,

    # The nupkg packed at the gated commit; required when the contract version is an increment.
    [string]
    $PackedPackageFile = "",

    # Directory of <published>-to-<current>.json review records; defaults to
    # eng/verification/IdentityWireCompatibility.
    [string]
    $ReviewRecordDirectory
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "IdentityWireContract.psm1") -Force

$arguments = @{} + $PSBoundParameters

return Invoke-IdentityWireContractGate @arguments
