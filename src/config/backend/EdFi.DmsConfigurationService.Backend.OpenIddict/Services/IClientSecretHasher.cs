// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services;

/// <summary>
/// Provides client secret hashing capabilities using OpenIddict's built-in security features.
/// This interface abstracts OpenIddict's IOpenIddictApplicationManager for secure client secret handling.
/// </summary>
public interface IClientSecretHasher
{
    /// <summary>
    /// Hashes a plain-text client secret using OpenIddict's secure hashing algorithm.
    /// </summary>
    /// <param name="plainTextSecret">The plain-text secret to hash</param>
    /// <returns>The hashed secret suitable for database storage</returns>
    Task<string> HashSecretAsync(string plainTextSecret);

    /// <summary>
    /// Verifies a plain-text secret against a stored hash using OpenIddict's verification logic.
    /// </summary>
    /// <param name="plainTextSecret">The plain-text secret to verify</param>
    /// <param name="hashedSecret">The stored hash to verify against</param>
    /// <returns>True if the secret matches the hash, false otherwise</returns>
    Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret);

    /// <summary>
    /// Determines if a stored secret value is already hashed or is plain text.
    /// </summary>
    /// <param name="secret">The secret value to check</param>
    /// <returns>True if the secret appears to be hashed, false if plain text</returns>
    bool IsSecretHashed(string secret);
}
