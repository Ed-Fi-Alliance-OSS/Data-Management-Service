// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using Keycloak.Net;
using Keycloak.Net.Models.Clients;
using Keycloak.Net.Models.Roles;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public class ClientRepository(KeycloakContext keycloakContext) : IClientRepository
{
    private readonly KeycloakClient _keycloakClient =
        new(
            keycloakContext.Url,
            keycloakContext.ClientSecret,
            new KeycloakOptions(adminClientId: keycloakContext.ClientId)
        );
    private readonly string _realm = keycloakContext.Realm!;

    public async Task<bool> CreateClientAsync(string clientId, string clientSecret, string displayName)
    {
        var realmRoles = await _keycloakClient.GetRolesAsync(_realm);

        Client client = new()
        {
            ClientId = clientId,
            Enabled = true,
            Secret = clientSecret,
            Name = displayName,
            ServiceAccountsEnabled = true,
            ProtocolMappers = ConfigServiceProtocolMapper(),
        };

        // Read service role from the realm
        Role? clientRole = realmRoles.FirstOrDefault(x =>
            x.Name.Equals(keycloakContext.ServiceRole, StringComparison.InvariantCultureIgnoreCase)
        );

        string? createdClientId = await _keycloakClient.CreateClientAndRetrieveClientIdAsync(_realm, client);
        if (!string.IsNullOrEmpty(createdClientId))
        {
            if (clientRole != null)
            {
                // Assign the service role to client's service account
                string serviceAccountUserId = await GetServiceAccountUserIdAsync(createdClientId);
                bool result = await _keycloakClient.AddRealmRoleMappingsToUserAsync(
                    _realm,
                    serviceAccountUserId,
                    [clientRole]
                );
                return result;
            }
            else
            {
                throw new Exception($"Role {keycloakContext.ServiceRole} not found.");
            }
        }
        else
        {
            throw new Exception($"Error while creating the client: {clientId}");
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

    public Task<bool> DeleteClientAsync(string clientId)
    {
        return _keycloakClient.DeleteClientAsync(_realm, clientId);
    }

    public async Task<IEnumerable<string>> GetAllClientsAsync()
    {
        try
        {
            var clients = await _keycloakClient.GetClientsAsync(_realm);
            return clients.Select(x => x.ClientId).ToList();
        }
        catch (Exception ex)
        {
            throw new Exception(ex.Message);
        }
    }

    private async Task<string> GetServiceAccountUserIdAsync(string clientId)
    {
        try
        {
            var serviceAccountUser = await _keycloakClient.GetUserForServiceAccountAsync(_realm, clientId);
            return serviceAccountUser.Id;
        }
        catch (Exception ex)
        {
            throw new Exception(ex.Message);
        }
    }
}
