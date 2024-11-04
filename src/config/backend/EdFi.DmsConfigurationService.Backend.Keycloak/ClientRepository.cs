// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using Flurl.Http;
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
        try
        {
            var realmRoles = await _keycloakClient.GetRolesAsync(_realm);

            Client client =
                new()
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
            else
            {
                return new ClientCreateResult.FailureUnknown($"Error while creating the client: {clientId}");
            }
        }
        catch (FlurlHttpException ex)
        {
            return HandleFlurlHttpException<ClientCreateResult>(ex);
        }
        catch (Exception ex)
        {
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
            return HandleFlurlHttpException<ClientDeleteResult>(ex);
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
            return HandleFlurlHttpException<ClientResetResult>(ex);
        }
        catch (Exception ex)
        {
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
            return HandleFlurlHttpException<ClientClientsResult>(ex);
        }
        catch (Exception ex)
        {
            return new ClientClientsResult.FailureUnknown(ex.Message);
        }
    }

    private T HandleFlurlHttpException<T>(FlurlHttpException ex)
        where T : class
    {
        var errorMap = new Dictionary<int, KeycloakError>
        {
            { -1, new KeycloakError.KeycloakUnreachable(ex.Message) },
            { 401, new KeycloakError.BadCredentials(ex.Message) },
            { 403, new KeycloakError.InsufficientPermissions(ex.Message) },
            { 404, new KeycloakError.InvalidRealm(ex.Message) }
        };

        int statusCode = ex.StatusCode ?? -1;

        var keycloakError = errorMap.TryGetValue(statusCode, out var error) ? error : new KeycloakError();

        if (typeof(T) == typeof(ClientClientsResult))
        {
            return (T)(object)new ClientClientsResult.FailureKeycloak(keycloakError);
        }

        if (typeof(T) == typeof(ClientCreateResult))
        {
            return (T)(object)new ClientCreateResult.FailureKeycloak(keycloakError);
        }

        if (typeof(T) == typeof(ClientDeleteResult))
        {
            return (T)(object)new ClientDeleteResult.FailureKeycloak(keycloakError);
        }

        if (typeof(T) == typeof(ClientResetResult))
        {
            return (T)(object)new ClientResetResult.FailureKeycloak(keycloakError);
        }

        throw new InvalidOperationException($"Error creating instance of type {typeof(T).Name}.");
    }
}
