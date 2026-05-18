# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

$script:DockerComposeRoot = $PSScriptRoot
$script:RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $script:DockerComposeRoot "../.."))
$script:BootstrapRoot = Join-Path $script:DockerComposeRoot ".bootstrap"
$script:BootstrapManifestPath = Join-Path $script:BootstrapRoot "bootstrap-manifest.json"
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$script:WorkspaceMismatchMessage = "Existing staged bootstrap workspace differs from requested inputs, manifest state is incomplete, or files were manually edited (partial prior state). Stop the local stack and remove eng/docker-compose/.bootstrap before retrying. For local Docker, run: pwsh eng/docker-compose/start-local-dms.ps1 -d -v -RemoveBootstrap. E2E teardown wrappers also remove the bootstrap workspace."

function Get-BootstrapRepoRoot {
    return $script:RepoRoot
}

function Get-BootstrapRoot {
    return $script:BootstrapRoot
}

function Get-BootstrapWorkspaceMismatchMessage {
    param(
        [string]
        $Reason
    )

    if (-not [string]::IsNullOrWhiteSpace($Reason)) {
        return $script:WorkspaceMismatchMessage.Replace(
            "Stop the local stack",
            "Diverging field: $(Format-LogSafeText $Reason). Stop the local stack"
        )
    }

    return $script:WorkspaceMismatchMessage
}

function Format-LogSafeText {
    param(
        $Value
    )

    if ($null -eq $Value) {
        return ""
    }

    $text = [string]$Value
    if ([string]::IsNullOrEmpty($text)) {
        return ""
    }

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

function New-BootstrapManifest {
    return @{
        version = 1
    }
}

function Read-BootstrapManifest {
    param(
        [string]
        $Path = $script:BootstrapManifestPath
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

    try {
        $manifestVersion = [int]$manifest["version"]
    } catch {
        throw "Bootstrap manifest '$(Format-LogSafeText $Path)' has malformed version: $(Format-LogSafeText ($manifest["version"]))"
    }

    if ($manifestVersion -gt 1) {
        throw "Bootstrap manifest version unsupported: $(Format-LogSafeText $manifestVersion). This checkout supports version 1."
    }

    if ($manifestVersion -lt 1) {
        throw "Bootstrap manifest '$(Format-LogSafeText $Path)' has malformed version: $(Format-LogSafeText $manifestVersion)"
    }

    $manifest["version"] = $manifestVersion
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
    [System.IO.File]::WriteAllText($Path, "$json`n", $script:Utf8NoBom)
}

function Write-BootstrapManifest {
    param(
        [Parameter(Mandatory)]
        $Manifest,

        [string]
        $Path = $script:BootstrapManifestPath
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
    param(
        [Parameter(Mandatory)]
        [ValidateSet("schema", "claims", "seed")]
        [string]
        $Name,

        [Parameter(Mandatory)]
        $Value,

        [string]
        $Path = $script:BootstrapManifestPath
    )

    $manifest = Read-BootstrapManifest -Path $Path
    if ($null -eq $manifest) {
        $manifest = New-BootstrapManifest
    }

    $manifest[$Name] = $Value
    Write-BootstrapManifest -Manifest $manifest -Path $Path
}

function Get-BootstrapRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]
        $Path,

        [string]
        $BasePath = $script:BootstrapRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullBasePath = [System.IO.Path]::GetFullPath($BasePath)
    return [System.IO.Path]::GetRelativePath($fullBasePath, $fullPath).Replace("\", "/")
}

function Resolve-BootstrapPath {
    param(
        [Parameter(Mandatory)]
        [string]
        $RelativePath
    )

    return [System.IO.Path]::GetFullPath((Join-Path $script:BootstrapRoot $RelativePath))
}

function Resolve-BootstrapWorkspaceRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]
        $RelativePath,

        [Parameter(Mandatory)]
        [string]
        $ManifestField
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw "Bootstrap manifest field '$(Format-LogSafeText $ManifestField)' must not be empty."
    }

    $normalizedPath = $RelativePath.Replace("\", "/")
    if ([System.IO.Path]::IsPathRooted($RelativePath) -or $normalizedPath.StartsWith("/")) {
        throw "Bootstrap manifest field '$(Format-LogSafeText $ManifestField)' must be relative to the bootstrap workspace: $(Format-LogSafeText $RelativePath)"
    }

    $pathSegments = @($normalizedPath -split "/")
    if ($pathSegments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -eq "." -or $_ -eq ".." }) {
        throw "Bootstrap manifest field '$(Format-LogSafeText $ManifestField)' must not contain empty, current, or parent path segments: $(Format-LogSafeText $RelativePath)"
    }

    return $normalizedPath
}

function Get-BootstrapFingerprintBytes {
    param(
        [Parameter(Mandatory)]
        [string]
        $Path
    )

    $extension = [System.IO.Path]::GetExtension($Path)
    $isTextFile = $extension.Equals(".json", [System.StringComparison]::OrdinalIgnoreCase) -or
                  $extension.Equals(".xsd", [System.StringComparison]::OrdinalIgnoreCase)

    if ($isTextFile) {
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
        $Path,

        [string[]]
        $ExcludeRelativePath = @()
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Workspace path does not exist: $(Format-LogSafeText $Path)"
    }

    $excludeSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in $ExcludeRelativePath) {
        $null = $excludeSet.Add($relativePath.Replace("\", "/"))
    }

    $entries = @(
        Get-ChildItem -LiteralPath $Path -File -Recurse | ForEach-Object {
            $relativePath = Get-BootstrapRelativePath -Path $_.FullName -BasePath $Path
            if (-not $excludeSet.Contains($relativePath)) {
                [pscustomobject]@{
                    RelativePath = $relativePath
                    FullName = $_.FullName
                }
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
        $incrementalHash.AppendData((Get-BootstrapFingerprintBytes -Path $entry.FullName))
        $incrementalHash.AppendData($separator)
    }

    return [System.Convert]::ToHexString($incrementalHash.GetHashAndReset()).ToLowerInvariant()
}

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

function Set-BootstrapStartupEnvironment {
    param(
        [switch]
        $SkipArtifactValidation
    )

    if (-not (Test-Path -LiteralPath $script:BootstrapManifestPath)) {
        return $false
    }

    $manifest = Read-BootstrapManifest
    if ($null -eq $manifest -or -not $manifest.ContainsKey("schema")) {
        throw "Bootstrap manifest is missing the schema section. Run prepare-dms-schema.ps1 before starting services."
    }

    $schemaSection = $manifest["schema"]
    if ($schemaSection -isnot [System.Collections.IDictionary]) {
        throw "Bootstrap manifest schema section must be a JSON object."
    }
    $schemaMode = $schemaSection["mode"]
    if ($schemaMode -ne "ApiSchemaPath") {
        throw "Bootstrap schema mode '$(Format-LogSafeText $schemaMode)' is not supported in Story 00; only 'ApiSchemaPath' is accepted."
    }
    if (-not $schemaSection.ContainsKey("apiSchemaManifestPath") -or [string]::IsNullOrWhiteSpace($schemaSection["apiSchemaManifestPath"])) {
        throw "Bootstrap manifest field 'schema.apiSchemaManifestPath' must not be empty."
    }
    $apiSchemaManifestRelativePath = Resolve-BootstrapWorkspaceRelativePath `
        -RelativePath $schemaSection["apiSchemaManifestPath"] `
        -ManifestField "schema.apiSchemaManifestPath"
    $apiSchemaManifestPath = Resolve-BootstrapPath -RelativePath $apiSchemaManifestRelativePath

    $apiSchemaPath = Join-Path $script:BootstrapRoot "ApiSchema"
    if (-not $SkipArtifactValidation -and -not (Test-Path -LiteralPath $apiSchemaPath)) {
        throw "Bootstrap ApiSchema workspace is missing: $(Format-LogSafeText $apiSchemaPath)"
    }

    if (-not $SkipArtifactValidation -and -not (Test-Path -LiteralPath $apiSchemaManifestPath -PathType Leaf)) {
        throw "Bootstrap ApiSchema manifest is missing: $(Format-LogSafeText $apiSchemaManifestPath)"
    }

    # NOTE (Story 04): Flipping DMS into staged-workspace runtime loading (USE_API_SCHEMA_PATH=true,
    # API_SCHEMA_PATH=/app/ApiSchema, clearing SCHEMA_PACKAGES, and mounting .bootstrap/ApiSchema via
    # bootstrap-dms.yml) is deferred to Story 04. ContentProvider still expects *.ApiSchema.dll assemblies,
    # so activating that path here would cause discovery/XSD content fetches to fail in bootstrap mode.
    # Story 00 validates the manifest and wires CMS claims loading only; Story 04 owns the DMS runtime flip.

    if (-not $manifest.ContainsKey("claims")) {
        if (-not $SkipArtifactValidation) {
            throw "Bootstrap manifest is missing the claims section. Run prepare-dms-claims.ps1 before starting services."
        }

        # Clear any stale claims env vars inherited from a prior run so teardown does not carry them forward.
        $env:DMS_CONFIG_CLAIMS_SOURCE = ""
        $env:DMS_CONFIG_CLAIMS_DIRECTORY = ""
        $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = ""
        return $true
    }

    $claimsSection = $manifest["claims"]
    if ($claimsSection -isnot [System.Collections.IDictionary]) {
        throw "Bootstrap manifest claims section must be a JSON object."
    }
    $claimsMode = $claimsSection["mode"]
    if ($claimsMode -notin @("Embedded", "Hybrid")) {
        throw "Bootstrap manifest field 'claims.mode' must be Embedded or Hybrid: $(Format-LogSafeText $claimsMode)"
    }

    if (-not $claimsSection.ContainsKey("directory") -or [string]::IsNullOrWhiteSpace($claimsSection["directory"])) {
        throw "Bootstrap manifest field 'claims.directory' must not be empty."
    }
    $claimsDirectory = $claimsSection["directory"]
    $claimsDirectory = Resolve-BootstrapWorkspaceRelativePath `
        -RelativePath $claimsDirectory `
        -ManifestField "claims.directory"

    $claimsPath = Resolve-BootstrapPath -RelativePath $claimsDirectory
    if (-not $SkipArtifactValidation -and -not (Test-Path -LiteralPath $claimsPath)) {
        throw "Bootstrap claims workspace is missing: $(Format-LogSafeText $claimsPath)"
    }

    if (-not $manifest.ContainsKey("seed")) {
        throw "Bootstrap manifest is missing the seed section. Run prepare-dms-claims.ps1 before starting services."
    }

    $seedSection = $manifest["seed"]
    if ($seedSection -isnot [System.Collections.IDictionary]) {
        throw "Bootstrap manifest seed section must be a JSON object."
    }

    if (-not $seedSection.ContainsKey("extensionNamespacePrefixes")) {
        throw "Bootstrap manifest seed section is missing 'extensionNamespacePrefixes'."
    }

    $extensionNamespacePrefixes = $seedSection["extensionNamespacePrefixes"]
    if ($null -ne $extensionNamespacePrefixes -and $extensionNamespacePrefixes -isnot [System.Collections.IList]) {
        throw "Bootstrap manifest seed.extensionNamespacePrefixes must be a JSON array."
    }

    foreach ($extensionNamespacePrefix in @($extensionNamespacePrefixes)) {
        if ($extensionNamespacePrefix -isnot [string]) {
            throw "Bootstrap manifest seed.extensionNamespacePrefixes must contain only strings."
        }
    }

    $env:DMS_CONFIG_CLAIMS_SOURCE = $claimsMode
    $env:DMS_CONFIG_CLAIMS_DIRECTORY = "/app/additional-claims"
    $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = [System.IO.Path]::GetFullPath((Join-Path $script:DockerComposeRoot ".bootstrap/$claimsDirectory"))

    return $true
}

$script:BootstrapEnvVarNames = @(
    "DMS_CONFIG_CLAIMS_SOURCE",
    "DMS_CONFIG_CLAIMS_DIRECTORY",
    "DMS_CONFIG_CLAIMS_MOUNT_SOURCE"
)

function Get-BootstrapEnvSnapshot {
    $snapshot = @{}
    foreach ($name in $script:BootstrapEnvVarNames) {
        $snapshot[$name] = [System.Environment]::GetEnvironmentVariable($name)
    }
    return $snapshot
}

function Restore-BootstrapEnvSnapshot {
    param(
        [Parameter(Mandatory)]
        [hashtable]
        $Snapshot
    )

    foreach ($name in $script:BootstrapEnvVarNames) {
        if ($Snapshot.ContainsKey($name)) {
            $value = $Snapshot[$name]
            if ($null -eq $value) {
                [System.Environment]::SetEnvironmentVariable($name, $null)
            } else {
                [System.Environment]::SetEnvironmentVariable($name, $value)
            }
        } else {
            [System.Environment]::SetEnvironmentVariable($name, $null)
        }
    }
}

Export-ModuleMember -Function `
    Get-BootstrapRepoRoot, `
    Get-BootstrapRoot, `
    Get-BootstrapWorkspaceMismatchMessage, `
    Format-LogSafeText, `
    New-BootstrapManifest, `
    Read-BootstrapManifest, `
    Read-RequiredJsonBoolean, `
    Write-BootstrapJson, `
    Write-BootstrapManifest, `
    Set-BootstrapManifestSection, `
    Get-BootstrapRelativePath, `
    Resolve-BootstrapWorkspaceRelativePath, `
    Resolve-BootstrapPath, `
    Get-BootstrapFingerprintBytes, `
    Get-BootstrapWorkspaceFingerprint, `
    Set-BootstrapStartupEnvironment, `
    Get-BootstrapEnvSnapshot, `
    Restore-BootstrapEnvSnapshot
