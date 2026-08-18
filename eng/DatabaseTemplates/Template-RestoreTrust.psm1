# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Trust primitives for database-template restore: trust-policy loading and validation,
    detached-attestation creation and verification (ECDSA P-256 over the exact package
    bytes' SHA-256), and file hashing.

.DESCRIPTION
    Database-template packages are executable deployment inputs, so the consumer
    authenticates the exact .nupkg bytes against operator-configured trust anchors before
    extraction, Docker startup, workspace creation, or any database mutation. This module
    owns the pure verification mechanics; it performs no docker, database, or network work
    and imports no siblings. There is deliberately no bypass: an empty or anchor-less trust
    policy makes every verification fail closed.
#>

$script:SupportedTrustPolicyVersion = 1
$script:SupportedAttestationVersion = 1
$script:AttestationAlgorithmEcdsaP256Sha256 = "ECDSA_P256_SHA256"
$script:TrustProviderDetachedAttestation = "detached-attestation"
$script:TrustProviderNugetAuthorSignature = "nuget-author-signature"

function Get-ByteSha256Hex {
    <#
    .SYNOPSIS
    Lowercase-hex SHA-256 of a byte array.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [byte[]]$Byte
    )

    return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Byte)).ToLowerInvariant()
}

function Get-JsonPropertyValue {
    <#
    .SYNOPSIS
    StrictMode-safe property read on parsed JSON objects; returns $null when absent.
    Written as an explicit helper (rather than the null-conditional member operator)
    because PSScriptAnalyzer 1.25 fails to analyze files using ?. member access.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }

    return $null
}

function Get-FileSha256Hex {
    <#
    .SYNOPSIS
    Lowercase-hex SHA-256 of a file's exact bytes.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot hash '$Path' because the file does not exist."
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $hashBytes = [System.Security.Cryptography.SHA256]::HashData($stream)
    }
    finally {
        $stream.Dispose()
    }

    return [System.Convert]::ToHexString($hashBytes).ToLowerInvariant()
}

function Get-TemplateAttestationFileName {
    <#
    .SYNOPSIS
    The sibling attestation document name for a template package file name
    (e.g. "x.nupkg" -> "x.nupkg.attestation.json").
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$PackageFileName
    )

    return "$PackageFileName.attestation.json"
}

function New-TemplateAttestationSigningKey {
    <#
    .SYNOPSIS
    Generates an ECDSA P-256 signing keypair for template attestation. The private key is
    written as a PKCS#8 PEM to the caller-supplied path (never overwriting an existing
    file); the returned object carries the public half in the exact shape a trust-policy
    producer entry needs (keyId, algorithm, base64 SubjectPublicKeyInfo).

    .NOTES
    Private key material must never be committed; callers are responsible for pointing
    -PrivateKeyPath at a git-ignored or out-of-repo location.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Key-generation helper for the dev/CI signer flows; callers do not expose -WhatIf end to end, and a silent no-op would hand back a key object whose private half was never written.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$PrivateKeyPath
    )

    if (Test-Path -LiteralPath $PrivateKeyPath) {
        throw "Refusing to overwrite existing key file '$PrivateKeyPath'. Remove it first if rotation is intended."
    }

    $parentDirectory = [System.IO.Path]::GetDirectoryName($PrivateKeyPath)
    if (-not [string]::IsNullOrWhiteSpace($parentDirectory) -and -not (Test-Path -LiteralPath $parentDirectory)) {
        New-Item -ItemType Directory -Path $parentDirectory -Force | Out-Null
    }

    $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $privateKeyPem = [System.Security.Cryptography.PemEncoding]::WriteString("PRIVATE KEY", $ecdsa.ExportPkcs8PrivateKey())
        [System.IO.File]::WriteAllText($PrivateKeyPath, $privateKeyPem + "`n", [System.Text.UTF8Encoding]::new($false))

        $publicKeySpki = $ecdsa.ExportSubjectPublicKeyInfo()

        return [pscustomobject]@{
            KeyId            = (Get-ByteSha256Hex -Byte $publicKeySpki)
            Algorithm        = $script:AttestationAlgorithmEcdsaP256Sha256
            PublicKeySpkiB64 = [System.Convert]::ToBase64String($publicKeySpki)
            PrivateKeyPath   = $PrivateKeyPath
        }
    }
    finally {
        $ecdsa.Dispose()
    }
}

function Read-TrustPolicyDocument {
    <#
    .SYNOPSIS
    Reads and validates one trust-policy JSON file, returning its normalized producer list.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $rawContent = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($rawContent)) {
        throw "Trust policy '$Path' is empty."
    }

    $document = $null
    try {
        $document = $rawContent | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Trust policy '$Path' is not valid JSON: $($_.Exception.Message)"
    }

    $versionProperty = $document.PSObject.Properties["version"]
    if ($null -eq $versionProperty -or $versionProperty.Value -isnot [int] -and $versionProperty.Value -isnot [long]) {
        throw "Trust policy '$Path' must declare an integer 'version'."
    }
    if ($versionProperty.Value -ne $script:SupportedTrustPolicyVersion) {
        throw "Trust policy '$Path' declares version '$($versionProperty.Value)' but only version $($script:SupportedTrustPolicyVersion) is supported."
    }

    $producersProperty = $document.PSObject.Properties["producers"]
    if ($null -eq $producersProperty -or $null -eq $producersProperty.Value) {
        throw "Trust policy '$Path' must declare a 'producers' array (empty is allowed and means no trust anchors)."
    }

    $producers = [System.Collections.Generic.List[object]]::new()
    foreach ($rawProducer in @($producersProperty.Value)) {
        if ($null -eq $rawProducer) { continue }

        $nameProperty = $rawProducer.PSObject.Properties["name"]
        if ($null -eq $nameProperty -or $nameProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($nameProperty.Value)) {
            throw "Trust policy '$Path' contains a producer without a non-empty string 'name'."
        }
        $producerName = [string]$nameProperty.Value

        $providerProperty = $rawProducer.PSObject.Properties["provider"]
        if ($null -eq $providerProperty -or $providerProperty.Value -isnot [string]) {
            throw "Trust policy '$Path' producer '$producerName' must declare a string 'provider'."
        }
        $provider = [string]$providerProperty.Value

        if ($provider -cnotin @($script:TrustProviderDetachedAttestation, $script:TrustProviderNugetAuthorSignature)) {
            throw "Trust policy '$Path' producer '$producerName' declares unknown provider '$provider'. Known providers: $($script:TrustProviderDetachedAttestation), $($script:TrustProviderNugetAuthorSignature)."
        }

        if ($provider -eq $script:TrustProviderDetachedAttestation) {
            # Plain branch assignment: a captured if/else expression flattens an empty-array
            # branch value to $null, which breaks the .Count read under StrictMode.
            $keysProperty = $rawProducer.PSObject.Properties["publicKeys"]
            $keyEntries = @()
            if ($null -ne $keysProperty -and $null -ne $keysProperty.Value) {
                $keyEntries = @($keysProperty.Value)
            }
            if ($keyEntries.Count -eq 0) {
                throw "Trust policy '$Path' producer '$producerName' uses provider '$provider' but declares no 'publicKeys'."
            }

            $publicKeys = [System.Collections.Generic.List[object]]::new()
            foreach ($rawKey in $keyEntries) {
                $keyId = [string](Get-JsonPropertyValue -InputObject $rawKey -Name "keyId")
                $algorithm = [string](Get-JsonPropertyValue -InputObject $rawKey -Name "algorithm")
                $publicKeySpkiB64 = [string](Get-JsonPropertyValue -InputObject $rawKey -Name "publicKeySpkiB64")

                if ($keyId -cnotmatch "^[0-9a-f]{64}\z") {
                    throw "Trust policy '$Path' producer '$producerName' contains a public key without a 64-character lowercase hex 'keyId'."
                }
                if ($algorithm -cne $script:AttestationAlgorithmEcdsaP256Sha256) {
                    throw "Trust policy '$Path' producer '$producerName' key '$keyId' declares unsupported algorithm '$algorithm'. Supported: $($script:AttestationAlgorithmEcdsaP256Sha256)."
                }

                $spkiBytes = $null
                try {
                    $spkiBytes = [System.Convert]::FromBase64String($publicKeySpkiB64)
                }
                catch {
                    throw "Trust policy '$Path' producer '$producerName' key '$keyId' has a 'publicKeySpkiB64' that is not valid base64."
                }

                if ((Get-ByteSha256Hex -Byte $spkiBytes) -cne $keyId) {
                    throw "Trust policy '$Path' producer '$producerName' key '$keyId' does not match the SHA-256 of its own publicKeySpkiB64; the policy entry is corrupt or mispasted."
                }

                # Prove importability at load time so a malformed key fails here with policy
                # context rather than at verification time.
                $probe = [System.Security.Cryptography.ECDsa]::Create()
                try {
                    $bytesRead = 0
                    $probe.ImportSubjectPublicKeyInfo($spkiBytes, [ref]$bytesRead)
                }
                catch {
                    throw "Trust policy '$Path' producer '$producerName' key '$keyId' is not an importable SubjectPublicKeyInfo: $($_.Exception.Message)"
                }
                finally {
                    $probe.Dispose()
                }

                $publicKeys.Add([pscustomobject]@{
                        KeyId            = $keyId
                        Algorithm        = $algorithm
                        PublicKeySpkiB64 = $publicKeySpkiB64
                    })
            }

            $producers.Add([pscustomobject]@{
                    Name       = $producerName
                    Provider   = $provider
                    PublicKeys = $publicKeys.ToArray()
                })
        }
        else {
            # nuget-author-signature is schema-reserved: entries validate so a policy can be
            # authored ahead of provider support, but no verification path consumes them yet.
            $fingerprintsProperty = $rawProducer.PSObject.Properties["certificateFingerprints"]
            $fingerprintEntries = @()
            if ($null -ne $fingerprintsProperty -and $null -ne $fingerprintsProperty.Value) {
                $fingerprintEntries = @($fingerprintsProperty.Value)
            }
            if ($fingerprintEntries.Count -eq 0) {
                throw "Trust policy '$Path' producer '$producerName' uses provider '$provider' but declares no 'certificateFingerprints'."
            }
            $fingerprints = [System.Collections.Generic.List[string]]::new()
            foreach ($rawFingerprint in $fingerprintEntries) {
                $fingerprint = ([string]$rawFingerprint).ToLowerInvariant()
                if ($fingerprint -cnotmatch "^[0-9a-f]{64}\z") {
                    throw "Trust policy '$Path' producer '$producerName' contains a certificate fingerprint that is not a 64-character hex SHA-256."
                }
                $fingerprints.Add($fingerprint)
            }

            $producers.Add([pscustomobject]@{
                    Name                    = $producerName
                    Provider                = $provider
                    CertificateFingerprints = $fingerprints.ToArray()
                })
        }
    }

    return $producers.ToArray()
}

function Read-TemplateTrustPolicy {
    <#
    .SYNOPSIS
    Loads the tracked trust policy plus the optional operator-local overlay and merges them
    additively. Producer names must be unique across both files. The tracked policy file is
    required (a missing file is a broken configuration, not an empty policy); the local
    overlay is optional. A merged policy with zero producers is valid and means every
    package verification fails closed.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$TrackedPolicyPath,

        [string]$LocalPolicyPath = ""
    )

    if (-not (Test-Path -LiteralPath $TrackedPolicyPath -PathType Leaf)) {
        throw "Tracked trust policy was not found at '$TrackedPolicyPath'. The tracked policy file is required; an intentionally empty policy declares 'producers': []."
    }

    $mergedProducers = [System.Collections.Generic.List[object]]::new()
    $seenNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($producer in @(Read-TrustPolicyDocument -Path $TrackedPolicyPath)) {
        if (-not $seenNames.Add($producer.Name)) {
            throw "Trust policy '$TrackedPolicyPath' declares duplicate producer name '$($producer.Name)'."
        }
        $mergedProducers.Add($producer)
    }

    if (-not [string]::IsNullOrWhiteSpace($LocalPolicyPath) -and (Test-Path -LiteralPath $LocalPolicyPath -PathType Leaf)) {
        foreach ($producer in @(Read-TrustPolicyDocument -Path $LocalPolicyPath)) {
            if (-not $seenNames.Add($producer.Name)) {
                throw "Local trust policy '$LocalPolicyPath' declares producer name '$($producer.Name)' that already exists in the tracked policy. Producer names must be unique across both files."
            }
            $mergedProducers.Add($producer)
        }
    }

    return [pscustomobject]@{
        Producers = $mergedProducers.ToArray()
    }
}

function New-TemplateAttestation {
    <#
    .SYNOPSIS
    Creates the detached attestation document for a template package: a signed payload
    binding the exact .nupkg SHA-256, the package id/version, and the producer identity.
    Returns the attestation document as a JSON string. The signature covers the raw decoded
    payload bytes, so no JSON canonicalization is involved in verification.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Returns the attestation JSON string; no system state is created or changed.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$PackageId,

        [Parameter(Mandatory = $true)]
        [string]$PackageVersion,

        [Parameter(Mandatory = $true)]
        [string]$PackageSha256,

        [Parameter(Mandatory = $true)]
        [string]$Producer,

        [Parameter(Mandatory = $true)]
        [string]$PrivateKeyPath,

        [string]$CreatedUtc = ""
    )

    if ($PackageSha256 -cnotmatch "^[0-9a-f]{64}\z") {
        throw "PackageSha256 must be a 64-character lowercase hex SHA-256 of the exact .nupkg bytes."
    }
    if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
        throw "Signing key was not found at '$PrivateKeyPath'."
    }
    if ([string]::IsNullOrWhiteSpace($CreatedUtc)) {
        $CreatedUtc = [System.DateTime]::UtcNow.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    }

    $payload = [ordered]@{
        attestationVersion = $script:SupportedAttestationVersion
        packageId          = $PackageId
        packageVersion     = $PackageVersion
        packageSha256      = $PackageSha256
        producer           = $Producer
        createdUtc         = $CreatedUtc
    }
    $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress))

    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $ecdsa.ImportFromPem((Get-Content -LiteralPath $PrivateKeyPath -Raw))
        $signatureBytes = $ecdsa.SignData($payloadBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
        $keyId = Get-ByteSha256Hex -Byte $ecdsa.ExportSubjectPublicKeyInfo()
    }
    finally {
        $ecdsa.Dispose()
    }

    $attestation = [ordered]@{
        version    = $script:SupportedAttestationVersion
        payloadB64 = [System.Convert]::ToBase64String($payloadBytes)
        signature  = [ordered]@{
            algorithm = $script:AttestationAlgorithmEcdsaP256Sha256
            keyId     = $keyId
            valueB64  = [System.Convert]::ToBase64String($signatureBytes)
        }
    }

    return ($attestation | ConvertTo-Json -Depth 5)
}

function New-AttestationVerdict {
    <#
    .SYNOPSIS
    Builds the uniform verification verdict object.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Returns a verdict object; no system state is created or changed.')]
    param (
        [bool]$IsTrusted,
        [string]$Producer = "",
        [string]$Reason = ""
    )

    return [pscustomobject]@{
        IsTrusted = $IsTrusted
        Producer  = $Producer
        Reason    = $Reason
    }
}

function Test-TemplateAttestation {
    <#
    .SYNOPSIS
    Verifies a detached attestation document against the exact package bytes' SHA-256, the
    requested package identity, and the merged trust policy. Returns a verdict object
    { IsTrusted, Producer, Reason } and never throws for untrusted input: every defect in
    the document, signature, payload binding, or policy anchoring yields IsTrusted=$false
    with a specific reason, so callers fail closed with actionable diagnostics.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$AttestationJson,

        [Parameter(Mandatory = $true)]
        [string]$PackageSha256,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedPackageId,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedPackageVersion,

        [Parameter(Mandatory = $true)]
        $TrustPolicy
    )

    if ($PackageSha256 -cnotmatch "^[0-9a-f]{64}\z") {
        throw "PackageSha256 must be a 64-character lowercase hex SHA-256 of the exact .nupkg bytes."
    }

    $detachedProducers = @(@($TrustPolicy.Producers) | Where-Object { $_.Provider -eq $script:TrustProviderDetachedAttestation })
    if ($detachedProducers.Count -eq 0) {
        return New-AttestationVerdict -IsTrusted $false -Reason "The trust policy contains no detached-attestation producers, so no template package can be trusted. Configure a producer with public keys in template-trust-policy.json (or the operator-local template-trust-policy.local.json overlay)."
    }

    if ([string]::IsNullOrWhiteSpace($AttestationJson)) {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation document is empty."
    }

    $document = $null
    try {
        $document = $AttestationJson | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation document is not valid JSON."
    }
    if ($null -eq $document) {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation document is empty."
    }

    $documentVersion = Get-JsonPropertyValue -InputObject $document -Name "version"
    if ($documentVersion -ne $script:SupportedAttestationVersion) {
        return New-AttestationVerdict -IsTrusted $false -Reason "Unsupported attestation document version '$documentVersion'; only version $($script:SupportedAttestationVersion) is supported."
    }

    $payloadB64 = [string](Get-JsonPropertyValue -InputObject $document -Name "payloadB64")
    $signature = Get-JsonPropertyValue -InputObject $document -Name "signature"
    if ([string]::IsNullOrWhiteSpace($payloadB64) -or $null -eq $signature) {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation document is missing payloadB64 or signature."
    }

    $algorithm = [string](Get-JsonPropertyValue -InputObject $signature -Name "algorithm")
    $keyId = [string](Get-JsonPropertyValue -InputObject $signature -Name "keyId")
    $signatureValueB64 = [string](Get-JsonPropertyValue -InputObject $signature -Name "valueB64")

    if ($algorithm -cne $script:AttestationAlgorithmEcdsaP256Sha256) {
        return New-AttestationVerdict -IsTrusted $false -Reason "Unsupported attestation signature algorithm '$algorithm'; only $($script:AttestationAlgorithmEcdsaP256Sha256) is supported."
    }

    $payloadBytes = $null
    $signatureBytes = $null
    try {
        $payloadBytes = [System.Convert]::FromBase64String($payloadB64)
        $signatureBytes = [System.Convert]::FromBase64String($signatureValueB64)
    }
    catch {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation payload or signature is not valid base64."
    }

    $matchedProducer = $null
    $matchedKey = $null
    foreach ($producer in $detachedProducers) {
        foreach ($publicKey in @($producer.PublicKeys)) {
            if ($publicKey.KeyId -ceq $keyId) {
                $matchedProducer = $producer
                $matchedKey = $publicKey
                break
            }
        }
        if ($null -ne $matchedProducer) { break }
    }

    if ($null -eq $matchedProducer) {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation is not signed by any trusted producer key (keyId '$keyId' is not in the trust policy)."
    }

    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $bytesRead = 0
        $ecdsa.ImportSubjectPublicKeyInfo([System.Convert]::FromBase64String($matchedKey.PublicKeySpkiB64), [ref]$bytesRead)
        $signatureValid = $ecdsa.VerifyData($payloadBytes, $signatureBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    }
    finally {
        $ecdsa.Dispose()
    }

    if (-not $signatureValid) {
        return New-AttestationVerdict -IsTrusted $false -Reason "Attestation signature verification failed for trusted key '$keyId' (producer '$($matchedProducer.Name)'). The payload or signature has been altered."
    }

    $payload = $null
    try {
        $payload = [System.Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation payload is not valid JSON."
    }

    $payloadVersion = Get-JsonPropertyValue -InputObject $payload -Name "attestationVersion"
    if ($payloadVersion -ne $script:SupportedAttestationVersion) {
        return New-AttestationVerdict -IsTrusted $false -Reason "Unsupported attestation payload version '$payloadVersion'; only version $($script:SupportedAttestationVersion) is supported."
    }

    $payloadSha = ([string](Get-JsonPropertyValue -InputObject $payload -Name "packageSha256")).ToLowerInvariant()
    if ($payloadSha -cne $PackageSha256) {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation binds package SHA-256 '$payloadSha', which does not match the resolved package bytes ('$PackageSha256'). The package was modified after signing or the attestation belongs to a different package."
    }

    $payloadPackageId = [string](Get-JsonPropertyValue -InputObject $payload -Name "packageId")
    $payloadPackageVersion = [string](Get-JsonPropertyValue -InputObject $payload -Name "packageVersion")
    if (-not $payloadPackageId.Equals($ExpectedPackageId, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $payloadPackageVersion.Equals($ExpectedPackageVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation binds package identity '$payloadPackageId@$payloadPackageVersion', which does not match the requested '$ExpectedPackageId@$ExpectedPackageVersion'."
    }

    $payloadProducer = [string](Get-JsonPropertyValue -InputObject $payload -Name "producer")
    if (-not $payloadProducer.Equals($matchedProducer.Name, [System.StringComparison]::OrdinalIgnoreCase)) {
        return New-AttestationVerdict -IsTrusted $false -Reason "The attestation payload names producer '$payloadProducer', but the signing key belongs to trusted producer '$($matchedProducer.Name)'."
    }

    return New-AttestationVerdict -IsTrusted $true -Producer $matchedProducer.Name
}

Export-ModuleMember -Function `
    Get-FileSha256Hex, `
    Get-TemplateAttestationFileName, `
    New-TemplateAttestationSigningKey, `
    Read-TemplateTrustPolicy, `
    New-TemplateAttestation, `
    Test-TemplateAttestation
