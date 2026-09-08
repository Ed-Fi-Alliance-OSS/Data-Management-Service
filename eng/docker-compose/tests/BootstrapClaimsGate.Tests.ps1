# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Pester callback scriptblocks keep delegate-compatible signatures.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Pester stubs intentionally shadow production plural-noun helpers.')]
param()

Describe "DMS-1153 Claims-ready gate (bootstrap-claims-gate.psm1)" {
    BeforeAll {
        $script:sourceDockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:moduleUnderTest = Join-Path $script:sourceDockerComposeRoot "bootstrap-claims-gate.psm1"

        # ---------------------------------------------------------------------------
        # Manifest factory helpers
        # ---------------------------------------------------------------------------

        function script:New-TempManifestDir {
            $path = Join-Path ([System.IO.Path]::GetTempPath()) "dms-claimsgate-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            return $path
        }

        function script:New-ManifestFile {
            param(
                [Parameter(Mandatory)]
                [string]
                $Dir,

                # List of hashtables with keys: claimSetName, resourceClaim, action
                [Parameter(Mandatory)]
                [array]
                $Checks,

                [string]
                $FileName = "bootstrap-manifest.json"
            )

            $manifest = [ordered]@{
                version = 1
                schema  = [ordered]@{
                    selectionMode       = "Standard"
                    selectedExtensions  = @()
                    effectiveSchemaHash = "abc123"
                }
                claims  = [ordered]@{
                    mode                      = "Hybrid"
                    directory                 = "claims"
                    fingerprint               = "def456"
                    expectedVerificationChecks = @($Checks)
                }
                seed    = [ordered]@{
                    extensionNamespacePrefixes = @()
                }
            }

            $manifestPath = Join-Path $Dir $FileName
            $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8
            return $manifestPath
        }

        # ---------------------------------------------------------------------------
        # /authorizationMetadata response factory
        # ---------------------------------------------------------------------------

        function script:New-AuthMetadataResponse {
            <#
            .SYNOPSIS
            Builds a synthetic /authorizationMetadata response matching the REAL CMS contract:
            AuthorizationMetadataModule returns AuthorizationMetadataResponse.ClaimSets, which
            serializes (default camelCase) as an array of
              { claimSetName, claims: [{ name, authorizationId }],
                authorizations: [{ id, actions: [{ name, authorizationStrategies }] }] }
            per AuthorizationMetadataResponse.cs. Claims join to their actions via
            claims[].authorizationId -> authorizations[].id. The object graph is JSON
            round-tripped so fixtures are PSCustomObjects exactly as Invoke-RestMethod
            would deserialize the live response.
            #>
            param(
                [Parameter(Mandatory)]
                [string]
                $ClaimSetName,

                # Array of hashtables: @{ name = "<resource claim URI>"; actions = @("Read","Create") }
                [Parameter(Mandatory)]
                [AllowEmptyCollection()]
                [array]
                $Claims
            )

            $claimObjects = [System.Collections.ArrayList]::new()
            $authorizationObjects = [System.Collections.ArrayList]::new()
            $nextAuthorizationId = 1

            foreach ($c in $Claims) {
                $authorizationId = $nextAuthorizationId++
                $null = $claimObjects.Add([ordered]@{
                    name            = $c.name
                    authorizationId = $authorizationId
                })

                $actionObjects = foreach ($a in $c.actions) {
                    [ordered]@{
                        name                    = $a
                        authorizationStrategies = @(@{ name = "NoFurtherAuthorizationRequired" })
                    }
                }
                $null = $authorizationObjects.Add([ordered]@{
                    id      = $authorizationId
                    actions = @($actionObjects)
                })
            }

            $response = @(
                [ordered]@{
                    claimSetName   = $ClaimSetName
                    claims         = @($claimObjects)
                    authorizations = @($authorizationObjects)
                }
            )

            # Round-trip through JSON so the fixture arrives as PSCustomObjects, exactly as
            # Invoke-RestMethod deserializes the live CMS response.
            return @(ConvertTo-Json -InputObject $response -Depth 10 | ConvertFrom-Json)
        }

        # ---------------------------------------------------------------------------
        # Standard fixture values (matching the EdFiSandbox baseline probe)
        # ---------------------------------------------------------------------------

        $script:sandboxClaimSet    = "EdFiSandbox"
        $script:sandboxResourceClaim = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"
        $script:sandboxAction      = "Read"
        $script:cmsBaseUrl         = "http://localhost:8081"
        $script:accessToken        = "unit-test-fake-token"
    }

    # ===========================================================================
    # Scenario (a): gate passes when /authorizationMetadata contains every
    # expected claim set + resource claim URI + action
    # ===========================================================================
    Context "Scenario (a) - gate passes with full matching metadata" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_a = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction },
                    @{ claimSetName = "EdFiAdmin"; resourceClaim = "http://ed-fi.org/identity/claims/domains/identity"; action = "Create" }
                )

            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    $claimSet = if ($Uri -match "claimSetName=EdFiSandbox") {
                        New-AuthMetadataResponse `
                            -ClaimSetName "EdFiSandbox" `
                            -Claims @(
                                @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Read") }
                            )
                    }
                    else {
                        New-AuthMetadataResponse `
                            -ClaimSetName "EdFiAdmin" `
                            -Claims @(
                                @{ name = "http://ed-fi.org/identity/claims/domains/identity"; actions = @("Create", "Read") }
                            )
                    }
                    return $claimSet
                }
            }

            $script:errorThrown_a = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_a
            }
            catch {
                $script:errorThrown_a = $_
            }
        }

        It "does not throw when all checks pass" {
            $script:errorThrown_a | Should -BeNullOrEmpty
        }

        It "calls /authorizationMetadata for each check" {
            Should -Invoke Invoke-RestMethod -ModuleName bootstrap-claims-gate -Times 2 -Scope Context -ParameterFilter {
                $Uri -match "/v3/authorizationMetadata"
            }
        }
    }

    # ===========================================================================
    # Scenario (b): fails when resource claim URI or action is absent even
    # though the claim set exists
    # ===========================================================================
    Context "Scenario (b) - fails when resource claim URI is absent" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_b1 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction }
                ) `
                -FileName "manifest-b1.json"

            # Response has the right claimSetName but the resource claim URI is WRONG
            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/domains/WRONG_CLAIM"; actions = @("Read") }
                        )
                }
            }

            $script:error_b1 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_b1
            }
            catch {
                $script:error_b1 = $_.Exception.Message
            }
        }

        It "throws when the expected resource claim URI is absent" {
            $script:error_b1 | Should -Not -BeNullOrEmpty
        }

        It "error message mentions the missing resource claim" {
            $script:error_b1 | Should -Match "resource claim"
        }
    }

    Context "Scenario (b) - fails when action is absent even though claim set and resource URI exist" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_b2 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction }
                ) `
                -FileName "manifest-b2.json"

            # Response has correct claimSetName and resourceClaim but WRONG action
            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Create") }
                        )
                }
            }

            $script:error_b2 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_b2
            }
            catch {
                $script:error_b2 = $_.Exception.Message
            }
        }

        It "throws when the expected action is absent" {
            $script:error_b2 | Should -Not -BeNullOrEmpty
        }

        It "error message mentions the missing action" {
            $script:error_b2 | Should -Match "action"
        }
    }

    # ===========================================================================
    # Scenario (c): fails when only EdFiSandbox is present but the check
    # references a different claim set
    # ===========================================================================
    Context "Scenario (c) - fails when only EdFiSandbox is present but check expects another claim set" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_c = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = "EdFiAdmin"; resourceClaim = "http://ed-fi.org/identity/claims/domains/identity"; action = "Create" }
                ) `
                -FileName "manifest-c.json"

            # /authorizationMetadata 404s for EdFiAdmin (claim set does not exist)
            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    # Simulate 404 by throwing an exception with a fake Response property
                    $webException = [System.Net.WebException]::new("The remote server returned an error: (404) Not Found.")
                    throw $webException
                }
            }

            $script:error_c = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_c
            }
            catch {
                $script:error_c = $_.Exception.Message
            }
        }

        It "throws when only EdFiSandbox is present (not the expected claim set)" {
            $script:error_c | Should -Not -BeNullOrEmpty
        }

        It "error message indicates the expected claim set was not found" {
            $script:error_c | Should -Match "(EdFiAdmin|claim set|not found|FAILED)"
        }
    }

    # ===========================================================================
    # Scenario (d): fails when manifest is present but expectedVerificationChecks
    # is missing or empty
    # ===========================================================================
    Context "Scenario (d) - fails when expectedVerificationChecks is missing from manifest" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $manifestWithoutChecks = [ordered]@{
                version = 1
                schema  = [ordered]@{ selectionMode = "Standard" }
                claims  = [ordered]@{
                    mode        = "Embedded"
                    directory   = "claims"
                    fingerprint = "abc"
                    # NOTE: expectedVerificationChecks intentionally omitted
                }
            }
            $script:manifestPath_d1 = Join-Path $tempDir "manifest-d1.json"
            $manifestWithoutChecks | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $script:manifestPath_d1 -Encoding utf8

            $script:error_d1 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_d1
            }
            catch {
                $script:error_d1 = $_.Exception.Message
            }
        }

        It "throws when expectedVerificationChecks is missing" {
            $script:error_d1 | Should -Not -BeNullOrEmpty
        }

        It "error message references expectedVerificationChecks" {
            $script:error_d1 | Should -Match "expectedVerificationChecks"
        }
    }

    Context "Scenario (d) - fails when expectedVerificationChecks is empty" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $manifestEmptyChecks = [ordered]@{
                version = 1
                schema  = [ordered]@{ selectionMode = "Standard" }
                claims  = [ordered]@{
                    mode                      = "Embedded"
                    directory                 = "claims"
                    fingerprint               = "abc"
                    expectedVerificationChecks = @()
                }
            }
            $script:manifestPath_d2 = Join-Path $tempDir "manifest-d2.json"
            $manifestEmptyChecks | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $script:manifestPath_d2 -Encoding utf8

            $script:error_d2 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_d2
            }
            catch {
                $script:error_d2 = $_.Exception.Message
            }
        }

        It "throws when expectedVerificationChecks is empty" {
            $script:error_d2 | Should -Not -BeNullOrEmpty
        }

        It "error message references expectedVerificationChecks being empty" {
            $script:error_d2 | Should -Match "empty"
        }
    }

    # ===========================================================================
    # Scenario (e): /v2/claimSets passing but /authorizationMetadata failing must fail
    # The module does NOT call /v2/claimSets; all verification is against
    # /authorizationMetadata. A claimSets presence alone cannot rescue a failing gate.
    # ===========================================================================
    Context "Scenario (e) - /v2/claimSets presence does not rescue failing /authorizationMetadata" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_e = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction }
                ) `
                -FileName "manifest-e.json"

            $script:claimSetsCalled_e = $false

            # /v2/claimSets returns the claim set (passing) but /authorizationMetadata returns
            # an empty claims array (failing) - the gate must still fail.
            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v2/claimSets") {
                    $script:claimSetsCalled_e = $true
                    return @(
                        [pscustomobject]@{ claimSetName = "EdFiSandbox"; id = 1 }
                    )
                }
                if ($Uri -match "/v3/authorizationMetadata") {
                    # Claim set exists but has NO claims - must fail the gate
                    return New-AuthMetadataResponse -ClaimSetName "EdFiSandbox" -Claims @()
                }
            }

            $script:error_e = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_e
            }
            catch {
                $script:error_e = $_.Exception.Message
            }
        }

        It "throws when /authorizationMetadata has no actions even if claimSets might show the set" {
            $script:error_e | Should -Not -BeNullOrEmpty
        }

        It "does not call /v2/claimSets (module does not use that endpoint)" {
            $script:claimSetsCalled_e | Should -Be $false
        }

        It "error message references the missing resource claim or no actions" {
            $script:error_e | Should -Match "(resource claim|no actions|FAILED)"
        }
    }

    # ===========================================================================
    # Scenario (f): token fallback path - dedicated client unavailable -> bootstrap
    # admin token - still verifies claims correctly
    # ===========================================================================
    Context "Scenario (f) - token fallback: dedicated client fails, admin client succeeds" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_f = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction }
                ) `
                -FileName "manifest-f.json"

            $script:tokenCallCount_f = 0
            $script:dedicatedClientAttempted_f = $false
            $script:adminClientAttempted_f = $false

            # First token call (dedicated CMSAuthMetadataReadOnlyAccess) must fail.
            # Second token call (bootstrap admin dms-data-store-admin) must succeed.
            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)

                if ($Method -eq "Post" -and $Uri -match "/connect/token") {
                    $script:tokenCallCount_f++

                    # Detect which client is being used from the body
                    if ($Body -match "CMSAuthMetadataReadOnlyAccess") {
                        $script:dedicatedClientAttempted_f = $true
                        throw [System.Net.WebException]::new("Simulated token failure for dedicated client.")
                    }

                    if ($Body -match "dms-data-store-admin") {
                        $script:adminClientAttempted_f = $true
                        return [pscustomobject]@{ access_token = "admin-fallback-token" }
                    }

                    # Unknown client - also fail
                    throw [System.Net.WebException]::new("Unknown client in token request.")
                }

                if ($Uri -match "/v3/authorizationMetadata") {
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Read") }
                        )
                }
            }

            $script:error_f = $null
            try {
                # Do NOT supply -AccessToken so the module exercises its token acquisition path
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -IdentityProvider "self-contained" `
                    -ManifestPath $script:manifestPath_f
            }
            catch {
                $script:error_f = $_.Exception.Message
            }
        }

        It "does not throw - gate passes after admin token fallback" {
            $script:error_f | Should -BeNullOrEmpty
        }

        It "attempted the dedicated CMSAuthMetadataReadOnlyAccess client first" {
            $script:dedicatedClientAttempted_f | Should -Be $true
        }

        It "fell back to the bootstrap admin client (dms-data-store-admin)" {
            $script:adminClientAttempted_f | Should -Be $true
        }

        It "made exactly two token requests (one failed, one succeeded)" {
            $script:tokenCallCount_f | Should -Be 2
        }

        It "verified claims using the admin token" {
            Should -Invoke Invoke-RestMethod -ModuleName bootstrap-claims-gate -Scope Context -ParameterFilter {
                $Uri -match "/v3/authorizationMetadata"
            }
        }
    }

    # ===========================================================================
    # Scenario (g): response-shape contract regression - the gate must navigate
    # the REAL CMS serialization (claims[].name + authorizationId joined to
    # authorizations[].id -> actions[].name per AuthorizationMetadataResponse.cs),
    # and must NOT pass against the pre-fix fantasy shape (claims[].claimName with
    # inline claims[].actions). Mocked suites cannot catch a shape drift unless a
    # fixture pins the serialized contract literally, so this context does both.
    # ===========================================================================
    Context "Scenario (g) - gate passes against the literal CMS /authorizationMetadata serialization" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_g1 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction }
                ) `
                -FileName "manifest-g1.json"

            # Literal JSON mirroring the CMS contract: AuthorizationMetadataModule returns
            # AuthorizationMetadataResponse.ClaimSets with default camelCase serialization
            # (see AuthorizationMetadataResponse.cs and the CMS AuthorizationMetadata.feature).
            $script:literalCmsJson_g1 = @'
[
  {
    "claimSetName": "EdFiSandbox",
    "claims": [
      { "name": "http://ed-fi.org/identity/claims/ed-fi/schoolYearType", "authorizationId": 1 }
    ],
    "authorizations": [
      {
        "id": 1,
        "actions": [
          { "name": "Read", "authorizationStrategies": [ { "name": "NoFurtherAuthorizationRequired" } ] }
        ]
      }
    ]
  }
]
'@

            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    return @($script:literalCmsJson_g1 | ConvertFrom-Json)
                }
            }

            $script:error_g1 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_g1
            }
            catch {
                $script:error_g1 = $_.Exception.Message
            }
        }

        It "does not throw against the documented CMS response serialization" {
            $script:error_g1 | Should -BeNullOrEmpty
        }
    }

    Context "Scenario (g) - gate FAILS against the pre-fix fantasy shape (claimName + inline actions)" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_g2 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction }
                ) `
                -FileName "manifest-g2.json"

            # The shape the gate wrongly expected before the fix: claims carry 'claimName'
            # and an inline 'actions' array, with no authorizations join. CMS never returns
            # this; a gate that accepts it has regressed to navigating a non-existent contract.
            $script:fantasyJson_g2 = @'
[
  {
    "claimSetName": "EdFiSandbox",
    "claims": [
      {
        "claimName": "http://ed-fi.org/identity/claims/ed-fi/schoolYearType",
        "actions": [ { "name": "Read" } ]
      }
    ]
  }
]
'@

            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    return @($script:fantasyJson_g2 | ConvertFrom-Json)
                }
            }

            $script:error_g2 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_g2
            }
            catch {
                $script:error_g2 = $_.Exception.Message
            }
        }

        It "throws - the fantasy shape has no claims[].name so the resource claim cannot match" {
            $script:error_g2 | Should -Not -BeNullOrEmpty
        }

        It "error message reports the unmatched resource claim" {
            $script:error_g2 | Should -Match "resource claim"
        }
    }

    # ===========================================================================
    # Scenario (h): parent-claim deferral - checks marked isParent target parent
    # fragment resource claims whose grants materialize on leaf descendants via
    # hierarchy lineage. /authorizationMetadata serializes leaf claims only, so
    # the gate defers parent checks (warning) instead of false-failing, but must
    # still fail when NOTHING is verifiable (all checks deferred).
    # ===========================================================================
    Context "Scenario (h) - parent-claim checks are deferred while leaf checks still verify" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_h1 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction },
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = "http://ed-fi.org/identity/claims/domains/sample"; action = "Read"; isParent = $true }
                ) `
                -FileName "manifest-h1.json"

            $script:authMetadataCalls_h1 = 0

            # Response carries ONLY the leaf claim - the parent name is never serialized,
            # mirroring the live CMS contract.
            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    $script:authMetadataCalls_h1++
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Read") }
                        )
                }
            }

            $script:error_h1 = $null
            $script:warnings_h1 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_h1 `
                    -WarningVariable +warnings_h1 3>$null
                $script:warnings_h1 = $warnings_h1
            }
            catch {
                $script:error_h1 = $_.Exception.Message
            }
        }

        It "does not throw - the leaf check verifies and the parent check is deferred" {
            $script:error_h1 | Should -BeNullOrEmpty
        }

        It "queries /authorizationMetadata only for the leaf check (parent check is skipped)" {
            $script:authMetadataCalls_h1 | Should -Be 1
        }

        It "emits a deferral warning naming the parent resource claim" {
            @($script:warnings_h1) -join "`n" | Should -Match "domains/sample"
        }
    }

    Context "Scenario (h) - gate fails when ALL checks are parent-claim deferrals" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_h2 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = "http://ed-fi.org/identity/claims/domains/sample"; action = "Read"; isParent = $true }
                ) `
                -FileName "manifest-h2.json"

            $script:authMetadataCalled_h2 = $false
            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    $script:authMetadataCalled_h2 = $true
                }
            }

            $script:error_h2 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_h2 3>$null
            }
            catch {
                $script:error_h2 = $_.Exception.Message
            }
        }

        It "throws - nothing was verifiable against /authorizationMetadata" {
            $script:error_h2 | Should -Not -BeNullOrEmpty
        }

        It "error message reports the all-deferred condition" {
            $script:error_h2 | Should -Match "deferral"
        }

        It "never queried /authorizationMetadata" {
            $script:authMetadataCalled_h2 | Should -Be $false
        }
    }

    # ===========================================================================
    # Scenario (h3): user-staged leaf checks are verified normally after Story 04
    # activation. A baseline probe alone is not enough when the manifest records a
    # user-supplied leaf claim from -ClaimsDirectoryPath.
    # ===========================================================================
    Context "Scenario (h3) - user-staged leaf checks fail when authorization metadata omits them" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_h3_missing = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction },
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = "http://example.org/identity/claims/customWidget"; action = "Read" }
                ) `
                -FileName "manifest-h3.json"

            $script:authMetadataCalls_h3_missing = 0

            # Response carries ONLY the baseline leaf claim; the user-staged leaf check must
            # still be asserted and fail when CMS authorization metadata omits it.
            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    $script:authMetadataCalls_h3_missing++
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Read") }
                        )
                }
            }

            $script:error_h3_missing = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_h3_missing 3>$null
            }
            catch {
                $script:error_h3_missing = $_.Exception.Message
            }
        }

        It "throws because the user-staged leaf claim is missing" {
            $script:error_h3_missing | Should -Not -BeNullOrEmpty
        }

        It "fetches /authorizationMetadata once for both EdFiSandbox checks (memoized per claim set)" {
            $script:authMetadataCalls_h3_missing | Should -Be 1
        }

        It "error message reports the missing user-staged resource claim" {
            $script:error_h3_missing | Should -Match "customWidget"
        }
    }

    Context "Scenario (h3) - user-staged leaf checks pass when authorization metadata contains them" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_h3_present = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction },
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = "http://example.org/identity/claims/customWidget"; action = "Read" }
                ) `
                -FileName "manifest-h3-present.json"

            $script:authMetadataCalls_h3_present = 0

            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    $script:authMetadataCalls_h3_present++
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Read") },
                            @{ name = "http://example.org/identity/claims/customWidget"; actions = @("Read") }
                        )
                }
            }

            $script:error_h3_present = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_h3_present 3>$null
            }
            catch {
                $script:error_h3_present = $_.Exception.Message
            }
        }

        It "does not throw when the user-staged leaf claim is present" {
            $script:error_h3_present | Should -BeNullOrEmpty
        }

        It "fetches /authorizationMetadata once for both EdFiSandbox checks (memoized per claim set)" {
            $script:authMetadataCalls_h3_present | Should -Be 1
        }
    }

    # ===========================================================================
    # Scenario (i): multi-tenant CMS routing - the gate must forward the env-file
    # CONFIG_SERVICE_TENANT as the Tenant header on /authorizationMetadata calls
    # (the same pattern every CMS Admin API call in Dms-Management.psm1 uses),
    # and must NOT send the header when no tenant is configured.
    # ===========================================================================
    Context "Scenario (i) - Tenant header is sent when CONFIG_SERVICE_TENANT is configured" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_i1 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction }
                ) `
                -FileName "manifest-i1.json"

            # Env file with multi-tenant CMS settings; -AccessToken is intentionally omitted
            # below so Test-CmsClaimsReady resolves env values (and the tenant) from this file.
            $script:envFile_i1 = Join-Path $tempDir "tenant.env"
            @(
                "DMS_CONFIG_MULTI_TENANCY=true"
                "CONFIG_SERVICE_TENANT=tenant1"
            ) | Set-Content -LiteralPath $script:envFile_i1 -Encoding utf8

            $script:capturedHeaders_i1 = $null

            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)

                if ($Method -eq "Post" -and $Uri -match "/connect/token") {
                    return [pscustomobject]@{ access_token = "tenant-test-token" }
                }

                if ($Uri -match "/v3/authorizationMetadata") {
                    $script:capturedHeaders_i1 = $Headers
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Read") }
                        )
                }
            }

            $script:error_i1 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -EnvironmentFile $script:envFile_i1 `
                    -IdentityProvider "self-contained" `
                    -ManifestPath $script:manifestPath_i1
            }
            catch {
                $script:error_i1 = $_.Exception.Message
            }
        }

        It "does not throw" {
            $script:error_i1 | Should -BeNullOrEmpty
        }

        It "sends the Tenant header with the env-file CONFIG_SERVICE_TENANT value" {
            $script:capturedHeaders_i1 | Should -Not -BeNullOrEmpty
            $script:capturedHeaders_i1["Tenant"] | Should -Be "tenant1"
        }
    }

    Context "Scenario (i) - Tenant header is absent when no tenant is configured" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_i2 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction }
                ) `
                -FileName "manifest-i2.json"

            # Env file with no CONFIG_SERVICE_TENANT - single-tenant local default.
            $script:envFile_i2 = Join-Path $tempDir "no-tenant.env"
            "DMS_CONFIG_MULTI_TENANCY=false" | Set-Content -LiteralPath $script:envFile_i2 -Encoding utf8

            $script:capturedHeaders_i2 = $null

            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)

                if ($Method -eq "Post" -and $Uri -match "/connect/token") {
                    return [pscustomobject]@{ access_token = "tenant-test-token" }
                }

                if ($Uri -match "/v3/authorizationMetadata") {
                    $script:capturedHeaders_i2 = $Headers
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Read") }
                        )
                }
            }

            $script:error_i2 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -EnvironmentFile $script:envFile_i2 `
                    -IdentityProvider "self-contained" `
                    -ManifestPath $script:manifestPath_i2
            }
            catch {
                $script:error_i2 = $_.Exception.Message
            }
        }

        It "does not throw" {
            $script:error_i2 | Should -BeNullOrEmpty
        }

        It "does not send a Tenant header" {
            $script:capturedHeaders_i2 | Should -Not -BeNullOrEmpty
            $script:capturedHeaders_i2.ContainsKey("Tenant") | Should -Be $false
        }
    }

    # ===========================================================================
    # Scenario (j): TPDM embedded-claims load verification (DMS-1247). A core + TPDM
    # bootstrap stages Embedded mode with NO fragment, but the manifest still records
    # the two TPDM leaf checks so this gate proves CMS actually composed the TPDM
    # claims from the embedded DS 5.2 Claims.json into EdFiSandbox - i.e. it exercises
    # CMS load verification against /authorizationMetadata, not just the manifest.
    # ===========================================================================
    Context "Scenario (j) - gate passes when CMS composed the TPDM leaf claims into EdFiSandbox" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_j1 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction },
                    @{ claimSetName = "EdFiSandbox"; resourceClaim = "http://ed-fi.org/identity/claims/tpdm/credentialStatusDescriptor"; action = "Read" },
                    @{ claimSetName = "EdFiSandbox"; resourceClaim = "http://ed-fi.org/identity/claims/tpdm/evaluation"; action = "Read" }
                ) `
                -FileName "manifest-j1.json"

            $script:authMetadataCalls_j1 = 0

            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    $script:authMetadataCalls_j1++
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Read") },
                            @{ name = "http://ed-fi.org/identity/claims/tpdm/credentialStatusDescriptor"; actions = @("Read") },
                            @{ name = "http://ed-fi.org/identity/claims/tpdm/evaluation"; actions = @("Read") }
                        )
                }
            }

            $script:error_j1 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_j1
            }
            catch {
                $script:error_j1 = $_.Exception.Message
            }
        }

        It "does not throw when CMS serves both TPDM leaf claims for EdFiSandbox" {
            $script:error_j1 | Should -BeNullOrEmpty
        }

        It "fetches /authorizationMetadata once for the three EdFiSandbox checks (memoized per claim set)" {
            $script:authMetadataCalls_j1 | Should -Be 1
        }
    }

    Context "Scenario (j) - gate fails when CMS did not compose a TPDM leaf claim" {
        BeforeAll {
            Import-Module $script:moduleUnderTest -Force

            $tempDir = New-TempManifestDir
            $script:manifestPath_j2 = New-ManifestFile `
                -Dir $tempDir `
                -Checks @(
                    @{ claimSetName = $script:sandboxClaimSet; resourceClaim = $script:sandboxResourceClaim; action = $script:sandboxAction },
                    @{ claimSetName = "EdFiSandbox"; resourceClaim = "http://ed-fi.org/identity/claims/tpdm/evaluation"; action = "Read" }
                ) `
                -FileName "manifest-j2.json"

            # CMS serves EdFiSandbox with the baseline claim but WITHOUT the TPDM claim - e.g. a
            # stale Config image whose embedded DS 5.2 Claims.json lacks TPDM. The gate must catch
            # this rather than pass on /health or claim-set-name presence alone.
            Mock Invoke-RestMethod -ModuleName bootstrap-claims-gate {
                param($Uri, $Method, $ContentType, $Headers, $Body)
                if ($Uri -match "/v3/authorizationMetadata") {
                    return New-AuthMetadataResponse `
                        -ClaimSetName "EdFiSandbox" `
                        -Claims @(
                            @{ name = "http://ed-fi.org/identity/claims/ed-fi/schoolYearType"; actions = @("Read") }
                        )
                }
            }

            $script:error_j2 = $null
            try {
                Test-CmsClaimsReady `
                    -CmsBaseUrl $script:cmsBaseUrl `
                    -AccessToken $script:accessToken `
                    -ManifestPath $script:manifestPath_j2
            }
            catch {
                $script:error_j2 = $_.Exception.Message
            }
        }

        It "throws because the TPDM leaf claim is absent from CMS authorization metadata" {
            $script:error_j2 | Should -Not -BeNullOrEmpty
        }

        It "error message reports the missing TPDM resource claim" {
            $script:error_j2 | Should -Match "tpdm/evaluation"
        }
    }
}
