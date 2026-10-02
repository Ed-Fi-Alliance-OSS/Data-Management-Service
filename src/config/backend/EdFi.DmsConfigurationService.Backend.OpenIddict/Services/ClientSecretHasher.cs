// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services;

/// <summary>
/// Implementation of client secret hashing using a custom password hasher.
/// Uses dependency injection to allow for flexible password hashing implementations.
/// </summary>
public class ClientSecretHasher(ILogger<ClientSecretHasher> logger, IOptions<IdentityOptions> identityOptions)
    : IClientSecretHasher,
        IFailurePreservingSecretVerifier
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
        int iterations = _identityOptions.Value.ClientSecretHashingIterations;

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

        writer.Write(Version); // 1 byte
        writer.Write(SaltLength); // 4 bytes
        writer.Write(salt); // 16 bytes
        writer.Write(subkey); // 32 bytes

        writer.Flush();
        byte[] finalBytes = memoryStream.ToArray();
        var hashedSecret = Convert.ToBase64String(finalBytes);

        _logger.LogDebug("Client secret hashed successfully");
        return Task.FromResult(hashedSecret);
    }

    /// <summary>
    /// Verifies a plain-text secret against a stored hash. Any failure while verifying is logged and
    /// answered <c>false</c>, the same as a mismatch; the token endpoint relies on that. Revocation
    /// uses <see cref="VerifySecretPreservingFailuresAsync"/> instead.
    /// </summary>
    public Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret)
    {
        if (!HasVerifiableInputs(plainTextSecret, hashedSecret))
        {
            return Task.FromResult(false);
        }

        try
        {
            return Task.FromResult(SecretMatches(plainTextSecret, hashedSecret, requireCompleteHash: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error verifying client secret: {ErrorMessage}", ex.Message);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Verifies a plain-text secret against a stored hash, letting any failure that prevents the
    /// comparison escape as a faulted task instead of answering <c>false</c>. Nothing about the
    /// failure is logged here: the caller classifies it and logs exception type names only.
    /// </summary>
    public Task<bool> VerifySecretPreservingFailuresAsync(string plainTextSecret, string hashedSecret)
    {
        if (!HasVerifiableInputs(plainTextSecret, hashedSecret))
        {
            return Task.FromResult(false);
        }

        try
        {
            return Task.FromResult(SecretMatches(plainTextSecret, hashedSecret, requireCompleteHash: true));
        }
        catch (Exception ex)
        {
            return Task.FromException<bool>(ex);
        }
    }

    /// <summary>
    /// An empty presented secret, or an application with no stored secret, is a mismatch rather
    /// than a failure: no credential could ever verify against it.
    /// </summary>
    private bool HasVerifiableInputs(string plainTextSecret, string hashedSecret)
    {
        if (string.IsNullOrEmpty(plainTextSecret))
        {
            _logger.LogWarning("Attempt to verify null or empty client secret");
            return false;
        }

        if (string.IsNullOrEmpty(hashedSecret))
        {
            _logger.LogWarning("Attempt to verify against null or empty hashed secret");
            return false;
        }

        return true;
    }

    private const int SubkeyLength = 32;

    /// <summary>Derives the subkey at the configured iteration count and compares in fixed time. Throws when it cannot.</summary>
    /// <param name="requireCompleteHash">
    /// <c>true</c> for the failure-preserving path: a stored hash shorter than its declared salt plus
    /// the 32-byte subkey is structural corruption, not a mismatch, because <see cref="BinaryReader.ReadBytes"/>
    /// returns a short array instead of throwing and the fixed-time comparison of unequal lengths would
    /// otherwise answer <c>false</c>. <c>false</c> keeps the lenient path exactly as it was.
    /// </param>
    private bool SecretMatches(string plainTextSecret, string hashedSecret, bool requireCompleteHash)
    {
        _logger.LogDebug("Verifying client secret");

        byte[] decoded = Convert.FromBase64String(hashedSecret);
        using var reader = new BinaryReader(new MemoryStream(decoded));

        _ = reader.ReadByte(); // version byte — read past, value not used
        int saltLength = reader.ReadInt32();
        byte[] salt = reader.ReadBytes(saltLength);
        byte[] expectedSubkey = reader.ReadBytes(SubkeyLength);

        if (requireCompleteHash && (salt.Length != saltLength || expectedSubkey.Length != SubkeyLength))
        {
            // Fixed text: nothing from the stored value or the presented secret.
            throw new InvalidDataException("The stored client secret hash is incomplete.");
        }

        byte[] actualSubkey = Rfc2898DeriveBytes.Pbkdf2(
            plainTextSecret,
            salt,
            _identityOptions.Value.ClientSecretHashingIterations,
            HashAlgorithmName.SHA256,
            SubkeyLength
        );

        var result = CryptographicOperations.FixedTimeEquals(actualSubkey, expectedSubkey);
        _logger.LogDebug("Client secret verification result: {Result}", result);
        return result;
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
