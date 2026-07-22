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
using FluentValidation;
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
            VendorInsertResult.FailureDuplicateCompanyName => FailureResults.NonUniqueIdentity(
                "The identifying value(s) of the item are the same as another item that already exists.",
                httpContext.TraceIdentifier,
                ["A vendor name already exists in the database. Please enter a unique name."]
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
        long id,
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
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        VendorUpdateCommand command,
        VendorUpdateCommand.Validator validator,
        HttpContext httpContext,
        IVendorRepository repository,
        IIdentityProviderRepository clientRepository,
        ILogger<ApplicationModule> logger
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var vendorUpdateResult = await repository.UpdateVendor(command);

        if (vendorUpdateResult is VendorUpdateResult.Success result)
        {
            foreach (var clientUuid in result.AffectedClientUuids)
            {
                var clientUpdateResult = await clientRepository.UpdateClientNamespaceClaimAsync(
                    clientUuid.ToString(),
                    command.NamespacePrefixes
                );

                switch (clientUpdateResult)
                {
                    case ClientUpdateResult.Success:
                        continue;
                    case ClientUpdateResult.FailureIdentityProvider failureIdentityProvider:
                        logger.LogError(
                            "Failure updating client: {FailureMessage}",
                            LoggingUtility.SanitizeForLog(
                                failureIdentityProvider.IdentityProviderError.FailureMessage
                            )
                        );
                        // The provider message can carry provider URLs and status detail, so surface only a
                        // fixed generic response and never the raw upstream message.
                        return FailureResults.BadGateway(
                            "Identity provider error during client update",
                            httpContext.TraceIdentifier
                        );
                    case ClientUpdateResult.FailureNotFound notFound:
                        logger.LogError(notFound.FailureMessage);
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                    case ClientUpdateResult.FailureUnknown unknownFailure:
                        logger.LogError(
                            "Error updating apiClient {ClientUuid}: {Message}",
                            clientUuid,
                            unknownFailure.FailureMessage
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                }
            }
        }

        return vendorUpdateResult switch
        {
            VendorUpdateResult.Success => Results.NoContent(),
            VendorUpdateResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
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
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetApplicationsByVendorId(
        long id,
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
