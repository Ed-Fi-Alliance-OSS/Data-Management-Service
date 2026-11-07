// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

/// <summary>
/// The operator trust policy in the delivered DMS-1271 schema (F-P5): a version-1 document listing producers,
/// each either a <c>detached-attestation</c> producer with one or more ECDSA P-256 public keys, or a
/// <c>nuget-author-signature</c> producer, which the schema reserves and no verification path consults.
/// Producer names are unique ignoring case. <see cref="TemplateTrustPolicyReader"/> is the only path that proves
/// every key importable and on P-256; the verifier re-proves the key it matches so a policy built another way
/// cannot bypass the contract. The CMS reads one file (<c>TrustPolicyPath</c>); there is no overlay merge.
/// </summary>
public sealed record TemplateTrustPolicy
{
    public const int SupportedVersion = 1;

    public const string DetachedAttestationProvider = "detached-attestation";

    public const string NugetAuthorSignatureProvider = "nuget-author-signature";

    public const string EcdsaP256Sha256Algorithm = "ECDSA_P256_SHA256";

    public TemplateTrustPolicy(IReadOnlyList<Producer> producers)
    {
        ArgumentNullException.ThrowIfNull(producers);
        Producers = producers;
    }

    /// <summary>Every producer in document order, whichever provider it declares.</summary>
    public IReadOnlyList<Producer> Producers { get; }

    /// <summary>The producers verification consults, in document order; none means every package fails closed.</summary>
    public IEnumerable<Producer> DetachedAttestationProducers =>
        Producers.Where(producer => producer.Provider == DetachedAttestationProvider);

    /// <param name="Name">The producer name an attestation payload must carry (compared ignoring case).</param>
    /// <param name="Provider">One of the two provider tokens, exactly as the schema spells it.</param>
    /// <param name="PublicKeys">The producer's keys; empty for a reserved <c>nuget-author-signature</c> producer.</param>
    public sealed record Producer(string Name, string Provider, IReadOnlyList<PublicKey> PublicKeys);

    /// <param name="KeyId">Lowercase hex SHA-256 of the DER SubjectPublicKeyInfo bytes; matched case-sensitively.</param>
    /// <param name="Algorithm">Exactly <see cref="EcdsaP256Sha256Algorithm"/>.</param>
    /// <param name="PublicKeySpkiB64">Base64 DER SubjectPublicKeyInfo of a NIST P-256 key.</param>
    public sealed record PublicKey(string KeyId, string Algorithm, string PublicKeySpkiB64);
}
