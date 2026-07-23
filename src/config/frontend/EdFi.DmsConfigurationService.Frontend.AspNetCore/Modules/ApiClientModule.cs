// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
        endpoints
            .MapLimitedAccess("/v3/apiClients/", GetAll)
            .Produces<List<ApiClientResponse>>(200)
            .WithQueryParameterValidation<FrontendApiClientQuery>();
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
        ApplicationResponse application;
        switch (applicationResult)
        {
            case ApplicationGetResult.Success applicationSuccess:
                application = applicationSuccess.ApplicationResponse;
                break;
            case ApplicationGetResult.FailureUnknown appFailure:
                logger.LogError(
                    "Error resolving ApplicationId: {Message}",
                    SanitizeForLog(appFailure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                return FailureResults.UnresolvedReference(
                    "One or more referenced items could not be resolved. See 'errors' for details.",
                    httpContext.TraceIdentifier,
                    [$"Application with ID {command.ApplicationId} not found."]
                );
        }

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
                    return FailureResults.UnresolvedReference(
                        "One or more referenced items could not be resolved. See 'errors' for details.",
                        httpContext.TraceIdentifier,
                        [
                            $"The following DataStoreIds were not found in database: {string.Join(", ", notFoundIds)}",
                        ]
                    );
                }
            }
            else if (existingIdsResult is DataStoreIdsExistResult.FailureUnknown failure)
            {
                logger.LogError("Error validating DataStoreIds: {Message}", failure.FailureMessage);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }
        }

        // Get vendor details for namespace prefixes. The vendor is derived from the resolved
        // application (the caller submits ApplicationId, not VendorId), so a vendor that cannot be
        // resolved here is internal data inconsistency, not a client unresolved-reference: report a
        // sanitized 500 rather than a 409.
        string namespacePrefixes;
        switch (await vendorRepository.GetVendor(application.VendorId))
        {
            case VendorGetResult.Success success:
                namespacePrefixes = success.VendorResponse.NamespacePrefixes;
                break;
            default:
                logger.LogError(
                    "The application's stored vendor could not be resolved for ApiClient creation."
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
                return FailureResults.UnresolvedReference(
                    "One or more referenced items could not be resolved. See 'errors' for details.",
                    httpContext.TraceIdentifier,
                    [$"Application with ID {command.ApplicationId} not found."]
                );
            case ApiClientInsertResult.FailureDataStoreNotFound:
                await clientRepository.DeleteClientAsync(clientUuid.ToString());
                return FailureResults.UnresolvedReference(
                    "One or more referenced items could not be resolved. See 'errors' for details.",
                    httpContext.TraceIdentifier,
                    ["Data store does not exist."]
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
        await validator.GuardQueryAsync(query);
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
        long id,
        ApiClientUpdateCommand command,
        ApiClientUpdateCommand.Validator validator,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository,
        IApplicationRepository applicationRepository,
        IVendorRepository vendorRepository,
        IDataStoreRepository dataStoreRepository,
        IIdentityProviderRepository identityProviderRepository,
        IOptions<IdentitySettings> identitySettings,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering UpdateApiClient for id: {Id}", SanitizeForLog(id.ToString()));

        // Set the ID from the route parameter
        command.Id = id;
        await validator.GuardAsync(command);

        // Get existing API client
        ApiClientGetResult existingResult = await apiClientRepository.GetApiClientById(id);
        ApiClientResponse existingApiClient;
        switch (existingResult)
        {
            case ApiClientGetResult.Success existingSuccess:
                existingApiClient = existingSuccess.ApiClientResponse;
                break;
            case ApiClientGetResult.FailureUnknown existingFailure:
                logger.LogError(
                    "Error resolving ApiClient {Id}: {Message}",
                    SanitizeForLog(id.ToString()),
                    SanitizeForLog(existingFailure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                return FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier);
        }

        // Validate Application exists and get application details
        ApplicationGetResult applicationResult = await applicationRepository.GetApplication(
            command.ApplicationId
        );
        ApplicationResponse application;
        switch (applicationResult)
        {
            case ApplicationGetResult.Success applicationSuccess:
                application = applicationSuccess.ApplicationResponse;
                break;
            case ApplicationGetResult.FailureUnknown appFailure:
                logger.LogError(
                    "Error resolving ApplicationId: {Message}",
                    SanitizeForLog(appFailure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                return FailureResults.UnresolvedReference(
                    "One or more referenced items could not be resolved. See 'errors' for details.",
                    httpContext.TraceIdentifier,
                    [$"Application with ID {command.ApplicationId} not found."]
                );
        }

        // Validate DataStoreIds exist (optimized single query)
        if (command.DataStoreIds.Length > 0)
        {
            var existingIdsResult = await dataStoreRepository.GetExistingDataStoreIds(command.DataStoreIds);
            if (existingIdsResult is DataStoreIdsExistResult.Success existingIdsSuccess)
            {
                var notFoundIds = command
                    .DataStoreIds.Where(id => !existingIdsSuccess.ExistingIds.Contains(id))
                    .ToList();

                if (notFoundIds.Count > 0)
                {
                    return FailureResults.UnresolvedReference(
                        "One or more referenced items could not be resolved. See 'errors' for details.",
                        httpContext.TraceIdentifier,
                        [
                            $"The following DataStoreIds were not found in database: {string.Join(", ", notFoundIds)}",
                        ]
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

        // The vendor is derived from the resolved application, so a vendor that cannot be resolved is
        // internal data inconsistency, not a client unresolved-reference: report a sanitized 500.
        if (await vendorRepository.GetVendor(application.VendorId) is not VendorGetResult.Success)
        {
            logger.LogError("The application's stored vendor could not be resolved for ApiClient update.");
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        // Get original application for rollback if needed
        ApplicationGetResult originalApplicationResult = await applicationRepository.GetApplication(
            existingApiClient.ApplicationId
        );
        ApplicationResponse? originalApplication = null;
        if (originalApplicationResult is ApplicationGetResult.Success originalAppSuccess)
        {
            originalApplication = originalAppSuccess.ApplicationResponse;
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
                logger.LogError("Failure updating client: {Failure}", SanitizeForLog(failure.FailureMessage));
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
                // The configuration store row exists (the entity precheck passed) but the identity
                // provider reports no such client. That is an upstream inconsistency, not a client-facing
                // 404, so surface a sanitized 502 without echoing the raw provider message.
                logger.LogError(
                    "Client not found in identity provider: {Failure}",
                    SanitizeForLog(notFound.FailureMessage)
                );
                return FailureResults.BadGateway(
                    "Identity provider client not found during client update",
                    httpContext.TraceIdentifier
                );
            case ClientUpdateResult.Success updateSuccess:
                // Persist the new UUID issued by the identity provider after delete-and-recreate
                command.ClientUuid = updateSuccess.ClientUuid;
                // Update database SECOND - attempt rollback if this fails
                var repositoryResult = await apiClientRepository.UpdateApiClient(command);

                // Local function: restores Keycloak to original state and syncs the resulting
                // UUID back to the DB. Keycloak's delete-and-recreate always produces a new UUID,
                // so ignoring the rollback result would leave DB and Keycloak out of sync.
                async Task AttemptRollback()
                {
                    if (originalApplication is null)
                    {
                        return;
                    }
                    logger.LogWarning(
                        "Database update failed for ApiClient {Id}, attempting to rollback identity provider changes",
                        id
                    );
                    var rollbackResult = await identityProviderRepository.UpdateClientAsync(
                        (command.ClientUuid ?? existingApiClient.ClientUuid).ToString(),
                        existingApiClient.Name,
                        originalApplication.ClaimSetName,
                        string.Join(",", originalApplication.EducationOrganizationIds),
                        [.. existingApiClient.DataStoreIds],
                        existingApiClient.IsApproved,
                        identitySettings.Value.ClientRole
                    );
                    if (rollbackResult is ClientUpdateResult.Success rollbackSuccess)
                    {
                        // Sync the rollback UUID back to DB so they stay consistent
                        var syncCommand = new ApiClientUpdateCommand
                        {
                            Id = id,
                            ApplicationId = existingApiClient.ApplicationId,
                            Name = existingApiClient.Name,
                            IsApproved = existingApiClient.IsApproved,
                            DataStoreIds = [.. existingApiClient.DataStoreIds],
                            ClientUuid = rollbackSuccess.ClientUuid,
                        };
                        var syncResult = await apiClientRepository.UpdateApiClient(syncCommand);
                        if (syncResult is not ApiClientUpdateResult.Success)
                        {
                            logger.LogError(
                                "Failed to sync Keycloak UUID to database after rollback for ApiClient {Id}; state is inconsistent",
                                id
                            );
                        }
                    }
                    else
                    {
                        logger.LogError(
                            "Keycloak rollback failed for ApiClient {Id}; state may be inconsistent",
                            id
                        );
                    }
                }

                switch (repositoryResult)
                {
                    case ApiClientUpdateResult.Success:
                        return Results.NoContent();
                    case ApiClientUpdateResult.FailureNotFound:
                        await AttemptRollback();
                        return FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier);
                    case ApiClientUpdateResult.FailureApplicationNotFound:
                        await AttemptRollback();
                        return FailureResults.UnresolvedReference(
                            "One or more referenced items could not be resolved. See 'errors' for details.",
                            httpContext.TraceIdentifier,
                            [$"Application with ID {command.ApplicationId} not found."]
                        );
                    case ApiClientUpdateResult.FailureDataStoreNotFound:
                        await AttemptRollback();
                        return FailureResults.UnresolvedReference(
                            "One or more referenced items could not be resolved. See 'errors' for details.",
                            httpContext.TraceIdentifier,
                            ["Data store does not exist."]
                        );
                    case ApiClientUpdateResult.FailureUnknown failure:
                        await AttemptRollback();
                        logger.LogError(
                            "Failure updating client: {Failure}",
                            SanitizeForLog(failure.FailureMessage)
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                }

                break;
        }

        logger.LogError("Failure updating client");
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }

    private static async Task<IResult> DeleteApiClient(
        long id,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository,
        IApplicationRepository applicationRepository,
        IVendorRepository vendorRepository,
        IIdentityProviderRepository identityProviderRepository,
        IOptions<IdentitySettings> identitySettings,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering DeleteApiClient for id: {Id}", SanitizeForLog(id.ToString()));

        // Get the API client to retrieve the ClientUuid for identity provider deletion
        ApiClientGetResult getResult = await apiClientRepository.GetApiClientById(id);
        ApiClientResponse apiClient;
        switch (getResult)
        {
            case ApiClientGetResult.Success getSuccess:
                apiClient = getSuccess.ApiClientResponse;
                break;
            case ApiClientGetResult.FailureUnknown getFailure:
                logger.LogError(
                    "Error resolving ApiClient {Id}: {Message}",
                    SanitizeForLog(id.ToString()),
                    SanitizeForLog(getFailure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                return FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier);
        }

        // Get application and vendor details for potential rollback
        ApplicationGetResult applicationResult = await applicationRepository.GetApplication(
            apiClient.ApplicationId
        );
        ApplicationResponse? application = null;
        string? namespacePrefixes = null;

        if (applicationResult is ApplicationGetResult.Success applicationSuccess)
        {
            application = applicationSuccess.ApplicationResponse;
            var vendorResult = await vendorRepository.GetVendor(application.VendorId);
            if (vendorResult is VendorGetResult.Success vendorSuccess)
            {
                namespacePrefixes = vendorSuccess.VendorResponse.NamespacePrefixes;
            }
        }

        // Delete from identity provider FIRST
        try
        {
            logger.LogInformation("Deleting client {ClientId}", SanitizeForLog(apiClient.ClientId));
            var clientDeleteResult = await identityProviderRepository.DeleteClientAsync(
                apiClient.ClientUuid.ToString()
            );

            switch (clientDeleteResult)
            {
                case ClientDeleteResult.FailureUnknown failureUnknown:
                    logger.LogError(
                        "Error deleting client {ClientId} {ClientUuid}: {FailureMessage}",
                        SanitizeForLog(apiClient.ClientId),
                        SanitizeForLog(apiClient.ClientUuid.ToString()),
                        SanitizeForLog(failureUnknown.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case ClientDeleteResult.FailureIdentityProvider failureIdentityProvider:
                    logger.LogError(
                        "Error deleting client from identity provider: {FailureMessage}",
                        SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)
                    );
                    return FailureResults.BadGateway(
                        "Identity provider error during client deletion",
                        httpContext.TraceIdentifier
                    );
            }
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

        // Delete from database SECOND - attempt rollback if this fails
        ApiClientDeleteResult deleteResult = await apiClientRepository.DeleteApiClient(id);

        switch (deleteResult)
        {
            case ApiClientDeleteResult.Success:
                return Results.NoContent();
            case ApiClientDeleteResult.FailureNotFound:
            case ApiClientDeleteResult.FailureUnknown:
                // Attempt to rollback by recreating client in identity provider
                if (application != null && namespacePrefixes != null)
                {
                    logger.LogError(
                        "Database delete failed for ApiClient {Id} after identity provider deletion succeeded. Attempting to recreate client in identity provider.",
                        id
                    );
                    try
                    {
                        await identityProviderRepository.CreateClientAsync(
                            apiClient.ClientId,
                            "ROLLBACK_PLACEHOLDER_SECRET", // Cannot recover original secret
                            identitySettings.Value.ClientRole,
                            application.ApplicationName,
                            application.ClaimSetName,
                            namespacePrefixes,
                            string.Join(",", application.EducationOrganizationIds),
                            [.. apiClient.DataStoreIds],
                            apiClient.IsApproved
                        );
                        logger.LogWarning(
                            "Successfully recreated client {ClientId} in identity provider after database delete failure. CLIENT SECRET HAS BEEN CHANGED - manual intervention required.",
                            SanitizeForLog(apiClient.ClientId)
                        );
                    }
                    catch (Exception rollbackEx)
                    {
                        logger.LogCritical(
                            rollbackEx,
                            "CRITICAL: Failed to rollback identity provider after database delete failure for ApiClient {Id}. Client {ClientId} exists in identity provider but not in database. Manual cleanup required. Error: {Error}",
                            id,
                            SanitizeForLog(apiClient.ClientId),
                            SanitizeForLog(rollbackEx.Message)
                        );
                    }
                }
                else
                {
                    logger.LogCritical(
                        "CRITICAL: Database delete failed for ApiClient {Id} after identity provider deletion succeeded. Cannot rollback - missing application or vendor data. Client {ClientId} deleted from identity provider but still in database. Manual cleanup required.",
                        id,
                        SanitizeForLog(apiClient.ClientId)
                    );
                }

                return deleteResult is ApiClientDeleteResult.FailureNotFound
                    ? FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier)
                    : FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                logger.LogCritical(
                    "CRITICAL: Unexpected delete result for ApiClient {Id} after identity provider deletion. Client {ClientId} may be in inconsistent state.",
                    id,
                    SanitizeForLog(apiClient.ClientId)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
    }

    private async Task<IResult> ResetCredential(
        long id,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository,
        IIdentityProviderRepository identityProviderRepository,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering ResetCredential for id: {Id}", SanitizeForLog(id.ToString()));

        // Get the API client to retrieve the ClientUuid and ClientId for identity provider reset
        ApiClientGetResult getResult = await apiClientRepository.GetApiClientById(id);
        ApiClientResponse apiClient;
        switch (getResult)
        {
            case ApiClientGetResult.Success getSuccess:
                apiClient = getSuccess.ApiClientResponse;
                break;
            case ApiClientGetResult.FailureUnknown getFailure:
                logger.LogError(
                    "Error resolving ApiClient {Id}: {Message}",
                    SanitizeForLog(id.ToString()),
                    SanitizeForLog(getFailure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                return FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier);
        }

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
                // The identity provider reports no such client for an ApiClient that exists in the
                // configuration store: an upstream inconsistency (sanitized 502), not a client-facing 404.
                ClientResetResult.FailureClientNotFound => FailureResults.BadGateway(
                    "Identity provider client not found during credential reset",
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
}
