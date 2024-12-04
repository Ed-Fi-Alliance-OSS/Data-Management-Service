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

public class KeycloakClientRepository(KeycloakContext keycloakContext, ILogger<KeycloakClientRepository> logger)
    : IClientRepository
{
    private readonly KeycloakClient _keycloakClient =
        new(
            keycloakContext.Url,
            keycloakContext.ClientSecret,
            new KeycloakOptions(adminClientId: keycloakContext.ClientId)
        );
    private readonly string _realm = keycloakContext.Realm;

    public async Task<ClientCreateResult> CreateClientAsync(
        string clientId,
        string clientSecret,
        string displayName
    )
    {
        try
        {
            Client client =
                new()
                {
                    ClientId = clientId,
                    Enabled = true,
                    Secret = clientSecret,
                    Name = displayName,
                    ServiceAccountsEnabled = true,
                    DefaultClientScopes = [keycloakContext.Scope],
                    ProtocolMappers = ConfigServiceProtocolMapper(),
                };

            // Read service role from the realm
            var realmRoles = await _keycloakClient.GetRolesAsync(_realm);
            Role? clientRole = realmRoles.FirstOrDefault(x =>
                x.Name.Equals(keycloakContext.ServiceRole, StringComparison.InvariantCultureIgnoreCase)
            );

            if (clientRole is null)
            {
                await _keycloakClient.CreateRoleAsync(
                    _realm,
                    new Role() { Name = keycloakContext.ServiceRole }
                );

                clientRole = await _keycloakClient.GetRoleByNameAsync(_realm, keycloakContext.ServiceRole);
            }

            var clientScopes = await _keycloakClient.GetClientScopesAsync(_realm);
            ClientScope? clientScope = clientScopes.FirstOrDefault(x => x.Name.Equals(keycloakContext.Scope));

            if (clientScope is null)
            {
                await _keycloakClient.CreateClientScopeAsync(
                    _realm,
                    new ClientScope()
                    {
                        Name = keycloakContext.Scope,
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
                    return new ClientCreateResult.FailureUnknown(
                        $"Role {keycloakContext.ServiceRole} not found."
                    );
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

        List<ClientProtocolMapper> ConfigServiceProtocolMapper()
        {
            return
            [
                new ClientProtocolMapper
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
}
