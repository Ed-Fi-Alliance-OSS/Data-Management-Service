// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.Repositories;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories
{
    /// <summary>
    /// Database-agnostic OpenIddict client repository that implements IClientRepository.
    /// Uses IOpenIddictDataRepository for database operations.
    /// </summary>
    public class OpenIddictClientRepository(
        ILogger<OpenIddictClientRepository> logger,
        IClientSecretHasher secretHasher,
        IOpenIddictDataRepository dataRepository
    ) : IClientRepository
    {
        /// <summary>
        /// Updates or adds the namespacePrefixes claim in the protocol mappers JSON.
        /// </summary>
        /// <param name="existingProtocolMappersJson">Existing protocol mappers JSON string.</param>
        /// <param name="namespacePrefixes">New namespacePrefixes value.</param>
        /// <returns>Updated protocol mappers JSON string.</returns>
        private static string MergeNamespacePrefixClaim(
            string existingProtocolMappersJson,
            string namespacePrefixes
        )
        {
            List<Dictionary<string, string>> protocolMappers = new();
            if (!string.IsNullOrWhiteSpace(existingProtocolMappersJson))
            {
                try
                {
                    protocolMappers =
                        JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                            existingProtocolMappersJson
                        ) ?? new List<Dictionary<string, string>>();
                }
                catch
                {
                    protocolMappers = new List<Dictionary<string, string>>();
                }
            }
            // Remove any existing namespacePrefixes claim
            protocolMappers.RemoveAll(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "namespacePrefixes"
            );
            // Add the updated namespacePrefixes claim
            protocolMappers.Add(ClientClaimHelper.CreateNamespacePrefixClaim(namespacePrefixes));
            return JsonSerializer.Serialize(protocolMappers);
        }

        /// <summary>
        /// Updates or adds the educationOrganizationIds claim in the protocol mappers JSON.
        /// </summary>
        /// <param name="existingProtocolMappersJson">Existing protocol mappers JSON string.</param>
        /// <param name="educationOrganizationIds">New educationOrganizationIds value.</param>
        /// <returns>Updated protocol mappers JSON string.</returns>
        private static string MergeEducationOrganizationClaim(
            string existingProtocolMappersJson,
            string educationOrganizationIds
        )
        {
            List<Dictionary<string, string>> protocolMappers = new();
            if (!string.IsNullOrWhiteSpace(existingProtocolMappersJson))
            {
                try
                {
                    protocolMappers =
                        JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                            existingProtocolMappersJson
                        ) ?? new List<Dictionary<string, string>>();
                }
                catch
                {
                    protocolMappers = new List<Dictionary<string, string>>();
                }
            }
            // Remove any existing educationOrganizationIds claim
            protocolMappers.RemoveAll(m =>
                m.ContainsKey("claim.name") && m["claim.name"] == "educationOrganizationIds"
            );
            // Add the updated educationOrganizationIds claim
            protocolMappers.Add(ClientClaimHelper.CreateEducationOrganizationClaim(educationOrganizationIds));
            return JsonSerializer.Serialize(protocolMappers);
        }

        public async Task<ClientCreateResult> CreateClientAsync(
            string clientId,
            string clientSecret,
            string role,
            string displayName,
            string scope,
            string namespacePrefixes,
            string educationOrganizationIds
        )
        {
            try
            {
                var clientUuid = Guid.NewGuid();
                using var connection = await dataRepository.CreateConnectionAsync();
                using var transaction = await dataRepository.BeginTransactionAsync(connection);

                // 1. Ensure role exists (openiddict_rol)
                Guid rolId;
                var existingRolId = await dataRepository.FindRoleIdByNameAsync(role, connection, transaction);
                if (existingRolId == null)
                {
                    rolId = Guid.NewGuid();
                    await dataRepository.InsertRoleAsync(rolId, role, connection, transaction);
                }
                else
                {
                    rolId = existingRolId.Value;
                }

                var namespacePrefixClaim = ClientClaimHelper.CreateNamespacePrefixClaim(namespacePrefixes);
                var educationOrgClaim = ClientClaimHelper.CreateEducationOrganizationClaim(
                    educationOrganizationIds
                );
                var protocolMappers = new List<Dictionary<string, string>>
                {
                    namespacePrefixClaim,
                    educationOrgClaim,
                };

                // Hash the client secret for secure storage
                var hashedClientSecret = await secretHasher.HashSecretAsync(clientSecret);

                var permissions =
                    scope?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                var requirementsArray = Array.Empty<string>();
                var protocolMappersJson = JsonSerializer.Serialize(protocolMappers);

                await dataRepository.InsertApplicationAsync(
                    clientUuid,
                    clientId,
                    hashedClientSecret,
                    displayName,
                    permissions,
                    requirementsArray,
                    "confidential",
                    protocolMappersJson,
                    connection,
                    transaction
                );

                // 3. Insert scopes and join records if scope is not null
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    var scopes = scope.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    foreach (var scopeName in scopes)
                    {
                        // Insert scope if not exists
                        var scopeId = await dataRepository.FindScopeIdByNameAsync(
                            scopeName,
                            connection,
                            transaction
                        );
                        if (scopeId == null)
                        {
                            scopeId = Guid.NewGuid();
                            await dataRepository.InsertScopeAsync(
                                scopeId.Value,
                                scopeName,
                                connection,
                                transaction
                            );
                        }
                        // Insert into join table
                        await dataRepository.InsertApplicationScopeAsync(
                            clientUuid,
                            scopeId.Value,
                            connection,
                            transaction
                        );
                    }
                }

                // 4. Assign role to client (openiddict_client_rol)
                await dataRepository.InsertClientRoleAsync(clientUuid, rolId, connection, transaction);

                transaction.Commit();
                return new ClientCreateResult.Success(clientUuid);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create OpenIddict client");
                return new ClientCreateResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientUpdateResult> UpdateClientAsync(
            string clientUuid,
            string displayName,
            string scope,
            string educationOrganizationIds
        )
        {
            try
            {
                using var connection = await dataRepository.CreateConnectionAsync();
                using var transaction = await dataRepository.BeginTransactionAsync(connection);

                // Read existing protocol mappers from the application
                var application = await dataRepository.GetApplicationByIdAsync(
                    Guid.Parse(clientUuid),
                    connection,
                    transaction
                );

                if (application == null)
                {
                    transaction.Rollback();
                    return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
                }

                // Merge the educationOrganizationIds claim with existing protocol mappers
                var protocolMappersJson = MergeEducationOrganizationClaim(
                    application.ProtocolMappers,
                    educationOrganizationIds
                );

                var permissions =
                    scope?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                var rows = await dataRepository.UpdateApplicationAsync(
                    Guid.Parse(clientUuid),
                    displayName,
                    permissions,
                    protocolMappersJson,
                    connection,
                    transaction
                );

                if (rows == 0)
                {
                    transaction.Rollback();
                    return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
                }

                // Update scopes if provided
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    // Remove existing associations
                    await dataRepository.DeleteApplicationScopesByApplicationIdAsync(
                        Guid.Parse(clientUuid),
                        connection,
                        transaction
                    );

                    var scopes = scope.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    );
                    foreach (var scopeName in scopes)
                    {
                        // Insert scope if not exists
                        var scopeId = await dataRepository.FindScopeIdByNameAsync(
                            scopeName,
                            connection,
                            transaction
                        );
                        if (scopeId == null)
                        {
                            scopeId = Guid.NewGuid();
                            await dataRepository.InsertScopeAsync(
                                scopeId.Value,
                                scopeName,
                                connection,
                                transaction
                            );
                        }
                        // Insert into join table
                        await dataRepository.InsertApplicationScopeAsync(
                            Guid.Parse(clientUuid),
                            scopeId.Value,
                            connection,
                            transaction
                        );
                    }
                }

                transaction.Commit();
                return new ClientUpdateResult.Success(Guid.Parse(clientUuid));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update OpenIddict client");
                return new ClientUpdateResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientUpdateResult> UpdateClientNamespaceClaimAsync(
            string clientUuid,
            string namespacePrefixes
        )
        {
            try
            {
                using var connection = await dataRepository.CreateConnectionAsync();
                using var transaction = await dataRepository.BeginTransactionAsync(connection);

                // Read existing protocol mappers from the application
                var application = await dataRepository.GetApplicationByIdAsync(
                    Guid.Parse(clientUuid),
                    connection,
                    transaction
                );

                if (application == null)
                {
                    transaction.Rollback();
                    return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
                }

                var protocolMappersJson = MergeNamespacePrefixClaim(
                    application.ProtocolMappers,
                    namespacePrefixes
                );

                var rows = await dataRepository.UpdateApplicationProtocolMappersAsync(
                    Guid.Parse(clientUuid),
                    protocolMappersJson,
                    connection,
                    transaction
                );

                if (rows == 0)
                {
                    transaction.Rollback();
                    return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
                }

                transaction.Commit();
                return new ClientUpdateResult.Success(Guid.Parse(clientUuid));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update namespace claim for OpenIddict client");
                return new ClientUpdateResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientClientsResult> GetAllClientsAsync()
        {
            try
            {
                var clients = await dataRepository.GetAllClientIdsAsync();
                return new ClientClientsResult.Success(clients);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get all OpenIddict clients");
                return new ClientClientsResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientDeleteResult> DeleteClientAsync(string clientUuid)
        {
            try
            {
                var rows = await dataRepository.DeleteApplicationByIdAsync(Guid.Parse(clientUuid));
                if (rows == 0)
                {
                    return new ClientDeleteResult.FailureClientNotFound($"Client {clientUuid} not found");
                }

                return new ClientDeleteResult.Success();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete OpenIddict client");
                return new ClientDeleteResult.FailureUnknown(ex.Message);
            }
        }

        public async Task<ClientResetResult> ResetCredentialsAsync(string clientUuid)
        {
            try
            {
                var newSecret = Guid.NewGuid().ToString("N");
                var hashedNewSecret = await secretHasher.HashSecretAsync(newSecret);
                using var connection = await dataRepository.CreateConnectionAsync();

                var rows = await dataRepository.UpdateClientSecretAsync(
                    Guid.Parse(clientUuid),
                    hashedNewSecret,
                    connection
                );

                if (rows == 0)
                {
                    return new ClientResetResult.FailureClientNotFound($"Client {clientUuid} not found");
                }

                return new ClientResetResult.Success(newSecret);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reset OpenIddict client credentials");
                return new ClientResetResult.FailureUnknown(ex.Message);
            }
        }
    }
}
