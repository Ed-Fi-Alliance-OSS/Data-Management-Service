// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
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
    private const string DuplicateNameError =
        "An API client with this name already exists for the application.";

    private static IResult EducationOrganizationScopeConflict(
        int applicationId,
        long[] missingEducationOrganizationIds,
        HttpContext httpContext
    ) =>
        Results.Json(
            FailureResponse.ForEducationOrganizationScopeConflict(
                $"The API client is narrowed to education organizations that are not on Application {applicationId}: {string.Join(", ", missingEducationOrganizationIds)}. To move it, send educationOrganizationIds on that Application or inheritsApplicationEducationOrganizations true.",
                httpContext.TraceIdentifier
            ),
            contentType: "application/problem+json",
            statusCode: (int)HttpStatusCode.Conflict
        );

    // A 409, as the Management API specification and ODS Admin API answer a name already used
    // by another API client of the Application
    private static IResult DuplicateName(HttpContext httpContext) =>
        Results.Json(
            FailureResponse.ForConflict(DuplicateNameError, httpContext.TraceIdentifier),
            contentType: "application/problem+json",
            statusCode: (int)HttpStatusCode.Conflict
        );

    /// <summary>
    /// Fixed 409 details for the identifier constraints. Neither names a row, key, or UUID: the
    /// key is generated per request, so a collision is retriable by the caller and nothing in
    /// the response may identify the row that holds the identifier.
    /// </summary>
    private const string DuplicateClientIdConflict =
        "The generated client key is already in use. Retry the request.";

    private const string DuplicateClientUuidConflict =
        "The identity provider client is already associated with another API client.";

    private static IResult IdentifierConflict(string detail, HttpContext httpContext) =>
        Results.Json(
            FailureResponse.ForConflict(detail, httpContext.TraceIdentifier),
            contentType: "application/problem+json",
            statusCode: (int)HttpStatusCode.Conflict
        );

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapSecuredPost("/v3/apiClients/", InsertApiClient)
            .Produces<ApiClientCredentialsResponse>(201)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(409)
            .ProducesProblem(500)
            .ProducesProblem(502)
            .WithDescription(ApiClientOpenApiMetadata.InsertDescription)
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    ApiClientOpenApiMetadata.SetInsertExamples(operation);
                    ApiClientOpenApiMetadata.SetResponseDescriptions(
                        operation,
                        ("400", ApiClientOpenApiMetadata.InsertBadRequest),
                        ("401", ApiClientOpenApiMetadata.Unauthorized),
                        ("403", ApiClientOpenApiMetadata.Forbidden),
                        ("409", ApiClientOpenApiMetadata.InsertConflict),
                        ("500", ApiClientOpenApiMetadata.ServerError),
                        ("502", ApiClientOpenApiMetadata.InsertBadGateway)
                    );
                    return Task.CompletedTask;
                }
            );
        endpoints
            .MapSecuredPut($"/v3/apiClients/{{id}}", UpdateApiClient)
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409)
            .ProducesProblem(500)
            .ProducesProblem(502)
            .WithDescription(ApiClientOpenApiMetadata.UpdateDescription)
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    ApiClientOpenApiMetadata.SetUpdateExamples(operation);
                    ApiClientOpenApiMetadata.SetResponseDescriptions(
                        operation,
                        ("400", ApiClientOpenApiMetadata.UpdateBadRequest),
                        ("401", ApiClientOpenApiMetadata.Unauthorized),
                        ("403", ApiClientOpenApiMetadata.Forbidden),
                        ("404", ApiClientOpenApiMetadata.NotFound),
                        ("409", ApiClientOpenApiMetadata.UpdateConflict),
                        ("500", ApiClientOpenApiMetadata.UpdateServerError),
                        ("502", ApiClientOpenApiMetadata.UpdateBadGateway)
                    );
                    return Task.CompletedTask;
                }
            );
        endpoints
            .MapSecuredDelete($"/v3/apiClients/{{id}}", DeleteApiClient)
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409)
            .ProducesProblem(500)
            .ProducesProblem(502)
            .WithDescription(
                "Deletes the API client. Deleting an Application's last API client keeps the "
                    + "Application, which then has no API clients: it reports enabled false and "
                    + "dataStoreIds [], and its reset-credential returns 409. Create another API client "
                    + "with POST /v3/apiClients."
            )
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    ApiClientOpenApiMetadata.SetResponseDescriptions(
                        operation,
                        ("400", ApiClientOpenApiMetadata.InvalidId),
                        ("401", ApiClientOpenApiMetadata.Unauthorized),
                        ("403", ApiClientOpenApiMetadata.Forbidden),
                        ("404", ApiClientOpenApiMetadata.NotFound),
                        ("409", ApiClientOpenApiMetadata.ConcurrentChange),
                        ("500", ApiClientOpenApiMetadata.ServerError),
                        ("502", ApiClientOpenApiMetadata.DeleteBadGateway)
                    );
                    return Task.CompletedTask;
                }
            );
        endpoints
            .MapSecuredPut($"/v3/apiClients/{{id}}/reset-credential", ResetCredential)
            .Produces<ApiClientCredentialsResponse>(200)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409)
            .ProducesProblem(500)
            .ProducesProblem(502)
            .WithDescription(ApiClientOpenApiMetadata.ResetDescription)
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    ApiClientOpenApiMetadata.SetResponseDescriptions(
                        operation,
                        ("400", ApiClientOpenApiMetadata.InvalidId),
                        ("401", ApiClientOpenApiMetadata.Unauthorized),
                        ("403", ApiClientOpenApiMetadata.Forbidden),
                        ("404", ApiClientOpenApiMetadata.NotFound),
                        ("409", ApiClientOpenApiMetadata.ConcurrentChange),
                        ("500", ApiClientOpenApiMetadata.ServerError),
                        ("502", ApiClientOpenApiMetadata.ResetBadGateway)
                    );
                    return Task.CompletedTask;
                }
            );
        // Limited access endpoints - accessible by service accounts for internal DMS operations
        endpoints
            .MapLimitedAccess("/v3/apiClients/", GetAll)
            .Produces<List<ApiClientResponse>>(200)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(500)
            .WithDescription(ApiClientOpenApiMetadata.ReadDescription)
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    ApiClientOpenApiMetadata.SetReadCollectionExamples(operation);
                    ApiClientOpenApiMetadata.SetResponseDescriptions(
                        operation,
                        ("400", ApiClientOpenApiMetadata.CollectionBadRequest),
                        ("401", ApiClientOpenApiMetadata.Unauthorized),
                        ("403", ApiClientOpenApiMetadata.Forbidden),
                        ("500", ApiClientOpenApiMetadata.ServerError)
                    );
                    return Task.CompletedTask;
                }
            );
        // One route carries both identifier forms. The OAuth client key is resolved first and wins
        // outright, because ClientId has no format constraint: a stored key of "12345" is possible
        // and must never be shadowed by the unrelated row whose primary key is 12345. Registering a
        // second templated path here would also collide in OpenAPI, where two paths differing only
        // by parameter name are the same path.
        endpoints
            .MapLimitedAccess("/v3/apiClients/{id}", GetByIdentifier)
            .Produces<ApiClientResponse>(200)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(500)
            .WithSummary("Retrieves a specific apiClient by OAuth client key or by numeric identifier.")
            .WithDescription(
                "The path segment is resolved key-first. It is matched against the OAuth client key "
                    + "('clientId' in responses, 'key' when credentials are issued), and that match "
                    + "wins whenever it exists. Only when no client key matches, and the segment is a "
                    + "valid 32-bit integer, is it resolved as the numeric ApiClient identifier ('id' "
                    + "in responses). "
                    + ApiClientOpenApiMetadata.ReadDescription
            )
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    ApiClientOpenApiMetadata.SetReadItemExamples(operation);
                    ApiClientOpenApiMetadata.SetResponseDescriptions(
                        operation,
                        ("401", ApiClientOpenApiMetadata.Unauthorized),
                        ("403", ApiClientOpenApiMetadata.Forbidden),
                        ("404", ApiClientOpenApiMetadata.NotFound),
                        ("500", ApiClientOpenApiMetadata.ServerError)
                    );
                    return Task.CompletedTask;
                }
            );
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
        IApplicationLockManager lockManager,
        IOptions<IdentitySettings> identitySettings,
        IOptions<ClientSecretValidationOptions> clientSecretValidationOptionsAccessor,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering InsertApiClient");
        await validator.GuardAsync(command);

        // The Application read, the provider client built from it and the row insert all happen
        // under the Application's aggregate lock. Without it, an Application save could commit and
        // sync its existing clients between the read and the insert, and this client would keep the
        // pre-save claims without being marked pending. Invalid input above never consumes a lock.
        ApplicationLockResult lockResult = await lockManager.AcquireAsync(
            command.ApplicationId,
            httpContext.RequestAborted
        );
        if (LockFailureResult(lockResult, httpContext, logger) is { } lockFailure)
        {
            return lockFailure;
        }

        await using var applicationLock = ((ApplicationLockResult.Acquired)lockResult).Handle;

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

        if (
            await CheckNameAvailableAsync(
                apiClientRepository,
                command.ApplicationId,
                command.Name,
                excludedApiClientId: null,
                httpContext,
                logger
            ) is
            { } nameFailure
        )
        {
            return nameFailure;
        }

        // The rules that need the Application are applied before the identity provider is touched,
        // so a rejected scope never provisions a provider client
        ApiClientEducationOrganizationScopeResolution resolution =
            ApiClientEducationOrganizationScopeResolver.Resolve(
                command.EducationOrganizationIdsSupplied ? command.EducationOrganizationIds : null,
                command.InheritsApplicationEducationOrganizations,
                application.EducationOrganizationIds,
                current: null
            );
        if (resolution is ApiClientEducationOrganizationScopeResolution.Invalid invalid)
        {
            return FailureResults.DataValidation([invalid.Failure], httpContext.TraceIdentifier);
        }

        // A create has no current scope to conflict with, so a valid request always resolves
        if (resolution is not ApiClientEducationOrganizationScopeResolution.Resolved scope)
        {
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        command.EducationOrganizationScope = scope.Scope;

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
            scope.Claim,
            command.DataStoreIds,
            command.IsApproved
        );

        switch (clientCreateResult)
        {
            case ClientCreateResult.FailureUnknown failure:
                logger.LogError(
                    "Failure creating client: {FailureMessage}",
                    SanitizeForLog(failure.FailureMessage)
                );
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
            case ClientCreateResult.FailureDuplicateClientId:
                // The provider already holds a client under the generated key and created nothing,
                // so there is no client to delete and no row is written.
                logger.LogWarning(
                    "The generated client key for client {ClientId} is already registered at the identity provider; no client was created and nothing was written",
                    SanitizeForLog(clientId)
                );
                return IdentifierConflict(DuplicateClientIdConflict, httpContext);
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
                // No row was written, so the constraints never judged the reported UUID; it is
                // deleted only once the check proves no row holds it.
                await CleanUpProvisionedClientWhenUnreferencedAsync(
                    apiClientRepository,
                    clientRepository,
                    clientUuid,
                    clientId,
                    logger
                );
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        $"Application with ID {command.ApplicationId} not found.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApiClientInsertResult.FailureRequestedEducationOrganizationsNotOnApplication notOnApplication:
                // Reached only after the row was inserted, which proved no row held the reported
                // UUID. The Application lost one of the requested education organizations after
                // the rules were applied.
                await CleanUpProvisionedClientAsync(clientRepository, clientUuid, clientId, logger);
                return FailureResults.DataValidation(
                    [
                        ApiClientEducationOrganizationScopeResolver
                            .NotOnApplication(notOnApplication.EducationOrganizationIds)
                            .Failure,
                    ],
                    httpContext.TraceIdentifier
                );
            case ApiClientInsertResult.FailureDataStoreNotFound:
                // Reached only after the row was inserted, which proved no row held the reported UUID.
                await CleanUpProvisionedClientAsync(clientRepository, clientUuid, clientId, logger);
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        "Data store does not exist.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApiClientInsertResult.FailureDuplicateName:
                // A row violating both the name and the UUID constraint can be reported as either,
                // so the name conflict does not prove the reported UUID is this request's own.
                await CleanUpProvisionedClientWhenUnreferencedAsync(
                    apiClientRepository,
                    clientRepository,
                    clientUuid,
                    clientId,
                    logger
                );
                return DuplicateName(httpContext);
            case ApiClientInsertResult.FailureDuplicateClientId:
                // The generated key collided with another row's. The provider client this
                // request created is deleted only once the reference check proves no row holds
                // its UUID; the 409 stands whatever the check and the cleanup report.
                logger.LogWarning(
                    "The generated client key for client {ClientId} is already in use; nothing was written",
                    SanitizeForLog(clientId)
                );
                await CleanUpProvisionedClientWhenUnreferencedAsync(
                    apiClientRepository,
                    clientRepository,
                    clientUuid,
                    clientId,
                    logger
                );
                return IdentifierConflict(DuplicateClientIdConflict, httpContext);
            case ApiClientInsertResult.FailureDuplicateClientUuid:
                // Another row already references the UUID the provider reported, so the provider
                // client is provably in use and is never deleted.
                logger.LogError(
                    "Provider client {ClientUuid} (client {ClientId}) is already associated with another API client; left in place; review manually",
                    clientUuid,
                    SanitizeForLog(clientId)
                );
                return IdentifierConflict(DuplicateClientUuidConflict, httpContext);
            case ApiClientInsertResult.FailureUnknown failure:
                logger.LogError(
                    "Failure inserting the API client row: {FailureMessage}",
                    SanitizeForLog(failure.FailureMessage)
                );
                // An unclassified failure says nothing about the reported UUID.
                await CleanUpProvisionedClientWhenUnreferencedAsync(
                    apiClientRepository,
                    clientRepository,
                    clientUuid,
                    clientId,
                    logger
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        logger.LogError("Failure creating client");
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }

    /// <summary>
    /// Rejects a name another client of the application already has, before the identity provider
    /// is touched, so a duplicate the caller can correct never provisions or changes a provider
    /// client. The comparison is ordinal, so it never rejects a name the database would accept;
    /// SQL Server case and trailing-space variants, and a concurrent writer taking the name after
    /// this read, are still rejected by UX_ApiClient_ApplicationId_Name through the repository's
    /// duplicate result. Returns null when the name is available.
    /// </summary>
    private static async Task<IResult?> CheckNameAvailableAsync(
        IApiClientRepository apiClientRepository,
        int applicationId,
        string name,
        int? excludedApiClientId,
        HttpContext httpContext,
        ILogger logger
    )
    {
        switch (
            await apiClientRepository.QueryApiClient(new ApiClientQuery { ApplicationId = applicationId })
        )
        {
            case ApiClientQueryResult.Success success:
                return success.ApiClientResponses.Exists(client =>
                    client.Id != excludedApiClientId
                    && string.Equals(client.Name, name, StringComparison.Ordinal)
                )
                    ? DuplicateName(httpContext)
                    : null;
            case ApiClientQueryResult.FailureUnknown failure:
                logger.LogError(
                    "Error checking API client name availability: {Message}",
                    SanitizeForLog(failure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                logger.LogError("Error checking API client name availability");
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
    }

    /// <summary>
    /// Removes the identity-provider client this request provisioned once the database insert has
    /// failed, and reports what became of it. The caller's response stays the one the database
    /// outcome dictates: the reference they have to correct is the actionable information, and a
    /// cleanup an operator must finish by hand does not change it. A client that may remain is
    /// therefore reported at Error with the identifiers needed to find it.
    /// </summary>
    private static async Task CleanUpProvisionedClientAsync(
        IIdentityProviderRepository clientRepository,
        Guid clientUuid,
        string clientId,
        ILogger logger
    )
    {
        ClientDeleteResult cleanupResult;
        try
        {
            cleanupResult = await clientRepository.DeleteClientAsync(clientUuid.ToString());
        }
        catch (Exception ex)
        {
            LogUnconfirmedCleanup(ex.GetType().Name, ex);
            return;
        }

        switch (cleanupResult)
        {
            case ClientDeleteResult.Success:
                logger.LogDebug(
                    "Deleted provider client {ClientUuid} (client {ClientId}) after the database insert failed",
                    clientUuid,
                    SanitizeForLog(clientId)
                );
                return;

            case ClientDeleteResult.FailureClientNotFound:
                logger.LogWarning(
                    "Provider client {ClientUuid} (client {ClientId}) was already absent when the database insert failed",
                    clientUuid,
                    SanitizeForLog(clientId)
                );
                return;

            case ClientDeleteResult.FailureIdentityProvider failureIdentityProvider:
                LogUnconfirmedCleanup(
                    $"{nameof(ClientDeleteResult.FailureIdentityProvider)}: {SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)}"
                );
                return;

            case ClientDeleteResult.FailureUnknown failureUnknown:
                LogUnconfirmedCleanup(
                    $"{nameof(ClientDeleteResult.FailureUnknown)}: {SanitizeForLog(failureUnknown.FailureMessage)}"
                );
                return;

            default:
                LogUnconfirmedCleanup(cleanupResult.GetType().Name);
                return;
        }

        void LogUnconfirmedCleanup(string outcome, Exception? exception = null) =>
            logger.LogError(
                exception,
                "Could not confirm deletion of provider client {ClientUuid} (client {ClientId}) after the database insert failed; cleanup outcome {Outcome}; the client may remain and must be reviewed manually",
                clientUuid,
                SanitizeForLog(clientId),
                outcome
            );
    }

    /// <summary>
    /// Removes the identity-provider client this request provisioned after a database insert
    /// failure that does not prove the UUID is this request's own (a key or name conflict, a
    /// missing application, or an unclassified failure), but only once the reference check proves
    /// no ApiClient row holds its UUID. The check is deliberately cross-tenant, so a referenced
    /// UUID, a failed or unrecognized check, or a check that throws all leave the client in place
    /// and report it at Error with the identifiers needed to find it. A thrown check is reported by
    /// exception type only: its message can carry provider or database text that the sanitizer
    /// never sees, so the exception itself is not attached. The caller's response is never
    /// replaced by what happens here.
    /// </summary>
    private static async Task CleanUpProvisionedClientWhenUnreferencedAsync(
        IApiClientRepository apiClientRepository,
        IIdentityProviderRepository clientRepository,
        Guid clientUuid,
        string clientId,
        ILogger logger
    )
    {
        ApiClientUuidReferenceResult referenceResult;
        try
        {
            referenceResult = await apiClientRepository.HasApiClientUuidReference(clientUuid);
        }
        catch (Exception ex)
        {
            LogLeftInPlace(ex.GetType().Name);
            return;
        }

        switch (referenceResult)
        {
            case ApiClientUuidReferenceResult.None:
                await CleanUpProvisionedClientAsync(clientRepository, clientUuid, clientId, logger);
                return;

            case ApiClientUuidReferenceResult.Referenced:
                LogLeftInPlace(nameof(ApiClientUuidReferenceResult.Referenced));
                return;

            case ApiClientUuidReferenceResult.FailureUnknown referenceFailure:
                LogLeftInPlace(
                    $"{nameof(ApiClientUuidReferenceResult.FailureUnknown)}: {SanitizeForLog(referenceFailure.FailureMessage)}"
                );
                return;

            default:
                LogLeftInPlace(referenceResult.GetType().Name);
                return;
        }

        void LogLeftInPlace(string outcome) =>
            logger.LogError(
                "Could not prove provider client {ClientUuid} (client {ClientId}) is unreferenced after the database insert failed; reference check outcome {Outcome}; left in place; review manually",
                clientUuid,
                SanitizeForLog(clientId),
                outcome
            );
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

    /// <summary>
    /// Resolves an ApiClient from a single path segment that may carry either identifier the
    /// Management API models: the OAuth client key or the numeric primary key.
    ///
    /// The client key is resolved first and wins outright. ClientId is stored as a variable-length
    /// string with no format constraint, so a key such as "12345" is possible even though CMS issues
    /// GUID keys, and resolving the numeric primary key first would hand that caller an unrelated
    /// row. The numeric lookup runs only after the key lookup reports the row absent, and reuses the
    /// same tenant-scoped read that update, delete, and reset-credential perform.
    ///
    /// A failed lookup at either stage is an unknown backend state rather than an absent row, so it
    /// is reported as a sanitized 500 and never falls through to the other lookup.
    /// </summary>
    private static async Task<IResult> GetByIdentifier(
        string id,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository
    )
    {
        ApiClientGetResult byClientId = await apiClientRepository.GetApiClientByClientId(id);

        if (byClientId is ApiClientGetResult.Success clientKeyMatch)
        {
            return Results.Ok(clientKeyMatch.ApiClientResponse);
        }

        if (byClientId is not ApiClientGetResult.FailureNotFound)
        {
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        // IntRouteConstraint's parse rules, so the segments treated as numeric are exactly those the
        // separate {id:int} route accepted before the two routes became one.
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericId))
        {
            return FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier);
        }

        ApiClientGetResult byId = await apiClientRepository.GetApiClientById(numericId);
        return byId switch
        {
            ApiClientGetResult.Success primaryKeyMatch => Results.Ok(primaryKeyMatch.ApiClientResponse),
            // The parsed value rather than the raw segment: it matches the message the other numeric
            // routes emit and keeps caller-supplied text out of the response body.
            ApiClientGetResult.FailureNotFound => FailureResults.NotFound(
                $"ApiClient with ID {numericId} not found.",
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

        PutGuards.GuardRouteIdMatchesBodyId(id, command.Id);

        await validator.GuardAsync(command);

        // A parent move must hold both aggregate locks; the helper takes them as one lock set
        // on one session, in ascending application id order, and rereads the client under them.
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

            if (
                await CheckNameAvailableAsync(
                    apiClientRepository,
                    command.ApplicationId,
                    command.Name,
                    excludedApiClientId: id,
                    httpContext,
                    logger
                ) is
                { } nameFailure
            )
            {
                return nameFailure;
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

            // The client's scope, read under the aggregate locks, resolves the request and is
            // what a failed update restores in the identity provider.
            ApiClientEducationOrganizationState originalScope;
            switch (await apiClientRepository.GetApiClientEducationOrganizationState(id))
            {
                case ApiClientEducationOrganizationStateResult.Success stateSuccess:
                    originalScope = stateSuccess.State;
                    break;
                case ApiClientEducationOrganizationStateResult.FailureNotFound:
                    return FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier);
                case ApiClientEducationOrganizationStateResult.FailureUnknown stateFailure:
                    logger.LogError(
                        "Error reading the education organization scope of ApiClient {Id}: {Message}",
                        id,
                        SanitizeForLog(stateFailure.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                default:
                    logger.LogError(
                        "The education organization scope of ApiClient {Id} could not be resolved",
                        id
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
            }

            // An inheriting client's effective list is its original Application's list
            string originalClaim = string.Join(
                ",",
                originalScope.InheritsApplicationEducationOrganizations
                    ? originalApplication.EducationOrganizationIds.Order()
                    : originalScope.StoredEducationOrganizationIds.Order()
            );

            // The rules are applied against the destination Application before the identity
            // provider is touched, so a rejected scope or move changes nothing
            ApiClientEducationOrganizationScopeResolution.Resolved scope;
            switch (
                ApiClientEducationOrganizationScopeResolver.Resolve(
                    command.EducationOrganizationIdsSupplied ? command.EducationOrganizationIds : null,
                    command.InheritsApplicationEducationOrganizations,
                    application.EducationOrganizationIds,
                    originalScope,
                    moves: command.ApplicationId != existingApiClient.ApplicationId
                )
            )
            {
                case ApiClientEducationOrganizationScopeResolution.Resolved resolved:
                    scope = resolved;
                    break;
                case ApiClientEducationOrganizationScopeResolution.ScopeConflict conflict:
                    return EducationOrganizationScopeConflict(
                        command.ApplicationId,
                        conflict.MissingEducationOrganizationIds,
                        httpContext
                    );
                case ApiClientEducationOrganizationScopeResolution.Invalid invalid:
                    return FailureResults.DataValidation([invalid.Failure], httpContext.TraceIdentifier);
                default:
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
            }

            command.EducationOrganizationScope = scope.Scope;

            // Update client in identity provider FIRST
            // The display name is the Application's, as on create and an Application update.
            var clientUpdateResult = await identityProviderRepository.UpdateClientAsync(
                existingApiClient.ClientUuid.ToString(),
                application.ApplicationName,
                application.ClaimSetName,
                scope.Claim,
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
                    // client in place, or a replacement when it recreated the client. The provider
                    // now holds this client's current settings, so the save also completes any
                    // pending identity provider synchronization of the client (DMS-1642), unless a
                    // claim set rename has committed since the Application was read above; then it
                    // leaves the client pending, even one moving into the renamed Application.
                    command.ClientUuid = updateSuccess.ClientUuid;
                    command.CompletesIdentityProviderSync = true;
                    command.SentClaimSetName = application.ClaimSetName;

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
                        // The rollback addresses the UUID the provider reported for this update.
                        // The stored UUID is this client's own; a replacement is this request's
                        // only while no ApiClient row holds it. Whichever constraint the database
                        // reported, a replacement another row holds is never addressed.
                        if (
                            updateSuccess.ClientUuid != existingApiClient.ClientUuid
                            && !await IsUnreferencedReplacementAsync(updateSuccess.ClientUuid)
                        )
                        {
                            return false;
                        }

                        logger.LogWarning(
                            "Repository update failed for ApiClient {Id}; rolling back the identity provider",
                            id
                        );
                        ClientUpdateResult rollbackResult;
                        try
                        {
                            rollbackResult = await identityProviderRepository.UpdateClientAsync(
                                updateSuccess.ClientUuid.ToString(),
                                originalApplication.ApplicationName,
                                originalApplication.ClaimSetName,
                                originalClaim,
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
                            case ApiClientUuidSyncResult.FailureNewUuidInUse:
                                // The rollback UUID the provider reported is already held by another
                                // ApiClient row: a provider inconsistency. That provider client belongs
                                // to the other row, so it is not deleted.
                                logger.LogError(
                                    "The rollback client UUID {ClientUuid} for ApiClient {Id} is already held by another API client; stored client state is inconsistent; nothing was deleted",
                                    rollbackSuccess.ClientUuid,
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

                    // True only when the reference check proves no ApiClient row holds the
                    // replacement UUID. A referenced UUID, a failed or unrecognized check, or a
                    // check that throws all refuse compensation and report it at Error; a thrown
                    // check is reported by exception type only, since its message is never
                    // sanitized.
                    async Task<bool> IsUnreferencedReplacementAsync(Guid replacementUuid)
                    {
                        string outcome;
                        try
                        {
                            switch (await apiClientRepository.HasApiClientUuidReference(replacementUuid))
                            {
                                case ApiClientUuidReferenceResult.None:
                                    return true;
                                case ApiClientUuidReferenceResult.Referenced:
                                    logger.LogError(
                                        "The identity provider reported a client UUID {ClientUuid} already held by another API client for ApiClient {Id}; stored client state is inconsistent; no compensation attempted",
                                        replacementUuid,
                                        id
                                    );
                                    return false;
                                case ApiClientUuidReferenceResult.FailureUnknown referenceFailure:
                                    outcome = SanitizeForLog(referenceFailure.FailureMessage);
                                    break;
                                case var unrecognized:
                                    outcome = unrecognized.GetType().Name;
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            outcome = ex.GetType().Name;
                        }

                        logger.LogError(
                            "Could not verify the identity provider client {ClientUuid} reported for ApiClient {Id} is unreferenced: {Outcome}; stored client state is inconsistent; no compensation attempted",
                            replacementUuid,
                            id,
                            outcome
                        );
                        return false;
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
                        && resolved.ClientUuid == updateSuccess.ClientUuid
                        && resolved.InheritsApplicationEducationOrganizations == scope.Inherits
                        && SetEquals(
                            resolved.StoredEducationOrganizationIds,
                            scope.StoredEducationOrganizationIds
                        )
                        // The committed update cleared the pending marker; a still-pending client
                        // whose other fields already matched (an unchanged request) did not commit.
                        // A claim set rename committed alongside it leaves the client pending, which
                        // resolves as a partial match and leaves the rename's marker in place.
                        && resolved.IdentityProviderSyncPendingSince is null;

                    bool MatchesOriginal(ApiClientResolutionState resolved) =>
                        resolved.Name == existingApiClient.Name
                        && resolved.ApplicationId == existingApiClient.ApplicationId
                        && resolved.IsApproved == existingApiClient.IsApproved
                        && SetEquals(resolved.DataStoreIds, existingApiClient.DataStoreIds)
                        && resolved.ClientId == existingApiClient.ClientId
                        && resolved.ClientUuid == existingApiClient.ClientUuid
                        && resolved.InheritsApplicationEducationOrganizations
                            == originalScope.InheritsApplicationEducationOrganizations
                        && SetEquals(
                            resolved.StoredEducationOrganizationIds,
                            originalScope.StoredEducationOrganizationIds
                        )
                        && resolved.IdentityProviderSyncPendingSince
                            == originalScope.IdentityProviderSyncPendingSince;

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
                        case ApiClientUpdateResult.FailureDuplicateName:
                            if (!await TryCompensateAsync())
                            {
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            }

                            return DuplicateName(httpContext);
                        case ApiClientUpdateResult.FailureRequestedEducationOrganizationsNotOnApplication notOnApplication:
                            if (!await TryCompensateAsync())
                            {
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            }

                            return FailureResults.DataValidation(
                                [
                                    ApiClientEducationOrganizationScopeResolver
                                        .NotOnApplication(notOnApplication.EducationOrganizationIds)
                                        .Failure,
                                ],
                                httpContext.TraceIdentifier
                            );
                        case ApiClientUpdateResult.FailureEducationOrganizationsNotOnApplication notOnApplication:
                            if (!await TryCompensateAsync())
                            {
                                return FailureResults.Unknown(httpContext.TraceIdentifier);
                            }

                            return EducationOrganizationScopeConflict(
                                command.ApplicationId,
                                notOnApplication.EducationOrganizationIds,
                                httpContext
                            );
                        case ApiClientUpdateResult.FailureDuplicateClientUuid:
                            // The provider reported a replacement UUID that another ApiClient row
                            // already holds: a provider inconsistency. Compensating would call
                            // UpdateClientAsync on that UUID, mutating the other row's provider
                            // object, so no compensation, synchronization, deletion or outcome
                            // resolution is attempted.
                            logger.LogError(
                                "The identity provider reported a client UUID {ClientUuid} already held by another API client for ApiClient {Id}; stored client state is inconsistent; no compensation attempted",
                                updateSuccess.ClientUuid,
                                id
                            );
                            return FailureResults.Unknown(httpContext.TraceIdentifier);
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
    /// application and, when an update moves the client, the target application, as one lock set
    /// on one session, in ascending application id order. The client is reread under the held
    /// locks, and the acquisition retries when the parent changed while waiting; a persistent
    /// change is a retriable concurrency conflict.
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
                // One set acquisition, not one call per application. Taking a move's two locks
                // separately made each attempt hold its first lock session while waiting for a
                // second from the bounded lock pool, so concurrent moves over disjoint
                // application pairs could starve that pool (hold-and-wait) until the driver's
                // connect timeout broke the wait and failed the requests. Ascending id order
                // does not prevent that, because the contended resource is the pool, not the
                // locks. The set is acquired on one session, so a move holds one connection.
                ApplicationLockResult lockResult = await lockManager.AcquireAllAsync(
                    applicationIdsToLock,
                    httpContext.RequestAborted
                );
                if (LockFailureResult(lockResult, httpContext, logger) is { } lockFailure)
                {
                    // A partially acquired set is released by the manager before it reports the
                    // failure, so there is nothing held here to give back.
                    return (lockFailure, null, []);
                }

                heldLocks.Add(((ApplicationLockResult.Acquired)lockResult).Handle);

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

    private static bool SetEquals<T>(IEnumerable<T> first, IEnumerable<T> second) =>
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
