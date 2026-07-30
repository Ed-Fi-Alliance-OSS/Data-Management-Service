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
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ApplicationModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v3/applications/", InsertApplication);
        endpoints.MapSecuredGet("/v3/applications/", GetAll).Produces<List<ApplicationResponse>>(200);
        endpoints.MapSecuredGet($"/v3/applications/{{id}}", GetById).Produces<ApplicationResponse>(200);
        endpoints.MapSecuredPut($"/v3/applications/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v3/applications/{{id}}", Delete);

        // Only register the reset-credential endpoint if the feature flag is enabled.
        // It is recommended to disable this endpoint when using multiple API clients for a single application.
        // This avoids confusion and potential credential mismatches when resetting credentials, since
        // the reset operation only affects the first API client found.
        var enableResetEndpoint = endpoints
            .ServiceProvider.GetRequiredService<IOptions<AppSettings>>()
            .Value.EnableApplicationResetEndpoint;

        if (enableResetEndpoint)
        {
            endpoints.MapSecuredPut($"/v3/applications/{{id}}/reset-credential", ResetCredential);
        }
    }

    private async Task<IResult> InsertApplication(
        ApplicationInsertCommand command,
        ApplicationInsertCommand.Validator validator,
        HttpContext httpContext,
        IApplicationRepository applicationRepository,
        IVendorRepository vendorRepository,
        IDataStoreRepository dataStoreRepository,
        IIdentityProviderRepository clientRepository,
        IOptions<IdentitySettings> identitySettings,
        IOptions<ClientSecretValidationOptions> clientSecretValidationOptionsAccessor,
        ILogger<ApplicationModule> logger
    )
    {
        logger.LogDebug("Entering UpsertApplication");
        await validator.GuardAsync(command);

        var clientId = Guid.NewGuid().ToString();
        var clientSecret = ClientSecretValidation.GenerateSecretWithMinimumLength(
            clientSecretValidationOptionsAccessor.Value
        );

        string namespacePrefixes;
        switch (await vendorRepository.GetVendor(command.VendorId))
        {
            case VendorGetResult.Success success:
                namespacePrefixes = success.VendorResponse.NamespacePrefixes;
                break;
            case VendorGetResult.FailureUnknown failure:
                logger.LogError(
                    "Error validating VendorId: {Message}",
                    SanitizeForLog(failure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        "Reference 'VendorId' does not exist.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
        }

        // Validate references before creating the identity provider client so a failed
        // repository insert cannot leave an orphaned client behind.
        if (
            await ValidateDataStoreIdsExist(command.DataStoreIds, dataStoreRepository, httpContext, logger) is
            { } dataStoreFailure
        )
        {
            return dataStoreFailure;
        }

        var clientCreateResult = await clientRepository.CreateClientAsync(
            clientId,
            clientSecret,
            identitySettings.Value.ClientRole,
            command.ApplicationName,
            command.ClaimSetName,
            namespacePrefixes,
            string.Join(",", command.EducationOrganizationIds),
            command.DataStoreIds
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
                var repositoryResult = await applicationRepository.InsertApplication(
                    command,
                    new()
                    {
                        ClientId = clientId,
                        ClientUuid = clientSuccess.ClientUuid,
                        DataStoreIds = command.DataStoreIds,
                    }
                );

                switch (repositoryResult)
                {
                    case ApplicationInsertResult.Success success:
                        var request = httpContext.Request;
                        return Results.Created(
                            $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                            new ApplicationCredentialsResponse()
                            {
                                Id = success.Id,
                                Key = clientId,
                                Secret = clientSecret,
                            }
                        );
                    case ApplicationInsertResult.FailureVendorNotFound:
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        return Results.Json(
                            FailureResponse.ForUnresolvedReference(
                                "Reference 'VendorId' does not exist.",
                                httpContext.TraceIdentifier
                            ),
                            contentType: "application/problem+json",
                            statusCode: (int)HttpStatusCode.Conflict
                        );
                    case ApplicationInsertResult.FailureDataStoreNotFound:
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        return Results.Json(
                            FailureResponse.ForUnresolvedReference(
                                "Data store does not exist.",
                                httpContext.TraceIdentifier
                            ),
                            contentType: "application/problem+json",
                            statusCode: (int)HttpStatusCode.Conflict
                        );
                    case ApplicationInsertResult.FailureProfileNotFound:
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        return Results.Json(
                            FailureResponse.ForUnresolvedReference(
                                "Profile does not exist.",
                                httpContext.TraceIdentifier
                            ),
                            contentType: "application/problem+json",
                            statusCode: (int)HttpStatusCode.Conflict
                        );
                    case ApplicationInsertResult.FailureDuplicateApplication duplicateApp:
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        throw new ValidationException([
                            new ValidationFailure(
                                "ApplicationName",
                                $"Application '{duplicateApp.ApplicationName}' already exists for vendor."
                            ),
                        ]);
                    case ApplicationInsertResult.FailureUnknown failure:
                        logger.LogError("Failure creating client {Failure}", failure);
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                }

                break;
        }

        logger.LogError("Failure creating client");
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }

    private static async Task<IResult> GetAll(
        IApplicationRepository applicationRepository,
        [AsParameters] FrontendApplicationQuery query,
        ApplicationPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(query);
        ApplicationQueryResult getResult = await applicationRepository.QueryApplication(query.ToQuery());
        return getResult switch
        {
            ApplicationQueryResult.Success success => Results.Ok(success.ApplicationResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        int id,
        HttpContext httpContext,
        IApplicationRepository applicationRepository,
        ILogger<ApplicationModule> logger
    )
    {
        logger.LogDebug("Entering Application GetById for id: {Id}", id);
        ApplicationGetResult getResult = await applicationRepository.GetApplication(id);
        return getResult switch
        {
            ApplicationGetResult.Success success => Results.Ok(success.ApplicationResponse),
            ApplicationGetResult.FailureNotFound => FailureResults.NotFound(
                "Application not found",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static string SanitizeForLog(string? input)
    {
        return LoggingUtility.SanitizeForLog(input);
    }

    /// <summary>
    /// Validates that every requested data store id exists within the current tenant.
    /// Throws a ValidationException when one is missing, returns a failure result for
    /// infrastructure errors, and returns null when the request is valid.
    /// </summary>
    private static async Task<IResult?> ValidateDataStoreIdsExist(
        int[] dataStoreIds,
        IDataStoreRepository dataStoreRepository,
        HttpContext httpContext,
        ILogger<ApplicationModule> logger
    )
    {
        if (dataStoreIds.Length == 0)
        {
            return null;
        }

        var existingIdsResult = await dataStoreRepository.GetExistingDataStoreIds(dataStoreIds);
        switch (existingIdsResult)
        {
            case DataStoreIdsExistResult.Success success
                when success.ExistingIds.Count != dataStoreIds.Distinct().Count():
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        "Data store does not exist.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case DataStoreIdsExistResult.FailureUnknown failure:
                logger.LogError(
                    "Error validating DataStoreIds: {Message}",
                    SanitizeForLog(failure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        return null;
    }

    /// <summary>
    /// Validates that every requested profile id exists. Throws a ValidationException
    /// when one is missing, returns a failure result for infrastructure errors, and
    /// returns null when the request is valid. Profiles are not tenant-scoped, so this
    /// existence check mirrors the repository's foreign-key validation exactly.
    /// </summary>
    private static async Task<IResult?> ValidateProfileIdsExist(
        int[] profileIds,
        IProfileRepository profileRepository,
        HttpContext httpContext,
        ILogger<ApplicationModule> logger
    )
    {
        foreach (var profileId in profileIds.Distinct())
        {
            switch (await profileRepository.GetProfile(profileId))
            {
                case ProfileGetResult.Success:
                    break;
                case ProfileGetResult.FailureNotFound:
                    return Results.Json(
                        FailureResponse.ForUnresolvedReference(
                            "Profile does not exist.",
                            httpContext.TraceIdentifier
                        ),
                        contentType: "application/problem+json",
                        statusCode: (int)HttpStatusCode.Conflict
                    );
                case ProfileGetResult.FailureUnknown failure:
                    logger.LogError("Error validating ProfileId: {Message}", SanitizeForLog(failure.Message));
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
            }
        }

        return null;
    }

    private static async Task<IResult> Update(
        int id,
        ApplicationUpdateCommand.Validator validator,
        ApplicationUpdateCommand command,
        HttpContext httpContext,
        IApplicationRepository repository,
        IApiClientRepository apiClientRepository,
        IVendorRepository vendorRepository,
        IDataStoreRepository dataStoreRepository,
        IProfileRepository profileRepository,
        IIdentityProviderRepository clientRepository,
        IApplicationLockManager lockManager,
        IOptions<IdentitySettings> identitySettings,
        ILogger<ApplicationModule> logger
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException([
                new ValidationFailure("Id", "Request body id must match the id in the url."),
            ]);
        }

        // Every read this workflow relies on happens under the aggregate lock, so acquisition
        // precedes them all; invalid input above never consumes a lock.
        var lockResult = await lockManager.AcquireAsync(id, httpContext.RequestAborted);
        if (LockFailureResult(lockResult, httpContext, logger) is { } lockFailure)
        {
            return lockFailure;
        }

        await using var applicationLock = ((ApplicationLockResult.Acquired)lockResult).Handle;

        // Resolve the application before validating references so a request for a
        // missing or foreign-tenant application is answered with 404 regardless of
        // what references the request body carries.
        var apiClientsResult = await repository.GetApplicationApiClients(id);

        switch (apiClientsResult)
        {
            case ApplicationApiClientsResult.Success success:
                var client = success.Clients.FirstOrDefault();
                if (client != null)
                {
                    // Validate the request's references (vendor, data stores, profiles)
                    // before mutating the identity provider so a rejected reference cannot
                    // leave the client update stranded ahead of a failed repository update.
                    // The duplicate-application-name constraint is still enforced by the
                    // repository after this point; a collision there is reconciled by the
                    // next successful update rather than pre-validated here.
                    switch (await vendorRepository.GetVendor(command.VendorId))
                    {
                        case VendorGetResult.Success:
                            break;
                        case VendorGetResult.FailureUnknown vendorFailure:
                            logger.LogError(
                                "Error validating VendorId: {Message}",
                                SanitizeForLog(vendorFailure.FailureMessage)
                            );
                            return FailureResults.Unknown(httpContext.TraceIdentifier);
                        default:
                            return Results.Json(
                                FailureResponse.ForUnresolvedReference(
                                    "Reference 'VendorId' does not exist.",
                                    httpContext.TraceIdentifier
                                ),
                                contentType: "application/problem+json",
                                statusCode: (int)HttpStatusCode.Conflict
                            );
                    }

                    if (
                        await ValidateDataStoreIdsExist(
                            command.DataStoreIds,
                            dataStoreRepository,
                            httpContext,
                            logger
                        ) is
                        { } dataStoreFailure
                    )
                    {
                        return dataStoreFailure;
                    }

                    if (
                        await ValidateProfileIdsExist(
                            command.ProfileIds,
                            profileRepository,
                            httpContext,
                            logger
                        ) is
                        { } profileFailure
                    )
                    {
                        return profileFailure;
                    }

                    // A failed repository update is compensated from the aggregate's exact
                    // current state, read under a row lock, so the update is refused when
                    // that state cannot be read.
                    ApplicationUpdateState originalState;
                    switch (await repository.GetApplicationUpdateState(id, client.ClientId))
                    {
                        case ApplicationUpdateStateResult.Success originalSuccess:
                            originalState = originalSuccess.State;
                            break;
                        case ApplicationUpdateStateResult.FailureNotExists:
                            return FailureResults.NotFound(
                                "Application not found",
                                httpContext.TraceIdentifier
                            );
                        case ApplicationUpdateStateResult.FailureUnknown originalFailure:
                            logger.LogError(
                                "Error reading the original state of Application {Id}: {Message}",
                                id,
                                SanitizeForLog(originalFailure.FailureMessage)
                            );
                            return FailureResults.Unknown(httpContext.TraceIdentifier);
                        default:
                            logger.LogError(
                                "Unexpected result reading the original state of Application {Id}",
                                id
                            );
                            return FailureResults.Unknown(httpContext.TraceIdentifier);
                    }

                    logger.LogInformation("Updating client {ClientId}", originalState.ClientId);
                    var clientUpdateResult = await clientRepository.UpdateClientAsync(
                        originalState.ClientUuid.ToString(),
                        command.ApplicationName,
                        command.ClaimSetName,
                        string.Join(",", command.EducationOrganizationIds),
                        command.DataStoreIds,
                        originalState.IsApproved,
                        identitySettings.Value.ClientRole
                    );
                    switch (clientUpdateResult)
                    {
                        case ClientUpdateResult.Success updateSuccess:
                            ApplicationUpdateResult applicationUpdateResult;
                            try
                            {
                                applicationUpdateResult = await repository.UpdateApplication(
                                    command,
                                    new()
                                    {
                                        ClientId = originalState.ClientId,
                                        ClientUuid = updateSuccess.ClientUuid,
                                        DataStoreIds = command.DataStoreIds,
                                    }
                                );
                            }
                            catch (Exception ex)
                            {
                                // A thrown repository failure enters the same authoritative
                                // outcome resolution as a returned unknown failure.
                                logger.LogError(ex, "Repository update threw for Application {Id}", id);
                                applicationUpdateResult = new ApplicationUpdateResult.FailureUnknown(
                                    "The repository update threw an exception."
                                );
                            }

                            // Restores the identity provider to the original state and persists
                            // whatever client UUID the rollback reports, guarded by the expected
                            // prior UUID so newer committed data is never overwritten. Providers
                            // that preserve the client's identity report the stored UUID
                            // unchanged, which the guard treats as already applied; providers that
                            // replace the client report a new one that must be persisted.
                            async Task<bool> TryCompensateAsync()
                            {
                                logger.LogWarning(
                                    "Repository update failed for Application {Id}; rolling back the identity provider",
                                    id
                                );
                                ClientUpdateResult rollbackResult;
                                try
                                {
                                    rollbackResult = await clientRepository.UpdateClientAsync(
                                        updateSuccess.ClientUuid.ToString(),
                                        originalState.ApplicationName,
                                        originalState.ClaimSetName,
                                        string.Join(",", originalState.EducationOrganizationIds),
                                        originalState.ClientDataStoreIds,
                                        originalState.IsApproved,
                                        identitySettings.Value.ClientRole
                                    );
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(
                                        ex,
                                        "Identity provider rollback threw for Application {Id}; stored client state is inconsistent",
                                        id
                                    );
                                    return false;
                                }

                                if (rollbackResult is not ClientUpdateResult.Success rollbackSuccess)
                                {
                                    logger.LogError(
                                        "Identity provider rollback failed for Application {Id}; stored client state is inconsistent",
                                        id
                                    );
                                    return false;
                                }

                                var syncResult = await repository.SyncApplicationApiClientUuid(
                                    id,
                                    originalState.ClientId,
                                    originalState.ClientUuid,
                                    rollbackSuccess.ClientUuid
                                );
                                switch (syncResult)
                                {
                                    case ApiClientUuidSyncResult.Success
                                    or ApiClientUuidSyncResult.AlreadyApplied:
                                        return true;
                                    case ApiClientUuidSyncResult.FailureNotExistsSafeToDelete:
                                        // The application vanished during the rollback and the
                                        // rolled-back client is provably unreferenced, so it is
                                        // deleted rather than kept.
                                        await TryDeleteRecreatedClientAsync(rollbackSuccess.ClientUuid);
                                        return false;
                                    case ApiClientUuidSyncResult.FailureStaleState:
                                        logger.LogError(
                                            "The stored client state for Application {Id} changed outside the aggregate lock; nothing was deleted",
                                            id
                                        );
                                        return false;
                                    case ApiClientUuidSyncResult.FailureNotExists:
                                        logger.LogError(
                                            "The rollback client for Application {Id} is still referenced although the target row is missing; nothing was deleted",
                                            id
                                        );
                                        return false;
                                    case ApiClientUuidSyncResult.FailureUnknown syncFailure:
                                        logger.LogError(
                                            "Failed to persist the rolled-back client UUID for Application {Id}: {Message}; stored client state is inconsistent",
                                            id,
                                            SanitizeForLog(syncFailure.FailureMessage)
                                        );
                                        return false;
                                    default:
                                        logger.LogError(
                                            "Failed to persist the rolled-back client UUID for Application {Id}; stored client state is inconsistent",
                                            id
                                        );
                                        return false;
                                }
                            }

                            // Removes the identity provider client left by the update of a vanished
                            // application; a client that is already gone is the same end state.
                            async Task<bool> TryDeleteRecreatedClientAsync(Guid clientUuid)
                            {
                                ClientDeleteResult cleanupResult;
                                try
                                {
                                    cleanupResult = await clientRepository.DeleteClientAsync(
                                        clientUuid.ToString()
                                    );
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(
                                        ex,
                                        "Failed to delete the identity provider client for the missing Application {Id}; stored client state is inconsistent",
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
                                    "Failed to delete the identity provider client for the missing Application {Id}; stored client state is inconsistent",
                                    id
                                );
                                return false;
                            }

                            // No compensation deletion of the update's provider client without a
                            // definitive reference check.
                            async Task<bool> TryDeleteRecreatedClientCheckedAsync(Guid clientUuid)
                            {
                                var referenceResult = await apiClientRepository.HasApiClientUuidReference(
                                    clientUuid
                                );
                                if (
                                    referenceResult
                                    is ApiClientUuidReferenceResult.FailureUnknown referenceFailure
                                )
                                {
                                    logger.LogError(
                                        "Could not verify the identity provider client for Application {Id} is unreferenced: {Message}; leaving it in place",
                                        id,
                                        SanitizeForLog(referenceFailure.FailureMessage)
                                    );
                                    return false;
                                }

                                if (referenceResult is not ApiClientUuidReferenceResult.None)
                                {
                                    logger.LogError(
                                        "Cannot prove the identity provider client for Application {Id} is unreferenced; leaving it in place",
                                        id
                                    );
                                    return false;
                                }

                                return await TryDeleteRecreatedClientAsync(clientUuid);
                            }

                            bool MatchesCommand(ApplicationUpdateState resolved) =>
                                resolved.ApplicationName == command.ApplicationName
                                && resolved.VendorId == command.VendorId
                                && resolved.ClaimSetName == command.ClaimSetName
                                && SetEquals(
                                    resolved.EducationOrganizationIds,
                                    command.EducationOrganizationIds
                                )
                                && SetEquals(resolved.ProfileIds, command.ProfileIds)
                                && SetEquals(resolved.ClientDataStoreIds, command.DataStoreIds)
                                && resolved.ClientId == originalState.ClientId
                                && resolved.IsApproved == originalState.IsApproved
                                && resolved.ClientUuid == updateSuccess.ClientUuid;

                            bool MatchesOriginal(ApplicationUpdateState resolved) =>
                                resolved.ApplicationName == originalState.ApplicationName
                                && resolved.VendorId == originalState.VendorId
                                && resolved.ClaimSetName == originalState.ClaimSetName
                                && SetEquals(
                                    resolved.EducationOrganizationIds,
                                    originalState.EducationOrganizationIds
                                )
                                && SetEquals(resolved.ProfileIds, originalState.ProfileIds)
                                && SetEquals(resolved.ClientDataStoreIds, originalState.ClientDataStoreIds)
                                && resolved.ClientId == originalState.ClientId
                                && resolved.IsApproved == originalState.IsApproved
                                && resolved.ClientUuid == originalState.ClientUuid;

                            // An unknown or thrown repository outcome is resolved with the
                            // authoritative row-locking read, which waits out any in-flight
                            // commit before classifying the state.
                            async Task<IResult> ResolveAmbiguousOutcomeAsync()
                            {
                                switch (
                                    await repository.GetApplicationUpdateState(id, originalState.ClientId)
                                )
                                {
                                    case ApplicationUpdateStateResult.Success resolution
                                        when MatchesCommand(resolution.State):
                                        // The ambiguous transaction committed completely; the
                                        // provider and the database already hold the intended
                                        // state.
                                        return Results.NoContent();
                                    case ApplicationUpdateStateResult.Success resolution
                                        when MatchesOriginal(resolution.State):
                                        // The transaction provably did not commit; the provider is
                                        // restored, and the unknown failure stays a server error.
                                        await TryCompensateAsync();
                                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                                    case ApplicationUpdateStateResult.Success:
                                        logger.LogError(
                                            "Application {Id} is in a partially matching state after an ambiguous update; no compensation or cleanup was attempted and stored client state may be inconsistent",
                                            id
                                        );
                                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                                    case ApplicationUpdateStateResult.FailureNotExists:
                                        await TryDeleteRecreatedClientCheckedAsync(updateSuccess.ClientUuid);
                                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                                    case ApplicationUpdateStateResult.FailureUnknown resolutionFailure:
                                        logger.LogError(
                                            "Could not resolve the outcome of the failed update for Application {Id}: {Message}; no compensation or cleanup was attempted and stored client state may be inconsistent",
                                            id,
                                            SanitizeForLog(resolutionFailure.FailureMessage)
                                        );
                                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                                    default:
                                        logger.LogError(
                                            "Could not resolve the outcome of the failed update for Application {Id}; no compensation or cleanup was attempted and stored client state may be inconsistent",
                                            id
                                        );
                                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                                }
                            }

                            switch (applicationUpdateResult)
                            {
                                case ApplicationUpdateResult.Success:
                                    return Results.NoContent();
                                case ApplicationUpdateResult.FailureVendorNotFound:
                                    if (!await TryCompensateAsync())
                                    {
                                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                                    }

                                    return Results.Json(
                                        FailureResponse.ForUnresolvedReference(
                                            "Reference 'VendorId' does not exist.",
                                            httpContext.TraceIdentifier
                                        ),
                                        contentType: "application/problem+json",
                                        statusCode: (int)HttpStatusCode.Conflict
                                    );
                                case ApplicationUpdateResult.FailureDataStoreNotFound:
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
                                case ApplicationUpdateResult.FailureProfileNotFound:
                                    if (!await TryCompensateAsync())
                                    {
                                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                                    }

                                    return Results.Json(
                                        FailureResponse.ForUnresolvedReference(
                                            "Profile does not exist.",
                                            httpContext.TraceIdentifier
                                        ),
                                        contentType: "application/problem+json",
                                        statusCode: (int)HttpStatusCode.Conflict
                                    );
                                case ApplicationUpdateResult.FailureDuplicateApplication duplicateApp:
                                    if (!await TryCompensateAsync())
                                    {
                                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                                    }

                                    throw new ValidationException([
                                        new ValidationFailure(
                                            "ApplicationName",
                                            $"Application '{duplicateApp.ApplicationName}' already exists for vendor."
                                        ),
                                    ]);
                                case ApplicationUpdateResult.FailureNotExists:
                                    // The application row is gone, so the update's identity
                                    // provider client is deleted rather than restored, once the
                                    // reference check proves that is safe.
                                    if (!await TryDeleteRecreatedClientCheckedAsync(updateSuccess.ClientUuid))
                                    {
                                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                                    }

                                    return FailureResults.NotFound(
                                        "Application not found",
                                        httpContext.TraceIdentifier
                                    );
                                case ApplicationUpdateResult.FailureUnknown updateFailure:
                                    logger.LogError(
                                        "Repository update failed for Application {Id}: {Message}",
                                        id,
                                        SanitizeForLog(updateFailure.FailureMessage)
                                    );
                                    return await ResolveAmbiguousOutcomeAsync();
                                default:
                                    return await ResolveAmbiguousOutcomeAsync();
                            }

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
                            // The stored identity-provider client disappeared: an internal
                            // consistency failure, not caller input and not an upstream fault.
                            logger.LogError(
                                "Client not found in identity provider during Application {Id} update: {Message}",
                                id,
                                SanitizeForLog(notFound.FailureMessage)
                            );
                            return FailureResults.Unknown(httpContext.TraceIdentifier);
                        case ClientUpdateResult.FailureUnknown unknownFailure:
                            logger.LogError(
                                "Error updating client {ClientId} {ClientUuid}: {Message}",
                                originalState.ClientId,
                                originalState.ClientUuid,
                                unknownFailure.FailureMessage
                            );
                            return FailureResults.Unknown(httpContext.TraceIdentifier);
                    }
                }
                else
                {
                    return FailureResults.NotFound("Application not found", httpContext.TraceIdentifier);
                }
                break;
            case ApplicationApiClientsResult.FailureUnknown failure:
                logger.LogError("Error fetching ApiClients: {Failure}", failure);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }

    /// <summary>
    /// Maps a failed lock acquisition: a timeout is a retriable concurrency conflict, and an
    /// infrastructure failure is a sanitized server error. Returns null when the lock was
    /// acquired.
    /// </summary>
    private static IResult? LockFailureResult(
        ApplicationLockResult lockResult,
        HttpContext httpContext,
        ILogger<ApplicationModule> logger
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

    private static bool SetEquals<T>(T[] first, T[] second) => first.ToHashSet().SetEquals(second);

    private static async Task<IResult> Delete(
        int id,
        HttpContext httpContext,
        IApplicationRepository repository,
        IIdentityProviderRepository clientRepository,
        IApplicationLockManager lockManager,
        ILogger<ApplicationModule> logger
    )
    {
        logger.LogInformation("Deleting Application {Id}", id);

        // The lock is held across every provider client deletion and the database delete, so a
        // concurrent workflow cannot recreate or mutate a client in between.
        var lockResult = await lockManager.AcquireAsync(id, httpContext.RequestAborted);
        if (LockFailureResult(lockResult, httpContext, logger) is { } lockFailure)
        {
            return lockFailure;
        }

        await using var applicationLock = ((ApplicationLockResult.Acquired)lockResult).Handle;

        var apiClientsResult = await repository.GetApplicationApiClients(id);
        switch (apiClientsResult)
        {
            case ApplicationApiClientsResult.Success success:
                // The database application row is deleted only after every provider client is
                // deleted or proven already absent. Any provider failure returns before the
                // database is mutated, so the surviving application row and its ApiClient rows
                // remain the authoritative work list for a retry, which treats clients deleted
                // by an earlier attempt as idempotent cleanup successes and converges on full
                // deletion.
                foreach (var client in success.Clients)
                {
                    ClientDeleteResult clientDeleteResult;
                    try
                    {
                        logger.LogInformation("Deleting client {ClientId}", SanitizeForLog(client.ClientId));
                        clientDeleteResult = await clientRepository.DeleteClientAsync(
                            client.ClientUuid.ToString()
                        );
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Error deleting client {ClientId} {ClientUuid}: {Message}",
                            SanitizeForLog(client.ClientId),
                            client.ClientUuid,
                            SanitizeForLog(ex.Message)
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                    }

                    switch (clientDeleteResult)
                    {
                        case ClientDeleteResult.Success:
                            break;
                        case ClientDeleteResult.FailureClientNotFound:
                            // An already-missing client is idempotent cleanup success: a retried
                            // delete resumes here after a partial failure.
                            logger.LogInformation(
                                "Client {ClientId} {ClientUuid} is already absent from the identity provider",
                                SanitizeForLog(client.ClientId),
                                client.ClientUuid
                            );
                            break;
                        case ClientDeleteResult.FailureIdentityProvider failureIdentityProvider:
                            logger.LogError(
                                "Error deleting client {ClientId} from identity provider: {FailureMessage}",
                                SanitizeForLog(client.ClientId),
                                SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)
                            );
                            return FailureResults.BadGateway(
                                "Identity provider error during client deletion",
                                httpContext.TraceIdentifier
                            );
                        case ClientDeleteResult.FailureUnknown failureUnknown:
                            logger.LogError(
                                "Error deleting client {ClientId} {ClientUuid}: {FailureMessage}",
                                SanitizeForLog(client.ClientId),
                                client.ClientUuid,
                                SanitizeForLog(failureUnknown.FailureMessage)
                            );
                            return FailureResults.Unknown(httpContext.TraceIdentifier);
                        default:
                            logger.LogError(
                                "Unexpected result deleting client {ClientId} {ClientUuid}",
                                SanitizeForLog(client.ClientId),
                                client.ClientUuid
                            );
                            return FailureResults.Unknown(httpContext.TraceIdentifier);
                    }
                }

                break;
            case ApplicationApiClientsResult.FailureUnknown failure:
                logger.LogError("Error fetching ApiClients: {Failure}", failure);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        ApplicationDeleteResult deleteResult = await repository.DeleteApplication(id);

        if (deleteResult is ApplicationDeleteResult.FailureUnknown unknown)
        {
            logger.LogError("Error deleting Application {Id}: {Message}", id, unknown.FailureMessage);
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        return deleteResult switch
        {
            ApplicationDeleteResult.Success => Results.NoContent(),
            ApplicationDeleteResult.FailureNotExists => FailureResults.NotFound(
                "Application not found",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> ResetCredential(
        int id,
        HttpContext httpContext,
        IApplicationRepository repository,
        IIdentityProviderRepository clientRepository,
        IApplicationLockManager lockManager,
        ILogger<ApplicationModule> logger
    )
    {
        var lockResult = await lockManager.AcquireAsync(id, httpContext.RequestAborted);
        if (LockFailureResult(lockResult, httpContext, logger) is { } lockFailure)
        {
            return lockFailure;
        }

        await using var applicationLock = ((ApplicationLockResult.Acquired)lockResult).Handle;

        var apiClientsResult = await repository.GetApplicationApiClients(id);
        switch (apiClientsResult)
        {
            case ApplicationApiClientsResult.Success success:
                var client = success.Clients.FirstOrDefault();
                if (client != null)
                {
                    try
                    {
                        logger.LogInformation("Resetting client {ClientId}", client.ClientId);
                        var clientResetResult = await clientRepository.ResetCredentialsAsync(
                            client.ClientUuid.ToString()
                        );
                        switch (clientResetResult)
                        {
                            case ClientResetResult.Success resetSuccess:
                                return Results.Ok(
                                    new ApplicationCredentialsResponse()
                                    {
                                        Id = id,
                                        Key = client.ClientId,
                                        Secret = resetSuccess.ClientSecret,
                                    }
                                );
                            case ClientResetResult.FailureClientNotFound notFound:
                                logger.LogError(
                                    "Client not found in identity provider during credential reset: {Message}",
                                    SanitizeForLog(notFound.FailureMessage)
                                );
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            case ClientResetResult.FailureIdentityProvider failureIdentityProvider:
                                logger.LogError(
                                    "Identity provider error during credential reset: {Message}",
                                    SanitizeForLog(
                                        failureIdentityProvider.IdentityProviderError.FailureMessage
                                    )
                                );
                                return FailureResults.BadGateway(
                                    "Identity provider error during credential reset",
                                    httpContext.TraceIdentifier
                                );
                            case ClientResetResult.FailureUnknown failure:
                                logger.LogError(
                                    "Error resetting client credentials {ClientId} {ClientUuid}: {Message}",
                                    client.ClientId,
                                    client.ClientUuid,
                                    failure.FailureMessage
                                );
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Error resetting client credentials {ClientId} {ClientUuid}: {Message}",
                            client.ClientId,
                            client.ClientUuid,
                            ex.Message
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                    }
                }
                else
                {
                    return FailureResults.NotFound("Application not found", httpContext.TraceIdentifier);
                }
                break;
            case ApplicationApiClientsResult.FailureUnknown failure:
                logger.LogError("Error fetching ApiClients: {Failure}", failure);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }
}
