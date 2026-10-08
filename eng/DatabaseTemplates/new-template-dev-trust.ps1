# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    One-time setup for a database-template attestation signer: generates an ECDSA P-256
    keypair and registers its public half where the restore trust policy can find it.

.DESCRIPTION
    Two purposes:

    Dev: generates the local development signer. The private key lands in the git-ignored
    eng/DatabaseTemplates/.dev-trust/ directory and the public half is registered as a
    producer in the operator-local (git-ignored) trust-policy overlay
    eng/docker-compose/template-trust-policy.local.json, which merges additively onto the
    tracked eng/docker-compose/template-trust-policy.json. Locally built template packages
    signed with this key then verify through the exact same code path as CI-published ones.

    CI: generates the keypair for the shared CI producer into a caller-supplied output
    directory and prints the tracked-policy producer block. Nothing tracked or local is
    modified: a repository administrator installs the private half as the
    TEMPLATE_ATTESTATION_PRIVATE_KEY Actions secret and the printed block is added to the
    tracked eng/docker-compose/template-trust-policy.json in a reviewed commit.

    Key rotation is deliberately manual: an existing key file or an existing producer entry
    is never overwritten; remove them explicitly first.

.PARAMETER Purpose
    "Dev" for the local development signer, "CI" for the shared CI producer keypair.

.PARAMETER ProducerName
    Trust-policy producer name the key is registered under. Defaults to "local-dev" for
    Dev and "edfi-alliance-ci" for CI.

.PARAMETER KeyDirectory
    Dev only: directory receiving the private key PEM. Defaults to the git-ignored
    eng/DatabaseTemplates/.dev-trust/. Do not point this at a tracked location.

.PARAMETER LocalPolicyPath
    Dev only: the operator-local trust-policy overlay to register the public key in.
    Defaults to the git-ignored eng/docker-compose/template-trust-policy.local.json.

.PARAMETER TrackedPolicyPath
    Dev only: the tracked trust policy the overlay merges onto. Producer names must be
    unique across both files, so a name already present in the tracked policy is refused
    before any key is generated - otherwise the production loader would later reject the
    overlay this script wrote. Defaults to eng/docker-compose/template-trust-policy.json.

.PARAMETER OutputDirectory
    CI only (required): directory receiving the generated private key PEM. Point this at a
    location outside the repository; the private half must never be committed.

.OUTPUTS
    PSCustomObject describing the generated signer: Purpose, ProducerName, PrivateKeyPath,
    KeyId, PublicKeySpkiB64, and (Dev) LocalPolicyPath or (CI) TrackedPolicyProducerJson.

.NOTES
    Private key material must never be committed. The Dev defaults are git-ignored; if you
    override the paths, keeping the key out of version control is on you.

.EXAMPLE
    pwsh ./new-template-dev-trust.ps1 -Purpose Dev
    Then build an attested package:
    Build-Template ... -AttestationSignerKeyPath ./.dev-trust/local-dev.pem -AttestationProducer local-dev
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Setup script intentionally writes operator guidance to the console; the structured result travels on the success pipeline.')]
[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [ValidateSet("Dev", "CI")]
    [string]$Purpose,

    [string]$ProducerName = "",

    [string]$KeyDirectory = "",

    [string]$LocalPolicyPath = "",

    [string]$TrackedPolicyPath = "",

    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot "Template-RestoreTrust.psm1") -Force

if ([string]::IsNullOrWhiteSpace($ProducerName)) {
    $ProducerName = if ($Purpose -eq "CI") { "edfi-alliance-ci" } else { "local-dev" }
}
# The producer name becomes the key file name, so it must be filesystem-safe. \z rejects a
# trailing newline the $ anchor would tolerate.
if ($ProducerName -cnotmatch "^[A-Za-z0-9][A-Za-z0-9._-]*\z") {
    throw "Producer name '$ProducerName' must start with a letter or digit and contain only letters, digits, dots, dashes, and underscores."
}

function New-TrustPolicyProducerEntry {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Returns a producer-entry object; no system state is created or changed.')]
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

if ($Purpose -eq "CI") {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        throw "-OutputDirectory is required for -Purpose CI. Point it OUTSIDE the repository; the private key must never be committed."
    }

    $privateKeyPath = Join-Path $OutputDirectory "$ProducerName.pem"
    $signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath $privateKeyPath

    $producerEntry = New-TrustPolicyProducerEntry -Name $ProducerName -SigningKey $signingKey
    $trackedPolicyProducerJson = $producerEntry | ConvertTo-Json -Depth 4

    Write-Host "CI attestation signer generated." -ForegroundColor Green
    Write-Host "  1. Install the PRIVATE key as the repository Actions secret TEMPLATE_ATTESTATION_PRIVATE_KEY:"
    Write-Host "       $privateKeyPath"
    Write-Host "     Then delete the file. Never commit it."
    Write-Host "  2. Add this producer entry to the tracked eng/docker-compose/template-trust-policy.json 'producers' array in a reviewed commit:"
    Write-Host $trackedPolicyProducerJson

    return [pscustomobject]@{
        Purpose                   = $Purpose
        ProducerName              = $ProducerName
        PrivateKeyPath            = $privateKeyPath
        KeyId                     = $signingKey.KeyId
        PublicKeySpkiB64          = $signingKey.PublicKeySpkiB64
        TrackedPolicyProducerJson = $trackedPolicyProducerJson
    }
}

# --- Dev purpose ---
if ([string]::IsNullOrWhiteSpace($KeyDirectory)) {
    $KeyDirectory = Join-Path $PSScriptRoot ".dev-trust"
}
if ([string]::IsNullOrWhiteSpace($LocalPolicyPath)) {
    $LocalPolicyPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../docker-compose/template-trust-policy.local.json"))
}
if ([string]::IsNullOrWhiteSpace($TrackedPolicyPath)) {
    $TrackedPolicyPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../docker-compose/template-trust-policy.json"))
}

# Producer names must be unique across the tracked policy and the local overlay, so a
# collision with the TRACKED policy is refused before any key is generated - otherwise the
# production loader would later reject the overlay this script wrote.
$trackedPolicy = Read-TemplateTrustPolicy -TrackedPolicyPath $TrackedPolicyPath
foreach ($trackedProducer in @($trackedPolicy.Producers)) {
    if (([string]$trackedProducer.Name).Equals($ProducerName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Producer '$ProducerName' already exists in the tracked policy '$TrackedPolicyPath'. Choose a different -ProducerName; tracked producers are managed through reviewed commits, not this script."
    }
}

# Validate the overlay BEFORE generating the key so a duplicate producer never leaves an
# orphaned key file behind.
$existingProducers = @()
if (Test-Path -LiteralPath $LocalPolicyPath -PathType Leaf) {
    $rawOverlay = Get-Content -LiteralPath $LocalPolicyPath -Raw
    $overlay = $null
    try {
        $overlay = $rawOverlay | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The local trust-policy overlay '$LocalPolicyPath' is not valid JSON: $($_.Exception.Message)"
    }

    $overlayVersion = $overlay.PSObject.Properties["version"]
    if ($null -eq $overlayVersion -or $overlayVersion.Value -ne 1) {
        throw "The local trust-policy overlay '$LocalPolicyPath' must declare version 1."
    }
    $overlayProducers = $overlay.PSObject.Properties["producers"]
    if ($null -ne $overlayProducers -and $null -ne $overlayProducers.Value) {
        $existingProducers = @($overlayProducers.Value)
    }

    foreach ($existingProducer in $existingProducers) {
        $existingName = [string]($existingProducer.PSObject.Properties["name"].Value)
        if ($existingName.Equals($ProducerName, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Producer '$ProducerName' already exists in '$LocalPolicyPath'. Key rotation is manual: remove the producer entry and its key file first, then rerun."
        }
    }
}

$privateKeyPath = Join-Path $KeyDirectory "$ProducerName.pem"
$signingKey = New-TemplateAttestationSigningKey -PrivateKeyPath $privateKeyPath

$producerEntry = New-TrustPolicyProducerEntry -Name $ProducerName -SigningKey $signingKey
$updatedProducers = @($existingProducers) + @($producerEntry)

$localPolicyDirectory = [System.IO.Path]::GetDirectoryName($LocalPolicyPath)
if (-not [string]::IsNullOrWhiteSpace($localPolicyDirectory) -and -not (Test-Path -LiteralPath $localPolicyDirectory)) {
    New-Item -ItemType Directory -Path $localPolicyDirectory -Force | Out-Null
}
[ordered]@{ version = 1; producers = $updatedProducers } |
    ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $LocalPolicyPath -Encoding utf8

Write-Host "Development attestation signer registered." -ForegroundColor Green
Write-Host "  Private key (git-ignored; never commit): $privateKeyPath"
Write-Host "  Trusted via local overlay:               $LocalPolicyPath"
Write-Host "  Build an attested template package with:"
Write-Host "    Build-Template ... -AttestationSignerKeyPath '$privateKeyPath' -AttestationProducer '$ProducerName'"

return [pscustomobject]@{
    Purpose          = $Purpose
    ProducerName     = $ProducerName
    PrivateKeyPath   = $privateKeyPath
    KeyId            = $signingKey.KeyId
    PublicKeySpkiB64 = $signingKey.PublicKeySpkiB64
    LocalPolicyPath  = $LocalPolicyPath
}
