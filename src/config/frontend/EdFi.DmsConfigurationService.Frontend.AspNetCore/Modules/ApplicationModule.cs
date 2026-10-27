// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
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
using Microsoft.OpenApi;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ApplicationModule : IEndpointModule
{
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

    /// <summary>
    /// How the Application reads compute enabled and dataStoreIds from the Application's API
    /// clients. Shared with the vendor's applications read, which computes them the same way.
    /// </summary>
    internal const string ApplicationClientStateDescription =
        "enabled is true only when the Application has at least one API client and every one is "
        + "approved; an Application with no API clients reports false. dataStoreIds is the union of "
        + "the data stores assigned to the Application's API clients, and [] for an Application with "
        + "no API clients.";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapSecuredPost("/v3/applications/", InsertApplication)
            .Produces<ApplicationInsertResponse>(201)
            .ProducesProblem(400)
            .ProducesProblem(409)
            .WithDescription(
                "Creates an Application and, unless createDefaultCredentials is false, its default "
                    + "API client. With createDefaultCredentials false no API client is created, the "
                    + "201 response carries key and secret as null, and a request that includes "
                    + "enabled or dataStoreIds, with any value, is rejected with 400."
            )
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    SetInsertExamples(operation);
                    SetResponseDescription(
                        operation,
                        "400",
                        "The request is invalid, including enabled or dataStoreIds sent with "
                            + "createDefaultCredentials false."
                    );
                    return Task.CompletedTask;
                }
            );
        endpoints
            .MapSecuredGet("/v3/applications/", GetAll)
            .Produces<List<ApplicationResponse>>(200)
            .WithDescription(ApplicationClientStateDescription);
        // Limited access: DMS reads an application's profile assignments through this endpoint, so the
        // limited scope its service account uses for its other reads must cover it too.
        endpoints
            .MapLimitedAccess($"/v3/applications/{{id}}", GetById)
            .Produces<ApplicationResponse>(200)
            .WithDescription(ApplicationClientStateDescription);
        endpoints
            .MapSecuredPut($"/v3/applications/{{id}}", Update)
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409)
            .ProducesProblem(500)
            .ProducesProblem(502)
            .WithDescription(UpdateDescription)
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    SetResponseDescription(operation, "404", "The Application does not exist.");
                    SetResponseDescription(
                        operation,
                        "409",
                        "The vendor or a profile does not exist, or a concurrent change to the Application "
                            + "held the request up; retry in that last case."
                    );
                    SetResponseDescription(operation, "502", PartialSyncDescription);
                    SetPartialSyncExample(operation);
                    return Task.CompletedTask;
                }
            );
        endpoints.MapSecuredDelete($"/v3/applications/{{id}}", Delete);

        endpoints
            .MapSecuredPut($"/v3/applications/{{id}}/reset-credential", ResetCredential)
            .Produces<ApplicationCredentialsResponse>(200)
            .ProducesProblem(404)
            .ProducesProblem(409)
            .WithDescription(
                "Rotates the secret of the Application's only API client. An Application with no "
                    + "API clients, or with more than one, has no single client to reset and returns "
                    + "409; reset a specific client with PUT /v3/apiClients/{id}/reset-credential."
            )
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    SetResponseDescription(operation, "404", "The Application does not exist.");
                    SetResponseDescription(
                        operation,
                        "409",
                        "The Application has no API clients or more than one, or a concurrent change "
                            + "to it held the request up; retry in that last case."
                    );
                    return Task.CompletedTask;
                }
            );
    }

    private const string UpdateDescription =
        "Updates an Application. The change is saved first, in one transaction that also marks every "
        + "API client it affects as pending: every client for a name or claim set change, inheriting "
        + "clients for an added or removed education organization, and narrowed clients that held a "
        + "removed one. A client narrowed to the Application's whole list keeps its list and does not "
        + "gain added education organizations. Each pending client of the Application, including any "
        + "left pending by an earlier request, is then updated in the identity provider in turn, and the "
        + "service clears its pending marker after the provider confirms the update. Tokens requested afterwards carry the "
        + "change; tokens already issued keep their claims until they expire. 204 means no client is "
        + "pending. When the identity provider could not update some clients the change stays saved and "
        + "the response is 502 urn:ed-fi:management-api:bad-gateway:partial-sync; send the same request "
        + "again to retry only the clients still pending. An Application with no API clients is "
        + "updated without an identity provider call. The update never changes client keys, secrets, "
        + "names, approval, data stores or education organization scope choices.";

    private const string PartialSyncDescription =
        "The Application change is saved, but the identity provider synchronization of some API "
        + "clients is not confirmed. type is urn:ed-fi:management-api:bad-gateway:partial-sync, "
        + "detail counts the pending clients, and apiClientIds lists their numeric ids in ascending "
        + "order. Send the same request again to retry only those clients.";

    /// <summary>Publishes the partial-sync 502 the update returns, built by the code that returns it.</summary>
    private static void SetPartialSyncExample(OpenApiOperation operation)
    {
        if (
            operation.Responses is not null
            && operation.Responses.TryGetValue("502", out var iResponse)
            && iResponse is OpenApiResponse { Content: not null } response
            && response.Content.TryGetValue("application/problem+json", out var mediaType)
        )
        {
            mediaType.Example = FailureResponse.ForPartialSync([12, 15], "0HNPKBEDOO2G9:00000001");
        }
    }

    /// <summary>
    /// Publishes a request and a 201 example for each credentials mode. The example without
    /// default credentials omits enabled and dataStoreIds, which that mode rejects.
    /// </summary>
    private static void SetInsertExamples(OpenApiOperation operation)
    {
        if (
            operation.RequestBody is OpenApiRequestBody requestBody
            && requestBody.Content is not null
            && requestBody.Content.TryGetValue("application/json", out var requestMediaType)
        )
        {
            requestMediaType.Examples = new Dictionary<string, IOpenApiExample>
            {
                ["defaultCredentials"] = new OpenApiExample
                {
                    Summary = "Create the Application and its default API client",
                    Value = JsonNode.Parse(
                        """
                        {
                          "applicationName": "Sample Application",
                          "vendorId": 1,
                          "claimSetName": "SIS-Vendor",
                          "educationOrganizationIds": [255901],
                          "dataStoreIds": [1],
                          "profileIds": []
                        }
                        """
                    ),
                },
                ["withoutDefaultCredentials"] = new OpenApiExample
                {
                    Summary = "Create the Application without an API client",
                    Value = JsonNode.Parse(
                        """
                        {
                          "applicationName": "Sample Application",
                          "vendorId": 1,
                          "claimSetName": "SIS-Vendor",
                          "educationOrganizationIds": [255901],
                          "profileIds": [],
                          "createDefaultCredentials": false
                        }
                        """
                    ),
                },
            };
        }

        if (
            operation.Responses is not null
            && operation.Responses.TryGetValue("201", out var iResponse)
            && iResponse is OpenApiResponse response
            && response.Content is not null
            && response.Content.TryGetValue("application/json", out var responseMediaType)
        )
        {
            responseMediaType.Examples = new Dictionary<string, IOpenApiExample>
            {
                ["defaultCredentials"] = new OpenApiExample
                {
                    Summary = "The default API client's credentials",
                    Value = JsonNode.Parse(
                        """
                        {
                          "id": 1,
                          "key": "6f3f0c2e-6d0b-4f4c-9a51-2f8f0a8d3c11",
                          "secret": "s3cr3t-Example-Value-Of-Minimum-Length-32!"
                        }
                        """
                    ),
                },
                ["withoutDefaultCredentials"] = new OpenApiExample
                {
                    Summary = "No API client was created",
                    Value = JsonNode.Parse(
                        """
                        {
                          "id": 2,
                          "key": null,
                          "secret": null
                        }
                        """
                    ),
                },
            };
        }
    }

    private static void SetResponseDescription(OpenApiOperation operation, string status, string description)
    {
        if (
            operation.Responses is not null
            && operation.Responses.TryGetValue(status, out var iResponse)
            && iResponse is OpenApiResponse response
        )
        {
            response.Description = description;
        }
    }

    private async Task<IResult> InsertApplication(
        ApplicationInsertCommand command,
        ApplicationInsertCommand.Validator validator,
        HttpContext httpContext,
        IApplicationRepository applicationRepository,
        IApiClientRepository apiClientRepository,
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

        if (!command.CreateDefaultCredentials)
        {
            return await InsertApplicationWithoutClient(command, httpContext, applicationRepository, logger);
        }

        var clientId = Guid.NewGuid().ToString();
        var clientSecret = ClientSecretValidation.GenerateSecretWithMinimumLength(
            clientSecretValidationOptionsAccessor.Value
        );

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
                            new ApplicationInsertResponse()
                            {
                                Id = success.Id,
                                Key = clientId,
                                Secret = clientSecret,
                            }
                        );
                    case ApplicationInsertResult.FailureVendorNotFound:
                        // The Application row was never written, so the ApiClient constraints never
                        // judged the reported UUID; it is deleted only once the check proves no
                        // row holds it.
                        await CleanUpProvisionedClientWhenUnreferencedAsync(
                            apiClientRepository,
                            clientRepository,
                            clientSuccess.ClientUuid,
                            clientId,
                            logger
                        );
                        return Results.Json(
                            FailureResponse.ForUnresolvedReference(
                                "Reference 'VendorId' does not exist.",
                                httpContext.TraceIdentifier
                            ),
                            contentType: "application/problem+json",
                            statusCode: (int)HttpStatusCode.Conflict
                        );
                    case ApplicationInsertResult.FailureDataStoreNotFound:
                        // This arm and the profile arm are reached only after the ApiClient row was
                        // inserted, which proved no row held the reported UUID.
                        await CleanUpProvisionedClientAsync(
                            clientRepository,
                            clientSuccess.ClientUuid,
                            clientId,
                            logger
                        );
                        return Results.Json(
                            FailureResponse.ForUnresolvedReference(
                                "Data store does not exist.",
                                httpContext.TraceIdentifier
                            ),
                            contentType: "application/problem+json",
                            statusCode: (int)HttpStatusCode.Conflict
                        );
                    case ApplicationInsertResult.FailureProfileNotFound:
                        await CleanUpProvisionedClientAsync(
                            clientRepository,
                            clientSuccess.ClientUuid,
                            clientId,
                            logger
                        );
                        return Results.Json(
                            FailureResponse.ForUnresolvedReference(
                                "Profile does not exist.",
                                httpContext.TraceIdentifier
                            ),
                            contentType: "application/problem+json",
                            statusCode: (int)HttpStatusCode.Conflict
                        );
                    case ApplicationInsertResult.FailureDuplicateApplication duplicateApp:
                        // The Application insert failed before the ApiClient row, so a UUID another
                        // row holds would surface here as the name conflict.
                        await CleanUpProvisionedClientWhenUnreferencedAsync(
                            apiClientRepository,
                            clientRepository,
                            clientSuccess.ClientUuid,
                            clientId,
                            logger
                        );
                        throw new ValidationException([
                            new ValidationFailure(
                                "ApplicationName",
                                $"Application '{duplicateApp.ApplicationName}' already exists for vendor."
                            ),
                        ]);
                    case ApplicationInsertResult.FailureDuplicateClientId:
                        // The generated key collided with another row's. The provider client this
                        // request created is deleted only once the reference check proves no row
                        // holds its UUID; the 409 stands whatever the check and the cleanup report.
                        logger.LogWarning(
                            "The generated client key for client {ClientId} is already in use; nothing was written",
                            SanitizeForLog(clientId)
                        );
                        await CleanUpProvisionedClientWhenUnreferencedAsync(
                            apiClientRepository,
                            clientRepository,
                            clientSuccess.ClientUuid,
                            clientId,
                            logger
                        );
                        return IdentifierConflict(DuplicateClientIdConflict, httpContext);
                    case ApplicationInsertResult.FailureDuplicateClientUuid:
                        // Another row already references the UUID the provider reported, so the
                        // provider client is provably in use and is never deleted.
                        logger.LogError(
                            "Provider client {ClientUuid} (client {ClientId}) is already associated with another API client; left in place; review manually",
                            clientSuccess.ClientUuid,
                            SanitizeForLog(clientId)
                        );
                        return IdentifierConflict(DuplicateClientUuidConflict, httpContext);
                    case ApplicationInsertResult.FailureUnknown failure:
                        logger.LogError(
                            "Failure inserting the application row: {FailureMessage}",
                            SanitizeForLog(failure.FailureMessage)
                        );
                        // An unclassified failure says nothing about the reported UUID.
                        await CleanUpProvisionedClientWhenUnreferencedAsync(
                            apiClientRepository,
                            clientRepository,
                            clientSuccess.ClientUuid,
                            clientId,
                            logger
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                }

                break;
        }

        logger.LogError("Failure creating client");
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }

    /// <summary>
    /// Creates an Application with no API client. No identity-provider client is provisioned, so
    /// a failed insert has nothing to compensate, and the response carries explicit null
    /// credentials.
    /// </summary>
    private static async Task<IResult> InsertApplicationWithoutClient(
        ApplicationInsertCommand command,
        HttpContext httpContext,
        IApplicationRepository applicationRepository,
        ILogger logger
    )
    {
        // The repository does not use the client command when CreateDefaultCredentials is false.
        var repositoryResult = await applicationRepository.InsertApplication(
            command,
            new() { ClientId = "", ClientUuid = Guid.Empty }
        );

        switch (repositoryResult)
        {
            case ApplicationInsertResult.Success success:
                var request = httpContext.Request;
                return Results.Created(
                    $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                    new ApplicationInsertResponse()
                    {
                        Id = success.Id,
                        Key = null,
                        Secret = null,
                    }
                );
            case ApplicationInsertResult.FailureVendorNotFound:
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        "Reference 'VendorId' does not exist.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApplicationInsertResult.FailureProfileNotFound:
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        "Profile does not exist.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApplicationInsertResult.FailureDuplicateApplication duplicateApp:
                throw new ValidationException([
                    new ValidationFailure(
                        "ApplicationName",
                        $"Application '{duplicateApp.ApplicationName}' already exists for vendor."
                    ),
                ]);
            case ApplicationInsertResult.FailureUnknown failure:
                logger.LogError(
                    "Failure inserting the application row: {FailureMessage}",
                    SanitizeForLog(failure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                logger.LogError(
                    "Unexpected result inserting an application without a client: {Result}",
                    repositoryResult.GetType().Name
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
    }

    /// <summary>
    /// Removes the identity-provider client this request provisioned once the database insert has
    /// failed, and reports what became of it. The caller's response stays the one the database
    /// outcome dictates: the reference or name they have to correct is the actionable
    /// information, and a cleanup an operator must finish by hand does not change it. A client
    /// that may remain is therefore reported at Error with the identifiers needed to find it.
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
    /// failure that does not prove the UUID is this request's own (a key collision, a name
    /// conflict or missing vendor reported before the ApiClient row, or an unclassified failure),
    /// but only once the reference check proves no ApiClient row holds its UUID. The check is
    /// deliberately cross-tenant, so a referenced UUID, a failed or unrecognized check, or a check
    /// that throws all leave the client in place and report it at Error with the identifiers needed
    /// to find it. A thrown check is reported by exception type only: its message can carry
    /// provider or database text that the sanitizer never sees, so the exception itself is not
    /// attached. The caller's response is never replaced by what happens here.
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

    private static async Task<IResult> Update(
        int id,
        ApplicationUpdateCommand.Validator validator,
        ApplicationUpdateCommand command,
        HttpContext httpContext,
        IApplicationRepository repository,
        IApiClientRepository apiClientRepository,
        IIdentityProviderRepository clientRepository,
        IApplicationLockManager lockManager,
        IOptions<IdentitySettings> identitySettings,
        ILogger<ApplicationModule> logger
    )
    {
        PutGuards.GuardRouteIdMatchesBodyId(id, command.Id);

        await validator.GuardAsync(command);

        // The save, the identity provider updates and the marker clears all happen under the
        // aggregate lock, so acquisition precedes them all; invalid input above never consumes a lock.
        var lockResult = await lockManager.AcquireAsync(id, httpContext.RequestAborted);
        if (LockFailureResult(lockResult, httpContext, logger) is { } lockFailure)
        {
            return lockFailure;
        }

        await using var applicationLock = ((ApplicationLockResult.Acquired)lockResult).Handle;

        // The database is saved first, marking every client the change affects. The save rejects a
        // missing or foreign-tenant Application before the request's references, and nothing reaches
        // the identity provider unless it commits.
        ApplicationSaveResult saveResult;
        try
        {
            saveResult = await repository.SaveApplication(command);
        }
        catch (Exception ex)
        {
            // A thrown save may or may not have committed, so it is resolved like an unknown commit
            // outcome. The exception message is never logged, since it is not sanitized.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogError(
                "Saving Application {Id} threw {ExceptionType}; reading the committed state",
                id,
                ex.GetType().Name
            );
#pragma warning restore S6667
            saveResult = new ApplicationSaveResult.FailureCommitOutcomeUnknown(ex.GetType().Name);
        }

        ApplicationSyncState savedState;
        switch (saveResult)
        {
            case ApplicationSaveResult.Success success:
                savedState = success.State;
                break;
            case ApplicationSaveResult.FailureNotExists:
                return FailureResults.NotFound("Application not found", httpContext.TraceIdentifier);
            case ApplicationSaveResult.FailureVendorNotFound:
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        "Reference 'VendorId' does not exist.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApplicationSaveResult.FailureProfileNotFound:
                return Results.Json(
                    FailureResponse.ForUnresolvedReference(
                        "Profile does not exist.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApplicationSaveResult.FailureDuplicateApplication duplicateApp:
                throw new ValidationException([
                    new ValidationFailure(
                        "ApplicationName",
                        $"Application '{duplicateApp.ApplicationName}' already exists for vendor."
                    ),
                ]);
            case ApplicationSaveResult.FailureUnknown unknownFailure:
                // Nothing was committed, so there is nothing to synchronize.
                logger.LogError(
                    "Saving Application {Id} failed: {Message}",
                    id,
                    SanitizeForLog(unknownFailure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            case ApplicationSaveResult.FailureCommitOutcomeUnknown:
                if (
                    await ReadCommittedRequestStateAsync(id, command, repository, logger) is not { } committed
                )
                {
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                }

                savedState = committed;
                break;
            default:
                logger.LogError(
                    "Saving Application {Id} returned an unexpected result {ResultType}",
                    id,
                    saveResult.GetType().Name
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        return await SyncPendingClientsAsync(
            id,
            savedState,
            httpContext,
            repository,
            apiClientRepository,
            clientRepository,
            identitySettings.Value.ClientRole,
            logger
        );
    }

    /// <summary>
    /// After a save whose commit outcome is unknown, reads the committed state under the aggregate
    /// lock the caller still holds. Returns it only when it provably holds the requested change, so
    /// its pending clients can be brought forward; otherwise returns null and nothing is sent to the
    /// identity provider and nothing is reverted.
    /// </summary>
    private static async Task<ApplicationSyncState?> ReadCommittedRequestStateAsync(
        int id,
        ApplicationUpdateCommand command,
        IApplicationRepository repository,
        ILogger<ApplicationModule> logger
    )
    {
        ApplicationSyncStateResult stateResult;
        try
        {
            stateResult = await repository.GetApplicationSyncState(id);
        }
        catch (Exception ex)
        {
            // The exception message is not sanitized, so only its type is logged.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogError(
                "Reading the committed state of Application {Id} threw {ExceptionType}; its save outcome is unknown",
                id,
                ex.GetType().Name
            );
#pragma warning restore S6667
            return null;
        }

        switch (stateResult)
        {
            case ApplicationSyncStateResult.Success success
                when success.State.ApplicationName == command.ApplicationName
                    && success.State.VendorId == command.VendorId
                    && success.State.ClaimSetName == command.ClaimSetName
                    && SetEquals(success.State.EducationOrganizationIds, command.EducationOrganizationIds)
                    && SetEquals(success.State.ProfileIds, command.ProfileIds):
                logger.LogWarning(
                    "The save of Application {Id} reported an unknown outcome, but the committed state holds the requested change; continuing with its pending clients",
                    id
                );
                return success.State;
            case ApplicationSyncStateResult.Success:
                logger.LogError(
                    "The save of Application {Id} reported an unknown outcome and the committed state does not hold the requested change; no identity provider update was attempted",
                    id
                );
                return null;
            case ApplicationSyncStateResult.FailureUnknown readFailure:
                logger.LogError(
                    "The save of Application {Id} reported an unknown outcome and the committed state could not be read: {Message}",
                    id,
                    SanitizeForLog(readFailure.FailureMessage)
                );
                return null;
            default:
                logger.LogError(
                    "The save of Application {Id} reported an unknown outcome and the committed state could not be read ({ResultType})",
                    id,
                    stateResult.GetType().Name
                );
                return null;
        }
    }

    /// <summary>
    /// Updates each pending client in the identity provider, in id order, and clears its marker
    /// only once the provider confirms the update. A client that fails stays pending and the rest
    /// are still attempted; the committed Application change is never reverted. Returns 204 when no
    /// client remains pending, and otherwise the partial-sync 502 naming the pending clients, read
    /// back from the database rather than assumed.
    /// </summary>
    private static async Task<IResult> SyncPendingClientsAsync(
        int id,
        ApplicationSyncState state,
        HttpContext httpContext,
        IApplicationRepository repository,
        IApiClientRepository apiClientRepository,
        IIdentityProviderRepository clientRepository,
        string clientRole,
        ILogger<ApplicationModule> logger
    )
    {
        bool everyMarkerCleared = true;
        foreach (ApplicationSyncPendingClient client in state.PendingClients)
        {
            everyMarkerCleared &=
                await TryUpdateProviderClientAsync(id, state, client, clientRepository, clientRole, logger)
                && await TryClearMarkerAsync(id, client.Id, apiClientRepository, logger);
        }

        if (everyMarkerCleared)
        {
            return Results.NoContent();
        }

        ApplicationSyncStateResult finalResult;
        try
        {
            finalResult = await repository.GetApplicationSyncState(id);
        }
        catch (Exception ex)
        {
            // The exception message is not sanitized, so only its type is logged.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogError(
                "Reading the pending clients of Application {Id} threw {ExceptionType}",
                id,
                ex.GetType().Name
            );
#pragma warning restore S6667
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        switch (finalResult)
        {
            case ApplicationSyncStateResult.Success { State.PendingClients.Length: 0 }:
                return Results.NoContent();
            case ApplicationSyncStateResult.Success success:
                int[] pendingIds = [.. success.State.PendingClients.Select(client => client.Id)];
                logger.LogWarning(
                    "Application {Id} is saved, but API clients are still pending an identity provider update ({PendingCount}): {ApiClientIds}",
                    id,
                    pendingIds.Length,
                    string.Join(",", pendingIds)
                );
                return Results.Json(
                    FailureResponse.ForPartialSync(pendingIds, httpContext.TraceIdentifier),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.BadGateway
                );
            case ApplicationSyncStateResult.FailureUnknown readFailure:
                logger.LogError(
                    "Could not read the pending clients of Application {Id}: {Message}",
                    id,
                    SanitizeForLog(readFailure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                logger.LogError(
                    "Could not read the pending clients of Application {Id} ({ResultType})",
                    id,
                    finalResult.GetType().Name
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
    }

    /// <summary>
    /// Sends one pending client the committed Application name and claim set with its own education
    /// organizations, data stores and approval. True only when the provider confirms the update of
    /// the client's stored UUID: a success naming any other client is not confirmation, and that
    /// other provider object is neither stored nor addressed.
    /// </summary>
    private static async Task<bool> TryUpdateProviderClientAsync(
        int id,
        ApplicationSyncState state,
        ApplicationSyncPendingClient client,
        IIdentityProviderRepository clientRepository,
        string clientRole,
        ILogger<ApplicationModule> logger
    )
    {
        ClientUpdateResult result;
        try
        {
            result = await clientRepository.UpdateClientAsync(
                client.ClientUuid.ToString(),
                state.ApplicationName,
                state.ClaimSetName,
                string.Join(",", client.EducationOrganizationIds),
                client.DataStoreIds,
                client.IsApproved,
                clientRole
            );
        }
        catch (Exception ex)
        {
            // The exception message is not sanitized, so only its type is logged.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogWarning(
                "Updating API client {ApiClientId} of Application {Id} in the identity provider threw {ExceptionType}; it stays pending",
                client.Id,
                id,
                ex.GetType().Name
            );
#pragma warning restore S6667
            return false;
        }

        switch (result)
        {
            case ClientUpdateResult.Success success when success.ClientUuid == client.ClientUuid:
                return true;
            case ClientUpdateResult.Success success:
                logger.LogWarning(
                    "The identity provider reported client {ReportedClientUuid} for API client {ApiClientId} of Application {Id}, not its stored client {ClientUuid}; the stored client is kept and it stays pending",
                    success.ClientUuid,
                    client.Id,
                    id,
                    client.ClientUuid
                );
                return false;
            case ClientUpdateResult.FailureIdentityProvider failure:
                logger.LogWarning(
                    "The identity provider rejected the update of API client {ApiClientId} of Application {Id}; it stays pending: {Message}",
                    client.Id,
                    id,
                    SanitizeForLog(failure.IdentityProviderError.FailureMessage)
                );
                return false;
            case ClientUpdateResult.FailureNotFound notFound:
                logger.LogWarning(
                    "The identity provider has no client for API client {ApiClientId} of Application {Id}; it stays pending: {Message}",
                    client.Id,
                    id,
                    SanitizeForLog(notFound.FailureMessage)
                );
                return false;
            case ClientUpdateResult.FailureUnknown unknown:
                logger.LogWarning(
                    "Updating API client {ApiClientId} of Application {Id} in the identity provider failed; it stays pending: {Message}",
                    client.Id,
                    id,
                    SanitizeForLog(unknown.FailureMessage)
                );
                return false;
            default:
                logger.LogWarning(
                    "Updating API client {ApiClientId} of Application {Id} in the identity provider returned {ResultType}; it stays pending",
                    client.Id,
                    id,
                    result.GetType().Name
                );
                return false;
        }
    }

    /// <summary>
    /// Clears a synchronized client's marker. False when the clear is not confirmed, whether it
    /// failed or its outcome is unknown; the caller then reads the markers back instead of assuming.
    /// </summary>
    private static async Task<bool> TryClearMarkerAsync(
        int id,
        int apiClientId,
        IApiClientRepository apiClientRepository,
        ILogger<ApplicationModule> logger
    )
    {
        ApiClientSyncPendingResult result;
        try
        {
            result = await apiClientRepository.ClearApiClientIdentityProviderSyncPending(apiClientId);
        }
        catch (Exception ex)
        {
            // The exception message is not sanitized, so only its type is logged.
#pragma warning disable S6667 // Logging in a catch clause should pass the caught exception - deliberately omitted, see comment above
            logger.LogWarning(
                "Clearing the pending marker of API client {ApiClientId} of Application {Id} threw {ExceptionType}",
                apiClientId,
                id,
                ex.GetType().Name
            );
#pragma warning restore S6667
            return false;
        }

        switch (result)
        {
            case ApiClientSyncPendingResult.Success:
                return true;
            case ApiClientSyncPendingResult.FailureUnknown failure:
                logger.LogWarning(
                    "Clearing the pending marker of API client {ApiClientId} of Application {Id} failed: {Message}",
                    apiClientId,
                    id,
                    SanitizeForLog(failure.FailureMessage)
                );
                return false;
            default:
                logger.LogWarning(
                    "Clearing the pending marker of API client {ApiClientId} of Application {Id} returned {ResultType}",
                    apiClientId,
                    id,
                    result.GetType().Name
                );
                return false;
        }
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
            case ApplicationApiClientsResult.Success { Clients.Length: > 1 }:
                // An application-level reset cannot tell which client's credentials to rotate.
                return Results.Json(
                    FailureResponse.ForConflict(
                        "The application has more than one API client. Reset a specific client's credentials with PUT /v3/apiClients/{id}/reset-credential.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApplicationApiClientsResult.Success success:
                var client = success.Clients.SingleOrDefault();
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
                    // No clients is either a missing (or foreign-tenant) Application or one with
                    // zero clients; the lock is still held, so the existence read is consistent.
                    return await ZeroClientResetResult(id, httpContext, repository, logger);
                }
                break;
            case ApplicationApiClientsResult.FailureUnknown failure:
                logger.LogError("Error fetching ApiClients: {Failure}", failure);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }

    private static async Task<IResult> ZeroClientResetResult(
        int id,
        HttpContext httpContext,
        IApplicationRepository repository,
        ILogger logger
    )
    {
        switch (await repository.GetApplication(id))
        {
            case ApplicationGetResult.Success:
                // An application-level reset has no client whose credentials it could rotate.
                return Results.Json(
                    FailureResponse.ForConflict(
                        "The application has no API clients. Create one with POST /v3/apiClients.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.Conflict
                );
            case ApplicationGetResult.FailureNotFound:
                return FailureResults.NotFound("Application not found", httpContext.TraceIdentifier);
            case ApplicationGetResult.FailureUnknown failure:
                logger.LogError(
                    "Error reading application during credential reset: {Message}",
                    SanitizeForLog(failure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                logger.LogError("Unexpected result reading application during credential reset");
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
    }
}
