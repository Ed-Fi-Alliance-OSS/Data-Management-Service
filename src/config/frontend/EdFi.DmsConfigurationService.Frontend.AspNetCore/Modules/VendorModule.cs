// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation.Results;
using Microsoft.OpenApi;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class VendorModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapSecuredPost("/v3/vendors/", InsertVendor)
            .Produces(201)
            .Produces(200)
            .AddOpenApiOperationTransformer(
                (operation, context, ct) =>
                {
                    if (operation.Responses is null)
                    {
                        return Task.CompletedTask;
                    }

                    foreach (var code in new[] { "201", "200" })
                    {
                        if (
                            operation.Responses.TryGetValue(code, out var iResponse)
                            && iResponse is OpenApiResponse response
                        )
                        {
                            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                            response.Headers["Location"] = new OpenApiHeader
                            {
                                Description = "The absolute URL of the vendor resource.",
                                Required = true,
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" },
                            };
                        }
                    }

                    return Task.CompletedTask;
                }
            );
        endpoints.MapSecuredGet("/v3/vendors/", GetAll);
        endpoints.MapSecuredGet($"/v3/vendors/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v3/vendors/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v3/vendors/{{id}}", Delete);
        endpoints
            .MapSecuredGet($"/v3/vendors/{{id}}/applications", GetApplicationsByVendorId)
            .Produces<List<ApplicationResponse>>(200);
    }

    private static async Task<IResult> InsertVendor(
        VendorInsertCommand entity,
        VendorInsertCommand.Validator validator,
        HttpContext httpContext,
        IVendorRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertVendor(entity);

        var request = httpContext.Request;
        var locationUrl =
            $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/";

        if (insertResult is VendorInsertResult.Success success)
        {
            var resourceUrl = $"{locationUrl}{success.Id}";
            if (success.IsNewVendor)
            {
                return Results.Created(resourceUrl, null);
            }

            httpContext.Response.Headers.Location = resourceUrl;
            return Results.Ok();
        }

        return insertResult switch
        {
            VendorInsertResult.FailureDuplicateCompanyName => Results.Json(
                FailureResponse.ForDataValidation(
                    [
                        new ValidationFailure(
                            "Name",
                            "A vendor name already exists in the database. Please enter a unique name."
                        ),
                    ],
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IVendorRepository repository,
        [AsParameters] FrontendVendorQuery query,
        VendorPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(query);
        VendorQueryResult getResult = await repository.QueryVendor(query.ToQuery());
        return getResult switch
        {
            VendorQueryResult.Success success => Results.Ok(success.VendorResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        int id,
        HttpContext httpContext,
        IVendorRepository repository,
        ILogger<VendorModule> logger
    )
    {
        logger.LogDebug("Entering Vendor GetById for id: {Id}", id);
        VendorGetResult getResult = await repository.GetVendor(id);
        return getResult switch
        {
            VendorGetResult.Success success => Results.Ok(success.VendorResponse),
            VendorGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    /// <summary>
    /// Updates the vendor and re-applies the resulting namespace prefixes to the identity
    /// provider client of every ApiClient the vendor owns.
    ///
    /// Every affected client's stable identity is resolved before any state is mutated; the
    /// identity provider is mutated before the database commit, so a provider failure returns
    /// with the vendor row untouched; and the UUID each successful provider call reports is
    /// persisted to that client's row, under a guard that refuses to overwrite a newer writer,
    /// before the next client is touched. A provider that preserves the client's identity
    /// reports the stored UUID, which the guard resolves as already applied.
    /// </summary>
    private static async Task<IResult> Update(
        int id,
        VendorUpdateCommand command,
        VendorUpdateCommand.Validator validator,
        HttpContext httpContext,
        IVendorRepository repository,
        IApiClientRepository apiClientRepository,
        IIdentityProviderRepository clientRepository,
        ILogger<VendorModule> logger
    )
    {
        PutGuards.GuardRouteIdMatchesBodyId(id, command.Id);

        await validator.GuardAsync(command);

        VendorUpdateState state;
        switch (await repository.GetVendorUpdateState(id))
        {
            case VendorUpdateStateResult.Success success:
                state = success.State;
                break;
            case VendorUpdateStateResult.FailureNotExists:
                return VendorNotFound();
            case VendorUpdateStateResult.FailureUnknown failure:
                logger.LogError(
                    "Error reading the update state of Vendor {Id}: {Message}",
                    id,
                    SanitizeForLog(failure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                logger.LogError("Unexpected result reading the update state of Vendor {Id}", id);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        foreach (VendorApiClient client in state.Clients)
        {
            if (await ApplyNamespaceClaimAsync(client) is { } clientFailure)
            {
                return clientFailure;
            }
        }

        VendorUpdateResult vendorUpdateResult;
        try
        {
            vendorUpdateResult = await repository.UpdateVendor(command);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Repository update threw for Vendor {Id}", id);
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        switch (vendorUpdateResult)
        {
            case VendorUpdateResult.Success:
                return Results.NoContent();
            case VendorUpdateResult.FailureNotExists:
                return VendorNotFound();
            case VendorUpdateResult.FailureUnknown updateFailure:
                logger.LogError(
                    "Repository update failed for Vendor {Id}: {Message}",
                    id,
                    SanitizeForLog(updateFailure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                logger.LogError("Unexpected repository update result for Vendor {Id}", id);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        IResult VendorNotFound() =>
            Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            );

        // Returns null once the client is done: its provider claim carries the requested
        // prefixes and the UUID the provider reported is stored on its row.
        async Task<IResult?> ApplyNamespaceClaimAsync(VendorApiClient client)
        {
            ClientUpdateResult clientUpdateResult;
            try
            {
                clientUpdateResult = await clientRepository.UpdateClientNamespaceClaimAsync(
                    client.ClientUuid.ToString(),
                    command.NamespacePrefixes
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "The namespace claim update threw for ApiClient {ApiClientId} of Vendor {Id}",
                    client.Id,
                    id
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }

            switch (clientUpdateResult)
            {
                case ClientUpdateResult.Success success:
                    return await PersistClientUuidAsync(client, success.ClientUuid);
                case ClientUpdateResult.FailureIdentityProvider failureIdentityProvider:
                    logger.LogError(
                        "Identity provider error updating the namespace claim of ApiClient {ApiClientId} of Vendor {Id}: {Message}",
                        client.Id,
                        id,
                        SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)
                    );
                    return FailureResults.BadGateway(
                        "Identity provider error during client update",
                        httpContext.TraceIdentifier
                    );
                case ClientUpdateResult.FailureNotFound notFound:
                    // The stored identity-provider client disappeared: an internal consistency
                    // failure, not caller input and not an upstream fault.
                    logger.LogError(
                        "Client not found in the identity provider while updating ApiClient {ApiClientId} of Vendor {Id}: {Message}",
                        client.Id,
                        id,
                        SanitizeForLog(notFound.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case ClientUpdateResult.FailureUnknown unknownFailure:
                    logger.LogError(
                        "Error updating the namespace claim of ApiClient {ApiClientId} of Vendor {Id}: {Message}",
                        client.Id,
                        id,
                        SanitizeForLog(unknownFailure.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                default:
                    logger.LogError(
                        "Unexpected identity provider result updating ApiClient {ApiClientId} of Vendor {Id}",
                        client.Id,
                        id
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
            }
        }

        async Task<IResult?> PersistClientUuidAsync(VendorApiClient client, Guid reportedClientUuid)
        {
            ApiClientUuidSyncResult syncResult;
            try
            {
                syncResult = await apiClientRepository.SyncApiClientUuid(
                    client.Id,
                    client.ClientUuid,
                    reportedClientUuid
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Persisting the identity-provider client UUID threw for ApiClient {ApiClientId} of Vendor {Id}; stored client state is inconsistent",
                    client.Id,
                    id
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }

            switch (syncResult)
            {
                case ApiClientUuidSyncResult.Success or ApiClientUuidSyncResult.AlreadyApplied:
                    return null;
                case ApiClientUuidSyncResult.FailureStaleState:
                    logger.LogError(
                        "The stored client state for ApiClient {ApiClientId} of Vendor {Id} changed during this request; the reported client UUID was not persisted and stored client state is inconsistent",
                        client.Id,
                        id
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case ApiClientUuidSyncResult.FailureNotExists
                or ApiClientUuidSyncResult.FailureNotExistsSafeToDelete:
                    logger.LogError(
                        "ApiClient {ApiClientId} of Vendor {Id} no longer exists; the reported client UUID was not persisted and stored client state is inconsistent",
                        client.Id,
                        id
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case ApiClientUuidSyncResult.FailureUnknown syncFailure:
                    logger.LogError(
                        "Failed to persist the identity-provider client UUID for ApiClient {ApiClientId} of Vendor {Id}: {Message}; stored client state is inconsistent",
                        client.Id,
                        id,
                        SanitizeForLog(syncFailure.FailureMessage)
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                default:
                    logger.LogError(
                        "Unexpected result persisting the identity-provider client UUID for ApiClient {ApiClientId} of Vendor {Id}; stored client state is inconsistent",
                        client.Id,
                        id
                    );
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
            }
        }
    }

    private static string SanitizeForLog(string? input)
    {
        return LoggingUtility.SanitizeForLog(input);
    }

    private static async Task<IResult> Delete(
        int id,
        HttpContext httpContext,
        IVendorRepository repository,
        ILogger<VendorModule> logger
    )
    {
        logger.LogDebug("Entering Vendor Delete for id: {Id}", id);
        VendorDeleteResult deleteResult = await repository.DeleteVendor(id);
        return deleteResult switch
        {
            VendorDeleteResult.Success => Results.NoContent(),
            VendorDeleteResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetApplicationsByVendorId(
        int id,
        IVendorRepository repository,
        HttpContext httpContext
    )
    {
        var getResult = await repository.GetVendorApplications(id);

        return getResult switch
        {
            VendorApplicationsResult.Success success => Results.Ok(success.ApplicationResponses),
            VendorApplicationsResult.FailureNotExists => FailureResults.NotFound(
                $"Vendor {id} not found. It may have been recently deleted.",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
