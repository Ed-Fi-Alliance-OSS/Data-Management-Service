# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $script:devTrustScript = Join-Path $script:templatesDir "new-template-dev-trust.ps1"
    $script:trackedPolicyPath = [System.IO.Path]::GetFullPath((Join-Path $script:templatesDir "../docker-compose/template-trust-policy.json"))
    Import-Module (Join-Path $script:templatesDir "Template-RestoreTrust.psm1") -Force

    function script:New-TestWorkspace {
        $path = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }
}

Describe "tracked template-trust-policy.json" {
    It "ships fail-closed: valid version-1 policy with zero producers" {
        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $script:trackedPolicyPath
        @($policy.Producers).Count | Should -Be 0
    }
}

Describe "new-template-dev-trust.ps1 -Purpose Dev" {
    It "generates a P-256 signer key and registers the producer in the local overlay, loadable alongside the tracked policy" {
        $workspace = New-TestWorkspace
        $keyDirectory = Join-Path $workspace ".dev-trust"
        $localPolicyPath = Join-Path $workspace "template-trust-policy.local.json"

        $result = & $script:devTrustScript -Purpose Dev -KeyDirectory $keyDirectory -LocalPolicyPath $localPolicyPath 6>$null

        $result.Purpose | Should -Be "Dev"
        $result.ProducerName | Should -Be "local-dev"
        $result.PrivateKeyPath | Should -Be (Join-Path $keyDirectory "local-dev.pem")
        $result.KeyId | Should -Match '^[0-9a-f]{64}$'
        $result.LocalPolicyPath | Should -Be $localPolicyPath

        # The private key is a valid P-256 signer.
        { Assert-TemplateAttestationSignerKey -PrivateKeyPath $result.PrivateKeyPath } | Should -Not -Throw

        # The overlay merges onto the real tracked policy through the production loader.
        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $script:trackedPolicyPath -LocalPolicyPath $localPolicyPath
        @($policy.Producers | ForEach-Object { $_.Name }) | Should -Contain "local-dev"
    }

    It "signs packages that verify end to end through the dev trust chain" {
        $workspace = New-TestWorkspace
        $result = & $script:devTrustScript -Purpose Dev `
            -KeyDirectory (Join-Path $workspace ".dev-trust") `
            -LocalPolicyPath (Join-Path $workspace "template-trust-policy.local.json") 6>$null

        $packagePath = Join-Path $workspace "edfi.api.minimal.template.postgresql.5.2.0.1.0.123.nupkg"
        Set-Content -LiteralPath $packagePath -Value "fake package bytes"
        $packageSha256 = Get-FileSha256Hex -Path $packagePath

        $attestationJson = New-TemplateAttestation `
            -PackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" `
            -PackageVersion "1.0.123" `
            -PackageSha256 $packageSha256 `
            -Producer $result.ProducerName `
            -PrivateKeyPath $result.PrivateKeyPath

        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $script:trackedPolicyPath -LocalPolicyPath $result.LocalPolicyPath
        $verdict = Test-TemplateAttestation `
            -AttestationJson $attestationJson `
            -PackageSha256 $packageSha256 `
            -ExpectedPackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" `
            -ExpectedPackageVersion "1.0.123" `
            -TrustPolicy $policy

        $verdict.IsTrusted | Should -BeTrue
        $verdict.Producer | Should -Be "local-dev"
    }

    It "refuses to overwrite an existing producer (manual rotation) before generating any key" {
        $workspace = New-TestWorkspace
        $keyDirectory = Join-Path $workspace ".dev-trust"
        $localPolicyPath = Join-Path $workspace "template-trust-policy.local.json"

        & $script:devTrustScript -Purpose Dev -KeyDirectory $keyDirectory -LocalPolicyPath $localPolicyPath 6>$null | Out-Null
        Remove-Item -LiteralPath (Join-Path $keyDirectory "local-dev.pem")

        { & $script:devTrustScript -Purpose Dev -KeyDirectory $keyDirectory -LocalPolicyPath $localPolicyPath 6>$null } |
            Should -Throw "*already exists*rotation is manual*"

        # The duplicate-producer refusal never regenerates the key file.
        Test-Path -LiteralPath (Join-Path $keyDirectory "local-dev.pem") | Should -BeFalse
    }

    It "appends additional producers while preserving existing overlay entries" {
        $workspace = New-TestWorkspace
        $keyDirectory = Join-Path $workspace ".dev-trust"
        $localPolicyPath = Join-Path $workspace "template-trust-policy.local.json"

        & $script:devTrustScript -Purpose Dev -KeyDirectory $keyDirectory -LocalPolicyPath $localPolicyPath 6>$null | Out-Null
        & $script:devTrustScript -Purpose Dev -ProducerName "second-dev" -KeyDirectory $keyDirectory -LocalPolicyPath $localPolicyPath 6>$null | Out-Null

        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $script:trackedPolicyPath -LocalPolicyPath $localPolicyPath
        @($policy.Producers | ForEach-Object { $_.Name }) | Should -Be @("local-dev", "second-dev")
    }

    It "rejects a producer name that already exists in the tracked policy, before generating any key" {
        $workspace = New-TestWorkspace
        $keyDirectory = Join-Path $workspace ".dev-trust"

        # A tracked policy already carrying the default producer name (as Step 2.4 will,
        # once edfi-alliance-ci lands): the dev script must refuse rather than write an
        # overlay the production loader would reject as a duplicate.
        $trackedKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "tracked.pem")
        $trackedPolicyPath = Join-Path $workspace "tracked-policy.json"
        [ordered]@{
            version   = 1
            producers = @(
                [ordered]@{
                    name       = "local-dev"
                    provider   = "detached-attestation"
                    publicKeys = @(
                        [ordered]@{
                            keyId            = $trackedKey.KeyId
                            algorithm        = $trackedKey.Algorithm
                            publicKeySpkiB64 = $trackedKey.PublicKeySpkiB64
                        }
                    )
                }
            )
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $trackedPolicyPath -Encoding utf8

        { & $script:devTrustScript -Purpose Dev `
                -KeyDirectory $keyDirectory `
                -LocalPolicyPath (Join-Path $workspace "overlay.json") `
                -TrackedPolicyPath $trackedPolicyPath 6>$null } |
            Should -Throw "*already exists in the tracked policy*"

        Test-Path -LiteralPath (Join-Path $keyDirectory "local-dev.pem") | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $workspace "overlay.json") | Should -BeFalse
    }

    It "rejects an unsafe producer name and a malformed existing overlay" {
        $workspace = New-TestWorkspace

        { & $script:devTrustScript -Purpose Dev -ProducerName "bad name" -KeyDirectory (Join-Path $workspace "k") -LocalPolicyPath (Join-Path $workspace "o.json") 6>$null } |
            Should -Throw "*Producer name*"

        $malformedPolicyPath = Join-Path $workspace "malformed.json"
        Set-Content -LiteralPath $malformedPolicyPath -Value "{ not json"
        { & $script:devTrustScript -Purpose Dev -KeyDirectory (Join-Path $workspace "k") -LocalPolicyPath $malformedPolicyPath 6>$null } |
            Should -Throw "*not valid JSON*"
    }
}

Describe "new-template-dev-trust.ps1 -Purpose CI" {
    It "requires an explicit output directory" {
        { & $script:devTrustScript -Purpose CI 6>$null } | Should -Throw "*-OutputDirectory is required*"
    }

    It "generates the CI keypair without touching any policy file and prints a loadable tracked-policy producer block" {
        $workspace = New-TestWorkspace
        $outputDirectory = Join-Path $workspace "ci-out"
        $localPolicyPath = Join-Path $workspace "template-trust-policy.local.json"

        $result = & $script:devTrustScript -Purpose CI -OutputDirectory $outputDirectory -LocalPolicyPath $localPolicyPath 6>$null

        $result.Purpose | Should -Be "CI"
        $result.ProducerName | Should -Be "edfi-alliance-ci"
        $result.PrivateKeyPath | Should -Be (Join-Path $outputDirectory "edfi-alliance-ci.pem")
        { Assert-TemplateAttestationSignerKey -PrivateKeyPath $result.PrivateKeyPath } | Should -Not -Throw

        # CI purpose never writes policy files; installation is a reviewed manual step.
        Test-Path -LiteralPath $localPolicyPath | Should -BeFalse

        # The printed producer block is directly loadable once placed in a policy file.
        $candidatePolicyPath = Join-Path $workspace "candidate-tracked.json"
        "{`"version`": 1, `"producers`": [$($result.TrackedPolicyProducerJson)]}" |
            Set-Content -LiteralPath $candidatePolicyPath -Encoding utf8
        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $candidatePolicyPath
        $policy.Producers[0].Name | Should -Be "edfi-alliance-ci"
        $policy.Producers[0].PublicKeys[0].KeyId | Should -Be $result.KeyId
    }
}
