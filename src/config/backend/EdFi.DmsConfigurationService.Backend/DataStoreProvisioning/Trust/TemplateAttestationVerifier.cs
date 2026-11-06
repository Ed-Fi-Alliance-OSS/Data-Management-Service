// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

/// <summary>
/// Verifies a detached attestation against the exact package bytes' SHA-256, the allowlisted package identity,
/// and the trust policy, in exactly the delivered <c>Test-TemplateAttestation</c> order (F-P6): at least one
/// detached-attestation producer; the document parses with integer version 1; the algorithm is exactly
/// <c>ECDSA_P256_SHA256</c>; payload and signature decode from base64; the key id matches a policy key
/// (case-sensitively, first match in policy order); the key is proven on P-256 and verifies the P1363 signature
/// over the raw payload bytes; the payload parses with integer version 1 and a lowercase 64-hex package hash;
/// the hash equals the caller's; the package id and version equal the allowlisted identity ignoring case; the
/// producer equals the matched producer's name ignoring case. <c>createdUtc</c> is never examined. Every defect
/// is an <see cref="TrustVerdict.Untrusted"/> verdict naming the first failing check, so callers fail closed.
/// Two caller defects throw instead of yielding a verdict about the package: a <c>packageSha256</c> argument that
/// is not 64 lowercase hex characters (<see cref="ArgumentException"/>), and a matched policy key that is not an
/// importable NIST P-256 key (<see cref="TemplateTrustPolicyException"/>; the reader never produces one, but a
/// policy constructed another way must not bypass the contract).
/// </summary>
public static class TemplateAttestationVerifier
{
    /// <param name="attestationJson">The attestation document text; null or blank is <see cref="TrustRefusalReason.Malformed"/>.</param>
    /// <param name="packageSha256">Lowercase hex SHA-256 of the exact package bytes being judged.</param>
    /// <param name="expectedPackageId">The allowlisted package id.</param>
    /// <param name="expectedPackageVersion">The allowlisted package version.</param>
    /// <param name="policy">The operator trust policy.</param>
    public static TrustVerdict Verify(
        string? attestationJson,
        string packageSha256,
        string expectedPackageId,
        string expectedPackageVersion,
        TemplateTrustPolicy policy
    )
    {
        ArgumentNullException.ThrowIfNull(packageSha256);
        if (!TrustContract.IsLowercaseSha256Hex(packageSha256))
        {
            throw new ArgumentException(
                "The package hash must be the 64-character lowercase hex SHA-256 of the exact package bytes.",
                nameof(packageSha256)
            );
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageVersion);
        ArgumentNullException.ThrowIfNull(policy);

        TemplateTrustPolicy.Producer[] producers = [.. policy.DetachedAttestationProducers];
        if (producers.Length == 0)
        {
            return new TrustVerdict.Untrusted(TrustRefusalReason.NoProducers);
        }

        (TrustRefusalReason? refusal, SignedPayload? signed) = ReadSignedPayload(attestationJson, producers);
        if (refusal is { } documentRefusal)
        {
            return new TrustVerdict.Untrusted(documentRefusal);
        }

        return JudgePayload(signed!, packageSha256, expectedPackageId, expectedPackageVersion);
    }

    /// <summary>F-P6 checks on the document: shape, algorithm, base64, key lookup, and signature.</summary>
    private static (TrustRefusalReason? Refusal, SignedPayload? Signed) ReadSignedPayload(
        string? attestationJson,
        TemplateTrustPolicy.Producer[] producers
    )
    {
        if (
            string.IsNullOrWhiteSpace(attestationJson)
            || !TemplateAttestation.TryParse(attestationJson, out TemplateAttestation? attestation)
        )
        {
            return (TrustRefusalReason.Malformed, null);
        }

        if (attestation.Signature.Algorithm != TemplateTrustPolicy.EcdsaP256Sha256Algorithm)
        {
            return (TrustRefusalReason.Malformed, null);
        }

        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(attestation.PayloadB64);
            signatureBytes = Convert.FromBase64String(attestation.Signature.ValueB64);
        }
        catch (FormatException)
        {
            return (TrustRefusalReason.Malformed, null);
        }

        KeyMatch? match = FindKey(producers, attestation.Signature.KeyId);
        if (match is null)
        {
            return (TrustRefusalReason.UnknownKey, null);
        }

        return SignatureVerifies(match.Key, payloadBytes, signatureBytes)
            ? (null, new SignedPayload(payloadBytes, match.Producer))
            : (TrustRefusalReason.BadSignature, null);
    }

    /// <summary>F-P6 checks on the verified payload: shape, hash form, hash equality, identity, producer.</summary>
    private static TrustVerdict JudgePayload(
        SignedPayload signed,
        string packageSha256,
        string expectedPackageId,
        string expectedPackageVersion
    )
    {
        if (
            !TemplateAttestation.TryParsePayload(
                signed.PayloadBytes,
                out TemplateAttestation.Payload? payload
            ) || !TrustContract.IsLowercaseSha256Hex(payload.PackageSha256)
        )
        {
            // A signed uppercase spelling is rejected rather than normalized into acceptance.
            return new TrustVerdict.Untrusted(TrustRefusalReason.Malformed);
        }

        if (payload.PackageSha256 != packageSha256)
        {
            return new TrustVerdict.Untrusted(TrustRefusalReason.PackageHashMismatch);
        }

        if (
            !SameIgnoringCase(payload.PackageId, expectedPackageId)
            || !SameIgnoringCase(payload.PackageVersion, expectedPackageVersion)
        )
        {
            return new TrustVerdict.Untrusted(TrustRefusalReason.IdentityMismatch);
        }

        if (!SameIgnoringCase(payload.Producer, signed.Producer.Name))
        {
            return new TrustVerdict.Untrusted(TrustRefusalReason.ProducerMismatch);
        }

        return new TrustVerdict.Trusted(signed.Producer.Name);
    }

    private static KeyMatch? FindKey(TemplateTrustPolicy.Producer[] producers, string keyId)
    {
        foreach (TemplateTrustPolicy.Producer producer in producers)
        {
            foreach (TemplateTrustPolicy.PublicKey key in producer.PublicKeys)
            {
                if (key.KeyId == keyId)
                {
                    return new KeyMatch(producer, key);
                }
            }
        }

        return null;
    }

    private static bool SignatureVerifies(
        TemplateTrustPolicy.PublicKey key,
        byte[] payloadBytes,
        byte[] signatureBytes
    )
    {
        const string Description = "The trust policy key matched by the attestation";
        byte[] subjectPublicKeyInfo;
        try
        {
            subjectPublicKeyInfo = Convert.FromBase64String(key.PublicKeySpkiB64);
        }
        catch (FormatException)
        {
            throw new TemplateTrustPolicyException(
                $"{Description} has a 'publicKeySpkiB64' that is not valid base64."
            );
        }

        using ECDsa ecdsa = EcdsaP256.ImportPublicKey(subjectPublicKeyInfo, Description);
        return ecdsa.VerifyData(
            payloadBytes,
            signatureBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
    }

    private static bool SameIgnoringCase(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private sealed record KeyMatch(TemplateTrustPolicy.Producer Producer, TemplateTrustPolicy.PublicKey Key);

    private sealed record SignedPayload(byte[] PayloadBytes, TemplateTrustPolicy.Producer Producer);
}
