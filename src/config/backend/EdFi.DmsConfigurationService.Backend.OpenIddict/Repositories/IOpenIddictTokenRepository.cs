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
    /// Stores a new token in the repository, unless the application already holds the maximum
    /// number of active tokens. Every store, whatever <paramref name="maxActiveTokens"/> is, locks
    /// the application row and then, in a separate statement, re-reads the stored client secret and
    /// the approval, so a credential reset, deletion or un-approval that commits first is never
    /// answered with a stored token.
    /// </summary>
    /// <param name="tokenId">The unique identifier for the token.</param>
    /// <param name="applicationId">The application ID associated with the token.</param>
    /// <param name="subject">The subject of the token.</param>
    /// <param name="expiration">The token expiration date.</param>
    /// <param name="maxActiveTokens">
    /// The maximum number of simultaneously active tokens the application may hold. Any value
    /// below 1 disables the count, but not the lock or the re-validation.
    /// </param>
    /// <param name="expectedClientSecret">
    /// The stored secret hash the grant authenticated against. It must still equal the stored
    /// value, compared ordinally, under the lock.
    /// </param>
    /// <returns>
    /// <see cref="TokenStoreOutcome.Stored"/> when the token was written,
    /// <see cref="TokenStoreOutcome.LimitExceeded"/> when the application already holds at least
    /// <paramref name="maxActiveTokens"/> active tokens,
    /// <see cref="TokenStoreOutcome.ClientNotFound"/> when the application no longer exists,
    /// <see cref="TokenStoreOutcome.ClientChanged"/> when its stored secret no longer matches
    /// <paramref name="expectedClientSecret"/> or the client is no longer approved, or
    /// <see cref="TokenStoreOutcome.LockTimeout"/> when a database lock wait during the store ran
    /// out, whether behind a competing grant for this application or another writer such as the
    /// expired-token sweep, or the store was chosen as a deadlock victim of such a writer.
    /// </returns>
    Task<TokenStoreOutcome> StoreTokenAsync(
        Guid tokenId,
        Guid applicationId,
        string subject,
        DateTimeOffset expiration,
        int maxActiveTokens,
        string expectedClientSecret
    );

    /// <summary>
    /// Gets the status of a token by its ID.
    /// </summary>
    /// <param name="tokenId">The token ID.</param>
    /// <returns>The token status if found, null otherwise.</returns>
    Task<string?> GetTokenStatusAsync(Guid tokenId);

    /// <summary>
    /// Gets the status of a token only when the token is stored for <paramref name="applicationId"/>.
    /// </summary>
    /// <param name="tokenId">The token ID.</param>
    /// <param name="applicationId">The application the token must be stored for.</param>
    /// <returns>The token status when it is stored for that application, null otherwise.</returns>
    Task<string?> GetTokenStatusForApplicationAsync(Guid tokenId, Guid applicationId);

    /// <summary>
    /// Revokes a token by its ID, provided it is stored for the given application and not already
    /// revoked. The ownership and status conditions are part of the UPDATE, so no other
    /// application's token can change and a repeated revocation keeps the original redemption date.
    /// </summary>
    /// <param name="tokenId">The token ID to revoke.</param>
    /// <param name="applicationId">The stored Id of the authenticated application.</param>
    /// <returns>
    /// True if a row changed; false if the token is unknown, already revoked, or stored for a
    /// different application.
    /// </returns>
    Task<bool> RevokeTokenAsync(Guid tokenId, Guid applicationId);

    /// <summary>
    /// Deletes tokens that expired before the given UTC bound, regardless of status.
    /// </summary>
    /// <param name="expiredBefore">The UTC upper bound for expiration date.</param>
    /// <returns>The number of deleted rows.</returns>
    Task<int> DeleteExpiredTokensAsync(DateTimeOffset expiredBefore);

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
    /// Gets all active public keys; <paramref name="cancellationToken"/> cancels both the connection open and the query.
    /// </summary>
    /// <returns>Collection of public key information.</returns>
    Task<IEnumerable<PublicKeyInfo>> GetActivePublicKeysAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets application information by client ID.
    /// </summary>
    /// <param name="clientId">The client ID to search for.</param>
    /// <returns>Application information if found, null otherwise.</returns>
    Task<ApplicationInfo?> GetApplicationByClientIdAsync(string clientId);

    /// <summary>
    /// Reads only what client authentication needs, before the presented secret is verified.
    /// <see cref="GetApplicationByClientIdAsync"/> also loads an application's scopes, permissions,
    /// data stores and protocol mappers, so it does more work when the client exists than when it
    /// does not; the token manager authenticates from this narrow read and loads the full
    /// application only once the secret has been verified and the client is approved.
    /// </summary>
    /// <param name="clientId">The client ID to search for.</param>
    /// <returns>The credential, or <c>null</c> when no application is registered under the client id.</returns>
    Task<ClientCredential?> GetClientCredentialAsync(string clientId);

    /// <summary>
    /// Gets the roles associated with a client.
    /// </summary>
    /// <param name="clientId">The client ID.</param>
    /// <returns>Collection of role names.</returns>
    Task<IEnumerable<string>> GetClientRolesAsync(Guid clientId);
}
