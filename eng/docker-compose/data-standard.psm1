# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

function Get-DataStandardRepo {
    <#
    .SYNOPSIS
        Materializes a pinned tag of the Ed-Fi-Data-Standard GitHub repository into a local cache
        directory and returns its absolute root path.

    .DESCRIPTION
        Downloads the GitHub source tarball for the requested tag, extracts it under
        <CacheRoot>/<RefTag>/, and writes a .fetched-ok marker file on success. Subsequent calls
        with the same RefTag short-circuit and return the cached path immediately.

        Bootstrap delivery uses the v5.2.0 tag as the single source of truth for:
          - Descriptors/         (bulk-load descriptor XMLs)
          - Samples/Sample XML/  (bulk-load resource XMLs + sample-only descriptors)
          - Schemas/Bulk/        (XSDs for bulk-load XML validation)

        Replaces the previous NuGet-package source (EdFi.DataStandard.SampleData), which lagged
        the live data standard and forced the bulk loader to skip XSD validation (-n).

    .PARAMETER RefTag
        Git tag in the Ed-Fi-Data-Standard repo (e.g. "v5.2.0"). Used both for the URL and for
        the cache subdirectory name.

    .PARAMETER CacheRoot
        Absolute path to the cache root directory (typically <BootstrapRoot>/data-standard).
        Created if missing.

    .PARAMETER FetchInvoker
        Test seam: when supplied, the scriptblock is invoked with ($zipUrl, $destZipPath) instead
        of Invoke-WebRequest. Implementations must produce a zip file at $destZipPath whose layout
        matches the GitHub source tarball.

    .OUTPUTS
        [string] Absolute path to the extracted repository root (<CacheRoot>/<RefTag>/).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper — no -WhatIf surface needed.')]
    param(
        [Parameter(Mandatory)] [string]$RefTag,
        [Parameter(Mandatory)] [string]$CacheRoot,
        [scriptblock]$FetchInvoker = $null
    )

    if ([string]::IsNullOrWhiteSpace($RefTag)) {
        throw "Get-DataStandardRepo: RefTag is required."
    }
    if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
        throw "Get-DataStandardRepo: CacheRoot is required."
    }

    $targetDir = Join-Path $CacheRoot $RefTag
    $markerPath = Join-Path $targetDir ".fetched-ok"

    if (Test-Path -LiteralPath $markerPath) {
        return $targetDir
    }

    # Wipe partial state (no marker but dir exists implies an interrupted prior fetch).
    if (Test-Path -LiteralPath $targetDir) {
        Remove-Item -LiteralPath $targetDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null

    $zipUrl = "https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-Data-Standard/archive/refs/tags/$RefTag.zip"
    $tempZip = Join-Path ([System.IO.Path]::GetTempPath()) "edfi-ds-$RefTag-$([Guid]::NewGuid().ToString('N')).zip"
    $tempExtract = Join-Path ([System.IO.Path]::GetTempPath()) "edfi-ds-$RefTag-$([Guid]::NewGuid().ToString('N'))"

    try {
        if ($null -ne $FetchInvoker) {
            & $FetchInvoker $zipUrl $tempZip
        }
        else {
            $ProgressPreference = "SilentlyContinue"
            Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -UseBasicParsing
        }

        if (-not (Test-Path -LiteralPath $tempZip -PathType Leaf)) {
            throw "Get-DataStandardRepo: tarball fetch did not produce a file at $tempZip."
        }

        Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

        # GitHub tarballs unpack into a single inner directory named "<repo>-<ref>" (e.g.
        # Ed-Fi-Data-Standard-5.2.0/). Locate that inner directory and rename it to the stable
        # target so callers see a deterministic path regardless of the tarball's naming convention.
        $innerDirs = @(Get-ChildItem -LiteralPath $tempExtract -Directory)
        if ($innerDirs.Count -ne 1) {
            throw "Get-DataStandardRepo: expected exactly one inner directory in the tarball, found $($innerDirs.Count) under $tempExtract."
        }

        Move-Item -LiteralPath $innerDirs[0].FullName -Destination $targetDir

        Set-Content -LiteralPath $markerPath -Value "fetched $(Get-Date -Format o) from $zipUrl" -Encoding utf8
    }
    finally {
        if (Test-Path -LiteralPath $tempZip) {
            Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $tempExtract) {
            Remove-Item -LiteralPath $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    return $targetDir
}

Export-ModuleMember -Function Get-DataStandardRepo
