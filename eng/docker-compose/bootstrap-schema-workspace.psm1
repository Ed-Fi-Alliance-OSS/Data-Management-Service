# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force

function Get-BootstrapSchemaProperty {
    param(
        [Parameter(Mandatory)]
        $Object,

        [Parameter(Mandatory)]
        [string]
        $Name
    )

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.ContainsKey($Name)) {
            return $Object[$Name]
        }

        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Resolve-StagedApiSchemaPath {
    param(
        [Parameter(Mandatory)]
        [string]
        $RelativePath,

        [Parameter(Mandatory)]
        [string]
        $ApiSchemaRoot,

        [Parameter(Mandatory)]
        [string]
        $ApiSchemaManifestDirectory,

        [Parameter(Mandatory)]
        [string]
        $ManifestField
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw "ApiSchema manifest field '$(Format-LogSafeText $ManifestField)' must not be empty."
    }

    $normalizedPath = $RelativePath.Replace("\", "/")
    if ([System.IO.Path]::IsPathRooted($RelativePath) -or $normalizedPath.StartsWith("/")) {
        throw "ApiSchema manifest field '$(Format-LogSafeText $ManifestField)' must be relative to the staged ApiSchema workspace: $(Format-LogSafeText $RelativePath)"
    }

    $pathSegments = @($normalizedPath -split "/")
    if ($pathSegments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq "." -or $_ -eq ".." }) {
        throw "ApiSchema manifest field '$(Format-LogSafeText $ManifestField)' must not contain empty, current, or parent path segments: $(Format-LogSafeText $RelativePath)"
    }

    $resolvedPath = [System.IO.Path]::GetFullPath((Join-Path $ApiSchemaManifestDirectory $normalizedPath))
    $resolvedRoot = [System.IO.Path]::GetFullPath($ApiSchemaRoot)
    $relativeToRoot = [System.IO.Path]::GetRelativePath($resolvedRoot, $resolvedPath).Replace("\", "/")

    if ($relativeToRoot.StartsWith("../", [System.StringComparison]::Ordinal) -or
        $relativeToRoot.Equals("..", [System.StringComparison]::Ordinal) -or
        [System.IO.Path]::IsPathRooted($relativeToRoot)) {
        throw "ApiSchema manifest field '$(Format-LogSafeText $ManifestField)' escapes the staged ApiSchema workspace: $(Format-LogSafeText $RelativePath)"
    }

    return $resolvedPath
}

function Resolve-BootstrapSchemaWorkspace {
    <#
    .SYNOPSIS
    Validates the staged schema handoff and returns core and extension schema paths in
    provisioning order.
    #>
    param(
        [string]
        $BootstrapManifestPath
    )

    if ([string]::IsNullOrWhiteSpace($BootstrapManifestPath)) {
        $BootstrapManifestPath = Join-Path (Get-BootstrapRoot) "bootstrap-manifest.json"
    }
    else {
        $BootstrapManifestPath = [System.IO.Path]::GetFullPath($BootstrapManifestPath)
    }

    if (-not (Test-Path -LiteralPath $BootstrapManifestPath -PathType Leaf)) {
        throw "Bootstrap manifest not found at $(Format-LogSafeText $BootstrapManifestPath). Run prepare-dms-schema.ps1 before invoking schema provisioning."
    }

    $manifest = Read-BootstrapManifest -Path $BootstrapManifestPath
    if ($null -eq $manifest -or -not $manifest.ContainsKey("schema") -or $manifest["schema"] -isnot [System.Collections.IDictionary]) {
        throw "Bootstrap manifest is missing or has a malformed schema section."
    }

    $schemaSection = $manifest["schema"]
    if (-not $schemaSection.ContainsKey("apiSchemaManifestPath") -or [string]::IsNullOrWhiteSpace([string]$schemaSection["apiSchemaManifestPath"])) {
        throw "Bootstrap manifest field 'schema.apiSchemaManifestPath' must not be empty."
    }

    $bootstrapRoot = Split-Path -Parent $BootstrapManifestPath
    $apiSchemaManifestRelativePath = Resolve-BootstrapWorkspaceRelativePath `
        -RelativePath ([string]$schemaSection["apiSchemaManifestPath"]) `
        -ManifestField "schema.apiSchemaManifestPath"
    $apiSchemaManifestPath = [System.IO.Path]::GetFullPath((Join-Path $bootstrapRoot $apiSchemaManifestRelativePath))

    if (-not (Test-Path -LiteralPath $apiSchemaManifestPath -PathType Leaf)) {
        throw "Bootstrap ApiSchema manifest is missing: $(Format-LogSafeText $apiSchemaManifestPath). Run prepare-dms-schema.ps1 before invoking schema provisioning."
    }

    try {
        $apiSchemaManifest = Get-Content -LiteralPath $apiSchemaManifestPath -Raw | ConvertFrom-Json -AsHashtable
    }
    catch {
        throw "Bootstrap ApiSchema manifest '$(Format-LogSafeText $apiSchemaManifestPath)' contains malformed JSON. $(Format-LogSafeText ($_.Exception.Message))"
    }

    $projects = $null
    if ($apiSchemaManifest -is [System.Collections.IDictionary] -and $apiSchemaManifest.ContainsKey("projects")) {
        $projects = $apiSchemaManifest["projects"]
    }
    elseif ($apiSchemaManifest -is [System.Collections.IList]) {
        $projects = $apiSchemaManifest
    }

    if ($null -eq $projects -or $projects -isnot [System.Collections.IList]) {
        throw "Bootstrap ApiSchema manifest must contain a projects array."
    }

    $apiSchemaRoot = [System.IO.Path]::GetFullPath((Join-Path $bootstrapRoot "ApiSchema"))
    $apiSchemaManifestDirectory = [System.IO.Path]::GetDirectoryName($apiSchemaManifestPath)
    $coreProjects = [System.Collections.ArrayList]::new()
    $extensionProjects = [System.Collections.ArrayList]::new()

    foreach ($project in @($projects)) {
        $isExtensionProject = Get-BootstrapSchemaProperty -Object $project -Name "isExtensionProject"
        if ($isExtensionProject -isnot [bool]) {
            throw "Bootstrap ApiSchema manifest project has malformed boolean for 'isExtensionProject'."
        }

        if ($isExtensionProject) {
            $null = $extensionProjects.Add($project)
        }
        else {
            $null = $coreProjects.Add($project)
        }
    }

    if ($coreProjects.Count -ne 1) {
        throw "Bootstrap ApiSchema manifest must contain exactly one core project. Found $($coreProjects.Count)."
    }

    $coreSchemaRelativePath = [string](Get-BootstrapSchemaProperty -Object $coreProjects[0] -Name "schemaPath")
    $coreSchemaPath = Resolve-StagedApiSchemaPath `
        -RelativePath $coreSchemaRelativePath `
        -ApiSchemaRoot $apiSchemaRoot `
        -ApiSchemaManifestDirectory $apiSchemaManifestDirectory `
        -ManifestField "projects[].schemaPath"

    if (-not (Test-Path -LiteralPath $coreSchemaPath -PathType Leaf)) {
        throw "Staged core schema file is missing: $(Format-LogSafeText $coreSchemaPath). Run prepare-dms-schema.ps1 before invoking schema provisioning."
    }

    $extensionSchemaPaths = [System.Collections.ArrayList]::new()
    foreach ($extensionProject in $extensionProjects) {
        $extensionSchemaRelativePath = [string](Get-BootstrapSchemaProperty -Object $extensionProject -Name "schemaPath")
        $extensionSchemaPath = Resolve-StagedApiSchemaPath `
            -RelativePath $extensionSchemaRelativePath `
            -ApiSchemaRoot $apiSchemaRoot `
            -ApiSchemaManifestDirectory $apiSchemaManifestDirectory `
            -ManifestField "projects[].schemaPath"

        if (-not (Test-Path -LiteralPath $extensionSchemaPath -PathType Leaf)) {
            throw "Staged extension schema file is missing: $(Format-LogSafeText $extensionSchemaPath). Run prepare-dms-schema.ps1 before invoking schema provisioning."
        }

        $null = $extensionSchemaPaths.Add($extensionSchemaPath)
    }

    $effectiveSchemaHash = if ($schemaSection.ContainsKey("effectiveSchemaHash")) { [string]$schemaSection["effectiveSchemaHash"] } else { "" }
    $workspaceFingerprint = if ($schemaSection.ContainsKey("workspaceFingerprint")) { [string]$schemaSection["workspaceFingerprint"] } else { "" }
    if ([string]::IsNullOrWhiteSpace($workspaceFingerprint)) {
        throw "Bootstrap manifest field 'schema.workspaceFingerprint' must not be empty."
    }

    $currentWorkspaceFingerprint = Get-BootstrapWorkspaceFingerprint -Path $apiSchemaRoot
    if ($currentWorkspaceFingerprint -ne $workspaceFingerprint) {
        throw (Get-BootstrapWorkspaceMismatchMessage -Reason "staged schema workspace fingerprint mismatch")
    }

    return [pscustomobject]@{
        BootstrapManifestPath = [System.IO.Path]::GetFullPath($BootstrapManifestPath)
        ApiSchemaManifestPath = $apiSchemaManifestPath
        CoreSchemaPath = $coreSchemaPath
        ExtensionSchemaPaths = [string[]]@($extensionSchemaPaths)
        EffectiveSchemaHash = $effectiveSchemaHash
        WorkspaceFingerprint = $workspaceFingerprint
    }
}

Export-ModuleMember -Function Resolve-BootstrapSchemaWorkspace
