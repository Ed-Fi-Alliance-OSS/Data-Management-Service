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

function Resolve-NupkgFileName
{
    <#
    .SYNOPSIS
    Returns the expected lowercased NuGet flat-container filename for a given package id and version.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $PackageId,

        [Parameter(Mandatory)]
        [string]
        $Version
    )

    return "$($PackageId.ToLowerInvariant()).$($Version.ToLowerInvariant()).nupkg"
}

function Expand-Nupkg
{
    <#
    .SYNOPSIS
    Extracts a .nupkg (zip) file into a target directory using System.IO.Compression.ZipFile.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper; callers do not expose -WhatIf end to end.')]
    param(
        [Parameter(Mandatory)]
        [string]
        $NupkgPath,

        [Parameter(Mandatory)]
        [string]
        $DestinationDirectory
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop

    try
    {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($NupkgPath, $DestinationDirectory)
    }
    catch
    {
        throw "Failed to extract package '$(Format-LogSafeText $NupkgPath)' into '$(Format-LogSafeText $DestinationDirectory)': $(Format-LogSafeText ($_.Exception.Message))"
    }
}

function Resolve-LocalFolderPackage
{
    <#
    .SYNOPSIS
    Locates and returns the absolute path to a .nupkg in a local folder feed, matching by
    package id and version (case-insensitively, using NuGet's lowercased id.version.nupkg naming).
    Throws a diagnostics error when the package or version is not found.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $FolderPath,

        [Parameter(Mandatory)]
        [string]
        $PackageId,

        [Parameter(Mandatory)]
        [string]
        $Version
    )

    if (-not (Test-Path -LiteralPath $FolderPath -PathType Container))
    {
        throw "Local feed folder not found: $(Format-LogSafeText $FolderPath)"
    }

    $expectedFileName = Resolve-NupkgFileName -PackageId $PackageId -Version $Version

    # Scan all .nupkg files in the folder (case-insensitive filename match).
    $candidates = @(
        Get-ChildItem -LiteralPath $FolderPath -Filter "*.nupkg" -File |
            Where-Object { $_.Name.Equals($expectedFileName, [System.StringComparison]::OrdinalIgnoreCase) }
    )

    if ($candidates.Count -eq 0)
    {
        # Distinguish between "package not found at all" and "package found but wrong version".
        $idLower = $PackageId.ToLowerInvariant()
        $anyVersion = @(
            Get-ChildItem -LiteralPath $FolderPath -Filter "*.nupkg" -File |
                Where-Object { $_.Name.StartsWith("$idLower.", [System.StringComparison]::OrdinalIgnoreCase) }
        )

        if ($anyVersion.Count -eq 0)
        {
            throw "Package '$(Format-LogSafeText $PackageId)' was not found in local feed folder '$(Format-LogSafeText $FolderPath)'."
        }

        throw "Package '$(Format-LogSafeText $PackageId)' version '$(Format-LogSafeText $Version)' was not found in local feed folder '$(Format-LogSafeText $FolderPath)'. Available versions may differ - pinned version resolution never falls back to latest."
    }

    return $candidates[0].FullName
}

function Resolve-HttpV3Package
{
    <#
    .SYNOPSIS
    Downloads a .nupkg from an HTTP NuGet v3 feed (service index URL), following 303 redirects,
    and saves it to a temporary file. Returns the path to the downloaded .nupkg.
    Throws diagnostics errors on unreachable feed, missing package id, or missing version.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper; callers do not expose -WhatIf end to end.')]
    param(
        [Parameter(Mandatory)]
        [string]
        $ServiceIndexUrl,

        [Parameter(Mandatory)]
        [string]
        $PackageId,

        [Parameter(Mandatory)]
        [string]
        $Version,

        [Parameter(Mandatory)]
        [string]
        $DownloadDirectory
    )

    # Fetch the service index JSON.
    try
    {
        $serviceIndexResponse = Invoke-WebRequest -Uri $ServiceIndexUrl -UseBasicParsing -ErrorAction Stop
    }
    catch
    {
        throw "NuGet feed service index is unreachable at '$(Format-LogSafeText $ServiceIndexUrl)': $(Format-LogSafeText ($_.Exception.Message))"
    }

    try
    {
        $serviceIndex = $serviceIndexResponse.Content | ConvertFrom-Json -AsHashtable
    }
    catch
    {
        throw "NuGet feed service index at '$(Format-LogSafeText $ServiceIndexUrl)' returned malformed JSON: $(Format-LogSafeText ($_.Exception.Message))"
    }

    # Locate the PackageBaseAddress/3.0.0 resource (flat container base). Member access goes through
    # hashtable ContainsKey so a service index missing 'resources'/'@type'/'@id' fails with the
    # diagnostic below rather than a raw StrictMode property-not-found error.
    $flatContainerBase = $null
    if ($serviceIndex -is [System.Collections.IDictionary] -and $serviceIndex.ContainsKey("resources"))
    {
        foreach ($resource in @($serviceIndex["resources"]))
        {
            if ($resource -is [System.Collections.IDictionary] -and
                $resource.ContainsKey("@type") -and
                $resource["@type"] -eq "PackageBaseAddress/3.0.0" -and
                $resource.ContainsKey("@id") -and
                -not [string]::IsNullOrWhiteSpace([string]$resource["@id"]))
            {
                $flatContainerBase = ([string]$resource["@id"]).TrimEnd("/")
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($flatContainerBase))
    {
        throw "NuGet service index at '$(Format-LogSafeText $ServiceIndexUrl)' does not advertise a PackageBaseAddress/3.0.0 resource."
    }

    $idLower = $PackageId.ToLowerInvariant()
    $versionLower = $Version.ToLowerInvariant()
    $nupkgFileName = Resolve-NupkgFileName -PackageId $PackageId -Version $Version
    $nupkgUrl = "$flatContainerBase/$idLower/$versionLower/$nupkgFileName"

    # Verify the package exists by checking the version index first.
    $versionIndexUrl = "$flatContainerBase/$idLower/index.json"
    try
    {
        $versionIndexResponse = Invoke-WebRequest -Uri $versionIndexUrl -UseBasicParsing -ErrorAction Stop
    }
    catch
    {
        throw "Package '$(Format-LogSafeText $PackageId)' was not found on the NuGet feed at '$(Format-LogSafeText $ServiceIndexUrl)'. The version index request failed: $(Format-LogSafeText ($_.Exception.Message))"
    }

    try
    {
        $versionIndex = $versionIndexResponse.Content | ConvertFrom-Json -AsHashtable
    }
    catch
    {
        throw "Package '$(Format-LogSafeText $PackageId)' version index at '$(Format-LogSafeText $versionIndexUrl)' returned malformed JSON: $(Format-LogSafeText ($_.Exception.Message))"
    }

    $availableVersions = if ($versionIndex -is [System.Collections.IDictionary] -and $versionIndex.ContainsKey("versions"))
    {
        @($versionIndex["versions"])
    }
    else
    {
        @()
    }
    $versionFound = $availableVersions | Where-Object { $_.Equals($Version, [System.StringComparison]::OrdinalIgnoreCase) }

    if ($null -eq $versionFound -or @($versionFound).Count -eq 0)
    {
        throw "Package '$(Format-LogSafeText $PackageId)' version '$(Format-LogSafeText $Version)' was not found on the NuGet feed. Pinned version resolution never falls back to latest."
    }

    # Download the .nupkg, following redirects (Invoke-WebRequest follows by default).
    $downloadPath = Join-Path $DownloadDirectory $nupkgFileName

    try
    {
        Invoke-WebRequest -Uri $nupkgUrl -OutFile $downloadPath -UseBasicParsing -ErrorAction Stop
    }
    catch
    {
        throw "Failed to download package '$(Format-LogSafeText $PackageId)' version '$(Format-LogSafeText $Version)' from '$(Format-LogSafeText $nupkgUrl)': $(Format-LogSafeText ($_.Exception.Message))"
    }

    return $downloadPath
}

function Resolve-StandardSchemaPackage
{
    <#
    .SYNOPSIS
    Resolves and extracts an asset-only ApiSchema NuGet package.

    .DESCRIPTION
    Supports local folder feeds and HTTP NuGet v3 feeds. The package is extracted into a
    package-specific directory under DestinationRoot, then validated for the required
    contentFiles/any/any/ApiSchema contract.

    .PARAMETER FeedUrl
    NuGet v3 service index URL, local filesystem path, or file:// URL.

    .PARAMETER PackageId
    NuGet package ID to resolve.

    .PARAMETER Version
    Pinned package version. Resolution never falls back to latest.

    .PARAMETER DestinationRoot
    Root directory for package-specific extraction directories.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]
        $FeedUrl,

        [Parameter(Mandatory)]
        [string]
        $PackageId,

        [Parameter(Mandatory)]
        [string]
        $Version,

        [Parameter(Mandatory)]
        [string]
        $DestinationRoot
    )

    # Determine feed mode.
    $isLocalFeed = $false
    $localFolderPath = $null

    if ($FeedUrl.StartsWith("file://", [System.StringComparison]::OrdinalIgnoreCase))
    {
        # file:// URL - convert to a local path.
        try
        {
            $uri = [System.Uri]::new($FeedUrl)
            $localFolderPath = $uri.LocalPath
        }
        catch
        {
            throw "FeedUrl '$(Format-LogSafeText $FeedUrl)' is not a valid file:// URL: $(Format-LogSafeText ($_.Exception.Message))"
        }

        $isLocalFeed = $true
    }
    elseif (Test-Path -LiteralPath $FeedUrl -PathType Container)
    {
        # Plain directory path.
        $localFolderPath = $FeedUrl
        $isLocalFeed = $true
    }

    # Create the package-specific isolation directory under DestinationRoot.
    $isolationDir = Join-Path $DestinationRoot $PackageId
    New-Item -ItemType Directory -Path $isolationDir -Force | Out-Null

    if ($isLocalFeed)
    {
        $nupkgPath = Resolve-LocalFolderPackage `
            -FolderPath $localFolderPath `
            -PackageId $PackageId `
            -Version $Version

        Write-Verbose "Resolved package '$(Format-LogSafeText $PackageId)' version '$(Format-LogSafeText $Version)' from local feed: $(Format-LogSafeText $nupkgPath)"
    }
    else
    {
        # HTTP v3 feed: download to the isolation dir then extract in place.
        $nupkgPath = Resolve-HttpV3Package `
            -ServiceIndexUrl $FeedUrl `
            -PackageId $PackageId `
            -Version $Version `
            -DownloadDirectory $isolationDir

        Write-Verbose "Downloaded package '$(Format-LogSafeText $PackageId)' version '$(Format-LogSafeText $Version)' from HTTP feed."
    }

    # Extract the .nupkg (a zip) into the isolation directory.
    Expand-Nupkg -NupkgPath $nupkgPath -DestinationDirectory $isolationDir

    # Only the HTTP path downloads the .nupkg into the isolation dir; remove that download artifact
    # after extraction. Local-feed packages are read in place from the feed folder (outside the
    # isolation dir), so there is nothing to clean up for them.
    if (-not $isLocalFeed -and (Test-Path -LiteralPath $nupkgPath -PathType Leaf))
    {
        Remove-Item -LiteralPath $nupkgPath -Force -ErrorAction SilentlyContinue
    }

    # Verify the asset-only contract path exists.
    $apiSchemaDir = Join-Path $isolationDir "contentFiles/any/any/ApiSchema"
    if (-not (Test-Path -LiteralPath $apiSchemaDir -PathType Container))
    {
        throw "Package '$(Format-LogSafeText $PackageId)' version '$(Format-LogSafeText $Version)' was extracted to '$(Format-LogSafeText $isolationDir)' but the expected asset-only contract path 'contentFiles/any/any/ApiSchema/' was not found. Verify the package is an asset-only ApiSchema NuGet package."
    }

    Write-Verbose "Resolved package '$(Format-LogSafeText $PackageId)' version '$(Format-LogSafeText $Version)' to '$(Format-LogSafeText $isolationDir)'"

    return [pscustomobject]@{
        PackageId           = $PackageId
        Version             = $Version
        ExtractionDirectory = $isolationDir
        ApiSchemaDirectory  = $apiSchemaDir
        PackageRoot         = $isolationDir
    }
}

function Assert-SafeManifestRelativePath
{
    <#
    .SYNOPSIS
    Rejects a manifest-declared relative path that is rooted/absolute or contains a '..' segment,
    which would let a package's declared asset escape the asset-only contract directory.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $RelativePath,

        [Parameter(Mandatory)]
        [string]
        $FieldName,

        [Parameter(Mandatory)]
        [string]
        $ExpectedPackageId
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath))
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest '$FieldName' must be a relative path inside the package, but '$(Format-LogSafeText $RelativePath)' is rooted."
    }

    if (($RelativePath -split '[\\/]+') -contains "..")
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest '$FieldName' must not contain '..' path segments, but '$(Format-LogSafeText $RelativePath)' does. Asset-only payloads must stay within 'contentFiles/any/any/ApiSchema/'."
    }
}

function Assert-AssetOnlyPackageContract
{
    <#
    .SYNOPSIS
    Validates an extracted asset-only ApiSchema NuGet package against the Story 05 contract.
    Throws a clear, fail-fast message on any violation.

    .DESCRIPTION
    Validates:
    - Required asset-only payload: contentFiles/any/any/ApiSchema/ must exist and contain ApiSchema.json.
    - Exactly one schema JSON file at the contract path.
    - Valid package-manifest.json with all required fields.
    - No forbidden DLL/assembly shape entries (lib/, ref/, bin/, obj/, *.dll, *.cs).
    - Identity match: manifest packageId must equal ExpectedPackageId (case-insensitive);
      manifest isExtensionProject must equal ExpectedIsExtension.
    - Manifest-declared static assets (discoverySpecPath, xsdDirectory) must exist on disk when non-null.
    - No duplicate normalized relative paths within the package payload.

    .PARAMETER ApiSchemaDirectory
    Absolute path to the contentFiles/any/any/ApiSchema/ directory of the extracted package.

    .PARAMETER PackageRoot
    Absolute path to the package extraction root directory.

    .PARAMETER ExpectedPackageId
    The NuGet package ID that was requested (used for identity validation).

    .PARAMETER ExpectedIsExtension
    Whether the package is expected to be an extension package.

    .OUTPUTS
    PSCustomObject with validated identity:
      - PackageId: manifest packageId
      - ProjectName: manifest projectName
      - ProjectEndpointName: manifest projectEndpointName
      - IsExtensionProject: manifest isExtensionProject
      - SchemaPath: absolute path to the schema JSON file
      - DiscoverySpecPath: absolute path to discovery-spec.json, or $null
      - XsdDirectory: absolute path to xsd directory, or $null
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]
        $ApiSchemaDirectory,

        [Parameter(Mandatory)]
        [string]
        $PackageRoot,

        [Parameter(Mandatory)]
        [string]
        $ExpectedPackageId,

        [Parameter(Mandatory)]
        [bool]
        $ExpectedIsExtension
    )

    # --- 1. Verify asset-only contract directory exists ---
    if (-not (Test-Path -LiteralPath $ApiSchemaDirectory -PathType Container))
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': asset-only contract directory 'contentFiles/any/any/ApiSchema/' was not found at '$(Format-LogSafeText $ApiSchemaDirectory)'."
    }

    # --- 2. Check for forbidden DLL/assembly shape entries anywhere in the payload ---
    # Forbidden assembly-shape directories (lib/, ref/, bin/, obj/) are rejected at any depth,
    # not only as direct children of the package root: a nested segment such as
    # contentFiles/any/any/ApiSchema/bin/ must also fail even when it carries no *.dll or *.cs.
    $forbiddenDirNames = @("lib", "ref", "bin", "obj")
    $forbiddenDir = Get-ChildItem -LiteralPath $PackageRoot -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $forbiddenDirNames -contains $_.Name } |
        Select-Object -First 1
    if ($null -ne $forbiddenDir)
    {
        $forbiddenDirName = $forbiddenDir.Name.ToLowerInvariant()
        $forbiddenDirPath = Format-LogSafeText ($forbiddenDir.FullName)
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': forbidden assembly-shape directory '$forbiddenDirName/' was found at '$forbiddenDirPath'. Asset-only packages must not contain lib/, ref/, bin/, or obj/ directories anywhere in the payload."
    }

    $dllFiles = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -ieq ".dll" -or $_.Extension -ieq ".cs" })
    if ($dllFiles.Count -gt 0)
    {
        $firstForbidden = Format-LogSafeText ($dllFiles[0].FullName)
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': forbidden file type found: '$firstForbidden'. Asset-only packages must not contain *.dll or *.cs files."
    }

    # --- 3. Read and validate package-manifest.json ---
    $manifestPath = Join-Path $ApiSchemaDirectory "package-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf))
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': required file 'package-manifest.json' is missing from '$(Format-LogSafeText $ApiSchemaDirectory)'."
    }

    $manifestContent = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop
    try
    {
        $manifest = $manifestContent | ConvertFrom-Json -ErrorAction Stop
    }
    catch
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': 'package-manifest.json' is not valid JSON: $(Format-LogSafeText ($_.Exception.Message))"
    }

    # Validate required fields (use Get-Member to handle StrictMode safely)
    $requiredFields = @("version", "packageId", "projectName", "projectEndpointName", "isExtensionProject", "schemaPath")
    foreach ($field in $requiredFields)
    {
        $propertyExists = $null -ne ($manifest | Get-Member -Name $field -MemberType NoteProperty)
        if (-not $propertyExists)
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': 'package-manifest.json' is missing required field '$field'."
        }
        $value = $manifest.$field
        # Reject JSON null as well as empty strings: a null value is not a [string], so checking only
        # the empty-string case would let "<field>": null pass the guard and surface later as a raw
        # null-reference error (e.g. packageId.Equals(...) or Join-Path on schemaPath) instead of this
        # clear "missing required field" message.
        if ($null -eq $value -or ($value -is [string] -and [string]::IsNullOrEmpty($value)))
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': 'package-manifest.json' is missing required field '$field'."
        }
    }

    # The required identity/path fields must be JSON strings. The presence loop above rejects missing,
    # null, and empty-string values, but a non-string value (e.g. "packageId": 123 or "schemaPath": true)
    # would otherwise pass and then fail with a non-contract error - Int32.Equals(string, StringComparison)
    # at the identity check below, or [string]/Join-Path coercion on projectName/projectEndpointName/
    # schemaPath here and in prepare-dms-schema.ps1. Reject it here with a clear contract message.
    $requiredStringFields = @("packageId", "projectName", "projectEndpointName", "schemaPath")
    foreach ($field in $requiredStringFields)
    {
        if ($manifest.$field -isnot [string])
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': 'package-manifest.json' field '$field' must be a JSON string, but found '$(Format-LogSafeText $manifest.$field)'."
        }
    }

    # 'version' is the integer schema-version of the manifest format (currently 1). The required-fields
    # loop above rejects only null and empty strings, so a value like "bad" or 1.5 would otherwise pass; require
    # a real JSON integer equal to the supported version.
    $supportedManifestVersion = 1
    $manifestVersion = $manifest.version
    $isIntegerVersion = ($manifestVersion -is [int]) -or ($manifestVersion -is [long]) -or ($manifestVersion -is [short]) -or ($manifestVersion -is [byte])
    if (-not $isIntegerVersion)
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': 'package-manifest.json' field 'version' must be an integer manifest schema version, but found '$(Format-LogSafeText $manifestVersion)'."
    }
    if ($manifestVersion -ne $supportedManifestVersion)
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': 'package-manifest.json' field 'version' is '$manifestVersion' but only manifest schema version $supportedManifestVersion is supported."
    }

    # --- 4. Validate identity: packageId must match ExpectedPackageId (case-insensitive) ---
    if (-not $manifest.packageId.Equals($ExpectedPackageId, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Package identity mismatch: manifest 'packageId' is '$(Format-LogSafeText $manifest.packageId)' but expected '$(Format-LogSafeText $ExpectedPackageId)'. The extracted package does not match the requested package ID."
    }

    # --- 5. Validate identity: isExtensionProject must match ExpectedIsExtension ---
    # Require a real JSON boolean. A raw [bool] cast would coerce any non-empty string (including
    # "false") to $true and $null to $false, letting a malformed manifest slip past this check.
    if ($manifest.isExtensionProject -isnot [bool])
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': 'package-manifest.json' field 'isExtensionProject' must be a JSON boolean (true/false), but found '$(Format-LogSafeText $manifest.isExtensionProject)'."
    }

    $manifestIsExtension = $manifest.isExtensionProject
    if ($manifestIsExtension -ne $ExpectedIsExtension)
    {
        $manifestFlag = if ($manifestIsExtension) { "true" } else { "false" }
        $expectedFlag = if ($ExpectedIsExtension) { "true" } else { "false" }
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest 'isExtensionProject' is '$manifestFlag' but expected '$expectedFlag'. Verify the package is the correct type (core vs. extension)."
    }

    # --- 5b. Validate optional static-asset path fields are JSON strings when present ---
    # discoverySpecPath / xsdDirectory are optional: omit the key or use JSON null. When present and
    # non-null they must be JSON strings. A raw [string] cast (used below to resolve them) would coerce a
    # number or boolean (e.g. discoverySpecPath: 123 or xsdDirectory: true) into a path that can match an
    # on-disk file/directory, letting a malformed manifest pass; require the real JSON string type instead
    # and fail fast before staging.
    foreach ($optionalPathField in @("discoverySpecPath", "xsdDirectory"))
    {
        $optionalPathMember = $manifest | Get-Member -Name $optionalPathField -MemberType NoteProperty
        if ($null -ne $optionalPathMember)
        {
            $optionalPathValue = $manifest.$optionalPathField
            if ($null -ne $optionalPathValue -and $optionalPathValue -isnot [string])
            {
                throw "Package '$(Format-LogSafeText $ExpectedPackageId)': 'package-manifest.json' field '$optionalPathField' must be a JSON string (or null/omitted), but found '$(Format-LogSafeText $optionalPathValue)'."
            }
        }
    }

    # --- 6. Verify exactly one schema JSON file exists at the contract path ---
    # Schema JSON files are all .json files at the root of ApiSchemaDirectory excluding
    # package-manifest.json and the manifest-declared optional discoverySpecPath file.
    $declaredDiscoverySpecFullPath = $null
    $discoverySpecMember = $manifest | Get-Member -Name "discoverySpecPath" -MemberType NoteProperty
    if ($null -ne $discoverySpecMember)
    {
        $discoverySpecValue = $manifest.discoverySpecPath
        if ($null -ne $discoverySpecValue -and -not [string]::IsNullOrEmpty([string]$discoverySpecValue))
        {
            # Resolve to a full path so we only exclude the root JSON file that actually IS the
            # discovery spec. Comparing bare file names would wrongly exclude an unrelated extra root
            # JSON that happens to share a basename with a *nested* discoverySpecPath (e.g. root
            # Other.json excluded by a declared nested/Other.json), letting a multi-schema package pass.
            $declaredDiscoverySpecFullPath = [System.IO.Path]::GetFullPath((Join-Path $ApiSchemaDirectory ([string]$discoverySpecValue)))
        }
    }

    $schemaJsonFiles = @(Get-ChildItem -LiteralPath $ApiSchemaDirectory -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Extension -ieq ".json" -and
            $_.Name -ine "package-manifest.json" -and
            ($null -eq $declaredDiscoverySpecFullPath -or
             -not [System.IO.Path]::GetFullPath($_.FullName).Equals($declaredDiscoverySpecFullPath, [System.StringComparison]::OrdinalIgnoreCase))
        })

    if ($schemaJsonFiles.Count -eq 0)
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': no schema JSON file found at the asset-only contract path '$(Format-LogSafeText $ApiSchemaDirectory)'. Expected exactly one schema JSON (e.g. ApiSchema.json)."
    }

    if ($schemaJsonFiles.Count -gt 1)
    {
        $fileList = ($schemaJsonFiles | ForEach-Object { Format-LogSafeText $_.Name }) -join ", "
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': multiple schema JSON files found at the asset-only contract path: $fileList. Expected exactly one schema JSON."
    }

    # --- 7. Verify manifest schemaPath exists and matches the single root schema JSON ---
    Assert-SafeManifestRelativePath -RelativePath ([string]$manifest.schemaPath) -FieldName "schemaPath" -ExpectedPackageId $ExpectedPackageId
    $schemaFilePath = Join-Path $ApiSchemaDirectory $manifest.schemaPath
    if (-not (Test-Path -LiteralPath $schemaFilePath -PathType Leaf))
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest 'schemaPath' references '$(Format-LogSafeText $manifest.schemaPath)' but that file does not exist at '$(Format-LogSafeText $schemaFilePath)'."
    }

    $resolvedSchemaFilePath = [System.IO.Path]::GetFullPath($schemaFilePath)
    $resolvedRootSchemaPath = [System.IO.Path]::GetFullPath($schemaJsonFiles[0].FullName)
    if (-not $resolvedSchemaFilePath.Equals($resolvedRootSchemaPath, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest 'schemaPath' must reference the single schema JSON at the asset-only contract root, but '$(Format-LogSafeText $manifest.schemaPath)' does not match the found root schema file '$(Format-LogSafeText $schemaJsonFiles[0].Name)'."
    }

    # --- 8. Verify optional manifest-declared static assets exist when non-null ---
    # Access optional fields through Get-Member: a conforming manifest may omit these keys entirely,
    # and a bare property access would throw under StrictMode.
    # Absence of an optional static-content path is expressed ONLY by omitting the key or setting it
    # to JSON null. A present-but-empty (or whitespace) string is a malformed declared path and must
    # fail fast rather than be silently treated as absent.
    $resolvedDiscoverySpecPath = $null
    $discoverySpecMember = $manifest | Get-Member -Name "discoverySpecPath" -MemberType NoteProperty
    if ($null -ne $discoverySpecMember -and $null -ne $manifest.discoverySpecPath)
    {
        $discoverySpecRaw = [string]$manifest.discoverySpecPath
        if ([string]::IsNullOrWhiteSpace($discoverySpecRaw))
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest 'discoverySpecPath' is present but empty. Omit the field or use JSON null to indicate no discovery spec."
        }
        Assert-SafeManifestRelativePath -RelativePath $discoverySpecRaw -FieldName "discoverySpecPath" -ExpectedPackageId $ExpectedPackageId
        $resolvedDiscoverySpecPath = Join-Path $ApiSchemaDirectory $manifest.discoverySpecPath
        if (-not (Test-Path -LiteralPath $resolvedDiscoverySpecPath -PathType Leaf))
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest 'discoverySpecPath' references '$(Format-LogSafeText $manifest.discoverySpecPath)' but that file does not exist at '$(Format-LogSafeText $resolvedDiscoverySpecPath)'."
        }

        # A declared discoverySpecPath must carry content. A zero-byte file would finalize a workspace
        # advertising a discovery spec it cannot actually serve, so a present-but-empty file fails fast.
        # Optional discovery-spec content is expressed by omitting the field or setting it to null.
        if ((Get-Item -LiteralPath $resolvedDiscoverySpecPath).Length -eq 0)
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest 'discoverySpecPath' references '$(Format-LogSafeText $manifest.discoverySpecPath)' but that file is empty at '$(Format-LogSafeText $resolvedDiscoverySpecPath)'. A declared discoverySpecPath must contain content; omit the field or use JSON null when the package has no discovery spec."
        }
    }

    $resolvedXsdDirectory = $null
    $xsdDirectoryMember = $manifest | Get-Member -Name "xsdDirectory" -MemberType NoteProperty
    if ($null -ne $xsdDirectoryMember -and $null -ne $manifest.xsdDirectory)
    {
        $xsdDirectoryRaw = [string]$manifest.xsdDirectory
        if ([string]::IsNullOrWhiteSpace($xsdDirectoryRaw))
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest 'xsdDirectory' is present but empty. Omit the field or use JSON null to indicate no XSD directory."
        }
        Assert-SafeManifestRelativePath -RelativePath $xsdDirectoryRaw -FieldName "xsdDirectory" -ExpectedPackageId $ExpectedPackageId
        $resolvedXsdDirectory = Join-Path $ApiSchemaDirectory $manifest.xsdDirectory
        if (-not (Test-Path -LiteralPath $resolvedXsdDirectory -PathType Container))
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest 'xsdDirectory' references '$(Format-LogSafeText $manifest.xsdDirectory)' but that directory does not exist at '$(Format-LogSafeText $resolvedXsdDirectory)'."
        }

        # A declared xsdDirectory must actually carry XSD content. prepare-dms-schema.ps1 records and
        # stages XSD only when *.xsd files exist, so an empty (or .xsd-free) declared directory would
        # silently finalize a workspace missing the advertised XSD content. Optional XSD content is
        # expressed by omitting xsdDirectory or setting it to null; a present directory must be populated.
        $declaredXsdFiles = @(Get-ChildItem -LiteralPath $resolvedXsdDirectory -File -Filter "*.xsd" -Recurse -ErrorAction SilentlyContinue)
        if ($declaredXsdFiles.Count -eq 0)
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': manifest 'xsdDirectory' references '$(Format-LogSafeText $manifest.xsdDirectory)' but no .xsd files were found under '$(Format-LogSafeText $resolvedXsdDirectory)'. A declared xsdDirectory must contain XSD content; omit the field or use JSON null when the package has no XSD content."
        }
    }

    # --- 9. Check for duplicate normalized relative paths in the package payload ---
    $allFiles = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File -ErrorAction SilentlyContinue)
    $normalizedPaths = @{}
    foreach ($file in $allFiles)
    {
        $relativePath = $file.FullName.Substring($PackageRoot.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, '/')
        $normalizedRelative = $relativePath.Replace('\', '/').ToLowerInvariant()
        if ($normalizedPaths.ContainsKey($normalizedRelative))
        {
            throw "Package '$(Format-LogSafeText $ExpectedPackageId)': duplicate normalized relative path detected: '$(Format-LogSafeText $normalizedRelative)'. Package payload must not contain duplicate paths (case-insensitive, path-separator normalized)."
        }
        $normalizedPaths[$normalizedRelative] = $true
    }

    # --- Return validated identity ---
    return [pscustomobject]@{
        PackageId            = $manifest.packageId
        ProjectName          = $manifest.projectName
        ProjectEndpointName  = $manifest.projectEndpointName
        IsExtensionProject   = $manifestIsExtension
        SchemaPath           = $schemaFilePath
        DiscoverySpecPath    = $resolvedDiscoverySpecPath
        XsdDirectory         = $resolvedXsdDirectory
    }
}

Export-ModuleMember -Function Resolve-StandardSchemaPackage, Assert-AssetOnlyPackageContract
