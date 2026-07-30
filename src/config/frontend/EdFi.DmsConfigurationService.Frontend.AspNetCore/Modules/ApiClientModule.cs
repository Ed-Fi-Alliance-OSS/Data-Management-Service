// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ApiClientModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapSecuredPost("/v3/apiClients/", InsertApiClient)
            .Produces<ApiClientCredentialsResponse>(201);
        endpoints.MapSecuredPut($"/v3/apiClients/{{id}}", UpdateApiClient);
        endpoints.MapSecuredDelete($"/v3/apiClients/{{id}}", DeleteApiClient);
        endpoints
            .MapSecuredPut($"/v3/apiClients/{{id}}/reset-credential", ResetCredential)
            .Produces<ApiClientCredentialsResponse>(200);
        // Limited access endpoints - accessible by service accounts for internal DMS operations
        endpoints.MapLimitedAccess("/v3/apiClients/", GetAll).Produces<List<ApiClientResponse>>(200);
        endpoints
            .MapLimitedAccess("/v3/apiClients/{clientId}", GetByClientId)
            .Produces<ApiClientResponse>(200);
    }

    private async Task<IResult> InsertApiClient(
        ApiClientInsertCommand command,
        ApiClientInsertCommand.Validator validator,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository,
        IApplicationRepository applicationRepository,
        IVendorRepository vendorRepository,
        IDataStoreRepository dataStoreRepository,
        IIdentityProviderRepository clientRepository,
        IOptions<IdentitySettings> identitySettings,
        IOptions<ClientSecretValidationOptions> clientSecretValidationOptionsAccessor,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering InsertApiClient");
        await validator.GuardAsync(command);

        // Validate Application exists and get application details
        ApplicationGetResult applicationResult = await applicationRepository.GetApplication(
            command.ApplicationId
        );
        if (applicationResult is ApplicationGetResult.FailureUnknown applicationFailure)
        {
            logger.LogError(
                "Error validating ApplicationId: {Message}",
                SanitizeForLog(applicationFailure.FailureMessage)
            );
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        if (applicationResult is not ApplicationGetResult.Success applicationSuccess)
        {
            return Results.Json(
                FailureResponse.ForUnresolvedReference(
                    $"Application with ID {command.ApplicationId} not found.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.Conflict
            );
        }

        ApplicationResponse application = applicationSuccess.ApplicationResponse;

        // Validate DataStoreIds exist (optimized single query)
        if (command.DataStoreIds.Length > 0)
        {
            var existingIdsResult = await dataStoreRepository.GetExistingDataStoreIds(command.DataStoreIds);
            if (existingIdsResult is DataStoreIdsExistResult.Success existingSuccess)
            {
                var notFoundIds = command
                    .DataStoreIds.Where(id => !existingSuccess.ExistingIds.Contains(id))
                    .ToList();

                if (notFoundIds.Count > 0)
                {
                    return Results.Json(
                        FailureResponse.ForUnresolvedReference(
                            $"The following DataStoreIds were not found in database: {string.Join(", ", notFoundIds)}",
                            httpContext.TraceIdentifier
                        ),
                        contentType: "application/problem+json",
                        statusCode: (int)HttpStatusCode.Conflict
                    );
                }
            }
            else if (existingIdsResult is DataStoreIdsExistResult.FailureUnknown failure)
            {
                logger.LogError("Error validating DataStoreIds: {Message}", failure.FailureMessage);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }
        }

        // Get vendor details for namespace prefixes
        string namespacePrefixes;
        switch (await vendorRepository.GetVendor(application.VendorId))
        {
            case VendorGetResult.Success success:
                namespacePrefixes = success.VendorResponse.NamespacePrefixes;
                break;
            default:
                logger.LogError(
                    "Application {ApplicationId}'s VendorId {VendorId} could not be resolved",
                    command.ApplicationId,
                    application.VendorId
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        var clientId = Guid.NewGuid().ToString();
        var clientSecret = ClientSecretValidation.GenerateSecretWithMinimumLength(
            clientSecretValidationOptionsAccessor.Value
        );

        Guid clientUuid;
        // Create the client in the identity provider first, with the correct enabled state
        // so a separate update-to-disable step is not needed (which would risk orphaning a client).
        var clientCreateResult = await clientRepository.CreateClientAsync(
            clientId,
            clientSecret,
            identitySettings.Value.ClientRole,
            application.ApplicationName,
            application.ClaimSetName,
            namespacePrefixes,
            string.Join(",", application.EducationOrganizationIds),
            command.DataStoreIds,
            command.IsApproved
        );

        switch (clientCreateResult)
        {
            case ClientCreateResult.FailureUnknown failure:
                logger.LogError("Failure creating client {Failure}", failure);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            case ClientCreateResult.FailureIdentityProvider failureIdentityProvider:
                logger.LogError(
                    "Failure creating client: {FailureMessage}",
                    SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)
                );
                return FailureResults.BadGateway(
                    "Identity provider error during client creation",
                    httpContext.TraceIdentifier
                );
            case ClientCreateResult.Success clientSuccess:
                clientUuid = clientSuccess.ClientUuid;
                break;
            default:
                logger.LogError("Failure creating client");
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        var repositoryResult = await apiClientRepository.InsertApiClient(
            command,
            new ApiClientCommand
            {
                ClientId = clientId,
                ClientUuid = clientUuid,
                DataStoreIds = command.DataStoreIds,
            }
        );

        switch (repositoryResult)
        {
            case ApiClientInsertResult.Success success:
                var request = httpContext.Request;
                return Results.Created(
                    $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                    new ApiClientCredentialsResponse
                    {
                        Id = success.Id,
                        ApplicationId = command.ApplicationId,
                        Name = command.Name,
                        Key = clientId,
                        Secret = clientSecret,
                    }
                );
            case ApiClientInsertResult.FailureApplicationNotFound:
                await clientRepository.DeleteClientAsync(clientUuid.ToString());
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        $"Application with ID {command.ApplicationId} not found.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApiClientInsertResult.FailureDataStoreNotFound:
                await clientRepository.DeleteClientAsync(clientUuid.ToString());
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        "Data store does not exist.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApiClientInsertResult.FailureUnknown failure:
                logger.LogError("Failure creating client {Failure}", failure);
                await clientRepository.DeleteClientAsync(clientUuid.ToString());
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        logger.LogError("Failure creating client");
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }

    private static async Task<IResult> GetAll(
        IApiClientRepository apiClientRepository,
        [AsParameters] FrontendApiClientQuery query,
        ApiClientPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(query);
        ApiClientQueryResult getResult = await apiClientRepository.QueryApiClient(query.ToQuery());
        return getResult switch
        {
            ApiClientQueryResult.Success success => Results.Ok(success.ApiClientResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetByClientId(
        string clientId,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository
    )
    {
        ApiClientGetResult getResult = await apiClientRepository.GetApiClientByClientId(clientId);
        return getResult switch
        {
            ApiClientGetResult.Success success => Results.Ok(success.ApiClientResponse),
            ApiClientGetResult.FailureNotFound => FailureResults.NotFound(
                "ApiClient not found",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
    /// Allows: letters, digits, spaces, and safe punctuation (_-.:/)
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        return LoggingUtility.SanitizeForLog(input);
    }

    private static async Task<IResult> UpdateApiClient(
        int id,
        ApiClientUpdateCommand command,
        ApiClientUpdateCommand.Validator validator,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository,
        IApplicationRepository applicationRepository,
        IVendorRepository vendorRepository,
        IDataStoreRepository dataStoreRepository,
        IIdentityProviderRepository identityProviderRepository,
        IApplicationLockManager lockManager,
        IOptions<IdentitySettings> identitySettings,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering UpdateApiClient for id: {Id}", SanitizeForLog(id.ToString()));

        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException([
                new ValidationFailure("Id", "Request body id must match the id in the url."),
            ]);
        }

        // A parent move must hold both aggregate locks; the helper acquires them in ascending
        // application id order and rereads the client under the locks.
        var (lockFailure, lockedApiClient, heldLocks) = await AcquireApiClientLocksAsync(
            id,
            command.ApplicationId,
            apiClientRepository,
            lockManager,
            httpContext,
            logger
        );
        if (lockFailure is not null)
        {
            return lockFailure;
        }

        ApiClientResponse existingApiClient = lockedApiClient!;
        try
        {
            // Validate Application exists and get application details
            ApplicationGetResult applicationResult = await applicationRepository.GetApplication(
                command.ApplicationId
            );
            if (applicationResult is ApplicationGetResult.FailureUnknown applicationFailure)
            {
                logger.LogError(
                    "Error validating ApplicationId: {Message}",
                    SanitizeForLog(applicationFailure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }
            if (applicationResult is not ApplicationGetResult.Success applicationSuccess)
            {
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        $"Application with ID {command.ApplicationId} not found.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            }

            ApplicationResponse application = applicationSuccess.ApplicationResponse;

            // Validate DataStoreIds exist (optimized single query)
            if (command.DataStoreIds.Length > 0)
            {
                var existingIdsResult = await dataStoreRepository.GetExistingDataStoreIds(
                    command.DataStoreIds
                );
                if (existingIdsResult is DataStoreIdsExistResult.Success existingIdsSuccess)
                {
                    var notFoundIds = command
                        .DataStoreIds.Where(id => !existingIdsSuccess.ExistingIds.Contains(id))
                        .ToList();

                    if (notFoundIds.Count > 0)
                    {
                        return Results.Json(
                            FailureResponse.ForUnresolvedReference(
                                $"The following DataStoreIds were not found in database: {string.Join(", ", notFoundIds)}",
                                httpContext.TraceIdentifier
                            ),
                            contentType: "application/problem+json",
                            statusCode: (int)HttpStatusCode.Conflict
                        );
                    }
                }
                else if (existingIdsResult is DataStoreIdsExistResult.FailureUnknown failure)
                {
                    logger.LogError(
                        "Error validating DataStoreIds: {Message}",
                        SanitizeForLog(failure.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                }
            }

            // Validate vendor exists
            if (await vendorRepository.GetVendor(application.VendorId) is not VendorGetResult.Success)
            {
                logger.LogError(
                    "Application {ApplicationId}'s VendorId {VendorId} could not be resolved",
                    command.ApplicationId,
                    application.VendorId
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }

            // A failed repository update is compensated from the client's current parent
            // application, so the update is refused when that state cannot be read; an
            // existing client whose parent is missing is a referential anomaly, not caller
            // input.
            ApplicationResponse originalApplication;
            switch (await applicationRepository.GetApplication(existingApiClient.ApplicationId))
            {
                case ApplicationGetResult.Success originalSuccess:
                    originalApplication = originalSuccess.ApplicationResponse;
                    break;
                case ApplicationGetResult.FailureUnknown originalFailure:
                    logger.LogError(
                        "Error reading the original application of ApiClient {Id}: {Message}",
                        id,
                        SanitizeForLog(originalFailure.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                default:
                    logger.LogError("The original application of ApiClient {Id} could not be resolved", id);
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
            }

            // Update client in identity provider FIRST
            var clientUpdateResult = await identityProviderRepository.UpdateClientAsync(
                existingApiClient.ClientUuid.ToString(),
                command.Name,
                application.ClaimSetName,
                string.Join(",", application.EducationOrganizationIds),
                command.DataStoreIds,
                command.IsApproved,
                identitySettings.Value.ClientRole
            );

            switch (clientUpdateResult)
            {
                case ClientUpdateResult.FailureUnknown failure:
                    logger.LogError(
                        "Failure updating client: {Failure}",
                        SanitizeForLog(failure.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case ClientUpdateResult.FailureIdentityProvider failureIdentityProvider:
                    logger.LogError(
                        "Failure updating client: {FailureMessage}",
                        SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)
                    );
                    return FailureResults.BadGateway(
                        "Identity provider error during client update",
                        httpContext.TraceIdentifier
                    );
                case ClientUpdateResult.FailureNotFound notFound:
                    logger.LogError(
                        "Client not found in identity provider: {Failure}",
                        SanitizeForLog(notFound.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case ClientUpdateResult.Success updateSuccess:
                    // Persist the client UUID the provider reports: unchanged when it updated the
                    // client in place, or a replacement when it recreated the client.
                    command.ClientUuid = updateSuccess.ClientUuid;

                    ApiClientUpdateResult repositoryResult;
                    try
                    {
                        repositoryResult = await apiClientRepository.UpdateApiClient(command);
                    }
                    catch (Exception ex)
                    {
                        // A thrown repository failure enters the same authoritative outcome
                        // resolution as a returned unknown failure.
                        logger.LogError(ex, "Repository update threw for ApiClient {Id}", id);
                        repositoryResult = new ApiClientUpdateResult.FailureUnknown(
                            "The repository update threw an exception."
                        );
                    }

                    // Restores the identity provider to the client's original state and persists
                    // whatever client UUID the rollback reports, guarded by the expected prior
                    // UUID so newer committed data is never overwritten. Providers that preserve
                    // the client's identity report the stored UUID unchanged, which the guard
                    // treats as already applied; providers that replace the client report a new
                    // one that must be persisted.
                    async Task<bool> TryCompensateAsync()
                    {
                        logger.LogWarning(
                            "Repository update failed for ApiClient {Id}; rolling back the identity provider",
                            id
                        );
                        ClientUpdateResult rollbackResult;
                        try
                        {
                            rollbackResult = await identityProviderRepository.UpdateClientAsync(
                                updateSuccess.ClientUuid.ToString(),
                                existingApiClient.Name,
                                originalApplication.ClaimSetName,
                                string.Join(",", originalApplication.EducationOrganizationIds),
                                [.. existingApiClient.DataStoreIds],
                                existingApiClient.IsApproved,
                                identitySettings.Value.ClientRole
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(
                                ex,
                                "Identity provider rollback threw for ApiClient {Id}; stored client state is inconsistent",
                                id
                            );
                            return false;
                        }

                        if (rollbackResult is not ClientUpdateResult.Success rollbackSuccess)
                        {
                            logger.LogError(
                                "Identity provider rollback failed for ApiClient {Id}; stored client state is inconsistent",
                                id
                            );
                            return false;
                        }

                        var syncResult = await apiClientRepository.SyncApiClientUuid(
                            id,
                            existingApiClient.ClientUuid,
                            rollbackSuccess.ClientUuid
                        );
                        switch (syncResult)
                        {
                            case ApiClientUuidSyncResult.Success or ApiClientUuidSyncResult.AlreadyApplied:
                                return true;
                            case ApiClientUuidSyncResult.FailureNotExistsSafeToDelete:
                                // The client vanished during the rollback and the rolled-back
                                // client is provably unreferenced, so it is deleted rather
                                // than kept.
                                await TryDeleteRecreatedClientAsync(rollbackSuccess.ClientUuid);
                                return false;
                            case ApiClientUuidSyncResult.FailureStaleState:
                                logger.LogError(
                                    "The stored state for ApiClient {Id} changed outside the aggregate lock; nothing was deleted",
                                    id
                                );
                                return false;
                            case ApiClientUuidSyncResult.FailureNotExists:
                                logger.LogError(
                                    "The rollback client for ApiClient {Id} is still referenced although the target row is missing; nothing was deleted",
                                    id
                                );
                                return false;
                            case ApiClientUuidSyncResult.FailureUnknown syncFailure:
                                logger.LogError(
                                    "Failed to persist the rolled-back client UUID for ApiClient {Id}: {Message}; stored client state is inconsistent",
                                    id,
                                    SanitizeForLog(syncFailure.FailureMessage)
                                );
                                return false;
                            default:
                                logger.LogError(
                                    "Failed to persist the rolled-back client UUID for ApiClient {Id}; stored client state is inconsistent",
                                    id
                                );
                                return false;
                        }
                    }

                    // Removes the identity provider client left by the update of a vanished
                    // ApiClient. A client that is already gone is the same end state.
                    async Task<bool> TryDeleteRecreatedClientAsync(Guid clientUuid)
                    {
                        ClientDeleteResult cleanupResult;
                        try
                        {
                            cleanupResult = await identityProviderRepository.DeleteClientAsync(
                                clientUuid.ToString()
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(
                                ex,
                                "Failed to delete the identity provider client for the missing ApiClient {Id}; stored client state is inconsistent",
                                id
                            );
                            return false;
                        }

                        if (
                            cleanupResult
                            is ClientDeleteResult.Success
                                or ClientDeleteResult.FailureClientNotFound
                        )
                        {
                            return true;
                        }

                        logger.LogError(
                            "Failed to delete the identity provider client for the missing ApiClient {Id}; stored client state is inconsistent",
                            id
                        );
                        return false;
                    }

                    // No compensation deletion of the update's provider client without a
                    // definitive reference check.
                    async Task<bool> TryDeleteRecreatedClientCheckedAsync(Guid clientUuid)
                    {
                        var referenceResult = await apiClientRepository.HasApiClientUuidReference(clientUuid);
                        if (referenceResult is ApiClientUuidReferenceResult.FailureUnknown referenceFailure)
                        {
                            logger.LogError(
                                "Could not verify the identity provider client for ApiClient {Id} is unreferenced: {Message}; leaving it in place",
                                id,
                                SanitizeForLog(referenceFailure.FailureMessage)
                            );
                            return false;
                        }

                        if (referenceResult is not ApiClientUuidReferenceResult.None)
                        {
                            logger.LogError(
                                "Cannot prove the identity provider client for ApiClient {Id} is unreferenced; leaving it in place",
                                id
                            );
                            return false;
                        }

                        return await TryDeleteRecreatedClientAsync(clientUuid);
                    }

                    bool MatchesCommand(ApiClientResolutionState resolved) =>
                        resolved.Name == command.Name
                        && resolved.ApplicationId == command.ApplicationId
                        && resolved.IsApproved == command.IsApproved
                        && SetEquals(resolved.DataStoreIds, command.DataStoreIds)
                        && resolved.ClientId == existingApiClient.ClientId
                        && resolved.ClientUuid == updateSuccess.ClientUuid;

                    bool MatchesOriginal(ApiClientResolutionState resolved) =>
                        resolved.Name == existingApiClient.Name
                        && resolved.ApplicationId == existingApiClient.ApplicationId
                        && resolved.IsApproved == existingApiClient.IsApproved
                        && SetEquals(resolved.DataStoreIds, existingApiClient.DataStoreIds)
                        && resolved.ClientId == existingApiClient.ClientId
                        && resolved.ClientUuid == existingApiClient.ClientUuid;

                    // An unknown or thrown repository outcome is resolved with the authoritative
                    // row-locking read, which waits out any in-flight commit before classifying
                    // the state.
                    async Task<IResult> ResolveAmbiguousOutcomeAsync()
                    {
                        switch (await apiClientRepository.GetApiClientResolutionState(id))
                        {
                            case ApiClientResolutionResult.Success resolution
                                when MatchesCommand(resolution.State):
                                // The ambiguous transaction committed completely; the provider
                                // and the database already hold the intended state.
                                return Results.NoContent();
                            case ApiClientResolutionResult.Success resolution
                                when MatchesOriginal(resolution.State):
                                // The transaction provably did not commit; the provider is
                                // restored, and the unknown failure stays a server error.
                                await TryCompensateAsync();
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            case ApiClientResolutionResult.Success:
                                logger.LogError(
                                    "ApiClient {Id} is in a partially matching state after an ambiguous update; no compensation or cleanup was attempted and stored client state may be inconsistent",
                                    id
                                );
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            case ApiClientResolutionResult.FailureNotExists:
                                await TryDeleteRecreatedClientCheckedAsync(updateSuccess.ClientUuid);
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            case ApiClientResolutionResult.FailureUnknown resolutionFailure:
                                logger.LogError(
                                    "Could not resolve the outcome of the failed update for ApiClient {Id}: {Message}; no compensation or cleanup was attempted and stored client state may be inconsistent",
                                    id,
                                    SanitizeForLog(resolutionFailure.FailureMessage)
                                );
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            default:
                                logger.LogError(
                                    "Could not resolve the outcome of the failed update for ApiClient {Id}; no compensation or cleanup was attempted and stored client state may be inconsistent",
                                    id
                                );
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                        }
                    }

                    switch (repositoryResult)
                    {
                        case ApiClientUpdateResult.Success:
                            return Results.NoContent();
                        case ApiClientUpdateResult.FailureNotFound:
                            // The ApiClient row is gone, so the update's identity provider
                            // client is deleted rather than restored, once the reference check
                            // proves that is safe.
                            if (!await TryDeleteRecreatedClientCheckedAsync(updateSuccess.ClientUuid))
                            {
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            }

                            return FailureResults.NotFound(
                                $"ApiClient with ID {id} not found.",
                                httpContext.TraceIdentifier
                            );
                        case ApiClientUpdateResult.FailureApplicationNotFound:
                            if (!await TryCompensateAsync())
                            {
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            }

                            return Results.Json(
                                FailureResponse.ForUnresolvedReference(
                                    $"Application with ID {command.ApplicationId} not found.",
                                    httpContext.TraceIdentifier
                                ),
                                contentType: "application/problem+json",
                                statusCode: (int)HttpStatusCode.Conflict
                            );
                        case ApiClientUpdateResult.FailureDataStoreNotFound:
                            if (!await TryCompensateAsync())
                            {
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            }

                            return Results.Json(
                                FailureResponse.ForUnresolvedReference(
                                    "Data store does not exist.",
                                    httpContext.TraceIdentifier
                                ),
                                contentType: "application/problem+json",
                                statusCode: (int)HttpStatusCode.Conflict
                            );
                        case ApiClientUpdateResult.FailureUnknown updateFailure:
                            logger.LogError(
                                "Repository update failed for ApiClient {Id}: {Message}",
                                id,
                                SanitizeForLog(updateFailure.FailureMessage)
                            );
                            return await ResolveAmbiguousOutcomeAsync();
                        default:
                            return await ResolveAmbiguousOutcomeAsync();
                    }
            }

            logger.LogError("Failure updating client");
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        finally
        {
            await DisposeLocksAsync(heldLocks);
        }
    }

    /// <summary>
    /// Acquires the aggregate lock for an ApiClient workflow: the client's current parent
    /// application and, when an update moves the client, the target application, always in
    /// ascending application id order. The client is reread under the held locks, and the
    /// acquisition retries when the parent changed while waiting; a persistent change is a
    /// retriable concurrency conflict.
    /// </summary>
    private static async Task<(
        IResult? Failure,
        ApiClientResponse? ApiClient,
        List<IAsyncDisposable> Locks
    )> AcquireApiClientLocksAsync(
        int id,
        int? targetApplicationId,
        IApiClientRepository apiClientRepository,
        IApplicationLockManager lockManager,
        HttpContext httpContext,
        ILogger<ApiClientModule> logger
    )
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var preRead = await apiClientRepository.GetApiClientById(id);
            if (preRead is ApiClientGetResult.FailureUnknown preReadFailure)
            {
                logger.LogError(
                    "Error retrieving ApiClient {Id}: {Message}",
                    id,
                    SanitizeForLog(preReadFailure.FailureMessage)
                );
                return (FailureResults.Unknown(httpContext.TraceIdentifier), null, []);
            }

            if (preRead is not ApiClientGetResult.Success preReadSuccess)
            {
                return (
                    FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier),
                    null,
                    []
                );
            }

            int sourceApplicationId = preReadSuccess.ApiClientResponse.ApplicationId;
            int[] applicationIdsToLock;
            if (targetApplicationId is { } target && target != sourceApplicationId)
            {
                applicationIdsToLock =
                    sourceApplicationId < target
                        ? [sourceApplicationId, target]
                        : [target, sourceApplicationId];
            }
            else
            {
                applicationIdsToLock = [sourceApplicationId];
            }

            List<IAsyncDisposable> heldLocks = [];
            try
            {
                foreach (int applicationIdToLock in applicationIdsToLock)
                {
                    var lockResult = await lockManager.AcquireAsync(
                        applicationIdToLock,
                        httpContext.RequestAborted
                    );
                    if (LockFailureResult(lockResult, httpContext, logger) is { } lockFailure)
                    {
                        await DisposeLocksAsync(heldLocks);
                        return (lockFailure, null, []);
                    }

                    heldLocks.Add(((ApplicationLockResult.Acquired)lockResult).Handle);
                }

                var underLock = await apiClientRepository.GetApiClientById(id);
                if (underLock is ApiClientGetResult.FailureUnknown underLockFailure)
                {
                    logger.LogError(
                        "Error retrieving ApiClient {Id}: {Message}",
                        id,
                        SanitizeForLog(underLockFailure.FailureMessage)
                    );
                    await DisposeLocksAsync(heldLocks);
                    return (FailureResults.Unknown(httpContext.TraceIdentifier), null, []);
                }

                if (underLock is not ApiClientGetResult.Success underLockSuccess)
                {
                    await DisposeLocksAsync(heldLocks);
                    return (
                        FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier),
                        null,
                        []
                    );
                }

                if (underLockSuccess.ApiClientResponse.ApplicationId != sourceApplicationId)
                {
                    // The client moved to another application while acquiring; retry against the
                    // new parent.
                    await DisposeLocksAsync(heldLocks);
                    continue;
                }

                return (null, underLockSuccess.ApiClientResponse, heldLocks);
            }
            catch
            {
                // A thrown acquisition or reread — including a propagated cancellation — must
                // not leak the locks already held.
                await DisposeLocksAsync(heldLocks);
                throw;
            }
        }

        return (
            Results.Json(
                FailureResponse.ForConflict(
                    "Unable to process the request due to a concurrent modification. Retry the request.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.Conflict
            ),
            null,
            []
        );
    }

    /// <summary>
    /// Maps a failed lock acquisition: a timeout is a retriable concurrency conflict, and an
    /// infrastructure failure is a sanitized server error. Returns null when the lock was
    /// acquired.
    /// </summary>
    private static IResult? LockFailureResult(
        ApplicationLockResult lockResult,
        HttpContext httpContext,
        ILogger<ApiClientModule> logger
    )
    {
        switch (lockResult)
        {
            case ApplicationLockResult.FailureTimeout:
                return Results.Json(
                    FailureResponse.ForConflict(
                        "Unable to process the request due to a concurrent modification. Retry the request.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApplicationLockResult.FailureUnknown failure:
                logger.LogError(
                    "Failed to acquire the application lock: {Message}",
                    SanitizeForLog(failure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                return null;
        }
    }

    private static async Task DisposeLocksAsync(List<IAsyncDisposable> heldLocks)
    {
        foreach (var heldLock in heldLocks)
        {
            await heldLock.DisposeAsync();
        }
    }

    private static bool SetEquals(IEnumerable<int> first, IEnumerable<int> second) =>
        first.ToHashSet().SetEquals(second);

    private static async Task<IResult> DeleteApiClient(
        int id,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository,
        IIdentityProviderRepository identityProviderRepository,
        IApplicationLockManager lockManager,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering DeleteApiClient for id: {Id}", SanitizeForLog(id.ToString()));

        // The lock is held across the provider deletion, the database delete, and any outcome
        // resolution, and the client is reread under it so a stale UUID is never targeted.
        var (lockFailure, lockedApiClient, heldLocks) = await AcquireApiClientLocksAsync(
            id,
            null,
            apiClientRepository,
            lockManager,
            httpContext,
            logger
        );
        if (lockFailure is not null)
        {
            return lockFailure;
        }

        ApiClientResponse apiClient = lockedApiClient!;
        try
        {
            // Delete from the identity provider first. A provider client this workflow deletes
            // stays deleted: the original secret is unrecoverable, so restoring the client could
            // not restore service and would only leave an active orphaned credential. On any
            // failure the database is untouched, so the surviving row remains the authoritative
            // work list and a retried delete converges — an already-missing provider client is
            // an idempotent cleanup success.
            ClientDeleteResult clientDeleteResult;
            try
            {
                logger.LogInformation("Deleting client {ClientId}", SanitizeForLog(apiClient.ClientId));
                clientDeleteResult = await identityProviderRepository.DeleteClientAsync(
                    apiClient.ClientUuid.ToString()
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Error deleting client {ClientId} {ClientUuid}: {Message}",
                    SanitizeForLog(apiClient.ClientId),
                    SanitizeForLog(apiClient.ClientUuid.ToString()),
                    SanitizeForLog(ex.Message)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }

            switch (clientDeleteResult)
            {
                case ClientDeleteResult.Success:
                    break;
                case ClientDeleteResult.FailureClientNotFound:
                    logger.LogInformation(
                        "Client {ClientId} {ClientUuid} is already absent from the identity provider",
                        SanitizeForLog(apiClient.ClientId),
                        SanitizeForLog(apiClient.ClientUuid.ToString())
                    );
                    break;
                case ClientDeleteResult.FailureIdentityProvider failureIdentityProvider:
                    logger.LogError(
                        "Error deleting client from identity provider: {FailureMessage}",
                        SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)
                    );
                    return FailureResults.BadGateway(
                        "Identity provider error during client deletion",
                        httpContext.TraceIdentifier
                    );
                case ClientDeleteResult.FailureUnknown failureUnknown:
                    logger.LogError(
                        "Error deleting client {ClientId} {ClientUuid}: {FailureMessage}",
                        SanitizeForLog(apiClient.ClientId),
                        SanitizeForLog(apiClient.ClientUuid.ToString()),
                        SanitizeForLog(failureUnknown.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                default:
                    logger.LogError(
                        "Unexpected result deleting client {ClientId} {ClientUuid}",
                        SanitizeForLog(apiClient.ClientId),
                        SanitizeForLog(apiClient.ClientUuid.ToString())
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
            }

            // Delete from the database second. A thrown repository failure enters the same
            // authoritative outcome resolution as a returned unknown failure.
            ApiClientDeleteResult deleteResult;
            try
            {
                deleteResult = await apiClientRepository.DeleteApiClient(id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Repository delete threw for ApiClient {Id}", id);
                deleteResult = new ApiClientDeleteResult.FailureUnknown(
                    "The repository delete threw an exception."
                );
            }

            // An unknown or thrown repository outcome is resolved with the authoritative
            // row-locking read, which waits out any in-flight commit before classifying the
            // state.
            async Task<IResult> ResolveAmbiguousDeleteOutcomeAsync()
            {
                ApiClientResolutionResult resolution;
                try
                {
                    resolution = await apiClientRepository.GetApiClientResolutionState(id);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Could not resolve the outcome of the failed delete for ApiClient {Id}",
                        id
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                }

                switch (resolution)
                {
                    case ApiClientResolutionResult.FailureNotExists:
                        // The ambiguous delete committed; the provider client and the row are
                        // both gone, which is the requested end state.
                        return Results.NoContent();
                    case ApiClientResolutionResult.Success:
                        logger.LogError(
                            "Database delete failed for ApiClient {Id}; the row survives as the retry marker while the stored provider client may already be deleted. Retry the delete to converge",
                            id
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                    case ApiClientResolutionResult.FailureUnknown resolutionFailure:
                        logger.LogError(
                            "Could not resolve the outcome of the failed delete for ApiClient {Id}: {Message}",
                            id,
                            SanitizeForLog(resolutionFailure.FailureMessage)
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                    default:
                        logger.LogError(
                            "Could not resolve the outcome of the failed delete for ApiClient {Id}",
                            id
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                }
            }

            switch (deleteResult)
            {
                case ApiClientDeleteResult.Success:
                    return Results.NoContent();
                case ApiClientDeleteResult.FailureNotFound:
                    // The repository's tenant-scoped existence precheck found no row inside its
                    // transaction — authoritative absence, so the requested end state already
                    // holds and no provider client is created for it.
                    logger.LogWarning("ApiClient {Id} was already absent from the database", id);
                    return FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier);
                case ApiClientDeleteResult.FailureUnknown deleteFailure:
                    logger.LogError(
                        "Database delete failed for ApiClient {Id}: {Message}",
                        id,
                        SanitizeForLog(deleteFailure.FailureMessage)
                    );
                    return await ResolveAmbiguousDeleteOutcomeAsync();
                default:
                    logger.LogError("Unexpected database delete result for ApiClient {Id}", id);
                    return await ResolveAmbiguousDeleteOutcomeAsync();
            }
        }
        finally
        {
            await DisposeLocksAsync(heldLocks);
        }
    }

    private async Task<IResult> ResetCredential(
        int id,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository,
        IIdentityProviderRepository identityProviderRepository,
        IApplicationLockManager lockManager,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering ResetCredential for id: {Id}", SanitizeForLog(id.ToString()));

        // The reset targets the UUID reread under the aggregate lock, so a concurrent workflow
        // that replaces the provider client cannot leave it aimed at a stale client.
        var (lockFailure, lockedApiClient, heldLocks) = await AcquireApiClientLocksAsync(
            id,
            null,
            apiClientRepository,
            lockManager,
            httpContext,
            logger
        );
        if (lockFailure is not null)
        {
            return lockFailure;
        }

        ApiClientResponse apiClient = lockedApiClient!;
        try
        {
            logger.LogInformation(
                "Resetting credentials for client {ClientId}",
                SanitizeForLog(apiClient.ClientId)
            );
            var clientResetResult = await identityProviderRepository.ResetCredentialsAsync(
                apiClient.ClientUuid.ToString()
            );

            return clientResetResult switch
            {
                ClientResetResult.Success resetSuccess => Results.Ok(
                    new ApiClientCredentialsResponse
                    {
                        Id = id,
                        ApplicationId = apiClient.ApplicationId,
                        Name = apiClient.Name,
                        Key = apiClient.ClientId,
                        Secret = resetSuccess.ClientSecret,
                    }
                ),
                ClientResetResult.FailureClientNotFound notFound => HandleStoredClientMissingOnReset(
                    notFound,
                    logger,
                    httpContext.TraceIdentifier
                ),
                ClientResetResult.FailureIdentityProvider failureIdentityProvider =>
                    HandleIdentityProviderResetFailure(
                        failureIdentityProvider,
                        logger,
                        httpContext.TraceIdentifier
                    ),
                ClientResetResult.FailureUnknown => FailureResults.Unknown(httpContext.TraceIdentifier),
                _ => FailureResults.Unknown(httpContext.TraceIdentifier),
            };
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Error resetting client credentials {ClientId} {ClientUuid}: {Message}",
                SanitizeForLog(apiClient.ClientId),
                SanitizeForLog(apiClient.ClientUuid.ToString()),
                SanitizeForLog(ex.Message)
            );
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        finally
        {
            await DisposeLocksAsync(heldLocks);
        }
    }

    private static IResult HandleIdentityProviderResetFailure(
        ClientResetResult.FailureIdentityProvider failureIdentityProvider,
        ILogger<ApiClientModule> logger,
        string traceIdentifier
    )
    {
        logger.LogError(
            "Identity provider error during credential reset: {FailureMessage}",
            SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)
        );

        return FailureResults.BadGateway("Identity provider error during credential reset", traceIdentifier);
    }

    private static IResult HandleStoredClientMissingOnReset(
        ClientResetResult.FailureClientNotFound notFound,
        ILogger<ApiClientModule> logger,
        string traceIdentifier
    )
    {
        logger.LogError(
            "Client not found in identity provider during credential reset: {Message}",
            SanitizeForLog(notFound.FailureMessage)
        );

        return FailureResults.Unknown(traceIdentifier);
    }
}
