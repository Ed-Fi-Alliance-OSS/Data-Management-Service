# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param(
    [string]
    $ClaimsDirectoryPath
)

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "bootstrap-schema-catalog.psm1") -Force

$baselineFragmentFileNames = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        "001-namespace-claimset.json",
        "002-nofurtherauth-claimset.json",
        "003-edorgsonly-claimset.json"
    ),
    [System.StringComparer]::OrdinalIgnoreCase
)

function Read-JsonHashtable {
    param(
        [Parameter(Mandatory)]
        [string]
        $Path,

        [Parameter(Mandatory)]
        [string]
        $ArtifactName
    )

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    } catch {
        throw "$(Format-LogSafeText $ArtifactName) '$(Format-LogSafeText $Path)' contains malformed JSON. $(Format-LogSafeText ($_.Exception.Message))"
    }
}

function Get-ValueOrNull {
    param(
        $Hashtable,

        [Parameter(Mandatory)]
        [string]
        $Key
    )

    if ($Hashtable -is [System.Collections.IDictionary] -and $Hashtable.Contains($Key)) {
        # Wrap with the unary comma so PowerShell does not flatten single-element arrays into
        # scalars on function return - callers rely on the original shape for IList checks.
        return ,$Hashtable[$Key]
    }

    return $null
}

function Add-EffectiveClaimSetName {
    param(
        [System.Collections.Generic.HashSet[string]]
        $ClaimSetNames,

        [string]
        $Name
    )

    if (-not [string]::IsNullOrWhiteSpace($Name)) {
        $null = $ClaimSetNames.Add($Name)
    }
}

function Test-TruthyJsonValue {
    param(
        $Value,

        [string]
        $Path
    )

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return $Value
    }

    throw "Claimset fragment '$(Format-LogSafeText $Path)' has malformed boolean for 'isParent'."
}

function Get-EffectiveClaimSetName {
    $repoRoot = Get-BootstrapRepoRoot
    $claimsPath = Join-Path $repoRoot "src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Standards/ds52/Claims.json"
    $claims = Read-JsonHashtable -Path $claimsPath -ArtifactName "Embedded claims"
    $claimSetNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    # Embedded Claims.json declares effective claim sets in the top-level claimSets[*].claimSetName list.
    # Nested claimsHierarchy[*].claimSets[*].name entries are attachments to those claim sets, not definitions.
    foreach ($claimSet in @((Get-ValueOrNull -Hashtable $claims -Key "claimSets"))) {
        if ($claimSet -is [System.Collections.IDictionary]) {
            Add-EffectiveClaimSetName `
                -ClaimSetNames $claimSetNames `
                -Name (Get-ValueOrNull -Hashtable $claimSet -Key "claimSetName")
        }
    }

    return $claimSetNames
}

function Add-FragmentInput {
    param(
        [System.Collections.Generic.Dictionary[string, string]]
        $TargetSources,

        [System.Collections.ArrayList]
        $Fragments,

        [Parameter(Mandatory)]
        [string]
        $SourcePath
    )

    $fileName = [System.IO.Path]::GetFileName($SourcePath)
    if ($TargetSources.ContainsKey($fileName)) {
        throw "Claimset fragment filename collision for '$(Format-LogSafeText $fileName)' from '$(Format-LogSafeText $SourcePath)' and '$(Format-LogSafeText ($TargetSources[$fileName]))'."
    }

    $TargetSources[$fileName] = $SourcePath
    $null = $Fragments.Add(
        [pscustomobject]@{
            SourcePath = $SourcePath
            FileName = $fileName
        }
    )
}

function Get-UserFragmentFile {
    param(
        [string]
        $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @()
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            throw "ClaimsDirectoryPath must be a directory: $(Format-LogSafeText $fullPath)"
        }

        throw "ClaimsDirectoryPath directory was not found: $(Format-LogSafeText $fullPath)"
    }

    $directory = Get-Item -LiteralPath $fullPath

    $claimsetFiles = @(
        Get-ChildItem -LiteralPath $directory.FullName -File -Filter "*-claimset.json" -Recurse |
            Sort-Object -Property FullName
    )

    $reservedFiles = @($claimsetFiles | Where-Object { $baselineFragmentFileNames.Contains($_.Name) })
    if ($reservedFiles.Count -gt 0) {
        $reservedFileNames = @($reservedFiles | ForEach-Object { $_.Name }) -join ", "
        throw "ClaimsDirectoryPath '$(Format-LogSafeText ($directory.FullName))' contains reserved baseline fragment filename(s): $(Format-LogSafeText $reservedFileNames). Baseline fragment names are reserved."
    }

    $files = @($claimsetFiles | ForEach-Object { $_.FullName })
    if ($files.Count -eq 0) {
        throw "ClaimsDirectoryPath '$(Format-LogSafeText ($directory.FullName))' does not contain any non-baseline *-claimset.json files."
    }

    return $files
}

function Add-ExpectedVerificationCheck {
    param(
        [System.Collections.Generic.HashSet[string]]
        $Seen,

        [System.Collections.ArrayList]
        $Checks,

        [string]
        $ClaimSetName,

        [string]
        $ResourceClaim,

        [string]
        $Action,

        # Marks a check extracted from a parent (isParent=true) fragment resource claim.
        # Parent grants propagate to leaf claims via hierarchy lineage and are NOT directly
        # observable in CMS /authorizationMetadata (which serializes leaf resource claims
        # only), so the claims-ready gate defers these instead of asserting them.
        [switch]
        $IsParent,

        # When set, an empty ClaimSetName/ResourceClaim/Action throws instead of being silently
        # skipped. The catalog loop passes this so a malformed static KnownExtensionClaimsMetadata
        # entry fails fast at prepare time; fragment-derived checks leave it off (default) because a
        # fragment's own structural validation already owns those errors.
        [switch]
        $ThrowOnInvalid,

        # Optional context surfaced in the -ThrowOnInvalid error message (e.g. the extension name).
        [string]
        $Source = ""

    )

    if ([string]::IsNullOrWhiteSpace($ClaimSetName) -or
        [string]::IsNullOrWhiteSpace($ResourceClaim) -or
        [string]::IsNullOrWhiteSpace($Action)) {
        if ($ThrowOnInvalid) {
            $sourceSuffix = if ([string]::IsNullOrWhiteSpace($Source)) { "" } else { " for $(Format-LogSafeText $Source)" }
            throw "Malformed verification check$sourceSuffix (ClaimSetName, ResourceClaim, and Action are all required)."
        }
        return
    }

    $key = "$ClaimSetName|$ResourceClaim|$Action"
    if ($Seen.Add($key)) {
        $check = [ordered]@{
            claimSetName = $ClaimSetName
            resourceClaim = $ResourceClaim
            action = $Action
        }
        if ($IsParent) {
            $check.isParent = $true
        }
        $null = $Checks.Add($check)
    }
}

function Assert-FragmentValidAndExtractCheck {
    param(
        [Parameter(Mandatory)]
        [string]
        $Path,

        [System.Collections.Generic.HashSet[string]]
        $EffectiveClaimSetNames,

        [System.Collections.Generic.HashSet[string]]
        $SeenChecks,

        [System.Collections.ArrayList]
        $ExpectedVerificationChecks

    )

    $fragment = Read-JsonHashtable -Path $Path -ArtifactName "Claimset fragment"
    $fragmentName = Get-ValueOrNull -Hashtable $fragment -Key "name"

    $resourceClaims = Get-ValueOrNull -Hashtable $fragment -Key "resourceClaims"
    if ($null -eq $resourceClaims) {
        throw "Claimset fragment '$(Format-LogSafeText $Path)' does not contain resourceClaims."
    }
    if ($resourceClaims -isnot [System.Collections.IList]) {
        throw "Claimset fragment '$(Format-LogSafeText $Path)' has a resourceClaims value that is not a JSON array."
    }
    if (@($resourceClaims).Count -eq 0) {
        throw "Claimset fragment '$(Format-LogSafeText $Path)' does not contain resourceClaims."
    }

    $usesTopLevelNameAsClaimSet = $false
    $implicitVerificationChecks = [System.Collections.ArrayList]::new()
    foreach ($resourceClaim in @($resourceClaims)) {
        if ($resourceClaim -isnot [System.Collections.IDictionary]) {
            throw "Claimset fragment '$(Format-LogSafeText $Path)' has a resourceClaims entry that is not a JSON object."
        }

        $resourceClaimName = Get-ValueOrNull -Hashtable $resourceClaim -Key "name"
        if ([string]::IsNullOrWhiteSpace($resourceClaimName)) {
            throw "Claimset fragment '$(Format-LogSafeText $Path)' has a resourceClaims entry missing 'name'."
        }

        $isParent = Test-TruthyJsonValue `
            -Value (Get-ValueOrNull -Hashtable $resourceClaim -Key "isParent") `
            -Path $Path

        $claimSets = Get-ValueOrNull -Hashtable $resourceClaim -Key "claimSets"
        if ($null -ne $claimSets -and $claimSets -isnot [System.Collections.IList]) {
            throw "Claimset fragment '$(Format-LogSafeText $Path)' has a claimSets value that is not a JSON array."
        }
        $claimSetsCount = if ($null -eq $claimSets) { 0 } else { @($claimSets).Count }
        if (-not $isParent -and $claimSetsCount -gt 0) {
            throw "Claimset fragment '$(Format-LogSafeText $Path)' has a non-parent resourceClaims entry with claimSets. CMS composes non-parent claims from the fragment top-level name and authorizationStrategyOverridesForCRUD; move the actions there or make the resource claim a parent."
        }

        $usesImplicitClaimSetName = -not $isParent -and $claimSetsCount -eq 0
        if ($usesImplicitClaimSetName) {
            $usesTopLevelNameAsClaimSet = $true
        }

        foreach ($claimSet in @($claimSets)) {
            if ($null -eq $claimSet) {
                continue
            }

            $claimSetName = Get-ValueOrNull -Hashtable $claimSet -Key "name"
            if ([string]::IsNullOrWhiteSpace($claimSetName)) {
                throw "Claimset fragment '$(Format-LogSafeText $Path)' has a claimSets entry missing 'name'."
            }

            if (-not $EffectiveClaimSetNames.Contains($claimSetName)) {
                throw "Claimset fragment '$(Format-LogSafeText $Path)' references unknown effective claim set '$(Format-LogSafeText $claimSetName)'."
            }

            $actions = Get-ValueOrNull -Hashtable $claimSet -Key "actions"
            if ($null -ne $actions -and $actions -isnot [System.Collections.IList]) {
                throw "Claimset fragment '$(Format-LogSafeText $Path)' has a claimSets actions value that is not a JSON array."
            }
            foreach ($action in @($actions)) {
                if ($null -eq $action) {
                    continue
                }

                $actionName = Get-ValueOrNull -Hashtable $action -Key "name"
                if ([string]::IsNullOrWhiteSpace($actionName)) {
                    throw "Claimset fragment '$(Format-LogSafeText $Path)' has a claimSets actions entry missing 'name'."
                }

                # Explicit claimSets entries only occur on parent resource claims (non-parent
                # entries with claimSets are rejected above), so mark the check as parent-derived:
                # the grant materializes on leaf descendants via hierarchy lineage and the parent
                # name itself never appears in /authorizationMetadata claims[].
                Add-ExpectedVerificationCheck `
                    -Seen $SeenChecks `
                    -Checks $ExpectedVerificationChecks `
                    -ClaimSetName $claimSetName `
                    -ResourceClaim $resourceClaimName `
                    -Action $actionName `
                    -IsParent:$isParent
            }
        }

        if ($usesImplicitClaimSetName) {
            $overrideActions = Get-ValueOrNull -Hashtable $resourceClaim -Key "authorizationStrategyOverridesForCRUD"
            if ($null -ne $overrideActions -and $overrideActions -isnot [System.Collections.IList]) {
                throw "Claimset fragment '$(Format-LogSafeText $Path)' has an authorizationStrategyOverridesForCRUD value that is not a JSON array."
            }
            foreach ($action in @($overrideActions)) {
                $actionName = Get-ValueOrNull -Hashtable $action -Key "actionName"
                if ([string]::IsNullOrWhiteSpace($actionName)) {
                    throw "Claimset fragment '$(Format-LogSafeText $Path)' has an authorizationStrategyOverridesForCRUD entry missing 'actionName'."
                }

                $null = $implicitVerificationChecks.Add(
                    [pscustomobject]@{
                        ResourceClaim = $resourceClaimName
                        Action = $actionName
                    }
                )
            }
        }
    }

    if ($usesTopLevelNameAsClaimSet) {
        if ([string]::IsNullOrWhiteSpace($fragmentName)) {
            throw "Claimset fragment '$(Format-LogSafeText $Path)' is missing top-level name required by non-parent resource claims."
        }

        if (-not $EffectiveClaimSetNames.Contains($fragmentName)) {
            throw "Claimset fragment '$(Format-LogSafeText $Path)' uses unknown effective claim set '$(Format-LogSafeText $fragmentName)'."
        }

        foreach ($implicitVerificationCheck in $implicitVerificationChecks) {
            Add-ExpectedVerificationCheck `
                -Seen $SeenChecks `
                -Checks $ExpectedVerificationChecks `
                -ClaimSetName $fragmentName `
                -ResourceClaim $implicitVerificationCheck.ResourceClaim `
                -Action $implicitVerificationCheck.Action
        }
    }
}

$bootstrapRoot = Get-BootstrapRoot
$rootManifest = Read-BootstrapManifest
if ($null -eq $rootManifest -or -not $rootManifest.ContainsKey("schema")) {
    throw "Bootstrap manifest is missing the schema section. Run prepare-dms-schema.ps1 before prepare-dms-claims.ps1."
}

# Resolve the ApiSchema manifest from the schema handoff's recorded schema.apiSchemaManifestPath
# rather than hardcoding a path, so claims staging validates and stages against the exact ApiSchema
# manifest the schema phase recorded. This matches the other consumers of the field
# (Resolve-BootstrapSchemaWorkspace and Set-BootstrapStartupEnvironment); hardcoding could otherwise
# let claims staging consume a different manifest than the one prepare-dms-schema.ps1 recorded.
$schemaSection = $rootManifest["schema"]
if ($schemaSection -isnot [System.Collections.IDictionary]) {
    throw "Bootstrap manifest schema section must be a JSON object. Run prepare-dms-schema.ps1 before prepare-dms-claims.ps1."
}
if (-not $schemaSection.ContainsKey("apiSchemaManifestPath") -or [string]::IsNullOrWhiteSpace([string]$schemaSection["apiSchemaManifestPath"])) {
    throw "Bootstrap manifest field 'schema.apiSchemaManifestPath' must not be empty. Run prepare-dms-schema.ps1 before prepare-dms-claims.ps1."
}
$apiSchemaManifestRelativePath = Resolve-BootstrapWorkspaceRelativePath `
    -RelativePath ([string]$schemaSection["apiSchemaManifestPath"]) `
    -ManifestField "schema.apiSchemaManifestPath"
$apiSchemaManifestPath = Resolve-BootstrapPath -RelativePath $apiSchemaManifestRelativePath
if (-not (Test-Path -LiteralPath $apiSchemaManifestPath -PathType Leaf)) {
    throw "Staged ApiSchema manifest was not found: $(Format-LogSafeText $apiSchemaManifestPath). Run prepare-dms-schema.ps1 before prepare-dms-claims.ps1."
}

$apiSchemaManifest = Read-JsonHashtable -Path $apiSchemaManifestPath -ArtifactName "ApiSchema manifest"
$projectsValue = Get-ValueOrNull -Hashtable $apiSchemaManifest -Key "projects"
if ($null -eq $projectsValue) {
    throw "Bootstrap ApiSchema manifest is missing 'projects': $(Format-LogSafeText $apiSchemaManifestPath)"
}
if ($apiSchemaManifest["projects"] -isnot [System.Collections.IList]) {
    throw "ApiSchema manifest projects must be a JSON array: $(Format-LogSafeText $apiSchemaManifestPath)"
}
$projects = @($projectsValue)
$extensionProjects = @(
    $projects |
        Where-Object {
            if ($_ -isnot [System.Collections.IDictionary]) {
                throw "Bootstrap ApiSchema manifest project entry is not a JSON object."
            }
            Read-RequiredJsonBoolean `
                -Hashtable $_ `
                -Key "isExtensionProject" `
                -ArtifactContext "Manifest project entry"
        }
)

$repoRoot = Get-BootstrapRepoRoot
$shippedClaimsDirectory = Join-Path $repoRoot "src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets"
$targetSources = [System.Collections.Generic.Dictionary[string, string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)
$fragments = [System.Collections.ArrayList]::new()
$namespacePrefixes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$unmappedExtensionNames = [System.Collections.ArrayList]::new()
# Readiness checks accumulate here across the baseline probe, known-extension entries, and fragment
# extraction; Add-ExpectedVerificationCheck dedups on claimSet|resourceClaim|action.
$expectedVerificationChecks = [System.Collections.ArrayList]::new()
$seenChecks = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

# Keep the core baseline probe even in Embedded mode; startup readiness owns verifying
# that CMS applied the embedded base claims before checking staged extension entries.
# The probe targets a LEAF resource claim: CMS /authorizationMetadata flattens the claims
# hierarchy to leaf resource claims only (verified live — domain parents such as
# domains/edFiTypes are never serialized in claims[].name). schoolYearType is the
# edFiTypes domain's child in the embedded Claims.json and carries Read for EdFiSandbox.
Add-ExpectedVerificationCheck `
    -Seen $seenChecks `
    -Checks $expectedVerificationChecks `
    -ClaimSetName "EdFiSandbox" `
    -ResourceClaim "http://ed-fi.org/identity/claims/ed-fi/schoolYearType" `
    -Action "Read"

foreach ($extensionProject in $extensionProjects) {
    $projectName = Get-ValueOrNull -Hashtable $extensionProject -Key "projectName"
    if ([string]::IsNullOrWhiteSpace($projectName)) {
        throw "Bootstrap ApiSchema manifest project entry is missing 'projectName'."
    }

    $knownExtension = Get-StandardKnownExtensionInfo -ProjectName $projectName
    if ($null -eq $knownExtension) {
        $null = $unmappedExtensionNames.Add($projectName)
        continue
    }

    # A known-extension entry must contribute at least one piece of security metadata. An entry that
    # contributes nothing - no fragment, no namespace prefix, and no (or an empty) VerificationChecks
    # list - would otherwise be silently treated as fully mapped: it would stage nothing, suppress the
    # unmapped-extension guard, and yield runtime 403s the claims-ready gate cannot detect. Checking
    # for non-empty *values* (not just key presence) also catches a misspelled key and an empty
    # VerificationChecks = @() no-op.
    $hasFragment = $knownExtension.ContainsKey("FragmentFileName") -and
        -not [string]::IsNullOrWhiteSpace([string]$knownExtension["FragmentFileName"])
    $hasNamespacePrefix = $knownExtension.ContainsKey("NamespacePrefix") -and
        -not [string]::IsNullOrWhiteSpace([string]$knownExtension["NamespacePrefix"])
    $hasVerificationChecks = $knownExtension.ContainsKey("VerificationChecks") -and
        @($knownExtension["VerificationChecks"]).Count -gt 0
    if (-not ($hasFragment -or $hasNamespacePrefix -or $hasVerificationChecks)) {
        throw "Known extension '$(Format-LogSafeText $projectName)' has a claims metadata entry that contributes no security metadata (expected a non-empty FragmentFileName, NamespacePrefix, or VerificationChecks). Check KnownExtensionClaimsMetadata for a misspelled or empty entry."
    }

    if ($knownExtension.ContainsKey("FragmentFileName")) {
        $fragmentPath = Join-Path $shippedClaimsDirectory $knownExtension["FragmentFileName"]
        if (-not (Test-Path -LiteralPath $fragmentPath -PathType Leaf)) {
            throw "Shipped claimset fragment was not found for extension '$(Format-LogSafeText $projectName)': $(Format-LogSafeText $fragmentPath)"
        }

        Add-FragmentInput -TargetSources $targetSources -Fragments $fragments -SourcePath $fragmentPath
    }

    if ($knownExtension.ContainsKey("NamespacePrefix")) {
        $null = $namespacePrefixes.Add($knownExtension["NamespacePrefix"])
    }

    # Extensions whose claims are already covered by the embedded Claims.json (e.g. TPDM in DS 5.2)
    # stage no fragment; their catalog-declared VerificationChecks are added to the readiness checks
    # directly so the claims-ready gate still confirms CMS composed those extension claims from the
    # embedded base. Each targets a leaf resource claim, so it is asserted directly against
    # /authorizationMetadata rather than deferred like parent-derived checks. -ThrowOnInvalid makes a
    # malformed static entry fail fast here (the sink owns the required-fields predicate, single source).
    if ($knownExtension.ContainsKey("VerificationChecks")) {
        foreach ($verificationCheck in @($knownExtension["VerificationChecks"])) {
            Add-ExpectedVerificationCheck `
                -Seen $seenChecks `
                -Checks $expectedVerificationChecks `
                -ClaimSetName ([string]$verificationCheck["ClaimSetName"]) `
                -ResourceClaim ([string]$verificationCheck["ResourceClaim"]) `
                -Action ([string]$verificationCheck["Action"]) `
                -ThrowOnInvalid `
                -Source "known extension '$projectName'"
        }
    }
}

$userFragmentFiles = @(Get-UserFragmentFile -Path $ClaimsDirectoryPath)
if ($unmappedExtensionNames.Count -gt 0 -and $userFragmentFiles.Count -eq 0) {
    throw "ClaimsDirectoryPath is required for unmapped extension project(s): $(Format-LogSafeText ($unmappedExtensionNames -join ', '))."
}

foreach ($userFragmentFile in $userFragmentFiles) {
    Add-FragmentInput -TargetSources $targetSources -Fragments $fragments -SourcePath $userFragmentFile
}

$effectiveClaimSetNames = Get-EffectiveClaimSetName

foreach ($fragment in $fragments) {
    Assert-FragmentValidAndExtractCheck `
        -Path $fragment.SourcePath `
        -EffectiveClaimSetNames $effectiveClaimSetNames `
        -SeenChecks $seenChecks `
        -ExpectedVerificationChecks $expectedVerificationChecks
}

$temporaryRoot = Join-Path (Join-Path $bootstrapRoot ".tmp") "claims-$([Guid]::NewGuid().ToString('N'))"
$finalWorkspace = Join-Path $bootstrapRoot "claims"
$temporaryMoved = $false

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null

    foreach ($fragment in $fragments) {
        Copy-Item -LiteralPath $fragment.SourcePath -Destination (Join-Path $temporaryRoot $fragment.FileName) -ErrorAction Stop
    }

    $fingerprint = Get-BootstrapWorkspaceFingerprint -Path $temporaryRoot
    $claimsMode = if ($fragments.Count -eq 0) { "Embedded" } else { "Hybrid" }
    $claimsSection = [ordered]@{
        mode = $claimsMode
        directory = "claims"
        fingerprint = $fingerprint
        expectedVerificationChecks = @($expectedVerificationChecks)
    }
    $seedSection = [ordered]@{
        extensionNamespacePrefixes = @($namespacePrefixes | Sort-Object)
    }

    if (Test-Path -LiteralPath $finalWorkspace) {
        if (-not $rootManifest.ContainsKey("claims")) {
            throw (Get-BootstrapWorkspaceMismatchMessage -Reason "manifest claims section missing")
        }

        $existingClaimsSection = $rootManifest["claims"]
        if ($existingClaimsSection -isnot [System.Collections.IDictionary]) {
            throw (Get-BootstrapWorkspaceMismatchMessage -Reason "manifest claims section malformed")
        }
        if ($existingClaimsSection["fingerprint"] -ne $fingerprint) {
            throw (Get-BootstrapWorkspaceMismatchMessage -Reason "claims fingerprint mismatch")
        }

        $existingFingerprint = Get-BootstrapWorkspaceFingerprint -Path $finalWorkspace
        if ($existingFingerprint -ne $fingerprint) {
            throw (Get-BootstrapWorkspaceMismatchMessage -Reason "staged claims content drift")
        }

        if (-not $rootManifest.ContainsKey("seed")) {
            throw (Get-BootstrapWorkspaceMismatchMessage -Reason "manifest seed section missing")
        }

        $existingSeedSection = $rootManifest["seed"]
        if ($existingSeedSection -isnot [System.Collections.IDictionary]) {
            throw (Get-BootstrapWorkspaceMismatchMessage -Reason "manifest seed section malformed")
        }

        $existingPrefixes = @(@($existingSeedSection["extensionNamespacePrefixes"]) | ForEach-Object { [string]$_ } | Sort-Object)
        $intendedPrefixes = @($seedSection["extensionNamespacePrefixes"] | ForEach-Object { [string]$_ } | Sort-Object)
        if ((($existingPrefixes -join "`n") -ne ($intendedPrefixes -join "`n"))) {
            throw (Get-BootstrapWorkspaceMismatchMessage -Reason "seed extensionNamespacePrefixes mismatch")
        }
    } else {
        if ($rootManifest.ContainsKey("claims") -or $rootManifest.ContainsKey("seed")) {
            throw (Get-BootstrapWorkspaceMismatchMessage -Reason "manifest has stale claims/seed sections but claims workspace is missing")
        }

        New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
        Move-Item -LiteralPath $temporaryRoot -Destination $finalWorkspace -ErrorAction Stop
        $temporaryMoved = $true
    }

    Set-BootstrapManifestSection -Name "claims" -Value $claimsSection
    Set-BootstrapManifestSection -Name "seed" -Value $seedSection

    Write-Output "Prepared claims workspace at $(Format-LogSafeText $finalWorkspace)"
    Write-Output "Claims mode: $claimsMode"
} finally {
    if (-not $temporaryMoved -and (Test-Path -LiteralPath $temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
