// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using Flurl.Http;
using Keycloak.Net;
using Keycloak.Net.Models.Clients;
using Keycloak.Net.Models.ClientScopes;
using Keycloak.Net.Models.ProtocolMappers;
using Keycloak.Net.Models.Roles;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public class KeycloakClientRepository(
    KeycloakContext keycloakContext,
    ILogger<KeycloakClientRepository> logger
) : IIdentityProviderRepository
{
    private readonly KeycloakClient _keycloakClient = new(
        $"{keycloakContext.Url.Trim('/')}/",
        keycloakContext.ClientSecret,
        new KeycloakOptions(adminClientId: keycloakContext.ClientId)
    );
    private readonly string _realm = keycloakContext.Realm;

    public async Task<ClientCreateResult> CreateClientAsync(
        string clientId,
        string clientSecret,
        string role,
        string displayName,
        string scope,
        string namespacePrefixes,
        string educationOrganizationIds,
        long[]? dmsInstanceIds = null
    )
    {
        try
        {
            var protocolMappers = ConfigServiceRoleProtocolMapper();
            protocolMappers.Add(NamespacePrefixProtocolMapper(namespacePrefixes));
            protocolMappers.Add(EducationOrganizationProtocolMapper(educationOrganizationIds));

            // Add DMS instance IDs as sorted comma-separated string
            if (dmsInstanceIds != null && dmsInstanceIds.Length > 0)
            {
                var sortedInstanceIds = string.Join(",", dmsInstanceIds.OrderBy(id => id));
                protocolMappers.Add(DmsInstanceIdsProtocolMapper(sortedInstanceIds));
            }

            Client client = new()
            {
                ClientId = clientId,
                Enabled = true,
                Secret = clientSecret,
                Name = displayName,
                ServiceAccountsEnabled = true,
                DefaultClientScopes = [scope],
                ProtocolMappers = protocolMappers,
            };

            // Read role from the realm
            var realmRoles = await _keycloakClient.GetRolesAsync(_realm);
            Role? clientRole = realmRoles.FirstOrDefault(x =>
                x.Name.Equals(role, StringComparison.InvariantCultureIgnoreCase)
            );

            if (clientRole is null)
            {
                await _keycloakClient.CreateRoleAsync(_realm, new Role() { Name = role });

                clientRole = await _keycloakClient.GetRoleByNameAsync(_realm, role);
            }

            await CheckAndCreateClientScopeAsync(scope);

            string? createdClientUuid = await _keycloakClient.CreateClientAndRetrieveClientIdAsync(
                _realm,
                client
            );
            if (!string.IsNullOrEmpty(createdClientUuid))
            {
                if (clientRole != null)
                {
                    // Assign the service role to client's service account
                    var serviceAccountUser = await _keycloakClient.GetUserForServiceAccountAsync(
                        _realm,
                        createdClientUuid
                    );

                    _ = await _keycloakClient.AddRealmRoleMappingsToUserAsync(
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
                $"Error while creating the client: {clientId}. CreateClientAndRetrieveClientIdAsync returned empty string with no exception."
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
            var client = await _keycloakClient.GetClientAsync(_realm, clientUuid);

            // Delete the existing client
            await _keycloakClient.DeleteClientAsync(_realm, clientUuid);

            var protocolMappers = ConfigServiceRoleProtocolMapper();
            protocolMappers.Add(NamespacePrefixProtocolMapper(namespacePrefixes));
            Client newClient = new()
            {
                ClientId = client.ClientId,
                Enabled = true,
                Secret = client.Secret,
                Name = client.Name,
                ServiceAccountsEnabled = true,
                DefaultClientScopes = client.DefaultClientScopes,
                ProtocolMappers = protocolMappers,
            };
            // Re-create the client
            string? newClientId = await _keycloakClient.CreateClientAndRetrieveClientIdAsync(
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
            return await _keycloakClient.DeleteClientAsync(_realm, clientUuid)
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
            var credentials = await _keycloakClient.GenerateClientSecretAsync(_realm, clientUuid);
            return new ClientResetResult.Success(credentials.Value);
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Delete client failure");
            return new ClientResetResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete client failure");
            return new ClientResetResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClientClientsResult> GetAllClientsAsync()
    {
        try
        {
            var clients = await _keycloakClient.GetClientsAsync(_realm);
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
            await _keycloakClient.CreateClientScopeAsync(
                _realm,
                new ClientScope()
                {
                    Name = scope,
                    Protocol = "openid-connect",
                    ProtocolMappers = new List<ProtocolMapper>(
                        [
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
                        ]
                    ),
                    Attributes = new Attributes() { IncludeInTokenScope = "true" },
                }
            );
        }
    }

    private async Task<bool> ClientScopeExistsAsync(string scope)
    {
        var clientScopes = await _keycloakClient.GetClientScopesAsync(_realm);
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
        long[]? dmsInstanceIds = null
    )
    {
        try
        {
            var client = await _keycloakClient.GetClientAsync(_realm, clientUuid);
            await CheckAndCreateClientScopeAsync(scope);
            var scopeExists = await ClientScopeExistsAsync(scope);
            if (scopeExists)
            {
                // Delete the existing client
                await _keycloakClient.DeleteClientAsync(_realm, clientUuid);
                var protocolMappers = client.ProtocolMappers.ToList();
                CheckAndUpdateEducationOrganizationIds(protocolMappers);
                CheckAndUpdateDmsInstanceIds(protocolMappers, dmsInstanceIds);
                Client newClient = new()
                {
                    ClientId = client.ClientId,
                    Enabled = true,
                    Secret = client.Secret,
                    Name = displayName,
                    ServiceAccountsEnabled = true,
                    DefaultClientScopes = [scope],
                    ProtocolMappers = protocolMappers,
                };
                // Re-create the client
                string? newClientId = await _keycloakClient.CreateClientAndRetrieveClientIdAsync(
                    _realm,
                    newClient
                );
                if (!string.IsNullOrEmpty(newClientId))
                {
                    return new ClientUpdateResult.Success(Guid.Parse(newClientId));
                }
            }
            else
            {
                var scopeNotFound = $"Scope {scope} not found";
                logger.LogError(message: scopeNotFound);
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
            var edOrgClaim = protocolMappers.FirstOrDefault(x =>
                x.Config["claim.name"].Equals("educationOrganizationIds")
            );
            if (edOrgClaim != null)
            {
                edOrgClaim.Config["claim.value"] = educationOrganizationIds;
            }
        }

        void CheckAndUpdateDmsInstanceIds(List<ClientProtocolMapper> protocolMappers, long[]? dmsInstanceIds)
        {
            // Remove existing DMS instance IDs claim
            protocolMappers.RemoveAll(x =>
                x.Config.ContainsKey("claim.name") && x.Config["claim.name"].Equals("dmsInstanceIds")
            );

            // Add updated DMS instance IDs if provided
            if (dmsInstanceIds != null && dmsInstanceIds.Length > 0)
            {
                var sortedInstanceIds = string.Join(",", dmsInstanceIds.OrderBy(id => id));
                protocolMappers.Add(DmsInstanceIdsProtocolMapper(sortedInstanceIds));
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

    private ClientProtocolMapper DmsInstanceIdsProtocolMapper(string value)
    {
        return ProtocolMapper("DMS Instance IDs", "dmsInstanceIds", value);
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
