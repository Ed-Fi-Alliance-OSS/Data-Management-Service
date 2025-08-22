// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using Dapper;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories
{
    /// <summary>
    /// PostgreSQL implementation of IOpenIddictDataRepository.
    /// Handles all database operations for OpenIddict using PostgreSQL-specific connections and SQL.
    /// </summary>
    public class OpenIddictDataRepository(IOptions<DatabaseOptions> databaseOptions) : IOpenIddictDataRepository
    {
        private readonly string _connectionString = databaseOptions.Value.DatabaseConnection;

        public async Task<T> ExecuteInTransactionAsync<T>(Func<IDbConnection, IDbTransaction, Task<T>> operation)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var result = await operation(connection, transaction);
                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task ExecuteInTransactionAsync(Func<IDbConnection, IDbTransaction, Task> operation)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await operation(connection, transaction);
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<IDbConnection> CreateConnectionAsync()
        {
            var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection;
        }

        public async Task<IDbTransaction> BeginTransactionAsync(IDbConnection connection)
        {
            return await ((NpgsqlConnection)connection).BeginTransactionAsync();
        }

        public async Task<Guid?> FindRoleIdByNameAsync(
            string roleName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql = "SELECT Id FROM dmscs.OpenIddictRole WHERE Name = @Name";
            return await connection.ExecuteScalarAsync<Guid?>(sql, new { Name = roleName }, transaction);
        }

        public async Task InsertRoleAsync(
            Guid roleId,
            string roleName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql = "INSERT INTO dmscs.OpenIddictRole (Id, Name) VALUES (@Id, @Name)";
            await connection.ExecuteAsync(sql, new { Id = roleId, Name = roleName }, transaction);
        }

        public async Task InsertApplicationAsync(
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
        )
        {
            const string sql =
                @"
INSERT INTO dmscs.OpenIddictApplication
    (Id, ClientId, ClientSecret, DisplayName, Permissions, Requirements, Type, CreatedAt, ProtocolMappers)
VALUES (@Id, @ClientId, @ClientSecret, @DisplayName, @Permissions, @Requirements, @Type, CURRENT_TIMESTAMP, @ProtocolMappers::jsonb)";

            await connection.ExecuteAsync(
                sql,
                new
                {
                    Id = id,
                    ClientId = clientId,
                    ClientSecret = clientSecret,
                    DisplayName = displayName,
                    Permissions = permissions,
                    Requirements = requirements,
                    Type = type,
                    ProtocolMappers = protocolMappers,
                },
                transaction
            );
        }

        public async Task<Guid?> FindScopeIdByNameAsync(
            string scopeName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql = "SELECT Id FROM dmscs.OpenIddictScope WHERE Name = @Name";
            return await connection.ExecuteScalarAsync<Guid?>(sql, new { Name = scopeName }, transaction);
        }

        public async Task InsertScopeAsync(
            Guid scopeId,
            string scopeName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql = "INSERT INTO dmscs.OpenIddictScope (Id, Name) VALUES (@Id, @Name)";
            await connection.ExecuteAsync(sql, new { Id = scopeId, Name = scopeName }, transaction);
        }

        public async Task InsertApplicationScopeAsync(
            Guid applicationId,
            Guid scopeId,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql =
                "INSERT INTO dmscs.OpenIddictApplicationScope (ApplicationId, ScopeId) VALUES (@AppId, @ScopeId)";
            await connection.ExecuteAsync(sql, new { AppId = applicationId, ScopeId = scopeId }, transaction);
        }

        public async Task InsertClientRoleAsync(
            Guid clientId,
            Guid roleId,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql =
                "INSERT INTO dmscs.OpenIddictClientRole (ClientId, RoleId) VALUES (@ClientId, @RoleId)";
            await connection.ExecuteAsync(sql, new { ClientId = clientId, RoleId = roleId }, transaction);
        }

        public async Task<int> UpdateApplicationAsync(
            Guid id,
            string displayName,
            string[] permissions,
            string protocolMappers,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql =
                @"
UPDATE dmscs.OpenIddictApplication
    SET display_name = @DisplayName,
        permissions = @Permissions,
        protocolmappers = @ProtocolMappers::jsonb
    WHERE id = @Id";

            return await connection.ExecuteAsync(
                sql,
                new
                {
                    Id = id,
                    DisplayName = displayName,
                    Permissions = permissions,
                    ProtocolMappers = protocolMappers,
                },
                transaction
            );
        }

        public async Task DeleteApplicationScopesByApplicationIdAsync(
            Guid applicationId,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql = "DELETE FROM dmscs.OpenIddictApplicationScope WHERE ApplicationId = @AppId";
            await connection.ExecuteAsync(sql, new { AppId = applicationId }, transaction);
        }

        public async Task<int> UpdateApplicationProtocolMappersAsync(
            Guid id,
            string protocolMappers,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql =
                @"
UPDATE dmscs.OpenIddictApplication
    SET ProtocolMappers = @ProtocolMappers::jsonb
    WHERE Id = @Id";

            return await connection.ExecuteAsync(
                sql,
                new { Id = id, ProtocolMappers = protocolMappers },
                transaction
            );
        }

        public async Task<IEnumerable<string>> GetAllClientIdsAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            const string sql = "SELECT ClientId FROM dmscs.OpenIddictApplication";
            return await connection.QueryAsync<string>(sql);
        }

        public async Task<int> DeleteApplicationByIdAsync(Guid id)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            const string sql = "DELETE FROM dmscs.OpenIddictApplication WHERE Id = @Id";
            return await connection.ExecuteAsync(sql, new { Id = id });
        }

        public async Task<int> UpdateClientSecretAsync(
            Guid id,
            string clientSecret,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql =
                @"
                UPDATE dmscs.OpenIddictApplication
                SET ClientSecret = @ClientSecret
                WHERE Id = @Id";

            return await connection.ExecuteAsync(
                sql,
                new { Id = id, ClientSecret = clientSecret },
                transaction
            );
        }

        // Methods used by OpenIddictTokenRepository
        public async Task<ApplicationInfo?> GetApplicationByClientIdAsync(string clientId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string applicationSql =
                @"SELECT a.Id, a.DisplayName, a.Permissions, a.ClientSecret,
                         array_agg(s.Name) as Scopes
                  FROM dmscs.OpenIddictApplication a
                  LEFT JOIN dmscs.OpenIddictApplicationScope aps ON a.Id = aps.ApplicationId
                  LEFT JOIN dmscs.OpenIddictScope s ON aps.ScopeId = s.Id
                  WHERE a.ClientId = @ClientId
                  GROUP BY a.Id, a.DisplayName, a.Permissions, a.ClientSecret";

            return await connection.QuerySingleOrDefaultAsync<ApplicationInfo>(
                applicationSql,
                new { ClientId = clientId }
            );
        }

        public async Task<IEnumerable<string>> GetClientRolesAsync(Guid clientId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            return await connection.QueryAsync<string>(
                @"SELECT r.Name
                  FROM dmscs.OpenIddictClientRole cr
                  JOIN dmscs.OpenIddictRole r ON cr.RoleId = r.Id
                  WHERE cr.ClientId = @ClientId",
                new { ClientId = clientId }
            );
        }

        public async Task<TokenInfo?> GetTokenByIdAsync(Guid tokenId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            const string sql = "SELECT * FROM dmscs.openiddict_token WHERE Id = @Id";
            return await connection.QuerySingleOrDefaultAsync<TokenInfo>(sql, new { Id = tokenId });
        }

        public async Task StoreTokenAsync(
            Guid tokenId,
            Guid applicationId,
            string subject,
            string payload,
            DateTimeOffset expiration)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            const string insertSql =
                @"
                INSERT INTO dmscs.OpenIddictToken
                (Id, ApplicationId, Subject, Type, CreationDate, ExpirationDate, Status, ReferenceId)
                VALUES
                (@Id, @ApplicationId, @Subject, @Type, @CreationDate, @ExpirationDate, @Status, @ReferenceId)";

            await connection.ExecuteAsync(
                insertSql,
                new
                {
                    Id = tokenId,
                    ApplicationId = applicationId,
                    Subject = subject,
                    Type = "access_token",
                    Payload = payload,
                    CreationDate = DateTimeOffset.UtcNow,
                    ExpirationDate = expiration,
                    Status = "valid",
                    ReferenceId = tokenId.ToString("N"),
                }
            );
        }

        public async Task<string?> GetTokenStatusAsync(Guid tokenId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT Status FROM dmscs.OpenIddictToken WHERE Id = @Id",
                new { Id = tokenId }
            );
        }

        public async Task<bool> RevokeTokenAsync(Guid tokenId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            var result = await connection.ExecuteAsync(
                "UPDATE dmscs.OpenIddictToken SET Status = 'revoked', RedemptionDate = CURRENT_TIMESTAMP WHERE Id = @Id",
                new { Id = tokenId }
            );
            return result > 0;
        }

        public async Task<(string PrivateKey, string KeyId)?> GetActivePrivateKeyInternalAsync(string encryptionKey)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            const string query =
                "SELECT pgp_sym_decrypt(PrivateKey::bytea, @encryptionKey) AS PrivateKey, KeyId FROM dmscs.OpenIddictKey WHERE IsActive = TRUE ORDER BY CreatedAt DESC LIMIT 1";
            var keyRecord = await connection.QuerySingleOrDefaultAsync<(string PrivateKey, string KeyId)>(
                query,
                new { encryptionKey }
            );

            if (string.IsNullOrEmpty(keyRecord.PrivateKey) || string.IsNullOrEmpty(keyRecord.KeyId))
            {
                return null;
            }

            return keyRecord;
        }

        public async Task<IEnumerable<(string KeyId, byte[] PublicKey)>> GetActivePublicKeysInternalAsync()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return await connection.QueryAsync<(string KeyId, byte[] PublicKey)>(
                "SELECT KeyId, PublicKey FROM dmscs.OpenIddictKey WHERE IsActive = TRUE"
            );
        }
    }
}
