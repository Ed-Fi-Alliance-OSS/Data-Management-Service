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
    <#
    .SYNOPSIS
    Repo root resolved from this module's location.
    #>
    return $script:RepoRoot
}

function Get-BootstrapRoot {
    <#
    .SYNOPSIS
    Absolute path to eng/docker-compose/.bootstrap (staged workspace root).
    #>
    return $script:BootstrapRoot
}

function Get-BootstrapWorkspaceMismatchMessage {
    <#
    .SYNOPSIS
    Formats the standard "staged workspace mismatch" error message, optionally including a diverging-field reason.
    #>
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
    <#
    .SYNOPSIS
    Sanitizes a value for safe inclusion in log output (whitelist of letters, digits, and safe punctuation).
    #>
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

function Format-LogSafePath {
    <#
    .SYNOPSIS
    Sanitizes a filesystem path for safe inclusion in log/guidance output. Strips only control
    characters (newlines, tabs, etc.) that enable log forging, while preserving every printable
    character so paths survive intact - including backslashes, spaces, parentheses, '#', and any
    other path-legal punctuation. Use Format-LogSafeText for untrusted/external values where the
    stricter whitelist is appropriate.
    #>
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
        if (-not [char]::IsControl($character)) {
            $null = $builder.Append($character)
        }
    }

    return $builder.ToString()
}

function New-BootstrapManifest {
    <#
    .SYNOPSIS
    Returns a fresh in-memory bootstrap manifest hashtable seeded with the supported schema version.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper; the bootstrap scripts do not expose -WhatIf end to end, so a partial ShouldProcess opt-in would just enable silent no-ops.')]
    param()
    return @{
        version = 1
    }
}

function Read-BootstrapManifest {
    <#
    .SYNOPSIS
    Reads and validates the on-disk bootstrap manifest; returns $null when the file does not exist.
    #>
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
    <#
    .SYNOPSIS
    Writes a JSON payload to disk using UTF-8 (no BOM) with a trailing newline, creating parent directories as needed.
    #>
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
    <#
    .SYNOPSIS
    Persists a bootstrap manifest hashtable to disk with sections in canonical order.
    #>
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
    <#
    .SYNOPSIS
    Replaces a named section (schema, claims, or seed) in the bootstrap manifest on disk.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper; the bootstrap scripts do not expose -WhatIf end to end, so a partial ShouldProcess opt-in would just enable silent no-ops.')]
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
    <#
    .SYNOPSIS
    Returns a forward-slash-normalized path relative to BasePath.
    #>
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
    <#
    .SYNOPSIS
    Resolves a manifest-relative path against the bootstrap workspace root into an absolute path.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $RelativePath
    )

    return [System.IO.Path]::GetFullPath((Join-Path $script:BootstrapRoot $RelativePath))
}

function Resolve-BootstrapWorkspaceRelativePath {
    <#
    .SYNOPSIS
    Validates a manifest field's relative path and returns it normalized; throws if the path escapes the workspace.
    #>
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

function Get-BootstrapFingerprintByte {
    <#
    .SYNOPSIS
    Reads a workspace file as a byte sequence (input to the workspace fingerprint hash).
    #>
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
    <#
    .SYNOPSIS
    Computes a deterministic SHA-256 fingerprint over a directory's file contents and relative paths.
    #>
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
        $incrementalHash.AppendData((Get-BootstrapFingerprintByte -Path $entry.FullName))
        $incrementalHash.AppendData($separator)
    }

    return [System.Convert]::ToHexString($incrementalHash.GetHashAndReset()).ToLowerInvariant()
}

function Read-RequiredJsonBoolean {
    <#
    .SYNOPSIS
    Reads a required boolean field from a JSON hashtable; throws if missing or malformed.
    #>
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
    <#
    .SYNOPSIS
    Validates the on-disk bootstrap manifest for startup; returns $true when a manifest is present. When a manifest is present, activates the staged schema and claims workspaces as the runtime-authoritative source (bootstrap-design.md §3 activation boundary).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper; the bootstrap scripts do not expose -WhatIf end to end, so a partial ShouldProcess opt-in would just enable silent no-ops.')]
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
    # Both package-backed standard mode (prepare-dms-schema.ps1 default, selectionMode "Standard")
    # and expert filesystem mode (-ApiSchemaPath, selectionMode "ApiSchemaPath") stage the same
    # normalized .bootstrap/ApiSchema/ workspace, and startup validation below is identical for both.
    # Rejecting "Standard" here broke the standard-mode/wrapper production path at startup.
    $schemaSelectionMode = $schemaSection["selectionMode"]
    if ($schemaSelectionMode -notin @("Standard", "ApiSchemaPath")) {
        throw "Bootstrap manifest 'schema.selectionMode' must be Standard or ApiSchemaPath, got: $(Format-LogSafeText $schemaSelectionMode)"
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

    # Activate the staged schema workspace as runtime-authoritative (bootstrap-design.md §3 activation
    # boundary). DMS_API_SCHEMA_MOUNT_SOURCE is the host-side source for the bootstrap-dms.yml volume
    # mount; SCHEMA_PACKAGES is set to an empty JSON array so run.sh performs no second package
    # download into the mounted workspace. Do not use an empty string: on Linux PowerShell that
    # removes the process env var, allowing docker compose to fall back to SCHEMA_PACKAGES from
    # the env file.
    $env:USE_API_SCHEMA_PATH = "true"
    $env:API_SCHEMA_PATH = "/app/ApiSchema"
    $env:DMS_API_SCHEMA_MOUNT_SOURCE = $apiSchemaPath
    $env:SCHEMA_PACKAGES = "[]"

    if (-not $manifest.ContainsKey("claims")) {
        if (-not $SkipArtifactValidation) {
            throw "Bootstrap manifest is missing the claims section. Run prepare-dms-claims.ps1 before starting services."
        }

        # No staged claims in manifest (only reachable with -SkipArtifactValidation / teardown).
        # No staged claims workspace to mount; keep mount source blank.
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

    # Activate staged claims per manifest claims.mode (bootstrap-design.md §3 activation boundary).
    # These process-env overrides govern in bootstrap mode regardless of .env-file Hybrid defaults.
    if ($claimsMode -eq "Hybrid") {
        $env:DMS_CONFIG_CLAIMS_SOURCE = "Hybrid"
        $env:DMS_CONFIG_CLAIMS_DIRECTORY = "/app/additional-claims"
        $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = $claimsPath
    } else {
        $env:DMS_CONFIG_CLAIMS_SOURCE = "Embedded"
        $env:DMS_CONFIG_CLAIMS_DIRECTORY = ""
        $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = ""
    }

    return $true
}

$script:BootstrapEnvVarNames = @(
    "DMS_CONFIG_CLAIMS_SOURCE",
    "DMS_CONFIG_CLAIMS_DIRECTORY",
    "DMS_CONFIG_CLAIMS_MOUNT_SOURCE",
    "USE_API_SCHEMA_PATH",
    "API_SCHEMA_PATH",
    "DMS_API_SCHEMA_MOUNT_SOURCE",
    "SCHEMA_PACKAGES"
)

function Get-BootstrapEnvSnapshot {
    <#
    .SYNOPSIS
    Captures current values of the bootstrap-managed environment variables for later restoration.
    #>
    $snapshot = @{}
    foreach ($name in $script:BootstrapEnvVarNames) {
        $snapshot[$name] = [System.Environment]::GetEnvironmentVariable($name)
    }
    return $snapshot
}

function Restore-BootstrapEnvSnapshot {
    <#
    .SYNOPSIS
    Restores bootstrap-managed environment variables from a prior snapshot.
    #>
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

function Invoke-BootstrapStartupConfiguration {
    <#
    .SYNOPSIS
    Shared startup helper for start-local-dms.ps1 / start-published-dms.ps1: validates the bootstrap
    manifest (teardown-tolerant), applies the transitional non-bootstrap Hybrid claims fallback, and
    returns a boolean indicating whether bootstrap mode is active. The caller owns the env snapshot so
    that Restore/Pop run cleanly even when this helper throws.
    .OUTPUTS
    [bool] $true when a valid bootstrap manifest is present (bootstrap mode); $false otherwise.
    #>
    param(
        [switch]
        $IsTeardown,

        [switch]
        $AddExtensionSecurityMetadata
    )

    try {
        $bootstrapMode = Set-BootstrapStartupEnvironment -SkipArtifactValidation:$IsTeardown
    } catch {
        if ($IsTeardown) {
            Write-Warning "Bootstrap manifest could not be loaded during teardown; continuing anyway. $(Format-LogSafeText ($_.Exception.Message))"
            $bootstrapMode = $false
        } else {
            throw
        }
    }

    if (-not $bootstrapMode -and -not $IsTeardown) {
        # DMS-1151: surface the post-bootstrap contract. The wrapper produces .bootstrap/ before
        # invoking the start scripts, so a missing manifest at non-teardown time means the caller
        # is either invoking the start script directly (legitimate for diagnostics or partial-phase
        # orchestration) or has stepped out of sequence. The warning is informational, not blocking
        # - Story 03 owns the eventual hard contract.
        Write-Warning "No bootstrap manifest detected at .bootstrap/. The DMS-1151 pre-start phases (prepare -> configure -> provision) have not produced a staged workspace. The bootstrap-(local|published)-dms.ps1 wrapper is the documented entry point; direct invocation of this script is supported only for diagnostic or partial-phase workflows. Bootstrap schema provisioning will NOT be run by this script."
    }

    if ($bootstrapMode) {
        Write-Information "Bootstrap manifest detected and validated. Staged schema and claims workspaces are now runtime-authoritative; DMS reads ApiSchema from the staged workspace and CMS claims are governed by the manifest claims.mode." -InformationAction Continue
        if ($AddExtensionSecurityMetadata) {
            # In bootstrap mode the manifest's claims.mode governs; -AddExtensionSecurityMetadata
            # is ignored. Set-BootstrapStartupEnvironment already activated staged claims above.
            Write-Information "Extension Security Metadata: bootstrap mode is active; staged claims from manifest govern (AddExtensionSecurityMetadata flag is ignored in bootstrap mode)." -InformationAction Continue
        }
    } elseif ($AddExtensionSecurityMetadata) {
        # Non-bootstrap mode: activate Hybrid claims so extension claimset fragments
        # are loaded from /app/additional-claims (already mounted by the Config Service compose file).
        $env:DMS_CONFIG_CLAIMS_SOURCE = "Hybrid"
        $env:DMS_CONFIG_CLAIMS_DIRECTORY = "/app/additional-claims"
        $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = ""
        Write-Information "Extension Security Metadata: Hybrid claims mode enabled (non-bootstrap startup)." -InformationAction Continue
    }

    return $bootstrapMode
}

function Remove-BootstrapWorkspaceIfRequested {
    <#
    .SYNOPSIS
    Removes the staged .bootstrap workspace when -RemoveBootstrap is requested (paired with teardown).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap helper; the bootstrap scripts do not expose -WhatIf end to end, so a partial ShouldProcess opt-in would just enable silent no-ops.')]
    param(
        [switch]
        $RemoveBootstrap
    )

    if (-not $RemoveBootstrap) {
        return
    }

    $bootstrapDir = Get-BootstrapRoot
    if (Test-Path -LiteralPath $bootstrapDir) {
        Write-Output "Removing bootstrap workspace at $(Format-LogSafeText $bootstrapDir)"
        # Remove-Item is non-terminating by default; promote to a terminating error so a failed
        # cleanup cannot leave a stale manifest behind for the next start to pick up.
        Remove-Item -LiteralPath $bootstrapDir -Recurse -Force -ErrorAction Stop
    }
}

Export-ModuleMember -Function `
    Get-BootstrapRepoRoot, `
    Get-BootstrapRoot, `
    Get-BootstrapWorkspaceMismatchMessage, `
    Format-LogSafeText, `
    Format-LogSafePath, `
    New-BootstrapManifest, `
    Read-BootstrapManifest, `
    Read-RequiredJsonBoolean, `
    Write-BootstrapJson, `
    Write-BootstrapManifest, `
    Set-BootstrapManifestSection, `
    Get-BootstrapRelativePath, `
    Resolve-BootstrapWorkspaceRelativePath, `
    Resolve-BootstrapPath, `
    Get-BootstrapFingerprintByte, `
    Get-BootstrapWorkspaceFingerprint, `
    Set-BootstrapStartupEnvironment, `
    Get-BootstrapEnvSnapshot, `
    Restore-BootstrapEnvSnapshot, `
    Invoke-BootstrapStartupConfiguration, `
    Remove-BootstrapWorkspaceIfRequested
