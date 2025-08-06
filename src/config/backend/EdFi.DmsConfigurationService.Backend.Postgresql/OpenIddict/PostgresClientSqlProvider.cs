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

                // 1. Ensure role exists (oidc_rol)
                Guid rolId;
                var existingRolId = await connection.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM dmscs.oidc_rol WHERE name = @Name",
                    new { Name = role },
                    transaction
                );
                if (existingRolId == null)
                {
                    rolId = Guid.NewGuid();
                    await connection.ExecuteAsync(
                        "INSERT INTO dmscs.oidc_rol (id, name) VALUES (@Id, @Name)",
                        new { Id = rolId, Name = role },
                        transaction
                    );
                }
                else
                {
                    rolId = existingRolId.Value;
                }

                // 2. Insert client with claims
                string sql = @"
INSERT INTO dmscs.openiddict_application
    (id, client_id, client_secret, display_name, permissions, requirements, type, created_at, namespace_prefixes, education_organization_ids)
VALUES (@Id, @ClientId, @ClientSecret, @DisplayName, @Permissions, @Requirements, @Type, CURRENT_TIMESTAMP, @NamespacePrefixes, @EducationOrganizationIds)
";
                var permissions = scope?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                var requirementsArray = Array.Empty<string>();
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
                        NamespacePrefixes = namespacePrefixes,
                        EducationOrganizationIds = educationOrganizationIds
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

                // 4. Assign role to client (oidc_client_rol)
                await connection.ExecuteAsync(
                    "INSERT INTO dmscs.oidc_client_rol (client_id, rol_id) VALUES (@ClientId, @RolId)",
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

                string sql = @"
UPDATE dmscs.openiddict_application
    SET display_name = @DisplayName,
        permissions = @Permissions
    WHERE id = @Id
";
                var permissions = scope?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                var rows = await connection.ExecuteAsync(
                    sql,
                    new
                    {
                        Id = Guid.Parse(clientUuid),
                        DisplayName = displayName,
                        Permissions = permissions
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
            // This method is obsolete as 'namespace_claim' does not exist in the schema.
            // To fix the CS1998 warning, we can explicitly return a completed task.
            return await Task.FromException<ClientUpdateResult>(
                new NotImplementedException("NamespaceClaim is not a column in dmscs.openiddict_application."));
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

