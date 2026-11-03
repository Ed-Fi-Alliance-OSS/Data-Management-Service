// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories
{
    /// <summary>
    /// Database abstraction interface for OpenIddict operations.
    /// Provides database-agnostic methods for managing OpenIddict entities.
    /// </summary>
    public interface IOpenIddictDataRepository
    {
        // High-level transaction wrapper methods
        Task<T> ExecuteInTransactionAsync<T>(Func<IDbConnection, IDbTransaction, Task<T>> operation);
        Task ExecuteInTransactionAsync(Func<IDbConnection, IDbTransaction, Task> operation);

        // Connection management
        Task<IDbConnection> CreateConnectionAsync();
        Task<IDbTransaction> BeginTransactionAsync(IDbConnection connection);

        // Role operations
        Task<Guid?> FindRoleIdByNameAsync(
            string roleName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        Task InsertRoleAsync(
            Guid roleId,
            string roleName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        // Application operations

        /// <summary>
        /// Inserts the application row inside the caller's transaction.
        /// </summary>
        /// <returns>
        /// <see cref="OpenIddictApplicationInsertOutcome.Inserted"/> when the row was written, or
        /// <see cref="OpenIddictApplicationInsertOutcome.DuplicateClientId"/> when the engine
        /// rejected it on <c>UX_OpenIddictApplication_ClientId</c> and nothing was written. Any
        /// other failure, a role or scope uniqueness violation included, propagates as thrown.
        /// </returns>
        Task<OpenIddictApplicationInsertOutcome> InsertApplicationAsync(
            Guid id,
            string clientId,
            string clientSecret,
            string displayName,
            string[] permissions,
            string[] requirements,
            string type,
            string protocolMappers,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        Task<int> UpdateApplicationAsync(
            Guid id,
            string displayName,
            string[] permissions,
            string protocolMappers,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        Task<int> UpdateApplicationProtocolMappersAsync(
            Guid id,
            string protocolMappers,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        /// <summary>
        /// Deletes the application row inside the caller's transaction. Application scopes and
        /// client roles cascade; token rows do not (there is no foreign key) and are revoked
        /// separately by <see cref="RevokeTokensForApplicationAsync"/>.
        /// </summary>
        /// <returns>The number of application rows deleted: 1, or 0 when none existed.</returns>
        Task<int> DeleteApplicationByIdAsync(
            Guid id,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        /// <summary>
        /// Locks the application row for the rest of the caller's transaction, with the same lock a
        /// token grant takes while it stores a token, and reports whether the row exists. A caller
        /// that then changes state a grant re-validates under that lock is serialized with every
        /// grant for the application.
        /// </summary>
        /// <returns>True when the row exists and is now locked; false when there is no such row.</returns>
        Task<bool> LockApplicationAsync(
            Guid applicationId,
            IDbConnection connection,
            IDbTransaction transaction
        );

        Task<int> UpdateClientSecretAsync(
            Guid id,
            string clientSecret,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        Task<IEnumerable<string>> GetAllClientIdsAsync();

        // Scope operations
        Task<Guid?> FindScopeIdByNameAsync(
            string scopeName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        Task InsertScopeAsync(
            Guid scopeId,
            string scopeName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        Task InsertApplicationScopeAsync(
            Guid applicationId,
            Guid scopeId,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        Task DeleteApplicationScopesByApplicationIdAsync(
            Guid applicationId,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        // Client role operations
        Task InsertClientRoleAsync(
            Guid clientId,
            Guid roleId,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        Task<IEnumerable<string>> GetClientRolesAsync(Guid clientId);

        // Token operations
        Task<TokenInfo?> GetTokenByIdAsync(Guid tokenId);

        /// <summary>
        /// Stores a new token, unless the application already holds the maximum number of active
        /// tokens. Every store runs in one transaction that locks the application row, then reads
        /// the stored client secret and the approval in a separate statement (on PostgreSQL a
        /// scalar read inside the locking statement would use the snapshot taken before it
        /// blocked), and inserts only when the secret still equals
        /// <paramref name="expectedClientSecret"/> ordinally and the client is still approved. When
        /// <paramref name="maxActiveTokens"/> is below 1 the active-token count is skipped; the lock
        /// and the re-validation are not.
        /// </summary>
        /// <returns>
        /// <see cref="TokenStoreOutcome.Stored"/> when the token was written,
        /// <see cref="TokenStoreOutcome.LimitExceeded"/> when the application already holds at
        /// least <paramref name="maxActiveTokens"/> active tokens,
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

        Task<string?> GetTokenStatusAsync(Guid tokenId);

        /// <summary>
        /// Gets the status of a token only when the token is stored for <paramref name="applicationId"/>.
        /// </summary>
        /// <param name="tokenId">The token ID.</param>
        /// <param name="applicationId">The application the token must be stored for.</param>
        /// <returns>The token status when it is stored for that application, null otherwise.</returns>
        Task<string?> GetTokenStatusForApplicationAsync(Guid tokenId, Guid applicationId);

        /// <summary>
        /// Marks the token revoked only when it is still unrevoked and stored for
        /// <paramref name="applicationId"/>; both conditions are enforced by the UPDATE itself, so a
        /// repeated revocation keeps the original redemption timestamp.
        /// </summary>
        /// <returns>True if a row changed; false for an unknown, already revoked, or foreign token.</returns>
        Task<bool> RevokeTokenAsync(Guid tokenId, Guid applicationId);

        /// <summary>
        /// Marks every unrevoked token stored for <paramref name="applicationId"/> as revoked,
        /// inside the caller's transaction. Status and ownership are predicates of the single
        /// UPDATE, as in <see cref="RevokeTokenAsync"/>: rows already revoked are not touched, so
        /// their original RedemptionDate survives, and no other application's token can change.
        /// </summary>
        /// <returns>The number of rows that changed; 0 when the application holds no unrevoked token.</returns>
        Task<int> RevokeTokensForApplicationAsync(
            Guid applicationId,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );

        Task<int> DeleteExpiredTokensAsync(DateTimeOffset expiredBefore);

        // Key management operations
        Task<(string PrivateKey, string KeyId)?> GetActivePrivateKeyInternalAsync(string encryptionKey);

        Task<IEnumerable<(string KeyId, byte[] PublicKey)>> GetActivePublicKeysInternalAsync();

        // Cancellable overload: the token reaches the connection open and the query.
        Task<IEnumerable<(string KeyId, byte[] PublicKey)>> GetActivePublicKeysInternalAsync(
            CancellationToken cancellationToken
        );

        // Application info operations
        Task<ApplicationInfo?> GetApplicationByClientIdAsync(string clientId);

        /// <summary>
        /// Reads only what client authentication needs, before the presented secret is verified:
        /// the credential of the application registered under <paramref name="clientId"/>, or
        /// <c>null</c> when none is. The statement makes the same index probes whether or not the
        /// client id is registered; a registered id also reads its matched rows.
        /// </summary>
        Task<ClientCredential?> GetClientCredentialAsync(string clientId);

        Task<ApplicationInfo?> GetApplicationByIdAsync(
            Guid id,
            IDbConnection connection,
            IDbTransaction? transaction = null
        );
    }
}
