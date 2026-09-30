// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// One public signing key in a <see cref="SigningKeySnapshot"/>: its key id and RSA public parameters. The
/// authoritative material is private and never handed out. Every projection is a detached copy: the JWKS parameters
/// (<see cref="PublicParameters"/>) and the validation key (<see cref="CreateSecurityKey"/>). A caller that changes a
/// projection, including a <see cref="SecurityKey"/>'s mutable <see cref="SecurityKey.KeyId"/> or its RSA arrays,
/// changes only its own copy, never the entry, later projections, or <see cref="SigningKeySnapshot.ContainsKeyId"/>.
/// </summary>
public sealed class SigningKeyEntry
{
    private readonly byte[] _modulus;
    private readonly byte[] _exponent;

    private SigningKeyEntry(string keyId, byte[] modulus, byte[] exponent)
    {
        KeyId = keyId;
        _modulus = modulus;
        _exponent = exponent;
    }

    /// <summary>The key id tokens reference in their <c>kid</c> header.</summary>
    public string KeyId { get; }

    /// <summary>A copy of the public parameters (modulus and exponent only).</summary>
    public RSAParameters PublicParameters =>
        new() { Modulus = (byte[])_modulus.Clone(), Exponent = (byte[])_exponent.Clone() };

    /// <summary>
    /// Creates a new, detached validation key carrying <see cref="KeyId"/>. Each call returns a new instance built from
    /// fresh copies of the parameters. A consumer that reuses one set of keys per snapshot version (the configuration
    /// manager, spec §4.3.7) creates them once and owns them.
    /// </summary>
    public SecurityKey CreateSecurityKey() => new RsaSecurityKey(PublicParameters) { KeyId = KeyId };

    /// <summary>
    /// Creates an entry from RSA public parameters.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The key id is empty, the modulus or exponent is missing, or the parameters carry private-key material. A snapshot
    /// holds public keys only, because it is what the JWKS endpoint publishes.
    /// </exception>
    public static SigningKeyEntry FromRsaPublicParameters(string keyId, RSAParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("A signing key must have a key id.", nameof(keyId));
        }

        if (parameters.Modulus is not { Length: > 0 } || parameters.Exponent is not { Length: > 0 })
        {
            throw new ArgumentException(
                "A signing key must have a modulus and an exponent.",
                nameof(parameters)
            );
        }

        if (
            parameters.D is not null
            || parameters.P is not null
            || parameters.Q is not null
            || parameters.DP is not null
            || parameters.DQ is not null
            || parameters.InverseQ is not null
        )
        {
            throw new ArgumentException(
                "A signing key entry must not carry private-key material.",
                nameof(parameters)
            );
        }

        return new SigningKeyEntry(
            keyId,
            (byte[])parameters.Modulus.Clone(),
            (byte[])parameters.Exponent.Clone()
        );
    }
}
