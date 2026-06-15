# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param(
    [string]
    $ApiSchemaPath,

    [string[]]
    $Extensions,

    [string]
    $SchemaToolPath = $env:DMS_SCHEMA_TOOL_PATH
)

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot "bootstrap-schema-tool.psm1") -Force -Global
Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force -Global

$story06Message = "Package-backed standard schema selection is deferred until Story 06. For Story 00, supply -ApiSchemaPath pointing to a filesystem ApiSchema directory."

if ($PSBoundParameters.ContainsKey("Extensions")) {
    throw $story06Message
}

if (-not $PSBoundParameters.ContainsKey("ApiSchemaPath") -or [string]::IsNullOrWhiteSpace($ApiSchemaPath)) {
    throw $story06Message
}

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
        $CopyOperations
    )

    $contentRoot = "content/$($Project.ProjectDirectoryName)"
    $discoverySpecSourcePath = Join-Path $Project.SourceDirectory "discovery-spec.json"
    $discoverySpecRelativePath = $null
    if (Test-Path -LiteralPath $discoverySpecSourcePath -PathType Leaf) {
        $discoverySpecRelativePath = "$contentRoot/discovery-spec.json"
        Add-CopyOperation `
            -TargetSources $TargetSources `
            -CopyOperations $CopyOperations `
            -SourcePath $discoverySpecSourcePath `
            -RelativeTargetPath $discoverySpecRelativePath
    }

    $xsdSourceDirectory = Join-Path $Project.SourceDirectory "xsd"
    if (-not (Test-Path -LiteralPath $xsdSourceDirectory -PathType Container)) {
        $xsdSourceDirectory = Join-Path $Project.SourceDirectory "XSD"
    }

    $xsdDirectoryRelativePath = $null
    if (Test-Path -LiteralPath $xsdSourceDirectory -PathType Container) {
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

$schemaSourceFiles = Find-ApiSchemaFile -Path $ApiSchemaPath
$schemaProjects = @($schemaSourceFiles | ForEach-Object { Read-ApiSchemaIdentity -Path $_ })

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
        # When two or more projects share a source directory AND that directory carries
        # schema-adjacent content (discovery-spec.json or xsd/), the core schema owns the staged
        # content path that every project in the group references. Extension-only groups have no
        # unambiguous owner in that case — fail fast and tell the caller to put each extension in
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
    # that ApiSchemaValidator uses to load this meta-schema at startup).
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
        selectionMode = "ApiSchemaPath"
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
