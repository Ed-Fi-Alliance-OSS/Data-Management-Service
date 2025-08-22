// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;

/// <summary>
/// Repository interface for managing OpenIddict tokens and related data.
/// </summary>
public interface IOpenIddictTokenRepository
{
    /// <summary>
    /// Retrieves token information by token ID.
    /// </summary>
    /// <param name="tokenId">The token ID to search for.</param>
    /// <returns>Token information if found, null otherwise.</returns>
    Task<TokenInfo?> GetTokenByIdAsync(Guid tokenId);

    /// <summary>
    /// Stores a new token in the repository.
    /// </summary>
    /// <param name="tokenId">The unique identifier for the token.</param>
    /// <param name="applicationId">The application ID associated with the token.</param>
    /// <param name="subject">The subject of the token.</param>
    /// <param name="payload">The token payload.</param>
    /// <param name="expiration">The token expiration date.</param>
    Task StoreTokenAsync(Guid tokenId, Guid applicationId, string subject, string payload, DateTimeOffset expiration);

    /// <summary>
    /// Gets the status of a token by its ID.
    /// </summary>
    /// <param name="tokenId">The token ID.</param>
    /// <returns>The token status if found, null otherwise.</returns>
    Task<string?> GetTokenStatusAsync(Guid tokenId);

    /// <summary>
    /// Revokes a token by its ID.
    /// </summary>
    /// <param name="tokenId">The token ID to revoke.</param>
    /// <returns>True if the token was successfully revoked, false otherwise.</returns>
    Task<bool> RevokeTokenAsync(Guid tokenId);

    /// <summary>
    /// Gets the active private key for encryption.
    /// </summary>
    /// <param name="encryptionKey">The encryption key.</param>
    /// <returns>The private key information if found, null otherwise.</returns>
    Task<PrivateKeyInfo?> GetActivePrivateKeyAsync(string encryptionKey);

    /// <summary>
    /// Gets all active public keys.
    /// </summary>
    /// <returns>Collection of public key information.</returns>
    Task<IEnumerable<PublicKeyInfo>> GetActivePublicKeysAsync();

    /// <summary>
    /// Gets application information by client ID.
    /// </summary>
    /// <param name="clientId">The client ID to search for.</param>
    /// <returns>Application information if found, null otherwise.</returns>
    Task<ApplicationInfo?> GetApplicationByClientIdAsync(string clientId);

    /// <summary>
    /// Gets the roles associated with a client.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <returns>Collection of role names.</returns>
    Task<IEnumerable<string>> GetClientRolesAsync(Guid clientId);
}
