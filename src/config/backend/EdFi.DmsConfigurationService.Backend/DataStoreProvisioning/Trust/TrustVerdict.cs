// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

/// <summary>
/// The result of <see cref="TemplateAttestationVerifier.Verify"/>: either the package bytes are attested by a
/// named policy producer, or the first F-P6 check that failed. A verdict never carries a key id, a hash, a
/// package identity, or any other value read from the document.
/// </summary>
public abstract record TrustVerdict
{
    private TrustVerdict() { }

    /// <summary>The attestation verified; <paramref name="Producer"/> is the producer's name as the policy spells it.</summary>
    public sealed record Trusted(string Producer) : TrustVerdict;

    public sealed record Untrusted(TrustRefusalReason Reason) : TrustVerdict;
}
