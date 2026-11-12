// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Trust;

/// <summary>
/// An in-memory producer: an ephemeral ECDSA key plus the policy entry and attestation documents the delivered
/// PowerShell tooling writes for one (same member names, same compact payload, same P1363 signature). Every
/// document is built from parts so a test can alter exactly one of them, and base64 is written with literal
/// <c>+</c> and <c>/</c> as the tooling writes it (the default encoder would escape <c>+</c>, making text
/// replacement of a base64 value miss at random). Dispose after arranging; nothing it returns depends on the key
/// staying alive. Public, like <c>TestTemplatePackageBuilder</c>, so the engine integration projects can share
/// one producer across the packages of a catalog.
/// </summary>
public sealed class EphemeralAttestationSigner : IDisposable
{
    public const string Algorithm = "ECDSA_P256_SHA256";

    private static readonly JsonSerializerOptions _literalBase64 = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ECDsa _ecdsa;

    public EphemeralAttestationSigner(string producerName, ECCurve? curve = null)
    {
        _ecdsa = ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        ProducerName = producerName;
        byte[] subjectPublicKeyInfo = _ecdsa.ExportSubjectPublicKeyInfo();
        PublicKeySpkiB64 = Convert.ToBase64String(subjectPublicKeyInfo);
        KeyId = Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));
    }

    public string ProducerName { get; }

    public string KeyId { get; }

    public string PublicKeySpkiB64 { get; }

    public TemplateTrustPolicy.PublicKey PolicyKey => new(KeyId, Algorithm, PublicKeySpkiB64);

    public TemplateTrustPolicy.Producer Producer =>
        new(ProducerName, TemplateTrustPolicy.DetachedAttestationProvider, [PolicyKey]);

    public TemplateTrustPolicy Policy => new([Producer]);

    /// <summary>The producer entry in the tracked policy schema, with any one member overridden.</summary>
    public string ProducerJson(
        string? keyId = null,
        string algorithm = Algorithm,
        string? publicKeySpkiB64 = null
    ) =>
        JsonSerializer.Serialize(
            new
            {
                name = ProducerName,
                provider = TemplateTrustPolicy.DetachedAttestationProvider,
                publicKeys = new[]
                {
                    new
                    {
                        keyId = keyId ?? KeyId,
                        algorithm,
                        publicKeySpkiB64 = publicKeySpkiB64 ?? PublicKeySpkiB64,
                    },
                },
            },
            _literalBase64
        );

    public string PolicyJson() => $$"""{ "version": 1, "producers": [ {{ProducerJson()}} ] }""";

    /// <summary>The compact payload <c>New-TemplateAttestation</c> signs.</summary>
    public static byte[] Payload(
        string packageId,
        string packageVersion,
        string packageSha256,
        string producer
    ) =>
        Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                new
                {
                    attestationVersion = 1,
                    packageId,
                    packageVersion,
                    packageSha256,
                    producer,
                    createdUtc = "2026-11-05T00:00:00.0000000Z",
                },
                _literalBase64
            )
        );

    public byte[] Sign(byte[] payloadBytes) =>
        _ecdsa.SignData(
            payloadBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );

    /// <summary>The attestation document, with any one signature member overridden.</summary>
    public string Document(
        byte[] payloadBytes,
        byte[] signatureBytes,
        string algorithm = Algorithm,
        string? keyId = null
    ) =>
        JsonSerializer.Serialize(
            new
            {
                version = 1,
                payloadB64 = Convert.ToBase64String(payloadBytes),
                signature = new
                {
                    algorithm,
                    keyId = keyId ?? KeyId,
                    valueB64 = Convert.ToBase64String(signatureBytes),
                },
            },
            _literalBase64
        );

    /// <summary>A well-formed attestation over the given identity and hash, naming this producer unless overridden.</summary>
    public string Attest(
        string packageId,
        string packageVersion,
        string packageSha256,
        string? producer = null
    )
    {
        byte[] payloadBytes = Payload(packageId, packageVersion, packageSha256, producer ?? ProducerName);
        return Document(payloadBytes, Sign(payloadBytes));
    }

    public void Dispose() => _ecdsa.Dispose();
}
