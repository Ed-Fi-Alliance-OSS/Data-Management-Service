// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using Flurl.Http;
using Keycloak.Net.Models.Clients;
using Keycloak.Net.Models.ClientScopes;
using Keycloak.Net.Models.ProtocolMappers;
using Keycloak.Net.Models.Roles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.DataModel.LoggingUtility;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public class KeycloakClientRepository(
    KeycloakContext keycloakContext,
    IKeycloakClientFacade keycloakClientFacade,
    ILogger<KeycloakClientRepository> logger,
    IOptions<ClientSecretValidationOptions> clientSecretValidationOptionsAccessor
) : IIdentityProviderRepository
{
    private readonly string _realm = keycloakContext.Realm;

    public async Task<ClientCreateResult> CreateClientAsync(
        string clientId,
        string clientSecret,
        string role,
        string displayName,
        string scope,
        string namespacePrefixes,
        string educationOrganizationIds,
        long[]? dataStoreIds = null,
        bool isApproved = true
    )
    {
        try
        {
            var protocolMappers = ConfigServiceRoleProtocolMapper();
            protocolMappers.Add(NamespacePrefixProtocolMapper(namespacePrefixes));
            protocolMappers.Add(EducationOrganizationProtocolMapper(educationOrganizationIds));

            // Add data store IDs as sorted comma-separated string
            if (dataStoreIds != null && dataStoreIds.Length > 0)
            {
                var sortedDataStoreIds = string.Join(",", dataStoreIds.OrderBy(id => id));
                protocolMappers.Add(DataStoreIdsProtocolMapper(sortedDataStoreIds));
            }

            Client client = new()
            {
                ClientId = clientId,
                Enabled = isApproved,
                Secret = clientSecret,
                Name = displayName,
                ServiceAccountsEnabled = true,
                DefaultClientScopes = [scope],
                ProtocolMappers = protocolMappers,
            };

            // Read role from the realm
            var realmRoles = await keycloakClientFacade.GetRolesAsync(_realm);
            Role? clientRole = realmRoles.FirstOrDefault(x =>
                x.Name.Equals(role, StringComparison.InvariantCultureIgnoreCase)
            );

            if (clientRole is null)
            {
                await keycloakClientFacade.CreateRoleAsync(_realm, new Role() { Name = role });

                clientRole = await keycloakClientFacade.GetRoleByNameAsync(_realm, role);
            }

            await CheckAndCreateClientScopeAsync(scope);

            string? createdClientUuid = await keycloakClientFacade.CreateClientAndRetrieveClientIdAsync(
                _realm,
                client
            );
            if (!string.IsNullOrEmpty(createdClientUuid))
            {
                if (clientRole != null)
                {
                    // Assign the service role to client's service account
                    var serviceAccountUser = await keycloakClientFacade.GetUserForServiceAccountAsync(
                        _realm,
                        createdClientUuid
                    );

                    _ = await keycloakClientFacade.AddRealmRoleMappingsToUserAsync(
                        _realm,
                        serviceAccountUser.Id,
                        [clientRole]
                    );

                    return new ClientCreateResult.Success(Guid.Parse(createdClientUuid));
                }
                else
                {
                    return new ClientCreateResult.FailureUnknown($"Role {role} not found.");
                }
            }

            logger.LogError(
                "Error while creating the client {ClientId}. CreateClientAndRetrieveClientIdAsync returned empty string with no exception.",
                SanitizeForLog(clientId)
            );
            return new ClientCreateResult.FailureUnknown($"Error while creating the client: {clientId}");
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Create client failure");
            return new ClientCreateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Create client failure");
            return new ClientCreateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClientUpdateResult> UpdateClientNamespaceClaimAsync(
        string clientUuid,
        string namespacePrefixes
    )
    {
        try
        {
            var client = await keycloakClientFacade.GetClientAsync(_realm, clientUuid);

            // Delete the existing client
            await keycloakClientFacade.DeleteClientAsync(_realm, clientUuid);

            var protocolMappers = ConfigServiceRoleProtocolMapper();
            protocolMappers.Add(NamespacePrefixProtocolMapper(namespacePrefixes));
            Client newClient = new()
            {
                ClientId = client.ClientId,
                Enabled = client.Enabled,
                Secret = client.Secret,
                Name = client.Name,
                ServiceAccountsEnabled = true,
                DefaultClientScopes = client.DefaultClientScopes,
                ProtocolMappers = protocolMappers,
            };
            // Re-create the client
            string? newClientId = await keycloakClientFacade.CreateClientAndRetrieveClientIdAsync(
                _realm,
                newClient
            );
            if (!string.IsNullOrEmpty(newClientId))
            {
                return new ClientUpdateResult.Success(Guid.Parse(newClientId));
            }

            logger.LogError("Update client failure");
            return new ClientUpdateResult.FailureUnknown($"Error while updating the client: {clientUuid}");
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Update client failure");
            return new ClientUpdateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update client failure");
            return new ClientUpdateResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClientDeleteResult> DeleteClientAsync(string clientUuid)
    {
        try
        {
            return await keycloakClientFacade.DeleteClientAsync(_realm, clientUuid)
                ? new ClientDeleteResult.Success()
                : new ClientDeleteResult.FailureUnknown($"Unknown failure deleting {clientUuid}");
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Delete client failure");
            return new ClientDeleteResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
    }

    public async Task<ClientResetResult> ResetCredentialsAsync(string clientUuid)
    {
        try
        {
            var newSecret = ClientSecretValidation.GenerateSecretWithMinimumLength(
                clientSecretValidationOptionsAccessor.Value
            );
            var client = await keycloakClientFacade.GetClientAsync(_realm, clientUuid);
            if (client is null)
            {
                return new ClientResetResult.FailureClientNotFound($"Client {clientUuid} not found");
            }

            client.Secret = newSecret;

            return await keycloakClientFacade.UpdateClientAsync(_realm, clientUuid, client)
                ? new ClientResetResult.Success(newSecret)
                : new ClientResetResult.FailureUnknown(
                    $"Unknown failure updating client secret for {clientUuid}"
                );
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Reset client credentials failure");
            return ex.StatusCode == 404
                ? new ClientResetResult.FailureClientNotFound($"Client {clientUuid} not found")
                : new ClientResetResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reset client credentials failure");
            return new ClientResetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClientClientsResult> GetAllClientsAsync()
    {
        try
        {
            var clients = await keycloakClientFacade.GetClientsAsync(_realm);
            return new ClientClientsResult.Success(clients.Select(x => x.ClientId).ToList());
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Get all clients failure");
            return new ClientClientsResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get all clients failure");
            return new ClientClientsResult.FailureUnknown(ex.Message);
        }
    }

    private async Task CheckAndCreateClientScopeAsync(string scope)
    {
        bool scopeExists = await ClientScopeExistsAsync(scope);

        if (!scopeExists)
        {
            await keycloakClientFacade.CreateClientScopeAsync(
                _realm,
                new ClientScope()
                {
                    Name = scope,
                    Protocol = "openid-connect",
                    ProtocolMappers = new List<ProtocolMapper>([
                        new ProtocolMapper()
                        {
                            Name = "audience resolve",
                            Protocol = "openid-connect",
                            _ProtocolMapper = "oidc-audience-resolve-mapper",
                            ConsentRequired = false,
                            Config = new Dictionary<string, string>
                            {
                                { "introspection.token.claim", "true" },
                                { "access.token.claim", "true" },
                            },
                        },
                    ]),
                    Attributes = new Attributes() { IncludeInTokenScope = "true" },
                }
            );
        }
    }

    private async Task<bool> ClientScopeExistsAsync(string scope)
    {
        var clientScopes = await keycloakClientFacade.GetClientScopesAsync(_realm);
        ClientScope? clientScope = clientScopes.FirstOrDefault(x => x.Name.Equals(scope));
        return clientScope != null;
    }

    private static IdentityProviderError ExceptionToKeycloakError(FlurlHttpException ex)
    {
        return ex.StatusCode switch
        {
            null => new IdentityProviderError.Unreachable(ex.Message),
            401 => new IdentityProviderError.Unauthorized(ex.Message),
            403 => new IdentityProviderError.Forbidden(ex.Message),
            404 => new IdentityProviderError.NotFound(ex.Message),
            _ => new IdentityProviderError("Unknown"),
        };
    }

    public async Task<ClientUpdateResult> UpdateClientAsync(
        string clientUuid,
        string displayName,
        string scope,
        string educationOrganizationIds,
        long[]? dataStoreIds = null,
        bool isApproved = true,
        string role = ""
    )
    {
        try
        {
            var client = await keycloakClientFacade.GetClientAsync(_realm, clientUuid);
            if (client is null)
            {
                return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
            }
            await CheckAndCreateClientScopeAsync(scope);
            var scopeExists = await ClientScopeExistsAsync(scope);
            if (scopeExists)
            {
                // Delete the existing client
                await keycloakClientFacade.DeleteClientAsync(_realm, clientUuid);
                var protocolMappers = client.ProtocolMappers.ToList();
                CheckAndUpdateEducationOrganizationIds(protocolMappers);
                CheckAndUpdateDataStoreIds(protocolMappers, dataStoreIds);
                Client newClient = new()
                {
                    ClientId = client.ClientId,
                    Enabled = isApproved,
                    Secret = client.Secret,
                    Name = displayName,
                    ServiceAccountsEnabled = true,
                    DefaultClientScopes = [scope],
                    ProtocolMappers = protocolMappers,
                };
                // Re-create the client
                string? newClientId = await keycloakClientFacade.CreateClientAndRetrieveClientIdAsync(
                    _realm,
                    newClient
                );
                if (!string.IsNullOrEmpty(newClientId))
                {
                    if (!string.IsNullOrEmpty(role))
                    {
                        // Re-assign the service account role lost during delete-and-recreate
                        var realmRoles = await keycloakClientFacade.GetRolesAsync(_realm);
                        Role? clientRole = realmRoles.FirstOrDefault(x =>
                            x.Name.Equals(role, StringComparison.InvariantCultureIgnoreCase)
                        );

                        if (clientRole != null)
                        {
                            var serviceAccountUser = await keycloakClientFacade.GetUserForServiceAccountAsync(
                                _realm,
                                newClientId
                            );
                            _ = await keycloakClientFacade.AddRealmRoleMappingsToUserAsync(
                                _realm,
                                serviceAccountUser.Id,
                                [clientRole]
                            );
                        }
                        else
                        {
                            logger.LogError(
                                "Role {Role} not found in Keycloak realm; service account role mapping could not be restored for client {ClientId}",
                                SanitizeForLog(role),
                                SanitizeForLog(client.ClientId)
                            );
                            return new ClientUpdateResult.FailureUnknown(
                                $"Role '{role}' not found in Keycloak realm; service account role mapping could not be restored"
                            );
                        }
                    }

                    return new ClientUpdateResult.Success(Guid.Parse(newClientId));
                }
            }
            else
            {
                var scopeNotFound = $"Scope {scope} not found";
                logger.LogError("Specified scope {Scope} not found", SanitizeForLog(scope));
                return new ClientUpdateResult.FailureIdentityProvider(
                    new IdentityProviderError(scopeNotFound)
                );
            }

            logger.LogError("Update client failure");
            return new ClientUpdateResult.FailureUnknown($"Error while updating the client: {displayName}");
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Update client failure");
            return new ClientUpdateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update client failure");
            return new ClientUpdateResult.FailureUnknown(ex.Message);
        }

        void CheckAndUpdateEducationOrganizationIds(List<ClientProtocolMapper> protocolMappers)
        {
            var edOrgClaim = protocolMappers.Find(x =>
                x.Config["claim.name"].Equals("educationOrganizationIds")
            );
            if (edOrgClaim != null)
            {
                edOrgClaim.Config["claim.value"] = educationOrganizationIds;
            }
        }

        void CheckAndUpdateDataStoreIds(List<ClientProtocolMapper> protocolMappers, long[]? dataStoreIds)
        {
            // Remove existing data store IDs claim
            protocolMappers.RemoveAll(x =>
                x.Config.ContainsKey("claim.name") && x.Config["claim.name"].Equals("dataStoreIds")
            );

            // Add updated data store IDs if provided
            if (dataStoreIds != null && dataStoreIds.Length > 0)
            {
                var sortedDataStoreIds = string.Join(",", dataStoreIds.OrderBy(id => id));
                protocolMappers.Add(DataStoreIdsProtocolMapper(sortedDataStoreIds));
            }
        }
    }

    private ClientProtocolMapper NamespacePrefixProtocolMapper(string value)
    {
        return ProtocolMapper("Namespace Prefixes", "namespacePrefixes", value);
    }

    private ClientProtocolMapper EducationOrganizationProtocolMapper(string value)
    {
        return ProtocolMapper("Education Organization Ids", "educationOrganizationIds", value);
    }

    private ClientProtocolMapper DataStoreIdsProtocolMapper(string value)
    {
        return ProtocolMapper("Data Store IDs", "dataStoreIds", value);
    }

    private ClientProtocolMapper ProtocolMapper(string name, string claimName, string value)
    {
        return new()
        {
            Name = name,
            Protocol = "openid-connect",
            ProtocolMapper = "oidc-hardcoded-claim-mapper",
            Config = new Dictionary<string, string>
            {
                { "access.token.claim", "true" },
                { "claim.name", claimName },
                { "claim.value", value },
                { "id.token.claim", "true" },
                { "introspection.token.claim", "true" },
                { "jsonType.label", "String" },
                { "lightweight.claim", "false" },
                { "userinfo.token.claim", "true" },
            },
        };
    }

    private List<ClientProtocolMapper> ConfigServiceRoleProtocolMapper()
    {
        List<ClientProtocolMapper> protocolMappers =
        [
            new()
            {
                Name = "Configuration service role mapper",
                Protocol = "openid-connect",
                ProtocolMapper = "oidc-usermodel-realm-role-mapper",
                Config = new Dictionary<string, string>
                {
                    { "claim.name", keycloakContext.RoleClaimType },
                    { "jsonType.label", "String" },
                    { "user.attribute", "roles" },
                    { "multivalued", "true" },
                    { "id.token.claim", "true" },
                    { "access.token.claim", "true" },
                    { "userinfo.token.claim", "true" },
                },
            },
        ];
        return protocolMappers;
    }
}
