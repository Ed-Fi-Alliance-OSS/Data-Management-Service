// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict
{
    public class PostgresClientSqlProvider : IClientRepository
    {
        private readonly IOptions<DatabaseOptions> _databaseOptions;
        private readonly ILogger<PostgresClientSqlProvider> _logger;

        public PostgresClientSqlProvider(
            IOptions<DatabaseOptions> databaseOptions,
            ILogger<PostgresClientSqlProvider> logger)
        {
            _databaseOptions = databaseOptions;
            _logger = logger;
        }

        public async Task<ClientCreateResult> CreateClientAsync(
            string clientId,
            string clientSecret,
            string role,
            string displayName,
            string scope,
            string namespacePrefixes,
            string educationOrganizationIds)
        {
            try
            {
                var clientUuid = Guid.NewGuid();
                await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();

                // 1. Ensure role exists (openiddict_rol)
                Guid rolId;
                var existingRolId = await connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM dmscs.openiddict_rol WHERE name = @Name",
                    new { Name = role },
                    transaction
                );
                if (existingRolId == null)
                {
                    rolId = Guid.NewGuid();
                    await connection.ExecuteAsync(
                        "INSERT INTO dmscs.openiddict_rol (id, name) VALUES (@Id, @Name)",
                        new { Id = rolId, Name = role },
                        transaction
                    );
                }
                else
                {
                    rolId = existingRolId.Value;
                }

                var namespacePrefixClaim = ClientClaimHelper.CreateNamespacePrefixClaim(namespacePrefixes);
                var educationOrgClaim = ClientClaimHelper.CreateEducationOrganizationClaim(educationOrganizationIds);
                var protocolMappers = new List<Dictionary<string, string>>
                {
                    namespacePrefixClaim,
                    educationOrgClaim
                };
                string sql = @"
INSERT INTO dmscs.openiddict_application
    (id, client_id, client_secret, display_name, permissions, requirements, type, created_at, protocolmappers)
VALUES (@Id, @ClientId, @ClientSecret, @DisplayName, @Permissions, @Requirements, @Type, CURRENT_TIMESTAMP, @ProtocolMappers::jsonb)
";
                var permissions = scope?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                var requirementsArray = Array.Empty<string>();
                var protocolMappersJson = JsonSerializer.Serialize(protocolMappers);
                await connection.ExecuteAsync(
                    sql,
                    new
                    {
                        Id = clientUuid,
                        ClientId = clientId,
                        ClientSecret = clientSecret,
                        DisplayName = displayName,
                        Permissions = permissions,
                        Requirements = requirementsArray,
                        Type = "confidential",
                        ProtocolMappers = protocolMappersJson
                    },
                    transaction
                );

                // 3. Insert scopes and join records if scope is not null
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    var scopes = scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var scopeName in scopes)
                    {
                        // Insert scope if not exists
                        var scopeId = await connection.ExecuteScalarAsync<Guid?>(
                            "SELECT id FROM dmscs.openiddict_scope WHERE name = @Name",
                            new { Name = scopeName },
                            transaction
                        );
                        if (scopeId == null)
                        {
                            scopeId = Guid.NewGuid();
                            await connection.ExecuteAsync(
                                "INSERT INTO dmscs.openiddict_scope (id, name) VALUES (@Id, @Name)",
                                new { Id = scopeId, Name = scopeName },
                                transaction
                            );
                        }
                        // Insert into join table
                        await connection.ExecuteAsync(
                            "INSERT INTO dmscs.openiddict_application_scope (application_id, scope_id) VALUES (@AppId, @ScopeId)",
                            new { AppId = clientUuid, ScopeId = scopeId },
                            transaction
                        );
                    }
                }

                // 4. Assign role to client (openiddict_client_rol)
                await connection.ExecuteAsync(
                    "INSERT INTO dmscs.openiddict_client_rol (client_id, rol_id) VALUES (@ClientId, @RolId)",
                    new { ClientId = clientUuid, RolId = rolId },
                    transaction
                );

                await transaction.CommitAsync();
                return new ClientCreateResult.Success(clientUuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create OpenIddict client");
                return new ClientCreateResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientUpdateResult> UpdateClientAsync(
            string clientUuid,
            string displayName,
            string scope,
            string educationOrganizationIds)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();

                // Build protocol mappers (simulate Keycloak workflow)
                var protocolMappers = new List<Dictionary<string, string>>
                {
                    ClientClaimHelper.CreateNamespacePrefixClaim(displayName),
                    ClientClaimHelper.CreateEducationOrganizationClaim(educationOrganizationIds)
                };
                var protocolMappersJson = System.Text.Json.JsonSerializer.Serialize(protocolMappers);

                string sql = @"
UPDATE dmscs.openiddict_application
    SET display_name = @DisplayName,
        permissions = @Permissions,
        protocolmappers = @ProtocolMappers::jsonb
    WHERE id = @Id
";
                var permissions = scope?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                var rows = await connection.ExecuteAsync(
                    sql,
                    new
                    {
                        Id = Guid.Parse(clientUuid),
                        DisplayName = displayName,
                        Permissions = permissions,
                        ProtocolMappers = protocolMappersJson
                    },
                    transaction
                );
                if (rows == 0)
                {
                    await transaction.RollbackAsync();
                    return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
                }

                // Update scopes if provided
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    // Remove existing associations
                    await connection.ExecuteAsync(
                        "DELETE FROM dmscs.openiddict_application_scope WHERE application_id = @AppId",
                        new { AppId = Guid.Parse(clientUuid) },
                        transaction
                    );
                    var scopes = scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var scopeName in scopes)
                    {
                        // Insert scope if not exists
                        var scopeId = await connection.ExecuteScalarAsync<Guid?>(
                            "SELECT id FROM dmscs.openiddict_scope WHERE name = @Name",
                            new { Name = scopeName },
                            transaction
                        );
                        if (scopeId == null)
                        {
                            scopeId = Guid.NewGuid();
                            await connection.ExecuteAsync(
                                "INSERT INTO dmscs.openiddict_scope (id, name) VALUES (@Id, @Name)",
                                new { Id = scopeId, Name = scopeName },
                                transaction
                            );
                        }
                        // Insert into join table
                        await connection.ExecuteAsync(
                            "INSERT INTO dmscs.openiddict_application_scope (application_id, scope_id) VALUES (@AppId, @ScopeId)",
                            new { AppId = Guid.Parse(clientUuid), ScopeId = scopeId },
                            transaction
                        );
                    }
                }

                await transaction.CommitAsync();
                return new ClientUpdateResult.Success(Guid.Parse(clientUuid));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update OpenIddict client");
                return new ClientUpdateResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientUpdateResult> UpdateClientNamespaceClaimAsync(string clientUuid, string namespacePrefixes)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();

                // Build protocol mappers with updated namespacePrefixes
                var protocolMappers = new List<Dictionary<string, string>>
                {
                    ClientClaimHelper.CreateNamespacePrefixClaim(namespacePrefixes)
                };
                var protocolMappersJson = System.Text.Json.JsonSerializer.Serialize(protocolMappers);

                string sql = @"
UPDATE dmscs.openiddict_application
    SET protocolmappers = @ProtocolMappers::jsonb
    WHERE id = @Id
";
                var rows = await connection.ExecuteAsync(
                    sql,
                    new
                    {
                        Id = Guid.Parse(clientUuid),
                        ProtocolMappers = protocolMappersJson
                    },
                    transaction
                );
                if (rows == 0)
                {
                    await transaction.RollbackAsync();
                    return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
                }

                await transaction.CommitAsync();
                return new ClientUpdateResult.Success(Guid.Parse(clientUuid));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update namespace claim for OpenIddict client");
                return new ClientUpdateResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientClientsResult> GetAllClientsAsync()
        {
            try
            {
                await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                await connection.OpenAsync();
                string sql = "SELECT client_id FROM dmscs.openiddict_application";
                var clients = await connection.QueryAsync<string>(sql);
                return new ClientClientsResult.Success(clients);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all OpenIddict clients");
                return new ClientClientsResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientDeleteResult> DeleteClientAsync(string clientUuid)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                await connection.OpenAsync();
                string sql = "DELETE FROM dmscs.openiddict_application WHERE id = @Id";
                var rows = await connection.ExecuteAsync(sql, new { Id = Guid.Parse(clientUuid) });
                if (rows == 0)
                {
                    return new ClientDeleteResult.FailureUnknown($"Client {clientUuid} not found");
                }

                return new ClientDeleteResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete OpenIddict client");
                return new ClientDeleteResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientResetResult> ResetCredentialsAsync(string clientUuid)
        {
            try
            {
                var newSecret = Guid.NewGuid().ToString("N");
                await using var connection = new NpgsqlConnection(_databaseOptions.Value.DatabaseConnection);
                await connection.OpenAsync();
                string sql = @"
                UPDATE dmscs.openiddict_application
                SET client_secret = @ClientSecret
                WHERE id = @Id
                ";
                var rows = await connection.ExecuteAsync(
                    sql,
                    new { Id = Guid.Parse(clientUuid), ClientSecret = newSecret }
                );
                if (rows == 0)
                {
                    return new ClientResetResult.FailureUnknown($"Client {clientUuid} not found");
                }

                return new ClientResetResult.Success(newSecret);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset OpenIddict client credentials");
                return new ClientResetResult.FailureUnknown(ex.Message);
            }
        }
    }
}

