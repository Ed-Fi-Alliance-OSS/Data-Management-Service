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
    public class OpenIddictDataRepository(IOptions<DatabaseOptions> databaseOptions)
        : IOpenIddictDataRepository
    {
        private readonly string _connectionString = databaseOptions.Value.DatabaseConnection;

        /// <summary>
        /// How long a token grant waits for another grant's row lock on the same client before
        /// failing. Matches the default <c>ApplicationLockOptions.AcquireTimeout</c> so the two
        /// lock paths in this service bound their waits alike.
        /// </summary>
        private const string LockTimeoutMilliseconds = "5000";

        private const string SetLockTimeoutSql = $"SET LOCAL lock_timeout = '{LockTimeoutMilliseconds}ms'";

        private const string LockApplicationSql =
            @"SELECT ""Id"" FROM ""dmscs"".""OpenIddictApplication"" WHERE ""Id"" = @ApplicationId FOR UPDATE";

        /// <summary>
        /// Inserts the token only while the client holds fewer than @MaxActiveTokens active access
        /// tokens, where active means not revoked and not yet expired. The count is scoped to
        /// access tokens so that another token type stored in this table cannot consume the budget
        /// this setting describes. Counting and inserting in one statement leaves no window between
        /// the two.
        /// </summary>
        private const string ConditionalInsertSql =
            @"
                INSERT INTO ""dmscs"".""OpenIddictToken""
                (""Id"", ""ApplicationId"", ""Subject"", ""Type"", ""CreationDate"", ""ExpirationDate"", ""Status"", ""ReferenceId"")
                SELECT @Id, @ApplicationId, @Subject, @Type, @CreationDate, @ExpirationDate, @Status, @ReferenceId
                WHERE (
                    SELECT COUNT(*)
                    FROM ""dmscs"".""OpenIddictToken""
                    WHERE ""ApplicationId"" = @ApplicationId
                      AND ""Type"" = 'access_token'
                      AND ""Status"" = 'valid'
                      AND ""ExpirationDate"" > @ActiveAsOf
                ) < @MaxActiveTokens";

        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<IDbConnection, IDbTransaction, Task<T>> operation
        )
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
            const string sql = "SELECT \"Id\" FROM \"dmscs\".\"OpenIddictRole\" WHERE \"Name\" = @Name";
            return await connection.ExecuteScalarAsync<Guid?>(sql, new { Name = roleName }, transaction);
        }

        public async Task InsertRoleAsync(
            Guid roleId,
            string roleName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql =
                "INSERT INTO \"dmscs\".\"OpenIddictRole\" (\"Id\", \"Name\") VALUES (@Id, @Name)";
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
INSERT INTO ""dmscs"".""OpenIddictApplication""
    (""Id"", ""ClientId"", ""ClientSecret"", ""DisplayName"", ""Permissions"", ""Requirements"", ""Type"", ""CreatedAt"", ""ProtocolMappers"")
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
            const string sql = "SELECT \"Id\" FROM \"dmscs\".\"OpenIddictScope\" WHERE \"Name\" = @Name";
            return await connection.ExecuteScalarAsync<Guid?>(sql, new { Name = scopeName }, transaction);
        }

        public async Task InsertScopeAsync(
            Guid scopeId,
            string scopeName,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string sql =
                "INSERT INTO \"dmscs\".\"OpenIddictScope\" (\"Id\", \"Name\") VALUES (@Id, @Name)";
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
                "INSERT INTO \"dmscs\".\"OpenIddictApplicationScope\" (\"ApplicationId\", \"ScopeId\") VALUES (@AppId, @ScopeId)";
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
                "INSERT INTO \"dmscs\".\"OpenIddictClientRole\" (\"ClientId\", \"RoleId\") VALUES (@ClientId, @RoleId)";
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
UPDATE ""dmscs"".""OpenIddictApplication""
    SET ""DisplayName"" = @DisplayName,
        ""Permissions"" = @Permissions,
        ""ProtocolMappers"" = @ProtocolMappers::jsonb
    WHERE ""Id"" = @Id";

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
            const string sql =
                "DELETE FROM \"dmscs\".\"OpenIddictApplicationScope\" WHERE \"ApplicationId\" = @AppId";
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
UPDATE ""dmscs"".""OpenIddictApplication""
    SET ""ProtocolMappers"" = @ProtocolMappers::jsonb
    WHERE ""Id"" = @Id";

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
            const string sql = "SELECT \"ClientId\" FROM \"dmscs\".\"OpenIddictApplication\"";
            return await connection.QueryAsync<string>(sql);
        }

        public async Task<int> DeleteApplicationByIdAsync(Guid id)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            const string sql = "DELETE FROM \"dmscs\".\"OpenIddictApplication\" WHERE \"Id\" = @Id";
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
                UPDATE ""dmscs"".""OpenIddictApplication""
                SET ""ClientSecret"" = @ClientSecret
                WHERE ""Id"" = @Id";

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
                @"SELECT a.""Id"", a.""ClientId"", a.""ClientSecret"", a.""DisplayName"", a.""RedirectUris"", a.""PostLogoutRedirectUris"",
                         a.""Permissions"", a.""Requirements"", a.""Type"", a.""CreatedAt"", a.""ProtocolMappers""::jsonb::text AS ""ProtocolMappers"",
                         COALESCE(array_agg(DISTINCT s.""Name"") FILTER (WHERE s.""Name"" IS NOT NULL), ARRAY[]::text[]) AS ""Scopes"",
                         COALESCE(array_agg(DISTINCT acd.""DataStoreId"") FILTER (WHERE acd.""DataStoreId"" IS NOT NULL), ARRAY[]::int[]) AS ""DataStoreIds"",
                         COALESCE(BOOL_AND(ac.""IsApproved""), true) AS ""IsApproved""
                  FROM ""dmscs"".""OpenIddictApplication"" a
                  LEFT JOIN ""dmscs"".""OpenIddictApplicationScope"" aps ON a.""Id"" = aps.""ApplicationId""
                  LEFT JOIN ""dmscs"".""OpenIddictScope"" s ON aps.""ScopeId"" = s.""Id""
                  LEFT JOIN ""dmscs"".""ApiClient"" ac ON a.""ClientId"" = ac.""ClientId""
                  LEFT JOIN ""dmscs"".""ApiClientDataStore"" acd ON ac.""Id"" = acd.""ApiClientId""
                  WHERE a.""ClientId"" = @ClientId
                  GROUP BY a.""Id"", a.""ClientId"", a.""ClientSecret"", a.""DisplayName"", a.""RedirectUris"", a.""PostLogoutRedirectUris"",
                           a.""Permissions"", a.""Requirements"", a.""Type"", a.""CreatedAt"", a.""ProtocolMappers""";

            return await connection.QuerySingleOrDefaultAsync<ApplicationInfo>(
                applicationSql,
                new { ClientId = clientId }
            );
        }

        public async Task<ApplicationInfo?> GetApplicationByIdAsync(
            Guid id,
            IDbConnection connection,
            IDbTransaction? transaction = null
        )
        {
            const string applicationSql =
                @"SELECT a.""Id"", a.""ClientId"", a.""ClientSecret"", a.""DisplayName"", a.""RedirectUris"", a.""PostLogoutRedirectUris"",
                         a.""Permissions"", a.""Requirements"", a.""Type"", a.""CreatedAt"", a.""ProtocolMappers""::jsonb::text AS ""ProtocolMappers"",
                         COALESCE(array_agg(DISTINCT s.""Name"") FILTER (WHERE s.""Name"" IS NOT NULL), ARRAY[]::text[]) AS ""Scopes"",
                         COALESCE(array_agg(DISTINCT acd.""DataStoreId"") FILTER (WHERE acd.""DataStoreId"" IS NOT NULL), ARRAY[]::int[]) AS ""DataStoreIds"",
                         COALESCE(BOOL_AND(ac.""IsApproved""), true) AS ""IsApproved""
                  FROM ""dmscs"".""OpenIddictApplication"" a
                  LEFT JOIN ""dmscs"".""OpenIddictApplicationScope"" aps ON a.""Id"" = aps.""ApplicationId""
                  LEFT JOIN ""dmscs"".""OpenIddictScope"" s ON aps.""ScopeId"" = s.""Id""
                  LEFT JOIN ""dmscs"".""ApiClient"" ac ON a.""ClientId"" = ac.""ClientId""
                  LEFT JOIN ""dmscs"".""ApiClientDataStore"" acd ON ac.""Id"" = acd.""ApiClientId""
                  WHERE a.""Id"" = @Id
                  GROUP BY a.""Id"", a.""ClientId"", a.""ClientSecret"", a.""DisplayName"", a.""RedirectUris"", a.""PostLogoutRedirectUris"",
                           a.""Permissions"", a.""Requirements"", a.""Type"", a.""CreatedAt"", a.""ProtocolMappers""";

            return await connection.QuerySingleOrDefaultAsync<ApplicationInfo>(
                applicationSql,
                new { Id = id },
                transaction
            );
        }

        public async Task<IEnumerable<string>> GetClientRolesAsync(Guid clientId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            return await connection.QueryAsync<string>(
                @"SELECT r.""Name""
                  FROM ""dmscs"".""OpenIddictClientRole"" cr
                  JOIN ""dmscs"".""OpenIddictRole"" r ON cr.""RoleId"" = r.""Id""
                  WHERE cr.""ClientId"" = @ClientId",
                new { ClientId = clientId }
            );
        }

        public async Task<TokenInfo?> GetTokenByIdAsync(Guid tokenId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            const string sql =
                @"SELECT ""Id"", ""ApplicationId"", ""Subject"", ""Type"", ""Payload"", ""CreationDate"",
                         ""ExpirationDate"", ""Status"", ""ReferenceId"", ""RedemptionDate""
                  FROM ""dmscs"".""OpenIddictToken""
                  WHERE ""Id"" = @Id";
            return await connection.QuerySingleOrDefaultAsync<TokenInfo>(sql, new { Id = tokenId });
        }

        public async Task<TokenStoreOutcome> StoreTokenAsync(
            Guid tokenId,
            Guid applicationId,
            string subject,
            DateTimeOffset expiration,
            int maxActiveTokens
        )
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string insertSql =
                @"
                INSERT INTO ""dmscs"".""OpenIddictToken""
                (""Id"", ""ApplicationId"", ""Subject"", ""Type"", ""CreationDate"", ""ExpirationDate"", ""Status"", ""ReferenceId"")
                VALUES
                (@Id, @ApplicationId, @Subject, @Type, @CreationDate, @ExpirationDate, @Status, @ReferenceId)";

            // Enforcement disabled: the unconditional insert that ran before the limit existed,
            // with no transaction and no lock.
            if (maxActiveTokens < 1)
            {
                await connection.ExecuteAsync(
                    insertSql,
                    new
                    {
                        Id = tokenId,
                        ApplicationId = applicationId,
                        Subject = subject,
                        Type = "access_token",
                        CreationDate = DateTimeOffset.UtcNow,
                        ExpirationDate = expiration,
                        Status = "valid",
                        ReferenceId = tokenId.ToString("N"),
                    }
                );

                return TokenStoreOutcome.Stored;
            }

            // Grants for one client serialize on that client's OpenIddictApplication row, which
            // makes the limit a strict ceiling rather than a best-effort one. The lock lives and
            // dies with this transaction, so every path below releases it.
            await using var transaction = await connection.BeginTransactionAsync();

            await connection.ExecuteAsync(SetLockTimeoutSql, transaction: transaction);

            Guid? lockedApplicationId = await connection.QuerySingleOrDefaultAsync<Guid?>(
                LockApplicationSql,
                new { ApplicationId = applicationId },
                transaction
            );

            // A FOR UPDATE matching no row takes no lock at all on PostgreSQL, so without this
            // guard concurrent grants for a client deleted mid-request would count and insert
            // unserialized and could exceed the cap. OpenIddictToken has no foreign key to
            // OpenIddictApplication, so nothing else would stop the insert either.
            if (lockedApplicationId is null)
            {
                await transaction.RollbackAsync();
                return TokenStoreOutcome.ClientNotFound;
            }

            int rowsAffected = await connection.ExecuteAsync(
                ConditionalInsertSql,
                new
                {
                    Id = tokenId,
                    ApplicationId = applicationId,
                    Subject = subject,
                    Type = "access_token",
                    CreationDate = DateTimeOffset.UtcNow,
                    ExpirationDate = expiration,
                    Status = "valid",
                    ReferenceId = tokenId.ToString("N"),
                    // Passed exactly as ExpirationDate is, so the count and the stored value take
                    // the identical session-time-zone conversion path.
                    ActiveAsOf = DateTimeOffset.UtcNow,
                    MaxActiveTokens = maxActiveTokens,
                },
                transaction
            );

            await transaction.CommitAsync();

            // Zero rows can only mean the count predicate was false. A lock-wait timeout
            // (SQLSTATE 55P03), a deadlock victim, or any other fault throws from the statements
            // above and is never reported here as a limit rejection.
            return rowsAffected > 0 ? TokenStoreOutcome.Stored : TokenStoreOutcome.LimitExceeded;
        }

        public async Task<string?> GetTokenStatusAsync(Guid tokenId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT \"Status\" FROM \"dmscs\".\"OpenIddictToken\" WHERE \"Id\" = @Id",
                new { Id = tokenId }
            );
        }

        public async Task<bool> RevokeTokenAsync(Guid tokenId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            var result = await connection.ExecuteAsync(
                "UPDATE \"dmscs\".\"OpenIddictToken\" SET \"Status\" = 'revoked', \"RedemptionDate\" = CURRENT_TIMESTAMP WHERE \"Id\" = @Id",
                new { Id = tokenId }
            );
            return result > 0;
        }

        public async Task<int> DeleteExpiredTokensAsync(DateTimeOffset expiredBefore)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            const string sql =
                "DELETE FROM \"dmscs\".\"OpenIddictToken\" WHERE \"ExpirationDate\" <= @ExpiredBefore";
            return await connection.ExecuteAsync(sql, new { ExpiredBefore = expiredBefore });
        }

        public async Task<(string PrivateKey, string KeyId)?> GetActivePrivateKeyInternalAsync(
            string encryptionKey
        )
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            const string query =
                "SELECT pgp_sym_decrypt(\"PrivateKey\"::bytea, @encryptionKey) AS \"PrivateKey\", \"KeyId\" FROM \"dmscs\".\"OpenIddictKey\" WHERE \"IsActive\" = TRUE ORDER BY \"CreatedAt\" DESC LIMIT 1";
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
                "SELECT \"KeyId\", \"PublicKey\" FROM \"dmscs\".\"OpenIddictKey\" WHERE \"IsActive\" = TRUE"
            );
        }
    }
}
