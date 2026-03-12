// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Keycloak.Net.Models.Clients;
using Keycloak.Net.Models.ClientScopes;
using Keycloak.Net.Models.Roles;
using Keycloak.Net.Models.Users;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public interface IKeycloakClientFacade
{
    Task<IList<Role>> GetRolesAsync(string realm);
    Task<bool> CreateRoleAsync(string realm, Role role);
    Task<Role> GetRoleByNameAsync(string realm, string roleName);
    Task<bool> CreateClientScopeAsync(string realm, ClientScope clientScope);
    Task<string?> CreateClientAndRetrieveClientIdAsync(string realm, Client client);
    Task<Client> GetClientAsync(string realm, string clientUuid);
    Task<bool> UpdateClientAsync(string realm, string clientUuid, Client client);
    Task<bool> DeleteClientAsync(string realm, string clientUuid);
    Task<Credentials> GenerateClientSecretAsync(string realm, string clientUuid);
    Task<IList<Client>> GetClientsAsync(string realm);
    Task<IList<ClientScope>> GetClientScopesAsync(string realm);
    Task<User> GetUserForServiceAccountAsync(string realm, string clientUuid);
    Task<bool> AddRealmRoleMappingsToUserAsync(string realm, string userId, IEnumerable<Role> roles);
}
