// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// One public signing key in a <see cref="SigningKeySnapshot"/>: its key id, the RSA public parameters (the JWKS
/// projection), and the <see cref="SecurityKey"/> token validation uses. Immutable: the parameters are copied on the
/// way in and on the way out, so no caller can alter a published key.
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
        SecurityKey = new RsaSecurityKey(
            new RSAParameters { Modulus = (byte[])modulus.Clone(), Exponent = (byte[])exponent.Clone() }
        )
        {
            KeyId = keyId,
        };
    }

    /// <summary>The key id tokens reference in their <c>kid</c> header.</summary>
    public string KeyId { get; }

    /// <summary>The key as token validation consumes it, carrying <see cref="KeyId"/>.</summary>
    public SecurityKey SecurityKey { get; }

    /// <summary>A copy of the public parameters (modulus and exponent only).</summary>
    public RSAParameters PublicParameters =>
        new() { Modulus = (byte[])_modulus.Clone(), Exponent = (byte[])_exponent.Clone() };

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
