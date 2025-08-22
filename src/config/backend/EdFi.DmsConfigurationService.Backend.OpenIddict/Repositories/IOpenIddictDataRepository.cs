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
        Task InsertApplicationAsync(
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

        Task<int> DeleteApplicationByIdAsync(Guid id);

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

        Task StoreTokenAsync(
            Guid tokenId,
            Guid applicationId,
            string subject,
            string payload,
            DateTimeOffset expiration
        );

        Task<string?> GetTokenStatusAsync(Guid tokenId);

        Task<bool> RevokeTokenAsync(Guid tokenId);

        // Key management operations
        Task<(string PrivateKey, string KeyId)?> GetActivePrivateKeyInternalAsync(string encryptionKey);

        Task<IEnumerable<(string KeyId, byte[] PublicKey)>> GetActivePublicKeysInternalAsync();

        // Application info operations
        Task<ApplicationInfo?> GetApplicationByClientIdAsync(string clientId);
    }
}
