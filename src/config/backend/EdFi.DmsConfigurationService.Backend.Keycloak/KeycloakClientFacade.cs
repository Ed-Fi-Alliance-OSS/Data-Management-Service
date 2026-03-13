// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Keycloak.Net;
using Keycloak.Net.Models.Clients;
using Keycloak.Net.Models.ClientScopes;
using Keycloak.Net.Models.Roles;
using Keycloak.Net.Models.Users;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public class KeycloakClientFacade(KeycloakContext keycloakContext) : IKeycloakClientFacade
{
    private readonly KeycloakClient _keycloakClient = new(
        $"{keycloakContext.Url.Trim('/')}/",
        keycloakContext.ClientSecret,
        new KeycloakOptions(adminClientId: keycloakContext.ClientId)
    );

    public Task<IEnumerable<Role>> GetRolesAsync(string realm) => _keycloakClient.GetRolesAsync(realm);

    public Task<bool> CreateRoleAsync(string realm, Role role)
        => _keycloakClient.CreateRoleAsync(realm, role);

    public Task<Role> GetRoleByNameAsync(string realm, string roleName)
        => _keycloakClient.GetRoleByNameAsync(realm, roleName);

    public Task<bool> CreateClientScopeAsync(string realm, ClientScope clientScope)
        => _keycloakClient.CreateClientScopeAsync(realm, clientScope);

    public Task<string?> CreateClientAndRetrieveClientIdAsync(string realm, Client client)
        => _keycloakClient.CreateClientAndRetrieveClientIdAsync(realm, client);

    public Task<Client> GetClientAsync(string realm, string clientUuid)
        => _keycloakClient.GetClientAsync(realm, clientUuid);

    public Task<bool> UpdateClientAsync(string realm, string clientUuid, Client client)
        => _keycloakClient.UpdateClientAsync(realm, clientUuid, client);

    public Task<bool> DeleteClientAsync(string realm, string clientUuid)
        => _keycloakClient.DeleteClientAsync(realm, clientUuid);

    public Task<Credentials> GenerateClientSecretAsync(string realm, string clientUuid)
        => _keycloakClient.GenerateClientSecretAsync(realm, clientUuid);

    public Task<IEnumerable<Client>> GetClientsAsync(string realm)
        => _keycloakClient.GetClientsAsync(realm);

    public Task<IEnumerable<ClientScope>> GetClientScopesAsync(string realm)
        => _keycloakClient.GetClientScopesAsync(realm);

    public Task<User> GetUserForServiceAccountAsync(string realm, string clientUuid)
        => _keycloakClient.GetUserForServiceAccountAsync(realm, clientUuid);

    public Task<bool> AddRealmRoleMappingsToUserAsync(
        string realm,
        string userId,
        IEnumerable<Role> roles
    ) => _keycloakClient.AddRealmRoleMappingsToUserAsync(realm, userId, roles);
}
