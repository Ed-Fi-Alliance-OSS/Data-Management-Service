// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using Dapper;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories
{
    /// <summary>
    /// PostgreSQL implementation of IOpenIddictDataRepository.
    /// Handles all database operations for OpenIddict using PostgreSQL-specific connections and SQL.
    /// </summary>
    public class OpenIddictDataRepository(
        IOptions<DatabaseOptions> databaseOptions,
        ILogger<OpenIddictDataRepository> logger
    ) : IOpenIddictDataRepository
    {
        private readonly string _connectionString = databaseOptions.Value.DatabaseConnection;
        private readonly ILogger<OpenIddictDataRepository> _logger = logger;

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

        /// <summary>
        /// Builds the application projection used by the client-id lookups. Only the WHERE
        /// predicate varies, and every predicate passed in is a literal owned by this class —
        /// never caller input — so there is no injection surface here.
        /// </summary>
        private static string ApplicationLookupSql(string predicate) =>
            $@"SELECT a.""Id"", a.""ClientId"", a.""ClientSecret"", a.""DisplayName"", a.""RedirectUris"", a.""PostLogoutRedirectUris"",
                         a.""Permissions"", a.""Requirements"", a.""Type"", a.""CreatedAt"", a.""ProtocolMappers""::jsonb::text AS ""ProtocolMappers"",
                         COALESCE(array_agg(DISTINCT s.""Name"") FILTER (WHERE s.""Name"" IS NOT NULL), ARRAY[]::text[]) AS ""Scopes"",
                         COALESCE(array_agg(DISTINCT acd.""DataStoreId"") FILTER (WHERE acd.""DataStoreId"" IS NOT NULL), ARRAY[]::int[]) AS ""DataStoreIds"",
                         COALESCE(BOOL_AND(ac.""IsApproved""), true) AS ""IsApproved""
                  FROM ""dmscs"".""OpenIddictApplication"" a
                  LEFT JOIN ""dmscs"".""OpenIddictApplicationScope"" aps ON a.""Id"" = aps.""ApplicationId""
                  LEFT JOIN ""dmscs"".""OpenIddictScope"" s ON aps.""ScopeId"" = s.""Id""
                  LEFT JOIN ""dmscs"".""ApiClient"" ac ON a.""ClientId"" = ac.""ClientId""
                  LEFT JOIN ""dmscs"".""ApiClientDataStore"" acd ON ac.""Id"" = acd.""ApiClientId""
                  WHERE {predicate}
                  GROUP BY a.""Id"", a.""ClientId"", a.""ClientSecret"", a.""DisplayName"", a.""RedirectUris"", a.""PostLogoutRedirectUris"",
                           a.""Permissions"", a.""Requirements"", a.""Type"", a.""CreatedAt"", a.""ProtocolMappers""";

        // Methods used by OpenIddictTokenRepository

        /// <summary>
        /// Resolves a registered client by id, accepting any casing. SQL Server already matches
        /// case-insensitively under its default collation; this brings PostgreSQL, whose default
        /// collation is case-sensitive, to the same behaviour so a client is not silently rejected
        /// (or issued a differently-cased identity) depending on which engine is deployed.
        ///
        /// An exact match is always preferred. The unique constraint on "ClientId" is
        /// case-sensitive, so a pre-existing database may legitimately hold several rows differing
        /// only by case; preferring the exact row keeps every currently-working credential working
        /// exactly as before and confines the new behaviour to requests that would previously have
        /// failed outright.
        /// </summary>
        public async Task<ApplicationInfo?> GetApplicationByClientIdAsync(string clientId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Exact match first. This is the path every correctly-cased credential takes, and it
            // can use the UX_OpenIddictApplication_ClientId unique index.
            var exactMatch = await connection.QuerySingleOrDefaultAsync<ApplicationInfo>(
                ApplicationLookupSql(@"a.""ClientId"" = @ClientId"),
                new { ClientId = clientId }
            );

            if (exactMatch is not null)
            {
                return exactMatch;
            }

            // Case-insensitive fallback. LOWER() on the column cannot use the unique index, so
            // this is a sequential scan of OpenIddictApplication. That is accepted deliberately:
            // the table holds one row per registered client, and this query only runs after an
            // exact match has already missed, which for a correctly-cased credential never
            // happens. A LOWER("ClientId") expression index would remove even that cost.
            var caseInsensitiveMatches = (
                await connection.QueryAsync<ApplicationInfo>(
                    ApplicationLookupSql(@"LOWER(a.""ClientId"") = LOWER(@ClientId)"),
                    new { ClientId = clientId }
                )
            ).ToList();

            if (caseInsensitiveMatches.Count == 1)
            {
                return caseInsensitiveMatches[0];
            }

            if (caseInsensitiveMatches.Count > 1)
            {
                // Several distinct clients differ only by case and none matched exactly. Choosing
                // one would make authentication depend on row order, so refuse instead. Returning
                // null surfaces as a normal invalid_client failure rather than an exception, but
                // an operator has to rename or remove the duplicates to make this client usable
                // case-insensitively, so it is logged at Warning.
                _logger.LogWarning(
                    "Client id {ClientId} matches {MatchCount} registered clients differing only by letter case; "
                        + "authentication refused as ambiguous. Resolve the duplicate registrations.",
                    LoggingUtility.SanitizeForLog(clientId),
                    caseInsensitiveMatches.Count
                );
            }

            return null;
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

        public async Task StoreTokenAsync(
            Guid tokenId,
            Guid applicationId,
            string subject,
            DateTimeOffset expiration
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
