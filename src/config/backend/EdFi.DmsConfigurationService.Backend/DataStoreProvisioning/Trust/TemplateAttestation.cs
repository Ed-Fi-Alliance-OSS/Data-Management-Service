// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

/// <summary>
/// The detached attestation document of the delivered trust contract (F-P4):
/// <c>{ "version": 1, "payloadB64", "signature": { "algorithm", "keyId", "valueB64" } }</c>. The signature is
/// ECDSA P-256 with SHA-256 over the raw decoded payload bytes (IEEE P1363 <c>r||s</c>), and the payload is the
/// compact JSON <c>{ attestationVersion, packageId, packageVersion, packageSha256, producer, createdUtc }</c>.
/// Parsing establishes shape only: values are kept exactly as written and nothing is decoded, compared, or
/// verified here; <see cref="TemplateAttestationVerifier"/> applies the F-P6 checks in order.
/// </summary>
/// <param name="PayloadB64">The base64 of the signed payload bytes, as written.</param>
/// <param name="Signature">The signature block, as written.</param>
public sealed record TemplateAttestation(string PayloadB64, TemplateAttestation.SignatureBlock Signature)
{
    public const int SupportedVersion = 1;

    /// <param name="Algorithm">The declared algorithm label; only <c>ECDSA_P256_SHA256</c> verifies.</param>
    /// <param name="KeyId">The signing key's id: lowercase hex SHA-256 of its SubjectPublicKeyInfo.</param>
    /// <param name="ValueB64">The base64 of the signature bytes.</param>
    public sealed record SignatureBlock(string Algorithm, string KeyId, string ValueB64);

    /// <summary>The signed payload. <c>createdUtc</c> is never examined by the contract and is not modelled.</summary>
    public sealed record Payload(
        string PackageId,
        string PackageVersion,
        string PackageSha256,
        string Producer
    );

    /// <summary>
    /// Parses a document. False unless it is a JSON object with an integer <c>version</c> equal to
    /// <see cref="SupportedVersion"/>, a non-blank string <c>payloadB64</c>, and a <c>signature</c> object whose
    /// <c>algorithm</c>, <c>keyId</c>, and <c>valueB64</c> are strings. Text that is not well-formed Unicode
    /// (an unpaired surrogate escape or character) is not a document. Unknown members are ignored.
    /// </summary>
    public static bool TryParse(string json, [NotNullWhen(true)] out TemplateAttestation? attestation)
    {
        ArgumentNullException.ThrowIfNull(json);
        attestation = null;
        if (!TrustContract.TryParse(json, out JsonDocument? document, out _))
        {
            return false;
        }

        using (document)
        {
            attestation = ReadDocument(document.RootElement);
            return attestation is not null;
        }
    }

    /// <summary>
    /// Parses the decoded payload bytes. False unless they are UTF-8 JSON for an object with an integer
    /// <c>attestationVersion</c> equal to <see cref="SupportedVersion"/> and string <c>packageId</c>,
    /// <c>packageVersion</c>, <c>packageSha256</c>, and <c>producer</c> members. Text that is not well-formed
    /// Unicode (an unpaired surrogate escape or invalid UTF-8 bytes) is not a payload. Unknown members are ignored.
    /// </summary>
    public static bool TryParsePayload(
        ReadOnlyMemory<byte> payloadBytes,
        [NotNullWhen(true)] out Payload? payload
    )
    {
        payload = null;
        if (!TrustContract.TryParse(payloadBytes, out JsonDocument? document, out _))
        {
            return false;
        }

        using (document)
        {
            payload = ReadPayload(document.RootElement);
            return payload is not null;
        }
    }

    private static TemplateAttestation? ReadDocument(JsonElement root)
    {
        if (!TrustContract.TryGetInteger(root, "version", out int version) || version != SupportedVersion)
        {
            return null;
        }

        if (
            !TrustContract.TryGetString(root, "payloadB64", out string? payloadB64)
            || string.IsNullOrWhiteSpace(payloadB64)
        )
        {
            return null;
        }

        if (!root.TryGetProperty("signature", out JsonElement signature))
        {
            return null;
        }

        if (
            !TrustContract.TryGetString(signature, "algorithm", out string? algorithm)
            || !TrustContract.TryGetString(signature, "keyId", out string? keyId)
            || !TrustContract.TryGetString(signature, "valueB64", out string? valueB64)
        )
        {
            return null;
        }

        return new TemplateAttestation(payloadB64, new SignatureBlock(algorithm, keyId, valueB64));
    }

    private static Payload? ReadPayload(JsonElement root)
    {
        if (
            !TrustContract.TryGetInteger(root, "attestationVersion", out int version)
            || version != SupportedVersion
        )
        {
            return null;
        }

        if (
            !TrustContract.TryGetString(root, "packageId", out string? packageId)
            || !TrustContract.TryGetString(root, "packageVersion", out string? packageVersion)
            || !TrustContract.TryGetString(root, "packageSha256", out string? packageSha256)
            || !TrustContract.TryGetString(root, "producer", out string? producer)
        )
        {
            return null;
        }

        return new Payload(packageId, packageVersion, packageSha256, producer);
    }
}
