// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

/// <summary>
/// Parses and validates one trust policy document in the delivered schema (F-P5), applying every rule of the
/// delivered <c>Read-TrustPolicyDocument</c> plus the producer-name uniqueness rule of
/// <c>Read-TemplateTrustPolicy</c>: an integer <c>version</c> equal to 1; a <c>producers</c> array (empty allowed);
/// each producer a non-blank <c>name</c> and a known, case-sensitive <c>provider</c>; each
/// <c>detached-attestation</c> producer at least one key whose <c>keyId</c> is 64 lowercase hex characters equal
/// to the SHA-256 of its decoded <c>publicKeySpkiB64</c>, whose <c>algorithm</c> is exactly
/// <c>ECDSA_P256_SHA256</c>, and whose key imports and is proven on P-256; each <c>nuget-author-signature</c>
/// producer at least one 64-hex certificate fingerprint. Unknown members are ignored so the tracked policy's
/// comment member and a future producer entry load verbatim. A violated rule throws
/// <see cref="TemplateTrustPolicyException"/> naming the rule and the entry position, never a value.
/// </summary>
public static class TemplateTrustPolicyReader
{
    public static TemplateTrustPolicy Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TemplateTrustPolicyException("The trust policy is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, TrustContract.DocumentOptions);
        }
        catch (JsonException)
        {
            throw new TemplateTrustPolicyException("The trust policy is not valid JSON.");
        }

        using (document)
        {
            return ReadDocument(document.RootElement);
        }
    }

    private static TemplateTrustPolicy ReadDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new TemplateTrustPolicyException("The trust policy must be a JSON object.");
        }

        if (!TrustContract.TryGetInteger(root, "version", out int version))
        {
            throw new TemplateTrustPolicyException("The trust policy must declare an integer 'version'.");
        }

        if (version != TemplateTrustPolicy.SupportedVersion)
        {
            throw new TemplateTrustPolicyException(
                $"The trust policy declares an unsupported version; only version {TemplateTrustPolicy.SupportedVersion} is supported."
            );
        }

        if (
            !root.TryGetProperty("producers", out JsonElement producersElement)
            || producersElement.ValueKind == JsonValueKind.Null
        )
        {
            throw new TemplateTrustPolicyException(
                "The trust policy must declare a 'producers' array (empty is allowed and means no trust anchors)."
            );
        }

        if (producersElement.ValueKind != JsonValueKind.Array)
        {
            throw new TemplateTrustPolicyException(
                "The trust policy 'producers' member must be a JSON array, not a single object or scalar."
            );
        }

        List<TemplateTrustPolicy.Producer> producers = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement producerElement in producersElement.EnumerateArray())
        {
            int index = producers.Count;
            TemplateTrustPolicy.Producer producer = ReadProducer(producerElement, index);
            if (!names.Add(producer.Name))
            {
                throw new TemplateTrustPolicyException(
                    $"Trust policy producer {index} repeats the name of an earlier producer; producer names must be unique ignoring case."
                );
            }

            producers.Add(producer);
        }

        return new TemplateTrustPolicy(producers);
    }

    private static TemplateTrustPolicy.Producer ReadProducer(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new TemplateTrustPolicyException($"Trust policy producer {index} is not a JSON object.");
        }

        if (!TrustContract.TryGetString(element, "name", out string? name) || string.IsNullOrWhiteSpace(name))
        {
            throw new TemplateTrustPolicyException(
                $"Trust policy producer {index} has no non-empty string 'name'."
            );
        }

        if (!TrustContract.TryGetString(element, "provider", out string? provider))
        {
            throw new TemplateTrustPolicyException(
                $"Trust policy producer {index} must declare a string 'provider'."
            );
        }

        if (provider == TemplateTrustPolicy.DetachedAttestationProvider)
        {
            return new TemplateTrustPolicy.Producer(name, provider, ReadPublicKeys(element, index));
        }

        if (provider == TemplateTrustPolicy.NugetAuthorSignatureProvider)
        {
            // Schema-reserved: the entry validates so a policy can be authored ahead of provider support,
            // but no verification path consumes it.
            ValidateCertificateFingerprints(element, index);
            return new TemplateTrustPolicy.Producer(name, provider, []);
        }

        throw new TemplateTrustPolicyException(
            $"Trust policy producer {index} declares an unknown provider; known providers are '{TemplateTrustPolicy.DetachedAttestationProvider}' and '{TemplateTrustPolicy.NugetAuthorSignatureProvider}'."
        );
    }

    private static IReadOnlyList<TemplateTrustPolicy.PublicKey> ReadPublicKeys(
        JsonElement producer,
        int index
    )
    {
        JsonElement keys = RequireNonEmptyArray(producer, "publicKeys", index);
        List<TemplateTrustPolicy.PublicKey> result = [];
        foreach (JsonElement key in keys.EnumerateArray())
        {
            result.Add(ReadPublicKey(key, index, result.Count));
        }

        return result;
    }

    private static TemplateTrustPolicy.PublicKey ReadPublicKey(
        JsonElement element,
        int producerIndex,
        int keyIndex
    )
    {
        string position = $"Trust policy producer {producerIndex} key {keyIndex}";
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new TemplateTrustPolicyException($"{position} is not a JSON object.");
        }

        if (
            !TrustContract.TryGetString(element, "keyId", out string? keyId)
            || !TrustContract.IsLowercaseSha256Hex(keyId)
        )
        {
            throw new TemplateTrustPolicyException($"{position} has no 64-character lowercase hex 'keyId'.");
        }

        if (
            !TrustContract.TryGetString(element, "algorithm", out string? algorithm)
            || algorithm != TemplateTrustPolicy.EcdsaP256Sha256Algorithm
        )
        {
            throw new TemplateTrustPolicyException(
                $"{position} declares an unsupported 'algorithm'; only {TemplateTrustPolicy.EcdsaP256Sha256Algorithm} is supported."
            );
        }

        if (!TrustContract.TryGetString(element, "publicKeySpkiB64", out string? publicKeySpkiB64))
        {
            throw new TemplateTrustPolicyException($"{position} has no string 'publicKeySpkiB64'.");
        }

        byte[] subjectPublicKeyInfo;
        try
        {
            subjectPublicKeyInfo = Convert.FromBase64String(publicKeySpkiB64);
        }
        catch (FormatException)
        {
            throw new TemplateTrustPolicyException(
                $"{position} has a 'publicKeySpkiB64' that is not valid base64."
            );
        }

        if (Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo)) != keyId)
        {
            throw new TemplateTrustPolicyException(
                $"{position} has a 'keyId' that does not match the SHA-256 of its own 'publicKeySpkiB64'; the entry is corrupt or mispasted."
            );
        }

        // Prove importability and the curve at load time so a malformed or off-curve key fails here with
        // policy context rather than at verification time.
        EcdsaP256.ImportPublicKey(subjectPublicKeyInfo, position).Dispose();
        return new TemplateTrustPolicy.PublicKey(keyId, algorithm, publicKeySpkiB64);
    }

    private static void ValidateCertificateFingerprints(JsonElement producer, int index)
    {
        JsonElement fingerprints = RequireNonEmptyArray(producer, "certificateFingerprints", index);
        foreach (JsonElement fingerprint in fingerprints.EnumerateArray())
        {
            if (
                fingerprint.ValueKind != JsonValueKind.String
                || !TrustContract.IsLowercaseSha256Hex(fingerprint.GetString()!.ToLowerInvariant())
            )
            {
                throw new TemplateTrustPolicyException(
                    $"Trust policy producer {index} contains a certificate fingerprint that is not a 64-character hex SHA-256."
                );
            }
        }
    }

    private static JsonElement RequireNonEmptyArray(JsonElement producer, string member, int index)
    {
        if (producer.TryGetProperty(member, out JsonElement array) && array.ValueKind != JsonValueKind.Null)
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                throw new TemplateTrustPolicyException(
                    $"Trust policy producer {index} '{member}' must be a JSON array, not a single object or scalar."
                );
            }

            if (array.GetArrayLength() > 0)
            {
                return array;
            }
        }

        throw new TemplateTrustPolicyException(
            $"Trust policy producer {index} declares no '{member}', which its provider requires."
        );
    }
}
