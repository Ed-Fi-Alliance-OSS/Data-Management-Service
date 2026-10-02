// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Tests.E2E.Keycloak;

/// <summary>A public client created for one scenario, with the user-flow access token its user obtained.</summary>
public sealed record KeycloakPublicClient(
    string ClientUuid,
    string ClientId,
    string UserUuid,
    string AccessToken
);

/// <summary>
/// The Keycloak resources <c>Revocation.feature</c> needs to observe token state (DMS-1327 D-16),
/// created for one feature run and removed afterwards:
/// <list type="bullet">
/// <item>a confidential observer client that only introspects; its credentials are held here and
/// are never sent to <c>/connect/revoke</c>;</item>
/// <item>a client scope with an audience mapper that puts the observer into the <c>aud</c> of every
/// observed access token, because Keycloak 26.4.12 and later refuse introspection to a client absent
/// from <c>aud</c> (§9.1.4 correction 2). It is attached to the existing clients whose tokens the
/// scenarios observe and detached again on disposal; it stays out of the token's <c>scope</c> claim
/// so the Configuration Service's own scope checks see the same value as without it;</item>
/// <item>public clients with a user, one per scenario that asks for one.</item>
/// </list>
/// Diagnostics follow the characterization harness: fixed text, operation, status and body kind only.
/// </summary>
public sealed class KeycloakRevocationObserver : IAsyncDisposable
{
    private readonly KeycloakCharacterizationApi _api;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _attachedExistingClientUuids = [];
    private readonly List<KeycloakPublicClient> _publicClients = [];
    private CharacterizationClient? _observer;
    private string? _audienceScopeUuid;
    private int _publicClientCount;

    private KeycloakRevocationObserver(KeycloakCharacterizationApi api) => _api = api;

    /// <summary>
    /// Creates the observer and the audience scope, and attaches the scope to each existing client
    /// in <paramref name="observedClientIds"/>. Anything created before a failure is removed again.
    /// </summary>
    public static async Task<KeycloakRevocationObserver> CreateAsync(IEnumerable<string> observedClientIds)
    {
        KeycloakRevocationObserver fixture = new(
            new KeycloakCharacterizationApi(
                KeycloakCharacterizationEnvironment.KeycloakUrl,
                KeycloakCharacterizationEnvironment.Realm,
                KeycloakCharacterizationEnvironment.AdminRealm,
                new SensitiveValueRegistry()
            )
        );

        try
        {
            await fixture.ProvisionAsync(observedClientIds);
            return fixture;
        }
        catch
        {
            try
            {
                await fixture.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                // The provisioning failure is the one to report; the cleanup problem is only logged.
                await TestContext.Progress.WriteLineAsync(
                    $"Revocation observer cleanup after a failed provisioning also failed: {cleanupFailure.GetType().FullName}"
                );
            }

            throw;
        }
    }

    /// <summary>The observer's view of an access token. A failed introspection throws; it is never "inactive".</summary>
    public Task<bool> IsActiveAsync(string accessToken) =>
        _api.IntrospectAsync(Observer.Credentials, accessToken, tokenTypeHint: null);

    /// <summary>
    /// Creates a public client with direct access grants and a user, puts the observer into the
    /// audience of its tokens, and returns the access token of a fresh password grant.
    /// </summary>
    public async Task<KeycloakPublicClient> CreatePublicClientAsync()
    {
        string adminToken = await GetAdminTokenAsync();
        string suffix = $"{_runId}-{++_publicClientCount}";
        string clientId = $"revocation-public-{suffix}";
        string clientUuid = await _api.CreateClientAsync(
            adminToken,
            new JsonObject
            {
                ["clientId"] = clientId,
                ["name"] = $"DMS-1327 revocation E2E public client {suffix}",
                ["protocol"] = "openid-connect",
                ["publicClient"] = true,
                ["directAccessGrantsEnabled"] = true,
                ["standardFlowEnabled"] = false,
                ["serviceAccountsEnabled"] = false,
            }
        );

        string userUuid = "";
        try
        {
            string username = $"revocation-user-{suffix}";
            string password = $"Pw-{Guid.NewGuid():N}!Aa1";
            userUuid = await _api.CreateUserAsync(
                adminToken,
                new JsonObject
                {
                    ["username"] = username,
                    ["enabled"] = true,
                    ["emailVerified"] = true,
                    ["email"] = $"{username}@example.invalid",
                    ["firstName"] = "Revocation",
                    ["lastName"] = suffix,
                    ["credentials"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "password",
                            ["value"] = password,
                            ["temporary"] = false,
                        }
                    ),
                }
            );
            await _api.AddDefaultClientScopeAsync(adminToken, clientUuid, AudienceScopeUuid);

            TokenGrant grant = await _api.RequestTokenAsync(
                new FormBody()
                    .Add("grant_type", "password")
                    .Add("client_id", clientId)
                    .Add("username", username)
                    .Add("password", password)
            );

            KeycloakPublicClient publicClient = new(clientUuid, clientId, userUuid, grant.AccessToken);
            _publicClients.Add(publicClient);
            return publicClient;
        }
        catch
        {
            // Not yet tracked, so remove what this call created before reporting the failure.
            try
            {
                if (userUuid.Length > 0)
                {
                    await _api.DeleteUserAsync(adminToken, userUuid);
                }

                await _api.DeleteClientAsync(adminToken, clientUuid);
            }
            catch (Exception cleanupFailure)
            {
                await TestContext.Progress.WriteLineAsync(
                    $"Public client cleanup after a failed setup also failed: {cleanupFailure.GetType().FullName}"
                );
            }

            throw;
        }
    }

    /// <summary>Deletes a public client created by <see cref="CreatePublicClientAsync"/> and its user.</summary>
    public async Task DeletePublicClientAsync(KeycloakPublicClient publicClient)
    {
        string adminToken = await GetAdminTokenAsync();
        await _api.DeleteUserAsync(adminToken, publicClient.UserUuid);
        await _api.DeleteClientAsync(adminToken, publicClient.ClientUuid);
        _publicClients.Remove(publicClient);
    }

    /// <summary>
    /// Detaches the audience scope from the existing clients, then deletes every public client and
    /// user still tracked, the observer and the scope. Every step is attempted; failures are
    /// reported together so a leaked resource is never silent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<Exception> failures = [];
        string? adminToken = null;
        try
        {
            adminToken = await GetAdminTokenAsync();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (adminToken is not null)
        {
            if (_audienceScopeUuid is not null)
            {
                foreach (string clientUuid in _attachedExistingClientUuids)
                {
                    await TryAsync(
                        () => _api.RemoveDefaultClientScopeAsync(adminToken, clientUuid, _audienceScopeUuid),
                        failures
                    );
                }
            }

            foreach (KeycloakPublicClient publicClient in _publicClients)
            {
                await TryAsync(() => _api.DeleteUserAsync(adminToken, publicClient.UserUuid), failures);
                await TryAsync(() => _api.DeleteClientAsync(adminToken, publicClient.ClientUuid), failures);
            }

            if (_observer is not null)
            {
                await TryAsync(() => _api.DeleteClientAsync(adminToken, _observer.Uuid), failures);
            }

            if (_audienceScopeUuid is not null)
            {
                await TryAsync(() => _api.DeleteClientScopeAsync(adminToken, _audienceScopeUuid), failures);
            }
        }

        _api.Dispose();

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"Cleanup of the revocation observer run {_runId} left resources in realm '{KeycloakCharacterizationEnvironment.Realm}'.",
                failures
            );
        }

        static async Task TryAsync(Func<Task> action, List<Exception> failures)
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private CharacterizationClient Observer =>
        _observer ?? throw new InvalidOperationException("The revocation observer client was not created.");

    private string AudienceScopeUuid =>
        _audienceScopeUuid
        ?? throw new InvalidOperationException("The revocation observer audience scope was not created.");

    // The master realm's admin token lives 60 seconds by default, so every admin operation fetches its own.
    private Task<string> GetAdminTokenAsync() =>
        _api.GetAdminTokenAsync(
            KeycloakCharacterizationEnvironment.AdminUsername,
            KeycloakCharacterizationEnvironment.AdminPassword
        );

    private async Task ProvisionAsync(IEnumerable<string> observedClientIds)
    {
        string adminToken = await GetAdminTokenAsync();

        string observerClientId = $"revocation-observer-{_runId}";
        string observerSecret = $"Secret-{Guid.NewGuid():N}!Aa1";
        string observerUuid = await _api.CreateClientAsync(
            adminToken,
            new JsonObject
            {
                ["clientId"] = observerClientId,
                ["name"] = $"DMS-1327 revocation E2E observer {_runId}",
                ["secret"] = observerSecret,
                ["protocol"] = "openid-connect",
                ["publicClient"] = false,
                ["serviceAccountsEnabled"] = false,
                ["standardFlowEnabled"] = false,
                ["directAccessGrantsEnabled"] = false,
            }
        );
        _observer = new CharacterizationClient(observerUuid, observerClientId, observerSecret);

        _audienceScopeUuid = await _api.CreateClientScopeAsync(
            adminToken,
            new JsonObject
            {
                ["name"] = $"revocation-observer-audience-{_runId}",
                ["protocol"] = "openid-connect",
                ["attributes"] = new JsonObject { ["include.in.token.scope"] = "false" },
                ["protocolMappers"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "observer audience",
                        ["protocol"] = "openid-connect",
                        ["protocolMapper"] = "oidc-audience-mapper",
                        ["config"] = new JsonObject
                        {
                            ["included.client.audience"] = observerClientId,
                            ["access.token.claim"] = "true",
                            ["introspection.token.claim"] = "true",
                            ["id.token.claim"] = "false",
                        },
                    }
                ),
            }
        );

        foreach (string clientId in observedClientIds)
        {
            string clientUuid = await _api.FindClientUuidAsync(adminToken, clientId);
            await _api.AddDefaultClientScopeAsync(adminToken, clientUuid, _audienceScopeUuid);
            _attachedExistingClientUuids.Add(clientUuid);
        }
    }
}
