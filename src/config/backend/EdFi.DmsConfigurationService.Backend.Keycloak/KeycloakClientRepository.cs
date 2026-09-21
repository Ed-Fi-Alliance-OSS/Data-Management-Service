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

    /// <summary>
    /// Keycloak attaches and manages this scope itself for clients with a service account. It is
    /// not a realm default, so it is recognized by name and never removed by scope convergence.
    /// </summary>
    private static readonly string _serviceAccountScopeName = "service_account";

    /// <summary>
    /// Fixed phrases the create-path failure logs carry to say whether a client may exist. They
    /// are the operator's entry point into the recovery procedure: only an attempted creation can
    /// have left a client behind, and a creation that reported no usable identifier cannot be
    /// compensated because there is nothing to address the deletion to.
    /// </summary>
    private const string CreationNotAttempted = "creation not attempted";

    private const string CreationOutcomeUnconfirmed = "creation outcome unconfirmed";

    /// <summary>
    /// Provisioning phases named in the create-path failure logs, so an operator can tell which
    /// step failed and, from that, what the client's role state is.
    /// </summary>
    private const string ClientIdentifierParsePhase = "client-identifier-parse";

    private const string ServiceAccountLookupPhase = "service-account-lookup";

    private const string RoleAssignmentPhase = "role-assignment";

    public async Task<ClientCreateResult> CreateClientAsync(
        string clientId,
        string clientSecret,
        string role,
        string displayName,
        string scope,
        string namespacePrefixes,
        string educationOrganizationIds,
        int[]? dataStoreIds = null,
        bool isApproved = true
    )
    {
        // Tracks whether the create call has been issued, so a failure reaching the catches below
        // can state whether a client may exist. Nothing before that call leaves a client behind,
        // while the call itself can fail after Keycloak has already persisted one.
        string creationOutcome = CreationNotAttempted;

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

            // Preflight: the realm role and the claim-set scope are resolved before the client is
            // created, so a failure in either leaves nothing behind to compensate. Each provider
            // boolean is checked; a rejected creation is reported as a provider failure rather
            // than being discovered later, or never, through a follow-up lookup.
            var realmRoles = await keycloakClientFacade.GetRolesAsync(_realm);
            Role? clientRole = realmRoles.FirstOrDefault(x =>
                x.Name.Equals(role, StringComparison.InvariantCultureIgnoreCase)
            );

            if (clientRole is null)
            {
                if (!await keycloakClientFacade.CreateRoleAsync(_realm, new Role() { Name = role }))
                {
                    logger.LogError(
                        "Client provisioning failed during role-creation for client {ClientId}: the identity provider did not create the realm role {Role}",
                        SanitizeForLog(clientId),
                        SanitizeForLog(role)
                    );
                    return new ClientCreateResult.FailureIdentityProvider(
                        new IdentityProviderError("The identity provider did not create the realm role.")
                    );
                }

                clientRole = await keycloakClientFacade.GetRoleByNameAsync(_realm, role);
            }

            if (clientRole is null)
            {
                logger.LogError(
                    "Client provisioning failed during role-lookup for client {ClientId}: realm role {Role} not found",
                    SanitizeForLog(clientId),
                    SanitizeForLog(role)
                );
                return new ClientCreateResult.FailureUnknown($"Role {role} not found.");
            }

            if (!await CheckAndCreateClientScopeAsync(scope))
            {
                logger.LogError(
                    "Client provisioning failed during scope-creation for client {ClientId}: the identity provider did not create the client scope {Scope}",
                    SanitizeForLog(clientId),
                    SanitizeForLog(scope)
                );
                return new ClientCreateResult.FailureIdentityProvider(
                    new IdentityProviderError("The identity provider did not create the client scope.")
                );
            }

            creationOutcome = CreationOutcomeUnconfirmed;
            string? createdClientUuid = await keycloakClientFacade.CreateClientAndRetrieveClientIdAsync(
                _realm,
                client
            );

            if (string.IsNullOrEmpty(createdClientUuid))
            {
                logger.LogError(
                    "Error while creating the client {ClientId}: the identity provider returned no client identifier and raised no exception, so the {CreationOutcome}",
                    SanitizeForLog(clientId),
                    CreationOutcomeUnconfirmed
                );
                return new ClientCreateResult.FailureUnknown($"Error while creating the client: {clientId}");
            }

            // The client exists at the provider from here on, so every failure below is
            // compensated by deleting it: the request returns no credentials and persists no row,
            // which would leave a client nothing could ever address again.
            if (!Guid.TryParse(createdClientUuid, out Guid parsedClientUuid))
            {
                LogProvisioningFailure(ClientIdentifierParsePhase, clientId, createdClientUuid);
                return await CompensateFailedProvisioningAsync(
                    createdClientUuid,
                    clientId,
                    new ClientCreateResult.FailureUnknown(
                        "The identity provider returned a client identifier that is not a UUID."
                    )
                );
            }

            if (
                await ProvisionServiceAccountRoleAsync(createdClientUuid, clientId, clientRole) is
                { } provisioningFailure
            )
            {
                return await CompensateFailedProvisioningAsync(
                    createdClientUuid,
                    clientId,
                    provisioningFailure
                );
            }

            return new ClientCreateResult.Success(parsedClientUuid);
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(
                ex,
                "Create client failure for client {ClientId}; {CreationOutcome}",
                SanitizeForLog(clientId),
                creationOutcome
            );
            return new ClientCreateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Create client failure for client {ClientId}; {CreationOutcome}",
                SanitizeForLog(clientId),
                creationOutcome
            );
            return new ClientCreateResult.FailureUnknown(ex.Message);
        }
    }

    /// <summary>
    /// Assigns the resolved realm role to the service account of the client this request created.
    /// Returns <c>null</c> when the provider reported the assignment applied, otherwise the
    /// classified failure, already logged with the phase that produced it. Exceptions are
    /// classified here rather than at the method boundary because the caller has to compensate
    /// the client that now exists; a lost response leaves the assignment unconfirmed rather than
    /// known to be absent.
    /// </summary>
    private async Task<ClientCreateResult?> ProvisionServiceAccountRoleAsync(
        string createdClientUuid,
        string clientId,
        Role clientRole
    )
    {
        string phase = ServiceAccountLookupPhase;
        try
        {
            var serviceAccountUser = await keycloakClientFacade.GetUserForServiceAccountAsync(
                _realm,
                createdClientUuid
            );

            if (serviceAccountUser is null || string.IsNullOrEmpty(serviceAccountUser.Id))
            {
                LogProvisioningFailure(phase, clientId, createdClientUuid);
                return new ClientCreateResult.FailureUnknown(
                    "The identity provider returned no service account for the client."
                );
            }

            phase = RoleAssignmentPhase;
            if (
                !await keycloakClientFacade.AddRealmRoleMappingsToUserAsync(
                    _realm,
                    serviceAccountUser.Id,
                    [clientRole]
                )
            )
            {
                LogProvisioningFailure(phase, clientId, createdClientUuid);
                return new ClientCreateResult.FailureIdentityProvider(
                    new IdentityProviderError(
                        "The identity provider did not assign the realm role to the client's service account."
                    )
                );
            }

            return null;
        }
        catch (FlurlHttpException ex)
        {
            LogProvisioningFailure(phase, clientId, createdClientUuid, ex);
            return new ClientCreateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            LogProvisioningFailure(phase, clientId, createdClientUuid, ex);
            return new ClientCreateResult.FailureUnknown(ex.Message);
        }
    }

    /// <summary>
    /// Deletes the client a failed provisioning attempt created and decides the outcome the
    /// caller sees. A deletion the provider reports successful, a client it reports already
    /// absent, and a deletion it reports unsuccessful for a provider reason all keep the
    /// provisioning classification: the whole request is then provider attributable. An unknown
    /// result, an unrecognized one, or an unexpected exception is not, so the outcome becomes an
    /// unknown failure. The client is never recreated and no secret is ever reissued.
    /// </summary>
    private async Task<ClientCreateResult> CompensateFailedProvisioningAsync(
        string createdClientUuid,
        string clientId,
        ClientCreateResult provisioningFailure
    )
    {
        ClientDeleteResult cleanupResult;
        try
        {
            cleanupResult = await DeleteClientAsync(createdClientUuid);
        }
        catch (Exception ex)
        {
            // DeleteClientAsync classifies provider failures itself, so only an unexpected
            // exception arrives here, and it may have been raised after the deletion took effect.
            LogUnconfirmedCleanup(createdClientUuid, clientId, ex.GetType().Name, ex);
            return AsUnknownFailure(provisioningFailure);
        }

        switch (cleanupResult)
        {
            case ClientDeleteResult.Success:
                logger.LogInformation(
                    "Deleted provider client {ClientUuid} (client {ClientId}) after failed provisioning",
                    SanitizeForLog(createdClientUuid),
                    SanitizeForLog(clientId)
                );
                return provisioningFailure;

            case ClientDeleteResult.FailureClientNotFound:
                logger.LogWarning(
                    "Provider client {ClientUuid} (client {ClientId}) was already absent during cleanup after failed provisioning",
                    SanitizeForLog(createdClientUuid),
                    SanitizeForLog(clientId)
                );
                return provisioningFailure;

            case ClientDeleteResult.FailureIdentityProvider:
                LogUnconfirmedCleanup(
                    createdClientUuid,
                    clientId,
                    nameof(ClientDeleteResult.FailureIdentityProvider)
                );
                return provisioningFailure;

            case ClientDeleteResult.FailureUnknown:
                LogUnconfirmedCleanup(createdClientUuid, clientId, nameof(ClientDeleteResult.FailureUnknown));
                return AsUnknownFailure(provisioningFailure);

            default:
                LogUnconfirmedCleanup(createdClientUuid, clientId, cleanupResult.GetType().Name);
                return AsUnknownFailure(provisioningFailure);
        }

        static ClientCreateResult AsUnknownFailure(ClientCreateResult failure) =>
            failure is ClientCreateResult.FailureUnknown unknown
                ? unknown
                : new ClientCreateResult.FailureUnknown(
                    "The client created for this request could not be removed after its provisioning failed."
                );
    }

    private void LogProvisioningFailure(
        string phase,
        string clientId,
        string createdClientUuid,
        Exception? exception = null
    ) =>
        logger.LogError(
            exception,
            "Client provisioning failed during {Phase} for client {ClientId} (provider client {ClientUuid}); deleting the created client",
            phase,
            SanitizeForLog(clientId),
            SanitizeForLog(createdClientUuid)
        );

    private void LogUnconfirmedCleanup(
        string createdClientUuid,
        string clientId,
        string outcome,
        Exception? exception = null
    ) =>
        logger.LogError(
            exception,
            "Could not confirm deletion of provider client {ClientUuid} (client {ClientId}) after failed provisioning; cleanup outcome {Outcome}; the client may remain and must be reviewed manually",
            SanitizeForLog(createdClientUuid),
            SanitizeForLog(clientId),
            outcome
        );

    /// <summary>
    /// Rewrites one client's namespace-prefixes claim **in place**, under its existing UUID. The
    /// client's identity therefore survives every outcome — with it the secret, the service
    /// account and its realm role mappings, the claim-set default scope, and every unrelated
    /// claim — so no failure arising from this update can invalidate the UUID the database
    /// already stores. The claim-set scope is never touched, so no scope convergence is needed.
    /// </summary>
    public async Task<ClientUpdateResult> UpdateClientNamespaceClaimAsync(
        string clientUuid,
        string namespacePrefixes
    )
    {
        if (!Guid.TryParse(clientUuid, out Guid storedClientUuid))
        {
            logger.LogError("The stored client identifier is not a valid UUID");
            return new ClientUpdateResult.FailureUnknown("The stored client identifier is not a valid UUID.");
        }

        // The stored-client lookup is classified on its own, before any other phase can fail.
        // Keycloak answers a missing client with a 404 that Flurl raises as an exception, so
        // without this phase the disappearance of a stored client would be reported as an
        // upstream provider fault instead of the internal consistency failure it is.
        Client client;
        try
        {
            client = await keycloakClientFacade.GetClientAsync(_realm, clientUuid);
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Namespace claim update failure while reading the stored client");
            return ex.StatusCode == 404
                ? new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found")
                : new ClientUpdateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Namespace claim update failure while reading the stored client");
            return new ClientUpdateResult.FailureUnknown(ex.Message);
        }

        if (client is null)
        {
            logger.LogError("The stored client {ClientUuid} was not found", SanitizeForLog(clientUuid));
            return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
        }

        List<ClientProtocolMapper> protocolMappers = [.. client.ProtocolMappers ?? []];
        UpsertNamespacePrefixesClaim(protocolMappers, namespacePrefixes);
        client.ProtocolMappers = protocolMappers;
        // The secret is never written back. Keycloak leaves a client's secret untouched when the
        // representation omits it or carries null, so the fetched value — which a provider is
        // free to mask — can never overwrite the real credential. The model declares the property
        // non-nullable, but clearing it is exactly how the update opts out of sending it.
        client.Secret = null!;

        bool updated;
        try
        {
            updated = await keycloakClientFacade.UpdateClientAsync(_realm, clientUuid, client);
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Namespace claim update failure while updating the stored client");
            // The client is addressed directly here, so a 404 is unambiguously its disappearance.
            return ex.StatusCode == 404
                ? new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found")
                : new ClientUpdateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Namespace claim update failure while updating the stored client");
            return new ClientUpdateResult.FailureUnknown(ex.Message);
        }

        if (!updated)
        {
            logger.LogError(
                "Keycloak did not apply the namespace claim update for client {ClientUuid}",
                SanitizeForLog(clientUuid)
            );
            return new ClientUpdateResult.FailureUnknown($"Error while updating the client: {clientUuid}");
        }

        return new ClientUpdateResult.Success(storedClientUuid);
    }

    /// <summary>
    /// Sets the client's namespace-prefixes claim to the requested value, preserving every
    /// unrelated mapper. The first matching mapper keeps its identity and configuration and only
    /// its value changes; any further duplicates are removed so exactly one mapper carries the
    /// claim. A client carrying no such mapper receives the fully configured one.
    /// </summary>
    private void UpsertNamespacePrefixesClaim(
        List<ClientProtocolMapper> protocolMappers,
        string namespacePrefixes
    )
    {
        ClientProtocolMapper? namespaceClaim = protocolMappers.Find(mapper =>
            HasClaimName(mapper, "namespacePrefixes")
        );

        if (namespaceClaim is null)
        {
            protocolMappers.Add(NamespacePrefixProtocolMapper(namespacePrefixes));
            return;
        }

        namespaceClaim.Config["claim.value"] = namespacePrefixes;
        protocolMappers.RemoveAll(mapper =>
            !ReferenceEquals(mapper, namespaceClaim) && HasClaimName(mapper, "namespacePrefixes")
        );
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
            return ex.StatusCode == 404
                ? new ClientDeleteResult.FailureClientNotFound($"Client {clientUuid} not found")
                : new ClientDeleteResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
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

    /// <summary>
    /// Ensures the claim-set client scope exists. Returns <c>true</c> when it already exists or
    /// the provider reports it created; <c>false</c> when the provider reports the creation
    /// unsuccessful, so callers fail fast instead of proceeding against a scope that may be
    /// absent.
    /// </summary>
    private async Task<bool> CheckAndCreateClientScopeAsync(string scope)
    {
        if (await ClientScopeExistsAsync(scope))
        {
            return true;
        }

        return await keycloakClientFacade.CreateClientScopeAsync(
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

    private async Task<bool> ClientScopeExistsAsync(string scope) =>
        await FindClientScopeAsync(scope) != null;

    private async Task<ClientScope?> FindClientScopeAsync(string scope)
    {
        var clientScopes = await keycloakClientFacade.GetClientScopesAsync(_realm);
        return clientScopes.FirstOrDefault(x => x.Name.Equals(scope));
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
        int[]? dataStoreIds = null,
        bool isApproved = true,
        // Intentionally unused for Keycloak. The update preserves the client's identity, so its
        // service account and realm role mappings survive without any reassignment; the parameter
        // remains for interface compatibility with the other identity providers.
        string role = ""
    )
    {
        if (!Guid.TryParse(clientUuid, out Guid storedClientUuid))
        {
            logger.LogError("The stored client identifier is not a valid UUID");
            return new ClientUpdateResult.FailureUnknown("The stored client identifier is not a valid UUID.");
        }

        // The stored-client lookup is classified on its own, before any other phase can fail.
        // Keycloak answers a missing client with a 404 that Flurl raises as an exception, so
        // without this phase the disappearance of a stored client would be reported as an
        // upstream provider fault instead of the internal consistency failure it is.
        Client client;
        try
        {
            client = await keycloakClientFacade.GetClientAsync(_realm, clientUuid);
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Update client failure while reading the stored client");
            return ex.StatusCode == 404
                ? new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found")
                : new ClientUpdateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update client failure while reading the stored client");
            return new ClientUpdateResult.FailureUnknown(ex.Message);
        }

        if (client is null)
        {
            logger.LogError("The stored client {ClientUuid} was not found", SanitizeForLog(clientUuid));
            return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
        }

        // Preflight: everything the update needs is resolved and validated before the client is
        // mutated, so a rejected scope or an unreadable realm default never leaves the client
        // half-updated.
        string targetScopeId;
        List<ClientScope> assignedScopes;
        HashSet<string> realmDefaultScopeIds;
        try
        {
            if (!await CheckAndCreateClientScopeAsync(scope))
            {
                logger.LogError(
                    "Update client failure: the identity provider did not create the client scope {Scope} for client {ClientUuid}",
                    SanitizeForLog(scope),
                    SanitizeForLog(clientUuid)
                );
                return new ClientUpdateResult.FailureIdentityProvider(
                    new IdentityProviderError("The identity provider did not create the client scope.")
                );
            }

            ClientScope? targetScope = await FindClientScopeAsync(scope);
            if (targetScope is null || string.IsNullOrEmpty(targetScope.Id))
            {
                logger.LogError("Specified scope {Scope} not found", SanitizeForLog(scope));
                return new ClientUpdateResult.FailureIdentityProvider(
                    new IdentityProviderError($"Scope {scope} not found")
                );
            }

            targetScopeId = targetScope.Id;
            assignedScopes = [.. await keycloakClientFacade.GetDefaultClientScopesAsync(_realm, clientUuid)];
            realmDefaultScopeIds =
            [
                .. (await keycloakClientFacade.GetRealmDefaultClientScopesAsync(_realm))
                    .Select(realmScope => realmScope.Id)
                    .Where(id => !string.IsNullOrEmpty(id)),
            ];
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Update client failure while resolving the client scopes");
            return await ClassifyPossibleClientDisappearanceAsync(ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update client failure while resolving the client scopes");
            return new ClientUpdateResult.FailureUnknown(ex.Message);
        }

        // The stored client is updated in place under its existing UUID. Its identity, secret,
        // service account, and realm role mappings therefore survive every outcome, so no
        // failure can strand the UUID the database already holds.
        List<ClientProtocolMapper> protocolMappers = [.. client.ProtocolMappers ?? []];
        UpsertEducationOrganizationIdsClaim(protocolMappers);
        ReplaceDataStoreIdsClaim(protocolMappers, dataStoreIds);

        client.Name = displayName;
        client.Enabled = isApproved;
        client.ServiceAccountsEnabled = true;
        client.ProtocolMappers = protocolMappers;
        // The secret is never written back. Keycloak leaves a client's secret untouched when the
        // representation omits it or carries null, so the fetched value — which a provider is
        // free to mask — can never overwrite the real credential. The model declares the property
        // non-nullable, but clearing it is exactly how the update opts out of sending it.
        client.Secret = null!;

        bool updated;
        try
        {
            updated = await keycloakClientFacade.UpdateClientAsync(_realm, clientUuid, client);
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Update client failure while updating the stored client");
            // The client is addressed directly here, so a 404 is unambiguously its disappearance.
            return ex.StatusCode == 404
                ? new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found")
                : new ClientUpdateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update client failure while updating the stored client");
            return new ClientUpdateResult.FailureUnknown(ex.Message);
        }

        if (!updated)
        {
            logger.LogError(
                "Keycloak did not apply the update for client {ClientUuid}",
                SanitizeForLog(clientUuid)
            );
            return new ClientUpdateResult.FailureUnknown($"Error while updating the client: {clientUuid}");
        }

        // Keycloak ignores the representation's default client scopes, so the claim-set scope is
        // converged through the dedicated assignments. Stale scopes are removed *before* the
        // target is assigned: an interrupted convergence then leaves the client without its
        // claim-set scope — losing access until a retry — rather than holding the old and new
        // authorization grants at the same time. Both operations are idempotent, so an identical
        // retry converges.
        try
        {
            foreach (
                ClientScope staleScope in assignedScopes
                    .Where(assigned =>
                        IsRemovableClaimSetScope(assigned, targetScopeId, realmDefaultScopeIds)
                    )
                    .ToList()
            )
            {
                if (
                    !await keycloakClientFacade.DeleteDefaultClientScopeAsync(
                        _realm,
                        clientUuid,
                        staleScope.Id
                    )
                )
                {
                    logger.LogError(
                        "Keycloak did not remove the stale default client scope from client {ClientUuid}",
                        SanitizeForLog(clientUuid)
                    );
                    return new ClientUpdateResult.FailureUnknown(
                        $"Error while updating the client: {clientUuid}"
                    );
                }
            }

            // The assignment is skipped when the target scope is already assigned, which also
            // makes an identical retry after a failed convergence a no-op here.
            if (
                !assignedScopes.Exists(assigned =>
                    targetScopeId.Equals(assigned.Id, StringComparison.Ordinal)
                )
                && !await keycloakClientFacade.UpdateDefaultClientScopeAsync(
                    _realm,
                    clientUuid,
                    targetScopeId
                )
            )
            {
                logger.LogError(
                    "Keycloak did not assign the requested default client scope to client {ClientUuid}",
                    SanitizeForLog(clientUuid)
                );
                return new ClientUpdateResult.FailureUnknown(
                    $"Error while updating the client: {clientUuid}"
                );
            }
        }
        catch (FlurlHttpException ex)
        {
            logger.LogError(ex, "Update client failure while converging the default client scopes");
            return await ClassifyPossibleClientDisappearanceAsync(ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update client failure while converging the default client scopes");
            return new ClientUpdateResult.FailureUnknown(ex.Message);
        }

        return new ClientUpdateResult.Success(storedClientUuid);

        void UpsertEducationOrganizationIdsClaim(List<ClientProtocolMapper> protocolMappers)
        {
            ClientProtocolMapper? edOrgClaim = protocolMappers.Find(mapper =>
                HasClaimName(mapper, "educationOrganizationIds")
            );

            if (edOrgClaim is not null)
            {
                edOrgClaim.Config["claim.value"] = educationOrganizationIds;
                return;
            }

            protocolMappers.Add(EducationOrganizationProtocolMapper(educationOrganizationIds));
        }

        void ReplaceDataStoreIdsClaim(List<ClientProtocolMapper> protocolMappers, int[]? dataStoreIds)
        {
            protocolMappers.RemoveAll(mapper => HasClaimName(mapper, "dataStoreIds"));

            if (dataStoreIds is { Length: > 0 })
            {
                protocolMappers.Add(
                    DataStoreIdsProtocolMapper(string.Join(",", dataStoreIds.OrderBy(id => id)))
                );
            }
        }

        // A 404 from a client-scope operation is ambiguous: either the client or the scope
        // reference is gone. The client is confirmed once so only its disappearance becomes an
        // internal consistency failure; everything else stays an upstream provider fault.
        async Task<ClientUpdateResult> ClassifyPossibleClientDisappearanceAsync(FlurlHttpException ex)
        {
            if (ex.StatusCode == 404 && await IsStoredClientMissingAsync())
            {
                return new ClientUpdateResult.FailureNotFound($"Client {clientUuid} not found");
            }

            return new ClientUpdateResult.FailureIdentityProvider(ExceptionToKeycloakError(ex));
        }

        async Task<bool> IsStoredClientMissingAsync()
        {
            try
            {
                return await keycloakClientFacade.GetClientAsync(_realm, clientUuid) is null;
            }
            catch (FlurlHttpException confirmationEx)
            {
                return confirmationEx.StatusCode == 404;
            }
            catch (Exception confirmationEx)
            {
                logger.LogError(
                    confirmationEx,
                    "Could not confirm whether client {ClientUuid} still exists",
                    SanitizeForLog(clientUuid)
                );
                return false;
            }
        }
    }

    private static bool HasClaimName(ClientProtocolMapper mapper, string claimName) =>
        mapper.Config is not null
        && mapper.Config.TryGetValue("claim.name", out string? configuredClaimName)
        && claimName.Equals(configuredClaimName, StringComparison.Ordinal);

    /// <summary>
    /// A default client scope is the client's own claim-set assignment — and therefore removable
    /// when the claim set changes — only when it is neither the requested target, nor a
    /// realm-managed default, nor the Keycloak-managed service account scope.
    /// </summary>
    private static bool IsRemovableClaimSetScope(
        ClientScope assigned,
        string targetScopeId,
        HashSet<string> realmDefaultScopeIds
    ) =>
        !string.IsNullOrEmpty(assigned.Id)
        && !targetScopeId.Equals(assigned.Id, StringComparison.Ordinal)
        && !realmDefaultScopeIds.Contains(assigned.Id)
        && !_serviceAccountScopeName.Equals(assigned.Name, StringComparison.Ordinal);

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
