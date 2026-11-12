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

    public Task<bool> CreateRoleAsync(string realm, Role role) =>
        _keycloakClient.CreateRoleAsync(realm, role);

    public Task<Role> GetRoleByNameAsync(string realm, string roleName) =>
        _keycloakClient.GetRoleByNameAsync(realm, roleName);

    public Task<bool> CreateClientScopeAsync(string realm, ClientScope clientScope) =>
        _keycloakClient.CreateClientScopeAsync(realm, clientScope);

    public Task<string?> CreateClientAndRetrieveClientIdAsync(string realm, Client client) =>
        _keycloakClient.CreateClientAndRetrieveClientIdAsync(realm, client);

    public Task<Client> GetClientAsync(string realm, string clientUuid) =>
        _keycloakClient.GetClientAsync(realm, clientUuid);

    public Task<bool> UpdateClientAsync(string realm, string clientUuid, Client client) =>
        _keycloakClient.UpdateClientAsync(realm, clientUuid, client);

    public Task<bool> DeleteClientAsync(string realm, string clientUuid) =>
        _keycloakClient.DeleteClientAsync(realm, clientUuid);

    public Task<Credentials> GenerateClientSecretAsync(string realm, string clientUuid) =>
        _keycloakClient.GenerateClientSecretAsync(realm, clientUuid);

    public Task<IEnumerable<Client>> GetClientsAsync(string realm) => _keycloakClient.GetClientsAsync(realm);

    public Task<IEnumerable<Client>> GetClientsByClientIdAsync(
        string realm,
        string clientId,
        TimeSpan timeout,
        CancellationToken cancellationToken
    ) =>
        RunBoundedAsync(
            operationToken =>
                _keycloakClient.GetClientsAsync(realm, clientId, cancellationToken: operationToken),
            timeout,
            TimeProvider.System,
            cancellationToken
        );

    /// <summary>
    /// Bounds one admin call by <paramref name="timeout"/> and the caller's cancellation
    /// (DMS-1327 D-11.4). The token alone cannot do it: Keycloak.Net fetches its admin access
    /// token synchronously, without the cancellation token, before the call returns a task, so
    /// the operation runs on the thread pool and the wait is bounded here. On timeout the token
    /// handed to the operation is cancelled, which aborts the part of the call that observes it;
    /// the same happens on caller cancellation. An admin token fetch already in progress cannot be
    /// aborted and finishes on its own, its result discarded. A caller cancellation always surfaces as a fresh
    /// <see cref="OperationCanceledException"/>, never as the package's wrapped exception, whose
    /// message carries the request URL.
    /// </summary>
    internal static async Task<T> RunBoundedAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken operationToken = operationCancellation.Token;
        Task<T> call = Task.Run(() => operation(operationToken), CancellationToken.None);

        try
        {
            return await call.WaitAsync(timeout, timeProvider, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Explicit, not left to the link: callbacks run in reverse registration order, so the
            // wait can observe the caller's cancellation and dispose the linked source before the
            // link has cancelled it.
            await operationCancellation.CancelAsync();
            ObserveAbandoned(call);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TimeoutException) when (!call.IsCompleted)
        {
            await operationCancellation.CancelAsync();
            ObserveAbandoned(call);
            throw;
        }
    }

    private static void ObserveAbandoned(Task call) =>
        _ = call.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    public Task<IEnumerable<ClientScope>> GetClientScopesAsync(string realm) =>
        _keycloakClient.GetClientScopesAsync(realm);

    public Task<IEnumerable<ClientScope>> GetDefaultClientScopesAsync(string realm, string clientUuid) =>
        _keycloakClient.GetDefaultClientScopesAsync(realm, clientUuid);

    public Task<IEnumerable<ClientScope>> GetRealmDefaultClientScopesAsync(string realm) =>
        _keycloakClient.GetRealmDefaultClientScopesAsync(realm);

    public Task<bool> UpdateDefaultClientScopeAsync(string realm, string clientUuid, string clientScopeId) =>
        _keycloakClient.UpdateDefaultClientScopeAsync(realm, clientUuid, clientScopeId);

    public Task<bool> DeleteDefaultClientScopeAsync(string realm, string clientUuid, string clientScopeId) =>
        _keycloakClient.DeleteDefaultClientScopeAsync(realm, clientUuid, clientScopeId);

    public Task<User> GetUserForServiceAccountAsync(string realm, string clientUuid) =>
        _keycloakClient.GetUserForServiceAccountAsync(realm, clientUuid);

    public Task<bool> AddRealmRoleMappingsToUserAsync(string realm, string userId, IEnumerable<Role> roles) =>
        _keycloakClient.AddRealmRoleMappingsToUserAsync(realm, userId, roles);
}
