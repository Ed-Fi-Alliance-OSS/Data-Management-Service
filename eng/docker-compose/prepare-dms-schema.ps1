# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Stages the ApiSchema bootstrap workspace from either a NuGet package feed (standard mode)
    or a local filesystem directory (expert mode).

.DESCRIPTION
    prepare-dms-schema.ps1 materializes the normalized ApiSchema workspace at
    eng/docker-compose/.bootstrap/ApiSchema/. Every downstream bootstrap phase
    (start-local-dms.ps1, bootstrap-local-dms.ps1) reads from that workspace;
    this script is the sole writer.

    Standard mode (default, package-backed):
        Omit -ApiSchemaPath. When -EnvironmentFile is supplied and its SCHEMA_PACKAGES value lists
        one or more packages, the script resolves and stages the FULL SCHEMA_PACKAGES set (the same
        package ids, versions, and feed URLs the DMS container entrypoint downloads at startup), so
        the staged workspace's effective schema hash matches the runtime's. Exactly one entry must be
        the core package (EdFi.DataStandard52.ApiSchema); every other entry is staged as an extension.
        When -EnvironmentFile is omitted, the script falls back to resolving the DS-qualified
        asset-only core ApiSchema NuGet package from the Ed-Fi package feed at the catalog-pinned
        version and stages the core package only (backward-compatible direct-invocation behavior).
        There is no -Extensions parameter; the extension set (if any) always comes from
        SCHEMA_PACKAGES, never from a caller-supplied list.

    Expert mode (filesystem):
        Supply -ApiSchemaPath pointing to a directory that contains one or more ApiSchema*.json
        files. The script normalizes those loose files into the same workspace layout. Use this
        path for custom or in-repo schema directories, including extension-containing schema sets,
        that are not staged as the package-backed core.

    After staging the schema workspace, run prepare-dms-claims.ps1 to stage security metadata,
    then start-local-dms.ps1 (or bootstrap-local-dms.ps1) to launch the stack.

.PARAMETER ApiSchemaPath
    Expert mode. Path to a local directory containing one or more ApiSchema*.json files.
    The script recursively discovers all matching files, normalizes them into the bootstrap
    workspace, and records selectionMode "ApiSchemaPath" in the manifest. This is the path for
    extension-containing or custom schema sets.

.PARAMETER SchemaToolPath
    Path to the dms-schema native executable. Defaults to the DMS_SCHEMA_TOOL_PATH environment
    variable. Build the tool once before running this script on a clean checkout:
        dotnet publish ../../src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj \
            -c Release -p:UseAppHost=true -o .bootstrap/tools/dms-schema

.PARAMETER EnvironmentFile
    Standard mode only. Path to a docker-compose env file whose SCHEMA_PACKAGES value drives the
    staged package set. When supplied and SCHEMA_PACKAGES lists packages, standard-mode staging
    resolves and stages that full set instead of the catalog-pinned core-only default, so the staged
    workspace's effective schema hash matches what the DMS container entrypoint (run.sh) computes
    from the same SCHEMA_PACKAGES value at startup. Ignored in expert (-ApiSchemaPath) mode.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -SchemaToolPath $schemaToolExe
    Standard mode, core only. Resolves EdFi.DataStandard52.ApiSchema from the Ed-Fi package feed
    and stages it into the bootstrap workspace.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -EnvironmentFile ../docker-compose/.env.mssql.relational -SchemaToolPath $schemaToolExe
    Standard mode, driven by SCHEMA_PACKAGES. Resolves and stages every package listed in the env
    file's SCHEMA_PACKAGES value (core plus any extensions) so the staged workspace matches the
    package set the DMS container entrypoint downloads for that same env file.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema -SchemaToolPath $schemaToolExe
    Expert mode. Stages ApiSchema*.json files from the in-repo directory (which includes TPDM).
    Use this path when you have a custom or in-repo schema directory not published as a NuGet package.
    After staging, run prepare-dms-claims.ps1 with -ClaimsDirectoryPath if the schema includes
    extensions whose claim fragments are not auto-staged (e.g. TPDM).
#>
[CmdletBinding()]
param(
    [string]
    $ApiSchemaPath,

    [string]
    $SchemaToolPath = $env:DMS_SCHEMA_TOOL_PATH,

    # Internal test seam: overrides the pinned default package feed so offline tests can point at a
    # local-folder feed. Not intended for normal operator use; production runs omit this.
    [string]
    $PackageFeedUrl,

    # Standard mode only. Path to a docker-compose env file whose SCHEMA_PACKAGES value drives the
    # staged package set (core plus any extensions), keeping the staged workspace's effective schema
    # hash in sync with what the DMS container entrypoint resolves from the same env file at startup.
    # When omitted, standard mode falls back to the catalog-pinned core-only default.
    [string]
    $EnvironmentFile
)

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force -Global
Import-Module (Join-Path $PSScriptRoot "bootstrap-schema-tool.psm1") -Force -Global

if (-not (Get-Command Format-LogSafeText -ErrorAction SilentlyContinue)) {
    function Format-LogSafeText {
        param($Value)

        if ($null -eq $Value) { return "" }
        $text = [string]$Value
        $builder = [System.Text.StringBuilder]::new()
        foreach ($character in $text.ToCharArray()) {
            if ([char]::IsLetterOrDigit($character) -or
                $character -eq " " -or
                $character -eq "_" -or
                $character -eq "-" -or
                $character -eq "." -or
                $character -eq ":" -or
                $character -eq "/") {
                $null = $builder.Append($character)
            }
        }

        return $builder.ToString()
    }
}

if (-not (Get-Command Read-RequiredJsonBoolean -ErrorAction SilentlyContinue)) {
    function Read-RequiredJsonBoolean {
        param(
            [Parameter(Mandatory)]
            $Hashtable,

            [Parameter(Mandatory)]
            [string]
            $Key,

            [Parameter(Mandatory)]
            [string]
            $ArtifactContext
        )

        if ($Hashtable -isnot [System.Collections.IDictionary] -or -not $Hashtable.Contains($Key)) {
            throw "$(Format-LogSafeText $ArtifactContext) has malformed boolean for '$(Format-LogSafeText $Key)'."
        }

        $value = $Hashtable[$Key]
        if ($value -is [bool]) {
            return $value
        }

        throw "$(Format-LogSafeText $ArtifactContext) has malformed boolean for '$(Format-LogSafeText $Key)'."
    }
}

if (-not (Get-Command Get-BootstrapRoot -ErrorAction SilentlyContinue)) {
    $script:PrepareBootstrapRoot = Join-Path $PSScriptRoot ".bootstrap"
    $script:PrepareBootstrapManifestPath = Join-Path $script:PrepareBootstrapRoot "bootstrap-manifest.json"
    $script:PrepareWorkspaceMismatchMessage = "Existing staged bootstrap workspace differs from requested inputs, manifest state is incomplete, or files were manually edited (partial prior state). Stop the local stack and remove eng/docker-compose/.bootstrap before retrying. For local Docker, run: pwsh eng/docker-compose/start-local-dms.ps1 -d -v -RemoveBootstrap. E2E teardown wrappers also remove the bootstrap workspace."
    $script:PrepareUtf8NoBom = [System.Text.UTF8Encoding]::new($false)

    function Get-BootstrapRoot {
        return $script:PrepareBootstrapRoot
    }

    function Get-BootstrapRelativePath {
        param(
            [Parameter(Mandatory)]
            [string]
            $Path,

            [string]
            $BasePath = $script:PrepareBootstrapRoot
        )

        $fullPath = [System.IO.Path]::GetFullPath($Path)
        $fullBasePath = [System.IO.Path]::GetFullPath($BasePath)
        return [System.IO.Path]::GetRelativePath($fullBasePath, $fullPath).Replace("\", "/")
    }

    function Get-BootstrapWorkspaceMismatchMessage {
        param(
            [string]
            $Reason
        )

        if (-not [string]::IsNullOrWhiteSpace($Reason)) {
            return $script:PrepareWorkspaceMismatchMessage.Replace(
                "Stop the local stack",
                "Diverging field: $(Format-LogSafeText $Reason). Stop the local stack"
            )
        }

        return $script:PrepareWorkspaceMismatchMessage
    }

    function New-BootstrapManifest {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory manifest object; no system state changes and no -WhatIf surface.')]
        param()

        return @{
            version = 1
        }
    }

    function Read-BootstrapManifest {
        param(
            [string]
            $Path = $script:PrepareBootstrapManifestPath
        )

        if (-not (Test-Path -LiteralPath $Path)) {
            return $null
        }

        try {
            $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
        } catch {
            throw "Bootstrap manifest '$(Format-LogSafeText $Path)' contains malformed JSON. $(Format-LogSafeText ($_.Exception.Message))"
        }

        if ($manifest -isnot [System.Collections.IDictionary]) {
            throw "Bootstrap manifest '$(Format-LogSafeText $Path)' must contain a JSON object."
        }

        if (-not $manifest.ContainsKey("version") -or $null -eq $manifest["version"]) {
            $manifest["version"] = 1
            return $manifest
        }

        $manifest["version"] = [int]$manifest["version"]
        return $manifest
    }

    function Write-BootstrapJson {
        param(
            [Parameter(Mandatory)]
            [string]
            $Path,

            [Parameter(Mandatory)]
            $Value
        )

        $directory = Split-Path -Parent $Path
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        $json = $Value | ConvertTo-Json -Depth 100
        [System.IO.File]::WriteAllText($Path, "$json`n", $script:PrepareUtf8NoBom)
    }

    function Write-BootstrapManifest {
        param(
            [Parameter(Mandatory)]
            $Manifest,

            [string]
            $Path = $script:PrepareBootstrapManifestPath
        )

        $orderedManifest = [ordered]@{
            version = 1
        }

        foreach ($sectionName in @("schema", "claims", "seed")) {
            if ($Manifest.ContainsKey($sectionName)) {
                $orderedManifest[$sectionName] = $Manifest[$sectionName]
            }
        }

        Write-BootstrapJson -Path $Path -Value $orderedManifest
    }

    function Set-BootstrapManifestSection {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Mutates an in-memory manifest hashtable; no system state changes and no -WhatIf surface.')]
        param(
            [Parameter(Mandatory)]
            [ValidateSet("schema", "claims", "seed")]
            [string]
            $Name,

            [Parameter(Mandatory)]
            $Value,

            [string]
            $Path = $script:PrepareBootstrapManifestPath
        )

        $manifest = Read-BootstrapManifest -Path $Path
        if ($null -eq $manifest) {
            $manifest = New-BootstrapManifest
        }

        $manifest[$Name] = $Value
        Write-BootstrapManifest -Manifest $manifest -Path $Path
    }

    function Get-BootstrapFingerprintByte {
        param(
            [Parameter(Mandatory)]
            [string]
            $Path
        )

        $extension = [System.IO.Path]::GetExtension($Path)
        if ($extension.Equals(".json", [System.StringComparison]::OrdinalIgnoreCase) -or
            $extension.Equals(".xsd", [System.StringComparison]::OrdinalIgnoreCase)) {
            $text = [System.IO.File]::ReadAllText($Path)
            $normalizedText = $text.Replace("`r`n", "`n").Replace("`r", "`n")
            return [System.Text.Encoding]::UTF8.GetBytes($normalizedText)
        }

        return [System.IO.File]::ReadAllBytes($Path)
    }

    function Get-BootstrapWorkspaceFingerprint {
        param(
            [Parameter(Mandatory)]
            [string]
            $Path
        )

        if (-not (Test-Path -LiteralPath $Path)) {
            throw "Workspace path does not exist: $(Format-LogSafeText $Path)"
        }

        $entries = @(
            Get-ChildItem -LiteralPath $Path -File -Recurse | ForEach-Object {
                [pscustomobject]@{
                    RelativePath = Get-BootstrapRelativePath -Path $_.FullName -BasePath $Path
                    FullName = $_.FullName
                }
            }
        )

        $sortedEntries = @($entries | Sort-Object -Property RelativePath)
        $incrementalHash = [System.Security.Cryptography.IncrementalHash]::CreateHash(
            [System.Security.Cryptography.HashAlgorithmName]::SHA256
        )
        [byte[]] $separator = 0

        foreach ($entry in $sortedEntries) {
            $relativePathBytes = [System.Text.Encoding]::UTF8.GetBytes($entry.RelativePath)
            $incrementalHash.AppendData($relativePathBytes)
            $incrementalHash.AppendData($separator)
            $incrementalHash.AppendData((Get-BootstrapFingerprintByte -Path $entry.FullName))
            $incrementalHash.AppendData($separator)
        }

        return [System.Convert]::ToHexString($incrementalHash.GetHashAndReset()).ToLowerInvariant()
    }
}

Import-Module (Join-Path $PSScriptRoot "bootstrap-schema-catalog.psm1") -Force -Global

# --- Schema-selection mode ---
# Expert mode when -ApiSchemaPath is supplied; otherwise package-backed core-only standard mode.
# There is no -Extensions parameter: extension/custom schema sets use expert -ApiSchemaPath.
#
# Standard mode is selected by OMITTING -ApiSchemaPath entirely. An explicitly bound but blank value
# (e.g. -ApiSchemaPath "" or -ApiSchemaPath $null) is invalid caller input for expert mode, not a request
# for standard mode; fail fast rather than silently routing it to package-backed core-only staging.
$apiSchemaPathBound = $PSBoundParameters.ContainsKey("ApiSchemaPath")
if ($apiSchemaPathBound -and [string]::IsNullOrWhiteSpace($ApiSchemaPath)) {
    throw "-ApiSchemaPath was supplied but is blank. Provide a path to an ApiSchema directory for expert mode, or omit -ApiSchemaPath entirely to use package-backed core-only standard mode."
}
$hasApiSchemaPath = $apiSchemaPathBound

function Get-ProjectDirectoryName {
    param(
        [Parameter(Mandatory)]
        [string]
        $ProjectName,

        [Parameter(Mandatory)]
        [string]
        $ProjectEndpointName
    )

    $candidate = if ([string]::IsNullOrWhiteSpace($ProjectName)) {
        $ProjectEndpointName
    } else {
        $ProjectName
    }

    $normalized = [System.Text.RegularExpressions.Regex]::Replace($candidate.Trim(), "[^A-Za-z0-9._-]", "-")
    $normalized = [System.Text.RegularExpressions.Regex]::Replace($normalized, "-+", "-").Trim(".-")

    if ([string]::IsNullOrWhiteSpace($normalized)) {
        throw "ApiSchema project '$(Format-LogSafeText $ProjectName)' has no usable normalized project directory name."
    }

    return $normalized
}

function Read-ApiSchemaIdentity {
    param(
        [Parameter(Mandatory)]
        [string]
        $Path
    )

    try {
        $schema = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    } catch {
        throw "ApiSchema file '$(Format-LogSafeText $Path)' contains malformed JSON. $(Format-LogSafeText ($_.Exception.Message))"
    }

    if (-not $schema.ContainsKey("projectSchema")) {
        throw "ApiSchema file '$(Format-LogSafeText $Path)' is missing projectSchema."
    }

    $projectSchema = $schema["projectSchema"]
    $projectName = $projectSchema["projectName"]
    $projectEndpointName = $projectSchema["projectEndpointName"]

    if ([string]::IsNullOrWhiteSpace($projectName)) {
        throw "ApiSchema file '$(Format-LogSafeText $Path)' is missing projectSchema.projectName."
    }

    if ([string]::IsNullOrWhiteSpace($projectEndpointName)) {
        throw "ApiSchema file '$(Format-LogSafeText $Path)' is missing projectSchema.projectEndpointName."
    }

    $isExtensionProject = Read-RequiredJsonBoolean `
        -Hashtable $projectSchema `
        -Key "isExtensionProject" `
        -ArtifactContext ("ApiSchema project schema in '" + (Format-LogSafeText $Path) + "'")

    $projectDirectoryName = Get-ProjectDirectoryName `
        -ProjectName $projectName `
        -ProjectEndpointName $projectEndpointName

    return [pscustomobject]@{
        SourcePath = [System.IO.Path]::GetFullPath($Path)
        SourceDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Path))
        ProjectName = $projectName
        ProjectEndpointName = $projectEndpointName
        IsExtensionProject = $isExtensionProject
        ProjectDirectoryName = $projectDirectoryName
    }
}

function Find-ApiSchemaFile {
    param(
        [Parameter(Mandatory)]
        [string]
        $Path
    )

    try {
        $resolvedPath = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    } catch {
        throw "ApiSchemaPath was not found: $(Format-LogSafeText $Path). $(Format-LogSafeText ($_.Exception.Message))"
    }

    $item = Get-Item -LiteralPath $resolvedPath

    if (-not $item.PSIsContainer) {
        throw "ApiSchemaPath must be a directory: $(Format-LogSafeText ($item.FullName))"
    }

    $schemaFiles = @(
        Get-ChildItem -LiteralPath $item.FullName -File -Filter "ApiSchema*.json" -Recurse |
            Sort-Object -Property FullName |
            ForEach-Object { $_.FullName }
    )

    if ($schemaFiles.Count -eq 0) {
        throw "No ApiSchema*.json files were found under '$(Format-LogSafeText ($item.FullName))'."
    }

    return $schemaFiles
}

function Add-CopyOperation {
    param(
        [System.Collections.Generic.Dictionary[string, string]]
        $TargetSources,

        [System.Collections.ArrayList]
        $CopyOperations,

        [Parameter(Mandatory)]
        [string]
        $SourcePath,

        [Parameter(Mandatory)]
        [string]
        $RelativeTargetPath
    )

    $normalizedRelativeTargetPath = $RelativeTargetPath.Replace("\", "/")
    if ($TargetSources.ContainsKey($normalizedRelativeTargetPath)) {
        throw "Normalized path collision for '$(Format-LogSafeText $normalizedRelativeTargetPath)' from '$(Format-LogSafeText $SourcePath)' and '$(Format-LogSafeText ($TargetSources[$normalizedRelativeTargetPath]))'."
    }

    $TargetSources[$normalizedRelativeTargetPath] = $SourcePath
    $null = $CopyOperations.Add(
        [pscustomobject]@{
            SourcePath = $SourcePath
            RelativeTargetPath = $normalizedRelativeTargetPath
        }
    )
}

function Add-ProjectContentOperation {
    param(
        [Parameter(Mandatory)]
        $Project,

        [System.Collections.Generic.Dictionary[string, string]]
        $TargetSources,

        [System.Collections.ArrayList]
        $CopyOperations,

        # Standard mode: the validated package manifest is authoritative for schema-adjacent
        # content. When set, source paths come exclusively from the two Manifest* parameters
        # (null/empty means the package declares no such asset) and convention-based sibling
        # discovery is skipped entirely. Expert mode omits it and keeps sibling discovery.
        [switch]
        $UseManifestAssets,

        # Absolute path to the manifest-declared discovery spec file, already validated to exist.
        [string]
        $ManifestDiscoverySpecPath,

        # Absolute path to the manifest-declared XSD directory, already validated to exist.
        [string]
        $ManifestXsdDirectory
    )

    $contentRoot = "content/$($Project.ProjectDirectoryName)"

    $discoverySpecSourcePath = if ($UseManifestAssets) {
        $ManifestDiscoverySpecPath
    } else {
        Join-Path $Project.SourceDirectory "discovery-spec.json"
    }

    $discoverySpecRelativePath = $null
    if (-not [string]::IsNullOrWhiteSpace($discoverySpecSourcePath) -and
        (Test-Path -LiteralPath $discoverySpecSourcePath -PathType Leaf)) {
        $discoverySpecRelativePath = "$contentRoot/discovery-spec.json"
        Add-CopyOperation `
            -TargetSources $TargetSources `
            -CopyOperations $CopyOperations `
            -SourcePath $discoverySpecSourcePath `
            -RelativeTargetPath $discoverySpecRelativePath
    }

    $xsdSourceDirectory = if ($UseManifestAssets) {
        $ManifestXsdDirectory
    } else {
        $conventionXsdDirectory = Join-Path $Project.SourceDirectory "xsd"
        if (-not (Test-Path -LiteralPath $conventionXsdDirectory -PathType Container)) {
            $conventionXsdDirectory = Join-Path $Project.SourceDirectory "XSD"
        }
        $conventionXsdDirectory
    }

    $xsdDirectoryRelativePath = $null
    if (-not [string]::IsNullOrWhiteSpace($xsdSourceDirectory) -and
        (Test-Path -LiteralPath $xsdSourceDirectory -PathType Container)) {
        # XSD files must sit flat directly under the source directory. Reject nested XSD files so the
        # staged workspace mirrors the published asset-only package contract (flat xsd/).
        $pathSeparatorChars = [char[]]@(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        )
        $xsdSourceDirectoryFullPath = [System.IO.Path]::GetFullPath($xsdSourceDirectory).TrimEnd($pathSeparatorChars)
        $xsdFiles = @(Get-ChildItem -LiteralPath $xsdSourceDirectory -File -Filter "*.xsd" -Recurse | Sort-Object -Property FullName)
        foreach ($xsdFile in $xsdFiles) {
            $xsdFileDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($xsdFile.FullName)).TrimEnd($pathSeparatorChars)
            if (-not [System.String]::Equals($xsdFileDirectory, $xsdSourceDirectoryFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "ApiSchema project '$(Format-LogSafeText $($Project.ProjectName))' contains nested XSD file '$(Format-LogSafeText $($xsdFile.FullName))'. XSD files must be flattened directly under '$(Format-LogSafeText $xsdSourceDirectory)'."
            }
        }

        if ($xsdFiles.Count -gt 0) {
            $xsdDirectoryRelativePath = "$contentRoot/xsd"
            foreach ($xsdFile in $xsdFiles) {
                $sourceRelativePath = Get-BootstrapRelativePath -Path $xsdFile.FullName -BasePath $xsdSourceDirectory
                Add-CopyOperation `
                    -TargetSources $TargetSources `
                    -CopyOperations $CopyOperations `
                    -SourcePath $xsdFile.FullName `
                    -RelativeTargetPath "$xsdDirectoryRelativePath/$sourceRelativePath"
            }
        }
    }

    return [pscustomobject]@{
        DiscoverySpecPath = $discoverySpecRelativePath
        XsdDirectory = $xsdDirectoryRelativePath
    }
}

# --- Shared schema workspace staging function ---
function Invoke-SchemaWorkspaceStaging {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Stages bootstrap files; callers do not expose -WhatIf end to end.')]
    param(
        [Parameter(Mandatory)]
        [string[]]
        $SchemaSourceFiles,

        [Parameter(Mandatory)]
        [ValidateSet("ApiSchemaPath", "Standard")]
        [string]
        $SelectionMode,

        # Optional map of absolute schema-file path -> validated package identity (PackageId,
        # ProjectName, ProjectEndpointName, IsExtensionProject, DiscoverySpecPath, XsdDirectory).
        # Standard mode supplies it so the identity declared inside each staged ApiSchema.json is
        # asserted against the validated package manifest, and so schema-adjacent content is staged
        # from the manifest-declared (contract-validated) asset paths instead of sibling
        # rediscovery; expert mode omits it, skipping the assertion and using sibling discovery.
        [hashtable]
        $ExpectedIdentities
    )

    $schemaProjects = @($SchemaSourceFiles | ForEach-Object { Read-ApiSchemaIdentity -Path $_ })

    # Lock package identity end to end: the project identity inside each staged ApiSchema.json must
    # match the validated package manifest. Without this, a package that passes packageId validation
    # could still stage a different project, producing wrong selectedExtensions, claims, and seed
    # handoff.
    if ($null -ne $ExpectedIdentities) {
        foreach ($project in $schemaProjects) {
            if (-not $ExpectedIdentities.ContainsKey($project.SourcePath)) {
                continue
            }

            $expected = $ExpectedIdentities[$project.SourcePath]
            if (-not $project.ProjectName.Equals([string]$expected.ProjectName, [System.StringComparison]::OrdinalIgnoreCase) -or
                -not $project.ProjectEndpointName.Equals([string]$expected.ProjectEndpointName, [System.StringComparison]::OrdinalIgnoreCase) -or
                $project.IsExtensionProject -ne [bool]$expected.IsExtensionProject) {
                throw "Package identity mismatch for '$(Format-LogSafeText $expected.PackageId)': the package manifest declares project '$(Format-LogSafeText $expected.ProjectName)' (endpoint '$(Format-LogSafeText $expected.ProjectEndpointName)', isExtension=$([bool]$expected.IsExtensionProject)), but the staged ApiSchema.json declares project '$(Format-LogSafeText $project.ProjectName)' (endpoint '$(Format-LogSafeText $project.ProjectEndpointName)', isExtension=$($project.IsExtensionProject)). The package manifest and schema asset must describe the same project."
            }
        }
    }

    $coreProjects = @($schemaProjects | Where-Object { -not $_.IsExtensionProject })
    if ($coreProjects.Count -ne 1) {
        throw "ApiSchemaPath must stage exactly one core schema. Found $($coreProjects.Count)."
    }

    $extensionProjects = @(
        $schemaProjects |
            Where-Object { $_.IsExtensionProject } |
            Sort-Object -Property @{ Expression = { $_.ProjectEndpointName.ToLowerInvariant() } }, SourcePath
    )

    $orderedProjects = @($coreProjects[0]) + $extensionProjects
    $sourceDirectoryGroups = $schemaProjects | Group-Object -Property SourceDirectory -AsHashTable

    $targetSources = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    $copyOperations = [System.Collections.ArrayList]::new()
    $manifestProjects = [System.Collections.ArrayList]::new()
    $contentBySourceDirectory = @{}

    foreach ($project in $orderedProjects) {
        $schemaRelativePath = "schemas/$($project.ProjectDirectoryName)/ApiSchema.json"
        Add-CopyOperation `
            -TargetSources $targetSources `
            -CopyOperations $copyOperations `
            -SourcePath $project.SourcePath `
            -RelativeTargetPath $schemaRelativePath

        if (-not $contentBySourceDirectory.ContainsKey($project.SourceDirectory)) {
            $validatedIdentity = $null
            if ($null -ne $ExpectedIdentities -and $ExpectedIdentities.ContainsKey($project.SourcePath)) {
                $validatedIdentity = $ExpectedIdentities[$project.SourcePath]
            }

            if ($null -ne $validatedIdentity) {
                # Standard mode: the validated package manifest declares its schema-adjacent content
                # (discoverySpecPath/xsdDirectory) and is authoritative - stage exactly what it
                # declares from the contract-validated paths, with no sibling rediscovery. Each
                # package extracts into its own isolation directory, so there is no shared-directory
                # ownership to disambiguate.
                $contentBySourceDirectory[$project.SourceDirectory] = Add-ProjectContentOperation `
                    -Project $project `
                    -TargetSources $targetSources `
                    -CopyOperations $copyOperations `
                    -UseManifestAssets `
                    -ManifestDiscoverySpecPath ([string]$validatedIdentity.DiscoverySpecPath) `
                    -ManifestXsdDirectory ([string]$validatedIdentity.XsdDirectory)
            } else {
                # When two or more projects share a source directory AND that directory carries
                # schema-adjacent content (discovery-spec.json or xsd/), the core schema owns the staged
                # content path that every project in the group references. Extension-only groups have no
                # unambiguous owner in that case - fail fast and tell the caller to put each extension in
                # its own directory (or add a sibling core schema). When no schema-adjacent content exists
                # in the directory, there is nothing to disambiguate, so multiple extensions in the same
                # directory are accepted (typical of recursive ApiSchema*.json discovery layouts).
                $sourceDirectoryGroup = @($sourceDirectoryGroups[$project.SourceDirectory])
                $coreInGroup = @($sourceDirectoryGroup | Where-Object { -not $_.IsExtensionProject })
                if ($sourceDirectoryGroup.Count -gt 1 -and $coreInGroup.Count -eq 0) {
                    $hasSchemaAdjacentContent =
                        (Test-Path -LiteralPath (Join-Path $project.SourceDirectory "discovery-spec.json") -PathType Leaf) -or
                        (Test-Path -LiteralPath (Join-Path $project.SourceDirectory "xsd") -PathType Container) -or
                        (Test-Path -LiteralPath (Join-Path $project.SourceDirectory "XSD") -PathType Container)
                    if ($hasSchemaAdjacentContent) {
                        throw "Ambiguous schema-adjacent content ownership: source directory '$(Format-LogSafeText $project.SourceDirectory)' contains multiple extension schemas and no core schema. Move each extension into its own directory."
                    }
                }

                $contentBySourceDirectory[$project.SourceDirectory] = Add-ProjectContentOperation `
                    -Project $project `
                    -TargetSources $targetSources `
                    -CopyOperations $copyOperations
            }
        }
        $contentPaths = $contentBySourceDirectory[$project.SourceDirectory]

        $manifestProject = [ordered]@{
            projectName = $project.ProjectName
            projectEndpointName = $project.ProjectEndpointName
            isExtensionProject = $project.IsExtensionProject
            schemaPath = $schemaRelativePath
        }

        if (-not [string]::IsNullOrWhiteSpace($contentPaths.DiscoverySpecPath)) {
            $manifestProject["discoverySpecPath"] = $contentPaths.DiscoverySpecPath
        }

        if (-not [string]::IsNullOrWhiteSpace($contentPaths.XsdDirectory)) {
            $manifestProject["xsdDirectory"] = $contentPaths.XsdDirectory
        }

        $null = $manifestProjects.Add($manifestProject)
    }

    $bootstrapRoot = Get-BootstrapRoot
    $temporaryRoot = Join-Path (Join-Path $bootstrapRoot ".tmp") "ApiSchema-$([Guid]::NewGuid().ToString('N'))"
    $finalWorkspace = Join-Path $bootstrapRoot "ApiSchema"
    $temporaryMoved = $false

    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null

        foreach ($copyOperation in $copyOperations) {
            $targetPath = Join-Path $temporaryRoot $copyOperation.RelativeTargetPath
            New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
            Copy-Item -LiteralPath $copyOperation.SourcePath -Destination $targetPath -ErrorAction Stop
        }

        # Copy JsonSchemaForApiSchema.json into the workspace root so it is available when
        # /app/ApiSchema is mounted in Docker (the mount shadows the DMS assembly output directory
        # that ApiSchemaValidator uses to load this meta-schema at startup). Required once Story 04
        # (DMS-1154) activates staged-workspace runtime loading.
        $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
        $jsonSchemaValidatorSource = Join-Path $repoRoot "src/dms/core/EdFi.DataManagementService.Core/ApiSchema/JsonSchemaForApiSchema.json"
        if (-not (Test-Path -LiteralPath $jsonSchemaValidatorSource -PathType Leaf)) {
            throw "JsonSchemaForApiSchema.json not found at $(Format-LogSafeText $jsonSchemaValidatorSource). Build the DMS solution before running prepare-dms-schema.ps1."
        }
        Copy-Item -LiteralPath $jsonSchemaValidatorSource -Destination (Join-Path $temporaryRoot "JsonSchemaForApiSchema.json") -ErrorAction Stop

        $apiSchemaManifest = [ordered]@{
            version = 1
            projects = @($manifestProjects)
        }
        $apiSchemaManifestPath = Join-Path $temporaryRoot "bootstrap-api-schema-manifest.json"
        Write-BootstrapJson -Path $apiSchemaManifestPath -Value $apiSchemaManifest

        $coreStagedSchemaPath = Join-Path $temporaryRoot $manifestProjects[0]["schemaPath"]
        $extensionStagedSchemaPaths = @(
            $manifestProjects |
                Where-Object { $_["isExtensionProject"] } |
                ForEach-Object { Join-Path $temporaryRoot $_["schemaPath"] }
        )

        $schemaTool = Resolve-DmsSchemaTool -RequestedPath $SchemaToolPath
        $effectiveSchemaHash = Invoke-DmsSchemaHash `
            -ToolPath $schemaTool `
            -CoreSchemaPath $coreStagedSchemaPath `
            -ExtensionSchemaPath $extensionStagedSchemaPaths

        $workspaceFingerprint = Get-BootstrapWorkspaceFingerprint -Path $temporaryRoot
        $schemaSection = [ordered]@{
            selectionMode = $SelectionMode
            selectedExtensions = @($extensionProjects | ForEach-Object { $_.ProjectEndpointName.ToLowerInvariant() })
            effectiveSchemaHash = $effectiveSchemaHash
            workspaceFingerprint = $workspaceFingerprint
            apiSchemaManifestPath = "ApiSchema/bootstrap-api-schema-manifest.json"
        }

        $rootManifest = Read-BootstrapManifest
        if ($null -eq $rootManifest) {
            $rootManifest = New-BootstrapManifest
        }
        if (Test-Path -LiteralPath $finalWorkspace) {
            if (-not $rootManifest.ContainsKey("schema")) {
                throw (Get-BootstrapWorkspaceMismatchMessage -Reason "manifest schema section missing")
            }

            $existingSchemaSection = $rootManifest["schema"]
            if ($existingSchemaSection -isnot [System.Collections.IDictionary]) {
                throw (Get-BootstrapWorkspaceMismatchMessage -Reason "manifest schema section malformed")
            }
            if ($existingSchemaSection["effectiveSchemaHash"] -ne $effectiveSchemaHash) {
                throw (Get-BootstrapWorkspaceMismatchMessage -Reason "effective schema hash mismatch")
            }

            if ($existingSchemaSection["workspaceFingerprint"] -ne $workspaceFingerprint) {
                throw (Get-BootstrapWorkspaceMismatchMessage -Reason "workspace fingerprint mismatch")
            }

            $existingFingerprint = Get-BootstrapWorkspaceFingerprint -Path $finalWorkspace
            if ($existingFingerprint -ne $workspaceFingerprint) {
                throw (Get-BootstrapWorkspaceMismatchMessage -Reason "staged content drift")
            }
        } else {
            if ($rootManifest.ContainsKey("claims") -or $rootManifest.ContainsKey("seed")) {
                throw (Get-BootstrapWorkspaceMismatchMessage -Reason "manifest has stale claims/seed sections but ApiSchema workspace is missing")
            }

            New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
            Move-Item -LiteralPath $temporaryRoot -Destination $finalWorkspace -ErrorAction Stop
            $temporaryMoved = $true
        }

        Set-BootstrapManifestSection -Name "schema" -Value $schemaSection

        Write-Output "Prepared ApiSchema workspace at $(Format-LogSafeText $finalWorkspace)"
        Write-Output "Effective schema hash: $effectiveSchemaHash"
    } finally {
        if (-not $temporaryMoved -and (Test-Path -LiteralPath $temporaryRoot)) {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
    }
}

# --- Standard-mode package resolution and staging ---
function Assert-RequestedPackageIdentity {
    <#
    .SYNOPSIS
    Asserts that a validated package's manifest projectName and projectEndpointName match the
    identity the request implies (the catalog ProjectToken/EndpointToken). Without this, a
    mislabeled package - correct packageId but internally consistent manifest+schema for a
    different project - would pass validation and stage the wrong project under the requested
    selection. Both fields matter independently: the endpoint drives selectedExtensions and
    routing, while the project name drives the staged directory layout and claims metadata
    lookup.
    #>
    param(
        [Parameter(Mandatory)]
        $Validated,

        [Parameter(Mandatory)]
        [string]
        $ExpectedProjectName,

        [Parameter(Mandatory)]
        [string]
        $ExpectedEndpointName,

        [Parameter(Mandatory)]
        [string]
        $RequestedName
    )

    if (-not ([string]$Validated.ProjectName).Equals($ExpectedProjectName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Package identity mismatch for '$(Format-LogSafeText $Validated.PackageId)': the requested selection '$(Format-LogSafeText $RequestedName)' implies project name '$(Format-LogSafeText $ExpectedProjectName)', but the package manifest declares projectName '$(Format-LogSafeText $Validated.ProjectName)'. The package content does not match the requested selection."
    }

    if (-not ([string]$Validated.ProjectEndpointName).Equals($ExpectedEndpointName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Package identity mismatch for '$(Format-LogSafeText $Validated.PackageId)': the requested selection '$(Format-LogSafeText $RequestedName)' implies project endpoint '$(Format-LogSafeText $ExpectedEndpointName)', but the package manifest declares projectEndpointName '$(Format-LogSafeText $Validated.ProjectEndpointName)'. The package content does not match the requested selection."
    }
}

function Invoke-StandardModeSchemaStaging {
    param(
        [string]
        $FeedUrl
    )

    Import-Module (Join-Path $PSScriptRoot "bootstrap-package-resolver.psm1") -Force -Global

    # Resolve feed URL: use the caller-supplied override or fall back to the pinned default.
    $resolvedFeedUrl = if (-not [string]::IsNullOrWhiteSpace($FeedUrl)) {
        $FeedUrl
    } else {
        Get-StandardSchemaFeed
    }

    $bootstrapRoot = Get-BootstrapRoot
    $extractionRoot = Join-Path (Join-Path $bootstrapRoot ".tmp") "pkg-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $extractionRoot -Force | Out-Null

    try {
        # Collect the core ApiSchema.json path plus its validated identity so staging can assert the
        # schema asset matches the package manifest. Standard mode is core-only.
        $schemaSourceFiles = [System.Collections.Generic.List[string]]::new()
        $expectedIdentities = @{}

        # Resolve and validate the core package.
        $corePackage = Get-StandardCorePackage
        $coreResolved = Resolve-StandardSchemaPackage `
            -FeedUrl $resolvedFeedUrl `
            -PackageId $corePackage.Id `
            -Version $corePackage.Version `
            -DestinationRoot $extractionRoot

        $coreValidated = Assert-AssetOnlyPackageContract `
            -ApiSchemaDirectory $coreResolved.ApiSchemaDirectory `
            -PackageRoot $coreResolved.PackageRoot `
            -ExpectedPackageId $corePackage.Id `
            -ExpectedIsExtension $false

        Assert-RequestedPackageIdentity `
            -Validated $coreValidated `
            -ExpectedProjectName $corePackage.ProjectToken `
            -ExpectedEndpointName $corePackage.EndpointToken `
            -RequestedName "core"

        $schemaSourceFiles.Add($coreValidated.SchemaPath)
        $expectedIdentities[[System.IO.Path]::GetFullPath($coreValidated.SchemaPath)] = $coreValidated

        # Stage the collected (core) schema file using the shared staging function. With no extension
        # projects in the staged set, the manifest records selectedExtensions = @().
        Invoke-SchemaWorkspaceStaging `
            -SchemaSourceFiles $schemaSourceFiles.ToArray() `
            -SelectionMode "Standard" `
            -ExpectedIdentities $expectedIdentities
    } finally {
        if (Test-Path -LiteralPath $extractionRoot) {
            Remove-Item -LiteralPath $extractionRoot -Recurse -Force
        }
    }
}

function Invoke-SchemaPackagesModeSchemaStaging {
    <#
    .SYNOPSIS
    Standard mode driven by an env file's SCHEMA_PACKAGES value: resolves and stages the FULL
    package set (core plus any extensions) so the staged workspace's effective schema hash matches
    what the DMS container entrypoint (run.sh) computes from the same SCHEMA_PACKAGES value at
    container startup.

    .DESCRIPTION
    Parses SCHEMA_PACKAGES from the supplied env file. Exactly one entry must resolve to the
    catalog-known core package id; every other entry is validated and staged as an extension. Each
    entry's own version and feedUrl drive resolution - not the catalog-pinned default - so a
    SCHEMA_PACKAGES set pinned to a newer (or different) version than the catalog default still
    stages a workspace that matches the runtime.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $EnvironmentFilePath
    )

    Import-Module (Join-Path $PSScriptRoot "bootstrap-package-resolver.psm1") -Force -Global
    Import-Module (Join-Path $PSScriptRoot "../schema-package-utility.psm1") -Force -Global

    $schemaPackages = @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $EnvironmentFilePath)

    # Core detection is data-standard-agnostic: the standard core package id is
    # EdFi.DataStandard<NN>.ApiSchema for every data standard (52, 61, ...), while extension
    # packages carry an extra project segment (e.g. EdFi.DataStandard52.TPDM.ApiSchema,
    # EdFi.DataStandard61.Sample.ApiSchema). The catalog core id is not compared directly
    # because it pins a single data standard; the catalog still supplies the canonical core
    # project/endpoint tokens asserted in the resolve loop below. The exactly-one-core
    # invariant is enforced BEFORE any package download so a malformed set fails fast.
    $script:StandardCorePackageIdPattern = '^EdFi\.DataStandard\d+\.ApiSchema$'
    $coreEntryCount = @($schemaPackages | Where-Object { ([string]$_.name) -match $script:StandardCorePackageIdPattern }).Count
    if ($coreEntryCount -ne 1) {
        throw "SCHEMA_PACKAGES in '$(Format-LogSafeText $EnvironmentFilePath)' must list exactly one core package (EdFi.DataStandard<NN>.ApiSchema). Found $coreEntryCount."
    }

    $corePackage = Get-StandardCorePackage
    $defaultFeedUrl = Get-StandardSchemaFeed

    $bootstrapRoot = Get-BootstrapRoot
    $extractionRoot = Join-Path (Join-Path $bootstrapRoot ".tmp") "pkg-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $extractionRoot -Force | Out-Null

    try {
        $schemaSourceFiles = [System.Collections.Generic.List[string]]::new()
        $expectedIdentities = @{}

        foreach ($schemaPackage in $schemaPackages) {
            $packageId = [string]$schemaPackage.name
            $packageVersion = [string]$schemaPackage.version
            $packageFeedUrl = [string]$schemaPackage.feedUrl
            $resolvedFeedUrl = if ([string]::IsNullOrWhiteSpace($packageFeedUrl)) {
                $defaultFeedUrl
            } else {
                $packageFeedUrl
            }

            $isCoreEntry = $packageId -match $script:StandardCorePackageIdPattern

            $resolved = Resolve-StandardSchemaPackage `
                -FeedUrl $resolvedFeedUrl `
                -PackageId $packageId `
                -Version $packageVersion `
                -DestinationRoot $extractionRoot

            $validated = Assert-AssetOnlyPackageContract `
                -ApiSchemaDirectory $resolved.ApiSchemaDirectory `
                -PackageRoot $resolved.PackageRoot `
                -ExpectedPackageId $packageId `
                -ExpectedIsExtension (-not $isCoreEntry)

            if ($isCoreEntry) {
                Assert-RequestedPackageIdentity `
                    -Validated $validated `
                    -ExpectedProjectName $corePackage.ProjectToken `
                    -ExpectedEndpointName $corePackage.EndpointToken `
                    -RequestedName "core"
            }

            $schemaSourceFiles.Add($validated.SchemaPath)
            $expectedIdentities[[System.IO.Path]::GetFullPath($validated.SchemaPath)] = $validated
        }

        # Stage the collected (core + extensions) schema files using the shared staging function.
        # Each entry's own version drove resolution above, so the effective schema hash computed here
        # matches the same SCHEMA_PACKAGES set the DMS container entrypoint resolves at startup.
        Invoke-SchemaWorkspaceStaging `
            -SchemaSourceFiles $schemaSourceFiles.ToArray() `
            -SelectionMode "Standard" `
            -ExpectedIdentities $expectedIdentities
    } finally {
        if (Test-Path -LiteralPath $extractionRoot) {
            Remove-Item -LiteralPath $extractionRoot -Recurse -Force
        }
    }
}

# --- Mode determination ---
if ($hasApiSchemaPath) {
    # Expert mode: use the shared staging function with ApiSchemaPath selection mode.
    Invoke-SchemaWorkspaceStaging `
        -SchemaSourceFiles (Find-ApiSchemaFile -Path $ApiSchemaPath) `
        -SelectionMode "ApiSchemaPath"
} else {
    # Standard mode. When -EnvironmentFile is supplied and its SCHEMA_PACKAGES value lists one or
    # more packages, drive staging from that full set (core plus any extensions) so the staged
    # workspace matches what the DMS container entrypoint resolves from the same env file at
    # startup. Otherwise fall back to the catalog-pinned core-only default (backward-compatible
    # direct-invocation behavior, e.g. no env file available).
    $schemaPackagesEnvironmentFile = $null
    if (-not [string]::IsNullOrWhiteSpace($EnvironmentFile)) {
        Import-Module (Join-Path $PSScriptRoot "../schema-package-utility.psm1") -Force -Global
        $environmentFileFullPath = [System.IO.Path]::GetFullPath($EnvironmentFile)
        if (-not (Test-Path -LiteralPath $environmentFileFullPath -PathType Leaf)) {
            throw "-EnvironmentFile was supplied but the file was not found: $(Format-LogSafeText $environmentFileFullPath)"
        }

        $environmentFileContent = Get-Content -LiteralPath $environmentFileFullPath -Raw
        if ($environmentFileContent -match "(?ms)^[ \t]*SCHEMA_PACKAGES=") {
            $schemaPackagesEnvironmentFile = $environmentFileFullPath
        }
    }

    if ($null -ne $schemaPackagesEnvironmentFile) {
        Invoke-SchemaPackagesModeSchemaStaging -EnvironmentFilePath $schemaPackagesEnvironmentFile
    } else {
        Invoke-StandardModeSchemaStaging `
            -FeedUrl $PackageFeedUrl
    }
}
