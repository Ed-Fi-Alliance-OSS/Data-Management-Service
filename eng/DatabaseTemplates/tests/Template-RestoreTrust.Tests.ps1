# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:templatesDir "Template-RestoreTrust.psm1") -Force

    function script:New-TestWorkspace {
        $path = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }

    function script:Write-TrustPolicyFile {
        param (
            [Parameter(Mandatory = $true)]
            [string]$Path,

            [Parameter(Mandatory = $true)]
            [AllowEmptyCollection()]
            [object[]]$Producer,

            [int]$Version = 1
        )

        [ordered]@{ version = $Version; producers = $Producer } |
            ConvertTo-Json -Depth 6 |
            Set-Content -LiteralPath $Path -Encoding utf8
        return $Path
    }

    function script:New-DetachedProducerEntry {
        param (
            [Parameter(Mandatory = $true)]
            [string]$Name,

            [Parameter(Mandatory = $true)]
            $SigningKey
        )

        return [ordered]@{
            name       = $Name
            provider   = "detached-attestation"
            publicKeys = @(
                [ordered]@{
                    keyId            = $SigningKey.KeyId
                    algorithm        = $SigningKey.Algorithm
                    publicKeySpkiB64 = $SigningKey.PublicKeySpkiB64
                }
            )
        }
    }

    function script:New-ForgedAttestation {
        # Signs an arbitrary payload object with a (trusted) key, bypassing
        # New-TemplateAttestation's own input validation, so tests can prove the VERIFIER
        # rejects malformed-but-correctly-signed payloads.
        param (
            [Parameter(Mandatory = $true)]
            $PayloadObject,

            [Parameter(Mandatory = $true)]
            [string]$PrivateKeyPath
        )

        $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes(($PayloadObject | ConvertTo-Json -Compress))
        $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
        try {
            $ecdsa.ImportFromPem((Get-Content -LiteralPath $PrivateKeyPath -Raw))
            $signatureBytes = $ecdsa.SignData($payloadBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
            $keyId = [System.Convert]::ToHexString(
                [System.Security.Cryptography.SHA256]::HashData($ecdsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
        }
        finally {
            $ecdsa.Dispose()
        }

        return ([ordered]@{
                version    = 1
                payloadB64 = [System.Convert]::ToBase64String($payloadBytes)
                signature  = [ordered]@{
                    algorithm = "ECDSA_P256_SHA256"
                    keyId     = $keyId
                    valueB64  = [System.Convert]::ToBase64String($signatureBytes)
                }
            } | ConvertTo-Json -Depth 5)
    }

    function script:New-NonP256SigningKey {
        # A P-384 keypair for negative tests: the ECDSA_P256_SHA256 label must never accept it.
        param (
            [Parameter(Mandatory = $true)]
            [string]$PrivateKeyPath
        )

        $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP384)
        try {
            $pem = [System.Security.Cryptography.PemEncoding]::WriteString("PRIVATE KEY", $ecdsa.ExportPkcs8PrivateKey())
            [System.IO.File]::WriteAllText($PrivateKeyPath, $pem, [System.Text.UTF8Encoding]::new($false))
            $spkiBytes = $ecdsa.ExportSubjectPublicKeyInfo()
            return [pscustomobject]@{
                PrivateKeyPath   = $PrivateKeyPath
                PublicKeySpkiB64 = [System.Convert]::ToBase64String($spkiBytes)
                KeyId            = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($spkiBytes)).ToLowerInvariant()
            }
        }
        finally {
            $ecdsa.Dispose()
        }
    }

    function script:New-TestTrustSetup {
        # One self-consistent trust world: a signing key, a tracked policy trusting it under
        # producer name "local-dev", a fake package file, its hash, and a valid attestation.
        param (
            [string]$ProducerName = "local-dev",
            [string]$PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
            [string]$PackageVersion = "1.0.123"
        )

        $workspace = New-TestWorkspace
        $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "signer.pem")
        $trackedPolicyPath = Write-TrustPolicyFile -Path (Join-Path $workspace "template-trust-policy.json") -Producer @(
            New-DetachedProducerEntry -Name $ProducerName -SigningKey $signingKey
        )

        $packagePath = Join-Path $workspace "$PackageId.$PackageVersion.nupkg".ToLowerInvariant()
        Set-Content -LiteralPath $packagePath -Value "fake package bytes $([Guid]::NewGuid())" -Encoding utf8
        $packageSha256 = Get-FileSha256Hex -Path $packagePath

        $attestationJson = New-TemplateAttestation `
            -PackageId $PackageId `
            -PackageVersion $PackageVersion `
            -PackageSha256 $packageSha256 `
            -Producer $ProducerName `
            -PrivateKeyPath $signingKey.PrivateKeyPath

        return [pscustomobject]@{
            Workspace         = $workspace
            SigningKey        = $signingKey
            TrackedPolicyPath = $trackedPolicyPath
            TrustPolicy       = (Read-TemplateTrustPolicy -TrackedPolicyPath $trackedPolicyPath)
            PackageId         = $PackageId
            PackageVersion    = $PackageVersion
            PackagePath       = $packagePath
            PackageSha256     = $packageSha256
            AttestationJson   = $attestationJson
            ProducerName      = $ProducerName
        }
    }
}

Describe "Get-FileSha256Hex" {
    It "matches Get-FileHash and is lowercase hex" {
        $filePath = Join-Path $TestDrive "hash-me.bin"
        [System.IO.File]::WriteAllBytes($filePath, [byte[]](0..255))

        $hash = Get-FileSha256Hex -Path $filePath
        $hash | Should -Be (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $hash | Should -Match '^[0-9a-f]{64}$'
    }

    It "throws for a missing file" {
        { Get-FileSha256Hex -Path (Join-Path $TestDrive "absent.bin") } | Should -Throw "*does not exist*"
    }

    It "resolves relative paths against the PowerShell location, not the process working directory" {
        $workspace = New-TestWorkspace
        Set-Content -LiteralPath (Join-Path $workspace "rel.bin") -Value "relative bytes"
        Push-Location $workspace
        try {
            Get-FileSha256Hex -Path "./rel.bin" | Should -Be (Get-FileSha256Hex -Path (Join-Path $workspace "rel.bin"))
        }
        finally {
            Pop-Location
        }
    }
}

Describe "Get-TemplateAttestationFileName" {
    It "appends the attestation suffix to the package file name" {
        Get-TemplateAttestationFileName -PackageFileName "edfi.api.minimal.template.postgresql.5.2.0.1.0.123.nupkg" |
            Should -Be "edfi.api.minimal.template.postgresql.5.2.0.1.0.123.nupkg.attestation.json"
    }
}

Describe "New-TemplateAttestationSigningKey" {
    It "writes a PKCS#8 PEM private key and returns the trust-policy-shaped public half" {
        $keyPath = Join-Path (New-TestWorkspace) "signer.pem"
        $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath $keyPath

        Test-Path -LiteralPath $keyPath | Should -BeTrue
        (Get-Content -LiteralPath $keyPath -Raw) | Should -Match "BEGIN PRIVATE KEY"

        $signingKey.Algorithm | Should -Be "ECDSA_P256_SHA256"
        $signingKey.KeyId | Should -Match '^[0-9a-f]{64}$'

        # The keyId contract: SHA-256 of the SubjectPublicKeyInfo bytes.
        $spkiBytes = [System.Convert]::FromBase64String($signingKey.PublicKeySpkiB64)
        $expectedKeyId = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($spkiBytes)).ToLowerInvariant()
        $signingKey.KeyId | Should -Be $expectedKeyId
    }

    It "refuses to overwrite an existing key file" {
        $keyPath = Join-Path (New-TestWorkspace) "signer.pem"
        New-TemplateAttestationSigningKey -PrivateKeyPath $keyPath | Out-Null
        { New-TemplateAttestationSigningKey -PrivateKeyPath $keyPath } | Should -Throw "*Refusing to overwrite*"
    }

    It "generates a distinct key on every call" {
        $workspace = New-TestWorkspace
        $first = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "a.pem")
        $second = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "b.pem")
        $first.KeyId | Should -Not -Be $second.KeyId
    }
}

Describe "Read-TemplateTrustPolicy" {
    It "loads a tracked policy and returns its producers" {
        $setup = New-TestTrustSetup
        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $setup.TrackedPolicyPath
        @($policy.Producers).Count | Should -Be 1
        $policy.Producers[0].Name | Should -Be "local-dev"
        $policy.Producers[0].Provider | Should -Be "detached-attestation"
        @($policy.Producers[0].PublicKeys).Count | Should -Be 1
    }

    It "merges the optional local overlay additively" {
        $workspace = New-TestWorkspace
        $trackedKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "ci.pem")
        $localKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "dev.pem")

        $trackedPath = Write-TrustPolicyFile -Path (Join-Path $workspace "tracked.json") -Producer @(
            New-DetachedProducerEntry -Name "edfi-alliance-ci" -SigningKey $trackedKey
        )
        $localPath = Write-TrustPolicyFile -Path (Join-Path $workspace "local.json") -Producer @(
            New-DetachedProducerEntry -Name "local-dev" -SigningKey $localKey
        )

        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $trackedPath -LocalPolicyPath $localPath
        @($policy.Producers).Count | Should -Be 2
        @($policy.Producers | ForEach-Object { $_.Name }) | Should -Be @("edfi-alliance-ci", "local-dev")
    }

    It "treats a missing local overlay as no overlay, but a missing tracked policy as an error" {
        $setup = New-TestTrustSetup
        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $setup.TrackedPolicyPath -LocalPolicyPath (Join-Path $setup.Workspace "absent-local.json")
        @($policy.Producers).Count | Should -Be 1

        { Read-TemplateTrustPolicy -TrackedPolicyPath (Join-Path $setup.Workspace "absent-tracked.json") } |
            Should -Throw "*Tracked trust policy was not found*"
    }

    It "accepts an intentionally empty producers list (fail-closed policy)" {
        $workspace = New-TestWorkspace
        $trackedPath = Write-TrustPolicyFile -Path (Join-Path $workspace "empty.json") -Producer @()
        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $trackedPath
        @($policy.Producers).Count | Should -Be 0
    }

    It "rejects duplicate producer names within a file and across the overlay" {
        $workspace = New-TestWorkspace
        $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "k.pem")

        $duplicateInOne = Write-TrustPolicyFile -Path (Join-Path $workspace "dup.json") -Producer @(
            (New-DetachedProducerEntry -Name "same" -SigningKey $signingKey),
            (New-DetachedProducerEntry -Name "SAME" -SigningKey $signingKey)
        )
        { Read-TemplateTrustPolicy -TrackedPolicyPath $duplicateInOne } | Should -Throw "*duplicate producer name*"

        $trackedPath = Write-TrustPolicyFile -Path (Join-Path $workspace "tracked.json") -Producer @(
            New-DetachedProducerEntry -Name "shared-name" -SigningKey $signingKey
        )
        $localPath = Write-TrustPolicyFile -Path (Join-Path $workspace "local.json") -Producer @(
            New-DetachedProducerEntry -Name "Shared-Name" -SigningKey $signingKey
        )
        { Read-TemplateTrustPolicy -TrackedPolicyPath $trackedPath -LocalPolicyPath $localPath } |
            Should -Throw "*already exists in the tracked policy*"
    }

    It "rejects an unsupported policy version and unknown providers" {
        $workspace = New-TestWorkspace
        $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "k.pem")

        $badVersion = Write-TrustPolicyFile -Path (Join-Path $workspace "v2.json") -Version 2 -Producer @()
        { Read-TemplateTrustPolicy -TrackedPolicyPath $badVersion } | Should -Throw "*only version 1 is supported*"

        $entry = New-DetachedProducerEntry -Name "p" -SigningKey $signingKey
        $entry.provider = "gpg"
        $unknownProvider = Write-TrustPolicyFile -Path (Join-Path $workspace "gpg.json") -Producer @($entry)
        { Read-TemplateTrustPolicy -TrackedPolicyPath $unknownProvider } | Should -Throw "*unknown provider 'gpg'*"
    }

    It "rejects a detached-attestation producer without keys, with a corrupt keyId, or with non-base64 key material" {
        $workspace = New-TestWorkspace
        $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "k.pem")

        $noKeys = Write-TrustPolicyFile -Path (Join-Path $workspace "nokeys.json") -Producer @(
            [ordered]@{ name = "p"; provider = "detached-attestation"; publicKeys = @() }
        )
        { Read-TemplateTrustPolicy -TrackedPolicyPath $noKeys } | Should -Throw "*declares no 'publicKeys'*"

        $wrongKeyIdEntry = New-DetachedProducerEntry -Name "p" -SigningKey $signingKey
        $wrongKeyIdEntry.publicKeys[0].keyId = ("0" * 64)
        $wrongKeyId = Write-TrustPolicyFile -Path (Join-Path $workspace "wrongid.json") -Producer @($wrongKeyIdEntry)
        { Read-TemplateTrustPolicy -TrackedPolicyPath $wrongKeyId } | Should -Throw "*does not match the SHA-256 of its own publicKeySpkiB64*"

        $badBase64Entry = New-DetachedProducerEntry -Name "p" -SigningKey $signingKey
        $badBase64Entry.publicKeys[0].publicKeySpkiB64 = "not base64!!"
        $badBase64 = Write-TrustPolicyFile -Path (Join-Path $workspace "badb64.json") -Producer @($badBase64Entry)
        { Read-TemplateTrustPolicy -TrackedPolicyPath $badBase64 } | Should -Throw "*not valid base64*"
    }

    It "rejects singleton objects where the policy schema requires JSON arrays" {
        $workspace = New-TestWorkspace
        $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "k.pem")

        # producers as a single JSON object rather than an array.
        $singletonProducersPath = Join-Path $workspace "singleton-producers.json"
        [ordered]@{ version = 1; producers = (New-DetachedProducerEntry -Name "p" -SigningKey $signingKey) } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $singletonProducersPath -Encoding utf8
        { Read-TemplateTrustPolicy -TrackedPolicyPath $singletonProducersPath } |
            Should -Throw "*'producers' must be a JSON array*"

        # publicKeys as a single JSON object rather than an array.
        $singletonKeysPath = Join-Path $workspace "singleton-keys.json"
        [ordered]@{
            version   = 1
            producers = @(
                [ordered]@{
                    name       = "p"
                    provider   = "detached-attestation"
                    publicKeys = [ordered]@{
                        keyId            = $signingKey.KeyId
                        algorithm        = $signingKey.Algorithm
                        publicKeySpkiB64 = $signingKey.PublicKeySpkiB64
                    }
                }
            )
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $singletonKeysPath -Encoding utf8
        { Read-TemplateTrustPolicy -TrackedPolicyPath $singletonKeysPath } |
            Should -Throw "*'publicKeys' must be a JSON array*"

        # certificateFingerprints as a single JSON string rather than an array.
        $singletonFingerprintPath = Join-Path $workspace "singleton-fingerprint.json"
        [ordered]@{
            version   = 1
            producers = @(
                [ordered]@{ name = "p"; provider = "nuget-author-signature"; certificateFingerprints = ("a1" * 32) }
            )
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $singletonFingerprintPath -Encoding utf8
        { Read-TemplateTrustPolicy -TrackedPolicyPath $singletonFingerprintPath } |
            Should -Throw "*'certificateFingerprints' must be a JSON array*"
    }

    It "loads a schema-reserved nuget-author-signature producer but requires fingerprints" {
        $workspace = New-TestWorkspace
        $withFingerprints = Write-TrustPolicyFile -Path (Join-Path $workspace "nuget.json") -Producer @(
            [ordered]@{ name = "signed-feed"; provider = "nuget-author-signature"; certificateFingerprints = @(("a1" * 32)) }
        )
        $policy = Read-TemplateTrustPolicy -TrackedPolicyPath $withFingerprints
        $policy.Producers[0].Provider | Should -Be "nuget-author-signature"

        $withoutFingerprints = Write-TrustPolicyFile -Path (Join-Path $workspace "nuget-empty.json") -Producer @(
            [ordered]@{ name = "signed-feed"; provider = "nuget-author-signature"; certificateFingerprints = @() }
        )
        { Read-TemplateTrustPolicy -TrackedPolicyPath $withoutFingerprints } | Should -Throw "*declares no 'certificateFingerprints'*"
    }
}

Describe "New-TemplateAttestation and Test-TemplateAttestation round trip" {
    It "verifies a freshly signed attestation against the exact package bytes and identity" {
        $setup = New-TestTrustSetup
        $verdict = Test-TemplateAttestation `
            -AttestationJson $setup.AttestationJson `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeTrue
        $verdict.Producer | Should -Be "local-dev"
        $verdict.Reason | Should -Be ""
    }

    It "rejects the attestation when the package bytes changed after signing" {
        $setup = New-TestTrustSetup
        Add-Content -LiteralPath $setup.PackagePath -Value "tampered"
        $tamperedSha = Get-FileSha256Hex -Path $setup.PackagePath

        $verdict = Test-TemplateAttestation `
            -AttestationJson $setup.AttestationJson `
            -PackageSha256 $tamperedSha `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*does not match the resolved package bytes*"
    }

    It "rejects a tampered payload (signature no longer verifies)" {
        $setup = New-TestTrustSetup
        $document = $setup.AttestationJson | ConvertFrom-Json

        # Re-encode a payload whose sha field was swapped to the attacker's target hash: the
        # signature was computed over the original bytes, so verification must fail.
        $payload = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($document.payloadB64))
        $forgedPayload = $payload.Replace($setup.PackageSha256, ("f" * 64))
        $document.payloadB64 = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($forgedPayload))

        $verdict = Test-TemplateAttestation `
            -AttestationJson ($document | ConvertTo-Json -Depth 5) `
            -PackageSha256 ("f" * 64) `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*signature verification failed*"
    }

    It "rejects a tampered signature value" {
        $setup = New-TestTrustSetup
        $document = $setup.AttestationJson | ConvertFrom-Json
        $signatureBytes = [System.Convert]::FromBase64String($document.signature.valueB64)
        $signatureBytes[0] = $signatureBytes[0] -bxor 0xFF
        $document.signature.valueB64 = [System.Convert]::ToBase64String($signatureBytes)

        $verdict = Test-TemplateAttestation `
            -AttestationJson ($document | ConvertTo-Json -Depth 5) `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*signature verification failed*"
    }

    It "rejects an attestation signed by a key outside the trust policy" {
        $setup = New-TestTrustSetup
        $untrustedKey = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $setup.Workspace "untrusted.pem")
        $untrustedAttestation = New-TemplateAttestation `
            -PackageId $setup.PackageId `
            -PackageVersion $setup.PackageVersion `
            -PackageSha256 $setup.PackageSha256 `
            -Producer $setup.ProducerName `
            -PrivateKeyPath $untrustedKey.PrivateKeyPath

        $verdict = Test-TemplateAttestation `
            -AttestationJson $untrustedAttestation `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*not signed by any trusted producer key*"
    }

    It "rejects a package identity mismatch" {
        $setup = New-TestTrustSetup
        $verdict = Test-TemplateAttestation `
            -AttestationJson $setup.AttestationJson `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId "EdFi.Api.Populated.Template.PostgreSql.5.2.0" `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*does not match the requested*"
    }

    It "accepts case-differing package identity spellings (NuGet identities are case-insensitive)" {
        $setup = New-TestTrustSetup
        $verdict = Test-TemplateAttestation `
            -AttestationJson $setup.AttestationJson `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId.ToUpperInvariant() `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeTrue
    }

    It "rejects a payload whose producer does not match the trusted key's producer" {
        $setup = New-TestTrustSetup
        $forgedProducerAttestation = New-TemplateAttestation `
            -PackageId $setup.PackageId `
            -PackageVersion $setup.PackageVersion `
            -PackageSha256 $setup.PackageSha256 `
            -Producer "someone-else" `
            -PrivateKeyPath $setup.SigningKey.PrivateKeyPath

        $verdict = Test-TemplateAttestation `
            -AttestationJson $forgedProducerAttestation `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*names producer 'someone-else', but the signing key belongs to*"
    }

    It "fails closed with configuration guidance when the policy has no detached-attestation producers" {
        $setup = New-TestTrustSetup

        $workspace = New-TestWorkspace
        $emptyPolicyPath = Write-TrustPolicyFile -Path (Join-Path $workspace "empty.json") -Producer @()
        $emptyPolicy = Read-TemplateTrustPolicy -TrackedPolicyPath $emptyPolicyPath

        $verdict = Test-TemplateAttestation `
            -AttestationJson $setup.AttestationJson `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $emptyPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*no detached-attestation producers*template-trust-policy*"

        # A policy carrying only the schema-reserved nuget-author-signature provider has no
        # usable anchors either: the provider is not implemented, so verification fails closed.
        $nugetOnlyPath = Write-TrustPolicyFile -Path (Join-Path $workspace "nuget-only.json") -Producer @(
            [ordered]@{ name = "signed-feed"; provider = "nuget-author-signature"; certificateFingerprints = @(("a1" * 32)) }
        )
        $nugetOnlyPolicy = Read-TemplateTrustPolicy -TrackedPolicyPath $nugetOnlyPath
        (Test-TemplateAttestation `
            -AttestationJson $setup.AttestationJson `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $nugetOnlyPolicy).IsTrusted | Should -BeFalse
    }

    It "rejects malformed, empty, and version-mismatched attestation documents without throwing" {
        $setup = New-TestTrustSetup

        foreach ($badDocument in @("", "not json at all", "null")) {
            $verdict = Test-TemplateAttestation `
                -AttestationJson $badDocument `
                -PackageSha256 $setup.PackageSha256 `
                -ExpectedPackageId $setup.PackageId `
                -ExpectedPackageVersion $setup.PackageVersion `
                -TrustPolicy $setup.TrustPolicy
            $verdict.IsTrusted | Should -BeFalse
        }

        $document = $setup.AttestationJson | ConvertFrom-Json
        $document.version = 2
        $verdict = Test-TemplateAttestation `
            -AttestationJson ($document | ConvertTo-Json -Depth 5) `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy
        $verdict.Reason | Should -BeLike "*Unsupported attestation document version '2'*"

        $document = $setup.AttestationJson | ConvertFrom-Json
        $document.signature.algorithm = "RSA_PKCS1_SHA256"
        $verdict = Test-TemplateAttestation `
            -AttestationJson ($document | ConvertTo-Json -Depth 5) `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy
        $verdict.Reason | Should -BeLike "*Unsupported attestation signature algorithm*"
    }

    It "rejects a correctly signed payload whose attestationVersion is the string '1' rather than the integer 1" {
        $setup = New-TestTrustSetup
        $forged = New-ForgedAttestation -PrivateKeyPath $setup.SigningKey.PrivateKeyPath -PayloadObject ([ordered]@{
                attestationVersion = "1"
                packageId          = $setup.PackageId
                packageVersion     = $setup.PackageVersion
                packageSha256      = $setup.PackageSha256
                producer           = $setup.ProducerName
                createdUtc         = "2026-08-18T00:00:00.0000000Z"
            })

        $verdict = Test-TemplateAttestation `
            -AttestationJson $forged `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*Unsupported attestation payload version '1'*"
    }

    It "rejects a correctly signed payload whose packageSha256 is uppercase instead of normalizing it into acceptance" {
        $setup = New-TestTrustSetup
        $forged = New-ForgedAttestation -PrivateKeyPath $setup.SigningKey.PrivateKeyPath -PayloadObject ([ordered]@{
                attestationVersion = 1
                packageId          = $setup.PackageId
                packageVersion     = $setup.PackageVersion
                packageSha256      = $setup.PackageSha256.ToUpperInvariant()
                producer           = $setup.ProducerName
                createdUtc         = "2026-08-18T00:00:00.0000000Z"
            })

        $verdict = Test-TemplateAttestation `
            -AttestationJson $forged `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*payload packageSha256 is not a 64-character lowercase hex*"
    }

    It "rejects a document whose version is the string '1' rather than the integer 1" {
        $setup = New-TestTrustSetup
        $document = $setup.AttestationJson | ConvertFrom-Json
        $document.version = "1"

        $verdict = Test-TemplateAttestation `
            -AttestationJson ($document | ConvertTo-Json -Depth 5) `
            -PackageSha256 $setup.PackageSha256 `
            -ExpectedPackageId $setup.PackageId `
            -ExpectedPackageVersion $setup.PackageVersion `
            -TrustPolicy $setup.TrustPolicy

        $verdict.IsTrusted | Should -BeFalse
        $verdict.Reason | Should -BeLike "*Unsupported attestation document version '1'*"
    }

    It "rejects an uppercase or malformed caller-supplied package hash as a caller defect" {
        $setup = New-TestTrustSetup
        { Test-TemplateAttestation `
                -AttestationJson $setup.AttestationJson `
                -PackageSha256 $setup.PackageSha256.ToUpperInvariant() `
                -ExpectedPackageId $setup.PackageId `
                -ExpectedPackageVersion $setup.PackageVersion `
                -TrustPolicy $setup.TrustPolicy } |
            Should -Throw "*must be a 64-character lowercase hex*"
    }

    It "rejects a malformed hash handed to New-TemplateAttestation" {
        $setup = New-TestTrustSetup
        { New-TemplateAttestation `
                -PackageId $setup.PackageId `
                -PackageVersion $setup.PackageVersion `
                -PackageSha256 "abc" `
                -Producer $setup.ProducerName `
                -PrivateKeyPath $setup.SigningKey.PrivateKeyPath } |
            Should -Throw "*must be a 64-character lowercase hex*"
    }
}

Describe "ECDSA P-256 curve enforcement" {
    It "refuses to sign with a non-P-256 private key even under the P-256 algorithm label" {
        $workspace = New-TestWorkspace
        $p384Key = New-NonP256SigningKey -PrivateKeyPath (Join-Path $workspace "p384.pem")

        { New-TemplateAttestation `
                -PackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" `
                -PackageVersion "1.0.123" `
                -PackageSha256 ("ab" * 32) `
                -Producer "local-dev" `
                -PrivateKeyPath $p384Key.PrivateKeyPath } |
            Should -Throw "*not a NIST P-256 ECDSA key*1.2.840.10045.3.1.7*"
    }

    It "validates signer keys up front: P-256 passes, P-384 and RSA and missing files fail" {
        $workspace = New-TestWorkspace

        $p256Key = New-TemplateAttestationSigningKey -PrivateKeyPath (Join-Path $workspace "p256.pem")
        { Assert-TemplateAttestationSignerKey -PrivateKeyPath $p256Key.PrivateKeyPath } | Should -Not -Throw

        $p384Key = New-NonP256SigningKey -PrivateKeyPath (Join-Path $workspace "p384.pem")
        { Assert-TemplateAttestationSignerKey -PrivateKeyPath $p384Key.PrivateKeyPath } |
            Should -Throw "*not a NIST P-256 ECDSA key*"

        $rsa = [System.Security.Cryptography.RSA]::Create(2048)
        try {
            $rsaPem = [System.Security.Cryptography.PemEncoding]::WriteString("PRIVATE KEY", $rsa.ExportPkcs8PrivateKey())
            Set-Content -LiteralPath (Join-Path $workspace "rsa.pem") -Value $rsaPem -Encoding utf8
        }
        finally {
            $rsa.Dispose()
        }
        { Assert-TemplateAttestationSignerKey -PrivateKeyPath (Join-Path $workspace "rsa.pem") } |
            Should -Throw "*not an importable ECDSA private key*"

        { Assert-TemplateAttestationSignerKey -PrivateKeyPath (Join-Path $workspace "absent.pem") } |
            Should -Throw "*was not found*"
    }

    It "rejects a non-P-256 public key at trust-policy load even with a correct keyId and the P-256 label" {
        $workspace = New-TestWorkspace
        $p384Key = New-NonP256SigningKey -PrivateKeyPath (Join-Path $workspace "p384.pem")

        $policyPath = Write-TrustPolicyFile -Path (Join-Path $workspace "p384-policy.json") -Producer @(
            [ordered]@{
                name       = "rogue"
                provider   = "detached-attestation"
                publicKeys = @(
                    [ordered]@{
                        keyId            = $p384Key.KeyId
                        algorithm        = "ECDSA_P256_SHA256"
                        publicKeySpkiB64 = $p384Key.PublicKeySpkiB64
                    }
                )
            }
        )

        { Read-TemplateTrustPolicy -TrackedPolicyPath $policyPath } |
            Should -Throw "*not a NIST P-256 ECDSA key*1.2.840.10045.3.1.7*"
    }

    It "throws on a caller-constructed policy carrying a non-P-256 key at verification (the load-bypass probe)" {
        $workspace = New-TestWorkspace
        $p384Key = New-NonP256SigningKey -PrivateKeyPath (Join-Path $workspace "p384.pem")

        # A policy object built by hand, bypassing Read-TemplateTrustPolicy's load-time curve
        # check, paired with an attestation signed by the matching P-384 private key. Before
        # the curve enforcement this verified as trusted; now it is a configuration defect.
        $roguePolicy = [pscustomobject]@{
            Producers = @(
                [pscustomobject]@{
                    Name       = "rogue"
                    Provider   = "detached-attestation"
                    PublicKeys = @(
                        [pscustomobject]@{
                            KeyId            = $p384Key.KeyId
                            Algorithm        = "ECDSA_P256_SHA256"
                            PublicKeySpkiB64 = $p384Key.PublicKeySpkiB64
                        }
                    )
                }
            )
        }

        $forged = New-ForgedAttestation -PrivateKeyPath $p384Key.PrivateKeyPath -PayloadObject ([ordered]@{
                attestationVersion = 1
                packageId          = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0"
                packageVersion     = "1.0.123"
                packageSha256      = ("ab" * 32)
                producer           = "rogue"
                createdUtc         = "2026-08-18T00:00:00.0000000Z"
            })

        { Test-TemplateAttestation `
                -AttestationJson $forged `
                -PackageSha256 ("ab" * 32) `
                -ExpectedPackageId "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" `
                -ExpectedPackageVersion "1.0.123" `
                -TrustPolicy $roguePolicy } |
            Should -Throw "*not a NIST P-256 ECDSA key*"
    }
}
