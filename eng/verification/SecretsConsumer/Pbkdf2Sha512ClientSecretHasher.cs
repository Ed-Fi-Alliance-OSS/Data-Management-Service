// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Secrets;

namespace SecretsConsumer;

/// <summary>
/// An implementer-authored <see cref="IClientSecretHasher"/> replacing the host's
/// PBKDF2-SHA256 default with PBKDF2-SHA512 at the implementer's own work factor, the reason an
/// operator would replace the hasher at all. The stored form is the salt followed by the subkey.
/// This is a verification fixture, compiled but never run.
/// </summary>
public sealed class Pbkdf2Sha512ClientSecretHasher : IClientSecretHasher
{
    private const int SaltLength = 16;
    private const int SubkeyLength = 64;
    private const int Iterations = 600000;

    public Task<string> HashSecretAsync(string plainTextSecret)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] subkey = Derive(plainTextSecret, salt);

        return Task.FromResult(Convert.ToBase64String([.. salt, .. subkey]));
    }

    public Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret)
    {
        if (!IsSecretHashed(hashedSecret))
        {
            return Task.FromResult(false);
        }

        byte[] decoded = Convert.FromBase64String(hashedSecret);
        byte[] actual = Derive(plainTextSecret, decoded[..SaltLength]);

        return Task.FromResult(CryptographicOperations.FixedTimeEquals(actual, decoded[SaltLength..]));
    }

    public bool IsSecretHashed(string secret)
    {
        Span<byte> buffer = stackalloc byte[SaltLength + SubkeyLength];
        return Convert.TryFromBase64String(secret, buffer, out int written)
            && written == SaltLength + SubkeyLength;
    }

    private static byte[] Derive(string plainTextSecret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(plainTextSecret, salt, Iterations, HashAlgorithmName.SHA512, SubkeyLength);
}
