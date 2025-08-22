// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services;

/// <summary>
/// Implementation of client secret hashing using a custom password hasher.
/// Uses dependency injection to allow for flexible password hashing implementations.
/// </summary>
public class ClientSecretHasher(
    ILogger<ClientSecretHasher> logger,
    IOptions<IdentityOptions> identityOptions) : IClientSecretHasher
{
    private readonly ILogger<ClientSecretHasher> _logger = logger;
    private readonly IOptions<IdentityOptions> _identityOptions = identityOptions;

    /// <summary>
    /// Hashes a plain-text client secret using a secure hashing algorithm.
    /// </summary>
    public Task<string> HashSecretAsync(string plainTextSecret)
    {
        if (string.IsNullOrEmpty(plainTextSecret))
        {
            _logger.LogWarning("Attempt to hash null or empty client secret");
            throw new ArgumentException("Secret cannot be null or empty", nameof(plainTextSecret));
        }

        _logger.LogDebug("Hashing client secret");

        const byte Version = 1;
        const int SaltLength = 16;
        const int SubkeyLength = 32;
        int iterations = _identityOptions.Value.HashingIterations;

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(
            plainTextSecret,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            SubkeyLength
        );

        using var memoryStream = new MemoryStream();
        using var writer = new BinaryWriter(memoryStream);

        writer.Write(Version);              // 1 byte
        writer.Write(SaltLength);           // 4 bytes
        writer.Write(salt);                 // 16 bytes
        writer.Write(subkey);               // 32 bytes

        writer.Flush();
        byte[] finalBytes = memoryStream.ToArray();
        var hashedSecret = Convert.ToBase64String(finalBytes);

        _logger.LogDebug("Client secret hashed successfully");
        return Task.FromResult(hashedSecret);
    }

    /// <summary>
    /// Verifies a plain-text secret against a stored hash.
    /// </summary>
    public Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret)
    {
        if (string.IsNullOrEmpty(plainTextSecret))
        {
            _logger.LogWarning("Attempt to verify null or empty client secret");
            return Task.FromResult(false);
        }

        if (string.IsNullOrEmpty(hashedSecret))
        {
            _logger.LogWarning("Attempt to verify against null or empty hashed secret");
            return Task.FromResult(false);
        }

        _logger.LogDebug("Verifying client secret");

        try
        {
            byte[] decoded = Convert.FromBase64String(hashedSecret);
            using var reader = new BinaryReader(new MemoryStream(decoded));

            byte version = reader.ReadByte();
            int saltLength = reader.ReadInt32();
            byte[] salt = reader.ReadBytes(saltLength);
            byte[] expectedSubkey = reader.ReadBytes(32);

            byte[] actualSubkey = Rfc2898DeriveBytes.Pbkdf2(
                plainTextSecret,
                salt,
                _identityOptions.Value.HashingIterations,
                HashAlgorithmName.SHA256,
                32
            );

            var result = CryptographicOperations.FixedTimeEquals(actualSubkey, expectedSubkey);
            _logger.LogDebug("Client secret verification result: {Result}", result);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error verifying client secret: {ErrorMessage}", ex.Message);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Determines if a secret appears to be hashed based on its format.
    /// </summary>
    public bool IsSecretHashed(string secret)
    {
        try
        {
            byte[] decoded = Convert.FromBase64String(secret);

            if (decoded.Length < 1 + 4 + 16 + 32)
            {
                return false;
            }

            using var reader = new BinaryReader(new MemoryStream(decoded));

            byte version = reader.ReadByte();
            if (version != 1)
            {
                return false;
            }

            int saltLength = reader.ReadInt32();
            if (saltLength <= 0 || saltLength > 64)
            {
                return false;
            }

            byte[] salt = reader.ReadBytes(saltLength);
            if (salt.Length != saltLength)
            {
                return false;
            }

            byte[] subkey = reader.ReadBytes(32);
            if (subkey.Length != 32)
            {
                return false;
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error verifying hash: {ErrorMessage}", ex.Message);
            return false;
        }
    }
}
