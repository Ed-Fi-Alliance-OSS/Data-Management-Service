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
    private readonly string _realm = keycloakContext.Realm;

    public async Task<ClientCreateResult> CreateClientAsync(
        string clientId,
        string clientSecret,
        string displayName
    )
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

        var createdClientUuid = await _keycloakClient.CreateClientAndRetrieveClientIdAsync(_realm, client);
        if (!string.IsNullOrEmpty(createdClientUuid))
        {
            if (clientRole != null)
            {
                // Assign the service role to client's service account
                var serviceAccountUserId = await GetServiceAccountUserIdAsync(createdClientUuid);
                var result = await _keycloakClient.AddRealmRoleMappingsToUserAsync(
                    _realm,
                    serviceAccountUserId,
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
        else
        {
            return new ClientCreateResult.FailureUnknown($"Error while creating the client: {clientId}");
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
        catch (Exception ex)
        {
            return new ClientDeleteResult.FailureUnknown(ex.Message);
        }
    }

    public async Task<ClientResetResult> ResetCredentialsAsync(string clientUuid)
    {
        try
        {
            var credentials = await _keycloakClient.GenerateClientSecretAsync(_realm, clientUuid);
            return new ClientResetResult.Success(credentials.Value);
        }
        catch (Exception ex)
        {
            return new ClientResetResult.FailureUnknown(ex.Message);
        }
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
