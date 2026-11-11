// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

/// <summary>
/// Why a detached attestation did not verify. Each value names the first attestation check that
/// failed; the catalog reports it as the detail of <see cref="DmsProvisioningFailureCode.ArtifactUntrusted"/>.
/// No value carries anything read from the document, the package, or the policy.
/// </summary>
public enum TrustRefusalReason
{
    /// <summary>The policy lists no detached-attestation producer, so no package can be trusted (fail closed).</summary>
    NoProducers,

    /// <summary>The document, its signature block, or its signed payload is not in the version-1 shape.</summary>
    Malformed,

    /// <summary>The signature names a key id that no producer in the policy holds.</summary>
    UnknownKey,

    /// <summary>The signature does not verify over the payload bytes with the matched key.</summary>
    BadSignature,

    /// <summary>The signed package hash differs from the hash of the package bytes.</summary>
    PackageHashMismatch,

    /// <summary>The signed package id or version differs from the allowlisted identity.</summary>
    IdentityMismatch,

    /// <summary>The signed producer name differs from the producer that holds the signing key.</summary>
    ProducerMismatch,
}
